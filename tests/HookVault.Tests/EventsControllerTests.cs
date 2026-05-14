using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Configuration;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

public sealed class EventsControllerTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var existing = s.SingleOrDefault(d => d.ServiceType == typeof(HookVaultOptions));
            if (existing is not null) s.Remove(existing);
            s.AddSingleton(new HookVaultOptions { Providers = [] });
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
        var options = new JwtOptions(
            HookVaultWebApplicationFactory.TestSecret,
            HookVaultWebApplicationFactory.TestIssuer,
            HookVaultWebApplicationFactory.TestAudience);
        var token = JwtTokenGenerator.Mint(options, "test", TimeSpan.FromMinutes(5));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task SeedAsync(params WebhookEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        db.Events.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static WebhookEvent NewEvent(string provider = "stripe",
        EventStatus status = EventStatus.Forwarded,
        DateTimeOffset? receivedAt = null)
    => new()
    {
        Provider = provider,
        Path = $"/api/ingest/{provider}",
        Headers = "{\"X-Test\":\"yes\"}",
        Body = "{}",
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
        ForwardUrl = "http://localhost/forward",
        ForwardStatusCode = 200,
        SignatureValid = true,
        Status = status,
    };

    [Fact]
    public async Task List_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_with_token_returns_empty_when_no_events()
    {
        var response = await AuthedClient().GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Empty(body.Items);
        Assert.Equal(0, body.Total);
        Assert.Equal(50, body.Limit);
        Assert.Equal(0, body.Offset);
    }

    [Fact]
    public async Task List_returns_events_in_received_at_desc_order()
    {
        var older = NewEvent(receivedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = NewEvent(receivedAt: DateTimeOffset.UtcNow);
        await SeedAsync(older, newer);
        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");
        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.Equal(newer.Id, body.Items[0].Id);
        Assert.Equal(older.Id, body.Items[1].Id);
    }

    [Fact]
    public async Task List_filters_by_provider()
    {
        await SeedAsync(NewEvent("stripe"), NewEvent("github"));
        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events?provider=stripe");
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("stripe", body.Items[0].Provider);
    }

    [Fact]
    public async Task List_filters_by_status_case_insensitive()
    {
        await SeedAsync(
            NewEvent(status: EventStatus.Forwarded),
            NewEvent(status: EventStatus.ForwardFailed));
        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events?status=forwardfailed");
        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("ForwardFailed", body.Items[0].Status);
    }

    [Fact]
    public async Task List_rejects_invalid_status_with_400()
    {
        var response = await AuthedClient().GetAsync("/api/events?status=notarealstatus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Contains("status", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_clamps_limit_to_500()
    {
        var response = await AuthedClient().GetAsync("/api/events?limit=10000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Equal(500, body.Limit);
    }

    [Fact]
    public async Task List_clamps_limit_to_1_when_zero_or_negative()
    {
        var response = await AuthedClient().GetAsync("/api/events?limit=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Limit);
    }

    [Fact]
    public async Task Detail_returns_404_for_missing_id()
    {
        var response = await AuthedClient().GetAsync($"/api/events/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_returns_full_payload_with_parsed_json()
    {
        var evt = NewEvent();
        evt.Headers = "{\"X-Test\":\"abc\"}";
        evt.ValidationDetails = "{\"isValid\":true}";
        await SeedAsync(evt);
        var response = await AuthedClient().GetAsync($"/api/events/{evt.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("abc", doc.RootElement.GetProperty("headers").GetProperty("X-Test").GetString());
        Assert.True(doc.RootElement.GetProperty("validationDetails").GetProperty("isValid").GetBoolean());
    }
}
