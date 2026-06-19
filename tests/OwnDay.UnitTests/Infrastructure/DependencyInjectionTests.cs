using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Infrastructure;
using Xunit;

namespace OwnDay.UnitTests.Infrastructure;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_Throws_WhenConnectionStringIsMissing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration));

        Assert.Contains("Connection string 'Default' is not configured", exception.Message);
    }

    [Fact]
    public void AddInfrastructure_RegistersServices_WhenRequiredConfigurationExists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Port=5432;Database=ownday;Username=ownday;Password=ownday",
                ["Telegram:BotToken"] = "000000:test-token"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(OwnDayDbContext));
    }
}
