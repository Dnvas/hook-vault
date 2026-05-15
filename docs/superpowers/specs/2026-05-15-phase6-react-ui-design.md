# HookVault Phase 6 — React UI Design

**Goal:** Add a split-pane React dashboard served from the existing single .NET container,
providing a live webhook event feed with per-event inspection and replay.

**Approved design decisions:**
- Layout: split pane — event list left, event detail right
- Theme: dark slate with indigo accents
- Detail panel: scrollable sections (Body → Headers → Validation → Forward), no tabs
- Auth: auto-generated startup URL using existing `JwtTokenGenerator`; `sessionStorage` fallback

---

## Architecture

`ui/` lives at the repo root alongside `src/` and `tests/`. It is a Vite + React 18
+ TypeScript project that produces a static build.

**Development:** Vite dev server (`:5173`) proxies `/api/*` to the .NET backend
(`:5000`). Developers run both concurrently (`dotnet run` + `npm run dev`).

**Production:** The Dockerfile gains a `node:20-alpine` build stage that runs
`npm ci && npm run build`, outputting to `ui/dist/`. The ASP.NET runtime stage
copies `ui/dist/` to `wwwroot/`. `Program.cs` gains `app.UseStaticFiles()` and
`app.MapFallbackToFile("index.html")` so the SPA is served at the root and all
unknown paths return `index.html` for client-side routing.

No change to `docker-compose.yml` — still single container, still port `7777:8080`.

---

## Tech Stack

| Layer | Choice |
|---|---|
| Build tool | Vite 5 |
| UI framework | React 18 + TypeScript |
| Data fetching | TanStack Query v5 |
| Styling | Tailwind CSS v3 |
| Linting | ESLint + `@typescript-eslint` |
| Formatting | Prettier |
| E2E testing | Playwright via MCP plugin |

No component library — Tailwind + custom components only. The `frontend-design:frontend-design`
skill MUST be invoked by any agent implementing UI components to ensure visual quality.

---

## New .NET Backend Additions

Four small additions to the existing backend. No existing service or controller is changed.

### 1. `EventNotifier` — `src/HookVault/Services/EventNotifier.cs`

Singleton `Channel<EventNotification>` wrapper. `IngestController` calls `Notify` after
saving an event. The SSE endpoint drains the reader.

```csharp
namespace HookVault.Services;

public sealed record EventNotification(Guid Id, string Provider, string Status);

public sealed class EventNotifier
{
    private readonly Channel<EventNotification> _channel =
        Channel.CreateUnbounded<EventNotification>(new() { SingleReader = false });

    public void Notify(EventNotification n) => _channel.Writer.TryWrite(n);
    public ChannelReader<EventNotification> Reader => _channel.Reader;
}
```

Registered as `Singleton` in `Program.cs`.

### 2. `GET /api/events/stream` — SSE endpoint

JWT-protected. Streams `text/event-stream` to connected clients. Each message is a
JSON-serialised `EventNotification`. Added to `EventsController` or a dedicated
`StreamController`.

The endpoint loops on `EventNotifier.Reader`, writing `data: {...}\n\n` per
notification until the client disconnects (`CancellationToken` cancelled).

**SSE token delivery:** `EventSource` cannot set `Authorization` headers. Pass token as
query param: `GET /api/events/stream?token=eyJ...`. The JWT middleware in `Program.cs`
must be configured to also read the token from the query string for this endpoint only:

```csharp
opts.Events = new JwtBearerEvents
{
    OnMessageReceived = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api/events/stream"))
            ctx.Token = ctx.Request.Query["token"];
        return Task.CompletedTask;
    }
};
```

### 3. Startup URL logging — `Program.cs`

After `var app = builder.Build()`, generate a 30-day token and log the URL:

```csharp
var uiToken = JwtTokenGenerator.Mint(jwtOptions, "ui", TimeSpan.FromDays(30));
app.Logger.LogInformation("HookVault UI → http://localhost:7777/?token={Token}", uiToken);
```

