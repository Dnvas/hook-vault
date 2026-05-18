import { test, expect } from "../fixtures";

test("TokenGate shows error when submitted empty", async ({ page }) => {
    await page.goto("/");
    // The brand name is rendered in a <span>, not a heading element.
    // The semantic <h2> on the gate reads "Access token required".
    await expect(
        page.getByRole("heading", { name: /access token required/i }),
    ).toBeVisible();

    // Submit with empty input — React handler calls e.preventDefault() so no
    // browser-native HTML5 validation fires; the custom error string is rendered.
    await page.getByRole("button", { name: /continue/i }).click();

    await expect(page.getByText(/token is required/i)).toBeVisible();
});
