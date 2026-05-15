# HookVault Phase 4 — Docker + Examples Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> Before doing anything else, invoke `Skill(hookvault-spec)` to load the product spec and `Skill(hookvault-conventions)` to load the code conventions. Do not write or review any code until both skills are loaded.

**Goal:** Package HookVault as a runnable Docker image with provider-agnostic compose files and example configs, and fix the HMAC validator to support base64 and base64url signature encodings (required for Shopify and similar providers).

**Architecture:** Six independent commits in order — validator encoding fix (TDD first, because the Shopify example depends on `SignatureEncoding` existing), Dockerfile + .dockerignore, two docker-compose files, five provider example configs, annotated root example, and a parallel CI job. Each commit is independently buildable and green.

**Tech Stack:** .NET 8 / ASP.NET Core, xUnit, Docker (multi-stage Alpine), Docker Compose v2, GitHub Actions (`docker/build-push-action@v6`)

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `src/HookVault/Configuration/ValidationConfig.cs` | Modify | Add nullable `SignatureEncoding` property |
| `src/HookVault/Services/SignatureValidator.cs` | Modify | Encoding switch replacing single hex line; case-sensitivity fix |
| `tests/HookVault.Tests/SignatureValidatorTests.cs` | Modify | base64 + base64url + null-default test cases |
| `Dockerfile` | Create | Multi-stage Alpine build, non-root `app` user |
| `.dockerignore` | Create | Exclude build artifacts and runtime data from build context |
| `docker-compose.yml` | Create | SQLite standalone — default developer path |
| `docker-compose.postgres.yml` | Create | PostgreSQL standalone — optional alternative |
| `examples/hookvault.stripe.json` | Create | Stripe HMAC-SHA256 hex, timestamp+body |
| `examples/hookvault.github.json` | Create | GitHub HMAC-SHA256 hex, body-only |
| `examples/hookvault.shopify.json` | Create | Shopify HMAC-SHA256 base64, body-only |
| `examples/hookvault.resend.json` | Create | Resend — no validation (Svix multi-header) |
| `examples/hookvault.generic-hmac.json` | Create | Annotated template for any HMAC provider |
| `hookvault.example.json` | Create | Multi-provider annotated starter at repo root |
| `.github/workflows/ci.yml` | Modify | Add parallel `docker-build` job |

---

### Task 1: Add `signatureEncoding` to validator (TDD)

`ValidationConfig` currently has no encoding field; `SignatureValidator` always
formats the computed HMAC as lowercase hex. Providers like Shopify send base64.
This task adds `SignatureEncoding` and updates the comparison logic.

**Files:**
- Modify: `src/HookVault/Configuration/ValidationConfig.cs`
- Modify: `src/HookVault/Services/SignatureValidator.cs`
- Modify: `tests/HookVault.Tests/SignatureValidatorTests.cs`

- [ ] **Step 1: Add two helper methods to `SignatureValidatorTests.cs`**

Insert after the `HmacSha512Hex` helper (after line 30, before the first test):

```csharp
    private static string HmacSha256Base64(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(HMACSHA256.HashData(key, data));
    }

    private static string HmacSha256Base64Url(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(HMACSHA256.HashData(key, data))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
```

- [ ] **Step 2: Add four failing tests to `SignatureValidatorTests.cs`**

Append before the final closing `}` of the class:

