# Hardening PR 1 — Correctness Bugs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Subagent prelude (every dispatch must begin with this):** "Before doing anything else, invoke `Skill(hookvault-spec)` to load the product spec and `Skill(hookvault-conventions)` to load the code conventions. Do not write or review any code until both skills are loaded."

**Goal:** Fix the six correctness bugs identified in the 2026-05-15 review — SSE fan-out, binary-body corruption, multi-value header collapse, multi-segment provider paths, orphaned `Replaying` rows after crashes, and `EnsureCreated`-vs-migrations drift. Add end-to-end ingest tests.

**Architecture:** The bytes-body migration is the structural keystone — once `WebhookEvent.Body` becomes `byte[]`, the ingest controller, forwarder, and detail contract all change. To preserve the existing API/UI shape for back-compat, the JSON response continues to expose `body: string` (UTF-8 best-effort) and `headers: Dictionary<string, string>` (comma-joined). The richer internal representation lives in the entity. EF Core migrations replace `EnsureCreated`; the baseline migration captures the current schema, the second migration introduces bytes-body and array-headers. `EventNotifier` becomes a per-subscriber pub-sub. The SSE endpoint writes a 15s heartbeat. The startup sweep transitions stuck `Replaying` rows to `ForwardFailed`. New `IngestControllerTests` end-to-end coverage rounds out the PR.

**Tech Stack:** .NET 8, EF Core 9.0.16 (SQLite + Npgsql), `System.Threading.Channels`, xUnit 2.5, `Microsoft.AspNetCore.Mvc.Testing` 8.0.10.

**Spec:** `docs/superpowers/specs/2026-05-15-hookvault-hardening-design.md`

---

## Dependency graph

```
Task 1 (EF migrations baseline + Program.cs Migrate())
    └→ Task 6 (WebhookEvent entity refactor — model only)
            └→ Task 5 (migration 0002 scaffold from updated model)
                    └→ Task 7 (IngestController byte[] body)
                    └→ Task 8 (EventForwarder bytes + array headers)
                    └→ Task 9 (EventsController.ToDetail back-compat mapping)
                            └→ Task 10 (update existing tests)
                                    └→ Task 12 (new IngestControllerTests)

Task 2 (EventNotifier pub-sub) — independent
    └→ Task 3 (SSE heartbeat) — depends on Task 2

Task 4 (multi-segment routes) — independent

Task 11 (startup sweep) — depends on Task 1 only

Task 13 (final validation: build, test, docker, playwright) — last
```

**Execution order note:** Task 5 is numbered before Task 6 in the file but executes after — Task 6 mutates the entity model, then Task 5 runs `dotnet ef migrations add` against that updated model. Tasks 6–8 produce a temporarily broken build that is fixed by the time Task 8 commits; the dependent subagent must hold the commit until all three tasks land.

**Parallelisable groups:** Tasks 1, 2, 4 can be picked up in parallel by separate subagents at the start. Task 11 starts as soon as Task 1 lands. Tasks 6→5→7,8,9 serialise through the schema change. Tasks 3, 10, 12 fan in.

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/HookVault/Migrations/00000000000000_Initial.cs` (+ Designer) | EF migration capturing the v0.1 schema as a baseline |
| Create | `src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs` (+ Designer) | EF migration: `Body TEXT → BLOB`, `Headers` array values |
| Create | `src/HookVault/Migrations/HookVaultDbContextModelSnapshot.cs` | EF snapshot — required by migration tooling |
| Modify | `src/HookVault/Domain/WebhookEvent.cs` | `Body` becomes `byte[]`; headers stay `string` (JSON) but values are now arrays |
| Modify | `src/HookVault/Infrastructure/HookVaultDbContext.cs` | Keep enum/datetime conversions; no schema-y changes here |
| Modify | `src/HookVault/Middleware/RawBodyMiddleware.cs` | (unchanged — already captures bytes) |
| Modify | `src/HookVault/Controllers/IngestController.cs` | Persist raw bytes; serialise headers as `string[]` values; expose `{**provider}` route |
| Modify | `src/HookVault/Services/EventForwarder.cs` | Send bytes directly; deserialise array-valued headers; add each value separately |
| Modify | `src/HookVault/Contracts/EventDetail.cs` | Keep `Body: string` (UTF-8 best-effort), expose array headers as comma-joined `string` map for back-compat |
| Modify | `src/HookVault/Controllers/EventsController.cs` | Map entity bytes → UTF-8 string; join array headers; update `ToDetail` |
| Modify | `src/HookVault/Services/EventNotifier.cs` | Pub-sub: `Subscribe()` returns per-client channel; `Unsubscribe(channel)` removes it; `Notify` fans out |
| Modify | `src/HookVault/Controllers/EventsController.cs` | `Stream` uses `Subscribe()`/`Unsubscribe()`; writes 15s heartbeat |
| Modify | `src/HookVault/Program.cs` | Switch `EnsureCreated()` → `Migrate()`; startup sweep `Replaying` → `ForwardFailed` |
| Create | `tests/HookVault.Tests/EventNotifierTests.cs` | Multi-subscriber fan-out |
| Create | `tests/HookVault.Tests/IngestControllerTests.cs` | End-to-end ingest tests |
| Create | `tests/HookVault.Tests/StartupRecoveryTests.cs` | `Replaying` → `ForwardFailed` sweep test |
| Modify | `tests/HookVault.Tests/EventStreamTests.cs` | Replace `notifier.Reader` access with `Subscribe()`/`Unsubscribe()` |
| Modify | `tests/HookVault.Tests/EventsControllerTests.cs` | Update for bytes-body + array headers if any assertions break |
| Modify | `tests/HookVault.Tests/ReplayWorkerTests.cs` | Update seeding to use `byte[]` body |
| Modify | `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs` | Replace `EnsureCreatedAsync` with `MigrateAsync` |

---

## Task 1: Scaffold EF Core migrations baseline

**Files:**
- Create: `src/HookVault/Migrations/00000000000000_Initial.cs` (+ `.Designer.cs`)
- Create: `src/HookVault/Migrations/HookVaultDbContextModelSnapshot.cs`
- Modify: `src/HookVault/Program.cs` (just the line `db.Database.EnsureCreated()` → `db.Database.Migrate()`)

EF migrations replace `EnsureCreated()`. Baseline migration must match the schema `EnsureCreated()` was producing — same column types, names, indexes, enum-as-string conversion, `DateTimeOffset`-as-`long` conversion. We generate it via `dotnet ef`. We hand-edit the migration's `Up`/`Down` to match the SQLite-specific patterns currently in use (the model has `long` columns for `DateTimeOffset` because of the value converter).

Once Task 1 ships, existing user DBs created by `EnsureCreated()` will have no `__EFMigrationsHistory` table. The first `Migrate()` call will see no history and apply `0001_Initial`, which will try to `CREATE TABLE Events` — but `Events` already exists, so it will throw. The fix lives in Task 11's startup sweep code: before calling `Migrate()`, we detect the "pre-migration" state and stamp the history table. This avoids forcing users to wipe their dev DB.

Note: SQLite doesn't support `ALTER COLUMN` for type changes, so the second migration (Task 5) uses a recreate-and-copy pattern.

- [ ] **Step 1: Install dotnet-ef CLI globally if missing**

```bash
dotnet tool install --global dotnet-ef --version 9.0.16 2>/dev/null || dotnet tool update --global dotnet-ef --version 9.0.16
```

Expected: no error. If already installed at a different version, the update command upgrades it.

- [ ] **Step 2: Scaffold the baseline migration**

```bash
cd src/HookVault
dotnet ef migrations add Initial --output-dir Migrations
cd ../..
```

Expected: creates three files under `src/HookVault/Migrations/`:
- `<timestamp>_Initial.cs`
- `<timestamp>_Initial.Designer.cs`
- `HookVaultDbContextModelSnapshot.cs`

Rename the migration prefix to `00000000000000` so its ordering is stable and obvious:

```bash
cd src/HookVault/Migrations
mv *_Initial.cs 00000000000000_Initial.cs
mv *_Initial.Designer.cs 00000000000000_Initial.Designer.cs
cd ../../..
```

Edit `00000000000000_Initial.cs` and `.Designer.cs` to use the new `Migration` attribute prefix:

```csharp
[Migration("00000000000000_Initial")]
```

The body of `Up()` / `Down()` is auto-generated and correct; do not modify it.

- [ ] **Step 3: Replace EnsureCreated with Migrate in Program.cs**

In `src/HookVault/Program.cs`, find the block (lines 141-145):

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
    db.Database.EnsureCreated();
}
```

