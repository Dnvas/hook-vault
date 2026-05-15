# Phase 6 — React UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Before doing anything:** invoke `Skill(hookvault-spec)` and `Skill(hookvault-conventions)` before writing any code.
>
> **For UI tasks (Sub-phase B onwards):** invoke `Skill(frontend-design:frontend-design)` before writing any React component. This is mandatory, not optional.

**Goal:** Add a live split-pane React dashboard served from the existing single .NET container, showing a real-time webhook event feed with per-event inspection and replay.

**Architecture:** Vite + React 18 + TypeScript in `ui/` at repo root; built to static files and served by ASP.NET Core's `UseStaticFiles`. Real-time updates via a new SSE endpoint (`GET /api/events/stream`) that pushes notifications when events are ingested; React invalidates TanStack Query cache on each notification. Auth uses existing `JwtTokenGenerator` — startup logs a clickable URL with a 30-day token.

**Tech Stack:** .NET 8 ASP.NET Core (backend additions), Vite 5, React 18, TypeScript, TanStack Query v5, Tailwind CSS v3, Playwright MCP (E2E).

---

## File map

### New .NET files
- `src/HookVault/Services/EventNotifier.cs` — singleton Channel wrapper + EventNotification record
- (SSE endpoint added to `src/HookVault/Controllers/EventsController.cs`)

### Modified .NET files
- `src/HookVault/Controllers/IngestController.cs` — call `EventNotifier.Notify` after saving
- `src/HookVault/Controllers/EventsController.cs` — add `GET /api/events/stream` action
- `src/HookVault/Program.cs` — register EventNotifier, configure JWT query-string, add static files + fallback, log startup URL
- `Dockerfile` — add `node:20-alpine` UI build stage

### New test files
- `tests/HookVault.Tests/EventStreamTests.cs` — SSE endpoint integration test

### New UI files (`ui/`)
- `ui/src/types.ts` — TypeScript types matching API shapes
- `ui/src/api/client.ts` — fetch wrapper injecting Bearer token
- `ui/src/hooks/useEvents.ts` — TanStack Query: GET /api/events
- `ui/src/hooks/useEvent.ts` — TanStack Query: GET /api/events/{id}
- `ui/src/hooks/useEventStream.ts` — EventSource hook for SSE
- `ui/src/components/TokenGate.tsx`
- `ui/src/components/EventRow.tsx`
- `ui/src/components/EventList.tsx`
- `ui/src/components/BodySection.tsx`
- `ui/src/components/HeadersSection.tsx`
- `ui/src/components/ValidationSection.tsx`
- `ui/src/components/ForwardSection.tsx`
- `ui/src/components/EventDetail.tsx`
- `ui/src/App.tsx`
- `ui/src/main.tsx`
- `ui/index.html`
- `ui/vite.config.ts`
- `ui/tailwind.config.ts`
- `ui/tsconfig.json`
- `ui/package.json`
- `ui/.eslintrc.cjs`
- `ui/.prettierrc`

### New E2E files
- `tests/e2e/auth.spec.ts`
- `tests/e2e/live-feed.spec.ts`
- `tests/e2e/event-detail.spec.ts`
- `tests/e2e/replay.spec.ts`
- `tests/e2e/filters.spec.ts`

---

## Sub-phase A — Backend additions

### Task 1: EventNotifier service

**Files:**
- Create: `src/HookVault/Services/EventNotifier.cs`

- [ ] **Step 1: Create EventNotifier.cs**

```csharp
using System.Threading.Channels;

namespace HookVault.Services;

public sealed record EventNotification(Guid Id, string Provider, string Status);

public sealed class EventNotifier
{
    private readonly Channel<EventNotification> _channel =
        Channel.CreateUnbounded<EventNotification>(
            new UnboundedChannelOptions { SingleReader = false });

    public void Notify(EventNotification notification) =>
        _channel.Writer.TryWrite(notification);

    public ChannelReader<EventNotification> Reader => _channel.Reader;
}
```

- [ ] **Step 2: Register in Program.cs**

In `src/HookVault/Program.cs`, after the `ReplayQueue` registration line, add:

```csharp
builder.Services.AddSingleton<EventNotifier>();
```

- [ ] **Step 3: Verify build**

```bash
dotnet build --configuration Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/Services/EventNotifier.cs src/HookVault/Program.cs
git commit -m "feat: add EventNotifier singleton for SSE fanout"
```

---

### Task 2: SSE endpoint + query-string JWT

**Files:**
- Modify: `src/HookVault/Controllers/EventsController.cs` — add stream action
- Modify: `src/HookVault/Program.cs` — configure JWT query-string for SSE route

- [ ] **Step 1: Add stream action to EventsController**

Read `src/HookVault/Controllers/EventsController.cs` first to find the correct insertion point. Add this action at the end of the controller class, before the closing `}`:

```csharp
[HttpGet("stream")]
public async Task Stream([FromServices] EventNotifier notifier, CancellationToken ct)
{
    Response.Headers.Append("Content-Type", "text/event-stream");
    Response.Headers.Append("Cache-Control", "no-cache");
    Response.Headers.Append("X-Accel-Buffering", "no");

    await foreach (var notification in notifier.Reader.ReadAllAsync(ct))
    {
        var data = System.Text.Json.JsonSerializer.Serialize(notification,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        await Response.WriteAsync($"data: {data}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
```

