using System.Text.Json;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[ApiController]
[Authorize]
[Route("api/events")]
public sealed class EventsController(EventRepository repo) : ControllerBase
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

    private static EventDetail ToDetail(WebhookEvent evt) => new(
        evt.Id,
        evt.Provider,
        evt.Path,
        ParseJsonOrEmpty(evt.Headers),
        evt.Body,
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

    private static JsonElement ParseJsonOrEmpty(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("{}").RootElement;
        try { return JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement; }
    }

    private static JsonElement? TryParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonDocument.Parse(raw).RootElement; }
        catch (JsonException) { return null; }
    }
}