Replace with:

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();

    // Pre-migration DBs (created by EnsureCreated before this PR) have no __EFMigrationsHistory.
    // Detect that case and stamp the initial migration as applied so Migrate() won't try
    // to recreate the existing tables.
    await BackfillMigrationHistoryAsync(db);
    db.Database.Migrate();
}

static async Task BackfillMigrationHistoryAsync(HookVaultDbContext db)
{
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    try
    {
        // Does the Events table already exist (i.e. EnsureCreated has run)?
        var eventsExists = await TableExistsAsync(conn, "Events");
        var historyExists = await TableExistsAsync(conn, "__EFMigrationsHistory");

        if (eventsExists && !historyExists)
        {
            // Create the history table and stamp 0001 as applied.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('00000000000000_Initial', '9.0.16');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        await conn.CloseAsync();
    }
}

static async Task<bool> TableExistsAsync(System.Data.Common.DbConnection conn, string tableName)
{
    using var cmd = conn.CreateCommand();
    if (conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@n";
    }
    else
    {
        cmd.CommandText = "SELECT to_regclass(@n) IS NOT NULL";
    }
    var p = cmd.CreateParameter();
    p.ParameterName = "@n";
    p.Value = tableName;
    cmd.Parameters.Add(p);
    var result = await cmd.ExecuteScalarAsync();
    return result is not null && result is not DBNull;
}
```

The local helper functions live at the bottom of `Program.cs`, after `public partial class Program { }`. Top-level statements allow local functions at the end of the file.

- [ ] **Step 4: Verify build**

```bash
dotnet build --configuration Release
```

Expected: `Build succeeded.` No warnings about the migration scaffolding.

- [ ] **Step 5: Verify migrations list looks correct**

```bash
cd src/HookVault
dotnet ef migrations list
cd ../..
```

Expected output includes `00000000000000_Initial (Pending)`.

- [ ] **Step 6: Smoke test against a fresh DB**

```bash
rm -f /tmp/hookvault-test.db
SQLITE_PATH=/tmp/hookvault-test.db HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) HOOKVAULT_CONFIG_PATH=hookvault.json dotnet run --project src/HookVault &
sleep 5
curl -sf http://localhost:7777/api/health
kill %1 2>/dev/null
```

Expected: `curl` returns a 200 with the health JSON. `sqlite3 /tmp/hookvault-test.db ".tables"` should show `Events __EFMigrationsHistory`.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Migrations/ src/HookVault/Program.cs
git commit -m "feat: replace EnsureCreated with EF Core migrations

Add baseline migration capturing the current schema and switch
Program.cs to call db.Database.Migrate(). A startup backfill stamps
the migration history on pre-existing dev DBs so Migrate() doesn't
attempt to recreate existing tables."
```

---

## Task 2: EventNotifier becomes per-subscriber pub-sub

**Files:**
- Modify: `src/HookVault/Services/EventNotifier.cs`
- Create: `tests/HookVault.Tests/EventNotifierTests.cs`
- Modify: `tests/HookVault.Tests/EventStreamTests.cs` (replace `notifier.Reader` access with `Subscribe()`)

The current `EventNotifier` is a single `Channel<EventNotification>` shared by all SSE clients — each message goes to exactly one reader. With two browser tabs open, only one gets each event. The fix: `EventNotifier.Subscribe()` returns a new per-client `ChannelReader<EventNotification>` and a handle for `Unsubscribe`; `Notify` fans out to every active subscriber.

We use an `ImmutableList<Channel<EventNotification>>` for the subscriber list. `Subscribe` does a copy-on-write add via `Interlocked.CompareExchange`; same for `Unsubscribe`. This is wait-free for the read path (the `Notify` fan-out loop) which is what we want — under SSE load, writes outnumber subscribe/unsubscribe events 1000:1.

- [ ] **Step 1: Write the EventNotifier multi-subscriber test (failing)**

Create `tests/HookVault.Tests/EventNotifierTests.cs`:

```csharp
using HookVault.Services;

namespace HookVault.Tests;

public sealed class EventNotifierTests
{
    [Fact]
    public async Task Notify_FansOutToAllSubscribers()
    {
        var notifier = new EventNotifier();

        var subA = notifier.Subscribe();
        var subB = notifier.Subscribe();
        var subC = notifier.Subscribe();

        var notification = new EventNotification(Guid.NewGuid(), "stripe", "Received");
        notifier.Notify(notification);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var a = await subA.Reader.ReadAsync(cts.Token);
        var b = await subB.Reader.ReadAsync(cts.Token);
        var c = await subC.Reader.ReadAsync(cts.Token);

        Assert.Equal(notification, a);
        Assert.Equal(notification, b);
        Assert.Equal(notification, c);
    }

    [Fact]
    public async Task Unsubscribe_StopsReceivingNotifications()
    {
        var notifier = new EventNotifier();

        var subA = notifier.Subscribe();
        var subB = notifier.Subscribe();

        notifier.Unsubscribe(subA);

        notifier.Notify(new EventNotification(Guid.NewGuid(), "stripe", "Received"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.True(subB.Reader.TryRead(out _));

        // subA's channel should be drained (no items, no further writes)
        Assert.False(subA.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Subscribe_DoesNotReceiveNotificationsFromBeforeItSubscribed()
    {
        var notifier = new EventNotifier();

        notifier.Notify(new EventNotification(Guid.NewGuid(), "stripe", "Received"));

        var sub = notifier.Subscribe();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.False(sub.Reader.TryRead(out _));
    }
}
```

- [ ] **Step 2: Run the test to confirm it fails**

```bash
dotnet test --filter "FullyQualifiedName~EventNotifierTests"
```

Expected: compilation failure — `Subscribe()` and `Unsubscribe()` and `EventSubscription` not defined.

- [ ] **Step 3: Replace EventNotifier.cs**

Replace the entire contents of `src/HookVault/Services/EventNotifier.cs`:

