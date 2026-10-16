#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const api = readFileSync(join(root, 'src/lib/api.ts'), 'utf8');

const emptyGuid = '00000000-0000-0000-0000-000000000000';
let failed = false;

if (!api.includes('function assertWorkspaceId')) {
  console.error('api.ts: missing assertWorkspaceId helper');
  failed = true;
}

if (api.match(/getTenants\s*\(\s*\)/)) {
  console.error('getTenants must require workspaceId parameter');
  failed = true;
}

if (api.includes(`'X-Tenant-Id': '${emptyGuid}'`)) {
  console.error('api.ts: tenant endpoints must not send empty workspace guid');
  failed = true;
}

if (!api.includes('getTenants(workspaceId: string)')) {
  console.error('getTenants(workspaceId: string) signature required');
  failed = true;
}

if (!api.includes('onboardTenant(\n  workspaceId: string')) {
  console.error('onboardTenant must require workspaceId as first argument');
  failed = true;
}

if (failed) process.exit(1);
console.log('check-tenant-workspace-header: ok');
