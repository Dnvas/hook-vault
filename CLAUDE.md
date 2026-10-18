# HookVault

Webhook capture and replay service: .NET 8 API and worker, React UI, SQLite or PostgreSQL. v1.0 is a single-container developer tool; this repo is now being taken to production grade. Read `docs/ROADMAP.md` for the target state and stages, and `docs/production-readiness-review.md` for why.

**Current stage:** 0 and 1 (load baseline, infrastructure as code). Week 1, 18 to 23 Oct 2026.
**Plan and tracker (maintainer only):** https://claude.ai/code/artifact/ddbc7d54-83c2-43ed-94d2-45593ff3cc38

## How we work in this repo

- The maintainer writes the spec first (`/spec`), you build from it. If the spec is missing or ambiguous, ask; do not widen scope.
- The maintainer reviews every diff before it is committed. Explain each change in two lines: what, and why.
- Documents in `docs/` (ADRs, incident notes, runbooks, SLOs, comparisons) are written by the maintainer. You may check them for errors and gaps; you do not draft them.
- Any decision with more than one reasonable option gets an ADR (`/adr`) in the same session, before the code lands.
- Every drill in Stage 4 produces an incident note (`/incident`) *before* the fix.
- When asked "why", answer with the mechanism, not the conclusion. Prefer a cross-language comparison (C#, Python, TypeScript) when it helps.
- End every working session with `/handoff` so the next session starts without re-discovery.

## Guardrails

- AWS costs money. Never create a NAT gateway, load balancer, RDS instance or EKS cluster without saying so first. Remind the maintainer to run `terraform destroy` at the end of any session that applied infrastructure.
- Never commit secrets, `.env`, `hookvault.json`, or Terraform state. Secrets are referenced by environment-variable name.
- Do not weaken: fixed-time HMAC comparison, `[Authorize]` by default, non-root container, the three CI gates (`build`, `test`, `format`).
- No AI attribution in commits or PRs (see `.claude/skills/commit-style`).

## Repo map

```
src/HookVault/        service (Controllers, Services, Infrastructure, Middleware, Observability)
tests/                HookVault.Tests (xUnit), HookVault.E2E (API), e2e-ui (Playwright)
ui/                   React dashboard (Vite, Tailwind)
infra/                Terraform (Stage 1)            load/        k6 scripts (Stage 0)
drills/               failure drills (Stage 4)       deploy/helm  Kubernetes chart (Stage 6)
docs/                 ROADMAP, review, architecture/ (C4, adr/), incidents/, runbooks, SLOs
.claude/skills/       spec, conventions, codemap, PR checklist, commit style: load the relevant one before coding
.claude/commands/     /spec /adr /incident /handoff
```

## Commands (Windows 11, PowerShell, no WSL)

```powershell
dotnet build --configuration Release
dotnet test tests/HookVault.Tests --configuration Release
dotnet format --verify-no-changes            # CI fails on drift; `dotnet format` fixes it
dotnet run --project src/HookVault           # Development: Swagger at /swagger, UI token logged at start
$env:HOOKVAULT_JWT_SECRET = (openssl rand -hex 32); docker compose up --build     # or set it in .env
docker compose -f docker-compose.postgres.yml up --build
cd ui; npm ci; npm run dev                   # UI against http://localhost:7777
k6 run load/ingest.js                        # Stage 0
cd infra; terraform init; terraform plan     # Stage 1; `terraform destroy` before you stop
```

Conventions for C#, EF Core, DI lifetimes, metrics and tests are in `.claude/skills/hookvault-conventions`. The codemap in `.claude/skills/hookvault-codemap` is faster than grepping.
