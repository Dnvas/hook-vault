using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HookVault.Tests;

public sealed class HookVaultWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestSecret = "test-secret-with-at-least-32-bytes-pad";
    public const string TestIssuer = "hookvault-test";
    public const string TestAudience = "hookvault-test";

    private readonly SqliteConnection _connection;
    private readonly string _configFilePath;

    public HookVaultWebApplicationFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Write a minimal hookvault.json so HookVaultOptions.Load() doesn't throw.
        // The test overrides the singleton after the host is built.
        _configFilePath = Path.GetTempFileName();
        File.WriteAllText(_configFilePath, """{"providers":[]}""");
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configFilePath);

        // JwtOptions.FromConfiguration reads builder.Configuration before ConfigureAppConfiguration
        // callbacks fire, so we must set the actual process env vars before the host builds.
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_SECRET", TestSecret);
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_ISSUER", TestIssuer);
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_AUDIENCE", TestAudience);
    }

    public SqliteConnection Connection => _connection;

    public string GenerateToken(string subject = "test") =>
        Auth.JwtTokenGenerator.Mint(
            new Auth.JwtOptions(TestSecret, TestIssuer, TestAudience),
            subject,
            TimeSpan.FromHours(1));

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

            // Remove background services so the ReplayWorker doesn't consume queue items during tests
            services.RemoveAll<IHostedService>();
        });
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();

        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_SECRET", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_ISSUER", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_AUDIENCE", null);
        if (File.Exists(_configFilePath))
            File.Delete(_configFilePath);
    }
}
