# Phase 2 — Replay System Design

**Date:** 2026-05-14  
**Status:** Approved  
**Scope:** `ReplayQueue`, `ReplayWorker`, `EventForwarder` refactor, integration tests

---

## Context

Phase 1 is complete: domain entity, EF Core context, signature validation, ingest
controller, event forwarding, health endpoint, and CI are all in place. `EventStatus`
already includes `Replaying` and `ReplayFailed`; `EventRepository` already has
`GetFailedAsync`. Phase 2 slots in cleanly with no domain changes.

Phase 3 (management API + JWT auth) will add the HTTP endpoints that enqueue events
for replay. Phase 2 builds only the internal machinery.

---

## Decisions

| Question | Decision | Rationale |
|---|---|---|
| Phase scope | Queue + worker + tests only | Replay endpoints are part of the JWT-protected management API (Phase 3). Adding them now means unprotected endpoints or premature auth plumbing. |
| `Channel<T>` type | `Channel<Guid>` | Worker always reads fresh state from DB; channel holds tiny payloads; no stale-object bugs. |
| Worker ↔ forwarder | Extract `SendAsync` from `EventForwarder` | Shared HTTP logic, separate status management. Avoids duplication without leaking replay status into the ingest path. |

---

## Architecture

```
Ingest path (unchanged):
  IngestController
    → EventForwarder.ForwardAsync     (Forwarding → Forwarded / ForwardFailed)
        → EventForwarder.SendAsync    (pure HTTP, returns ForwardResult)

Replay path (new):
  Phase 3 API → ReplayQueue.EnqueueAsync(Guid)
                      ↓ Channel<Guid>
               ReplayWorker.ExecuteAsync   (BackgroundService, always running)
                 per item:
                   IServiceScopeFactory.CreateScope()
                   → EventRepository.GetByIdAsync
                   → EventForwarder.SendAsync   (shared)
                   → EventRepository.UpdateAsync  (Replaying → Forwarded / ReplayFailed)
```

---

## Files

### Modified

**`src/HookVault/Services/EventForwarder.cs`**

Extract the HTTP-send logic from `ForwardAsync` into a new `internal` method:

```csharp
internal sealed record ForwardResult(bool Success, int? StatusCode, string? Error);

internal async Task<ForwardResult> SendAsync(WebhookEvent evt, CancellationToken ct)
{
    // builds HttpRequestMessage, copies headers, calls client.SendAsync
    // returns ForwardResult — no DB touches, no status mutations
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
```

`SendAsync` is `internal` — shared implementation detail within the assembly, not public API.

**`src/HookVault/Program.cs`**

Add after existing service registrations:

```csharp
builder.Services.AddSingleton<ReplayQueue>();
builder.Services.AddHostedService<ReplayWorker>();
```

### New

**`src/HookVault/Services/ReplayQueue.cs`**

Singleton wrapper around `Channel<Guid>`. Exposes `EnqueueAsync` and `Reader`.

```csharp
public sealed class ReplayQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid eventId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(eventId, ct);
}
```

`SingleReader = true` is a perf hint; `UnboundedChannel` requires no backpressure for
a local dev tool.

**`src/HookVault/Services/ReplayWorker.cs`**

`BackgroundService` that dequeues event IDs and forwards with retry + exponential backoff.

Key design points:
- `IServiceScopeFactory` used to create a scope per item — required because
  `EventRepository` and `EventForwarder` are scoped, but `BackgroundService` is singleton.
- `RetryDelays` is `internal` with `init` so tests can inject `[TimeSpan.Zero, ...]`
  without waiting 7 seconds.
- `ReadAllAsync` exits cleanly when `stoppingToken` is cancelled (graceful shutdown).
- `ReplayCount` increments once per replay trigger (not per HTTP attempt).
- 4 total attempts: 1 initial + 3 retries matching the 3 delay slots `[1s, 2s, 4s]`.

Status flow per item:
```
load event → Replaying (saved) → attempt loop:
  success  → Forwarded (saved), return
  failure  → wait delay, retry
  exhausted → ReplayFailed (saved)
```

**`tests/HookVault.Tests/ReplayWorkerTests.cs`**

Integration tests using:
- Real SQLite (`DataSource=:memory:`) — per project conventions
- Custom `HttpMessageHandler` returning a pre-programmed response sequence
- `RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]` so tests don't sleep

| Test | HTTP responses | Expected status | ReplayCount |
|---|---|---|---|
| Event not in DB | — | no crash, warning logged | — |
| Success on attempt 1 | 200 | `Forwarded` | 1 |
| Fail twice, succeed third | 500, 500, 200 | `Forwarded` | 1 |
| All attempts fail | 500 ×4 | `ReplayFailed` | 1 |

---

## DI Lifetimes

| Service | Lifetime | Reason |
|---|---|---|
| `ReplayQueue` | Singleton | Owns the `Channel<Guid>` for the app lifetime |
| `ReplayWorker` | Singleton (via `AddHostedService`) | `BackgroundService` is always singleton |
| `EventForwarder` | Scoped (unchanged) | Depends on scoped `EventRepository` |
| `EventRepository` | Scoped (unchanged) | Depends on scoped `DbContext` |

---

## Out of Scope (Phase 3)

- `POST /api/events/{id}/replay`
- `POST /api/events/replay-failed`
- JWT authentication
