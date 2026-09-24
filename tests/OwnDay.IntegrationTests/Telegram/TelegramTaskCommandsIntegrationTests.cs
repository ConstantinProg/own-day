using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Domain.Tasks;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramTaskCommandsIntegrationTests
{
    [Fact]
    public async Task Post_TaskCommands_PersistAndIsolateTasksByTelegramUser()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();

        await PostAsync(client, 1, 101, "/add Buy groceries");
        await PostAsync(client, 2, 202, "/add Read book");
        await PostAsync(client, 3, 101, "/tasks");
        await PostAsync(client, 4, 202, "/done 1");
        await PostAsync(client, 5, 101, "/done 1");
        await PostAsync(client, 6, 101, "/tasks");
        await PostAsync(client, 7, 101, "/done 1");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await db.TelegramOutboxMessages.OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Text).ToListAsync();
        Assert.Contains(replies, message => message.Text == "Task #1 added: Buy groceries");
        Assert.Contains(replies, message => message.Text == "Task #2 added: Read book");
        Assert.Contains(replies, message => message.Text == "#1 Buy groceries");
        Assert.Contains(replies, message => message.ChatId == 202 && message.Text == "Task not found.");
        Assert.Contains(replies, message => message.ChatId == 101 && message.Text == "Task #1 completed.");
        Assert.Contains(replies, message => message.ChatId == 101 && message.Text == "No active tasks.");
        Assert.Contains(replies, message => message.ChatId == 101 && message.Text == "Task #1 is already completed.");
        Assert.Equal(2, await db.Tasks.CountAsync());
    }

    [Theory]
    [InlineData("/add", "Usage: /add <title>")]
    [InlineData("/done", "Usage: /done <task-id>")]
    [InlineData("/done nope", "Usage: /done <task-id>")]
    [InlineData("/done 99", "Task not found.")]
    [InlineData("/tasks extra", "Usage: /tasks")]
    public async Task Post_InvalidTaskCommand_QueuesHelpfulReply(string command, string expectedReply)
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();

        await PostAsync(client, 1, 101, command);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal(expectedReply, Assert.Single(await db.TelegramOutboxMessages.ToListAsync()).Text);
        Assert.Empty(await db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Post_TooLongTitle_DoesNotCreateTask()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();

        await PostAsync(client, 1, 101, "/add " + new string('x', 201));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Equal("Task title must be at most 200 characters.",
            Assert.Single(await db.TelegramOutboxMessages.ToListAsync()).Text);
        Assert.Empty(await db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Post_ManyTasks_QueuesOrderedMessagesWithinTelegramLimit()
    {
        using var factory = new OwnDayHostFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            for (var index = 0; index < 50; index++)
            {
                db.Tasks.Add(TaskItem.Create(new UserId(101),
                    new string('x', TaskItem.MaxTitleLength), DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
        }

        await PostAsync(client, 1, 101, "/tasks");

        using var resultScope = factory.Services.CreateScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var messages = await resultDb.TelegramOutboxMessages
            .OrderBy(message => message.CreatedAt).ToListAsync();
        var tasks = await resultDb.Tasks.OrderBy(task => task.Id).ToListAsync();

        Assert.True(messages.Count > 1);
        Assert.All(messages, message => Assert.InRange(message.Text.Length, 1, 4096));
        Assert.Equal(string.Join('\n', tasks.Select(task => $"#{task.Id} {task.Title}")),
            string.Join('\n', messages.Select(message => message.Text)));
    }

    private static async Task PostAsync(HttpClient client, long updateId, long userId, string command)
    {
        var json = $$"""
            {
              "update_id": {{updateId}},
              "message": {
                "message_id": {{updateId}},
                "date": 1790244000,
                "from": { "id": {{userId}}, "is_bot": false, "first_name": "Test" },
                "chat": { "id": {{userId}}, "type": "private" },
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
}
