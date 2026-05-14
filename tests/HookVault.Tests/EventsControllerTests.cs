using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Configuration;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
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

    private ReplayQueue Queue() => _factory.Services.GetRequiredService<ReplayQueue>();

    private async Task<List<Guid>> DrainQueueAsync(int expected, int timeoutMs = 1000)
    {
        var queue = Queue();
        var ids = new List<Guid>();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (ids.Count < expected)
            {
                var id = await queue.Reader.ReadAsync(cts.Token);
                ids.Add(id);
            }
        }
        catch (OperationCanceledException) { }
        return ids;
    }

    [Fact]
    public async Task ReplaySingle_returns_404_when_event_missing()
    {
        var response = await AuthedClient().PostAsync($"/api/events/{Guid.NewGuid()}/replay", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReplaySingle_enqueues_event_and_returns_202()
    {
        var evt = NewEvent();
        await SeedAsync(evt);

        var response = await AuthedClient().PostAsync($"/api/events/{evt.Id}/replay", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayEnqueuedResponse>();
        Assert.NotNull(body);
        Assert.Equal(evt.Id, body.EventId);
        Assert.Equal("Queued", body.Status);

        var queued = await DrainQueueAsync(1);
        Assert.Single(queued);
        Assert.Equal(evt.Id, queued[0]);
    }

    [Fact]
    public async Task ReplayBulk_returns_202_with_zero_when_nothing_to_replay()
    {
        var response = await AuthedClient().PostAsync("/api/events/replay-failed", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.Enqueued);
    }

    [Fact]
    public async Task ReplayBulk_enqueues_both_ForwardFailed_and_ReplayFailed_by_default()
    {
        await SeedAsync(
            NewEvent(status: EventStatus.ForwardFailed),
            NewEvent(status: EventStatus.ReplayFailed),
            NewEvent(status: EventStatus.Forwarded));

        var response = await AuthedClient().PostAsync("/api/events/replay-failed", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Enqueued);

        var queued = await DrainQueueAsync(2);
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public async Task ReplayBulk_filters_by_status_when_provided()
    {
        await SeedAsync(
            NewEvent(status: EventStatus.ForwardFailed),
            NewEvent(status: EventStatus.ReplayFailed));

        var response = await AuthedClient().PostAsync("/api/events/replay-failed?status=ForwardFailed", null);

        var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Enqueued);
        Assert.Equal("ForwardFailed", body.Status);
    }

    [Fact]
    public async Task ReplayBulk_filters_by_provider_when_provided()
    {
        await SeedAsync(
            NewEvent("stripe", EventStatus.ForwardFailed),
            NewEvent("github", EventStatus.ForwardFailed));

        var response = await AuthedClient().PostAsync("/api/events/replay-failed?provider=stripe", null);

        var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Enqueued);
        Assert.Equal("stripe", body.Provider);
    }

    [Fact]
    public async Task ReplayBulk_rejects_invalid_status_with_400()
    {
        var response = await AuthedClient().PostAsync("/api/events/replay-failed?status=Forwarded", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Contains("ForwardFailed", error.Error);
    }

    [Fact]
    public async Task Delete_without_confirm_returns_400()
    {
        var response = await AuthedClient().DeleteAsync("/api/events");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Contains("confirm", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_with_confirm_removes_all_events()
    {
        await SeedAsync(NewEvent("stripe"), NewEvent("github"));

        var response = await AuthedClient().DeleteAsync("/api/events?confirm=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DeleteResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Deleted);
        Assert.Null(body.Provider);

        var list = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");
        Assert.NotNull(list);
        Assert.Equal(0, list.Total);
    }

    [Fact]
    public async Task Delete_with_provider_filter_only_removes_matching()
    {
        await SeedAsync(NewEvent("stripe"), NewEvent("github"));

        var response = await AuthedClient().DeleteAsync("/api/events?confirm=true&provider=stripe");

        var body = await response.Content.ReadFromJsonAsync<DeleteResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Deleted);
        Assert.Equal("stripe", body.Provider);

        var list = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");
        Assert.NotNull(list);
        Assert.Equal(1, list.Total);
        Assert.Equal("github", list.Items[0].Provider);
    }

    [Fact]
    public async Task Delete_confirm_is_case_insensitive()
    {
        await SeedAsync(NewEvent());

        var response = await AuthedClient().DeleteAsync("/api/events?confirm=TRUE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_without_token_returns_401()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/events?confirm=true");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
