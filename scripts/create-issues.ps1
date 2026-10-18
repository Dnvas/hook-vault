<#
.SYNOPSIS
  Creates the roadmap milestones, labels and issues in the GitHub repo using the gh CLI.

.DESCRIPTION
  One milestone per stage (docs/ROADMAP.md), one issue per deliverable. Safe to re-run:
  existing milestones, labels and issues (matched by exact title) are skipped.

  Requires: gh (authenticated: `gh auth status`), run from the repo root.

.EXAMPLE
  pwsh scripts/create-issues.ps1
  pwsh scripts/create-issues.ps1 -DryRun
#>
[CmdletBinding()]
param(
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repo = (gh repo view --json nameWithOwner -q .nameWithOwner)
if (-not $repo) { throw 'gh could not resolve the repository. Run this from the repo root after `gh auth login`.' }
Write-Host "Repository: $repo"

# ---------------------------------------------------------------- milestones
$milestones = @(
  @{ title = 'Stage 0: Load baseline';                 description = 'k6 script and baseline p50/p95/p99 against the local stack.' },
  @{ title = 'Stage 1: Infrastructure as code';        description = 'AWS estate from Terraform; plan on PR; destroy to nothing.' },
  @{ title = 'Stage 2: Observability';                 description = 'Structured logs, OpenTelemetry traces and metrics, three dashboards, health split.' },
  @{ title = 'Stage 3: Service levels';                description = 'Two SLOs, error budget panel, burn-rate alerts, saturation point.' },
  @{ title = 'Stage 4: Failure drills and runbook';    description = 'Ten drills, ten incident notes, runbook.' },
  @{ title = 'Stage 5: Data layer at scale';           description = '5M rows, query tuning, timestamptz, partitioning, analytics mart.' },
  @{ title = 'Stage 6: Kubernetes deployment';         description = 'Helm chart on kind, probes and limits, two replicas, ECS vs K8s comparison.' },
  @{ title = 'Stage 7: Hardening';                     description = 'Review findings fixed with measurements; async ingest; DB-backed queue.' },
  @{ title = 'Stage 8: Release engineering and recovery'; description = 'Staging, canary, zero-downtime migration, DR with RPO/RTO, architecture docs.' }
)

$existingMilestones = gh api "repos/$repo/milestones?state=all&per_page=100" --jq '.[].title'
foreach ($m in $milestones) {
  if ($existingMilestones -contains $m.title) { Write-Host "milestone exists: $($m.title)"; continue }
  Write-Host "create milestone: $($m.title)"
  if (-not $DryRun) {
    gh api "repos/$repo/milestones" -f title="$($m.title)" -f description="$($m.description)" | Out-Null
  }
}

# -------------------------------------------------------------------- labels
$labels = @(
  @{ name = 'infra';         color = '1d76db'; description = 'Terraform, AWS, networking' },
  @{ name = 'observability'; color = '0e8a16'; description = 'Logs, metrics, traces, dashboards, alerts' },
  @{ name = 'reliability';   color = 'd93f0b'; description = 'SLOs, drills, incidents, runbooks' },
  @{ name = 'data';          color = '5319e7'; description = 'Postgres, migrations, partitioning, analytics' },
  @{ name = 'kubernetes';    color = '006b75'; description = 'Helm, kind, probes, scaling' },
  @{ name = 'hardening';     color = 'b60205'; description = 'Fixes from the production-readiness review' },
  @{ name = 'release';       color = 'fbca04'; description = 'Environments, canary, migrations as a step, DR' },
  @{ name = 'docs';          color = 'c5def5'; description = 'ADRs, architecture, runbooks, write-ups' },
  @{ name = 'drill';         color = 'e99695'; description = 'A failure caused on purpose; produces an incident note' },
  @{ name = 'load';          color = 'bfdadc'; description = 'k6 and performance measurement' }
)
$existingLabels = gh label list --limit 200 --json name --jq '.[].name'
foreach ($l in $labels) {
  if ($existingLabels -contains $l.name) { continue }
  Write-Host "create label: $($l.name)"
  if (-not $DryRun) { gh label create $l.name --color $l.color --description $l.description | Out-Null }
}

# -------------------------------------------------------------------- issues
# title, milestone, labels, body
$issues = @(
  # Stage 0
  @('k6 ingest script: steady 50 rps, bursts to 500, 10% to a slow destination', 'Stage 0: Load baseline', 'load',
    "Done when: ``load/ingest.js`` runs against the local compose stack; a mock destination that sleeps 5 s receives 10% of traffic; thresholds defined for p95 and error rate.`n`nRead first: k6 scenarios and thresholds; coordinated omission (Gil Tene)."),
  @('Record the load baseline: p50, p95, p99 per level in load/README.md', 'Stage 0: Load baseline', 'load,docs',
    "Done when: ``load/README.md`` has a table of latency percentiles and error rate at 50, 100, 250 and 500 rps, plus the point where p99 breaks and what broke first (CPU, DB, forwarder timeouts).`n`nThis baseline is re-run in Stage 7 before and after hardening."),

  # Stage 1
  @('Draw the target architecture: C4 context and container diagrams (c4-week1.md)', 'Stage 1: Infrastructure as code', 'docs',
    "Done when: ``docs/architecture/c4-week1.md`` shows HookVault as it will be at Stage 8 (API, worker, UI, Postgres, queue, Collector/Grafana, ALB, identity provider) with one reading sentence under each diagram. Drawn **before** the first Terraform resource; never edited afterwards."),
  @('ADR-0001: ECS Fargate rather than Kubernetes for the first deployment', 'Stage 1: Infrastructure as code', 'docs',
    "Done when: ``docs/architecture/adr/0001-ecs-fargate-over-kubernetes.md`` has Options, Decision and Consequences in the maintainer's words and status ``accepted``."),
  @('AWS budget alerts and Terraform IAM profile', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: AWS Budgets alerts at £10, £20 and £40 confirmed by email; Cost Explorer enabled; a named profile for Terraform with programmatic access and no root keys."),
  @('Terraform: VPC with public and private subnets, NAT, route tables', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: ``terraform apply`` creates the network; ``plan`` is clean afterwards; the Miribeth estate is the spec.`n`nRead first: HashiCorp Get started on AWS; VPC subnets and NAT."),
  @('Terraform: ECR, ECS Fargate service, ALB with TLS, IAM task roles', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: the HookVault image from ECR runs as a Fargate service behind the ALB; the task reaches Parameter Store and RDS through its task role, with no keys in the image; ``/api/health`` is green through the ALB."),
  @('Terraform: RDS Postgres and SSM Parameter Store secrets', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: RDS Postgres in the private subnets; ``DATABASE_URL`` and ``HOOKVAULT_JWT_SECRET`` injected from Parameter Store as secrets, never in state as plain variables where avoidable. Record what does end up in state and why (ADR or note)."),
  @('Remote state in S3 with locking; terraform plan on every pull request', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: state in an S3 bucket with locking; ``.github/workflows/infra-plan.yml`` runs ``fmt -check``, ``validate``, ``tflint`` and ``plan`` on PRs touching ``infra/``; ``apply`` remains manual for now.`n`nRead first: Terraform state, backends, locking."),
  @('Split infra into modules: network, service, database', 'Stage 1: Infrastructure as code', 'infra',
    "Done when: three modules with explicit inputs and outputs; a note in ``infra/README.md`` on what the split cost and gained."),
  @('Drill: console drift, lost lock, destroy and recreate from nothing (timed)', 'Stage 1: Infrastructure as code', 'infra,drill',
    "Done when: three short notes in ``infra/README.md``: what ``plan`` showed after a console change; how the lost-lock recovery went; minutes for destroy and full recreate."),

  # Stage 2
  @('Structured JSON logs with a trace id on every line', 'Stage 2: Observability', 'observability',
    "Done when: console output is one JSON object per line; every log line carries ``trace_id`` and ``span_id``; one request can be followed end to end with a single filter in Loki. No secrets or bodies in logs."),
  @('OpenTelemetry traces and metrics through a Collector to the local LGTM stack', 'Stage 2: Observability', 'observability',
    "Done when: ``docker-compose.observability.yml`` runs the OTel Collector and ``grafana/otel-lgtm``; API and ReplayWorker emit spans (ingest, forward, replay attempt) and the existing HookVault meter; a trace of one replay shows the forward call as a child span.`n`nRead first: OpenTelemetry .NET getting started; context propagation."),
  @('Dashboards: RED for ingest, USE for container and Postgres, replay queue depth and age', 'Stage 2: Observability', 'observability',
    "Done when: three dashboards exported to ``dashboards/*.json``; top row of each answers 'is it healthy'; the replay dashboard shows queue depth, age of oldest queued item, and retries. Screenshots under load in ``docs/observability.md``.`n`nRead first: the RED method (Wilkie); the USE method (Gregg); ``histogram_quantile``."),
  @('Split health: cheap /healthz and /readyz, statistics to authenticated /api/stats (review finding 5)', 'Stage 2: Observability', 'observability,hardening',
    "Done when: ``/healthz`` returns without touching the database; ``/readyz`` checks the database with ``SELECT 1`` and pending migrations; ``COUNT(*)`` and ``MIN(ReceivedAt)`` move to ``/api/stats`` behind ``[Authorize]``; Docker, the ALB target group and (later) Kubernetes use the right one each. Tests updated."),
  @('docs/observability.md: how to find one event in logs, metrics and traces', 'Stage 2: Observability', 'observability,docs',
    "Done when: the doc walks through finding a given webhook id in Loki, then its trace in Tempo, then the panel it moved, in that order, with the exact queries."),

  # Stage 3
  @('Define two SLOs with SLIs as PromQL (docs/slo.md)', 'Stage 3: Service levels', 'reliability,docs',
    "Done when: ``docs/slo.md`` defines ingest availability (99.9% over 30 days, measured at the ALB) and replay latency (95% delivered within 5 s), each with the SLI expression, the window, and what the error budget is in minutes.`n`nRead first: SRE Workbook, Implementing SLOs."),
  @('Error-budget panel and fast/slow burn-rate alert rules', 'Stage 3: Service levels', 'reliability,observability',
    "Done when: a Grafana panel shows remaining budget for the month; alert rules in ``alerts/`` for fast burn (1 h at 14x) and slow burn (6 h at 6x); a forced burn with k6 makes the fast-burn alert fire, with the time-to-fire recorded.`n`nRead first: SRE Workbook, Alerting on SLOs."),
  @('Find the saturation point and name what breaks first', 'Stage 3: Service levels', 'reliability,load',
    "Done when: load raised until p99 breaks the SLO; the first saturated resource named with evidence (pool wait, CPU, channel full, forwarder timeouts); recorded in ``load/README.md``."),

  # Stage 4 (drills)
  @('Drill 1: kill Postgres mid-load and mid-bulk-replay', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes review findings 2 and 8. Done when: ``drills/01-kill-postgres.ps1`` exists; an incident note records what the dashboards showed, how long to alert, whether HookVault recovered alone, and which queued replays were lost."),
  @('Drill 2: connection pool of 2 under load', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes finding 8. Done when: the pool wait is visible in a trace span and in the incident note."),
  @('Drill 3: drop the ReceivedAt index at 1M rows', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Done when: p99 before and after, the plan from ``EXPLAIN ANALYZE`` showing the seq scan, and the incident note."),
  @('Drill 4: replays against a destination that sleeps 30 s', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes findings 1 and 2 (head-of-line blocking). Done when: the queue dashboard catches it before the error rate does, and the note says by how long."),
  @('Drill 5: remove the forwarder timeout and repeat drill 4', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Done when: the note explains what got worse and why timeouts exist at every hop."),
  @('Drill 6: long transaction holding a row lock under load', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Done when: ``pg_stat_activity`` and ``pg_locks`` output in the note; ``idle in transaction`` explained."),
  @('Drill 7: 128 MB container, large payloads until OOM-killed', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes finding 6. Done when: exit code 137 observed and explained; memory panel screenshot; the note says why the body cap did not help."),
  @('Drill 8: deploy a broken config value, roll back in under 5 minutes', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Done when: rollback time recorded; the note says what signal told you to roll back and what you would automate."),
  @('Drill 9: destination down for an hour, then bulk-replay 20,000 failures', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes finding 3. Done when: the blocked request, the ALB 502 and the partial enqueue are all in the note with timestamps."),
  @('Drill 10: 10,000 identical webhooks at once', 'Stage 4: Failure drills and runbook', 'reliability,drill',
    "Exposes finding 4. Done when: the number of duplicate rows created is in the note, with the query that found them."),
  @('docs/runbook.md: alert, meaning, first three commands, mitigation', 'Stage 4: Failure drills and runbook', 'reliability,docs',
    "Done when: every alert from Stage 3 and every drill has an entry; a stranger could follow it. Linux fluency items from the drills (``ss``, ``top``, ``dmesg``, ``journalctl``, ``pg_stat_activity``) appear as the actual commands."),

  # Stage 5
  @('Generate 5M events; find and fix the three slowest queries (docs/query-tuning.md)', 'Stage 5: Data layer at scale', 'data',
    "Done when: ``pg_stat_statements`` top three identified; each fixed with an index or a rewrite; before-and-after ``EXPLAIN ANALYZE`` plans in the doc with buffers.`n`nRead first: Use The Index, Luke; Postgres Using EXPLAIN."),
  @('Store timestamps as timestamptz on Postgres (review finding 9) with an ADR', 'Stage 5: Data layer at scale', 'data,docs',
    "Done when: provider-specific mapping (``timestamptz`` on Npgsql, ticks on SQLite); a migration that converts existing rows; tests on both providers; ADR written."),
  @('Partition the event table by day; retention as partition drops', 'Stage 5: Data layer at scale', 'data',
    "Done when: range partitioning by day with automatic partition creation; retention drops partitions instead of deleting rows; measured effect on the list query and on deletes.`n`nRead first: Postgres table partitioning."),
  @('Reproduce a deadlock between two replays and fix it with lock ordering', 'Stage 5: Data layer at scale', 'data,drill',
    "Done when: the Postgres deadlock log line is in an incident note; the fix explained in terms of lock order."),
  @('Batch the retention count-cap delete', 'Stage 5: Data layer at scale', 'data',
    "Done when: deletes run in batches with a bound on rows per statement and no giant ``IN`` list; tested at 1M rows."),
  @('Replay analytics mart behind Metabase', 'Stage 5: Data layer at scale', 'data',
    "Done when: views or dbt models answering three questions (replays per destination per hour, failure rate by provider, time-to-forward percentiles) using window functions; a Metabase dashboard over them."),

  # Stage 6
  @('Dockerfile review: layer order, size, non-root, signals', 'Stage 6: Kubernetes deployment', 'kubernetes',
    "Done when: image size and build time before and after recorded; PID 1 handles SIGTERM (graceful shutdown of the worker verified)."),
  @('Helm chart on kind: Deployment, Service, Ingress, ConfigMap, Secret, probes, limits, HPA', 'Stage 6: Kubernetes deployment', 'kubernetes',
    "Done when: ``deploy/helm/hookvault`` installs on kind; ``/healthz`` and ``/readyz`` wired to the right probes; requests and limits set; HPA scales under the k6 script; a note on why CPU is the wrong signal for the worker and what to use."),
  @('Set probes and limits wrong on purpose and record what Kubernetes does', 'Stage 6: Kubernetes deployment', 'kubernetes,drill',
    "Done when: readiness that always fails, liveness that flaps, memory limit too low: each observed and written up."),
  @('Run two replicas and document what breaks', 'Stage 6: Kubernetes deployment', 'kubernetes,reliability',
    "Exposes finding 2 (duplicate processing) and the single-process SSE notifier. Done when: both failures observed and written up as input to Stage 7."),
  @('docs/request-path.md: one request traced from curl to the container', 'Stage 6: Kubernetes deployment', 'docs',
    "Done when: ``dig``, ``openssl s_client``, ``curl -v`` and a ``tcpdump`` or Wireshark capture of the handshake, each explained in one paragraph."),
  @('docs/ecs-vs-k8s.md: the same service on both, compared from experience', 'Stage 6: Kubernetes deployment', 'docs',
    "Done when: what each made easy and hard, cost, operational load, and when you would pick which. Confirms or supersedes ADR-0001."),

  # Stage 7
  @('Async ingest: persist, return 202, forward through the worker (review finding 1)', 'Stage 7: Hardening', 'hardening',
    "Done when: ingest p99 no longer tracks destination latency under the Stage 0 script; response reports ``Queued``; UI live feed shows the outcome; tests updated. Measure before and after."),
  @('Database-backed replay queue with SKIP LOCKED and concurrent workers (finding 2) with an ADR', 'Stage 7: Hardening', 'hardening,data,docs',
    "Done when: ``Queued`` state with ``next_attempt_at`` and ``locked_until``; workers claim with ``SELECT ... FOR UPDATE SKIP LOCKED``; N workers with a per-destination cap; retries scheduled, not slept; the Channel kept only as a wake-up; two replicas drain one backlog with zero duplicates; drills 1, 4 and 9 re-run.`n`nRead first: Postgres SKIP LOCKED; transactional outbox."),
  @('Bulk replay as a single UPDATE (finding 3)', 'Stage 7: Hardening', 'hardening',
    "Done when: ``POST /api/events/replay-failed`` marks rows ``Queued`` in one statement and returns the count; no bodies loaded; drill 9 re-run."),
  @('Unique index for dedup on (Provider, ProviderEventId) with unique-violation handling (finding 4)', 'Stage 7: Hardening', 'hardening,data',
    "Done when: partial unique index where ``ProviderEventId`` is not null; the race handled by catching the violation and returning the existing row; drill 10 re-run with zero duplicates."),
  @('Body cap before buffering; raw-body middleware scoped to /api/ingest (finding 6)', 'Stage 7: Hardening', 'hardening',
    "Done when: ``Content-Length`` checked first; Kestrel ``MaxRequestBodySize`` set; drill 7 re-run without an OOM."),
  @('EnableRetryOnFailure, command timeouts, pool sizing (finding 8)', 'Stage 7: Hardening', 'hardening,data',
    "Done when: a one-second Postgres restart under load produces no 500s; drills 1 and 2 re-run."),
  @('Idempotency keys on mutating management endpoints; token-bucket rate limit on ingest per provider; ETag on event detail', 'Stage 7: Hardening', 'hardening',
    "Done when: duplicate ``POST`` with the same key returns the first result; 429 with ``Retry-After`` under burst; conditional GET returns 304; load test before and after."),
  @('Authorization code with PKCE for the UI against a real identity provider', 'Stage 7: Hardening', 'hardening',
    "Done when: the UI logs in via Keycloak (Docker) or Auth0; the API validates the issuer's keys; the ``generate-token`` CLI remains for automation; ADR on the auth model."),
  @('Re-run the Stage 0 baseline after hardening and record the delta', 'Stage 7: Hardening', 'hardening,load',
    "Done when: the same k6 script, same levels, numbers side by side in ``load/README.md``."),

  # Stage 8
  @('Staging environment from the same Terraform; deploy to staging on merge, production on tag', 'Stage 8: Release engineering and recovery', 'release,infra',
    "Done when: a second environment directory or workspace; pipeline promotes by tag; a note on what differs between environments and why."),
  @('Migrations as a pipeline step; app refuses to start with pending migrations (finding 7)', 'Stage 8: Release engineering and recovery', 'release,data',
    "Done when: ``MigrateAsync`` removed from startup; a migration job runs before rollout; the readiness probe fails on pending migrations; rolling update on kind verified safe."),
  @('Canary deploy: 10% of traffic, dashboards split by version, promote or roll back by one command', 'Stage 8: Release engineering and recovery', 'release',
    "Done when: two target groups or a weighted service; a bad canary rolled back from the dashboard signal alone.`n`nRead first: blue-green versus canary."),
  @('Feature flag in front of one replay behaviour, toggled without a deploy', 'Stage 8: Release engineering and recovery', 'release',
    "Done when: the flag source is documented; toggle observed in the dashboard without a restart."),
  @('Expand-and-contract: rename a column on the live event table under load with zero errors', 'Stage 8: Release engineering and recovery', 'release,data',
    "Done when: three deploys (add new, dual-write and backfill, drop old) with the k6 script running throughout and zero errors.`n`nRead first: expand and contract migrations."),
  @('Destroy the production database and restore it; measure RPO and RTO (docs/dr-runbook.md)', 'Stage 8: Release engineering and recovery', 'release,reliability,docs',
    "Done when: restore from an RDS snapshot or PITR; both numbers measured and written down with the commands; what would make each smaller."),
  @('Git drill: bisect a regression five commits back, revert, rebase across a conflict', 'Stage 8: Release engineering and recovery', 'release',
    "Done when: a short note of the commands used and what each does."),
  @('Dependency and container scan; triage findings; upgrade to current .NET LTS', 'Stage 8: Release engineering and recovery', 'release,hardening',
    "Done when: scanner output triaged into real and noise with reasons; .NET 8 (out of support 10 Nov 2026) replaced by the current LTS; EF Core aligned; CI green."),
  @('docs/architecture: redraw C4 and diff against week 1; tidy the ADR set; write overview.md', 'Stage 8: Release engineering and recovery', 'docs',
    "Done when: ``c4-current.md`` with a written diff against ``c4-week1.md``; ADR index complete; ``overview.md`` is one page a new engineer could operate from."),
  @('Re-record the walkthrough as a 15-minute architecture talk', 'Stage 8: Release engineering and recovery', 'docs',
    "Done when: Terraform, a deploy, the dashboards under load, one drill live, the runbook, and the three decisions you would make differently.")
)

$existingIssues = gh issue list --state all --limit 500 --json title --jq '.[].title'
$created = 0
foreach ($i in $issues) {
  $title, $milestone, $labelCsv, $body = $i
  if ($existingIssues -contains $title) { Write-Host "issue exists: $title"; continue }
  Write-Host "create issue: [$milestone] $title"
  if (-not $DryRun) {
    gh issue create --title $title --milestone $milestone --label $labelCsv --body $body | Out-Null
    $created++
    Start-Sleep -Milliseconds 400   # be polite to the API
  }
}
Write-Host "Done. Created $created issues."
