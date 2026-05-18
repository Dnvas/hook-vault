import { test, expect } from '../fixtures';

test('ingested event appears in live feed', async ({ page, api }) => {
  await api.ingestStripe(JSON.stringify({
    event: 'payment_intent.succeeded',
    id: 'evt_ui_1',
  }));

  await page.goto('/?token=e2e');

  // data-testid is set on EventRow's root <button> (ui/src/components/EventRow.tsx).
  // Tighter than scoping by container class — survives styling refactors and
  // does not collide with provider filter pills or other Tailwind chrome.
  const eventRow = page
    .getByTestId('event-row')
    .filter({ hasText: /stripe/i })
    .first();

  await expect(eventRow).toBeVisible({ timeout: 10_000 });
});
