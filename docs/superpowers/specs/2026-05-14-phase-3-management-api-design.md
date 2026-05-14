# Phase 3 — Management API + JWT Auth

**Status:** approved (brainstorming)
**Date:** 2026-05-14
**Branch:** `feat/phase-3-management-api`
**Spec source:** [`hookvault-spec`](../../../.claude/skills/hookvault-spec/SKILL.md) §"Build order" Phase 3
**Conventions:** [`hookvault-conventions`](../../../.claude/skills/hookvault-conventions/SKILL.md)

## Goals

Expose the captured-event corpus and the replay machinery (Phase 2) over an
authenticated HTTP surface so a developer can list, inspect, re-fire, and purge
webhook events from `curl`, a UI, or `.http` files.

Phase 3 does **not** ship a UI, a Docker image, or example configs — those are
Phase 4 / 5. Only the API and its auth layer.

## Non-goals

- No role / scope model. One token authenticates one operator with full access.
- No login endpoint, no admin password, no token issuance over HTTP. The CLI
  mints tokens.
- No refresh-token flow. Expired tokens get re-minted by the CLI.
- No rate limiting (it's a single-tenant local dev tool).
- No telemetry / audit log beyond standard `ILogger` lines.

## Auth model

**Validate-only JWT bearer.** HookVault is an OAuth2 resource server. Operators
mint tokens via a CLI subcommand using a shared secret; the app validates and
trusts them.

**Why this shape:**

1. Smallest attack surface for an OSS dev tool — no password handling, no login
   UI, no token-issuance endpoint exposed to the network.
2. Frictionless SaaS-ification path: swap HS256 + symmetric secret for
   RS256 + JWKS endpoint in `JwtBearerOptions`, no controller rewrite needed.
   A login flow built now would be thrown away the moment a real IdP appears.

### Configuration

All from env vars; appsettings supported for local dev only.

| Var | Default | Notes |
|---|---|---|
| `HOOKVAULT_JWT_SECRET` | **required** | Min 32 bytes (256 bits). App throws at startup if missing/short. |
| `HOOKVAULT_JWT_ISSUER` | `hookvault` | `iss` claim + validation. |
| `HOOKVAULT_JWT_AUDIENCE` | `hookvault` | `aud` claim + validation. |

Bound to a `JwtOptions` record. Registered as `IOptions<JwtOptions>`. The
secret-length check happens once during DI build (fail-fast).

### Token claims

```json
{
  "sub": "admin",
  "iss": "hookvault",
  "aud": "hookvault",
  "iat": 1714694400,
  "exp": 1717286400
}
```

No roles/scopes. `sub` is informational only.

### `generate-token` CLI subcommand

`dotnet run --project src/HookVault -- generate-token [options]`

Options:
- `--subject <s>` — token `sub` claim. Default `admin`.
- `--expires <duration>` — token lifetime. Format `<n>{h,d}` (e.g. `1h`, `7d`,
  `30d`). Default `30d`.

Behaviour:
- Reads `HOOKVAULT_JWT_SECRET`, `HOOKVAULT_JWT_ISSUER`, `HOOKVAULT_JWT_AUDIENCE`
  directly from environment (no DI container, no web host built).
- Writes the JWT to stdout, nothing else — pipeable to `pbcopy` / a `.env` file.
- Exit 0 on success, 1 on missing secret or bad args, with a clear stderr message.
- Subcommand handling lives **before** `WebApplication.CreateBuilder` so the
  command exits cleanly without spinning up the web host.
- Works identically in Docker: `docker run hookvault generate-token`.

Never reads the secret from CLI args — it must come from env, so it doesn't
land in shell history.

### Bearer-token middleware

Wired with `AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(...)`.

`TokenValidationParameters`:
- `ValidateIssuer = true`, `ValidIssuer = options.Issuer`
- `ValidateAudience = true`, `ValidAudience = options.Audience`
- `ValidateLifetime = true`
- `ValidateIssuerSigningKey = true`
- `IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret))`
- `ClockSkew = TimeSpan.FromSeconds(30)` (tightened from default 5 minutes —
  for a single-host dev tool the clocks are the same; expiry should mean expiry)

Middleware order in `Program.cs`:

```
RawBodyMiddleware → UseAuthentication → UseAuthorization → MapControllers
```

Raw body capture stays before auth so ingest is unaffected.

`[AllowAnonymous]` on `IngestController` and `HealthController` (webhooks and
liveness probes can't carry bearer tokens). `[Authorize]` on `EventsController`.

## HTTP surface

All paths under `/api/events`. JWT required on every endpoint.

### `GET /api/events`

List events. All query params optional.

| Param | Type | Default | Constraints |
|---|---|---|---|
| `provider` | string | — | exact match against `WebhookEvent.Provider` |
| `status` | string | — | case-insensitive `EventStatus` enum; 400 if invalid |
| `from` | ISO-8601 `DateTimeOffset` | — | filter `ReceivedAt >= from` |
| `to` | ISO-8601 `DateTimeOffset` | — | filter `ReceivedAt <= to` |
| `limit` | int | `50` | clamped `[1, 500]` |
| `offset` | int | `0` | clamped `>= 0` |

Response `200`:
```json
{
  "items": [EventSummary, ...],
  "total": 142,
  "limit": 50,
  "offset": 0
}
```

`total` reflects the count of rows matching filters *before* paging.

### `GET /api/events/{id:guid}`

Full detail. Returns `EventDetail` or `404 { "error": "Event not found." }`.

`Headers` and `ValidationDetails` are deserialised from their stored JSON
strings to `JsonElement` so the response contains structured JSON, not
double-encoded strings.

### `POST /api/events/{id:guid}/replay`

Enqueue a single event for replay.

1. Resolve event; `404` if missing.
2. `replayQueue.EnqueueAsync(id, ct)`.
3. Return `202 { "eventId": "<guid>", "status": "Queued" }`.

Does **not** mutate DB state. The `ReplayWorker` flips status to `Replaying`
when it picks up the item. Re-enqueuing an already-running event is benign —
the worker just runs the HTTP attempt again.

### `POST /api/events/replay-failed`

Bulk-enqueue events whose previous forward/replay failed.

Query params (optional):
- `provider` — narrow to one provider
- `status` — `ForwardFailed` or `ReplayFailed` (case-insensitive, parsed the
  same way as `GET /api/events?status=`); defaults to both. 400 if any other
  value.

Behaviour:
1. `repo.GetFailedAsync(provider, ct)` (already returns both failure statuses).
2. Apply `status` filter if specified.
3. Loop `replayQueue.EnqueueAsync(id, ct)` for each.
4. Return `202 { "enqueued": <count>, "provider": "<provider|null>", "status": "<status|null>" }`.

`enqueued: 0` is success, not an error. Logs at `Information` with the count.

### `DELETE /api/events`

Permanently delete events.

Query params:
- `provider` — optional, narrows the delete
- `confirm` — **required**, must equal `true` (case-insensitive)

Without `confirm=true`: `400 { "error": "Pass ?confirm=true to delete events." }`.

With `confirm=true`: `repo.DeleteAsync(provider, ct)` → `200 { "deleted": <count>, "provider": "<provider|null>" }`. Logs at `Warning`.

## DTOs

```csharp
namespace HookVault.Contracts;

public sealed record EventSummary(
    Guid Id,
    string Provider,
    string Status,
    DateTimeOffset ReceivedAt,
    bool? SignatureValid,
    int? ForwardStatusCode,
    int ReplayCount,
    DateTimeOffset? ForwardedAt);

public sealed record EventDetail(
    Guid Id,
    string Provider,
    string Path,
    JsonElement Headers,
    string Body,
    DateTimeOffset ReceivedAt,
    string? SignatureHeader,
    bool? SignatureValid,
    JsonElement? ValidationDetails,
    string ForwardUrl,
    DateTimeOffset? ForwardedAt,
    int? ForwardStatusCode,
    string? ForwardError,
    string Status,
    int ReplayCount,
    DateTimeOffset? LastReplayAt,
    string? LastError);

public sealed record ListEventsResponse(
    IReadOnlyList<EventSummary> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record ReplayEnqueuedResponse(Guid EventId, string Status);

public sealed record ReplayBulkResponse(int Enqueued, string? Provider, string? Status);

public sealed record DeleteResponse(int Deleted, string? Provider);

public sealed record ApiError(string Error, string? Code = null);
```

## Repository additions

`EventRepository` gets one new method:

```csharp
public async Task<(List<EventSummary> Items, int Total)> ListSummariesAsync(
    string? provider, string? status, DateTimeOffset? from, DateTimeOffset? to,
    int limit, int offset, CancellationToken ct);
```

Same filter logic as `ListAsync`, but the final `Select` projects to
`EventSummary` so the generated SQL only pulls the summary columns — large
bodies/headers stay in the DB.

`ListAsync` (full-entity) is left intact in case future internal callers need
it. No other repo changes.

## Error response shape

A single shape across all controller-returned errors:

```json
{ "error": "Human readable message", "code": "optional_machine_token" }
```

The default `[ApiController]` 400 returns `ValidationProblemDetails`. We
override that with a small `InvalidModelStateResponseFactory` in `Program.cs`
that maps to our `ApiError` shape, so clients only see one error format.

`401` responses come from the JWT middleware unchanged — empty body,
`WWW-Authenticate: Bearer error="invalid_token"` (and `error_description` for
expired tokens). That's standard and clients already know how to read it.

## Logging

- `Information`: single-replay enqueued, bulk-replay count, CLI token mint.
- `Warning`: DELETE invoked (destructive), invalid `status` filter value.
- `Error`: nothing in this layer. The worker owns replay failure logging.

Never log: request bodies, token values, the JWT secret.
Always log: event IDs, provider names, counts.

## Security

- HS256 secret minimum 32 bytes enforced at startup.
- `ClockSkew = 30s` so expiry has bite.
- No token logging or echoing anywhere.
- DELETE gated by explicit `?confirm=true`.
- CLI never reads the secret from arguments (env only) — keeps secrets out of
  shell history.

## File layout

New files:
```
src/HookVault/Auth/JwtOptions.cs
src/HookVault/Auth/JwtTokenGenerator.cs
src/HookVault/Cli/GenerateTokenCommand.cs
src/HookVault/Controllers/EventsController.cs
src/HookVault/Contracts/EventSummary.cs
src/HookVault/Contracts/EventDetail.cs
src/HookVault/Contracts/ListEventsResponse.cs
src/HookVault/Contracts/ReplayEnqueuedResponse.cs
src/HookVault/Contracts/ReplayBulkResponse.cs
src/HookVault/Contracts/DeleteResponse.cs
src/HookVault/Contracts/ApiError.cs
tests/HookVault.Tests/JwtAuthTests.cs
tests/HookVault.Tests/GenerateTokenCommandTests.cs
tests/HookVault.Tests/EventsControllerTests.cs
```

Touched files:
```
src/HookVault/Program.cs           — CLI intercept, auth wiring, error factory
src/HookVault/Infrastructure/EventRepository.cs   — add ListSummariesAsync
```

## Build sequence (separate commits)

Each step is independently buildable + green-testable.

1. `feat: add jwt options + token generator` — `JwtOptions`, `JwtTokenGenerator`
   pure helper, unit tests for the generator. No wiring yet.
2. `feat: wire jwt bearer authentication` — `AddAuthentication`/`AddAuthorization`,
   startup secret validation, `[AllowAnonymous]` on existing public controllers,
   `JwtAuthTests` against a throwaway protected probe endpoint.
3. `feat: add generate-token cli subcommand` — `GenerateTokenCommand`, intercept
   in `Program.cs`, `GenerateTokenCommandTests`.
4. `feat: add events controller list + detail` — DTOs, `ListSummariesAsync`,
   `EventsController` with list + detail only, paired tests.
5. `feat: add events controller replay + bulk-replay` — both replay endpoints
   wired to `ReplayQueue`, paired tests.
6. `feat: add events controller delete with confirm guard` — DELETE + tests.
7. `feat: standardise api error response shape` — `InvalidModelStateResponseFactory`
   override + an updated error-shape test on an existing 400 path. *(May fold into
   step 2 if naturally adjacent.)*

## Test strategy

xUnit, real SQLite (in-memory with a shared `SqliteConnection` per the
conventions), real JWT crypto.

**`JwtAuthTests`** — `WebApplicationFactory<Program>`:
- No token → 401.
- Expired token → 401 with `error_description` containing "expired".
- Wrong audience → 401.
- Wrong issuer → 401.
- Tampered signature → 401.
- Valid token → 200 on the protected probe.

**`GenerateTokenCommandTests`** — call `GenerateTokenCommand.Run` directly with
controlled env vars:
- Missing secret → exit 1, stderr explains.
- Default args → JWT with `sub=admin`, exp ≈ now + 30d.
- `--subject ci --expires 1h` → claims reflect args.
- Bad `--expires` format → exit 1.
- Generated token validates against the same `TokenValidationParameters` used
  by the app.

**`EventsControllerTests`** — `WebApplicationFactory<Program>`, tokens minted
in-test via the same `JwtTokenGenerator`:
- List: empty → `total=0`; seeded 3 events → returned; filter by `provider` /
  `status` / `from` / `to` works; bad `status` → 400; `limit=0` clamped to 1;
  `limit=1000` clamped to 500.
- Detail: missing GUID → 404; existing → full payload with parsed JSON
  `headers` and `validationDetails`.
- Single replay: missing → 404; existing → 202, item present on
  `ReplayQueue.Reader`.
- Bulk replay: zero matches → 202/0; seeded `ForwardFailed` + `ReplayFailed` →
  both enqueued; `?status=ForwardFailed` → only those; `?provider=stripe` →
  only stripe.
- Delete: no `confirm` → 400; `confirm=true` → all rows gone; `confirm=true
  &provider=stripe` → only stripe rows gone.

All controller tests assert the JSON shape, not just the status code.

## Out of scope (deferred to later phases)

- Docker image (Phase 4).
- Provider-config examples (Phase 4).
- README / docs (Phase 5).
- UI for browsing events (not in roadmap — out of scope).
- Multi-user / role / scope auth model (not in roadmap).
- RS256 + JWKS swap (post-SaaS-ification; trivial config change when needed).
