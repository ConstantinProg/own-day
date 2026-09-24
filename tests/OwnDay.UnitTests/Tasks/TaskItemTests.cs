using OwnDay.Domain.Tasks;
using Xunit;

namespace OwnDay.UnitTests.Tasks;

public sealed class TaskItemTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_ValidTitle_CreatesActiveTask()
    {
        var task = TaskItem.Create(new UserId(42), "  Buy groceries  ", CreatedAt);

        Assert.Equal(new UserId(42), task.UserId);
        Assert.Equal("Buy groceries", task.Title);
        Assert.Equal(TaskItemStatus.Active, task.Status);
        Assert.Equal(CreatedAt, task.CreatedAt);
        Assert.Null(task.CompletedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Create_EmptyTitle_ThrowsArgumentException(string title)
    {
        Assert.Throws<ArgumentException>(() => TaskItem.Create(new UserId(42), title, CreatedAt));
    }

    [Fact]
    public void Create_LongTitle_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            TaskItem.Create(new UserId(42), new string('x', TaskItem.MaxTitleLength + 1), CreatedAt));
    }

    [Fact]
    public void Complete_ActiveTask_SetsCompletionTime()
    {
        var task = TaskItem.Create(new UserId(42), "Buy groceries", CreatedAt);
        var completedAt = CreatedAt.AddHours(1);

        Assert.True(task.Complete(completedAt));
        Assert.Equal(TaskItemStatus.Completed, task.Status);
        Assert.Equal(completedAt, task.CompletedAt);
    }

    [Fact]
    public void Complete_CompletedTask_DoesNotChangeCompletionTime()
    {
        var task = TaskItem.Create(new UserId(42), "Buy groceries", CreatedAt);
        task.Complete(CreatedAt.AddHours(1));

        Assert.False(task.Complete(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), task.CompletedAt);
    }
}
