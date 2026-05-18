# E2E testing — design spec

**Status:** draft
**Date:** 2026-05-18
**Approach:** A (layered API + UI E2E, single compose stack, test-only reset endpoint)

## 1. Why

HookVault has unit tests (`tests/HookVault.Tests/`) and a Postgres smoke
test in CI that asserts `/api/health`. It has **no automated end-to-end
coverage**. The files under `tests/e2e/*.spec.ts` are manual scenario
docs for the Playwright MCP — not runnable tests, not wired into CI.

This spec defines the first slice of real E2E coverage:

- Boots the production-shape stack (Postgres + HookVault container +
  forward target) via `docker compose`.
- Exercises the golden path through the public HTTP surface and the
  React UI.
- Runs in CI on every PR, fails the build on regression.
- Establishes a pattern that ports cleanly into a multi-tenant SaaS
  project (Supabase + React + Stripe/email/calendar integrations) as
  the next consumer.

## 2. Scope

### In scope (this slice)

- Three API-level E2E tests covering the ingest → forward → store path,
  HMAC validation, and replay.
- Two UI-level Playwright tests covering token-gate auth and end-to-end
  visibility of an ingested event in the live feed.
- A `docker-compose.e2e.yml` stack: Postgres + HookVault + mock upstream.
- A test-only `POST /api/test/reset` endpoint, available only when
  `ASPNETCORE_ENVIRONMENT=Testing`.
- Two new CI jobs (`e2e-api`, `e2e-ui`) added to `.github/workflows/ci.yml`,
  running in parallel after `build-and-test`.

### Out of scope (deferred)

- Porting the remaining seven scenario docs in `tests/e2e/` (filters,
  search, body-edit, retention, etc.) into real tests. Follow-up PRs.
- Cross-browser coverage. Chromium only for now.
- Parallel Playwright workers. Single-worker; multi-worker is a follow-up
  once the seed/teardown pattern lands.
- Running E2E on every push. PR-only to save GitHub Actions minutes.
- Cleaning up or deleting the existing `tests/e2e/*.spec.ts` manual
  scenario docs. Left in place; replaced or deleted in a later pass.

## 3. Architecture

```
┌────────────────────── docker-compose.e2e.yml ──────────────────────┐
│                                                                    │
│   postgres:16-alpine          hookvault                            │
│   (test DB, fresh volume)   ┌──────────────────────────────┐       │
│                             │ ASPNETCORE_ENVIRONMENT=Testing│      │
│                             │ DATABASE_URL=postgres://...   │      │
│                             │ HOOKVAULT_CONFIG_PATH=        │      │
│                             │   /app/hookvault.e2e.json     │      │
│                             │ forward_url →                 │      │
│                             │   http://mock-upstream:8080   │      │
│                             └──────────────────────────────┘       │
│                                                                    │
│   mock-upstream                                                    │
│   (mendhak/http-https-echo:34, echoes received requests + logs)    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
     ▲ http://localhost:7777                ▲ docker logs mock-upstream
     │                                      │ (assertion source)
     │                                      │
┌────┴───────────────┐                ┌─────┴───────────────────┐
│ tests/HookVault.E2E│                │ tests/e2e-ui            │
│ (xUnit, HttpClient)│                │ (@playwright/test, TS)  │
│ — IngestTests      │                │ — auth.spec.ts          │
│ — ReplayTests      │                │ — live-feed.spec.ts     │
└────────────────────┘                └─────────────────────────┘
        │                                       │
        └──────────────┬────────────────────────┘
                       │
              ┌────────┴─────────┐
              │ POST /api/test/  │ shared reset helper
              │ reset            │ (truncate events + clear queue)
              └──────────────────┘
```

### Layer responsibilities

- **API tests** drive HTTP directly. Fast, deterministic, exercise the
  ingest/forward/replay state machine and the mock-upstream assertion
  flow. They are the primary safety net.
- **UI tests** drive a real Chromium against the running React build
  (served from the HookVault container). They cover the auth UX and
  prove end-to-end visibility of an event from ingest to dashboard.
