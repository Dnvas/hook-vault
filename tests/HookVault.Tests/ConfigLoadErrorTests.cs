using HookVault.Configuration;
using Microsoft.Extensions.Logging;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class ConfigLoadErrorTests : IDisposable
{
    private readonly string _configPath = Path.GetTempFileName();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", null);
        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }

    private ILogger NullLogger() => LoggerFactory.Create(_ => { }).CreateLogger("test");

    [Fact]
    public void Load_MalformedJson_ThrowsInvalidOperationException_WithFileAndLine()
    {
        File.WriteAllText(_configPath, """
            {
              "providers": [
                { "name": "broken",
            }
            """);
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configPath);

        var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(NullLogger()));

        Assert.Contains(_configPath, ex.Message);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public void Load_TrailingGarbage_ThrowsInvalidOperationException()
    {
        File.WriteAllText(_configPath, """{"providers":[]}}""");
        Environment.SetEnvironmentVariable("HOOKVAULT_CONFIG_PATH", _configPath);

        var ex = Assert.Throws<InvalidOperationException>(() => HookVaultOptions.Load(NullLogger()));

        Assert.Contains(_configPath, ex.Message);
    }
}
