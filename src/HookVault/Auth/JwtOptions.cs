using System.Text;
using Microsoft.Extensions.Configuration;

namespace HookVault.Auth;

public sealed record JwtOptions(string Secret, string Issuer, string Audience)
{
    public const int MinimumSecretBytes = 48;

    public static JwtOptions FromConfiguration(IConfiguration config)
    {
        var secret = config["HOOKVAULT_JWT_SECRET"] ?? config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                "HOOKVAULT_JWT_SECRET must be set (min 48 bytes) before HookVault can start.");
        }

        if (Encoding.UTF8.GetByteCount(secret) < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                $"HOOKVAULT_JWT_SECRET must be at least {MinimumSecretBytes} bytes (UTF-8).");
        }

        var issuer = config["HOOKVAULT_JWT_ISSUER"] ?? config["Jwt:Issuer"] ?? "hookvault";
        var audience = config["HOOKVAULT_JWT_AUDIENCE"] ?? config["Jwt:Audience"] ?? "hookvault";

        return new JwtOptions(secret, issuer, audience);
    }
}
