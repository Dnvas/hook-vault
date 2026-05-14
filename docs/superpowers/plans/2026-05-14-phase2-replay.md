# Phase 2 — Replay System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `ReplayQueue` and `ReplayWorker` to asynchronously re-forward failed webhook events with exponential backoff, and extract `SendAsync` from `EventForwarder` as the shared HTTP primitive both paths use.

**Architecture:** `ForwardResult` record + `SendAsync` are extracted from `EventForwarder` so the ingest and replay paths share HTTP logic without sharing status management. `ReplayQueue` wraps `Channel<Guid>` as a singleton. `ReplayWorker` is a `BackgroundService` that dequeues event IDs, fetches fresh from SQLite via `IServiceScopeFactory`, and retries up to 3 times with 1 s / 2 s / 4 s delays.

**Tech Stack:** .NET 8, `System.Threading.Channels`, EF Core 8 + SQLite, xUnit 2.5, `IHttpClientFactory`

**Spec:** `docs/superpowers/specs/2026-05-14-phase2-replay-design.md`

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Modify | `src/HookVault/HookVault.csproj` | Add `InternalsVisibleTo` so tests can set `RetryDelays` |
| Modify | `tests/HookVault.Tests/HookVault.Tests.csproj` | Add `Microsoft.Data.Sqlite` + `Microsoft.Extensions.DependencyInjection` |
| Create | `src/HookVault/Services/ForwardResult.cs` | Internal record returned by `SendAsync` |
| Modify | `src/HookVault/Services/EventForwarder.cs` | Extract `SendAsync`; `ForwardAsync` delegates to it |
| Create | `src/HookVault/Services/ReplayQueue.cs` | Singleton `Channel<Guid>` wrapper |
| Create | `src/HookVault/Services/ReplayWorker.cs` | `BackgroundService` with retry loop |
| Modify | `src/HookVault/Program.cs` | Register `ReplayQueue` + `AddHostedService<ReplayWorker>` |
| Create | `tests/HookVault.Tests/ReplayWorkerTests.cs` | 4 integration tests with real SQLite |

---

### Task 1: Expose internals to the test project

**Files:**
- Modify: `src/HookVault/HookVault.csproj`
- Modify: `tests/HookVault.Tests/HookVault.Tests.csproj`

`RetryDelays` on `ReplayWorker` is `internal { get; init; }` so tests can inject `TimeSpan.Zero` delays and not wait 7 seconds. MSBuild supports `InternalsVisibleTo` in the csproj directly — no separate `AssemblyInfo.cs` needed.

The test project also needs two packages: `Microsoft.Data.Sqlite` for `SqliteConnection` (kept open so all EF Core contexts share the same in-memory DB), and `Microsoft.Extensions.DependencyInjection` for `ServiceCollection`.

- [ ] **Step 1: Add InternalsVisibleTo to the main project**

Edit `src/HookVault/HookVault.csproj`. Add a new `<ItemGroup>` immediately before the closing `</Project>` tag:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="HookVault.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add required test packages**

```bash
cd tests/HookVault.Tests
dotnet add package Microsoft.Data.Sqlite --version 8.0.10
dotnet add package Microsoft.Extensions.DependencyInjection --version 8.0.1
cd ../..
```

- [ ] **Step 3: Verify build still passes**

```bash
dotnet build
```

Expected: `Build succeeded.` No errors.

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/HookVault.csproj tests/HookVault.Tests/HookVault.Tests.csproj
git commit -m "chore: expose internals to test project and add test packages"
```

---

### Task 2: Create ForwardResult + refactor EventForwarder

**Files:**
- Create: `src/HookVault/Services/ForwardResult.cs`
- Modify: `src/HookVault/Services/EventForwarder.cs`

`SendAsync` is the pure HTTP primitive — it builds the request, copies headers, calls the HTTP client, and returns `ForwardResult`. No DB touches, no status mutations. `ForwardAsync` keeps the same public signature and observable behaviour; it now delegates to `SendAsync` and owns the status transitions. All existing tests must still pass after this refactor.

- [ ] **Step 1: Create ForwardResult.cs**

Create `src/HookVault/Services/ForwardResult.cs`:

```csharp
namespace HookVault.Services;

