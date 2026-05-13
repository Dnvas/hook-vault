namespace HookVault.Services;

public sealed record SignatureValidationResult
{
    public bool IsValid { get; init; }
    public string AlgorithmUsed { get; init; } = string.Empty;
    public string PayloadUsed { get; init; } = string.Empty;
    public string? ExtractedTimestamp { get; init; }
    public string? ReceivedSignature { get; init; }
    public string? ComputedSignature { get; init; }
    public string? Error { get; init; }

    public static SignatureValidationResult Skipped() =>
        new() { IsValid = true, AlgorithmUsed = "none" };

    public static SignatureValidationResult Fail(string error) =>
        new() { IsValid = false, Error = error, AlgorithmUsed = "unknown" };
}
