/**
 * E2E: Authentication flow
 *
 * Prerequisites: app running at http://localhost:5000 (or 7777 in Docker)
 * Use Playwright MCP tools to verify these scenarios.
 *
 * Scenario 1 — no token: navigate to / without a token in URL or sessionStorage.
 *   Expected: TokenGate card visible with "HookVault" heading and token input.
 *
 * Scenario 2 — URL token: navigate to /?token=<valid_token>.
 *   Expected: token stripped from URL, split pane (Events header) visible.
 *
 * Scenario 3 — invalid token entered in TokenGate: paste an empty string, click Continue.
 *   Expected: "Token is required" error message shown.
 *
 * Scenario 4 — after providing a valid token via TokenGate: split pane visible.
 *
 * How to run:
 *   1. `dotnet run --project src/HookVault` (note the ?token= URL in the logs)
 *   2. Use mcp__plugin_playwright_playwright__browser_navigate to http://localhost:5000
 *   3. Use mcp__plugin_playwright_playwright__browser_snapshot to assert page state
 *   4. Use mcp__plugin_playwright_playwright__browser_fill_form / browser_click as needed
 */
export {}
