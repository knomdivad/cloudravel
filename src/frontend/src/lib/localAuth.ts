/**
 * Local username/password authentication — the non-Entra login path.
 * Lets the platform run without an Entra tenant configured at all (local
 * dev, or self-hosting on any cloud). Entra ID SSO (msalInstance.ts) is
 * completely independent of this file.
 */

const STORAGE_KEY = 'cloudravel-local-token';
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

/** Normalize API login payloads (camelCase or PascalCase) into LocalSession. */
function normalizeSession(raw: Record<string, unknown>): LocalSession | null {
  const token = (raw.token ?? raw.Token) as string | undefined;
  const expiresAtRaw = raw.expiresAt ?? raw.ExpiresAt;
  const userRaw = (raw.user ?? raw.User) as Record<string, unknown> | undefined;
  if (!token || !expiresAtRaw || !userRaw) return null;

  const expiresAt = typeof expiresAtRaw === 'string'
    ? expiresAtRaw
    : new Date(expiresAtRaw as string | number | Date).toISOString();

  return {
    token,
    expiresAt,
    user: {
      userId: String(userRaw.userId ?? userRaw.UserId ?? ''),
      displayName: String(userRaw.displayName ?? userRaw.DisplayName ?? 'User'),
      email: String(userRaw.email ?? userRaw.Email ?? ''),
      globalRole: String(userRaw.globalRole ?? userRaw.GlobalRole ?? 'member'),
    },
  };
}

/** Returns the stored local session, or null if absent/expired (and clears it if expired). */
export function getStoredLocalToken(): LocalSession | null {
  if (typeof window === 'undefined') return null;

  const raw = sessionStorage.getItem(STORAGE_KEY);
  if (!raw) return null;

  try {
    const parsed = normalizeSession(JSON.parse(raw) as Record<string, unknown>);
    if (!parsed) {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }
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

  const payload = await response.json().catch(() => ({} as Record<string, unknown>));

  if (!response.ok) {
    const message =
      (payload as { message?: string; Message?: string }).message
      || (payload as { message?: string; Message?: string }).Message
      || (response.status === 429
        ? 'Too many login attempts. Try again in a minute.'
        : 'Invalid username or password.');
    throw new Error(message);
  }

  const session = normalizeSession(payload as Record<string, unknown>);
  if (!session?.token) {
    throw new Error('Login succeeded but the server returned an unexpected response.');
  }

  sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  return session;
}
