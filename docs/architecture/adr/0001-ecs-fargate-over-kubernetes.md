# 0001. Use ECS Fargate rather than Kubernetes for the first production deployment

- **Status:** proposed
- **Date:** 2026-10-19
- **Deciders:** maintainer
- **Related:** Stage 1; revisited in Stage 6 (`docs/ecs-vs-k8s.md`)

## Context

HookVault v1.0 runs as one container with a SQLite volume or a Postgres connection. The first production deployment needs: a container runtime, a load balancer with TLS, a managed Postgres, secrets, and a path to run more than one replica later. The maintainer has run ECS Fargate, RDS and an ALB in production before (Miribeth, 2025 to 2026) and has not run Kubernetes. The budget target is under £25 a month with the environment destroyed between working sessions. Stage 6 of the roadmap deploys the same service to Kubernetes on purpose, so this decision is about where to start, not where to end.

## Options considered

1. **ECS Fargate behind an ALB, RDS Postgres.** _Fill in: what it gives, what it costs, what it hides from you._
2. **EKS (managed Kubernetes).** _Fill in: control-plane cost per hour, what you would have to learn before the first deploy, what you gain._
3. **A single EC2 or the Hetzner VPS with docker compose.** _Fill in: cheapest and simplest; what it does not teach and what it cannot do (no managed failover, no IAM task roles, no load balancer)._

## Decision

_Write the decision in one sentence and the reasons that actually decided it. The honest reasons are allowed to include "I already know it" and "it is cheaper to destroy and recreate"._

## Consequences

- Easier: _e.g. first deploy in week 1; Terraform from a pattern you have seen; IAM task roles instead of keys._
- Harder: _e.g. Kubernetes vocabulary deferred to Stage 6; ECS-specific service discovery and scaling concepts that do not transfer one to one._
- Follow-ups: Stage 6 comparison; this ADR is superseded or confirmed by the result.
