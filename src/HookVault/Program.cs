using System.Data;
using System.Text;
using HookVault.Auth;
using HookVault.Cli;
using HookVault.Configuration;
using HookVault.Contracts;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using HookVaultSignatureValidator = HookVault.Services.SignatureValidator;

if (args.Length > 0 && args[0] == "generate-token")
{
    return GenerateTokenCommand.Run(args[1..]);
}

var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Startup");

WebApplicationBuilder builder;
HookVaultOptions hookVaultOptions;
JwtOptions jwtOptions;
bool noAuth;

try
{
    builder = WebApplication.CreateBuilder(args);

    // --- Configuration ---
    // Load hookvault.json at startup; registered as a singleton so controllers can inject it.
    // Singleton = one shared instance for the lifetime of the app (like a module-level global).
    hookVaultOptions = HookVaultOptions.Load(bootstrapLogger);
    builder.Services.AddSingleton(hookVaultOptions);

    // HOOKVAULT_NO_AUTH=true disables [Authorize] enforcement (single-user local dev).
    // Read it BEFORE JwtOptions resolution so we can substitute an ephemeral key when set.
    noAuth = string.Equals(
        Environment.GetEnvironmentVariable("HOOKVAULT_NO_AUTH"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (noAuth)
    {
        jwtOptions = JwtOptions.Ephemeral();
        bootstrapLogger.LogWarning(
            "HOOKVAULT_NO_AUTH=true — using ephemeral in-memory JWT key. " +
            "Tokens issued by this process will not validate after restart.");
    }
    else
    {
        jwtOptions = JwtOptions.FromConfiguration(builder.Configuration);
    }
    builder.Services.AddSingleton(jwtOptions);

    // Warn — don't fail — when a provider's signature env var is unset. The container can
    // still start and capture events; signature validation will simply return an error
    // until the operator sets the var.
    foreach (var provider in hookVaultOptions.Providers)
    {
        if (provider.Validation is { } v
            && !string.IsNullOrWhiteSpace(v.SecretEnvVar)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v.SecretEnvVar)))
        {
            bootstrapLogger.LogWarning(
                "Provider '{Provider}': env var '{EnvVar}' is not set; " +
                "signature validation will fail until you set it.",
                provider.Name, v.SecretEnvVar);
        }
    }
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
{
    bootstrapLogger.LogCritical("HookVault failed to start: {Message}", ex.Message);
    return 1;
}

// --- Database ---
// If DATABASE_URL is set, use PostgreSQL; otherwise default to SQLite.
// AddDbContext registers the context as Scoped (one instance per HTTP request).
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    // Heroku / Render / Railway / docker-compose all emit DATABASE_URL in the
    // postgres://user:pass@host:port/db URI form, but Npgsql's connection-string
    // parser only accepts the Host=...;Port=... key-value form. Translate when
    // we detect a URI scheme; pass through anything else verbatim so power users
    // can still set the native form (with sslmode, pooling, etc.) directly.
    var npgsqlConnectionString = NormalizePostgresConnectionString(databaseUrl);
    builder.Services.AddDbContext<HookVaultDbContext>(opts =>
        opts.UseNpgsql(npgsqlConnectionString)
            .ConfigureWarnings(SuppressCrossProviderSnapshotWarnings));
}
else
{
    var dbPath = Environment.GetEnvironmentVariable("SQLITE_PATH") ?? "/data/hookvault.db";
    builder.Services.AddDbContext<HookVaultDbContext>(opts =>
        opts.UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(SuppressCrossProviderSnapshotWarnings));
}

// --- Application Services ---
// Scoped: EventRepository wraps the Scoped DbContext — must also be Scoped.
builder.Services.AddScoped<EventRepository>();

// Transient: each scheme is stateless; new instance each time is fine.
builder.Services.AddTransient<HookVault.Services.Schemes.IIngestSignatureScheme,
                              HookVault.Services.Schemes.SingleHeaderHmacScheme>();
builder.Services.AddTransient<HookVault.Services.Schemes.IIngestSignatureScheme,
                              HookVault.Services.Schemes.SvixHmacScheme>();

// Transient: SignatureValidator is stateless; new instance each time is fine.
builder.Services.AddTransient<HookVaultSignatureValidator>();

// Scoped: EventForwarder holds no state; Scoped is appropriate since it depends on
// EventRepository (Scoped).
builder.Services.AddScoped<EventForwarder>();

// HttpClient: IHttpClientFactory manages pooled HttpMessageHandlers to avoid socket exhaustion.
// This is the .NET best-practice replacement for newing up HttpClient directly.
// Timeout defaults to 30s (configurable via HOOKVAULT_FORWARD_TIMEOUT_SECONDS, clamped 1-300);
// the .NET default of 100s blocks the single-reader replay worker for ~7 minutes per
// unreachable target across 4 attempts.
var forwardTimeout = ParseForwardTimeoutEnv(bootstrapLogger);
builder.Services.AddHttpClient("forwarder", c => c.Timeout = forwardTimeout);

