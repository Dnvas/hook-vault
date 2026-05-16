/**
 * E2E: Body edit + replay flow
 *
 * Prerequisites:
 *   - app running at http://localhost:7777
 *   - at least one event captured with a non-empty body
 *
 * Scenario:
 *   1. Navigate to /?token=<valid_token>
 *   2. Click the first event row
 *   3. Click the "Edit & Replay" button in the detail header
 *   4. Assert a textarea appears, pre-populated with the event's body
 *   5. Modify the textarea content
 *   6. Click "Replay with edits"
 *   7. Assert the textarea collapses (editor closes)
 *   8. Assert ReplayCount increments after the SSE notification arrives
 *
 * How to run interactively with Playwright MCP:
 *   - browser_navigate → /?token=...
 *   - browser_click → first event row
 *   - browser_click → "Edit & Replay" button
 *   - browser_snapshot → confirm textarea is present
 *   - browser_type → edited body text
 *   - browser_click → "Replay with edits"
 *   - browser_wait_for → editor collapsed
 *
 * Or via ui/scripts/screenshots.mjs as the Playwright-script template if MCP is unavailable.
 */
export {}
