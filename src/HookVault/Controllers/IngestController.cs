using System.Text.Json;
using HookVault.Configuration;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[AllowAnonymous]
[ApiController]
public class IngestController(
    HookVaultOptions options,
    SignatureValidator validator,
    EventRepository repo,
    EventForwarder forwarder,
    EventNotifier notifier,
    IWebHostEnvironment environment,
    ILogger<IngestController> logger) : ControllerBase
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
    };
    [HttpPost("api/ingest/{**provider}")]
    public async Task<IActionResult> Ingest(string provider, CancellationToken ct)
    {
        // Normalise both sides: strip leading slash from configured path,
        // compare against the catch-all `provider` segment (which has no leading slash).
        var normalisedRequest = provider.TrimStart('/');
        var config = options.Providers.FirstOrDefault(p =>
            p.Path.TrimStart('/').Equals(normalisedRequest, StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            logger.LogWarning("Ingest request for unknown provider path '{Provider}'",
                provider.Replace('\n', '_').Replace('\r', '_'));
            return NotFound(new { error = $"No provider configured for path '{provider}'." });
        }

        var rawBody = HttpContext.Items[RawBodyMiddleware.RawBodyKey] as byte[] ?? [];

        // Compute body hash (SHA-256 lowercase hex) for dedup.
        var bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBody))
                              .ToLowerInvariant();

        // Extract optional provider event id from the configured header.
        string? providerEventId = null;
        if (!string.IsNullOrEmpty(config.DedupEventIdHeader)
            && Request.Headers.TryGetValue(config.DedupEventIdHeader, out var idValues)
            && !string.IsNullOrEmpty(idValues))
        {
            providerEventId = idValues.ToString();
        }

        // Dedup: within a 24h window, identical (provider, body-hash[, event-id])
        // returns the existing event instead of double-storing.
        var existing = await repo.FindDuplicateAsync(
            config.Name, bodyHash, providerEventId,
            DateTimeOffset.UtcNow.AddHours(-24), ct);

        if (existing is not null)
        {
            logger.LogInformation(
                "Duplicate ingest for provider '{Provider}' matched existing event {EventId}",
                config.Name, existing.Id);

            return Accepted(new
            {
                eventId = existing.Id,
                provider = config.Name,
                duplicate = true,
                status = existing.Status.ToString(),
            });
        }

        // Strip sensitive header values before persistence. The forwarder still
        // uses the live Request headers on the ingest path; replays from the DB
        // will see [redacted] in place, which means the local upstream won't
        // receive provider-issued bearer tokens on replay — that's the safer
        // default for a dev tool.
        var headersDict = Request.Headers
            .ToDictionary(
                h => h.Key,
                h => SensitiveHeaders.Contains(h.Key)
                    ? new[] { "[redacted]" }
                    : h.Value.Where(v => v is not null).Select(v => v!).ToArray());
        var headersJson = JsonSerializer.Serialize(headersDict);

        // --- Signature Validation ---
        bool? signatureValid = null;
        string? validationDetails = null;

        if (config.Validation is not null)
        {
            var result = validator.Validate(config.Validation, rawBody, Request.Headers);
            signatureValid = result.IsValid;

            // Redact computedSignature outside Development unless explicitly opted in.
            // Exposing (payload, computed) pairs gives an attacker oracle data;
            // default off in production.
            var resultForSerialization = ShouldExposeComputedSignature(environment)
                ? result
                : result with { ComputedSignature = "[redacted]" };

            validationDetails = JsonSerializer.Serialize(resultForSerialization,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

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
            Body = rawBody,
            SignatureHeader = config.Validation?.SignatureHeader,
            SignatureValid = signatureValid,
            ValidationDetails = validationDetails,
            ForwardUrl = config.ForwardUrl,
            BodyHash = bodyHash,
            ProviderEventId = providerEventId,
            Status = EventStatus.Received,
        };

        await repo.AddAsync(evt, ct);
        logger.LogInformation("Captured event {Id} for provider '{Provider}'", evt.Id, config.Name);
        notifier.Notify(new EventNotification(evt.Id, config.Name, evt.Status.ToString()));

        if (config.CaptureOnly)
        {
            // Resting state for capture-only providers: persisted but not forwarded.
            // Users can manually replay when their downstream is ready.
            evt.Status = EventStatus.Captured;
            await repo.UpdateAsync(evt, ct);
        }
        else
        {
            // Forward synchronously so the response reflects the current forward status.
            // The forwarder writes the result back onto the event record.
            await forwarder.ForwardAsync(evt, ct);
        }

        return Accepted(new
        {
            eventId = evt.Id,
            provider = config.Name,
            signatureValid,
            status = evt.Status.ToString(),
        });
    }

    private static bool ShouldExposeComputedSignature(IWebHostEnvironment env)
    {
        var optIn = Environment.GetEnvironmentVariable("HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE");
        if (string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return env.IsDevelopment();
    }
}
