using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed partial class PostgresObservabilityTests
{
    [Theory]
    [InlineData("calls_service")]
    [InlineData("calls_user")]
    [InlineData("tokens_service")]
    [InlineData("tokens_user")]
    public async Task AdmitAsync_FiniteServiceAndUserCaps_PreventConcurrentOversubscription(string cap)
    {
        var settings = MonthlySettings();
        settings.Models["fake"].OutputUsdPerMillion = 10_000;
        if (cap == "calls_service") settings.MonthlyCallLimit = 1;
        if (cap == "calls_user") settings.MonthlyUserCallLimit = 1;
        if (cap == "tokens_service") settings.MonthlyTokenLimit = 150;
        if (cap == "tokens_user") settings.MonthlyUserTokenLimit = 150;
        using var provider = Provider();
        var ledger = Ledger(provider, settings, enforceMonthly: true);
        var admitted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default)));
        Assert.Single(admitted, id => id is not null);
    }

    [Fact]
    public async Task MigrateAsync_LegacyFinancialHistory_PreservesUnknownCostAndBlocksInventedFreeBudget()
    {
        await using (var db = new OwnDayDbContext(_options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261112150000_AddLearningProviderAttempts");
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO openai_request_attempts ("Id", "AnalysisId", "UserId", "Sequence", "Model", "StartedAt", "Code", "ReservedTokens", "ChargedTokens")
                VALUES ({id}, {_analysisId}, {Owner.Value}, 1, 'legacy', clock_timestamp(), 'pending', 100, 100)
                """);
            await db.Database.MigrateAsync();
        }
        using var provider = Provider();
        Assert.Null(await Ledger(provider, MonthlySettings(), enforceMonthly: true).AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        await using var observer = new OwnDayDbContext(_options);
        var legacy = await observer.OpenAIRequestAttempts.SingleAsync();
        Assert.Equal(_analysisId, legacy.AnalysisId);
        Assert.Null(legacy.LearningProviderAttemptId);
        Assert.Null(legacy.EstimatedUsd);
        Assert.Null(legacy.ChargedUsd);
        Assert.Equal(100, legacy.ChargedTokens);
    }

    [Fact]
    public async Task AdmitAsync_InboxAndLearning_ShareConcurrentMonthlyBudgetAndKeepSourcesDistinct()
    {
        Guid physicalId;
        await using (var db = new OwnDayDbContext(_options))
        {
            var request = new LearningRequest
            {
                UserId = Owner, Sequence = 1, SourceKey = "telegram:update:40", SourceUpdateId = 40, ChatId = Owner.Value,
                Status = LearningRequestStatus.Ready, Outcome = null, ReceivedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow,
                NormalizedInput = "{}"
            };
            var step = new LearningProviderStep { RequestId = request.Id, Ordinal = 0, Kind = "assess" };
            var attempt = new LearningProviderAttempt
            {
                RequestId = request.Id, Ordinal = 0, Number = 1, LeaseToken = Guid.NewGuid(), StartedAt = DateTime.UtcNow
            };
            db.AddRange(request, step, attempt);
            await db.SaveChangesAsync();
            physicalId = attempt.Id;
        }
        using var provider = Provider();
        var ledger = Ledger(provider, MonthlySettings(), enforceMonthly: true);
        var admitted = await Task.WhenAll(ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default),
            ledger.AdmitLearningAsync(physicalId, new("fake", "assess-v1", "learning-v1"), default));
        Assert.All(admitted, id => Assert.NotNull(id));
        Assert.Null(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        Assert.Null(await ledger.AdmitLearningAsync(physicalId, new("fake", "assess-v1", "learning-v1"), default));
        await using var observer = new OwnDayDbContext(_options);
        var rows = await observer.OpenAIRequestAttempts.ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.AnalysisId == _analysisId && row.LearningProviderAttemptId == null);
        Assert.Single(rows, row => row.AnalysisId == null && row.LearningProviderAttemptId == physicalId);
        Assert.All(rows, row => Assert.Equal(Owner.Value, row.UserId));
        var statistics = await new EfInboxStatisticsStore(observer).ReadAsync(Owner,
            StatisticsPeriod.Parse("all", DateTime.UtcNow)!, default);
        Assert.Equal(1, statistics.ProviderAttempts);
    }

    private static OpenAICostOptions MonthlySettings() => new()
    {
        TokenLimit = null, TokensPerRequest = 100,
        Models = new(StringComparer.Ordinal)
        {
            ["fake"] = new() { InputUsdPerMillion = 10_000, CachedInputUsdPerMillion = 100, OutputUsdPerMillion = 40_000 }
        }
    };

    [Fact]
    public async Task AdmitAsync_DefaultMonthlyBudget_ReservesAtMostTenDollarsUnderConcurrentAdmission()
    {
        using var provider = Provider();
        var ledger = Ledger(provider, MonthlySettings(), enforceMonthly: true);
        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default)));
        Assert.Equal(2, ids.Count(id => id is not null));
        using var restarted = Provider();
        Assert.Null(await Ledger(restarted, MonthlySettings(), enforceMonthly: true).AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
    }

    [Fact]
    public async Task AdmitAsync_UnknownModelPrice_RejectsBeforeProviderInvocation()
    {
        using var provider = Provider();
        Assert.Null(await Ledger(provider, MonthlySettings(), enforceMonthly: true).AdmitAsync(Request(), new("unpriced", "inbox-v1", "test"), default));
        await using var db = new OwnDayDbContext(_options);
        Assert.Empty(await db.OpenAIRequestAttempts.ToListAsync());
    }

    [Fact]
    public async Task AdmitAsync_PreviousCalendarMonth_DoesNotConsumeNewMonthlyBudget()
    {
        using var provider = Provider();
        var ledger = Ledger(provider, MonthlySettings(), enforceMonthly: true);
        for (var i = 0; i < 2; i++) Assert.NotNull(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        await using (var db = new OwnDayDbContext(_options))
            await db.Database.ExecuteSqlRawAsync("""UPDATE openai_request_attempts SET "StartedAt" = date_trunc('month', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' - interval '1 day'""");
        for (var i = 0; i < 2; i++) Assert.NotNull(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        Assert.Null(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
    }

    [Fact]
    public async Task FinishAsync_UnknownConsumption_KeepsReservationUntilKnownReconciliation()
    {
        using var provider = Provider();
        var ledger = Ledger(provider, MonthlySettings(), enforceMonthly: true);
        var first = (await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default))!.Value;
        var second = (await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default))!.Value;
        await ledger.FinishAsync(first, "provider_failure", null, default);
        await using (var db = new OwnDayDbContext(_options))
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20261112150000_AddLearningProviderAttempts"));
        Assert.Null(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        await ledger.FinishAsync(second, "response", new("", "fake", "provider-id", 10, 10, 0), default);
        await ledger.FinishAsync(second, "response", new("", "fake", "provider-id", 0, 0, 0), default);
        Assert.NotNull(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
        Assert.Null(await ledger.AdmitAsync(Request(), new("fake", "inbox-v1", "test"), default));
    }
}
