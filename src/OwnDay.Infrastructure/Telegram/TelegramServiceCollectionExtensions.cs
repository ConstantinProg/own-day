using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OwnDay.Application.TesterMode;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Actions;
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
        services.AddOptions<TesterModeOptions>()
            .BindConfiguration(TesterModeOptions.SectionName)
            .Validate(settings => !settings.Enabled || settings.HasValidSecretHash(),
                "Enabled Tester Mode requires a SHA-256 secret hash encoded as 64 hexadecimal digits.")
            .Validate(settings => settings.ConfirmationTimeoutSeconds is >= 10 and <= 600,
                "Tester Mode confirmation timeout must be between 10 and 600 seconds.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TesterModeOptions>>().Value);
        services.AddScoped<ITesterModeStore, EfTesterModeStore>();
        services.AddScoped<TesterModeService>();
        services.AddOptions<OwnDay.Infrastructure.Learning.LearningRequestProcessingOptions>()
            .BindConfiguration("Learning:Processing")
            .Validate(settings => settings.DialogueEnabled && settings.ConsultationEnabled,
                "Telegram supports only learning dialogue with first-goal consultation. Remove legacy disabled-mode overrides.")
            .Validate(settings => settings.Parallelism is >= 1 and <= 8 && settings.PollMilliseconds is >= 20 and <= 60000 &&
                settings.LeaseSeconds is >= 1 and <= 300 && settings.MaxAttempts is >= 1 and <= 5 &&
                settings.ProviderTimeoutSeconds is >= 1 and <= 120 && settings.MaxProviderAttempts is >= 1 and <= 3,
                "Learning processing requires finite concurrency, poll, lease and attempt limits.")
            .Validate(settings => !settings.DurableIngressEnabled || settings.Enabled,
                "Durable Learning ingress requires the installed worker to be enabled.")
            .Validate(settings => !settings.DialogueEnabled || settings.DurableIngressEnabled && settings.Enabled,
                "Learning dialogue requires durable ingress and the compatible worker.")
            .Validate(settings => !settings.ConsultationEnabled || settings.DialogueEnabled,
                "First-goal consultation requires learning dialogue.")
            .Validate(settings => !settings.EnglishDiagnosticEnabled || settings.ConsultationEnabled,
                "English diagnostic enrollment requires first-goal consultation.")
            .Validate(settings => !settings.PracticeEnabled || settings.DialogueEnabled,
                "Bounded practice requires the learning dialogue and compatible worker.")
            .Validate(settings => !settings.ProgramSwitchEnabled || settings.DialogueEnabled,
                "Confirmed program switching requires the learning dialogue and compatible worker.")
            .Validate(settings => settings.MaxQueueAgeSeconds is >= 30 and <= 86400,
                "Learning queue age must be between 30 seconds and one day.")
            .ValidateOnStart();
        services.AddScoped<OwnDay.Infrastructure.Persistence.EfLearningRequestStore>();
        services.AddScoped<OwnDay.Application.Learning.Requests.ILearningRequestStore>(provider => provider.GetRequiredService<OwnDay.Infrastructure.Persistence.EfLearningRequestStore>());
        services.AddScoped<OwnDay.Application.Learning.Requests.ILearningRequestRunner, OwnDay.Infrastructure.Learning.LearningRequestRunner>();
        services.AddSingleton<OwnDay.Infrastructure.Learning.LearningProviderCallGate>();
        services.AddSingleton<OwnDay.Infrastructure.Learning.LearningProviderExecutor>();
        services.AddScoped<OwnDay.Infrastructure.Learning.LearningProviderUsage>();
        services.AddScoped<OwnDay.Application.Dialogue.IDialogueStore, OwnDay.Infrastructure.Persistence.EfDialogueStore>();
        services.AddScoped<OwnDay.Application.Dialogue.ILearningSessionStore, OwnDay.Infrastructure.Persistence.EfLearningSessionStore>();
        services.AddScoped<OwnDay.Application.Dialogue.LearningSessionService>();
        services.AddScoped<OwnDay.Application.Learning.IAssessmentReviewStore, OwnDay.Infrastructure.Persistence.EfAssessmentReviewStore>();
        services.AddScoped<OwnDay.Application.Dialogue.ILearningIntentResolver, OwnDay.Infrastructure.OpenAIAnalysis.OpenAILessonTeacher>();
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
        services.AddScoped<TelegramChangesCommandHandler>();
        services.AddSingleton<TelegramActionCodec>();
        services.AddSingleton<TelegramActionKeyboard>();
        services.AddScoped<TelegramActionCommandHandler>();
        services.AddScoped<TelegramStructuredCommandHandler>();
        services.AddScoped<TelegramLanguageFlow>();
        services.AddSingleton<TelegramUpdateRouter>();

        services.AddScoped<OwnDay.Application.Learning.ILessonStore, OwnDay.Infrastructure.Persistence.EfLessonStore>();
        services.AddSingleton(provider => new OwnDay.Infrastructure.OpenAIAnalysis.RfcLessonSourceReader(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new OwnDay.Infrastructure.OpenAIAnalysis.MoexLessonSourceReader(
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
