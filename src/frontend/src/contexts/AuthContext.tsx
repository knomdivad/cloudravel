'use client';

import React, { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { useMsal } from '@azure/msal-react';
import { clearStoredLocalToken, getStoredLocalToken, loginLocal as apiLoginLocal, LocalSession } from '../lib/localAuth';

interface AuthContextValue {
  isAuthenticated: boolean;
  authMode: 'entra' | 'local' | null;
  userName: string;
  loginError: string | null;
  loginLocal: (username: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

/**
 * Unifies session state across the two independent login paths — Entra ID
 * SSO (via MSAL) and local username/password — so the rest of the app
 * (layout.tsx's login gate, AppShell's user display) doesn't need to know
 * which one is active.
 */
export function AuthProvider({ children }: { children: React.ReactNode }) {
  const { instance, accounts } = useMsal();
  const [localSession, setLocalSession] = useState<LocalSession | null>(null);
  const [loginError, setLoginError] = useState<string | null>(null);

  useEffect(() => {
    setLocalSession(getStoredLocalToken());
  }, []);

  const hasMsalAccount = accounts.length > 0;
  const authMode: 'entra' | 'local' | null = hasMsalAccount ? 'entra' : localSession ? 'local' : null;

  const userName = hasMsalAccount
    ? accounts[0]?.name || accounts[0]?.username || 'User'
    : localSession?.user.displayName || 'User';

  const loginLocal = useCallback(async (username: string, password: string) => {
    setLoginError(null);
    try {
      const session = await apiLoginLocal(username, password);
      setLocalSession(session);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Login failed.';
      setLoginError(message);
      throw err;
    }
  }, []);

  const logout = useCallback(() => {
    clearStoredLocalToken();
    setLocalSession(null);
    if (hasMsalAccount) {
      instance.logoutRedirect();
    }
  }, [hasMsalAccount, instance]);

  return (
    <AuthContext.Provider
      value={{ isAuthenticated: authMode !== null, authMode, userName, loginError, loginLocal, logout }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
