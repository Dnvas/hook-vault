using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Configuration;
using HookVault.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
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

    [Fact]
    public async Task Ingest_unknown_provider_returns_ApiError_shape()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await _factory.CreateClient().PostAsync("/api/ingest/unknown-provider", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp),
            "Response body should contain an `error` property (ApiError shape).");
        Assert.False(string.IsNullOrEmpty(errorProp.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("code", out var codeProp),
            "Response body should contain a `code` property (ApiError shape).");
        Assert.Equal("provider_not_found", codeProp.GetString());
    }
}

[Collection("EnvVarMutation")]
public sealed class BodyTooLargeApiErrorShapeTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", "256");

        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "open",
                        Path = "/open",
                        ForwardUrl = "http://localhost/open",
                        Validation = null,
                    }
                ]
            });
        }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", null);
    }

    [Fact]
    public async Task Body_over_cap_returns_ApiError_shape()
    {
        var client = _factory.CreateClient();
        var body = new string('x', 1024); // > 256 cap
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/ingest/open", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp),
            "Response body should contain an `error` property (ApiError shape).");
        Assert.False(string.IsNullOrEmpty(errorProp.GetString()));
        Assert.True(doc.RootElement.TryGetProperty("code", out var codeProp),
            "Response body should contain a `code` property (ApiError shape).");
        Assert.Equal("body_too_large", codeProp.GetString());
    }
}
