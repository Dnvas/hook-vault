# Roadmap: HookVault to production grade

HookVault v1.0 is a single-container developer tool. This roadmap takes the same codebase to a service that runs unattended in AWS, is observable, has service-level objectives with alerts, has been broken on purpose and recovered, scales past one replica, and is documented well enough that a new engineer can operate it from the docs alone. The motivating findings are in [`production-readiness-review.md`](production-readiness-review.md).

Work is tracked as GitHub Issues, one per deliverable, grouped into one milestone per stage (`scripts/create-issues.ps1` creates them). This file holds the target state, the stages, and a stage-level status that changes only when a stage closes.

## Target state

| Property | v1.0 today | Target |
| --- | --- | --- |
| Deployment | `docker compose up` on a laptop | AWS from Terraform; staging and production from the same code; deploy by pipeline, promote by tag |
| Observability | Prometheus metrics, console logs | Structured logs with trace ids, metrics, traces via OpenTelemetry; three dashboards; alerts on burn rate |
| Reliability | Best effort, single replica | Two published SLOs with error budgets; ten rehearsed failure modes with a runbook; measured RPO and RTO |
| Scale | One process, in-memory queue | Two or more replicas; database-backed queue with concurrent workers; partitioned event table at millions of rows |
| Change | Migrate on boot, replace container | Migrations as a pipeline step; canary releases; feature flags; zero-downtime schema change |
| Documentation | README, spec, audit | C4 diagrams, a numbered ADR set, runbooks, a one-page system overview |

## Stages

Status: `planned` → `in progress` → `done`. Each stage ends with a short write-up in `docs/` and, where a decision was made, an ADR in `docs/architecture/adr/`.

### Stage 0: Load baseline — `planned`

**Goal:** know what the system does under load before changing anything.
**Done when:** `load/` contains a k6 script producing steady 50 requests/s with bursts to 500, 10% of traffic to a destination that sleeps 5 s; `load/README.md` records p50, p95 and p99 at each level against the local compose stack.
**Deliverables:** `load/ingest.js`, `load/README.md`.
**Background reading:** k6 documentation (scenarios, thresholds); Gil Tene, *How NOT to Measure Latency* (coordinated omission).

### Stage 1: Infrastructure as code — `planned`

**Goal:** the production environment exists only as Terraform.
**Done when:** `terraform apply` from `infra/` creates the VPC (public and private subnets, NAT), ECR, an ECS Fargate service behind an ALB, RDS Postgres, IAM task roles, and SSM Parameter Store secrets; state lives in S3; `terraform plan` runs on every pull request; `terraform destroy` returns the account to nothing.
**Deliverables:** `infra/` (modules `network`, `service`, `database` once it works as one file), `.github/workflows/infra-plan.yml`, `docs/architecture/c4-week1.md` (the target sketch, drawn before the first resource), ADR-0001.
**Drills:** change a security group in the console and read the drift in `plan`; delete the state lock mid-apply and recover; destroy and recreate from nothing, timed.
**Cost guardrail:** budget alerts before the first `apply`; destroy between sessions (NAT and ALB are the expensive lines).
**Background reading:** HashiCorp *Get started: AWS*; Terraform state and backends; *Terraform: Up and Running*, chapters on state and modules.

### Stage 2: Observability — `planned`

**Goal:** any single webhook can be followed through logs, metrics and traces, and the system's health is visible on one screen.
**Done when:** logs are structured JSON with a trace id on every line; the API and the replay worker emit OpenTelemetry traces and metrics through a Collector to a local LGTM stack (Loki, Grafana, Tempo, Mimir); three Grafana dashboards exist (RED for ingest, USE for the container and Postgres, replay queue depth and age); `/healthz` (liveness) and `/readyz` (readiness) are cheap and the statistics move to an authenticated `/api/stats`; `docs/observability.md` explains how to find an event by id in each signal.
**Deliverables:** `docker-compose.observability.yml`, `dashboards/*.json`, `docs/observability.md`, the health split (review finding 5).
**Background reading:** OpenTelemetry .NET docs; the RED method; the USE method; Prometheus histograms and `histogram_quantile`.

### Stage 3: Service levels — `planned`

**Goal:** "good enough" is defined in numbers and alerts fire on how fast the budget is being spent.
**Done when:** `docs/slo.md` defines two SLOs (ingest availability 99.9% over 30 days; 95% of replays delivered within 5 s) with their SLIs as PromQL; a Grafana panel shows remaining error budget; fast-burn and slow-burn alert rules exist and have been seen to fire under a forced burn; the saturation point under the Stage 0 script is measured and named (what broke first).
**Deliverables:** `docs/slo.md`, `alerts/*.yml`, the saturation note in `load/README.md`.
**Background reading:** Google SRE Workbook, *Implementing SLOs* and *Alerting on SLOs*; the availability table (99.9% is 43.8 minutes a month).

### Stage 4: Failure drills and runbook — `planned`

**Goal:** the ten most likely production failures have been caused on purpose, watched on the dashboards, and written up before being fixed.
**Done when:** `drills/` contains a script per drill; `docs/incidents/` contains one note per drill (symptom, cause, fix, detection); `docs/runbook.md` maps each alert to its first three commands and its mitigation.

