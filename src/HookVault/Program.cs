using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Middleware;
using HookVault.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Load hookvault.json at startup; registered as a singleton so controllers can inject it.
// Singleton = one shared instance for the lifetime of the app (like a module-level global).
var hookVaultOptions = HookVaultOptions.Load(
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup"));
builder.Services.AddSingleton(hookVaultOptions);

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

// Transient: SignatureValidator is stateless; new instance each time is fine.
builder.Services.AddTransient<SignatureValidator>();

// Scoped: EventForwarder holds no state; Scoped is appropriate since it depends on
// EventRepository (Scoped).
builder.Services.AddScoped<EventForwarder>();

// HttpClient: IHttpClientFactory manages pooled HttpMessageHandlers to avoid socket exhaustion.
// This is the .NET best-practice replacement for newing up HttpClient directly.
builder.Services.AddHttpClient("forwarder");

// --- ASP.NET Core ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "HookVault", Version = "v1" });
});

var app = builder.Build();

// --- Auto-migrate DB on startup ---
// EnsureCreated / Migrate creates tables from EF model without needing CLI migrations.
// Fine for SQLite dev/prod; for PostgreSQL prod you'd want explicit migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
    db.Database.EnsureCreated();
}

// --- Middleware pipeline ---
// Order matters in ASP.NET Core middleware — think Django MIDDLEWARE list.
app.UseMiddleware<RawBodyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.Run();

// Partial class declaration makes Program accessible from xUnit test projects.
public partial class Program { }
