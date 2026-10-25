using System.Globalization;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public sealed class TelegramInboxFlow(
    InboxDraftService drafts,
    InboxCaptureService capture,
    TelegramInboxPresenter presenter)
{
    public async Task<IReadOnlyList<string>> CaptureOrContinueAsync(UserId user, string text, string locale, CancellationToken token)
    {
        var result = await drafts.CaptureOrLoadAsync(user, text, token);
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
