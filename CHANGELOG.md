# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `EventRetentionWorker` background service. Configure with
  `HOOKVAULT_MAX_EVENTS`, `HOOKVAULT_RETENTION_DAYS`,
  `HOOKVAULT_RETENTION_INTERVAL_SECONDS` (defaults: unset, unset, 300).
- Svix multi-header HMAC scheme. Set `"scheme": "svix"` in a provider's
  `validation` block to verify Resend / Clerk / PostHog webhooks.
- Webhook deduplication. Opt in per provider with `dedupEventIdHeader`;
  duplicates within a 24h window return the existing event id.
- `?bodyContains=` and `?providerEventId=` filters on `GET /api/events`.
- `SECURITY.md` describing the vulnerability-reporting policy.
- Bug-report and feature-request issue templates.

### Changed
- License: AGPL-3.0 → Apache-2.0.

### Migration notes
- Schema migration `00000000000002_DedupColumns` adds two nullable
  columns (`BodyHash`, `ProviderEventId`) to the `Events` table. SQLite
  upgrades in place. PostgreSQL upgrades in place. No data loss.

## [0.1.0] — 2026-05-15

Initial pre-release. See git history for the phased build:
- Phase 1 — Foundation
- Phase 2 — Replay system
- Phase 3 — Management API + JWT auth
- Phase 4 — Docker + example provider configs
- Phase 5 — README + CONTRIBUTING + AGPL-3.0 license
- Phase 6 — React UI
- Hardening sprint PR 1 — six correctness bugs
- Hardening sprint PR 2 — security hardening
- Hardening sprint PR 3 — architecture + OSS readiness (this release)
