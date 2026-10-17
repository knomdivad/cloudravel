import { beforeEach, describe, expect, it, vi } from 'vitest';

const PublicClientApplicationMock = vi.hoisted(() =>
  vi.fn(function MockPublicClientApplication() {
    return { initialize: vi.fn().mockResolvedValue(undefined) };
  }),
);

vi.mock('@azure/msal-browser', () => ({
  PublicClientApplication: PublicClientApplicationMock,
}));

describe('msalInstance', () => {
  beforeEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
    PublicClientApplicationMock.mockClear();
  });

  it('imports without throw and getMsalInstance is null in a non-secure context', async () => {
    vi.stubEnv('NEXT_PUBLIC_AZURE_AD_CLIENT_ID', 'test-client-id');
    vi.stubGlobal('window', {
      isSecureContext: false,
      location: { origin: 'http://cloudravel-insecure.test:3000' },
      crypto: { getRandomValues: (arr: Uint8Array) => arr },
    });

    const mod = await import('./msalInstance');
    expect(() => mod.getMsalInstance()).not.toThrow();
    expect(mod.getMsalInstance()).toBeNull();
    expect(PublicClientApplicationMock).not.toHaveBeenCalled();
  });

  it('getMsalInstance is null when Entra client id is not configured', async () => {
    vi.stubEnv('NEXT_PUBLIC_AZURE_AD_CLIENT_ID', '');
    vi.stubGlobal('window', {
      isSecureContext: true,
      location: { origin: 'http://localhost:3000' },
      crypto: { subtle: {}, getRandomValues: (arr: Uint8Array) => arr },
    });

    const mod = await import('./msalInstance');
    expect(mod.getMsalInstance()).toBeNull();
    expect(PublicClientApplicationMock).not.toHaveBeenCalled();
  });

  it('returns a PublicClientApplication when Entra is configured and the context is secure', async () => {
    vi.stubEnv('NEXT_PUBLIC_AZURE_AD_CLIENT_ID', 'test-client-id');
    vi.stubGlobal('window', {
      isSecureContext: true,
      location: { origin: 'https://app.example.com' },
      crypto: { subtle: {}, getRandomValues: (arr: Uint8Array) => arr },
    });

    const mod = await import('./msalInstance');
    const instance = mod.getMsalInstance();
    expect(instance).not.toBeNull();
    expect(PublicClientApplicationMock).toHaveBeenCalledTimes(1);
    expect(mod.getMsalInstance()).toBe(instance);
    expect(PublicClientApplicationMock).toHaveBeenCalledTimes(1);
  });
});
