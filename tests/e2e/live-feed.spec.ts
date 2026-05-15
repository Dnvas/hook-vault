/**
 * E2E: Live feed — event appears without page refresh
 *
 * Prerequisites:
 *   - app running at http://localhost:5000
 *   - a provider named "stripe" configured in hookvault.json
 *
 * Scenario — POST a webhook, expect it appears in the list within 3 seconds:
 *   1. Navigate to /?token=<valid_token>
 *   2. Assert split pane with "Events" header is visible
 *   3. POST to /api/ingest/stripe (via fetch in the browser or external curl)
 *   4. Wait up to 3 seconds for the event row to appear (SSE invalidation)
 *   5. Assert a row with "stripe" text is visible — no page reload required
 *
 * Key assertion: the event list updates via SSE without a manual refresh.
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → /?token=...
 *   - browser_snapshot → confirm "Events" heading visible
 *   - browser_network_request → POST /api/ingest/stripe with body {"type":"test"}
 *   - browser_wait_for + browser_snapshot → assert "stripe" row visible
 */
export {}
