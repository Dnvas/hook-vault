using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace HookVault.E2E.Fixtures;

/// <summary>
/// Boots / tears down the docker compose e2e stack for the duration of the
/// xUnit test collection. CI sets <c>E2E_SKIP_COMPOSE=1</c> to manage the
/// stack at the job level; locally the fixture self-manages.
/// </summary>
public sealed class ComposeStackFixture : IAsyncLifetime
{
    private static readonly bool SkipCompose =
        Environment.GetEnvironmentVariable("E2E_SKIP_COMPOSE") == "1";

    public string BaseUrl { get; }
    public string MockUpstreamContainer { get; }
    private readonly string _composeFile;

    public ComposeStackFixture()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.E2E.json", optional: false)
            .AddEnvironmentVariables(prefix: "HOOKVAULT_E2E_")
            .Build();

        BaseUrl = config["HookVault:BaseUrl"]
                  ?? throw new InvalidOperationException("HookVault:BaseUrl not configured");
        MockUpstreamContainer = config["HookVault:MockUpstreamContainer"]
                  ?? throw new InvalidOperationException("HookVault:MockUpstreamContainer not configured");
        _composeFile = config["HookVault:ComposeFile"] ?? "docker-compose.e2e.yml";
    }

    public async Task InitializeAsync()
    {
        if (!SkipCompose)
        {
            Run("docker", $"compose -f {_composeFile} up -d --build");
        }
        await WaitForHealthyAsync(TimeSpan.FromSeconds(60));
    }

    public Task DisposeAsync()
    {
        if (!SkipCompose)
        {
            Run("docker", $"compose -f {_composeFile} down -v");
        }
        return Task.CompletedTask;
    }

    private async Task WaitForHealthyAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync("/api/health");
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException(
            $"HookVault not healthy at {BaseUrl} within {timeout}", last);
    }

    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{file} {args} exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        }
    }
}

[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<ComposeStackFixture> { }
