using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Theory]
    [InlineData("learn", "generate")]
    [InlineData("answer", "assess")]
    [InlineData("learn_request", "plan")]
    [InlineData("learn_adapt", "teach")]
    public async Task Process_SharedMonthlyBudgetInsufficient_DoesNotInvokeAnyLearningProvider(string command, string kind)
    {
        var arguments = await SeedAsync(command);
        var probe = new Probe("");
        using var host = Host(probe, worker: true, publicIngress: true).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<OpenAICostOptions>(options =>
            {
                options.TokenLimit = null;
                options.TokensPerRequest = 100_000;
                options.Models["gpt-6-luna"] = new()
                {
                    InputUsdPerMillion = 1000, CachedInputUsdPerMillion = 1000, OutputUsdPerMillion = 1000
                };
            })));
        using var client = host.CreateClient();
        using var response = await PostPublicAsync(client, 1, "/" + command + " " + arguments);
        response.EnsureSuccessStatusCode();
        await WaitAllPublicAsync(1);
        Assert.Equal(0, probe.Calls);
        Assert.False(probe.KindCalls.ContainsKey(kind));
        await using var observer = Open();
        Assert.Empty(await observer.OpenAIRequestAttempts.ToListAsync());
        Assert.Single(await observer.Set<LearningAppliedOperation>().ToListAsync());
        Assert.NotEmpty(await observer.TelegramOutboxMessages.ToListAsync());
    }
}
