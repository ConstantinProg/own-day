using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.IntegrationTests;

public sealed class OwnDayHostFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public OwnDayHostFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(GetHostContentRoot());
        builder.UseSetting(
            "Telegram:BotToken",
            "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi");
        builder.UseSetting("Telegram:BotUsername", "OwnDayBot");
        builder.UseSetting(
            "ConnectionStrings:Default",
            "Host=localhost;Database=unused;Username=unused;Password=unused");
        builder.UseSetting(
            "Telegram:WebhookSecret",
            "integration-test-webhook-secret");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OwnDayDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OwnDayDbContext>>();
            services.RemoveAll<IHostedService>();
            services.AddDbContext<OwnDayDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OwnDayDbContext>()
            .Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }

        base.Dispose(disposing);
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