- [ ] **Step 2: Configure JWT to also accept query-string token for the SSE route**

In `src/HookVault/Program.cs`, find the `.AddJwtBearer(opts =>` block and add an `Events` property inside it, after the `TokenValidationParameters` assignment:

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

- [ ] **Step 3: Wire IngestController to notify**

Read `src/HookVault/Controllers/IngestController.cs`. Add `EventNotifier notifier` to the primary constructor parameters. After the event is saved to the database (after `await _repository.AddAsync(webhookEvent, ct)`), add:

```csharp
_notifier.Notify(new EventNotification(webhookEvent.Id, provider.Name, webhookEvent.Status.ToString()));
```

You will also need to add a `private readonly EventNotifier _notifier;` field assignment in the primary constructor, or use the primary constructor parameter directly (whichever pattern the file already uses).

- [ ] **Step 4: Build and verify**

```bash
dotnet build --configuration Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/HookVault/Controllers/EventsController.cs \
        src/HookVault/Controllers/IngestController.cs \
        src/HookVault/Program.cs
git commit -m "feat: add SSE stream endpoint and wire IngestController notifications"
```

---

### Task 3: Write integration test for SSE endpoint

**Files:**
- Create: `tests/HookVault.Tests/EventStreamTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System.Net.Http.Headers;
using HookVault.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

public class EventStreamTests(HookVaultWebApplicationFactory factory)
    : IClassFixture<HookVaultWebApplicationFactory>
{
    [Fact]
    public async Task Stream_EmitsNotification_WhenEventIngested()
    {
        var token = factory.GenerateToken();
        var client = factory.CreateClient();

        // Start reading the SSE stream in the background
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var streamRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/events/stream?token={token}");

        var streamResponse = await client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        streamResponse.EnsureSuccessStatusCode();

        var readTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                await streamResponse.Content.ReadAsStreamAsync());
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line?.StartsWith("data:") == true)
                    return line;
            }
            return null;
        }, cts.Token);

        // Give the stream reader a moment to connect
        await Task.Delay(200, cts.Token);

        // Trigger an event via the ingest endpoint
        var body = """{"type":"test"}""";
        var ingest = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/stripe")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        ingest.Headers.Add("stripe-signature", "t=1,v1=invalid");
        await client.SendAsync(ingest, cts.Token);

        var line = await readTask;
        Assert.NotNull(line);
        Assert.Contains("stripe", line, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Check HookVaultWebApplicationFactory for GenerateToken helper**

Read `tests/HookVault.Tests/HookVaultWebApplicationFactory.cs`. If `GenerateToken()` doesn't exist, add it:

```csharp
public string GenerateToken(string subject = "test") =>
    Auth.JwtTokenGenerator.Mint(
        Services.GetRequiredService<Auth.JwtOptions>(),
        subject,
        TimeSpan.FromHours(1));
```

- [ ] **Step 3: Run the test**

```bash
dotnet test tests/HookVault.Tests/HookVault.Tests.csproj \
  --filter "EventStreamTests" --configuration Release
```

Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 4: Commit**

```bash
git add tests/HookVault.Tests/EventStreamTests.cs \
        tests/HookVault.Tests/HookVaultWebApplicationFactory.cs
git commit -m "test: add SSE stream integration test"
```

---

### Task 4: Startup URL log + static file serving

**Files:**
- Modify: `src/HookVault/Program.cs`

- [ ] **Step 1: Add startup URL logging**

In `src/HookVault/Program.cs`, after `var app = builder.Build();` and before the `using (var scope...)` block, add:

```csharp
var uiToken = JwtTokenGenerator.Mint(jwtOptions, "ui", TimeSpan.FromDays(30));
app.Logger.LogInformation(
    "HookVault UI → http://localhost:7777/?token={Token}", uiToken);
```

- [ ] **Step 2: Add static files and SPA fallback**

After `app.UseMiddleware<RawBodyMiddleware>();` and before `app.UseAuthentication();`, add:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();
```

After `app.MapControllers();`, add:

```csharp
app.MapFallbackToFile("index.html");
```

- [ ] **Step 3: Build and run all tests**

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

Expected: all existing tests still pass (57 or more).

- [ ] **Step 4: Commit**

```bash
git add src/HookVault/Program.cs
git commit -m "feat: add startup UI URL log and static file serving"
```

---

## Sub-phase B — React scaffold + auth

> **REQUIRED:** Before writing any component in this sub-phase, invoke `Skill(frontend-design:frontend-design)` and follow its guidance for visual quality.

### Task 5: Scaffold Vite + React project

**Files:**
- Create: `ui/` directory and all scaffold files

- [ ] **Step 1: Scaffold Vite project**

From the repo root:

```bash
npm create vite@latest ui -- --template react-ts
cd ui
npm install
npm install -D tailwindcss postcss autoprefixer prettier eslint @typescript-eslint/eslint-plugin @typescript-eslint/parser eslint-plugin-react-hooks
npm install @tanstack/react-query
npx tailwindcss init -p
```

- [ ] **Step 2: Configure Tailwind**

Replace the contents of `ui/tailwind.config.ts`:

```ts
import type { Config } from 'tailwindcss'

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        slate: { 850: '#0f1729' },
      },
    },
  },
  plugins: [],
} satisfies Config
```

