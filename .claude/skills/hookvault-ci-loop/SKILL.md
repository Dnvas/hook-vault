---
name: hookvault-ci-loop
description: Briefing template for dispatching loop-operator to watch a HookVault PR's CI. Codifies the CI surface, failure classification, resolver selection, and cycle-cap policy. Load when finishing a PR and starting the autonomous CI watch.
---

# HookVault CI Loop Briefing Template

Use when dispatching `loop-operator` (background) after `gh pr create`. The template ensures the loop knows HookVault's specific CI shape and which resolver to call per failure class.

## HookVault's CI surface (as of v1.0)

| Check name | Workflow | Typical duration | Failure shape |
|---|---|---|---|
| `Build, test, and format check` | CI | 45–60s | `dotnet build` errors, `dotnet test` failures, `dotnet format` diffs |
| `Build and lint React UI` | CI | 15–20s | npm errors, eslint failures, TypeScript errors |
| `Build Docker image` | CI | 40–60s | `docker build` errors, missing files in COPY, base image issues |
| `Analyze (C#)` | CodeQL | 100–125s | Security/quality findings (commented inline on the PR) |
| `CodeQL` | CodeQL | 2s | Aggregator only — fails only if `Analyze (C#)` fails |

Two parallel "CI" workflow runs are normal (one per push event + one per PR open).

## Dispatch template

When you're about to dispatch `loop-operator` after `gh pr create`, use this exact briefing structure:

```
You are watching PR #<NUM> on branch <BRANCH>. Your job: poll CI until green
or escalate after 3 fix cycles.

Read these skills first:
  Skill(hookvault-codemap)        — to navigate any file a failure references
  Skill(hookvault-conventions)    — to know what fixes are conventional
  Skill(commit-style)             — for any commit you make

Loop (max 3 cycles):
  1. Sleep 180s (3 min) — CodeQL is the long pole at ~2min.
  2. Run: gh pr checks <NUM>
  3. If all checks pass: done. Return "CI green for PR #<NUM>".
  4. If any check is still pending/in_progress: go to 1.
  5. If any check failed:
     a. Identify which check.
     b. Fetch logs: gh run view <run-id> --log-failed
     c. Classify the failure (see classification below).
     d. Dispatch the right resolver agent (see resolver map below).
     e. After resolver pushes a fix: go to 1.

Escalation (any of):
  - 3 fix cycles completed with the same check still red.
  - Resolver agent returns "I cannot fix this" diagnosis.
  - Failure class is "code-q-finding-policy" (CodeQL flagged something that
    needs a human design decision, not a mechanical fix).
  In all escalation cases, return:
  "ESCALATED: PR #<NUM>, check '<NAME>', cycle <N>/3, diagnosis: <summary>"
```

## Failure classification

When a check fails, classify into one of:

| Class | Symptom (in logs) | Resolver |
|---|---|---|
| `dotnet-build` | `error CS\d+` lines | `build-error-resolver` briefed with .NET context |
| `dotnet-test` | `Failed!` followed by xUnit test names | `build-error-resolver` (treat like build for now — half-measure) |
| `dotnet-format` | `Formatted code file` listing | `build-error-resolver` — fix is always `dotnet format` + commit |
| `eslint-or-ts` | `ESLint: ` / `error TS\d+` | **ESCALATE** — no JS resolver yet |
| `docker-build` | `failed to compute cache key` / `COPY failed` | `build-error-resolver` — usually a missing file or wrong path |
| `codeql-quality` | CodeQL inline comment, fixable mechanically (unused var, missing Dispose, etc.) | `build-error-resolver` with the CodeQL comment as briefing |
| `codeql-policy` | CodeQL inline comment requiring design judgment (e.g. "SQL injection in this method") | **ESCALATE** — human call needed |

## Resolver briefing

When dispatching `build-error-resolver` (half-measure — generic, not .NET-specific), brief with:

```
You are resolving a CI failure on HookVault's PR #<NUM>, branch <BRANCH>.

Load: Skill(hookvault-codemap), Skill(hookvault-conventions), Skill(commit-style).

Failure class: <CLASS from table above>
Failing job: <NAME>
Job log excerpt:
<paste the last ~200 lines of the failed job>

Fix the underlying cause. Stage. Commit with conventional message format
(no AI attribution). Push.

If you cannot mechanically determine the fix, return "I cannot fix this:
<reason>" without making any changes.

Cycle cap is enforced by the parent loop-operator; you make at most one
fix attempt and return.
```

## Cycle-cap policy

- **3 cycles max** per `loop-operator` invocation.
- One resolver dispatch per cycle.
- After cycle 3, escalate regardless of state.
- Why 3 not 5: by the third cycle, if the same check is still red, the failure class is probably misclassified or genuinely needs human judgment. Looping further burns tokens.

## Escalation surface

When `loop-operator` returns to the orchestrator (this Claude session):

- If the return summary starts with `CI green for PR #<NUM>` → tell the user "ready to merge."
- If starts with `ESCALATED:` → surface the diagnosis to the user with the failing job URL, then stop. Do NOT auto-merge.

## When NOT to use this template

- CI failures during local dev (use `systematic-debugging` skill instead).
- Branches without an open PR (this template targets `gh pr checks <NUM>`).
- Multi-PR coordination (loop-operator watches one PR at a time).

## When this template will need updating

- A new CI check is added → update the surface table.
- A `dotnet-build-resolver` agent is written (currently we use generic) → update the resolver map.
- A `codeql-resolver` agent is written for the policy-level findings → update the escalation criteria.
- Webhook security-related checks added (e.g. SAST tool) → add to classification with `webhook-security-reviewer` as the resolver.
