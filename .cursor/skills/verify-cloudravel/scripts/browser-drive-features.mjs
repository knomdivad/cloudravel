#!/usr/bin/env node
/**
 * Playwright drive for dashboard-home, inventory-browse, organization-create.
 * Run from skill root after browser-login or standalone (logs in first).
 */
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const runId = process.env.VERIFY_RUN_ID || 'verify';
const root =
  process.env.VERIFY_ARTIFACTS_ROOT ||
  `/opt/cursor/artifacts/verify-cloudravel`;
const artifactRoot = join(root, runId);
const webUrl = process.env.WEB_URL || 'http://127.0.0.1:3000';
const apiUrl = process.env.API_URL || 'http://127.0.0.1:7071/api';
const email = process.env.CLOUDRAVEL_LOCAL_EMAIL || 'admin@local';
const password = process.env.CLOUDRAVEL_LOCAL_PASSWORD || 'ChangeMe123!';

function proof(feature, data) {
  const dir = join(artifactRoot, feature);
  mkdirSync(dir, { recursive: true });
  writeFileSync(
    join(dir, 'proof.json'),
    JSON.stringify({ feature, webUrl, apiUrl, ...data }, null, 2),
  );
  return dir;
}

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });

try {
  await page.goto(webUrl, { waitUntil: 'networkidle', timeout: 120_000 });
  await page.getByText('Email', { exact: true }).locator('..').getByRole('textbox').fill(email);
  await page.locator('input[type="password"]').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.getByRole('link', { name: 'Dashboard' }).waitFor({ timeout: 60_000 });

  await page.getByRole('link', { name: 'Dashboard' }).click();
  await page.getByText('No tenant selected').waitFor({ timeout: 30_000 });
  const dashDir = proof('dashboard-home', { subFeature: 'dashboard-empty-org' });
  await page.screenshot({ path: join(dashDir, 'dashboard-empty.png') });
  writeFileSync(join(dashDir, 'dashboard-empty.aria.txt'), await page.locator('body').ariaSnapshot());

  await page.getByRole('link', { name: 'Inventory' }).click();
  await page.getByText('Select a tenant to view inventory.').waitFor({ timeout: 30_000 });
  const invDir = proof('inventory-browse', { subFeature: 'inventory-empty-tenant' });
  await page.screenshot({ path: join(invDir, 'inventory-no-tenant.png') });

  const orgName = `Verify Org ${runId}`;
  await page.getByRole('button', { name: '+ New' }).click();
  await page.getByRole('heading', { name: 'New Organization' }).waitFor();
  await page.getByPlaceholder('Acme Corp').fill(orgName);
  await page.getByRole('button', { name: 'Create Organization' }).click();
  await page.getByRole('heading', { name: orgName }).waitFor({ timeout: 30_000 });
  const orgDir = proof('organization-create', { orgName, subFeature: 'org-create' });
  await page.screenshot({ path: join(orgDir, 'org-created.png') });

  await page.getByRole('link', { name: 'Inventory' }).click();
  await page.getByRole('heading', { name: 'Inventory Explorer' }).waitFor({ timeout: 30_000 });
  writeFileSync(
    join(invDir, 'proof-with-org.json'),
    JSON.stringify({ feature: 'inventory-browse', subFeature: 'inventory-nav', orgName }, null, 2),
  );
  await page.screenshot({ path: join(invDir, 'inventory-with-org.png') });

  console.log('OK: dashboard-home, inventory-browse, organization-create');
} finally {
  await browser.close();
}
