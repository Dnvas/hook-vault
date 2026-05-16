/**
 * E2E: bodyContains search returns totalApproximate (UI footer adapts)
 *
 * Prerequisites:
 *   - app running at http://localhost:7777
 *   - at least 6 events captured across multiple providers
 *
 * Scenario 1 — no body filter, exact count shown:
 *   1. Navigate to /?token=<valid_token>
 *   2. Confirm the footer reads "N events" with N being a number
 *      (e.g. "6 events")
 *   3. API GET /api/events returns { "total": 6, "totalApproximate": false }
 *
 * Scenario 2 — body filter active, count hidden:
 *   Skipping the UI half because the React app doesn't expose a body-search
 *   text input in v0.3 (the filter is API-only for now — UI follow-up
 *   in v0.4). Verify via direct API:
 *
 *   1. curl -H "Authorization: Bearer <token>" \
 *        "http://localhost:7777/api/events?bodyContains=evt"
 *   2. Expect: JSON response with shape { items: [...], total: null,
 *      totalApproximate: true, limit: 50, offset: 0 }
 *   3. Items contain only events whose UTF-8-decoded body contains
 *      "evt" (case-insensitive).
 *
 * Scenario 3 — UI footer when bodyContains is set via query string:
 *   Since the React app doesn't currently call /api/events with bodyContains,
 *   you can simulate by editing the URL or calling the API directly. When
 *   a future UI input lands (v0.4 fixtures library or v0.4 search bar),
 *   verify the footer renders "N shown — refine to count exactly" in
 *   place of the numeric count.
 *
 * How to run interactively:
 *   curl -H "Authorization: Bearer $TOKEN" \
 *     "http://localhost:7777/api/events?bodyContains=evt" | jq .
 *
 *   Expected JSON keys: items, total (null), totalApproximate (true),
 *                       limit, offset.
 */
export {}
