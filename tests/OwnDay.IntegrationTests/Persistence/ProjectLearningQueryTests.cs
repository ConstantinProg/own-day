using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class ProjectLearningQueryTests
{
    [Fact]
    public async Task GetActiveProjectPageAsync_MixedTasks_FiltersAndPaginatesInSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new RecordingCommands();
        await using var db = CreateDb(connection, commands);
        await db.Database.EnsureCreatedAsync();
        var user = new UserId(101);
        var project = Project.Create(user, "Project", DateTime.UnixEpoch);
        var other = Project.Create(user, "Other", DateTime.UnixEpoch);
        db.AddRange(project, other);
        await db.SaveChangesAsync();
        var tasks = Enumerable.Range(0, 15).Select(i => Action.Create(user, "Task " + i, DateTime.UnixEpoch, project.Id)).ToArray();
        tasks[0].Complete(DateTime.UnixEpoch);
        db.AddRange(tasks);
        db.Add(Action.Create(user, "Other project", DateTime.UnixEpoch, other.Id));
        db.Add(Action.Create(user, "Standalone", DateTime.UnixEpoch));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Texts.Clear();

        var page = await new EfActionStore(db).GetActiveProjectPageAsync(user, project.Id, 10, 3, CancellationToken.None);

        Assert.Equal(tasks.OrderBy(a => a.Id).Skip(11).Take(3).Select(a => a.Id), page.Select(a => a.Id));
        var sql = Assert.Single(commands.Texts);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Empty(db.ChangeTracker.Entries<Action>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public async Task TopicStatusesAsync_AdaptedRoute_LoadsEvidenceAndSharedIdentitiesWithBoundedQueries(int count)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new RecordingCommands();
        await using var db = CreateDb(connection, commands);
        await db.Database.EnsureCreatedAsync();
        var user = new UserId(101);
        var program = LearningProgram.Create(user, "Goal", "Experience", null, "Math",
            Enumerable.Range(0, count).Select(i => new CurriculumTopic("t" + i, "Topic " + i, [])), null);
        db.Add(program);
        foreach (var topic in program.CurrentRoute.Topics)
        {
            var skill = Skill.Create(user, topic.Title, "Description");
            var lesson = Lesson.Start(user, topic.Title, "Theory", "Question");
            lesson.LinkToProgram(program, topic.Code);
            lesson.LinkToSkill(skill);
            lesson.Complete("Answer", "Feedback", true);
            var evidence = LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, "Test teacher");
            db.AddRange(skill, lesson, evidence, ReviewSchedule.FromAnswer(evidence));
        }
        var shared = Skill.Create(user, "Shared", "Confirmed identity");
        db.Add(shared);
        db.Add(ProgramSkillLink.Confirm(program, "t0", shared, "User confirmation", DateTimeOffset.UnixEpoch));
        program.ApplyAdaptation(program.ProposeAdaptation("t0", "Practice first topic"));
        await db.SaveChangesAsync();
        commands.Texts.Clear();

        var store = new EfKnowledgeStore(db);
        var statuses = await store.TopicStatusesAsync(program, CancellationToken.None);

        Assert.InRange(commands.Texts.Count, 1, 5);
        Assert.Equal(count, statuses.Count);
        Assert.Equal(shared.Id, statuses["t0"].Skill.Id);
        Assert.Empty(statuses["t0"].Evidence);
        foreach (var topic in program.CurrentRoute.Topics)
        {
            var skill = await store.FindTopicAsync(user, program.Id, program.CurrentRoute.Version, topic.Code, CancellationToken.None);
            Assert.Equal(skill!.Id, statuses[topic.Code].Skill.Id);
            var evidence = await store.EvidenceAsync(user, skill.Id, CancellationToken.None);
            Assert.Equal(LearnerSkillState.Evaluate(evidence), statuses[topic.Code].State);
            Assert.Equal(evidence.Select(e => e.LessonId), statuses[topic.Code].Evidence.Select(e => e.LessonId));
            Assert.Equal((await store.ReviewAsync(user, skill.Id, CancellationToken.None))?.DueAt, statuses[topic.Code].Review?.DueAt);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TopicStatusesAsync(program, cancellation.Token));
    }

    private static OwnDayDbContext CreateDb(SqliteConnection connection, RecordingCommands commands) =>
        new(new DbContextOptionsBuilder<OwnDayDbContext>().UseSqlite(connection).AddInterceptors(commands).Options);

    private sealed class RecordingCommands : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
