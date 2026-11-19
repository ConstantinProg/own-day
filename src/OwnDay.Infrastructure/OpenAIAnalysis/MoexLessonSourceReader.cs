using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public sealed partial class MoexLessonSourceReader(HttpClient client, TimeProvider clock) : IDisposable
{
    public const string Reference = "https://www.moex.com/a1684";
    public const string PolicyVersion = "MOEX equity settlement code v1";
    private const int MaxBytes = 512 * 1024;

    public void Dispose() => client.Dispose();

    public async Task<LessonProvenance> VerifySettlementCodeAsync(bool russian, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Reference);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var now = clock.GetUtcNow().ToUniversalTime();
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.AbsoluteUri != Reference ||
                response.Content.Headers.ContentType?.MediaType != "text/html" || response.Content.Headers.ContentLength > MaxBytes ||
                response.Headers.Date is not { } dated || dated > now.AddMinutes(5) || now - dated > TimeSpan.FromHours(24) ||
                response.Headers.Age > TimeSpan.FromHours(24)) return Unavailable();

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[MaxBytes + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
            if (count is 0 or > MaxBytes) return Unavailable();
            var html = UntrustedBlocks().Replace(Encoding.UTF8.GetString(bytes, 0, count), " ");
            var headings = MarketHeadings().Matches(html);
            var equities = headings.Where(heading => heading.Groups["market"].Value == "акций").ToArray();
            if (equities.Length != 1) return Unavailable();
            var start = equities[0].Index + equities[0].Length;
            var end = headings.FirstOrDefault(heading => heading.Index >= start)?.Index ?? html.Length;
            var supportedCodes = ModeRows().Matches(html[start..end])
                .Select(row => row.Groups["code"].Value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (supportedCodes is not ["Y1"]) return Unavailable();

            token.ThrowIfCancellationRequested();
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)))[..16];
            return new("OwnDay source verification", Reference, $"{PolicyVersion} / {fingerprint}", now, null, MaterialSourceStatus.VerifiedClaim)
            {
                VerifiedStatement = russian
                    ? "В расписании рынка акций Московской биржи рублёвый режим «Стакан Т+1» указан с кодом расчётов Y1."
                    : "The Moscow Exchange equity schedule lists settlement code Y1 for the RUB T+1 order book."
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or RegexMatchTimeoutException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    public static LessonProvenance Unavailable() => new("OwnDay source verification", Reference, PolicyVersion, null, null, MaterialSourceStatus.Unavailable);

    [GeneratedRegex(@"<!--.*?-->|<(script|style|template|noscript)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex UntrustedBlocks();

    [GeneratedRegex(@"<strong>\s*Расписание торгов на рынке (?<market>[^<]+?)\s*</strong>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex MarketHeadings();

    [GeneratedRegex(@"<td\b[^>]*>\s*Стакан\s+[ТT]\+1\s*(?:<br\s*/?>\s*)?\(RUB\)\s*</td>\s*<td\b[^>]*>(?<code>(?:(?!</td>)[\s\S])*)</td>",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ModeRows();
}
