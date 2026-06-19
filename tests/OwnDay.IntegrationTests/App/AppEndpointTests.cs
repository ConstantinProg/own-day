using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OwnDay.IntegrationTests.App;

public sealed class AppEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AppEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Ready_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_ReturnsServiceName()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<VersionResponse>("/version");

        Assert.NotNull(response);
        Assert.Equal("OwnDay.App", response.Service);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
    }

    private sealed record VersionResponse(string Service, string Version);
}
