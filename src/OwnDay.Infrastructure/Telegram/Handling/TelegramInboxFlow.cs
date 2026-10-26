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
    public async Task<IReadOnlyList<string>> CaptureAsync(UserId user, string text, string locale, long chatId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [TelegramTexts.Get(locale, "capture.usage")];
        }
        await drafts.CancelAsync(user, token);
        var item = await capture.CaptureAsync(user, text, token);
        db.TelegramInboxDestinations.Add(new() { InboxItemId = item.Id, ChatId = chatId, Locale = locale });
        return [TelegramTexts.Get(locale, "inbox.captured")];
    }

    public Task<bool> CloseAsync(UserId user, CancellationToken token) => drafts.CancelAsync(user, token);

    public async Task<IReadOnlyList<string>> BackAsync(UserId user, string locale, CancellationToken token)
    {
        var result = await drafts.BackAsync(user, token);
        return result is null ? await OpenAsync(user, "", locale, token) : await presenter.RenderAsync(user, result, locale, token);
    }

    public async Task<IReadOnlyList<string>> ProcessAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        var parts = argument.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return await OpenAsync(user, parts[0], locale, token);
        }
        if (parts.Length < 2 || !TryParseId(parts[0], out var id) || ParseTarget(parts[1]) is not { } target)
        {
            return [TelegramTexts.Get(locale, "process.usage")];
        }
        var result = await drafts.OpenAsync(user, id, token);
        if (result is not InboxDraftResult.Prompt prompt)
        {
            return await presenter.RenderAsync(user, result, locale, token);
        }
        // Explicit typed creation uses the same validated workflow without prompting for optional fields.
        await drafts.AdvanceAsync(user, prompt.Draft, prompt.Item, target, token);
        result = await drafts.AdvanceAsync(user, prompt.Draft, prompt.Item,
            new InboxDraftInput.EditText(parts.Length == 3 ? parts[2] : null), token);
        while (result is InboxDraftResult.Prompt next)
        {
            InboxDraftInput input = next.Draft.Step switch
            {
                InboxDraftStep.Source => new InboxDraftInput.SetSource(null),
                InboxDraftStep.Project => new InboxDraftInput.SelectProject(null),
                InboxDraftStep.Confirm => new InboxDraftInput.Confirm(),
                _ => throw new InvalidOperationException("Unexpected shortcut step.")
            };
            result = await drafts.AdvanceAsync(user, next.Draft, next.Item, input, token);
        }
        return await presenter.RenderAsync(user, result, locale, token);
    }

    public async Task<IReadOnlyList<string>> ResolveEntryAsync(UserId user, string argument, bool accept, string locale, CancellationToken token)
    {
        var parts = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryParseId(parts[0], out var id) || !TryParseId(parts[1], out var review))
        {
            return [TelegramTexts.Get(locale, "review.usage")];
        }
        // Bind the text command to the same immutable offer as its buttons, never to a newer review.
        var analysisId = await db.InboxAnalyses.Where(a => a.UserId == user && a.InboxItemId == id && a.Version == review)
            .Select(a => (Guid?)a.Id).SingleOrDefaultAsync(token);
        return analysisId is { } found
            ? await ResolveAsync(user, found.ToString("N"), accept, locale, token)
            : [TelegramTexts.Get(locale, "auto.unavailable")];
    }

    public async Task<IReadOnlyList<string>> UndoAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        if (!TryParseId(argument.Trim(), out var id))
        {
            return [TelegramTexts.Get(locale, "undo.usage")];
        }
        var result = await automatic.UndoAsync(user, id, token);
        if (result.FeedbackQueued)
        {
            return [];
        }
        if (result.Outcome == InboxUndoOutcome.Undone)
        {
            var application = await db.InboxAutomaticApplications.AsNoTracking()
                .SingleAsync(a => a.UserId == user && a.InboxItemId == id && a.UndoneAt != null, token);
            return [TelegramInboxFeedback.Render(application, InboxAnalysisStatus.Completed, user.Value, locale).Text];
        }
        return [TelegramTexts.Get(locale, result.Outcome switch
        {
            InboxUndoOutcome.AlreadyUndone => "undo.already",
            InboxUndoOutcome.Conflict => "undo.conflict",
            _ => "undo.unavailable"
        }, id)];
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(UserId user, string argument, bool accept, string locale,
        CancellationToken token)
    {
        if (!Guid.TryParseExact(argument.Trim(), "N", out var analysisId))
        {
            return [TelegramTexts.Get(locale, "auto.unavailable")];
        }
        var result = await automatic.ConfirmAsync(user, analysisId, accept, token);
        if (result.Changed)
        {
            if (result.FeedbackQueued)
            {
                return [];
            }
            // Entries without a proactive destination still receive a reply to this command.
            var application = await db.InboxAutomaticApplications.AsNoTracking()
                .SingleAsync(a => a.UserId == user && a.AnalysisId == analysisId, token);
            var status = await db.InboxAnalyses.Where(a => a.Id == analysisId).Select(a => a.Status).SingleAsync(token);
            return [TelegramInboxFeedback.Render(application, status, user.Value, locale).Text];
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
        if (item.Status == InboxItemStatus.Processed && application?.UndoneAt is not null)
        {
            return [TelegramTexts.Get(locale, "inbox.processed", item.Id,
                TelegramInboxPresenter.Label(item.TargetKind!.Value, locale), item.TargetId) + "\n" + original];
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
        var feedback = TelegramInboxFeedback.Render(application, status, user.Value, locale, detailed: true);
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
                if (draft.Step is InboxDraftStep.Target or InboxDraftStep.Project or InboxDraftStep.Confirm &&
                    !int.TryParse(text.Trim(), out _) && !text.Trim().Equals("no", StringComparison.OrdinalIgnoreCase) &&
                    !text.Trim().Equals("нет", StringComparison.OrdinalIgnoreCase) && chatId is { } captureChat)
                {
                    return await CaptureAsync(user, text, locale, captureChat, token);
                }
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
                item => $"#{item.Id} {TelegramInboxPresenter.Short(item.OriginalText)}\n" +
                    TelegramTexts.Get(locale, "inbox.row.actions", item.Id), locale);
        }

        if (!TryParseId(argument, out var id))
        {
            return [TelegramTexts.Get(locale, "inbox.open.usage")];
        }

        var result = await drafts.OpenAsync(user, id, token);
        return await presenter.RenderAsync(user, result, locale, token);
    }

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

    private static InboxDraftInput? ParseTarget(string value) => value.ToLowerInvariant() switch
        {
            "1" or "task" or "задача" => new InboxDraftInput.SelectTarget(InboxTargetKind.Action),
            "2" or "project" or "проект" => new InboxDraftInput.SelectTarget(InboxTargetKind.Project),
            "3" or "idea" or "идея" => new InboxDraftInput.SelectTarget(InboxTargetKind.SomedayMaybe),
            "4" or "note" or "заметка" => new InboxDraftInput.SelectTarget(InboxTargetKind.Reference),
            "5" or "waiting" or "ожидание" => new InboxDraftInput.SelectTarget(InboxTargetKind.WaitingFor),
            _ => null
        };

    private static bool TryParseId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
