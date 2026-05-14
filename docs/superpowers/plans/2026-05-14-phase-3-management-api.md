# Phase 3 — Management API + JWT Auth — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> Every implementer dispatched against this plan MUST also load `Skill(hookvault-spec)` and `Skill(hookvault-conventions)` before touching code.

**Goal:** Expose captured webhook events over a JWT-protected HTTP API (list/detail/replay/bulk-replay/delete) and ship a CLI subcommand to mint tokens.

**Architecture:** Validate-only HS256 JWT bearer auth (HookVault is an OAuth2 resource server). Operators mint tokens via `dotnet run -- generate-token`. New `EventsController` is a thin layer over the existing `EventRepository` + `ReplayQueue`. No new persistence, no new background services.

**Tech Stack:** .NET 8, ASP.NET Core, EF Core (SQLite), xUnit, `Microsoft.AspNetCore.Authentication.JwtBearer`, `System.IdentityModel.Tokens.Jwt`, `Microsoft.AspNetCore.Mvc.Testing`.

**Spec:** [`docs/superpowers/specs/2026-05-14-phase-3-management-api-design.md`](../specs/2026-05-14-phase-3-management-api-design.md)

**Conventions:** [`.claude/skills/hookvault-conventions/SKILL.md`](../../../.claude/skills/hookvault-conventions/SKILL.md) — file-scoped namespaces, primary constructors, sealed records, `IHttpClientFactory`, constant-time crypto compare, real SQLite/real crypto in tests, no mocks.

**Commit style:** [`.claude/skills/commit-style/SKILL.md`](../../../.claude/skills/commit-style/SKILL.md) — `type: subject`, no AI attribution. Ever.

---

## File Map

**Created (in order of appearance below):**
- `src/HookVault/Auth/JwtOptions.cs` — record + `FromConfiguration` factory + secret-length validation.
- `src/HookVault/Auth/JwtTokenGenerator.cs` — static `Mint` helper. Pure crypto, no DI.
- `src/HookVault/Cli/GenerateTokenCommand.cs` — argv parser + token printer. Pure (no host).
- `src/HookVault/Contracts/EventSummary.cs`
- `src/HookVault/Contracts/EventDetail.cs`
- `src/HookVault/Contracts/ListEventsResponse.cs`
- `src/HookVault/Contracts/ReplayEnqueuedResponse.cs`
- `src/HookVault/Contracts/ReplayBulkResponse.cs`
- `src/HookVault/Contracts/DeleteResponse.cs`
- `src/HookVault/Contracts/ApiError.cs`
- `src/HookVault/Controllers/EventsController.cs`
- `tests/HookVault.Tests/JwtTokenGeneratorTests.cs`
- `tests/HookVault.Tests/JwtOptionsTests.cs`
- `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs` — shared test fixture for integration tests.
- `tests/HookVault.Tests/JwtAuthTests.cs`
- `tests/HookVault.Tests/GenerateTokenCommandTests.cs`
- `tests/HookVault.Tests/EventsControllerTests.cs`

**Modified:**
- `src/HookVault/HookVault.csproj` — add `Microsoft.AspNetCore.Authentication.JwtBearer`.
- `tests/HookVault.Tests/HookVault.Tests.csproj` — add `Microsoft.AspNetCore.Mvc.Testing`.
- `src/HookVault/Program.cs` — CLI intercept, auth wiring, error factory, `[AllowAnonymous]` decisions.
- `src/HookVault/Controllers/IngestController.cs` — add `[AllowAnonymous]`.
- `src/HookVault/Controllers/HealthController.cs` — add `[AllowAnonymous]`.
- `src/HookVault/Infrastructure/EventRepository.cs` — add `ListSummariesAsync`.

---

## Task 0: Add NuGet package references

**Files:**
- Modify: `src/HookVault/HookVault.csproj`
- Modify: `tests/HookVault.Tests/HookVault.Tests.csproj`

- [ ] **Step 1: Add JwtBearer package to the main project**

In `src/HookVault/HookVault.csproj`, inside the existing `<ItemGroup>` that lists package references, add:

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.10" />
```

(Version 8.0.10 matches the existing `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.10 — keep the ASP.NET Core 8 line consistent.)

- [ ] **Step 2: Add Mvc.Testing package to the test project**

In `tests/HookVault.Tests/HookVault.Tests.csproj`, inside the existing `<ItemGroup>` that lists package references, add:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.10" />
```

- [ ] **Step 3: Verify build**

Run: `dotnet build --configuration Release --nologo`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/HookVault.csproj tests/HookVault.Tests/HookVault.Tests.csproj
git commit -m "chore: add jwt bearer + mvc.testing packages"
```

---

## Task 1: `JwtOptions` + `JwtTokenGenerator` (pure helpers, no wiring)

**Files:**
- Create: `src/HookVault/Auth/JwtOptions.cs`
- Create: `src/HookVault/Auth/JwtTokenGenerator.cs`
- Create: `tests/HookVault.Tests/JwtOptionsTests.cs`
- Create: `tests/HookVault.Tests/JwtTokenGeneratorTests.cs`

- [ ] **Step 1: Write the failing tests for `JwtOptions.FromConfiguration`**

Create `tests/HookVault.Tests/JwtOptionsTests.cs`:

```csharp
using HookVault.Auth;
using Microsoft.Extensions.Configuration;

namespace HookVault.Tests;

public sealed class JwtOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_reads_hookvault_env_keys()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = new string('s', 32),
            ["HOOKVAULT_JWT_ISSUER"] = "iss",
            ["HOOKVAULT_JWT_AUDIENCE"] = "aud",
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal(new string('s', 32), options.Secret);
        Assert.Equal("iss", options.Issuer);
        Assert.Equal("aud", options.Audience);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_jwt_section()
    {
        var config = Config(new()
        {
            ["Jwt:Secret"] = new string('s', 32),
            ["Jwt:Issuer"] = "iss2",
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal("iss2", options.Issuer);
        Assert.Equal("hookvault", options.Audience); // default
    }

    [Fact]
    public void FromConfiguration_defaults_issuer_and_audience()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = new string('s', 32),
        });

        var options = JwtOptions.FromConfiguration(config);

        Assert.Equal("hookvault", options.Issuer);
        Assert.Equal("hookvault", options.Audience);
    }

    [Fact]
    public void FromConfiguration_throws_when_secret_missing()
    {
        var config = Config([]);

        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(config));
        Assert.Contains("HOOKVAULT_JWT_SECRET", ex.Message);
    }

    [Fact]
    public void FromConfiguration_throws_when_secret_too_short()
    {
        var config = Config(new()
        {
            ["HOOKVAULT_JWT_SECRET"] = "tooshort",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => JwtOptions.FromConfiguration(config));
        Assert.Contains("32 bytes", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~JwtOptionsTests"`
