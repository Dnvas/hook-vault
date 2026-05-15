/**
 * E2E: Event detail panel — click event shows all four sections
 *
 * Prerequisites:
 *   - app running at http://localhost:5000
 *   - at least one event already captured
 *
 * Scenario:
 *   1. Navigate to /?token=<valid_token>
 *   2. Click the first event row in the left panel
 *   3. Assert right panel shows:
 *      - Provider name and path in the header
 *      - "↺ Replay" button
 *      - "BODY" section header (uppercase, indigo)
 *      - "HEADERS" section header
 *      - "VALIDATION" section header
 *      - "FORWARD" section header
 *   4. If signature was configured: assert green/red badge in Validation section
 *   5. If no validation: assert "No validation configured" text
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → /?token=...
 *   - browser_click → first event row
 *   - browser_snapshot → assert panel content
 */
export {}
