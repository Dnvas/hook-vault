---
name: hookvault-spec
description: Canonical product specification for HookVault. Preload before planning, implementing, or reviewing any feature. Defines what HookVault is, what it does, the config schema, entity fields, replay behaviour, management API, Docker setup, and the build-order roadmap.
user-invocable: false
---

# HookVault — Product Specification

This is the canonical source of truth for HookVault's product requirements.
Read this end-to-end before planning or implementing any feature.

## What HookVault is

A .NET 8 open-source developer tool. Generic, provider-agnostic webhook
capture / inspection / replay engine for local development environments.

Runs as a single Docker container alongside any project's dev stack and acts
as a transparent pass-through proxy that records every webhook event for
debugging and replay.

**Distribution target:** any developer working with webhooks (Stripe, GitHub,
Twilio, Resend, anything) should be able to add HookVault to their
docker-compose, drop in a config file, and have full capture + replay working
in under a minute.

## What HookVault does

1. Receives incoming webhooks from any provider via dynamically registered routes.
2. Optionally validates HMAC signatures based on provider-specific configuration
   (no hardcoded provider logic — purely config-driven).
3. Stores the full event (headers, body, signature validation result with debug
   info, timestamp, provider name).
4. Forwards the event to the configured local destination URL (transparent
   pass-through).
5. Provides a JWT-protected management API to browse, filter, inspect, and
   replay captured events.
6. Replays events asynchronously via a background worker with retry logic and
   exponential backoff.
7. Optionally exposes a Prometheus scraping endpoint at `/metrics`
   (unauthenticated; metrics are operational data).
8. Supports a per-provider "capture-only" mode (`captureOnly: true`)
   that persists events without forwarding, with the resting state
   `EventStatus.Captured`.
9. Supports body-edit replay: tweak the captured payload in the UI
   and replay with the edited body without mutating the stored event.

## Core design principles

- **Provider-agnostic** — zero knowledge of Stripe, Resend, or any service.
  All provider behaviour comes from the user's config file.
- **Zero-friction setup** — single Docker container, SQLite by default
  (no external DB needed). PostgreSQL supported as an optional upgrade.
- **Config-driven** — each project supplies its own `hookvault.json` defining
  providers, validation schemes, and forwarding URLs.
- **Transparent pass-through** — to the target app, the webhook looks identical
  to receiving it directly. HookVault just records it along the way.

## Provider configuration schema

Each project supplies a `hookvault.json` mounted into the container. Read on
startup; dynamically registers ingest routes.

```json
{
  "providers": [
    {
      "name": "stripe",
      "path": "/stripe",
      "forwardUrl": "http://host.docker.internal:54321/functions/v1/stripe-webhook",
      "captureOnly": false,
      "validation": {
        "algorithm": "hmac-sha256",
        "secretEnvVar": "STRIPE_WEBHOOK_SECRET",
        "signatureHeader": "Stripe-Signature",
        "payloadFormat": "{timestamp}.{body}",
        "signatureEncoding": "hex",
        "signaturePattern": "v1={signature}",
        "timestampPattern": "t={timestamp}"
      }
    },
    {
      "name": "resend",
      "path": "/resend",
      "forwardUrl": "http://host.docker.internal:54321/functions/v1/resend-webhook",
      "validation": null
    }
  ]
}
```

Field descriptions:

- `name` — human-readable provider label, used for filtering and display
- `path` — ingest route registered dynamically (POST /api/ingest/{path})
- `forwardUrl` — where to proxy the event in the local dev environment
- `captureOnly` — boolean (default `false`). When `true`, the event is
  persisted with status `Captured` and **not** forwarded. Users can
  trigger replays manually whenever the downstream is ready.
- `validation` — nullable; when null, skip validation and just capture + forward
- `validation.algorithm` — HMAC algorithm (`hmac-sha1`, `hmac-sha256` (default),
  `hmac-sha512`). SHA-1 is for legacy schemes (GitHub `X-Hub-Signature`,
  Twitter, some Eventbrite providers).
- `validation.secretEnvVar` — env var name holding the secret
  (**not** the secret itself — secrets never go in the config file)
- `validation.signatureHeader` — request header containing the signature
- `validation.payloadFormat` — how the signed string is constructed
  (`{body}` = raw request body, `{timestamp}` = extracted timestamp)
