import { defineConfig } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

function readEnvFile(path) {
  if (!existsSync(path)) {
    return {};
  }

  return readFileSync(path, 'utf8')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('#') && line.includes('='))
    .reduce((acc, line) => {
      const [key, ...rest] = line.split('=');
      acc[key] = rest.join('=');
      return acc;
    }, {});
}

const configDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = dirname(configDir);
const env = readEnvFile(join(repoRoot, '.env'));

process.env.CRM_BASE_URL ||= 'http://127.0.0.1:5050';
process.env.CRM_ADMIN_LOGIN ||= env.CRM_ADMIN_LOGIN || 'admin';
process.env.CRM_AUTH_BOOTSTRAP_PASSWORD ||= env.CRM_AUTH_BOOTSTRAP_PASSWORD || '';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  timeout: 60_000,
  expect: {
    timeout: 15_000,
  },
  reporter: 'list',
  use: {
    baseURL: process.env.CRM_BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        browserName: 'chromium',
        viewport: { width: 1440, height: 960 },
      },
    },
  ],
});
