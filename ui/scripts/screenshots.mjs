// Captures README screenshots from a running HookVault dev instance.
//
// Prereqs:
//   1. App listening on http://localhost:7777 (Docker compose, or `dotnet run`
//      with HOOKVAULT_JWT_SECRET etc. set and --urls http://localhost:7777).
//   2. Dev JWT written to /tmp/hookvault-token (or %TEMP%\hookvault-token on
//      Windows). The startup log line prints the URL; copy the token from it.
//   3. A handful of seeded events so the UI isn't empty. Quickest way is to
//      curl a few /api/ingest/<provider> requests with realistic bodies.
//   4. `npm install --no-save playwright && npx playwright install chromium`
//      from the ui/ directory (playwright is intentionally not a permanent
//      dev-dependency — this script is run ad-hoc when README screenshots
//      need refreshing).
//
// Usage:
//   cd ui && node scripts/screenshots.mjs
//
// Outputs:
//   ../docs/img/screenshot-events.png  (events list + a selected event)
//   ../docs/img/screenshot-detail.png  (validation-failed event highlighted)
import { chromium } from 'playwright'
import { readFileSync, mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const __dirname = dirname(fileURLToPath(import.meta.url))
const REPO = resolve(__dirname, '..', '..')
const IMG_DIR = resolve(REPO, 'docs', 'img')
mkdirSync(IMG_DIR, { recursive: true })

const TOKEN_PATH = process.platform === 'win32'
  ? resolve(process.env.TEMP || 'C:\\Users\\dnvas\\AppData\\Local\\Temp', 'hookvault-token')
  : '/tmp/hookvault-token'
const token = readFileSync(TOKEN_PATH, 'utf8').trim()

const BASE = 'http://localhost:7777'
const URL = `${BASE}/?token=${token}`

const browser = await chromium.launch({ headless: true })
const ctx = await browser.newContext({
  viewport: { width: 1440, height: 900 },
  deviceScaleFactor: 2,
})
const page = await ctx.newPage()

page.on('console', m => {
  if (m.type() === 'error') console.error('[console]', m.text())
})

// SSE keeps a long-lived connection open, so 'networkidle' never fires.
// 'domcontentloaded' is enough; we explicitly wait for the events list below.
await page.goto(URL, { waitUntil: 'domcontentloaded' })

// The default state has no event selected. Wait for the events list to render
// (we seeded 6 events). The first EventRow's clickable element is in the left panel.
await page.waitForSelector('text=Events', { timeout: 5000 })

// Wait until the list shows real rows by waiting for the "6 events" footer AND
// for at least one row that is NOT the empty state.
await page.waitForFunction(
  () => {
    const empty = Array.from(document.querySelectorAll('p')).find(p =>
      p.textContent?.toLowerCase().includes('no events yet'))
    return !empty
  },
  { timeout: 10000 }
).catch(() => console.error('Timed out waiting for events to render — taking screenshot anyway'))

// Settle layout
await page.waitForTimeout(800)

// Event rows are <button> elements that include a HH:MM:SS timestamp; filter
// pills don't. Use the timestamp pattern to disambiguate from the pills.
const eventRows = page.locator('button').filter({ hasText: /\d{2}:\d{2}:\d{2}/ })
const count = await eventRows.count()
console.log('Event row count:', count)
if (count === 0) {
  console.error('No event rows found — aborting before screenshots')
  await browser.close()
  process.exit(1)
}

// Screenshot 1 (hero): click the first valid Stripe event so the right panel
// is populated. Shows the full app: list + selected event with all sections.
await eventRows.first().click()
await page.waitForSelector('text=Replay', { timeout: 5000 })
await page.waitForTimeout(800)

const eventsPath = resolve(IMG_DIR, 'screenshot-events.png')
await page.screenshot({ path: eventsPath, fullPage: false })
console.log('Saved', eventsPath)

// Screenshot 2 (validation closeup): click the event with the failed signature
// (red ✗ badge). The detail panel now shows the validation debug info.
const failedSigRow = eventRows.filter({ has: page.locator('text=✗') }).first()
const failedCount = await failedSigRow.count()
if (failedCount > 0) {
  await failedSigRow.click()
  await page.waitForTimeout(800)
} else {
  // Fall back to the second event if no failed-sig row found.
  await eventRows.nth(1).click()
  await page.waitForTimeout(800)
}

const detailPath = resolve(IMG_DIR, 'screenshot-detail.png')
await page.screenshot({ path: detailPath, fullPage: false })
console.log('Saved', detailPath)

await browser.close()
console.log('Done')
