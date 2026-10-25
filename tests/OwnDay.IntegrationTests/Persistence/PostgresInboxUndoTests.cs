using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed partial class PostgresInboxAutomaticProcessingTests
{
    [Theory]
    [InlineData(InboxTargetKind.Action)]
    [InlineData(InboxTargetKind.Project)]
    [InlineData(InboxTargetKind.SomedayMaybe)]
    [InlineData(InboxTargetKind.Reference)]
    [InlineData(InboxTargetKind.WaitingFor)]
    public async Task UndoAsync_EachKindAfterRestart_RestoresOriginalAndPreservesAudit(InboxTargetKind kind)
    {
        var (itemId, analysisId) = await SeedAsync([Proposal(kind)]);
        string originalTargets;
        await using (var db = Db())
        {
            originalTargets = (await Service(db).ApplyAsync(Owner, itemId, analysisId, Token))!.TargetsJson;
        }
        await using (var restarted = Db())
        {
            var result = await Service(restarted).UndoAsync(Owner, itemId, Token);
            Assert.Equal(InboxUndoOutcome.Undone, result.Outcome);
            Assert.True(result.FeedbackQueued);
        }
        await using var verify = Db();
        Assert.Equal(0, await CountTargetsAsync(verify));
        var item = await verify.InboxItems.SingleAsync(Token);
        Assert.Equal(InboxItemStatus.Active, item.Status);
        Assert.True(item.ManualProcessingOnly);
        Assert.NotEmpty(item.OriginalText);
        Assert.Empty(item.Targets);
        Assert.Null(item.TargetId);
        Assert.Null(item.ProcessedAt);
        var audit = await verify.InboxAutomaticApplications.SingleAsync(Token);
        Assert.NotNull(audit.UndoneAt);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(originalTargets).RootElement,
            JsonDocument.Parse(audit.TargetsJson).RootElement));
        Assert.NotEmpty((await verify.InboxAnalyses.Include(a => a.Proposals).SingleAsync(Token)).Proposals);
        Assert.Equal(2, await verify.TelegramOutboxMessages.CountAsync(Token));
        Assert.False(verify.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task UndoAsync_CompositeConcurrentRequests_RemovesWholeBatchExactlyOnce()
    {
        var (itemId, analysisId) = await SeedAsync(Enum.GetValues<InboxTargetKind>().Select(k => Proposal(k)).ToArray());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InboxUndoResult> RunAsync()
        {
            await gate.Task.WaitAsync(Token);
            await using var db = Db();
            return await Service(db).UndoAsync(Owner, itemId, Token);
        }
        var tasks = Enumerable.Range(0, 4).Select(_ => RunAsync()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Outcome == InboxUndoOutcome.Undone);
        Assert.Equal(3, results.Count(r => r.Outcome == InboxUndoOutcome.AlreadyUndone));
        await using var verify = Db();
        Assert.Equal(0, await CountTargetsAsync(verify));
        Assert.Equal(2, await verify.TelegramOutboxMessages.CountAsync(Token));
        var message = await verify.TelegramOutboxMessages.SingleAsync(m => m.Id != analysisId, Token);
        Assert.Contains("Entities affected: 5", message.Text, StringComparison.Ordinal);
        Assert.Null(message.ReplyMarkupJson);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("complete")]
    [InlineData("project")]
    [InlineData("missing")]
    [InlineData("owner")]
    public async Task UndoAsync_SubsequentActionChange_RefusesWholeComposite(string change)
    {
        var (itemId, analysisId) = await SeedAsync([Proposal(), Proposal(InboxTargetKind.Reference)]);
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            var action = await db.Actions.SingleAsync(Token);
            switch (change)
            {
                case "title":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE actions SET title = 'Edited' WHERE id = {action.Id}", Token);
                    break;
                case "complete":
                    action.Complete(DateTime.UtcNow);
                    await db.SaveChangesAsync(Token);
                    break;
                case "missing":
                    db.Actions.Remove(action);
                    await db.SaveChangesAsync(Token);
                    break;
                case "owner":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE actions SET user_id = 42 WHERE id = {action.Id}", Token);
                    break;
                default:
                    var project = OwnDay.Domain.Projects.Project.Create(Owner, "Later project", DateTime.UtcNow);
                    db.Projects.Add(project);
                    await db.SaveChangesAsync(Token);
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE actions SET project_id = {project.Id} WHERE id = {action.Id}", Token);
                    break;
            }
        }
        await using var restarted = Db();
        Assert.Equal(InboxUndoOutcome.Conflict, (await Service(restarted).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Single(await restarted.References.ToListAsync(Token));
        Assert.Equal(change == "missing" ? 0 : 1, await restarted.Actions.CountAsync(Token));
        Assert.Null((await restarted.InboxAutomaticApplications.SingleAsync(Token)).UndoneAt);
        Assert.Equal(InboxItemStatus.Processed, (await restarted.InboxItems.SingleAsync(Token)).Status);
        Assert.Single(await restarted.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Theory]
    [InlineData(InboxTargetKind.Action)]
    [InlineData(InboxTargetKind.SomedayMaybe)]
    [InlineData(InboxTargetKind.Reference)]
    [InlineData(InboxTargetKind.WaitingFor)]
    public async Task UndoAsync_ProjectHasLaterChild_PreservesProjectAndChild(InboxTargetKind childKind)
    {
        var (itemId, analysisId) = await SeedAsync([Proposal(InboxTargetKind.Project)]);
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            var project = await db.Projects.SingleAsync(Token);
            var later = InboxItem.Capture(Owner, "Later", DateTime.UtcNow);
            db.InboxItems.Add(later);
            await db.SaveChangesAsync(Token);
            await Processing(db).ProcessAsync(Owner, new(later.Id, childKind, "Later", project.Id), Token);
        }
        await using var restarted = Db();
        Assert.Equal(InboxUndoOutcome.Conflict, (await Service(restarted).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Equal(2, await CountTargetsAsync(restarted));
    }

    [Fact]
    public async Task UndoAsync_ForeignOwnerOrManualResult_DoesNotRemoveTargets()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        Assert.Equal(InboxUndoOutcome.Unavailable, (await Service(db).UndoAsync(new(42), itemId, Token)).Outcome);
        Assert.Equal(InboxUndoOutcome.Unavailable, (await Service(db).UndoAsync(Owner, long.MaxValue, Token)).Outcome);
        Assert.Single(await db.Actions.ToListAsync(Token));
        var manual = InboxItem.Capture(Owner, "Manual", DateTime.UtcNow);
        db.InboxItems.Add(manual);
        await db.SaveChangesAsync(Token);
        await Processing(db).ProcessAsync(Owner, new(manual.Id, InboxTargetKind.Reference, "Manual"), Token);
        Assert.Equal(InboxUndoOutcome.Unavailable, (await Service(db).UndoAsync(Owner, manual.Id, Token)).Outcome);
        Assert.Single(await db.References.ToListAsync(Token));
    }

    [Fact]
    public async Task UndoAsync_OutboxFailure_RollsBackAndRecoversAfterRestart()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        await using (var failing = Db(new FailInsert("telegram_outbox_messages")))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(failing).UndoAsync(Owner, itemId, Token));
        }
        await using (var restarted = Db())
        {
            Assert.Equal(3, await CountTargetsAsync(restarted));
            Assert.Null((await restarted.InboxAutomaticApplications.SingleAsync(Token)).UndoneAt);
            Assert.False((await restarted.InboxItems.SingleAsync(Token)).ManualProcessingOnly);
            Assert.Equal(InboxUndoOutcome.Undone, (await Service(restarted).UndoAsync(Owner, itemId, Token)).Outcome);
        }
        await using var verify = Db();
        Assert.Equal(0, await CountTargetsAsync(verify));
        Assert.Equal(InboxUndoOutcome.AlreadyUndone, (await Service(verify).UndoAsync(Owner, itemId, Token)).Outcome);
    }

    [Fact]
    public async Task UndoAsync_ThenManualCorrection_OldAndNewAnalysesCannotAutoApply()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            await Service(db).UndoAsync(Owner, itemId, Token);
        }
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            Assert.False(await Service(db).ProcessNextAsync(Token));
            var item = await db.InboxItems.SingleAsync(Token);
            var context = JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options);
            var analysis = InboxAnalysis.Start(Owner, itemId, 2, new("fake", "inbox-v1", "test"), context,
                Hash(context), Hash(item.OriginalText), DateTime.UtcNow);
            analysis.Finish(InboxAnalysisStatus.Completed, DateTime.UtcNow, "{}", new() { Items = [Proposal()] });
            db.InboxAnalyses.Add(analysis);
            await db.SaveChangesAsync(Token);
            var blocked = await Service(db).ApplyAsync(Owner, itemId, analysis.Id, Token);
            Assert.Equal(InboxPolicyRoute.KeepInInbox, blocked!.Route);
            Assert.Contains(nameof(InboxPolicyReason.ManualProcessingOnly), blocked.DecisionJson, StringComparison.Ordinal);
            Assert.Equal(0, await CountTargetsAsync(db));
            var manual = await Processing(db).ProcessAsync(Owner, new(itemId, InboxTargetKind.Reference, "Corrected"), Token);
            Assert.Equal(ProcessInboxItemStatus.Processed, manual.Status);
        }
        await using var verify = Db();
        Assert.Equal(InboxUndoOutcome.AlreadyUndone, (await Service(verify).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Equal("Corrected", (await verify.References.SingleAsync(Token)).Text);
        Assert.Equal(2, await verify.InboxAnalyses.CountAsync(Token));
        Assert.Equal(2, await verify.InboxAutomaticApplications.CountAsync(Token));
    }

    [Fact]
    public async Task UndoAsync_ConcurrentCompletionWinsLock_RefusesUndo()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        await using var completion = Db();
        await using var transaction = await completion.Database.BeginTransactionAsync(Token);
        var action = await completion.Actions.SingleAsync(Token);
        action.Complete(DateTime.UtcNow);
        await completion.SaveChangesAsync(Token);
        var observer = new ObserveUndoTargetLock();
        await using var undo = Db(observer);
        var undoing = Service(undo).UndoAsync(Owner, itemId, Token);
        // The completion row lock is held until commit. Undo must validate the committed state.
        await observer.Attempted.Task.WaitAsync(Token);
        Assert.False(undoing.IsCompleted);
        await transaction.CommitAsync(Token);
        Assert.Equal(InboxUndoOutcome.Conflict, (await undoing).Outcome);
        await using var verify = Db();
        Assert.Single(await verify.Actions.ToListAsync(Token));
    }

    [Fact]
    public async Task UndoAsync_UnrelatedTargetsAndExistingProject_RemainUntouched()
    {
        await using var seed = Db();
        var project = OwnDay.Domain.Projects.Project.Create(Owner, "Existing", DateTime.UtcNow);
        seed.Projects.Add(project);
        seed.References.Add(OwnDay.Domain.References.Reference.Create(Owner, "Unrelated", DateTime.UtcNow));
        await seed.SaveChangesAsync(Token);
        await seed.Entry(project).ReloadAsync(Token);
        var (itemId, analysisId) = await SeedAsync([Proposal() with { ProjectIds = [project.Id] }],
            new(null, [new(project.Id, project.Title, project.CreatedAt)], false));
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            Assert.Equal(project.Id, (await db.Actions.SingleAsync(Token)).ProjectId);
            await Service(db).UndoAsync(Owner, itemId, Token);
        }
        await using var verify = Db();
        Assert.Single(await verify.Projects.ToListAsync(Token));
        Assert.Equal("Unrelated", (await verify.References.SingleAsync(Token)).Text);
        Assert.Empty(await verify.Actions.ToListAsync(Token));
    }

    [Theory]
    [InlineData(InboxTargetKind.Project, false)]
    [InlineData(InboxTargetKind.Reference, false)]
    [InlineData(InboxTargetKind.SomedayMaybe, false)]
    [InlineData(InboxTargetKind.WaitingFor, false)]
    [InlineData(InboxTargetKind.WaitingFor, true)]
    public async Task UndoAsync_StructuredEntityChanged_PreservesEntity(InboxTargetKind kind, bool changeSource)
    {
        var (itemId, analysisId) = await SeedAsync([Proposal(kind)]);
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        var now = DateTime.UtcNow;
        switch (kind)
        {
            case InboxTargetKind.Project:
                (await db.Projects.SingleAsync(Token)).Complete(now);
                break;
            case InboxTargetKind.Reference:
                (await db.References.SingleAsync(Token)).Archive(now);
                break;
            case InboxTargetKind.SomedayMaybe:
                (await db.SomedayMaybes.SingleAsync(Token)).Archive(now);
                break;
            default:
                if (changeSource)
                {
                    await db.Database.ExecuteSqlRawAsync("UPDATE waiting_fors SET source = 'Updated source'", Token);
                }
                else
                {
                    (await db.WaitingFors.SingleAsync(Token)).Resolve(now);
                }
                break;
        }
        await db.SaveChangesAsync(Token);
        Assert.Equal(InboxUndoOutcome.Conflict, (await Service(db).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Equal(1, await CountTargetsAsync(db));
    }

    [Fact]
    public async Task UndoAsync_CommitFailure_RollsBackWholeBatchAndAudit()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_undo() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test failure'; END $$;
                CREATE CONSTRAINT TRIGGER reject_undo AFTER UPDATE ON inbox_automatic_applications
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_undo();
                """, Token);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Service(db).UndoAsync(Owner, itemId, Token));
        }
        await using var verify = Db();
        Assert.Equal(3, await CountTargetsAsync(verify));
        Assert.Null((await verify.InboxAutomaticApplications.SingleAsync(Token)).UndoneAt);
        Assert.Equal(InboxItemStatus.Processed, (await verify.InboxItems.SingleAsync(Token)).Status);
        Assert.Single(await verify.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task UndoAsync_LostCommitAcknowledgement_RetryDoesNotRepeatDeletion()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        await using (var lost = Db(new LostCommitAcknowledgement()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(lost).UndoAsync(Owner, itemId, Token));
        }
        await using var restarted = Db();
        Assert.Equal(InboxUndoOutcome.AlreadyUndone, (await Service(restarted).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Equal(0, await CountTargetsAsync(restarted));
        Assert.Equal(2, await restarted.TelegramOutboxMessages.CountAsync(Token));
    }

    [Fact]
    public async Task UndoAsync_Cancelled_DoesNotChangeResult()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db).UndoAsync(Owner, itemId, cancelled.Token));
        Assert.Equal(3, await CountTargetsAsync(db));
        Assert.Null((await db.InboxAutomaticApplications.SingleAsync(Token)).UndoneAt);
    }

    [Fact]
    public async Task UndoAsync_ConcurrentManualProcessing_WaitsAndProcessesRestoredOriginal()
    {
        var (itemId, analysisId) = await SeedAsync(Composite());
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        await using var undo = Db();
        await using var transaction = await undo.Database.BeginTransactionAsync(Token);
        Assert.Equal(InboxUndoOutcome.Undone, (await Service(undo).UndoAsync(Owner, itemId, Token)).Outcome);
        var observer = new ObserveInboxLock();
        await using var manual = Db(observer);
        var processing = Processing(manual).ProcessAsync(Owner, new(itemId, InboxTargetKind.Reference, "Corrected"), Token);
        await observer.Attempted.Task.WaitAsync(Token);
        Assert.False(processing.IsCompleted);
        await transaction.CommitAsync(Token);
        Assert.Equal(ProcessInboxItemStatus.Processed, (await processing).Status);
        await using var verify = Db();
        Assert.Equal(1, await CountTargetsAsync(verify));
        Assert.Equal("Corrected", (await verify.References.SingleAsync(Token)).Text);
        Assert.Equal(InboxUndoOutcome.AlreadyUndone, (await Service(verify).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Single((await verify.InboxItems.SingleAsync(Token)).Targets);
    }

    [Fact]
    public async Task UndoAsync_ProjectReferencedByConcurrentTransaction_WaitsThenRefuses()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal(InboxTargetKind.Project)]);
        await using (var db = Db())
        {
            await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        }
        await using var writer = Db();
        await using var transaction = await writer.Database.BeginTransactionAsync(Token);
        var project = await writer.Projects.SingleAsync(Token);
        writer.Actions.Add(OwnDay.Domain.Actions.Action.Create(Owner, "Later action", DateTime.UtcNow, project.Id));
        await writer.SaveChangesAsync(Token);
        var observer = new ObserveUndoTargetLock();
        await using var undo = Db(observer);
        var undoing = Service(undo).UndoAsync(Owner, itemId, Token);
        await observer.Attempted.Task.WaitAsync(Token);
        Assert.False(undoing.IsCompleted);
        await transaction.CommitAsync(Token);
        Assert.Equal(InboxUndoOutcome.Conflict, (await undoing).Outcome);
        await using var verify = Db();
        Assert.Equal(2, await CountTargetsAsync(verify));
    }

    private sealed class ObserveUndoTargetLock : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("inbox_items", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Attempted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task UndoAsync_LegacyApplicationWithoutSnapshot_RefusesSafely()
    {
        var (itemId, analysisId) = await SeedAsync([Proposal()]);
        await using var db = Db();
        await Service(db).ApplyAsync(Owner, itemId, analysisId, Token);
        await db.InboxAutomaticApplications.ExecuteUpdateAsync(s => s.SetProperty(a => a.UndoStateJson, "[]"), Token);
        Assert.Equal(InboxUndoOutcome.Conflict, (await Service(db).UndoAsync(Owner, itemId, Token)).Outcome);
        Assert.Single(await db.Actions.ToListAsync(Token));
    }
}
