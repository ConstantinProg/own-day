using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Application.Dialogue;

namespace OwnDay.Application.Learning.Requests;

// Concrete snapshots for the five existing Learning provider operations. No Domain
// entity or service scope is retained by a durable step or sent to a provider.
public sealed record LessonSnapshot(Guid Id, UserId UserId, string Topic, string Theory, string Question,
    LessonStatus Status, LessonTeaching? Teaching, InterdisciplinaryPractice? Transfer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LearningContent? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LessonRubricEvaluation? RubricEvaluation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AssessmentReviewContext? Review = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LearningPreferenceValues? ExplanationPreferences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FactCheckDiscipline = null)
{
    public static LessonSnapshot From(Lesson lesson) => new(lesson.Id, lesson.UserId, lesson.Topic,
        lesson.Theory, lesson.Question, lesson.Status, lesson.Teaching, lesson.Transfer, lesson.Content, lesson.RubricEvaluation);
}

public sealed record AssessmentReviewContext(string Argument, string Stage);

public sealed record LessonAssessmentInput(LessonSnapshot Lesson, string Answer);
public sealed record LessonExplanationInput(LessonSnapshot Lesson, string Request, LessonInteractionKind Kind);
public sealed record TeachingInput(string Goal, string Discipline, CurriculumTopic[] Topics,
    string CurrentTopicCode, string[] Completed, LearnerSkillState State, string[] RecentFeedback)
{
    public TeachingRequest ToRequest() => new(Goal, Discipline, Topics, CurrentTopicCode,
        Completed.ToHashSet(StringComparer.Ordinal), State, RecentFeedback);
}

public sealed class LearningProviderStep
{
    public Guid RequestId { get; set; }
    public int Ordinal { get; init; }
    public string Kind { get; init; } = "";
    public int Version { get; init; } = 1;
    public string Input { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string TargetStamp { get; init; } = "";
    public string Targets { get; init; } = "[]";
    public string? Result { get; set; }
    public LessonProviderFailure? Failure { get; set; }

    public T ReadInput<T>() => JsonSerializer.Deserialize<T>(Input, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Missing Learning provider input.");

    public LearningProviderStep WithResult<T>(T result) => new()
    {
        RequestId = RequestId, Ordinal = Ordinal, Kind = Kind, Version = Version, Input = Input,
        Fingerprint = Fingerprint, TargetStamp = TargetStamp, Targets = Targets,
        Result = JsonSerializer.Serialize(result, JsonSerializerOptions.Web)
    };
}

public sealed class LearningProviderRequiredException(LearningProviderStep step) : Exception
{
    public LearningProviderStep Step { get; } = step;
}

public sealed class StaleLearningPreparationException : Exception;

// The existing use cases run to the next external boundary in a short rollback-only
// preparation. A fresh invocation repeats their guards and consumes saved results;
// only a complete invocation may commit Domain effects. This keeps one set of
// use-case rules for legacy commands and the worker, including conditional Adapt.
public sealed class LearningProviderSession(IReadOnlyList<LearningProviderStep> steps, Func<string> targetStamp,
    Func<string>? targets = null)
    : ILessonTeacher, ICurriculumPlanner, ITeachingPlanner, ILearningIntentResolver
{
    private int _ordinal;

    public Task<LearningIntentProposal> ResolveAsync(LearningIntentInput input, CancellationToken token) =>
        ConsumeAsync<LearningIntentProposal>("intent", input, token);

    public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) =>
        ConsumeAsync<LessonMaterial>("generate", request, token);

    public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
        ConsumeAsync<LessonAssessment>("assess", new LessonAssessmentInput(LessonSnapshot.From(lesson), answer), token);

    public Task<LessonAssessment> AssessAsync(LessonSnapshot lesson, string answer, CancellationToken token) =>
        ConsumeAsync<LessonAssessment>("assess", new LessonAssessmentInput(lesson, answer), token);

    public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
        ExplainAsync(LessonSnapshot.From(lesson), request, kind, token);

    public Task<LessonExplanation> ExplainAsync(LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
        ConsumeAsync<LessonExplanation>("explain", new LessonExplanationInput(lesson, request, kind), token);

    public Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token) =>
        ConsumeAsync<CurriculumProposal>("plan", request, token);

    public Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token) =>
        ConsumeAsync<TeachingProposal>("teach", new TeachingInput(request.Goal, request.Discipline, request.Topics.ToArray(),
            request.CurrentTopicCode, request.Completed.Order(StringComparer.Ordinal).ToArray(), request.State, request.RecentFeedback), token);

    public void EnsureComplete()
    {
        if (_ordinal != steps.Count) throw new StaleLearningPreparationException();
    }

    private Task<T> ConsumeAsync<T>(string kind, object input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(input, JsonSerializerOptions.Web);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var stamp = targetStamp();
        var ordinal = _ordinal++;
        if (ordinal >= 2) throw new InvalidOperationException("Learning command exceeded its provider step limit.");
        if (ordinal == steps.Count)
            throw new LearningProviderRequiredException(new()
            {
                Ordinal = ordinal, Kind = kind, Input = json, Fingerprint = fingerprint, TargetStamp = stamp,
                Targets = targets?.Invoke() ?? "[]"
            });
        var step = steps[ordinal];
        if (step.Ordinal != ordinal || step.Version != 1 || step.Kind != kind || step.Fingerprint != fingerprint || step.TargetStamp != stamp)
            throw new StaleLearningPreparationException();
        if (step.Failure is { } failure) throw new LessonProviderException(failure);
        if (step.Result is null) throw new LearningProviderRequiredException(step);
        return Task.FromResult(JsonSerializer.Deserialize<T>(step.Result, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Missing Learning provider result."));
    }
}
