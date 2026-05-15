/**
 * E2E: Provider and status filters narrow the event list
 *
 * Prerequisites:
 *   - app running at http://localhost:5000
 *   - events from at least two different providers captured
 *
 * Scenario A — provider filter:
 *   1. Navigate to /?token=<valid_token>
 *   2. Assert all events visible (no filter active)
 *   3. Click the "stripe" provider pill
 *   4. Assert only stripe events are visible (non-stripe rows gone)
 *   5. Click "all" pill to restore all events
 *
 * Scenario B — status filter:
 *   1. Click the "ForwardFailed" status pill
 *   2. Assert only ForwardFailed events visible
 *   3. Click "all" status pill to restore
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → /?token=...
 *   - browser_snapshot → note all providers in list
 *   - browser_click → "stripe" filter pill
 *   - browser_snapshot → assert filtered view
 *   - browser_click → "all" filter pill
 *   - browser_click → "ForwardFailed" status pill
 *   - browser_snapshot → assert filtered view
 */
export {}
