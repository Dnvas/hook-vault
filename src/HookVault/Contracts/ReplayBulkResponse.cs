namespace HookVault.Contracts;

public sealed record ReplayBulkResponse(int Enqueued, string? Provider, string? Status);
