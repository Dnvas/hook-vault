/**
 * E2E: /api/health surfaces retention sweep state
 *
 * Prerequisites:
 *   - app started with HOOKVAULT_MAX_EVENTS and/or HOOKVAULT_RETENTION_DAYS
 *     set to a small value (e.g. HOOKVAULT_MAX_EVENTS=5)
 *   - HOOKVAULT_RETENTION_INTERVAL_SECONDS=10 to see sweeps fire quickly
 *   - listener bound to http://localhost:7777
 *
 * Scenario 1 — retention configured, idle (no sweep run yet):
 *   1. Within ~5 seconds of startup, curl /api/health
 *   2. Expect: response includes a "retention" object with:
 *      - maxEvents: 5 (or whatever value)
 *      - retentionDays: null (when only count cap is set)
 *      - lastSweepAt: null (no sweep has run yet)
 *      - lastSweepDeleted: 0
 *
 * Scenario 2 — retention configured, sweep has run:
 *   1. Send 10 webhook events via /api/ingest/<provider>
 *   2. Wait > HOOKVAULT_RETENTION_INTERVAL_SECONDS for the worker to fire
 *   3. curl /api/health
 *   4. Expect: response.retention.lastSweepAt is a non-null ISO timestamp;
 *      lastSweepDeleted equals the number of events past the cap (5 in
 *      this case: 10 ingested - 5 cap = 5 deleted on first sweep).
 *
 * Scenario 3 — retention NOT configured (both env vars unset):
 *   1. Start the app without HOOKVAULT_MAX_EVENTS or HOOKVAULT_RETENTION_DAYS
 *   2. curl /api/health
 *   3. Expect: response includes "retention": null
 *
 * How to verify via curl:
 *
 *   curl -s http://localhost:7777/api/health | jq .retention
 *
 *   When idle without caps:  null
 *   When caps configured:    { "maxEvents": 5, "retentionDays": null,
 *                              "lastSweepAt": null|"...",
 *                              "lastSweepDeleted": 0|N }
 *
 * This endpoint is unauthenticated (health is public), so no token needed.
 */
export {}
