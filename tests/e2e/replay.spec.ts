/**
 * E2E: Replay button transitions event status
 *
 * Prerequisites:
 *   - app running at http://localhost:5000
 *   - at least one ForwardFailed event (POST to a non-reachable forwardUrl)
 *
 * Scenario:
 *   1. Navigate to /?token=<valid_token>
 *   2. Find a row with "ForwardFailed" status indicator (red left border)
 *   3. Click the row to open event detail
 *   4. Assert "↺ Replay" button is visible
 *   5. Click "↺ Replay"
 *   6. Assert the row status transitions to "Replaying" or "Forwarded"
 *      (the list invalidates after replay click)
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → /?token=...
 *   - browser_click → ForwardFailed event row
 *   - browser_click → Replay button
 *   - browser_wait_for + browser_snapshot → assert status change
 */
export {}