- `validation.signatureEncoding` — how the computed HMAC digest is encoded:
  `"hex"` (lowercase hex, default when omitted or null), `"base64"` (standard
  Base64 with padding), `"base64url"` (URL-safe Base64, no padding). Unknown
  values return a validation error. Hex comparison is case-insensitive;
  base64/base64url comparisons are case-sensitive.
- `validation.signaturePattern` — how to extract the signature from the header
  value (e.g. Stripe puts it after `v1=`). Omit or set null if the entire
  header value is the digest.
- `validation.timestampPattern` — how to extract the timestamp from the header
  value (nullable, for providers that include a timestamp)

## Tech stack

- .NET 8
- ASP.NET Core Web API
- Entity Framework Core (SQLite default, PostgreSQL optional)
- `System.Threading.Channels` for the internal replay queue (`Channel<T>`,
  producer/consumer pattern)
- JWT authentication on the management API endpoints
- Swagger / OpenAPI via Swashbuckle
- Docker (single-container, multi-stage build)
- GitHub Actions CI (build, test, format check)
- xUnit for unit tests

## Database

- SQLite default at `/data/hookvault.db` (configurable via `SQLITE_PATH`,
  mountable as a Docker volume).
- PostgreSQL when `DATABASE_URL` env var is set with a connection string.
- Provider selection happens in `Program.cs` based on env presence; no
  code changes required between modes.

## Dynamic route registration

On startup, read the provider config file and register
`POST /api/ingest/{providerPath}` for each provider. Use a single
`IngestController` that resolves the provider config from the route parameter.
If a request hits a path with no configured provider, return 404.

## Generic HMAC signature validation

The `SignatureValidator` must:

- Read the signature from the header specified in config (`signatureHeader`)
- Extract the actual signature value using `signaturePattern`
- Extract the timestamp if `timestampPattern` is specified
- Construct the payload string using `payloadFormat`
  (replacing `{body}` with raw body and `{timestamp}` with extracted timestamp)
- Compute HMAC using the configured algorithm and the secret from the env var
- Encode the computed digest per `signatureEncoding`: `"hex"` (default/null),
  `"base64"`, or `"base64url"`. Unknown values throw `NotSupportedException`,
  caught by the outer try/catch and returned as a validation error.
- Compare using constant-time comparison
  (`CryptographicOperations.FixedTimeEquals`)
- Return a detailed result: `isValid`, `computedSignature`, `receivedSignature`,
  `payloadUsed`, `extractedTimestamp`, `algorithmUsed` — so when validation
  fails, the developer can see EXACTLY what went wrong

## Event forwarding

After capturing, immediately forward to the provider's `forwardUrl` using
`HttpClient` (from `IHttpClientFactory`). Preserve the original request body.
Forward original headers but add:

- `X-HookVault-Event-Id`
- `X-HookVault-Provider`

Store the forward result (status code, success/failure, error message) on the
event record. If forwarding fails, mark as `ForwardFailed` — **don't auto-retry
on initial forward**. Retry is the replay system's job.

## Replay system

**Internal machinery (Phase 2 — DONE):**
- `ReplayQueue` — singleton `Channel<Guid>` wrapper. Exposes `EnqueueAsync(Guid)` and
  `Reader`. `Channel.CreateUnbounded` with `SingleReader = true`.
- `ReplayWorker` — `BackgroundService` that drains the channel via `ReadAllAsync`.
  Per item: creates a DI scope (`IServiceScopeFactory`), resolves `EventRepository`
  and `EventForwarder`, sets status to `Replaying`, then calls `EventForwarder.SendAsync`
  (the pure HTTP method — no DB touches).
  - 4 total attempts: 1 initial + 3 retries with delays `[1s, 2s, 4s]`.
  - On success: sets `Forwarded`, records `ForwardedAt` + `ForwardStatusCode`.
  - On exhaustion: sets `ReplayFailed`. Final DB write uses `CancellationToken.None`
    to prevent orphaned `Replaying` rows on graceful shutdown.
  - Increments `ReplayCount` once per replay trigger (not per HTTP attempt).
- `EventForwarder.SendAsync` — `internal` pure HTTP method extracted from `ForwardAsync`.
  Returns `ForwardResult(bool Success, int? StatusCode, string? Error)`. No DB touches.
  Shared by both the ingest path (`ForwardAsync`) and the replay path (`ReplayWorker`).

