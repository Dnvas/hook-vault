using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Contracts;

namespace HookVault.Tests;

public sealed class ApiErrorShapeTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HookVaultWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpClient AuthedClient()
    {
        var options = new JwtOptions(
            HookVaultWebApplicationFactory.TestSecret,
            HookVaultWebApplicationFactory.TestIssuer,
            HookVaultWebApplicationFactory.TestAudience);
        var token = JwtTokenGenerator.Mint(options, "test", TimeSpan.FromMinutes(5));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Model_binding_failure_returns_ApiError_shape()
    {
        var response = await AuthedClient().GetAsync("/api/events?limit=notanumber");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Response body should contain an `error` property (ApiError shape).");
        Assert.False(doc.RootElement.TryGetProperty("errors", out _),
            "Response body should not contain `errors` (default ValidationProblemDetails shape).");
    }
}
