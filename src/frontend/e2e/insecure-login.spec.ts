import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true });
    Object.defineProperty(window.crypto, 'subtle', { value: undefined, configurable: true });
    Object.defineProperty(window.crypto, 'randomUUID', { value: undefined, configurable: true });
  });
});

test('login page renders with no page errors on non-localhost HTTP', async ({ page, baseURL }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));

  await page.goto(baseURL ?? '/');

  await expect(page.getByText('Email', { exact: true })).toBeVisible();
  await expect(page.getByText('Password', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

  expect(pageErrors).toEqual([]);
});
