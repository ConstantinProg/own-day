using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace OwnDay.IntegrationTests.App;

public sealed class AppEndpointTests : IClassFixture<OwnDayHostFactory>
{
    private readonly OwnDayHostFactory _factory;

    public AppEndpointTests(OwnDayHostFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("analyze-inbox-user")]
    [InlineData("analyze-inbox-item")]
    public void Startup_RetiredInboxOperatorArgument_RejectsInvocation(string argument)
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting(argument, "101"));

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Equal("The Inbox analysis operator command has been retired.", error.Message);
    }

    [Fact]
    public async Task Health_SqliteWithLearningEnabled_ReturnsServiceUnavailable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_SqliteWithLearningEnabled_ReturnsServiceUnavailable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Version_ReturnsServiceNameAndVersion()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<VersionResponse>("/version");

        Assert.NotNull(response);
        Assert.Equal("OwnDay.Host", response.Service);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
    }

    private sealed record VersionResponse(string Service, string Version);
}
