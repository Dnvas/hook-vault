using System.Net;
using System.Text;
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
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

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
}
