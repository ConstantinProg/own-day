using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class PostgresInboxAnalysisTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task AnalyzeAsync_ConcurrentAttempts_CommitsIndependentVersionsAndJsonProposalsWithoutHoldingLocks()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connectionString).Options);
#pragma warning disable EF1003 // Schema names are generated from GUIDs, not user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(
                new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString).Options;
            var owner = new UserId(41);
            var foreign = new UserId(42);
            long itemId;
            long projectId;
            var original = "  купит молоко для ремонта; может в горы  ";
            await using (var setup = new OwnDayDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var item = InboxItem.Capture(owner, original, DateTime.UtcNow);
                var project = Project.Create(owner, "Ремонт", DateTime.UtcNow);
                setup.InboxItems.Add(item);
                setup.Projects.Add(project);
                setup.Projects.Add(Project.Create(foreign, "Private project", DateTime.UtcNow));
                await setup.SaveChangesAsync();
                itemId = item.Id;
                projectId = project.Id;
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var bothInNetwork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            var ai = new FakeAnalyzer(async (request, token) =>
            {
                Assert.Equal(original, request.OriginalText);
                Assert.Equal(projectId, Assert.Single(request.Context.Projects).Id);
                Assert.Null(request.Context.TimeZoneId);
                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    bothInNetwork.SetResult();
                }

                // Both attempts must reach the network concurrently, with their versions already committed.
                await bothInNetwork.Task.WaitAsync(token);
                await using var observer = new OwnDayDbContext(options);
                Assert.Equal(2, await observer.InboxAnalyses.CountAsync(row => row.InboxItemId == itemId, token));
                var proposals = Enum.GetValues<InboxTargetKind>().Select(kind => new InboxProposal
                {
                    Kind = kind, Title = kind == InboxTargetKind.Action ? "Купить молоко для ремонта" : kind.ToString(),
                    Description = null,
                    Properties = new() { Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null },
                    ProjectIds = kind == InboxTargetKind.Action ? [projectId] : [], Ambiguities = []
                }).ToArray();
                return new(JsonSerializer.Serialize(new InboxAnalysisOutput { Items = proposals }, InboxAnalysisJson.Options),
                    "fake-model", "test-request", 100, 50);
            });

            async Task<InboxAnalysis?> RunAsync()
            {
                await using var db = new OwnDayDbContext(options);
                var checkingAi = new FakeAnalyzer((request, token) =>
                {
                    Assert.Null(db.Database.CurrentTransaction);
                    return ai.AnalyzeAsync(request, token);
                });
                return await Service(db, checkingAi).AnalyzeAsync(owner, itemId, cancellation.Token);
            }

            var attempts = await Task.WhenAll(RunAsync(), RunAsync());
            Assert.Equal(new[] { 1, 2 }, attempts.Select(attempt => attempt!.Version).Order());

            await using var verify = new OwnDayDbContext(options);
            var service = Service(verify, ai);
            var history = await service.GetAsync(owner, itemId, cancellation.Token);
            Assert.Equal(new[] { 2, 1 }, history.Select(row => row.Version));
            Assert.All(history, row =>
            {
                Assert.Equal(InboxAnalysisStatus.Completed, row.Status);
                Assert.NotNull(row.CompletedAt);
                Assert.Equal(5, row.Proposals.Count);
                Assert.Equal(projectId, Assert.Single(row.Proposals.Single(proposal => proposal.Kind == InboxTargetKind.Action)
                    .ReadProposal().ProjectIds));
            });
            Assert.Equal(10, await verify.Set<InboxAnalysisProposal>().CountAsync(cancellation.Token));
            Assert.Empty(await service.GetAsync(foreign, itemId, cancellation.Token));
            Assert.Null(await service.AnalyzeAsync(foreign, itemId, cancellation.Token));
            var originalItem = await verify.InboxItems.SingleAsync(cancellation.Token);
            Assert.Equal(original, originalItem.OriginalText);
            Assert.Equal(InboxItemStatus.Active, originalItem.Status);
            Assert.Null(originalItem.TargetId);
            Assert.Equal(2, await verify.Projects.CountAsync(cancellation.Token));
            Assert.Empty(verify.Actions);
            Assert.Empty(verify.WaitingFors);
            Assert.Empty(verify.References);
            Assert.Empty(verify.SomedayMaybes);

            // Ownership is also enforced by the composite FK, beyond the application query filter.
            await using var invalid = new OwnDayDbContext(options);
            invalid.InboxAnalyses.Add(InboxAnalysis.Start(foreign, itemId, 3, ai.Descriptor, "{}", "hash", "hash", DateTime.UtcNow));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync(cancellation.Token));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-generated schema.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task AnalyzeAsync_NulInTitleOrJsonPayload_PersistsInvalidResponseWithoutProposals(bool inPayload)
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION") ??
            throw new InvalidOperationException("OWNDAY_TEST_POSTGRES_CONNECTION is required for this test.");
        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = cancellation.Token;
        await using var admin = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(connectionString).Options);
#pragma warning disable EF1003 // Schema names are generated from GUIDs, not user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema, token);
#pragma warning restore EF1003
        try
        {
            var options = new DbContextOptionsBuilder<OwnDayDbContext>().UseNpgsql(
                new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString).Options;
            await using var db = new OwnDayDbContext(options);
            await db.Database.MigrateAsync(token);
            var owner = new UserId(41);
            var item = InboxItem.Capture(owner, "Reply", DateTime.UtcNow);
            db.InboxItems.Add(item);
            await db.SaveChangesAsync(token);
            var proposal = new InboxProposal
            {
                Kind = InboxTargetKind.Action, Title = inPayload ? "Reply" : "a\0b",
                Description = inPayload ? "a\0b" : null,
                Properties = new() { Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null },
                ProjectIds = [], Ambiguities = []
            };
            var json = JsonSerializer.Serialize(new InboxAnalysisOutput { Items = [proposal] }, InboxAnalysisJson.Options);
            var ai = new FakeAnalyzer((_, _) => Task.FromResult(new InboxAnalyzerResponse(json, "fake-model")));

            var result = await Service(db, ai).AnalyzeAsync(owner, item.Id, token);

            await using var reader = new OwnDayDbContext(options);
            var saved = Assert.Single(await Service(reader, ai).GetAsync(owner, item.Id, token));
            Assert.NotNull(result);
            Assert.Equal(result.Id, saved.Id);
            Assert.Equal(InboxAnalysisStatus.InvalidResponse, saved.Status);
            Assert.NotNull(saved.CompletedAt);
            Assert.Empty(saved.Proposals);
            Assert.Contains("invalid_response", saved.DiagnosticsJson);
            Assert.Equal(0, await reader.Set<InboxAnalysisProposal>().CountAsync(token));
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-generated schema.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static InboxAnalysisService Service(OwnDayDbContext db, IInboxAnalyzer ai) => new(
        new EfInboxItemStore(db), new EfInboxAnalysisStore(db, new EfInboxProcessingStore(db)), ai, new(), TimeProvider.System);

    private sealed class FakeAnalyzer(Func<InboxAnalysisRequest, CancellationToken, Task<InboxAnalyzerResponse>> run) : IInboxAnalyzer
    {
        public InboxAnalyzerDescriptor Descriptor => new("fake-model", "inbox-v1", "test-v1");
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken) =>
            run(request, cancellationToken);
    }
}