Replace `ui/src/index.css` with:

```css
@tailwind base;
@tailwind components;
@tailwind utilities;
```

- [ ] **Step 3: Configure Vite proxy**

Replace `ui/vite.config.ts`:

```ts
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
})
```

- [ ] **Step 4: Add Prettier config**

Create `ui/.prettierrc`:

```json
{
  "semi": false,
  "singleQuote": true,
  "trailingComma": "all",
  "printWidth": 100
}
```

- [ ] **Step 5: Add ESLint config**

Create `ui/.eslintrc.cjs`:

```js
module.exports = {
  root: true,
  env: { browser: true, es2020: true },
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:react-hooks/recommended',
  ],
  ignorePatterns: ['dist', '.eslintrc.cjs'],
  parser: '@typescript-eslint/parser',
  rules: {
    '@typescript-eslint/no-explicit-any': 'error',
  },
}
```

- [ ] **Step 6: Add lint + format scripts to package.json**

In `ui/package.json`, add to `"scripts"`:

```json
"lint": "eslint . --ext ts,tsx --report-unused-disable-directives --max-warnings 0",
"format:check": "prettier --check src"
```

- [ ] **Step 7: Verify scaffold builds**

```bash
cd ui && npm run build
```

Expected: `dist/` produced, no errors.

- [ ] **Step 8: Commit**

```bash
cd ..
git add ui/
git commit -m "feat: scaffold Vite React TypeScript UI project"
```

---

### Task 6: API types and client

**Files:**
- Create: `ui/src/types.ts`
- Create: `ui/src/api/client.ts`

- [ ] **Step 1: Create types.ts**

```ts
export type EventStatus =
  | 'Received'
  | 'Forwarding'
  | 'Forwarded'
  | 'ForwardFailed'
  | 'Replaying'
  | 'ReplayFailed'

export interface EventSummary {
  id: string
  provider: string
  path: string
  receivedAt: string
  status: EventStatus
  signatureValid: boolean | null
  forwardStatusCode: number | null
  replayCount: number
}

export interface EventDetail extends EventSummary {
  headers: Record<string, string>
  body: string
  forwardUrl: string
  forwardedAt: string | null
  forwardError: string | null
  lastReplayAt: string | null
  lastError: string | null
  validationDetails: ValidationDetails | null
}

export interface ValidationDetails {
  isValid: boolean
  algorithmUsed: string
  computedSignature: string
  receivedSignature: string
  payloadUsed: string
  extractedTimestamp: string | null
}

export interface ListEventsResponse {
  events: EventSummary[]
  total: number
  limit: number
  offset: number
}

export interface HealthResponse {
  status: string
  version: string
  providers: string[]
  database: string
  eventCount: number
  oldestEvent: string | null
}

export interface EventNotification {
  id: string
  provider: string
  status: string
}
```

- [ ] **Step 2: Create api/client.ts**

```ts
const TOKEN_KEY = 'hv_token'

export function getToken(): string | null {
  return sessionStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string): void {
  sessionStorage.setItem(TOKEN_KEY, token)
}

export function clearToken(): void {
  sessionStorage.removeItem(TOKEN_KEY)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken()
  const res = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  })

  if (res.status === 401) {
    clearToken()
    window.location.reload()
    throw new Error('Unauthorized')
  }

  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }

  return res.json() as Promise<T>
}

export const api = {
  getHealth: () => request<import('./types').HealthResponse>('/api/health'),

  listEvents: (params: {
    provider?: string
    status?: string
    limit?: number
    offset?: number
  }) => {
    const q = new URLSearchParams()
    if (params.provider) q.set('provider', params.provider)
    if (params.status) q.set('status', params.status)
    if (params.limit != null) q.set('limit', String(params.limit))
    if (params.offset != null) q.set('offset', String(params.offset))
    return request<import('./types').ListEventsResponse>(`/api/events?${q}`)
  },

  getEvent: (id: string) =>
    request<import('./types').EventDetail>(`/api/events/${id}`),

  replayEvent: (id: string) =>
    request<unknown>(`/api/events/${id}/replay`, { method: 'POST' }),

  replayFailed: () =>
    request<unknown>('/api/events/replay-failed', { method: 'POST' }),

  deleteEvents: (provider?: string) => {
    const q = provider ? `?provider=${provider}` : ''
    return request<unknown>(`/api/events${q}`, { method: 'DELETE' })
  },
}
```

- [ ] **Step 3: Verify TypeScript compiles**

```bash
cd ui && npx tsc --noEmit
```

Expected: no errors.

- [ ] **Step 4: Commit**

```bash
cd ..
git add ui/src/types.ts ui/src/api/client.ts
git commit -m "feat: add API types and client wrapper"
```

---

### Task 7: TanStack Query hooks

**Files:**
- Create: `ui/src/hooks/useEvents.ts`
- Create: `ui/src/hooks/useEvent.ts`
- Create: `ui/src/hooks/useEventStream.ts`

- [ ] **Step 1: Create useEvents.ts**

```ts
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'

export function useEvents(filters: { provider?: string; status?: string }) {
  return useQuery({
    queryKey: ['events', filters],
    queryFn: () => api.listEvents({ ...filters, limit: 100, offset: 0 }),
    refetchInterval: 10_000,
  })
}

export function useInvalidateEvents() {
  const qc = useQueryClient()
  return () => qc.invalidateQueries({ queryKey: ['events'] })
}
```

