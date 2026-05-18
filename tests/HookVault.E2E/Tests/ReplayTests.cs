using System.Net;
using System.Text;
using HookVault.E2E.Fixtures;

namespace HookVault.E2E.Tests;

[Collection("e2e")]
public sealed class ReplayTests(ComposeStackFixture stack)
{
    private static readonly string Secret =
        Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET")
        ?? throw new InvalidOperationException("STRIPE_WEBHOOK_SECRET must be set");

    [Fact]
    public async Task Replay_Forwards_Again()
    {
        var hookvault = new HookVaultClient(stack.BaseUrl);
        var mock = new MockUpstreamClient(stack.MockUpstreamContainer);
        await hookvault.ResetAsync();

        var body = Encoding.UTF8.GetBytes("""{"event":"checkout.session.completed","id":"evt_replay_1"}""");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since = DateTimeOffset.UtcNow;

        var ingest = await hookvault.IngestStripeAsync(body, Secret, ts);
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        // Wait for the first forward to land in the mock log.
        await mock.WaitForRequestAsync(
            since,
            r => r.Body.Contains("checkout.session.completed"),
            TimeSpan.FromSeconds(10));

        var events = await hookvault.GetEventsAsync();
        Assert.Single(events);

        var replay = await hookvault.ReplayAsync(events[0].Id);
        Assert.True(
            replay.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted,
            $"replay returned {replay.StatusCode}");

        // Two forwards expected: original + replay. Poll up to 10s.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        int matched = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            matched = mock.Count(since, r => r.Body.Contains("checkout.session.completed"));
            if (matched >= 2) break;
            await Task.Delay(500);
        }
        Assert.True(matched >= 2, $"expected ≥2 forwards, observed {matched}");
    }
}
