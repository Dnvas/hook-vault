using System.Net;
using System.Text.Json;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class HeaderRedactionTests : IAsyncLifetime
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
                        Name = "open",
                        Path = "/open",
                        ForwardUrl = "http://localhost/open",
                        Validation = null,
                    }
                ]
            });
            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    [Fact]
    public async Task Ingest_StoresSensitiveHeadersAsRedacted()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/open")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        // Set sensitive headers at the request level so TestServer includes them in
        // Request.Headers on the server — content.Headers only covers Content-* headers.
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret-token");
        request.Headers.TryAddWithoutValidation("Cookie", "session=abc123");
        request.Headers.TryAddWithoutValidation("X-Provider-Event-Id", "evt_keep_me");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();

        var headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stored.Headers)!;
        Assert.Equal(new[] { "[redacted]" }, headers["Authorization"]);
        Assert.Equal(new[] { "[redacted]" }, headers["Cookie"]);
        Assert.Equal(new[] { "evt_keep_me" }, headers["X-Provider-Event-Id"]);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        private HttpResponseMessage? _response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            _response = new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response?.Dispose();
            base.Dispose(disposing);
        }
    }
}
