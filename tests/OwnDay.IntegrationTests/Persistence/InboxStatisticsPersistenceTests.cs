using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class InboxStatisticsPersistenceTests
{
    [Fact]
    public async Task ReadAsync_RepeatedApplyUndoAndManualCorrection_CountsDistinctFactsAfterRestart()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var user = new UserId(101);
        var start = DateTime.UtcNow.AddMinutes(-1);
        long itemId;
        Guid analysisId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDay.Infrastructure.Persistence.OwnDayDbContext>();
            var item = await scope.ServiceProvider.GetRequiredService<InboxCaptureService>()
                .CaptureAsync(user, "PRIVATE", CancellationToken.None);
            itemId = item.Id;
            var context = JsonSerializer.Serialize(new InboxAnalysisContext(null, [], false), InboxAnalysisJson.Options);
            var analysis = InboxAnalysis.Start(user, item.Id, 1, new("fake", "inbox-v1", "test"),
                context, Hash(context), Hash(item.OriginalText), item.CapturedAt);
            analysis.Finish(InboxAnalysisStatus.Completed, item.CapturedAt.AddMilliseconds(1), "{\"inputTokens\":100,\"outputTokens\":20}", new()
            {
                Items = Enum.GetValues<InboxTargetKind>().Select(kind => Proposal(kind, item.CapturedAt)).ToArray()
            });
            db.InboxAnalyses.Add(analysis);
            await db.SaveChangesAsync();
            analysisId = analysis.Id;
        }
        using (var scope = factory.Services.CreateScope())
        {
            var automatic = scope.ServiceProvider.GetRequiredService<InboxAutomaticProcessingService>();
            await automatic.ApplyAsync(user, itemId, analysisId, CancellationToken.None);
            await automatic.ApplyAsync(user, itemId, analysisId, CancellationToken.None);
            Assert.Equal(InboxUndoOutcome.Undone, (await automatic.UndoAsync(user, itemId, CancellationToken.None)).Outcome);
            Assert.Equal(InboxUndoOutcome.AlreadyUndone, (await automatic.UndoAsync(user, itemId, CancellationToken.None)).Outcome);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var manual = scope.ServiceProvider.GetRequiredService<InboxProcessingService>();
            await manual.ProcessAsync(user, new(itemId, InboxTargetKind.Reference, "Correction"), CancellationToken.None);
            await manual.ProcessAsync(user, new(itemId, InboxTargetKind.Reference, "Repeat"), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<OwnDay.Infrastructure.Persistence.OwnDayDbContext>();
            var item = await scope.ServiceProvider.GetRequiredService<InboxCaptureService>()
                .CaptureAsync(user, "FAILED PRIVATE", CancellationToken.None);
            var failed = InboxAnalysis.Start(user, item.Id, 1, new("fake", "inbox-v1", "test"), "{}", "x", "x", item.CapturedAt);
            failed.Finish(InboxAnalysisStatus.Failed, item.CapturedAt, "{}");
            db.InboxAnalyses.Add(failed);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<InboxAutomaticProcessingService>()
                .ApplyAsync(user, item.Id, failed.Id, CancellationToken.None);
        }
        using var verify = factory.Services.CreateScope();
        var result = await verify.ServiceProvider.GetRequiredService<InboxStatisticsService>()
            .ReadAsync(user, new("all", start, DateTime.UtcNow.AddSeconds(1)), CancellationToken.None);
        Assert.Equal(2, result.Received);
        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Active);
        Assert.Equal(1, result.AutoApplied);
        Assert.Equal(1, result.ManualRequired);
        Assert.Equal(1, result.ManualInterventions);
        Assert.Equal(1, result.Undone);
        Assert.Equal(1, result.UndoRefused);
        Assert.Equal(new[] { 1, 1, 1, 2, 1 }, result.CreatedByKind);
        Assert.Equal(1, result.AnalysisSucceeded);
        Assert.Equal(1, result.AnalysisFailed);
        Assert.Equal(0.001, result.MeanAnalysisSeconds!.Value, 4);
        Assert.Equal(2, result.LegacyUnmeteredAnalyses);
        Assert.Equal(100, result.LegacyTokens.Input);
        Assert.Equal(20, result.LegacyTokens.Output);
        Assert.Equal(2, result.LegacyTokens.UnknownCached);
        Assert.Equal(0, result.ProviderAttempts);
        Assert.NotNull(result.MeanProcessingSeconds);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static InboxProposal Proposal(InboxTargetKind kind, DateTime now) => new()
    {
        Kind = kind, Title = "Target", Description = null, ProjectIds = [], Ambiguities = [],
        Properties = new()
        {
            Source = null, WaitingSince = null,
            WaitingSinceEvidence = null, UnresolvedDateText = null
        }
    };
}
