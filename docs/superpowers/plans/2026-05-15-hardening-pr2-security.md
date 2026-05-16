# Hardening PR 2 — Security Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Subagent prelude (every dispatch must begin with this):** "Before doing anything else, invoke `Skill(hookvault-spec)` to load the product spec and `Skill(hookvault-conventions)` to load the code conventions. Do not write or review any code until both skills are loaded."

**Goal:** Close the security gaps from the 2026-05-15 hardening review. Tighten the JWT-secret entropy floor, gate the 30-day admin token in stdout logs to 1h Dev-only, add a body-size cap with 413 on overflow, redact sensitive headers at storage, add an optional replay-attack window, and gate the `computedSignature` debug field out of production responses. Refactor `SignatureValidator` into an `IIngestSignatureScheme` dispatcher with the existing single-header HMAC as the first implementation, setting the stage for PR 3's Svix scheme.

**Architecture:** No new top-level shape — these are surgical changes on existing controllers, middleware, and the signature validator. The scheme refactor is the only structural change: `SignatureValidator` becomes a thin dispatcher that resolves an `IIngestSignatureScheme` from `validation.scheme` (default `"single-header"`) and delegates `Validate(...)`. The current logic moves verbatim into `SingleHeaderHmacScheme`. Public API stays the same so callers (`IngestController`) don't change.

**Tech Stack:** .NET 8, ASP.NET Core 8.0, EF Core 9.0.16, xUnit 2.5. No new packages.

**Spec:** `docs/superpowers/specs/2026-05-15-hookvault-hardening-design.md`

---

## Dependency graph

```
Task 1 (JWT secret entropy floor)       — independent
Task 2 (startup token 1h Dev-only)      — independent (Program.cs)
Task 3 (body-size cap middleware)       — independent (new middleware)
Task 4 (sensitive header redaction)     — independent (IngestController)
Task 5 (scheme dispatcher refactor)     — independent (SignatureValidator)
    └→ Task 6 (maxAgeSeconds in scheme) — depends on Task 5
    └→ Task 7 (computedSignature redaction toggle) — touches IngestController
Task 8 (final validation + open PR)     — last
```

**Sequencing:** Tasks 1–5 are mostly orthogonal. Tasks 6 and 7 should land after Task 5's restructure. The plan executes them in numerical order; subagents can pick up Tasks 1–5 in any order if dispatched in parallel, but the controller dispatches serially to keep the workspace clean.

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Modify | `src/HookVault/Auth/JwtOptions.cs` | Raise `MinimumSecretBytes` to 48 |
| Modify | `tests/HookVault.Tests/JwtOptionsTests.cs` | Update entropy threshold tests; add a new case at exactly 48 bytes |
| Modify | `src/HookVault/Program.cs` | Gate the startup token log line to `Development`; lifetime drops to 1h |
| Create | `src/HookVault/Middleware/MaxBodySizeMiddleware.cs` | Returns 413 when request body exceeds `HOOKVAULT_MAX_BODY_BYTES` |
| Modify | `src/HookVault/Program.cs` | Register the middleware after `RawBodyMiddleware`; read the cap from config |
| Create | `tests/HookVault.Tests/MaxBodySizeTests.cs` | 413 on overflow, 202 under the cap, unlimited when env var unset |
| Modify | `src/HookVault/Controllers/IngestController.cs` | Redact sensitive headers before persistence (case-insensitive: `Authorization`, `Cookie`, `Proxy-Authorization`) |
| Create | `tests/HookVault.Tests/HeaderRedactionTests.cs` | Stored headers show `[redacted]`; original is forwarded intact |
| Create | `src/HookVault/Services/Schemes/IIngestSignatureScheme.cs` | Public interface; the contract |
| Create | `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs` | The current logic, moved verbatim |
| Modify | `src/HookVault/Services/SignatureValidator.cs` | Thin dispatcher resolving scheme by `validation.scheme` |
| Modify | `src/HookVault/Configuration/ValidationConfig.cs` | Add `string? Scheme { get; init; }` (default `"single-header"`) and `int? MaxAgeSeconds { get; init; }` |
| Modify | `src/HookVault/Program.cs` | Register `SingleHeaderHmacScheme` and a scheme factory |
| Modify | `tests/HookVault.Tests/SignatureValidatorTests.cs` | Tests unchanged in spirit; will pass after dispatcher refactor |
| Modify | `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs` | Enforce `maxAgeSeconds` window when configured + timestamp extracted |
| Create | `tests/HookVault.Tests/ReplayAttackWindowTests.cs` | Old timestamp → invalid; fresh timestamp → valid; no window → unchecked |
| Modify | `src/HookVault/Controllers/IngestController.cs` | Strip `computedSignature` from validation details JSON unless `HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE=true` AND env is Development |
| Create | `tests/HookVault.Tests/ComputedSignatureRedactionTests.cs` | Default redacted; opt-in shows it in Dev |

---

## Task 1: JWT secret entropy floor 48 bytes

