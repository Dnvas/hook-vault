# HookVault hardening sprint — design

**Date:** 2026-05-15
**Status:** Design — pending user approval
**Author:** Brainstormed with the user via `superpowers:brainstorming`

## Goal

Close the correctness, security, and OSS-readiness gaps surfaced by the
2026-05-15 review of `main`. Ship in three reviewable PRs, validating each
before starting the next.

## Strategic decisions (locked in)

| Decision | Choice | Rationale |
|---|---|---|
| License | Switch AGPL-3.0 → **Apache-2.0** | Maximises adoption and portfolio reach; patent grant included. |
| Startup admin token | **1h, Dev-only** | Keeps friendly URL UX without leaking 30-day admin keys to log aggregators. |
| Forward path | **Keep synchronous** | Preserves "transparent pass-through" semantics; async forward would hide upstream errors from the provider. |
| Body storage | **Bytes (`byte[]` BLOB)** | Source of truth is the original payload. API/UI gets a UTF-8 best-effort projection. |
| PR shape | **Three PRs by review section** | Matches the user's intuition; each PR is independently reviewable; validation gate between PRs. |

## Architecture overview

The hardening preserves HookVault's shape — single Web API + background
worker + EF Core on SQLite/Postgres. The changes:

- **`WebhookEvent.Body` becomes `byte[]` (BLOB).** EF migration. The ingest
  controller stops UTF-8-decoding; `EventForwarder` stops re-encoding.
  `EventDetail` exposes a derived `bodyText` (UTF-8 best-effort, `null` on
  non-text), `bodyEncoding` (`"utf8"` | `"binary"`), and `bodySize` so the UI
  can render a hex/size view when bytes aren't valid UTF-8.
- **`WebhookEvent.Headers` becomes JSON-serialized
  `Dictionary<string, string[]>`.** Migration wraps existing string values in
  single-element arrays.
- **`EventNotifier` becomes a real pub-sub.** `Subscribe()` returns a
  per-client `Channel<EventNotification>`. `Unsubscribe(channel)` removes it.
  `Notify` fans out to every subscriber. Thread-safe via
  `ImmutableList<Channel<…>>` swap.
- **New `EventRetentionWorker` (`BackgroundService`).** Runs every 5 minutes,
  deletes oldest events past `HOOKVAULT_MAX_EVENTS` and older than
  `HOOKVAULT_RETENTION_DAYS`. Both optional.
- **EF Core migrations replace `EnsureCreated`.** New `Migrations/` folder;
  `Program.cs` calls `db.Database.Migrate()` on startup. First migration
  captures the current schema; second introduces bytes-body, array-headers,
  `BodyHash`, `ProviderEventId` columns.
- **New `IIngestSignatureScheme` abstraction.** Existing single-header HMAC
  becomes `SingleHeaderHmacScheme`. New `SvixHmacScheme` for
  `svix-id` / `svix-timestamp` / `svix-signature`. Resolved by a new
  `validation.scheme: "single-header" | "svix"` field; defaults to
  `"single-header"` for back-compat. `SignatureValidator` becomes a thin
  dispatcher.

## Data flow

```
Provider POST
   │
   ▼
RawBodyMiddleware  ─►  rawBytes captured (unchanged)
   │
   ▼
IngestController
  ├─ Resolve provider (now supports multi-segment via {**provider})
  ├─ Compute body hash (SHA-256 of rawBytes) for dedup
  ├─ Optional: look up existing event with same (provider, bodyHash,
  │   providerEventId header) within last 24h → return Accepted with
  │   existing.Id (no double-store, no double-forward)
  ├─ Resolve IIngestSignatureScheme from config.scheme
  ├─ scheme.Validate(rawBytes, headers, config)
  │     └─ optional MaxAgeSeconds check on extracted timestamp
  ├─ Redact sensitive headers (Authorization / Cookie / Proxy-Authorization)
  │   before persistence
  ├─ Persist event (bytes body, array headers, BodyHash, ProviderEventId)
  ├─ Notify fan-out
  └─ Forward synchronously (unchanged semantics, now bytes end-to-end)
```

## Component-level changes

