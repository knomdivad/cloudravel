'use client';

import { MsalProvider, useMsal } from '@azure/msal-react';
import { EventType } from '@azure/msal-browser';
import { SWRConfig } from 'swr';
import { loginRequest } from '../lib/auth';
import { swrConfig } from '../lib/hooks';
import { msalInstance } from '../lib/msalInstance';
import { TenantProvider } from '../contexts/TenantContext';
import { AuthProvider, useAuth } from '../contexts/AuthContext';
import './globals.css';
import React, { useEffect, useMemo, useState } from 'react';

const entraConfigured = Boolean(process.env.NEXT_PUBLIC_AZURE_AD_CLIENT_ID);

export default function RootLayout({ children }: { children: React.ReactNode }) {
  const [msalReady, setMsalReady] = useState(false);

  useEffect(() => {
    msalInstance.initialize().then(() => {
      // Set the first account as active if available
      const accounts = msalInstance.getAllAccounts();
      if (accounts.length > 0 && !msalInstance.getActiveAccount()) {
        msalInstance.setActiveAccount(accounts[0]);
      }

      // Listen for sign-in events
      msalInstance.addEventCallback((event) => {
        if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
          const payload = event.payload as any;
          if (payload.account) {
            msalInstance.setActiveAccount(payload.account);
          }
        }
      });

      setMsalReady(true);
    });
  }, []);

  if (!msalReady) {
    return (
      <html lang="en">
        <body className="bg-gray-50">
          <div className="flex items-center justify-center h-screen">
            <LoadingSpinner text="Initializing..." />
          </div>
        </body>
      </html>
    );
  }

  return (
    <html lang="en">
      <body className="bg-gray-50 text-gray-900 antialiased">
        <MsalProvider instance={msalInstance}>
          <SWRConfig value={swrConfig}>
            <AuthProvider>
              <AuthGate>{children}</AuthGate>
            </AuthProvider>
          </SWRConfig>
        </MsalProvider>
      </body>
    </html>
  );
}

// ============================================================================
// Auth gate — Entra ID (MSAL) and local username/password are independent
// login paths; either one being active is enough to show the app.
// ============================================================================

function AuthGate({ children }: { children: React.ReactNode }) {
  const { isAuthenticated } = useAuth();

  if (!isAuthenticated) {
    return <LoginPage />;
  }

  return (
    <TenantProvider>
      <AppShell>{children}</AppShell>
    </TenantProvider>
  );
}

