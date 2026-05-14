using System.Text.Json;
using HookVault.Configuration;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[ApiController]
public class IngestController(
    HookVaultOptions options,
    SignatureValidator validator,
    EventRepository repo,
    EventForwarder forwarder,
    ILogger<IngestController> logger) : ControllerBase
{
    [HttpPost("api/ingest/{provider}")]
    public async Task<IActionResult> Ingest(string provider, CancellationToken ct)
    {
        // Resolve provider by path segment (e.g. "stripe" matches path "/stripe")
        var config = options.Providers.FirstOrDefault(p =>
            p.Path.TrimStart('/').Equals(provider, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            logger.LogWarning("Ingest request for unknown provider path '{Provider}'",
                provider.Replace('\n', '_').Replace('\r', '_'));
            return NotFound(new { error = $"No provider configured for path '{provider}'." });
        }

        var rawBody = HttpContext.Items[RawBodyMiddleware.RawBodyKey] as byte[] ?? [];
        var bodyText = System.Text.Encoding.UTF8.GetString(rawBody);

        // Capture headers as a flat JSON dict (take first value per header)
        var headersDict = Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString());
        var headersJson = JsonSerializer.Serialize(headersDict);

        // --- Signature Validation ---
        bool? signatureValid = null;
        string? validationDetails = null;

        if (config.Validation is not null)
        {
            var result = validator.Validate(config.Validation, rawBody, Request.Headers);
            signatureValid = result.IsValid;
            validationDetails = JsonSerializer.Serialize(result);

            if (!result.IsValid)
                logger.LogWarning("Signature validation failed for provider '{Provider}': {Error}",
                    config.Name, result.Error ?? "mismatch");
        }

        // --- Persist ---
        var evt = new WebhookEvent
        {
            Provider = config.Name,
            Path = $"/api/ingest/{provider}",
            Headers = headersJson,
            Body = bodyText,
            SignatureHeader = config.Validation?.SignatureHeader,
            SignatureValid = signatureValid,
            ValidationDetails = validationDetails,
            ForwardUrl = config.ForwardUrl,
            Status = EventStatus.Received,
        };

        await repo.AddAsync(evt, ct);
        logger.LogInformation("Captured event {Id} for provider '{Provider}'", evt.Id, config.Name);

        // --- Forward (fire and update, non-blocking to caller) ---
        // We still await it here so the caller gets a response after the forward attempt.
        // The forwarder updates the event's status internally.
        await forwarder.ForwardAsync(evt, ct);

        return Accepted(new
        {
            eventId = evt.Id,
            provider = config.Name,
            signatureValid,
            status = evt.Status.ToString(),
        });
    }
}
