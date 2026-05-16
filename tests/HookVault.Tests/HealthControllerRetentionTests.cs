using System.Net.Http.Json;
using System.Text.Json;
using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class HealthControllerRetentionTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_EVENTS", "1000");
        Environment.SetEnvironmentVariable("HOOKVAULT_RETENTION_DAYS", "7");

        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions { Providers = [] });
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_EVENTS", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_RETENTION_DAYS", null);
    }

    [Fact]
    public async Task Health_IncludesRetention_WhenCapsConfigured()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, payload.GetProperty("retention").ValueKind);
        Assert.Equal(1000, payload.GetProperty("retention").GetProperty("maxEvents").GetInt32());
        Assert.Equal(7.0, payload.GetProperty("retention").GetProperty("retentionDays").GetDouble());
    }

    [Fact]
    public async Task Health_RetentionNull_WhenNoCapsConfigured()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_MAX_EVENTS", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_RETENTION_DAYS", null);

        await using var bare = new HookVaultWebApplicationFactory();
        var factory = bare.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions { Providers = [] });
        }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("retention").ValueKind);
    }
}
