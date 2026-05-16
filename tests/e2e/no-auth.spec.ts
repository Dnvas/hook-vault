/**
 * E2E: HOOKVAULT_NO_AUTH=true bypasses JWT enforcement
 *
 * Prerequisites:
 *   - app started with HOOKVAULT_NO_AUTH=true and a hookvault.json with at
 *     least one provider configured
 *   - listener bound to http://localhost:7777
 *
 * Scenario 1 — UI without a token:
 *   1. Navigate to http://localhost:7777/  (no ?token= query, no sessionStorage)
 *   2. Expect: the TokenGate component still appears (the UI doesn't know
 *      that auth is disabled — it always expects a token for its own API
 *      calls).
 *   3. The TokenGate's "Continue" still requires a non-empty value; pasting
 *      any string (even "anything") and submitting will let the UI through
 *      because the backend accepts the bogus token.
 *
 *   Note: this exposes a small UX gap — when HOOKVAULT_NO_AUTH is on, the
 *   TokenGate is technically unnecessary. A polish improvement would be a
 *   /api/health field exposing auth.required so the UI skips the gate.
 *   Out of scope for v0.3 PR1; tracked as a v0.4 candidate.
 *
 * Scenario 2 — direct API hit:
 *   1. curl -i http://localhost:7777/api/events
 *   2. Expect: HTTP 200 with a JSON body containing items[]
 *      (without HOOKVAULT_NO_AUTH, this would return 401).
 *
 * Scenario 3 — startup warning:
 *   1. Run the app with HOOKVAULT_NO_AUTH=true
 *   2. Expect: stdout contains a `warn` level line matching the pattern:
 *      "HOOKVAULT_NO_AUTH=true: the management API is unauthenticated."
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → http://localhost:7777/
 *   - browser_snapshot → confirm TokenGate visible
 *   - browser_fill_form / browser_click → paste any value, submit
 *   - browser_snapshot → confirm split-pane (Events + detail) visible
 *
 * How to verify the API path without Playwright:
 *   curl -i http://localhost:7777/api/events  # expect 200, not 401
 */
export {}