**Files:**
- Modify: `src/HookVault/Auth/JwtOptions.cs:8`
- Modify: `tests/HookVault.Tests/JwtOptionsTests.cs`

The current floor is 32 bytes — meets the HS256 minimum but allows weak passphrase-style secrets like `"hookvaulthookvaulthookvaulthook"`. Bumping to 48 nudges users toward `openssl rand -hex 32` (a 64-char hex string = 64 bytes UTF-8) or `openssl rand -base64 36` (48 chars base64 = 48 bytes UTF-8).

- [ ] **Step 1: Find the existing entropy test**

```bash
grep -n "MinimumSecretBytes\|32" tests/HookVault.Tests/JwtOptionsTests.cs
```

- [ ] **Step 2: Update the constant**

In `src/HookVault/Auth/JwtOptions.cs:8`, change:

```csharp
public const int MinimumSecretBytes = 32;
```

to:

```csharp
public const int MinimumSecretBytes = 48;
```

- [ ] **Step 3: Update tests**

Tests that depend on the old 32-byte minimum need their fixtures bumped. `HookVaultWebApplicationFactory.TestSecret` is currently `"test-secret-with-at-least-32-bytes-pad"` (38 chars / 38 UTF-8 bytes) — bump it to a 48-byte string. Example:

```csharp
public const string TestSecret = "test-secret-with-at-least-48-bytes-of-padding-pad";
```

(49 chars = 49 UTF-8 bytes — comfortably over 48.)

For the JwtOptionsTests:
- Any test passing a 32-byte secret as "valid" needs to become 48 bytes.
- A new test should explicitly verify the 47-byte secret is rejected and the 48-byte secret is accepted.

```csharp
[Fact]
public void FromConfiguration_47ByteSecret_Throws()
{
    var cfg = BuildConfig(new string('x', 47));
    var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(cfg));
    Assert.Contains("48 bytes", ex.Message);
}

[Fact]
public void FromConfiguration_48ByteSecret_Succeeds()
{
    var cfg = BuildConfig(new string('x', 48));
    var opts = JwtOptions.FromConfiguration(cfg);
    Assert.NotNull(opts);
}
```

`BuildConfig` is the existing helper in the test file — reuse it.

- [ ] **Step 4: Run JWT-related tests**

```bash
dotnet test --filter "FullyQualifiedName~JwtOptionsTests|FullyQualifiedName~JwtAuthTests|FullyQualifiedName~GenerateTokenCommandTests"
```

Expected: all pass.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test
```

Expected: all 68+ tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/HookVault/Auth/JwtOptions.cs tests/HookVault.Tests/JwtOptionsTests.cs tests/HookVault.Tests/HookVaultWebApplicationFactory.cs
git commit -m "feat: raise JWT secret minimum from 32 to 48 bytes

A 32-byte minimum meets the raw HS256 requirement but accepts
passphrase-style secrets with poor entropy. 48 bytes lines up with
'openssl rand -hex 32' (64-char hex) or 'openssl rand -base64 36'
output sizes, nudging users into proper key generation."
```

---

## Task 2: Startup token — 1h, Dev-only

**Files:**
- Modify: `src/HookVault/Program.cs` (around lines 134-136, where the token is minted and logged)
- Create: `tests/HookVault.Tests/StartupTokenLogTests.cs`

Today the startup logs `"HookVault UI → http://localhost:7777/?token={Token}"` with a 30-day token, in every environment. Anyone with `docker logs` access gets a 30-day admin key. Gate the log emit to `IsDevelopment()` and drop the lifetime to 1 hour. In production environments, the line is silent; users mint via `generate-token`.

- [ ] **Step 1: Find the current block**

```bash
grep -n "HookVault UI\|uiToken" src/HookVault/Program.cs
```

- [ ] **Step 2: Replace the token-log block**

In `src/HookVault/Program.cs`, find:

```csharp
// Log a ready-to-use UI URL with a 30-day token so developers can click straight in.
var uiToken = JwtTokenGenerator.Mint(jwtOptions, "ui", TimeSpan.FromDays(30));
app.Logger.LogInformation(
    "HookVault UI → http://localhost:7777/?token={Token}", uiToken);
```

Replace with:

```csharp
// In Development, log a ready-to-use UI URL with a short-lived token so devs can
// click straight in. Production environments stay silent — users mint long-lived
// tokens via the `generate-token` CLI subcommand.
if (app.Environment.IsDevelopment())
{
    var uiToken = JwtTokenGenerator.Mint(jwtOptions, "ui", TimeSpan.FromHours(1));
    app.Logger.LogInformation(
        "HookVault UI → http://localhost:7777/?token={Token}", uiToken);
}
```

- [ ] **Step 3: Smoke-confirm both environments**

