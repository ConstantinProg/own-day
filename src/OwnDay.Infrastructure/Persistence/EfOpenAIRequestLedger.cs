using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;

namespace OwnDay.Infrastructure.Persistence;

// Independent short transactions: a paid response must survive losing the analysis lease or Inbox transaction.
public sealed class EfOpenAIRequestLedger(IServiceScopeFactory scopes, IOptions<OpenAICostOptions> options,
    TimeProvider clock, ILogger<EfOpenAIRequestLedger> logger) : IOpenAIRequestLedger
{
    private const long BudgetLock = -8042281001;

    public async Task<Guid?> AdmitAsync(InboxAnalysisRequest request, InboxAnalyzerDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (request.AnalysisId is not { } analysisId || request.OwnerId is not > 0)
        {
            throw new InvalidOperationException("A persisted, owned analysis is required for metered requests.");
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BudgetLock})", cancellationToken);
        }
        if (!await db.InboxAnalyses.AnyAsync(a => a.Id == analysisId && a.UserId == new OwnDay.Domain.UserId(request.OwnerId.Value), cancellationToken))
        {
            throw new InvalidOperationException("Analysis ownership does not match the request.");
        }
        var settings = options.Value;
        var charged = await db.OpenAIRequestAttempts.SumAsync(a => (long?)a.ChargedTokens, cancellationToken) ?? 0;
        if (settings.TokenLimit is { } limit && (charged >= limit || settings.TokensPerRequest > limit - charged))
        {
            logger.LogWarning("OpenAI admission blocked for analysis {AnalysisId}: {Code}, accounted tokens {Tokens}, limit {Limit}.",
                analysisId, "budget_limit", charged, limit);
            return null;
        }
        var row = new OpenAIRequestAttempt
        {
            Sequence = await db.OpenAIRequestAttempts.CountAsync(a => a.AnalysisId == analysisId, cancellationToken) + 1,
            AnalysisId = analysisId, UserId = request.OwnerId.Value, Model = descriptor.Model,
            StartedAt = clock.GetUtcNow().UtcDateTime,
            ReservedTokens = settings.TokensPerRequest, ChargedTokens = settings.TokensPerRequest
        };
        db.OpenAIRequestAttempts.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("OpenAI request {AttemptId} admitted for analysis {AnalysisId}, model {Model}, reservation {Tokens}.",
            row.Id, analysisId, row.Model, row.ReservedTokens);
        return row.Id;
    }

    public async Task FinishAsync(Guid id, string code, InboxAnalyzerResponse? response, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Same lock fences reconciliation and admission, including concurrent duplicate completion.
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BudgetLock})", cancellationToken);
        }
        var row = await db.OpenAIRequestAttempts.SingleAsync(a => a.Id == id, cancellationToken);
        if (row.FinishedAt is not null)
        {
            return;
        }
        row.FinishedAt = clock.GetUtcNow().UtcDateTime;
        row.Code = code;
        row.InputTokens = response?.InputTokens;
        row.CachedInputTokens = response?.CachedInputTokens;
        row.OutputTokens = response?.OutputTokens;
        row.ProviderRequestId = response?.RequestId;
        row.Model = response?.Model ?? row.Model;
        // Exact model match only: no guessing prices for snapshots or custom aliases.
        var price = options.Value.Models.FirstOrDefault(p => p.Key == row.Model).Value;
        row.InputUsdPerMillion = price?.InputUsdPerMillion;
        row.CachedInputUsdPerMillion = price?.CachedInputUsdPerMillion;
        row.OutputUsdPerMillion = price?.OutputUsdPerMillion;
        row.EstimatedUsd = price?.Estimate(row.InputTokens, row.CachedInputTokens, row.OutputTokens);
        if (row.InputTokens is >= 0 && row.OutputTokens is >= 0)
        {
            row.ChargedTokens = (long)row.InputTokens.Value + row.OutputTokens.Value;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("OpenAI request {AttemptId}, analysis {AnalysisId}: {Code}, input {InputTokens}, cached {CachedTokens}, output {OutputTokens}, estimated USD {EstimatedUsd}.",
            id, row.AnalysisId, code, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.EstimatedUsd);
    }
}
