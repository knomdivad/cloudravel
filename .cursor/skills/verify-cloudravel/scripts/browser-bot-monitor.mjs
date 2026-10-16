#!/usr/bin/env node
import { chromium } from 'playwright';
import fs from 'fs';
import path from 'path';

const runId = process.env.VERIFY_RUN_ID || 'local';
const webUrl = process.env.WEB_URL || 'http://127.0.0.1:3000';
const outDir = path.join(
  process.env.VERIFY_ARTIFACTS_ROOT || '/opt/cursor/artifacts/verify-cloudravel',
  runId,
  'bot-monitor-pages',
);
fs.mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });

await page.goto(webUrl, { waitUntil: 'networkidle' });
await page.getByRole('heading', { name: 'CloudRavel' }).waitFor({ timeout: 30_000 });
await page.getByText('Email', { exact: true }).locator('..').getByRole('textbox').fill('admin@local');
await page.locator('input[type="password"]').fill('ChangeMe123!');
await page.getByRole('button', { name: 'Sign in', exact: true }).click();
await page.getByRole('link', { name: 'Bot fleet' }).waitFor({ timeout: 60_000 });

await page.goto(`${webUrl}/ops/bots`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);
await page.screenshot({ path: path.join(outDir, 'ops-bots.png'), fullPage: true });

const orgSelect = page.locator('select').first();
await orgSelect.waitFor({ timeout: 15_000 });
await orgSelect.selectOption({ label: 'cloudravel-internal' });
await page.goto(`${webUrl}/portal/bots`, { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);
await page.screenshot({ path: path.join(outDir, 'portal-bots.png'), fullPage: true });

fs.writeFileSync(
  path.join(outDir, 'proof.json'),
  JSON.stringify({ feature: 'bot-monitor-pages', webUrl, capturedAt: new Date().toISOString() }, null, 2),
);

await browser.close();
console.log(`Artifacts: ${outDir}`);
