using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HookVault.Tests;

// .NET xUnit pattern: [Theory] + [InlineData] = parameterised test (like @pytest.mark.parametrize).
// [Fact] = single non-parameterised test.
public class SignatureValidatorTests
{
    private static SignatureValidator BuildValidator() => new();

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string HmacSha256Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    private static string HmacSha512Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToHexString(HMACSHA512.HashData(key, data)).ToLowerInvariant();
    }

    private static string HmacSha256Base64(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(HMACSHA256.HashData(key, data));
    }

    private static string HmacSha256Base64Url(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(HMACSHA256.HashData(key, data))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ------------------------------------------------------------------ helpers

    private static IHeaderDictionary MakeHeaders(params (string Key, string Value)[] pairs)
    {
        var h = new HeaderDictionary();
        foreach (var (k, v) in pairs)
            h[k] = v;
        return h;
    }

    // ------------------------------------------------------------------ Stripe-style
    // Header: "t=<timestamp>,v1=<hex>"
    // Payload: "<timestamp>.<body>"

    [Fact]
    public void Stripe_style_valid_signature_passes()
    {
        const string secret = "whsec_test";
        const string body = """{"type":"payment_intent.created"}""";
        const string timestamp = "1714000000";
        var payload = $"{timestamp}.{body}";
        var sig = HmacSha256Hex(secret, payload);

        Environment.SetEnvironmentVariable("TEST_STRIPE_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_STRIPE_SECRET",
            SignatureHeader = "Stripe-Signature",
            PayloadFormat = "{timestamp}.{body}",
            SignaturePattern = "v1={signature}",
            TimestampPattern = "t={timestamp}",
        };

        var headers = MakeHeaders(("Stripe-Signature", $"t={timestamp},v1={sig}"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
        Assert.Equal(timestamp, result.ExtractedTimestamp);
    }

    [Fact]
    public void Stripe_style_wrong_signature_fails()
    {
        const string secret = "whsec_test";
        const string body = """{"type":"payment_intent.created"}""";
        const string timestamp = "1714000000";

        Environment.SetEnvironmentVariable("TEST_STRIPE_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_STRIPE_SECRET",
            SignatureHeader = "Stripe-Signature",
            PayloadFormat = "{timestamp}.{body}",
            SignaturePattern = "v1={signature}",
            TimestampPattern = "t={timestamp}",
        };

        var headers = MakeHeaders(("Stripe-Signature", $"t={timestamp},v1=badhex"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.False(result.IsValid);
        Assert.Null(result.Error); // error is null when it's a mismatch (vs a setup problem)
    }

    // ------------------------------------------------------------------ GitHub-style
    // Header: "sha256=<hex>"
    // Payload: raw body only

    [Fact]
    public void GitHub_style_valid_signature_passes()
    {
        const string secret = "github_secret";
        const string body = """{"action":"opened"}""";
        var sig = HmacSha256Hex(secret, body);

        Environment.SetEnvironmentVariable("TEST_GITHUB_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_GITHUB_SECRET",
            SignatureHeader = "X-Hub-Signature-256",
            PayloadFormat = "{body}",
            SignaturePattern = "sha256={signature}",
            TimestampPattern = null,
        };

        var headers = MakeHeaders(("X-Hub-Signature-256", $"sha256={sig}"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
        Assert.Null(result.ExtractedTimestamp);
    }

    // ------------------------------------------------------------------ hmac-sha512

    [Fact]
    public void Hmac_sha512_valid_signature_passes()
    {
        const string secret = "secret512";
        const string body = "hello world";
        var sig = HmacSha512Hex(secret, body);

        Environment.SetEnvironmentVariable("TEST_SHA512_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha512",
            SecretEnvVar = "TEST_SHA512_SECRET",
            SignatureHeader = "X-Signature",
            PayloadFormat = "{body}",
            SignaturePattern = null, // whole header is the signature
        };

        var headers = MakeHeaders(("X-Signature", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal("hmac-sha512", result.AlgorithmUsed);
    }

    // ------------------------------------------------------------------ error cases

    [Fact]
    public void Missing_env_var_returns_error()
    {
        Environment.SetEnvironmentVariable("TEST_MISSING_SECRET", null);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_MISSING_SECRET",
            SignatureHeader = "X-Sig",
            PayloadFormat = "{body}",
        };

        var headers = MakeHeaders(("X-Sig", "abc"));
        var result = BuildValidator().Validate(config, Utf8("body"), headers);

        Assert.False(result.IsValid);
        Assert.Contains("TEST_MISSING_SECRET", result.Error);
    }

    [Fact]
    public void Missing_header_returns_error()
    {
        Environment.SetEnvironmentVariable("TEST_PRESENT_SECRET", "s3cr3t");

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_PRESENT_SECRET",
            SignatureHeader = "X-Missing-Header",
            PayloadFormat = "{body}",
        };

        var result = BuildValidator().Validate(config, Utf8("body"), MakeHeaders());

        Assert.False(result.IsValid);
        Assert.Contains("X-Missing-Header", result.Error);
    }

    [Fact]
    public void Unparseable_signature_pattern_returns_error()
    {
        const string secret = "s";
        Environment.SetEnvironmentVariable("TEST_PARSE_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_PARSE_SECRET",
            SignatureHeader = "X-Sig",
            PayloadFormat = "{body}",
            SignaturePattern = "v1={signature}", // header doesn't have "v1=" prefix
        };

        var headers = MakeHeaders(("X-Sig", "noprefixhere"));
        var result = BuildValidator().Validate(config, Utf8("body"), headers);

        Assert.False(result.IsValid);
        Assert.Contains("Could not extract signature", result.Error);
    }

    [Fact]
    public void Validation_details_contain_computed_and_received_signature_on_failure()
    {
        const string secret = "s3cr3t";
        const string body = "test";
        Environment.SetEnvironmentVariable("TEST_DETAIL_SECRET", secret);

        var correct = HmacSha256Hex(secret, body);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_DETAIL_SECRET",
            SignatureHeader = "X-Sig",
            PayloadFormat = "{body}",
        };

        var headers = MakeHeaders(("X-Sig", "wrongsig"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.False(result.IsValid);
        Assert.Equal(correct, result.ComputedSignature);
        Assert.Equal("wrongsig", result.ReceivedSignature);
        Assert.Equal(body, result.PayloadUsed);
    }

    // ------------------------------------------------------------------ base64 encoding (Shopify-style)

    [Fact]
    public void Base64_encoding_valid_shopify_style_signature_passes()
    {
        const string secret = "shopify_secret";
        const string body = """{"id":1,"topic":"orders/create"}""";
        var sig = HmacSha256Base64(secret, body);

        Environment.SetEnvironmentVariable("TEST_SHOPIFY_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_SHOPIFY_SECRET",
            SignatureHeader = "X-Shopify-Hmac-SHA256",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64",
        };

        var headers = MakeHeaders(("X-Shopify-Hmac-SHA256", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
    }

    [Fact]
    public void Base64_encoding_wrong_signature_fails()
    {
        const string secret = "shopify_secret";
        const string body = """{"id":1}""";

        Environment.SetEnvironmentVariable("TEST_SHOPIFY_FAIL_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_SHOPIFY_FAIL_SECRET",
            SignatureHeader = "X-Shopify-Hmac-SHA256",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64",
        };

        var headers = MakeHeaders(("X-Shopify-Hmac-SHA256", "bm90YXZhbGlkc2ln"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.False(result.IsValid);
        Assert.Null(result.Error);
    }

    // ------------------------------------------------------------------ base64url encoding

    [Fact]
    public void Base64url_encoding_valid_signature_passes()
    {
        const string secret = "url_safe_secret";
        const string body = "test payload";
        var sig = HmacSha256Base64Url(secret, body);

        Environment.SetEnvironmentVariable("TEST_B64URL_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_B64URL_SECRET",
            SignatureHeader = "X-Signature",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64url",
        };

        var headers = MakeHeaders(("X-Signature", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
        Assert.DoesNotContain("=", result.ComputedSignature!);
        Assert.DoesNotContain("+", result.ComputedSignature);
        Assert.DoesNotContain("/", result.ComputedSignature);
    }

    // ------------------------------------------------------------------ hex default (backward compat)

    [Fact]
    public void Null_signatureEncoding_defaults_to_hex()
    {
        const string secret = "hex_default";
        const string body = "test";
        var sig = HmacSha256Hex(secret, body);

        Environment.SetEnvironmentVariable("TEST_HEX_DEFAULT_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_HEX_DEFAULT_SECRET",
            SignatureHeader = "X-Sig",
            PayloadFormat = "{body}",
            SignatureEncoding = null,
        };

        var headers = MakeHeaders(("X-Sig", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
    }
}