internal sealed record ForwardResult(bool Success, int? StatusCode, string? Error);
```

- [ ] **Step 2: Replace EventForwarder.cs**

Replace the entire contents of `src/HookVault/Services/EventForwarder.cs`:

```csharp
using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public sealed class EventForwarder(IHttpClientFactory httpClientFactory, EventRepository repo, ILogger<EventForwarder> logger)
{
    // Hop-by-hop headers (RFC 9110 §7.6.1) and Authorization must not be forwarded.
    // Hop-by-hop headers describe the original transport connection, not the payload.
    // Authorization is omitted so the captured token from the provider cannot be
    // replayed against the local destination without explicit opt-in.
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding",
        "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Authenticate",
        "Authorization",
    };

    internal async Task<ForwardResult> SendAsync(WebhookEvent evt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("forwarder");

        using var request = new HttpRequestMessage(HttpMethod.Post, evt.ForwardUrl);
        request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(evt.Body));

        var storedHeaders = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(evt.Headers)
            ?? [];

        foreach (var (key, value) in storedHeaders)
        {
            if (SkippedHeaders.Contains(key)) continue;

            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.TryAddWithoutValidation(key, value);
            else
                request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Headers.TryAddWithoutValidation("X-HookVault-Event-Id", evt.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-HookVault-Provider", evt.Provider);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var success = response.IsSuccessStatusCode;
            var statusCode = (int)response.StatusCode;
            logger.LogInformation("Forwarded event {Id} to {Url} → {Status}", evt.Id, evt.ForwardUrl, statusCode);
            return new ForwardResult(success, statusCode, success ? null : $"Upstream returned {statusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Forward timed out for event {Id}", evt.Id);
            return new ForwardResult(false, null, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Forward failed for event {Id}", evt.Id);
            return new ForwardResult(false, null, ex.Message);
        }
    }

    public async Task ForwardAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        evt.Status = EventStatus.Forwarding;
        await repo.UpdateAsync(evt, ct);

        var result = await SendAsync(evt, ct);

        evt.ForwardedAt = DateTimeOffset.UtcNow;
        evt.ForwardStatusCode = result.StatusCode;
        evt.Status = result.Success ? EventStatus.Forwarded : EventStatus.ForwardFailed;
        if (!result.Success) evt.ForwardError = result.Error;

        await repo.UpdateAsync(evt, ct);
    }
}
```

- [ ] **Step 3: Run existing tests — verify no regression**

```bash
dotnet test
```

Expected: all pre-existing tests pass. Zero failures.

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/Services/ForwardResult.cs src/HookVault/Services/EventForwarder.cs
git commit -m "refactor: extract SendAsync from EventForwarder"
```

---

### Task 3: Write failing ReplayWorker tests

**Files:**
- Create: `tests/HookVault.Tests/ReplayWorkerTests.cs`

Write all four tests now, before `ReplayQueue` or `ReplayWorker` exist. The project will not compile yet — that is the TDD red phase.

Test infrastructure explained:
- One `SqliteConnection` is opened once and kept alive for the test class instance. Every `DbContext` — both the test's own and the ones the worker creates per-scope — is configured with this same connection object, so they all share the same in-memory database.
- `SequencedHandler` returns pre-programmed HTTP status codes in order, repeating the last one if exhausted.
- `BuildWorker` wires a fresh `ServiceCollection` per test so there is no state leakage between tests.
- `RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]` removes all waiting; tests run in milliseconds.
- `WaitForStatusAsync` polls the DB with a 5-second timeout rather than sleeping a fixed amount.

- [ ] **Step 1: Create the test file**

Create `tests/HookVault.Tests/ReplayWorkerTests.cs`:

