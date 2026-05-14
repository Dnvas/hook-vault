using System.IdentityModel.Tokens.Jwt;
using System.Text;
using HookVault.Auth;
using HookVault.Cli;
using Microsoft.IdentityModel.Tokens;

namespace HookVault.Tests;

[Collection("EnvVarMutation")] // serialises because we mutate env vars
public sealed class GenerateTokenCommandTests : IDisposable
{
    private const string SecretEnv = "HOOKVAULT_JWT_SECRET";
    private const string IssuerEnv = "HOOKVAULT_JWT_ISSUER";
    private const string AudienceEnv = "HOOKVAULT_JWT_AUDIENCE";

    private readonly string? _origSecret = Environment.GetEnvironmentVariable(SecretEnv);
    private readonly string? _origIssuer = Environment.GetEnvironmentVariable(IssuerEnv);
    private readonly string? _origAudience = Environment.GetEnvironmentVariable(AudienceEnv);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SecretEnv, _origSecret);
        Environment.SetEnvironmentVariable(IssuerEnv, _origIssuer);
        Environment.SetEnvironmentVariable(AudienceEnv, _origAudience);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = GenerateTokenCommand.Run(args, stdout, stderr);
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    [Fact]
    public void Missing_secret_exits_1_with_stderr_message()
    {
        Environment.SetEnvironmentVariable(SecretEnv, null);

        var (code, stdout, stderr) = Run();

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("HOOKVAULT_JWT_SECRET", stderr);
    }

    [Fact]
    public void Default_args_mint_admin_token_with_30day_expiry()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var before = DateTime.UtcNow;
        var (code, stdout, stderr) = Run();

        Assert.Equal(0, code);
        Assert.Empty(stderr);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(stdout);
        Assert.Equal("admin", jwt.Subject);
        Assert.InRange(jwt.ValidTo, before.AddDays(29), before.AddDays(31));
    }

    [Fact]
    public void Subject_and_expires_flags_are_respected()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var before = DateTime.UtcNow;
        var (code, stdout, _) = Run("--subject", "ci", "--expires", "1h");

        Assert.Equal(0, code);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(stdout);
        Assert.Equal("ci", jwt.Subject);
        Assert.InRange(jwt.ValidTo, before.AddMinutes(59), before.AddMinutes(61));
    }

    [Fact]
    public void Bad_expires_format_exits_1()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));

        var (code, _, stderr) = Run("--expires", "frog");

        Assert.Equal(1, code);
        Assert.Contains("--expires", stderr);
    }

    [Fact]
    public void Generated_token_validates_against_runtime_parameters()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var (_, token, _) = Run();

        // First, verify the token is parseable and contains the expected subject
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal("admin", jwt.Subject);

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "hookvault",
            ValidAudience = "hookvault",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('s', 32))),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var principal = handler.ValidateToken(token, parameters, out _);
        // The subject is available via the standard Principal property
        Assert.NotNull(principal);
    }
}