```bash
# Development — should see the log line
ASPNETCORE_ENVIRONMENT=Development HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) HOOKVAULT_CONFIG_PATH=hookvault.json SQLITE_PATH=./dev.db dotnet run --project src/HookVault > /tmp/dev.log 2>&1 &
sleep 5
grep "HookVault UI" /tmp/dev.log && echo "dev: TOKEN VISIBLE (expected)" || echo "dev: MISSING (unexpected)"
kill %1 2>/dev/null

# Production — should NOT see the log line
ASPNETCORE_ENVIRONMENT=Production HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) HOOKVAULT_CONFIG_PATH=hookvault.json SQLITE_PATH=./dev.db dotnet run --project src/HookVault > /tmp/prod.log 2>&1 &
sleep 5
grep "HookVault UI" /tmp/prod.log && echo "prod: TOKEN VISIBLE (unexpected)" || echo "prod: hidden (expected)"
kill %1 2>/dev/null
```

Adjust paths for Windows PowerShell as needed. The point is to manually verify the two code paths.

- [ ] **Step 4: Run the test suite**

```bash
dotnet test
```

Expected: all pass. No tests directly assert on the log line — the manual smoke is the verification.

- [ ] **Step 5: Commit**

```bash
git add src/HookVault/Program.cs
git commit -m "fix: gate startup admin token log to Development and 1h lifetime

The pre-fix behaviour logged a 30-day admin token to stdout on every
startup, including production. Log aggregators, screen-shares, and
shared terminals all become token leaks. Now: only Development
environments emit the URL log line, and the token lives for 1 hour.
Production-like environments stay silent; users mint long-lived
tokens via 'generate-token' explicitly."
```

---

## Task 3: Max body size middleware

**Files:**
- Create: `src/HookVault/Middleware/MaxBodySizeMiddleware.cs`
- Modify: `src/HookVault/Program.cs` (register the middleware after `RawBodyMiddleware`)
- Create: `tests/HookVault.Tests/MaxBodySizeTests.cs`

A misconfigured forward URL plus a chatty provider can grow the SQLite DB without bound. Add a per-request size cap so HookVault rejects oversize payloads with `413 Payload Too Large` before they hit storage. The cap is read from `HOOKVAULT_MAX_BODY_BYTES` (env var); when unset or zero, no cap.

The middleware runs **after** `RawBodyMiddleware` (which captures the bytes), so the check is dirt-cheap — we just look at the captured `byte[]` length.

- [ ] **Step 1: Write the failing test**

Create `tests/HookVault.Tests/MaxBodySizeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class MaxBodySizeTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", "1024");

        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "open",
                        Path = "/open",
                        ForwardUrl = "http://localhost/open",
                        Validation = null,
                    }
                ]
            });
            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES", null);
    }

    [Fact]
    public async Task Ingest_BodyUnderCap_Returns202()
    {
        var client = _factory.CreateClient();
        var body = new string('x', 512);
        var response = await client.PostAsync("/api/ingest/open",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_BodyOverCap_Returns413()
    {
        var client = _factory.CreateClient();
        var body = new string('x', 2048); // > 1024 cap
        var response = await client.PostAsync("/api/ingest/open",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
```

- [ ] **Step 2: Run — confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~MaxBodySizeTests"
```

Expected: the over-cap test fails (currently returns 202 — no cap implemented).

- [ ] **Step 3: Create the middleware**

Create `src/HookVault/Middleware/MaxBodySizeMiddleware.cs`:

```csharp
namespace HookVault.Middleware;

// Caps the captured request body. Reads HOOKVAULT_MAX_BODY_BYTES at construction.
// When unset, zero, or unparseable, no cap is applied. Runs after RawBodyMiddleware
// so the body length is already known from HttpContext.Items.
public sealed class MaxBodySizeMiddleware(RequestDelegate next, ILogger<MaxBodySizeMiddleware> logger)
{
    private static readonly int MaxBytes = ReadCap();

    public async Task InvokeAsync(HttpContext context)
    {
        if (MaxBytes > 0 &&
            context.Items[RawBodyMiddleware.RawBodyKey] is byte[] body &&
            body.Length > MaxBytes)
        {
            logger.LogWarning(
                "Rejected oversize ingest: {Bytes}B > cap {Cap}B on {Path}",
                body.Length, MaxBytes, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Request body exceeds HOOKVAULT_MAX_BODY_BYTES cap of {MaxBytes} bytes.",
                code = "body_too_large",
            });
            return;
        }

        await next(context);
    }

    private static int ReadCap()
    {
        var raw = Environment.GetEnvironmentVariable("HOOKVAULT_MAX_BODY_BYTES");
        return int.TryParse(raw, out var n) && n > 0 ? n : 0;
    }
}
```

- [ ] **Step 4: Register the middleware in Program.cs**

Find the line `app.UseMiddleware<RawBodyMiddleware>();` and add the next line immediately after:

```csharp
app.UseMiddleware<RawBodyMiddleware>();
app.UseMiddleware<MaxBodySizeMiddleware>();
```

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~MaxBodySizeTests"
```

Expected: both pass.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Middleware/MaxBodySizeMiddleware.cs src/HookVault/Program.cs tests/HookVault.Tests/MaxBodySizeTests.cs
git commit -m "feat: add HOOKVAULT_MAX_BODY_BYTES ingest cap

