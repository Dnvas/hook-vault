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
using HookVaultSignatureValidator = HookVault.Services.SignatureValidator;

if (args.Length > 0 && args[0] == "generate-token")
{
    return GenerateTokenCommand.Run(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Load hookvault.json at startup; registered as a singleton so controllers can inject it.
// Singleton = one shared instance for the lifetime of the app (like a module-level global).
var hookVaultOptions = HookVaultOptions.Load(
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup"));
builder.Services.AddSingleton(hookVaultOptions);

var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(jwtOptions);

// --- Database ---
// If DATABASE_URL is set, use PostgreSQL; otherwise default to SQLite.
// AddDbContext registers the context as Scoped (one instance per HTTP request).
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    builder.Services.AddDbContext<HookVaultDbContext>(opts =>
        opts.UseNpgsql(databaseUrl));
}
else
{
    var dbPath = Environment.GetEnvironmentVariable("SQLITE_PATH") ?? "/data/hookvault.db";
    builder.Services.AddDbContext<HookVaultDbContext>(opts =>
        opts.UseSqlite($"Data Source={dbPath}"));
}

// --- Application Services ---
// Scoped: EventRepository wraps the Scoped DbContext — must also be Scoped.
builder.Services.AddScoped<EventRepository>();

// Transient: each scheme is stateless; new instance each time is fine.
builder.Services.AddTransient<HookVault.Services.Schemes.IIngestSignatureScheme,
                              HookVault.Services.Schemes.SingleHeaderHmacScheme>();

// Transient: SignatureValidator is stateless; new instance each time is fine.
builder.Services.AddTransient<HookVaultSignatureValidator>();

// Scoped: EventForwarder holds no state; Scoped is appropriate since it depends on
// EventRepository (Scoped).
builder.Services.AddScoped<EventForwarder>();

// HttpClient: IHttpClientFactory manages pooled HttpMessageHandlers to avoid socket exhaustion.
// This is the .NET best-practice replacement for newing up HttpClient directly.
builder.Services.AddHttpClient("forwarder");

// Singleton: owns the Channel<Guid> for the application lifetime.
builder.Services.AddSingleton<ReplayQueue>();

// Singleton: owns the Channel<EventNotification> for SSE fanout to connected clients.
builder.Services.AddSingleton<EventNotifier>();

// Hosted service: BackgroundService started on app start, stopped on graceful shutdown.
builder.Services.AddHostedService<ReplayWorker>();
builder.Services.AddHostedService(sp =>
    HookVault.Services.EventRetentionWorker.FromEnvironment(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<HookVault.Services.EventRetentionWorker>>()));

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

builder.Services.AddAuthorization();

// --- ASP.NET Core ---
// InvalidModelStateResponseFactory maps automatic model-binding failures (e.g. a string
// where an int is expected) to ApiError, so clients see one error shape instead of both
// ApiError and the default ValidationProblemDetails.
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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "HookVault", Version = "v1" });
});

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

app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
return 0;

// Partial class declaration makes Program accessible from xUnit test projects.
public partial class Program { }