```csharp
    // ------------------------------------------------------------------ base64 encoding (Shopify-style)
    // Header: "<base64_digest>"  — entire header value is the signature
    // signatureEncoding: "base64"

    [Fact]
    public void Base64_encoding_valid_shopify_style_signature_passes()
    {
        const string secret = "shopify_secret";
        const string body = """{"id":1,"topic":"orders/create"}""";
        var sig = HmacSha256Base64(secret, body);

        Environment.SetEnvironmentVariable("TEST_SHOPIFY_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_SHOPIFY_SECRET",
            SignatureHeader = "X-Shopify-Hmac-SHA256",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64",
        };

        var headers = MakeHeaders(("X-Shopify-Hmac-SHA256", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
    }

    [Fact]
    public void Base64_encoding_wrong_signature_fails()
    {
        const string secret = "shopify_secret";
        const string body = """{"id":1}""";

        Environment.SetEnvironmentVariable("TEST_SHOPIFY_FAIL_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_SHOPIFY_FAIL_SECRET",
            SignatureHeader = "X-Shopify-Hmac-SHA256",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64",
        };

        // "bm90YXZhbGlkc2ln" decodes to "notavalidsig" — not the real HMAC
        var headers = MakeHeaders(("X-Shopify-Hmac-SHA256", "bm90YXZhbGlkc2ln"));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.False(result.IsValid);
        Assert.Null(result.Error);
    }

    // ------------------------------------------------------------------ base64url encoding

    [Fact]
    public void Base64url_encoding_valid_signature_passes()
    {
        const string secret = "url_safe_secret";
        const string body = "test payload";
        var sig = HmacSha256Base64Url(secret, body);

        Environment.SetEnvironmentVariable("TEST_B64URL_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_B64URL_SECRET",
            SignatureHeader = "X-Signature",
            PayloadFormat = "{body}",
            SignatureEncoding = "base64url",
        };

        var headers = MakeHeaders(("X-Signature", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
        Assert.Equal(sig, result.ComputedSignature);
        // base64url uses - and _ instead of + and /, no = padding
        Assert.DoesNotContain("=", result.ComputedSignature!);
        Assert.DoesNotContain("+", result.ComputedSignature);
        Assert.DoesNotContain("/", result.ComputedSignature);
    }

    // ------------------------------------------------------------------ hex default (backward compat)

    [Fact]
    public void Null_signatureEncoding_defaults_to_hex()
    {
        const string secret = "hex_default";
        const string body = "test";
        var sig = HmacSha256Hex(secret, body);

        Environment.SetEnvironmentVariable("TEST_HEX_DEFAULT_SECRET", secret);

        var config = new ValidationConfig
        {
            Algorithm = "hmac-sha256",
            SecretEnvVar = "TEST_HEX_DEFAULT_SECRET",
            SignatureHeader = "X-Sig",
            PayloadFormat = "{body}",
            SignatureEncoding = null,
        };

        var headers = MakeHeaders(("X-Sig", sig));
        var result = BuildValidator().Validate(config, Utf8(body), headers);

        Assert.True(result.IsValid);
    }
```

- [ ] **Step 3: Build to confirm compile failure**

```bash
dotnet build tests/HookVault.Tests --configuration Release
```

Expected output contains:
```
error CS1061: 'ValidationConfig' does not contain a definition for 'SignatureEncoding'
```

- [ ] **Step 4: Add `SignatureEncoding` to `ValidationConfig.cs`**

In `src/HookVault/Configuration/ValidationConfig.cs`, insert after the
`TimestampPattern` property and before the closing `}`:

```csharp
    [JsonPropertyName("signatureEncoding")]
    public string? SignatureEncoding { get; init; }
```

- [ ] **Step 5: Build and run new tests — expect three failures**

```bash
dotnet build tests/HookVault.Tests --configuration Release
dotnet test tests/HookVault.Tests --configuration Release --no-build \
  --filter "Base64_encoding_valid|Base64url_encoding|Null_signatureEncoding"
```

Expected: `Base64_encoding_valid_shopify_style_signature_passes`,
`Base64_encoding_wrong_signature_fails`, and `Base64url_encoding_valid_signature_passes`
fail (the validator still computes hex). `Null_signatureEncoding_defaults_to_hex`
passes already since null falls through to hex behavior after the property exists.

- [ ] **Step 6: Update `SignatureValidator.cs` — replace the comparison block**

In `src/HookVault/Services/SignatureValidator.cs`, replace the block starting
with `var computedHex =` through `ComputedSignature = computedHex,` (lines 80–94)
with:

```csharp
        var computed = config.SignatureEncoding?.ToLowerInvariant() switch
        {
            "base64"    => Convert.ToBase64String(computedBytes),
            "base64url" => Convert.ToBase64String(computedBytes)
                               .Replace('+', '-').Replace('/', '_').TrimEnd('='),
            _           => Convert.ToHexString(computedBytes).ToLowerInvariant(),
        };

        // hex is case-insensitive — normalize both sides before compare
        // base64 / base64url are case-sensitive — compare received value as-is
        var normalizedReceived = config.SignatureEncoding?.ToLowerInvariant() is "base64" or "base64url"
            ? receivedSignature
            : receivedSignature.ToLowerInvariant();

        // 6. Constant-time compare to prevent timing attacks
        var isValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(normalizedReceived));

        return new SignatureValidationResult
        {
            IsValid = isValid,
            AlgorithmUsed = algorithm,
            PayloadUsed = payload,
            ExtractedTimestamp = extractedTimestamp,
            ReceivedSignature = receivedSignature,
            ComputedSignature = computed,
        };
```

