using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class ProgramSkillLinkTests
{
    [Fact]
    public void Confirm_CompatibleRevision_PreservesIdentityProvenanceAndRoot()
    {
        var program = Program();
        program.ApplyAdaptation(program.ProposeAdaptation("basics", "User choice"));
        var skill = Skill.Create(program.UserId, "Same concept", "Exact concept in another discipline");
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(3));
        var link = ProgramSkillLink.Confirm(program, "basics", skill, "Both topics assess this concept", now);
        Assert.Equal(skill.Id, link.SkillId);
        Assert.Equal(program.Id, link.ProgramId);
        Assert.Equal(1, link.ProgressVersion);
        Assert.Equal(now.ToUniversalTime(), link.ConfirmedAt);
        Assert.Equal("User confirmation", link.Source);
        Assert.Equal("UserConfirmedIdentity", link.Trust);
        Assert.Equal("Both topics assess this concept", link.Reason);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("topic")]
    [InlineData("diagnostic")]
    [InlineData("empty")]
    [InlineData("long")]
    public void Confirm_InvalidRelation_RejectsWithoutChangingProgram(string kind)
    {
        var program = Program(kind == "diagnostic" ? "Question" : null);
        var skill = Skill.Create(new UserId(kind == "foreign" ? 2 : 1), "Basics");
        Assert.Throws<ArgumentException>(() => ProgramSkillLink.Confirm(program, kind == "topic" ? "unknown" : "basics", skill,
            kind == "empty" ? " " : kind == "long" ? new string('r', 501) : "Reason", DateTimeOffset.UnixEpoch));
        Assert.Single(program.Routes);
        Assert.Equal("Goal", program.Goal);
    }

    [Fact]
    public void Select_ForeignProgram_PreservesSelectedContext()
    {
        var program = Program();
        var context = LearningContext.Create(program);
        var foreign = LearningProgram.Create(new UserId(2), "Foreign", "Beginner", null, "Discipline", [new("a", "A", [])], null);
        Assert.Throws<ArgumentException>(() => context.Select(foreign));
        Assert.Equal(program.Id, context.ProgramId);
        var other = Program();
        context.Select(other);
        Assert.Equal(other.Id, context.ProgramId);
    }

    [Fact]
    public async Task ConfirmTopicAsync_RepeatedConfirmation_PreservesOriginalReasonAndCreatesNoEvidence()
    {
        var store = new KnowledgeStore();
        var skill = Skill.Create(new UserId(1), "Basics");
        store.Add(skill);
        var service = new KnowledgeService(store, TimeProvider.System);
        var program = Program();
        var link = await service.ConfirmTopicAsync(program, "basics", skill.Id, "Original reason", CancellationToken.None);
        var duplicate = await service.ConfirmTopicAsync(program, "basics", skill.Id, "Changed reason", CancellationToken.None);
        Assert.Same(link, duplicate);
        Assert.Equal("Original reason", Assert.Single(store.Links).Reason);
        Assert.Single(store.Skills);
        Assert.Empty(store.Evidence);
        Assert.Empty(store.Reviews);
        var different = Skill.Create(program.UserId, "Basics");
        store.Add(different);
        Assert.Null(await service.ConfirmTopicAsync(program, "basics", different.Id, "Replace", CancellationToken.None));
        Assert.Single(store.Links);
    }

    [Fact]
    public async Task ConfirmTopicAsync_CanceledRequest_DoesNotCreateLink()
    {
        var store = new KnowledgeStore();
        var skill = Skill.Create(new UserId(1), "Basics");
        store.Add(skill);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KnowledgeService(store, TimeProvider.System)
            .ConfirmTopicAsync(Program(), "basics", skill.Id, "Reason", canceled.Token));
        Assert.Empty(store.Links);
    }

    private static LearningProgram Program(string? question = null) => LearningProgram.Create(new UserId(1), "Goal", "Beginner", null,
        "Discipline", [new("basics", "Basics", [])], question);
}
