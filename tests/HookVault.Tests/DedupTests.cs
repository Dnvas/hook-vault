using System.Net;
using System.Net.Http.Json;
using System.Text;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class DedupTests : IAsyncLifetime
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
                        Name = "stripe",
                        Path = "/stripe",
                        ForwardUrl = "http://localhost/stripe",
                        Validation = null,
                        DedupEventIdHeader = "Stripe-Event-Id",
                    },
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
    public async Task Ingest_SameBodyAndEventId_ReturnsExistingEventAndDoesNotDuplicate()
    {
        var client = _factory.CreateClient();
        var body = """{"id":"evt_1","type":"x"}""";

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/stripe")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        first.Headers.TryAddWithoutValidation("Stripe-Event-Id", "evt_1");
        var r1 = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var doc1 = await r1.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var firstId = doc1!["eventId"].ToString();

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/stripe")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        second.Headers.TryAddWithoutValidation("Stripe-Event-Id", "evt_1");
        var r2 = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Accepted, r2.StatusCode);
        var doc2 = await r2.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal(firstId, doc2!["eventId"].ToString());
        Assert.Equal("True", doc2["duplicate"].ToString(), ignoreCase: true);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        Assert.Equal(1, await db.Events.CountAsync());
    }

    [Fact]
    public async Task Ingest_DifferentBody_CreatesSeparateEvent()
    {
        var client = _factory.CreateClient();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/stripe")
        {
            Content = new StringContent("""{"id":"evt_a"}""", Encoding.UTF8, "application/json"),
        };
        first.Headers.TryAddWithoutValidation("Stripe-Event-Id", "evt_a");
        await client.SendAsync(first);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/stripe")
        {
            Content = new StringContent("""{"id":"evt_b"}""", Encoding.UTF8, "application/json"),
        };
        second.Headers.TryAddWithoutValidation("Stripe-Event-Id", "evt_b");
        await client.SendAsync(second);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        Assert.Equal(2, await db.Events.CountAsync());
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
