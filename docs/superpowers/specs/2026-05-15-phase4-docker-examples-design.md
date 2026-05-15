# Phase 4 — Docker + Examples

**Status:** approved (brainstorming)
**Date:** 2026-05-15
**Branch:** `feat/phase-4-docker-examples`
**Spec source:** [`hookvault-spec`](../../../.claude/skills/hookvault-spec/SKILL.md) §"Build order" Phase 4
**Conventions:** [`hookvault-conventions`](../../../.claude/skills/hookvault-conventions/SKILL.md)

## Goals

Package HookVault as a runnable Docker image and provide ready-to-use provider
configurations so developers can drop HookVault into any project's
`docker-compose` in under a minute.

Phase 4 also closes a gap in the generic HMAC validator: the current
implementation is hex-only, which silently breaks providers (Shopify, WooCommerce,
Typeform) that encode their HMAC digest as base64. Adding a `signatureEncoding`
field is small, backward-compatible, and directly enables Shopify as a working
validation example.

## Non-goals

- No image published to a registry (Docker Hub / GHCR). Publishing is Phase 5+.
- No multi-arch build (`linux/arm64`). The CI job verifies the image builds for
  `linux/amd64`. Multi-arch is deferred to when images are actually published.
- No UI, no README (Phase 5).
- No rate limiting, health-check tuning, or production hardening beyond the
  non-root user.

## signatureEncoding — validator enhancement

### Problem

`SignatureValidator` always formats the computed HMAC as lowercase hex
(`Convert.ToHexString(...).ToLowerInvariant()`). Providers that send a
base64-encoded digest (Shopify, WooCommerce, Typeform, Zapier) silently fail
validation — the computed hex never matches the received base64.

### Fix

Add an optional `signatureEncoding` field to `ValidationConfig` with three
accepted values:

| Value | Encoding | Providers |
|---|---|---|
| `"hex"` | lowercase hex (default) | GitHub, Stripe, Linear, Bitbucket |
| `"base64"` | standard base64 with `=` padding | Shopify, WooCommerce, Typeform, PayPal |
| `"base64url"` | URL-safe, no padding (`-`/`_`, no `=`) | newer APIs following JWT conventions |

Default is `"hex"` — existing configs without the field continue to work
unchanged.

### Implementation

`ValidationConfig.cs` — add one property:

```csharp
[JsonPropertyName("signatureEncoding")]
public string? SignatureEncoding { get; init; }
```

`SignatureValidator.cs` — replace the single hex line and comparison with:

```csharp
var computed = config.SignatureEncoding?.ToLowerInvariant() switch
{
    "base64"    => Convert.ToBase64String(computedBytes),
    "base64url" => Convert.ToBase64String(computedBytes)
                       .Replace('+', '-').Replace('/', '_').TrimEnd('='),
    _           => Convert.ToHexString(computedBytes).ToLowerInvariant()
};

// hex: case-insensitive — normalize both sides
// base64 / base64url: case-sensitive — never lowercase the received value
var normalizedReceived = config.SignatureEncoding?.ToLowerInvariant() is "base64" or "base64url"
    ? receivedSignature
    : receivedSignature.ToLowerInvariant();

var isValid = CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(computed),
    Encoding.UTF8.GetBytes(normalizedReceived));
```

`SignatureValidatorTests.cs` — add tests for:
- base64 encoding: correct Shopify-style signature validates; wrong signature fails
- base64url encoding: correct signature validates; padding stripped correctly
- hex default: existing tests continue to pass (backward-compat)

`SignatureValidationResult` — update `ComputedSignature` to store the encoded
form (not raw hex) so debug output is meaningful when encoding is base64.

## Dockerfile

Multi-stage build. SDK stage compiles and publishes; ASP.NET Alpine runtime
stage is the final image.

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

**Design decisions:**

- **Alpine** (`aspnet:8.0-alpine`): ~100MB final image. No ICU/globalization
  needed for a webhook tool. Shell (`sh`) available for debugging — important
  for an OSS developer tool.
- **Non-root `app` user**: pre-exists in Microsoft's Alpine image. `/data` is
  `chown`'d before the `USER` switch so SQLite can write at runtime.
- **Restore as a separate layer**: `COPY *.csproj` then `dotnet restore` before
  `COPY .` means the restore layer is cached on code-only changes.
- **`ENTRYPOINT` exec form**: `["dotnet", "HookVault.dll"]` passes extra args
  directly to the process, so `docker run hookvault generate-token --subject admin`
  works correctly via `Program.cs`'s CLI intercept.
- **Framework-dependent publish**: the ASP.NET runtime in the final image
  provides the runtime — no `--self-contained` needed.

`.dockerignore` — keeps the build context lean:

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

## docker-compose.yml (SQLite — default)

Standalone file. The primary path for all users.

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

