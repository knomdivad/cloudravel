/**
 * Essential cloud-connect help. Keys are used by Clouds forms and /help/clouds.
 * Keep copy short — field-level tips and help-page bullets only.
 */

export type CloudHelpKey =
  | 'azure.displayName'
  | 'azure.tenantId'
  | 'azure.onboardingMethod'
  | 'azure.clientId'
  | 'azure.clientSecret'
  | 'azure.subscriptions'
  | 'aws.orgName'
  | 'aws.orgId'
  | 'aws.accountId'
  | 'aws.displayName'
  | 'aws.accessKeyId'
  | 'aws.secretAccessKey'
  | 'aws.sessionToken'
  | 'aws.defaultRegion'
  | 'aws.regions'
  | 'gcp.orgName'
  | 'gcp.orgId'
  | 'gcp.projectId'
  | 'gcp.displayName'
  | 'gcp.serviceAccountJson'
  | 'gcp.regions';

export interface CloudFieldHelp {
  /** One-line tip under the field */
  tip: string;
  /** Anchor on /help/clouds (without #) */
  helpId: string;
}

/** Field tips + deep-link anchors (helpId → section on /help/clouds). */
export const CLOUD_FIELD_HELP: Record<CloudHelpKey, CloudFieldHelp> = {
  'azure.displayName': {
    tip: 'Label only — any name you will recognize in CloudRavel.',
    helpId: 'azure',
  },
  'azure.tenantId': {
    tip: 'Entra ID → Overview → Tenant ID (GUID).',
    helpId: 'azure-tenant-id',
  },
  'azure.onboardingMethod': {
    tip: 'Lighthouse: delegated access, no secret. App Registration: app + client secret in customer tenant.',
    helpId: 'azure-onboarding',
  },
  'azure.clientId': {
    tip: 'App registration → Application (client) ID.',
    helpId: 'azure-app-reg',
  },
  'azure.clientSecret': {
    tip: 'App registration → Certificates & secrets → New client secret (value, not Secret ID).',
    helpId: 'azure-app-reg',
  },
  'azure.subscriptions': {
    tip: 'Subscription ID GUIDs. Leave “All” if every sub in the tenant should be in scope.',
    helpId: 'azure-subscriptions',
  },
  'aws.orgName': {
    tip: 'Friendly label in CloudRavel (not the AWS org ID).',
    helpId: 'aws-org',
  },
  'aws.orgId': {
    tip: 'AWS Organizations → Settings → Organization ID (starts with o-). Optional grouping only.',
    helpId: 'aws-org-id',
  },
  'aws.accountId': {
    tip: '12-digit account number (top-right of the AWS console, or IAM → Account).',
    helpId: 'aws-account-id',
  },
  'aws.displayName': {
    tip: 'Label only in CloudRavel.',
    helpId: 'aws-account',
  },
  'aws.accessKeyId': {
    tip: 'IAM user/role access key with resource-groups tagging read (and write if remediations are used).',
    helpId: 'aws-keys',
  },
  'aws.secretAccessKey': {
    tip: 'Shown only once when the key is created. Stored in OpenBao, not SQL.',
    helpId: 'aws-keys',
  },
  'aws.sessionToken': {
    tip: 'Only for temporary STS credentials. Leave blank for long-lived IAM user keys.',
    helpId: 'aws-keys',
  },
  'aws.defaultRegion': {
    tip: 'Region used for API signing (e.g. us-east-1).',
    helpId: 'aws-regions',
  },
  'aws.regions': {
    tip: 'Comma-separated regions to inventory. Blank = default region only.',
    helpId: 'aws-regions',
  },
  'gcp.orgName': {
    tip: 'Friendly label in CloudRavel — not the numeric Org ID.',
    helpId: 'gcp-org',
  },
  'gcp.orgId': {
    tip: 'Numeric org ID (IAM & Admin → Manage Resources). Optional grouping only.',
    helpId: 'gcp-org-id',
  },
  'gcp.projectId': {
    tip: 'Project ID (not display name) — Home or project picker, e.g. my-prod-123.',
    helpId: 'gcp-project-id',
  },
  'gcp.displayName': {
    tip: 'Label only in CloudRavel.',
    helpId: 'gcp-project',
  },
  'gcp.serviceAccountJson': {
    tip: 'Full JSON key file. Needs Cloud Asset Viewer on the project; enable cloudasset.googleapis.com.',
    helpId: 'gcp-sa-key',
  },
  'gcp.regions': {
    tip: 'Usually leave blank (project-wide asset list).',
    helpId: 'gcp-project',
  },
};

