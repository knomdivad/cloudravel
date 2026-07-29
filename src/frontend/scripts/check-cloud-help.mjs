/**
 * Structural check: shipped cloud-help content and form wiring.
 * Run: node scripts/check-cloud-help.mjs (cwd = src/frontend)
 */
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, '..');

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}

const helpTs = read('src/lib/cloud-help.ts');
const helpPage = read('src/app/help/clouds/page.tsx');
const tenantsPage = read('src/app/tenants/page.tsx');
const fieldHelp = read('src/components/FieldHelp.tsx');
const layout = read('src/app/layout.tsx');

const failures = [];

function assert(cond, msg) {
  if (!cond) failures.push(msg);
}

// Content module must export essentials used by UI and tests
const markers = [
  'GCP Organization ID',
  'numeric org ID',
  'Project ID',
  'service account',
  'roles/cloudasset.viewer',
  'cloudasset.googleapis.com',
  'Azure Tenant ID',
  'Lighthouse',
  'App Registration',
  'Organization ID (starts with o-)',
  '12-digit',
  'Access Key',
  'Resource Groups Tagging',
];
for (const m of markers) {
  assert(helpTs.includes(m), `cloud-help.ts missing essential marker: ${m}`);
}

// Field keys that forms must wire
const keys = [
  'gcp.orgId',
  'gcp.projectId',
  'gcp.serviceAccountJson',
  'azure.tenantId',
  'azure.onboardingMethod',
  'azure.clientId',
  'aws.orgId',
  'aws.accountId',
  'aws.accessKeyId',
];
for (const k of keys) {
  assert(helpTs.includes(`'${k}'`), `cloud-help.ts missing field key ${k}`);
  assert(tenantsPage.includes(k) || tenantsPage.includes(`"${k}"`) || tenantsPage.includes(`'${k}'`),
    `tenants/page.tsx must reference helpKey ${k}`);
}

assert(tenantsPage.includes("from '@/components/FieldHelp'") || tenantsPage.includes('from "@/components/FieldHelp"'),
  'tenants page must import FieldHelp');
assert(tenantsPage.includes('FieldHelp') && tenantsPage.includes('CloudHelpLink'),
  'tenants page must use FieldHelp and CloudHelpLink');
assert(helpPage.includes('HELP_SECTIONS') || helpPage.includes("from '@/lib/cloud-help'"),
  'help page must import HELP_SECTIONS from cloud-help');
assert(helpPage.includes('gcp-sa-key') || helpPage.includes('id={section.id}'),
  'help page must render section anchors');
assert(fieldHelp.includes('helpCloudsHref') && fieldHelp.includes('CLOUD_FIELD_HELP'),
  'FieldHelp must use shipped cloud-help module');
assert(layout.includes('/help/clouds'), 'layout nav should link to /help/clouds');

// Deep-link pattern
assert(helpTs.includes("'/help/clouds'") || helpTs.includes('"/help/clouds"') || helpTs.includes('HELP_CLOUDS_PATH'),
  'HELP_CLOUDS_PATH must point at /help/clouds');

if (failures.length) {
  console.error('check-cloud-help FAILED:');
  for (const f of failures) console.error(' -', f);
  process.exit(1);
}
console.log('check-cloud-help OK:', markers.length, 'markers,', keys.length, 'field keys wired');
