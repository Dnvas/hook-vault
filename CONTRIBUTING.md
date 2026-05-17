# Contributing to HookVault

Thanks for your interest in contributing! HookVault is an open-source developer
tool and welcomes bug reports, documentation improvements, new provider example
configs, and well-scoped feature additions.

## What we accept

- **Bug fixes** — clear description of the problem and steps to reproduce it
- **Provider example configs** — new files in `examples/` for services not yet
  covered
- **Documentation improvements** — corrections, clarity, better examples
- **Small, well-scoped features** aligned with the product spec

## What we don't accept

- **Hardcoded provider logic in application code.** HookVault's core principle
  is that all provider behaviour lives in `hookvault.json`, never in the binary.
  If a new provider requires changes to `SignatureValidator`, `IngestController`,
  or any service, the approach should be reconsidered.
- **Features that require external services** beyond SQLite/PostgreSQL.
- **Breaking changes** to the `hookvault.json` config schema without a
  compelling, documented migration path.

## Development setup

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download), Docker
(optional — for PostgreSQL integration tests)

```bash
# Clone
git clone https://github.com/dnvas/hook-vault.git
cd hook-vault

# Build
dotnet build

# Run all tests
dotnet test --configuration Release

# Check formatting (must pass in CI)
dotnet format --verify-no-changes

# Fix formatting
dotnet format
```

## Code conventions

- **File-scoped namespaces** — no braced namespace blocks
- **Primary constructors** for dependency injection — no private readonly field
  boilerplate
- **Nullable enabled** — `string?` for nullable reference types, never
  `string` for values that can be absent
- **`record`** for immutable result types; **`sealed`** for new concrete classes
- **DI lifetimes:** `Singleton` for stateless app-wide services, `Scoped` for
  `DbContext` and anything depending on it, `Transient` for cheap stateless
  helpers
- **`IHttpClientFactory` always** — never `new HttpClient()` directly
- **Constant-time comparison** for any signature or secret comparison —
  `CryptographicOperations.FixedTimeEquals`, never `==` or `string.Equals`
- **EF Core:** async LINQ only; add database indexes on filter columns
- **Tests:** real SQLite and real HMAC inputs — no mocks for crypto or the
  database layer

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

## Commit style

```
type: short subject in lowercase, no trailing period
```

Types: `feat`, `fix`, `chore`, `test`, `ci`, `docs`, `refactor`

The commit body explains *why* the change was made, not *what* — keep lines
wrapped at 72 characters. No AI/assistant attribution in commits, PR
descriptions, or code comments.

## Branch naming

| Prefix | Use for |
|---|---|
| `feat/<slug>` | New features |
| `fix/<slug>` | Bug fixes |
| `docs/<slug>` | Documentation only |
| `chore/<slug>` | Tooling, config |
| `ci/<slug>` | CI/CD changes |
| `test/<slug>` | Test-only changes |
| `refactor/<slug>` | Refactors with no behaviour change |

## Submitting a pull request

1. Fork the repo and create a branch using the naming convention above.
2. Make your changes with tests where applicable.
3. Ensure all of the following pass locally before opening a PR:
   ```bash
   dotnet build --configuration Release
   dotnet test --configuration Release
   dotnet format --verify-no-changes
   ```
4. Open a PR with a clear description of what changed and why.
5. All CI checks must pass: build, tests, format, CodeQL analysis, and Docker
   image build.

For significant changes, open an issue first to discuss the approach — this
avoids wasted effort on PRs that won't be merged.