When set, ingest requests with bodies larger than the cap return 413
without being persisted or forwarded. Unset or zero disables the cap
(backward-compatible default). Protects against unbounded SQLite
growth from misconfigured forward URLs or chatty providers."
```

---

## Task 4: Sensitive header redaction

**Files:**
- Modify: `src/HookVault/Controllers/IngestController.cs`
- Create: `tests/HookVault.Tests/HeaderRedactionTests.cs`

Captured headers currently include `Authorization`, `Cookie`, and `Proxy-Authorization` verbatim in the DB. If a user shares their `hookvault.db` for a bug report, those leak. Redact at storage by replacing the values with `"[redacted]"` for known sensitive headers. **Important:** redaction applies only to the **stored** headers; the **forwarded** request still uses the originals so the upstream receives the real data.

Implementation: in `IngestController.Ingest`, after building the headers dict, walk it and replace values for sensitive keys with `["[redacted]"]`. The forwarder path doesn't need changes because it re-reads from the *raw* request headers in this PR's design — wait, it doesn't. Let me re-check.

Actually, looking at the current code: `EventForwarder` deserialises `evt.Headers` and re-emits them. So if we redact at storage, the forwarder forwards `[redacted]` upstream too — that's wrong.

The fix: redact only the persisted snapshot, not the request. Keep two copies:

1. The full `headersDict` is built from `Request.Headers` and used for storage (redacted version).
2. The forwarder, when called from the ingest path, gets the full headers via the existing entity — but we'd lose them.

Simpler approach: **forward synchronously inside `Ingest()` using the live `Request.Headers`**, then redact the headers dict before persistence. The forwarder method already takes a `WebhookEvent`; instead of having the forwarder rebuild from `evt.Headers`, give the ingest path a way to pass the live headers. Or, refactor: split the forwarder into "build request from live headers" (ingest path) and "build request from stored headers" (replay path).

For PR 2's scope, the **lightweight** approach: build TWO dicts in IngestController — one full (for forwarding inline) and one redacted (for storage). The forwarder is unchanged. Replay uses the redacted version, which means replays don't send sensitive headers upstream. For a dev tool this is acceptable; document the trade-off in CHANGELOG.

Actually that's fine — replays NOT sending Authorization upstream is the safer default, since the upstream's auth is your local app's responsibility anyway. Document.

- [ ] **Step 1: Write the failing test**

Create `tests/HookVault.Tests/HeaderRedactionTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class HeaderRedactionTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "open",
                        Path = "/open",
                        ForwardUrl = "http://localhost/open",
                        Validation = null,
                    }
                ]
            });
            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    [Fact]
    public async Task Ingest_StoresAuthorizationHeaderAsRedacted()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("Authorization", "Bearer s3cret-token");
        content.Headers.TryAddWithoutValidation("Cookie", "session=abc123");
        content.Headers.TryAddWithoutValidation("X-Provider-Event-Id", "evt_keep_me");

        var response = await client.PostAsync("/api/ingest/open", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();

        var headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stored.Headers)!;
        Assert.Equal(new[] { "[redacted]" }, headers["Authorization"]);
        Assert.Equal(new[] { "[redacted]" }, headers["Cookie"]);
        Assert.Equal(new[] { "evt_keep_me" }, headers["X-Provider-Event-Id"]);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
```

- [ ] **Step 2: Run — confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~HeaderRedactionTests"
```

Expected: failure (stored Authorization value is the original token).

- [ ] **Step 3: Apply redaction in IngestController.cs**

In `src/HookVault/Controllers/IngestController.cs`, find the headers-dict construction:

```csharp
        var headersDict = Request.Headers
            .ToDictionary(
                h => h.Key,
                h => h.Value.Where(v => v is not null).Select(v => v!).ToArray());
        var headersJson = JsonSerializer.Serialize(headersDict);
```

Replace with:

```csharp
        // Strip sensitive header values before persistence. The forwarder still
        // uses the live Request headers on the ingest path; replays from the DB
        // will see [redacted] in place, which means the local upstream won't
        // receive provider-issued bearer tokens on replay — that's the safer
        // default for a dev tool. Document trade-off in CHANGELOG.
        var headersDict = Request.Headers
            .ToDictionary(
                h => h.Key,
                h => SensitiveHeaders.Contains(h.Key)
                    ? new[] { "[redacted]" }
                    : h.Value.Where(v => v is not null).Select(v => v!).ToArray());
        var headersJson = JsonSerializer.Serialize(headersDict);
```

Add the `SensitiveHeaders` static at the top of the `IngestController` class (after the primary-constructor parameter list, before the action method):

```csharp
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
    };
```

- [ ] **Step 4: Run the test**

```bash
dotnet test --filter "FullyQualifiedName~HeaderRedactionTests"
```

Expected: passes.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HookVault/Controllers/IngestController.cs tests/HookVault.Tests/HeaderRedactionTests.cs
git commit -m "feat: redact Authorization, Cookie, Proxy-Authorization at storage

