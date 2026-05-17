# PR #1 — v1.0 Polish (Cheap Fixes Bucket)

**Branch:** `feat/v1.0-polish`
**Source:** [`docs/architecture-audit-v1.0.md`](../docs/architecture-audit-v1.0.md), Bucket 1
**Estimated size:** ~100 LOC production + ~80 LOC tests + targeted docs
**Spec impact:** None for code; docs sync only (C7)

## Goal

Eliminate the eight first-run / consistency papercuts surfaced by the v1.0 audit. Ship as a single PR that any self-hosting user benefits from.

## In scope

C1, C2, C3, C4, C5, C6, C7, C8 from the audit doc (Bucket 1).

## Out of scope (other PRs)

- R1–R5 (reliability bundle) → PR #2 `feat/v1.0-reliability`
- P1–P3 (Postgres parity) → PR #3 `feat/v1.0-postgres`
- Any deferred item from audit's v1.x list

## Conventions enforced

All work follows [`hookvault-conventions`](../.claude/skills/hookvault-conventions/SKILL.md): file-scoped namespaces, primary-ctor DI, `sealed` on new concrete classes, `record` for immutable result types, async-only EF, no `new HttpClient()`, no `==` on secrets. Commit format per [`commit-style`](../.claude/skills/commit-style/SKILL.md) — `feat:` / `fix:` / `docs:` prefixes, no AI attribution anywhere.

---

## Task groups

Dispatched as **G1 / G2 / G3 in parallel**, then **G4 sequential** (docs must reflect shipped behaviour).

### G1 — Config + Bootstrap (C1, C2, C3, C6)

**Files:** `src/HookVault/Configuration/HookVaultOptions.cs`, `src/HookVault/Auth/JwtOptions.cs`, `src/HookVault/Program.cs`
**Tests dir:** `tests/HookVault.Tests/`

**C1 — `forwardUrl` optional when `captureOnly:true`**
- In `HookVaultOptions.Validate` (currently around line 49), guard the `forwardUrl` non-empty check behind `!p.CaptureOnly`.
- Test: extend `tests/HookVault.Tests/CaptureOnlyTests.cs` (or new test file) — a provider with `captureOnly:true` and empty `forwardUrl` loads without throwing.
- **Success criterion:** capture-only providers with empty `forwardUrl` pass validation; non-capture-only providers still require `forwardUrl`.

**C2 — Wrap `JsonException` with config-path context + top-level startup try/catch**
- In `HookVaultOptions.Load`, wrap the `JsonSerializer.Deserialize` call in `try/catch (JsonException ex)`; rethrow as `InvalidOperationException` with `configPath`, line, and column included in the message.
- In `Program.cs`, add a top-level `try/catch` around the bootstrap section before `builder.Build()`. On caught `InvalidOperationException` (config/JWT/options errors), log via the bootstrap logger at `LogCritical`, then `Environment.Exit(1)`. Do NOT let raw exceptions bubble with stack traces.
- Test: new `tests/HookVault.Tests/ConfigLoadErrorTests.cs` — malformed `hookvault.json` produces an `InvalidOperationException` whose message contains the file path and line number.
- **Success criterion:** malformed config produces a one-line readable error and exit code 1, no stack trace.

**C3 — JWT ephemeral under `NO_AUTH=true`**
- In `Auth/JwtOptions.cs`, add a `public static JwtOptions Ephemeral()` factory that returns a record with a freshly-generated 48-byte random secret (use `RandomNumberGenerator.GetBytes(48)` → base64), default issuer/audience.
- In `Program.cs`, move the `noAuth` env-var read **before** `JwtOptions.FromConfiguration` is called. If `noAuth == true`, call `JwtOptions.Ephemeral()` instead, and log a `Warning`: "HOOKVAULT_NO_AUTH=true — using ephemeral in-memory JWT key. Tokens issued by this process will not validate after restart."
- Test: extend `tests/HookVault.Tests/NoAuthOptOutTests.cs` — application starts cleanly with `HOOKVAULT_NO_AUTH=true` and no `HOOKVAULT_JWT_SECRET` env var.
- **Success criterion:** `NO_AUTH=true` + missing secret → startup succeeds + warning logged; `NO_AUTH` unset + missing secret → startup still fails fast with clear message (C2 covers the messaging).

