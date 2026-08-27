'use client';

import { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import { useAuth } from '@/contexts/AuthContext';
import type { SystemSettings, AdminUser } from '@/lib/types';
import { Field, ModalShell, ModalActions, Spinner, ErrorBox, Toast, AccessDenied, type ToastType } from '@/components/admin-ui';

export default function AdminPage() {
  const { isSystemAdmin, systemRole } = useAuth();
  const [toast, setToast] = useState<{ message: string; type: ToastType } | null>(null);
  const showToast = useCallback((message: string, type: ToastType) => {
    setToast({ message, type });
    setTimeout(() => setToast(null), 5000);
  }, []);

  // systemRole === null while /me is still loading; only deny once we know.
  if (systemRole !== null && !isSystemAdmin) return <AccessDenied />;

  return (
    <div className="space-y-8 max-w-5xl">
      {toast && <Toast toast={toast} onClose={() => setToast(null)} />}
      <div>
        <h1 className="text-2xl font-bold text-gray-900">System Administration</h1>
        <p className="text-sm text-gray-500 mt-1">Instance-wide settings and global user management.</p>
      </div>
      <SystemSettingsCard showToast={showToast} />
      <UsersCard showToast={showToast} />
      <AboutCard />
    </div>
  );
}

function AboutCard() {
  const { userName, systemRole } = useAuth();
  const [info, setInfo] = useState<{ environment: string; version: string; commit: string | null } | null>(null);

  useEffect(() => {
    api.getPlatformInfo().then(setInfo).catch(() => {});
  }, []);

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6">
      <h2 className="text-lg font-semibold text-gray-900 mb-1">About</h2>
      <p className="text-sm text-gray-500 mb-4">Build and instance information.</p>
      <dl className="grid grid-cols-2 gap-x-6 gap-y-3 text-sm">
        <div>
          <dt className="text-gray-500">Version</dt>
          <dd className="font-mono text-gray-900">{info ? `v${info.version}` : '—'}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Commit</dt>
          <dd className="font-mono text-gray-900">{info?.commit ? info.commit.slice(0, 7) : '—'}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Environment</dt>
          <dd className="text-gray-900">{info?.environment ?? '—'}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Signed in as</dt>
          <dd className="text-gray-900">{userName} <span className="text-gray-400">({systemRole ?? '—'})</span></dd>
        </div>
      </dl>
    </section>
  );
}

function SystemSettingsCard({ showToast }: { showToast: (m: string, t: ToastType) => void }) {
  const [settings, setSettings] = useState<SystemSettings | null>(null);
  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);

  const load = useCallback(async () => {
    try {
      const s = await api.getSystemSettings();
      setSettings(s);
      setBaseUrl(s.openAiBaseUrl ?? '');
      setModel(s.openAiModel ?? '');
    } catch {
      /* non-fatal */
    }
  }, []);
  useEffect(() => { load(); }, [load]);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      const updated = await api.updateSystemSettings({
        openAiBaseUrl: baseUrl.trim(),
        openAiModel: model.trim(),
        openAiApiKey: apiKey.trim() || undefined,
      });
      setSettings(updated);
      setApiKey('');
      showToast('AI settings saved.', 'success');
    } catch (err: any) {
      showToast(err?.message ?? 'Failed to save settings.', 'error');
    } finally {
      setSaving(false);
    }
  };

  const test = async () => {
    setTesting(true);
    try {
      const result = await api.testAiConnection();
      showToast(result.message ?? 'Connected.', 'success');
    } catch (err: any) {
      showToast(err?.message ?? 'Connection test failed.', 'error');
    } finally {
      setTesting(false);
    }
  };

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6">
      <h2 className="text-lg font-semibold text-gray-900 mb-1">AI Model (OpenAI-compatible)</h2>
      <p className="text-sm text-gray-500 mb-4">
        Used by AI Insights. Any OpenAI-compatible endpoint. The API key is stored in the secret store, never in the database.
        A 429 on the first prompt is usually billing quota or a 0 TPM project limit at platform.openai.com — not traffic.
        Save, then use Test connection (a tiny no-tools ping) before asking Insights.
      </p>
      <form onSubmit={save} className="space-y-4">
        <Field label="Base URL" value={baseUrl} onChange={setBaseUrl} mono placeholder="https://api.openai.com/v1 (blank = OpenAI default)" />
        <Field label="Model" value={model} onChange={setModel} mono placeholder="gpt-5.5" />
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">API Key</label>
          <input
            type="password" value={apiKey} onChange={(e) => setApiKey(e.target.value)}
            placeholder={settings?.apiKeyConfigured ? '•••••••• (configured — leave blank to keep)' : 'Not set'}
            className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-azure-500 focus:border-azure-500"
          />
        </div>
        <div className="flex justify-end gap-2">
          <button type="button" disabled={testing || saving} onClick={test}
            className="px-4 py-2 border border-gray-300 text-gray-800 rounded-lg text-sm hover:bg-gray-50 disabled:opacity-50 flex items-center gap-2">
            {testing && <Spinner />}{testing ? 'Testing...' : 'Test connection'}
          </button>
          <button type="submit" disabled={saving}
            className="px-4 py-2 bg-azure-600 text-white rounded-lg text-sm hover:bg-azure-700 disabled:opacity-50 flex items-center gap-2">
            {saving && <Spinner />}{saving ? 'Saving...' : 'Save Settings'}
          </button>
        </div>
      </form>
    </section>
  );
}

