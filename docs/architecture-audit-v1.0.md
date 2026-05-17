# HookVault — Architecture Audit (v1.0-readiness)

**Date:** 2026-05-17
**Frame:** What's needed to ship HookVault as a polished self-hosted open-source webhook capture/replay tool. Not SaaS, not v∞ — credible v1.0.
**Method:** Four parallel layer audits (HTTP, Services, Data, Configuration+Bootstrap) followed by three sequential architect deep-dives (first-run robustness, forward/replay reliability, Postgres parity).
**Cross-references:** [`hookvault-spec`](../.claude/skills/hookvault-spec/SKILL.md), [`hookvault-conventions`](../.claude/skills/hookvault-conventions/SKILL.md), [`pr-review-checklist`](../.claude/skills/pr-review-checklist/SKILL.md), [`v0.4-candidates.md`](./v0.4-candidates.md).

---

## Executive summary

HookVault is structurally sound. The spec is implemented faithfully, conventions are followed consistently (file-scoped namespaces, primary-ctor DI, `IHttpClientFactory`, `CryptographicOperations.FixedTimeEquals`, async-only EF), and the architectural seams (signature-scheme pluggability, capture-only mode, body-edit replay, retention worker) are clean.

The gap between "v0.3 today" and "credible v1.0" is **polish, not redesign**. Three buckets of work:

1. **Cheap fixes** (~100 LOC + docs) — error-contract consistency, first-run UX, missing healthcheck, doc/spec drift.
2. **Reliability bundle** (~30 LOC + tests) — HttpClient timeout, 4xx short-circuit, bounded channel, retry metric.
3. **Postgres parity** (~100 LOC + CI) — fix the destructive migration, add a CI smoke step, document the single-replica assumption.

**Total: ~230 LOC production + ~80 LOC tests + targeted docs.** Splits cleanly into three independent PRs.

---

## Strategic calls made during this audit

| # | Decision | Implication |
|---|---|---|
| **S1** | **`HOOKVAULT_NO_AUTH=true` skips secret-check and generates an ephemeral in-memory secret.** | First-run friction floor drops to "set one env var, run, done." JWT scheme still registers cleanly. |
| **S2** | **Postgres is first-class at the contract level, common-denominator at the optimization level.** | Migration 1 must work on Postgres without data loss. Native-Postgres optimizations (server-side body search, `timestamptz`) stay deferred. |
| **S3** | **Postgres CI coverage = manual `docker-compose.postgres.yml` smoke job.** | Half-day to set up. Testcontainers full matrix deferred to v1.x. |
| **S4** | **Spec retry contract `[1s, 2s, 4s] × 3` is preserved.** | Reliability bundle refines *eligibility* (4xx skipped), not *timing*. Additive change to spec. |
| **S5** | **Single-replica deployment is the documented v1.0 contract.** | No advisory locks, no multi-pod orchestration. Multi-replica is v1.x+ if ever. |

---

## Findings by layer

### HTTP surface (`Controllers/`, `Middleware/`)

**Solid:** Three controllers cleanly scoped. `IngestController` is `[AllowAnonymous]`; `EventsController` is `[Authorize]`; `HealthController` is `[AllowAnonymous]`. Spec alignment is good for routes, status codes, pagination envelope, capture-only behaviour, dedup, body-edit replay, SSE fan-out.

**Gaps:**
- `IngestController.cs:43` returns `new { error = ... }` (anonymous type); `MaxBodySizeMiddleware.cs:25` same. All other errors use `ApiError`. → **C5**.
- `IngestController.Ingest` orchestrates 8 concerns in 140 lines (provider lookup, dedup, header redaction, signature validation, persistence, capture-only branching, forwarding, SSE/metrics). YAGNI-defensible for v1.0 but flagged for v1.x.
- `EventsController.ReplayFailed` (line 102–104) does an in-memory `.Where(...).ToList()` post-filter after DB query. Push status filter into repository. Flagged but defer to v1.x (no current user pain at expected scale).
- `RawBodyMiddleware` runs on every route including `/`, `/metrics`. Architecturally messy but functionally fine. Defer.
- `MaxBodySizeMiddleware` runs *after* `RawBodyMiddleware` — the cap doesn't actually protect memory, only persistence/forwarding. Defer (would require restructuring the buffer read).
- `DELETE /api/events?confirm=true` guard exists but is undocumented anywhere. → **C7**.