| Area | Files touched | Notes |
|---|---|---|
| Schema/storage | `Domain/WebhookEvent.cs`, `Infrastructure/HookVaultDbContext.cs`, `Migrations/**` | Bytes body, array headers, `BodyHash`, `ProviderEventId` columns |
| Ingest path | `Controllers/IngestController.cs`, `Services/EventForwarder.cs`, `Middleware/RawBodyMiddleware.cs` | Bytes end-to-end; dedup; multi-segment routes; header redaction |
| SSE | `Services/EventNotifier.cs`, `Controllers/EventsController.cs:Stream` | Per-client channel; 15s heartbeat |
| Replay | `Services/ReplayWorker.cs`, `Program.cs` | Startup sweep: `Replaying` → `ForwardFailed` |
| Retention | `Services/EventRetentionWorker.cs` (new), `Program.cs` | Periodic cleanup, env-var-driven caps |
| Auth | `Program.cs` (token log), `Auth/JwtOptions.cs` (entropy check), `Controllers/EventsController.cs` (audit log on DELETE) | 1h Dev-only token; min 48-byte JWT secret |
| Redaction | `Controllers/IngestController.cs`, `Controllers/EventsController.cs`, `Services/SignatureValidator.cs` | Header allowlist; computed-sig redaction outside Dev |
| Validation | `Services/SignatureValidator.cs` → dispatcher; `Services/Schemes/SingleHeaderHmacScheme.cs`, `Services/Schemes/SvixHmacScheme.cs` (new); `Services/Schemes/IIngestSignatureScheme.cs` (new); `Configuration/ValidationConfig.cs` | Scheme indirection; replay-attack window |
| Search | `Controllers/EventsController.cs`, `Infrastructure/EventRepository.cs` | `?bodyContains=`, `?providerEventId=` filters |
| Tests | `tests/HookVault.Tests/IngestControllerTests.cs` (new), `EventNotifierTests.cs` (new), `EventRetentionWorkerTests.cs` (new), `SvixHmacSchemeTests.cs` (new), updates to existing | True end-to-end ingest tests + new component tests |
| Docker / CI | `.github/workflows/ci.yml`, `Dockerfile`, new `release.yml` | GHCR push on tag, `linux/amd64` only (multi-arch deferred) |
| Docs | `README.md`, `SECURITY.md` (new), `CHANGELOG.md` (new), `LICENSE` (replaced) | Apache-2.0, screenshots, comparison table |

## Error handling

- **Migration failure on startup** → log fatal with the migration name; exit
  non-zero. Don't run with a half-migrated DB.
- **Bytes-body decode for UI**: if the body contains a `U+FFFD` replacement
  character or contains a null byte in the first 1KB, treat as binary —
  return `bodyText: null`, `bodyEncoding: "binary"`. UI renders size + hex
  preview.
- **Header allowlist redaction**: store the header keys; replace values for
  `Authorization` / `Cookie` / `Proxy-Authorization` with the literal string
  `"[redacted]"` before persistence. The signature header itself is kept
  as-is (HMAC output is not a secret).
- **SSE heartbeat write error**: swallow exception, break out of the loop,
  remove the subscriber's channel. The client reconnects via `EventSource`'s
  built-in retry.
- **Retention worker**: log Warning on each batch deleted; no Error on
  partial failures — next tick retries.
- **Dedup collision (provider + body-hash match)**: return `Accepted` with
  the existing event id and a `duplicate: true` flag in the response. Do not
  re-forward.

## Testing strategy

- **New `IngestControllerTests`** — true integration via
  `HookVaultWebApplicationFactory`. Cases: valid HMAC, invalid HMAC, no
  validation config, dedup hit, multi-segment path
  (`/api/ingest/stripe/v2`), binary body forwarded byte-equal.
- **`EventNotifierTests`** — three subscribers, every notification delivered
  to every subscriber; one subscriber disconnects, others continue.
- **`EventRetentionWorkerTests`** — real SQLite, seed N+10 events with mixed
  `ReceivedAt`; assert N newest remain.
- **`SvixHmacSchemeTests`** — three Resend-shaped fixtures (valid, tampered
  body, tampered timestamp) + missing-header case.
- **Startup sweep test** — seed `Replaying` row, start app, assert it
  transitions to `ForwardFailed`.
- **All existing tests must still pass.** The bytes/headers migration is
  the riskiest — `ReplayWorkerTests`, `EventStreamTests`,
  `EventsControllerTests` must remain green after the schema change.
- **UI smoke via Playwright** — for PR 1, verify the SSE fan-out fix with
  two browser tabs open: post a webhook, both tabs see the new row.

## PR sequencing

### PR 1 — Correctness bugs

**Scope:**
1. SSE fan-out (`EventNotifier` per-subscriber channels) + 15s heartbeat
2. Bytes body schema migration + ingest/forward refactor
3. Headers as `Dictionary<string, string[]>` (with one-shot data migration)
4. Multi-segment provider paths (`{**provider}` route)
5. Startup `Replaying` → `ForwardFailed` sweep
6. EF Core migrations replace `EnsureCreated`
7. New `IngestControllerTests` for end-to-end coverage
8. `EventNotifierTests` for fan-out

**Parallelisable subagent tasks (after the schema baseline migration lands):**
- A: `EventNotifier` pub-sub + heartbeat + `EventNotifierTests`
- B: schema migration + `WebhookEvent`/`DbContext` changes (serialises with C, D)
- C: ingest/forward bytes refactor (depends on B)
- D: headers-as-array + redaction stub (depends on B)
- E: multi-segment routes + tests (independent)
- F: startup sweep in `Program.cs` (independent of A–E)

**Validation gate:** all tests green; `docker compose up` succeeds against a
fresh volume; a real Stripe-shaped curl ingests + forwards + appears in UI;
two-tab Playwright check confirms SSE fan-out.

