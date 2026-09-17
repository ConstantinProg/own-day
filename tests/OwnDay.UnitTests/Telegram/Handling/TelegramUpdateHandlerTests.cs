using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OwnDay.Application.Interactions;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Handling;

public sealed class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task HandleAsync_NonCommand_RecordsUpdateWithoutOutboxMessage()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("hello"));

        Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Empty(fixture.DbContext.TelegramOutboxMessages);
    }

    [Fact]
    public async Task HandleAsync_Command_RecordsUpdateAndQueuesReply()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/ping", chatId: 123456789));

        var processedUpdate = Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.NotNull(processedUpdate.ProcessedAt);
        var outboxMessage = Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.Equal(123456789, outboxMessage.ChatId);
        Assert.Equal("pong", outboxMessage.Text);
        Assert.Equal(OutboxMessageStatus.Pending, outboxMessage.Status);
    }

    [Fact]
    public async Task HandleAsync_DuplicateCommand_QueuesReplyOnce()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var update = CreateTextMessageUpdate("/ping");

        await fixture.Handler.HandleAsync(update);
        await fixture.Handler.HandleAsync(update);

        Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Single(fixture.DbContext.TelegramOutboxMessages);
    }

    private static Update CreateTextMessageUpdate(string text, long chatId = 1) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = chatId, Type = ChatType.Private },
                Text = text
            }
        };

    private sealed class HandlerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private HandlerFixture(
            SqliteConnection connection,
            OwnDayDbContext dbContext,
            TelegramUpdateHandler handler)
        {
            _connection = connection;
            DbContext = dbContext;
            Handler = handler;
        }

        public OwnDayDbContext DbContext { get; }

        public TelegramUpdateHandler Handler { get; }

        public static async Task<HandlerFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbContext = new OwnDayDbContext(
                new DbContextOptionsBuilder<OwnDayDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await dbContext.Database.EnsureCreatedAsync();

            var router = new TelegramUpdateRouter(
                new TelegramCommandParser(),
                Options.Create(new TelegramOptions { BotUsername = "OwnDayBot" }));
            var handler = new TelegramUpdateHandler(
                router,
                new IncomingCommandHandler(),
                dbContext);

            return new HandlerFixture(connection, dbContext, handler);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
