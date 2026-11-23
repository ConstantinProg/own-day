using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Diagnostics;
using OwnDay.Infrastructure.Persistence;
using Xunit;
using TaskItem = OwnDay.Domain.Actions.Action;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresTesterModeTests
{
    private static readonly InboxAnalyzerDescriptor MeteredModel = new("gpt-6-luna", "test-schema", "test-prompt");

    [Fact]
    public async Task Post_DeleteWithoutFreshButtonAndExactPhrase_PreservesData()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await SeedPersonalRecordsAsync();

        await PostAsync(client, 3, "DELETE");
        await PostAsync(client, 4, "/tester_delete_all");
        var button = Assert.Single(await ReadButtonsAsync());
        await PostAsync(client, 5, "DELETE");
        await CallbackAsync(client, 7, button);
        await PostAsync(client, 6, "DELETE");
        await AssertProjectsRemainAsync();

        await CallbackAsync(client, 8, button);
        await PostAsync(client, 9, "delete");
        await PostAsync(client, 10, "DELETE");
        await AssertProjectsRemainAsync();

        await PostAsync(client, 11, "/tester_delete_all");
        await CallbackAsync(client, 12, Assert.Single(await ReadButtonsAsync()));
        await using (var db = Open())
            await db.Database.ExecuteSqlAsync($"""
                UPDATE tester_mode_users SET prompt_expires_at = clock_timestamp() - interval '1 second', prompt_update_id = 1000000
                WHERE user_id = {Tester.Value}
                """);
        await PostAsync(client, 13, "DELETE");

        await AssertProjectsRemainAsync();
        await using var saved = Open();
        var state = await saved.Set<TesterModeState>().SingleAsync();
        Assert.True(state.Authorized);
        Assert.Null(state.PendingOperation);
        Assert.Equal(3, await saved.Set<LearningRequest>().CountAsync());
        var replies = await saved.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains(replies, reply => reply.StartsWith("The phrase did not match.", StringComparison.Ordinal));
        Assert.DoesNotContain(replies, reply => reply.StartsWith("Your user data was deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_OldExpiredPromptWithHigherUpdateId_AllowsNewConfirmation()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await PostAsync(client, 1_000_000, "/tester_delete_all");
        var oldButton = Assert.Single(await ReadButtonsAsync());
        await using (var db = Open())
            await db.Database.ExecuteSqlAsync($"""
                UPDATE tester_mode_users SET prompt_expires_at = clock_timestamp() - interval '1 second'
                WHERE user_id = {Tester.Value}
                """);

        await PostAsync(client, 3, "/tester_delete_all");

        Assert.NotEqual(oldButton, Assert.Single(await ReadButtonsAsync()));
    }

    [Fact]
    public async Task Post_DeleteFailureAndRedelivery_RollsBackThenErasesOnlyOwnerAndRetainsBudget()
    {
        using var host = Host();
        using var client = host.CreateClient();
        await AuthorizeAsync(client);
        await SeedPersonalRecordsAsync();
        await PostAsync(client, 3, "Private pending lesson");
        await TesterModeAuthorizationTests.PostAsync(client, 4, "Other pending lesson", Other.Value);
        await using (var db = Open())
        {
            db.AddRange(Event(Guid.NewGuid(), Tester.Value, DateTime.UtcNow, Guid.NewGuid()),
                Event(Guid.NewGuid(), Other.Value, DateTime.UtcNow, Guid.NewGuid()));
            await db.SaveChangesAsync();
        }

        var ledger = host.Services.GetRequiredService<IOpenAIRequestLedger>();
        var (lease, attempt) = await BeginProviderAttemptAsync(Tester);
        var (_, otherAttempt) = await BeginProviderAttemptAsync(Other);
        var reservation = await ledger.AdmitLearningAsync(attempt, MeteredModel, default);
        Assert.NotNull(reservation);
        Assert.NotNull(await ledger.AdmitLearningAsync(otherAttempt, MeteredModel, default));
        await AddDeletionFailureAsync();
        await PostAsync(client, 5, "/tester_delete_all");
        var button = Assert.Single(await ReadButtonsAsync());
        await CallbackAsync(client, 6, button);
        await TesterModeAuthorizationTests.PostAsync(client, 7, "DELETE", status: HttpStatusCode.InternalServerError);

        await AssertProjectsRemainAsync();
        await using (var rolledBack = Open())
        {
            Assert.Equal(2, await rolledBack.Set<LearningRequest>().CountAsync());
            Assert.Equal(2, await rolledBack.OpenAIRequestAttempts.CountAsync());
            Assert.False(await rolledBack.ProcessedTelegramUpdates.AnyAsync(update => update.UpdateId == 7));
            Assert.Equal(2, await rolledBack.Set<DiagnosticEvent>().CountAsync());
            Assert.True(await rolledBack.TelegramOutboxMessages.AnyAsync(message => message.Text == "Private committed reply"));
            Assert.Equal(TesterModeOperation.DeleteAll, (await rolledBack.Set<TesterModeState>().SingleAsync()).PendingOperation);
            await rolledBack.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_project_delete ON projects");
        }

        await PostAsync(client, 7, "DELETE");
        await PostAsync(client, 7, "DELETE");
        await CallbackAsync(client, 8, button);
        await using (var cleared = Open())
        {
            await AssertOnlySecondOwnerRemainsAsync(cleared);
            Assert.Equal(Other.Value, (await cleared.Set<DiagnosticEvent>().SingleAsync()).OwnerId);
            var erased = new EfLearningRequestStore(cleared);
            Assert.False(await erased.ApplyAsync(lease, (_, _) => throw new InvalidOperationException("Erased request ran."), default));
        }
        Assert.Null(await ledger.AdmitLearningAsync(attempt, MeteredModel, default));
        await ledger.FinishAsync(reservation.Value, "response", new("{}", MeteredModel.Model, InputTokens: 1, OutputTokens: 1), default);

        await PostAsync(client, 9, "/start");
        await PostAsync(client, 10, "New lesson after deletion");
        var (_, newAttempt) = await BeginProviderAttemptAsync(Tester);
        Assert.Null(await ledger.AdmitLearningAsync(newAttempt, MeteredModel, default));
        await using var saved = Open();
        Assert.True((await saved.Set<TesterModeState>().SingleAsync()).Authorized);
        Assert.Equal(Tester, (await saved.Set<DialogueContext>().SingleAsync()).UserId);
        Assert.Contains(await saved.TelegramOutboxMessages.Select(message => message.Text).ToListAsync(),
            text => text == OwnDay.Infrastructure.Telegram.Commands.TelegramDialoguePresenter.EnglishIntroduction);
        Assert.Equal(1, await saved.Database.SqlQuery<long>($"""
            SELECT call_count AS "Value" FROM openai_budget_carryover WHERE user_id = {Tester.Value}
            """).SingleAsync());
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => saved.GetService<IMigrator>()
            .MigrateAsync("20261120130000_AddTesterMode"));
        Assert.Contains("retained charges", downgrade.MessageText, StringComparison.Ordinal);
    }

    private async Task SeedPersonalRecordsAsync()
    {
        await using var db = Open();
        var now = DateTime.UtcNow;
        var item = InboxItem.Capture(Tester, "Legacy private note", now);
        SeedLearning(db, Tester, "Private goal");
        SeedLearning(db, Other, "Other goal");
        db.AddRange(item, Project.Create(Tester, "Private project", now), Project.Create(Other, "Other project", now),
            TaskItem.Create(Tester, "Private task", now), TaskItem.Create(Other, "Other task", now),
            LearningPreference.Create(Tester, LearningPreferenceKind.Format, "Private setting", now),
            new TelegramUserLanguage { UserId = Tester.Value, Locale = "en" },
            new InboxOperationEvent { UserId = Tester.Value, Code = "test_owner_event", At = now },
            new InboxOperationEvent { UserId = Other.Value, Code = "test_owner_event", At = now });
        await db.SaveChangesAsync();
        db.AddRange(new TelegramInboxDestination { InboxItemId = item.Id, ChatId = Tester.Value }, InboxAnalysisJob.Schedule(item),
            InboxAnalysis.Start(Tester, item.Id, 1, MeteredModel, "{}", "context", "input", now),
            new TelegramOutboxMessage { Id = Guid.NewGuid(), ChatId = Tester.Value, Text = "Private committed reply", CreatedAt = now, NextAttemptAt = now },
            new TelegramOutboxMessage { Id = Guid.NewGuid(), ChatId = Other.Value, Text = "Other committed reply", CreatedAt = now, NextAttemptAt = now });
        await db.SaveChangesAsync();
    }

    private async Task AssertProjectsRemainAsync()
    {
        await using var db = Open();
        Assert.Equal(2, await db.Projects.CountAsync());
        Assert.Equal(2, await db.Set<LearningEvidence>().CountAsync());
    }

    private static async Task AssertOnlySecondOwnerRemainsAsync(OwnDayDbContext db)
    {
        Assert.Equal(Other, (await db.Projects.SingleAsync()).UserId);
        Assert.Equal(Other, (await db.Actions.SingleAsync()).UserId);
        Assert.Equal(Other, (await db.Set<LearningProgram>().SingleAsync()).UserId);
        Assert.Equal(Other, (await db.Set<LearningEvidence>().SingleAsync()).UserId);
        Assert.Equal(Other, (await db.Set<LearningRequest>().SingleAsync()).UserId);
        Assert.Equal(Other.Value, (await db.OpenAIRequestAttempts.SingleAsync()).UserId);
        Assert.False(await db.Set<DialogueContext>().AnyAsync());
        Assert.False(await db.Set<LearningPreference>().AnyAsync());
        Assert.False(await db.TelegramUserLanguages.AnyAsync());
        Assert.False(await db.TelegramInboxDestinations.AnyAsync());
        Assert.False(await db.InboxItems.AnyAsync());
        Assert.False(await db.InboxAnalyses.AnyAsync());
        Assert.False(await db.InboxAnalysisJobs.AnyAsync());
        Assert.False(await db.InboxOperationEvents.AnyAsync(item => item.UserId == Tester.Value));
        Assert.True(await db.InboxOperationEvents.AnyAsync(item => item.UserId == Other.Value));
        var replies = await db.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Contains("Other committed reply", replies);
        Assert.DoesNotContain("Private committed reply", replies);
        Assert.Single(replies, reply => reply.StartsWith("Your user data was deleted", StringComparison.Ordinal));
    }

    private async Task<(LearningRequestLease Lease, Guid AttemptId)> BeginProviderAttemptAsync(UserId owner)
    {
        await using var db = Open();
        var requests = new EfLearningRequestStore(db);
        var lease = await requests.ClaimAsync(TimeSpan.FromMinutes(2), default);
        Assert.NotNull(lease);
        Assert.Equal(owner, lease.UserId);
        Assert.True(await requests.SaveStepAsync(lease, new LearningProviderStep { Kind = "intent", Input = "Private input" },
            TimeSpan.FromMinutes(2), default));
        var admitted = await requests.BeginAttemptAsync(lease, 0, 1, TimeSpan.FromMinutes(2), default);
        Assert.NotNull(admitted.Id);
        return (lease, admitted.Id.Value);
    }

    private async Task AddDeletionFailureAsync()
    {
        await using var db = Open();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_project_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Injected deletion failure';
            END $$;
            CREATE TRIGGER reject_project_delete BEFORE DELETE ON projects
                FOR EACH ROW EXECUTE FUNCTION reject_project_delete();
            """);
    }
}
