'use client';

import React from 'react';
import type { InventoryResource } from './types';

const PROVIDER_BADGES: Record<string, string> = {
  azure: 'bg-blue-100 text-blue-700',
  aws: 'bg-amber-100 text-amber-800',
  gcp: 'bg-emerald-100 text-emerald-700',
};

export function normalizeCloudProvider(p?: string | null): 'azure' | 'aws' | 'gcp' {
  const v = (p ?? '').trim().toLowerCase();
  if (v === 'aws' || v === 'amazon') return 'aws';
  if (v === 'gcp' || v === 'google' || v === 'googlecloud') return 'gcp';
  return 'azure';
}

/**
 * Infer cloud provider from a resource id (ARN, ARM path, GCP asset name).
 * Prefer an explicit API `provider` hint when present.
 */
export function inferProviderFromResourceId(
  resourceId?: string | null,
  providerHint?: string | null,
): 'azure' | 'aws' | 'gcp' {
  return resolveCloudProvider({ resourceId, provider: providerHint });
}

/**
 * Resolve cloud provider for multi-cloud UI badges (changes, security, governance, ops).
 * Free-text / id signals override a stuck Azure stored label when the payload is clearly AWS/GCP.
 */
export function resolveCloudProvider(opts: {
  provider?: string | null;
  resourceId?: string | null;
  id?: string | null;
  text?: string | null;
}): 'azure' | 'aws' | 'gcp' {
  const blob = [opts.resourceId, opts.id, opts.text, opts.provider].filter(Boolean).join('\n');

  if (
    /arn:aws:|aws-s3-|aws-sh:|aws-sg-|aws-ta:|aws-config:|Security Hub|Trusted Advisor/i.test(blob)
  ) {
    return 'aws';
  }
  if (
    /googleapis\.com|gcp-scc:|gcp-rec:|gcp-storage|Security Command Center|publicAccessPrevention/i.test(
      blob,
    ) ||
    (opts.resourceId?.startsWith('projects/') &&
      /\/(instances|buckets|zones)\//.test(opts.resourceId))
  ) {
    return 'gcp';
  }
  if (
    /\/subscriptions\/|providers\/Microsoft\.|Microsoft Defender|Azure Advisor/i.test(blob)
  ) {
    return 'azure';
  }

  const fromId = inferFromId(opts.resourceId) ?? inferFromId(opts.id);
  if (fromId) return fromId;

  if (opts.provider?.trim()) {
    return normalizeCloudProvider(opts.provider);
  }
  return 'azure';
}

function inferFromId(id?: string | null): 'azure' | 'aws' | 'gcp' | null {
  if (!id?.trim()) return null;
  const v = id.trim();
  if (
    v.startsWith('arn:aws:') ||
    v.startsWith('aws-') ||
    v.startsWith('aws:') ||
    /^aws/i.test(v)
  ) {
    return 'aws';
  }
  if (
    v.includes('googleapis.com') ||
    v.startsWith('gcp-') ||
    v.startsWith('gcp:')
  ) {
    return 'gcp';
  }
  if (
    v.includes('/subscriptions/') ||
    v.startsWith('/subscriptions') ||
    v.includes('providers/Microsoft.')
  ) {
    return 'azure';
  }
  return null;
}

/** Short display name for a change row (leaf of resource id / ARN). */
export function changeResourceLeafName(resourceId?: string | null): string {
  if (!resourceId) return '—';
  return leafName(resourceId) || resourceId;
}

export function cloudLabel(p?: string | null): string {
  const n = normalizeCloudProvider(p);
  if (n === 'aws') return 'AWS';
  if (n === 'gcp') return 'GCP';
  return 'Azure';
}

export function ProviderBadge({ provider, className = '' }: { provider?: string | null; className?: string }) {
  const n = normalizeCloudProvider(provider);
  return (
    <span className={`inline-flex text-xs px-2 py-0.5 rounded-full font-medium ${PROVIDER_BADGES[n]} ${className}`}>
      {cloudLabel(n)}
    </span>
  );
}

/**
 * Compact multi-line cloud hierarchy for inventory tables:
 * Cloud · Org · Account/Project/Subscription
 */
export function CloudScopeCell({ resource }: { resource: InventoryResource }) {
  const provider = resource.provider ?? resource.cloud;
  const scopeKind = resource.scopeKind
    ?? (normalizeCloudProvider(provider) === 'aws'
      ? 'account'
      : normalizeCloudProvider(provider) === 'gcp'
        ? 'project'
        : 'subscription');
  const scopeId = resource.scopeId || resource.subscriptionId || '';
  const scopeName = resource.scopeName;
  const org = resource.cloudOrgName;
  const kindLabel =
    scopeKind === 'account' ? 'Account' : scopeKind === 'project' ? 'Project' : 'Subscription';

  return (
    <div className="min-w-0 space-y-0.5">
      <div className="flex items-center gap-1.5">
        <ProviderBadge provider={provider} />
        {org && (
          <span className="text-xs text-gray-600 truncate" title={org}>
            {org}
          </span>
        )}
      </div>
      <p className="text-xs text-gray-500 truncate" title={[kindLabel, scopeName, scopeId].filter(Boolean).join(' · ')}>
        <span className="text-gray-400">{kindLabel}:</span>{' '}
        {scopeName ? (
          <>
            <span className="text-gray-700 font-medium">{scopeName}</span>
            {scopeId && scopeId !== scopeName && (
              <span className="text-gray-400 font-mono"> · {truncateId(scopeId)}</span>
            )}
          </>
        ) : (
          <span className="font-mono text-gray-600">{truncateId(scopeId) || '—'}</span>
        )}
      </p>
      {resource.azureTenantId && normalizeCloudProvider(provider) === 'azure' && (
        <p className="text-[11px] text-gray-400 font-mono truncate" title={resource.azureTenantId}>
          Tenant: {truncateId(resource.azureTenantId)}
        </p>
      )}
    </div>
  );
}

export function scopeKindLabel(resource: InventoryResource): string {
  if (resource.resourceGroupKind) return resource.resourceGroupKind;
  const p = normalizeCloudProvider(resource.provider ?? resource.cloud);
  if (p === 'aws') return 'Service';
  if (p === 'gcp') return 'Namespace';
  return 'Resource group';
}

/**
 * Human-facing name for list/detail. Prefer resourceName / Name tag; never lead with full ARN or asset path.
 */
export function displayResourceName(resource: InventoryResource): string {
  const tags = resource.tags;
  if (tags) {
    for (const key of ['Name', 'name', 'displayName']) {
      const v = tags[key];
      if (v && !looksLikeFullId(v)) return v;
    }
  }
  const name = (resource.resourceName || '').trim();
  if (name && !looksLikeFullId(name) && name !== resource.resourceId) return name;
  const leaf = leafName(resource.resourceId);
  if (leaf && !looksLikeFullId(leaf)) return leaf;
  return name || leaf || resource.resourceId || '—';
}

function looksLikeFullId(value: string): boolean {
  return (
    value.startsWith('arn:') ||
    value.startsWith('//') ||
    value.startsWith('https://') ||
    (value.includes('/projects/') && (value.match(/\//g) || []).length >= 4) ||
    (value.startsWith('/subscriptions/') && (value.match(/\//g) || []).length >= 6)
  );
}

function leafName(id?: string): string {
  if (!id) return '';
  if (id.startsWith('arn:')) {
    const parts = id.split(':');
    const resourcePart = parts.length > 5 ? parts.slice(5).join(':') : parts[parts.length - 1] || '';
    if (resourcePart.includes('/')) return resourcePart.slice(resourcePart.lastIndexOf('/') + 1);
    return resourcePart;
  }
  if (id.includes('/')) return id.slice(id.lastIndexOf('/') + 1);
  return id;
}

function truncateId(id: string, max = 18): string {
  if (!id) return '';
  if (id.length <= max) return id;
  return `${id.slice(0, 8)}…${id.slice(-6)}`;
}