- [ ] **Step 2: Create useEvent.ts**

```ts
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'

export function useEvent(id: string | null) {
  return useQuery({
    queryKey: ['event', id],
    queryFn: () => api.getEvent(id!),
    enabled: id != null,
  })
}

export function useInvalidateEvent() {
  const qc = useQueryClient()
  return (id: string) => qc.invalidateQueries({ queryKey: ['event', id] })
}
```

- [ ] **Step 3: Create useEventStream.ts**

```ts
import { useEffect } from 'react'
import { getToken } from '../api/client'

export function useEventStream(onNotification: () => void) {
  useEffect(() => {
    const token = getToken()
    if (!token) return

    const url = `/api/events/stream?token=${encodeURIComponent(token)}`
    const es = new EventSource(url)

    es.onmessage = () => {
      onNotification()
    }

    es.onerror = () => {
      // EventSource auto-reconnects; nothing to handle here
    }

    return () => es.close()
  }, [onNotification])
}
```

- [ ] **Step 4: Verify TypeScript**

```bash
cd ui && npx tsc --noEmit
```

Expected: no errors.

- [ ] **Step 5: Commit**

```bash
cd ..
git add ui/src/hooks/
git commit -m "feat: add TanStack Query hooks and SSE stream hook"
```

---

### Task 8: TokenGate and App bootstrap

> **REQUIRED:** Invoke `Skill(frontend-design:frontend-design)` before writing these components.

**Files:**
- Create: `ui/src/components/TokenGate.tsx`
- Create: `ui/src/App.tsx`
- Modify: `ui/src/main.tsx`

- [ ] **Step 1: Create TokenGate.tsx**

```tsx
import { useState } from 'react'
import { setToken } from '../api/client'

interface TokenGateProps {
  onToken: () => void
}

export function TokenGate({ onToken }: TokenGateProps) {
  const [value, setValue] = useState('')
  const [error, setError] = useState('')

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const trimmed = value.trim()
    if (!trimmed) {
      setError('Token is required')
      return
    }
    setToken(trimmed)
    onToken()
  }

  return (
    <div className="min-h-screen bg-slate-900 flex items-center justify-center">
      <div className="w-full max-w-md bg-slate-800 rounded-xl border border-slate-700 p-8">
        <div className="mb-6">
          <h1 className="text-xl font-semibold text-white mb-1">HookVault</h1>
          <p className="text-slate-400 text-sm">
            Paste your API token to continue. Generate one with:
          </p>
          <pre className="mt-2 text-xs bg-slate-900 text-indigo-300 rounded p-2 font-mono">
            docker compose run --rm hookvault generate-token
          </pre>
        </div>
        <form onSubmit={handleSubmit}>
          <input
            type="password"
            value={value}
            onChange={e => setValue(e.target.value)}
            placeholder="eyJhbGci..."
            className="w-full bg-slate-900 border border-slate-600 rounded-lg px-4 py-2.5
                       text-white placeholder-slate-500 text-sm font-mono
                       focus:outline-none focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
          />
          {error && <p className="mt-1.5 text-xs text-red-400">{error}</p>}
          <button
            type="submit"
            className="mt-4 w-full bg-indigo-600 hover:bg-indigo-500 text-white
                       font-medium rounded-lg py-2.5 text-sm transition-colors"
          >
            Continue
          </button>
        </form>
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Create App.tsx**

```tsx
import { useEffect, useState } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { getToken, setToken } from './api/client'
import { TokenGate } from './components/TokenGate'

const queryClient = new QueryClient()

function Inner() {
  const [hasToken, setHasToken] = useState(false)

  useEffect(() => {
    // Read ?token= from URL, store, strip from address bar
    const params = new URLSearchParams(window.location.search)
    const urlToken = params.get('token')
    if (urlToken) {
      setToken(urlToken)
      params.delete('token')
      const newUrl = params.toString()
        ? `${window.location.pathname}?${params}`
        : window.location.pathname
      window.history.replaceState({}, '', newUrl)
    }
    setHasToken(!!getToken())
  }, [])

  if (!hasToken) {
    return <TokenGate onToken={() => setHasToken(true)} />
  }

  // Split pane placeholder — replaced in Sub-phase C
  return (
    <div className="min-h-screen bg-slate-900 flex">
      <div className="w-80 border-r border-slate-700 flex items-center justify-center">
        <span className="text-slate-500 text-sm">Event list — coming soon</span>
      </div>
      <div className="flex-1 flex items-center justify-center">
        <span className="text-slate-500 text-sm">Event detail — coming soon</span>
      </div>
    </div>
  )
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <Inner />
    </QueryClientProvider>
  )
}
```

- [ ] **Step 3: Update main.tsx**

```tsx
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
```

- [ ] **Step 4: Build and verify**

```bash
cd ui && npm run build && npm run lint
```

Expected: no errors.

- [ ] **Step 5: Commit**

```bash
cd ..
git add ui/src/components/TokenGate.tsx ui/src/App.tsx ui/src/main.tsx
git commit -m "feat: add TokenGate component and App token bootstrap"
```

---

### Task 9: Dockerfile multi-stage update

**Files:**
- Modify: `Dockerfile`

- [ ] **Step 1: Read the existing Dockerfile**

Read `Dockerfile` to understand the current stage structure before modifying.

- [ ] **Step 2: Add Node build stage and wire wwwroot**

Add the Node build stage as the FIRST stage (before the existing `FROM mcr.microsoft.com/dotnet/sdk` line):

```dockerfile
# Stage 0: build React UI
FROM node:20-alpine AS ui-build
WORKDIR /ui
COPY ui/package*.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build
```

In the existing .NET SDK build stage, after `COPY src/ ./src/` (or wherever the source is copied), add:

```dockerfile
COPY --from=ui-build /ui/dist ./src/HookVault/wwwroot/
```

This copies the built React app into the .NET project's `wwwroot/` before `dotnet publish`, so it is included in the publish output automatically.

- [ ] **Step 3: Verify Docker build**

```bash
docker build -t hookvault:ui-test .
```

Expected: build succeeds. All 4 stages complete.

- [ ] **Step 4: Verify UI is served**

```bash
docker run --rm -e HOOKVAULT_JWT_SECRET=test-secret-32-bytes-minimum-length \
  -v ./hookvault.example.json:/app/config/hookvault.json:ro \
  -p 7778:8080 hookvault:ui-test &
