---
name: pr-review-checklist
description: Strict checklist for reviewing PRs in the HookVault repo. Every item is a potential blocker. Preload into the pr-reviewer agent.
user-invocable: false
---

# PR review checklist — HookVault

Reviews are **strict**. Any failure in the BLOCKERS section is a
request-changes verdict. Items in NITS are advisory.

## BLOCKERS — these fail the review

### Build + tests + format

- [ ] `dotnet build --configuration Release` succeeds with **0 warnings**.
- [ ] `dotnet test --configuration Release --no-build` — all tests pass.
- [ ] `dotnet format --verify-no-changes` — no drift.

### Test coverage

- [ ] Every new public method / branch has at least one xUnit test, unless it
      is a pure wiring file (e.g. `Program.cs` DI registration).
- [ ] New crypto / signature / auth code MUST have tests covering both the
      success path and at least two failure modes.

### Convention adherence

Cross-reference `.claude/skills/hookvault-conventions/SKILL.md`. Common
violations to flag:

- [ ] `new HttpClient()` used directly (should be `IHttpClientFactory`).
- [ ] Signature/token compared with `==` or `string.Equals` (should be
      `CryptographicOperations.FixedTimeEquals`).
- [ ] Secrets pulled from config file rather than env var.
- [ ] Sync EF Core calls in a request path (`ToList()`, `FirstOrDefault()`).
- [ ] Missing `CancellationToken` on an async public method.
- [ ] String-concatenated SQL.
- [ ] Provider-specific code paths (`if (provider == "stripe")`) — must be
      driven by config instead.

### Commit hygiene

- [ ] Commit messages follow `type: subject` format with lowercase type
      (`feat`, `fix`, `chore`, `test`, `ci`, `docs`, `refactor`).
- [ ] Body (when present) explains *why*, not *what*.
- [ ] **No AI / Claude / Assistant / Co-Authored-By attribution anywhere.**
      Check: commit messages, PR body, code comments. Use
      `git log <base>..HEAD --format=%B | grep -iE 'claude|co-authored|assistant|anthropic'`.
- [ ] Commits are logically scoped — one concern per commit when reasonable.

### Scope discipline

- [ ] All changes match the PR title / description. Drive-by refactors in
      unrelated files = request changes.
- [ ] No commented-out code.
- [ ] No orphan files (created but never referenced).
- [ ] No half-finished `TODO` comments without a tracking issue.
- [ ] No new dependencies without justification in the PR body.

### v0.3 invariants

- [ ] **`/metrics` must remain unauthenticated.** Any PR that adds `[Authorize]`
      or otherwise gates `/metrics` is wrong — the threat model assumes metrics
      are not secrets.
- [ ] **`captureOnly` events must rest at `Status = Captured`.** If a PR
      makes capture-only events transition to `Received` or `Forwarded`
      without an explicit replay, that's a regression.
- [ ] **Replay body overrides must not mutate the stored event.** The
      `WebhookEvent.Body` bytes are immutable across replays. Only
      `LastReplayWithEditedBody` and replay-stat fields update.
- [ ] **`HOOKVAULT_NO_AUTH` must log a Warning.** A silent auth bypass is
      a footgun. The startup line is the only mitigation.

### Security (high-level — the security-reviewer agent does the deep pass)

- [ ] No secrets / API keys / connection strings hard-coded.
- [ ] No `Console.WriteLine` of secrets or full webhook payloads.
- [ ] No raw user input concatenated into SQL or shell commands.

### Frontend (Phase 6 React UI — applies to PRs touching `ui/`)

- [ ] `npm run build` in `ui/` succeeds with zero TypeScript errors.
- [ ] `npm run lint` passes — no ESLint errors.
- [ ] No `any` types in component props or hook return values.
- [ ] All API response shapes imported from `src/types.ts` — no ad-hoc inline types.
- [ ] All API calls go through `src/api/client.ts` — no raw `fetch` in components.
- [ ] Token stored in `sessionStorage`, not `localStorage` or a React global.
- [ ] No hardcoded colour values — Tailwind palette classes only.
- [ ] `frontend-design:frontend-design` skill was used — PR description confirms it.
- [ ] E2E tests in `tests/e2e/` cover the changed behaviour; all pass against a real container.
- [ ] No `console.log` left in production code.
- [ ] Docker image still builds with the updated multi-stage Dockerfile.

## NITS — advisory only

- Naming inconsistencies.
- Method longer than ~50 lines that could be split.
- Magic numbers without a named constant.
- Imports that could be grouped better.
- Comments that explain *what* obvious code does.

## Output format

When invoked, the agent MUST return a structured report:

```
## Verdict: APPROVE | REQUEST_CHANGES

## Blockers
- [file.cs:LL] description

## Nits
- [file.cs:LL] description

## Notes
- anything else worth flagging
```

If there are zero blockers, verdict is APPROVE. Otherwise REQUEST_CHANGES.
Nits alone never block.
