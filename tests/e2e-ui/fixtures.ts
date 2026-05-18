import { test as base } from '@playwright/test';
import { createHmac } from 'crypto';

const STRIPE_SECRET = process.env.STRIPE_WEBHOOK_SECRET;
if (!STRIPE_SECRET) {
  throw new Error('STRIPE_WEBHOOK_SECRET must be set (same as docker compose env)');
}

const BASE = process.env.HOOKVAULT_BASE_URL ?? 'http://localhost:7777';

export class ApiHelper {
  constructor(private base: string) {}

  async reset(): Promise<void> {
    const r = await fetch(`${this.base}/api/test/reset`, { method: 'POST' });
    if (!r.ok) throw new Error(`reset returned ${r.status}: ${await r.text()}`);
  }

  async ingestStripe(body: string): Promise<void> {
    const ts = Math.floor(Date.now() / 1000);
    const payload = `${ts}.${body}`;
    const sig = createHmac('sha256', STRIPE_SECRET!).update(payload).digest('hex');
    const r = await fetch(`${this.base}/api/ingest/stripe`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'Stripe-Signature': `t=${ts},v1=${sig}`,
      },
      body,
    });
    if (!r.ok) throw new Error(`ingest returned ${r.status}: ${await r.text()}`);
  }
}

type Fixtures = { api: ApiHelper };

export const test = base.extend<Fixtures>({
  api: async ({}, use) => {
    const api = new ApiHelper(BASE);
    await api.reset();
    await use(api);
  },
});

export { expect } from '@playwright/test';