**C6 — `ValidationConfig` startup checks + missing secret-env warnings**
- In `HookVaultOptions.Validate`, for each provider with a non-null `Validation`:
  - Throw `InvalidOperationException` if `Validation.Algorithm` is not in `{"hmac-sha1", "hmac-sha256", "hmac-sha512"}` (case-insensitive).
  - Throw if `Validation.SignatureHeader` is null/whitespace.
  - Throw if `Validation.SecretEnvVar` is null/whitespace.
- In `Program.cs`, after options are loaded, iterate providers; for each `Validation.SecretEnvVar` whose `Environment.GetEnvironmentVariable(...)` is null, log a `Warning`: "Provider '{name}': env var '{secretEnvVar}' is not set; signature validation will fail until you set it."
- Test: new tests in `tests/HookVault.Tests/ConfigValidationTests.cs` covering each throw case + a warning-emission test.
- **Success criterion:** invalid algorithm/header/secretEnvVar fails startup with clear error; missing secret env var produces startup warning, not error.

**G1 dispatch model:** Single implementer subagent (Sonnet) executes C1 → C2 → C3 → C6 in order (they share `HookVaultOptions.cs` and `Program.cs`).

---

### G2 — Error contract consistency (C5)

**Files:** `src/HookVault/Controllers/IngestController.cs`, `src/HookVault/Middleware/MaxBodySizeMiddleware.cs`
**Tests dir:** `tests/HookVault.Tests/`

- Replace the anonymous `new { error = "..." }` return at `IngestController.cs:43` with `new ApiError("...", "provider_not_found")` (or similar `code` value matching the situation).
- In `MaxBodySizeMiddleware.cs:25`, replace the anonymous-object serialization with `await context.Response.WriteAsJsonAsync(new ApiError("...", "body_too_large"))`. `ApiError` is a plain record — no DI lookup needed.
- Test: extend `tests/HookVault.Tests/ApiErrorShapeTests.cs` to assert that:
  - `POST /api/ingest/unknown-provider` returns 404 with body matching `ApiError` shape (has both `error` and `code` fields).
  - A request exceeding `HOOKVAULT_MAX_BODY_BYTES` returns 413 with body matching `ApiError` shape.

**Success criterion:** every non-2xx response from any HookVault endpoint deserializes as `ApiError`.

**G2 dispatch model:** Single implementer subagent (Sonnet). Independent of G1.

---

### G3 — Docker HEALTHCHECK (C4)

**Files:** `Dockerfile`, `docker-compose.yml`, `docker-compose.postgres.yml`

- In `Dockerfile`, add (after the final `USER app` line):
  ```
  HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/api/health >/dev/null 2>&1 || exit 1
  ```
  Verify `wget` is available in the base image. If not (Alpine `aspnet:8.0-alpine` typically has `wget`), use `wget`. If somehow missing, add `apk add --no-cache wget` in the runtime stage.
- In `docker-compose.yml`, add a `healthcheck:` block on the `hookvault` service mirroring the Dockerfile directive (same interval/timeout/retries). Compose-level healthcheck takes precedence and is more visible to operators.
- In `docker-compose.postgres.yml`, add the same `healthcheck:` block on the `hookvault` service.
- **No tests required** — verify manually via `docker compose up` + `docker compose ps` showing `(healthy)`.

**Success criterion:** `docker compose ps` shows the `hookvault` service as `(healthy)` after start; `depends_on: condition: service_healthy` becomes usable downstream.

**G3 dispatch model:** Single implementer subagent (Sonnet). Independent of G1, G2.

---

### G4 — Docs sync (C7, C8) — runs AFTER G1/G2/G3 merge into branch

**Files:** `README.md`, `.claude/skills/hookvault-spec/SKILL.md`