Expected: build fails with `JwtOptions` not found.

- [ ] **Step 3: Implement `JwtOptions`**

Create `src/HookVault/Auth/JwtOptions.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Configuration;

namespace HookVault.Auth;

public sealed record JwtOptions(string Secret, string Issuer, string Audience)
{
    public const int MinimumSecretBytes = 32;

    public static JwtOptions FromConfiguration(IConfiguration config)
    {
        var secret = config["HOOKVAULT_JWT_SECRET"] ?? config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                "HOOKVAULT_JWT_SECRET must be set (min 32 bytes) before HookVault can start.");
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
```

`.NET idiom note:` `IConfiguration` is the unified config API in ASP.NET Core — it stacks providers (env vars, `appsettings.json`, command-line) and exposes them through string keys. `config["X:Y"]` works for both env var `X__Y` and an `appsettings.json` `{ "X": { "Y": "..." } }` block. Similar to Django's `settings` module, but composable.

- [ ] **Step 4: Run `JwtOptions` tests, verify they pass**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~JwtOptionsTests"`
Expected: `Passed: 5, Failed: 0`.

- [ ] **Step 5: Write the failing tests for `JwtTokenGenerator`**

Create `tests/HookVault.Tests/JwtTokenGeneratorTests.cs`:

```csharp
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

        var principal = handler.ValidateToken(token, parameters, out var validated);

        Assert.Equal("admin", principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal("hookvault", ((JwtSecurityToken)validated).Issuer);
        Assert.Contains("hookvault", ((JwtSecurityToken)validated).Audiences);
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
```

- [ ] **Step 6: Run generator tests, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~JwtTokenGeneratorTests"`
Expected: build fails with `JwtTokenGenerator` not found.

- [ ] **Step 7: Implement `JwtTokenGenerator`**

Create `src/HookVault/Auth/JwtTokenGenerator.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace HookVault.Auth;

public static class JwtTokenGenerator
{
    public static string Mint(JwtOptions options, string subject, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64),
            ],
            notBefore: now,
            expires: now.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

`.NET idiom note:` `JwtSecurityTokenHandler` lives in `System.IdentityModel.Tokens.Jwt` and is the standard mint/parse helper. `SymmetricSecurityKey` + `SigningCredentials` + `SecurityAlgorithms.HmacSha256` is the canonical HS256 setup. The handler is cheap to allocate; no need to cache it.

- [ ] **Step 8: Run generator tests, verify they pass**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~JwtTokenGenerator"`
Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 9: Full test run + format check**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 20, Failed: 0` (12 pre-existing + 5 options + 3 generator).

Run: `dotnet format --verify-no-changes`
Expected: exit 0 (no formatting issues).

- [ ] **Step 10: Commit**

```bash
git add src/HookVault/Auth tests/HookVault.Tests/JwtOptionsTests.cs tests/HookVault.Tests/JwtTokenGeneratorTests.cs
git commit -m "feat: add jwt options + token generator"
```

---

## Task 2: Wire JWT bearer authentication in `Program.cs`

This task wires the auth middleware. There are no protected endpoints yet, so the verification is: existing public endpoints still work, and startup fails fast when the secret is missing/short.

**Files:**
- Modify: `src/HookVault/Program.cs`
- Modify: `src/HookVault/Controllers/IngestController.cs`
- Modify: `src/HookVault/Controllers/HealthController.cs`
- Create: `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs`
- Create: `tests/HookVault.Tests/JwtAuthTests.cs`

- [ ] **Step 1: Add `[AllowAnonymous]` to existing public controllers**

Edit `src/HookVault/Controllers/IngestController.cs` — add the attribute and using.

Add at top with the other usings:
```csharp
using Microsoft.AspNetCore.Authorization;
```

Replace:
```csharp
[ApiController]
public class IngestController(
```
with:
```csharp
[ApiController]
[AllowAnonymous]
public class IngestController(
```

Edit `src/HookVault/Controllers/HealthController.cs` the same way.

Add:
```csharp
using Microsoft.AspNetCore.Authorization;
```

Change:
```csharp
[ApiController]
public class HealthController(
```
to:
```csharp
[ApiController]
[AllowAnonymous]
public class HealthController(
```

- [ ] **Step 2: Create the integration-test factory**

Create `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs`:

```csharp
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

public sealed class HookVaultWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestSecret = "test-secret-with-at-least-32-bytes-pad";
    public const string TestIssuer = "hookvault-test";
    public const string TestAudience = "hookvault-test";

    private readonly SqliteConnection _connection;

    public HookVaultWebApplicationFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public SqliteConnection Connection => _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HOOKVAULT_JWT_SECRET"] = TestSecret,
                ["HOOKVAULT_JWT_ISSUER"] = TestIssuer,
                ["HOOKVAULT_JWT_AUDIENCE"] = TestAudience,
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<HookVaultDbContext>));
            services.Remove(descriptor);

            services.AddDbContext<HookVaultDbContext>(opts => opts.UseSqlite(_connection));
        });
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
```

`.NET idiom note:` `WebApplicationFactory<TEntryPoint>` boots the real `Program.cs` in-process. `ConfigureAppConfiguration` layers extra config on top of what `WebApplicationBuilder` already loads (env vars, appsettings, etc.). `ConfigureServices` runs *after* the app's own `ConfigureServices`, so you can replace registered services — the pattern of "find descriptor, remove, re-add" swaps a real service for a test one.

- [ ] **Step 3: Write the failing auth wiring tests**

Create `tests/HookVault.Tests/JwtAuthTests.cs`:

```csharp
using System.Net;
using HookVault.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