**Port `7777:8080`**: `7777` is uncommon enough to avoid conflicts with typical
local dev servers (Spring Boot, nginx, webpack, etc.) while being memorable.

**`${VAR:?error}`**: Docker Compose v2 exits immediately with a clear message if
the variable is unset or empty — better than the app starting and crashing at
runtime with a cryptic JWT error.

**`hookvault-data` named volume**: SQLite database survives `docker compose down`.
Users who want to reset can `docker volume rm hookvault-data`.

## docker-compose.postgres.yml (PostgreSQL — optional)

Complete standalone file. Not a layered override — users run it with
`docker compose -f docker-compose.postgres.yml up` and need no knowledge of
override semantics.

```yaml
services:
  hookvault:
    build: .
    ports:
      - "7777:8080"
    environment:
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
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
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

**When to use PostgreSQL**: teams sharing a single HookVault instance on a dev
server (SQLite has write-contention with concurrent connections), or projects
that already run Postgres and want one engine across the stack.

## Provider examples (`/examples/`)

Five files. Pure JSON with `//` comments (the config loader sets
`ReadCommentHandling = JsonCommentHandling.Skip`).

### `examples/hookvault.stripe.json`

Stripe embeds the timestamp and signature in a single header, separated by
commas. The signed payload is `{timestamp}.{body}`.

```json
// Stripe-Signature: t=<unix_ts>,v1=<hex_digest>
// Stripe signs "<timestamp>.<raw_body>" with HMAC-SHA256.
{
  "providers": [{
    "name": "stripe",
    "path": "/stripe",
    "forwardUrl": "http://host.docker.internal:3000/webhooks/stripe",
    "validation": {
      "algorithm": "hmac-sha256",
      "secretEnvVar": "STRIPE_WEBHOOK_SECRET",
      "signatureHeader": "Stripe-Signature",
      "payloadFormat": "{timestamp}.{body}",
      "signatureEncoding": "hex",
      "signaturePattern": "v1={signature}",
      "timestampPattern": "t={timestamp}"
    }
  }]
}
```

**Tracing through `ExtractToken`:** header `"t=1492774577,v1=5257..."` splits
on `,`. `"v1={signature}"` matches the second segment and extracts `"5257..."`.
`"t={timestamp}"` matches the first segment and extracts `"1492774577"`. Payload
becomes `"1492774577.{rawBody}"`. ✅

### `examples/hookvault.github.json`

GitHub signs the raw body. The `sha256=` prefix is stripped by the pattern.

```json
// X-Hub-Signature-256: sha256=<hex_digest>
// GitHub signs the raw request body with HMAC-SHA256.
{
  "providers": [{
    "name": "github",
    "path": "/github",
    "forwardUrl": "http://host.docker.internal:3000/webhooks/github",
    "validation": {
      "algorithm": "hmac-sha256",
      "secretEnvVar": "GITHUB_WEBHOOK_SECRET",
      "signatureHeader": "X-Hub-Signature-256",
      "payloadFormat": "{body}",
      "signatureEncoding": "hex",
      "signaturePattern": "sha256={signature}"
    }
  }]
}
```

### `examples/hookvault.shopify.json`

Shopify sends the entire header value as a base64-encoded digest with no prefix.
Requires `signatureEncoding: "base64"` (the fix added in this phase).

```json
// X-Shopify-Hmac-SHA256: <base64_digest>
// Shopify signs the raw request body with HMAC-SHA256, encoded as standard base64.
{
  "providers": [{
    "name": "shopify",
    "path": "/shopify",
    "forwardUrl": "http://host.docker.internal:3000/webhooks/shopify",
    "validation": {
      "algorithm": "hmac-sha256",
      "secretEnvVar": "SHOPIFY_WEBHOOK_SECRET",
      "signatureHeader": "X-Shopify-Hmac-SHA256",
      "payloadFormat": "{body}",
      "signatureEncoding": "base64"
      // signaturePattern omitted: the entire header value is the signature
    }
  }]
}
```

**Case-sensitivity note:** `base64` comparison must NOT lowercase the received
value — `Convert.ToBase64String()` produces mixed-case output and lowercasing
corrupts it. The validator fix handles this (see signatureEncoding section above).

### `examples/hookvault.resend.json`

Resend delivers via Svix. Svix constructs its signed payload from three separate
headers (`svix-id`, `svix-timestamp`, `svix-signature`). This multi-header scheme
cannot be expressed in HookVault's single-header validation schema.

```json
// Resend delivers via Svix. Svix's signed payload spans svix-id, svix-timestamp,
// and svix-signature — three separate headers that can't be expressed in
// HookVault's single-header validation schema. Events are captured and forwarded;
// signatures are not verified.
{
  "providers": [{
    "name": "resend",
    "path": "/resend",
    "forwardUrl": "http://host.docker.internal:3000/webhooks/resend",
    "validation": null
  }]
}
```

