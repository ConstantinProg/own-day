using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public abstract record InboxDraftInput
{
    private InboxDraftInput() { }

    public sealed record SelectTarget(InboxTargetKind Kind) : InboxDraftInput;
    public sealed record EditText(string? Text) : InboxDraftInput;
    public sealed record SetSource(string? Source) : InboxDraftInput;
    public sealed record SelectProject(long? ProjectId) : InboxDraftInput;
    public sealed record Confirm : InboxDraftInput;
}
