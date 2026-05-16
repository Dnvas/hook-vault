---
name: hookvault-conventions
description: Canonical C# / ASP.NET Core / EF Core conventions used in the HookVault codebase. Preload into any agent that writes or reviews code in this repo.
user-invocable: false
---

# HookVault code conventions

Authoritative reference for how code in this repo is written.

## C# language style

- **File-scoped namespaces.** `namespace HookVault.X;` — no braces.
- **Primary constructors** for DI: `public class Foo(IBar bar)`. Do not write
  a constructor that just copies parameters into fields.
- **`Nullable` is enabled.** Use `string?` for nullables. Don't suppress with `!`
  unless you can prove it can't be null.
- **`record` for immutable result types** (e.g. `SignatureValidationResult`).
  Use init-only properties (`{ get; init; }`).
- **`sealed` by default** on new concrete classes unless you have a reason for
  inheritance.
- **`required` / `init`** for properties that must be set at construction.
- **Collection expressions** (`[]`, `[..]`) over `new List<T>()` / `Array.Empty<T>()`.
- **Target-typed `new()`** when the type is obvious from context.

## Dependency injection

- **Singleton** — stateless services that hold app-wide config or pooled resources.
  Example: `HookVaultOptions`.
- **Scoped** — one instance per HTTP request. Anything that depends on `DbContext`
  must also be Scoped. Example: `EventRepository`, `EventForwarder`.
- **Transient** — cheap stateless helpers, new instance each time. Example:
  `SignatureValidator`.
- **`IHttpClientFactory` always.** Register with `AddHttpClient("name")`, resolve
  via `factory.CreateClient("name")`. Never `new HttpClient()` — it leaks sockets.

## BackgroundService and scoped services

`BackgroundService` is registered as a singleton (via `AddHostedService`), but
`DbContext`, `EventRepository`, and `EventForwarder` are scoped. A singleton cannot
directly depend on a scoped service — inject `IServiceScopeFactory` instead and create
a scope per unit of work:

```csharp
using var scope = scopeFactory.CreateScope();
var repo = scope.ServiceProvider.GetRequiredService<EventRepository>();
```

When the final DB write in a background operation must not be cancelled on shutdown
(to avoid orphaned in-progress status rows), use `CancellationToken.None` for that
one call only — not for the entire operation.

## Async

- All I/O methods are `async Task<T>` or `async Task`.
- Pass `CancellationToken ct` through every async chain. ASP.NET Core
  provides one per request — accept it in controller actions and pass it down.
- Don't use `.Result` or `.Wait()`. Don't `async void` outside event handlers.
- `ConfigureAwait(false)` is **not** required in ASP.NET Core (no sync context).

## EF Core

- Async LINQ only: `ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`. Never the
  sync variants in request paths.
- Add indexes on columns used in filter / order clauses
  (`HasIndex(e => e.Status)`).
- Store enums as strings (`HasConversion<string>()`) so the DB is human-readable.
- Never build SQL with string concatenation. Use LINQ or
  `FromSqlInterpolated` (parameterised).

## Cryptography / secrets

- Secrets only from environment variables. **Never** put a secret value in
  config files.
- Constant-time compare with `CryptographicOperations.FixedTimeEquals`.
  Never `==` or `string.Equals` on signatures / tokens.
- Use built-in `HMACSHA256.HashData(key, data)` / `HMACSHA512.HashData(...)`.
  Don't instantiate the disposable form when you only hash once.

## Logging

- Inject `ILogger<T>` via primary constructor.
- Structured logging only: `logger.LogInformation("Did X for {Id}", id)` — not
  `$"Did X for {id}"` (the structured form is searchable / parseable).
- Levels: `Information` for normal flow, `Warning` for recoverable issues,
  `Error` for failures that require attention. No `Debug` in production paths.

## Controllers

- Inherit from `ControllerBase`, not `Controller` (no view support needed).
- `[ApiController]` attribute on every controller — gives automatic model
  validation + `[FromBody]` inference + ProblemDetails responses.
- Use attribute routing (`[HttpPost("api/...")]`), not conventional routing.
- Return `IActionResult` (`Ok(...)`, `NotFound(...)`, `Accepted(...)`).

## Testing (xUnit)

- `[Fact]` for single tests, `[Theory]` + `[InlineData]` for parameterised.
- Real SQLite (in-memory or temp file) — **do not mock `DbContext`**.
- Real HMAC inputs — **do not mock cryptography**.
- Arrange / Act / Assert structure, no comments needed if it reads cleanly.
- Test names: `MethodOrScenario_condition_expectedOutcome`.