public sealed class JwtAuthTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HookVaultWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Health_endpoint_is_anonymous_after_auth_is_wired()
    {
        // Override hookvault.json with an empty provider list so Health doesn't
        // depend on a real config file.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var existing = s.Single(d => d.ServiceType == typeof(HookVaultOptions));
            s.Remove(existing);
            s.AddSingleton(new HookVaultOptions { Providers = [] });
        }));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run the new tests, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~JwtAuthTests"`
Expected: 1 test, currently failing. The factory tries to build the host with auth wired, but no `AddAuthentication`/`AddAuthorization` is registered yet — the test should fail at host-build time or return a non-200 from `/api/health`.

- [ ] **Step 5: Wire JWT bearer authentication in `Program.cs`**

Open `src/HookVault/Program.cs`.

Replace these existing top-of-file lines:
```csharp
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.EntityFrameworkCore;
```

with:
```csharp
using System.Text;
using HookVault.Auth;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
```

Just before the line `builder.Services.AddSingleton(hookVaultOptions);` (right after `var hookVaultOptions = HookVaultOptions.Load(...)`), add:

```csharp
// --- JWT auth options ---
// Resolved at startup so a missing / short secret crashes immediately rather than
// at the first authenticated request. The .NET equivalent of Django's
// SECRET_KEY validation at boot.
var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(jwtOptions);
```

Just before `builder.Services.AddControllers();`, add the authentication + authorization registration:

```csharp
// --- Authentication ---
// AddJwtBearer registers the validation pipeline for incoming Bearer tokens.
// ClockSkew is tightened from the 5-minute default — on a single host, "expired" means expired.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();
```

In the middleware pipeline section (after `app.UseMiddleware<RawBodyMiddleware>();` and before `app.MapControllers();`), insert:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Final pipeline order should be:
```csharp
app.UseMiddleware<RawBodyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 21, Failed: 0` (20 pre-existing + 1 new auth test).

- [ ] **Step 7: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 8: Commit**

```bash
git add src/HookVault/Program.cs src/HookVault/Controllers/IngestController.cs src/HookVault/Controllers/HealthController.cs tests/HookVault.Tests/HookVaultWebApplicationFactory.cs tests/HookVault.Tests/JwtAuthTests.cs
git commit -m "feat: wire jwt bearer authentication"
```

---

## Task 3: `generate-token` CLI subcommand

**Files:**
- Create: `src/HookVault/Cli/GenerateTokenCommand.cs`
- Create: `tests/HookVault.Tests/GenerateTokenCommandTests.cs`
- Modify: `src/HookVault/Program.cs`

- [ ] **Step 1: Write the failing CLI tests**

Create `tests/HookVault.Tests/GenerateTokenCommandTests.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using HookVault.Auth;
using HookVault.Cli;
using Microsoft.IdentityModel.Tokens;

namespace HookVault.Tests;

[Collection("GenerateToken")] // serialises because we mutate env vars
public sealed class GenerateTokenCommandTests : IDisposable
{
    private const string SecretEnv = "HOOKVAULT_JWT_SECRET";
    private const string IssuerEnv = "HOOKVAULT_JWT_ISSUER";
    private const string AudienceEnv = "HOOKVAULT_JWT_AUDIENCE";

    private readonly string? _origSecret = Environment.GetEnvironmentVariable(SecretEnv);
    private readonly string? _origIssuer = Environment.GetEnvironmentVariable(IssuerEnv);
    private readonly string? _origAudience = Environment.GetEnvironmentVariable(AudienceEnv);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SecretEnv, _origSecret);
        Environment.SetEnvironmentVariable(IssuerEnv, _origIssuer);
        Environment.SetEnvironmentVariable(AudienceEnv, _origAudience);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = GenerateTokenCommand.Run(args, stdout, stderr);
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    [Fact]
    public void Missing_secret_exits_1_with_stderr_message()
    {
        Environment.SetEnvironmentVariable(SecretEnv, null);

        var (code, stdout, stderr) = Run();

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("HOOKVAULT_JWT_SECRET", stderr);
    }

    [Fact]
    public void Default_args_mint_admin_token_with_30day_expiry()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var before = DateTime.UtcNow;
        var (code, stdout, stderr) = Run();

        Assert.Equal(0, code);
        Assert.Empty(stderr);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(stdout);
        Assert.Equal("admin", jwt.Subject);
        Assert.InRange(jwt.ValidTo, before.AddDays(29), before.AddDays(31));
    }

    [Fact]
    public void Subject_and_expires_flags_are_respected()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var before = DateTime.UtcNow;
        var (code, stdout, _) = Run("--subject", "ci", "--expires", "1h");

        Assert.Equal(0, code);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(stdout);
        Assert.Equal("ci", jwt.Subject);
        Assert.InRange(jwt.ValidTo, before.AddMinutes(59), before.AddMinutes(61));
    }

    [Fact]
    public void Bad_expires_format_exits_1()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));

        var (code, _, stderr) = Run("--expires", "frog");

        Assert.Equal(1, code);
        Assert.Contains("--expires", stderr);
    }

    [Fact]
    public void Generated_token_validates_against_runtime_parameters()
    {
        Environment.SetEnvironmentVariable(SecretEnv, new string('s', 32));
        Environment.SetEnvironmentVariable(IssuerEnv, "hookvault");
        Environment.SetEnvironmentVariable(AudienceEnv, "hookvault");

        var (_, token, _) = Run();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "hookvault",
            ValidAudience = "hookvault",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('s', 32))),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        Assert.Equal("admin", principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
    }
}
```

- [ ] **Step 2: Run, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~GenerateTokenCommand"`
Expected: build fails — `GenerateTokenCommand` does not exist.

- [ ] **Step 3: Implement `GenerateTokenCommand`**

Create `src/HookVault/Cli/GenerateTokenCommand.cs`:

```csharp
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
```

- [ ] **Step 4: Hook the CLI into `Program.cs`**

