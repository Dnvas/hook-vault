using System.Net;
using System.Text;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class MetricsEndpointTests : IAsyncLifetime
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
    public async Task Metrics_ReachableWithoutAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // OpenTelemetry Prometheus exporter returns text/plain with metric lines.
        Assert.Contains("# TYPE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_IncludesEventsTotalAfterIngest()
    {
        var client = _factory.CreateClient();

        using var content = new StringContent("""{"x":1}""", Encoding.UTF8, "application/json");
        var ingest = await client.PostAsync("/api/ingest/open", content);
        ingest.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("hookvault_events_total", body, StringComparison.Ordinal);
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
