using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TesterModeAuthorizationTests
{
    internal const string Secret = "ownday-tester-0000000000000000000000000000000000000000000000000000000000000000";
    internal static string SecretHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Secret)));

    [Fact]
    public async Task Post_TesterModeDisabled_RejectsPreviouslyAuthorizedUserAndProtectsSecret()
    {
        using var root = new OwnDayHostFactory();
        using var host = root.WithWebHostBuilder(builder => builder.UseSetting("TesterMode:Enabled", "false"));
        using var client = host.CreateClient();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO tester_mode_users (user_id, authorized, prompt_update_id, failed_attempts)
                VALUES (101, TRUE, 0, 0)
                """);
        }

        await PostAsync(client, 1, "/tester");
        await PostAsync(client, 2, "/tester_reset");
        await PostAsync(client, 3, Secret);

        using var read = host.Services.CreateScope();
        var saved = read.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.True((await saved.Set<TesterModeState>().SingleAsync()).Authorized);
        Assert.Equal(3, await saved.TelegramOutboxMessages.CountAsync(message => message.Text == "Tester Mode is disabled."));
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
    }

    [Fact]
    public async Task Post_SecretAttemptAndReactivation_PreservesAuthorizationUntilRevoked()
    {
        using var root = new OwnDayHostFactory();
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TesterMode:Enabled", "true");
            builder.UseSetting("TesterMode:SecretHash", SecretHash);
        });
        using var client = host.CreateClient();
        await PostAsync(client, 1, "/tester_status");
        await PostAsync(client, 2, "/tester", userId: 202, chatType: "group");
        await PostAsync(client, 3, "/tester");
        await PostAsync(client, 4, "incorrect-secret");
        await PostAsync(client, 5, Secret);
        await PostAsync(client, 5, Secret);
        await PostAsync(client, 6, "/tester");
        await PostAsync(client, 7, "/start");

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var state = await db.Set<TesterModeState>().AsNoTracking().SingleAsync();
        Assert.Equal(new UserId(101), state.UserId);
        Assert.True(state.Authorized);
        Assert.Null(state.PromptExpiresAt);
        var replies = await db.TelegramOutboxMessages.Select(message => message.Text).ToListAsync();
        Assert.Single(replies, reply => reply.Contains("Incorrect code", StringComparison.Ordinal));
        Assert.Equal(2, replies.Count(reply => reply.Contains("Tester Mode is active", StringComparison.Ordinal)));
        Assert.All(replies, reply => Assert.DoesNotContain(Secret, reply, StringComparison.Ordinal));

        await PostAsync(client, 8, "/tester_off");
        await PostAsync(client, 9, "/tester_status");
        Assert.False((await db.Set<TesterModeState>().AsNoTracking().SingleAsync()).Authorized);
        Assert.Equal(2, await db.TelegramOutboxMessages.CountAsync(message => message.Text.StartsWith("Use /tester and send")));
        Assert.Single(await db.Set<DialogueContext>().ToListAsync());
        Assert.Empty(await db.Set<LearningRequest>().ToListAsync());
    }

    [Fact]
    public async Task Post_RepeatedFailedAttempts_LocksAuthorizationAcrossNewPrompts()
    {
        using var root = new OwnDayHostFactory();
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TesterMode:Enabled", "true");
            builder.UseSetting("TesterMode:SecretHash", SecretHash);
        });
        using var client = host.CreateClient();
        await PostAsync(client, 1, "/tester");
        for (var update = 2; update <= 6; update++)
            await PostAsync(client, update, "ownday-tester-wrong");
        await PostAsync(client, 6, "ownday-tester-wrong");
        await PostAsync(client, 7, "/tester");
        await PostAsync(client, 8, Secret);

        using var read = host.Services.CreateScope();
        var saved = read.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var state = await saved.Set<TesterModeState>().SingleAsync();
        Assert.False(state.Authorized);
        Assert.Equal(5, state.FailedAttempts);
        Assert.Equal(3, await saved.TelegramOutboxMessages.CountAsync(message => message.Text.StartsWith("Too many attempts")));
        Assert.Empty(await saved.Set<LearningRequest>().ToListAsync());
    }

    internal static async Task PostAsync(HttpClient client, int updateId, string text, long userId = 101, string chatType = "private")
    {
        using var content = JsonContent.Create(new
        {
            update_id = updateId,
            message = new
            {
                message_id = updateId,
                date = 1790244000,
                from = new { id = userId, is_bot = false, first_name = "Test", language_code = "en" },
                chat = new { id = chatType == "private" ? userId : -100, type = chatType },
                text
            }
        });
        await PostUpdateAsync(client, content);
    }

    internal static async Task PostUpdateAsync(HttpClient client, HttpContent content, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.TryAddWithoutValidation("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = content;
        using var response = await client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
    }
}