function UsersCard({ showToast }: { showToast: (m: string, t: ToastType) => void }) {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [showCreate, setShowCreate] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setLoadError(null);
      setUsers(await api.getAllUsers());
    } catch (err: any) {
      setLoadError(err?.message ?? 'Failed to load users.');
      setUsers([]);
    }
  }, []);
  useEffect(() => { load(); }, [load]);

  const setRole = async (u: AdminUser, role: string) => {
    try { await api.updateUser(u.userId, { globalRole: role }); showToast(`${u.displayName} → ${role}.`, 'success'); load(); }
    catch (err: any) { showToast(err?.message ?? 'Failed.', 'error'); }
  };
  const toggleActive = async (u: AdminUser) => {
    try { await api.updateUser(u.userId, { isActive: !u.isActive }); load(); }
    catch (err: any) { showToast(err?.message ?? 'Failed.', 'error'); }
  };

  return (
    <section className="bg-white rounded-lg border border-gray-200 p-6">
      {showCreate && <CreateUserModal onClose={() => setShowCreate(false)} onCreated={(created) => {
        setShowCreate(false);
        if (created) setUsers((prev) => {
          const rest = prev.filter((u) => u.userId !== created.userId && u.email !== created.email);
          return [...rest, created].sort((a, b) => a.displayName.localeCompare(b.displayName));
        });
        load();
        showToast('User created.', 'success');
      }} />}
      <div className="flex items-center justify-between mb-4">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Users</h2>
          <p className="text-sm text-gray-500">All platform users. System admins can create users and grant system access.</p>
        </div>
        <button onClick={() => setShowCreate(true)} className="px-4 py-2 bg-azure-600 text-white rounded-lg text-sm hover:bg-azure-700">+ Add User</button>
      </div>
      {loadError && <ErrorBox>{loadError}</ErrorBox>}
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-gray-500 border-b border-gray-100">
              <th className="py-2 pr-4 font-medium">Name</th>
              <th className="py-2 pr-4 font-medium">Email (login)</th>
              <th className="py-2 pr-4 font-medium">System role</th>
              <th className="py-2 pr-4 font-medium">Status</th>
              <th className="py-2 pr-4 font-medium">Last login</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {users.length === 0 && !loadError && (
              <tr><td colSpan={5} className="py-6 text-center text-gray-400 text-sm">No users loaded.</td></tr>
            )}
            {users.map((u) => (
              <tr key={u.userId || u.email}>
                <td className="py-2.5 pr-4 font-medium text-gray-900">{u.displayName}</td>
                <td className="py-2.5 pr-4 text-gray-500">
                  <div className="font-mono text-xs">{u.email || u.username}</div>
                </td>
                <td className="py-2.5 pr-4">
                  <select value={u.globalRole} onChange={(e) => setRole(u, e.target.value)}
                    className="border border-gray-300 rounded px-2 py-1 text-xs">
                    <option value="system_admin">system_admin</option>
                    <option value="member">member</option>
                  </select>
                </td>
                <td className="py-2.5 pr-4">
                  <button onClick={() => toggleActive(u)}
                    className={`px-2 py-0.5 rounded-full text-xs font-medium ${u.isActive ? 'bg-green-100 text-green-800' : 'bg-gray-200 text-gray-600'}`}>
                    {u.isActive ? 'active' : 'disabled'}
                  </button>
                </td>
                <td className="py-2.5 pr-4 text-gray-400 text-xs">{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : 'never'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function CreateUserModal({ onClose, onCreated }: { onClose: () => void; onCreated: (created?: AdminUser) => void }) {
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [globalRole, setGlobalRole] = useState('member');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const eaddr = email.trim().toLowerCase();
      // Email is the unique login identity (username = email server-side).
      const created = await api.createUser({ displayName: displayName.trim(), email: eaddr, username: eaddr, password, globalRole });
      onCreated(created);
    } catch (err: any) { setError(err?.message ?? 'Failed to create user.'); }
    finally { setBusy(false); }
  };

  return (
    <ModalShell title="Add User" onClose={onClose}>
      {error && <ErrorBox>{error}</ErrorBox>}
      <form onSubmit={submit} className="space-y-4">
        <Field label="Display Name" required value={displayName} onChange={setDisplayName} placeholder="Jane Operator" />
        <Field label="Email (login)" required mono type="email" value={email} onChange={setEmail} placeholder="jane@example.com" />
        <p className="text-xs text-gray-500 -mt-2">Email is the unique login identity (same as SSO). No separate username.</p>
        <Field label="Temporary Password" required type="password" value={password} onChange={setPassword} placeholder="Communicate out-of-band" />
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">System role</label>
          <select value={globalRole} onChange={(e) => setGlobalRole(e.target.value)} className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm">
            <option value="member">member — no system privileges</option>
            <option value="system_admin">system_admin — full control</option>
          </select>
        </div>
        <ModalActions busy={busy} submitLabel="Create User" onClose={onClose} />
      </form>
    </ModalShell>
  );
}

