namespace OwnDay.Domain.Learning;

public sealed class Lesson
{
    private Lesson() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public string Topic { get; private set; } = "";
    public string Theory { get; private set; } = "";
    public string Question { get; private set; } = "";
    public string? Answer { get; private set; }
    public string? Feedback { get; private set; }
    public bool? IsCorrect { get; private set; }

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
        if (Answer is not null) throw new InvalidOperationException("Lesson is already completed.");
        Validate(answer, 3000);
        Validate(feedback, 3000);
        Answer = answer;
        Feedback = feedback;
        IsCorrect = correct;
    }

    private static void Validate(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException("Invalid lesson text.", nameof(value));
    }
}