### Services (`Services/`)

**Solid:** Lifetimes correct (Transient schemes, Scoped forwarder, Singleton queue/notifier/meter, hosted workers). `IHttpClientFactory` used consistently. `FixedTimeEquals` in both signature schemes. Cancellation propagation correct. Async-only. The single intentional `CancellationToken.None` at `ReplayWorker.cs:70` (final DB write on shutdown) is the right call.

The `Channel<ReplayJob>` worker pipeline is well-designed for its scope. `SingleHeaderHmacScheme` and `SvixHmacScheme` are genuinely extensible — adding a third scheme is purely additive.

**Gaps:**
- No timeout on `"forwarder"` HttpClient (`Program.cs:70`). Default 100s × 4 attempts = ~7 minutes head-of-line blocking per dead target. **Highest-impact bug in the audit.** → **R1**.
- No 4xx vs 5xx distinction in `ReplayWorker` — retries on any non-success. `ForwardResult` already carries `StatusCode`. → **R2**.
- `Channel.CreateUnbounded<ReplayJob>()` (`ReplayQueue.cs:7`). Bulk replay of 100k events fills memory. → **R3**.
- `ReplayWorker` mutates `WebhookEvent` directly across the forward call (status, timestamps, error). Tight coupling to `EventForwarder`. Defer to v1.x.
- No retry-attempt metric — only `success`/`exhausted` outcomes recorded. → **R4**.
- `EventRetentionWorker.RunOnceAsync` uses two queries + in-memory IN-list for count-cap delete. Acceptable at expected scale. Defer.

### Data (`Infrastructure/`, `Domain/`, migrations)

**Solid:** Entity matches spec (with documented additions `LastReplayWithEditedBody`, `BodyHash`, `ProviderEventId`). All seven `EventStatus` values present. Async LINQ everywhere. Indexes on filter columns (`Provider`, `Status`, `ReceivedAt`, `BodyHash`, `ProviderEventId`). Repository is a thin organizer over `DbContext` per YAGNI; no `IEventRepository` interface (intentional). Migrations are reversible in principle.

**Gaps:**
- **Migration 1 (`BytesBodyAndArrayHeaders.cs:13-17`) throws `NotSupportedException` on Postgres.** Any pre-migration Postgres user is forced to drop their database. Hostile. → **P1**.
- No composite indexes for `(Provider, ReceivedAt DESC)` or `(Status, ReceivedAt)`. Defer — no user pain at expected scale.
- Auto-migrate on every startup (`Program.cs:221`) without guard or advisory lock. Fine for single-replica; documented assumption. → **C8**.
- Body search is a `limit × 4` client-side post-filter with `totalApproximate=true` returned. Documented limitation. Defer.
- `DateTimeOffset` stored as `long` ticks via converters — SQLite necessity; common-denominator constraint. Keep.
- Migration IDs use zero-padded sequential format (non-EF-default). Internally consistent. → document in CONTRIBUTING.

### Configuration + Bootstrap (`Configuration/`, `Program.cs`, Docker)

**Solid:** Config schema matches spec exactly. `secretEnvVar` correctly stores names, not values. `HOOKVAULT_JWT_SECRET:?` syntax in compose file fails fast with a clear message. Multi-stage Alpine Dockerfile, non-root `app` user. Provider switch (SQLite ↔ Npgsql) is clean.

**Gaps:**
- **`HOOKVAULT_JWT_SECRET` validated *before* `NO_AUTH` is read** (`Auth/JwtOptions.cs:14`). Zero-friction mode requires generating a 48-byte secret anyway. → **C3** (per S1).
- Malformed `hookvault.json` throws raw `JsonException` with no config-path context. → **C2**.
- `forwardUrl` validated unconditionally — even for `captureOnly:true` providers. → **C1**.
- No `HEALTHCHECK` in Dockerfile or compose blocks for the `hookvault` service. → **C4**.
- `ValidationConfig.algorithm` / `signatureHeader` / `secretEnvVar` not validated at startup; missing secret env vars don't warn. → **C6**.
- `BackfillMigrationHistoryAsync` hardcodes EF product version `'9.0.16'`. Cosmetic, legacy-DB-only path. Defer.
- Console logger only — no JSON-structured option for aggregators. Defer (v1.x observability work).
- Single `/api/health` — no readiness/liveness split. Out of scope for v1.0 (Kubernetes-only concern).

