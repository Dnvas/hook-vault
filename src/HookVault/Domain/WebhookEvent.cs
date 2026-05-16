using System.ComponentModel.DataAnnotations;

namespace HookVault.Domain;

public sealed class WebhookEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string Path { get; set; } = string.Empty;

    // JSON-encoded Dictionary<string, string[]>. Multi-value headers are preserved
    // as arrays. Single-value headers are stored as single-element arrays.
    public string Headers { get; set; } = "{}";

    // Raw request body bytes. Stored as BLOB so binary payloads (multipart, protobuf)
    // round-trip without UTF-8 corruption.
    public byte[] Body { get; set; } = [];

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? SignatureHeader { get; set; }

    public bool? SignatureValid { get; set; }

    public string? ValidationDetails { get; set; }

    public string ForwardUrl { get; set; } = string.Empty;

    public DateTimeOffset? ForwardedAt { get; set; }

    public int? ForwardStatusCode { get; set; }

    public string? ForwardError { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Received;

    public int ReplayCount { get; set; }

    public DateTimeOffset? LastReplayAt { get; set; }

    public string? LastError { get; set; }

    // SHA-256 of the raw body bytes (lowercase hex). Used by the dedup path to
    // detect identical re-deliveries from a provider.
    public string? BodyHash { get; set; }

    // Provider-supplied event id, extracted from a header the provider config
    // names (e.g. Stripe-Signature 't=...' or X-GitHub-Delivery). Optional;
    // when null, dedup uses BodyHash alone.
    public string? ProviderEventId { get; set; }
}
