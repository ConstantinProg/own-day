using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public enum InboxDraftOutcome
{
    Prompt,
    Captured,
    Expired,
    Unavailable,
    ProcessingUnavailable,
    NotFound,
    InvalidText,
    InvalidSource,
    InvalidProject,
    ProjectUnavailable,
    InvalidTargetData,
    Processed
}

public abstract record InboxDraftResult
{
    private InboxDraftResult(InboxDraftOutcome outcome) => Outcome = outcome;

    public InboxDraftOutcome Outcome { get; }

    public sealed record Prompt : InboxDraftResult
    {
        public Prompt(InboxDraft draft, InboxItem item) : base(InboxDraftOutcome.Prompt)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(item);
            Draft = draft;
            Item = item;
        }

        public InboxDraft Draft { get; }
        public InboxItem Item { get; }
    }

    public sealed record Processed : InboxDraftResult
    {
        public Processed(long itemId, InboxTargetKind targetKind, long targetId) : base(InboxDraftOutcome.Processed)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
            if (!Enum.IsDefined(targetKind))
            {
                throw new ArgumentOutOfRangeException(nameof(targetKind));
            }

            ItemId = itemId;
            TargetKind = targetKind;
            TargetId = targetId;
        }

        public long ItemId { get; }
        public InboxTargetKind TargetKind { get; }
        public long TargetId { get; }
    }

    public sealed record Notice : InboxDraftResult
    {
        public Notice(InboxDraftOutcome outcome, long? itemId = null) : base(outcome)
        {
            if (!Enum.IsDefined(outcome) || outcome is InboxDraftOutcome.Prompt or InboxDraftOutcome.Processed)
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }
            ItemId = itemId;
        }

        public long? ItemId { get; }
    }
}
