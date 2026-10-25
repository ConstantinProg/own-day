using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Analysis;

public sealed class InboxAnalysisService(
    IInboxItemStore inbox,
    IInboxAnalysisStore analyses,
    IInboxAnalyzer analyzer,
    InboxAnalysisValidator validator,
    TimeProvider clock)
{
    public const int MaxInputCharacters = 16_000;
    public const int MaxProjects = 50;

    // No deadline property exists on the current targets. Unknown facts remain null.
    public static IReadOnlyList<SupportedInboxType> SupportedTypes { get; } =
        Array.AsReadOnly(new[]
        {
            new SupportedInboxType(InboxTargetKind.Action, Domain.Actions.Action.MaxTitleLength, []),
            new SupportedInboxType(InboxTargetKind.Project, Domain.Projects.Project.MaxTitleLength, []),
            new SupportedInboxType(InboxTargetKind.WaitingFor, Domain.WaitingFors.WaitingFor.MaxDescriptionLength,
                ["source", "waitingSince", "waitingSinceEvidence"]),
            new SupportedInboxType(InboxTargetKind.Reference, Domain.References.Reference.MaxTextLength, []),
            new SupportedInboxType(InboxTargetKind.SomedayMaybe, Domain.SomedayMaybes.SomedayMaybe.MaxTextLength, [])
        });

    public async Task<InboxAnalysis?> AnalyzeAsync(UserId userId, long itemId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        var item = await inbox.FindAsync(userId, itemId, cancellationToken);
        if (item?.Status != InboxItemStatus.Active)
        {
            return null;
        }

        if (item.OriginalText.Length > MaxInputCharacters)
        {
            throw new ArgumentException("Inbox text exceeds the analysis input limit.", nameof(itemId));
        }

        var projects = await analyses.GetProjectsAsync(userId, MaxProjects + 1, cancellationToken);
        var context = new InboxAnalysisContext(null, projects.Take(MaxProjects).ToArray(), projects.Count > MaxProjects);
        var contextJson = JsonSerializer.Serialize(context, InboxAnalysisJson.Options);
        var analysis = await analyses.StartAsync(userId, itemId, analyzer.Descriptor, contextJson,
            Fingerprint(contextJson), Fingerprint(item.OriginalText), clock.GetUtcNow().UtcDateTime, cancellationToken);
        if (analysis is null)
        {
            return null;
        }

        var request = new InboxAnalysisRequest(item.OriginalText, item.CapturedAt, context, SupportedTypes);
        InboxAnalyzerResponse response;
        try
        {
            response = await analyzer.AnalyzeAsync(request, cancellationToken);
        }
        catch (InboxAnalyzerException exception)
        {
            var code = exception.Code is "refused" or "incomplete_response" or "timeout" or "context_limit"
                ? exception.Code : "provider_failure";
            analysis.Finish(InboxAnalysisStatus.Failed, Now(analysis),
                JsonSerializer.Serialize(new { code }, InboxAnalysisJson.Options));
            await analyses.SaveAsync(analysis, cancellationToken);
            return analysis;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = validator.ParseAndValidate(response.Json, request);
        var diagnostics = JsonSerializer.Serialize(new
        {
            code = output is null ? "invalid_response" : "validated",
            requestId = response.RequestId,
            inputTokens = response.InputTokens,
            outputTokens = response.OutputTokens
        }, InboxAnalysisJson.Options);
        analysis.Finish(output is null ? InboxAnalysisStatus.InvalidResponse : InboxAnalysisStatus.Completed,
            Now(analysis), diagnostics, output, response.Model);
        await analyses.SaveAsync(analysis, cancellationToken);
        return analysis;
    }

    public Task<IReadOnlyList<InboxAnalysis>> GetAsync(UserId userId, long itemId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        return analyses.GetAsync(userId, itemId, cancellationToken);
    }

    private DateTime Now(InboxAnalysis analysis) =>
        new(Math.Max(clock.GetUtcNow().UtcDateTime.Ticks, analysis.CreatedAt.Ticks), DateTimeKind.Utc);

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
