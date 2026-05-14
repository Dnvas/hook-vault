using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public class EventForwarder(IHttpClientFactory httpClientFactory, EventRepository repo, ILogger<EventForwarder> logger)
{
    // Headers we never forward — they describe the original connection, not the payload.
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding",
    };

    public async Task ForwardAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        evt.Status = EventStatus.Forwarding;
        await repo.UpdateAsync(evt, ct);

        var client = httpClientFactory.CreateClient("forwarder");

        using var request = new HttpRequestMessage(HttpMethod.Post, evt.ForwardUrl);
        request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(evt.Body));

        // Restore original headers (stored as JSON dict)
        var storedHeaders = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Headers)
            ?? [];

        foreach (var (key, value) in storedHeaders)
        {
            if (SkippedHeaders.Contains(key)) continue;

            // Content headers live on Content, not on the request itself in .NET
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.TryAddWithoutValidation(key, value);
            else
                request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Headers.TryAddWithoutValidation("X-HookVault-Event-Id", evt.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-HookVault-Provider", evt.Provider);

        try
        {
            using var response = await client.SendAsync(request, ct);
            evt.ForwardedAt = DateTimeOffset.UtcNow;
            evt.ForwardStatusCode = (int)response.StatusCode;
            evt.Status = response.IsSuccessStatusCode ? EventStatus.Forwarded : EventStatus.ForwardFailed;

            if (!response.IsSuccessStatusCode)
                evt.ForwardError = $"Upstream returned {(int)response.StatusCode}";

            logger.LogInformation("Forwarded event {Id} to {Url} → {Status}",
                evt.Id, evt.ForwardUrl, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            evt.Status = EventStatus.ForwardFailed;
            evt.ForwardError = "Request timed out";
            evt.ForwardedAt = DateTimeOffset.UtcNow;
            logger.LogWarning("Forward timed out for event {Id}", evt.Id);
        }
        catch (HttpRequestException ex)
        {
            evt.Status = EventStatus.ForwardFailed;
            evt.ForwardError = ex.Message;
            evt.ForwardedAt = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "Forward failed for event {Id}", evt.Id);
        }

        await repo.UpdateAsync(evt, ct);
    }
}
