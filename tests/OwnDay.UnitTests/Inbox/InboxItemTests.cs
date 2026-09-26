using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.UnitTests.Inbox;

public sealed class InboxItemTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Capture_ValidText_PreservesExactUnclassifiedInput()
    {
        var text = "  a long thought  \n" + new string('x', 500);

        var item = InboxItem.Capture(new UserId(7), text, Now);

        Assert.Equal(new UserId(7), item.UserId);
        Assert.Equal(text, item.OriginalText);
        Assert.Equal(Now, item.CapturedAt);
        Assert.Equal(InboxItemStatus.Active, item.Status);
        Assert.Null(item.TargetKind);
        Assert.Null(item.TargetId);
        Assert.Null(item.ProcessedAt);
        Assert.Null(item.DiscardedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void Capture_BlankText_Throws(string? text)
    {
        Assert.Throws<ArgumentException>(() => InboxItem.Capture(new UserId(1), text!, Now));
    }

    [Fact]
    public void Capture_InvalidOwnerOrNonUtcTime_Throws()
    {
        Assert.Throws<ArgumentException>(() => InboxItem.Capture(default, "text", Now));
        Assert.Throws<ArgumentException>(() => InboxItem.Capture(new UserId(1), "text", DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void Process_ValidTarget_PreservesOriginalAndRejectsFurtherTransitions()
    {
        var item = InboxItem.Capture(new UserId(1), "  raw  ", Now);

        Assert.True(item.Process(InboxTargetKind.Action, 17, Now.AddMinutes(1)));
        Assert.Equal(InboxItemStatus.Processed, item.Status);
        Assert.Equal(InboxTargetKind.Action, item.TargetKind);
        Assert.Equal(17, item.TargetId);
        Assert.Equal(Now.AddMinutes(1), item.ProcessedAt);
        Assert.Equal("  raw  ", item.OriginalText);
        Assert.False(item.Discard(Now.AddMinutes(2)));
        Assert.False(item.Process(InboxTargetKind.Project, 20, Now.AddMinutes(2)));
    }

    [Fact]
    public void Discard_ValidTime_HasNoTarget()
    {
        var item = InboxItem.Capture(new UserId(1), "raw", Now);

        Assert.True(item.Discard(Now.AddSeconds(1)));
        Assert.Equal(InboxItemStatus.Discarded, item.Status);
        Assert.Null(item.TargetKind);
        Assert.Null(item.TargetId);
        Assert.Null(item.ProcessedAt);
        Assert.Equal(Now.AddSeconds(1), item.DiscardedAt);
        Assert.False(item.Process(InboxTargetKind.Action, 1, Now.AddSeconds(2)));
    }

    [Fact]
    public void Process_InvalidTargetOrTime_LeavesItemActive()
    {
        var item = InboxItem.Capture(new UserId(1), "raw", Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => item.Process(InboxTargetKind.Action, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Process((InboxTargetKind)99, 1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Process(InboxTargetKind.Action, 1, Now.AddSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Discard(Now.AddSeconds(-1)));
        Assert.Equal(InboxItemStatus.Active, item.Status);
    }
}