**C7 — Spec/docs sync**
- README: document `DELETE /api/events?confirm=true` requirement under Management API.
- README: document `GET /api/events/stream` (SSE) endpoint with auth note (`?token=` query param).
- README: document `bodyContains` and `providerEventId` query params on `GET /api/events`.
- Update `.claude/skills/hookvault-spec/SKILL.md` to include the same items (SSE in management API section, dedup query params on list endpoint, `?confirm=true` on delete).
- Also document `HOOKVAULT_NO_AUTH=true` ephemeral-key behaviour added by C3.
- Also document the startup validation behaviour added by C6 (warns on missing secret env vars).

**C8 — Single-replica deployment note**
- Add one paragraph to README under "Deployment": "HookVault assumes a single replica per database. Auto-migration on startup is not multi-replica-safe; run only one container against any given SQLite file or Postgres database."

**G4 dispatch model:** Single docs-focused subagent (Haiku — pure text work). Runs sequentially after G1/G2/G3 land so it can reference the actually-shipped code paths.

---

## Verification pipeline

After all four groups land on the branch, run **in order**:

```bash
# 1. Format check
dotnet format --verify-no-changes

# 2. Build
dotnet build --configuration Release

# 3. Tests
dotnet test --configuration Release

# 4. Manual smoke (Docker healthcheck)
docker compose up -d --build
sleep 15
docker compose ps | grep "(healthy)"
docker compose down
```

All four must pass before opening the PR.

---

## PR shape

**Title:** `feat: v1.0 polish — first-run UX, error contract, healthcheck (PR 1 of 3)`

**Body skeleton:**
```
## Summary
- C1: `forwardUrl` optional when `captureOnly:true`
- C2: malformed `hookvault.json` produces a clean error, not a stack trace
- C3: `HOOKVAULT_NO_AUTH=true` no longer requires a JWT secret (ephemeral key)
- C4: Dockerfile + compose HEALTHCHECK
- C5: all errors return `ApiError` shape
- C6: startup validates `algorithm`/`signatureHeader`/`secretEnvVar`; warns on missing secret env vars
- C7: README/spec doc sync (SSE, confirm=true, dedup params, NO_AUTH ephemeral key)
- C8: single-replica deployment note

## Spec impact
None (code). Docs sync only.

## Test plan
- [ ] `dotnet format --verify-no-changes` passes
- [ ] `dotnet build --configuration Release` passes
- [ ] `dotnet test --configuration Release` passes (existing + new tests)
- [ ] `docker compose up -d` followed by `docker compose ps` shows `(healthy)`
- [ ] Smoke: `HOOKVAULT_NO_AUTH=true` startup with no `HOOKVAULT_JWT_SECRET` succeeds
- [ ] Smoke: malformed `hookvault.json` produces a one-line error and exit 1

## Refs
- Audit: docs/architecture-audit-v1.0.md (Bucket 1)
- Plan: plans/pr1-v1.0-polish.md
```

---

## Reviewer rubric

PR review checks (use [`pr-review-checklist`](../.claude/skills/pr-review-checklist/SKILL.md)):

- [ ] No `new HttpClient()` anywhere
- [ ] No `==` on signatures/secrets
- [ ] Constant-time compare preserved in any auth path touched
- [ ] All new tests use real SQLite (not mocks)
- [ ] No AI attribution in commits, PR body, or comments
- [ ] No emojis in code/docs
- [ ] No provider-specific code paths introduced
- [ ] All async paths thread `CancellationToken`
- [ ] All new public types are `sealed`
- [ ] All new files use file-scoped namespaces
- [ ] No secrets leaked in logs (verify `_secret` substring not in any new log line)

Plus `webhook-security-reviewer` agent invoked because C3 touches the JWT/auth path.

---

## Risks

1. **C3 ephemeral key + token validation** — tokens minted before `NO_AUTH=true` was enabled won't validate against the ephemeral key. Acceptable; warning log surfaces this.
2. **C4 wget availability** — if the Alpine base image strips `wget`, fallback is `curl` or `apk add wget`. Implementer must verify.
3. **C2 startup try/catch** — must not swallow hosted-service runtime errors, only bootstrap-time configuration errors. The catch scope must be tight (around the bootstrap block, NOT around `app.Run()`).

## Cycle cap

Per [`autonomous-loops`](../.claude/skills/autonomous-loops/SKILL.md): if any task group fails verification 3 times in a row, escalate to the maintainer rather than looping further.
