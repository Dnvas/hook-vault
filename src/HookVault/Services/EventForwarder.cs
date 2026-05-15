using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public sealed class EventForwarder(IHttpClientFactory httpClientFactory, EventRepository repo, ILogger<EventForwarder> logger)
{
    // Hop-by-hop headers (RFC 9110 §7.6.1) and Authorization must not be forwarded.
    // Hop-by-hop headers describe the original transport connection, not the payload.
    // Authorization is omitted so the captured token from the provider cannot be
    // replayed against the local destination without explicit opt-in.
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding",
        "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Authenticate",
        "Authorization",
    };

    internal async Task<ForwardResult> SendAsync(WebhookEvent evt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("forwarder");

        using var request = new HttpRequestMessage(HttpMethod.Post, evt.ForwardUrl);
        request.Content = new ByteArrayContent(evt.Body);

        var storedHeaders =
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(evt.Headers)
            ?? [];

        foreach (var (key, values) in storedHeaders)
        {
            if (SkippedHeaders.Contains(key)) continue;

            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.TryAddWithoutValidation(key, values);
            else
                request.Headers.TryAddWithoutValidation(key, values);
        }

        request.Headers.TryAddWithoutValidation("X-HookVault-Event-Id", evt.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-HookVault-Provider", evt.Provider);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var success = response.IsSuccessStatusCode;
            var statusCode = (int)response.StatusCode;
            logger.LogInformation("Forwarded event {Id} to {Url} → {Status}", evt.Id, evt.ForwardUrl, statusCode);
            return new ForwardResult(success, statusCode, success ? null : $"Upstream returned {statusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Forward timed out for event {Id}", evt.Id);
            return new ForwardResult(false, null, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Forward failed for event {Id}", evt.Id);
            return new ForwardResult(false, null, ex.Message);
        }
    }

    public async Task ForwardAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        evt.Status = EventStatus.Forwarding;
        await repo.UpdateAsync(evt, ct);

        var result = await SendAsync(evt, ct);

        evt.ForwardedAt = DateTimeOffset.UtcNow;
        evt.ForwardStatusCode = result.StatusCode;
        evt.Status = result.Success ? EventStatus.Forwarded : EventStatus.ForwardFailed;
        evt.ForwardError = result.Success ? null : result.Error;

        await repo.UpdateAsync(evt, ct);
    }
}
