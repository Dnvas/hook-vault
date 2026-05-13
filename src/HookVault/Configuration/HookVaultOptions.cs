using System.Text.Json;

namespace HookVault.Configuration;

public sealed class HookVaultOptions
{
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
        var options = JsonSerializer.Deserialize<HookVaultOptions>(json, JsonOptions)
            ?? throw new InvalidOperationException("Config file deserialized to null.");

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
            if (string.IsNullOrWhiteSpace(p.ForwardUrl))
                throw new InvalidOperationException($"Provider '{p.Name}' is missing a 'forwardUrl'.");
            if (!paths.Add(p.Path))
                throw new InvalidOperationException($"Duplicate provider path '{p.Path}'.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
