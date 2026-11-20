using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;

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
        return await AdmitCoreAsync(analysisId, null, request.OwnerId, descriptor, cancellationToken);
    }

    public Task<Guid?> AdmitLearningAsync(Guid physicalAttemptId, InboxAnalyzerDescriptor descriptor, CancellationToken cancellationToken) =>
        AdmitCoreAsync(null, physicalAttemptId, null, descriptor, cancellationToken);

    private async Task<Guid?> AdmitCoreAsync(Guid? analysisId, Guid? physicalAttemptId, long? expectedOwner,
        InboxAnalyzerDescriptor descriptor, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockBudgetAsync(db, cancellationToken);
        long owner;
        int sequence;
        if (analysisId is { } inboxId)
        {
            var inboxOwner = expectedOwner ?? throw new InvalidOperationException("Inbox ownership is required.");
            if (!await db.InboxAnalyses.AnyAsync(a => a.Id == inboxId && a.UserId == new OwnDay.Domain.UserId(inboxOwner), cancellationToken))
                throw new InvalidOperationException("Analysis ownership does not match the request.");
            owner = inboxOwner;
            sequence = await db.OpenAIRequestAttempts.CountAsync(a => a.AnalysisId == inboxId, cancellationToken) + 1;
        }
        else
        {
            var source = await (from attempt in db.Set<LearningProviderAttempt>()
                                join request in db.Set<LearningRequest>() on attempt.RequestId equals request.Id
                                where attempt.Id == physicalAttemptId && attempt.FinishedAt == null
                                select new { request.UserId, attempt.Number }).SingleOrDefaultAsync(cancellationToken);
            if (source is null) return null;
            if (await db.OpenAIRequestAttempts.AnyAsync(a => a.LearningProviderAttemptId == physicalAttemptId, cancellationToken)) return null;
            owner = source.UserId.Value;
            sequence = source.Number;
        }
        var settings = options.Value;
        var now = db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken)
            : clock.GetUtcNow().UtcDateTime;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = month.AddMonths(1);
        var monthly = db.OpenAIRequestAttempts.Where(a => a.StartedAt >= month && a.StartedAt < end);
        var userMonthly = monthly.Where(a => a.UserId == owner);
        var carryover = db.Set<OpenAIBudgetCarryover>();
        var monthlyCarryover = carryover.Where(total => total.Month == DateOnly.FromDateTime(month));
        var carried = await ReadCarriedBudgetAsync(monthlyCarryover, cancellationToken);
        var userCarried = await ReadCarriedBudgetAsync(monthlyCarryover.Where(total => total.UserId == owner), cancellationToken);
        var price = settings.Models.GetValueOrDefault(descriptor.Model);
        decimal? reservation = price?.InputUsdPerMillion is { } inputPrice && price.OutputUsdPerMillion is { } outputPrice &&
            price.CachedInputUsdPerMillion is { } cachedPrice
            ? settings.TokensPerRequest * Math.Max(inputPrice, Math.Max(outputPrice, cachedPrice)) / 1_000_000m : null;
        var charged = settings.TokenLimit is null ? 0 :
            (await db.OpenAIRequestAttempts.SumAsync(a => (long?)a.ChargedTokens, cancellationToken) ?? 0) +
            await carryover.SumAsync(total => total.ChargedTokens, cancellationToken);
        var monthlyTokens = carried.Tokens + (await monthly.SumAsync(a => (long?)a.ChargedTokens, cancellationToken) ?? 0);
        var userTokens = userCarried.Tokens + (await userMonthly.SumAsync(a => (long?)a.ChargedTokens, cancellationToken) ?? 0);
        var monthlyUsd = carried.Usd + (await monthly.SumAsync(a => a.ChargedUsd ?? a.EstimatedUsd, cancellationToken) ?? 0);
        if (settings.TokenLimit is { } limit && settings.TokensPerRequest > limit - charged ||
            settings.TokensPerRequest > settings.MonthlyTokenLimit - monthlyTokens ||
            settings.TokensPerRequest > settings.MonthlyUserTokenLimit - userTokens ||
            carried.Calls + await monthly.LongCountAsync(cancellationToken) >= settings.MonthlyCallLimit ||
            userCarried.Calls + await userMonthly.LongCountAsync(cancellationToken) >= settings.MonthlyUserCallLimit ||
            settings.MonthlyUsdLimit is { } usdLimit && (reservation is null || reservation > usdLimit - monthlyUsd ||
                carried.HasUnknownCost || await monthly.AnyAsync(a => a.ChargedUsd == null && a.EstimatedUsd == null, cancellationToken)))
        {
            logger.LogWarning("OpenAI admission blocked for owner {Owner}: {Code}.", owner, "budget_limit");
            return null;
        }
        var row = new OpenAIRequestAttempt
        {
            Sequence = sequence, AnalysisId = analysisId, LearningProviderAttemptId = physicalAttemptId,
            UserId = owner, Model = descriptor.Model, StartedAt = now,
            ReservedTokens = settings.TokensPerRequest, ChargedTokens = settings.TokensPerRequest,
            ReservedUsd = reservation, ChargedUsd = reservation,
            PromptVersion = descriptor.PromptVersion, SchemaVersion = descriptor.SchemaVersion, MaxOutputTokens = descriptor.MaxOutputTokens,
            InputUsdPerMillion = price?.InputUsdPerMillion, CachedInputUsdPerMillion = price?.CachedInputUsdPerMillion,
            OutputUsdPerMillion = price?.OutputUsdPerMillion
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
        await LockBudgetAsync(db, cancellationToken);
        var row = await db.OpenAIRequestAttempts.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (row is null || row.FinishedAt is not null)
        {
            return;
        }
        row.FinishedAt = db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken)
            : clock.GetUtcNow().UtcDateTime;
        row.Code = code;
        row.InputTokens = response?.InputTokens;
        row.CachedInputTokens = response?.CachedInputTokens;
        row.OutputTokens = response?.OutputTokens;
        row.ProviderRequestId = response?.RequestId;
        // Exact model match only: no guessing prices for snapshots or custom aliases.
        var price = response?.Model is null || response.Model == row.Model
            ? new OpenAIModelPrice { InputUsdPerMillion = row.InputUsdPerMillion,
                CachedInputUsdPerMillion = row.CachedInputUsdPerMillion, OutputUsdPerMillion = row.OutputUsdPerMillion }
            : options.Value.Models.GetValueOrDefault(response.Model);
        row.Model = response?.Model ?? row.Model;
        row.InputUsdPerMillion = price?.InputUsdPerMillion;
        row.CachedInputUsdPerMillion = price?.CachedInputUsdPerMillion;
        row.OutputUsdPerMillion = price?.OutputUsdPerMillion;
        row.EstimatedUsd = price?.Estimate(row.InputTokens, row.CachedInputTokens, row.OutputTokens);
        row.ChargedUsd = row.EstimatedUsd ?? row.ReservedUsd;
        if (row.InputTokens is >= 0 && row.OutputTokens is >= 0)
        {
            row.ChargedTokens = (long)row.InputTokens.Value + row.OutputTokens.Value;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("OpenAI request {AttemptId}, analysis {AnalysisId}: {Code}, input {InputTokens}, cached {CachedTokens}, output {OutputTokens}, estimated USD {EstimatedUsd}.",
            id, row.AnalysisId, code, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.EstimatedUsd);
    }

    internal static async Task PreserveOwnerChargesAsync(OwnDayDbContext db, long userId, CancellationToken cancellationToken)
    {
        await LockBudgetAsync(db, cancellationToken);
        var attempts = db.OpenAIRequestAttempts.Where(attempt => attempt.UserId == userId);
        var totals = await attempts.GroupBy(attempt => new { attempt.StartedAt.Year, attempt.StartedAt.Month })
            .Select(month => new
            {
                month.Key.Year,
                month.Key.Month,
                Calls = month.LongCount(),
                Tokens = month.Sum(attempt => attempt.ChargedTokens),
                Usd = month.Sum(attempt => attempt.ChargedUsd ?? attempt.EstimatedUsd ?? 0),
                HasUnknownCost = month.Any(attempt => attempt.ChargedUsd == null && attempt.EstimatedUsd == null)
            }).ToListAsync(cancellationToken);
        foreach (var source in totals)
        {
            var month = new DateOnly(source.Year, source.Month, 1);
            var carryover = await db.Set<OpenAIBudgetCarryover>().FindAsync([userId, month], cancellationToken);
            if (carryover is null)
            {
                carryover = new OpenAIBudgetCarryover { UserId = userId, Month = month };
                db.Add(carryover);
            }
            carryover.CallCount += source.Calls;
            carryover.ChargedTokens += source.Tokens;
            carryover.ChargedUsd += source.Usd;
            carryover.HasUnknownCost |= source.HasUnknownCost;
        }
        await attempts.ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task LockBudgetAsync(OwnDayDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BudgetLock})", cancellationToken);
    }

    private static async Task<CarriedBudget> ReadCarriedBudgetAsync(IQueryable<OpenAIBudgetCarryover> totals, CancellationToken cancellationToken) =>
        await totals.GroupBy(_ => 1)
            .Select(month => new CarriedBudget(month.Sum(total => total.CallCount), month.Sum(total => total.ChargedTokens),
                month.Sum(total => total.ChargedUsd), month.Any(total => total.HasUnknownCost)))
            .SingleOrDefaultAsync(cancellationToken);

    private readonly record struct CarriedBudget(long Calls, long Tokens, decimal Usd, bool HasUnknownCost);
}
