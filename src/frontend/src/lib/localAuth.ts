/**
 * Local username/password authentication — the non-Entra login path.
 * Lets the platform run without an Entra tenant configured at all (local
 * dev, or self-hosting on any cloud). Entra ID SSO (msalInstance.ts) is
 * completely independent of this file.
 */

const STORAGE_KEY = 'aim-local-token';
const API_BASE = process.env.NEXT_PUBLIC_API_BASE_URL || 'http://localhost:7071/api';

export interface LocalSession {
  token: string;
  expiresAt: string;
  user: {
    userId: string;
    displayName: string;
    email: string;
    globalRole: string;
  };
}

/** Returns the stored local session, or null if absent/expired (and clears it if expired). */
export function getStoredLocalToken(): LocalSession | null {
  if (typeof window === 'undefined') return null;

  const raw = sessionStorage.getItem(STORAGE_KEY);
  if (!raw) return null;

  try {
    const parsed: LocalSession = JSON.parse(raw);
    if (new Date(parsed.expiresAt).getTime() <= Date.now()) {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }
    return parsed;
  } catch {
    sessionStorage.removeItem(STORAGE_KEY);
    return null;
  }
}

export function clearStoredLocalToken(): void {
  if (typeof window === 'undefined') return;
  sessionStorage.removeItem(STORAGE_KEY);
}

/** Calls POST /api/auth/login and persists the returned session. */
export async function loginLocal(username: string, password: string): Promise<LocalSession> {
  const response = await fetch(`${API_BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  });

  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: 'Invalid username or password.' }));
    throw new Error(error.message || 'Invalid username or password.');
  }

  const session: LocalSession = await response.json();
  sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  return session;
}
