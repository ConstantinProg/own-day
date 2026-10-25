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
        var outcome = await EvaluateAsync(request, cancellationToken);
        analysis.Finish(outcome.Status, Now(analysis), outcome.DiagnosticsJson, outcome.Output, outcome.Model);
        await analyses.SaveAsync(analysis, cancellationToken);
        return analysis;
    }

    public async Task<string> ProcessJobAsync(InboxAnalysisLease lease, IInboxAnalysisJobStore jobs,
        InboxAnalysisProcessingOptions options, CancellationToken cancellationToken)
    {
        InboxAnalysisOutcome outcome;
        var item = await inbox.FindAsync(lease.UserId, lease.ItemId, cancellationToken);
        if (item?.Status != InboxItemStatus.Active)
        {
            outcome = InboxAnalysisOutcome.Failure(InboxAnalysisCode.ItemInactive);
        }
        else if (lease.Attempt > options.MaxAttempts)
        {
            outcome = InboxAnalysisOutcome.Failure(InboxAnalysisCode.AttemptsExhausted);
        }
        else if (item.OriginalText.Length > MaxInputCharacters)
        {
            outcome = InboxAnalysisOutcome.Failure(InboxAnalysisCode.InputLimit);
        }
        else
        {
            var projects = await analyses.GetProjectsAsync(lease.UserId, MaxProjects + 1, cancellationToken);
            var context = new InboxAnalysisContext(null, projects.Take(MaxProjects).ToArray(), projects.Count > MaxProjects);
            var json = JsonSerializer.Serialize(context, InboxAnalysisJson.Options);
            var analysis = await jobs.StartAsync(lease, analyzer.Descriptor, json, Fingerprint(json),
                Fingerprint(item.OriginalText), cancellationToken);
            if (analysis is null)
            {
                return Report(await jobs.FinishAsync(lease, InboxAnalysisOutcome.Failure(InboxAnalysisCode.ItemInactive), null, cancellationToken));
            }

            if (analysis.Status != InboxAnalysisStatus.Pending)
            {
                return Report(await jobs.FinishAsync(lease, InboxAnalysisOutcome.Failure(InboxAnalysisCode.AlreadyFinished), null, cancellationToken));
            }

            var descriptor = new InboxAnalyzerDescriptor(analysis.Model, analysis.SchemaVersion, analysis.PromptVersion,
                analysis.ReasoningEffort, analysis.MaxOutputTokens, analysis.TimeoutSeconds);
            if (descriptor != analyzer.Descriptor)
            {
                return Report(await jobs.FinishAsync(lease, InboxAnalysisOutcome.Failure(InboxAnalysisCode.DescriptorMismatch), null, cancellationToken));
            }

            // A retry uses the committed context snapshot of the logical analysis.
            context = JsonSerializer.Deserialize<InboxAnalysisContext>(analysis.ContextJson, InboxAnalysisJson.Options)!;
            var request = new InboxAnalysisRequest(item.OriginalText, item.CapturedAt, context, SupportedTypes);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                // WaitAsync bounds even an adapter that fails to honor cancellation. Evaluation has no persistence side effects.
                outcome = await EvaluateAsync(request, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = InboxAnalysisOutcome.Failure(InboxAnalysisCode.Timeout);
            }
        }

        var retry = options.RetryAfter(outcome.Code, lease.Attempt, Random.Shared.NextDouble());
        var saved = await jobs.FinishAsync(lease, outcome, retry, cancellationToken);
        return Report(saved);
    }

    private static string Report(InboxAnalysisJobResult? result) => result is null ? InboxAnalysisCode.LeaseLost.ToStorageValue()
        : result.Status == InboxAnalysisJobStatus.Ready ? "retry_" + result.Code.ToStorageValue() : result.Code.ToStorageValue();

    private async Task<InboxAnalysisOutcome> EvaluateAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
    {
        InboxAnalyzerResponse response;
        try
        {
            response = await analyzer.AnalyzeAsync(request, cancellationToken);
        }
        catch (InboxAnalyzerException exception)
        {
            var code = exception.Code is InboxAnalysisCode.Refused or InboxAnalysisCode.IncompleteResponse
                or InboxAnalysisCode.Timeout or InboxAnalysisCode.ContextLimit or InboxAnalysisCode.RateLimit
                or InboxAnalysisCode.BudgetLimit or InboxAnalysisCode.Configuration
                or InboxAnalysisCode.PermanentFailure or InboxAnalysisCode.InvalidResponse
                ? exception.Code : InboxAnalysisCode.ProviderFailure;
            return InboxAnalysisOutcome.Failure(code);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = validator.ParseAndValidate(response.Json, request);
        var diagnostics = JsonSerializer.Serialize(new
        {
            code = (output is null ? InboxAnalysisCode.InvalidResponse : InboxAnalysisCode.Validated).ToStorageValue(),
            requestId = response.RequestId,
            inputTokens = response.InputTokens,
            outputTokens = response.OutputTokens
        }, InboxAnalysisJson.Options);
        return new(output is null ? InboxAnalysisCode.InvalidResponse : InboxAnalysisCode.Validated, output, response.Model, diagnostics);
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
