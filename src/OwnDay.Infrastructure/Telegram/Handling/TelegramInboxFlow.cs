using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxFlow(
    OwnDayDbContext db,
    InboxCaptureService capture,
    InboxProcessingService processing,
    StructuredItemService structured,
    TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<string>> CaptureOrContinueAsync(UserId user, string text, string locale, CancellationToken token)
    {
        var expired = await db.TelegramInboxDrafts.AnyAsync(row => row.UserId == user.Value && row.ExpiresAt <= Now(), token);
        if (expired)
        {
            var stale = await db.TelegramInboxDrafts.SingleAsync(row => row.UserId == user.Value, token);
            db.TelegramInboxDrafts.Remove(stale);
            await capture.CaptureAsync(user, text, token);
            return [TelegramTexts.Get(locale, "inbox.draft.expired")];
        }

        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            await capture.CaptureAsync(user, text, token);
            return [TelegramTexts.Get(locale, "inbox.captured")];
        }

        var item = await capture.FindAsync(user, draft.InboxItemId, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            db.TelegramInboxDrafts.Remove(draft);
            return [TelegramTexts.Get(locale, "inbox.draft.unavailable")];
        }

        var value = text.Trim();
        switch (draft.Step)
        {
            case TelegramInboxDraftStep.Target:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice is < 1 or > 5)
                {
                    return [TelegramTexts.Get(locale, "inbox.target.invalid")];
                }

                draft.TargetKind = (InboxTargetKind)(choice - 1);
                draft.Text = item.OriginalText;
                draft.Step = TelegramInboxDraftStep.Text;
                break;
            case TelegramInboxDraftStep.Text:
                var edited = value == "." ? draft.Text : value;
                if (string.IsNullOrWhiteSpace(edited) || edited.Trim().Length > 200)
                {
                    return [TelegramTexts.Get(locale, "inbox.text.invalid")];
                }

                draft.Text = edited.Trim();
                draft.Step = draft.TargetKind == InboxTargetKind.WaitingFor
                    ? TelegramInboxDraftStep.Source
                    : draft.TargetKind == InboxTargetKind.Project
                        ? TelegramInboxDraftStep.Confirm
                        : TelegramInboxDraftStep.Project;
                break;
            case TelegramInboxDraftStep.Source:
                if (value != "." && value.Length > 200)
                {
                    return [TelegramTexts.Get(locale, "inbox.source.invalid")];
                }

                draft.Source = value == "." ? null : value;
                draft.Step = TelegramInboxDraftStep.Project;
                break;
            case TelegramInboxDraftStep.Project:
                if (value is "0" or ".")
                {
                    draft.ProjectId = null;
                }
                else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId) && projectId > 0 &&
                    (await structured.FindProjectAsync(user, projectId, token)) is { Status: OwnDay.Domain.Projects.ProjectStatus.Active })
                {
                    draft.ProjectId = projectId;
                }
                else
                {
                    return [TelegramTexts.Get(locale, "inbox.project.not_found")];
                }

                draft.Step = TelegramInboxDraftStep.Confirm;
                break;
            case TelegramInboxDraftStep.Confirm:
                if (!value.Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("да", StringComparison.OrdinalIgnoreCase))
                {
                    return [TelegramTexts.Get(locale, "inbox.confirm.invalid")];
                }

                var result = await processing.ProcessAsync(user, new ProcessInboxItem(
                    draft.InboxItemId, draft.TargetKind!.Value, draft.Text!, draft.ProjectId, draft.Source), token);
                if (result.Status == ProcessInboxItemStatus.InvalidProject)
                {
                    draft.Step = TelegramInboxDraftStep.Project;
                    return [TelegramTexts.Get(locale, "inbox.project.unavailable")];
                }

                if (result.Status == ProcessInboxItemStatus.InvalidTargetData)
                {
                    draft.Step = TelegramInboxDraftStep.Text;
                    return [TelegramTexts.Get(locale, "inbox.data.invalid")];
                }

                db.TelegramInboxDrafts.Remove(draft);
                return result.Status == ProcessInboxItemStatus.Processed
                    ? [TelegramTexts.Get(locale, "inbox.processed", draft.InboxItemId, Label(draft.TargetKind.Value, locale), result.TargetId)]
                    : [TelegramTexts.Get(locale, "inbox.processed.unavailable")];
        }

        draft.Version++;
        draft.ExpiresAt = Now().Add(Lifetime);
        return draft.Step switch
        {
            TelegramInboxDraftStep.Text => [TelegramTexts.Get(locale, "inbox.text.prompt", Label(draft.TargetKind!.Value, locale), Short(item.OriginalText))],
            TelegramInboxDraftStep.Source => [TelegramTexts.Get(locale, "inbox.source.prompt")],
            TelegramInboxDraftStep.Project => await ProjectPromptAsync(user, locale, token),
            _ => [await SummaryAsync(user, item.OriginalText, draft, locale, token)]
        };
    }

    public async Task<IReadOnlyList<string>> OpenAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var items = await capture.GetActiveAsync(user, token);
            return TelegramTextLists.Render(TelegramTexts.Get(locale, "inbox.title"), items,
                item => $"#{item.Id} {Short(item.OriginalText)} — /inbox {item.Id}", locale);
        }

        if (!long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return [TelegramTexts.Get(locale, "inbox.open.usage")];
        }

        var item = await capture.FindAsync(user, id, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            return [TelegramTexts.Get(locale, "inbox.open.not_found")];
        }

        var current = await LoadAsync(user, token);
        if (current is not null)
        {
            db.TelegramInboxDrafts.Remove(current);
            await db.SaveChangesAsync(token);
        }

        db.TelegramInboxDrafts.Add(new TelegramInboxDraft
        {
            UserId = user.Value,
            InboxItemId = id,
            Step = TelegramInboxDraftStep.Target,
            ExpiresAt = Now().Add(Lifetime)
        });
        return [TelegramTexts.Get(locale, "inbox.target.prompt", id, Short(item.OriginalText))];
    }

    public async Task<IReadOnlyList<string>> CancelAsync(UserId user, string locale, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            return [TelegramTexts.Get(locale, "inbox.cancel.none")];
        }

        db.TelegramInboxDrafts.Remove(draft);
        return [TelegramTexts.Get(locale, "inbox.cancel.done")];
    }

    public async Task<IReadOnlyList<string>> DiscardAsync(UserId user, string argument, string locale, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        var id = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : string.IsNullOrWhiteSpace(argument) ? draft?.InboxItemId : null;
        if (id is null)
        {
            return [TelegramTexts.Get(locale, "inbox.discard.usage")];
        }

        var result = await processing.DiscardAsync(user, id.Value, token);
        if (draft?.InboxItemId == id.Value)
        {
            db.TelegramInboxDrafts.Remove(draft);
        }

        return result switch
        {
            DiscardInboxItemResult.Discarded => [TelegramTexts.Get(locale, "inbox.discard.done", id)],
            DiscardInboxItemResult.AlreadyProcessed => [TelegramTexts.Get(locale, "inbox.discard.processed")],
            DiscardInboxItemResult.AlreadyDiscarded => [TelegramTexts.Get(locale, "inbox.discard.already")],
            _ => [TelegramTexts.Get(locale, "inbox.discard.not_found")]
        };
    }

    private async Task<TelegramInboxDraft?> LoadAsync(UserId user, CancellationToken token)
    {
        var draft = await db.TelegramInboxDrafts.SingleOrDefaultAsync(row => row.UserId == user.Value, token);
        if (draft is not null && draft.ExpiresAt <= Now())
        {
            db.TelegramInboxDrafts.Remove(draft);
            await db.SaveChangesAsync(token);
            return null;
        }

        return draft;
    }

    private async Task<IReadOnlyList<string>> ProjectPromptAsync(UserId user, string locale, CancellationToken token)
    {
        var projects = await structured.GetActiveProjectsAsync(user, token);
        return TelegramTextLists.Render(TelegramTexts.Get(locale, "inbox.project.prompt"), projects,
            project => $"#{project.Id} {project.Title}", locale);
    }

    private async Task<string> SummaryAsync(UserId user, string original, TelegramInboxDraft draft, string locale, CancellationToken token)
    {
        var project = draft.ProjectId is long id ? await structured.FindProjectAsync(user, id, token) : null;
        var projectText = project is null ? TelegramTexts.Get(locale, "common.none") : $"#{project.Id} {project.Title}";
        var sourceText = draft.TargetKind == InboxTargetKind.WaitingFor
            ? TelegramTexts.Get(locale, "inbox.summary.source", draft.Source ?? TelegramTexts.Get(locale, "common.none"))
            : string.Empty;
        return TelegramTexts.Get(locale, "inbox.summary", Label(draft.TargetKind!.Value, locale), Short(original), draft.Text, projectText, sourceText);
    }

    private static string Label(InboxTargetKind kind, string locale) => kind switch
    {
        InboxTargetKind.Action => TelegramTexts.Get(locale, "kind.task"),
        InboxTargetKind.Project => TelegramTexts.Get(locale, "kind.project"),
        InboxTargetKind.SomedayMaybe => TelegramTexts.Get(locale, "kind.idea"),
        InboxTargetKind.Reference => TelegramTexts.Get(locale, "kind.note"),
        _ => TelegramTexts.Get(locale, "kind.waiting")
    };

    private static string Short(string text) => text.Length <= 500 ? text : text[..500] + "…";
    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
