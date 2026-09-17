using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
        services.AddScoped<TelegramOutboxDeliveryService>();
        services.AddHostedService<TelegramOutboxWorker>();
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

        services.AddSingleton<TelegramCommandParser>();
        services.AddSingleton<TelegramUpdateRouter>();

        services.AddScoped<TelegramUpdateHandler>();
        services.AddScoped<ITelegramUpdateHandler>(
            serviceProvider =>
                serviceProvider.GetRequiredService<TelegramUpdateHandler>());

        return services;
    }
}
