using HookVault.Auth;

namespace HookVault.Cli;

public static class GenerateTokenCommand
{
    public static int Run(string[] args) =>
        Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string subject = "admin";
        TimeSpan lifetime = TimeSpan.FromDays(30);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--subject":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("--subject requires a value.");
                        return 1;
                    }
                    subject = args[++i];
                    break;
                case "--expires":
                    if (i + 1 >= args.Length || !TryParseDuration(args[++i], out lifetime))
                    {
                        stderr.WriteLine("--expires must be like 1h, 7d, 30d.");
                        return 1;
                    }
                    break;
                default:
                    stderr.WriteLine($"Unknown argument: {args[i]}");
                    return 1;
            }
        }

        var secret = Environment.GetEnvironmentVariable("HOOKVAULT_JWT_SECRET");
        if (string.IsNullOrEmpty(secret))
        {
            stderr.WriteLine("HOOKVAULT_JWT_SECRET must be set to mint a token.");
            return 1;
        }

        try
        {
            var issuer = Environment.GetEnvironmentVariable("HOOKVAULT_JWT_ISSUER") ?? "hookvault";
            var audience = Environment.GetEnvironmentVariable("HOOKVAULT_JWT_AUDIENCE") ?? "hookvault";

            if (System.Text.Encoding.UTF8.GetByteCount(secret) < JwtOptions.MinimumSecretBytes)
            {
                stderr.WriteLine($"HOOKVAULT_JWT_SECRET must be at least {JwtOptions.MinimumSecretBytes} bytes.");
                return 1;
            }

            var options = new JwtOptions(secret, issuer, audience);
            var token = JwtTokenGenerator.Mint(options, subject, lifetime);
            stdout.WriteLine(token);
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Failed to mint token: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        if (value.Length < 2) return false;

        var unit = value[^1];
        if (!int.TryParse(value[..^1], out var n) || n <= 0) return false;

        duration = unit switch
        {
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }
}