sleep 3
curl -s http://localhost:7778/ | grep -q "HookVault" && echo "UI served OK"
```

Expected: `UI served OK`

- [ ] **Step 5: Commit**

```bash
git add Dockerfile
git commit -m "feat: add Node UI build stage to Dockerfile"
```

---

## Sub-phase C — Event list + live feed

> **REQUIRED:** Invoke `Skill(frontend-design:frontend-design)` before writing these components.

### Task 10: EventRow and EventList components

**Files:**
- Create: `ui/src/components/EventRow.tsx`
- Create: `ui/src/components/EventList.tsx`

- [ ] **Step 1: Create EventRow.tsx**

```tsx
import type { EventSummary, EventStatus } from '../types'

interface EventRowProps {
  event: EventSummary
  selected: boolean
  onClick: () => void
}

function statusColor(status: EventStatus): string {
  switch (status) {
    case 'Forwarded': return 'border-green-500'
    case 'ForwardFailed':
    case 'ReplayFailed': return 'border-red-500'
    case 'Replaying': return 'border-amber-500'
    default: return 'border-slate-600'
  }
}

function sigBadge(valid: boolean | null) {
  if (valid === null) return null
  return valid
    ? <span className="text-green-400 text-xs">✓</span>
    : <span className="text-red-400 text-xs">✗</span>
}

