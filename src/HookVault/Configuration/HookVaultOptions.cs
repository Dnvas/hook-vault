using System.Text.Json;

namespace HookVault.Configuration;

public sealed class HookVaultOptions
{
    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "hmac-sha1",
        "hmac-sha256",
        "hmac-sha512",
    };

    public IReadOnlyList<ProviderConfig> Providers { get; init; } = [];

    public static HookVaultOptions Load(ILogger logger)
    {
        var configPath = ResolveConfigPath();
        logger.LogInformation("Loading HookVault config from {Path}", configPath);

        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                $"HookVault config not found at '{configPath}'. " +
                "Mount a hookvault.json file or set HOOKVAULT_CONFIG_PATH.", configPath);

        var json = File.ReadAllText(configPath);
        HookVaultOptions? options;
        try
        {
            options = JsonSerializer.Deserialize<HookVaultOptions>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse HookVault config '{configPath}' at line {ex.LineNumber + 1}, " +
                $"column {ex.BytePositionInLine + 1}: {ex.Message}", ex);
        }

        if (options is null)
            throw new InvalidOperationException(
                $"HookVault config '{configPath}' deserialized to null.");

        Validate(options);
        logger.LogInformation("Loaded {Count} provider(s): {Names}",
            options.Providers.Count,
            string.Join(", ", options.Providers.Select(p => p.Name)));

        return options;
    }

    private static string ResolveConfigPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HOOKVAULT_CONFIG_PATH");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        if (File.Exists("/app/config/hookvault.json")) return "/app/config/hookvault.json";
        if (File.Exists("hookvault.json")) return "hookvault.json";
        return "/app/config/hookvault.json"; // canonical Docker path — will produce a clear error
    }

    private static void Validate(HookVaultOptions opts)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in opts.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new InvalidOperationException("A provider is missing a 'name'.");
            if (string.IsNullOrWhiteSpace(p.Path))
                throw new InvalidOperationException($"Provider '{p.Name}' is missing a 'path'.");
            if (!p.CaptureOnly && string.IsNullOrWhiteSpace(p.ForwardUrl))
                throw new InvalidOperationException(
                    $"Provider '{p.Name}' is missing a 'forwardUrl' " +
                    "(required unless 'captureOnly' is true).");
            if (!paths.Add(p.Path))
                throw new InvalidOperationException($"Duplicate provider path '{p.Path}'.");

            if (p.Validation is { } v)
            {
                if (string.IsNullOrWhiteSpace(v.Algorithm) || !AllowedAlgorithms.Contains(v.Algorithm))
                    throw new InvalidOperationException(
                        $"Provider '{p.Name}': validation.algorithm '{v.Algorithm}' is not supported. " +
                        "Use one of: hmac-sha1, hmac-sha256, hmac-sha512.");
                if (string.IsNullOrWhiteSpace(v.SignatureHeader))
                    throw new InvalidOperationException(
                        $"Provider '{p.Name}': validation.signatureHeader is required.");
                if (string.IsNullOrWhiteSpace(v.SecretEnvVar))
                    throw new InvalidOperationException(
                        $"Provider '{p.Name}': validation.secretEnvVar is required.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
