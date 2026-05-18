import { test, expect } from '../fixtures';

test('ingested event appears in live feed', async ({ page, api }) => {
  await api.ingestStripe(JSON.stringify({
    event: 'payment_intent.succeeded',
    id: 'evt_ui_1',
  }));

  await page.goto('/?token=e2e');

  // EventRow renders inside the scrollable events container (div.overflow-y-auto).
  // Each row is a <button> whose text content starts with the provider name.
  // Scoping to the scroll container excludes the provider filter pills (which
  // are outside it), giving a tight, unambiguous match.
  const eventRow = page
    .locator('div.overflow-y-auto button')
    .filter({ hasText: /stripe/i })
    .first();

  await expect(eventRow).toBeVisible({ timeout: 10_000 });
});