export function EventRow({ event, selected, onClick }: EventRowProps) {
  const time = new Date(event.receivedAt).toLocaleTimeString()

  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-3 py-2.5 border-l-2 transition-colors
                  ${statusColor(event.status)}
                  ${selected ? 'bg-slate-700/60' : 'hover:bg-slate-800'}`}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="text-sm font-medium text-white truncate">{event.provider}</span>
        <div className="flex items-center gap-1.5 shrink-0">
          {sigBadge(event.signatureValid)}
          {event.forwardStatusCode != null && (
            <span className="text-xs text-slate-400">{event.forwardStatusCode}</span>
          )}
        </div>
      </div>
      <div className="text-xs text-slate-500 mt-0.5">{time}</div>
    </button>
  )
}
```

- [ ] **Step 2: Create EventList.tsx**

```tsx
import { useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useEvents, useInvalidateEvents } from '../hooks/useEvents'
import { useEventStream } from '../hooks/useEventStream'
import { EventRow } from './EventRow'
import type { EventSummary } from '../types'

interface EventListProps {
  selectedId: string | null
  onSelect: (id: string) => void
  providerFilter: string
  statusFilter: string
  onProviderFilter: (p: string) => void
  onStatusFilter: (s: string) => void
}

const STATUS_OPTIONS = ['', 'Forwarded', 'ForwardFailed', 'Replaying', 'ReplayFailed']

export function EventList({
  selectedId, onSelect,
  providerFilter, statusFilter,
  onProviderFilter, onStatusFilter,
}: EventListProps) {
  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: api.getHealth,
    staleTime: 60_000,
  })

  const { data, isLoading } = useEvents({
    provider: providerFilter || undefined,
    status: statusFilter || undefined,
  })

  const invalidate = useInvalidateEvents()
  const onNotification = useCallback(() => { invalidate() }, [invalidate])
  useEventStream(onNotification)

  const events: EventSummary[] = data?.events ?? []
  const providers = health?.providers ?? []

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <div className="flex items-center justify-between px-3 py-2 border-b border-slate-700">
        <span className="text-xs font-semibold text-indigo-400 uppercase tracking-wider">
          Events
        </span>
        <span className="text-xs text-green-400">● live</span>
      </div>

      {/* Provider filter */}
      <div className="flex gap-1.5 px-3 py-2 border-b border-slate-700 flex-wrap">
        <FilterPill label="all" active={!providerFilter} onClick={() => onProviderFilter('')} />
        {providers.map(p => (
          <FilterPill key={p} label={p} active={providerFilter === p}
            onClick={() => onProviderFilter(providerFilter === p ? '' : p)} />
        ))}
      </div>

      {/* Status filter */}
      <div className="flex gap-1.5 px-3 py-2 border-b border-slate-700 flex-wrap">
        {STATUS_OPTIONS.map(s => (
          <FilterPill key={s || 'all'} label={s || 'all'} active={statusFilter === s}
            onClick={() => onStatusFilter(statusFilter === s && s ? '' : s)} />
        ))}
      </div>

      {/* Event list */}
      <div className="flex-1 overflow-y-auto">
        {isLoading && (
          <div className="text-slate-500 text-sm text-center py-8">Loading...</div>
        )}
        {!isLoading && events.length === 0 && (
          <div className="text-slate-600 text-sm text-center py-8">
            No events yet. Send a webhook to get started.
          </div>
        )}
        {events.map(e => (
          <EventRow key={e.id} event={e} selected={e.id === selectedId}
            onClick={() => onSelect(e.id)} />
        ))}
      </div>
    </div>
  )
}

function FilterPill({ label, active, onClick }: { label: string; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className={`px-2 py-0.5 rounded text-xs font-medium transition-colors
                  ${active
                    ? 'bg-indigo-600 text-white'
                    : 'bg-slate-700 text-slate-400 hover:text-slate-200'}`}
    >
      {label}
    </button>
  )
}
```

- [ ] **Step 3: Update App.tsx to use EventList**

Replace the split pane placeholder in `App.tsx` with real split pane:

```tsx
// Add these imports at the top of App.tsx
import { useState } from 'react'
import { EventList } from './components/EventList'

// Replace the placeholder split pane div inside Inner() with:
const [selectedId, setSelectedId] = useState<string | null>(null)
const [providerFilter, setProviderFilter] = useState('')
const [statusFilter, setStatusFilter] = useState('')

return (
  <div className="min-h-screen bg-slate-900 flex overflow-hidden h-screen">
    <div className="w-72 shrink-0 border-r border-slate-700 flex flex-col">
      <EventList
        selectedId={selectedId}
        onSelect={setSelectedId}
        providerFilter={providerFilter}
        statusFilter={statusFilter}
        onProviderFilter={setProviderFilter}
        onStatusFilter={setStatusFilter}
      />
    </div>
    <div className="flex-1 flex items-center justify-center">
      <span className="text-slate-500 text-sm">
        {selectedId ? `Selected: ${selectedId}` : 'Select an event to inspect'}
      </span>
    </div>
  </div>
)
```

- [ ] **Step 4: Build and lint**

```bash
cd ui && npm run build && npm run lint
```

Expected: no errors.

- [ ] **Step 5: Commit**

```bash
cd ..
git add ui/src/components/EventRow.tsx ui/src/components/EventList.tsx ui/src/App.tsx
git commit -m "feat: add EventList with live SSE feed and provider/status filters"
```

---

## Sub-phase D — Event detail + replay

> **REQUIRED:** Invoke `Skill(frontend-design:frontend-design)` before writing these components.

### Task 11: Detail section components

**Files:**
- Create: `ui/src/components/BodySection.tsx`
- Create: `ui/src/components/HeadersSection.tsx`
- Create: `ui/src/components/ValidationSection.tsx`
- Create: `ui/src/components/ForwardSection.tsx`

- [ ] **Step 1: Create BodySection.tsx**

```tsx
interface BodySectionProps { body: string }

export function BodySection({ body }: BodySectionProps) {
  let display = body
  try {
    display = JSON.stringify(JSON.parse(body), null, 2)
  } catch { /* raw */ }

  return (
    <section>
      <SectionHeader>Body</SectionHeader>
      <pre className="text-xs font-mono text-slate-300 bg-slate-900 rounded-lg p-3
                       overflow-x-auto whitespace-pre-wrap break-words">
        {display}
      </pre>
    </section>
  )
}

export function SectionHeader({ children }: { children: React.ReactNode }) {
  return (
    <h3 className="text-xs font-semibold text-indigo-400 uppercase tracking-wider mb-2">
      {children}
    </h3>
  )
}
```

- [ ] **Step 2: Create HeadersSection.tsx**

```tsx
import { SectionHeader } from './BodySection'

interface HeadersSectionProps { headers: Record<string, string> }

export function HeadersSection({ headers }: HeadersSectionProps) {
  return (
    <section>
      <SectionHeader>Headers</SectionHeader>
      <table className="w-full text-xs">
        <tbody>
          {Object.entries(headers).map(([k, v]) => (
            <tr key={k} className="border-b border-slate-700/50">
              <td className="py-1.5 pr-3 text-slate-400 font-mono align-top w-1/3 break-all">{k}</td>
              <td className="py-1.5 text-slate-300 font-mono break-all">{v}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
```

- [ ] **Step 3: Create ValidationSection.tsx**

```tsx
import type { ValidationDetails } from '../types'
import { SectionHeader } from './BodySection'

interface ValidationSectionProps {
  valid: boolean | null
  details: ValidationDetails | null
}

export function ValidationSection({ valid, details }: ValidationSectionProps) {
  return (
    <section>
      <SectionHeader>Validation</SectionHeader>
      {valid === null ? (
        <p className="text-xs text-slate-500">No validation configured for this provider.</p>
      ) : (
        <div className="space-y-2">
          <div className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium
                           ${valid ? 'bg-green-900/50 text-green-300' : 'bg-red-900/50 text-red-300'}`}>
            {valid ? '✓ Signature valid' : '✗ Signature invalid'}
          </div>
          {details && (
            <dl className="text-xs space-y-1.5 mt-2">
              <Row label="Algorithm" value={details.algorithmUsed} />
              <Row label="Payload used" value={details.payloadUsed} mono />
              <Row label="Computed" value={details.computedSignature} mono />
              <Row label="Received" value={details.receivedSignature} mono />
              {details.extractedTimestamp && (
                <Row label="Timestamp" value={details.extractedTimestamp} />
              )}
            </dl>
          )}
        </div>
      )}
    </section>
  )
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex gap-2">
      <dt className="text-slate-500 w-24 shrink-0">{label}</dt>
      <dd className={`text-slate-300 break-all ${mono ? 'font-mono' : ''}`}>{value}</dd>
    </div>
  )
}
```

- [ ] **Step 4: Create ForwardSection.tsx**

```tsx
import { SectionHeader } from './BodySection'
import type { EventDetail } from '../types'