---

## v1.0 plan

### Bucket 1: Cheap fixes (PR #1)

| ID | Fix | Files | LOC |
|---|---|---|---|
| C1 | `forwardUrl` optional when `captureOnly:true` | `Configuration/HookVaultOptions.cs` | ~3 |
| C2 | Wrap `JsonException` with config-path context; top-level startup try/catch with clean exit | `Configuration/HookVaultOptions.cs`, `Program.cs` | ~25 |
| C3 | `JwtOptions.Ephemeral()` factory; skip secret check under `NO_AUTH=true`, generate ephemeral key with warning log | `Auth/JwtOptions.cs`, `Program.cs` | ~15 |
| C4 | `HEALTHCHECK CMD wget -qO- http://localhost:8080/api/health \|\| exit 1` in Dockerfile; `healthcheck:` blocks in both compose files | `Dockerfile`, `docker-compose.yml`, `docker-compose.postgres.yml` | ~15 |
| C5 | Replace two anonymous-object error returns with `ApiError` | `Controllers/IngestController.cs:43`, `Middleware/MaxBodySizeMiddleware.cs:25` | ~10 |
| C6 | Startup-time validation of `algorithm` (one of `hmac-sha1\|hmac-sha256\|hmac-sha512`) and `signatureHeader` non-empty; warn (don't throw) when `Environment.GetEnvironmentVariable(p.Validation.SecretEnvVar)` is null | `Configuration/HookVaultOptions.cs`, `Program.cs` | ~30 |
| C7 | Doc: `DELETE /api/events?confirm=true`; SSE endpoint; `bodyContains` + `providerEventId` query params; sync `hookvault-spec` skill with actual surface | README, `.claude/skills/hookvault-spec/SKILL.md` | docs |
| C8 | Doc: single-replica deployment assumed (S5) | README | 1 line |

**Total:** ~100 LOC + targeted tests + docs.

### Bucket 2: Reliability bundle (PR #2)

| ID | Fix | Files | LOC |
|---|---|---|---|
| R1 | `Timeout = TimeSpan.FromSeconds(30)` on `"forwarder"` HttpClient; env-overridable via `HOOKVAULT_FORWARD_TIMEOUT_SECONDS` (default 30, min 1, max 300) | `Program.cs:70`, README | ~10 |
| R2 | `ReplayWorker` short-circuits 4xx (except 408, 425, 429) to `ReplayFailed` immediately. Helper: `IsRetriable(int? statusCode)` | `Services/ReplayWorker.cs`, new test | ~10 + 1 test |
| R3 | `Channel.CreateBounded<ReplayJob>(new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait })` | `Services/ReplayQueue.cs` | ~3 |
| R4 | Add `"retry"` outcome dimension on `ReplaysTotal` per non-final failed attempt | `Services/ReplayWorker.cs`, `HookVaultMeter` | ~3 |
| R5 | Spec note: "4xx responses (except 408, 425, 429) skip remaining retries — they indicate a configuration or auth error, not transient failure." | `.claude/skills/hookvault-spec/SKILL.md` | docs |

**Total:** ~30 LOC + ~30 LOC tests + spec note.

### Bucket 3: Postgres parity (PR #3)

| ID | Fix | Files | LOC |
|---|---|---|---|
| P1 | Migration 1 `Up`: branch on `migrationBuilder.ActiveProvider`. Postgres path uses `ALTER TABLE "Events" ALTER COLUMN "Body" TYPE bytea USING convert_to("Body", 'UTF8')` plus a `jsonb_object_agg(key, jsonb_build_array(value))` rewrite of `Headers`. Mirror in `Down`. | `Migrations/00000000000001_BytesBodyAndArrayHeaders.cs` | ~60 |
| P2 | CI job: boot `docker-compose.postgres.yml`, run `dotnet ef database update`, hit `/api/health`, tear down. xUnit Postgres matrix deferred to v1.x. | `.github/workflows/*.yml` | ~40 |
| P3 | `CONTRIBUTING.md`: migration ID format convention (zero-padded sequential) | `CONTRIBUTING.md` | docs |

**Total:** ~100 LOC + CI workflow + docs.

### Migration safety (P1)

Existing v0.2 Postgres users upgrade in place:

1. v1.0 startup runs `MigrateAsync` as today.
2. Migration 1 Postgres branch executes the `ALTER TABLE … USING` cast (text → bytea is lossless: SQLite TEXT is UTF-8 byte-storage already) and the `jsonb_object_agg` headers rewrite.
3. Migrations 2 and 3 are pure `ADD COLUMN` and already provider-portable.
4. Startup orphan-sweep at `Program.cs:225-237` runs against the migrated schema unchanged.

Users who already ran the broken v0.3 migration and were forced to drop their DB are unaffected (no data to migrate). Users on v0.2 upgrading directly to v1.0 keep their data. Release notes should call this out explicitly.

---

## Deferred to v1.x with explicit reasoning

| Item | Reason |
|---|---|
| Composite indexes `(Provider, ReceivedAt DESC)`, `(Status, ReceivedAt)` | No current user pain at expected scale (<50k events/provider) |
| `IngestService` extraction from `IngestController` | YAGNI; current 140-line action method is traceable. Revisit when v1.x adds enrichment / filtering / multi-destination fanout |
| Body storage in a separate `EventBody` table | Works at typical webhook sizes (Stripe/GitHub/Shopify < 64 KB) |
| `IReplayQueue` interface for durable-queue swap | YAGNI; DB-as-source-of-truth + bulk `replay-failed` endpoint is enough |
| Config hot-reload | Restart-to-reload acceptable for dev tool |
| Readiness/liveness probe split | Kubernetes-only concern; out of v1.0 deployment scope |
| Postgres-native server-side body search | Would diverge query semantics by provider |
| `timestamptz` on Postgres | High churn, no concrete query benefit |
| ProblemDetails RFC 7807 error envelope | `ApiError` is fine; revisit when OpenAPI SDK generation is added |
| Advisory-lock multi-replica safety | Single-replica is the v1.0 contract (S5) |
| Polly / circuit-breaker on forward path | 30s timeout + 4xx short-circuit covers 90% of dead-target failure modes |
| Per-provider rate-limit / per-provider retry override | Permutation explosion vs YAGNI |
| Configurable retry curve | Spec contract is more valuable than the flexibility |
| Per-attempt metric beyond `outcome=retry` | One retry signal is enough for v1.0 dashboards |
| EF `BackfillMigrationHistoryAsync` hardcoded version string | Cosmetic, legacy-DB-only. Fix on next EF major bump |
| Structured-JSON console logger | v1.x observability work |
| Testcontainers Postgres matrix in CI | Manual compose smoke (S3) is enough for v1.0 |
| Push status filter into `repo.GetFailedAsync` query | No user pain at expected backlog sizes |
| `MaxBodySize` cap before `RawBody` buffering | Would require restructuring middleware; cap currently protects persistence/forwarding, not memory |

---

## Sequencing & branch plan

1. **`feat/v1.0-polish`** — Bucket 1 (Cheap fixes). Independent. No spec contract changes. Ship first.
2. **`feat/v1.0-reliability`** — Bucket 2 (Reliability bundle). One additive spec refinement (R5). Independent of Bucket 1; can be parallel branch.
3. **`feat/v1.0-postgres`** — Bucket 3 (Postgres parity). Requires Bucket 1 merged first if any CI workflow changes overlap.

After all three merge: cut **v1.0.0-rc.1**, smoke-test on both SQLite and `docker-compose.postgres.yml`, then tag **v1.0.0**.

---

## Conventions and skills referenced

- All code changes follow [`hookvault-conventions`](../.claude/skills/hookvault-conventions/SKILL.md): file-scoped namespaces, primary-ctor DI, `IHttpClientFactory`, `CryptographicOperations.FixedTimeEquals`, async-only EF, `sealed` on new concrete classes, `record` for immutable result types.
- Commit format per [`commit-style`](../.claude/skills/commit-style/SKILL.md): `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`. No AI attribution.
- All PRs reviewed against [`pr-review-checklist`](../.claude/skills/pr-review-checklist/SKILL.md), with `webhook-security-reviewer` agent invoked on any change to ingest / HMAC / forwarding paths.
