---
name: Bug report
about: Report a defect, regression, or unexpected behaviour
title: ''
labels: bug
assignees: ''
---

## Description

What did you expect to happen? What happened instead?

## Reproduction

Minimal steps to reproduce:

1. ...
2. ...
3. ...

If possible, attach a redacted `hookvault.json` (remove `forwardUrl` /
`secretEnvVar` values) and a sample webhook payload.

## Environment

- HookVault version (commit SHA or tag):
- Host OS:
- Running via Docker / `dotnet run` / something else:
- Database: SQLite / PostgreSQL

## Logs

```text
(paste the relevant lines from `docker compose logs` or `dotnet run` stdout)
```

## Additional context

Anything else that might help — recent changes, related providers, etc.
