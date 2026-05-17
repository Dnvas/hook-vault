using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class ForwarderTimeoutTests
{
    [Fact]
    public async Task DefaultTimeout_IsThirtySeconds()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", null);
        await using var factory = new HookVaultWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var http = scope.ServiceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("forwarder");

        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
    }

    [Fact]
    public async Task EnvOverride_AppliesToForwarderClient()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", "5");
        try
        {
            await using var factory = new HookVaultWebApplicationFactory();
            using var scope = factory.Services.CreateScope();
            var http = scope.ServiceProvider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient("forwarder");

            Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("301")]
    public async Task InvalidValues_FallBackToDefault(string raw)
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", raw);
        try
        {
            await using var factory = new HookVaultWebApplicationFactory();
            using var scope = factory.Services.CreateScope();
            var http = scope.ServiceProvider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient("forwarder");

            Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", null);
        }
    }

    [Fact]
    public async Task UnreachableHost_FailsWithinConfiguredTimeout()
    {
        // Bind a TCP listener but never accept — connect() hangs on most platforms,
        // forcing the request past the kernel-level connect path so the HttpClient
        // timeout (not OS RST) is what cuts the call off.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", "2");
        try
        {
            await using var factory = new HookVaultWebApplicationFactory();
            using var scope = factory.Services.CreateScope();
            var http = scope.ServiceProvider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient("forwarder");

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await http.GetAsync($"http://127.0.0.1:{port}/"));
            sw.Stop();

            // Configured 2s. Allow generous slack for CI: must be well under .NET's 100s default.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
                $"Expected forward to abort within configured timeout; took {sw.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOOKVAULT_FORWARD_TIMEOUT_SECONDS", null);
            listener.Stop();
        }
    }
}
