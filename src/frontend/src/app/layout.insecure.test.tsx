import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import RootLayout from './layout';

vi.mock('next/navigation', () => ({
  usePathname: () => '/',
}));

const msalModuleMocks = vi.hoisted(() => ({
  entraConfigured: false,
  entraBlockedByInsecureContext: vi.fn(() => false),
  getMsalInstance: vi.fn(() => null),
}));

vi.mock('../lib/msalInstance', () => msalModuleMocks);

vi.mock('../lib/api', () => ({
  api: {
    getPlatformInfo: vi.fn().mockResolvedValue({ environment: 'Development', version: '1.0.0', commit: null }),
  },
  getMe: vi.fn(),
}));

afterEach(() => {
  cleanup();
});

async function renderLoginShell() {
  const mount = document.createElement('div');
  document.body.appendChild(mount);
  const view = render(
    <RootLayout>
      <div>child</div>
    </RootLayout>,
    { container: mount },
  );
  await waitFor(() => {
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
  });
  return { view, mount };
}

describe('RootLayout without MSAL', () => {
  it('renders the local login form', async () => {
    msalModuleMocks.entraBlockedByInsecureContext.mockReturnValue(false);
    await renderLoginShell();

    expect(screen.getByText('Email', { exact: true })).toBeInTheDocument();
    expect(screen.getByText('Password', { exact: true })).toBeInTheDocument();
  });

  it('shows the Entra HTTPS banner when Entra is blocked by an insecure context', async () => {
    msalModuleMocks.entraBlockedByInsecureContext.mockReturnValue(true);
    await renderLoginShell();

    expect(screen.getByRole('alert')).toHaveTextContent('Entra sign-in needs HTTPS');
  });

  it('hides the Entra HTTPS banner when the insecure-context check is false', async () => {
    msalModuleMocks.entraBlockedByInsecureContext.mockReturnValue(false);
    await renderLoginShell();

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