- [ ] **Step 7: Run all tests — expect all green**

```bash
dotnet test tests/HookVault.Tests --configuration Release --no-build
```

Expected: all tests pass including all pre-existing ones. The existing
`Validation_details_contain_computed_and_received_signature_on_failure` test
still passes because `SignatureEncoding` is null → hex default →
`computed` equals the old `computedHex`.

- [ ] **Step 8: Commit**

```bash
git add src/HookVault/Configuration/ValidationConfig.cs \
        src/HookVault/Services/SignatureValidator.cs \
        tests/HookVault.Tests/SignatureValidatorTests.cs
git commit -m "feat: add signatureEncoding to validator"
```

---

### Task 2: Dockerfile + `.dockerignore`

**Files:**
- Create: `Dockerfile`
- Create: `.dockerignore`

- [ ] **Step 1: Create `.dockerignore` at the repo root**

```
bin/
obj/
out/
TestResults/
*.db
*.db-shm
*.db-wal
.git/
.github/
.claude/
hookvault.json
.env
.env.*
```

- [ ] **Step 2: Create `Dockerfile` at the repo root**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore in a separate layer — only re-runs when .csproj changes
COPY src/HookVault/HookVault.csproj src/HookVault/
RUN dotnet restore src/HookVault/HookVault.csproj

COPY . .
RUN dotnet publish src/HookVault/HookVault.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app

# /data is the SQLite volume mount point — must be owned by app before USER switch
RUN mkdir -p /data && chown app:app /data

COPY --from=build /app/publish .

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "HookVault.dll"]
```

- [ ] **Step 3: Build the image**

```bash
docker build -t hookvault:dev .
```

Expected: build completes without error. Both stages complete. Final image is
tagged `hookvault:dev`.

- [ ] **Step 4: Verify the image runs as a non-root user**

```bash
docker run --rm hookvault:dev whoami
```

Expected: `app`

- [ ] **Step 5: Verify `generate-token` works inside the container**

The CLI intercept in `Program.cs` checks `args[0] == "generate-token"` before
spinning up the web host — extra args after the image name are passed straight
through as `args`.

```bash
docker run --rm \
  -e HOOKVAULT_JWT_SECRET=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff \
  hookvault:dev generate-token --subject test --expires 1h
```

Expected: a JWT string on stdout, nothing else. Exit 0.

- [ ] **Step 6: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "feat: add dockerfile and dockerignore"
```

---

### Task 3: Docker Compose files

**Files:**
- Create: `docker-compose.yml`
- Create: `docker-compose.postgres.yml`

- [ ] **Step 1: Create `docker-compose.yml` at the repo root**

```yaml
services:
  hookvault:
    build: .
    ports:
      - "7777:8080"
    environment:
      # Generate a secret: openssl rand -hex 32
      # Mint a token:      docker compose run --rm hookvault generate-token
      HOOKVAULT_JWT_SECRET: ${HOOKVAULT_JWT_SECRET:?Set HOOKVAULT_JWT_SECRET in a .env file}
      HOOKVAULT_JWT_ISSUER: ${HOOKVAULT_JWT_ISSUER:-hookvault}
      HOOKVAULT_JWT_AUDIENCE: ${HOOKVAULT_JWT_AUDIENCE:-hookvault}
      # Uncomment to enable Swagger UI at /swagger
      # ASPNETCORE_ENVIRONMENT: Development
    volumes:
      - ./hookvault.json:/app/config/hookvault.json:ro
      - hookvault-data:/data
    restart: unless-stopped

volumes:
  hookvault-data:
```

- [ ] **Step 2: Validate `docker-compose.yml` syntax**

```bash
HOOKVAULT_JWT_SECRET=dummy docker compose config
```

Expected: Docker Compose prints the fully-interpolated config without error.
`${VAR:?error}` is satisfied by the inline env var.

- [ ] **Step 3: Create `docker-compose.postgres.yml` at the repo root**

This is a complete standalone file — not a layered override.
Run with: `docker compose -f docker-compose.postgres.yml up`

