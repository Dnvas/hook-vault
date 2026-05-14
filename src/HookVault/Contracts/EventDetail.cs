using System.Text.Json;

namespace HookVault.Contracts;

public sealed record EventDetail(
    Guid Id,
    string Provider,
    string Path,
    JsonElement Headers,
    string Body,
    DateTimeOffset ReceivedAt,
    string? SignatureHeader,
    bool? SignatureValid,
    JsonElement? ValidationDetails,
    string ForwardUrl,
    DateTimeOffset? ForwardedAt,
    int? ForwardStatusCode,
    string? ForwardError,
    string Status,
    int ReplayCount,
    DateTimeOffset? LastReplayAt,
    string? LastError);
