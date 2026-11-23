using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain.Dialogue;
using OwnDay.Infrastructure;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
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
    [InlineData("/start", "en")]
    [InlineData("/ping", "en")]
    [InlineData("/unknown", "en")]
    [InlineData("/start", "ru")]
    [InlineData("/ping", "ru")]
    [InlineData("/unknown", "ru")]
    [InlineData("/help", "en")]
    [InlineData("/add Buy groceries", "en")]
    public async Task HandleAsync_LegacyCommand_IntroducesDialogueWithoutLegacyEffects(string command, string locale)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate(command, languageCode: locale));

        var replies = await fixture.DbContext.TelegramOutboxMessages.OrderBy(reply => reply.CreatedAt).ToListAsync();
        Assert.Equal(2, replies.Count);
        Assert.Equal(locale == "ru" ? TelegramDialoguePresenter.Introduction : TelegramDialoguePresenter.EnglishIntroduction, replies[0].Text);
        Assert.Contains(locale == "ru" ? "Чему вы хотите научиться" : "What would you like to learn", replies[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.All(replies, reply =>
        {
            Assert.Equal(1, reply.ChatId);
            Assert.Equal(OutboxMessageStatus.Pending, reply.Status);
            Assert.Null(reply.ReplyMarkupJson);
        });
        Assert.Equal(Now, replies[0].CreatedAt);
        Assert.True(replies[1].CreatedAt > replies[0].CreatedAt);
        var receipt = Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Equal(Now, receipt.ReceivedAt);
        Assert.Equal(Now, receipt.ProcessedAt);
        Assert.Single(fixture.DbContext.Set<DialogueContext>());
        Assert.Empty(fixture.DbContext.InboxItems);
        Assert.Empty(fixture.DbContext.Actions);
        Assert.Empty(fixture.DbContext.Set<LearningRequest>());
    }

    [Theory]
    [InlineData("en", "/changes — show changes in this version.")]
    [InlineData("ru", "/changes — изменения в этой версии.")]
    public async Task HandleAsync_HelpAfterStart_ListsReleaseCommandsWithoutRepeatingIntroduction(string locale, string description)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/start"));

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/help", languageCode: locale, updateId: 2));

        var replies = await fixture.DbContext.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(3, replies.Count);
        Assert.Single(replies, reply => reply.Text == TelegramDialoguePresenter.EnglishIntroduction);
        Assert.Single(replies, reply => reply.Text.Contains("/settings", StringComparison.Ordinal) && reply.Text.Contains("/language", StringComparison.Ordinal));
        Assert.Single(replies, reply => reply.Text.Contains("/version", StringComparison.Ordinal));
        Assert.Single(replies, reply => reply.Text.Contains(description, StringComparison.Ordinal));
        Assert.Single(fixture.DbContext.Set<DialogueContext>());
    }

    [Theory]
    [InlineData("en-US", "No release notes are available", "Full changelog")]
    [InlineData("ru", "нет записи", "Полная история изменений")]
    public async Task HandleAsync_ChangesWithNoReleaseNotes_QueuesReplyWithoutChangingDialogue(string locale, string missingText, string historyText)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        await fixture.Handler.HandleAsync(CreateTextMessageUpdate("/changes", languageCode: locale));

        Assert.Empty(fixture.DbContext.Set<DialogueContext>());
        Assert.Empty(fixture.DbContext.Set<LearningRequest>());
        var reply = Assert.Single(fixture.DbContext.TelegramOutboxMessages);
        Assert.Equal(1, reply.ChatId);
        Assert.StartsWith("OwnDay 0.0.0-dev", reply.Text);
        Assert.Contains(missingText, reply.Text);
        Assert.Contains(historyText, reply.Text);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Fact]
    public async Task HandleAsync_DuplicateCommand_QueuesIntroductionAndPromptOnce()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var update = CreateTextMessageUpdate("/ping");

        await fixture.Handler.HandleAsync(update);
        await fixture.Handler.HandleAsync(update);

        Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Equal(2, await fixture.DbContext.TelegramOutboxMessages.CountAsync());
        Assert.Single(fixture.Cleaner.Deletions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_DeletionFails_KeepsCommittedDialogueAndReplies(bool cancelled)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        fixture.Cleaner.Failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("Deletion failed");
        var update = CreateTextMessageUpdate("/start");

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(update));
        }
        else
        {
            await fixture.Handler.HandleAsync(update);
        }
        await fixture.Handler.HandleAsync(update);

        fixture.DbContext.ChangeTracker.Clear();
        Assert.Single(fixture.Cleaner.Deletions);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.Equal(2, await fixture.DbContext.TelegramOutboxMessages.CountAsync());
        Assert.Single(fixture.DbContext.Set<DialogueContext>());
    }

    [Theory]
    [InlineData("/start", 2)]
    [InlineData("/changes", 1)]
    public async Task HandleAsync_OutboxSaveFails_RollsBackUpdateAndAllowsRedelivery(string command, int replyCount)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.DbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER reject_outbox BEFORE INSERT ON telegram_outbox_messages
            BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
            """);

        var update = CreateTextMessageUpdate(command);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Handler.HandleAsync(update));

        fixture.DbContext.ChangeTracker.Clear();
        Assert.Empty(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Empty(fixture.DbContext.TelegramOutboxMessages);
        Assert.Empty(fixture.DbContext.Set<DialogueContext>());
        Assert.Empty(fixture.Cleaner.Deletions);
        await fixture.DbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_outbox");
        await fixture.Handler.HandleAsync(update);
        Assert.Single(fixture.DbContext.ProcessedTelegramUpdates);
        Assert.Equal(replyCount, await fixture.DbContext.TelegramOutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("buy milk")]
    [InlineData("2")]
    public async Task HandleAsync_OrdinaryText_QueuesDurableDialogueRequestAndClosesLegacyEditors(string text)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        fixture.DbContext.TelegramUserLanguages.Add(new TelegramUserLanguage
        {
            UserId = 1, Locale = "en", SelectionExpiresAt = Now.AddMinutes(10)
        });
        fixture.DbContext.TelegramInboxDrafts.Add(new InboxDraft
        {
            UserId = 1, InboxItemId = 999, ExpiresAt = Now.AddMinutes(10)
        });
        await fixture.DbContext.SaveChangesAsync();
        var update = CreateTextMessageUpdate(text);

        await fixture.Handler.HandleAsync(update);
        await fixture.Handler.HandleAsync(update);

        var request = Assert.Single(fixture.DbContext.Set<LearningRequest>());
        Assert.Equal("learning_text", request.PayloadKind);
        Assert.Equal(LearningRequestStatus.Ready, request.Status);
        Assert.Contains(text, request.NormalizedInput, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.Empty(fixture.DbContext.InboxItems);
        Assert.Empty(fixture.DbContext.TelegramOutboxMessages);
        Assert.Empty(fixture.DbContext.TelegramInboxDrafts);
        Assert.Null(Assert.Single(fixture.DbContext.TelegramUserLanguages).SelectionExpiresAt);
        Assert.Empty(fixture.Cleaner.Deletions);
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
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Theory]
    [InlineData("/ping")]
    [InlineData("/ping@OwnDayBot")]
    [InlineData("/add Buy groceries")]
    [InlineData("/capture Buy groceries")]
    [InlineData("/done invalid")]
    [InlineData("/unknown")]
    public async Task HandleAsync_ProcessedCommand_DeletesSourceMessage(string command)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();

        await fixture.Handler.HandleAsync(
            CreateTextMessageUpdate(command, chatId: 987, updateId: 42), cancellation.Token);

        var deletion = Assert.Single(fixture.Cleaner.Deletions);
        Assert.Equal(987, deletion.ChatId);
        Assert.Equal(42, deletion.MessageId);
        Assert.Equal(cancellation.Token, deletion.CancellationToken);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        Assert.NotEmpty(fixture.DbContext.TelegramOutboxMessages);
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
    public async Task HandleAsync_BotMessage_DoesNotProcessOrClean()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var update = CreateTextMessageUpdate("buy milk");
        update.Message!.From!.IsBot = true;

        await fixture.Handler.HandleAsync(update);

        Assert.Empty(fixture.DatabaseCommands);
        Assert.Empty(fixture.Cleaner.Deletions);
    }

    [Theory]
    [InlineData("nav:list", null, null)]
    [InlineData("nav:help", "ru-RU", "/changes — изменения в этой версии.")]
    [InlineData("nav:help", "en", "/changes — show changes in this version.")]
    public async Task HandleAsync_NavigationCallback_QueuesHelpAndPreservesBotMessage(
        string action, string? locale, string? releaseDescription)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var source = CreateTextMessageUpdate("Menu", languageCode: locale).Message!;
        var sender = source.From!;
        source.From = new User { Id = 123, IsBot = true, FirstName = "OwnDay" };
        var update = new Update
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "navigation-callback",
                From = sender,
                Message = source,
                Data = action
            }
        };

        await fixture.Handler.HandleAsync(update);

        Assert.Empty(fixture.Cleaner.Deletions);
        Assert.NotNull(Assert.Single(fixture.DbContext.ProcessedTelegramUpdates).ProcessedAt);
        var replies = await fixture.DbContext.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(2, replies.Count);
        if (releaseDescription is not null)
        {
            Assert.Single(replies, reply => reply.Text.Contains(releaseDescription, StringComparison.Ordinal));
        }
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
            RecordingTelegramMessageCleaner cleaner,
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

        public RecordingTelegramMessageCleaner Cleaner { get; }

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

            var options = Options.Create(new TelegramOptions
            {
                BotUsername = "OwnDayBot",
                BotToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
                CallbackSigningKey = Convert.ToBase64String(new byte[32])
            });
            var codec = new TelegramActionCodec(options);
            var router = new TelegramUpdateRouter(new TelegramCommandParser(), options, codec);
            var cleaner = new RecordingTelegramMessageCleaner();
            var teacher = new OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher(
                () => throw new InvalidOperationException("Provider is disabled."),
                Options.Create(new OwnDay.Infrastructure.OpenAIAnalysis.OpenAIInboxAnalysisOptions()),
                NullLogger<OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher>.Instance);
            var knowledge = new OwnDay.Application.Learning.KnowledgeService(new EfKnowledgeStore(dbContext), timeProvider);
            var learning = new OwnDay.Application.Learning.LearningService(new EfLessonStore(dbContext), teacher, knowledge);
            var curriculum = new OwnDay.Application.Learning.CurriculumService(new EfCurriculumStore(dbContext), teacher,
                new EfLessonStore(dbContext), teacher,
                new StructuredItemService(new EfStructuredItemStore(dbContext), timeProvider), knowledge, teacher, new EfActionStore(dbContext));
            var languages = new EfUserLanguageStore(dbContext);
            var handler = new TelegramUpdateHandler(
                router,
                new EfInboxDraftStore(dbContext),
                new TelegramLanguageFlow(languages, new OwnDay.Application.Settings.UserLanguageSettings(languages), timeProvider, codec),
                dbContext,
                timeProvider,
                cleaner,
                NullLogger<TelegramUpdateHandler>.Instance,
                learning,
                new TelegramCurriculumCommandHandler(curriculum),
                new TelegramKnowledgeCommandHandler(knowledge, learning),
                new TelegramStudyCommandHandler(new OwnDay.Application.Learning.StudyService(
                    new EfStudyScheduleStore(dbContext), new EfLessonStore(dbContext), new EfCurriculumStore(dbContext),
                    new EfKnowledgeStore(dbContext), learning, curriculum, timeProvider)),
                ApplicationVersion.FromAssembly(typeof(Program).Assembly),
                new TelegramChangesCommandHandler(new ApplicationVersion("0.0.0-dev", null)));

            databaseCommands.Clear();
            return new HandlerFixture(connection, dbContext, handler, cleaner, databaseCommands);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingTelegramMessageCleaner : ITelegramMessageCleaner
    {
        public List<(long ChatId, int MessageId, CancellationToken CancellationToken)> Deletions { get; } = [];

        public Exception? Failure { get; set; }

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            Deletions.Add((chatId, messageId, cancellationToken));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }
    }

}
