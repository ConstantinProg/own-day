namespace OwnDay.Domain.Learning;

public sealed record CurriculumTopic
{
    public const int MaxCodeLength = 40;
    public const int MaxTitleLength = 100;

    public CurriculumTopic(string code, string title, IReadOnlyList<string> prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        Code = code;
        Title = title;
        Prerequisites = Array.AsReadOnly(prerequisites.ToArray());
    }

    public string Code { get; }
    public string Title { get; }
    public IReadOnlyList<string> Prerequisites { get; }
}

public sealed partial class LearningProgram
{
    public const int MaxGoalLength = 500;
    public const int MaxGoalCriterionLength = 500;
    public const int MaxExperienceLength = 1000;
    public const int MaxDisciplineLength = 100;
    public const int MaxDiagnosticQuestionLength = 1000;
    public const int MaxDiagnosticAnswerLength = 3000;

    private readonly List<CurriculumRoute> _routes = [];
    private LearningProgram() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Goal { get; private set; } = "";
    public string? GoalCriterion { get; private set; }
    public string Experience { get; private set; } = "";
    public string Discipline { get; private set; } = "";
    public long? ProjectId { get; private set; }
    public int Priority { get; private set; } = 1;
    public string? DiagnosticQuestion { get; private set; }
    public string? DiagnosticAnswer { get; private set; }
    public bool DiagnosticPending { get; private set; }
    public IReadOnlyCollection<CurriculumRoute> Routes => _routes.AsReadOnly();
    public CurriculumRoute CurrentRoute => _routes.MaxBy(r => r.Version)!;

    public static LearningProgram Create(UserId user, string goal, string experience, long? projectId,
        string discipline, IEnumerable<CurriculumTopic> topics, string? diagnosticQuestion, string? goalCriterion = null)
    {
        if (!user.IsValid || projectId is <= 0) throw new ArgumentException("Invalid owner or project.");
        Validate(goal, MaxGoalLength);
        if (goalCriterion is not null) Validate(goalCriterion, MaxGoalCriterionLength);
        Validate(experience, MaxExperienceLength);
        Validate(discipline, MaxDisciplineLength);
        if (diagnosticQuestion is not null) Validate(diagnosticQuestion, MaxDiagnosticQuestionLength);
        var program = new LearningProgram
        {
            Id = Guid.NewGuid(), UserId = user, Goal = goal, GoalCriterion = goalCriterion, Experience = experience, ProjectId = projectId,
            Discipline = discipline, DiagnosticQuestion = diagnosticQuestion, DiagnosticPending = diagnosticQuestion is not null
        };
        program._routes.Add(CurriculumRoute.Create(program.Id, 1, "Initial route based on self-reported experience.", topics));
        return program;
    }

    public void ApplyDiagnostic(string answer, string discipline, IEnumerable<CurriculumTopic> topics)
    {
        if (EnglishDiagnostic is not null) throw new InvalidOperationException("Use the versioned English diagnostic.");
        if (SubjectDiagnostic is not null) throw new InvalidOperationException("Use the versioned subject diagnostic.");
        if (!DiagnosticPending) throw new InvalidOperationException("Diagnosis has ended.");
        Validate(answer, MaxDiagnosticAnswerLength);
        if (!string.Equals(Discipline, discipline, StringComparison.Ordinal))
        {
            throw new ArgumentException("Diagnosis cannot change the program discipline.", nameof(discipline));
        }
        var route = CurriculumRoute.Create(Id, CurrentRoute.Version + 1,
            "Route revised from a diagnostic answer; AI interpretation is provisional.", topics);
        _routes.Add(route);
        DiagnosticAnswer = answer;
        DiagnosticPending = false;
    }

    public void SkipDiagnostic()
    {
        if (!DiagnosticPending) throw new InvalidOperationException("Diagnosis has ended.");
        if (EnglishDiagnostic is { } diagnostic)
        {
            if (!diagnostic.Supported) throw new InvalidOperationException("A compatible diagnostic reader is required.");
            EnglishDiagnostic = diagnostic with { Status = EnglishDiagnosticStatus.Skipped };
        }
        if (SubjectDiagnostic is { } subject)
        {
            if (!subject.Supported) throw new InvalidOperationException("A compatible diagnostic reader is required.");
            SubjectDiagnostic = subject with { Deferred = true, WasDeferred = true };
            if (subject.Observations.Length > 0)
                DiagnosticAnswer = SummarizeSubjectDiagnostic(subject);
        }
        DiagnosticPending = false;
    }

    public CurriculumTopic? NextTopic(IReadOnlySet<string> completed)
    {
        if (DiagnosticPending) throw new InvalidOperationException("Finish or skip diagnosis first.");
        var route = CurrentRoute;
        return route.Topics.FirstOrDefault(t => !completed.Contains(t.Code) && !route.IsSkipped(t.Code) &&
            route.PrerequisitesSatisfied(t, completed));
    }

