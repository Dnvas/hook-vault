using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HookVault.E2E.Fixtures;

/// <summary>Typed HTTP wrapper around the live HookVault container.</summary>
public sealed class HookVaultClient(string baseUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl) };

    public async Task ResetAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("/api/test/reset", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"reset returned {(int)resp.StatusCode}: {body}");
        }
    }

    public async Task<HttpResponseMessage> IngestStripeAsync(
        byte[] body, string secret, long timestamp, CancellationToken ct = default)
    {
        var payload = $"{timestamp}.{Encoding.UTF8.GetString(body)}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
                                Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();

        using var content = new ByteArrayContent(body);
        content.Headers.Add("Stripe-Signature", $"t={timestamp},v1={sig}");
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return await _http.PostAsync("/api/ingest/stripe", content, ct);
    }

    public async Task<HttpResponseMessage> IngestStripeWithBadSignatureAsync(
        byte[] body, long timestamp, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.Add("Stripe-Signature", $"t={timestamp},v1=deadbeef");
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await _http.PostAsync("/api/ingest/stripe", content, ct);
    }

    public async Task<List<EventSummary>> GetEventsAsync(CancellationToken ct = default)
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/events", ct);
        var list = new List<EventSummary>();
        // ListEventsResponse serialises as { "items": [...], "total": ..., "limit": ..., "offset": ... }
        // The C# record property is Items; ASP.NET Core's camelCase policy serialises it to "items".
        foreach (var e in doc.GetProperty("items").EnumerateArray())
        {
            list.Add(new EventSummary(
                e.GetProperty("id").GetGuid(),
                e.GetProperty("provider").GetString() ?? "",
                e.GetProperty("status").GetString() ?? ""));
        }
        return list;
    }

    public async Task<HttpResponseMessage> ReplayAsync(Guid eventId, CancellationToken ct = default) =>
        await _http.PostAsync($"/api/events/{eventId}/replay", content: null, ct);

    public sealed record EventSummary(Guid Id, string Provider, string Status);
}
