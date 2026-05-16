using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Services.Schemes;
using Microsoft.AspNetCore.Http;

namespace HookVault.Tests;

public sealed class ReplayAttackWindowTests
{
    [Fact]
    public void Validate_FreshTimestamp_WithinWindow_Returns_Valid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var headers = SignedHeaders(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: 60);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.True(result.IsValid, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Validate_OldTimestamp_OutsideWindow_Returns_Invalid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.AddSeconds(-300).ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var headers = SignedHeaders(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: 60);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.False(result.IsValid);
            Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Validate_OldTimestamp_NoWindow_Returns_Valid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.AddSeconds(-3600).ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var headers = SignedHeaders(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: null);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.True(result.IsValid, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    private static IHeaderDictionary SignedHeaders(long ts, string body, string secret)
    {
        var payload = $"{ts}.{body}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={ts},v1={sig}",
        };
    }

    private static ValidationConfig StripeLikeConfig(int? maxAgeSeconds) => new()
    {
        Algorithm = "hmac-sha256",
        SecretEnvVar = "REPLAY_TEST_SECRET",
        SignatureHeader = "Stripe-Signature",
        PayloadFormat = "{timestamp}.{body}",
        SignatureEncoding = "hex",
        SignaturePattern = "v1={signature}",
        TimestampPattern = "t={timestamp}",
        MaxAgeSeconds = maxAgeSeconds,
    };
}