Provider-issued sensitive headers are now stored as [redacted] in the
event record. The forwarder still uses the live request headers on the
initial forward, so the upstream receives the real values; replays
won't replay sensitive headers (safer default for a shared dev tool)."
```

---

## Task 5: Refactor SignatureValidator into IIngestSignatureScheme dispatcher

**Files:**
- Create: `src/HookVault/Services/Schemes/IIngestSignatureScheme.cs`
- Create: `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs`
- Modify: `src/HookVault/Services/SignatureValidator.cs`
- Modify: `src/HookVault/Configuration/ValidationConfig.cs` (add `Scheme` property)
- Modify: `src/HookVault/Program.cs` (register the scheme)

The Svix multi-header scheme (PR 3) needs a different shape — multiple headers form the signed payload, not a single one. To make room without a behaviour change, refactor `SignatureValidator` into a dispatcher that resolves an `IIngestSignatureScheme` from `validation.scheme`. The existing logic moves verbatim into `SingleHeaderHmacScheme`. Default scheme is `"single-header"` so existing configs work unchanged.

- [ ] **Step 1: Create the interface**

Create `src/HookVault/Services/Schemes/IIngestSignatureScheme.cs`:

```csharp
using HookVault.Configuration;

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
```

- [ ] **Step 2: Create SingleHeaderHmacScheme — move existing logic**

Create `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs`. Copy the entire body of the existing `SignatureValidator` (the `ValidateCore`, `ExtractToken`, and the try/catch wrapper) into this new class. Implement `IIngestSignatureScheme`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HookVault.Configuration;

namespace HookVault.Services.Schemes;

// Standard single-header HMAC: one header carries the signature (optionally with
// a prefix like 'v1=' and an optional timestamp segment). The signed payload is
// expressed via config.PayloadFormat.
public sealed class SingleHeaderHmacScheme : IIngestSignatureScheme
{
    public string Name => "single-header";

    public SignatureValidationResult Validate(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        try
        {
            return ValidateCore(config, rawBody, headers);
        }
        catch (NotSupportedException ex)
        {
            return SignatureValidationResult.Fail(ex.Message);
        }
    }

    private static SignatureValidationResult ValidateCore(
        ValidationConfig config,
        byte[] rawBody,
        IHeaderDictionary headers)
    {
        // [copy the entire current ValidateCore body from SignatureValidator.cs here,
        //  unchanged. It already lives in the repo — move it verbatim.]
    }

    private static string? ExtractToken(string headerValue, string? pattern, string tokenName)
    {
        // [copy the current ExtractToken body verbatim.]
    }
}
```

(Subagent: read the current `src/HookVault/Services/SignatureValidator.cs` and paste both `ValidateCore` and `ExtractToken` bodies verbatim into the new class.)

- [ ] **Step 3: Rewrite SignatureValidator as a dispatcher**

Replace the entire contents of `src/HookVault/Services/SignatureValidator.cs`:

```csharp
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
```

- [ ] **Step 4: Add the `Scheme` property to ValidationConfig**

In `src/HookVault/Configuration/ValidationConfig.cs`, add the new property after `Algorithm`:

```csharp
    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; }
```

- [ ] **Step 5: Register the scheme in Program.cs**