// Singleton: owns the Channel<Guid> for the application lifetime.
builder.Services.AddSingleton<ReplayQueue>();

// Singleton: owns the Channel<EventNotification> for SSE fanout to connected clients.
builder.Services.AddSingleton<EventNotifier>();

// Singleton: hosts all custom Prometheus metric instruments for the app lifetime.
builder.Services.AddSingleton<HookVault.Observability.HookVaultMeter>();

// Hosted service: BackgroundService started on app start, stopped on graceful shutdown.
builder.Services.AddHostedService<ReplayWorker>();

// Singleton: registered before the worker so the hosted-service factory can resolve it.
builder.Services.AddSingleton(_ =>
{
    var maxEvents = int.TryParse(Environment.GetEnvironmentVariable("HOOKVAULT_MAX_EVENTS"), out var m) && m > 0 ? m : (int?)null;
    var days = int.TryParse(Environment.GetEnvironmentVariable("HOOKVAULT_RETENTION_DAYS"), out var d) && d > 0 ? d : (int?)null;
    return new HookVault.Services.RetentionStats
    {
        MaxEvents = maxEvents,
        Retention = days is { } x ? TimeSpan.FromDays(x) : null,
    };
});

builder.Services.AddHostedService(sp =>
    HookVault.Services.EventRetentionWorker.FromEnvironment(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<HookVault.Services.EventRetentionWorker>>(),
        sp.GetRequiredService<HookVault.Observability.HookVaultMeter>(),
        sp.GetRequiredService<HookVault.Services.RetentionStats>()));

// --- Authentication / Authorisation ---
// JwtBearer validates the Bearer token on every request. Controllers that don't
// opt out via [AllowAnonymous] will require a valid token automatically once
// UseAuthorization() is added to the pipeline below.
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
        // EventSource cannot set Authorization headers — accept token as ?token= for the SSE route only.
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api/events/stream"))
                    ctx.Token = ctx.Request.Query["token"];
                return Task.CompletedTask;
            }
        };
    });

// HOOKVAULT_NO_AUTH=true disables [Authorize] enforcement. The JwtBearer scheme
// stays registered so callers that *do* present a token (e.g. SSE clients) still
// authenticate, but the default policy passes for everyone.
if (noAuth)
{
    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
        options.FallbackPolicy = options.DefaultPolicy;
    });
}
else
{
    builder.Services.AddAuthorization();
}

// --- ASP.NET Core ---
// InvalidModelStateResponseFactory maps automatic model-binding failures (e.g. a string
// where an int is expected) to ApiError, so clients see one error shape instead of both
// ApiError and the default ValidationProblemDetails.
// Test-only endpoints are only mapped under ASPNETCORE_ENVIRONMENT=Testing.
// We keep the controller class in the production assembly (no separate project)
// but exclude it from controller discovery outside Testing.
builder.Services.AddControllers(options =>
{
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        options.Conventions.Add(new HookVault.Infrastructure.ExcludeTestControllersConvention());
    }
})
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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "HookVault", Version = "v1" });
});

// OpenTelemetry metrics: HookVault's custom Meter + AspNetCore built-ins,
// exported via the Prometheus scraping endpoint mapped below.
builder.Services.AddOpenTelemetry()
    .WithMetrics(b => b
        .AddAspNetCoreInstrumentation()
        .AddMeter(HookVault.Observability.HookVaultMeter.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

// In Development, log a ready-to-use UI URL with a short-lived token so devs can
// click straight in. Production environments stay silent — users mint long-lived
// tokens via the `generate-token` CLI subcommand.
if (app.Environment.IsDevelopment())
{
    var uiToken = JwtTokenGenerator.Mint(jwtOptions, "ui", TimeSpan.FromHours(1));
    app.Logger.LogInformation(
        "HookVault UI → http://localhost:7777/?token={Token}", uiToken);
}

if (noAuth)
{
    app.Logger.LogWarning(
        "HOOKVAULT_NO_AUTH=true: the management API is unauthenticated. " +
        "Anyone who can reach this listener can read, replay, and delete events. " +
        "Make sure HookVault is bound to 127.0.0.1 only, or remove this flag.");
}

// --- Auto-migrate DB on startup ---
// db.Database.Migrate() applies any pending EF Core migrations and creates the DB if needed.
// On pre-migration DBs (created by EnsureCreated before this change) there is no
// __EFMigrationsHistory table, so Migrate() would try to CREATE TABLE Events and fail
// because it already exists. BackfillMigrationHistoryAsync detects that case and stamps
// the initial migration as applied before Migrate() runs.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
    await BackfillMigrationHistoryAsync(db);
    await db.Database.MigrateAsync();

    // Recover from a crash mid-replay: any row stuck at Replaying gets bumped
    // to ForwardFailed so it's eligible for the bulk replay-failed endpoint.
    var orphaned = await db.Events
        .Where(e => e.Status == HookVault.Domain.EventStatus.Replaying)
        .ToListAsync();
    if (orphaned.Count > 0)
    {
        foreach (var evt in orphaned)
        {
            evt.Status = HookVault.Domain.EventStatus.ForwardFailed;
            evt.LastError ??= "Recovered from interrupted replay attempt.";
        }
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Startup sweep: recovered {Count} orphaned Replaying events.", orphaned.Count);
    }
}

static void SuppressCrossProviderSnapshotWarnings(Microsoft.EntityFrameworkCore.Diagnostics.WarningsConfigurationBuilder w)
{
    // HookVault ships one model snapshot but supports both SQLite and Postgres.
    // The runtime model on the non-snapshot provider legitimately differs in
    // store types (byte[] -> BLOB vs bytea, string -> TEXT vs text). EF Core's
    // PendingModelChangesWarning treats this as an error by default since 9.x;
    // suppress it so MigrateAsync can run instead of throwing on startup. The
    // canonical migration (BytesBodyAndArrayHeaders) carries its own provider
    // branch so the on-disk schema converges regardless of which snapshot the
    // designer file was generated against.
    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning);
}

