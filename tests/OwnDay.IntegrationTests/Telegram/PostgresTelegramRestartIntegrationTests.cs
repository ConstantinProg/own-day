using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class PostgresTelegramRestartIntegrationTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL via OWNDAY_TEST_POSTGRES_CONNECTION")]
    public async Task Post_TaskCommandsAcrossHostRestart_RetainsAndCompletesTasks()
    {
        var connectionString = Environment.GetEnvironmentVariable("OWNDAY_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var schema = "ownday_test_" + Guid.NewGuid().ToString("N");
        var adminOptions = new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(connectionString).Options;
        await using var admin = new OwnDayDbContext(adminOptions);
#pragma warning disable EF1003 // Schema name is generated from a GUID, never user input.
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA " + schema);
#pragma warning restore EF1003

        try
        {
            var testConnectionString = connectionString + ";Search Path=" + schema;
            var options = new DbContextOptionsBuilder<OwnDayDbContext>()
                .UseNpgsql(testConnectionString).Options;
            await using (var db = new OwnDayDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            using (var firstHost = new PostgresHostFactory(testConnectionString))
            using (var firstClient = firstHost.CreateClient())
            {
                await PostAsync(firstClient, 1001, "/add Buy groceries");
                await PostAsync(firstClient, 1002, "/add Read book");
                await AssertReplyAsync(firstHost, "Task #1 added: Buy groceries");
                await AssertReplyAsync(firstHost, "Task #2 added: Read book");
            }

            using (var restartedHost = new PostgresHostFactory(testConnectionString))
            using (var restartedClient = restartedHost.CreateClient())
            {
                await PostAsync(restartedClient, 1003, "/tasks");
                await AssertReplyAsync(restartedHost, "#1 Buy groceries\n#2 Read book");

                await PostAsync(restartedClient, 1004, "/done 1");
                await AssertReplyAsync(restartedHost, "Task #1 completed.");

                await PostAsync(restartedClient, 1005, "/tasks");
                await AssertReplyAsync(restartedHost, "#2 Read book");
            }
        }
        finally
        {
#pragma warning disable EF1003 // Same GUID-derived schema name used above.
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA " + schema + " CASCADE");
#pragma warning restore EF1003
        }
    }

    private static async Task AssertReplyAsync(PostgresHostFactory host, string expectedText)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), message => message.Text == expectedText);
    }

    private static async Task PostAsync(HttpClient client, long updateId, string command)
    {
        var json = $$"""
            {
              "update_id": {{updateId}},
              "message": {
                "message_id": {{updateId}},
                "date": 1790244000,
                "from": { "id": 101, "is_bot": false, "first_name": "Test" },
                "chat": { "id": 101, "type": "private" },
                "text": "{{command}}"
              }
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.TryAddWithoutValidation(
            "X-Telegram-Bot-Api-Secret-Token",
            "integration-test-webhook-secret");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class PostgresHostFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(GetHostContentRoot());
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
            builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
            builder.UseSetting("Telegram:WebhookSecret", "integration-test-webhook-secret");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }

        private static string GetHostContentRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OwnDay.slnx")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Could not locate the OwnDay solution root.");
            }

            return Path.Combine(directory.FullName, "src", "OwnDay.Host");
        }
    }
}
