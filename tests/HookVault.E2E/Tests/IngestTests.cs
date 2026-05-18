using System.Net;
using System.Text;
using System.Text.Json;
using HookVault.E2E.Fixtures;

namespace HookVault.E2E.Tests;

[Collection("e2e")]
public sealed class IngestTests(ComposeStackFixture stack)
{
    private static readonly string Secret =
        Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET")
        ?? throw new InvalidOperationException(
            "STRIPE_WEBHOOK_SECRET must be set (same as docker compose env)");

    [Fact]
    public async Task ValidHmac_Ingests_And_Forwards()
    {
        var hookvault = new HookVaultClient(stack.BaseUrl);
        var mock = new MockUpstreamClient(stack.MockUpstreamContainer);
        await hookvault.ResetAsync();

        var body = Encoding.UTF8.GetBytes("""{"event":"payment_intent.succeeded","id":"evt_test_1"}""");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // since is captured AFTER reset (consistent with the InvalidHmac
        // test) so forwards from any earlier test in the same log window
        // are excluded from the assertions below.
        var since = DateTimeOffset.UtcNow;

        var resp = await hookvault.IngestStripeAsync(body, Secret, ts);

        // IngestController returns 202 Accepted: the event has been captured
        // and queued; forwarding happens asynchronously via ReplayWorker.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var events = await hookvault.GetEventsAsync();
        Assert.Single(events);
        Assert.Equal("stripe", events[0].Provider);

        var forwarded = await mock.WaitForRequestAsync(
            since,
            r => r.Path.Contains("/forwarded") && r.Body.Contains("payment_intent.succeeded"),
            TimeSpan.FromSeconds(10));

        Assert.Equal("POST", forwarded.Method);
    }

    [Fact]
    public async Task InvalidHmac_CapturedAndFlagged_StillForwarded()
    {
        // HookVault is a "transparent pass-through" debug proxy: it captures
        // EVERY incoming webhook including those that fail signature
        // validation, persists the validation result for inspection, and
        // forwards to the configured upstream regardless. The downstream
        // app is responsible for its own signature verification — HookVault
        // intentionally does not act as a security perimeter. See
        // hookvault-spec §"What HookVault does" for the principle.
        var hookvault = new HookVaultClient(stack.BaseUrl);
        var mock = new MockUpstreamClient(stack.MockUpstreamContainer);
        await hookvault.ResetAsync();

        var body = Encoding.UTF8.GetBytes("""{"event":"payment_intent.failed"}""");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // since is captured AFTER reset so we don't count forwards left
        // over from earlier tests still inside the same-second log window.
        var since = DateTimeOffset.UtcNow;

        var resp = await hookvault.IngestStripeWithBadSignatureAsync(body, ts);

        // 202 Accepted — captured-and-flagged, not rejected.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("signatureValid").GetBoolean());

        var events = await hookvault.GetEventsAsync();
        Assert.Single(events);
        Assert.Equal("stripe", events[0].Provider);

        // Forward still happens. Give the worker time to deliver.
        await Task.Delay(1000);
        Assert.Equal(1, mock.Count(since, r => r.Path.Contains("/forwarded")));
    }
}
