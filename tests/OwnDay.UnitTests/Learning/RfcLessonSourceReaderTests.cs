using System.Net;
using System.Text;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class RfcLessonSourceReaderTests
{
    [Theory]
    [InlineData("JSON values")]
    [InlineData("json")]
    public async Task ReadAsync_KnownTopic_FetchesOnlyCatalogueUrlAndRecordsAccess(string topic)
    {
        using var handler = new Handler();
        using var reader = new RfcLessonSourceReader(new HttpClient(handler), TimeProvider.System);
        var result = await reader.ReadAsync(new(topic), CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result.Text);
        Assert.NotNull(result.AccessedAt);
        Assert.Equal(RfcLessonSourceReader.Reference, handler.Url!.ToString());
        var source = result.Provenance("model", "Theory: Literal names MUST be lowercase.", "Literal names MUST be lowercase.");
        Assert.Equal(MaterialSourceStatus.QuoteMatched, source.Status);
        source.Validate();
        Assert.Equal(MaterialSourceStatus.Retrieved, result.Provenance("model", "Invented assertion", "Invented assertion").Status);
        Assert.Equal(MaterialSourceStatus.Retrieved, result.Provenance("model", "Unrelated theory", "Literal names MUST be lowercase.").Status);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("algebra")]
    public async Task ReadAsync_UnknownTopic_DoesNotFetchUserAddress(string topic)
    {
        using var handler = new Handler();
        using var reader = new RfcLessonSourceReader(new HttpClient(handler), TimeProvider.System);
        Assert.Null(await reader.ReadAsync(new(topic), CancellationToken.None));
        Assert.Null(handler.Url);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("redirect")]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("oversized")]
    [InlineData("invalid")]
    [InlineData("html")]
    public async Task ReadAsync_UnavailableSource_DoesNotClaimVerification(string failure)
    {
        using var handler = new Handler { Failure = failure };
        using var reader = new RfcLessonSourceReader(new HttpClient(handler), TimeProvider.System);
        var result = await reader.ReadAsync(new("JSON"), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Null(result.Text);
        Assert.Null(result.AccessedAt);
        var source = result.Provenance("model", "AI theory", "invented");
        Assert.Equal(MaterialSourceStatus.Unavailable, source.Status);
        Assert.Null(source.Quote);
    }

    [Fact]
    public async Task ReadAsync_CallerCanceled_PropagatesWithoutFetching()
    {
        using var handler = new Handler();
        using var reader = new RfcLessonSourceReader(new HttpClient(handler), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new("JSON"), cancellation.Token));
        Assert.Null(handler.Url);
    }

    [Fact]
    public async Task ReadAsync_CanceledDuringFetch_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler { Cancel = cancellation };
        using var reader = new RfcLessonSourceReader(new HttpClient(handler), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new("JSON"), cancellation.Token));
        Assert.NotNull(handler.Url);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public Uri? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri;
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure == "failure") throw new HttpRequestException("private body");
            if (Failure == "timeout") throw new OperationCanceledException();
            return Task.FromResult(new HttpResponseMessage(Failure == "http" ? HttpStatusCode.ServiceUnavailable
                : Failure == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK)
            {
                Content = new StringContent(Failure == "invalid" ? "not the document"
                    : Failure == "oversized" ? new string('x', 65537) : "RFC 8259\nLiteral names MUST be lowercase.",
                    Encoding.UTF8, Failure == "html" ? "text/html" : "text/plain")
            });
        }
    }
}
