using System.Net;
using System.Text;
using HookVault.Configuration;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class CaptureOnlyTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private CountingHandler _forwardHandler = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();
        _forwardHandler = new CountingHandler();

        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "stripe",
                        Path = "/stripe",
                        ForwardUrl = "http://localhost/stripe",
                        Validation = null,
                        CaptureOnly = true,
                    },
                ]
            });
            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => _forwardHandler);
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
    public async Task Ingest_CaptureOnly_PersistsAsCapturedAndSkipsForward()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent("""{"id":"evt_1"}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/ingest/stripe", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();

        Assert.Equal(EventStatus.Captured, stored.Status);
        Assert.Equal(0, _forwardHandler.CallCount);
        Assert.Null(stored.ForwardedAt);
    }

    [Fact]
    public void Load_CaptureOnlyProvider_WithEmptyForwardUrl_Succeeds()
    {
        var configPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, """
                {
                  "providers": [
                    { "name": "stripe", "path": "/stripe", "captureOnly": true }
                  ]
                }
                """);
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", configPath);

            var logger = LoggerFactory.Create(b => { }).CreateLogger("test");
            var options = HookVaultOptions.Load(logger);

            Assert.Single(options.Providers);
            Assert.True(options.Providers[0].CaptureOnly);
            Assert.Equal(string.Empty, options.Providers[0].ForwardUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
            File.Delete(configPath);
        }
    }

    [Fact]
    public void Load_NonCaptureOnlyProvider_WithEmptyForwardUrl_Throws()
    {
        var configPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, """
                {
                  "providers": [
                    { "name": "stripe", "path": "/stripe" }
                  ]
                }
                """);
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", configPath);

            var logger = LoggerFactory.Create(b => { }).CreateLogger("test");
            var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(logger));
            Assert.Contains("forwardUrl", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
            File.Delete(configPath);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount;
        private HttpResponseMessage? _response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref CallCount);
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
