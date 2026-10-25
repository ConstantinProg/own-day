using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxPresenter(StructuredItemService structured)
{
    public async Task<IReadOnlyList<string>> RenderAsync(UserId user, InboxDraftResult result, string locale, CancellationToken token)
    {
        if (result is InboxDraftResult.Prompt prompt)
        {
            var draft = prompt.Draft;
            var item = prompt.Item;
            return draft.Step switch
            {
                InboxDraftStep.Target => [TelegramTexts.Get(locale, "inbox.target.prompt", item.Id, Short(item.OriginalText))],
                InboxDraftStep.Text => [TelegramTexts.Get(locale, "inbox.text.prompt", Label(draft.TargetKind!.Value, locale), Short(item.OriginalText))],
                InboxDraftStep.Source => [TelegramTexts.Get(locale, "inbox.source.prompt")],
                InboxDraftStep.Project => await ProjectPromptAsync(user, locale, token),
                InboxDraftStep.Confirm => [await SummaryAsync(user, item.OriginalText, draft, locale, token)],
                _ => throw new InvalidOperationException("Unknown inbox draft step.")
            };
        }

        if (result is InboxDraftResult.Processed processed)
        {
            return [TelegramTexts.Get(locale, "inbox.processed", processed.ItemId, Label(processed.TargetKind, locale), processed.TargetId)];
        }

        var key = result.Outcome switch
        {
            InboxDraftOutcome.Captured => "inbox.captured",
            InboxDraftOutcome.Expired => "inbox.draft.expired",
            InboxDraftOutcome.Unavailable => "inbox.draft.unavailable",
            InboxDraftOutcome.ProcessingUnavailable => "inbox.processed.unavailable",
            InboxDraftOutcome.NotFound => "inbox.open.not_found",
            InboxDraftOutcome.InvalidText => "inbox.text.invalid",
            InboxDraftOutcome.InvalidSource => "inbox.source.invalid",
            InboxDraftOutcome.InvalidProject => "inbox.project.not_found",
            InboxDraftOutcome.ProjectUnavailable => "inbox.project.unavailable",
            InboxDraftOutcome.InvalidTargetData => "inbox.data.invalid",
            _ => throw new InvalidOperationException("Unknown inbox draft outcome.")
        };
        return [TelegramTexts.Get(locale, key)];
    }

    private async Task<IReadOnlyList<string>> ProjectPromptAsync(UserId user, string locale, CancellationToken token)
    {
        var projects = await structured.GetActiveProjectsAsync(user, token);
        return TelegramTextLists.Render(TelegramTexts.Get(locale, "inbox.project.prompt"), projects,
            project => $"#{project.Id} {project.Title}", locale);
    }

    private async Task<string> SummaryAsync(UserId user, string original, InboxDraft draft, string locale, CancellationToken token)
    {
        var project = draft.ProjectId is long id ? await structured.FindProjectAsync(user, id, token) : null;
        var projectText = project is null ? TelegramTexts.Get(locale, "common.none") : $"#{project.Id} {project.Title}";
        var sourceText = draft.TargetKind == InboxTargetKind.WaitingFor
            ? TelegramTexts.Get(locale, "inbox.summary.source", draft.Source ?? TelegramTexts.Get(locale, "common.none"))
            : string.Empty;
        return TelegramTexts.Get(locale, "inbox.summary", Label(draft.TargetKind!.Value, locale), Short(original), draft.Text, projectText, sourceText);
    }

    internal static string Label(InboxTargetKind kind, string locale) => kind switch
    {
        InboxTargetKind.Action => TelegramTexts.Get(locale, "kind.task"),
        InboxTargetKind.Project => TelegramTexts.Get(locale, "kind.project"),
        InboxTargetKind.SomedayMaybe => TelegramTexts.Get(locale, "kind.idea"),
        InboxTargetKind.Reference => TelegramTexts.Get(locale, "kind.note"),
        InboxTargetKind.WaitingFor => TelegramTexts.Get(locale, "kind.waiting"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string Short(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
