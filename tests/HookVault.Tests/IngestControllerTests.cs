using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class IngestControllerTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private CapturingHandler _forwardHandler = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("INGEST_TEST_SECRET", "shh-its-a-secret");

        _baseFactory = new HookVaultWebApplicationFactory();
        _forwardHandler = new CapturingHandler();

        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "open",
                        Path = "/open",
                        ForwardUrl = "http://localhost/open",
                        Validation = null,
                    },
                    new ProviderConfig
                    {
                        Name = "signed",
                        Path = "/signed",
                        ForwardUrl = "http://localhost/signed",
                        Validation = new ValidationConfig
                        {
                            Algorithm = "hmac-sha256",
                            SecretEnvVar = "INGEST_TEST_SECRET",
                            SignatureHeader = "X-Signature",
                            PayloadFormat = "{body}",
                            SignatureEncoding = "hex",
                            SignaturePattern = null,
                            TimestampPattern = null,
                        },
                    },
                ]
            });

            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => _forwardHandler);
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        Environment.SetEnvironmentVariable("INGEST_TEST_SECRET", null);
    }

    [Fact]
    public async Task Ingest_UnknownProvider_Returns404()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/ingest/does-not-exist", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_OpenProvider_Returns202AndForwards()
    {
        var client = _factory.CreateClient();
        var body = """{"type":"evt"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/ingest/open", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(_forwardHandler.LastRequest);
        Assert.Equal("http://localhost/open", _forwardHandler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Ingest_BinaryBodyForwardsByteEqual()
    {
        var client = _factory.CreateClient();
        byte[] binary = [0x00, 0xFF, 0xFE, 0xFD, 0xC0, 0xC1]; // invalid UTF-8 sequences

        using var content = new ByteArrayContent(binary);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await client.PostAsync("/api/ingest/open", content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.NotNull(_forwardHandler.LastBodyBytes);
        Assert.Equal(binary, _forwardHandler.LastBodyBytes);
    }

    [Fact]
    public async Task Ingest_SignedProvider_ValidSignature_Returns202AndStoresValid()
    {
        var client = _factory.CreateClient();
        var body = """{"type":"evt","id":"e_1"}""";
        var secret = Encoding.UTF8.GetBytes("shh-its-a-secret");
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToHexString(HMACSHA256.HashData(secret, bodyBytes)).ToLowerInvariant();

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("X-Signature", signature);
        var response = await client.PostAsync("/api/ingest/signed", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = await db.Events.SingleAsync();
        Assert.True(stored.SignatureValid);
    }

    [Fact]
    public async Task Ingest_SignedProvider_InvalidSignature_StillStoresEventWithValidFalse()
    {
        var client = _factory.CreateClient();
        var body = """{"type":"evt"}""";

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("X-Signature", "deadbeef");
        var response = await client.PostAsync("/api/ingest/signed", content);

        // Spec: capture-and-forward regardless of signature validity; the validity
        // is recorded so the developer can debug.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var stored = await db.Events.SingleAsync();
        Assert.False(stored.SignatureValid);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public byte[]? LastBodyBytes;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBodyBytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
