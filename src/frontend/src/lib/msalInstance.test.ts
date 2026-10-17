import { beforeEach, describe, expect, it, vi } from 'vitest';

describe('msalInstance', () => {
  beforeEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
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
  });
});
