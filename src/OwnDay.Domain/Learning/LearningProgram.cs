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

public sealed class LearningProgram
{
    public const int MaxGoalLength = 500;
    public const int MaxExperienceLength = 1000;
    public const int MaxDisciplineLength = 100;
    public const int MaxDiagnosticQuestionLength = 1000;
    public const int MaxDiagnosticAnswerLength = 3000;

    private readonly List<CurriculumRoute> _routes = [];
    private LearningProgram() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Goal { get; private set; } = "";
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
        string discipline, IEnumerable<CurriculumTopic> topics, string? diagnosticQuestion)
    {
        if (!user.IsValid || projectId is <= 0) throw new ArgumentException("Invalid owner or project.");
        Validate(goal, MaxGoalLength);
        Validate(experience, MaxExperienceLength);
        Validate(discipline, MaxDisciplineLength);
        if (diagnosticQuestion is not null) Validate(diagnosticQuestion, MaxDiagnosticQuestionLength);
        var program = new LearningProgram
        {
            Id = Guid.NewGuid(), UserId = user, Goal = goal, Experience = experience, ProjectId = projectId,
            Discipline = discipline, DiagnosticQuestion = diagnosticQuestion, DiagnosticPending = diagnosticQuestion is not null
        };
        program._routes.Add(CurriculumRoute.Create(program.Id, 1, "Initial route based on self-reported experience.", topics));
        return program;
    }

    public void ApplyDiagnostic(string answer, string discipline, IEnumerable<CurriculumTopic> topics)
    {
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
        DiagnosticPending = false;
    }

    public CurriculumTopic? NextTopic(IReadOnlySet<string> completed)
    {
        if (DiagnosticPending) throw new InvalidOperationException("Finish or skip diagnosis first.");
        return CurrentRoute.Topics.FirstOrDefault(t => !completed.Contains(t.Code) && t.Prerequisites.All(completed.Contains));
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
        route.PreserveProgress(CurrentRoute.ProgressVersion ?? CurrentRoute.Version);
        return route;
    }

    public void ApplyAdaptation(CurriculumRoute route)
    {
        if (route.ProgramId != Id || route.Version != CurrentRoute.Version + 1 ||
            route.ProgressVersion != (CurrentRoute.ProgressVersion ?? CurrentRoute.Version))
            throw new ArgumentException("Invalid adaptation revision.", nameof(route));
        _routes.Add(route);
    }

    public int[] ProgressVersions(int version)
    {
        var route = _routes.Single(r => r.Version == version);
        var root = route.ProgressVersion ?? route.Version;
        return _routes.Where(r => r.Version <= version && (r.ProgressVersion ?? r.Version) == root)
            .Select(r => r.Version).ToArray();
    }

    internal static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("Invalid curriculum text.");
    }
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
    public IReadOnlyList<CurriculumTopic> Topics { get; private set; } = [];

    internal void PreserveProgress(int version) => ProgressVersion = version;

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