type ForwardProps = Pick<EventDetail,
  'forwardUrl' | 'forwardStatusCode' | 'forwardError' |
  'forwardedAt' | 'replayCount' | 'lastReplayAt' | 'lastError'>

export function ForwardSection(props: ForwardProps) {
  const { forwardUrl, forwardStatusCode, forwardError,
          forwardedAt, replayCount, lastReplayAt, lastError } = props

  return (
    <section>
      <SectionHeader>Forward</SectionHeader>
      <dl className="text-xs space-y-1.5">
        <Row label="Destination" value={forwardUrl} />
        {forwardStatusCode != null && (
          <Row label="Status"
            value={forwardStatusCode.toString()}
            valueClass={forwardStatusCode < 300 ? 'text-green-400' : 'text-red-400'} />
        )}
        {forwardError && <Row label="Error" value={forwardError} valueClass="text-red-400" />}
        {forwardedAt && <Row label="Forwarded" value={new Date(forwardedAt).toLocaleString()} />}
        {replayCount > 0 && <Row label="Replays" value={String(replayCount)} />}
        {lastReplayAt && <Row label="Last replay" value={new Date(lastReplayAt).toLocaleString()} />}
        {lastError && <Row label="Last error" value={lastError} valueClass="text-red-400" />}
      </dl>
    </section>
  )
}

function Row({ label, value, valueClass = 'text-slate-300' }: {
  label: string; value: string; valueClass?: string
}) {
  return (
    <div className="flex gap-2">
      <dt className="text-slate-500 w-24 shrink-0">{label}</dt>
      <dd className={`break-all ${valueClass}`}>{value}</dd>
    </div>
  )
}
```

- [ ] **Step 5: Build and lint**

```bash
cd ui && npm run build && npm run lint
```

- [ ] **Step 6: Commit**

```bash
cd ..
git add ui/src/components/BodySection.tsx ui/src/components/HeadersSection.tsx \
        ui/src/components/ValidationSection.tsx ui/src/components/ForwardSection.tsx
git commit -m "feat: add event detail section components"
```

---

### Task 12: EventDetail component + replay wiring

**Files:**
- Create: `ui/src/components/EventDetail.tsx`
- Modify: `ui/src/App.tsx` — replace placeholder with EventDetail

- [ ] **Step 1: Create EventDetail.tsx**

```tsx
import { useEvent, useInvalidateEvent } from '../hooks/useEvent'
import { useInvalidateEvents } from '../hooks/useEvents'
import { api } from '../api/client'
import { BodySection } from './BodySection'
import { HeadersSection } from './HeadersSection'
import { ValidationSection } from './ValidationSection'
import { ForwardSection } from './ForwardSection'

interface EventDetailProps { id: string }

export function EventDetail({ id }: EventDetailProps) {
  const { data, isLoading, error } = useEvent(id)
  const invalidateEvent = useInvalidateEvent()
  const invalidateEvents = useInvalidateEvents()

  async function handleReplay() {
    await api.replayEvent(id)
    invalidateEvent(id)
    invalidateEvents()
  }

  if (isLoading) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <span className="text-slate-500 text-sm">Loading...</span>
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <span className="text-red-400 text-sm">Failed to load event</span>
      </div>
    )
  }

  const time = new Date(data.receivedAt).toLocaleString()

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Header */}
      <div className="flex items-start justify-between gap-4 px-4 py-3 border-b border-slate-700 shrink-0">
        <div>
          <div className="text-sm font-semibold text-white">
            {data.provider} · POST {data.path}
          </div>
          <div className="text-xs text-slate-500 mt-0.5">{time}</div>
        </div>
        <button
          onClick={handleReplay}
          className="shrink-0 px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500
                     text-white text-xs font-medium rounded-lg transition-colors"
        >
          ↺ Replay
        </button>
      </div>

      {/* Scrollable sections */}
      <div className="flex-1 overflow-y-auto px-4 py-4 space-y-6">
        <BodySection body={data.body} />
        <HeadersSection headers={data.headers} />
        <ValidationSection valid={data.signatureValid} details={data.validationDetails} />
        <ForwardSection
          forwardUrl={data.forwardUrl}
          forwardStatusCode={data.forwardStatusCode}
          forwardError={data.forwardError}
          forwardedAt={data.forwardedAt}
          replayCount={data.replayCount}
          lastReplayAt={data.lastReplayAt}
          lastError={data.lastError}
        />
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Update App.tsx right panel**