- **Mock upstream** is the forward target. It echoes every received
  request; tests assert against its container logs via
  `docker logs --since <timestamp>` parsed by a small client helper.

## 4. File / project layout

```
docker-compose.e2e.yml              # new
hookvault.e2e.json                  # new; forward_url → http://mock-upstream:8080

src/HookVault/Controllers/
  TestController.cs                 # new; POST /api/test/reset
                                    # only mapped if env.IsTesting()

tests/
  HookVault.Tests/                  # unchanged
  HookVault.E2E/                    # new xUnit project
    HookVault.E2E.csproj
    Fixtures/
      ComposeStackFixture.cs        # xUnit collection fixture
      HookVaultClient.cs            # typed HttpClient wrapper
      MockUpstreamClient.cs         # parses `docker logs mock-upstream`
    Tests/
      IngestTests.cs
      ReplayTests.cs
    appsettings.E2E.json            # localhost:7777 base URL, JWT, etc.

  e2e-ui/                           # new Playwright Test project
    package.json
    playwright.config.ts
    tests/
      auth.spec.ts
      live-feed.spec.ts
    fixtures.ts                     # injects { api, token } helpers

.github/workflows/ci.yml            # extended: e2e-api, e2e-ui jobs
```

## 5. Component design

### 5.1 `TestController`

```csharp
[ApiController]
[Route("api/test")]
public sealed class TestController(
    HookVaultDbContext db,
    ReplayQueue queue,
    IHostEnvironment env) : ControllerBase
{
    [HttpPost("reset")]
    [Authorize]
    public async Task<IActionResult> Reset()
    {
        // Belt-and-braces: refuse if somehow mapped outside Testing.
        if (!env.IsEnvironment("Testing"))
        {
            return NotFound();
        }

        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE events");
        queue.Drain();
        return NoContent();
    }
}
```

Registration in `Program.cs`:

```csharp
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddControllers().AddApplicationPart(
        typeof(TestController).Assembly);
}
```

Outside Testing the controller class is never instantiated and the
route 404s.

**Implementation dependency:** `ReplayQueue` does not currently expose
a public `Drain()` method. This spec assumes adding a minimal
`Drain()` that drains the underlying channel without blocking. If
draining proves intrusive, the alternative is to wait synchronously
for the queue to empty inside `Reset` before returning 204; the
external contract is unchanged.

The exact `TRUNCATE` target table name follows the EF Core mapping
(`__EFMigrationsHistory` excluded). The implementation step resolves
the concrete table identifier from the `DbContext` model rather than
hard-coding `events`.

### 5.2 `ComposeStackFixture` (xUnit collection fixture)

Lifecycle: per-test-collection.

- **Setup:** if `E2E_SKIP_COMPOSE=1` is set, no-op (CI sets the env and
  starts compose itself). Otherwise `docker compose -f
  docker-compose.e2e.yml up -d --build` and poll `/api/health` until
  ready or 60s timeout.
- **Per-test reset:** the fixture exposes a `ResetAsync()` helper that
  each test calls in its arrange phase.
- **Teardown:** if `E2E_SKIP_COMPOSE=1`, no-op. Otherwise `down -v`.

The skip-compose env lets CI control lifecycle at the job level (boot
once, run all tests, tear down once) while local runs default to
self-managed.

### 5.3 `MockUpstreamClient`

Reads the mock container's log stream and parses `mendhak/http-https-echo`'s
JSON-per-line output. Public API:

```csharp
Task<ReceivedRequest> WaitForRequestAsync(
    Func<ReceivedRequest, bool> predicate,
    TimeSpan timeout);
```

Implementation detail: spawns `docker logs --since <iso8601> --follow
mock-upstream`, parses lines, returns the first match. Each test
captures `DateTimeOffset.UtcNow` before its action and passes it as
`since` to scope the window.

If log parsing proves fragile in practice (escaping, multiline bodies),
the fallback is to replace the mendhak image with a 30-line ASP.NET
minimal API echo container that exposes `GET /received` returning a
JSON array. Same compose slot, same `MockUpstreamClient` interface.

### 5.4 Playwright `fixtures.ts`

