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
        if (string.IsNullOrWhiteSpace(Generator) || Generator.Length > 200)
            throw new ArgumentException("Generator must contain 1–200 characters.", nameof(Generator));
        if (Reference?.Length > 500)
            throw new ArgumentException("Reference must not exceed 500 characters.", nameof(Reference));
        if (Version?.Length > 100)
            throw new ArgumentException("Version must not exceed 100 characters.", nameof(Version));
        if (Quote?.Length > 500)
            throw new ArgumentException("Quote must not exceed 500 characters.", nameof(Quote));
        if (AccessedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("Access time must be UTC.", nameof(AccessedAt));
        if (!Enum.IsDefined(Status))
            throw new ArgumentException("Unknown material source status.", nameof(Status));

        if (Status != MaterialSourceStatus.None)
        {
            if (string.IsNullOrWhiteSpace(Reference))
                throw new ArgumentException("A source requires a reference.", nameof(Reference));
            if (string.IsNullOrWhiteSpace(Version))
                throw new ArgumentException("A source requires a version.", nameof(Version));
        }
        if (Status != MaterialSourceStatus.QuoteMatched && Quote is not null)
            throw new ArgumentException("Only a matched excerpt may have a quote.", nameof(Quote));

        switch (Status)
        {
            case MaterialSourceStatus.None:
                if (Reference is not null || Version is not null || AccessedAt is not null)
                    throw new ArgumentException("An absent source cannot have reference, version or access metadata.");
                break;
            case MaterialSourceStatus.Unavailable:
                if (AccessedAt is not null)
                    throw new ArgumentException("An unavailable source cannot have an access time.", nameof(AccessedAt));
                break;
            case MaterialSourceStatus.Retrieved:
            case MaterialSourceStatus.QuoteMatched:
                if (AccessedAt is null)
                    throw new ArgumentException("A retrieved source requires an access time.", nameof(AccessedAt));
                if (Status == MaterialSourceStatus.QuoteMatched && string.IsNullOrWhiteSpace(Quote))
                    throw new ArgumentException("A matched excerpt requires a quote.", nameof(Quote));
                break;
        }
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
    bool Check(string answer, string expectedAnswer);
}

// A reference comparison, not an independent proof that an AI-generated reference is correct.
public sealed class NormalizedTextAnswerCheck : IAnswerCheck
{
    public const string MethodName = "NormalizedText/v1";

    public bool Check(string answer, string expectedAnswer) =>
        string.Equals(Normalize(answer), Normalize(expectedAnswer), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
