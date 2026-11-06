namespace OwnDay.Domain.Learning;

public enum MaterialSourceStatus
{
    None,
    Unavailable,
    Retrieved,
    QuoteMatched
}

// QuoteMatched confirms only that this excerpt occurs in the retrieved document and lesson.
public sealed record LessonProvenance(string Generator, string? Reference, string? Version,
    DateTimeOffset? AccessedAt, string? Quote, MaterialSourceStatus Status)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Generator) || Generator.Length > 200 || !Enum.IsDefined(Status) ||
            Reference?.Length > 500 || Version?.Length > 100 || Quote?.Length > 500 ||
            AccessedAt is { Offset: var offset } && offset != TimeSpan.Zero ||
            Status != MaterialSourceStatus.None && (string.IsNullOrWhiteSpace(Reference) || string.IsNullOrWhiteSpace(Version)) ||
            (Status is MaterialSourceStatus.Retrieved or MaterialSourceStatus.QuoteMatched) && AccessedAt is null ||
            Status == MaterialSourceStatus.QuoteMatched && string.IsNullOrWhiteSpace(Quote) ||
            Status == MaterialSourceStatus.None && (Reference is not null || Version is not null || AccessedAt is not null) ||
            Status == MaterialSourceStatus.Unavailable && AccessedAt is not null ||
            Status != MaterialSourceStatus.QuoteMatched && Quote is not null)
            throw new ArgumentException("Invalid material provenance.");
    }
}

public enum AnswerAssessmentStatus
{
    AiOnly,
    Agreement,
    Conflict
}

public interface IAnswerCheck
{
    string Method { get; }
    bool Check(string answer, string expectedAnswer);
}

// A reference comparison, not an independent proof that an AI-generated reference is correct.
public sealed class NormalizedTextAnswerCheck : IAnswerCheck
{
    public string Method => "NormalizedText/v1";

    public bool Check(string answer, string expectedAnswer) =>
        string.Equals(Normalize(answer), Normalize(expectedAnswer), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
