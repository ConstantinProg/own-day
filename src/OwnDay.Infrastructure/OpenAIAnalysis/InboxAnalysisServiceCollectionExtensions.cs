using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public static class InboxAnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddInboxAnalysis(this IServiceCollection services)
    {
        services.AddOptions<OpenAIInboxAnalysisOptions>()
            .BindConfiguration(OpenAIInboxAnalysisOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model) && options.Model.Length <= 200,
                "InboxAnalysis:OpenAI:Model is required (at most 200 characters).")
            .Validate(options => options.MaxOutputTokens is >= 256 and <= 16_384,
                "InboxAnalysis:OpenAI:MaxOutputTokens must be between 256 and 16384.")
            .Validate(options => options.ReasoningEffort is "none" or "low" or "medium" or "high" or "xhigh" or "max",
                "InboxAnalysis:OpenAI:ReasoningEffort must be none, low, medium, high, xhigh or max.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 300,
                "InboxAnalysis:OpenAI:TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();

        services.AddOptions<InboxAnalysisProcessingOptions>()
            .BindConfiguration(InboxAnalysisProcessingOptions.SectionName)
            .Validate(options => options.Parallelism is >= 1 and <= 8 && options.PollSeconds is >= 1 and <= 300 &&
                options.TimeoutSeconds is >= 1 and <= 300 && options.LeaseSeconds >= options.TimeoutSeconds + 30 &&
                options.LeaseSeconds <= 3600 && options.MaxAttempts is >= 1 and <= 10 &&
                options.InitialBackoffSeconds is >= 1 and <= 3600 &&
                options.MaxBackoffSeconds >= options.InitialBackoffSeconds && options.MaxBackoffSeconds <= 86400,
                "Invalid InboxAnalysis:Background limits; lease must exceed timeout by at least 30 seconds.")
            .ValidateOnStart();

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddOptions<OpenAICostOptions>()
            .BindConfiguration(OpenAICostOptions.SectionName)
            .Validate(o => o.TokenLimit is null or > 0 && o.TokensPerRequest is > 0 and <= 10_000_000,
                "CostControl token limit and reservation must be positive.")
            .Validate(o => o.Models.All(p => !string.IsNullOrWhiteSpace(p.Key) &&
                p.Value.InputUsdPerMillion is null or >= 0 && p.Value.CachedInputUsdPerMillion is null or >= 0 &&
                p.Value.OutputUsdPerMillion is null or >= 0), "Model prices must be nonnegative USD per million tokens.")
            .ValidateOnStart();
        services.AddScoped<IInboxTelemetry, EfInboxTelemetry>();
        services.AddScoped<IInboxStatisticsStore, EfInboxStatisticsStore>();
        services.AddScoped<InboxStatisticsService>();
        services.AddSingleton<IOpenAIRequestLedger, EfOpenAIRequestLedger>();
        services.AddSingleton<InboxBackgroundDiagnostics>();
        services.AddScoped<InboxAnalysisService>();
        services.AddSingleton<InboxAnalysisValidator>();
        services.AddScoped<IInboxAnalysisStore, EfInboxAnalysisStore>();
        services.AddScoped<IInboxAnalysisJobStore, EfInboxAnalysisJobStore>();
        services.TryAddSingleton<InboxPolicyEngine>();
        services.AddScoped<InboxAutomaticProcessingService>();
        services.AddScoped<IInboxAutomaticProcessingStore, EfInboxAutomaticProcessingStore>();
        services.AddHostedService<InboxAnalysisWorker>();
        services.AddSingleton<ChatClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<OpenAIInboxAnalysisOptions>>().Value;
            if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
            {
                throw new InvalidOperationException("OpenAI Inbox analysis is disabled. Enable it in configuration before invoking analysis.");
            }

            return new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey!), new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                RetryPolicy = new ClientRetryPolicy(0)
            });
        });
        services.AddSingleton<OpenAIInboxAnalyzer>();
        services.AddScoped<IInboxAnalyzer>(provider => new CostControlledInboxAnalyzer(
            provider.GetRequiredService<OpenAIInboxAnalyzer>(), provider.GetRequiredService<IOpenAIRequestLedger>()));
        return services;
    }
}
