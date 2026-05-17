# PR #2 — v1.0 Reliability Bundle

**Branch:** `feat/v1.0-reliability`
**Source:** [`docs/architecture-audit-v1.0.md`](../docs/architecture-audit-v1.0.md), Bucket 2
**Estimated size:** ~30 LOC production + ~30 LOC tests + spec note
**Spec impact:** Additive refinement only (4xx short-circuit), non-breaking

## Goal

Close the four forward/replay reliability gaps the v1.0 audit surfaced: 100s default HttpClient timeout, no 4xx vs 5xx distinction, unbounded replay channel, no per-attempt retry metric. Plus document the retry-eligibility contract refinement.

## In scope

R1, R2, R3, R4, R5 from the audit doc (Bucket 2). Retry timing `[1s, 2s, 4s]` × 3 is **preserved exactly** — only retry *eligibility* changes.

## Out of scope

- Polly / circuit-breaker — deferred (audit Bucket 1.x)
- Durable queue (Postgres LISTEN/NOTIFY, Redis Streams) — deferred
- Per-provider retry overrides — deferred
- Bucket 3 (Postgres parity) — separate PR

## Conventions

All work follows [`hookvault-conventions`](../.claude/skills/hookvault-conventions/SKILL.md): `IHttpClientFactory` only, async-only, file-scoped namespaces, `sealed` on new concrete classes. Commit format per [`commit-style`](../.claude/skills/commit-style/SKILL.md), no AI attribution.

---

## Tasks

### R1 — Forwarder HttpClient timeout

**Files:** `src/HookVault/Program.cs` (around line 70 where `AddHttpClient("forwarder")` is registered)

- Configure the named `"forwarder"` HttpClient with `Timeout = TimeSpan.FromSeconds(ParseForwardTimeoutEnv())`.
- Add a private static helper `ParseForwardTimeoutEnv()` (or place it on a small static class) that reads `HOOKVAULT_FORWARD_TIMEOUT_SECONDS`, defaults to 30, clamps to `[1, 300]`. Invalid values log a warning and use the default.
- Document in README under existing env-var section.

**Test:** New `tests/HookVault.Tests/ForwarderTimeoutTests.cs` or extend an existing forwarder test. Assert that an unreachable forward URL (use TCP-loopback to a closed port) fails within ~30s, not ~100s. Optionally a second test asserting the env-var override applies. **Add `[Collection("EnvVarMutation")]` since this test mutates env state.**

**Success criterion:** A forward to a closed port returns `ForwardResult(success: false)` within the configured timeout, not the .NET default 100s.

### R2 — ReplayWorker short-circuits 4xx (except 408, 425, 429)

**Files:** `src/HookVault/Services/ReplayWorker.cs`

- In the retry loop, after evaluating `!result.Success`, add a helper: `IsRetriable(int? statusCode)` that returns false for 4xx codes *except* 408, 425, 429 (which remain retriable). Network errors / `null` status code remain retriable. 5xx remains retriable.
- If non-retriable, skip remaining backoff and set `ReplayFailed` with the existing error metadata path.
- `ForwardResult` already carries `int? StatusCode` — use it.

**Test:** Extend `tests/HookVault.Tests/ReplayWorkerTests.cs` with two new cases:
1. A 404 response causes one attempt only (no retries) and the event ends as `ReplayFailed`.
2. A 429 response triggers full retry sequence (proves the exception list works).

**Success criterion:** 4xx (except 408/425/429) terminates after 1 attempt, not 4. Retry-timing for retriable failures unchanged.

### R3 — Bounded replay channel

**Files:** `src/HookVault/Services/ReplayQueue.cs`

- Replace `Channel.CreateUnbounded<ReplayJob>()` with:
  ```csharp
  Channel.CreateBounded<ReplayJob>(new BoundedChannelOptions(10_000)
  {
      SingleReader = true,
      FullMode = BoundedChannelFullMode.Wait,
  });
  ```
- Callers already `await` enqueue methods — backpressure surfaces naturally via async wait. No caller changes needed.

**Test:** Not strictly required (config-only behavioural change), but consider a test that enqueueing 10_001 items blocks the 10_001st until one is read. Use `Task.WhenAny` with a short timeout to assert blocking; release by reading one item. **Skip the test if it adds flakiness risk.**

**Success criterion:** Bulk replay of >10k events doesn't blow memory.

### R4 — Per-attempt retry metric

**Files:** `src/HookVault/Services/ReplayWorker.cs` (and possibly `HookVaultMeter` if a new instrument is needed)

- The existing `ReplaysTotal` counter likely has outcomes `"success"` and `"exhausted"`. Add a `"retry"` outcome that increments on each non-final failed attempt (i.e., attempts that will be retried, not the final attempt that gives up).
- Verify by reading `Services/ReplayWorker.cs` and the metric instrument definitions — the implementer must locate where the existing increment calls live and match the pattern.

**Test:** Extend `tests/HookVault.Tests/MetricsEndpointTests.cs` or `ReplayWorkerTests.cs` to assert the `outcome="retry"` label appears at `/metrics` after a forced retry scenario.

**Success criterion:** After a 5xx that retries twice then succeeds, `/metrics` shows `replays_total{outcome="retry"} 2` and `replays_total{outcome="success"} 1`.

### R5 — Spec note (additive contract refinement)

**Files:** `.claude/skills/hookvault-spec/SKILL.md`, `README.md`

- Under the replay-system section, add: "4xx responses (except 408, 425, 429) skip remaining retries — they indicate a configuration or auth error, not transient failure. Retry timing `[1s, 2s, 4s] × 3` is unchanged for retriable failures."
- Mention `HOOKVAULT_FORWARD_TIMEOUT_SECONDS` env var (default 30, range 1-300) in env-var docs.

**Runs:** Sequentially *after* R1–R4 land so docs reflect shipped behaviour.

---

## Verification

```bash
dotnet format --verify-no-changes
dotnet build --configuration Release
dotnet test --configuration Release
```

All three must pass. Cycle cap per [`autonomous-loops`](../.claude/skills/autonomous-loops/SKILL.md): 3 retry cycles, then escalate.

## PR shape

**Title:** `feat: v1.0 reliability — forward timeout, 4xx short-circuit, bounded queue (PR 2 of 3)`

**Body skeleton:** see PR #1 (`#23`) as a template.

## Risks

1. **Test for R2 with 429** — need a way to make the test HttpClient return 429. The existing tests should have a fake forwarder pattern; reuse it.
2. **R3 bounded channel + existing tests** — if any existing test pushes >10k items synchronously, this PR would cause it to deadlock. Run full test suite to verify no regression.
3. **R4 metric label** — depend on exact existing label name. Verify before implementing.

## Reviewer rubric

Same as PR #1: pr-review-checklist + webhook-security-reviewer (R1+R2 touch forwarding/auth-adjacent paths).
