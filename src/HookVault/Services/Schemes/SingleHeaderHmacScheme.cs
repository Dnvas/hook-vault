using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HookVault.Configuration;
using HookVault.Services;

namespace HookVault.Services.Schemes;

// Standard single-header HMAC: one header carries the signature (optionally with
// a prefix like 'v1=' and an optional timestamp segment). The signed payload is
// expressed via config.PayloadFormat.
public sealed class SingleHeaderHmacScheme : IIngestSignatureScheme
{
    public string Name => "single-header";

    public SignatureValidationResult Validate(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        try
        {
            return ValidateCore(config, rawBody, headers);
        }
        catch (NotSupportedException ex)
        {
            return SignatureValidationResult.Fail(ex.Message);
        }
    }

    private static SignatureValidationResult ValidateCore(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        // 1. Resolve secret from environment — never from config
        var secret = Environment.GetEnvironmentVariable(config.SecretEnvVar);
        if (string.IsNullOrEmpty(secret))
            return SignatureValidationResult.Fail(
                $"Secret env var '{config.SecretEnvVar}' is not set or empty.");

        // 2. Read the raw header value
        if (!headers.TryGetValue(config.SignatureHeader, out var headerValues)
            || string.IsNullOrEmpty(headerValues))
            return SignatureValidationResult.Fail(
                $"Header '{config.SignatureHeader}' is missing or empty.");

        var headerRaw = headerValues.ToString();

        // 3. Parse signature and optional timestamp from the header
        var receivedSignature = ExtractToken(headerRaw, config.SignaturePattern, "signature");
        if (receivedSignature is null)
            return SignatureValidationResult.Fail(
                $"Could not extract signature from header value '{headerRaw}' " +
                $"using pattern '{config.SignaturePattern}'.");

        var extractedTimestamp = config.TimestampPattern is not null
            ? ExtractToken(headerRaw, config.TimestampPattern, "timestamp")
            : null;

        if (config.TimestampPattern is not null && extractedTimestamp is null)
            return SignatureValidationResult.Fail(
                $"Could not extract timestamp from header value '{headerRaw}' " +
                $"using pattern '{config.TimestampPattern}'.");

        // 4. Build the payload string that was signed
        var bodyText = Encoding.UTF8.GetString(rawBody);
        var payload = config.PayloadFormat
            .Replace("{body}", bodyText)
            .Replace("{timestamp}", extractedTimestamp ?? string.Empty);

        // 5. Compute HMAC
        var algorithm = config.Algorithm.ToLowerInvariant();
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var computedBytes = algorithm switch
        {
            "hmac-sha256" => HMACSHA256.HashData(keyBytes, payloadBytes),
            "hmac-sha512" => HMACSHA512.HashData(keyBytes, payloadBytes),
            _ => throw new NotSupportedException($"Unsupported algorithm '{config.Algorithm}'."),
        };

        var computed = config.SignatureEncoding?.ToLowerInvariant() switch
        {
            "base64" => Convert.ToBase64String(computedBytes),
            "base64url" => Convert.ToBase64String(computedBytes)
                               .Replace('+', '-').Replace('/', '_').TrimEnd('='),
            "hex" or null => Convert.ToHexString(computedBytes).ToLowerInvariant(),
            _ => throw new NotSupportedException(
                                 $"Unsupported signatureEncoding '{config.SignatureEncoding}'."),
        };

        // hex is case-insensitive — normalize both sides before compare
        // base64 / base64url are case-sensitive — compare received value as-is
        var normalizedReceived = config.SignatureEncoding?.ToLowerInvariant() is "base64" or "base64url"
            ? receivedSignature
            : receivedSignature.ToLowerInvariant();

        // 6. Constant-time compare to prevent timing attacks
        var isValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(normalizedReceived));

        return new SignatureValidationResult
        {
            IsValid = isValid,
            AlgorithmUsed = algorithm,
            PayloadUsed = payload,
            ExtractedTimestamp = extractedTimestamp,
            ReceivedSignature = receivedSignature,
            ComputedSignature = computed,
        };
    }

    // Extracts a named token from a header value using a simple pattern.
    //
    // Pattern format: literal prefix + {tokenName}, e.g. "v1={signature}".
    // The header may contain multiple comma-separated segments (like Stripe's
    // "t=...,v1=..." format); we check each segment independently.
    //
    // Returns null if no segment matched.
    private static string? ExtractToken(string headerValue, string? pattern, string tokenName)
    {
        if (string.IsNullOrEmpty(pattern))
            return headerValue.Trim(); // no pattern: use the whole value as-is

        // Derive the literal prefix by stripping {tokenName} placeholder
        var placeholder = $"{{{tokenName}}}";
        var prefixIndex = pattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            // Treat the entire pattern as a regex capture group fallback
            var m = Regex.Match(headerValue, pattern);
            return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
        }

        var prefix = pattern[..prefixIndex];
        var suffix = pattern[(prefixIndex + placeholder.Length)..];

        return headerValue.Split(',')
            .Select(s => s.Trim())
            .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(t =>
            {
                var remaining = t[prefix.Length..];
                return !string.IsNullOrEmpty(suffix) && remaining.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    ? remaining[..^suffix.Length]
                    : remaining;
            })
            .FirstOrDefault();
    }
}
