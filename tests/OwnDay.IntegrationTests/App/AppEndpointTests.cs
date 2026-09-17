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

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthLive_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
