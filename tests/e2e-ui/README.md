# `tests/e2e-ui` — Playwright UI E2E

Browser-driven end-to-end tests for the HookVault React dashboard.
Hits the live container at `http://localhost:7777` (override via
`HOOKVAULT_BASE_URL`).

Pairs with the C# API E2E project at `tests/HookVault.E2E/`. The two
share the same `docker-compose.e2e.yml` stack and the same
`POST /api/test/reset` endpoint for per-test isolation.

## Prerequisites

- Node 22+
- Docker + docker compose
- The e2e compose stack — defined at the repo root in
  `docker-compose.e2e.yml` (postgres + hookvault + mendhak echo).

## One-time setup

```bash
cd tests/e2e-ui
npm ci
npx playwright install --with-deps chromium
```

## Running the suite locally

The stack must be running and reachable. The container has to be
booted with `ASPNETCORE_ENVIRONMENT=Testing` and `HOOKVAULT_E2E_TEST=1`
so the test-only reset endpoint is mapped — both are wired into
`docker-compose.e2e.yml`.

```bash
# From repo root:
export HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32)
export POSTGRES_PASSWORD=$(openssl rand -hex 16)
export STRIPE_WEBHOOK_SECRET=$(openssl rand -hex 32)
docker compose -f docker-compose.e2e.yml up -d --build
# Wait for healthy:
until curl -fsS http://localhost:7777/api/health >/dev/null; do sleep 1; done

# From tests/e2e-ui:
cd tests/e2e-ui
HOOKVAULT_BASE_URL=http://localhost:7777 npx playwright test

# Tear down:
cd ../..
docker compose -f docker-compose.e2e.yml down -v
```

The `STRIPE_WEBHOOK_SECRET` env var must be the **same value** in the
shell that runs `docker compose up` and the shell that runs
`playwright test` — both sides sign with it.

## Inspecting failures

On CI failure, Playwright uploads `playwright-report/` and
`test-results/` as artifacts (see `.github/workflows/ci.yml`). Locally:

```bash
npx playwright show-report
```

Traces (`trace.zip`), screenshots, and video are retained on failure
(configured in `playwright.config.ts`).

## How tests are isolated

Every test pulls the `api` fixture from `fixtures.ts`. The fixture
calls `POST /api/test/reset` before the test body runs — this
truncates the `Events` table and drains the replay queue, so each test
starts from an empty state. The stack itself is shared across all
tests (single docker compose boot per CI job), but DB state is not.

## Conventions for new tests

- Import `test` and `expect` from `../fixtures`, not from
  `@playwright/test` directly. Otherwise the per-test reset will not
  fire and you'll get cross-test contamination.
- Prefer `data-testid` locators when the React side exposes them
  (`getByTestId('event-row')` over CSS-class scoping). Add a new
  testid to the component if the existing surface is brittle —
  testid is a public contract for tests, not a styling concern.
- For role-based locators (`getByRole('button', { name: ... })`),
  match copy text with case-insensitive regex (`/continue/i`) to
  survive trivial copy edits.
- Use `expect(...).toBeVisible({ timeout: 10_000 })` for async UI
  updates (live feed, SSE-driven views). The default expect timeout
  (10s, see `playwright.config.ts`) already covers most cases.
