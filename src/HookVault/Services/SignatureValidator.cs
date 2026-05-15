using HookVault.Configuration;
using HookVault.Services.Schemes;

namespace HookVault.Services;

// Thin dispatcher: resolves an IIngestSignatureScheme from validation.scheme and
// delegates. Default scheme is "single-header" (matches every existing config).
public sealed class SignatureValidator(IEnumerable<IIngestSignatureScheme> schemes)
{
    private readonly IReadOnlyDictionary<string, IIngestSignatureScheme> _schemes =
        schemes.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public SignatureValidationResult Validate(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        var schemeName = string.IsNullOrEmpty(config.Scheme) ? "single-header" : config.Scheme;
        if (!_schemes.TryGetValue(schemeName, out var scheme))
            return SignatureValidationResult.Fail(
                $"Unknown signature scheme '{schemeName}'. Available: " +
                string.Join(", ", _schemes.Keys) + ".");

        return scheme.Validate(config, rawBody, headers);
    }
}
