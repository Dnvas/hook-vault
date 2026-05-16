using System.Net;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class MaxBodySizeTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", "1024");

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
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", null);
    }

    [Fact]
    public async Task Ingest_BodyUnderCap_Returns202()
    {
        var client = _factory.CreateClient();
        var body = new string('x', 512);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/ingest/open", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_BodyOverCap_Returns413()
    {
        var client = _factory.CreateClient();
        var body = new string('x', 2048); // > 1024 cap
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/ingest/open", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
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
