using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxFlow(
    InboxDraftService drafts,
    InboxCaptureService capture,
    TelegramInboxPresenter presenter,
    OwnDayDbContext db,
    InboxAutomaticProcessingService automatic)
{
    public async Task<IReadOnlyList<string>> ResolveAsync(UserId user, string argument, bool accept, string locale,
        CancellationToken token)
    {
        if (!Guid.TryParseExact(argument.Trim(), "N", out var analysisId))
        {
            return [TelegramTexts.Get(locale, "auto.unavailable")];
        }
        var result = await automatic.ConfirmAsync(user, analysisId, accept, token);
        // The first resolution queues its own durable feedback in the same transaction.
        if (result.Changed)
        {
            return [];
        }
        return [TelegramTexts.Get(locale, result.Outcome switch
        {
            InboxConfirmationOutcome.Applied => "auto.already_applied",
            InboxConfirmationOutcome.Rejected => "auto.already_rejected",
            _ => "auto.unavailable"
        })];
    }

    public async Task<IReadOnlyList<string>> ResultAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        if (!TryParseId(argument.Trim(), out var itemId) || await capture.FindAsync(user, itemId, token) is not { } item)
        {
            return [TelegramTexts.Get(locale, "inbox.open.not_found")];
        }
        var application = await db.InboxAutomaticApplications.AsNoTracking()
            .Where(a => a.UserId == user && a.InboxItemId == itemId)
            .OrderByDescending(a => a.Route == InboxPolicyRoute.AutoApply || a.ConfirmationOutcome == InboxConfirmationOutcome.Applied)
            .ThenByDescending(a => a.EvaluatedAt).FirstOrDefaultAsync(token);
        var original = TelegramTexts.Get(locale, "auto.original", item.Id, TelegramInboxPresenter.Short(item.OriginalText));
        if (item.Status == InboxItemStatus.Discarded)
        {
            return [TelegramTexts.Get(locale, "inbox.discard.already", item.Id) + "\n" + original];
        }
        if (item.Status == InboxItemStatus.Processed && application?.Route != InboxPolicyRoute.AutoApply &&
            application?.ConfirmationOutcome != InboxConfirmationOutcome.Applied)
        {
            return [TelegramTexts.Get(locale, "inbox.processed", item.Id,
                TelegramInboxPresenter.Label(item.TargetKind!.Value, locale), item.TargetId) + "\n" + original];
        }
        if (application is null)
        {
            return [original + "\n" + TelegramTexts.Get(locale, "auto.manual", item.Id)];
        }
        var status = await db.InboxAnalyses.Where(a => a.Id == application.AnalysisId).Select(a => a.Status).SingleAsync(token);
        var feedback = TelegramInboxFeedback.Render(application, status, user.Value, locale);
        return [feedback.Text + "\n" + TelegramTexts.Get(locale, "auto.original", item.Id,
            TelegramInboxFeedback.Short(item.OriginalText, Math.Min(500, Math.Max(1, 4020 - feedback.Text.Length))))];
    }

    public async Task<IReadOnlyList<string>> CaptureOrContinueAsync(UserId user, string text, string locale, CancellationToken token,
        long? chatId = null)
    {
        var result = await drafts.CaptureOrLoadAsync(user, text, token);
        if (result is InboxDraftResult.Notice { ItemId: { } capturedId } && chatId is { } destination)
        {
            db.TelegramInboxDestinations.Add(new() { InboxItemId = capturedId, ChatId = destination, Locale = locale });
        }
        if (result is InboxDraftResult.Prompt { Draft: var draft, Item: var item })
        {
            var input = ParseInput(draft.Step, text.Trim());
            if (input is null)
            {
                var key = draft.Step switch
                {
                    InboxDraftStep.Target => "inbox.target.invalid",
                    InboxDraftStep.Project => "inbox.project.not_found",
                    InboxDraftStep.Confirm => "inbox.confirm.invalid",
                    _ => throw new InvalidOperationException("Unexpected input for draft step.")
                };
                return [TelegramTexts.Get(locale, key)];
            }

            result = await drafts.AdvanceAsync(user, draft, item, input, token);
        }

        return await presenter.RenderAsync(user, result, locale, token);
    }

    public async Task<IReadOnlyList<string>> OpenAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var items = await capture.GetActiveAsync(user, token);
            return TelegramTextLists.Render(TelegramTexts.Get(locale, "inbox.title"), items,
                item => $"#{item.Id} {TelegramInboxPresenter.Short(item.OriginalText)} \u2014 /inbox {item.Id}", locale);
        }

        if (!TryParseId(argument, out var id))
        {
            return [TelegramTexts.Get(locale, "inbox.open.usage")];
        }

        var result = await drafts.OpenAsync(user, id, token);
        return await presenter.RenderAsync(user, result, locale, token);
    }

    public async Task<IReadOnlyList<string>> CancelAsync(UserId user, string locale, CancellationToken token) =>
        [TelegramTexts.Get(locale, await drafts.CancelAsync(user, token) ? "inbox.cancel.done" : "inbox.cancel.none")];

    public async Task<IReadOnlyList<string>> DiscardAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        long? id = null;
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (!TryParseId(argument, out var parsed))
            {
                return [TelegramTexts.Get(locale, "inbox.discard.usage")];
            }

            id = parsed;
        }

        var result = await drafts.DiscardAsync(user, id, token);
        var key = result.Result switch
        {
            DiscardInboxItemResult.Discarded => "inbox.discard.done",
            DiscardInboxItemResult.AlreadyProcessed => "inbox.discard.processed",
            DiscardInboxItemResult.AlreadyDiscarded => "inbox.discard.already",
            DiscardInboxItemResult.NotFound => "inbox.discard.not_found",
            null => "inbox.discard.usage",
            _ => throw new InvalidOperationException("Unknown discard result.")
        };
        return [TelegramTexts.Get(locale, key, result.ItemId)];
    }

    private static InboxDraftInput? ParseInput(InboxDraftStep step, string value) => step switch
    {
        InboxDraftStep.Target => ParseTarget(value),
        InboxDraftStep.Text => new InboxDraftInput.EditText(value == "." ? null : value),
        InboxDraftStep.Source => new InboxDraftInput.SetSource(value == "." ? null : value),
        InboxDraftStep.Project => value is "0" or "."
            ? new InboxDraftInput.SelectProject(null)
            : TryParseId(value, out var id) ? new InboxDraftInput.SelectProject(id) : null,
        InboxDraftStep.Confirm => value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("да", StringComparison.OrdinalIgnoreCase) ? new InboxDraftInput.Confirm() : null,
        _ => throw new InvalidOperationException("Unknown inbox draft step.")
    };

    private static InboxDraftInput? ParseTarget(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) ? choice switch
        {
            1 => new InboxDraftInput.SelectTarget(InboxTargetKind.Action),
            2 => new InboxDraftInput.SelectTarget(InboxTargetKind.Project),
            3 => new InboxDraftInput.SelectTarget(InboxTargetKind.SomedayMaybe),
            4 => new InboxDraftInput.SelectTarget(InboxTargetKind.Reference),
            5 => new InboxDraftInput.SelectTarget(InboxTargetKind.WaitingFor),
            _ => null
        } : null;

    private static bool TryParseId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
