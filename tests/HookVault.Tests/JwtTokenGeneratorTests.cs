using System.IdentityModel.Tokens.Jwt;
using System.Text;
using HookVault.Auth;
using Microsoft.IdentityModel.Tokens;

namespace HookVault.Tests;

public sealed class JwtTokenGeneratorTests
{
    private static readonly JwtOptions Options =
        new(new string('s', 32), "hookvault", "hookvault");

    [Fact]
    public void Mint_produces_validatable_token_with_expected_claims()
    {
        var token = JwtTokenGenerator.Mint(Options, subject: "admin", lifetime: TimeSpan.FromMinutes(30));

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.Secret)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        handler.ValidateToken(token, parameters, out _);

        Assert.Equal("admin", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("hookvault", jwt.Issuer);
        Assert.Contains("hookvault", jwt.Audiences);
    }

    [Fact]
    public void Mint_uses_hs256_signature()
    {
        var token = JwtTokenGenerator.Mint(Options, subject: "admin", lifetime: TimeSpan.FromMinutes(30));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Header.Alg);
    }

    [Fact]
    public void Mint_respects_lifetime()
    {
        var before = DateTime.UtcNow;
        var token = JwtTokenGenerator.Mint(Options, subject: "admin", lifetime: TimeSpan.FromHours(1));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        // Allow 60s of test-clock drift either side
        Assert.InRange(jwt.ValidTo, before.AddMinutes(59), before.AddMinutes(61));
    }
}
