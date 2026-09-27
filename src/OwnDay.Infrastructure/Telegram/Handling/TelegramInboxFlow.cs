using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxFlow(
    OwnDayDbContext db,
    InboxCaptureService capture,
    InboxProcessingService processing,
    StructuredItemService structured,
    TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<string>> CaptureOrContinueAsync(UserId user, string text, CancellationToken token)
    {
        var expired = await db.TelegramInboxDrafts.AnyAsync(row => row.UserId == user.Value && row.ExpiresAt <= Now(), token);
        if (expired)
        {
            var stale = await db.TelegramInboxDrafts.SingleAsync(row => row.UserId == user.Value, token);
            db.TelegramInboxDrafts.Remove(stale);
            await capture.CaptureAsync(user, text, token);
            return ["Your draft expired. The previous item is still in your inbox. Your new message was saved to the inbox. Open /inbox to continue."];
        }

        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            await capture.CaptureAsync(user, text, token);
            return ["Saved to your inbox. Open /inbox to process it."];
        }

        var item = await capture.FindAsync(user, draft.InboxItemId, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            db.TelegramInboxDrafts.Remove(draft);
            return ["This item is no longer available. The draft was closed. Open /inbox."];
        }

        var value = text.Trim();
        switch (draft.Step)
        {
            case TelegramInboxDraftStep.Target:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var choice) || choice is < 1 or > 5)
                {
                    return ["Choose a type with a number from 1 to 5. /cancel — cancel; /discard — remove from inbox."];
                }

                draft.TargetKind = (InboxTargetKind)(choice - 1);
                draft.Text = item.OriginalText;
                draft.Step = TelegramInboxDraftStep.Text;
                break;
            case TelegramInboxDraftStep.Text:
                var edited = value == "." ? draft.Text : value;
                if (string.IsNullOrWhiteSpace(edited) || edited.Trim().Length > 200)
                {
                    return ["Enter 1 to 200 characters, or send a period to keep the original text."];
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
                    return ["The source must be at most 200 characters. Send a period for no source."];
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
                    return ["Project not found. Enter its ID, or 0 for no project."];
                }

                draft.Step = TelegramInboxDraftStep.Confirm;
                break;
            case TelegramInboxDraftStep.Confirm:
                if (!value.Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("да", StringComparison.OrdinalIgnoreCase))
                {
                    return ["Send 'yes' to create it. /cancel — cancel; /discard — remove from inbox."];
                }

                var result = await processing.ProcessAsync(user, new ProcessInboxItem(
                    draft.InboxItemId, draft.TargetKind!.Value, draft.Text!, draft.ProjectId, draft.Source), token);
                if (result.Status == ProcessInboxItemStatus.InvalidProject)
                {
                    draft.Step = TelegramInboxDraftStep.Project;
                    return ["The selected project is unavailable. Enter another ID, or 0 for no project."];
                }

                if (result.Status == ProcessInboxItemStatus.InvalidTargetData)
                {
                    draft.Step = TelegramInboxDraftStep.Text;
                    return ["Invalid data. Enter new text from 1 to 200 characters."];
                }

                db.TelegramInboxDrafts.Remove(draft);
                return result.Status == ProcessInboxItemStatus.Processed
                    ? [$"Inbox item #{draft.InboxItemId} processed. Created {Label(draft.TargetKind.Value)} #{result.TargetId}."]
                    : ["This item was already processed or is unavailable. The draft was closed."];
        }

        draft.Version++;
        draft.ExpiresAt = Now().Add(Lifetime);
        return draft.Step switch
        {
            TelegramInboxDraftStep.Text => [$"Type: {Label(draft.TargetKind!.Value)}. Original text: {Short(item.OriginalText)}\nEnter the final text (up to 200 characters), or send a period to keep the original."],
            TelegramInboxDraftStep.Source => ["Waiting source: enter up to 200 characters, or send a period for no source."],
            TelegramInboxDraftStep.Project => await ProjectPromptAsync(user, token),
            _ => [await SummaryAsync(user, item.OriginalText, draft, token)]
        };
    }

    public async Task<IReadOnlyList<string>> OpenAsync(UserId user, string argument, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var items = await capture.GetActiveAsync(user, token);
            return TelegramTextLists.Render("Inbox", items,
                item => $"#{item.Id} {Short(item.OriginalText)} — /inbox {item.Id}");
        }

        if (!long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return ["Use /inbox <id>."];
        }

        var item = await capture.FindAsync(user, id, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            return ["Active inbox item not found."];
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
        return [$"Inbox item #{id}: {Short(item.OriginalText)}\nChoose a type:\n1 — Task\n2 — Project\n3 — Idea\n4 — Note\n5 — Waiting\n/cancel — cancel; /discard — remove from inbox."];
    }

    public async Task<IReadOnlyList<string>> CancelAsync(UserId user, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        if (draft is null)
        {
            return ["No active selection. Open /inbox."];
        }

        db.TelegramInboxDrafts.Remove(draft);
        return ["Selection canceled. The item remains in your inbox."];
    }

    public async Task<IReadOnlyList<string>> DiscardAsync(UserId user, string argument, CancellationToken token)
    {
        var draft = await LoadAsync(user, token);
        var id = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : string.IsNullOrWhiteSpace(argument) ? draft?.InboxItemId : null;
        if (id is null)
        {
            return ["Use /discard <id> or open /inbox <id>."];
        }

        var result = await processing.DiscardAsync(user, id.Value, token);
        if (draft?.InboxItemId == id.Value)
        {
            db.TelegramInboxDrafts.Remove(draft);
        }

        return result switch
        {
            DiscardInboxItemResult.Discarded => [$"Inbox item #{id} discarded."],
            DiscardInboxItemResult.AlreadyProcessed => ["This item was already processed."],
            DiscardInboxItemResult.AlreadyDiscarded => ["This item was already discarded."],
            _ => ["Inbox item not found."]
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

    private async Task<IReadOnlyList<string>> ProjectPromptAsync(UserId user, CancellationToken token)
    {
        var projects = await structured.GetActiveProjectsAsync(user, token);
        return TelegramTextLists.Render("Choose a project: 0 — no project", projects,
            project => $"#{project.Id} {project.Title}");
    }

    private async Task<string> SummaryAsync(UserId user, string original, TelegramInboxDraft draft, CancellationToken token)
    {
        var project = draft.ProjectId is long id ? await structured.FindProjectAsync(user, id, token) : null;
        return $"Confirm creation of {Label(draft.TargetKind!.Value)}.\nOriginal text: {Short(original)}\nFinal text: {draft.Text}\nProject: {(project is null ? "none" : $"#{project.Id} {project.Title}")}" +
            (draft.TargetKind == InboxTargetKind.WaitingFor ? $"\nSource: {draft.Source ?? "none"}" : "") +
            "\nSend 'yes' to process it, /cancel to cancel, or /discard to remove it from your inbox.";
    }

    private static string Label(InboxTargetKind kind) => kind switch
    {
        InboxTargetKind.Action => "Task",
        InboxTargetKind.Project => "Project",
        InboxTargetKind.SomedayMaybe => "Idea",
        InboxTargetKind.Reference => "Note",
        _ => "Waiting item"
    };

    private static string Short(string text) => text.Length <= 500 ? text : text[..500] + "…";
    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
