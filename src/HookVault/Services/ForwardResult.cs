namespace HookVault.Services;

internal sealed record ForwardResult(bool Success, int? StatusCode, string? Error);