    public CurriculumRoute ProposeAdaptation(string topicCode, string reason)
    {
        if (DiagnosticPending) throw new InvalidOperationException("Finish diagnosis first.");
        Validate(reason, CurriculumRoute.MaxReasonLength);
        if (!CurrentRoute.Topics.Any(t => t.Code == topicCode)) throw new ArgumentException("Unknown topic.");
        // Prioritize the whole prerequisite chain so topological ordering retains the selected topic's priority.
        var priority = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([topicCode]);
        while (pending.TryPop(out var code))
        {
            if (!priority.Add(code)) continue;
            foreach (var prerequisite in CurrentRoute.Topics.Single(t => t.Code == code).Prerequisites)
                pending.Push(prerequisite);
        }
        var route = CurriculumRoute.Create(Id, CurrentRoute.Version + 1, reason,
            CurrentRoute.Topics.OrderBy(t => priority.Contains(t.Code) ? 0 : 1));
        route.PreserveProgress(CurrentRoute.ProgressRootVersion);
        route.SetPath(CurrentRoute.Path?.ForSuccessor());
        return route;
    }

    public void ApplyAdaptation(CurriculumRoute route)
    {
        if (route.ProgramId != Id || route.Version != CurrentRoute.Version + 1 ||
            route.ProgressVersion != CurrentRoute.ProgressRootVersion)
            throw new ArgumentException("Invalid adaptation revision.", nameof(route));
        _routes.Add(route);
    }

    public int[] ProgressVersions(int version)
    {
        var route = _routes.Single(r => r.Version == version);
        var root = route.ProgressRootVersion;
        return _routes.Where(r => r.Version <= version && r.ProgressRootVersion == root)
            .Select(r => r.Version).ToArray();
    }

    public bool ReviseRoute(int expectedVersion, IReadOnlyList<CurriculumTopic> topics, string reason)
    {
        var current = CurrentRoute;
        if (DiagnosticPending || current.Version != expectedVersion) return false;
        Validate(reason, CurriculumRoute.MaxReasonLength);
        var next = CurriculumRoute.Create(Id, current.Version + 1, reason, topics);
        // Editing the path preserves the meaning of every topic in this progress family.
        if (next.Topics.Count != current.Topics.Count || next.Topics.Any(topic => !current.Topics.Any(previous =>
                previous.Code == topic.Code && previous.Title == topic.Title)))
            throw new ArgumentException("Route edits must preserve topic identities.", nameof(topics));
        if (!next.Topics.SequenceEqual(topics) || next.HasSameOrderedTopics(current)) return false;
        next.PreserveProgress(current.ProgressRootVersion);
        next.SetPath(current.Path?.ForSuccessor());
        _routes.Add(next);
        return true;
    }

    public bool SetTopicSkipped(int expectedVersion, string code, bool skipped, string reason)
    {
        var current = CurrentRoute;
        if (DiagnosticPending || current.Version != expectedVersion || current.IsSkipped(code) == skipped ||
            !current.Topics.Any(topic => topic.Code == code)) return false;
        var codes = current.Path?.SkippedTopics ?? [];
        var next = skipped ? codes.Append(code) : codes.Where(value => value != code);
        AppendPath(current, new(next.ToArray(), current.Path?.DepthOverrides ?? [], null), reason);
        return true;
    }

    public bool SetTopicDepth(int expectedVersion, string code, CurriculumDepth depth, string reason)
    {
        var current = CurrentRoute;
        if (!Enum.IsDefined(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        if (DiagnosticPending || current.Version != expectedVersion || current.DepthFor(code) == depth ||
            !current.Topics.Any(topic => topic.Code == code)) return false;
        var depths = (current.Path?.DepthOverrides ?? []).Where(topic => topic.Code != code);
        if (depth != CurriculumDepth.Standard) depths = depths.Append(new(code, depth));
        AppendPath(current, new(current.Path?.SkippedTopics ?? [], depths.ToArray(), null), reason);
        return true;
    }

    public bool RestoreRoute(int expectedVersion, int version, string reason)
    {
        var current = CurrentRoute;
        if (DiagnosticPending || current.Version != expectedVersion || version >= expectedVersion ||
            _routes.SingleOrDefault(route => route.Version == version) is not { } source) return false;
        if (current.ProgressRootVersion == source.ProgressRootVersion && current.HasSameOrderedTopics(source) &&
            current.Topics.All(topic => current.IsSkipped(topic.Code) == source.IsSkipped(topic.Code) &&
                current.DepthFor(topic.Code) == source.DepthFor(topic.Code))) return false;
        AppendPath(source, new(source.Path?.SkippedTopics ?? [], source.Path?.DepthOverrides ?? [], source.Version), reason);
        return true;
    }

    private void AppendPath(CurriculumRoute source, CurriculumPath path, string reason)
    {
        Validate(reason, CurriculumRoute.MaxReasonLength);
        var next = CurriculumRoute.Create(Id, CurrentRoute.Version + 1, reason, source.Topics);
        next.PreserveProgress(source.ProgressRootVersion);
        next.SetPath(path);
        _routes.Add(next);
    }

    internal static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("Invalid curriculum text.");
    }
}

public enum CurriculumDepth { Standard, Brief, Detailed }

public sealed record CurriculumTopicDepth(string Code, CurriculumDepth Depth);

public sealed record CurriculumPath
{
    public CurriculumPath(IReadOnlyList<string> skippedTopics, IReadOnlyList<CurriculumTopicDepth> depthOverrides, int? restoredFrom)
    {
        SkippedTopics = Array.AsReadOnly(skippedTopics.ToArray());
        DepthOverrides = Array.AsReadOnly(depthOverrides.ToArray());
        RestoredFrom = restoredFrom;
    }

