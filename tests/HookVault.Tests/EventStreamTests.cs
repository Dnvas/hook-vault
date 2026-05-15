using System.Net;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class EventStreamTests : IAsyncLifetime
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
                        Name = "test",
                        Path = "/test",
                        ForwardUrl = "http://localhost/test",
                        Validation = null,
                    }
                ]
            });

            s.AddHttpClient("forwarder")
             .ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
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
    public async Task Ingest_NotifiesEventNotifier_WithCorrectProvider()
    {
        // Read from the singleton EventNotifier directly — tests that IngestController
        // wires up to EventNotifier without the complexity of SSE streaming in TestServer.
        var notifier = _factory.Services.GetRequiredService<EventNotifier>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var notificationTask = Task.Run(async () =>
        {
            await foreach (var n in notifier.Reader.ReadAllAsync(cts.Token))
                return n;
            return null;
        }, cts.Token);

        await Task.Delay(100, cts.Token);

        var client = _factory.CreateClient();
        using var ingest = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/test")
        {
            Content = new StringContent("""{"type":"test"}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        var response = await client.SendAsync(ingest, cts.Token);
        response.EnsureSuccessStatusCode();

        var notification = await notificationTask;

        Assert.NotNull(notification);
        Assert.Equal("test", notification.Provider, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        private HttpResponseMessage? _response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
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
