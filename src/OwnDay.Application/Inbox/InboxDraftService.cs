using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Domain.References;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.WaitingFors;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Application.Inbox;

public sealed class InboxDraftService(
    IInboxDraftStore drafts,
    InboxCaptureService capture,
    InboxProcessingService processing,
    StructuredItemService structured,
    TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task<InboxDraftResult> CaptureOrLoadAsync(UserId user, string text, CancellationToken token)
    {
        var draft = await drafts.FindAsync(user, token);
        if (draft is null || draft.ExpiresAt <= Now())
        {
            var expired = draft is not null;
            if (draft is not null)
            {
                drafts.Remove(draft);
            }

            await capture.CaptureAsync(user, text, token);
            return new InboxDraftResult.Notice(expired ? InboxDraftOutcome.Expired : InboxDraftOutcome.Captured);
        }

        var item = await capture.FindAsync(user, draft.InboxItemId, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            drafts.Remove(draft);
            return new InboxDraftResult.Notice(InboxDraftOutcome.Unavailable);
        }

        return new InboxDraftResult.Prompt(draft, item);
    }

    public async Task<InboxDraftResult> OpenAsync(UserId user, long itemId, CancellationToken token)
    {
        var item = await capture.FindAsync(user, itemId, token);
        if (item?.Status != InboxItemStatus.Active)
        {
            return new InboxDraftResult.Notice(InboxDraftOutcome.NotFound);
        }

        var draft = await drafts.FindAsync(user, token);
        if (draft is null)
        {
            draft = new InboxDraft { UserId = user.Value };
            drafts.Add(draft);
        }

        draft.InboxItemId = itemId;
        draft.Step = InboxDraftStep.Target;
        draft.TargetKind = null;
        draft.Text = null;
        draft.Source = null;
        draft.ProjectId = null;
        Refresh(draft);
        return new InboxDraftResult.Prompt(draft, item);
    }

    public async Task<bool> CancelAsync(UserId user, CancellationToken token)
    {
        var draft = await drafts.FindAsync(user, token);
        if (draft is null)
        {
            return false;
        }

        drafts.Remove(draft);
        return draft.ExpiresAt > Now();
    }

    public async Task<(long? ItemId, DiscardInboxItemResult? Result)> DiscardAsync(
        UserId user, long? itemId, CancellationToken token)
    {
        var draft = await drafts.FindAsync(user, token);
        if (draft is not null && draft.ExpiresAt <= Now())
        {
            drafts.Remove(draft);
            draft = null;
        }

        itemId ??= draft?.InboxItemId;
        if (itemId is null)
        {
            return (null, null);
        }

        var result = await processing.DiscardAsync(user, itemId.Value, token);
        if (draft?.InboxItemId == itemId)
        {
            drafts.Remove(draft);
        }

        return (itemId, result);
    }

    public async Task<InboxDraftResult> AdvanceAsync(
        UserId user, InboxDraft draft, InboxItem item, InboxDraftInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (draft.UserId != user.Value || item.UserId != user || draft.InboxItemId != item.Id)
        {
            throw new ArgumentException("Draft and inbox item must belong to the caller.");
        }

        switch (draft.Step, input)
        {
            case (InboxDraftStep.Target, InboxDraftInput.SelectTarget target) when Enum.IsDefined(target.Kind):
                draft.TargetKind = target.Kind;
                draft.Text = item.OriginalText;
                draft.Step = InboxDraftStep.Text;
                break;
            case (InboxDraftStep.Text, InboxDraftInput.EditText edit):
                var text = edit.Text ?? draft.Text;
                if (!IsValidText(draft.TargetKind, text))
                {
                    return new InboxDraftResult.Notice(InboxDraftOutcome.InvalidText);
                }

                draft.Text = text!.Trim();
                draft.Step = draft.TargetKind switch
                {
                    InboxTargetKind.WaitingFor => InboxDraftStep.Source,
                    InboxTargetKind.Project => InboxDraftStep.Confirm,
                    _ => InboxDraftStep.Project
                };
                break;
            case (InboxDraftStep.Source, InboxDraftInput.SetSource source):
                if (source.Source is not null &&
                    (string.IsNullOrWhiteSpace(source.Source) || source.Source.Trim().Length > WaitingFor.MaxSourceLength))
                {
                    return new InboxDraftResult.Notice(InboxDraftOutcome.InvalidSource);
                }

                draft.Source = source.Source?.Trim();
                draft.Step = InboxDraftStep.Project;
                break;
            case (InboxDraftStep.Project, InboxDraftInput.SelectProject project):
                if (project.ProjectId is long id &&
                    (await structured.FindProjectAsync(user, id, token)) is not { Status: ProjectStatus.Active })
                {
                    return new InboxDraftResult.Notice(InboxDraftOutcome.InvalidProject);
                }

                draft.ProjectId = project.ProjectId;
                draft.Step = InboxDraftStep.Confirm;
                break;
            case (InboxDraftStep.Confirm, InboxDraftInput.Confirm):
                return await ConfirmAsync(user, draft, token);
            default:
                throw new ArgumentException("Input does not match the current draft step.", nameof(input));
        }

        Refresh(draft);
        return new InboxDraftResult.Prompt(draft, item);
    }

    private async Task<InboxDraftResult> ConfirmAsync(UserId user, InboxDraft draft, CancellationToken token)
    {
        var targetKind = draft.TargetKind ?? throw new InvalidOperationException("Target is required.");
        var result = await processing.ProcessAsync(user, new ProcessInboxItem(
            draft.InboxItemId, targetKind,
            draft.Text ?? throw new InvalidOperationException("Text is required."), draft.ProjectId, draft.Source), token);
        if (result.Status == ProcessInboxItemStatus.InvalidProject)
        {
            draft.Step = InboxDraftStep.Project;
            Refresh(draft);
            return new InboxDraftResult.Notice(InboxDraftOutcome.ProjectUnavailable);
        }

        if (result.Status == ProcessInboxItemStatus.InvalidTargetData)
        {
            draft.Step = InboxDraftStep.Text;
            Refresh(draft);
            return new InboxDraftResult.Notice(InboxDraftOutcome.InvalidTargetData);
        }

        drafts.Remove(draft);
        return result.Status == ProcessInboxItemStatus.Processed
            ? new InboxDraftResult.Processed(draft.InboxItemId, targetKind,
                result.TargetId ?? throw new InvalidOperationException("Processed item requires a target ID."))
            : new InboxDraftResult.Notice(InboxDraftOutcome.ProcessingUnavailable);
    }

    private static bool IsValidText(InboxTargetKind? kind, string? text)
    {
        var maximum = kind switch
        {
            InboxTargetKind.Action => Action.MaxTitleLength,
            InboxTargetKind.Project => Project.MaxTitleLength,
            InboxTargetKind.SomedayMaybe => SomedayMaybe.MaxTextLength,
            InboxTargetKind.Reference => Reference.MaxTextLength,
            InboxTargetKind.WaitingFor => WaitingFor.MaxDescriptionLength,
            _ => throw new InvalidOperationException("Target is required before editing text.")
        };
        return !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= maximum;
    }

    private void Refresh(InboxDraft draft)
    {
        draft.Version++;
        draft.ExpiresAt = Now().Add(Lifetime);
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
