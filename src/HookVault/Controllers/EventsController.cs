using System.Text.Json;
using System.Threading.Channels;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[ApiController]
[Authorize]
[Route("api/events")]
public sealed class EventsController(
    EventRepository repo,
    ReplayQueue queue,
    ILogger<EventsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(status) && !Enum.TryParse<EventStatus>(status, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<EventStatus>());
            return BadRequest(new ApiError(
                $"Invalid status '{status}'. Valid values: {valid}.",
                "invalid_status"));
        }

        var clampedLimit = Math.Clamp(limit ?? 50, 1, 500);
        var clampedOffset = Math.Max(offset ?? 0, 0);

        var (items, total) = await repo.ListSummariesAsync(
            provider, status, from, to, clampedLimit, clampedOffset, ct);

        return Ok(new ListEventsResponse(items, total, clampedLimit, clampedOffset));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var evt = await repo.GetByIdAsync(id, ct);
        if (evt is null)
            return NotFound(new ApiError("Event not found.", "event_not_found"));

        return Ok(ToDetail(evt));
    }

    [HttpPost("{id:guid}/replay")]
    public async Task<IActionResult> Replay(Guid id, CancellationToken ct)
    {
        var evt = await repo.GetByIdAsync(id, ct);
        if (evt is null)
            return NotFound(new ApiError("Event not found.", "event_not_found"));

        await queue.EnqueueAsync(id, ct);
        logger.LogInformation("Enqueued replay for event {EventId} (provider {Provider})", id, evt.Provider);

        return Accepted(new ReplayEnqueuedResponse(id, "Queued"));
    }

    [HttpPost("replay-failed")]
    public async Task<IActionResult> ReplayFailed(
        [FromQuery] string? provider,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        EventStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<EventStatus>(status, ignoreCase: true, out var parsed)
                || (parsed != EventStatus.ForwardFailed && parsed != EventStatus.ReplayFailed))
            {
                return BadRequest(new ApiError(
                    "Invalid status. Must be ForwardFailed or ReplayFailed (case-insensitive).",
                    "invalid_status"));
            }
            statusFilter = parsed;
        }

        var failed = await repo.GetFailedAsync(provider, ct);
        if (statusFilter is { } s)
            failed = failed.Where(e => e.Status == s).ToList();

        foreach (var evt in failed)
            await queue.EnqueueAsync(evt.Id, ct);

        logger.LogInformation(
            "Bulk replay enqueued {Count} events (provider={Provider}, status={Status})",
            failed.Count, provider ?? "*", statusFilter?.ToString() ?? "*");

        return Accepted(new ReplayBulkResponse(failed.Count, provider, statusFilter?.ToString()));
    }

    [HttpGet("stream")]
    public async Task Stream([FromServices] EventNotifier notifier, CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        // Commit headers immediately so the client (including TestServer) sees the 200
        // before any data events arrive — otherwise headers only flush on first write.
        await Response.StartAsync(ct);

        var subscription = notifier.Subscribe();
        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            while (!ct.IsCancellationRequested)
            {
                var readTask = subscription.Reader.ReadAsync(ct).AsTask();
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), ct);

                var winner = await Task.WhenAny(readTask, heartbeatTask);

                if (winner == readTask)
                {
                    var notification = await readTask;
                    var data = JsonSerializer.Serialize(notification, jsonOptions);
                    await Response.WriteAsync($"data: {data}\n\n", ct);
                }
                else
                {
                    // Heartbeat: SSE comment line, ignored by EventSource client.
                    await Response.WriteAsync(": heartbeat\n\n", ct);
                }

                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (ChannelClosedException) { /* notifier shutting down */ }
        catch (IOException) { /* underlying transport gone */ }
        finally
        {
            notifier.Unsubscribe(subscription);
        }
    }

    [HttpDelete]
    public async Task<IActionResult> Purge(
        [FromQuery] string? provider,
        [FromQuery] string? confirm,
        CancellationToken ct)
    {
        if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ApiError(
                "Pass ?confirm=true to delete events.",
                "delete_confirm_required"));
        }

        var deleted = await repo.DeleteAsync(provider, ct);
        logger.LogWarning(
            "Deleted {Count} events (provider={Provider})",
            deleted, provider ?? "*");

        return Ok(new DeleteResponse(deleted, provider));
    }

    private static EventDetail ToDetail(WebhookEvent evt) => new(
        evt.Id,
        evt.Provider,
        evt.Path,
        ParseHeadersForApi(evt.Headers),
        BodyToText(evt.Body),
        evt.ReceivedAt,
        evt.SignatureHeader,
        evt.SignatureValid,
        TryParseJson(evt.ValidationDetails),
        evt.ForwardUrl,
        evt.ForwardedAt,
        evt.ForwardStatusCode,
        evt.ForwardError,
        evt.Status.ToString(),
        evt.ReplayCount,
        evt.LastReplayAt,
        evt.LastError);

    // UTF-8 decode with replacement chars for invalid sequences. The API contract
    // stays string-typed for back-compat with the existing UI; richer binary
    // exposure is a future PR.
    private static string BodyToText(byte[] body)
    {
        if (body.Length == 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(body);
    }

    // Read the JSON-stored Dictionary<string, string[]> and reproject as
    // Dictionary<string, string> (comma-joined) for the UI's existing shape.
    private static JsonElement ParseHeadersForApi(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("{}").RootElement;
        try
        {
            var arrayShape = JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw);
            if (arrayShape is null) return JsonDocument.Parse("{}").RootElement;
            var flat = arrayShape.ToDictionary(
                kv => kv.Key,
                kv => string.Join(", ", kv.Value));
            var json = JsonSerializer.Serialize(flat);
            return JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static JsonElement? TryParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return null; }
    }
}
