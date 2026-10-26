using System.Globalization;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramStatisticsCommandHandler(InboxStatisticsService statistics)
{
    public async Task<IReadOnlyList<string>> HandleAsync(UserId userId, string argument, string locale, CancellationToken cancellationToken)
    {
        var parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var detailed = parts.Length > 0 && parts[^1] == "details";
        var periodArgument = string.Join(' ', detailed ? parts[..^1] : parts);
        var period = statistics.Period(periodArgument == "7d" ? "" : periodArgument);
        if (period is null)
        {
            return [TelegramTexts.Get(locale, "stats.usage")];
        }
        var result = await statistics.ReadAsync(userId, period, cancellationToken);
        return [detailed ? Render(period, result, locale) : RenderSummary(period, result, locale)];
    }

    private static string RenderSummary(StatisticsPeriod period, InboxStatistics stats, string locale) => string.Join('\n',
        TelegramTexts.Get(locale, "stats.title", TelegramTexts.Get(locale, "stats.period." + period.Key)),
        TelegramTexts.Get(locale, "stats.received", stats.Received, stats.Processed, stats.Active, stats.Discarded),
        TelegramTexts.Get(locale, "stats.automation", stats.AutoApplied, stats.ManualRequired),
        TelegramTexts.Get(locale, "stats.created", stats.CreatedByKind[(int)InboxTargetKind.Action],
            stats.CreatedByKind[(int)InboxTargetKind.Project], stats.CreatedByKind[(int)InboxTargetKind.WaitingFor],
            stats.CreatedByKind[(int)InboxTargetKind.Reference], stats.CreatedByKind[(int)InboxTargetKind.SomedayMaybe]),
        TelegramTexts.Get(locale, "stats.summary.undo", stats.Undone),
        TelegramTexts.Get(locale, "stats.summary.more", period.Key));

    public static string Render(StatisticsPeriod period, InboxStatistics stats, string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var total = stats.AnalysisSucceeded + stats.AnalysisFailed;
        var success = total == 0 ? TelegramTexts.Get(locale, "stats.unknown") :
            (100m * stats.AnalysisSucceeded / total).ToString("0.#", culture) + "%";
        var speed = stats.MeanAnalysisSeconds?.ToString("0.#", culture) ?? TelegramTexts.Get(locale, "stats.unknown");
        var t = stats.Tokens;
        var lines = new List<string>
        {
            TelegramTexts.Get(locale, "stats.title", TelegramTexts.Get(locale, "stats.period." + period.Key)),
            TelegramTexts.Get(locale, "stats.received", stats.Received, stats.Processed, stats.Active, stats.Discarded),
            TelegramTexts.Get(locale, "stats.processing_time", stats.MeanProcessingSeconds?.ToString("0.#", culture) ??
                TelegramTexts.Get(locale, "stats.unknown")),
            TelegramTexts.Get(locale, "stats.automation", stats.AutoApplied, stats.ManualRequired),
            TelegramTexts.Get(locale, "stats.created", stats.CreatedByKind[(int)InboxTargetKind.Action],
                stats.CreatedByKind[(int)InboxTargetKind.Project], stats.CreatedByKind[(int)InboxTargetKind.WaitingFor],
                stats.CreatedByKind[(int)InboxTargetKind.Reference], stats.CreatedByKind[(int)InboxTargetKind.SomedayMaybe]),
            TelegramTexts.Get(locale, "stats.undo", stats.Undone, stats.UndoRefused, stats.ManualInterventions),
            TelegramTexts.Get(locale, "stats.analysis", stats.AnalysisSucceeded, stats.AnalysisFailed, success, speed),
            TelegramTexts.Get(locale, "stats.background", stats.ProviderAttempts, stats.Retries, stats.ProviderErrors,
                stats.AnalysisPending, stats.Stalled),
            TelegramTexts.Get(locale, "stats.worker", stats.BackgroundRetries, stats.BackgroundErrors, stats.GlobalBackgroundErrors, stats.GlobalBackgroundRetries),
            TelegramTexts.Get(locale, "stats.tokens", Known(t.Input, t.UnknownInput), Known(t.Cached, t.UnknownCached), Known(t.Output, t.UnknownOutput)),
            TelegramTexts.Get(locale, "stats.cost", Known(t.KnownEstimatedUsd, t.UnknownCost, "0.######")),
            TelegramTexts.Get(locale, "stats.legacy", stats.LegacyUnmeteredAnalyses),
            TelegramTexts.Get(locale, "stats.coverage", stats.DetailedAccountingStartedAt?.ToString("yyyy-MM-dd HH:mm", culture) ??
                TelegramTexts.Get(locale, "stats.unknown")),
            TelegramTexts.Get(locale, "stats.rules")
        };
        if (stats.LegacyUnmeteredAnalyses > 0)
        {
            var legacy = stats.LegacyTokens;
            lines.Insert(lines.Count - 2, TelegramTexts.Get(locale, "stats.legacy_tokens",
                Known(legacy.Input, legacy.UnknownInput, observations: stats.LegacyUnmeteredAnalyses),
                Known(legacy.Cached, legacy.UnknownCached, observations: stats.LegacyUnmeteredAnalyses),
                Known(legacy.Output, legacy.UnknownOutput, observations: stats.LegacyUnmeteredAnalyses)));
        }
        return string.Join('\n', lines);

        string Known(decimal value, int unknown, string format = "0", int? observations = null) => unknown == 0
            ? value.ToString(format, culture)
            : unknown == (observations ?? stats.ProviderAttempts)
                ? TelegramTexts.Get(locale, "stats.all_unknown", unknown)
                : TelegramTexts.Get(locale, "stats.partial", value.ToString(format, culture), unknown);
    }
}
