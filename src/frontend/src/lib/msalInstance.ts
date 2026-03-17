import { PublicClientApplication } from '@azure/msal-browser';
import { msalConfig } from './auth';

/**
 * Singleton MSAL instance shared across the entire app.
 * Both the MsalProvider (layout.tsx) and API client (api.ts) use this
 * so that login state is visible to token acquisition.
 */
export const msalInstance = new PublicClientApplication(msalConfig);
