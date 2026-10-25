using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Projects;
using OwnDay.Domain.References;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.WaitingFors;
using Xunit;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.UnitTests.StructuredItems;

public sealed class TemporalInvariantTests
{
    private static readonly UserId Owner = new(1);
    private static readonly DateTime CreatedAt = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Create_NonUtcTimestamp_RejectsAllStructuredItems(DateTimeKind kind)
    {
        var timestamp = DateTime.SpecifyKind(CreatedAt, kind);

        Assert.Throws<ArgumentException>(() => Action.Create(Owner, "Task", timestamp));
        Assert.Throws<ArgumentException>(() => Project.Create(Owner, "Project", timestamp));
        Assert.Throws<ArgumentException>(() => Reference.Create(Owner, "Note", timestamp));
        Assert.Throws<ArgumentException>(() => SomedayMaybe.Create(Owner, "Idea", timestamp));
        Assert.Throws<ArgumentException>(() => WaitingFor.Create(Owner, "Reply", null, timestamp));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void Transition_InvalidTimestamp_LeavesAllStructuredItemsActive(DateTimeKind kind)
    {
        var action = Action.Create(Owner, "Task", CreatedAt);
        var project = Project.Create(Owner, "Project", CreatedAt);
        var reference = Reference.Create(Owner, "Note", CreatedAt);
        var idea = SomedayMaybe.Create(Owner, "Idea", CreatedAt);
        var waiting = WaitingFor.Create(Owner, "Reply", null, CreatedAt);
        var timestamp = kind == DateTimeKind.Utc
            ? CreatedAt.AddTicks(-1)
            : DateTime.SpecifyKind(CreatedAt.AddHours(1), kind);

        Assert.ThrowsAny<ArgumentException>(() => action.Complete(timestamp));
        Assert.ThrowsAny<ArgumentException>(() => project.Complete(timestamp));
        Assert.ThrowsAny<ArgumentException>(() => reference.Archive(timestamp));
        Assert.ThrowsAny<ArgumentException>(() => idea.Archive(timestamp));
        Assert.ThrowsAny<ArgumentException>(() => waiting.Resolve(timestamp));

        Assert.Equal(ActionStatus.Active, action.Status);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(ReferenceStatus.Active, reference.Status);
        Assert.Equal(SomedayMaybeStatus.Active, idea.Status);
        Assert.Equal(WaitingForStatus.Active, waiting.Status);
        Assert.Null(action.CompletedAt);
        Assert.Null(project.CompletedAt);
        Assert.Null(reference.ArchivedAt);
        Assert.Null(idea.ArchivedAt);
        Assert.Null(waiting.ResolvedAt);
    }

    [Fact]
    public void Transition_AtCreationTime_CompletesOnceAndPreservesOriginalTimestamp()
    {
        var action = Action.Create(Owner, "Task", CreatedAt);
        var project = Project.Create(Owner, "Project", CreatedAt);
        var reference = Reference.Create(Owner, "Note", CreatedAt);
        var idea = SomedayMaybe.Create(Owner, "Idea", CreatedAt);
        var waiting = WaitingFor.Create(Owner, "Reply", null, CreatedAt);

        Assert.True(action.Complete(CreatedAt));
        Assert.True(project.Complete(CreatedAt));
        Assert.True(reference.Archive(CreatedAt));
        Assert.True(idea.Archive(CreatedAt));
        Assert.True(waiting.Resolve(CreatedAt));
        Assert.False(action.Complete(CreatedAt.AddHours(1)));
        Assert.False(project.Complete(CreatedAt.AddHours(1)));
        Assert.False(reference.Archive(CreatedAt.AddHours(1)));
        Assert.False(idea.Archive(CreatedAt.AddHours(1)));
        Assert.False(waiting.Resolve(CreatedAt.AddHours(1)));
        Assert.Equal(CreatedAt, action.CompletedAt);
        Assert.Equal(CreatedAt, project.CompletedAt);
        Assert.Equal(CreatedAt, reference.ArchivedAt);
        Assert.Equal(CreatedAt, idea.ArchivedAt);
        Assert.Equal(CreatedAt, waiting.ResolvedAt);
    }
}
