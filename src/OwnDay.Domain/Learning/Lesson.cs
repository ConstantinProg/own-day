namespace OwnDay.Domain.Learning;

public sealed class Lesson
{
    private readonly List<LessonInteraction> _interactions = [];
    private Lesson() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Topic { get; private set; } = "";
    public string Theory { get; private set; } = "";
    public string Question { get; private set; } = "";
    public string? Answer { get; private set; }
    public string? Feedback { get; private set; }
    public bool? IsCorrect { get; private set; }
    public LessonStatus Status { get; private set; }
    public IReadOnlyCollection<LessonInteraction> Interactions => _interactions.AsReadOnly();

    public static Lesson Start(UserId user, string topic, string theory, string question)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        Validate(topic, 500);
        Validate(theory, 2500);
        Validate(question, 1000);
        return new Lesson { Id = Guid.NewGuid(), UserId = user, Topic = topic, Theory = theory, Question = question };
    }

    public void Complete(string answer, string feedback, bool correct)
    {
        RequireAwaitingAnswer();
        Validate(answer, 3000);
        Validate(feedback, 3000);
        Answer = answer;
        Feedback = feedback;
        IsCorrect = correct;
        Status = LessonStatus.Completed;
    }

    public void Pause()
    {
        RequireAwaitingAnswer();
        Status = LessonStatus.Paused;
    }

    public void Resume()
    {
        if (Status != LessonStatus.Paused) throw new InvalidOperationException("Lesson is not paused.");
        Status = LessonStatus.AwaitingAnswer;
    }

    public void Finish()
    {
        if (Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused))
            throw new InvalidOperationException("Lesson has ended.");
        Status = LessonStatus.Finished;
    }

    public void AddExplanation(LessonInteractionKind kind, string request, string explanation)
    {
        RequireAwaitingAnswer();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Validate(request, 1000);
        Validate(explanation, 2500);
        _interactions.Add(new LessonInteraction(Id, kind, request, explanation));
    }

    private void RequireAwaitingAnswer()
    {
        if (Status != LessonStatus.AwaitingAnswer) throw new InvalidOperationException("Lesson is not awaiting an answer.");
    }

    private static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Invalid lesson text.", nameof(value));
    }
}