    public IReadOnlyList<string> SkippedTopics { get; }
    public IReadOnlyList<CurriculumTopicDepth> DepthOverrides { get; }
    public int? RestoredFrom { get; }

    internal CurriculumPath? ForSuccessor() => RestoredFrom is null ? this
        : SkippedTopics.Count == 0 && DepthOverrides.Count == 0 ? null : new(SkippedTopics, DepthOverrides, null);
}

public sealed class CurriculumRoute
{
    public const int MaxTopicCount = 12;
    public const int MaxReasonLength = 500;

    private CurriculumRoute() { }
    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public int Version { get; private set; }
    public string Reason { get; private set; } = "";
    public int? ProgressVersion { get; private set; }
    public int ProgressRootVersion => ProgressVersion ?? Version;
    public IReadOnlyList<CurriculumTopic> Topics { get; private set; } = [];
    public CurriculumPath? Path { get; private set; }

    public bool IsSkipped(string code) => Path?.SkippedTopics.Contains(code, StringComparer.Ordinal) == true;

    public CurriculumDepth DepthFor(string code) => Path?.DepthOverrides.SingleOrDefault(topic => topic.Code == code)?.Depth ?? CurriculumDepth.Standard;

    public bool PrerequisitesSatisfied(CurriculumTopic topic, IReadOnlySet<string> completed) =>
        topic.Prerequisites.All(code => completed.Contains(code) || IsSkipped(code));

    internal bool HasSameOrderedTopics(CurriculumRoute other) => Topics.Count == other.Topics.Count && Topics.Zip(other.Topics).All(pair =>
        pair.First.Code == pair.Second.Code && pair.First.Title == pair.Second.Title &&
        pair.First.Prerequisites.SequenceEqual(pair.Second.Prerequisites));

    internal void PreserveProgress(int version) => ProgressVersion = version;
    internal void SetPath(CurriculumPath? path) => Path = path;

    internal static CurriculumRoute Create(Guid programId, int version, string reason, IEnumerable<CurriculumTopic> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        var source = topics.ToArray();
        if (source.Length is < 1 or > MaxTopicCount || source.Any(t => t is null)) throw new ArgumentException("Invalid topic count.");
        foreach (var topic in source)
        {
            LearningProgram.Validate(topic.Code, CurriculumTopic.MaxCodeLength);
            LearningProgram.Validate(topic.Title, CurriculumTopic.MaxTitleLength);
            if (!topic.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || topic.Prerequisites is null ||
                topic.Prerequisites.Count > MaxTopicCount || topic.Prerequisites.Any(string.IsNullOrWhiteSpace) ||
                topic.Prerequisites.Distinct(StringComparer.Ordinal).Count() != topic.Prerequisites.Count)
                throw new ArgumentException("Invalid topic or prerequisites.");
        }
        var codes = source.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Count != source.Length || source.Any(t => t.Prerequisites.Any(p => !codes.Contains(p))))
            throw new ArgumentException("Duplicate topic or unknown prerequisite.");
        var route = new CurriculumRoute { Id = Guid.NewGuid(), ProgramId = programId, Version = version, Reason = reason };
        var ordered = new List<CurriculumTopic>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < source.Length)
        {
            var next = source.FirstOrDefault(t => !visited.Contains(t.Code) && t.Prerequisites.All(visited.Contains));
            if (next is null) throw new ArgumentException("Cyclic prerequisites.");
            visited.Add(next.Code);
            ordered.Add(next);
        }
        route.Topics = ordered.AsReadOnly();
        return route;
    }
}
