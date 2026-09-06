using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OwnDay.IntegrationTests;

public sealed class OwnDayHostFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindHostContentRoot());
    }

    private static string FindHostContentRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OwnDay.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "OwnDay.Host");
            }
        }

        throw new InvalidOperationException("Could not locate the OwnDay solution root.");
    }
}
