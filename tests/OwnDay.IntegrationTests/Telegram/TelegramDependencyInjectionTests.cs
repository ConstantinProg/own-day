using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram;
using OwnDay.Infrastructure.Telegram.Cleanup;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Handling;
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

    [Fact]
    public void TelegramMessageSender_IsRegistered()
    {
        var sender = _factory.Services.GetRequiredService<ITelegramMessageSender>();

        Assert.IsType<TelegramBotMessageSender>(sender);
    }

    [Fact]
    public void TelegramMessageCleaner_IsRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new TelegramOptions
        {
            BotToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"
        }));
        services.AddTelegram();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<TelegramBotMessageCleaner>(provider.GetRequiredService<ITelegramMessageCleaner>());
    }

    [Fact]
    public void TelegramUpdateHandler_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ITelegramUpdateHandler>();

        Assert.IsType<TelegramUpdateHandler>(handler);
    }
}