```csharp
using System.Net;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HookVault.Tests;

public sealed class ReplayWorkerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HookVaultDbContext _db;
    private readonly EventRepository _repo;

    public ReplayWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<HookVaultDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new HookVaultDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new EventRepository(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private (ReplayWorker worker, ReplayQueue queue) BuildWorker(params HttpStatusCode[] responses)
    {
        var handler = new SequencedHandler(responses);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HookVaultDbContext>(opts => opts.UseSqlite(_connection));
        services.AddScoped<EventRepository>();
        services.AddScoped<EventForwarder>();
        services.AddHttpClient("forwarder")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        var queue = new ReplayQueue();
        var worker = new ReplayWorker(queue, provider, NullLogger<ReplayWorker>.Instance)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]
        };

        return (worker, queue);
    }

    private static WebhookEvent MakeEvent() => new()
    {
        Provider = "test",
        Path = "/test",
        Headers = "{}",
        Body = "hello",
        ForwardUrl = "http://localhost/webhook",
        Status = EventStatus.ForwardFailed,
    };

    private async Task<WebhookEvent?> FreshFetchAsync(Guid id) =>
        await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

    private static async Task WaitForStatusAsync(
        Func<Task<WebhookEvent?>> fetch, EventStatus status, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            var updated = await fetch();
            if (updated?.Status == status) return;
        }
        throw new TimeoutException($"Event did not reach status {status} within {timeoutMs} ms");
    }

    [Fact]
    public async Task EventNotFound_LogsWarningAndContinues()
    {
        var (worker, queue) = BuildWorker(HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(Guid.NewGuid()); // unknown ID — not in DB

        await Task.Delay(200); // give the worker time to process

        await worker.StopAsync(CancellationToken.None);
        // Reaching here without an exception means the worker survived the unknown ID.
    }

    [Fact]
    public async Task SuccessOnFirstAttempt_SetsForwarded()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue) = BuildWorker(HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.Forwarded, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.NotNull(updated.ForwardedAt);
    }

    [Fact]
    public async Task FailTwiceThenSucceed_SetsForwarded()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue) = BuildWorker(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.Forwarded, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
    }

    [Fact]
    public async Task AllAttemptsFail_SetsReplayFailed()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue) = BuildWorker(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.ReplayFailed);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.ReplayFailed, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.NotNull(updated.LastError);
    }

    private sealed class SequencedHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var code = _index < codes.Length ? codes[_index++] : codes[^1];
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }
}
```

- [ ] **Step 2: Verify build fails with expected errors**

```bash
dotnet build
```

Expected: compilation errors referencing `ReplayQueue` and `ReplayWorker` not found. This is the expected red state.

---

### Task 4: Implement ReplayQueue

**Files:**
- Create: `src/HookVault/Services/ReplayQueue.cs`

`UnboundedChannel` — no backpressure needed for a local dev tool. `SingleReader = true` is a channel perf hint since only `ReplayWorker` ever reads from it.

- [ ] **Step 1: Create ReplayQueue.cs**

Create `src/HookVault/Services/ReplayQueue.cs`:

```csharp
using System.Threading.Channels;

namespace HookVault.Services;

public sealed class ReplayQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid eventId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(eventId, ct);
}
```

- [ ] **Step 2: Verify build still fails (ReplayWorker still missing)**

```bash
dotnet build
```

Expected: `ReplayQueue` errors are gone; `ReplayWorker` errors remain.

---

### Task 5: Implement ReplayWorker

**Files:**
- Create: `src/HookVault/Services/ReplayWorker.cs`

Key .NET concept for this class: `BackgroundService` is a singleton, but `EventRepository` and `EventForwarder` are scoped (they depend on `DbContext` which is scoped). Injecting a scoped service directly into a singleton causes a DI captive-dependency exception at startup. The fix is `IServiceScopeFactory`: the worker creates a short-lived scope per channel item, resolves the scoped services from that scope, processes the event, then disposes the scope. Django analogy: manually opening and closing a DB connection inside a long-running management command rather than relying on the request/response lifecycle.

`ReadAllAsync(stoppingToken)` is the idiomatic way to consume a channel — it blocks waiting for items and exits cleanly when the token is cancelled (ASP.NET Core cancels it on shutdown).

