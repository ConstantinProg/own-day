using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

[Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
public sealed class PostgresInboxPolicyTests : IAsyncLifetime
{
    private readonly string _schema = "ownday_test_" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private DbContextOptions<OwnDayDbContext> _options = null!;
    private OwnDayDbContext _admin = null!;
    private static readonly UserId Owner = new(41);
    private CancellationToken Token => _deadline.Token;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        _admin = new(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connection).Options);
#pragma warning disable EF1003 // Generated GUID schema, never user input.
        await _admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + _schema, Token);
#pragma warning restore EF1003
        _options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(
            new NpgsqlConnectionStringBuilder(connection) { SearchPath = _schema }.ConnectionString).Options;
        await using var db = new OwnDayDbContext(_options);
        await db.Database.MigrateAsync(Token);
    }

    public async Task DisposeAsync()
    {
#pragma warning disable EF1003 // Same generated schema.
        await _admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + _schema + " CASCADE");
#pragma warning restore EF1003
        await _admin.DisposeAsync();
        _deadline.Dispose();
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("deleted")]
    [InlineData("foreign")]
    [InlineData("renamed")]
    public async Task EvaluateAsync_CurrentProjectDiffersFromHistoricalContext_BlocksAndPreservesPreviousAssessment(string change)
    {
        var (itemId, analysisId, projectId) = await SeedAsync();
        await using var reader = new OwnDayDbContext(_options);
        var service = Service(reader);
        var first = await service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        var repeated = await service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(repeated));
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, first.Route);
        Assert.Empty(reader.ChangeTracker.Entries());

        await using (var writer = new OwnDayDbContext(_options))
        {
            var project = await writer.Projects.SingleAsync(Token);
            switch (change)
            {
                case "completed": project.Complete(DateTime.UtcNow); break;
                case "deleted": writer.Projects.Remove(project); break;
                case "foreign":
                    await writer.Projects.Where(p => p.Id == projectId).ExecuteUpdateAsync(
                        setters => setters.SetProperty(p => p.UserId, new UserId(42)), Token);
                    break;
                case "renamed": writer.Entry(project).Property(p => p.Title).CurrentValue = "Changed meaning"; break;
            }
            await writer.SaveChangesAsync(Token);
        }
        var next = await service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, next.Route);
        Assert.Contains(change == "renamed" ? InboxPolicyReason.ProjectChanged : InboxPolicyReason.ProjectUnavailable, next.Reasons);
        Assert.NotEqual(first.StateFingerprint, next.StateFingerprint);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, first.Route);
        Assert.Equal(projectId, Assert.Single((await reader.InboxAnalyses.AsNoTracking().Include(a => a.Proposals)
            .SingleAsync(Token)).Proposals).ReadProposal().ProjectIds[0]);
        Assert.Equal(InboxItemStatus.Active, (await reader.InboxItems.AsNoTracking().SingleAsync(Token)).Status);
        Assert.Empty(await reader.Actions.ToListAsync(Token));
        Assert.Empty(await reader.TelegramOutboxMessages.ToListAsync(Token));
    }

    [Fact]
    public async Task EvaluateAsync_OwnershipLatestVersionCancellationAndAmbientTransaction_EnforcesReadBoundary()
    {
        var (itemId, analysisId, _) = await SeedAsync();
        await using var db = new OwnDayDbContext(_options);
        var service = Service(db);
        var foreign = await service.EvaluateAsync(new UserId(42), itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Contains(InboxPolicyReason.AnalysisUnavailable, foreign.Reasons);
        Assert.Empty(foreign.Proposals);
        var missing = await service.EvaluateAsync(Owner, itemId, Guid.NewGuid(), InboxPolicyEngine.Version, Token);
        Assert.Contains(InboxPolicyReason.AnalysisUnavailable, missing.Reasons);
        var analysis = await db.InboxAnalyses.AsNoTracking().SingleAsync(Token);
        db.InboxAnalyses.Add(InboxAnalysis.Start(Owner, itemId, 2, new("fake", "inbox-v1", "test"),
            analysis.ContextJson, analysis.ContextFingerprint, analysis.InputFingerprint, DateTime.UtcNow));
        await db.SaveChangesAsync(Token);
        var superseded = await service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Contains(InboxPolicyReason.AnalysisSuperseded, superseded.Reasons);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, cancelled.Token));
        await using var transaction = await db.Database.BeginTransactionAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token));
    }

    [Fact]
    public async Task EvaluateAsync_ConcurrentProjectChange_UsesOneSnapshotAndRechecksOnNextEvaluation()
    {
        var (itemId, analysisId, _) = await SeedAsync();
        var interceptor = new AfterInboxRead(async () =>
        {
            await using var writer = new OwnDayDbContext(_options);
            var project = await writer.Projects.SingleAsync(Token);
            project.Complete(DateTime.UtcNow);
            await writer.SaveChangesAsync(Token);
        });
        var options = new DbContextOptionsBuilder<OwnDayDbContext>(_options).AddInterceptors(interceptor).Options;
        await using var db = new OwnDayDbContext(options);
        var first = await Service(db).EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.True(interceptor.Fired);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, first.Route);
        var second = await Service(db).EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Contains(InboxPolicyReason.ProjectUnavailable, second.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_RegisteredWithoutProvider_DoesNotSaveTrackedMutations()
    {
        var (itemId, analysisId, _) = await SeedAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => new OwnDayDbContext(_options));
        services.AddInboxPolicy();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var tracked = await db.InboxItems.SingleAsync(Token);
        tracked.Discard(DateTime.UtcNow);
        var result = await scope.ServiceProvider.GetRequiredService<InboxPolicyService>()
            .EvaluateAsync(Owner, itemId, analysisId, InboxPolicyEngine.Version, Token);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, result.Route);
        await using var observer = new OwnDayDbContext(_options);
        Assert.Equal(InboxItemStatus.Active, (await observer.InboxItems.SingleAsync(Token)).Status);
        Assert.Single(await observer.InboxAnalyses.ToListAsync(Token));
    }

    private async Task<(long ItemId, Guid AnalysisId, long ProjectId)> SeedAsync()
    {
        await using var db = new OwnDayDbContext(_options);
        var now = DateTime.UtcNow;
        var item = InboxItem.Capture(Owner, "Call about renovation", now);
        var project = Project.Create(Owner, "Renovation", now);
        db.InboxItems.Add(item);
        db.Projects.Add(project);
        await db.SaveChangesAsync(Token);
        await db.Entry(project).ReloadAsync(Token);
        var context = JsonSerializer.Serialize(new InboxAnalysisContext(null, [new(project.Id, project.Title, project.CreatedAt)], false), InboxAnalysisJson.Options);
        var analysis = InboxAnalysis.Start(Owner, item.Id, 1, new("fake", "inbox-v1", "test"), context, Hash(context), Hash(item.OriginalText), now);
        analysis.Finish(InboxAnalysisStatus.Completed, now, "{}", new()
        {
            Items = [new()
            {
                Kind = InboxTargetKind.Action, Title = "Call about renovation", Description = null,
                ProjectIds = [project.Id], Ambiguities = [], Properties = new()
                {
                    Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null
                }
            }]
        });
        db.InboxAnalyses.Add(analysis);
        await db.SaveChangesAsync(Token);
        return (item.Id, analysis.Id, project.Id);
    }

    private static InboxPolicyService Service(OwnDayDbContext db) => new(new EfInboxPolicyStore(db), new());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class AfterInboxRead(Func<Task> change) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM inbox_items", StringComparison.Ordinal))
            {
                Fired = true;
                await change();
            }
            return result;
        }
    }
}
