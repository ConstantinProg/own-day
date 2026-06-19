using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace OwnDay.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddDbContext<OwnDayDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddSingleton<ITelegramBotClient>(_ =>
        {
            var token = configuration["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Telegram bot token is not configured.");
            }

            return new TelegramBotClient(token);
        });

        return services;
    }
}
