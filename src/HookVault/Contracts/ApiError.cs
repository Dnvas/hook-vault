namespace HookVault.Contracts;

public sealed record ApiError(string Error, string? Code = null);