`RetryDelays` is `internal { get; init; }` — production DI gets the 1s/2s/4s defaults; tests set it to `TimeSpan.Zero` via object initializer without touching the constructor.

Status flow: `Replaying` (saved before the loop) → on success `Forwarded` (saved, return) → on failure with retries remaining `Task.Delay` → on exhaustion `ReplayFailed` (saved). `ReplayCount` increments once per dequeue, not per HTTP attempt.

- [ ] **Step 1: Create ReplayWorker.cs**

Create `src/HookVault/Services/ReplayWorker.cs`:

```csharp
using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public sealed class ReplayWorker(
    ReplayQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ReplayWorker> logger) : BackgroundService
{
    internal TimeSpan[] RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
            await ProcessAsync(eventId, stoppingToken);
    }

    private async Task ProcessAsync(Guid eventId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<EventRepository>();
        var forwarder = scope.ServiceProvider.GetRequiredService<EventForwarder>();

        var evt = await repo.GetByIdAsync(eventId, ct);
        if (evt is null)
        {
            logger.LogWarning("Replay skipped: event {Id} not found", eventId);
            return;
        }

        evt.Status = EventStatus.Replaying;
        evt.ReplayCount++;
        evt.LastReplayAt = DateTimeOffset.UtcNow;
        await repo.UpdateAsync(evt, ct);

        for (var attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            var result = await forwarder.SendAsync(evt, ct);

            if (result.Success)
            {
                evt.Status = EventStatus.Forwarded;
                evt.ForwardedAt = DateTimeOffset.UtcNow;
                evt.ForwardStatusCode = result.StatusCode;
                await repo.UpdateAsync(evt, ct);
                logger.LogInformation("Replay succeeded for {Id} on attempt {N}", eventId, attempt + 1);
                return;
            }

            evt.LastError = result.Error;

            if (attempt < RetryDelays.Length)
            {
                logger.LogWarning("Replay attempt {N} failed for {Id}, retrying in {Delay}s",
                    attempt + 1, eventId, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        evt.Status = EventStatus.ReplayFailed;
        await repo.UpdateAsync(evt, ct);
        logger.LogError("Replay exhausted all attempts for {Id}. Last error: {Error}",
            eventId, evt.LastError);
    }
}
```

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```

Expected: all tests pass including the 4 new `ReplayWorkerTests`. Zero failures.

- [ ] **Step 3: Commit**

```bash
git add src/HookVault/Services/ReplayQueue.cs src/HookVault/Services/ReplayWorker.cs tests/HookVault.Tests/ReplayWorkerTests.cs
git commit -m "feat: add ReplayQueue, ReplayWorker, and integration tests"
```

---

### Task 6: Wire DI + final verification

**Files:**
- Modify: `src/HookVault/Program.cs`

`AddSingleton<ReplayQueue>()` registers the channel wrapper for the app lifetime. `AddHostedService<ReplayWorker>()` is the .NET idiom for "start this `BackgroundService` when the host starts and stop it gracefully on shutdown" — the framework calls `StopAsync` with the shutdown timeout and waits for `ExecuteAsync` to return.

- [ ] **Step 1: Register services in Program.cs**

In `src/HookVault/Program.cs`, add two lines after `builder.Services.AddHttpClient("forwarder");`:

```csharp
// Singleton: owns the Channel<Guid> for the application lifetime.
builder.Services.AddSingleton<ReplayQueue>();

// Hosted service: BackgroundService started on app start, stopped on graceful shutdown.
builder.Services.AddHostedService<ReplayWorker>();
```

- [ ] **Step 2: Full Release build + test run**

```bash
dotnet build --configuration Release && dotnet test --configuration Release
```

Expected:

```
Build succeeded.
...
Passed! - Failed:   0, Passed: X, Skipped:   0, Total: X
```

- [ ] **Step 3: Format check**

```bash
dotnet format --verify-no-changes
```

Expected: exits with code 0 (no formatting issues). If it exits non-zero, run `dotnet format` to fix, then re-run the check.

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/Program.cs
git commit -m "feat: register ReplayQueue and ReplayWorker in DI container"
```
