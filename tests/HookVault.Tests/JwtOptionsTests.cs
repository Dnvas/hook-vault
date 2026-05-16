using HookVault.Auth;
using Microsoft.Extensions.Configuration;

namespace HookVault.Tests;

public sealed class JwtOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_reads_hookvault_env_keys()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = new string('s', 48),
            ["HOOKVAULT_JWT_ISSUER"] = "iss",
            ["HOOKVAULT_JWT_AUDIENCE"] = "aud",
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal(new string('s', 48), options.Secret);
        Assert.Equal("iss", options.Issuer);
        Assert.Equal("aud", options.Audience);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_jwt_section()
    {
        var config = Config(new()
        {
            ["Jwt:Secret"] = new string('s', 48),
            ["Jwt:Issuer"] = "iss2",
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal("iss2", options.Issuer);
        Assert.Equal("hookvault", options.Audience); // default
    }

    [Fact]
    public void FromConfiguration_defaults_issuer_and_audience()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = new string('s', 48),
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal("hookvault", options.Issuer);
        Assert.Equal("hookvault", options.Audience);
    }

    [Fact]
    public void FromConfiguration_throws_when_secret_missing()
    {
        var config = Config([]);

        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(config));
        Assert.Contains("HOOKVAULT_JWT_SECRET", ex.Message);
    }

    [Fact]
    public void FromConfiguration_throws_when_secret_too_short()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = "tooshort",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(config));
        Assert.Contains("48 bytes", ex.Message);
    }

    [Fact]
    public void FromConfiguration_47ByteSecret_Throws()
    {
        var cfg = Config(new() { ["HOOKVAULT_JWT_SECRET"] = new string('x', 47) });
        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(cfg));
        Assert.Contains("48 bytes", ex.Message);
    }

    [Fact]
    public void FromConfiguration_48ByteSecret_Succeeds()
    {
        var cfg = Config(new() { ["HOOKVAULT_JWT_SECRET"] = new string('x', 48) });
        var opts = JwtOptions.FromConfiguration(cfg);
        Assert.NotNull(opts);
    }
}
