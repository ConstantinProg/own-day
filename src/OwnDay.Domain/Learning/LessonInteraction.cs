namespace OwnDay.Domain.Learning;

public enum LessonInteractionKind
{
    Clarification,
    AlternativeExplanation,
    FullAnswer
}

public sealed class LessonInteraction
{
    private LessonInteraction() { }

    internal LessonInteraction(Guid lessonId, LessonInteractionKind kind, string request, string explanation,
        LearningPreferenceValues? explanationPreferences, LessonProvenance? provenance)
    {
        LessonId = lessonId;
        Kind = kind;
        Request = request;
        Explanation = explanation;
        ExplanationPreferences = explanationPreferences;
        Provenance = provenance;
    }

    public long Id { get; private set; }
    public Guid LessonId { get; private set; }
    public LessonInteractionKind Kind { get; private set; }
    public string Request { get; private set; } = "";
    public string Explanation { get; private set; } = "";
    public LearningPreferenceValues? ExplanationPreferences { get; private set; }
    public LessonProvenance? Provenance { get; private set; }
}
