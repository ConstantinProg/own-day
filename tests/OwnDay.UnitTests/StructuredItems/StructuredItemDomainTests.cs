using OwnDay.Domain;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using Action = OwnDay.Domain.Actions.Action;
using Xunit;

namespace OwnDay.UnitTests.StructuredItems;

public sealed class StructuredItemDomainTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Owner = new(42);

    [Fact]
    public void Project_CreateAndComplete_UsesActionStyleLifecycleWithoutActions()
    {
        var project = Project.Create(Owner, "  Outcome  ", CreatedAt);
        Assert.Equal("Outcome", project.Title);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Null(project.CompletedAt);
        Assert.True(project.Complete(CreatedAt.AddHours(1)));
        Assert.False(project.Complete(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), project.CompletedAt);
    }

    [Fact]
    public void Project_InvalidOwnerOrTitle_Throws()
    {
        Assert.Throws<ArgumentException>(() => Project.Create(new UserId(0), "Valid", CreatedAt));
        Assert.Throws<ArgumentException>(() => Project.Create(Owner, " ", CreatedAt));
        Assert.Throws<ArgumentException>(() => Project.Create(Owner, new string('x', 201), CreatedAt));
    }

    [Fact]
    public void SomedayMaybe_CreateAndArchive_PreservesRelationAndFirstTimestamp()
    {
        var item = SomedayMaybe.Create(Owner, "  Idea  ", CreatedAt, 7);
        Assert.Equal("Idea", item.Text);
        Assert.Equal(7, item.ProjectId);
        Assert.Equal(SomedayMaybeStatus.Active, item.Status);
        Assert.True(item.Archive(CreatedAt.AddHours(1)));
        Assert.False(item.Archive(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), item.ArchivedAt);
        Assert.Null(SomedayMaybe.Create(Owner, "Idea", CreatedAt).ProjectId);
    }

    [Fact]
    public void SomedayMaybe_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => SomedayMaybe.Create(new UserId(0), "Idea", CreatedAt));
        Assert.Throws<ArgumentException>(() => SomedayMaybe.Create(Owner, " ", CreatedAt));
        Assert.Throws<ArgumentException>(() => SomedayMaybe.Create(Owner, new string('x', 201), CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => SomedayMaybe.Create(Owner, "Idea", CreatedAt, 0));
    }

    [Fact]
    public void Reference_CreateAndArchive_PreservesRelationAndFirstTimestamp()
    {
        var item = Reference.Create(Owner, "  Note  ", CreatedAt, 7);
        Assert.Equal("Note", item.Text);
        Assert.Equal(7, item.ProjectId);
        Assert.Equal(ReferenceStatus.Active, item.Status);
        Assert.True(item.Archive(CreatedAt.AddHours(1)));
        Assert.False(item.Archive(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), item.ArchivedAt);
        Assert.Null(Reference.Create(Owner, "Note", CreatedAt).ProjectId);
    }

    [Fact]
    public void Reference_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => Reference.Create(new UserId(0), "Note", CreatedAt));
        Assert.Throws<ArgumentException>(() => Reference.Create(Owner, " ", CreatedAt));
        Assert.Throws<ArgumentException>(() => Reference.Create(Owner, new string('x', 201), CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => Reference.Create(Owner, "Note", CreatedAt, 0));
    }

    [Fact]
    public void WaitingFor_CreateAndResolve_PreservesSourceRelationAndFirstTimestamp()
    {
        var item = WaitingFor.Create(Owner, "  Reply  ", "  Alice  ", CreatedAt, 7);
        Assert.Equal("Reply", item.Description);
        Assert.Equal("Alice", item.Source);
        Assert.Equal(7, item.ProjectId);
        Assert.Equal(CreatedAt, item.WaitingSince);
        Assert.Equal(WaitingForStatus.Active, item.Status);
        Assert.True(item.Resolve(CreatedAt.AddHours(1)));
        Assert.False(item.Resolve(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), item.ResolvedAt);
        Assert.Null(WaitingFor.Create(Owner, "Reply", null, CreatedAt).Source);
    }

    [Fact]
    public void WaitingFor_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => WaitingFor.Create(new UserId(0), "Reply", null, CreatedAt));
        Assert.Throws<ArgumentException>(() => WaitingFor.Create(Owner, " ", null, CreatedAt));
        Assert.Throws<ArgumentException>(() => WaitingFor.Create(Owner, new string('x', 201), null, CreatedAt));
        Assert.Throws<ArgumentException>(() => WaitingFor.Create(Owner, "Reply", " ", CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaitingFor.Create(Owner, "Reply", null, CreatedAt, 0));
    }

    [Fact]
    public void Action_WithAndWithoutProject_PreservesCompletionLifecycle()
    {
        var without = Action.Create(Owner, "Task", CreatedAt);
        var with = Action.Create(Owner, "Task", CreatedAt, 7);
        Assert.Null(without.ProjectId);
        Assert.Equal(7, with.ProjectId);
        Assert.True(with.Complete(CreatedAt.AddHours(1)));
        Assert.False(with.Complete(CreatedAt.AddHours(2)));
        Assert.Equal(CreatedAt.AddHours(1), with.CompletedAt);
    }
}
