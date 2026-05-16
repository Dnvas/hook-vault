using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;

namespace HookVault.Services.Schemes;

// Svix multi-header HMAC. Used by Resend, Clerk, PostHog, and other providers
// delivered via the Svix platform.
//
// Signed payload: "{svix-id}.{svix-timestamp}.{body}"
// Signature header: "svix-signature" — value is "v1,<base64sig>" optionally
// space-separated for key-rotation windows.
public sealed class SvixHmacScheme : IIngestSignatureScheme
{
    private const string IdHeader = "svix-id";
    private const string TsHeader = "svix-timestamp";
    private const string SigHeader = "svix-signature";

    public string Name => "svix";

    public SignatureValidationResult Validate(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        try
        {
            return ValidateCore(config, rawBody, headers);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return SignatureValidationResult.Fail(ex.Message);
        }
    }

    private static SignatureValidationResult ValidateCore(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        var rawSecret = Environment.GetEnvironmentVariable(config.SecretEnvVar);
        if (string.IsNullOrEmpty(rawSecret))
            return SignatureValidationResult.Fail($"Secret env var '{config.SecretEnvVar}' is not set or empty.");

        if (!headers.TryGetValue(IdHeader, out var id) || string.IsNullOrEmpty(id))
            return SignatureValidationResult.Fail($"Header '{IdHeader}' is missing or empty.");
        if (!headers.TryGetValue(TsHeader, out var ts) || string.IsNullOrEmpty(ts))
            return SignatureValidationResult.Fail($"Header '{TsHeader}' is missing or empty.");
        if (!headers.TryGetValue(SigHeader, out var sig) || string.IsNullOrEmpty(sig))
            return SignatureValidationResult.Fail($"Header '{SigHeader}' is missing or empty.");

        var key = DecodeKey(rawSecret);
        var payload = $"{id}.{ts}.{Encoding.UTF8.GetString(rawBody)}";
        var computedBytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        var computedB64 = Convert.ToBase64String(computedBytes);

        // Svix sends space-separated signatures during key rotation windows.
        // Match any.
        var candidates = sig.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var anyMatch = false;
        foreach (var candidate in candidates)
        {
            // Each candidate is "v1,<base64>"; strip the version prefix.
            var commaIx = candidate.IndexOf(',');
            if (commaIx < 0) continue;
            var receivedB64 = candidate[(commaIx + 1)..];
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(computedB64),
                    Encoding.UTF8.GetBytes(receivedB64)))
            {
                anyMatch = true;
                break;
            }
        }

        if (config.MaxAgeSeconds is { } maxAge && long.TryParse(ts, out var unixSeconds))
        {
            var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds;
            if (ageSeconds > maxAge)
                return SignatureValidationResult.Fail(
                    $"Signature timestamp expired: {ageSeconds}s old, max age is {maxAge}s.");
        }

        return new SignatureValidationResult
        {
            IsValid = anyMatch,
            AlgorithmUsed = "hmac-sha256",
            PayloadUsed = payload,
            ExtractedTimestamp = ts.ToString(),
            ReceivedSignature = sig.ToString(),
            ComputedSignature = computedB64,
        };
    }

    // Svix secrets are advertised as "whsec_<base64>". Strip the prefix and
    // base64-decode; fall through for non-prefixed strings (test fixtures, etc.).
    private static byte[] DecodeKey(string rawSecret)
    {
        var keyB64 = rawSecret.StartsWith("whsec_", StringComparison.Ordinal)
            ? rawSecret[6..]
            : rawSecret;
        return Convert.FromBase64String(keyB64);
    }
}
