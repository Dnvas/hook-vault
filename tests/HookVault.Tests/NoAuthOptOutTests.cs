using System.Net;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