In `src/HookVault/Program.cs`, add this as the very first executable line (before `var builder = WebApplication.CreateBuilder(args);`):

```csharp
using HookVault.Cli;
```
(if not already present — add to using block at top of file)

Then at the top of the executable code:

```csharp
if (args.Length > 0 && args[0] == "generate-token")
{
    return GenerateTokenCommand.Run(args[1..]);
}
```

This requires `Program.cs` to have an `int` return type. With top-level statements, simply add `return 0;` *after* `app.Run();` — but `app.Run()` blocks until shutdown, so the easier fix is to make the file's implicit `Main` return `int` by ensuring the last statement returns. Replace `app.Run();` with:

```csharp
app.Run();
return 0;
```

(Top-level statements infer the entry point's return type from the trailing expression. Once any branch returns `int`, all branches must.)

- [ ] **Step 5: Run all tests**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 26, Failed: 0` (21 prior + 5 CLI tests).

- [ ] **Step 6: Smoke-test the CLI**

Run (PowerShell):
```powershell
$env:HOOKVAULT_JWT_SECRET = "test-secret-with-at-least-32-bytes-pad"
dotnet run --project src/HookVault --configuration Release -- generate-token --subject admin --expires 1h
```
Expected: a single line of output that looks like `eyJhbGciOiJIUzI1NiIs...` and no other startup logs.

Run (no secret set, in a fresh shell):
```powershell
Remove-Item Env:\HOOKVAULT_JWT_SECRET -ErrorAction SilentlyContinue
dotnet run --project src/HookVault --configuration Release -- generate-token
```
Expected: exit code 1, stderr contains `HOOKVAULT_JWT_SECRET`.

- [ ] **Step 7: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 8: Commit**

```bash
git add src/HookVault/Cli src/HookVault/Program.cs tests/HookVault.Tests/GenerateTokenCommandTests.cs
git commit -m "feat: add generate-token cli subcommand"
```

---

## Task 4: `EventsController` — list + detail (+ DTOs + repo addition)

This is the largest task. Splits into: DTOs, repo addition, controller skeleton + two endpoints, integration tests.

**Files:**
- Create: `src/HookVault/Contracts/EventSummary.cs`
- Create: `src/HookVault/Contracts/EventDetail.cs`
- Create: `src/HookVault/Contracts/ListEventsResponse.cs`
- Create: `src/HookVault/Contracts/ApiError.cs`
- Create: `src/HookVault/Controllers/EventsController.cs`
- Create: `tests/HookVault.Tests/EventsControllerTests.cs`
- Modify: `src/HookVault/Infrastructure/EventRepository.cs`

- [ ] **Step 1: Create the response DTOs**

Create `src/HookVault/Contracts/EventSummary.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record EventSummary(
    Guid Id,
    string Provider,
    string Status,
    DateTimeOffset ReceivedAt,
    bool? SignatureValid,
    int? ForwardStatusCode,
    int ReplayCount,
    DateTimeOffset? ForwardedAt);
```

Create `src/HookVault/Contracts/EventDetail.cs`:

```csharp
using System.Text.Json;

namespace HookVault.Contracts;

public sealed record EventDetail(
    Guid Id,
    string Provider,
    string Path,
    JsonElement Headers,
    string Body,
    DateTimeOffset ReceivedAt,
    string? SignatureHeader,
    bool? SignatureValid,
    JsonElement? ValidationDetails,
    string ForwardUrl,
    DateTimeOffset? ForwardedAt,
    int? ForwardStatusCode,
    string? ForwardError,
    string Status,
    int ReplayCount,
    DateTimeOffset? LastReplayAt,
    string? LastError);
```

Create `src/HookVault/Contracts/ListEventsResponse.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record ListEventsResponse(
    IReadOnlyList<EventSummary> Items,
    int Total,
    int Limit,
    int Offset);
```

Create `src/HookVault/Contracts/ApiError.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record ApiError(string Error, string? Code = null);
```

- [ ] **Step 2: Add `ListSummariesAsync` to `EventRepository`**

Edit `src/HookVault/Infrastructure/EventRepository.cs`. Add at top with other usings:

```csharp
using HookVault.Contracts;
```

Add this method (paste it after the existing `ListAsync`):

```csharp
public async Task<(List<EventSummary> Items, int Total)> ListSummariesAsync(
    string? provider, string? status, DateTimeOffset? from, DateTimeOffset? to,
    int limit = 50, int offset = 0, CancellationToken ct = default)
{
    var query = db.Events.AsQueryable();

    if (!string.IsNullOrEmpty(provider))
        query = query.Where(e => e.Provider == provider);

    if (!string.IsNullOrEmpty(status) && Enum.TryParse<EventStatus>(status, true, out var s))
        query = query.Where(e => e.Status == s);

    if (from.HasValue)
        query = query.Where(e => e.ReceivedAt >= from.Value);

    if (to.HasValue)
        query = query.Where(e => e.ReceivedAt <= to.Value);

    var total = await query.CountAsync(ct);

    var items = await query
        .OrderByDescending(e => e.ReceivedAt)
        .Skip(offset)
        .Take(limit)
        .Select(e => new EventSummary(
            e.Id,
            e.Provider,
            e.Status.ToString(),
            e.ReceivedAt,
            e.SignatureValid,
            e.ForwardStatusCode,
            e.ReplayCount,
            e.ForwardedAt))
        .ToListAsync(ct);

    return (items, total);
}
```

- [ ] **Step 3: Write the failing tests for list + detail**

Create `tests/HookVault.Tests/EventsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HookVault.Configuration;

namespace HookVault.Tests;

public sealed class EventsControllerTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new HookVaultWebApplicationFactory();
        // Force the factory to bind config (creates a client to materialise the host)
        _factory = (HookVaultWebApplicationFactory)_factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                var existing = s.Single(d => d.ServiceType == typeof(HookVaultOptions));
                s.Remove(existing);
                s.AddSingleton(new HookVaultOptions { Providers = [] });
            });
        });
        await EnsureSchemaAsync();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private async Task EnsureSchemaAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    private HttpClient AuthedClient()
    {
        var options = new JwtOptions(
            HookVaultWebApplicationFactory.TestSecret,
            HookVaultWebApplicationFactory.TestIssuer,
            HookVaultWebApplicationFactory.TestAudience);
        var token = JwtTokenGenerator.Mint(options, "test", TimeSpan.FromMinutes(5));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task SeedAsync(params WebhookEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        db.Events.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static WebhookEvent NewEvent(string provider = "stripe",
        EventStatus status = EventStatus.Forwarded,
        DateTimeOffset? receivedAt = null)
    => new()
    {
        Provider = provider,
        Path = $"/api/ingest/{provider}",
        Headers = "{\"X-Test\":\"yes\"}",
        Body = "{}",
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
        ForwardUrl = "http://localhost/forward",
        ForwardStatusCode = 200,
        SignatureValid = true,
        Status = status,
    };

    [Fact]
    public async Task List_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_with_token_returns_empty_when_no_events()
    {
        var response = await AuthedClient().GetAsync("/api/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Empty(body.Items);
        Assert.Equal(0, body.Total);
        Assert.Equal(50, body.Limit);
        Assert.Equal(0, body.Offset);
    }

    [Fact]
    public async Task List_returns_events_in_received_at_desc_order()
    {
        var older = NewEvent(receivedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = NewEvent(receivedAt: DateTimeOffset.UtcNow);
        await SeedAsync(older, newer);

        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");

        Assert.NotNull(body);
        Assert.Equal(2, body.Total);
        Assert.Equal(newer.Id, body.Items[0].Id);
        Assert.Equal(older.Id, body.Items[1].Id);
    }

    [Fact]
    public async Task List_filters_by_provider()
    {
        await SeedAsync(NewEvent("stripe"), NewEvent("github"));

        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events?provider=stripe");

        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("stripe", body.Items[0].Provider);
    }

    [Fact]
    public async Task List_filters_by_status_case_insensitive()
    {
        await SeedAsync(
            NewEvent(status: EventStatus.Forwarded),
            NewEvent(status: EventStatus.ForwardFailed));

        var body = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events?status=forwardfailed");

        Assert.NotNull(body);
        Assert.Equal(1, body.Total);
        Assert.Equal("ForwardFailed", body.Items[0].Status);
    }

    [Fact]
    public async Task List_rejects_invalid_status_with_400()
    {
        var response = await AuthedClient().GetAsync("/api/events?status=notarealstatus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Contains("status", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_clamps_limit_to_500()
    {
        var response = await AuthedClient().GetAsync("/api/events?limit=10000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Equal(500, body.Limit);
    }

    [Fact]
    public async Task List_clamps_limit_to_1_when_zero_or_negative()
    {
        var response = await AuthedClient().GetAsync("/api/events?limit=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ListEventsResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Limit);
    }

    [Fact]
    public async Task Detail_returns_404_for_missing_id()
    {
        var response = await AuthedClient().GetAsync($"/api/events/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_returns_full_payload_with_parsed_json()
    {
        var evt = NewEvent();
        evt.Headers = "{\"X-Test\":\"abc\"}";
        evt.ValidationDetails = "{\"isValid\":true}";
        await SeedAsync(evt);

        var response = await AuthedClient().GetAsync($"/api/events/{evt.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("abc", doc.RootElement.GetProperty("headers").GetProperty("X-Test").GetString());
        Assert.True(doc.RootElement.GetProperty("validationDetails").GetProperty("isValid").GetBoolean());
    }
}
```

- [ ] **Step 4: Run, verify tests fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~EventsControllerTests"`
Expected: build fails — `EventsController` does not exist; tests reference `Contracts` types that may not be discoverable yet (they should compile after Step 1).

- [ ] **Step 5: Implement `EventsController` with list + detail**

Create `src/HookVault/Controllers/EventsController.cs`:

```csharp
using System.Text.Json;
using HookVault.Contracts;
using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[ApiController]
[Authorize]
[Route("api/events")]
public sealed class EventsController(
    EventRepository repo,
    ILogger<EventsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(status) && !Enum.TryParse<EventStatus>(status, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<EventStatus>());
            return BadRequest(new ApiError(
                $"Invalid status '{status}'. Valid values: {valid}.",
                "invalid_status"));
        }

        var clampedLimit = Math.Clamp(limit ?? 50, 1, 500);
        var clampedOffset = Math.Max(offset ?? 0, 0);

        var (items, total) = await repo.ListSummariesAsync(
            provider, status, from, to, clampedLimit, clampedOffset, ct);

        return Ok(new ListEventsResponse(items, total, clampedLimit, clampedOffset));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var evt = await repo.GetByIdAsync(id, ct);
        if (evt is null)
            return NotFound(new ApiError("Event not found.", "event_not_found"));

        return Ok(ToDetail(evt));
    }

    private static EventDetail ToDetail(WebhookEvent evt) => new(
        evt.Id,
        evt.Provider,
        evt.Path,
        ParseJsonOrEmpty(evt.Headers),
        evt.Body,
        evt.ReceivedAt,
        evt.SignatureHeader,
        evt.SignatureValid,
        TryParseJson(evt.ValidationDetails),
        evt.ForwardUrl,
        evt.ForwardedAt,
        evt.ForwardStatusCode,
        evt.ForwardError,
        evt.Status.ToString(),
        evt.ReplayCount,
        evt.LastReplayAt,
        evt.LastError);

    private static JsonElement ParseJsonOrEmpty(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("{}").RootElement;
        try
        {
            return JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static JsonElement? TryParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

`.NET idiom note:` `JsonDocument.Parse(...).RootElement` returns a `JsonElement` that can be serialised as raw JSON by `System.Text.Json` (the default ASP.NET Core serializer). That's why `EventDetail.Headers` is a `JsonElement` instead of a `string` — the response contains a real JSON object, not a JSON-escaped string. Trade-off: the `JsonDocument` parents would normally need disposal; for short-lived response bodies this is fine because the framework holds them only until serialisation completes.

- [ ] **Step 6: Run all tests**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 37, Failed: 0` (26 prior + 11 new EventsController tests).

If `Detail_returns_full_payload_with_parsed_json` fails, the most likely cause is ASP.NET's default JSON naming policy (camelCase). The test reads `headers` (lowercase) — the response should have `headers` because the DTO property is `Headers` and camelCase is the default. If the field comes back as `Headers`, add `builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);` to `Program.cs`. (It should already work — this is a fallback check.)

- [ ] **Step 7: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 8: Commit**

```bash
git add src/HookVault/Contracts src/HookVault/Controllers/EventsController.cs src/HookVault/Infrastructure/EventRepository.cs tests/HookVault.Tests/EventsControllerTests.cs
git commit -m "feat: add events controller list + detail"
```

---

## Task 5: `EventsController` — single replay + bulk replay

**Files:**
- Create: `src/HookVault/Contracts/ReplayEnqueuedResponse.cs`
- Create: `src/HookVault/Contracts/ReplayBulkResponse.cs`
- Modify: `src/HookVault/Controllers/EventsController.cs`
- Modify: `tests/HookVault.Tests/EventsControllerTests.cs`

- [ ] **Step 1: Create replay DTOs**

Create `src/HookVault/Contracts/ReplayEnqueuedResponse.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record ReplayEnqueuedResponse(Guid EventId, string Status);
```

Create `src/HookVault/Contracts/ReplayBulkResponse.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record ReplayBulkResponse(int Enqueued, string? Provider, string? Status);
```

- [ ] **Step 2: Add the failing replay tests**

Edit `tests/HookVault.Tests/EventsControllerTests.cs`. Add at the top of the file alongside the existing usings:

```csharp
using HookVault.Services;
```

Append these test methods inside the `EventsControllerTests` class (before the closing brace):

```csharp
private ReplayQueue Queue() => _factory.Services.GetRequiredService<ReplayQueue>();

private async Task<List<Guid>> DrainQueueAsync(int expected, int timeoutMs = 1000)
{
    var queue = Queue();
    var ids = new List<Guid>();
    using var cts = new CancellationTokenSource(timeoutMs);
    try
    {
        while (ids.Count < expected)
        {
            var id = await queue.Reader.ReadAsync(cts.Token);
            ids.Add(id);
        }
    }
    catch (OperationCanceledException) { }
    return ids;
}

[Fact]
public async Task ReplaySingle_returns_404_when_event_missing()
{
    var response = await AuthedClient().PostAsync($"/api/events/{Guid.NewGuid()}/replay", null);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task ReplaySingle_enqueues_event_and_returns_202()
{
    var evt = NewEvent();
    await SeedAsync(evt);

    var response = await AuthedClient().PostAsync($"/api/events/{evt.Id}/replay", null);

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<ReplayEnqueuedResponse>();
    Assert.NotNull(body);
    Assert.Equal(evt.Id, body.EventId);
    Assert.Equal("Queued", body.Status);

    var queued = await DrainQueueAsync(1);
    Assert.Single(queued);
    Assert.Equal(evt.Id, queued[0]);
}

[Fact]
public async Task ReplayBulk_returns_202_with_zero_when_nothing_to_replay()
{
    var response = await AuthedClient().PostAsync("/api/events/replay-failed", null);

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
    Assert.NotNull(body);
    Assert.Equal(0, body.Enqueued);
}

[Fact]
public async Task ReplayBulk_enqueues_both_ForwardFailed_and_ReplayFailed_by_default()
{
    await SeedAsync(
        NewEvent(status: EventStatus.ForwardFailed),
        NewEvent(status: EventStatus.ReplayFailed),
        NewEvent(status: EventStatus.Forwarded));

    var response = await AuthedClient().PostAsync("/api/events/replay-failed", null);

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
    Assert.NotNull(body);
    Assert.Equal(2, body.Enqueued);

    var queued = await DrainQueueAsync(2);
    Assert.Equal(2, queued.Count);
}

[Fact]
public async Task ReplayBulk_filters_by_status_when_provided()
{
    await SeedAsync(
        NewEvent(status: EventStatus.ForwardFailed),
        NewEvent(status: EventStatus.ReplayFailed));

    var response = await AuthedClient().PostAsync("/api/events/replay-failed?status=ForwardFailed", null);

    var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
    Assert.NotNull(body);
    Assert.Equal(1, body.Enqueued);
    Assert.Equal("ForwardFailed", body.Status);
}

[Fact]
public async Task ReplayBulk_filters_by_provider_when_provided()
{
    await SeedAsync(
        NewEvent("stripe", EventStatus.ForwardFailed),
        NewEvent("github", EventStatus.ForwardFailed));

    var response = await AuthedClient().PostAsync("/api/events/replay-failed?provider=stripe", null);

    var body = await response.Content.ReadFromJsonAsync<ReplayBulkResponse>();
    Assert.NotNull(body);
    Assert.Equal(1, body.Enqueued);
    Assert.Equal("stripe", body.Provider);
}

[Fact]
public async Task ReplayBulk_rejects_invalid_status_with_400()
{
    var response = await AuthedClient().PostAsync("/api/events/replay-failed?status=Forwarded", null);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var error = await response.Content.ReadFromJsonAsync<ApiError>();
    Assert.NotNull(error);
    Assert.Contains("ForwardFailed", error.Error);
}
```

- [ ] **Step 3: Run replay tests, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~EventsControllerTests"`
Expected: existing list/detail tests pass; new replay tests fail with 404/405 (route not found).

- [ ] **Step 4: Add replay actions to the controller**

Edit `src/HookVault/Controllers/EventsController.cs`.

Update the using block at the top:
```csharp
using HookVault.Services;
```

Update the constructor parameter list:
```csharp
public sealed class EventsController(
    EventRepository repo,
    ReplayQueue queue,
    ILogger<EventsController> logger) : ControllerBase
```

Add these action methods inside the class (after `Detail`):

```csharp
[HttpPost("{id:guid}/replay")]
public async Task<IActionResult> Replay(Guid id, CancellationToken ct)
{
    var evt = await repo.GetByIdAsync(id, ct);
    if (evt is null)
        return NotFound(new ApiError("Event not found.", "event_not_found"));

    await queue.EnqueueAsync(id, ct);
    logger.LogInformation("Enqueued replay for event {EventId} (provider {Provider})", id, evt.Provider);

    return Accepted(new ReplayEnqueuedResponse(id, "Queued"));
}

[HttpPost("replay-failed")]
public async Task<IActionResult> ReplayFailed(
    [FromQuery] string? provider,
    [FromQuery] string? status,
    CancellationToken ct)
{
    EventStatus? statusFilter = null;
    if (!string.IsNullOrEmpty(status))
    {
        if (!Enum.TryParse<EventStatus>(status, ignoreCase: true, out var parsed)
            || (parsed != EventStatus.ForwardFailed && parsed != EventStatus.ReplayFailed))
        {
            return BadRequest(new ApiError(
                "Invalid status. Must be ForwardFailed or ReplayFailed (case-insensitive).",
                "invalid_status"));
        }
        statusFilter = parsed;
    }

    var failed = await repo.GetFailedAsync(provider, ct);
    if (statusFilter is { } s)
        failed = failed.Where(e => e.Status == s).ToList();

    foreach (var evt in failed)
        await queue.EnqueueAsync(evt.Id, ct);

    logger.LogInformation(
        "Bulk replay enqueued {Count} events (provider={Provider}, status={Status})",
        failed.Count, provider ?? "*", statusFilter?.ToString() ?? "*");

    return Accepted(new ReplayBulkResponse(failed.Count, provider, statusFilter?.ToString()));
}
```

- [ ] **Step 5: Run tests, verify all pass**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 44, Failed: 0` (37 prior + 7 new replay tests).

If `ReplaySingle_enqueues_event_and_returns_202` fails because the `ReplayQueue` is consumed by the background worker before the test drains it: the test factory uses an in-memory SQLite DB so the worker's "look up event by id" will succeed and the worker may consume the item before `DrainQueueAsync` reads. Mitigation: in the factory, register a `null`-handler `HttpClient` for the `"forwarder"` named client that never completes (or returns 500) — but actually the cleanest fix is in `HookVaultWebApplicationFactory.ConfigureWebHost`: remove the `ReplayWorker` hosted service so the queue is only drained by tests.

If you need it, add this inside `ConfigureWebHost` after the existing service swaps:

```csharp
services.RemoveAll<IHostedService>();
```

(Add `using Microsoft.Extensions.DependencyInjection.Extensions;` at the top of the file for `RemoveAll`.)

This removes the `ReplayWorker` (and any other hosted services) for the test host. Background work isn't being tested in this file — `ReplayWorkerTests.cs` already covers it with its own host.

- [ ] **Step 6: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Contracts/ReplayEnqueuedResponse.cs src/HookVault/Contracts/ReplayBulkResponse.cs src/HookVault/Controllers/EventsController.cs tests/HookVault.Tests/EventsControllerTests.cs tests/HookVault.Tests/HookVaultWebApplicationFactory.cs
git commit -m "feat: add events controller replay + bulk-replay"
```

---

## Task 6: `EventsController` — DELETE with `?confirm=true` guard

**Files:**
- Create: `src/HookVault/Contracts/DeleteResponse.cs`
- Modify: `src/HookVault/Controllers/EventsController.cs`
- Modify: `tests/HookVault.Tests/EventsControllerTests.cs`

- [ ] **Step 1: Create the delete DTO**

Create `src/HookVault/Contracts/DeleteResponse.cs`:

```csharp
namespace HookVault.Contracts;

public sealed record DeleteResponse(int Deleted, string? Provider);
```

- [ ] **Step 2: Write the failing delete tests**

Append to `tests/HookVault.Tests/EventsControllerTests.cs` (inside the class):

```csharp
[Fact]
public async Task Delete_without_confirm_returns_400()
{
    var response = await AuthedClient().DeleteAsync("/api/events");

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var error = await response.Content.ReadFromJsonAsync<ApiError>();
    Assert.NotNull(error);
    Assert.Contains("confirm", error.Error, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task Delete_with_confirm_removes_all_events()
{
    await SeedAsync(NewEvent("stripe"), NewEvent("github"));

    var response = await AuthedClient().DeleteAsync("/api/events?confirm=true");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<DeleteResponse>();
    Assert.NotNull(body);
    Assert.Equal(2, body.Deleted);
    Assert.Null(body.Provider);

    var list = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");
    Assert.NotNull(list);
    Assert.Equal(0, list.Total);
}

[Fact]
public async Task Delete_with_provider_filter_only_removes_matching()
{
    await SeedAsync(NewEvent("stripe"), NewEvent("github"));

    var response = await AuthedClient().DeleteAsync("/api/events?confirm=true&provider=stripe");

    var body = await response.Content.ReadFromJsonAsync<DeleteResponse>();
    Assert.NotNull(body);
    Assert.Equal(1, body.Deleted);
    Assert.Equal("stripe", body.Provider);

    var list = await AuthedClient().GetFromJsonAsync<ListEventsResponse>("/api/events");
    Assert.NotNull(list);
    Assert.Equal(1, list.Total);
    Assert.Equal("github", list.Items[0].Provider);
}

[Fact]
public async Task Delete_confirm_is_case_insensitive()
{
    await SeedAsync(NewEvent());

    var response = await AuthedClient().DeleteAsync("/api/events?confirm=TRUE");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task Delete_without_token_returns_401()
{
    var response = await _factory.CreateClient().DeleteAsync("/api/events?confirm=true");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

- [ ] **Step 3: Run tests, verify they fail**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~EventsControllerTests"`
Expected: new delete tests fail (no DELETE route registered).

- [ ] **Step 4: Add the DELETE action**

Edit `src/HookVault/Controllers/EventsController.cs`. Add this action method:

```csharp
[HttpDelete]
public async Task<IActionResult> Purge(
    [FromQuery] string? provider,
    [FromQuery] string? confirm,
    CancellationToken ct)
{
    if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
    {
        return BadRequest(new ApiError(
            "Pass ?confirm=true to delete events.",
            "delete_confirm_required"));
    }

    var deleted = await repo.DeleteAsync(provider, ct);
    logger.LogWarning(
        "Deleted {Count} events (provider={Provider})",
        deleted, provider ?? "*");

    return Ok(new DeleteResponse(deleted, provider));
}
```

- [ ] **Step 5: Run all tests**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 49, Failed: 0` (44 prior + 5 new delete tests).

- [ ] **Step 6: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Contracts/DeleteResponse.cs src/HookVault/Controllers/EventsController.cs tests/HookVault.Tests/EventsControllerTests.cs
git commit -m "feat: add events controller delete with confirm guard"
```

---

## Task 7: Standardise model-validation error response shape

ASP.NET's default `[ApiController]` 400 returns `ValidationProblemDetails`, which has a different shape from `ApiError`. Override the factory so clients only ever see the `ApiError` shape on 400s for our endpoints.

**Files:**
- Modify: `src/HookVault/Program.cs`
- Create: `tests/HookVault.Tests/ApiErrorShapeTests.cs`

- [ ] **Step 1: Write the failing test for the unified error shape**

Create `tests/HookVault.Tests/ApiErrorShapeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HookVault.Auth;
using HookVault.Contracts;

namespace HookVault.Tests;

public sealed class ApiErrorShapeTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HookVaultWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpClient AuthedClient()
    {
        var options = new JwtOptions(
            HookVaultWebApplicationFactory.TestSecret,
            HookVaultWebApplicationFactory.TestIssuer,
            HookVaultWebApplicationFactory.TestAudience);
        var token = JwtTokenGenerator.Mint(options, "test", TimeSpan.FromMinutes(5));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Model_binding_failure_returns_ApiError_shape()
    {
        // `limit` is typed as int? in the controller — supplying a non-int triggers
        // model binding failure, which goes through InvalidModelStateResponseFactory.
        var response = await AuthedClient().GetAsync("/api/events?limit=notanumber");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Response body should contain an `error` property (ApiError shape).");
        // Should NOT contain `errors` (the ValidationProblemDetails shape).
        Assert.False(doc.RootElement.TryGetProperty("errors", out _),
            "Response body should not contain `errors` (default ValidationProblemDetails shape).");
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --configuration Release --nologo --filter "FullyQualifiedName~ApiErrorShapeTests"`
Expected: test fails — response body has `errors` (default `ValidationProblemDetails`), not `error`.

- [ ] **Step 3: Add the response factory override in `Program.cs`**

Edit `src/HookVault/Program.cs`. Add to the usings:
```csharp
using HookVault.Contracts;
using Microsoft.AspNetCore.Mvc;
```

Replace:
```csharp
builder.Services.AddControllers();
```

with:
```csharp
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(opts =>
    {
        opts.InvalidModelStateResponseFactory = context =>
        {
            var first = context.ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .SelectMany(kv => kv.Value!.Errors.Select(e => (Field: kv.Key, e.ErrorMessage)))
                .FirstOrDefault();

            var message = string.IsNullOrEmpty(first.Field)
                ? "Invalid request."
                : $"Invalid value for '{first.Field}': {first.ErrorMessage}";

            return new BadRequestObjectResult(new ApiError(message, "invalid_request"));
        };
    });
```

`.NET idiom note:` `[ApiController]` enables automatic model validation — when binding fails (e.g. an int parameter receives `"notanumber"`), the action never runs; instead the framework invokes `InvalidModelStateResponseFactory` with the model state. Overriding it gives us a single error shape across the controller surface. Equivalent to Django REST Framework's `exception_handler` for validation errors.

- [ ] **Step 4: Run all tests**

Run: `dotnet test --configuration Release --nologo`
Expected: `Passed: 50, Failed: 0` (49 prior + 1 new shape test).

- [ ] **Step 5: Format check**

Run: `dotnet format --verify-no-changes`
Expected: exit 0.

- [ ] **Step 6: Commit**

```bash
git add src/HookVault/Program.cs tests/HookVault.Tests/ApiErrorShapeTests.cs
git commit -m "feat: standardise api error response shape"
```

---

## Final verification

- [ ] **Run the full test suite**

```bash
dotnet build --configuration Release --nologo
dotnet test --configuration Release --nologo
dotnet format --verify-no-changes
```

Expected:
- Build: 0 warnings, 0 errors.
- Tests: 50 passed, 0 failed.
- Format: exit 0.

- [ ] **Smoke-test the running app end-to-end**

Run in one terminal:
```powershell
$env:HOOKVAULT_JWT_SECRET = "smoke-secret-with-at-least-32-bytes!"
dotnet run --project src/HookVault --configuration Release
```

In another terminal:
```powershell
$env:HOOKVAULT_JWT_SECRET = "smoke-secret-with-at-least-32-bytes!"
$token = dotnet run --project src/HookVault --configuration Release -- generate-token --subject smoke --expires 1h
# (capture takes a couple of seconds on cold build; subsequent runs are quick)

# Anonymous → 401
curl.exe -i http://localhost:5xxx/api/events           # use real port from kestrel startup log

# Authed → 200 with empty list
curl.exe -i -H "Authorization: Bearer $token" http://localhost:5xxx/api/events

# Delete without confirm → 400
curl.exe -i -X DELETE -H "Authorization: Bearer $token" http://localhost:5xxx/api/events
```

- [ ] **Inspect the commit history**

```bash
git log --oneline feat/phase-3-management-api
```

Expected (oldest to newest after the spec commit):
```
<sha> feat: standardise api error response shape
<sha> feat: add events controller delete with confirm guard
<sha> feat: add events controller replay + bulk-replay
<sha> feat: add events controller list + detail
<sha> feat: add generate-token cli subcommand
<sha> feat: wire jwt bearer authentication
<sha> feat: add jwt options + token generator
<sha> chore: add jwt bearer + mvc.testing packages
<sha> docs: add phase 3 management api + jwt auth design
```

---

## What this plan does NOT do

- No Docker image (Phase 4).
- No README or example provider configs (Phase 4/5).
- No PR creation — finishing-a-development-branch handles that.
- No `appsettings.Development.json` JWT defaults — keep secrets out of the repo. Developer sets env vars.
- No refresh token endpoint, no scopes/roles, no rate limits, no audit logging.