**HTTP endpoints (Phase 3 — DONE):**
- `POST /api/events/{id}/replay` — enqueues a single event into `ReplayQueue`.
- `POST /api/events/replay-failed` — bulk-enqueues all `ForwardFailed` events.
Both are JWT-protected via `[Authorize]` on `EventsController`.

## Health endpoint (public, no auth)

`GET /api/health` returns:

```json
{
  "status": "healthy",
  "version": "1.0.0",
  "providers": ["stripe", "resend"],
  "database": "sqlite",
  "eventCount": 142,
  "oldestEvent": "2026-05-10T08:30:00Z",
  "retention": {
    "maxEvents": 10000,
    "retentionDays": 7,
    "lastSweepAt": "2026-05-16T13:00:00Z",
    "lastSweepDeleted": 3
  }
}
```

`retention` is `null` when no caps are configured.

## Metrics endpoint (public, no auth)

`GET /metrics` returns Prometheus-format text with both built-in
AspNetCore HTTP metrics and HookVault's custom instruments:

- `hookvault_events_total{provider, status}` — counter
- `hookvault_replays_total{outcome}` — counter
- `hookvault_forward_duration_seconds{provider, outcome}` — histogram
- `hookvault_retention_deleted_total{reason}` — counter
- `hookvault_signature_validation_total{provider, result}` — counter

Unauthenticated by design — metrics are operational data, not secrets.
The threat model assumes HookVault is not exposed to the public internet.

## Management API — JWT protected

- `GET /api/events` — list events; filterable by provider, status, dateFrom,
  dateTo. Paginated with limit/offset.
- `GET /api/events/{id}` — full event detail including headers, body,
  validation debug info, forward result.
- `POST /api/events/{id}/replay` — enqueue single event for replay.
- `POST /api/events/replay-failed` — bulk replay all failed events.
- `DELETE /api/events` — clear all captured events
  (optional `?provider=stripe` filter).

### Auth opt-out

`HOOKVAULT_NO_AUTH=true` disables JWT enforcement on the management API.
A loud startup warning is logged. Intended for single-user local dev
where the listener is bound to `127.0.0.1`. Do not enable in any
environment where the listener is reachable from outside the host.

## WebhookEvent entity fields

| Field                | Type                | Nullable | Notes                          |
|----------------------|---------------------|----------|--------------------------------|
| Id                   | Guid                | no       | PK                             |
| Provider             | string              | no       |                                |
| Path                 | string              | no       |                                |
| Headers              | string (JSON)       | no       | serialised dict                |
| Body                 | string              | no       |                                |
| ReceivedAt           | DateTimeOffset      | no       |                                |
| SignatureHeader      | string?             | yes      |                                |
| SignatureValid       | bool?               | yes      | null = no validation configured|
| ValidationDetails    | string? (JSON)      | yes      | debug info                     |
| ForwardUrl           | string              | no       |                                |
| ForwardedAt          | DateTimeOffset?     | yes      |                                |
| ForwardStatusCode    | int?                | yes      |                                |
| ForwardError         | string?             | yes      |                                |
| Status               | EventStatus enum    | no       | stored as string in DB         |
| ReplayCount          | int                 | no       | default 0                      |
| LastReplayAt         | DateTimeOffset?     | yes      |                                |
| LastError            | string?             | yes      |                                |

`EventStatus`: `Received`, `Forwarding`, `Forwarded`, `ForwardFailed`,
`Replaying`, `ReplayFailed`, `Captured`.

## Example provider configs (`/examples/`)

Pure JSON with `//` comments (the config loader sets `JsonCommentHandling.Skip`
and `AllowTrailingCommas = true`). Each file is a complete, self-contained
single-provider config with explanatory comments.

- `hookvault.stripe.json` — HMAC-SHA256 hex, `{timestamp}.{body}` payload, `v1={signature}` + `t={timestamp}` patterns
- `hookvault.github.json` — HMAC-SHA256 hex, body-only, `sha256={signature}` pattern
- `hookvault.shopify.json` — HMAC-SHA256 base64 (`signatureEncoding: "base64"`), body-only, no prefix
- `hookvault.resend.json` — `"validation": null`; Svix multi-header scheme cannot be expressed in the single-header schema
- `hookvault.generic-hmac.json` — annotated template covering all algorithms, encodings, and pattern options

## Docker setup

- Multi-stage Dockerfile optimised for image size (SDK for build, ASP.NET
  runtime for final).
