import { Configuration, LogLevel } from '@azure/msal-browser';

/**
 * MSAL configuration for Entra ID authentication.
 * 
 * The frontend authenticates users against the MSP's Entra ID tenant.
 * Tokens are scoped to the CloudRavel API (not directly to Azure resources).
 * Tenant-level access control is handled by the API using RBAC tables.
 */
export const msalConfig: Configuration = {
  auth: {
    clientId: process.env.NEXT_PUBLIC_AZURE_AD_CLIENT_ID || '',
    authority: `https://login.microsoftonline.com/${process.env.NEXT_PUBLIC_AZURE_AD_TENANT_ID}`,
    redirectUri: typeof window !== 'undefined' ? window.location.origin : '',
    postLogoutRedirectUri: typeof window !== 'undefined' ? window.location.origin : '',
  },
  cache: {
    cacheLocation: 'sessionStorage', // sessionStorage for security (not localStorage)
    storeAuthStateInCookie: false,
  },
  system: {
    loggerOptions: {
      loggerCallback: (level, message, containsPii) => {
        if (containsPii) return;
        switch (level) {
          case LogLevel.Error:
            console.error(message);
            break;
          case LogLevel.Warning:
            console.warn(message);
            break;
        }
      },
    },
  },
};

/** Scopes required for API access */
export const apiScopes = {
  default: [process.env.NEXT_PUBLIC_API_SCOPE || 'api://aim-api/.default'],
};

/** Login request */
export const loginRequest = {
  scopes: apiScopes.default,
};
