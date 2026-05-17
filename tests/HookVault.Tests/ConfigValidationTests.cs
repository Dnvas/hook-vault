using HookVault.Configuration;
using Microsoft.Extensions.Logging;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class ConfigValidationTests : IDisposable
{
    private readonly string _configPath = Path.GetTempFileName();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }

    private void WriteConfig(string body)
    {
        File.WriteAllText(_configPath, body);
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configPath);
    }

    private static ILogger NullLogger() => LoggerFactory.Create(_ => { }).CreateLogger("test");

    [Fact]
    public void Validate_UnknownAlgorithm_Throws()
    {
        WriteConfig("""
            {
              "providers": [
                {
                  "name": "p1",
                  "path": "/p1",
                  "forwardUrl": "http://localhost",
                  "validation": {
                    "algorithm": "md5",
                    "secretEnvVar": "X",
                    "signatureHeader": "X-Sig"
                  }
                }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(NullLogger()));
        Assert.Contains("md5", ex.Message);
        Assert.Contains("not supported", ex.Message);
    }

    [Theory]
    [InlineData("hmac-sha1")]
    [InlineData("hmac-sha256")]
    [InlineData("hmac-sha512")]
    [InlineData("HMAC-SHA256")]
    public void Validate_KnownAlgorithm_Succeeds(string algorithm)
    {
        WriteConfig($$"""
            {
              "providers": [
                {
                  "name": "p1",
                  "path": "/p1",
                  "forwardUrl": "http://localhost",
                  "validation": {
                    "algorithm": "{{algorithm}}",
                    "secretEnvVar": "X",
                    "signatureHeader": "X-Sig"
                  }
                }
              ]
            }
            """);

        var options = HookVaultOptions.Load(NullLogger());
        Assert.Single(options.Providers);
    }

    [Fact]
    public void Validate_MissingSignatureHeader_Throws()
    {
        WriteConfig("""
            {
              "providers": [
                {
                  "name": "p1",
                  "path": "/p1",
                  "forwardUrl": "http://localhost",
                  "validation": {
                    "algorithm": "hmac-sha256",
                    "secretEnvVar": "X",
                    "signatureHeader": ""
                  }
                }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(NullLogger()));
        Assert.Contains("signatureHeader", ex.Message);
    }

    [Fact]
    public void Validate_MissingSecretEnvVar_Throws()
    {
        WriteConfig("""
            {
              "providers": [
                {
                  "name": "p1",
                  "path": "/p1",
                  "forwardUrl": "http://localhost",
                  "validation": {
                    "algorithm": "hmac-sha256",
                    "secretEnvVar": "",
                    "signatureHeader": "X-Sig"
                  }
                }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(NullLogger()));
        Assert.Contains("secretEnvVar", ex.Message);
    }

    [Fact]
    public void Validate_NullValidation_Succeeds()
    {
        WriteConfig("""
            {
              "providers": [
                { "name": "p1", "path": "/p1", "forwardUrl": "http://localhost", "validation": null }
              ]
            }
            """);

        var options = HookVaultOptions.Load(NullLogger());
        Assert.Single(options.Providers);
        Assert.Null(options.Providers[0].Validation);
    }
}
