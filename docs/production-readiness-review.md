# Production-readiness review

**Date:** 2026-10-18
**Scope:** what stands between HookVault v1.0 (a single-container local-dev tool, see `architecture-audit-v1.0.md`) and a service that can run unattended in a cloud environment, under load, with more than one replica, and recover from failure.
**Verdict:** the codebase is in better shape than most projects that call themselves production-ready: tests at three layers, CI with Postgres smoke and e2e, non-root multi-stage image, metrics, timeouts, bounded queue, fixed-time HMAC comparison. The v1.0 audit deliberately scoped the work to a single replica and a developer's laptop (its decision S5). Everything below is the cost of lifting that scope. None of it is a reason not to deploy Stage 1; most of it is meant to be *discovered by a drill first* and then fixed, because that is how the fix sticks.

Severity is about production impact, not code quality. "Exposed by" names the drill in `ROADMAP.md` that will make the problem visible on a dashboard before it is fixed.

## Findings

### 1. Ingest forwards synchronously inside the provider's request — High

`Controllers/IngestController.cs`, the `forwarder.ForwardAsync(evt, ct)` call near the end of `Ingest`.

The provider (Stripe, GitHub) holds its connection open while HookVault persists the event **and** forwards it to the destination, up to the 30 s forward timeout. Under load, ingest latency equals destination latency. Providers typically time out at 5 to 10 s and retry, so a slow destination turns into duplicate deliveries and a growing dedup workload. The ALB's idle timeout (60 s default) will also cut the connection and return a 502 to the provider.

For a local tool this was a reasonable choice (the response tells you whether the forward worked). For a service, ingest should persist, return 202 in milliseconds, and hand forwarding to the worker. The response then reports `Queued`, and the UI's live feed shows the outcome.

- Exposed by: Stage 0 baseline with the 10% slow destination; Stage 3 saturation test.
- Fixed in: Stage 7 (together with finding 2, since they share the queue).
- Read first: the HTTP semantics of 202 Accepted; your own `ReplayWorker`, which already does this for replays.

### 2. The replay queue is in-memory and single-reader — High

`Services/ReplayQueue.cs` (bounded `Channel<ReplayJob>`, 10,000, `FullMode.Wait`), `Services/ReplayWorker.cs` (one reader, retries with `Task.Delay` inside the loop).

Three production consequences:

- **Lost work on restart.** Jobs enqueued but not yet started vanish when the process stops. The startup sweep only recovers rows already marked `Replaying`. A deploy during a bulk replay silently drops the rest.
- **Head-of-line blocking.** One slow destination (30 s timeout × 4 attempts, plus 1 + 2 + 4 s of sleeps, about 2 minutes 7 seconds) blocks every other replay behind it. The retry sleeps happen inside the only consumer.
- **No horizontal scaling.** Two replicas means two independent queues and two workers that may both pick up the same event (nothing claims a row), and the SSE notifier in `EventNotifier.cs` only fans out within one process.

The production shape is a database-backed job table (or an external queue): the worker claims rows with `SELECT ... FOR UPDATE SKIP LOCKED`, several workers run concurrently with a per-destination cap, and a retry is a `next_attempt_at` timestamp rather than a sleep. The `Channel` can stay as an in-process wake-up signal. This is the single most instructive change in the roadmap because it is the same mechanism as the `select_for_update` work at Miribeth, seen from the consumer side.

- Exposed by: Stage 4 drills 1 (kill the process mid-bulk-replay), 4 and 5 (slow destination), and Stage 6 (two replicas).
- Fixed in: Stage 7, with an ADR.
- Read first: Postgres `SELECT ... FOR UPDATE SKIP LOCKED`; the "transactional outbox" pattern; .NET `BackgroundService` with bounded concurrency.

### 3. Bulk replay loads every failed event into memory and can block the request indefinitely — High

`Controllers/EventsController.cs`, `ReplayFailed`: `repo.GetFailedAsync` returns full rows including bodies, then `foreach ... await queue.EnqueueAsync`.

With 20,000 failed events of 50 KB each that is a gigabyte of bodies in one request, and after the first 10,000 enqueues the request awaits a full channel until the single worker drains it, which can be hours. The client, or the ALB at 60 s, gives up; the cancellation token then stops the loop part-way through, so the operation is also non-atomic.

- Exposed by: Stage 4 drill 9 (bulk replay after a destination outage).
- Fixed in: Stage 7, as a consequence of finding 2 (an `UPDATE ... SET status = 'Queued' WHERE status IN (...)` becomes the whole operation).

### 4. Dedup has a check-then-insert race — Medium

`IngestController.Ingest`: `FindDuplicateAsync` followed by `AddAsync`, with only non-unique indexes on `BodyHash` and `ProviderEventId` (`Infrastructure/HookVaultDbContext.cs`).

Two identical deliveries arriving within milliseconds (exactly what a provider retry produces) both miss the lookup and both insert. Correctness needs a unique constraint the database enforces: `(Provider, ProviderEventId)` where the provider supplies an id, and a catch of the unique-violation that returns the existing row. The 24-hour window for hash-only dedup can stay as a lookup; it cannot be a constraint.

