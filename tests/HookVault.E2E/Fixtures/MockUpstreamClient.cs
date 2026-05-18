using System.Diagnostics;
using System.Text.Json;

namespace HookVault.E2E.Fixtures;

public sealed record ReceivedRequest(
    string Method,
    string Path,
    Dictionary<string, string> Headers,
    string Body);

public sealed class MockUpstreamLogException(string message, string rawLine)
    : Exception(message)
{
    public string RawLine { get; } = rawLine;
}

public sealed class MockUpstreamClient(string containerName)
{
    /// <summary>
    /// Polls <c>docker logs --since</c> until a line matching the predicate appears,
    /// or the timeout expires.
    /// </summary>
    public async Task<ReceivedRequest> WaitForRequestAsync(
        DateTimeOffset since,
        Func<ReceivedRequest, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var sinceArg = since.UtcDateTime.ToString("o");
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var raw in DumpLogs(sinceArg))
            {
                if (TryParse(raw, out var req) && predicate(req))
                {
                    return req;
                }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException(
            $"no request matching predicate received by {containerName} within {timeout}");
    }

    public int Count(DateTimeOffset since, Func<ReceivedRequest, bool> predicate)
    {
        var sinceArg = since.UtcDateTime.ToString("o");
        return DumpLogs(sinceArg)
            .Select(l => TryParse(l, out var r) ? r : null)
            .Count(r => r is not null && predicate(r));
    }

    private IEnumerable<string> DumpLogs(string sinceArg)
    {
        var psi = new ProcessStartInfo(
            "docker",
            $"logs --since {sinceArg} {containerName}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start docker logs");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryParse(string raw, out ReceivedRequest req)
    {
        req = null!;
        // mendhak/http-https-echo emits JSON lines; lines that don't start with '{'
        // are framing chatter we ignore.
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }
        try
        {
            var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString() ?? "";
            var path = root.GetProperty("path").GetString() ?? "";
            var body = root.TryGetProperty("body", out var b)
                ? (b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : b.GetRawText())
                : "";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("headers", out var h))
            {
                foreach (var prop in h.EnumerateObject())
                {
                    headers[prop.Name] = prop.Value.GetString() ?? prop.Value.GetRawText();
                }
            }
            req = new ReceivedRequest(method, path, headers, body);
            return true;
        }
        catch (JsonException)
        {
            // mendhak occasionally interleaves non-JSON lines; skip rather than throw.
            return false;
        }
    }
}