### 4. Static file serving — `Program.cs`

```csharp
app.UseStaticFiles();
// ... existing middleware ...
app.MapControllers();
app.MapFallbackToFile("index.html");
```

`MapFallbackToFile` must come after `MapControllers` so API routes take priority.

---

## UI File Structure

```
ui/
├── src/
│   ├── api/
│   │   └── client.ts           — fetch wrapper injecting Bearer token
│   ├── hooks/
│   │   ├── useEvents.ts        — TanStack Query: GET /api/events (+ 10s poll fallback)
│   │   ├── useEvent.ts         — TanStack Query: GET /api/events/{id}
│   │   └── useEventStream.ts   — EventSource hook for GET /api/events/stream
│   ├── components/
│   │   ├── TokenGate.tsx       — paste-token input shown when no token in sessionStorage
│   │   ├── EventList.tsx       — left panel: filterable live feed
│   │   ├── EventRow.tsx        — single row in the event list
│   │   ├── EventDetail.tsx     — right panel: composes the four scrollable sections
│   │   ├── BodySection.tsx     — pretty-printed JSON body
│   │   ├── HeadersSection.tsx  — request headers key/value table
│   │   ├── ValidationSection.tsx — HMAC debug display (computed vs received digest)
│   │   └── ForwardSection.tsx  — forward result + replay button
│   ├── App.tsx                 — root: token bootstrap, split pane shell
│   ├── main.tsx                — Vite entry point
│   └── types.ts                — TypeScript types matching API response shapes
├── index.html
├── vite.config.ts              — proxy /api/* → :5000 in dev
├── tailwind.config.ts
├── tsconfig.json
├── package.json
└── .eslintrc.cjs
```

---

## Component Behaviour

### App.tsx — token bootstrap

1. On mount: read `?token=` from URL → store in `sessionStorage` → `history.replaceState`
   to strip the token from the address bar
2. Check `sessionStorage` for existing token
3. No token → render `<TokenGate />`
4. Token present → render split pane with `<EventList />` and `<EventDetail />`

Filter state (provider, status) lives in `App.tsx` and flows down as props.

### EventList.tsx — left panel

- Provider filter pills populated from `GET /api/health` (`providers` array)
- Status filter: All / Forwarded / Failed / Replaying
- Events from `useEvents(filters)` (TanStack Query, initial load)
- `useEventStream` receives SSE notifications → calls
  `queryClient.invalidateQueries(['events'])` → list refetches
- New rows animate in at the top (CSS `transition`)
- Selected row highlighted with indigo left border (`border-l-2 border-indigo-400`)
- Empty state when no events match filters

### EventDetail.tsx — right panel

Rendered when a row is selected. Fetches full detail via `useEvent(id)`.

Panel header: `{provider} · POST {path} · {timestamp}` + `↺ Replay` button

Sections stacked vertically, each with a collapsible header:

1. **Body** — `JSON.parse` → `<pre>` pretty-print; raw string fallback
2. **Headers** — `<table>` of original request headers (key / value)
3. **Validation** — green/red badge; when configured shows: algorithm, payload used,
   computed digest, received digest, extracted timestamp. When `validation` is null:
   "No validation configured"
4. **Forward** — status code badge, destination URL, error message (if failed),
   replay count, last replay time

**Replay button:** `POST /api/events/{id}/replay` → `queryClient.invalidateQueries(['events', id])`

### TokenGate.tsx

Centred card with a password input. On submit, validates token is non-empty, stores in
`sessionStorage`, and triggers re-render. No API call — the first API call will return
401 if the token is invalid, which redirects back to `TokenGate`.

---

## Real-time Strategy

`useEventStream` opens `EventSource` on `GET /api/events/stream?token={token}`. On each
`message` event, it calls `queryClient.invalidateQueries(['events'])` which causes
TanStack Query to refetch the events list with the current filters applied.

If SSE drops, `EventSource` auto-reconnects (built-in browser behaviour). TanStack
Query also polls as a 10-second fallback (`refetchInterval: 10_000`).

