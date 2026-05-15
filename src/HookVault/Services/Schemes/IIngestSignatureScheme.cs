using HookVault.Configuration;
using HookVault.Services;

namespace HookVault.Services.Schemes;

// One signature scheme. Implementations get the raw body, the request headers,
// and the provider's validation config, and return a structured result so the
// caller can persist debug detail.
public interface IIngestSignatureScheme
{
    string Name { get; }

    SignatureValidationResult Validate(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers);
}