export const HELP_CLOUDS_PATH = '/help/clouds';

export function helpCloudsHref(helpId: string): string {
  return `${HELP_CLOUDS_PATH}#${helpId}`;
}

/** Section bodies for /help/clouds — essential bullets + official links only. */
export const HELP_SECTIONS = [
  {
    id: 'azure',
    title: 'Azure',
    bullets: [
      'Connect an Entra (Azure AD) tenant to this organization, then optionally limit subscriptions.',
      'Display Name is CloudRavel-only.',
    ],
  },
  {
    id: 'azure-tenant-id',
    title: 'Azure Tenant ID',
    bullets: [
      'Azure Portal → Microsoft Entra ID → Overview → Tenant ID.',
      'Must be the GUID of the customer directory you want to inventory.',
    ],
    links: [{ label: 'Find tenant ID', href: 'https://learn.microsoft.com/en-us/azure/azure-portal/get-subscription-tenant-id' }],
  },
  {
    id: 'azure-onboarding',
    title: 'Lighthouse vs App Registration',
    bullets: [
      'Lighthouse: customer deploys a Lighthouse offer; CloudRavel uses delegated Reader (no client secret stored).',
      'App Registration: create an app in the customer tenant, grant Reader (and remediation roles if used), paste Client ID + secret here.',
    ],
    links: [
      { label: 'Azure Lighthouse', href: 'https://learn.microsoft.com/en-us/azure/lighthouse/overview' },
      { label: 'App registration', href: 'https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app' },
    ],
  },
  {
    id: 'azure-app-reg',
    title: 'Client ID & secret',
    bullets: [
      'Entra ID → App registrations → your app → Application (client) ID.',
      'Certificates & secrets → New client secret → copy the Value immediately.',
    ],
  },
  {
    id: 'azure-subscriptions',
    title: 'Subscriptions',
    bullets: [
      'All: every subscription the principal can see.',
      'Specific: paste subscription GUIDs (Subscriptions blade → Subscription ID).',
    ],
  },
  {
    id: 'aws-org',
    title: 'AWS organization (grouping)',
    bullets: [
      'An AWS “organization” in CloudRavel is a folder for accounts under this workspace.',
      'Name is a label; accounts and keys are added next.',
    ],
  },
  {
    id: 'aws-org-id',
    title: 'AWS Organization ID',
    bullets: [
      'Console (management account) → AWS Organizations → Settings → Organization ID (o-…).',
      'Optional metadata only; inventory runs per account credentials you attach.',
    ],
    links: [{ label: 'AWS Organizations', href: 'https://docs.aws.amazon.com/organizations/latest/userguide/orgs_manage_org_details.html' }],
  },
  {
    id: 'aws-account',
    title: 'AWS account',
    bullets: ['Add each 12-digit account you want inventoried under the AWS org grouping.'],
  },
  {
    id: 'aws-account-id',
    title: 'AWS Account ID',
    bullets: [
      '12 digits. Console top-right account menu, or IAM → Dashboard → Account ID.',
    ],
  },
  {
    id: 'aws-keys',
    title: 'IAM access keys',
    bullets: [
      'IAM → Users → Security credentials → Create access key (or use STS temporary keys + session token).',
      'Inventory: tag:GetResources (Resource Groups Tagging API) in each scanned region.',
      'Security / Governance parity (read): securityhub:GetFindings, config:Describe*, support:DescribeTrustedAdvisor* (Business/Enterprise support), s3:ListAllMyBuckets, s3:GetBucketPublicAccessBlock, ec2:DescribeSecurityGroups.',
      'Remediation playbooks need matching write actions (e.g. s3:PutBucketPublicAccessBlock, ec2:StopInstances).',
      'Missing APIs or IAM are skipped with a note — other sources still sync.',
    ],
    links: [
      { label: 'Manage access keys', href: 'https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html' },
      { label: 'Security Hub', href: 'https://docs.aws.amazon.com/securityhub/latest/userguide/what-is-securityhub.html' },
      { label: 'AWS Config', href: 'https://docs.aws.amazon.com/config/latest/developerguide/WhatIsConfig.html' },
    ],
  },
  {
    id: 'aws-regions',
    title: 'Regions',
    bullets: [
      'Default region: used for signing.',
      'Regions to scan: comma-separated list to inventory; blank uses the default only.',
    ],
  },
  {
    id: 'gcp-org',
    title: 'GCP organization (grouping)',
    bullets: [
      'CloudRavel “GCP organization” groups projects under this workspace.',
      'Name is a label; Project ID + service account key are what collection uses.',
    ],
  },
  {
    id: 'gcp-org-id',
    title: 'GCP Organization ID',
    bullets: [
      'Numeric ID (not the display name).',
      'Console → IAM & Admin → Manage Resources → select the organization → ID in details.',
      'Optional here — projects are collected with project-level credentials.',
    ],
    links: [{ label: 'Getting organization info', href: 'https://cloud.google.com/resource-manager/docs/creating-managing-organization#retrieving_your_organization_id' }],
  },
  {
    id: 'gcp-project',
    title: 'GCP project',
    bullets: ['Each project needs its Project ID and a service account key JSON.'],
  },
  {
    id: 'gcp-project-id',
    title: 'GCP Project ID',
    bullets: [
      'Not the display name. Project picker or Home → Project info → Project ID (e.g. my-app-prod).',
    ],
    links: [{ label: 'Creating and managing projects', href: 'https://cloud.google.com/resource-manager/docs/creating-managing-projects' }],
  },
  {
    id: 'gcp-sa-key',
    title: 'Service account JSON key',
    bullets: [
      'IAM & Admin → Service Accounts → create or pick an SA → Keys → Add key → JSON → download.',
      'Paste the entire JSON file contents into the form.',
      'Enable APIs: Cloud Asset, Cloud Resource Manager, Security Command Center, Recommender, Organization Policy.',
      'Inventory: roles/browser + roles/cloudasset.viewer on the project.',
      'Security / Governance parity: roles/securitycenter.findingsViewer, roles/recommender.viewer, roles/orgpolicy.policyViewer (as available).',
      'Remediation playbooks need write roles (e.g. storage.admin for public access prevention).',
      'Key is stored in the secret store (OpenBao), not in SQL.',
    ],
    links: [
      { label: 'Create service account keys', href: 'https://cloud.google.com/iam/docs/keys-create-delete' },
      { label: 'Cloud Asset API', href: 'https://cloud.google.com/asset-inventory/docs/overview' },
      { label: 'Security Command Center', href: 'https://cloud.google.com/security-command-center/docs/overview' },
      { label: 'Recommender', href: 'https://cloud.google.com/recommender/docs/overview' },
    ],
  },
] as const;

/** Essential substrings tests and UI must keep in sync with shipped help. */
export const ESSENTIAL_HELP_MARKERS = [
  'GCP Organization ID',
  'numeric org ID',
  'Project ID',
  'service account',
  'roles/browser',
  'roles/cloudasset.viewer',
  'Security Command Center',
  'securityhub:GetFindings',
  'cloudasset.googleapis.com',
  'Azure Tenant ID',
  'Lighthouse',
  'App Registration',
  'Organization ID (starts with o-)',
  '12-digit',
  'Access Key',
  'Resource Groups Tagging',
] as const;