The SSE stream sends minimal payloads — React always fetches full data via the existing
paginated API. The SSE is a cache-invalidation signal only.

---

## Auth Flow

1. Container starts → `Program.cs` generates 30-day token, logs
   `HookVault UI → http://localhost:7777/?token=eyJ...`
2. Developer clicks URL → React reads `?token=`, stores in `sessionStorage`, strips from URL
3. All API calls: `Authorization: Bearer <token>` header injected by `client.ts`
4. SSE stream: token passed as `?token=` query param (EventSource limitation)
5. Session expires or token cleared → `TokenGate` shown automatically on next 401
6. `generate-token` CLI and Swagger flows unchanged

---

## Docker Build Changes

`Dockerfile` gains a Node build stage before the existing .NET build stage:

```dockerfile
# Stage 1: build React UI
FROM node:20-alpine AS ui-build
WORKDIR /ui
COPY ui/package*.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

# Stage 2: build .NET app (existing — unchanged)
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY --from=ui-build /ui/dist ./src/HookVault/wwwroot/
# ... rest of existing build stage ...
```

`wwwroot/` is included in the `dotnet publish` output automatically.

---

## E2E Testing Strategy

Playwright is already available via the MCP plugin (`mcp__plugin_playwright_playwright__*`
tools). Tests live in `tests/e2e/` and run against a real local container.

| File | Scenario |
|---|---|
| `auth.spec.ts` | No token → TokenGate shown; valid `?token=` → dashboard loads |
| `live-feed.spec.ts` | POST webhook → event appears in list within 3s, no refresh |
| `event-detail.spec.ts` | Click event → all four sections visible with correct data |
| `validation.spec.ts` | Bad-signature webhook → red badge, computed/received digest shown |
| `replay.spec.ts` | Replay button → status transitions ForwardFailed → Replaying → Forwarded |
| `bulk-replay.spec.ts` | Bulk replay → all failed events enqueued, counts update |
| `filters.spec.ts` | Provider and status filters narrow the event list correctly |

Agents implementing Phase 6 tasks MUST use the Playwright MCP tools to verify UI
behaviour before declaring any task complete. TypeScript compilation passing is not
sufficient — the browser interaction must be verified.

---

## Phase 6 Build Order

### Sub-phase A — Backend additions

1. `EventNotifier` singleton + `EventNotification` record
2. `GET /api/events/stream` SSE endpoint + query-string JWT config
3. Startup URL logging in `Program.cs`
4. `app.UseStaticFiles()` + `app.MapFallbackToFile` in `Program.cs`
5. xUnit integration test: SSE endpoint emits notification when event is ingested

### Sub-phase B — React scaffold + auth

6. `ui/` scaffold: `npm create vite@latest ui -- --template react-ts`
7. Tailwind, ESLint, Prettier config; `vite.config.ts` proxy
8. `types.ts` matching API response shapes
9. `client.ts` fetch wrapper
10. `TokenGate.tsx`
11. `App.tsx` token bootstrap + split pane shell
12. Dockerfile multi-stage update; verify `docker build` passes CI

### Sub-phase C — Event list + live feed

13. `useEvents` TanStack Query hook
14. `useEventStream` EventSource hook
15. `EventRow.tsx`
16. `EventList.tsx` with provider + status filter pills
17. E2E: `live-feed.spec.ts` + `filters.spec.ts`

### Sub-phase D — Event detail + replay

18. `useEvent` TanStack Query hook
19. `BodySection`, `HeadersSection`, `ValidationSection`, `ForwardSection`
20. `EventDetail.tsx` composing all sections + replay button
21. E2E: `event-detail.spec.ts`, `validation.spec.ts`, `replay.spec.ts`, `bulk-replay.spec.ts`

### Sub-phase E — Visual polish + CI

22. `frontend-design:frontend-design` skill pass over all components
23. Full E2E suite green
24. CI update: add `npm ci && npm run build` step in the Docker build job