**SQLite in-memory with multiple scopes:** SQLite `:memory:` databases are
per-connection. When a test needs multiple `DbContext` instances (e.g. a seed scope
and a worker scope), open one `SqliteConnection`, keep it open for the test lifetime,
and pass it to every `DbContextOptions` via `UseSqlite(connection)`. Dispose the
connection in `DisposeAsync`. Each scope gets its own `DbContext` but they all share
the same in-memory database.

**Controlling `HttpClient` in integration tests:** Subclass `HttpMessageHandler`,
override `SendAsync` to return a pre-programmed response sequence. Register via
`AddHttpClient("forwarder").ConfigurePrimaryHttpMessageHandler(() => handler)`.
Use `Interlocked.Increment` for thread-safe call counting in the handler.

## React / TypeScript (Phase 6 UI — `ui/` directory)

**IMPORTANT:** Any agent implementing React components MUST invoke
`Skill(frontend-design:frontend-design)` before writing UI code. This skill
enforces visual quality standards and must be loaded first.

### Component conventions

- **Functional components only** — no class components.
- **One component per file**, named to match the file (`EventList.tsx` → `export function EventList`).
- **Props typed inline** with an interface: `interface EventListProps { ... }`.
- **`const` arrow functions** for callbacks inside components; named function declarations
  for the component export itself (`export function EventList(...)`).
- **No `any`** — use `unknown` and narrow, or define the type properly.
- **`types.ts`** is the single source of truth for API response shapes. Components
  import from there; they do not define their own API types.

### Data fetching

- **TanStack Query v5** for all API calls — no raw `fetch` calls in components.
- `useEvents`, `useEvent` hooks live in `src/hooks/`. Each wraps a single query key.
- All API calls go through `src/api/client.ts` which injects `Authorization: Bearer <token>`.
- On 401 response, `client.ts` clears `sessionStorage` and reloads the page (shows `TokenGate`).

### Real-time

- `useEventStream` opens `EventSource` on `GET /api/events/stream?token={token}`.
- On SSE message: invalidate **both** `['events']` (list) and `['event']` (detail) —
  missing the detail invalidation leaves the detail panel stale after replay.
- TanStack Query `refetchInterval: 10_000` as fallback if SSE drops.
- **Hook stability is critical.** `useEventStream`'s `useEffect` depends on
  `[onNotification]`. If `onNotification` is a new function every render, the
  effect re-runs every render (hundreds of SSE reconnections). Always return a
  stable callback from `useInvalidateEvents` using `useCallback([qc])`:

  ```ts
  export function useInvalidateEvents() {
    const qc = useQueryClient()
    return useCallback(() => {
      qc.invalidateQueries({ queryKey: ['events'] })
      qc.invalidateQueries({ queryKey: ['event'] })
    }, [qc])
  }
  ```

### API response field names

- `GET /api/events` returns `{ items: EventSummary[], total, limit, offset }`.
  The C# record property is `Items`; ASP.NET Core's camelCase policy serializes
  it to `items`. **Do not rename to `events`** — no backend change needed.
- `validationDetails` is JSON-encoded inside `IngestController` using
  `JsonNamingPolicy.CamelCase`. TypeScript types must use camelCase keys
  (`algorithmUsed`, `payloadUsed`, `computedSignature`, `receivedSignature`,
  `extractedTimestamp`, `error`). The outer `isValid` field maps to
  `ValidationDetails.isValid` in `types.ts`.

### Styling

- **Tailwind CSS v3** utility classes only. No custom CSS files, no inline `style`
  props (except for dynamic values that can't be expressed as Tailwind classes).
- Colour palette: `slate-900` background, `slate-800` panel, `indigo-400` accent,
  `green-400` success, `red-400` failure, `amber-400` in-progress.
- Dark mode only — no light mode toggle needed.

### Auth / token

- Token stored in `sessionStorage` (not `localStorage`).
- `App.tsx` reads `?token=` on mount → stores → strips from URL via `history.replaceState`.
- `<TokenGate />` shown when `sessionStorage` has no token.

### E2E testing

- Tests live in `tests/e2e/` and use Playwright via the MCP plugin.
- Agents MUST verify UI interactions via Playwright before declaring any task complete.
  `npm run build` passing is necessary but not sufficient.

## What NOT to do

- No comments that explain *what* obvious code does. Only *why* if non-obvious.
- No commented-out code. Delete it. Git remembers.
- No defensive checks for impossible conditions (e.g. null-checking a `[Required]`
  param after `[ApiController]` validation).
- No backwards-compatibility shims when there are zero existing callers.
- No `var` for non-obvious literal types (e.g. `var x = SomeMethod();` is fine,
  `var i = 5` is debatable). Prefer `var` when the type is obvious from RHS.
- No abstractions for hypothetical future needs. YAGNI.
