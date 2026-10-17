import { PublicClientApplication } from '@azure/msal-browser';
import { msalConfig } from './auth';

/**
 * Singleton MSAL instance shared across the entire app.
 * Both the MsalProvider (layout.tsx) and API client (api.ts) use this
 * so that login state is visible to token acquisition.
 *
 * Created lazily on first use only when Entra is configured and the browser
 * exposes Web Crypto in a secure context (HTTPS or localhost).
 */
export const entraConfigured = Boolean(process.env.NEXT_PUBLIC_AZURE_AD_CLIENT_ID);

let cachedInstance: PublicClientApplication | null = null;
let instanceCreated = false;

function canCreateMsalInBrowser(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext &&
    Boolean(window.crypto?.subtle)
  );
}

/**
 * Returns the MSAL client when Entra is enabled and this origin supports it;
 * otherwise null (local auth only).
 */
export function getMsalInstance(): PublicClientApplication | null {
  if (!entraConfigured || !canCreateMsalInBrowser()) {
    return null;
  }
  if (!instanceCreated) {
    cachedInstance = new PublicClientApplication(msalConfig);
    instanceCreated = true;
  }
  return cachedInstance;
}

/** True when Entra env vars are set but this page is not a secure context. */
export function entraBlockedByInsecureContext(): boolean {
  return entraConfigured && typeof window !== 'undefined' && !window.isSecureContext;
}