### `examples/hookvault.generic-hmac.json`

Annotated template for any HMAC-based webhook provider.

```json
// Generic HMAC template — copy and adapt for any HMAC-based webhook provider.
// Supported algorithms: "hmac-sha256", "hmac-sha512"
// Supported encodings:  "hex" (default), "base64", "base64url"
{
  "providers": [{
    "name": "my-provider",
    "path": "/my-provider",
    "forwardUrl": "http://host.docker.internal:3000/webhooks/my-provider",
    "validation": {
      "algorithm": "hmac-sha256",
      "secretEnvVar": "MY_WEBHOOK_SECRET",
      "signatureHeader": "X-My-Signature",
      "payloadFormat": "{body}",
      "signatureEncoding": "hex",
      "signaturePattern": null,
      "timestampPattern": null
    }
  }]
}
```

## `hookvault.example.json` (repo root)

Annotated multi-provider starter. Users copy this to `hookvault.json`.

```json
// Copy to hookvault.json and customise.
// Mount: -v ./hookvault.json:/app/config/hookvault.json
// Provider-specific configs: see examples/
{
  "providers": [
    {
      "name": "stripe",
      "path": "/stripe",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/stripe",
      "validation": {
        "algorithm": "hmac-sha256",
        "secretEnvVar": "STRIPE_WEBHOOK_SECRET",
        "signatureHeader": "Stripe-Signature",
        "payloadFormat": "{timestamp}.{body}",
        "signatureEncoding": "hex",
        "signaturePattern": "v1={signature}",
        "timestampPattern": "t={timestamp}"
      }
    },
    {
      // Set validation to null to capture + forward without verifying signatures.
      "name": "resend",
      "path": "/resend",
      "forwardUrl": "http://host.docker.internal:3000/webhooks/resend",
      "validation": null
    }
  ]
}
```

## CI update

New `docker-build` job in `.github/workflows/ci.yml`. Runs **in parallel** with
`build-and-test` — independent failure domain, no serialisation cost.

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

`push: false` — builds and verifies, never touches a registry. GHA layer cache
(`type=gha`) means subsequent runs skip unchanged layers.

## File inventory

| File | Action |
|---|---|
| `Dockerfile` | new |
| `.dockerignore` | new |
| `docker-compose.yml` | new |
| `docker-compose.postgres.yml` | new |
| `examples/hookvault.stripe.json` | new |
| `examples/hookvault.github.json` | new |
| `examples/hookvault.shopify.json` | new |
| `examples/hookvault.resend.json` | new |
| `examples/hookvault.generic-hmac.json` | new |
| `hookvault.example.json` | new |
| `src/HookVault/Configuration/ValidationConfig.cs` | add `SignatureEncoding` property |
| `src/HookVault/Services/SignatureValidator.cs` | encoding switch + case-sensitivity fix |
| `tests/HookVault.Tests/SignatureValidatorTests.cs` | base64 + base64url test cases |
| `.github/workflows/ci.yml` | add parallel `docker-build` job |

## Build sequence (separate commits)

1. `feat: add signatureEncoding to validator` — `ValidationConfig`, `SignatureValidator`,
   updated tests. Green before any Docker files exist.
2. `feat: add dockerfile and dockerignore` — multi-stage Alpine build, non-root user.
3. `feat: add docker compose files` — `docker-compose.yml` + `docker-compose.postgres.yml`.
4. `feat: add provider example configs` — five files in `examples/`.
5. `feat: add hookvault example config` — annotated `hookvault.example.json` at repo root.
6. `ci: add docker build job` — parallel `docker-build` job in CI workflow.

## Test strategy

`SignatureValidatorTests.cs` additions (real HMAC, no mocks):

- **base64 encoding**: compute a known Shopify-style HMAC-SHA256 digest, base64-encode
  it, pass it as the header value, assert `IsValid = true`. Mutate one byte, assert
  `IsValid = false`.
- **base64url encoding**: same pattern — correct digest validates; `=` padding stripped;
  `+`/`/` replaced with `-`/`_`.
- **hex default (backward compat)**: existing hex test cases pass unchanged when
  `SignatureEncoding` is null or omitted.
- **mixed-case received hex**: provider sends uppercase hex — `ToLowerInvariant`
  normalisation means it still validates (existing behaviour).

No new integration tests needed for Dockerfile or compose files. The CI Docker
build job is the integration test for the image.

## Security

- Non-root `app` user in the final image — security baseline for all OSS users.
- `hookvault.json` mounted read-only (`:ro`) — the container cannot modify the
  config on the host filesystem.
- `HOOKVAULT_JWT_SECRET` sourced from environment, never hardcoded in compose
  files. `${VAR:?error}` syntax ensures the container won't start with an empty secret.
- Secrets never appear in example configs — only env var names.
