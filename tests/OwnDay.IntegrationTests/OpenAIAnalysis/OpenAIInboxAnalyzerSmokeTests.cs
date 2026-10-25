using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.IntegrationTests.OpenAIAnalysis;

public sealed class OpenAIInboxAnalyzerSmokeTests
{
    [OpenAISmokeFact]
    [Trait("Category", "OpenAISmoke")]
    public async Task AnalyzeAsync_RealApi_ReturnsValidInboxStructuredOutput()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "Set OPENAI_API_KEY before explicitly running the smoke test.");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InboxAnalysis:OpenAI:Enabled"] = "true",
            ["InboxAnalysis:OpenAI:ApiKey"] = key
        }).Build());
        services.AddInboxAnalysis();
        await using var provider = services.BuildServiceProvider();
        var analyzer = provider.GetRequiredService<IInboxAnalyzer>();
        Assert.Equal("gpt-6-luna", analyzer.Descriptor.Model);
        Assert.Equal("low", analyzer.Descriptor.ReasoningEffort);
        var request = new InboxAnalysisRequest("Купить молоко", DateTime.UtcNow,
            new(null, [], false), InboxAnalysisService.SupportedTypes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var response = await analyzer.AnalyzeAsync(request, timeout.Token);

        Assert.False(string.IsNullOrWhiteSpace(response.Model));
        Assert.NotNull(new InboxAnalysisValidator().ParseAndValidate(response.Json, request));
        Assert.InRange(response.OutputTokens ?? 0, 1, 8192);
    }
}

public sealed class OpenAISmokeFactAttribute : FactAttribute
{
    public OpenAISmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OWNDAY_RUN_OPENAI_SMOKE") != "1")
        {
            Skip = "Real paid API call; set OWNDAY_RUN_OPENAI_SMOKE=1 and OPENAI_API_KEY to opt in.";
        }
    }
}
