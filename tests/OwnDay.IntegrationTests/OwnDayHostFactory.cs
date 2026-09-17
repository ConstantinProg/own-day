using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OwnDay.IntegrationTests;

public sealed class OwnDayHostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(GetHostContentRoot());
        builder.UseSetting(
            "Telegram:BotToken",
            "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
        builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
        builder.UseSetting(
            "Telegram:WebhookSecret",
            "integration-test-webhook-secret");
    }

    private static string GetHostContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "OwnDay.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not locate the OwnDay solution root.");
        }

        return Path.Combine(
            directory.FullName,
            "src",
            "OwnDay.Host");
    }
}
