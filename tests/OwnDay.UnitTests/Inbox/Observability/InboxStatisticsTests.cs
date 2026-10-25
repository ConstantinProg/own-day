using OwnDay.Application.Inbox.Observability;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Observability;

public sealed class InboxStatisticsTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("", 19)]
    [InlineData("today", 25)]
    [InlineData("30d", 26)]
    public void Parse_CalendarPeriod_UsesUtcMidnightAndReportInstant(string argument, int day)
    {
        var period = Assert.IsType<StatisticsPeriod>(StatisticsPeriod.Parse(argument, Now));
        Assert.Equal(day, period.Start.Day);
        Assert.Equal(TimeSpan.Zero, period.Start.TimeOfDay);
        Assert.Equal(argument == "30d" ? 9 : 10, period.Start.Month);
        Assert.Equal(DateTimeKind.Utc, period.Start.Kind);
        Assert.Equal(Now, period.End);
    }

    [Fact]
    public void Parse_AllTimeAndYearBoundary_UsesConsistentUtcRange()
    {
        Assert.Equal(DateTime.MinValue, StatisticsPeriod.Parse("all", Now)!.Start);
        Assert.Equal(new DateTime(2025, 12, 26, 0, 0, 0, DateTimeKind.Utc),
            StatisticsPeriod.Parse("", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))!.Start);
        Assert.Null(StatisticsPeriod.Parse("7d", Now));
        Assert.Throws<ArgumentException>(() => StatisticsPeriod.Parse("today", DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }

    [Fact]
    public void Estimate_CachedInput_IsSubsetOfInputAndUsesSeparateTariff()
    {
        var price = new OpenAIModelPrice { InputUsdPerMillion = 2, CachedInputUsdPerMillion = 0.5m, OutputUsdPerMillion = 8 };
        Assert.Equal(0.003m, price.Estimate(1000, 400, 200));
        Assert.Null(price.Estimate(1000, null, 200));
        Assert.Null(price.Estimate(1000, 1001, 200));
        Assert.Null(price.Estimate(null, 0, 200));
        Assert.Null(price.Estimate(1000, 0, -1));
        Assert.Null(new OpenAIModelPrice().Estimate(1000, 0, 200));
    }

    [Fact]
    public void Aggregate_PartiallyKnownUsage_PreservesUnknownCountsAndKnownSubtotal()
    {
        var aggregate = TokenStatistics.Aggregate([
            new() { InputTokens = 100, CachedInputTokens = 40, OutputTokens = 20, EstimatedUsd = 0.001m },
            new() { InputTokens = 50, OutputTokens = 10 },
            new()
        ]);
        Assert.Equal(150, aggregate.Input);
        Assert.Equal(40, aggregate.Cached);
        Assert.Equal(30, aggregate.Output);
        Assert.Equal(0.001m, aggregate.KnownEstimatedUsd);
        Assert.Equal(1, aggregate.UnknownInput);
        Assert.Equal(2, aggregate.UnknownCached);
        Assert.Equal(1, aggregate.UnknownOutput);
        Assert.Equal(2, aggregate.UnknownCost);
        Assert.Equal(0, TokenStatistics.Aggregate([]).UnknownCost);
    }
}
