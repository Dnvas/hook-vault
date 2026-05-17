---
name: hookvault-codemap
description: Canonical file-by-file codemap of the HookVault codebase. Preload when an agent needs to navigate source files without re-discovering structure. Pairs with hookvault-spec (what the system does) and hookvault-conventions (how to write code in it).
---

# HookVault Codemap

Last sync: 2026-05-17, after v1.0 reliability PR.

Load this skill when you need to **navigate or modify** the codebase. It replaces "let me grep around to find X" with a direct file lookup. For *what* the system does, load `hookvault-spec`. For *how to write code* in it, load `hookvault-conventions`.

## Top-level layout

```
src/HookVault/             — the service
tests/HookVault.Tests/     — xUnit tests against the real factory + SQLite
docs/                      — design docs, audits, plans
plans/                     — per-PR implementation plans
.claude/                   — agent / skill / hook config (mostly gitignored)
Dockerfile                 — multi-stage alpine, non-root, HEALTHCHECK
docker-compose.yml         — SQLite default
docker-compose.postgres.yml — Postgres alternative
```

## src/HookVault/ — service code

### `Program.cs` — the bootstrap

Owns: DI registration, middleware pipeline, hosted services, startup orphan-sweep, auto-migrate, top-level try/catch (terminates at `return 1` before `app.Run()`).

Order to read: config load → DI registrations (~30 lines) → JWT setup → middleware pipeline (~lines 295-318) → `MapPrometheusScrapingEndpoint("/metrics")` → `MapControllers` → `MapFallbackToFile("index.html")`.

### `Auth/`
| File | Purpose | Key types |
|---|---|---|
| `JwtOptions.cs` | JWT config record. `FromConfiguration(IConfiguration)` for normal startup; `Ephemeral()` for `HOOKVAULT_NO_AUTH=true` mode (48-byte random key per process). | `JwtOptions` (sealed record) |
| `JwtTokenGenerator.cs` | Mints JWT tokens (used by the CLI). | `JwtTokenGenerator` (sealed) |

### `Cli/`
| File | Purpose |
|---|---|
| `GenerateTokenCommand.cs` | `hookvault generate-token` CLI subcommand. |

### `Configuration/`
| File | Purpose | Key types |
|---|---|---|
| `HookVaultOptions.cs` | Loads `hookvault.json`; **Singleton** in DI. `Load(ILogger)` static factory; `Validate()` enforces non-empty `name`/`path`/(`forwardUrl` unless `captureOnly`), unique paths, valid `algorithm`/`signatureHeader`/`secretEnvVar`. | `HookVaultOptions` (sealed) |
| `ProviderConfig.cs` | Per-provider record. | `ProviderConfig` (sealed record) |
| `ValidationConfig.cs` | HMAC validation config record. `Algorithm` ∈ {hmac-sha1, hmac-sha256, hmac-sha512}. | `ValidationConfig` (sealed record) |

### `Contracts/` — DTOs for the management API
| File | Returned by |
|---|---|
| `ApiError.cs` | All non-2xx responses. `(string Error, string? Code)`. |
| `DeleteResponse.cs` | `DELETE /api/events`. |
| `EventDetail.cs` | `GET /api/events/{id}`. |
| `EventSummary.cs` | Each item in `GET /api/events` list. |
| `ListEventsResponse.cs` | `GET /api/events` envelope (`items`, `total`, `limit`, `offset`). |
| `ReplayBulkResponse.cs` | `POST /api/events/replay-failed`. |
| `ReplayEnqueuedResponse.cs` | `POST /api/events/{id}/replay`. |

### `Controllers/`
| File | Route | Auth |
|---|---|---|
| `IngestController.cs` | `POST /api/ingest/{**provider}` | `[AllowAnonymous]` |
| `HealthController.cs` | `GET /api/health` | `[AllowAnonymous]` |
| `EventsController.cs` | `/api/events/*` (list/detail/replay/bulk-replay/stream/delete) | `[Authorize]` (skipped when `HOOKVAULT_NO_AUTH=true`) |

`IngestController.Ingest` is intentionally long (~140 lines, 8 concerns). Extraction to `IngestService` is deferred per v1.0 audit.

### `Domain/`
| File | Purpose | Key types |
|---|---|---|
| `WebhookEvent.cs` | Mutable entity persisted by EF. Public setters everywhere. | `WebhookEvent` |
| `EventStatus.cs` | Status enum stored as string. | `EventStatus` (Received, Forwarding, Forwarded, ForwardFailed, Replaying, ReplayFailed, Captured) |

### `Infrastructure/`
| File | Purpose | Lifetime |
|---|---|---|
| `HookVaultDbContext.cs` | EF DbContext. Provider switch (SQLite ↔ Npgsql) in `Program.cs` `DATABASE_URL` branch. `DateTimeOffset` stored as `long` ticks for SQLite parity. | Scoped |
| `HookVaultDbContextFactory.cs` | Design-time factory for `dotnet ef migrations`. | — |
| `EventRepository.cs` | Thin query organizer over `DbContext`. No interface (YAGNI). Async LINQ only. | Scoped |

