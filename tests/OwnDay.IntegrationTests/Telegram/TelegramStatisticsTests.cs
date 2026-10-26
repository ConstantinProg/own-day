using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramStatisticsTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("/stats", 2, "last 7 days", "en")]
    [InlineData("/stats today", 1, "today", "en")]
    [InlineData("/stats 30d", 3, "last 30 days", "en")]
    [InlineData("/stats all", 4, "all time", "en")]
    [InlineData("/stats", 2, "последние 7 дней", "ru")]
    [InlineData("/stats today", 1, "сегодня", "ru")]
    [InlineData("/stats 30d", 3, "последние 30 дней", "ru")]
    [InlineData("/stats all", 4, "всё время", "ru")]
    public async Task Post_StatisticsPeriods_IsOwnerQualifiedLocalizedAndDeduplicated(string command, int expected, string period, string locale)
    {
        using var host = new OwnDayHostFactory();
        using var factory = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new Clock());
        }));
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            foreach (var days in new[] { 0, 6, 29, 60 })
            {
                db.InboxItems.Add(InboxItem.Capture(new UserId(101), "PRIVATE SENTINEL", Now.Date.AddDays(-days)));
            }
            db.InboxItems.Add(InboxItem.Capture(new UserId(102), "OTHER PRIVATE SENTINEL", Now.Date));
            // The exclusive upper boundary must not include future/current instant events.
            db.InboxItems.Add(InboxItem.Capture(new UserId(101), "FUTURE", Now));
            db.InboxOperationEvents.Add(new InboxOperationEvent
            {
                UserId = 101, Code = "created", At = Now.AddMinutes(-1), Kind = InboxTargetKind.WaitingFor, TargetId = 10
            });
            db.InboxOperationEvents.Add(new InboxOperationEvent
            {
                UserId = 101, Code = "created", At = Now.AddMinutes(-1), Kind = InboxTargetKind.SomedayMaybe, TargetId = 20
            });
            db.InboxOperationEvents.Add(new InboxOperationEvent
            {
                UserId = 101, Code = "created", At = Now.AddMinutes(-1), Kind = InboxTargetKind.SomedayMaybe, TargetId = 21
            });
            await db.SaveChangesAsync();
        }
        await PostAsync(client, command, locale, "integration-test-webhook-secret");
        await PostAsync(client, command, locale, "integration-test-webhook-secret");
        using var verify = factory.Services.CreateScope();
        var saved = verify.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var report = Assert.Single(await saved.TelegramOutboxMessages.ToListAsync());
        Assert.Equal(101, report.ChatId);
        Assert.Contains(period, report.Text, StringComparison.Ordinal);
        Assert.Contains(locale == "ru" ? $"Получено: {expected}." : $"Received: {expected}.", report.Text, StringComparison.Ordinal);
        Assert.Contains("UTC", report.Text, StringComparison.Ordinal);
        Assert.Contains(locale == "ru" ? "ожиданий 1, заметок 0, идей 2" : "waiting 1, notes 0, ideas 2", report.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", report.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", report.Text, StringComparison.Ordinal);
        Assert.True(report.Text.Length < 4096);
        Assert.Single(await saved.ProcessedTelegramUpdates.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("integration-test-webhook-secret,integration-test-webhook-secret")]
    public async Task Post_StatisticsWithoutValidAuthentication_RejectsBeforeReadingOrQueueing(string? secret)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        var response = await PostAsync(client, "/stats all", "en", secret, assertOk: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.TelegramOutboxMessages.ToListAsync());
        Assert.Empty(await db.ProcessedTelegramUpdates.ToListAsync());
    }

    [Fact]
    public async Task Post_InvalidStatisticsArgument_ReturnsUsageWithoutCapturingInbox()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        await PostAsync(client, "/stats yesterday", "en", "integration-test-webhook-secret");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Contains("Use /stats", (await db.TelegramOutboxMessages.SingleAsync()).Text, StringComparison.Ordinal);
        Assert.Empty(await db.InboxItems.ToListAsync());
    }

    [Fact]
    public async Task Post_UnknownPriceAndCachedUsage_ShowsUnknownWithoutDoubleCountingAnalysisDiagnostics()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var now = DateTime.UtcNow.AddSeconds(-10);
            var item = InboxItem.Capture(new UserId(101), "PRIVATE", now);
            db.InboxItems.Add(item);
            await db.SaveChangesAsync();
            var analysis = InboxAnalysis.Start(new UserId(101), item.Id, 1, new("unknown-model", "inbox-v1", "test"),
                "{}", "x", "x", now);
            analysis.Finish(InboxAnalysisStatus.InvalidResponse, now.AddSeconds(1), "{\"inputTokens\":100,\"outputTokens\":20}");
            db.InboxAnalyses.Add(analysis);
            db.OpenAIRequestAttempts.Add(new()
            {
                AnalysisId = analysis.Id, UserId = 101, Sequence = 1, StartedAt = now, FinishedAt = now.AddSeconds(1),
                Model = "unknown-model", Code = "response", InputTokens = 100, OutputTokens = 20,
                ReservedTokens = 100000, ChargedTokens = 120
            });
            await db.SaveChangesAsync();
        }
        await PostAsync(client, "/stats all details", "ru", "integration-test-webhook-secret");
        using var verify = factory.Services.CreateScope();
        var report = await verify.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages.SingleAsync();
        Assert.Contains("Токены: вход 100, из них кеш неизвестно", report.Text, StringComparison.Ordinal);
        Assert.Contains("выход 20.", report.Text, StringComparison.Ordinal);
        Assert.Contains("Оценочная стоимость: неизвестно", report.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-model", report.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", report.Text, StringComparison.Ordinal);
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string command, string locale, string? secret, bool assertOk = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", secret.Split(','));
        }
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            update_id = 8001,
            message = new
            {
                message_id = 81, date = 1792929600,
                from = new { id = 101, is_bot = false, first_name = "Test", language_code = locale },
                chat = new { id = 101, type = "private" }, text = command
            }
        }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        if (assertOk)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return response.StatusCode;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
