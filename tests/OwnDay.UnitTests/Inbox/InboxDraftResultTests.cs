using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.UnitTests.Inbox;

public sealed class InboxDraftResultTests
{
    [Theory]
    [InlineData(InboxDraftOutcome.Prompt)]
    [InlineData(InboxDraftOutcome.Processed)]
    [InlineData((InboxDraftOutcome)999)]
    public void Notice_OutcomeRequiresDataOrIsUnknown_Rejects(InboxDraftOutcome outcome)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboxDraftResult.Notice(outcome));
    }

    [Fact]
    public void Prompt_MissingDraftOrItem_Rejects()
    {
        var item = InboxItem.Capture(new UserId(11), "Original", DateTime.UtcNow);
        Assert.Throws<ArgumentNullException>(() => new InboxDraftResult.Prompt(null!, item));
        Assert.Throws<ArgumentNullException>(() => new InboxDraftResult.Prompt(new InboxDraft(), null!));
    }

    [Theory]
    [InlineData(0, InboxTargetKind.Action, 1)]
    [InlineData(1, InboxTargetKind.Action, 0)]
    [InlineData(1, (InboxTargetKind)999, 1)]
    public void Processed_InvalidIdentityOrTarget_Rejects(long itemId, InboxTargetKind kind, long targetId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboxDraftResult.Processed(itemId, kind, targetId));
    }
}
