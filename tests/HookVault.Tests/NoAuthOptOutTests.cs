using System.Net;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class NoAuthOptOutTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_NO_AUTH", "true");
        _baseFactory = new HookVaultWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("HOOKVAULT_NO_AUTH", null);
    }

    [Fact]
    public async Task GetEvents_WithoutToken_Returns200_WhenNoAuthFlagSet()
    {
        var factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions { Providers = [] });
        }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Application_StartsCleanly_WithNoSecret_WhenNoAuthFlagSet()
    {
        // Simulate a fresh first-run install: no HOOKVAULT_JWT_SECRET in the
        // environment, NO_AUTH flag flipped on. Bootstrap must succeed using
        // an ephemeral key instead of throwing.
        var savedSecret = Environment.GetEnvironmentVariable("HOOKVAULT_JWT_SECRET");
        Environment.SetEnvironmentVariable("HOOKVAULT_JWT_SECRET", null);
        try
        {
            await using var freshFactory = new NoSecretFactory();

            var factory = freshFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<HookVaultOptions>();
                s.AddSingleton(new HookVaultOptions { Providers = [] });
            }));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
            await db.Database.EnsureCreatedAsync();

            var client = factory.CreateClient();
            var response = await client.GetAsync("/api/events");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_JWT_SECRET", savedSecret);
        }
    }

    // Variant factory that does NOT seed HOOKVAULT_JWT_SECRET, so we can exercise the
    // NO_AUTH=true / no-secret first-run path.
    private sealed class NoSecretFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly string _configFilePath;

        public NoSecretFactory()
        {
            _connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _configFilePath = Path.GetTempFileName();
            File.WriteAllText(_configFilePath, """{"providers":[]}""");
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configFilePath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<HookVaultDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<HookVaultDbContext>(opts =>
                    opts.UseSqlite(_connection));
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            });
        }

        public new async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _connection.DisposeAsync();
            Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
            if (File.Exists(_configFilePath))
                File.Delete(_configFilePath);
        }
    }
}
