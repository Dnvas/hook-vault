# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] — 2026-05-16

First public release. Closes the hardening sprint (three merged PRs) and
publishes the first Docker image to GitHub Container Registry.

### Added

- **Webhook deduplication** — opt-in per provider via `dedupEventIdHeader`.
  Captures the same provider event id + body hash within a 24h window
  return the existing event with `duplicate: true`. Schema adds `BodyHash`
  and `ProviderEventId` columns (indexed).
- **Svix multi-header HMAC scheme** — set `"scheme": "svix"` in a
  provider's `validation` block to verify Resend / Clerk / PostHog
  webhooks. Handles the `whsec_` secret prefix and the space-separated
  multi-signature key-rotation format.
- **`EventRetentionWorker` background service** — prunes captured events
  past env-var caps. Configure with `HOOKVAULT_MAX_EVENTS`,
  `HOOKVAULT_RETENTION_DAYS`, `HOOKVAULT_RETENTION_INTERVAL_SECONDS`
  (defaults: unset, unset, 300).
- **Search filters** — `GET /api/events` accepts `?bodyContains=`
  (case-insensitive UTF-8 substring) and `?providerEventId=` (exact match).
- **Replay-attack window** — optional `validation.maxAgeSeconds` rejects
  signatures whose timestamp is older than the configured age.
- **Ingest body cap** — `HOOKVAULT_MAX_BODY_BYTES` rejects oversize
  requests with `413 Payload Too Large` before persistence.
- **Multi-segment provider paths** — `path: "/stripe/v2"` now matches.
  Ingest route uses ASP.NET Core's `{**provider}` catch-all.
- **Startup recovery sweep** — orphaned `Replaying` events (left by a
  mid-replay crash) transition to `ForwardFailed` on next startup.
- **EF Core migrations** — replace `EnsureCreated`. Pre-existing dev DBs
  get a backfilled `__EFMigrationsHistory` row so `Migrate()` doesn't
  attempt to recreate existing tables.
- **`IIngestSignatureScheme` dispatcher** — `SignatureValidator` resolves
  the scheme from `validation.scheme` (default `"single-header"`). Sets
  the stage for new schemes (Svix; future RSA/ed25519).
- **GHCR release workflow** — `v*` tag push builds and pushes
  `ghcr.io/<owner>/hookvault:<tag>` + `:latest`.
- **`SECURITY.md`** documents the local-dev-tool threat model and the
  email-based vulnerability reporting flow.
- **GitHub issue templates** for bug reports and feature requests.
- **README screenshots** of the events list and validation-debug detail
  view.

### Changed

- **License: AGPL-3.0 → Apache-2.0.**
- **`WebhookEvent.Body` is now stored as `byte[]` (BLOB)** instead of
  `string`. Binary webhook payloads (multipart, protobuf, anything with
  non-UTF-8 bytes) now round-trip byte-equal. The API contract is
  unchanged — the JSON response still exposes `body` as a UTF-8 string.
- **`WebhookEvent.Headers` JSON shape** is now
  `Dictionary<string, string[]>` so multi-value headers (`Set-Cookie`,
  repeated `Forwarded`) preserve their values. The API response still
  flattens to a comma-joined dict for back-compat.
- **JWT secret minimum bumped from 32 to 48 bytes.** A 32-byte minimum
  meets the HS256 requirement but accepts passphrase-style secrets;
  48 bytes lines up with `openssl rand -hex 32` and `openssl rand -base64 36`.
- **Startup admin token is now 1-hour, Development-only.** Production
  environments stay silent; users mint long-lived tokens via the
  `generate-token` CLI subcommand.
- **`SignatureValidator` refactored into a dispatcher** with the existing
  single-header HMAC logic moved verbatim into `SingleHeaderHmacScheme`.

### Fixed

- **SSE fan-out to every subscriber.** The previous implementation used a
  single shared `Channel<T>` which delivered each message to exactly one
  reader. With multiple browser tabs, each event only updated one of
  them at random. `EventNotifier` now hands each subscriber its own
  channel; `Notify` fans out to every active subscription.
- **SSE heartbeat every 15 seconds.** Reverse proxies (nginx default 60s,
  ALB default 60s) previously dropped idle EventSource connections.
- **`cs/log-forging` in `EventsController.Purge` / `ReplayFailed` and
  `MaxBodySizeMiddleware.InvokeAsync`** — user-controlled query / path
  values now sanitised before logging.

### Security

- **Sensitive headers redacted at storage.** `Authorization`, `Cookie`,
  and `Proxy-Authorization` are stored as `[redacted]` on the event
  record. The initial forward still uses the live request headers;
  replays don't replay sensitive headers.
- **`validationDetails.computedSignature` redacted outside Development.**
  Opt-in via `HOOKVAULT_EXPOSE_COMPUTED_SIGNATURE=true`. Eliminates the
  (payload, computed) oracle for read-only API consumers.

### Migration notes

- **SQLite upgrades in place.** Migration `00000000000001` converts
  `Body` from `TEXT` to `BLOB` via the recreate-and-copy pattern;
  migration `00000000000002` adds the dedup columns via
  `ALTER TABLE`. No data loss.
- **PostgreSQL: schema upgrade not supported in this release.** The
  bytes-body migration throws `NotSupportedException` on Postgres.
  Postgres users running a v0.1 build must drop and recreate the
  database. Multi-provider migration support is tracked for a future
  release.

## [0.1.0] — 2026-05-15

Pre-release covering the six initial build phases:

- Phase 1 — Foundation (domain models, EF Core, config loader, signature
  validator, ingest controller, forwarder, health endpoint, tests)
- Phase 2 — Replay system (`ReplayQueue`, `ReplayWorker`, retries)
- Phase 3 — Management API + JWT bearer auth
- Phase 4 — Docker + example provider configs (Stripe, GitHub, Shopify,
  Resend, generic HMAC)
- Phase 5 — README + CONTRIBUTING + AGPL-3.0 license
- Phase 6 — React UI (dark-themed split-pane, SSE live feed)