- Expose port 8080.
- Default SQLite DB at `/data/hookvault.db`.
- Mount config: `-v ./hookvault.json:/app/config/hookvault.json`.
- Mount data volume for SQLite persistence: `-v hookvault-data:/data`.
- `docker-compose.yml` — standalone (single container, SQLite).
- `docker-compose.postgres.yml` — optional PostgreSQL companion.

## GitHub Actions CI

- Build the solution
- Run all xUnit tests
- Check formatting (`dotnet format --verify-no-changes`)
- Build the Docker image (verify it builds, don't push)
- CodeQL security analysis on push/PR + weekly cron
- Dependabot weekly updates for NuGet + actions

## README structure (when written)

- One-line description: "Capture, inspect, and replay webhooks during local
  development"
- "Quick Start" — 3 steps to get running (create config, docker run, point
  your webhook URL at it)
- "How it works" — simple flow diagram (text-based is fine)
- "Configuration" — `hookvault.json` format
- "Provider examples" — pointing to `/examples`
- "Management API" — endpoint reference
- "Development" — how to build and run from source
- License: AGPL-3.0

## Build order (phased)

### ✅ Phase 1 — Foundation (DONE)

1. Scaffold the solution and project structure (`.sln`, `src/`, `tests/`)
2. Domain models + EF Core DbContext (SQLite default, Npgsql optional)
3. Configuration loader for `hookvault.json`
4. Raw body capture middleware
5. Generic `SignatureValidator` with detailed debug output
6. `IngestController` with dynamic provider resolution
7. `EventForwarder` service
8. Public health endpoint
9. xUnit tests for signature validation
10. `.gitignore`, `.editorconfig`, CI (build/test/format/CodeQL), Dependabot

### ✅ Phase 2 — Replay (DONE)

- `ReplayQueue` wrapping `Channel<Guid>` (singleton)
- `ReplayWorker` `BackgroundService` with 4 attempts (1 + 3 retries), exponential backoff
- `EventForwarder.SendAsync` extracted as `internal` pure-HTTP method shared by ingest + replay
- `ForwardResult` internal record in `src/HookVault/Services/ForwardResult.cs`
- Integration tests in `tests/HookVault.Tests/ReplayWorkerTests.cs` (real SQLite, real HTTP handler)

### ✅ Phase 3 — Management API + Auth (DONE)

- `EventsController` with list / detail / replay / bulk-replay / delete
- JWT bearer authentication (`Microsoft.AspNetCore.Authentication.JwtBearer`, HS256, `ClockSkew = 30s`)
- `JwtOptions` sealed record + `JwtTokenGenerator` static helper (`src/HookVault/Auth/`)
- `GenerateTokenCommand` CLI subcommand (`generate-token --subject ci --expires 1h`)
- `ApiError` unified error contract; `InvalidModelStateResponseFactory` override in `Program.cs`
- Settings: `HOOKVAULT_JWT_SECRET`, `HOOKVAULT_JWT_ISSUER`, `HOOKVAULT_JWT_AUDIENCE` env vars
- 49 xUnit tests (integration + unit), all green (56 after Phase 4)

### ✅ Phase 4 — Docker + Examples (DONE)

- `signatureEncoding` field added to `ValidationConfig`: `"hex"` (default/null) | `"base64"` | `"base64url"`. Unknown values throw `NotSupportedException` (caught → validation error).
- Multi-stage Dockerfile: `sdk:8.0-alpine` build stage → `aspnet:8.0-alpine` final stage, non-root `app` user, `/data` for SQLite persistence
- `.dockerignore` excluding build artifacts, secrets (`.env`, `hookvault.json`), and VCS metadata
- `docker-compose.yml` — SQLite standalone (port `7777:8080`, named volume, `:?` required secrets, `:-default` optional vars)
- `docker-compose.postgres.yml` — PostgreSQL standalone with `postgres:16-alpine`, healthcheck (`pg_isready`), `depends_on: service_healthy`
- `examples/` — 5 provider example configs: Stripe, GitHub, Shopify, Resend, generic-hmac template
- `hookvault.example.json` at repo root — annotated multi-provider starter with quick-start guide
- CI `docker-build` job: parallel, `docker/build-push-action@v7`, `push: false`, GHA layer cache, `permissions: contents: read`
- 56 xUnit tests total (7 new tests for signatureEncoding: hex default, base64 valid/invalid, base64url valid, unknown encoding)

### ✅ Phase 5 — Docs (DONE)

- README with the structure above
- LICENSE (AGPL-3.0)
- CONTRIBUTING.md
- `.gitattributes` enforcing LF line endings

### ✅ Phase 6 — React UI (DONE)

Full spec: `docs/superpowers/specs/2026-05-15-phase6-react-ui-design.md`

**What was built:**
- Layout: split pane — event list left (288 px), event detail right; no navigation
- Theme: dark slate (`slate-900` bg) with indigo accents
- Detail panel: scrollable sections (Body → Headers → Validation → Forward), no tabs
- Auth: startup URL with auto-generated 30-day JWT token (`?token=` query param);
  `sessionStorage` fallback with `<TokenGate />` paste input; token stripped from
  address bar via `history.replaceState` on mount

**Backend additions (all landed):**
- `EventNotifier` singleton — `Channel<EventNotification>` for SSE fanout
- `GET /api/events/stream` — SSE endpoint; JWT accepted via `?token=` query param
  (EventSource cannot set Authorization headers)
- `Program.cs`: startup log prints full UI URL with freshly minted 30-day token
- `app.UseStaticFiles()` + `app.MapFallbackToFile("index.html")` for SPA hosting
- Dockerfile: `node:20-alpine` build stage produces `ui/dist/` → copied to `wwwroot/`
- CI: Node build + lint job added

**React stack:** Vite 6 + React 18 + TypeScript + TanStack Query v5 + Tailwind CSS v3

**Repository layout (`ui/`):**
```
ui/src/
├── api/client.ts          — fetch wrapper, injects Bearer token, handles 401
├── hooks/
│   ├── useEvents.ts       — list query + useInvalidateEvents
│   ├── useEvent.ts        — detail query + useInvalidateEvent
│   └── useEventStream.ts  — EventSource lifecycle
├── components/
│   ├── App.tsx / main.tsx
│   ├── TokenGate.tsx
│   ├── EventList.tsx      — provider + status filter pills, SSE subscription
│   ├── EventRow.tsx
│   ├── EventDetail.tsx    — panel header, Replay button
│   ├── BodySection.tsx
│   ├── HeadersSection.tsx
│   ├── ValidationSection.tsx
│   └── ForwardSection.tsx
└── types.ts               — single source of truth for API shapes
```

**Critical field name facts (traps for future agents):**
- `GET /api/events` returns `{ items: EventSummary[], total, limit, offset }`.
  The C# record property is `Items`; ASP.NET Core serializes it as `items`.
  The TypeScript type in `types.ts` uses `items` — do not rename to `events`.
- `validationDetails` is stored as raw JSON and returned as a `JsonElement?`
  inside the event detail response. It uses **camelCase** keys (`isValid`,
  `algorithmUsed`, `computedSignature`, `receivedSignature`, `payloadUsed`,
  `extractedTimestamp`, `error`) — the IngestController serializes with
  `JsonNamingPolicy.CamelCase`. The TypeScript `ValidationDetails` interface
  must use camelCase to match.

**Hook stability rule (learned from Phase 6 E2E):**
- `useInvalidateEvents()` returns a `useCallback` with `[qc]` dep so the
  function reference is stable. Without this, `useEventStream`'s effect
  dep changes every render → opens/closes EventSource on every render.
- `useInvalidateEvents` invalidates **both** `['events']` (list) and
  `['event']` (all detail views). Without the second key, the detail panel
  does not refresh after the replay worker transitions a status.

### ✅ Phase 7 — Hardening sprint (DONE, v0.2.0)

Three merged PRs closing the post-Phase-6 review gaps:
- PR #17 — correctness bugs (SSE fan-out, bytes body, EF migrations, …)
- PR #18 — security hardening (JWT entropy, body cap, header redaction, …)
- PR #19 — architecture + OSS readiness (retention worker, Svix scheme, dedup, GHCR, …)

### ✅ Phase 8 — Polish + ergonomics + distribution (DONE, v0.3.0)

Three merged PRs:
- PR v0.3-1 — polish (auth opt-out, search pagination, retention surfacing)
- PR v0.3-2 — ergonomics (capture-only mode, in-UI body edit, hmac-sha1)
- PR v0.3-3 — distribution (Prometheus /metrics, multi-arch Docker, agent files committed)