```yaml
services:
  hookvault:
    build: .
    ports:
      - "7777:8080"
    environment:
      # Generate a secret: openssl rand -hex 32
      # Mint a token: docker compose -f docker-compose.postgres.yml run --rm hookvault generate-token
      HOOKVAULT_JWT_SECRET: ${HOOKVAULT_JWT_SECRET:?Set HOOKVAULT_JWT_SECRET in a .env file}
      HOOKVAULT_JWT_ISSUER: ${HOOKVAULT_JWT_ISSUER:-hookvault}
      HOOKVAULT_JWT_AUDIENCE: ${HOOKVAULT_JWT_AUDIENCE:-hookvault}
      DATABASE_URL: postgresql://hookvault:${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in a .env file}@db:5432/hookvault
    volumes:
      - ./hookvault.json:/app/config/hookvault.json:ro
    depends_on:
      db:
        condition: service_healthy
    restart: unless-stopped

  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: hookvault
      POSTGRES_USER: hookvault
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in a .env file}
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U hookvault"]
      interval: 5s
      timeout: 5s
      retries: 5
    restart: unless-stopped

volumes:
  postgres-data:
```

- [ ] **Step 4: Validate `docker-compose.postgres.yml` syntax**

```bash
HOOKVAULT_JWT_SECRET=dummy POSTGRES_PASSWORD=dummy \
  docker compose -f docker-compose.postgres.yml config
```

Expected: prints interpolated config without error.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml docker-compose.postgres.yml
git commit -m "feat: add docker compose files"
```

---

### Task 4: Provider example configs

**Files:**
- Create: `examples/hookvault.stripe.json`
- Create: `examples/hookvault.github.json`
- Create: `examples/hookvault.shopify.json`
- Create: `examples/hookvault.resend.json`
- Create: `examples/hookvault.generic-hmac.json`

The config loader uses `ReadCommentHandling = JsonCommentHandling.Skip` and
`AllowTrailingCommas = true`, so `//` comments are valid in all these files.

- [ ] **Step 1: Create `examples/hookvault.stripe.json`**

Stripe embeds timestamp and signature in one header: `t=<ts>,v1=<hex>`.
Signed payload is `<timestamp>.<raw_body>`.

```json
// Stripe webhook — HMAC-SHA256, timestamp prepended to body.
// Stripe-Signature: t=<unix_ts>,v1=<hex_digest>
// Stripe signs "<timestamp>.<raw_body>" with the webhook signing secret.
{
  "providers": [
    {
      "name": "stripe",
      "path": "/stripe",
      // Point forwardUrl at your local webhook handler.
      "forwardUrl": "http://host.docker.internal:3000/webhooks/stripe",
      "validation": {
        "algorithm": "hmac-sha256",
        // Set STRIPE_WEBHOOK_SECRET to the signing secret from the Stripe dashboard.
        "secretEnvVar": "STRIPE_WEBHOOK_SECRET",
        "signatureHeader": "Stripe-Signature",
        // Stripe signs "<timestamp>.<raw_body>".
        "payloadFormat": "{timestamp}.{body}",
        "signatureEncoding": "hex",
        // The header is "t=<ts>,v1=<hex>" — extract the hex digest after "v1=".
        "signaturePattern": "v1={signature}",
        // Extract the Unix timestamp after "t=".
        "timestampPattern": "t={timestamp}"
      }
    }
  ]
}
```

- [ ] **Step 2: Create `examples/hookvault.github.json`**

GitHub signs the raw body only. The header is `sha256=<hex>`.

```json
// GitHub webhook — HMAC-SHA256 over the raw body.
// X-Hub-Signature-256: sha256=<hex_digest>
{
  "providers": [
    {
      "name": "github",
      "path": "/github",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/github",
      "validation": {
        "algorithm": "hmac-sha256",
        // Set GITHUB_WEBHOOK_SECRET to the secret configured in GitHub settings.
        "secretEnvVar": "GITHUB_WEBHOOK_SECRET",
        "signatureHeader": "X-Hub-Signature-256",
        // GitHub signs just the raw body — no timestamp in the payload.
        "payloadFormat": "{body}",
        "signatureEncoding": "hex",
        // Strip the "sha256=" prefix to extract the hex digest.
        "signaturePattern": "sha256={signature}"
      }
    }
  ]
}
```

- [ ] **Step 3: Create `examples/hookvault.shopify.json`**

Shopify sends the entire header value as a base64-encoded digest (no prefix).
Requires `signatureEncoding: "base64"` added in Task 1.

