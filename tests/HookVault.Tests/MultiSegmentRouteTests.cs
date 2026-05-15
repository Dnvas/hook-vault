using System.Net;
using System.Net.Http.Json;
using HookVault.Configuration;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class MultiSegmentRouteTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
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
                        Name = "stripe-v2",
                        Path = "/stripe/v2",
                        ForwardUrl = "http://localhost/forward",
                        Validation = null,
                    }
                ]
            });

            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    [Fact]
    public async Task Ingest_MatchesMultiSegmentProviderPath()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ingest/stripe/v2", new { type = "evt" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_ReturnsNotFoundForUnconfiguredMultiSegmentPath()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ingest/stripe/v9", new { type = "evt" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