### `Middleware/`
| File | Purpose | Order |
|---|---|---|
| `RawBodyMiddleware.cs` | Buffers request body into `HttpContext.Items["RawBody"]` so signature validation can re-read it. Runs on all routes (architecturally messy but functional). | 3rd |
| `MaxBodySizeMiddleware.cs` | Enforces `HOOKVAULT_MAX_BODY_BYTES`. Reads env per request (intentional for test isolation). Returns 413 + `ApiError`. | 4th |

### `Migrations/`
4 numbered migrations (`00000000000000_Initial.cs` through `00000000000003_LastReplayEditedBody.cs`). Migration ID format is **zero-padded sequential**, not EF default timestamps. Migration 1 has provider-conditional Postgres path (added in v1.0 audit's Bucket 3).

### `Observability/`
| File | Purpose | Key types |
|---|---|---|
| `HookVaultMeter.cs` | All Prometheus counters/histograms. Free-form string labels. | `HookVaultMeter` (Singleton). Instruments: `ReplaysTotal` (outcome ∈ {success, retry, exhausted}), `RetentionDeletedTotal`, `SignatureValidationTotal`, etc. |

### `Services/`
| File | Purpose | Lifetime |
|---|---|---|
| `SignatureValidator.cs` | Transient dispatcher → resolves scheme by `Validation.Scheme` name (default `single-header`). | Transient |
| `Schemes/IIngestSignatureScheme.cs` | Pluggable scheme interface. | — |
| `Schemes/SingleHeaderHmacScheme.cs` | hmac-sha1/sha256/sha512 in hex/base64/base64url. Constant-time compare via `CryptographicOperations.FixedTimeEquals`. | Transient |
| `Schemes/SvixHmacScheme.cs` | Multi-key, multi-signature Svix format (Resend, Clerk, PostHog). Timestamp expiry check. | Transient |
| `SignatureValidationResult.cs` | Debug record persisted as JSON into `WebhookEvent.ValidationDetails`. | record |
| `EventForwarder.cs` | `ForwardAsync` (ingest path, touches DB); `SendAsync` (pure HTTP, used by ReplayWorker). Uses named `"forwarder"` HttpClient with `HOOKVAULT_FORWARD_TIMEOUT_SECONDS` (default 30s). | Scoped |
| `ForwardResult.cs` | `(bool Success, int? StatusCode, string? Error)`. | internal record |
| `ReplayJob.cs` | `(Guid EventId, byte[]? BodyOverride)`. | public record |
| `ReplayQueue.cs` | `Channel.CreateBounded<ReplayJob>(10_000, FullMode=Wait)`. `EnqueueAsync` + `EnqueueWithBodyAsync` + `Reader`. | Singleton |
| `ReplayWorker.cs` | `BackgroundService` draining the channel. 4 attempts max (1 + 3 retries with `[1s, 2s, 4s]`). `IsRetriable(int? statusCode)`: 4xx terminal except 408/425/429. Final DB write uses `CancellationToken.None`. | Hosted (Singleton) |
| `EventNotifier.cs` | Lock-free CAS over `ImmutableList<EventSubscription>`. Per-subscriber `Channel<EventNotification>`. SSE fan-out. | Singleton |
| `EventRetentionWorker.cs` | Periodic sweep enforcing `HOOKVAULT_MAX_EVENTS` + `HOOKVAULT_RETENTION_DAYS`. Bypasses `EventRepository`. Uses `IServiceScopeFactory` per sweep. | Hosted (Singleton) |
| `RetentionStats.cs` | Shared mutable state (last sweep timestamp + deletion count) for `/api/health`. `lock`-protected. | Singleton |

## Test conventions reference

- All env-var-mutating tests use `[Collection("EnvVarMutation")]` — definition lives in `tests/HookVault.Tests/TestCollections.cs` (with `DisableParallelization = true`).
- Integration tests boot via `HookVaultWebApplicationFactory` with a real SQLite DB.
- Metric assertions use `System.Diagnostics.Metrics.MeterListener` (more deterministic than scraping `/metrics`).
- `JwtAuthTests`, `NoAuthOptOutTests`, `JwtOptionsTests` are the auth path.
- `ReplayWorkerTests`, `ForwarderTimeoutTests` cover the replay/forward reliability surface.

## Most-touched files by recent PRs (v1.0 work)

`Program.cs`, `Services/ReplayWorker.cs`, `Services/ReplayQueue.cs`, `Configuration/HookVaultOptions.cs`, `Controllers/IngestController.cs`, `Middleware/MaxBodySizeMiddleware.cs`, `Auth/JwtOptions.cs`.

## What is NOT here

- The React UI (separate `ui/` workspace, gitignored under most contexts; touched only by the React-UI CI job)
- Generated migration `*.Designer.cs` files (don't edit by hand)
- The `bin/` and `obj/` outputs