```json
// Shopify webhook — HMAC-SHA256 over the raw body, base64-encoded.
// X-Shopify-Hmac-SHA256: <base64_digest>
// The entire header value is the signature — no prefix to strip.
{
  "providers": [
    {
      "name": "shopify",
      "path": "/shopify",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/shopify",
      "validation": {
        "algorithm": "hmac-sha256",
        // Set SHOPIFY_WEBHOOK_SECRET to your Shopify webhook signing secret.
        "secretEnvVar": "SHOPIFY_WEBHOOK_SECRET",
        "signatureHeader": "X-Shopify-Hmac-SHA256",
        // Shopify signs just the raw body.
        "payloadFormat": "{body}",
        // Shopify encodes the digest as standard base64, not hex.
        "signatureEncoding": "base64"
        // signaturePattern omitted: the entire header value is the signature.
      }
    }
  ]
}
```

- [ ] **Step 4: Create `examples/hookvault.resend.json`**

Resend uses Svix, which requires three separate headers to construct its signed
payload. This cannot be expressed in HookVault's single-header schema.

```json
// Resend webhook — delivered via Svix.
//
// Svix constructs its signed payload from three separate headers:
//   svix-id:        unique message identifier
//   svix-timestamp: Unix timestamp
//   svix-signature: v1,<base64_sig1> v1,<base64_sig2>  (multiple, space-separated)
//
// Because the timestamp and message ID live in different headers from the
// signature, Svix cannot be expressed in HookVault's single-header validation
// schema. Events are captured and forwarded; signatures are not verified.
{
  "providers": [
    {
      "name": "resend",
      "path": "/resend",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/resend",
      "validation": null
    }
  ]
}
```

- [ ] **Step 5: Create `examples/hookvault.generic-hmac.json`**

```json
// Generic HMAC template — copy and adapt for any HMAC-based webhook provider.
//
// Supported algorithms:  "hmac-sha256" | "hmac-sha512"
// Supported encodings:   "hex" (default) | "base64" | "base64url"
//
// Common patterns:
//   hex with prefix:      signaturePattern "sha256={signature}" for header "sha256=abc123"
//   base64, no prefix:    omit signaturePattern; entire header value is the digest
//   timestamp in header:  set payloadFormat "{timestamp}.{body}", provide timestampPattern
{
  "providers": [
    {
      "name": "my-provider",
      "path": "/my-provider",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/my-provider",
      "validation": {
        // "hmac-sha256" or "hmac-sha512"
        "algorithm": "hmac-sha256",
        // Name of the environment variable holding the shared secret.
        // Never put the secret value here — only the variable name.
        "secretEnvVar": "MY_WEBHOOK_SECRET",
        // Header the provider uses to send the signature.
        "signatureHeader": "X-My-Signature",
        // How the signed string is constructed.
        // {body} = raw request body (UTF-8).
        // {timestamp} = value extracted via timestampPattern (if set).
        "payloadFormat": "{body}",
        // How the digest is encoded in the header value.
        // "hex"       = lowercase hex (default when omitted).
        // "base64"    = standard base64 with = padding.
        // "base64url" = URL-safe base64, no padding (- and _ instead of + and /).
        "signatureEncoding": "hex",
        // Pattern to extract the digest from the header.
        // Use {signature} as the placeholder. e.g. "sha256={signature}" strips "sha256=".
        // Omit or set null if the entire header value is the digest.
        "signaturePattern": null,
        // Pattern to extract a timestamp from the signature header.
        // e.g. "t={timestamp}" for a header like "t=1234,v1=abcd".
        // Omit or set null if the header contains no timestamp.
        "timestampPattern": null
      }
    }
  ]
}
```

- [ ] **Step 6: Run full test suite to confirm nothing regressed**

The example files contain no C# code — they do not affect compilation or
existing tests. This step confirms no accidental file changes were made.

```bash
dotnet test tests/HookVault.Tests --configuration Release --no-build
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add examples/
git commit -m "feat: add provider example configs"
```

---

### Task 5: `hookvault.example.json` at repo root

The annotated starter file users copy to `hookvault.json` in their project.
Note: `hookvault.json` is gitignored; `hookvault.example.json` is not.

**Files:**
- Create: `hookvault.example.json`

- [ ] **Step 1: Create `hookvault.example.json`**

