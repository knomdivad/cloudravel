'use client';

import { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import { useTenantContext } from '@/contexts/TenantContext';
import { useAuth } from '@/contexts/AuthContext';
import type { AdminUser, OrgSso } from '@/lib/types';
import { Field, ModalShell, ModalActions, Spinner, ErrorBox, Toast, AccessDenied, type ToastType } from '@/components/admin-ui';
const ORG_ROLES = ['org_admin', 'cloud_admin', 'read_only'];

export default function OrganizationPage() {
  const { currentOrg, tenantId, isOrgAdmin } = useTenantContext();
  const { isSystemAdmin, systemRole } = useAuth();
  const [toast, setToast] = useState<{ message: string; type: ToastType } | null>(null);
  const showToast = useCallback((message: string, type: ToastType) => {
    setToast({ message, type }); setTimeout(() => setToast(null), 5000);
  }, []);

  if (!tenantId) {
    return <div className="text-center py-16 bg-white rounded-lg border border-gray-200 max-w-lg mx-auto">
      <p className="text-gray-500">Select an organization from the sidebar to manage it.</p></div>;
  }
  // Wait for role resolution before denying (currentOrg carries callerRole; systemRole from /me).
  const canManage = isOrgAdmin || isSystemAdmin;
  if (currentOrg && systemRole !== null && !canManage) return <AccessDenied />;

  return (
    <div className="space-y-8 max-w-5xl">
      {toast && <Toast toast={toast} onClose={() => setToast(null)} />}
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Organization</h1>
        <p className="text-sm text-gray-500 mt-1">Manage <strong>{currentOrg?.name}</strong> — members and single sign-on.</p>
      </div>
      <MembersCard orgId={tenantId} showToast={showToast} />
      <SsoCard orgId={tenantId} showToast={showToast} />
    </div>
  );
}

function MembersCard({ orgId, showToast }: { orgId: string; showToast: (m: string, t: ToastType) => void }) {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [showAdd, setShowAdd] = useState(false);

  const load = useCallback(async () => {
    try { setUsers(await api.getOrgUsers(orgId)); } catch { /* non-fatal */ }
  }, [orgId]);
  useEffect(() => { load(); }, [load]);

  const setRole = async (u: AdminUser, role: string) => {
    try { await api.updateOrgUserRole(orgId, u.userId, role); showToast(`${u.displayName} → ${role}.`, 'success'); load(); }
    catch (err: any) { showToast(err?.message ?? 'Failed.', 'error'); }
  };
  const remove = async (u: AdminUser) => {
    try { await api.removeOrgUser(orgId, u.userId); showToast(`Removed ${u.displayName}.`, 'success'); load(); }
    catch (err: any) { showToast(err?.message ?? 'Failed.', 'error'); }
  };

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6">
      {showAdd && <AddMemberModal orgId={orgId} onClose={() => setShowAdd(false)} onAdded={() => { setShowAdd(false); load(); showToast('Member added.', 'success'); }} />}
      <div className="flex items-center justify-between mb-4">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Members</h2>
          <p className="text-sm text-gray-500">org_admin manages everything; cloud_admin manages clouds; read_only can only view.</p>
        </div>
        <button onClick={() => setShowAdd(true)} className="px-4 py-2 bg-azure-600 text-white rounded-lg text-sm hover:bg-azure-700">+ Add Member</button>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-gray-500 border-b border-gray-100">
              <th className="py-2 pr-4 font-medium">Name</th>
              <th className="py-2 pr-4 font-medium">Username / Email</th>
              <th className="py-2 pr-4 font-medium">Org role</th>
              <th className="py-2 pr-4 font-medium"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {users.length === 0 && <tr><td colSpan={4} className="py-4 text-gray-400">No members yet.</td></tr>}
            {users.map((u) => (
              <tr key={u.userId}>
                <td className="py-2.5 pr-4 font-medium text-gray-900">{u.displayName}</td>
                <td className="py-2.5 pr-4 text-gray-500"><span className="font-mono text-xs">{u.username ?? u.email}</span></td>
                <td className="py-2.5 pr-4">
                  <select value={u.orgRole ?? 'read_only'} onChange={(e) => setRole(u, e.target.value)}
                    className="border border-gray-300 rounded px-2 py-1 text-xs">
                    {ORG_ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
                  </select>
                </td>
                <td className="py-2.5 pr-4 text-right">
                  <button onClick={() => remove(u)} className="text-xs text-red-600 hover:text-red-700">Remove</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function AddMemberModal({ orgId, onClose, onAdded }: { orgId: string; onClose: () => void; onAdded: () => void }) {
  const [mode, setMode] = useState<'existing' | 'new'>('existing');
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState('read_only');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const eaddr = email.trim().toLowerCase();
      await api.addOrgUser(orgId, mode === 'new'
        ? { email: eaddr, username: eaddr, displayName: displayName.trim(), password, role }
        : { email: eaddr, role });
      onAdded();
    } catch (err: any) { setError(err?.message ?? 'Failed to add member.'); }
    finally { setBusy(false); }
  };

  return (
    <ModalShell title="Add Member" onClose={onClose}>
      {error && <ErrorBox>{error}</ErrorBox>}
      <div className="grid grid-cols-2 gap-2 mb-4">
        {(['existing', 'new'] as const).map((m) => (
          <button key={m} type="button" onClick={() => setMode(m)}
            className={`px-3 py-2 rounded-lg border text-sm font-medium ${mode === m ? 'border-azure-500 bg-azure-50 text-azure-700' : 'border-gray-300 text-gray-600 hover:bg-gray-50'}`}>
            {m === 'existing' ? 'Existing user' : 'New local user'}
          </button>
        ))}
      </div>
      <form onSubmit={submit} className="space-y-4">
        {mode === 'existing' ? (
          <>
            <p className="text-xs text-gray-500">Grant access by email (login identity).</p>
            <Field label="Email" required mono type="email" value={email} onChange={setEmail} placeholder="jane@example.com" />
          </>
        ) : (
          <>
            <Field label="Display Name" required value={displayName} onChange={setDisplayName} placeholder="Jane Operator" />
            <Field label="Email (login)" required mono type="email" value={email} onChange={setEmail} placeholder="jane@example.com" />
            <p className="text-xs text-gray-500 -mt-2">Email is the unique login identity (same as SSO). No separate username.</p>
            <Field label="Temporary Password" required type="password" value={password} onChange={setPassword} placeholder="Communicate out-of-band" />
          </>
        )}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Org role</label>
          <select value={role} onChange={(e) => setRole(e.target.value)} className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm">
            {ORG_ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </div>
        <ModalActions busy={busy} submitLabel="Add Member" onClose={onClose} />
      </form>
    </ModalShell>
  );
}

function SsoCard({ orgId, showToast }: { orgId: string; showToast: (m: string, t: ToastType) => void }) {
  const [sso, setSso] = useState<OrgSso | null>(null);
  const [provider, setProvider] = useState('none');
  const [idpTenantId, setIdpTenantId] = useState('');
  const [idpClientId, setIdpClientId] = useState('');
  const [domain, setDomain] = useState('');
  const [enabled, setEnabled] = useState(false);
  const [clientSecret, setClientSecret] = useState('');
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      const s = await api.getOrgSso(orgId);
      setSso(s); setProvider(s.provider); setIdpTenantId(s.idpTenantId ?? '');
      setIdpClientId(s.idpClientId ?? ''); setDomain(s.domain ?? ''); setEnabled(s.enabled);
    } catch { /* non-fatal */ }
  }, [orgId]);
  useEffect(() => { load(); }, [load]);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      const s = await api.updateOrgSso(orgId, {
        provider, idpTenantId: idpTenantId.trim() || undefined, idpClientId: idpClientId.trim() || undefined,
        domain: domain.trim() || undefined, enabled, clientSecret: clientSecret.trim() || undefined,
      });
      setSso(s); setClientSecret('');
      showToast('SSO settings saved.', 'success');
    } catch (err: any) { showToast(err?.message ?? 'Failed to save SSO.', 'error'); }
    finally { setSaving(false); }
  };

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6">
      <h2 className="text-lg font-semibold text-gray-900 mb-1 flex items-center gap-2">
        Single Sign-On
        <span className="text-xs font-medium px-2 py-0.5 rounded-full bg-amber-100 text-amber-800">
          config only — not enforced
        </span>
      </h2>
      <p className="text-sm text-gray-500 mb-4">
        Settings are stored for future use ({sso?.enforcementStatus ?? 'not_implemented'}).
        <span className="text-amber-700"> Per-org login federation is not enforced yet</span> —
        the platform authenticates via its global Entra tenant and local accounts only.
      </p>
      <form onSubmit={save} className="space-y-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Provider</label>
          <div className="grid grid-cols-3 gap-2">
            {['none', 'entra', 'oidc'].map((p) => (
              <button key={p} type="button" onClick={() => setProvider(p)}
                className={`px-3 py-2 rounded-lg border text-sm font-medium ${provider === p ? 'border-azure-500 bg-azure-50 text-azure-700' : 'border-gray-300 text-gray-600 hover:bg-gray-50'}`}>
                {p === 'none' ? 'None' : p === 'entra' ? 'Microsoft Entra' : 'OIDC'}
              </button>
            ))}
          </div>
        </div>
        {provider !== 'none' && (
          <>
            <Field label={provider === 'entra' ? 'Entra Tenant ID' : 'Issuer / Tenant'} mono value={idpTenantId} onChange={setIdpTenantId}
              placeholder={provider === 'entra' ? '00000000-0000-0000-0000-000000000000' : 'https://idp.example.com/'} />
            <Field label="Client ID" mono value={idpClientId} onChange={setIdpClientId} placeholder="Application (client) ID" />
            <Field label="Email Domain" value={domain} onChange={setDomain} placeholder="example.com" />
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Client Secret</label>
              <input type="password" value={clientSecret} onChange={(e) => setClientSecret(e.target.value)}
                placeholder={sso?.clientSecretConfigured ? '•••••••• (configured — leave blank to keep)' : 'Not set'}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-azure-500 focus:border-azure-500" />
            </div>
            <label className="flex items-center gap-2 text-sm text-gray-700">
              <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} className="rounded border-gray-300" />
              Enabled
            </label>
          </>
        )}
        <div className="flex justify-end">
          <button type="submit" disabled={saving} className="px-4 py-2 bg-azure-600 text-white rounded-lg text-sm hover:bg-azure-700 disabled:opacity-50 flex items-center gap-2">
            {saving && <Spinner />}{saving ? 'Saving...' : 'Save SSO Settings'}
          </button>
        </div>
      </form>
    </section>
  );
}
