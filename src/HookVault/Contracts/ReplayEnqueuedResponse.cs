namespace HookVault.Contracts;

public sealed record ReplayEnqueuedResponse(Guid EventId, string Status);