```ts
export const test = base.extend<{ api: ApiHelper; token: string }>({
  api: async ({}, use) => use(new ApiHelper('http://localhost:7777', JWT)),
  token: async ({}, use) => use(JWT),
});

test.beforeEach(async ({ api }) => { await api.reset(); });
```

`ApiHelper` mirrors the C# `HookVaultClient` — ingest, reset, get
events. Tests use it for arrange-phase data setup and never for
assertions (assertions belong on the UI surface).

## 6. Test catalogue

### API (xUnit)

| # | Name                              | Action                                  | Assertion                                    |
|---|-----------------------------------|-----------------------------------------|----------------------------------------------|
| 1 | `ValidHmac_Ingests_And_Forwards`  | POST /ingest/stripe with valid HMAC     | 200; event stored status=forwarded; mock-upstream received same body |
| 2 | `InvalidHmac_Rejected`            | POST /ingest/stripe with bad HMAC       | 401; no event stored; mock-upstream got nothing |
| 3 | `Replay_Forwards_Again`           | Ingest then POST /api/events/{id}/replay| 204; mock-upstream received 2 deliveries     |

### UI (Playwright)

| # | Name                              | Action                                   | Assertion                                  |
|---|-----------------------------------|------------------------------------------|--------------------------------------------|
| 1 | `TokenGate rejects empty input`   | Visit `/` with no token, submit empty    | "Token is required" error visible          |
| 2 | `Ingested event appears in feed`  | Ingest via API, visit `/?token=<jwt>`    | Event row visible (`expect(...).toBeVisible({ timeout: 10_000 })`) with provider + timestamp |

## 7. CI integration

```yaml
e2e-api:
  needs: build-and-test
  runs-on: ubuntu-latest
  env:
    E2E_SKIP_COMPOSE: "1"
  steps:
    - checkout
    - setup-dotnet 8.0.x
    - generate test secrets (openssl rand -hex)
    - cp hookvault.example.json hookvault.json   # for compose mount
    - docker compose -f docker-compose.e2e.yml up -d --build
    - wait for hookvault healthcheck (reuse postgres-smoke loop)
    - dotnet test tests/HookVault.E2E
        --logger "trx;LogFileName=e2e-api.trx"
        --results-directory ./TestResults
    - if: failure() — docker compose logs > artifact
    - if: always() — upload TestResults/
    - if: always() — docker compose down -v

e2e-ui:
  needs: build-and-test
  runs-on: ubuntu-latest
  env:
    E2E_SKIP_COMPOSE: "1"
  steps:
    - checkout
    - setup-node 22
    - npm ci  (working-directory: tests/e2e-ui)
    - generate test secrets
    - cp hookvault.example.json hookvault.json
    - docker compose -f docker-compose.e2e.yml up -d --build
    - wait for hookvault healthcheck
    - npx playwright install --with-deps chromium  (working-directory: tests/e2e-ui)
    - npx playwright test  (working-directory: tests/e2e-ui)
    - if: failure() — upload playwright-report/ + traces
    - if: always() — docker compose logs > artifact
    - if: always() — docker compose down -v
```

Both jobs run in parallel after `build-and-test`. Wall-clock cost:
roughly +4 minutes vs current CI.

## 8. Data flow — golden path test

```
Test                  HookVault container        Mock upstream
  │
  │ POST /api/test/reset
  ├────────────────────────►│
  │                          │ TRUNCATE events; queue.Drain()
  │◄────────────────────── 204
  │
  │ POST /ingest/stripe (HMAC-signed)
  ├────────────────────────►│
  │                          │ verify HMAC ✓
  │                          │ persist event status=received
  │                          │ ReplayQueue.Enqueue
  │◄────────────────────── 200
  │
  │                          │ background: ReplayWorker
  │                          │   forwards to http://mock-upstream:8080
  │                          ├──────────────────────────►│
  │                          │                            │ echoes,
  │                          │                            │ logs JSON line
  │                          │◄────────────────────── 200 │
  │                          │ updates event status=forwarded
  │
  │ GET /api/events
  ├────────────────────────►│
  │◄────────────────────── 200 [{id, status: "forwarded"}]
  │
  │ docker logs mock-upstream (parsed via MockUpstreamClient)
  ├──────────────────────────────────────────────────►│
  │◄──── ReceivedRequest{body: <same as ingested>} ─────│
  │
  Assertions: event.status == "forwarded"
              forwarded.body == ingested.body
```

