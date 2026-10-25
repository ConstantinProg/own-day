using OwnDay.Domain;

namespace OwnDay.Application.Inbox.Observability;

public sealed record StatisticsPeriod(string Key, DateTime Start, DateTime End)
{
    // UTC calendar days, inclusive lower / exclusive upper boundary. End is the captured report instant.
    public static StatisticsPeriod? Parse(string argument, DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("UTC is required.", nameof(utcNow));
        }
        return argument.Trim() switch
        {
            "" => new("7d", utcNow.Date.AddDays(-6), utcNow),
            "today" => new("today", utcNow.Date, utcNow),
            "30d" => new("30d", utcNow.Date.AddDays(-29), utcNow),
            "all" => new("all", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), utcNow),
            _ => null
        };
    }
}

public sealed record TokenStatistics(long Input, long Cached, long Output, decimal KnownEstimatedUsd,
    int UnknownInput, int UnknownCached, int UnknownOutput, int UnknownCost)
{
    public static TokenStatistics Aggregate(IEnumerable<OpenAIRequestAttempt> attempts)
    {
        var rows = attempts.ToArray();
        return new(rows.Sum(a => (long?)a.InputTokens ?? 0), rows.Sum(a => (long?)a.CachedInputTokens ?? 0),
            rows.Sum(a => (long?)a.OutputTokens ?? 0), rows.Sum(a => a.EstimatedUsd ?? 0),
            rows.Count(a => a.InputTokens is null), rows.Count(a => a.CachedInputTokens is null),
            rows.Count(a => a.OutputTokens is null), rows.Count(a => a.EstimatedUsd is null));
    }
}

public sealed record InboxStatistics(
    int Received, int Processed, int Active, int Discarded,
    int AutoApplied, int ManualRequired, int ManualInterventions, int Undone, int UndoRefused,
    IReadOnlyList<int> CreatedByKind,
    int AnalysisSucceeded, int AnalysisFailed, double? MeanAnalysisSeconds, int AnalysisPending,
    int ProviderAttempts, int Retries, int ProviderErrors, int Stalled, int LegacyUnmeteredAnalyses,
    TokenStatistics Tokens, int BackgroundRetries, int BackgroundErrors, int GlobalBackgroundErrors, int GlobalBackgroundRetries,
    TokenStatistics LegacyTokens, DateTime? DetailedAccountingStartedAt, double? MeanProcessingSeconds);

public interface IInboxStatisticsStore
{
    Task<InboxStatistics> ReadAsync(UserId userId, StatisticsPeriod period, CancellationToken cancellationToken);
}

public sealed class InboxStatisticsService(IInboxStatisticsStore store, TimeProvider clock)
{
    public Task<InboxStatistics> ReadAsync(UserId userId, StatisticsPeriod period, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        return store.ReadAsync(userId, period, cancellationToken);
    }

    public StatisticsPeriod? Period(string argument) => StatisticsPeriod.Parse(argument, clock.GetUtcNow().UtcDateTime);
}
