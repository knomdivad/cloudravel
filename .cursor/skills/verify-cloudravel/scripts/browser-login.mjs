#!/usr/bin/env node
/**
 * Drive local login through the real Next.js UI (nginx on WEB_URL).
 * Requires: npx playwright (installed on first run via package in skill root).
 */
import { chromium } from 'playwright';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: browser-login.mjs <artifact-dir>');
  process.exit(1);
}

const webUrl = process.env.WEB_URL || 'http://127.0.0.1:3000';
const email = process.env.CLOUDRAVEL_LOCAL_EMAIL || 'admin@local';
const password = process.env.CLOUDRAVEL_LOCAL_PASSWORD || 'ChangeMe123!';

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });

try {
  await page.goto(webUrl, { waitUntil: 'networkidle', timeout: 120_000 });
  await page.getByRole('heading', { name: 'CloudRavel' }).waitFor({ timeout: 30_000 });

  await page.getByText('Email', { exact: true }).locator('..').getByRole('textbox').fill(email);
  await page.locator('input[type="password"]').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();

  await page.getByRole('link', { name: 'Dashboard' }).waitFor({ timeout: 60_000 });
  await page.getByText('CloudRavel', { exact: true }).first().waitFor();

  const aria = await page.locator('body').ariaSnapshot();
  writeFileSync(join(outDir, 'post-login.aria.txt'), aria, 'utf8');
  await page.screenshot({ path: join(outDir, 'post-login.png'), fullPage: false });

  const sidebarOrg = await page.getByText('No organizations yet').count();
  writeFileSync(
    join(outDir, 'proof.json'),
    JSON.stringify(
      {
        feature: 'local-login',
        webUrl,
        email,
        dashboardNavVisible: true,
        freshEstateNoOrgs: sidebarOrg > 0,
      },
      null,
      2,
    ),
    'utf8',
  );
  console.log('OK: logged in; Dashboard nav visible');
} finally {
  await browser.close();
}