## 9. Error handling

- **Compose boot timeout.** Fixture polls `/api/health` for 60s; on
  failure dumps `docker compose logs` and fails fast with a clear
  exception. CI jobs upload the dumped logs as a failure artifact.
- **Mock upstream log parse failure.** `MockUpstreamClient` raises a
  typed `MockUpstreamLogException` containing the raw line that failed
  to parse. Tests surface this directly so the message lands in the
  TRX report.
- **Reset endpoint 404.** A failed `ResetAsync()` is a fatal arrange
  error; the fixture throws `InvalidOperationException` with the
  response body so the misconfiguration is obvious.
- **Flake budget.** Single retry per Playwright test
  (`retries: process.env.CI ? 1 : 0` in `playwright.config.ts`). Two
  consecutive failures fail the run. No retries at the xUnit layer.

## 10. Security posture

The `POST /api/test/reset` endpoint is the only new attack surface.
Defences, in order:

1. **Compile-time:** controller is in a separate folder but the same
   assembly. (Not stripped from the binary — keeps things simple.)
2. **Registration-time:** controller is only added to the MVC parts
   list when `env.IsEnvironment("Testing")`. Outside Testing, the route
   does not exist.
3. **Runtime guard:** the action body itself checks
   `env.IsEnvironment("Testing")` and returns 404 if not, defending
   against accidental registration.
4. **Auth:** the endpoint requires the same JWT bearer as other
   `/api/*` routes. An attacker would need both production
   misconfiguration *and* a valid token to reach it.
5. **Docs:** `docs/architecture-audit-v1.0.md` (or successor) gets a
   one-line note describing the endpoint and its gating.

## 11. Transferability — SaaS lift notes

When this pattern moves to the multi-tenant CRM project:

- `/api/test/reset` (truncate-all) becomes `/api/test/seed-tenant` and
  `/api/test/delete-tenant?id=`. Each test gets a fresh `org_id`;
  parallel workers safe because Supabase RLS isolates by `org_id`.
- `mock-upstream` slot is reused per integration: `stripe-mock` for
  payments, a tiny SMTP echo (`maildev`) for transactional email, an
  in-memory calendar mock for booking. Same compose-slot pattern.
- `MockUpstreamClient` becomes one client per mock; the
  `WaitForRequestAsync(predicate, timeout)` shape is unchanged.
- Playwright `fixtures.ts` grows a `tenant` fixture that injects
  `{ org, trainer, clients }` into every test. User-story tests
  decompose mechanically: seed tenant → drive UI → assert UI + mock
  upstream side effects.

## 12. Open questions

None blocking. Documented decisions:

- **Mock image:** start with `mendhak/http-https-echo:34`. If log
  parsing turns out fragile, swap to a 30-line custom echo container
  in a follow-up. Same compose slot, same client interface.
- **Reset auth:** require JWT (same as other routes) rather than IP
  allowlisting. Simpler; reuses existing middleware.
- **Test data file:** ship `hookvault.e2e.json` with a single
  `stripe`-style provider entry forwarding to `http://mock-upstream:8080`.
  Real HMAC secret is generated at CI step time and injected via env.

## 13. Acceptance criteria

1. New `tests/HookVault.E2E` xUnit project builds with zero warnings.
2. `dotnet test tests/HookVault.E2E` passes locally with compose running.
3. `npx playwright test` in `tests/e2e-ui` passes locally with compose
   running.
4. `e2e-api` and `e2e-ui` GitHub Actions jobs are green on the merge PR.
5. On a deliberately introduced regression (e.g., breaking HMAC check),
   both jobs fail with informative output.
6. `tests/e2e/*.spec.ts` (the old MCP scenario docs) untouched; no
   regression in `build-and-test` or `postgres-smoke` jobs.
