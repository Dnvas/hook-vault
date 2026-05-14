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

    public string Headers { get; set; } = "{}";

    public string Body { get; set; } = string.Empty;

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
}
