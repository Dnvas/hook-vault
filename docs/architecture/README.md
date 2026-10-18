# Architecture

This folder holds the system as it is understood at a point in time: diagrams, decisions, and the one-page overview. It is written by the maintainer.

## Contents

- `c4-week1.md`: the target architecture drawn **before** Stage 1, as C4 context and container diagrams (Mermaid is fine). Deliberately left unchanged afterwards.
- `c4-current.md`: the architecture as built, redrawn at Stage 8 and diffed against `c4-week1.md`. The diff is the point.
- `overview.md`: one page a new engineer reads on day one: what the system does, the components, how a webhook flows through it, where the data lives, how it is deployed, where to look when it misbehaves. Written at Stage 8.
- `adr/`: architecture decision records, numbered, never deleted.

## C4 in two diagrams

**Context** (level 1): HookVault as one box; the people and systems around it (webhook providers, the destination applications, the operator, the identity provider, AWS services it depends on). Arrows say what flows and over what protocol.

**Container** (level 2): the deployable units inside HookVault (API, replay worker, UI, Postgres, the Collector and Grafana stack, the queue once it exists) and the technology of each. One arrow per real call path.

Component diagrams (level 3) are only drawn for the part of the system under change, for example the replay pipeline during Stage 7.

A diagram without a sentence under it saying how to read it is not finished.