static string NormalizePostgresConnectionString(string input)
{
    // Pass through anything that is not a postgres:// URI — power users may
    // supply the native Npgsql key=value form directly with sslmode, pooling,
    // etc., and we should not second-guess that.
    if (!input.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !input.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return input;
    }

    var uri = new Uri(input);
    var userInfo = uri.UserInfo.Split(':', 2);

    var builder = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
    };

    return builder.ToString();
}

static TimeSpan ParseForwardTimeoutEnv(ILogger logger)
{
    const int defaultSeconds = 30;
    const int minSeconds = 1;
    const int maxSeconds = 300;

    var raw = Environment.GetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS");
    if (string.IsNullOrWhiteSpace(raw))
        return TimeSpan.FromSeconds(defaultSeconds);

    if (!int.TryParse(raw, out var parsed))
    {
        logger.LogWarning(
            "HOOKVAULT_FORWARD_TIMEOUT_SECONDS='{Raw}' is not a valid integer; using default {Default}s.",
            raw, defaultSeconds);
        return TimeSpan.FromSeconds(defaultSeconds);
    }

    if (parsed < minSeconds || parsed > maxSeconds)
    {
        logger.LogWarning(
            "HOOKVAULT_FORWARD_TIMEOUT_SECONDS={Parsed} is outside [{Min}, {Max}]; using default {Default}s.",
            parsed, minSeconds, maxSeconds, defaultSeconds);
        return TimeSpan.FromSeconds(defaultSeconds);
    }

    return TimeSpan.FromSeconds(parsed);
}

static async Task BackfillMigrationHistoryAsync(HookVaultDbContext db)
{
    var conn = db.Database.GetDbConnection();
    var openedHere = false;
    if (conn.State != ConnectionState.Open)
    {
        await conn.OpenAsync();
        openedHere = true;
    }
    try
    {
        var eventsExists = await TableExistsAsync(conn, "Events");
        var historyExists = await TableExistsAsync(conn, "__EFMigrationsHistory");

        if (eventsExists && !historyExists)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('00000000000000_Initial', '9.0.16');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        if (openedHere) await conn.CloseAsync();
    }
}

static async Task<bool> TableExistsAsync(System.Data.Common.DbConnection conn, string tableName)
{
    using var cmd = conn.CreateCommand();
    if (conn is SqliteConnection)
    {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@n";
    }
    else
    {
        cmd.CommandText = "SELECT to_regclass(@n) IS NOT NULL";
    }
    var p = cmd.CreateParameter();
    p.ParameterName = "@n";
    p.Value = tableName;
    cmd.Parameters.Add(p);
    var result = await cmd.ExecuteScalarAsync();
    return result is not null && result is not DBNull;
}

// --- Middleware pipeline ---
// Order matters in ASP.NET Core middleware — think Django MIDDLEWARE list.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<RawBodyMiddleware>();
app.UseMiddleware<MaxBodySizeMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// UseAuthentication reads the Bearer token and populates HttpContext.User.
// UseAuthorization enforces [Authorize] / [AllowAnonymous] attributes.
// Must come after routing-aware middleware and before MapControllers.
app.UseAuthentication();
app.UseAuthorization();

// Metrics endpoint is unauthenticated by design — metrics are operational
// data, not secrets. Threat model assumes HookVault is not exposed to the
// public internet (see SECURITY.md).
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

app.MapControllers();
// Exclude /api/ paths from the SPA fallback so unknown API routes return 404
// rather than the React shell. Without this exclusion, endpoint routing generates
// a 405 for POST to any unregistered /api/ path because the fallback registers a
// GET-only endpoint that matches all paths including /api/*.
app.MapFallbackToFile("index.html").Add(b =>
{
    b.Metadata.Add(new Microsoft.AspNetCore.Routing.HttpMethodMetadata(["GET"]));
});
app.Map("/api/{**path}", (HttpContext ctx) =>
{
    ctx.Response.StatusCode = 404;
    return Task.CompletedTask;
});
app.Run();
return 0;

// Partial class declaration makes Program accessible from xUnit test projects.
public partial class Program { }