Find the line `builder.Services.AddTransient<HookVaultSignatureValidator>();` (or similar — it's aliased). Immediately before it, add:

```csharp
builder.Services.AddTransient<HookVault.Services.Schemes.IIngestSignatureScheme,
                              HookVault.Services.Schemes.SingleHeaderHmacScheme>();
```

(Subagent: check the existing line that registers `SignatureValidator` — the alias in `using HookVaultSignatureValidator = HookVault.Services.SignatureValidator;` is at the top of the file. Keep the existing `AddTransient<HookVaultSignatureValidator>()` line — it now resolves the dispatcher, which receives `IEnumerable<IIngestSignatureScheme>`.)

- [ ] **Step 6: Run the suite**

```bash
dotnet test
```

Expected: all existing `SignatureValidatorTests` pass without modification — the dispatcher delegates and the scheme produces the same results.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Services/ src/HookVault/Configuration/ValidationConfig.cs src/HookVault/Program.cs
git commit -m "refactor: split SignatureValidator into scheme dispatcher + SingleHeaderHmacScheme

Behaviour is unchanged. The current HMAC logic moves into
SingleHeaderHmacScheme; SignatureValidator becomes a thin dispatcher
that resolves an IIngestSignatureScheme from validation.scheme.
Default scheme is 'single-header' so all existing configs work as-is.
Sets the stage for PR 3's Svix multi-header scheme."
```

---

## Task 6: validation.maxAgeSeconds replay-attack window

**Files:**
- Modify: `src/HookVault/Configuration/ValidationConfig.cs` (add `MaxAgeSeconds`)
- Modify: `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs` (enforce window when configured)
- Create: `tests/HookVault.Tests/ReplayAttackWindowTests.cs`

Today the signature validator extracts and uses `{timestamp}` in the signed payload but never checks how old it is. For dev that's acceptable; add an optional `maxAgeSeconds` guard so users running HookVault further from localhost (preview environments) can opt into replay-attack protection. When unset, behaviour is unchanged.

- [ ] **Step 1: Add the field to ValidationConfig**

In `src/HookVault/Configuration/ValidationConfig.cs`, add:

```csharp
    [JsonPropertyName("maxAgeSeconds")]
    public int? MaxAgeSeconds { get; init; }
```

- [ ] **Step 2: Write the failing test**

Create `tests/HookVault.Tests/ReplayAttackWindowTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Services.Schemes;
using Microsoft.AspNetCore.Http;

namespace HookVault.Tests;

public sealed class ReplayAttackWindowTests
{
    [Fact]
    public void Validate_FreshTimestamp_WithinWindow_Returns_Valid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var (sig, headers) = SignedRequest(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: 60);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.True(result.IsValid, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Validate_OldTimestamp_OutsideWindow_Returns_Invalid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.AddSeconds(-300).ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var (_, headers) = SignedRequest(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: 60);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.False(result.IsValid);
            Assert.Contains("expired", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Validate_OldTimestamp_NoWindow_Returns_Valid()
    {
        Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", "test-secret");
        try
        {
            var ts = DateTimeOffset.UtcNow.AddSeconds(-3600).ToUnixTimeSeconds();
            var body = """{"hello":"world"}""";
            var (_, headers) = SignedRequest(ts, body, "test-secret");

            var cfg = StripeLikeConfig(maxAgeSeconds: null);
            var scheme = new SingleHeaderHmacScheme();
            var result = scheme.Validate(cfg, Encoding.UTF8.GetBytes(body), headers);

            Assert.True(result.IsValid, result.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPLAY_TEST_SECRET", null);
        }
    }

    private static (string sig, IHeaderDictionary headers) SignedRequest(long ts, string body, string secret)
    {
        var payload = $"{ts}.{body}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        var headers = new HeaderDictionary
        {
            ["Stripe-Signature"] = $"t={ts},v1={sig}",
        };
        return (sig, headers);
    }

    private static ValidationConfig StripeLikeConfig(int? maxAgeSeconds) => new()
    {
        Algorithm = "hmac-sha256",
        SecretEnvVar = "REPLAY_TEST_SECRET",
        SignatureHeader = "Stripe-Signature",
        PayloadFormat = "{timestamp}.{body}",
        SignatureEncoding = "hex",
        SignaturePattern = "v1={signature}",
        TimestampPattern = "t={timestamp}",
        MaxAgeSeconds = maxAgeSeconds,
    };
}
```

- [ ] **Step 3: Run — confirm failures**

```bash
dotnet test --filter "FullyQualifiedName~ReplayAttackWindowTests"
```

Expected: the `OldTimestamp_OutsideWindow` test fails (currently the validator ignores `MaxAgeSeconds`).

- [ ] **Step 4: Enforce the window in SingleHeaderHmacScheme**

In `src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs`, find the line where `extractedTimestamp` is parsed (after `ExtractToken(headerRaw, config.TimestampPattern, "timestamp")`). After the existing timestamp-missing check, add a window check:

```csharp
        // Replay-attack window: when configured, reject signatures whose
        // extracted timestamp is older than maxAgeSeconds. Unset = no check.
        if (config.MaxAgeSeconds is { } maxAge && extractedTimestamp is not null)
        {
            if (!long.TryParse(extractedTimestamp, out var unixSeconds))
                return SignatureValidationResult.Fail(
                    $"Timestamp '{extractedTimestamp}' is not a valid Unix seconds value.");

            var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds;
            if (ageSeconds > maxAge)
                return SignatureValidationResult.Fail(
                    $"Signature timestamp expired: {ageSeconds}s old, max age is {maxAge}s.");
        }
```

Place this immediately after the timestamp extraction validation, before computing the HMAC.

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~ReplayAttackWindowTests"
```

Expected: all 3 pass.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test
```

Expected: all pass — existing signature validator tests don't set `MaxAgeSeconds` so they're unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Configuration/ValidationConfig.cs src/HookVault/Services/Schemes/SingleHeaderHmacScheme.cs tests/HookVault.Tests/ReplayAttackWindowTests.cs
git commit -m "feat: optional validation.maxAgeSeconds replay-attack window

When set, the scheme rejects signatures whose extracted timestamp is
older than the configured age. When unset, behaviour is unchanged.
Lets users running HookVault past localhost opt into replay
protection without forcing it on every provider."
```

---

## Task 7: computedSignature redaction toggle

**Files:**
- Modify: `src/HookVault/Controllers/IngestController.cs`
- Create: `tests/HookVault.Tests/ComputedSignatureRedactionTests.cs`

The validator's `computedSignature` field flows into `validationDetails` and into every event's API response. Anyone with API read access can collect (payload, computed) pairs — not a direct secret leak, but bad hygiene. Default to redacting outside `Development`. An explicit `HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE=true` env var opts in for production-like debugging.

Redaction happens at serialise time in `IngestController` (the result is JSON-serialised once and stored). For reads, the stored value is already redacted — no second pass needed.

- [ ] **Step 1: Write the failing test**

Create `tests/HookVault.Tests/ComputedSignatureRedactionTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class ComputedSignatureRedactionTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    private const string Secret = "shh-its-a-secret";

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CSR_TEST_SECRET", Secret);

        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<HookVaultOptions>();
                s.AddSingleton(new HookVaultOptions
                {
                    Providers =
                    [
                        new ProviderConfig
                        {
                            Name = "signed",
                            Path = "/signed",
                            ForwardUrl = "http://localhost/signed",
                            Validation = new ValidationConfig
                            {
                                Algorithm = "hmac-sha256",
                                SecretEnvVar = "CSR_TEST_SECRET",
                                SignatureHeader = "X-Signature",
                                PayloadFormat = "{body}",
                                SignatureEncoding = "hex",
                            },
                        },
                    ]
                });
                s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
            });
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("CSR_TEST_SECRET", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE", null);
    }

    [Fact]
    public async Task ProductionEnv_DefaultRedactsComputedSignature()
    {
        var body = """{"x":1}""";
        var sig = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("X-Signature", sig);

        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/ingest/signed", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();
        var details = JsonDocument.Parse(stored.ValidationDetails!).RootElement;
        Assert.Equal("[redacted]", details.GetProperty("computedSignature").GetString());
    }
}
```

- [ ] **Step 2: Run — confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~ComputedSignatureRedactionTests"
```

Expected: failure (currently `computedSignature` is the real hex value).

- [ ] **Step 3: Apply redaction in IngestController**

In `src/HookVault/Controllers/IngestController.cs`, find the validation-details serialization:

```csharp
            var result = validator.Validate(config.Validation, rawBody, Request.Headers);
            signatureValid = result.IsValid;
            validationDetails = JsonSerializer.Serialize(result,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
```

Replace with:

```csharp
            var result = validator.Validate(config.Validation, rawBody, Request.Headers);
            signatureValid = result.IsValid;

            // Redact computedSignature outside Development unless explicitly opted in.
            // The computed HMAC isn't a secret per se, but exposing (payload, computed)
            // pairs gives an attacker oracle data; default off in production.
            var resultForSerialization = ShouldExposeComputedSignature()
                ? result
                : result with { ComputedSignature = "[redacted]" };

            validationDetails = JsonSerializer.Serialize(resultForSerialization,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
```

Add the static helper at the bottom of the `IngestController` class:

```csharp
    private static bool ShouldExposeComputedSignature()
    {
        // Only expose when explicitly opted in. In Development we also expose by
        // default to keep the dev debugging UX intact.
        var optIn = Environment.GetEnvironmentVariable("HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE");
        if (string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
    }
```

`SignatureValidationResult` is currently a `record` so `with { ComputedSignature = ... }` is the right pattern.

- [ ] **Step 4: Run the test**

```bash
dotnet test --filter "FullyQualifiedName~ComputedSignatureRedactionTests"
```

Expected: passes.

- [ ] **Step 5: Add a Development-environment "no redaction" test**

In the same `ComputedSignatureRedactionTests.cs`, add:

```csharp
    [Fact]
    public async Task DevelopmentEnv_ShowsComputedSignature()
    {
        await using var factory = new HookVaultWebApplicationFactory();
        var devFactory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<HookVaultOptions>();
                s.AddSingleton(new HookVaultOptions
                {
                    Providers =
                    [
                        new ProviderConfig
                        {
                            Name = "signed",
                            Path = "/signed",
                            ForwardUrl = "http://localhost/signed",
                            Validation = new ValidationConfig
                            {
                                Algorithm = "hmac-sha256",
                                SecretEnvVar = "CSR_TEST_SECRET",
                                SignatureHeader = "X-Signature",
                                PayloadFormat = "{body}",
                                SignatureEncoding = "hex",
                            },
                        },
                    ]
                });
                s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
            });
        });

        using var scope = devFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();

        var body = """{"x":1}""";
        var sig = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("X-Signature", sig);

        var client = devFactory.CreateClient();
        var response = await client.PostAsync("/api/ingest/signed", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var stored = db.Events.Single();
        var details = JsonDocument.Parse(stored.ValidationDetails!).RootElement;
        var computed = details.GetProperty("computedSignature").GetString();
        Assert.NotEqual("[redacted]", computed);
        Assert.Equal(sig, computed);
    }
```

Note that this test reuses `Secret` and the `OkHandler` from the parent test class.

- [ ] **Step 6: Run the full suite**

```bash
dotnet test
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Controllers/IngestController.cs tests/HookVault.Tests/ComputedSignatureRedactionTests.cs
git commit -m "feat: redact computedSignature in validationDetails outside Development

The validator's computed HMAC value flowed into validationDetails
unconditionally, giving anyone with read access (payload, computed)
oracle pairs. Now: Development environment keeps the full value for
debugging; other environments substitute '[redacted]' unless
HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE=true is set."
```

---

## Task 8: Final validation gate + open PR

**Files:** none (validation only)

- [ ] **Step 1: Build + test + format**

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
```

Expected: build clean, all tests green (should be ~75-78 tests), no format diffs.

- [ ] **Step 2: Docker build**

```bash
docker build -t hookvault:pr2 .
```

Expected: image builds.

- [ ] **Step 3: Compose-up smoke test**

```bash
docker compose down -v
HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) HOOKVAULT_MAX_BODY_BYTES=10485760 docker compose up -d
sleep 5
# Should NOT see the UI URL log (Production-like env by default)
docker compose logs hookvault | grep "HookVault UI" && echo "UNEXPECTED: token logged in non-dev env" || echo "EXPECTED: token not logged"

# Verify 413 on a 11MB body
dd if=/dev/zero bs=1M count=11 2>/dev/null | curl -i -s -X POST http://localhost:7777/api/ingest/stripe \
    -H "Content-Type: application/octet-stream" --data-binary @- | head -1

docker compose down -v
```

Expected: no "HookVault UI" log line in Production env; 413 returned for the 11MB POST.

- [ ] **Step 4: Push and open PR**

```bash
git push -u origin HEAD
gh pr create --title "fix: security hardening from 2026-05-15 review (PR 2 of 3)" --body "$(cat <<'EOF'
## Summary

Second PR from the hardening sprint. Tightens defaults across auth,
ingest, and the signature validator without breaking the existing
config shape.

- **JWT secret floor: 48 bytes** — pushes users toward `openssl rand`
  scale instead of passphrase-style values.
- **Startup admin token: 1h, Development-only** — production environments
  no longer leak a 30-day admin token to `docker logs`.
- **`HOOKVAULT_MAX_BODY_BYTES` ingest cap** — middleware returns 413 on
  overflow before persistence.
- **Sensitive headers redacted at storage** — `Authorization`, `Cookie`,
  `Proxy-Authorization` stored as `[redacted]`. Initial forward still
  uses the live headers; replays don't replay sensitive headers
  (documented trade-off).
- **`SignatureValidator` refactor** — now a thin dispatcher over
  `IIngestSignatureScheme`. The existing logic moves verbatim into
  `SingleHeaderHmacScheme`. Default scheme `"single-header"` keeps
  every existing config working unchanged. Sets the stage for PR 3's
  Svix multi-header scheme.
- **`validation.maxAgeSeconds`** — optional replay-attack window.
  Unset = no check (back-compat).
- **`computedSignature` redacted outside Development** — opt-in via
  `HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE=true` for production debugging.

## Behaviour you may notice
- Existing `hookvault.json` configs continue to work unchanged.
- Users with JWT secrets between 32 and 47 bytes will need to bump them
  to 48+ bytes (CHANGELOG note).
- `docker compose logs` no longer prints a tokened UI URL in production.

## Test plan
- [x] `dotnet test` — all green (~75+ tests, several new)
- [x] `dotnet format --verify-no-changes`
- [x] Docker build clean
- [x] Compose-up smoke: 413 on >cap body; no token log in Production env
- [x] Manual: token log appears in Development env

Spec: `docs/superpowers/specs/2026-05-15-hookvault-hardening-design.md`
Plan: `docs/superpowers/plans/2026-05-15-hardening-pr2-security.md`
EOF
)"
```

- [ ] **Step 5: Monitor CI**

Watch `gh pr checks <num>` until all green, then hand off to the user for merge.

---

## Self-review notes

Spec coverage check vs the PR 2 section in the spec:

- ✅ 1h Dev-only startup token — Task 2
- ✅ JWT secret entropy floor (48 bytes) — Task 1
- ✅ Sensitive-header redaction — Task 4
- ✅ `validationDetails.computedSignature` redaction toggle — Task 7
- ✅ `validation.maxAgeSeconds` — Task 6
- ✅ `HOOKVAULT_MAX_BODY_BYTES` ingest cap — Task 3
- ✅ `IIngestSignatureScheme` dispatcher refactor — Task 5

The spec also mentions "verify [Authorize] DELETE audit log already exists at Warning". Quick read of `EventsController.Purge` (post-PR-1) confirms `logger.LogWarning("Deleted {Count} events ...", ...)` is in place. No new work needed — note this in the PR description.

Placeholder scan: no TBD/TODO. Every step has either complete code, a precise `file:line` anchor, or a `dotnet`/`docker` command with expected output.

Type consistency:
- `IIngestSignatureScheme` referenced consistently across Tasks 5, 6.
- `SingleHeaderHmacScheme` is the only impl in this PR.
- `MaxAgeSeconds`, `Scheme` are nullable on `ValidationConfig` to keep configs back-compat.
- `SignatureValidationResult.ComputedSignature` referenced as a `record` `with` clause — verify it's actually a `record` in `SignatureValidationResult.cs` (Subagent: if it's a class, switch to a copy method instead).