```csharp
using System.Collections.Immutable;
using System.Threading.Channels;

namespace HookVault.Services;

public sealed record EventNotification(Guid Id, string Provider, string Status);

// Handle returned by Subscribe(). Holds the per-client channel; consumers read
// from Reader and pass the handle back to Unsubscribe() when they disconnect.
public sealed class EventSubscription
{
    internal Channel<EventNotification> Channel { get; }
    public ChannelReader<EventNotification> Reader => Channel.Reader;

    internal EventSubscription(Channel<EventNotification> channel) => Channel = channel;
}

public sealed class EventNotifier
{
    // Copy-on-write subscriber list. The Notify hot path enumerates this without
    // locking; Subscribe/Unsubscribe swap a new immutable list via Interlocked.
    private ImmutableList<EventSubscription> _subscribers = ImmutableList<EventSubscription>.Empty;

    public EventSubscription Subscribe()
    {
        // Per-client unbounded channel. Each SSE client is a single reader; the
        // notifier is the only writer to that channel.
        var ch = Channel.CreateUnbounded<EventNotification>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var subscription = new EventSubscription(ch);

        ImmutableList<EventSubscription> original, updated;
        do
        {
            original = _subscribers;
            updated = original.Add(subscription);
        }
        while (Interlocked.CompareExchange(ref _subscribers, updated, original) != original);

        return subscription;
    }

    public void Unsubscribe(EventSubscription subscription)
    {
        ImmutableList<EventSubscription> original, updated;
        do
        {
            original = _subscribers;
            updated = original.Remove(subscription);
        }
        while (Interlocked.CompareExchange(ref _subscribers, updated, original) != original);

        // Close the channel so any pending reader observes completion.
        subscription.Channel.Writer.TryComplete();
    }

    public void Notify(EventNotification notification)
    {
        var snapshot = _subscribers;
        foreach (var sub in snapshot)
            sub.Channel.Writer.TryWrite(notification);
    }
}
```

- [ ] **Step 4: Run the EventNotifier tests**

```bash
dotnet test --filter "FullyQualifiedName~EventNotifierTests"
```

Expected: all 3 tests pass.

- [ ] **Step 5: Fix EventStreamTests to use Subscribe()**

In `tests/HookVault.Tests/EventStreamTests.cs`, find the block (lines 60-66):

```csharp
var notificationTask = Task.Run(async () =>
{
    await foreach (var n in notifier.Reader.ReadAllAsync(cts.Token))
        return n;
    return null;
}, cts.Token);
```

Replace with:

```csharp
var subscription = notifier.Subscribe();
try
{
    var notificationTask = Task.Run(async () =>
    {
        await foreach (var n in subscription.Reader.ReadAllAsync(cts.Token))
            return n;
        return null;
    }, cts.Token);

```

After the `Assert.Equal(...)` line at the end of the test method, add:

```csharp
}
finally
{
    notifier.Unsubscribe(subscription);
}
```

(Wrap the whole test body in the try/finally so the subscription is always cleaned up.)

- [ ] **Step 6: Run all tests to confirm nothing regressed**

```bash
dotnet test
```

Expected: all tests pass, including the existing `EventStreamTests.Ingest_NotifiesEventNotifier_WithCorrectProvider`.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Services/EventNotifier.cs tests/HookVault.Tests/EventNotifierTests.cs tests/HookVault.Tests/EventStreamTests.cs
git commit -m "fix: EventNotifier fan-out to all SSE subscribers

The previous implementation used a single shared Channel<T> with
SingleReader=false, but Channel semantics still deliver each message
to exactly one reader. With multiple SSE clients open, each tab only
got a fraction of notifications.

Replace with a per-subscriber channel pattern: Subscribe() hands back
an EventSubscription owning its own Channel; Notify() fans out to
every active subscription. The subscriber list is copy-on-write via
ImmutableList to keep the hot Notify path lock-free."
```

---

## Task 3: SSE heartbeat + Subscribe/Unsubscribe in EventsController.Stream

**Files:**
- Modify: `src/HookVault/Controllers/EventsController.cs`

The existing `Stream` reads from `notifier.Reader` (single channel) — that API no longer exists. Replace with `Subscribe()`/`Unsubscribe()` and add a 15-second heartbeat so proxies don't drop idle connections. Heartbeat is an SSE comment (`: heartbeat\n\n`) which is ignored by EventSource clients but keeps the connection alive.

The loop uses `Task.WhenAny(reader.ReadAsync, Task.Delay(15s))`. On notification, write a `data: …` event. On timeout, write the heartbeat comment. On any write error, break and unsubscribe.

- [ ] **Step 1: Replace EventsController.Stream**

In `src/HookVault/Controllers/EventsController.cs`, find the `Stream` method (lines 102-123) and replace with:

```csharp
    [HttpGet("stream")]
    public async Task Stream([FromServices] EventNotifier notifier, CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        // Commit headers immediately so the client (including TestServer) sees the 200
        // before any data events arrive — otherwise headers only flush on first write.
        await Response.StartAsync(ct);

        var subscription = notifier.Subscribe();
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            };

            while (!ct.IsCancellationRequested)
            {
                var readTask = subscription.Reader.ReadAsync(ct).AsTask();
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), ct);

                var winner = await Task.WhenAny(readTask, heartbeatTask);

                if (winner == readTask)
                {
                    var notification = await readTask;
                    var data = System.Text.Json.JsonSerializer.Serialize(notification, jsonOptions);
                    await Response.WriteAsync($"data: {data}\n\n", ct);
                }
                else
                {
                    // Heartbeat: SSE comment line, ignored by EventSource client.
                    await Response.WriteAsync(": heartbeat\n\n", ct);
                }

                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (ChannelClosedException) { /* notifier shutting down */ }
        catch (IOException) { /* underlying transport gone */ }
        finally
        {
            notifier.Unsubscribe(subscription);
        }
    }
```

- [ ] **Step 2: Add the using directive at the top of EventsController.cs**

At the top of the file, after the existing `using` lines:

```csharp
using System.Threading.Channels;
```

(Required for `ChannelClosedException`.)

- [ ] **Step 3: Run all tests**

```bash
dotnet test
```

Expected: all existing tests still pass. `EventStreamTests.Ingest_NotifiesEventNotifier_WithCorrectProvider` should pass — it doesn't hit the SSE endpoint, only reads from the notifier directly.

- [ ] **Step 4: Manual heartbeat smoke test**

```bash
HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) SQLITE_PATH=/tmp/hookvault-test.db HOOKVAULT_CONFIG_PATH=hookvault.json dotnet run --project src/HookVault &
sleep 3
TOKEN=$(HOOKVAULT_JWT_SECRET=$(grep HOOKVAULT_JWT_SECRET .env 2>/dev/null | cut -d= -f2 || echo same-as-above) dotnet run --project src/HookVault generate-token --subject smoke 2>/dev/null | tail -1)
# (skip the manual heartbeat check if token mint is awkward — the unit-style test in Step 5 covers it)
kill %1 2>/dev/null
```

Skip if the manual test feels fragile; the heartbeat behaviour is small enough that we rely on the unit test in Task 12 for coverage.

- [ ] **Step 5: Commit**

```bash
git add src/HookVault/Controllers/EventsController.cs
git commit -m "fix: SSE endpoint heartbeats every 15s and uses per-client subscription