### PR 2 — Security hardening

**Scope:**
1. 1h Dev-only startup token log line (production envs silent)
2. JWT secret minimum 48 bytes (entropy hint)
3. Sensitive-header redaction at storage (`Authorization` / `Cookie` /
   `Proxy-Authorization`)
4. `validationDetails.computedSignature` redaction toggle
   (`HOOKVAULT_REDACT_COMPUTED_SIGNATURE`), default true outside Development
5. `validation.maxAgeSeconds` optional replay-attack window
6. `HOOKVAULT_MAX_BODY_BYTES` ingest cap (returns `413 Payload Too Large`)
7. `[Authorize]` DELETE audit log already exists — verify it logs at
   `Warning` with caller subject

**Parallelisable subagent tasks:**
- A: token-log gating + secret-entropy bump (single file area)
- B: redaction features (headers + computed-sig) — both touch
  IngestController + EventsController
- C: `maxAgeSeconds` + scheme dispatcher wiring (sets stage for PR 3 Svix)
- D: body-size cap middleware

**Validation gate:** new security tests pass; manual check that Dev token
log is hidden when `ASPNETCORE_ENVIRONMENT=Production`; API response shows
`[redacted]` for sensitive headers; >max body returns 413.

### PR 3 — Architecture + OSS readiness

**Scope:**
1. `EventRetentionWorker` + `HOOKVAULT_MAX_EVENTS` /
   `HOOKVAULT_RETENTION_DAYS` env vars
2. Svix multi-header HMAC scheme + `validation.scheme` field +
   `hookvault.svix.json` example
3. Webhook dedup (provider-event-id header config + body-hash unique-ish
   index — actually a non-unique index for fast lookup; uniqueness enforced
   in code)
4. `?bodyContains=` and `?providerEventId=` filters on
   `GET /api/events`
5. CI: publish single-arch (`linux/amd64`) Docker image to
   `ghcr.io/<owner>/hookvault:{tag,latest}` on tag push
6. README screenshots + "Why HookVault vs ngrok / webhook.site / smee.io"
   comparison table + "do not expose to the internet" warning
7. `SECURITY.md` (report-to email + AGPL→Apache note), `CHANGELOG.md`
   (Keep-a-Changelog format starting at v0.1.0)
8. Apache-2.0 `LICENSE` replaces AGPL-3.0; update `.csproj`
   `PackageLicenseExpression` if present; remove AGPL header text from
   any source comments
9. GitHub `ISSUE_TEMPLATE/bug_report.md` and `feature_request.md`

**Parallelisable subagent tasks:**
- A: `EventRetentionWorker` + tests (independent service)
- B: Svix scheme + tests + `validation.scheme` field (sets up dispatcher
   from PR 2)
- C: dedup logic in `IngestController` + repo lookup (touches ingest path)
- D: search filters in `EventRepository` + `EventsController.List` (touches
   list endpoint)
- E: GHCR push CI workflow (independent)
- F: docs — README rewrite, SECURITY.md, CHANGELOG.md, issue templates
   (fully parallel, no code overlap)
- G: license switch (single commit, no dependencies)

**Validation gate:** all tests green; `git tag v0.2.0-rc.1 && git push
--tags` produces a working GHCR image; README renders cleanly on GitHub;
fresh-checkout build still works.

## Out of scope (deferred)

- HMAC-SHA1 / RSA / ed25519 algorithms
- "Capture only" no-forward mode
- In-UI body edit / fixture replay
- Prometheus metrics
- Postgres-specific migration tooling (current `db.Database.Migrate()` on
  startup is acceptable for v0.x)
- DCO/CLA enforcement bot
- Comparison-table screenshots themselves (text table is enough for now)

## Risks

- **Bytes-body migration data conversion.** Existing dev DBs have string
  bodies; the migration must convert `TEXT` → `BLOB`. SQLite's loose typing
  helps (text bytes are valid blob bytes). For Postgres the conversion needs
  an explicit `USING convert_to(body, 'UTF8')` clause in the migration.
- **EF migrations on Postgres** — first migration must `Sql()` rather than
  `EnsureCreated` to capture the existing schema. Plan: scaffold a
  `0001_Initial` migration from the current model, then a `0002_BytesBody`
  follow-up. Users with existing DBs should back up before upgrading;
  document this in CHANGELOG.
- **SSE fan-out concurrency**. `ImmutableList<Channel<…>>` swap is
  lock-free, but `Subscribe` / `Unsubscribe` under load needs to be
  benchmarked. For < 10 concurrent dev clients, this is a non-issue.
- **License change** — once Apache-2.0 ships, downstream forks under AGPL
  remain AGPL; only HookVault itself relicenses. The change requires
  preserving prior contributor authorship; since the repo has a single
  author this is straightforward.

## Open questions

None — all strategic decisions resolved during brainstorming.

## Next step

Invoke `superpowers:writing-plans` to author the implementation plan
covering all three PRs with detailed task breakdowns and subagent prompts.
