using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_RouteChangesAfterConfirmationIsOffered_RejectsStaleConsent()
    {
        var probe = new Probe("");
        var program = LearningProgram.Create(_user, "Read a TypeScript response", "Some experience", null, "TypeScript",
            [new("basics", "Unions", []), new("handlers", "Handlers", ["basics"])], null);
        await using (var db = Open())
        {
            db.AddRange(program, LearningContext.Create(program));
            await db.SaveChangesAsync();
        }
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "/learn_route_edit detailed basics");
        string code;
        await using (var db = Open())
        {
            code = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
            var active = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            Assert.True(active.SetTopicSkipped(1, "handlers", true, "Concurrent route update"));
            await db.SaveChangesAsync();
        }
        await SendEnglishAsync(client, 2, "/learn_confirm " + code);

        await using var result = Open();
        var routes = await result.Set<CurriculumRoute>().OrderBy(route => route.Version).ToArrayAsync();
        Assert.Equal([1, 2], routes.Select(route => route.Version));
        Assert.Equal(CurriculumDepth.Standard, routes[1].DepthFor("basics"));
        Assert.True(routes[1].IsSkipped("handlers"));
        Assert.Contains(await result.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            message => message.Contains("устарело", StringComparison.Ordinal));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_RouteSkipAndRestoreAcrossRestart_PreservesPublishedExerciseAndAppliesOnce()
    {
        var probe = new Probe("") { Assessment = (_, _) => new("Accepted", true) };
        var program = LearningProgram.Create(_user, "Review a TypeScript response", "Some experience", null, "TypeScript",
            [new("basics", "Unions", []), new("handlers", "Handlers", ["basics"])], null);
        await using (var db = Open())
        {
            db.AddRange(program, LearningContext.Create(program));
            await db.SaveChangesAsync();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261118180000_AddAssessmentReviews");
            await migrator.MigrateAsync();
        }

        string skipCode;
        Guid lessonId;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "/learn_next");
            await SendEnglishAsync(client, 2, "пропустить тему basics");
            await using var db = Open();
            lessonId = (await db.Set<Lesson>().SingleAsync()).Id;
            var confirmation = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!;
            Assert.Equal(program.Id, confirmation.Target!.ProgramId);
            Assert.Equal(1, confirmation.Target.RouteVersion);
            skipCode = confirmation.Code;
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 3, "/learn_confirm " + skipCode);
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Single(await db.Set<CurriculumRoute>().ToListAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }

        using var restarted = ConsultationHost(probe);
        using var restartedClient = restarted.CreateClient();
        await WaitAllPublicAsync(3);
        using var duplicate = await PostPublicAsync(restartedClient, 3, "/learn_confirm " + skipCode);
        duplicate.EnsureSuccessStatusCode();
        await SendEnglishAsync(restartedClient, 4, "/learn_confirm " + skipCode);
        await SendEnglishAsync(restartedClient, 5, "show full route");
        await using (var db = Open())
        {
            var persisted = await db.Set<LearningProgram>().Include(value => value.Routes).SingleAsync();
            Assert.Equal(2, persisted.CurrentRoute.Version);
            Assert.True(persisted.CurrentRoute.IsSkipped("basics"));
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
            var lesson = await db.Set<Lesson>().SingleAsync();
            Assert.Equal(lessonId, lesson.Id);
            Assert.Equal(1, lesson.RouteVersion);
            Assert.Equal(probe.Material.Question, lesson.Question);
            Assert.Contains(await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
                message => message.Contains("/learn_focus basics", StringComparison.Ordinal));
        }

        await SendEnglishAsync(restartedClient, 6, "/answer independently explained unions");
        await SendEnglishAsync(restartedClient, 7, "restore route version 1");
        string restoreCode;
        await using (var db = Open())
            restoreCode = (await db.Set<DialogueContext>().SingleAsync()).PendingConfirmation!.Code;
        await SendEnglishAsync(restartedClient, 8, "/learn_confirm " + restoreCode);
        await SendEnglishAsync(restartedClient, 9, "/learn_next");

        await using var result = Open();
        var routes = await result.Set<CurriculumRoute>().OrderBy(route => route.Version).ToArrayAsync();
        Assert.Equal([1, 2, 3], routes.Select(route => route.Version));
        Assert.Equal(1, routes[2].Path!.RestoredFrom);
        Assert.False(routes[2].IsSkipped("basics"));
        var next = await result.Set<Lesson>().SingleAsync(lesson => lesson.Id != lessonId);
        Assert.Equal("handlers", next.TopicCode);
        Assert.Equal(3, next.RouteVersion);
        Assert.Equal(lessonId, (await result.Set<LearningEvidence>().SingleAsync()).LessonId);
        Assert.False(result.Database.HasPendingModelChanges());
        var rollback = await Assert.ThrowsAsync<PostgresException>(() =>
            result.GetService<IMigrator>().MigrateAsync("20261118180000_AddAssessmentReviews"));
        Assert.Contains("Retain curriculum path revisions", rollback.MessageText, StringComparison.Ordinal);
    }
}
