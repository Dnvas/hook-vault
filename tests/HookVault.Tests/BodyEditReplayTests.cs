using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HookVault.Configuration;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class BodyEditReplayTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private CapturingHandler _forwardHandler = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();
        _forwardHandler = new CapturingHandler();

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
                    },
                ]
            });
            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => _forwardHandler);
            // Re-add the replay worker (HookVaultWebApplicationFactory removes hosted services).
            s.AddHostedService<ReplayWorker>();
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

    private HttpClient AuthedClient()
    {
        var token = _baseFactory.GenerateToken();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Replay_WithBodyOverride_ForwardsOverrideNotStoredBody()
    {
        Guid eventId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
            var evt = new WebhookEvent
            {
                Provider = "stripe",
                Path = "/api/ingest/stripe",
                Headers = "{}",
                Body = Encoding.UTF8.GetBytes("""{"original":true}"""),
                ForwardUrl = "http://localhost/stripe",
                Status = EventStatus.Forwarded,
            };
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            eventId = evt.Id;
        }

        var response = await AuthedClient().PostAsJsonAsync($"/api/events/{eventId}/replay",
            new { body = """{"edited":true}""" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Wait for ReplayWorker to drain the queue (up to 3s).
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_forwardHandler.LastBodyBytes is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.NotNull(_forwardHandler.LastBodyBytes);
        Assert.Equal("""{"edited":true}""", Encoding.UTF8.GetString(_forwardHandler.LastBodyBytes!));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
            var stored = await db.Events.SingleAsync();
            Assert.Equal("""{"original":true}""", Encoding.UTF8.GetString(stored.Body));
            Assert.True(stored.LastReplayWithEditedBody);
        }
    }

    [Fact]
    public async Task Replay_WithoutBody_ForwardsStoredBody()
    {
        Guid eventId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
            var evt = new WebhookEvent
            {
                Provider = "stripe",
                Path = "/api/ingest/stripe",
                Headers = "{}",
                Body = Encoding.UTF8.GetBytes("""{"original":true}"""),
                ForwardUrl = "http://localhost/stripe",
                Status = EventStatus.Forwarded,
            };
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            eventId = evt.Id;
        }

        // No body: existing replay button path.
        var response = await AuthedClient().PostAsync($"/api/events/{eventId}/replay", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_forwardHandler.LastBodyBytes is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.NotNull(_forwardHandler.LastBodyBytes);
        Assert.Equal("""{"original":true}""", Encoding.UTF8.GetString(_forwardHandler.LastBodyBytes!));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
            var stored = await db.Events.SingleAsync();
            Assert.False(stored.LastReplayWithEditedBody);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public byte[]? LastBodyBytes;
        private HttpResponseMessage? _response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastBodyBytes = req.Content is null ? [] : await req.Content.ReadAsByteArrayAsync(ct);
            _response = new HttpResponseMessage(HttpStatusCode.OK);
            return _response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response?.Dispose();
            base.Dispose(disposing);
        }
    }
}