```json
// Copy this file to hookvault.json in your project root and customise.
// Mount into the container: -v ./hookvault.json:/app/config/hookvault.json
//
// Provider-specific configs with detailed comments: see examples/
//
// Quick start:
//   1. Copy to hookvault.json and set your forwardUrl and secretEnvVar values.
//   2. Set the secret env vars in your .env file.
//   3. docker compose up
{
  "providers": [
    {
      // Unique label — appears in event listings and log output.
      "name": "stripe",
      // Ingest path: HookVault registers POST /api/ingest/stripe.
      // Point your Stripe dashboard webhook URL at http://localhost:7777/api/ingest/stripe.
      "path": "/stripe",
      // Where to forward the event in your local dev stack.
      // host.docker.internal resolves to the Docker host machine.
      "forwardUrl": "http://host.docker.internal:3000/webhooks/stripe",
      "validation": {
        // "hmac-sha256" or "hmac-sha512"
        "algorithm": "hmac-sha256",
        // Name of the env var holding your webhook secret — never the value itself.
        "secretEnvVar": "STRIPE_WEBHOOK_SECRET",
        // Header containing the provider's signature.
        "signatureHeader": "Stripe-Signature",
        // How the signed payload is constructed from {body} and optional {timestamp}.
        "payloadFormat": "{timestamp}.{body}",
        // How the digest is encoded: "hex" (default) | "base64" | "base64url"
        "signatureEncoding": "hex",
        // Pattern to extract the digest. "v1={signature}" matches "t=123,v1=abc" → "abc".
        "signaturePattern": "v1={signature}",
        // Pattern to extract the timestamp from the same header.
        "timestampPattern": "t={timestamp}"
      }
    },
    {
      // Set validation to null to capture and forward without verifying signatures.
      // Useful for providers whose signing scheme can't be expressed in config
      // (e.g. Resend/Svix, which uses three separate headers).
      "name": "resend",
      "path": "/resend",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/resend",
      "validation": null
    }
  ]
}
```

- [ ] **Step 2: Confirm `hookvault.example.json` is not gitignored**

```bash
git check-ignore -v hookvault.example.json
```

Expected: no output (file is not ignored). The `.gitignore` only excludes
`hookvault.json`, not `hookvault.example.json`.

- [ ] **Step 3: Commit**

```bash
git add hookvault.example.json
git commit -m "feat: add hookvault example config"
```

---

### Task 6: CI — parallel `docker-build` job

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add `docker-build` job to `.github/workflows/ci.yml`**

Add the following job at the same indentation level as `build-and-test`, after
the `build-and-test` job block. No `needs:` — it runs in parallel.

```yaml
  docker-build:
    name: Build Docker image
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Build image
        uses: docker/build-push-action@v6
        with:
          context: .
          push: false
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

The full updated `jobs:` section of `ci.yml` should look like:

```yaml
jobs:
  build-and-test:
    name: Build, test, and format check
    runs-on: ubuntu-latest
    # ... (existing steps unchanged)

  docker-build:
    name: Build Docker image
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Build image
        uses: docker/build-push-action@v6
        with:
          context: .
          push: false
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

- [ ] **Step 2: Validate CI YAML syntax**

```bash
# Check for YAML parse errors (requires Python or the GitHub CLI)
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))" \
  && echo "YAML valid" || echo "YAML invalid"
```

If Python is not available:

```bash
gh workflow list  # will fail loudly if YAML is malformed
```

- [ ] **Step 3: Run full test suite one final time**

```bash
dotnet test tests/HookVault.Tests --configuration Release --no-build
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add docker build job"
```

---

## Self-Review

### Spec coverage

| Spec requirement | Covered by |
|---|---|
| Multi-stage Dockerfile, SDK build / ASP.NET runtime final | Task 2 |
| Alpine base image | Task 2 |
| Non-root `app` user | Task 2 |
| Expose port 8080 | Task 2 |
| `/data/hookvault.db` SQLite default (via env) | Task 3 compose volumes |
| Mount config: `-v ./hookvault.json:/app/config/hookvault.json` | Task 3 compose volumes |
| `docker-compose.yml` standalone SQLite | Task 3 |
| `docker-compose.postgres.yml` PostgreSQL companion | Task 3 |
| 5 example provider configs in `/examples/` | Task 4 |
| `hookvault.example.json` at repo root with comments | Task 5 |
| CI: Build Docker image, no push | Task 6 |
| `signatureEncoding` field (hex / base64 / base64url) | Task 1 |

All requirements covered. No gaps.

### Placeholder scan

No TBD, TODO, or "similar to" references. All code blocks are complete.

### Type consistency

- `ValidationConfig.SignatureEncoding` (`string?`) added in Task 1, used in
  example JSON files in Tasks 4–5 — consistent spelling throughout.
- `config.SignatureEncoding?.ToLowerInvariant()` switch — consistent between
  Task 1 implementation and design doc.
- `ComputedSignature = computed` — replaces `computedHex` consistently.
