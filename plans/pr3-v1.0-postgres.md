# PR #3 — v1.0 Postgres Parity

**Branch:** `feat/v1.0-postgres`
**Source:** [`docs/architecture-audit-v1.0.md`](../docs/architecture-audit-v1.0.md), Bucket 3
**Estimated size:** ~60 LOC migration + ~40 LOC CI YAML + docs
**Spec impact:** None — closes a behaviour gap (Postgres migration crashed before this PR; now it doesn't).

## Goal

Make Migration 1 (`BytesBodyAndArrayHeaders`) work on Postgres without data loss, add a manual Postgres CI smoke job that boots `docker-compose.postgres.yml` and runs the migration end-to-end, and document the zero-padded sequential migration-ID convention.

## In scope

P1, P2, P3 from the audit doc (Bucket 3). The provider switch inside the migration is the audit's explicitly-sanctioned exception to "no provider-specific code paths" (`CLAUDE.local.md`).

## Out of scope (deferred to v1.x per audit)

- Composite indexes `(Provider, ReceivedAt DESC)`, `(Status, ReceivedAt)`
- Postgres-native body search (`convert_from … LIKE`)
- `timestamptz` migration
- Advisory-lock multi-replica safety
- Testcontainers full xUnit Postgres matrix
- Body storage table split
- Hot-reload config

## Conventions

All work follows [`hookvault-conventions`](../.claude/skills/hookvault-conventions/SKILL.md). The migration file is the one place where a provider switch is legitimate. Commit format per [`commit-style`](../.claude/skills/commit-style/SKILL.md): `type: subject` lowercase, no trailing period, **no AI attribution**.

---

## Tasks

### P1 — Migration 1 Postgres branch

**File:** `src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs`

The current `Up` and `Down` both throw `NotSupportedException` if `migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite"`. Replace that hard stop with a Postgres branch.

#### Schema facts (verify before editing)

- EF Core maps `string` → `text` on `Npgsql.EntityFrameworkCore.PostgreSQL`, **not** `jsonb`. At the *start* of Migration 1, on Postgres, the `Events` table has:
  - `"Body"` → `text` (must become `bytea`)
  - `"Headers"` → `text` containing JSON of shape `{"k":"v"}` (must become `text` containing JSON of shape `{"k":["v"]}`)
  - other columns unchanged
- Provider strings to branch on:
  - SQLite: `"Microsoft.EntityFrameworkCore.Sqlite"`
  - Npgsql: `"Npgsql.EntityFrameworkCore.PostgreSQL"`
- Unknown providers must still throw.

#### Up — Postgres path

Replace lines 13-17 of the existing file with:

```csharp
if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
{
    // Body: text -> bytea. UTF-8 round-trip is lossless because the SQLite
    // path stored UTF-8 byte content as TEXT; Postgres did the same via the
    // default text encoding.
    migrationBuilder.Sql("""
        ALTER TABLE "Events"
        ALTER COLUMN "Body" TYPE bytea
        USING convert_to("Body", 'UTF8');
    """);

    // Headers: rewrite {"k":"v"} -> {"k":["v"]} in place. Headers stays text;
    // only its JSON shape changes. We parse with ::jsonb, build new value
    // arrays, then cast the result back to text for storage. Single-element
    // wrapping is correct for every row written prior to this migration —
    // no code path produces multi-value headers.
    migrationBuilder.Sql("""
        UPDATE "Events"
        SET "Headers" = (
            SELECT jsonb_object_agg(key, jsonb_build_array(value))::text
            FROM jsonb_each_text("Headers"::jsonb)
        )
        WHERE "Headers" IS NOT NULL AND "Headers" <> '';
    """);

    return;
}

if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
    throw new NotSupportedException(
        $"BytesBodyAndArrayHeaders does not support provider '{migrationBuilder.ActiveProvider}'. " +
        "Supported providers: Microsoft.EntityFrameworkCore.Sqlite, " +
        "Npgsql.EntityFrameworkCore.PostgreSQL.");

// (existing SQLite Events_new recreate path stays exactly as-is below)
```

#### Down — Postgres path

Mirror image. Replace lines 84-86 with:

```csharp
if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
{
    // Flatten {"k":["v"]} -> {"k":"v"} (takes the first element; safe because
    // the up-migration always wrapped in a single-element array and no caller
    // introduces multi-value headers on the old shape).
    migrationBuilder.Sql("""
        UPDATE "Events"
        SET "Headers" = (
            SELECT jsonb_object_agg(key, value -> 0)::text
            FROM jsonb_each("Headers"::jsonb)
        )
        WHERE "Headers" IS NOT NULL AND "Headers" <> '';
    """);

    migrationBuilder.Sql("""
        ALTER TABLE "Events"
        ALTER COLUMN "Body" TYPE text
        USING convert_from("Body", 'UTF8');
    """);

    return;
}

if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
    throw new NotSupportedException(
        $"BytesBodyAndArrayHeaders does not support provider '{migrationBuilder.ActiveProvider}'. " +
        "Supported providers: Microsoft.EntityFrameworkCore.Sqlite, " +
        "Npgsql.EntityFrameworkCore.PostgreSQL.");

// (existing SQLite down path stays exactly as-is below)
```

#### Steps

- [ ] **1.** Read `src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs` to confirm the SQLite branch you're keeping.
- [ ] **2.** Replace the `Up` guard with the Postgres branch above, leaving the SQLite recreate code unchanged.
- [ ] **3.** Replace the `Down` guard with the Postgres branch above, leaving the SQLite down code unchanged.
- [ ] **4.** `dotnet build --configuration Release` — expect clean build, no new warnings.
- [ ] **5.** `dotnet test --configuration Release` — expect 56+ tests pass (no SQLite regression).
- [ ] **6.** `dotnet format --verify-no-changes` — expect clean exit.
- [ ] **7.** Commit:
  ```bash
  git add src/HookVault/Migrations/00000000000001_BytesBodyAndArrayHeaders.cs
  git commit -m "fix: support postgres in BytesBodyAndArrayHeaders migration"
  ```
  Body should mention: (a) Postgres users previously had to drop the database, (b) the cast is lossless because SQLite stored UTF-8 bytes as TEXT, (c) the SQLite path is unchanged.

**Success criterion:** A v0.2 Postgres database can be upgraded in-place to v1.0 by running `dotnet ef database update` against it, with no data loss. Verified end-to-end by P2.

---

### P2 — Postgres smoke job in CI

**File:** `.github/workflows/ci.yml`

Add a job called `postgres-smoke` that:

1. Boots the existing `docker-compose.postgres.yml`.
2. Waits for the `hookvault` service healthcheck to flip to `healthy` (compose already polls `/api/health`).
3. Hits `GET /api/health` from the runner and asserts HTTP 200 + JSON `status == "healthy"` + `database == "postgres"`.
4. Tears down with `docker compose -f docker-compose.postgres.yml down -v`.

The startup auto-migrate path in `Program.cs:221` runs Migration 1 against an empty Postgres database, so a clean boot exercises the P1 code path.

The compose file requires `HOOKVAULT_JWT_SECRET` and `POSTGRES_PASSWORD` (`:?` syntax fails fast otherwise). The CI step generates dummy values inline per run — **no real secrets in the workflow file**.

#### Steps

- [ ] **1.** Read `.github/workflows/ci.yml` to match its structure (runner OS, formatting).
- [ ] **2.** Read `docker-compose.postgres.yml` to confirm service names (`hookvault`, `db`), the `7777:8080` port mapping, and the healthcheck blocks on both services.
- [ ] **3.** Read `src/HookVault/Controllers/HealthController.cs` to verify the `database` field value is `"postgres"` (lowercase) when Npgsql is the provider. Adjust the `grep -q` assertion below if the value differs.
- [ ] **4.** Append the `postgres-smoke` job to `.github/workflows/ci.yml`. Place it as a sibling of `docker-build`, no `needs:` chain:

  ```yaml
    postgres-smoke:
      name: Postgres migration smoke test
      runs-on: ubuntu-latest
      steps:
        - uses: actions/checkout@v6

        - name: Generate dummy secrets
          run: |
            echo "HOOKVAULT_JWT_SECRET=$(openssl rand -hex 32)" >> $GITHUB_ENV
            echo "POSTGRES_PASSWORD=$(openssl rand -hex 16)" >> $GITHUB_ENV

        - name: Provide a config file for the container
          # docker-compose.postgres.yml mounts ./hookvault.json read-only.
          # The repo gitignores hookvault.json so users supply their own;
          # the example file works as a smoke-test stand-in.
          run: cp hookvault.example.json hookvault.json

        - name: Boot the Postgres stack
          run: docker compose -f docker-compose.postgres.yml up -d --build

        - name: Wait for hookvault healthcheck
          run: |
            for i in {1..30}; do
              status=$(docker inspect --format='{{.State.Health.Status}}' \
                $(docker compose -f docker-compose.postgres.yml ps -q hookvault))
              echo "attempt $i: $status"
              if [ "$status" = "healthy" ]; then exit 0; fi
              sleep 5
            done
            echo "hookvault never became healthy"
            docker compose -f docker-compose.postgres.yml logs hookvault
            exit 1

        - name: Assert /api/health reports postgres
          run: |
            body=$(curl -fsS http://localhost:7777/api/health)
            echo "$body"
            echo "$body" | grep -q '"database":"postgres"'
            echo "$body" | grep -q '"status":"healthy"'

        - name: Dump logs on failure
          if: failure()
          run: docker compose -f docker-compose.postgres.yml logs

        - name: Tear down
          if: always()
          run: docker compose -f docker-compose.postgres.yml down -v
  ```

- [ ] **5.** Verify YAML loads: `python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"`.
- [ ] **6.** Commit:
  ```bash
  git add .github/workflows/ci.yml
  git commit -m "ci: add postgres migration smoke job"
  ```

**Success criterion:** A push to the PR branch triggers `postgres-smoke`. The job boots both containers, the migration runs cleanly, `/api/health` returns `database: "postgres"`, and the job tears the stack down.

---

### P3 — CONTRIBUTING.md migration-ID note

**File:** `CONTRIBUTING.md`

Add a "Database migrations" section between "Code conventions" and "Commit style". The repo's migrations use zero-padded sequential IDs (`00000000000000_Initial`), not EF Core's default timestamp format. A contributor running `dotnet ef migrations add Foo` will produce a non-conformant filename, so the rename step must be documented.

#### Steps

- [ ] **1.** Read `CONTRIBUTING.md` to find the insertion point.
- [ ] **2.** Insert this section immediately before `## Commit style`:

  ````markdown
  ## Database migrations

  HookVault names migrations with **zero-padded sequential IDs**
  (`00000000000001_Foo`), not EF Core's default timestamp prefix
  (`20260517123045_Foo`). This keeps ordering deterministic across
  contributors in different timezones and matches the existing files in
  `src/HookVault/Migrations/`.

  When you add a new migration:

  ```bash
  dotnet ef migrations add YourMigrationName --project src/HookVault
  ```

  Then **rename** the generated `.cs` and `.Designer.cs` files so the prefix
  is `previous + 1`, zero-padded to 14 digits. Update the `[Migration("...")]`
  attribute inside both files to match the new prefix. The model snapshot
  (`HookVaultDbContextModelSnapshot.cs`) does not need to change.

  Migrations that change column types or shape **must** branch on
  `migrationBuilder.ActiveProvider` to support both SQLite and PostgreSQL.
  See `00000000000001_BytesBodyAndArrayHeaders.cs` for the canonical example.
  ````

- [ ] **3.** Commit:
  ```bash
  git add CONTRIBUTING.md
  git commit -m "docs: document migration id convention"
  ```

**Success criterion:** A new contributor following `CONTRIBUTING.md` can add a migration without breaking the sequential-ID convention.

---

## Dispatch groups

P1 and P3 are independent and can run in parallel. P2 should run **after** P1 commits so the CI job exercises the fixed migration on the first push.

| Group | Tasks | Subagent brief skills |
|---|---|---|
| 1 (parallel) | P1, P3 | `postgres-patterns`, `database-migrations`, `hookvault-codemap`, `hookvault-conventions`, `commit-style` |
| 2 (after P1) | P2 | `docker-patterns`, `hookvault-codemap`, `hookvault-conventions`, `commit-style` |

Every implementer brief MUST start with:

> "Before doing anything else, invoke `Skill(hookvault-spec)` and
> `Skill(hookvault-conventions)`. Do not edit code until both are loaded."

## Final verification (after all three tasks land)

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format --verify-no-changes
```

All three must pass locally. The `postgres-smoke` CI job gives the live Postgres verification.

## Review pass

Parallel:
- `webhook-security-reviewer` — confirm the migration changes don't touch ingest/HMAC/forwarding paths (they shouldn't); confirm the CI job doesn't leak secrets.
- `feature-dev:code-reviewer` briefed with `pr-review-checklist` — generic correctness + spec-alignment review.

## PR shape

**Title:** `feat: v1.0 postgres parity — migration fix, CI smoke, docs (PR 3 of 3)`

**Body skeleton:** see PR #1 (`#23`) and PR #2 (`#24`). Call out explicitly:

- v0.2 Postgres users can now upgrade in place without dropping the database.
- New CI job validates the migration end-to-end on every push.
- Migration-ID convention is now documented.

## Autonomous CI watch

After `gh pr create` returns, dispatch `loop-operator` per the `hookvault-ci-loop` skill template:

```
Agent(
  subagent_type="loop-operator",
  run_in_background=true,
  prompt="<per hookvault-ci-loop template, with PR URL and cycle cap 3>"
)
```

The main thread returns immediately; the loop will notify on green or escalate after 3 failed resolver cycles.

## Risks

1. **`convert_to` UTF-8 round-trip** — assumes existing `"Body"` text is valid UTF-8. SQLite stored UTF-8 bytes as TEXT, and ingest writes are UTF-8, so this holds for every body HookVault has ever persisted. If a v0.2 user has invalid-UTF-8 bytes (only via direct DB write), the cast fails — acceptable; document in PR body.
2. **`jsonb_each_text` on `NULL`/empty `"Headers"`** — guarded by the `WHERE` clause. Column is `NOT NULL`, but defensive guard is cheap.
3. **CI smoke flakiness** — the 30 × 5s healthcheck loop tolerates a slow first Postgres boot. If flake appears, bump to 60 attempts before bigger changes.
4. **`hookvault.example.json` content** — if it requires env vars beyond `HOOKVAULT_JWT_SECRET` / `POSTGRES_PASSWORD`, the CI step must set them too. Verify before committing P2.
5. **Down-migration data loss** — flattening `{"k":["v","v2"]}` back to `{"k":"v"}` drops `v2`. No current code path produces multi-value headers (ingest writes one value per key from `HttpRequest.Headers`), but the migration comment must call this out.

## Reviewer rubric

Same as PR #1/#2: `pr-review-checklist` + `webhook-security-reviewer`. Special attention on:

- Migration `Up` and `Down` are mirror images (each provider has both directions).
- The unknown-provider branch still throws (we don't silently no-op for, say, MySQL).
- The CI job tears down even on failure (`if: always()` on the down step).
- No secrets committed to the repo or workflow.
