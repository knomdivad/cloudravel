import { defineConfig } from '@playwright/test';

const port = 4173;
const insecureHost = 'cloudravel-insecure.test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? `http://${insecureHost}:${port}`,
  },
  webServer: {
    command: `npx serve out -l ${port}`,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: !process.env.CI,
    cwd: __dirname,
  },
});
