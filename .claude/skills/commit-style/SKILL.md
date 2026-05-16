---
name: commit-style
description: Commit and PR message conventions for the HookVault repo. No AI attribution allowed.
user-invocable: false
---

# Commit + PR style

## Subject line

- Format: `type: short imperative subject`
- All lowercase.
- No trailing period.
- ~70 characters max.
- Imperative mood: "add", "fix", "remove" — not "added", "fixes".

## Type prefixes

| Prefix     | Use for                                        |
|------------|-----------------------------------------------|
| `feat:`    | new user-visible functionality                |
| `fix:`     | bug fixes                                     |
| `chore:`   | tooling, dependency bumps, repo hygiene       |
| `ci:`      | CI/CD pipeline changes                        |
| `test:`    | test-only changes (no production code)        |
| `docs:`    | documentation only                            |
| `refactor:`| code change with no behavior change           |

## Body

- Optional but encouraged when the *why* isn't obvious from the subject.
- Wrap lines at ~72 characters.
- Explain *why* the change is needed and *what* trade-offs it makes.
  Don't restate the diff in prose.
- Reference related issues / commits by short SHA if relevant.

## What NEVER appears

- `Co-Authored-By: Claude ...`
- `Generated with Claude ...`
- `🤖 Generated with ...`
- Any other AI / assistant / Anthropic attribution
- Emojis (unless explicitly requested)

## PR titles + descriptions

- PR title follows the same `type: subject` format as a commit.
- PR body has two sections:

```
## Summary
- 1-3 bullets covering the user-visible effect.

## Test plan
- [ ] checklist items the reviewer can use to verify.
```

- No AI attribution in PR body. No `🤖 Generated with Claude Code` footer.

## Examples

Good:

```
feat: add health endpoint at GET /api/health

Returns service status, version, configured provider names, database
kind, total captured event count, and oldest event timestamp. Useful
for Docker health checks and uptime monitors.
```

Bad:

```
Added a new health endpoint feature for monitoring 🎉

🤖 Generated with Claude Code

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
```
