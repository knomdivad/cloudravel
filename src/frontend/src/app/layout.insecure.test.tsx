import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import RootLayout from './layout';

vi.mock('next/navigation', () => ({
  usePathname: () => '/',
}));

vi.mock('../lib/msalInstance', () => ({
  entraConfigured: false,
  entraBlockedByInsecureContext: () => false,
  getMsalInstance: () => null,
}));

vi.mock('../lib/api', () => ({
  api: {
    getPlatformInfo: vi.fn().mockResolvedValue({ environment: 'Development', version: '1.0.0', commit: null }),
  },
  getMe: vi.fn(),
}));

describe('RootLayout without MSAL', () => {
  it('renders the local login form', async () => {
    render(
      <RootLayout>
        <div>child</div>
      </RootLayout>,
      { container: document.body },
    );

    await waitFor(() => {
      expect(screen.getByText('Email')).toBeInTheDocument();
      expect(screen.getByText('Password')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    });
  });
});
