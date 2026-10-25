using System.Text.Json;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Localization;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Handling;

public static class TelegramInboxFeedback
{
    public static TelegramOutboxMessage Render(InboxAutomaticApplication application, InboxAnalysisStatus status,
        long chatId, string locale)
    {
        if (application.UndoneAt is { } undoneAt)
        {
            return new()
            {
                Id = Guid.NewGuid(), ChatId = chatId,
                Text = TelegramTexts.Get(locale, "undo.done", application.InboxItemId) + "\n" + UndoSummary(application, locale),
                Status = OutboxMessageStatus.Pending, CreatedAt = undoneAt, NextAttemptAt = undoneAt
            };
        }
        var resolution = application.ConfirmationOutcome;
        var applied = resolution == InboxConfirmationOutcome.Applied || application.Route == InboxPolicyRoute.AutoApply;
        var confirmation = resolution is null && application.Route == InboxPolicyRoute.RequireConfirmation;
        var key = resolution switch
        {
            InboxConfirmationOutcome.Rejected => "auto.rejected",
            InboxConfirmationOutcome.Stale => "auto.stale",
            _ when applied => "auto.applied",
            _ when confirmation => "auto.confirm",
            _ when status != InboxAnalysisStatus.Completed => "auto.analysis_failed",
            _ => "auto.attention"
        };
        var text = TelegramTexts.Get(locale, key, application.InboxItemId);
        if (applied || confirmation)
        {
            var proposals = JsonSerializer.Deserialize<SavedProposal[]>(application.ProposalsJson, InboxAnalysisJson.Options)!;
            var targets = JsonSerializer.Deserialize<AppliedInboxProposal[]>(resolution == InboxConfirmationOutcome.Applied
                ? application.ConfirmedTargetsJson : application.TargetsJson, InboxAnalysisJson.Options)!;
            // Reserve space for all labels/IDs and the footer, then divide the remaining text budget fairly.
            var titleLimit = Math.Min(200, 2600 / Math.Max(1, proposals.Length));
            foreach (var proposal in proposals.OrderBy(p => p.Position))
            {
                var target = targets.SingleOrDefault(t => t.ProposalId == proposal.Id);
                var identity = target is null ? string.Empty : $" #{target.TargetId}";
                text += $"\n{TelegramInboxPresenter.Label(proposal.Kind, locale)}{identity}: {Short(proposal.Title, titleLimit)}";
                if (confirmation)
                {
                    var value = JsonSerializer.Deserialize<InboxProposal>(proposal.PayloadJson, InboxAnalysisJson.Options)!;
                    if (value.Properties.Source is { } source)
                    {
                        text += "\n" + TelegramTexts.Get(locale, "auto.source", Short(source, titleLimit));
                    }
                    if (value.Properties.WaitingSince is { } since)
                    {
                        text += "\n" + TelegramTexts.Get(locale, "auto.since", since.UtcDateTime.ToString("u"));
                    }
                    if (value.ProjectIds.Length == 1)
                    {
                        text += "\n" + TelegramTexts.Get(locale, "auto.project", value.ProjectIds[0]);
                    }
                }
            }
        }
        text += "\n" + TelegramTexts.Get(locale, applied ? "auto.details" : "auto.manual", application.InboxItemId);
        InlineKeyboardMarkup? keyboard = confirmation ? new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(TelegramTexts.Get(locale, "auto.accept"), $"ia:y:{application.AnalysisId:N}"),
                InlineKeyboardButton.WithCallbackData(TelegramTexts.Get(locale, "auto.reject"), $"ia:n:{application.AnalysisId:N}")
            }
        }) : null;
        if (applied)
        {
            text += "\n" + TelegramTexts.Get(locale, "undo.offer", application.InboxItemId) + "\n" + UndoSummary(application, locale);
            keyboard = new(new[] { new[] { InlineKeyboardButton.WithCallbackData(
                TelegramTexts.Get(locale, "undo.button"), $"iu:{application.InboxItemId}") } });
        }
        var now = application.ResolvedAt ?? application.EvaluatedAt;
        return new()
        {
            Id = resolution is null ? application.AnalysisId : Guid.NewGuid(), ChatId = chatId, Text = text,
            ReplyMarkupJson = keyboard is null ? null : JsonSerializer.Serialize(keyboard),
            Status = OutboxMessageStatus.Pending, CreatedAt = now, NextAttemptAt = now
        };
    }

    public static string UndoSummary(InboxAutomaticApplication application, string locale)
    {
        var targets = JsonSerializer.Deserialize<AppliedInboxProposal[]>(
            application.ConfirmationOutcome == InboxConfirmationOutcome.Applied
                ? application.ConfirmedTargetsJson : application.TargetsJson, InboxAnalysisJson.Options)!;
        return TelegramTexts.Get(locale, "undo.summary", targets.Length) + " " + string.Join(", ",
            targets.GroupBy(t => t.Kind).Select(g => $"{TelegramInboxPresenter.Label(g.Key, locale)}: {g.Count()}"));
    }

    private sealed record SavedProposal(Guid Id, Guid AnalysisId, int Position, InboxTargetKind Kind, string Title, string PayloadJson);

    internal static string Short(string text, int limit)
    {
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= limit)
        {
            return clean;
        }
        var length = char.IsHighSurrogate(clean[limit - 1]) ? limit - 1 : limit;
        return clean[..length] + "…";
    }
}
