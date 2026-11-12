using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Handling;
using OwnDay.Infrastructure.Telegram.Routing;
using Telegram.Bot;

namespace OwnDay.Infrastructure.Telegram;

public static class TelegramServiceCollectionExtensions
{
    public static IServiceCollection AddTelegram(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddOptions<OwnDay.Infrastructure.Learning.LearningRequestProcessingOptions>()
            .BindConfiguration("Learning:Processing")
            .Validate(settings => settings.Parallelism is >= 1 and <= 8 && settings.PollMilliseconds is >= 20 and <= 60000 &&
                settings.LeaseSeconds is >= 1 and <= 300 && settings.MaxAttempts is >= 1 and <= 5 &&
                settings.ProviderTimeoutSeconds is >= 1 and <= 120,
                "Learning processing requires finite concurrency, poll, lease and attempt limits.")
            .ValidateOnStart();
        services.AddScoped<OwnDay.Infrastructure.Persistence.EfLearningRequestStore>();
        services.AddScoped<OwnDay.Application.Learning.Requests.ILearningRequestStore>(provider => provider.GetRequiredService<OwnDay.Infrastructure.Persistence.EfLearningRequestStore>());
        services.AddScoped<OwnDay.Application.Learning.Requests.ILearningRequestRunner, OwnDay.Infrastructure.Learning.LearningRequestRunner>();
        services.AddScoped<OwnDay.Application.Learning.Requests.LearningRequestProcessor>();
        services.AddHostedService<OwnDay.Infrastructure.Learning.LearningRequestWorker>();
        services.AddScoped<TelegramOutboxDeliveryService>();
        services.AddHostedService<TelegramOutboxWorker>();
        services.AddScoped<StudyInvitationDispatcher>();
        services.AddHostedService<StudyInvitationWorker>();
        services.AddScoped<OwnDay.Application.Learning.IStudyScheduleStore, OwnDay.Infrastructure.Persistence.EfStudyScheduleStore>();
        services.AddScoped<OwnDay.Application.Learning.StudyService>();
        services.AddScoped<TelegramStudyCommandHandler>();
        services.AddScoped<TelegramOutboxCleanupService>();
        services.AddHostedService<TelegramOutboxCleanupWorker>();

        services.AddSingleton<ITelegramBotClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<TelegramOptions>>()
                .Value;

            return new TelegramBotClient(options.BotToken!);
        });

        services.AddSingleton<ITelegramMessageSender, TelegramBotMessageSender>();
        services.AddSingleton<ITelegramMessageCleaner, TelegramBotMessageCleaner>();
        services.AddSingleton<ITelegramCallbackResponder, TelegramCallbackResponder>();

        services.AddSingleton<TelegramCommandParser>();
        services.AddScoped<TelegramStatisticsCommandHandler>();
        services.AddScoped<TelegramActionCommandHandler>();
        services.AddScoped<TelegramStructuredCommandHandler>();
        services.AddScoped<TelegramInboxFlow>();
        services.AddScoped<TelegramInboxPresenter>();
        services.AddScoped<TelegramLanguageFlow>();
        services.AddSingleton<TelegramUpdateRouter>();

        services.AddScoped<OwnDay.Application.Learning.ILessonStore, OwnDay.Infrastructure.Persistence.EfLessonStore>();
        services.AddSingleton(provider => new OwnDay.Infrastructure.OpenAIAnalysis.RfcLessonSourceReader(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<OwnDay.Application.Learning.ILessonTeacher, OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher>();
        services.AddScoped<OwnDay.Application.Learning.LearningService>();
        services.AddScoped<OwnDay.Application.Learning.IKnowledgeStore, OwnDay.Infrastructure.Persistence.EfKnowledgeStore>();
        services.AddScoped<OwnDay.Application.Learning.KnowledgeService>();
        services.AddScoped<TelegramKnowledgeCommandHandler>();
        services.AddScoped<OwnDay.Application.Learning.ICurriculumStore, OwnDay.Infrastructure.Persistence.EfCurriculumStore>();
        services.AddScoped<OwnDay.Application.Learning.ICurriculumPlanner, OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher>();
        services.AddScoped<OwnDay.Application.Learning.ITeachingPlanner, OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher>();
        services.AddScoped<OwnDay.Application.Learning.CurriculumService>();
        services.AddScoped<TelegramCurriculumCommandHandler>();
        services.AddScoped<TelegramLearningCommandHandler>();
        services.AddScoped<TelegramUpdateHandler>();
        services.AddScoped<ITelegramUpdateHandler>(
            serviceProvider =>
                serviceProvider.GetRequiredService<TelegramUpdateHandler>());

        return services;
    }
}