function LoginPage() {
  const { instance } = useMsal();
  const { loginLocal, loginError } = useAuth();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const handleLocalLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    try {
      await loginLocal(username, password);
    } catch {
      // loginError is surfaced below; nothing further to do here
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="flex items-center justify-center h-screen bg-gradient-to-br from-azure-600 to-azure-900">
      <div className="bg-white rounded-xl shadow-2xl p-10 max-w-md w-full text-center">
        <div className="mb-6">
          <div className="w-16 h-16 bg-azure-100 rounded-full flex items-center justify-center mx-auto mb-4">
            <svg className="w-8 h-8 text-azure-600" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75 11.25 15 15 9.75m-3-7.036A11.959 11.959 0 0 1 3.598 6 11.99 11.99 0 0 0 3 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285Z" />
            </svg>
          </div>
          <h1 className="text-2xl font-bold text-gray-900">AnanVision</h1>
          <p className="text-gray-500 mt-2">
            Multi-cloud AIOps operating platform
          </p>
        </div>

        {entraConfigured && (
          <>
            <button
              onClick={() => instance.loginRedirect(loginRequest)}
              className="w-full bg-azure-600 text-white py-3 px-6 rounded-lg font-medium hover:bg-azure-700 transition-colors"
            >
              Sign in with Microsoft
            </button>
            <p className="text-xs text-gray-400 mt-4 mb-6">
              Secured by Microsoft Entra ID
            </p>
            <div className="flex items-center gap-3 mb-6">
              <div className="flex-1 h-px bg-gray-200" />
              <span className="text-xs text-gray-400 uppercase">or</span>
              <div className="flex-1 h-px bg-gray-200" />
            </div>
          </>
        )}

        <form onSubmit={handleLocalLogin} className="text-left space-y-3">
          <div>
            <label className="text-xs text-gray-500 block mb-1">Username</label>
            <input
              type="text"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              required
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            />
          </div>
          <div>
            <label className="text-xs text-gray-500 block mb-1">Password</label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:border-azure-500 focus:outline-none"
            />
          </div>
          {loginError && <p className="text-sm text-red-600">{loginError}</p>}
          <button
            type="submit"
            disabled={submitting}
            className="w-full border border-gray-300 text-gray-700 py-3 px-6 rounded-lg font-medium hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {submitting ? 'Signing in...' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  );
}

// ============================================================================
// App Shell (sidebar + content area)
// ============================================================================

import { useTenantContext } from '../contexts/TenantContext';

const navItems = [
  { label: 'Dashboard', href: '/', icon: DashboardIcon },
  { label: 'Operations', href: '/operations', icon: OperationsIcon },
  { label: 'Approvals', href: '/approvals', icon: ApprovalsIcon },
  { label: 'Inventory', href: '/inventory', icon: InventoryIcon },
  { label: 'Changes', href: '/changes', icon: ChangesIcon },
  { label: 'Security', href: '/security', icon: SecurityIcon },
  { label: 'Governance', href: '/governance', icon: GovernanceIcon },
  { label: 'AI Insights', href: '/ai', icon: AiIcon },
  { label: 'Tenants', href: '/tenants', icon: TenantsIcon },
];

function AppShell({ children }: { children: React.ReactNode }) {
  const { userName, logout } = useAuth();
  const { tenants, currentTenant, selectTenant } = useTenantContext();
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  return (
    <div className="flex h-screen overflow-hidden">
      {/* Sidebar */}
      <aside
        className={`bg-gray-900 text-white flex flex-col transition-all duration-200 ${
          sidebarCollapsed ? 'w-16' : 'w-64'
        }`}
      >
        {/* Logo */}
        <div className="flex items-center gap-3 px-4 py-4 border-b border-gray-700">
          <div className="w-8 h-8 bg-azure-500 rounded-lg flex items-center justify-center flex-shrink-0">
            <span className="text-white font-bold text-sm">A</span>
          </div>
          {!sidebarCollapsed && (
            <span className="font-semibold text-sm truncate">AnanVision</span>
          )}
        </div>

        {/* Tenant Switcher */}
        {!sidebarCollapsed && tenants.length > 0 && (
          <div className="px-3 py-3 border-b border-gray-700">
            <label className="text-xs text-gray-400 block mb-1">Tenant</label>
            <select
              value={currentTenant?.tenantId || ''}
              onChange={(e) => selectTenant(e.target.value)}
              className="w-full bg-gray-800 text-white text-sm rounded py-1.5 px-2 border border-gray-600 focus:border-azure-400 focus:outline-none"
            >
              {tenants.map((t) => (
                <option key={t.tenantId} value={t.tenantId}>
                  {t.displayName}
                </option>
              ))}
            </select>
          </div>
        )}

        {/* Navigation */}
        <nav className="flex-1 py-4 space-y-1 px-2 overflow-y-auto">
          {navItems.map((item) => (
            <a
              key={item.href}
              href={item.href}
              className="flex items-center gap-3 px-3 py-2.5 rounded-lg text-gray-300 hover:bg-gray-800 hover:text-white transition-colors text-sm"
            >
              <item.icon className="w-5 h-5 flex-shrink-0" />
              {!sidebarCollapsed && <span>{item.label}</span>}
            </a>
          ))}
        </nav>

        {/* User section */}
        <div className="border-t border-gray-700 p-3">
          {!sidebarCollapsed && (
            <div className="flex items-center gap-2 mb-2">
              <div className="w-8 h-8 bg-gray-700 rounded-full flex items-center justify-center text-xs">
                {userName.charAt(0).toUpperCase()}
              </div>
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium truncate">{userName}</p>
              </div>
            </div>
          )}
          <button
            onClick={logout}
            className={`text-gray-400 hover:text-white text-xs transition-colors ${
              sidebarCollapsed ? 'w-full text-center' : ''
            }`}
          >
            Sign out
          </button>
        </div>

        {/* Collapse toggle */}
        <button
          onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
          className="p-2 text-gray-400 hover:text-white border-t border-gray-700"
        >
          {sidebarCollapsed ? '→' : '←'}
        </button>
      </aside>

      {/* Main content */}
      <main className="flex-1 overflow-y-auto">
        {/* Top bar */}
        <header className="bg-white border-b border-gray-200 px-6 py-3 flex items-center justify-between sticky top-0 z-10">
          <div>
            {currentTenant && (
              <h2 className="text-lg font-semibold text-gray-900">{currentTenant.displayName}</h2>
            )}
          </div>
          <div className="flex items-center gap-3">
            {currentTenant && (
              <span
                className={`text-xs font-medium px-2 py-1 rounded-full ${
                  currentTenant.status === 'active'
                    ? 'bg-green-100 text-green-700'
                    : 'bg-yellow-100 text-yellow-700'
                }`}
              >
                {currentTenant.status}
              </span>
            )}
          </div>
        </header>

        {/* Page content */}
        <div className="p-6">{children}</div>
      </main>
    </div>
  );
}

// ============================================================================
// Icons (inline SVG components)
// ============================================================================

function DashboardIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M3.75 6A2.25 2.25 0 0 1 6 3.75h2.25A2.25 2.25 0 0 1 10.5 6v2.25a2.25 2.25 0 0 1-2.25 2.25H6a2.25 2.25 0 0 1-2.25-2.25V6ZM3.75 15.75A2.25 2.25 0 0 1 6 13.5h2.25a2.25 2.25 0 0 1 2.25 2.25V18a2.25 2.25 0 0 1-2.25 2.25H6A2.25 2.25 0 0 1 3.75 18v-2.25ZM13.5 6a2.25 2.25 0 0 1 2.25-2.25H18A2.25 2.25 0 0 1 20.25 6v2.25A2.25 2.25 0 0 1 18 10.5h-2.25a2.25 2.25 0 0 1-2.25-2.25V6ZM13.5 15.75a2.25 2.25 0 0 1 2.25-2.25H18a2.25 2.25 0 0 1 2.25 2.25V18A2.25 2.25 0 0 1 18 20.25h-2.25a2.25 2.25 0 0 1-2.25-2.25v-2.25Z" />
    </svg>
  );
}

function OperationsIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M3.75 13.5 9 7.5l4.5 4.5 6.75-8.25M3.75 20.25h16.5M6.75 17.25v-3m4.5 3v-6m4.5 6v-4.5" />
    </svg>
  );
}

function ApprovalsIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M11.35 3.836c-.065.21-.1.433-.1.664 0 .414.336.75.75.75h4.5a.75.75 0 0 0 .75-.75 2.25 2.25 0 0 0-.1-.664m-5.8 0A2.251 2.251 0 0 1 13.5 2.25H15c1.012 0 1.867.668 2.15 1.586m-5.8 0c-.376.023-.75.05-1.124.08C9.095 4.01 8.25 4.973 8.25 6.108V8.25m8.9-4.414c.376.023.75.05 1.124.08 1.131.094 1.976 1.057 1.976 2.192V16.5A2.25 2.25 0 0 1 18 18.75h-2.25m-7.5-10.5H4.875c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125h9.75c.621 0 1.125-.504 1.125-1.125V18.75m-7.5-10.5h6.375c.621 0 1.125.504 1.125 1.125v9.375m-8.25-3 1.5 1.5 3-3.75" />
    </svg>
  );
}

function InventoryIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 0v3.75m-16.5-3.75v3.75m16.5 0v3.75C20.25 16.153 16.556 18 12 18s-8.25-1.847-8.25-4.125v-3.75m16.5 0c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125" />
    </svg>
  );
}

function ChangesIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M12 6v6h4.5m4.5 0a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z" />
    </svg>
  );
}

function SecurityIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75 11.25 15 15 9.75m-3-7.036A11.959 11.959 0 0 1 3.598 6 11.99 11.99 0 0 0 3 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285Z" />
    </svg>
  );
}

function GovernanceIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 6h9.75M10.5 6a1.5 1.5 0 1 1-3 0m3 0a1.5 1.5 0 1 0-3 0M3.75 6H7.5m3 12h9.75m-9.75 0a1.5 1.5 0 0 1-3 0m3 0a1.5 1.5 0 0 0-3 0m-3.75 0H7.5m9-6h3.75m-3.75 0a1.5 1.5 0 0 1-3 0m3 0a1.5 1.5 0 0 0-3 0m-9.75 0h9.75" />
    </svg>
  );
}

function AiIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09ZM18.259 8.715 18 9.75l-.259-1.035a3.375 3.375 0 0 0-2.455-2.456L14.25 6l1.036-.259a3.375 3.375 0 0 0 2.455-2.456L18 2.25l.259 1.035a3.375 3.375 0 0 0 2.456 2.456L21.75 6l-1.035.259a3.375 3.375 0 0 0-2.456 2.456ZM16.894 20.567 16.5 21.75l-.394-1.183a2.25 2.25 0 0 0-1.423-1.423L13.5 18.75l1.183-.394a2.25 2.25 0 0 0 1.423-1.423l.394-1.183.394 1.183a2.25 2.25 0 0 0 1.423 1.423l1.183.394-1.183.394a2.25 2.25 0 0 0-1.423 1.423Z" />
    </svg>
  );
}

function TenantsIcon({ className }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" d="M18 18.72a9.094 9.094 0 0 0 3.741-.479 3 3 0 0 0-4.682-2.72m.94 3.198.001.031c0 .225-.012.447-.037.666A11.944 11.944 0 0 1 12 21c-2.17 0-4.207-.576-5.963-1.584A6.062 6.062 0 0 1 6 18.719m12 0a5.971 5.971 0 0 0-.941-3.197m0 0A5.995 5.995 0 0 0 12 12.75a5.995 5.995 0 0 0-5.058 2.772m0 0a3 3 0 0 0-4.681 2.72 8.986 8.986 0 0 0 3.74.477m.94-3.197a5.971 5.971 0 0 0-.94 3.197M15 6.75a3 3 0 1 1-6 0 3 3 0 0 1 6 0Zm6 3a2.25 2.25 0 1 1-4.5 0 2.25 2.25 0 0 1 4.5 0Zm-13.5 0a2.25 2.25 0 1 1-4.5 0 2.25 2.25 0 0 1 4.5 0Z" />
    </svg>
  );
}

function LoadingSpinner({ text }: { text?: string }) {
  return (
    <div className="flex flex-col items-center gap-3">
      <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-azure-600" />
      {text && <p className="text-sm text-gray-500">{text}</p>}
    </div>
  );
}
