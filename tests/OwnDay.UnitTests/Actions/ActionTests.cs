using OwnDay.Domain;
using OwnDay.Domain.Actions;
using Action = OwnDay.Domain.Actions.Action;
using Xunit;

namespace OwnDay.UnitTests.Actions;

public sealed class ActionTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_ValidTitle_CreatesActiveAction()
    {
        var action = Action.Create(new UserId(42), "  Buy groceries  ", CreatedAt);

        Assert.Equal(new UserId(42), action.UserId);
        Assert.Equal("Buy groceries", action.Title);
        Assert.Equal(ActionStatus.Active, action.Status);
        Assert.Equal(CreatedAt, action.CreatedAt);
        Assert.Null(action.CompletedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Create_EmptyTitle_ThrowsArgumentException(string title)
    {
        Assert.Throws<ArgumentException>(() => Action.Create(new UserId(42), title, CreatedAt));
    }

    [Fact]
    public void Create_LongTitle_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Action.Create(new UserId(42), new string('x', Action.MaxTitleLength + 1), CreatedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_InvalidOwner_ThrowsArgumentException(long userId)
    {
        Assert.Throws<ArgumentException>(() => Action.Create(new UserId(userId), "Buy groceries", CreatedAt));
    }

    [Fact]
    public void Complete_ActiveAction_SetsCompletionTime()
    {
        var action = Action.Create(new UserId(42), "Buy groceries", CreatedAt);
        var completedAt = CreatedAt.AddHours(1);

        Assert.True(action.Complete(completedAt));
        Assert.Equal(ActionStatus.Completed, action.Status);
        Assert.Equal(completedAt, action.CompletedAt);
    }

    [Fact]
    public void Complete_CompletedAction_DoesNotChangeCompletionTime()
    {
        var action = Action.Create(new UserId(42), "Buy groceries", CreatedAt);
        action.Complete(CreatedAt.AddHours(1));

        Assert.False(action.Complete(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), action.CompletedAt);
    }
}
