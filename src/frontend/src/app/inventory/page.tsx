'use client';

import React, { useState, useMemo } from 'react';
import { useTenantContext } from '../../contexts/TenantContext';
import { useInventoryResources, useInventorySummary } from '../../lib/hooks';

const PAGE_SIZE = 50;

export default function InventoryPage() {
  const { tenantId } = useTenantContext();

  const [filters, setFilters] = useState({
    resourceType: '',
    subscriptionId: '',
    resourceGroup: '',
    search: '',
  });
  const [page, setPage] = useState(0);

  const { data: summary } = useInventorySummary(tenantId);
  const { data, isLoading, error } = useInventoryResources(tenantId, {
    resourceType: filters.resourceType || undefined,
    subscriptionId: filters.subscriptionId || undefined,
    resourceGroup: filters.resourceGroup || undefined,
    offset: page * PAGE_SIZE,
    limit: PAGE_SIZE,
  });

  // Extract unique resource types for filter dropdown
  const resourceTypes = summary?.resourceTypes ?? [];

  // Client-side name search (supplement to server filter)
  const resources = useMemo(() => {
    if (!data?.resources) return [];
    if (!filters.search) return data.resources;
    const q = filters.search.toLowerCase();
    return data.resources.filter(
      (r) =>
        r.resourceName.toLowerCase().includes(q) ||
        r.resourceId.toLowerCase().includes(q) ||
        r.resourceGroup.toLowerCase().includes(q)
    );
  }, [data?.resources, filters.search]);

  if (!tenantId) {
    return <EmptyState message="Select a tenant to view inventory." />;
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-bold">Inventory Explorer</h1>
          <p className="text-sm text-gray-500 mt-1">
            {summary?.totalResources?.toLocaleString() ?? '...'} resources across{' '}
            {resourceTypes.length} types
          </p>
        </div>
      </div>

      {/* Filters */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-4">
        <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
          <div>
            <label className="text-xs text-gray-500 block mb-1">Resource Type</label>
            <select
              value={filters.resourceType}
              onChange={(e) => {
                setFilters({ ...filters, resourceType: e.target.value });
                setPage(0);
              }}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            >
              <option value="">All Types</option>
              {resourceTypes.map((rt) => (
                <option key={rt.resourceType} value={rt.resourceType}>
                  {rt.resourceType.split('/').pop()} ({rt.count})
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className="text-xs text-gray-500 block mb-1">Subscription</label>
            <input
              type="text"
              placeholder="Filter by subscription ID..."
              value={filters.subscriptionId}
              onChange={(e) => {
                setFilters({ ...filters, subscriptionId: e.target.value });
                setPage(0);
              }}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            />
          </div>
          <div>
            <label className="text-xs text-gray-500 block mb-1">Resource Group</label>
            <input
              type="text"
              placeholder="Filter by resource group..."
              value={filters.resourceGroup}
              onChange={(e) => {
                setFilters({ ...filters, resourceGroup: e.target.value });
                setPage(0);
              }}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            />
          </div>
          <div>
            <label className="text-xs text-gray-500 block mb-1">Search</label>
            <input
              type="text"
              placeholder="Search by name..."
              value={filters.search}
              onChange={(e) => setFilters({ ...filters, search: e.target.value })}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            />
          </div>
        </div>
      </div>

      {/* Resource Table */}
      <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
        {isLoading ? (
          <div className="p-12 text-center text-gray-400">Loading resources...</div>
        ) : error ? (
          <div className="p-12 text-center text-red-500">Error loading resources: {error.message}</div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-gray-50">
                  <tr className="text-left text-gray-500">
                    <th className="px-4 py-3 font-medium">Name</th>
                    <th className="px-4 py-3 font-medium">Type</th>
                    <th className="px-4 py-3 font-medium">Resource Group</th>
                    <th className="px-4 py-3 font-medium">Location</th>
                    <th className="px-4 py-3 font-medium">SKU</th>
                    <th className="px-4 py-3 font-medium">Tags</th>
                  </tr>
                </thead>
                <tbody>
                  {resources.map((r) => (
                    <tr
                      key={r.resourceId}
                      className="border-t border-gray-100 hover:bg-blue-50 cursor-pointer transition-colors"
                    >
                      <td className="px-4 py-3">
                        <a href={`/inventory/detail?id=${encodeURIComponent(r.resourceId)}`} className="text-azure-600 hover:underline font-medium">
                          {r.resourceName}
                        </a>
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {r.resourceType.split('/').slice(-1)[0]}
                      </td>
                      <td className="px-4 py-3 text-gray-600">{r.resourceGroup}</td>
                      <td className="px-4 py-3 text-gray-600">{r.location}</td>
                      <td className="px-4 py-3 text-gray-600">
                        {r.skuName ? `${r.skuName}${r.skuTier ? ` / ${r.skuTier}` : ''}` : '—'}
                      </td>
                      <td className="px-4 py-3">
                        {r.tags && Object.keys(r.tags).length > 0 ? (
                          <div className="flex flex-wrap gap-1">
                            {Object.entries(r.tags)
                              .slice(0, 3)
                              .map(([k, v]) => (
                                <span key={k} className="bg-gray-100 text-gray-600 text-xs px-1.5 py-0.5 rounded">
                                  {k}: {v}
                                </span>
                              ))}
                            {Object.keys(r.tags).length > 3 && (
                              <span className="text-xs text-gray-400">
                                +{Object.keys(r.tags).length - 3} more
                              </span>
                            )}
                          </div>
                        ) : (
                          <span className="text-gray-300">—</span>
                        )}
                      </td>
                    </tr>
                  ))}
                  {resources.length === 0 && (
                    <tr>
                      <td colSpan={6} className="px-4 py-12 text-center text-gray-400">
                        No resources found matching filters.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            {data && (data.pagination?.total ?? 0) > PAGE_SIZE && (
              <div className="flex items-center justify-between px-4 py-3 border-t border-gray-100">
                <p className="text-sm text-gray-500">
                  Showing {page * PAGE_SIZE + 1}–
                  {Math.min((page + 1) * PAGE_SIZE, data.pagination.total)} of{' '}
                  {data.pagination.total.toLocaleString()}
                </p>
                <div className="flex gap-2">
                  <button
                    disabled={page === 0}
                    onClick={() => setPage(page - 1)}
                    className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
                  >
                    Previous
                  </button>
                  <button
                    disabled={!data.pagination.hasMore}
                    onClick={() => setPage(page + 1)}
                    className="px-3 py-1.5 text-sm border border-gray-300 rounded-lg disabled:opacity-40 hover:bg-gray-50"
                  >
                    Next
                  </button>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function EmptyState({ message }: { message: string }) {
  return (
    <div className="text-center py-20 text-gray-500">
      <p>{message}</p>
    </div>
  );
}
