using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramDependencyInjectionTests : IClassFixture<OwnDayHostFactory>
{
    private readonly OwnDayHostFactory _factory;

    public TelegramDependencyInjectionTests(OwnDayHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TelegramBotClient_IsRegistered()
    {
        var botClient = _factory.Services.GetRequiredService<ITelegramBotClient>();

        Assert.IsType<TelegramBotClient>(botClient);
    }
}
