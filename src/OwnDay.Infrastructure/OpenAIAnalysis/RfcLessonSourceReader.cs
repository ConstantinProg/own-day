using System.Text;
using System.Text.RegularExpressions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed record RetrievedLessonSource(string Reference, string Version, DateTimeOffset? AccessedAt, string? Text)
{
    public LessonProvenance Provenance(string generator, string theory, string? quote)
    {
        var matched = !string.IsNullOrWhiteSpace(theory) && !string.IsNullOrWhiteSpace(quote) && quote.Length <= 500 && Text is not null &&
            NormalizedTextAnswerCheck.Normalize(Text).Contains(NormalizedTextAnswerCheck.Normalize(quote), StringComparison.Ordinal) &&
            NormalizedTextAnswerCheck.Normalize(theory).Contains(NormalizedTextAnswerCheck.Normalize(quote), StringComparison.Ordinal);
        return new(generator, Reference, Version, AccessedAt, matched ? quote : null,
            Text is null ? MaterialSourceStatus.Unavailable : matched ? MaterialSourceStatus.QuoteMatched : MaterialSourceStatus.Retrieved);
    }
}

// The catalogue owns URLs. User/model text never selects a network address.
public sealed class RfcLessonSourceReader(HttpClient client, TimeProvider clock) : IDisposable
{
    public const string Reference = "https://www.rfc-editor.org/rfc/rfc8259.txt";
    private const int MaximumBytes = 65536;

    public void Dispose() => client.Dispose();

    public async Task<RetrievedLessonSource?> ReadAsync(LessonRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Regex.IsMatch(request.Topic + " " + request.Program?.Discipline, @"\bJSON\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.GetAsync(Reference, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes ||
                response.Content.Headers.ContentType?.MediaType != "text/plain") return Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > MaximumBytes) return Unavailable();
            var text = Encoding.UTF8.GetString(bytes, 0, count);
            if (!text.Contains("RFC 8259", StringComparison.Ordinal)) return Unavailable();
            token.ThrowIfCancellationRequested();
            return new(Reference, "RFC 8259 / December 2017", clock.GetUtcNow(), text[..Math.Min(text.Length, 16000)]);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    private static RetrievedLessonSource Unavailable() => new(Reference, "RFC 8259 / December 2017", null, null);
}
