# Architecture decision records

One file per decision, numbered, in the order the decisions were made. An ADR is one page: the situation, the options actually considered, the decision, and what it costs. It is written within the hour of deciding, by the maintainer, in plain language. ADRs are never edited after the fact; a changed mind is a new ADR that supersedes the old one.

## Rules

- File name: `NNNN-short-title-in-kebab-case.md`, four digits, next free number.
- Status is one of `proposed`, `accepted`, `superseded by NNNN`, `deprecated`.
- Context states facts and constraints, not opinions. Options are the ones genuinely on the table (two to four). Consequences list what gets harder as well as easier.
- Link the ADR from the pull request that implements it.
- Use the template in `0000-template.md` or the `/adr` command, which fills the frame and leaves the thinking to you.

## Index

| # | Title | Status | Date |
| --- | --- | --- | --- |
| 0001 | ECS Fargate rather than Kubernetes for the first deployment | proposed | 2026-10-19 |

Decisions expected during the roadmap, each to get its own ADR when made: Postgres as the production database; remote state layout and environment separation in Terraform; structured logging and trace propagation approach; the two SLO targets; `timestamptz` and partitioning strategy; the database-backed queue design; dedup uniqueness rule; canary mechanism; backup and restore policy.
