using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.OpenAIAnalysis;

public static class InboxAnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddInboxAnalysis(this IServiceCollection services)
    {
        services.AddOptions<OpenAIInboxAnalysisOptions>()
            .BindConfiguration(OpenAIInboxAnalysisOptions.SectionName)
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "InboxAnalysis:OpenAI:ApiKey is required when enabled.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model) && options.Model.Length <= 200,
                "InboxAnalysis:OpenAI:Model is required (at most 200 characters).")
            .Validate(options => options.MaxOutputTokens is >= 256 and <= 16_384,
                "InboxAnalysis:OpenAI:MaxOutputTokens must be between 256 and 16384.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 300,
                "InboxAnalysis:OpenAI:TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<InboxAnalysisService>();
        services.AddSingleton<InboxAnalysisValidator>();
        services.AddScoped<IInboxAnalysisStore, EfInboxAnalysisStore>();
        services.AddSingleton<ChatClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<OpenAIInboxAnalysisOptions>>().Value;
            if (!options.Enabled)
            {
                throw new InvalidOperationException("OpenAI Inbox analysis is disabled. Enable it in configuration before invoking analysis.");
            }

            return new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey!), new OpenAIClientOptions
            {
                NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                RetryPolicy = new ClientRetryPolicy(0)
            });
        });
        services.AddSingleton<IInboxAnalyzer, OpenAIInboxAnalyzer>();
        return services;
    }
}