In `App.tsx`, replace the placeholder right-panel div with:

```tsx
import { EventDetail } from './components/EventDetail'

// Replace the right-panel div:
<div className="flex-1 overflow-hidden">
  {selectedId
    ? <EventDetail id={selectedId} />
    : (
      <div className="h-full flex items-center justify-center">
        <p className="text-slate-600 text-sm">Select an event to inspect</p>
      </div>
    )
  }
</div>
```

- [ ] **Step 3: Build and lint**

```bash
cd ui && npm run build && npm run lint
```

Expected: no errors.

- [ ] **Step 4: Commit**

```bash
cd ..
git add ui/src/components/EventDetail.tsx ui/src/App.tsx
git commit -m "feat: add EventDetail component with replay button"
```

---

## Sub-phase E — Polish + E2E + CI

### Task 13: Frontend-design polish pass

- [ ] **Step 1: Invoke frontend-design skill**

```
Skill(frontend-design:frontend-design)
```

Follow the skill's guidance to review and improve the visual quality of all components in `ui/src/components/`. Pay particular attention to:
- Spacing and visual hierarchy in `EventDetail.tsx`
- Colour usage for status indicators in `EventRow.tsx`
- Overall dark slate aesthetic consistency

- [ ] **Step 2: Build and lint after polish**

```bash
cd ui && npm run build && npm run lint
```

- [ ] **Step 3: Commit polish changes**

```bash
cd ..
git add ui/src/
git commit -m "feat: apply frontend-design polish pass to UI components"
```

---

### Task 14: E2E tests

**Files:**
- Create: `tests/e2e/auth.spec.ts`
- Create: `tests/e2e/live-feed.spec.ts`
- Create: `tests/e2e/event-detail.spec.ts`
- Create: `tests/e2e/replay.spec.ts`
- Create: `tests/e2e/filters.spec.ts`

> E2E tests use the Playwright MCP tools interactively. They are verified by running the app locally and using `mcp__plugin_playwright_playwright__*` tools to navigate, click, and assert.

- [ ] **Step 1: Start the app for E2E testing**

```bash
dotnet run --project src/HookVault -- &
# Note the ?token= URL printed to the console
```

- [ ] **Step 2: auth.spec — No token shows TokenGate**

Using Playwright MCP tools:
1. Navigate to `http://localhost:5000` (no token)
2. Assert the token input and "HookVault" heading are visible
3. Paste a valid token generated by `generate-token`
4. Assert the split pane appears with "Events" heading

- [ ] **Step 3: live-feed.spec — Event appears without refresh**

1. Navigate to `http://localhost:5000/?token={valid_token}`
2. Assert split pane is visible
3. POST a webhook: `curl -X POST http://localhost:5000/api/ingest/stripe -H 'Content-Type: application/json' -d '{"type":"test"}'`
4. Wait up to 3 seconds
5. Assert "stripe" appears in the event list without page reload

- [ ] **Step 4: event-detail.spec — Click event shows all sections**

1. With the app running and an event captured, click the event row
2. Assert the detail panel shows: Body section, Headers section, Validation section, Forward section
3. Assert the Replay button is visible

- [ ] **Step 5: replay.spec — Replay button works**

1. Find a ForwardFailed event in the list
2. Click its row to open detail
3. Click Replay button
4. Assert the status indicator updates (Replaying or Forwarded)

- [ ] **Step 6: filters.spec — Provider and status filters**

1. With multiple events from different providers
2. Click the "stripe" provider pill
3. Assert only stripe events are visible
4. Click "all" to restore
5. Click "ForwardFailed" status pill
6. Assert only failed events are visible

- [ ] **Step 7: Commit E2E spec files**

```bash
git add tests/e2e/
git commit -m "test: add E2E test specs for Phase 6 UI"
```

---

### Task 15: CI update

**Files:**
- Modify: `.github/workflows/` — add npm build step to Docker job

- [ ] **Step 1: Read existing CI workflow**

Read `.github/workflows/` to find the Docker build job.

- [ ] **Step 2: Add npm cache + build step**

In the Docker build job, before the `Build Docker image` step, add:

```yaml
- name: Set up Node.js
  uses: actions/setup-node@v4
  with:
    node-version: '20'
    cache: 'npm'
    cache-dependency-path: ui/package-lock.json

- name: Install and build UI
  run: |
    cd ui
    npm ci
    npm run build
```

Note: The Docker build itself also runs `npm ci && npm run build` inside the container. This CI step is a fast early-failure check that catches TypeScript or lint errors before the slower Docker build.

- [ ] **Step 3: Verify CI config is valid YAML**

```bash
cat .github/workflows/*.yml | python3 -c "import sys,yaml; yaml.safe_load(sys.stdin)" && echo "YAML valid"
```

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/
git commit -m "ci: add Node build step to CI for React UI"
```

---

## Final verification

- [ ] **Run all .NET tests**

```bash
dotnet test --configuration Release
```

Expected: all pass (58+ tests including new EventStreamTests).

- [ ] **Run UI build + lint**

```bash
cd ui && npm run build && npm run lint
```

- [ ] **Run format check**

```bash
dotnet format --verify-no-changes
```

- [ ] **Build Docker image**

```bash
docker build -t hookvault:final .
```

Expected: all stages complete, image produced.
