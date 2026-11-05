namespace OwnDay.Domain.Learning;

public enum LessonInteractionKind
{
    Clarification,
    AlternativeExplanation
}

public sealed class LessonInteraction
{
    private LessonInteraction() { }

    internal LessonInteraction(Guid lessonId, LessonInteractionKind kind, string request, string explanation)
    {
        LessonId = lessonId;
        Kind = kind;
        Request = request;
        Explanation = explanation;
    }

    public long Id { get; private set; }
    public Guid LessonId { get; private set; }
    public LessonInteractionKind Kind { get; private set; }
    public string Request { get; private set; } = "";
    public string Explanation { get; private set; } = "";
}