- Exposed by: Stage 4 drill 10 (10,000 identical webhooks at once).
- Fixed in: Stage 7.
- Read first: unique partial indexes in Postgres; why "check then act" is never safe across connections.

### 5. The health endpoint runs `COUNT(*)` and `MIN(ReceivedAt)` on every call — Medium

`Controllers/HealthController.cs`.

Docker calls it every 30 s and the ALB target group will call it every 10 s per target. On Postgres, `COUNT(*)` over a large table is a sequential scan; at the 5 million rows Stage 5 generates, the health check becomes a measurable share of database load, and a slow health check marks the target unhealthy exactly when the system is busiest. There is also no distinction between *alive* (process is up) and *ready* (database reachable, migrations applied), which Kubernetes and the ALB both want.

- Exposed by: Stage 5 (5 M rows) and Stage 6 (probes).
- Fixed in: Stage 2 (cheap liveness and readiness) with the statistics moved to a separate, authenticated `/api/stats`.

### 6. The body-size cap runs after the whole body is already in memory — Medium

`Middleware/RawBodyMiddleware.cs` buffers every request body on every route, then `MaxBodySizeMiddleware` compares the length. A 500 MB POST is fully read before it is rejected, so the cap protects the database and the forwarder but not memory. The v1.0 audit noted this and deferred it.

The fix is to honour `Content-Length` before reading, set Kestrel's `MaxRequestBodySize`, and scope the raw-body middleware to `/api/ingest`.

- Exposed by: Stage 4 drill 7 (128 MB container, large payloads).
- Fixed in: Stage 7.

### 7. Migrations run at startup, unguarded — Medium

`Program.cs`, the `MigrateAsync` block, with the documented single-replica assumption (v1.0 audit C8).

With two replicas starting together, both run the migration; EF Core does not take an advisory lock. In a rolling deploy the new version migrates the schema while the old version is still serving. Production practice is to run migrations as an explicit pipeline step (or a one-off task) before the new version is rolled out, and to have the application refuse to start with pending migrations rather than apply them.

- Exposed by: Stage 6 (rolling update on kind) and Stage 8 (expand-and-contract migration under load).
- Fixed in: Stage 8.
- Read first: expand-and-contract migrations; why "migrate on boot" and "more than one replica" are mutually exclusive.

### 8. No resilience on the database connection — Medium

Neither `UseNpgsql` nor `UseSqlite` is configured with `EnableRetryOnFailure`, and there is no connection-pool sizing, command timeout, or circuit breaking. A one-second Postgres blip (failover, restart) turns into a burst of 500s and, in the worker, an unhandled exception path.

- Exposed by: Stage 4 drill 1 (kill Postgres mid-load) and drill 2 (pool of 2).
- Fixed in: Stage 7.

### 9. Timestamps are stored as `long` ticks on Postgres too — Low, but it shapes Stage 5

`HookVaultDbContext.cs`, the `DateTimeOffset -> long` value converters, needed for SQLite ordering.

On Postgres this loses `timestamptz`, makes the data unreadable in SQL and Metabase without conversion, and makes range partitioning by day awkward. The v1.0 audit chose the common denominator on purpose. Once Postgres is the production database, a provider-specific mapping (`timestamptz` on Npgsql, ticks on SQLite) is worth the migration, and Stage 5 partitions on the real column.

- Fixed in: Stage 5, with an ADR.

### 10. Operational gaps that are not bugs

- **Logs are unstructured console output** with no correlation id. Stage 2 (structured JSON, trace id on every line).
- **No traces.** `v0.4-candidates.md` already lists OTLP. Stage 2.
- **Secrets missing at startup only warn** (`Program.cs`, the provider loop). Reasonable for a dev tool; for a service, decide per environment and record the decision in an ADR. Stage 8.
- **.NET 8 leaves support on 10 November 2026**, three weeks from this review. Upgrading to .NET 10 LTS is a real dependency-update exercise and belongs in Stage 8 (or earlier if a CVE forces it). Note EF Core is already 9.x on a net8.0 target, which is supported but worth aligning.
- **Retention's count-cap path** builds a potentially huge `IN (...)` list and deletes unbatched. Fine until Stage 5's volumes; batch it there.
- **Body search** over-fetches `limit × 4` and filters in memory (documented). Postgres can do this server-side; optional in Stage 5.
- **`.dockerignore` change on 18 Oct** (ignoring `ui/node_modules` and `ui/dist`) is correct: the image builds the UI itself and a Windows `node_modules` would break the Alpine stage. Keep it.

## What is already right and should be protected

Named here so the roadmap does not regress them: fixed-time HMAC comparison; secrets by environment variable name, never value; `IHttpClientFactory` with a clamped timeout; 4xx short-circuit on retries; the orphan sweep at startup; non-root container with a healthcheck; `[Authorize]` by default with an explicit, loud opt-out; three test layers with CI gates including `dotnet format`; Dependabot and CodeQL enabled.

## Order of work

The roadmap does not fix these first. Stages 1 to 4 deploy the system as it is, make it observable, and break it on purpose so that each finding above appears on a dashboard with a timestamp and an incident note. Stage 7 then fixes them with the measurements to prove the fix. That order is deliberate: a fix you have watched fail is one you can explain.
