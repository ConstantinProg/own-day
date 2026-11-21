using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OwnDay.Application.Learning;

// Values are part of the versioned contextual-action request contract.
public enum LearningActionKind : byte
{
    SelectChoice = 1,
    ToggleChoice = 2,
    SubmitChoice = 3,
    Continue = 4,
    Next = 5,
    Hint = 6,
    Solution = 7,
    Criteria = 8,
    ExplainAssessment = 9,
    AnotherExercise = 10,
    ReviewSuggestions = 11,
    Confirm = 12,
    Reject = 13,
    ClarifyAnswer = 14,
    ClarifyQuestion = 15,
    ClarifyGoal = 16,
    ClarifyPause = 17,
    ClarifyContinue = 18,
    RetryConsultation = 19,
    StartDiagnosis = 20,
    SkipDiagnosis = 21,
    DiagnosisHelp = 22,
    PauseDiagnosis = 23,
    PausePractice = 24,
    StopPractice = 25,
    StartInvitation = 26,
    PostponeInvitation = 27,
    SkipInvitation = 28,
    PauseReminders = 29,
    ResumeReminders = 30,
    StopReminders = 31,
    SelectProgram = 32,
    RouteTree = 33,
    RouteHistory = 34,
    StartReview = 35,
    PostponeReview = 36,
    SkipReview = 37,
    SkillPractice = 38,
    ProjectReportPage = 39,
    ProjectPractice = 40,
    FocusTopic = 41,
    RouteVersion = 42,
    ProgramsPage = 43,
    ReviewsPage = 44,
    NoPriorExperience = 45,
    Progress = 46,
    ExplainMaterial = 47,
    ShowExample = 48
}

public sealed record LearningAction(LearningActionKind Kind, Guid TargetId = default, long Revision = 0, byte Option = 0)
{
    public int ChoiceVersion { get; init; }
    public long Number { get; init; }
    public int RouteVersion { get; init; }
    public int TopicIndex { get; init; }

    public static LearningAction ForNumber(LearningActionKind kind, long number, int routeVersion = 0, int topicIndex = 0) =>
        new(kind) { Number = number, RouteVersion = routeVersion, TopicIndex = topicIndex };

    public static long TopicIdentity(Guid programId, int routeVersion, string topicCode) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{programId:N}/{routeVersion}/{topicCode}"))));

    public static LearningAction ForChoice(ChoicePrompt prompt, LearningActionKind kind, byte option = 0) =>
        new(kind, prompt.LessonId, prompt.Revision, option) { ChoiceVersion = prompt.Version };

    public ChoiceInput? ToChoiceInput()
    {
        if (TargetId == Guid.Empty || ChoiceVersion < 1 || Revision is < 1 or > int.MaxValue) return null;
        return Kind switch
        {
            LearningActionKind.SelectChoice when Option is >= 1 and <= OwnDay.Domain.Learning.ChoiceExercise.MaxOptions =>
                new(TargetId, ChoiceVersion, (int)Revision, ChoiceAction.Select, Option),
            LearningActionKind.ToggleChoice when Option is >= 1 and <= OwnDay.Domain.Learning.ChoiceExercise.MaxOptions =>
                new(TargetId, ChoiceVersion, (int)Revision, ChoiceAction.Toggle, Option),
            LearningActionKind.SubmitChoice when Option == 0 => new(TargetId, ChoiceVersion, (int)Revision, ChoiceAction.Submit),
            _ => null
        };
    }
}
