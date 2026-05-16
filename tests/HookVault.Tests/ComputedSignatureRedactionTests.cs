using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class ComputedSignatureRedactionTests : IAsyncLifetime
{
    private const string Secret = "shh-its-a-secret";

    private HookVaultWebApplicationFactory _baseFactory = null!;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CSR_TEST_SECRET", Secret);
        _baseFactory = new HookVaultWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("CSR_TEST_SECRET", null);
        Environment.SetEnvironmentVariable("HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE", null);
    }

    [Fact]
    public async Task ProductionEnv_DefaultRedactsComputedSignature()
    {
        var factory = await BuildFactoryAsync("Production");

        var body = """{"x":1}""";
        var sig = ComputeSig(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/signed")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Signature", sig);

        var client = factory.CreateClient();
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();
        var details = JsonDocument.Parse(stored.ValidationDetails!).RootElement;
        Assert.Equal("[redacted]", details.GetProperty("computedSignature").GetString());
    }

    [Fact]
    public async Task DevelopmentEnv_ShowsComputedSignature()
    {
        var factory = await BuildFactoryAsync("Development");

        var body = """{"x":1}""";
        var sig = ComputeSig(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/signed")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Signature", sig);

        var client = factory.CreateClient();
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();
        var details = JsonDocument.Parse(stored.ValidationDetails!).RootElement;
        var computed = details.GetProperty("computedSignature").GetString();
        Assert.NotEqual("[redacted]", computed);
        Assert.Equal(sig, computed);
    }

    [Fact]
    public async Task ProductionEnv_WithExposeOptIn_ShowsComputedSignature()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE", "true");
        var factory = await BuildFactoryAsync("Production");

        var body = """{"x":1}""";
        var sig = ComputeSig(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/signed")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Signature", sig);

        var client = factory.CreateClient();
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = db.Events.Single();
        var details = JsonDocument.Parse(stored.ValidationDetails!).RootElement;
        Assert.Equal(sig, details.GetProperty("computedSignature").GetString());
    }

    private async Task<WebApplicationFactory<Program>> BuildFactoryAsync(string env)
    {
        var f = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(env);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<HookVaultOptions>();
                s.AddSingleton(new HookVaultOptions
                {
                    Providers =
                    [
                        new ProviderConfig
                        {
                            Name = "signed",
                            Path = "/signed",
                            ForwardUrl = "http://localhost/signed",
                            Validation = new ValidationConfig
                            {
                                Algorithm = "hmac-sha256",
                                SecretEnvVar = "CSR_TEST_SECRET",
                                SignatureHeader = "X-Signature",
                                PayloadFormat = "{body}",
                                SignatureEncoding = "hex",
                            },
                        },
                    ]
                });
                s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
            });
        });

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
        return f;
    }

    private static string ComputeSig(string body) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)))
        .ToLowerInvariant();

    private sealed class OkHandler : HttpMessageHandler
    {
        private HttpResponseMessage? _response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            _response = new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response?.Dispose();
            base.Dispose(disposing);
        }
    }
}
