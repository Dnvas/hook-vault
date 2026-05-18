using System.Net;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HookVault.Tests;

public sealed class TestControllerTests : IDisposable
{
    // Minimal config so HookVaultOptions.Load() succeeds.
    private readonly string _configFilePath;
    private readonly SqliteConnection _connection;

    public TestControllerTests()
    {
        _configFilePath = Path.GetTempFileName();
        File.WriteAllText(_configFilePath, """{"providers":[]}""");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Set process-level env vars before the host builds — Program.cs reads them
        // during startup before any ConfigureWebHost callbacks fire.
        Environment.SetEnvironmentVariable("HOOKVAULT_NO_AUTH", "true");
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configFilePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_NO_AUTH", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
        if (File.Exists(_configFilePath))
            File.Delete(_configFilePath);
        _connection.Dispose();
    }

    private WebApplicationFactory<Program> Factory(string environment) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment(environment);
                b.ConfigureServices(services =>
                {
                    // Replace the real SQLite DB with an in-memory connection.
                    var descriptor = services.Single(
                        d => d.ServiceType == typeof(DbContextOptions<HookVaultDbContext>));
                    services.Remove(descriptor);
                    services.AddDbContext<HookVaultDbContext>(
                        opts => opts.UseSqlite(_connection));

                    // Remove background services so workers don't interfere.
                    services.RemoveAll<IHostedService>();

                    // Create the schema on the shared in-memory connection.
                    using var sp = services.BuildServiceProvider();
                    using var scope = sp.CreateScope();
                    scope.ServiceProvider.GetRequiredService<HookVaultDbContext>()
                        .Database.EnsureCreated();
                });
            });

    [Fact]
    public async Task Reset_UnderTesting_Returns204()
    {
        using var factory = Factory("Testing");
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/test/reset", content: null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Reset_UnderProduction_Returns404()
    {
        using var factory = Factory("Production");
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/test/reset", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