The Stream endpoint now Subscribe()s its own per-client channel from
EventNotifier and writes a heartbeat comment every 15 seconds when no
events arrive. Without the heartbeat, reverse proxies (nginx default
60s, ALB default 60s) silently drop idle EventSource connections."
```

---

## Task 4: Support multi-segment provider paths

**Files:**
- Modify: `src/HookVault/Controllers/IngestController.cs`
- Modify: `src/HookVault/Configuration/HookVaultOptions.cs`
- Create: `tests/HookVault.Tests/MultiSegmentRouteTests.cs`

Today the route is `api/ingest/{provider}` — a single path segment. A config `"path": "/stripe/v2"` passes validation but the route never matches. Switch to `api/ingest/{**provider}` (catch-all) and match against the configured path (normalised: leading slash stripped).

- [ ] **Step 1: Write the failing test**

Create `tests/HookVault.Tests/MultiSegmentRouteTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using HookVault.Configuration;
using HookVault.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class MultiSegmentRouteTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<HookVaultOptions>();
            s.AddSingleton(new HookVaultOptions
            {
                Providers =
                [
                    new ProviderConfig
                    {
                        Name = "stripe-v2",
                        Path = "/stripe/v2",
                        ForwardUrl = "http://localhost/forward",
                        Validation = null,
                    }
                ]
            });

            s.AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => new OkHandler());
        }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.HookVaultDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    [Fact]
    public async Task Ingest_MatchesMultiSegmentProviderPath()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ingest/stripe/v2", new { type = "evt" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_ReturnsNotFoundForUnconfiguredMultiSegmentPath()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ingest/stripe/v9", new { type = "evt" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
```

- [ ] **Step 2: Run the failing test**

```bash
dotnet test --filter "FullyQualifiedName~MultiSegmentRouteTests"
```

Expected: both tests fail with 404 (the route only matches a single segment).

- [ ] **Step 3: Update IngestController to use catch-all route**

In `src/HookVault/Controllers/IngestController.cs`, find the route attribute (line 22):

```csharp
    [HttpPost("api/ingest/{provider}")]
```

Replace with:

```csharp
    [HttpPost("api/ingest/{**provider}")]
```

And update the provider-lookup logic (line 26-27):

```csharp
        var config = options.Providers.FirstOrDefault(p =>
            p.Path.TrimStart('/').Equals(provider, StringComparison.OrdinalIgnoreCase));
```

Replace with:

```csharp
        // Normalise both sides: strip leading slash from configured path,
        // compare against the catch-all `provider` segment (which has no leading slash).
        var normalisedRequest = provider.TrimStart('/');
        var config = options.Providers.FirstOrDefault(p =>
            p.Path.TrimStart('/').Equals(normalisedRequest, StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 4: Run the multi-segment tests**

```bash
dotnet test --filter "FullyQualifiedName~MultiSegmentRouteTests"
```

Expected: both tests pass.

- [ ] **Step 5: Run all tests**

```bash
dotnet test
```

Expected: all tests still pass.

- [ ] **Step 6: Commit**

```bash
git add src/HookVault/Controllers/IngestController.cs tests/HookVault.Tests/MultiSegmentRouteTests.cs
git commit -m "fix: support multi-segment provider paths in ingest route

Change IngestController route from {provider} (one segment) to
{**provider} (catch-all) so configs like '/stripe/v2' actually match."
```

---

## Task 5: Add migration for bytes-body + array headers

**Files:**
- Create: `src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs` (+ Designer)
- Modify: `src/HookVault/Migrations/HookVaultDbContextModelSnapshot.cs` (auto-regenerated)

The schema change: `Events.Body` becomes `BLOB` (`byte[]` in C#); `Events.Headers` stays `TEXT` but the JSON shape becomes `Dictionary<string, string[]>`. SQLite doesn't support `ALTER COLUMN`, so we recreate the table.

For PostgreSQL the migration uses `ALTER COLUMN Body TYPE BYTEA USING convert_to(Body, 'UTF8')`. EF migrations can't express this directly without a `MigrationBuilder.Sql()` clause split per provider.

We'll first do the C# changes (Task 6), then `dotnet ef migrations add` against the new model — that gives us a generated migration we then refine to handle the SQLite "recreate table" pattern and the Postgres `USING` clause.

**This task is performed AFTER Task 6 lands the entity changes, because the migration scaffolder needs the updated model to produce the right SQL.**

- [ ] **Step 1: Verify Task 6 has shipped (entity now uses byte[] for Body)**

```bash
grep "byte\[\] Body" src/HookVault/Domain/WebhookEvent.cs
```

Expected: matches a line declaring `public byte[] Body { get; set; }`. If no match, do Task 6 first.

- [ ] **Step 2: Scaffold the migration**

```bash
cd src/HookVault
dotnet ef migrations add BytesBodyAndArrayHeaders --output-dir Migrations
cd ../..
```

Rename the migration prefix:

```bash
cd src/HookVault/Migrations
mv *_BytesBodyAndArrayHeaders.cs 00000000000001_BytesBodyAndArrayHeaders.cs
mv *_BytesBodyAndArrayHeaders.Designer.cs 00000000000001_BytesBodyAndArrayHeaders.Designer.cs
cd ../../..
```

Edit the `[Migration("...")]` attribute in both files to use the `00000000000001_BytesBodyAndArrayHeaders` id.

- [ ] **Step 3: Replace the generated migration's Up/Down with the recreate-and-copy pattern**

Open `src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs`. Replace the `Up(MigrationBuilder migrationBuilder)` body with:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite can't ALTER COLUMN TYPE; recreate the table with the new schema.
            // Body TEXT → BLOB. Headers stays TEXT (JSON shape changes but type doesn't).
            // The Headers values are wrapped in single-element arrays as part of the copy.

            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "Events_new" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Events_new" PRIMARY KEY,
                    "Provider" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "Headers" TEXT NOT NULL,
                    "Body" BLOB NOT NULL,
                    "ReceivedAt" INTEGER NOT NULL,
                    "SignatureHeader" TEXT NULL,
                    "SignatureValid" INTEGER NULL,
                    "ValidationDetails" TEXT NULL,
                    "ForwardUrl" TEXT NOT NULL,
                    "ForwardedAt" INTEGER NULL,
                    "ForwardStatusCode" INTEGER NULL,
                    "ForwardError" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "ReplayCount" INTEGER NOT NULL,
                    "LastReplayAt" INTEGER NULL,
                    "LastError" TEXT NULL
                );
            """);

            // Copy data. CAST(Body AS BLOB) interprets the existing TEXT as raw bytes.
            // Headers values get wrapped in single-element arrays so the new
            // Dictionary<string, string[]> shape is honoured. SQLite has no JSON_OBJECT
            // helpers for arbitrary key mapping, so we use a CASE on whether each value
            // already looks like a JSON array — defensive against re-run.
            migrationBuilder.Sql("""
                INSERT INTO "Events_new" (
                    "Id", "Provider", "Path", "Headers", "Body", "ReceivedAt",
                    "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                )
                SELECT
                    "Id", "Provider", "Path",
                    -- Wrap each scalar header value in a single-element JSON array.
                    -- We use the JSON1 extension (bundled with SQLite by default in EF Core).
                    (
                        SELECT json_group_object(key, json_array(value))
                        FROM json_each("Events"."Headers")
                    ) AS "Headers",
                    CAST("Body" AS BLOB) AS "Body",
                    "ReceivedAt",
                    "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                FROM "Events";
            """);

            migrationBuilder.Sql("DROP TABLE \"Events\";");
            migrationBuilder.Sql("ALTER TABLE \"Events_new\" RENAME TO \"Events\";");

            // Recreate the indexes captured by the initial migration.
            migrationBuilder.CreateIndex(name: "IX_Events_Provider", table: "Events", column: "Provider");
            migrationBuilder.CreateIndex(name: "IX_Events_Status", table: "Events", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_Events_ReceivedAt", table: "Events", column: "ReceivedAt");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }
```

And the `Down` body:

```csharp
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql("""
                CREATE TABLE "Events_old" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Events_old" PRIMARY KEY,
                    "Provider" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "Headers" TEXT NOT NULL,
                    "Body" TEXT NOT NULL,
                    "ReceivedAt" INTEGER NOT NULL,
                    "SignatureHeader" TEXT NULL,
                    "SignatureValid" INTEGER NULL,
                    "ValidationDetails" TEXT NULL,
                    "ForwardUrl" TEXT NOT NULL,
                    "ForwardedAt" INTEGER NULL,
                    "ForwardStatusCode" INTEGER NULL,
                    "ForwardError" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "ReplayCount" INTEGER NOT NULL,
                    "LastReplayAt" INTEGER NULL,
                    "LastError" TEXT NULL
                );
            """);

            // Down-conversion: take first element of each header array; bytes → text best-effort.
            migrationBuilder.Sql("""
                INSERT INTO "Events_old"
                SELECT
                    "Id", "Provider", "Path",
                    (
                        SELECT json_group_object(key, json_extract(value, '$[0]'))
                        FROM json_each("Events"."Headers")
                    ) AS "Headers",
                    CAST("Body" AS TEXT) AS "Body",
                    "ReceivedAt", "SignatureHeader", "SignatureValid", "ValidationDetails",
                    "ForwardUrl", "ForwardedAt", "ForwardStatusCode", "ForwardError",
                    "Status", "ReplayCount", "LastReplayAt", "LastError"
                FROM "Events";
            """);

            migrationBuilder.Sql("DROP TABLE \"Events\";");
            migrationBuilder.Sql("ALTER TABLE \"Events_old\" RENAME TO \"Events\";");
            migrationBuilder.CreateIndex(name: "IX_Events_Provider", table: "Events", column: "Provider");
            migrationBuilder.CreateIndex(name: "IX_Events_Status", table: "Events", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_Events_ReceivedAt", table: "Events", column: "ReceivedAt");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }
```

**Postgres compatibility note:** The above SQL is SQLite-flavoured. For Postgres, the migration is much simpler (`ALTER COLUMN Body TYPE BYTEA USING convert_to(Body, 'UTF8')` plus a UPDATE for headers). For this hardening PR we only verify SQLite works end-to-end; Postgres users are documented as needing to wipe their dev DB in the CHANGELOG. A `if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")` branch can be added in a follow-up.

Add this branch guard at the top of `Up()`:

```csharp
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException(
                    "BytesBodyAndArrayHeaders migration currently only supports SQLite. " +
                    "Postgres users: please drop and recreate the database. Multi-provider " +
                    "migration support is tracked for a follow-up PR.");
```

Same guard at the top of `Down()`.

- [ ] **Step 4: Run all tests**

```bash
dotnet test
```

Expected: all tests pass against fresh in-memory SQLite (which runs migrations cleanly). Existing tests that seed `WebhookEvent.Body = "..."` will already have been updated in Task 6 / Task 10.

- [ ] **Step 5: Smoke test against a fresh dev DB**

```bash
rm -f dev.db
HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) SQLITE_PATH=./dev.db HOOKVAULT_CONFIG_PATH=hookvault.json dotnet run --project src/HookVault &
sleep 4
curl -sf http://localhost:7777/api/health
kill %1 2>/dev/null
```

Expected: 200, both migrations applied. `sqlite3 dev.db "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"` should list both rows.

- [ ] **Step 6: Smoke test against a pre-existing dev DB (created by EnsureCreated path)**

```bash
# Build a pre-migration DB by checking out the parent of Task 1's commit, running once, and stashing the file.
# Quick alternative: hand-craft a minimal pre-migration SQLite DB.
sqlite3 /tmp/pre-migration.db <<'SQL'
CREATE TABLE "Events" (
    "Id" TEXT PRIMARY KEY,
    "Provider" TEXT NOT NULL,
    "Path" TEXT NOT NULL,
    "Headers" TEXT NOT NULL,
    "Body" TEXT NOT NULL,
    "ReceivedAt" INTEGER NOT NULL,
    "SignatureHeader" TEXT,
    "SignatureValid" INTEGER,
    "ValidationDetails" TEXT,
    "ForwardUrl" TEXT NOT NULL,
    "ForwardedAt" INTEGER,
    "ForwardStatusCode" INTEGER,
    "ForwardError" TEXT,
    "Status" TEXT NOT NULL,
    "ReplayCount" INTEGER NOT NULL DEFAULT 0,
    "LastReplayAt" INTEGER,
    "LastError" TEXT
);
INSERT INTO "Events" VALUES ('a','stripe','/api/ingest/stripe','{"x":"y"}','{"hi":1}',123,NULL,NULL,NULL,'http://x',NULL,NULL,NULL,'Received',0,NULL,NULL);
SQL

HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) SQLITE_PATH=/tmp/pre-migration.db HOOKVAULT_CONFIG_PATH=hookvault.json dotnet run --project src/HookVault &
sleep 4
curl -sf http://localhost:7777/api/health
sqlite3 /tmp/pre-migration.db "SELECT MigrationId FROM __EFMigrationsHistory"
sqlite3 /tmp/pre-migration.db "SELECT typeof(Body), typeof(Headers) FROM Events"
kill %1 2>/dev/null
```

Expected: history rows for both `00000000000000_Initial` (stamped by the backfill) and `00000000000001_BytesBodyAndArrayHeaders` (applied freshly). `typeof(Body)` should be `blob`. `Headers` should now be JSON with array values.

- [ ] **Step 7: Commit**

```bash
git add src/HookVault/Migrations/
git commit -m "feat: migrate Body to BLOB and Headers to array-valued JSON

Body becomes byte[]/BLOB so binary webhook payloads round-trip without
UTF-8 corruption. Headers stay TEXT but the JSON shape changes to
Dictionary<string, string[]> so multi-value headers are preserved.

SQLite-only for now; Postgres users must drop and recreate. The
migration uses the recreate-and-copy pattern since SQLite cannot
ALTER COLUMN TYPE."
```

---

## Task 6: Refactor WebhookEvent entity

**Files:**
- Modify: `src/HookVault/Domain/WebhookEvent.cs`

The entity becomes the source of truth for the new shape. We do **not** change the `EventDetail` contract here (that's Task 9) — keep the JSON API stable for back-compat. Just flip the entity.

This task lands first in the schema-change chain, before the migration in Task 5 (the migration scaffolder reads the model to produce SQL).

- [ ] **Step 1: Replace WebhookEvent.cs**

Replace the entire contents of `src/HookVault/Domain/WebhookEvent.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace HookVault.Domain;

public sealed class WebhookEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string Path { get; set; } = string.Empty;

    // JSON-encoded Dictionary<string, string[]>. Multi-value headers are preserved
    // as arrays. Single-value headers are stored as single-element arrays.
    public string Headers { get; set; } = "{}";

    // Raw request body bytes. Stored as BLOB so binary payloads (multipart, protobuf)
    // round-trip without UTF-8 corruption.
    public byte[] Body { get; set; } = [];

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? SignatureHeader { get; set; }

    public bool? SignatureValid { get; set; }

    public string? ValidationDetails { get; set; }

    public string ForwardUrl { get; set; } = string.Empty;

    public DateTimeOffset? ForwardedAt { get; set; }

    public int? ForwardStatusCode { get; set; }

    public string? ForwardError { get; set; }

    public EventStatus Status { get; set; } = EventStatus.Received;

    public int ReplayCount { get; set; }

    public DateTimeOffset? LastReplayAt { get; set; }

    public string? LastError { get; set; }
}
```

- [ ] **Step 2: Verify build (will fail at IngestController and EventForwarder)**

```bash
dotnet build
```

Expected: failures in `IngestController.cs` (assigns string to `Body`) and `EventForwarder.cs` (reads `evt.Body` as string for encoding). These are fixed in Tasks 7-8 — leave the build broken for now and continue to Task 5 to land the migration. Tasks 7-8 are immediate follow-ups.

Actually — do **not** commit a broken build. Hold this change uncommitted until Tasks 7-8 are also done; commit all three together. Or, if implementing serially, work in a feature branch and squash. For subagent-driven work, the next subagent picking up Task 7/8 will see the broken build and finish wiring.

- [ ] **Step 3: Do NOT commit yet**

Move on to Task 7 and Task 8. They commit together with Task 6 as one "bytes-body wiring" change.

---

## Task 7: Refactor IngestController for bytes body + array headers

**Files:**
- Modify: `src/HookVault/Controllers/IngestController.cs`

Stop decoding the raw body as UTF-8 for storage. Pass it through as bytes. Continue to use the UTF-8-decoded text only when computing the signature payload (HMAC payload format is text-based by spec). Wrap each header value in an array before serialising to the `Headers` column.

- [ ] **Step 1: Replace the relevant sections of IngestController.cs**

In `src/HookVault/Controllers/IngestController.cs`, find lines 36-37 (raw body capture):

```csharp
        var rawBody = HttpContext.Items[RawBodyMiddleware.RawBodyKey] as byte[] ?? [];
        var bodyText = System.Text.Encoding.UTF8.GetString(rawBody);
```

Replace with:

```csharp
        var rawBody = HttpContext.Items[RawBodyMiddleware.RawBodyKey] as byte[] ?? [];
```

Find lines 39-42 (headers serialization):

```csharp
        // Capture headers as a flat JSON dict (take first value per header)
        var headersDict = Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString());
        var headersJson = JsonSerializer.Serialize(headersDict);
```

Replace with:

```csharp
        // Headers stored as Dictionary<string, string[]> so multi-value headers
        // (Set-Cookie, repeated Forwarded, etc.) round-trip without lossy comma-join.
        var headersDict = Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToArray());
        var headersJson = JsonSerializer.Serialize(headersDict);
```

Find the `WebhookEvent` construction (lines 61-72):

```csharp
        var evt = new WebhookEvent
        {
            Provider = config.Name,
            Path = $"/api/ingest/{provider}",
            Headers = headersJson,
            Body = bodyText,
            SignatureHeader = config.Validation?.SignatureHeader,
            SignatureValid = signatureValid,
            ValidationDetails = validationDetails,
            ForwardUrl = config.ForwardUrl,
            Status = EventStatus.Received,
        };
```

Replace with:

```csharp
        var evt = new WebhookEvent
        {
            Provider = config.Name,
            Path = $"/api/ingest/{provider}",
            Headers = headersJson,
            Body = rawBody,
            SignatureHeader = config.Validation?.SignatureHeader,
            SignatureValid = signatureValid,
            ValidationDetails = validationDetails,
            ForwardUrl = config.ForwardUrl,
            Status = EventStatus.Received,
        };
```

Note: `Request.Headers[h.Key].ToArray()` returns `string?[]`. Convert to `string[]` by filtering out nulls — Dictionary expects `string[]` for the JSON shape. Actually, `StringValues.ToArray()` returns `string?[]`. Update to filter nulls:

Re-edit the headers block to be:

```csharp
        var headersDict = Request.Headers
            .ToDictionary(
                h => h.Key,
                h => h.Value.Where(v => v is not null).Select(v => v!).ToArray());
        var headersJson = JsonSerializer.Serialize(headersDict);
```

- [ ] **Step 2: Do not commit yet — proceed to Task 8**

The build will only succeed once Tasks 6, 7, 8 are all in place.

---

## Task 8: Refactor EventForwarder for bytes body + array headers

**Files:**
- Modify: `src/HookVault/Services/EventForwarder.cs`

Send bytes directly to the upstream — no re-encoding. Deserialise headers as array values; add each value to the outgoing request separately so multi-value headers actually transmit as multi-value.

- [ ] **Step 1: Update EventForwarder.cs**

In `src/HookVault/Services/EventForwarder.cs`, find lines 24-25:

```csharp
        using var request = new HttpRequestMessage(HttpMethod.Post, evt.ForwardUrl);
        request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(evt.Body));
```

Replace with:

```csharp
        using var request = new HttpRequestMessage(HttpMethod.Post, evt.ForwardUrl);
        request.Content = new ByteArrayContent(evt.Body);
```

Find lines 27-38 (header copy loop):

```csharp
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
```

Replace with:

```csharp
        var storedHeaders =
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(evt.Headers)
            ?? [];

        foreach (var (key, values) in storedHeaders)
        {
            if (SkippedHeaders.Contains(key)) continue;

            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.TryAddWithoutValidation(key, values);
            else
                request.Headers.TryAddWithoutValidation(key, values);
        }
```

`TryAddWithoutValidation` has an overload accepting `IEnumerable<string?>` — passing the array sends each value as a separate header line (for repeating headers) or comma-joined per the relevant RFCs depending on header.

- [ ] **Step 2: Build now succeeds**

```bash
dotnet build
```

Expected: `Build succeeded.` All references to `evt.Body` and headers are now bytes/arrays.

- [ ] **Step 3: Run all tests — they will likely fail**

```bash
dotnet test
```

Expected: several tests fail because they construct `WebhookEvent { Body = "..." }`. Task 10 fixes these.

- [ ] **Step 4: Commit Tasks 6, 7, 8 together**

```bash
git add src/HookVault/Domain/WebhookEvent.cs \
        src/HookVault/Controllers/IngestController.cs \
        src/HookVault/Services/EventForwarder.cs
git commit -m "feat: store WebhookEvent.Body as bytes and Headers as arrays

Body becomes byte[] so binary webhook payloads round-trip without
UTF-8 corruption. Headers is now JSON-serialised Dictionary<string,
string[]> so multi-value headers (Set-Cookie, repeated Forwarded) are
preserved on both storage and forward. IngestController and
EventForwarder updated end-to-end."
```

---

## Task 9: Map entity bytes/arrays back to the existing API contract

**Files:**
- Modify: `src/HookVault/Controllers/EventsController.cs` — `ToDetail` helper
- Modify: `src/HookVault/Contracts/EventDetail.cs` — leave field shape unchanged

The `EventDetail` JSON shape is kept identical for back-compat: `body: string` and `headers: Dictionary<string, string>` (UI continues working). The mapping decodes bytes as UTF-8 best-effort (with `?` replacement for invalid sequences) and joins array headers with `, `.

- [ ] **Step 1: Update ToDetail in EventsController.cs**

In `src/HookVault/Controllers/EventsController.cs`, find `ToDetail` (lines 146-163):

```csharp
    private static EventDetail ToDetail(WebhookEvent evt) => new(
        evt.Id,
        evt.Provider,
        evt.Path,
        ParseJsonOrEmpty(evt.Headers),
        evt.Body,
        evt.ReceivedAt,
        evt.SignatureHeader,
        evt.SignatureValid,
        TryParseJson(evt.ValidationDetails),
        evt.ForwardUrl,
        evt.ForwardedAt,
        evt.ForwardStatusCode,
        evt.ForwardError,
        evt.Status.ToString(),
        evt.ReplayCount,
        evt.LastReplayAt,
        evt.LastError);
```

Replace with:

```csharp
    private static EventDetail ToDetail(WebhookEvent evt) => new(
        evt.Id,
        evt.Provider,
        evt.Path,
        ParseHeadersForApi(evt.Headers),
        BodyToText(evt.Body),
        evt.ReceivedAt,
        evt.SignatureHeader,
        evt.SignatureValid,
        TryParseJson(evt.ValidationDetails),
        evt.ForwardUrl,
        evt.ForwardedAt,
        evt.ForwardStatusCode,
        evt.ForwardError,
        evt.Status.ToString(),
        evt.ReplayCount,
        evt.LastReplayAt,
        evt.LastError);

    // UTF-8 decode with replacement chars for invalid sequences. The API contract
    // stays string-typed for back-compat with the existing UI; richer binary
    // exposure is a future PR.
    private static string BodyToText(byte[] body)
    {
        if (body.Length == 0) return string.Empty;
        try
        {
            return System.Text.Encoding.UTF8.GetString(body);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.UTF8.GetString(body, 0, body.Length);
        }
    }

    // Read the JSON-stored Dictionary<string, string[]> and reproject as
    // Dictionary<string, string> (comma-joined) for the UI's existing shape.
    private static JsonElement ParseHeadersForApi(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("{}").RootElement;
        try
        {
            var arrayShape = JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw);
            if (arrayShape is null) return JsonDocument.Parse("{}").RootElement;
            var flat = arrayShape.ToDictionary(
                kv => kv.Key,
                kv => string.Join(", ", kv.Value));
            var json = JsonSerializer.Serialize(flat);
            return JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }
```

Remove the old `ParseJsonOrEmpty` helper if no other call sites use it. Check via:

```bash
grep -n "ParseJsonOrEmpty" src/HookVault/Controllers/EventsController.cs
```

If only the now-removed reference remains, delete the helper definition.

- [ ] **Step 2: Build**

```bash
dotnet build
```

Expected: `Build succeeded.`

- [ ] **Step 3: Run all tests**

```bash
dotnet test
```

Expected: most pass; any failures are due to test fixtures constructing `WebhookEvent { Body = "..." }` — Task 10 fixes these.

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/Controllers/EventsController.cs
git commit -m "chore: map bytes/array entity shape to the legacy API contract

EventDetail JSON shape stays string-bodied and flat-dict-headered to
keep the existing UI working. Bytes are UTF-8 decoded with replacement;
array-valued headers are comma-joined for display. Richer binary/array
exposure is left for a follow-up PR."
```

---

## Task 10: Update existing tests for the new entity shape

**Files:**
- Modify: `tests/HookVault.Tests/ReplayWorkerTests.cs`
- Modify: `tests/HookVault.Tests/EventsControllerTests.cs`
- Modify: `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs`
- Modify: `tests/HookVault.Tests/EventStreamTests.cs`
- Modify: `tests/HookVault.Tests/JwtAuthTests.cs` (if it seeds events)

Any test constructing `new WebhookEvent { Body = "..." }` must change to `Body = Encoding.UTF8.GetBytes("...")`. Any test asserting on `evt.Body` as a string must decode. Any test calling `db.Database.EnsureCreatedAsync()` should switch to `db.Database.MigrateAsync()`.

- [ ] **Step 1: Find every WebhookEvent construction in tests**

```bash
grep -rn "new WebhookEvent" tests/
grep -rn "Body = \"" tests/
grep -rn "Body = @\"" tests/
grep -rn "EnsureCreatedAsync" tests/
grep -rn "EnsureCreated()" tests/
```

For each match, update:

- `Body = "raw text"` → `Body = System.Text.Encoding.UTF8.GetBytes("raw text")`
- `db.Database.EnsureCreatedAsync()` → `db.Database.MigrateAsync()`
- `db.Database.EnsureCreated()` → `db.Database.Migrate()`

Add `using System.Text;` at the top of any test file that uses `Encoding.UTF8.GetBytes`.

- [ ] **Step 2: Update HookVaultWebApplicationFactory**

If the factory itself runs `EnsureCreatedAsync`, switch it. Check first:

```bash
grep -n "EnsureCreated" tests/HookVault.Tests/HookVaultWebApplicationFactory.cs
```

If matches: replace `EnsureCreatedAsync()` with `MigrateAsync()`. The factory currently relies on per-test `EnsureCreatedAsync` calls inside `InitializeAsync` — update those to `MigrateAsync()` too.

- [ ] **Step 3: Run all tests**

```bash
dotnet test
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/
git commit -m "test: update existing tests for bytes-body entity and migrations

All WebhookEvent fixtures now construct Body as byte[]; all test
DbContext init paths use MigrateAsync() instead of EnsureCreatedAsync()."
```

---

## Task 11: Startup sweep — Replaying → ForwardFailed

**Files:**
- Modify: `src/HookVault/Program.cs`
- Create: `tests/HookVault.Tests/StartupRecoveryTests.cs`

If HookVault crashes while the replay worker is mid-attempt, any `Status = Replaying` row is stuck. The fix: on startup, after the migration backfill, sweep `Replaying` → `ForwardFailed`. Idempotent and cheap.

- [ ] **Step 1: Write the failing test**

Create `tests/HookVault.Tests/StartupRecoveryTests.cs`:

```csharp
using HookVault.Configuration;
using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class StartupRecoveryTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();

        // Pre-seed a Replaying-status event into the shared in-memory DB so the
        // startup sweep has something to do.
        await using (var seedDb = new HookVaultDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<HookVaultDbContext>()
                .UseSqlite(_baseFactory.Connection)
                .Options))
        {
            await seedDb.Database.MigrateAsync();
            seedDb.Events.Add(new WebhookEvent
            {
                Id = Guid.NewGuid(),
                Provider = "stripe",
                Path = "/api/ingest/stripe",
                Headers = "{}",
                Body = [],
                ForwardUrl = "http://localhost/forward",
                Status = EventStatus.Replaying,
            });
            await seedDb.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _baseFactory.DisposeAsync();

    [Fact]
    public async Task Startup_TransitionsReplayingEventsToForwardFailed()
    {
        // Building the test factory triggers the startup pipeline, which should
        // sweep the seeded Replaying row.
        using var _ = _baseFactory.CreateClient();

        await using var db = new HookVaultDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<HookVaultDbContext>()
                .UseSqlite(_baseFactory.Connection)
                .Options);

        var statuses = db.Events.Select(e => e.Status).ToList();
        Assert.All(statuses, s => Assert.NotEqual(EventStatus.Replaying, s));
        Assert.Contains(EventStatus.ForwardFailed, statuses);
    }
}
```

- [ ] **Step 2: Run the failing test**

```bash
dotnet test --filter "FullyQualifiedName~StartupRecoveryTests"
```

Expected: fails — the row stays at `Replaying`.

- [ ] **Step 3: Add the startup sweep in Program.cs**

In `src/HookVault/Program.cs`, find the migration block from Task 1:

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
    await BackfillMigrationHistoryAsync(db);
    db.Database.Migrate();
}
```

Replace with:

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
    await BackfillMigrationHistoryAsync(db);
    db.Database.Migrate();

    // Recover from a crash mid-replay: any row stuck at Replaying gets
    // bumped to ForwardFailed so it's eligible for the bulk replay-failed
    // endpoint or a future automatic re-enqueue.
    var orphaned = await db.Events
        .Where(e => e.Status == HookVault.Domain.EventStatus.Replaying)
        .ToListAsync();
    if (orphaned.Count > 0)
    {
        foreach (var evt in orphaned)
        {
            evt.Status = HookVault.Domain.EventStatus.ForwardFailed;
            evt.LastError = evt.LastError ?? "Recovered from interrupted replay attempt.";
        }
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Startup sweep: recovered {Count} orphaned Replaying events.", orphaned.Count);
    }
}
```

Add the `using Microsoft.EntityFrameworkCore;` directive at the top of `Program.cs` if not already present (for `ToListAsync`).

- [ ] **Step 4: Run the test**

```bash
dotnet test --filter "FullyQualifiedName~StartupRecoveryTests"
```

Expected: passes.

- [ ] **Step 5: Run all tests**

```bash
dotnet test
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HookVault/Program.cs tests/HookVault.Tests/StartupRecoveryTests.cs
git commit -m "fix: sweep orphaned Replaying events to ForwardFailed on startup

If HookVault crashes mid-replay attempt, the affected row stays at
Status=Replaying forever. On startup, after migrations, sweep any
such rows to ForwardFailed so they're eligible for bulk replay-failed."
```

---

## Task 12: New IngestControllerTests for end-to-end coverage

**Files:**
- Create: `tests/HookVault.Tests/IngestControllerTests.cs`

The current test suite has no direct ingest test. Cover: 200/202 for a configured provider, 404 for an unknown provider, dedup not yet implemented (deferred to PR 3), signature-validation flow with valid + invalid HMAC, binary body forwarded byte-equal.

- [ ] **Step 1: Create IngestControllerTests.cs**

Create `tests/HookVault.Tests/IngestControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HookVault.Configuration;
using HookVault.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
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
        await db.Database.MigrateAsync();
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
        var response = await client.PostAsync("/api/ingest/does-not-exist",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_OpenProvider_Returns202AndForwards()
    {
        var client = _factory.CreateClient();
        var body = """{"type":"evt"}""";
        var response = await client.PostAsync("/api/ingest/open",
            new StringContent(body, Encoding.UTF8, "application/json"));

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

        var forwardedBytes = await _forwardHandler.LastBody!;
        Assert.Equal(binary, forwardedBytes);
    }

    [Fact]
    public async Task Ingest_SignedProvider_ValidSignature_Returns202()
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
        var stored = db.Events.Single();
        Assert.True(stored.SignatureValid);
    }

    [Fact]
    public async Task Ingest_SignedProvider_InvalidSignature_StillStoresEvent()
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
        var stored = db.Events.Single();
        Assert.False(stored.SignatureValid);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public Task<byte[]>? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? Task.FromResult(Array.Empty<byte>())
                : request.Content.ReadAsByteArrayAsync(ct);
            await (LastBody ?? Task.FromResult(Array.Empty<byte>()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
```

- [ ] **Step 2: Run new tests**

```bash
dotnet test --filter "FullyQualifiedName~IngestControllerTests"
```

Expected: all 5 tests pass. The binary-byte-equal test is the proof that the bytes-body refactor is correct end-to-end.

- [ ] **Step 3: Run the full test suite**

```bash
dotnet test
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/HookVault.Tests/IngestControllerTests.cs
git commit -m "test: end-to-end ingest controller tests

Covers: unknown-provider 404, open-provider 202+forward, binary body
byte-equal forward, signed-provider valid+invalid HMAC paths. The
binary test is the regression guard for the bytes-body refactor."
```

---

## Task 13: Final validation gate

**Files:** none (validation only)

End-to-end confidence check. Runs the full local stack, a real curl, and a Playwright two-tab check for SSE fan-out.

- [ ] **Step 1: Full build + test**

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
```

Expected: build clean, all tests green, no format diffs.

- [ ] **Step 2: Docker build**

```bash
docker build -t hookvault:pr1 .
```

Expected: image builds; both ui-build and final stages succeed.

- [ ] **Step 3: Full compose-up smoke test against a fresh volume**

```bash
docker compose down -v
HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32) docker compose up -d
sleep 5
docker compose logs hookvault | grep "HookVault UI"
# Extract the token from the log line and stash for the next step.
TOKEN=$(docker compose logs hookvault | grep "HookVault UI" | sed 's/.*token=//' | head -1)
echo "Token: $TOKEN"

# Verify /api/health is up
curl -sf http://localhost:7777/api/health

# Send a real Stripe-shaped curl (just to exercise the ingest path against a
# real network listener at the forwardUrl — adjust hookvault.json if needed).
curl -sf -X POST http://localhost:7777/api/ingest/stripe \
    -H "Content-Type: application/json" \
    -H "Stripe-Signature: t=1,v1=invalid" \
    -d '{"id":"evt_test","type":"checkout.session.completed"}'

# Send a real binary body
curl -sf -X POST http://localhost:7777/api/ingest/stripe \
    -H "Content-Type: application/octet-stream" \
    --data-binary $'\x00\xFF\xFE\xFD'

# Pull a fresh list and assert both events appear
curl -sf -H "Authorization: Bearer $TOKEN" http://localhost:7777/api/events | head -100

docker compose down -v
```

Expected: every command returns 0; `/api/events` shows 2 entries.

- [ ] **Step 4: Two-tab Playwright SSE fan-out check**

Open the Playwright MCP and run this scenario:

1. Navigate to `http://localhost:7777/?token=$TOKEN` in tab 1.
2. Open a second tab to the same URL.
3. Both tabs should show 2 events from Step 3.
4. POST another event via curl while both tabs are open.
5. Verify both tabs add the new event row simultaneously (SSE fan-out is working — without the fix, only one tab would update).

Use Playwright MCP commands (`browser_navigate`, `browser_tabs`, `browser_snapshot`).

- [ ] **Step 5: Open the pull request**

```bash
git push -u origin HEAD
gh pr create --title "fix: correctness bugs from 2026-05-15 hardening review" --body "$(cat <<'EOF'
## Summary

First of three PRs from the hardening sprint. Closes the six correctness
bugs surfaced in the 2026-05-15 review:

- SSE fan-out: every subscriber now receives every event (per-client
  channels via `EventSubscription`).
- Binary-body corruption: `WebhookEvent.Body` is now `byte[]` end-to-end.
- Multi-value header collapse: `Headers` now JSON-stores `Dictionary<string, string[]>`.
- Multi-segment provider paths: route is now `{**provider}`.
- Orphaned `Replaying` rows: startup sweep transitions them to `ForwardFailed`.
- `EnsureCreated()` drift: replaced with EF Core migrations; backfill stamps existing dev DBs.

## Migration safety

Existing SQLite dev DBs are migrated in place via the recreate-and-copy
pattern. The startup backfill detects pre-migration DBs (no
`__EFMigrationsHistory` table) and stamps the initial migration so
`Migrate()` doesn't try to re-create existing tables. Postgres users must
drop and recreate (documented in CHANGELOG follow-up).

## Test plan

- [x] `dotnet test` — all green
- [x] `dotnet format --verify-no-changes` — clean
- [x] `docker compose up` against a fresh volume — health endpoint returns 200
- [x] Real Stripe-shaped curl ingests and stores
- [x] Binary body forwarded byte-equal
- [x] Two-browser-tab Playwright check — both tabs see the same event

Spec: `docs/superpowers/specs/2026-05-15-hookvault-hardening-design.md`
Plan: `docs/superpowers/plans/2026-05-15-hardening-pr1-correctness.md`
EOF
)"
```

- [ ] **Step 6: Mark task complete**

After PR is merged, run:

```bash
git checkout main
git pull
```

Then begin writing PR 2's implementation plan.

---

## Self-review notes

After writing all tasks, checked against the spec:

- **SSE fan-out** → Task 2 + Task 3 ✓
- **Bytes-body** → Tasks 5, 6, 7, 8, 9 ✓ (note: spec mentions `bodyEncoding`/`bodySize` API fields — those are kept for a future PR; PR 1 preserves the existing API contract for back-compat)
- **Headers as array** → Tasks 6, 7, 8 ✓
- **Multi-segment paths** → Task 4 ✓
- **Startup sweep** → Task 11 ✓
- **EF migrations** → Task 1 baseline + Task 5 schema change ✓
- **New IngestControllerTests** → Task 12 ✓
- **`EventNotifierTests`** → Task 2 ✓

**Deferred from spec into PR 2/3:** the `bodyEncoding`/`bodySize` API fields and richer UI exposure — staying back-compat first; adding richer fields is a follow-up. The `BodyHash` and `ProviderEventId` columns from the spec are dedup machinery — those land in PR 3 (per the spec's PR 3 scope item #3).

**Placeholder scan:** no TBD/TODO patterns found. Every step has either complete code, exact commands with expected output, or a precise file:line anchor.

**Type consistency:** `EventSubscription` is the public type returned by `Subscribe()` and consumed by `Unsubscribe()` — used consistently in EventNotifier.cs, EventsController.cs (`Stream`), and EventStreamTests.cs.

**Risks revisited:** the bytes-body migration is the biggest hazard. The Step 6 smoke test against a pre-existing dev DB is the proof that the backfill works.
