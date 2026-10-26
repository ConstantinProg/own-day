using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Localization;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Handling;

public sealed class TelegramUpdateHandlerTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task HandleAsync_LanguageSelectionDeadline_ExpiresAtTenMinutes(long offsetTicks, bool expired)
    {
        var clock = new AdjustableTimeProvider();
        await using var fixture = await HandlerFixture.CreateAsync(clock);
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("1", updateId: 2));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language", updateId: 3));
        Assert.Equal(Now.AddMinutes(10), Assert.Single(fixture.DbContext.TelegramUserLanguages).SelectionExpiresAt);
        clock.UtcNow = Now.AddMinutes(10).AddTicks(offsetTicks);

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 4));

        fixture.DbContext.ChangeTracker.Clear();
        var preference = await fixture.DbContext.TelegramUserLanguages.SingleAsync();
        Assert.Equal(expired ? "en" : "ru", preference.Locale);
        Assert.Null(preference.SelectionExpiresAt);
        var replies = await fixture.DbContext.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(4, replies.Count);
        if (expired)
        {
            Assert.Equal("2", (await fixture.DbContext.InboxItems.SingleAsync()).OriginalText);
            Assert.Contains(replies, reply => reply.Text == "Saved to inbox.");
        }
        else
        {
            Assert.Empty(await fixture.DbContext.InboxItems.ToListAsync());
            Assert.Contains(replies, reply => reply.Text == "Язык изменён на русский.");
        }
    }

    [Fact]
    public async Task HandleAsync_ExpiredLanguageSelection_CapturesNewEntryWithoutResumingOldDraft()
    {
        var clock = new AdjustableTimeProvider();
        await using var fixture = await HandlerFixture.CreateAsync(clock);
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("original"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/inbox 1", updateId: 2));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language", updateId: 3));
        clock.UtcNow = Now.AddMinutes(10);

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 4));

        fixture.DbContext.ChangeTracker.Clear();
        var preference = await fixture.DbContext.TelegramUserLanguages.SingleAsync();
        Assert.Null(preference.Locale);
        Assert.Null(preference.SelectionExpiresAt);
        Assert.Equal(2, await fixture.DbContext.InboxItems.CountAsync());
        Assert.Empty(await fixture.DbContext.TelegramInboxDrafts.ToListAsync());
        Assert.Empty(await fixture.DbContext.Projects.ToListAsync());
        Assert.Contains(await fixture.DbContext.TelegramOutboxMessages.ToListAsync(),
            reply => reply.Text == "Saved to inbox.");
    }

    [Theory]
    [InlineData("/start", "en", "Send a thought, task or note.")]
    [InlineData("/ping", "en", "pong")]
    [InlineData("/unknown", "en", "Unknown command. Use /help.")]
    [InlineData("/start", "ru", "Отправьте мысль, задачу или заметку.")]
    [InlineData("/ping", "ru", "pong")]
    [InlineData("/unknown", "ru", "Неизвестная команда. Отправьте /help.")]
    public async Task HandleAsync_ServiceCommand_QueuesLocalizedReply(string command, string locale, string expected)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate(command, languageCode: locale));

        Assert.StartsWith(expected, Assert.Single(fixture.DbContext.TelegramOutboxMessages).Text, StringComparison.Ordinal);
        Assert.Empty(fixture.DbContext.InboxItems);
    }

    [Theory]
    [InlineData("en", "Send text")]
    [InlineData("ru", "Отправьте текст")]
    public async Task HandleAsync_HelpCommand_QueuesLocalizedCommandInstructions(string locale, string expectedPrefix)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/help", languageCode: locale));

        var text = Assert.Single(fixture.DbContext.TelegramOutboxMessages).Text;
        Assert.StartsWith(expectedPrefix, text, StringComparison.Ordinal);
        Assert.Contains("/inbox", text, StringComparison.Ordinal);
        Assert.Contains("/done", text, StringComparison.Ordinal);
        Assert.Contains("/settings", text, StringComparison.Ordinal);
        Assert.Contains("/undo", text, StringComparison.Ordinal);
        Assert.Contains("/stats", text, StringComparison.Ordinal);
    }

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
    }

    [Fact]
    public async Task HandleAsync_Command_RecordsUpdateAndQueuesReply()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/ping", chatId: 123456789));

        var processedUpdate = Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Equal(Now, processedUpdate.ReceivedAt);
        Assert.Equal(Now, processedUpdate.ProcessedAt);
        var outboxMessage = Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.Equal(123456789, outboxMessage.ChatId);
        Assert.Equal("pong", outboxMessage.Text);
        Assert.Equal(OutboxMessageStatus.Pending, outboxMessage.Status);
        Assert.Equal(Now, outboxMessage.CreatedAt);
        Assert.Equal(Now, outboxMessage.NextAttemptAt);
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

    [Fact]
    public async Task HandleAsync_UnsupportedUpdate_DoesNotAccessDatabase()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(new Update { Id = 1 });

        Assert.Empty(fixture.DatabaseCommands);
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
    }

    [Fact]
    public async Task HandleAsync_OrdinaryPrivateText_CapturesAndPreservesChatHistory()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("buy milk", chatId: 987));

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
    }

    [Theory]
    [InlineData("/language")]
    [InlineData("/language wrong")]
    public async Task HandleAsync_LanguageCommand_ShowsNumberedOptions(string command)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate(command));

        Assert.Equal(TelegramTexts.Get("en", "language.menu"),
            Assert.Single(fixture.DbContext.TelegramOutboxMessages).Text);
        Assert.NotNull(Assert.Single(fixture.DbContext.TelegramUserLanguages).SelectionExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_LanguageNumber_ChangesPreferenceAndLocalizesConfirmation()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language"));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 2));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/help", updateId: 3));

        var preference = Assert.Single(fixture.DbContext.TelegramUserLanguages);
        Assert.Equal("ru", preference.Locale);
        Assert.Null(preference.SelectionExpiresAt);
        var replies = fixture.DbContext.TelegramOutboxMessages.ToArray();
        Assert.Contains(replies, reply => reply.Text == "Язык изменён на русский.");
        Assert.Contains(replies, reply => reply.Text.StartsWith("Отправьте текст", StringComparison.Ordinal));
        Assert.Empty(fixture.DbContext.InboxItems);
    }

    [Fact]
    public async Task HandleAsync_InvalidLanguageNumber_RepeatsMenuWithoutCapturingInboxItem()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language"));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("3", updateId: 2));

        Assert.Null(Assert.Single(fixture.DbContext.TelegramUserLanguages).Locale);
        Assert.NotNull(Assert.Single(fixture.DbContext.TelegramUserLanguages).SelectionExpiresAt);
        Assert.Empty(fixture.DbContext.InboxItems);
        Assert.Equal(2, fixture.DbContext.TelegramOutboxMessages.Count());
        Assert.All(fixture.DbContext.TelegramOutboxMessages,
            reply => Assert.StartsWith("Choose a language:", reply.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_TelegramRussianLanguage_UsesRussianUntilPreferenceIsChanged()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/start", languageCode: "ru-RU"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language", updateId: 2, languageCode: "ru-RU"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("1", updateId: 3, languageCode: "ru-RU"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/start", updateId: 4, languageCode: "ru-RU"));

        var replies = fixture.DbContext.TelegramOutboxMessages.ToArray();
        Assert.Contains(replies, reply => reply.Text.StartsWith("Отправьте мысль", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Text.StartsWith("Выберите язык:", StringComparison.Ordinal));
        Assert.Contains(replies, reply => reply.Text == "Language changed to English.");
        Assert.Contains(replies, reply => reply.Text.StartsWith("Send a thought", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_SelectedRussianLanguage_LocalizesTaskAndInboxReplies()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 2));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/add Купить молоко", updateId: 3));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("входящий текст", updateId: 4));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/inbox", updateId: 5));

        var replies = fixture.DbContext.TelegramOutboxMessages.ToArray();
        Assert.Contains(replies, reply => reply.Text == "Задача #1 добавлена: Купить молоко");
        Assert.Contains(replies, reply => reply.Text == "Сохранено во входящие.");
        Assert.Contains(replies, reply => reply.Text.StartsWith("Входящие\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_LanguageSelection_ClosesInboxDraft()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("buy milk"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/inbox 1", updateId: 2));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language", updateId: 3));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 4));

        Assert.Equal("ru", Assert.Single(fixture.DbContext.TelegramUserLanguages).Locale);
        Assert.Empty(fixture.DbContext.TelegramInboxDrafts);
        Assert.Single(fixture.DbContext.InboxItems);
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("1", updateId: 5));
        Assert.Empty(fixture.DbContext.TelegramInboxDrafts);
        Assert.Equal(2, fixture.DbContext.InboxItems.Count());
    }

    [Fact]
    public async Task HandleAsync_CancelLanguageSelection_LeavesNoHiddenInboxDraft()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("buy milk"));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/inbox 1", updateId: 2));
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language", updateId: 3));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/cancel", updateId: 4));

        Assert.Null(Assert.Single(fixture.DbContext.TelegramUserLanguages).SelectionExpiresAt);
        Assert.Empty(fixture.DbContext.TelegramInboxDrafts);
        Assert.Contains(fixture.DbContext.TelegramOutboxMessages,
            reply => reply.Text == "Selection canceled. Your entries are saved.");
    }

    [Fact]
    public async Task HandleAsync_LanguageConfirmationSaveFails_RollsBackPreference()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/language"));
        await fixture.DbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_language_reply BEFORE INSERT ON telegram_outbox_messages
            BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Handler.HandleAsync(CreateTextMessageUpdate("2", updateId: 2)));

        fixture.DbContext.ChangeTracker.Clear();
        var row = Assert.Single(fixture.DbContext.TelegramUserLanguages);
        Assert.Null(row.Locale);
        Assert.NotNull(row.SelectionExpiresAt);
        Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
    }

    private static Update CreateTextMessageUpdate(string text, long chatId = 1, int updateId = 1, string? languageCode = null) =>
        new()
        {
            Id = updateId,
            Message = new Message
            {
                Id = updateId,
                Date = Now,
                Chat = new Chat { Id = chatId, Type = ChatType.Private },
                From = new User { Id = chatId, IsBot = false, FirstName = "Test", LanguageCode = languageCode },
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
            List<string> databaseCommands)
        {
            _connection = connection;
            DbContext = dbContext;
            Handler = handler;
            DatabaseCommands = databaseCommands;
        }

        public OwnDayDbContext DbContext { get; }

        public TelegramUpdateHandler Handler { get; }

        public List<string> DatabaseCommands { get; }

        public static async Task<HandlerFixture> CreateAsync(TimeProvider? clock = null)
        {
            var timeProvider = clock ?? new FixedTimeProvider(Now);
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
            var handler = new TelegramUpdateHandler(
                router,
                new TelegramActionCommandHandler(new ActionService(new EfActionStore(dbContext), timeProvider)),
                new TelegramStructuredCommandHandler(new StructuredItemService(new EfStructuredItemStore(dbContext), timeProvider)),
                new TelegramInboxFlow(
                    new InboxDraftService(new EfInboxDraftStore(dbContext),
                        new InboxCaptureService(new EfInboxItemStore(dbContext), timeProvider),
                        new InboxProcessingService(new EfInboxProcessingStore(dbContext),
                            new EfActionStore(dbContext), new EfStructuredItemStore(dbContext), timeProvider),
                        new StructuredItemService(new EfStructuredItemStore(dbContext), timeProvider),
                        timeProvider),
                    new InboxCaptureService(new EfInboxItemStore(dbContext), timeProvider),
                    new TelegramInboxPresenter(new StructuredItemService(new EfStructuredItemStore(dbContext), timeProvider)),
                    dbContext,
                    new InboxAutomaticProcessingService(new EfInboxProcessingStore(dbContext),
                        new EfInboxAutomaticProcessingStore(dbContext), new(),
                        new InboxProcessingService(new EfInboxProcessingStore(dbContext), new EfActionStore(dbContext),
                            new EfStructuredItemStore(dbContext), timeProvider), timeProvider)),
                new TelegramLanguageFlow(dbContext, timeProvider),
                dbContext,
                timeProvider,
                NullLogger<TelegramUpdateHandler>.Instance);

            databaseCommands.Clear();
            return new HandlerFixture(connection, dbContext, handler, databaseCommands);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        public DateTime UtcNow { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

}
