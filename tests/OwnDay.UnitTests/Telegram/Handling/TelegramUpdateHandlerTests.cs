using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.Interactions;
using OwnDay.Application.StructuredItems;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Cleanup;
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
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("/", ChatType.Private)]
    [InlineData("/ping@OtherBot", ChatType.Private)]
    [InlineData("/ping", ChatType.Group)]
    public async Task HandleAsync_IgnoredMessage_DoesNotAccessDatabase(string text, ChatType chatType)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var update = CreateTextMessageUpdate(text);
        update.Message!.Chat.Type = chatType;
        await fixture.Handler.HandleAsync(update);

        Assert.Empty(fixture.DatabaseCommands);
        Assert.Empty(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Empty(fixture.DbContext.TelegramOutboxMessages);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_Command_RecordsUpdateAndQueuesReply()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/ping", chatId: 123456789));

        Assert.DoesNotContain(fixture.DatabaseCommands, command => command.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        var processedUpdate = Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Equal(Now, processedUpdate.ReceivedAt);
        Assert.Equal(Now, processedUpdate.ProcessedAt);
        var outboxMessage = Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.Equal(123456789, outboxMessage.ChatId);
        Assert.Equal("pong", outboxMessage.Text);
        Assert.Equal(OutboxMessageStatus.Pending, outboxMessage.Status);
        Assert.Equal(Now, outboxMessage.CreatedAt);
        Assert.Equal(Now, outboxMessage.NextAttemptAt);
        Assert.Equal((123456789, 1), Assert.Single(fixture.Cleaner.Deletions));
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
        Assert.Single(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedUpdate_DoesNotAccessDatabase()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(new Update { Id = 1 });

        Assert.Empty(fixture.DatabaseCommands);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/ping")]
    public async Task HandleAsync_Cancelled_DoesNotAccessDatabase(string text)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.HandleAsync(CreateTextMessageUpdate(text), cancellation.Token));

        Assert.Empty(fixture.DatabaseCommands);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_OutboxSaveFails_RollsBackDeduplicationRecord()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.DbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_outbox BEFORE INSERT ON telegram_outbox_messages
            BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Handler.HandleAsync(CreateTextMessageUpdate("/ping")));

        fixture.DbContext.ChangeTracker.Clear();
        Assert.Empty(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Empty(fixture.DbContext.TelegramOutboxMessages);
        Assert.Empty(fixture.Cleaner.Deletions);

        await fixture.DbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_outbox");
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/ping"));

        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.Single(fixture.DbContext.TelegramOutboxMessages);
    }

    [Fact]
    public async Task HandleAsync_TaskReplySaveFails_RollsBackTaskAndDeduplication()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.DbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_outbox BEFORE INSERT ON telegram_outbox_messages
            BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Handler.HandleAsync(CreateTextMessageUpdate("/add Buy groceries")));

        fixture.DbContext.ChangeTracker.Clear();
        Assert.Empty(fixture.DbContext.Actions);
        Assert.Empty(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_OrdinaryPrivateText_CapturesAndCleansCorrectMessage()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("buy milk", chatId: 987));

        Assert.Equal((987, 1), Assert.Single(fixture.Cleaner.Deletions));
        Assert.Single(fixture.DbContext.InboxItems);
        Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
    }

    [Fact]
    public async Task HandleAsync_BotMessage_DoesNotProcessOrClean()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var update = CreateTextMessageUpdate("buy milk");
        update.Message!.From!.IsBot = true;

        await fixture.Handler.HandleAsync(update);

        Assert.Empty(fixture.DatabaseCommands);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_DeletionFails_PreservesCommittedResultAndOutbox()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        fixture.Cleaner.Failure = new InvalidOperationException("Telegram rejected deletion");

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("buy milk"));

        Assert.Single(fixture.DbContext.InboxItems);
        Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.Single(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_DeletionCancelled_PropagatesCancellationWithoutReprocessing()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Cleaner.Failure = new OperationCanceledException(cancellation.Token);
        var update = CreateTextMessageUpdate("buy milk");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.HandleAsync(update, cancellation.Token));

        Assert.Single(fixture.DbContext.InboxItems);
        Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.Equal(cancellation.Token, fixture.Cleaner.LastCancellationToken);
        await fixture.Handler.HandleAsync(update);
        Assert.Single(fixture.Cleaner.Deletions);
    }

    private static Update CreateTextMessageUpdate(string text, long chatId = 1) =>
        new()
        {
            Id = 1,
            Message = new Message
            {
                Id = 1,
                Date = Now,
                Chat = new Chat { Id = chatId, Type = ChatType.Private },
                From = new User { Id = chatId, IsBot = false, FirstName = "Test" },
                Text = text
            }
        };

    private sealed class HandlerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private HandlerFixture(
            SqliteConnection connection,
            OwnDayDbContext dbContext,
            TelegramUpdateHandler handler,
            RecordingMessageCleaner cleaner,
            List<string> databaseCommands)
        {
            _connection = connection;
            DbContext = dbContext;
            Handler = handler;
            Cleaner = cleaner;
            DatabaseCommands = databaseCommands;
        }

        public OwnDayDbContext DbContext { get; }

        public TelegramUpdateHandler Handler { get; }

        public RecordingMessageCleaner Cleaner { get; }

        public List<string> DatabaseCommands { get; }

        public static async Task<HandlerFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var databaseCommands = new List<string>();
            var dbContext = new OwnDayDbContext(
                new DbContextOptionsBuilder<OwnDayDbContext>()
                    .UseSqlite(connection)
                    .LogTo(databaseCommands.Add, [RelationalEventId.CommandExecuted])
                    .Options);
            await dbContext.Database.EnsureCreatedAsync();

            var router = new TelegramUpdateRouter(
                new TelegramCommandParser(),
                Options.Create(new TelegramOptions { BotUsername = "OwnDayBot" }));
            var cleaner = new RecordingMessageCleaner();
            var handler = new TelegramUpdateHandler(
                router,
                new IncomingCommandHandler(),
                new TelegramActionCommandHandler(new ActionService(new EfActionStore(dbContext), new FixedTimeProvider(Now))),
                new TelegramStructuredCommandHandler(new StructuredItemService(new EfStructuredItemStore(dbContext), new FixedTimeProvider(Now))),
                new TelegramInboxFlow(dbContext,
                    new InboxCaptureService(new EfInboxItemStore(dbContext), new FixedTimeProvider(Now)),
                    new InboxProcessingService(new EfInboxProcessingStore(dbContext),
                        new ActionService(new EfActionStore(dbContext), new FixedTimeProvider(Now)),
                        new StructuredItemService(new EfStructuredItemStore(dbContext), new FixedTimeProvider(Now)),
                        new FixedTimeProvider(Now)),
                    new StructuredItemService(new EfStructuredItemStore(dbContext), new FixedTimeProvider(Now)),
                    new FixedTimeProvider(Now)),
                dbContext,
                new FixedTimeProvider(Now),
                cleaner,
                NullLogger<TelegramUpdateHandler>.Instance);

            databaseCommands.Clear();
            return new HandlerFixture(connection, dbContext, handler, cleaner, databaseCommands);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingMessageCleaner : ITelegramMessageCleaner
    {
        public List<(long ChatId, int MessageId)> Deletions { get; } = [];

        public Exception? Failure { get; set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            Deletions.Add((chatId, messageId));
            LastCancellationToken = cancellationToken;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }
    }
}
