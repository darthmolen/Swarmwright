import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

// Mock MSAL
const mockUseMsal = vi.fn();
vi.mock('@azure/msal-react', () => ({
  useMsal: () => mockUseMsal(),
  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  AuthenticatedTemplate: ({ children }: { children: React.ReactNode }) => {
    const { accounts } = mockUseMsal();
    return accounts.length > 0 ? <>{children}</> : null;
  },
  UnauthenticatedTemplate: ({ children }: { children: React.ReactNode }) => {
    const { accounts } = mockUseMsal();
    return accounts.length === 0 ? <>{children}</> : null;
  },
}));

// Mock auth modules
vi.mock('../auth/useAuthToken', () => ({
  useAuthToken: () => ({
    getToken: async () => 'mock-token',
  }),
}));

vi.mock('../auth/ConfigContext', () => ({
  ConfigContext: { Provider: ({ children }: { children: React.ReactNode }) => <>{children}</> },
  useConfig: () => ({
    clientId: 'test-client-id',
    tenantId: 'test-tenant-id',
    defaultScope: 'api://test/.default',
    requiredPermissions: ['api://test/.default'],
  }),
}));

// Mock components that break in jsdom
vi.mock('../components/SwarmControls', () => ({
  SwarmControls: () => <div data-testid="swarm-controls">SwarmControls</div>,
}));

vi.mock('../components/InboxFeed', () => ({
  InboxFeed: () => <div>InboxFeed</div>,
}));

vi.mock('../hooks/useSSE', () => ({
  useSSE: () => ({ connected: false, disconnect: () => {} }),
}));

vi.mock('../hooks/useMermaid', () => ({
  useMermaid: () => {},
}));

// Import App after mocks are set up
import App from '../App';

beforeEach(() => {
  vi.restoreAllMocks();
  sessionStorage.clear();
});

describe('MSAL Auth', () => {
  it('shows login when user is not authenticated', async () => {
    mockUseMsal.mockReturnValue({
      instance: { loginPopup: vi.fn(), acquireTokenSilent: vi.fn() },
      accounts: [],
      inProgress: 'none',
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Sign in with Microsoft')).toBeTruthy();
    });
  });

  it('shows dashboard when user is authenticated', async () => {
    mockUseMsal.mockReturnValue({
      instance: {
        loginPopup: vi.fn(),
        acquireTokenSilent: vi.fn().mockResolvedValue({ accessToken: 'token' }),
        getAllAccounts: () => [{ username: 'test@example.com' }],
      },
      accounts: [{ username: 'test@example.com' }],
      inProgress: 'none',
    });

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: async () => ({ templates: [] }),
    } as Response);

    render(<App />);

    await waitFor(() => {
      expect(screen.queryByText('Sign in with Microsoft')).toBeNull();
    });
  });
});
