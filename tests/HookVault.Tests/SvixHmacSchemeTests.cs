using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Services.Schemes;
using Microsoft.AspNetCore.Http;

namespace HookVault.Tests;

public sealed class SvixHmacSchemeTests
{
    private const string SecretEnv = "SVIX_TEST_SECRET";

    public SvixHmacSchemeTests()
    {
        Environment.SetEnvironmentVariable(SecretEnv, "whsec_dGVzdC1zZWNyZXQ=");
    }

    [Fact]
    public void Validate_ValidSignature_Returns_Valid()
    {
        var (cfg, headers, body) = BuildRequest("msg_1", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), tamper: false);
        var scheme = new SvixHmacScheme();
        var result = scheme.Validate(cfg, body, headers);
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void Validate_TamperedBody_Returns_Invalid()
    {
        var (cfg, headers, _) = BuildRequest("msg_2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), tamper: false);
        var tampered = Encoding.UTF8.GetBytes("""{"hello":"changed"}""");
        var scheme = new SvixHmacScheme();
        var result = scheme.Validate(cfg, tampered, headers);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MissingSignatureHeader_Returns_Invalid()
    {
        var (cfg, headers, body) = BuildRequest("msg_3", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), tamper: false);
        headers.Remove("svix-signature");
        var scheme = new SvixHmacScheme();
        var result = scheme.Validate(cfg, body, headers);
        Assert.False(result.IsValid);
        Assert.Contains("svix-signature", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MultipleSignatures_AcceptsAnyMatch()
    {
        // Svix delivers space-separated signatures for key-rotation windows.
        var (cfg, headers, body) = BuildRequest("msg_4", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), tamper: false);
        var realSig = headers["svix-signature"].ToString();
        headers["svix-signature"] = $"v1,bogusbogusbogusbogusbogusbogusbogusbogusbogu= {realSig}";
        var scheme = new SvixHmacScheme();
        var result = scheme.Validate(cfg, body, headers);
        Assert.True(result.IsValid, result.Error);
    }

    private static (ValidationConfig, HeaderDictionary, byte[]) BuildRequest(string id, long ts, bool tamper)
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var bodyForSign = tamper ? Encoding.UTF8.GetBytes("""{"hello":"different"}""") : body;
        var payload = $"{id}.{ts}.{Encoding.UTF8.GetString(bodyForSign)}";

        // Svix secret has the form "whsec_<base64-key>" — split off the prefix and base64-decode.
        var rawSecret = Environment.GetEnvironmentVariable(SecretEnv)!;
        var keyB64 = rawSecret.StartsWith("whsec_", StringComparison.Ordinal) ? rawSecret[6..] : rawSecret;
        var key = Convert.FromBase64String(keyB64);

        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        var sigB64 = Convert.ToBase64String(mac);

        var headers = new HeaderDictionary
        {
            ["svix-id"] = id,
            ["svix-timestamp"] = ts.ToString(),
            ["svix-signature"] = $"v1,{sigB64}",
        };

        var cfg = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = SecretEnv,
            Scheme = "svix",
        };
        return (cfg, headers, body);
    }
}