| Drill | What you do | Finding it exposes |
| --- | --- | --- |
| 1 | Kill Postgres mid-load, and once mid-bulk-replay | 2 (lost queued work), 8 (no connection retry) |
| 2 | Connection pool set to 2 under load | 8; pool wait visible in traces |
| 3 | Drop the `ReceivedAt` index at 1 M rows | p99 under a missing index |
| 4 | Point replays at a destination that sleeps 30 s | 1, 2 (head-of-line blocking) |
| 5 | Remove the forwarder timeout and repeat drill 4 | why timeouts exist |
| 6 | Hold a long transaction with a row lock, run the load | lock waits, `idle in transaction` |
| 7 | 128 MB container, large payloads until OOM-killed | 6 (cap after buffering); exit code 137 |
| 8 | Deploy a broken config value; roll back in under 5 minutes | release discipline |
| 9 | Take the destination down for an hour, then bulk-replay 20,000 failures | 3 (bulk replay blocks) |
| 10 | 10,000 identical webhooks at once | 4 (dedup race) |

**Background reading:** Google SRE book, *Monitoring Distributed Systems* and *Emergency Response*; three published postmortems (Cloudflare, GitHub); Postgres `pg_stat_activity` and `pg_stat_statements`.

### Stage 5: Data layer at scale — `planned`

**Goal:** the event table behaves at millions of rows and the data can be analysed.
**Done when:** 5 M generated rows; the three slowest queries found with `pg_stat_statements` and fixed, with before-and-after `EXPLAIN ANALYZE` plans in `docs/query-tuning.md`; timestamps stored as `timestamptz` on Postgres (review finding 9, with an ADR); the event table partitioned by day with retention as partition drops; a deadlock reproduced between two replays and fixed by lock ordering; retention's count-cap path batched; a small replay-analytics mart (dbt or plain SQL views) behind Metabase.
**Deliverables:** `docs/query-tuning.md`, the partitioning migration, ADR on timestamps and partitioning, `analytics/`.
**Background reading:** *Use The Index, Luke*; Postgres *Using EXPLAIN*; Postgres table partitioning; Kleppmann, *Designing Data-Intensive Applications*, chapter 7.

### Stage 6: Kubernetes deployment — `planned`

**Goal:** the same service runs on Kubernetes, and the trade-off against ECS is written down from experience.
**Done when:** the Dockerfile is reviewed for layer order, size and signals; a Helm chart deploys HookVault to a kind cluster with liveness and readiness probes, requests and limits, ConfigMap and Secret, Ingress and an HPA; probes and limits have been set wrong on purpose and the behaviour recorded; two replicas run (which exposes review finding 2 and the single-process SSE notifier); `docs/ecs-vs-k8s.md` compares both deployments honestly.
**Deliverables:** `deploy/helm/hookvault/`, `docs/ecs-vs-k8s.md`, `docs/request-path.md` (one request traced from `curl` to the container with `dig`, `openssl s_client`, `curl -v`, `tcpdump`).
**Background reading:** Kubernetes *Basics* tutorial; probes; requests and limits; Helm docs; *High Performance Browser Networking*, chapters on TCP, TLS and HTTP.

### Stage 7: Hardening — `planned`

**Goal:** the review findings are fixed, each with a measurement from the drill that exposed it.
**Done when:**

- Ingest persists and returns 202 immediately; forwarding runs through the worker (finding 1).
- The queue is database-backed: a `Queued` state with `next_attempt_at` and `locked_until`, workers claim with `SELECT ... FOR UPDATE SKIP LOCKED`, N concurrent workers with a per-destination cap, retries as scheduled rows; the `Channel` remains only as a wake-up signal; two replicas process one backlog without duplicates (finding 2, ADR).
- Bulk replay is a single `UPDATE` that marks rows `Queued` (finding 3).
- Dedup is enforced by a unique index on `(Provider, ProviderEventId)` with the unique-violation handled (finding 4).
- Body cap honours `Content-Length` and Kestrel `MaxRequestBodySize`; raw-body buffering scoped to `/api/ingest` (finding 6).
- `EnableRetryOnFailure`, command timeouts and pool sizing configured (finding 8).
- Idempotency keys on the management API's mutating endpoints, a token-bucket rate limiter on ingest per provider, `ETag` on event detail, authorization code with PKCE for the UI against a real identity provider.
- The Stage 0 load test re-run before and after, numbers in `load/README.md`.

**Background reading:** Postgres `SKIP LOCKED`; the transactional outbox pattern; .NET rate-limiting middleware; OAuth 2.0 authorization code with PKCE.

### Stage 8: Release engineering and recovery — `planned`

**Goal:** the system can be changed and recovered without downtime, and a new engineer can operate it from the docs.
**Done when:** a staging environment from the same Terraform; the pipeline deploys to staging on merge and to production on a tag; migrations run as a pipeline step and the app refuses to start with pending migrations (finding 7); a canary deploy takes 10% of traffic with dashboards split by version; one feature flag toggled without a deploy; a column rename on the live event table under load with zero errors in three deploys; the production database destroyed on purpose and restored with RPO and RTO measured in `docs/dr-runbook.md`; a dependency and container scan with findings triaged; .NET upgraded to the current LTS; `docs/architecture/` holds the redrawn C4 diagrams diffed against the Stage 1 sketch, the ADR set, and a one-page overview; the 15-minute walkthrough re-recorded as an architecture talk.
**Background reading:** expand-and-contract migrations; blue-green versus canary; RPO and RTO; OWASP top ten; C4 model; ADR format.

## Conventions that apply to every stage

- A deliverable is not done until its documentation is written by the maintainer, not generated.
- Every decision with more than one reasonable option gets an ADR within the hour.
- Every drill produces an incident note before the fix.
- `terraform destroy` at the end of any session that touched AWS.
- Commit style, code conventions and the PR checklist are unchanged: see `.claude/skills/`.
