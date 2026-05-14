namespace HookVault.Contracts;

public sealed record EventSummary(
    Guid Id,
    string Provider,
    string Status,
    DateTimeOffset ReceivedAt,
    bool? SignatureValid,
    int? ForwardStatusCode,
    int ReplayCount,
    DateTimeOffset? ForwardedAt);
