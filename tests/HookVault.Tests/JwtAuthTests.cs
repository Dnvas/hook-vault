using System.Net;
using HookVault.Configuration;
using Microsoft.AspNetCore.Hosting;
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
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            var existing = s.SingleOrDefault(d => d.ServiceType == typeof(HookVaultOptions));
            if (existing is not null) s.Remove(existing);
            s.AddSingleton(new HookVaultOptions { Providers = [] });
        }));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
