# Documentation index

Start with the roadmap; everything else is produced by one of its stages.

| Document | What it is | Produced in |
| --- | --- | --- |
| [ROADMAP.md](ROADMAP.md) | Target state, nine stages, stage status | now |
| [production-readiness-review.md](production-readiness-review.md) | The findings that motivate the roadmap, with the drill that exposes each | now |
| [architecture/](architecture/README.md) | C4 diagrams, the ADR set, the one-page system overview | Stage 1 onward |
| [incidents/](incidents/README.md) | One note per failure drill or real incident | Stage 4 |
| observability.md | How to find one event in logs, metrics and traces | Stage 2 |
| slo.md | The two SLOs, their SLIs as PromQL, alert rules | Stage 3 |
| runbook.md | Alert → meaning → first three commands → mitigation | Stage 4 |
| query-tuning.md | The three slowest queries, plans before and after | Stage 5 |
| request-path.md | One request traced from `curl` to the container | Stage 6 |
| ecs-vs-k8s.md | The same service on both, compared from experience | Stage 6 |
| dr-runbook.md | Backup, restore, measured RPO and RTO | Stage 8 |
| [architecture-audit-v1.0.md](architecture-audit-v1.0.md) | The v1.0 audit this work builds on | historical |
| superpowers/, ../plans/ | Specs and plans from the v0.x and v1.0 work | historical |

Documents listed without a link do not exist yet. They are created by the maintainer when their stage runs; the roadmap says what each must contain.
