using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LessonProvenanceTests
{
    private static LessonProvenance[] Sources =>
    [
        new("model", null, null, null, null, MaterialSourceStatus.None),
        new("model", "reference", "v1", null, null, MaterialSourceStatus.Unavailable),
        Retrieved,
        Retrieved with { Status = MaterialSourceStatus.QuoteMatched, Quote = "excerpt" },
        Retrieved with { Generator = new string('g', 200), Reference = new string('r', 500), Version = new string('v', 100) },
        Retrieved with { Status = MaterialSourceStatus.QuoteMatched, Quote = new string('q', 500) }
    ];

    public static TheoryData<LessonProvenance> ValidSources
    {
        get
        {
            var sources = new TheoryData<LessonProvenance>();
            foreach (var source in Sources) sources.Add(source);
            return sources;
        }
    }

    private static LessonProvenance Retrieved =>
        new("model", "reference", "v1", DateTimeOffset.UnixEpoch, null, MaterialSourceStatus.Retrieved);

    public static TheoryData<LessonProvenance> InvalidSources
    {
        get
        {
            var sources = new TheoryData<LessonProvenance>
            {
                Retrieved with { Generator = " " },
                Retrieved with { Generator = new string('g', 201) },
                Retrieved with { Reference = new string('r', 501) },
                Retrieved with { Version = new string('v', 101) },
                Retrieved with { Status = (MaterialSourceStatus)99 },
                Retrieved with { AccessedAt = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1)) },
                Retrieved with { Status = MaterialSourceStatus.QuoteMatched, Quote = new string('q', 501) }
            };
            foreach (var source in Sources)
            {
                if (source.Status == MaterialSourceStatus.None)
                {
                    sources.Add(source with { Reference = "reference" });
                    sources.Add(source with { Version = "v1" });
                    sources.Add(source with { AccessedAt = DateTimeOffset.UnixEpoch });
                }
                else
                {
                    sources.Add(source with { Reference = null });
                    sources.Add(source with { Reference = " " });
                    sources.Add(source with { Version = null });
                    sources.Add(source with { Version = " " });
                }
                if (source.Status is MaterialSourceStatus.Retrieved or MaterialSourceStatus.QuoteMatched)
                    sources.Add(source with { AccessedAt = null });
                if (source.Status == MaterialSourceStatus.Unavailable)
                    sources.Add(source with { AccessedAt = DateTimeOffset.UnixEpoch });
                if (source.Status == MaterialSourceStatus.QuoteMatched)
                {
                    sources.Add(source with { Quote = null });
                    sources.Add(source with { Quote = " " });
                }
                else
                {
                    sources.Add(source with { Quote = "excerpt" });
                }
            }
            return sources;
        }
    }

    [Theory]
    [MemberData(nameof(ValidSources))]
    public void Validate_ConsistentSourceMetadata_AcceptsSource(LessonProvenance source)
    {
        source.Validate();
    }

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public void Validate_InconsistentSourceMetadata_RejectsWithDiagnostic(LessonProvenance source)
    {
        var exception = Assert.Throws<ArgumentException>(source.Validate);
        Assert.NotEqual("Invalid material provenance.", exception.Message);
    }
}
