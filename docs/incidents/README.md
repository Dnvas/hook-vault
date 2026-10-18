# Incident notes

One file per failure, whether caused on purpose (Stage 4 drills) or real. The note is written **before** the fix, from what the dashboards, logs and traces showed at the time. It is short: the point is detection and diagnosis, not prose.

File name: `YYYY-MM-DD-short-symptom.md`, for example `2026-11-10-replays-stalled-behind-slow-destination.md`.

## Template

```markdown
# <Symptom as a user or operator would state it>

- **Date / time (UTC):**
- **Drill or real:** drill N / real
- **Duration until detected:** (from the first bad request to the first alert or human notice)
- **Duration until mitigated:**

## What was observed
Which dashboard panel, log query or trace showed it first. Screenshot or query pasted. Numbers.

## Cause
The mechanism, one paragraph, ending with "because".

## Mitigation
What stopped the bleeding (rollback, restart, scale, flag), and how long it took.

## Fix
What change prevents it. Link the issue or PR. If no fix yet, say so.

## Detection next time
The alert, panel or log query that would catch it in under five minutes. Added to `docs/runbook.md`? yes / no.
```

The `/incident` command creates a file from this template with the date filled in.
