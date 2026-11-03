import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import React from 'react';

// Mock react-oidc-context
const mockSigninRedirect = vi.fn();
const mockSigninSilent = vi.fn();
const mockRemoveUser = vi.fn();
const mockUseOidcAuth = vi.fn();

vi.mock('react-oidc-context', () => ({
  useAuth: () => mockUseOidcAuth(),
  hasAuthParams: vi.fn(() => false),
  AuthProvider: ({ children }: { children: React.ReactNode }) => children
}));

// Mock api service
const mockVerifyAuth = vi.fn().mockResolvedValue({ authenticated: true });
let capturedUnauthorizedHandler: (() => Promise<void> | void) | null = null;

vi.mock('../../services/api', () => ({
  setAuthTokenProvider: vi.fn(),
  setUnauthorizedHandler: vi.fn((handler) => {
    capturedUnauthorizedHandler = handler;
  }),
  api: {
    verifyAuth: () => mockVerifyAuth()
  }
}));

// Import AppAuthProvider after mocking
import { AppAuthProvider } from '../AuthProvider';

describe('AppAuthProvider & AuthGate', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    capturedUnauthorizedHandler = null;
    mockVerifyAuth.mockResolvedValue({ authenticated: true });
    mockSigninRedirect.mockResolvedValue(undefined);
    mockSigninSilent.mockResolvedValue(null);
    mockRemoveUser.mockResolvedValue(undefined);
  });

  it('renders loading state when auth is loading', () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: true,
      isAuthenticated: false,
      error: null,
      signinRedirect: mockSigninRedirect,
      activeNavigator: null,
      user: null
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(screen.getByText('Authenticating with AgentMesh SSO...')).toBeInTheDocument();
    expect(screen.queryByText('Protected App Content')).not.toBeInTheDocument();
  });

  it('renders error UI when OIDC auth fails (e.g. Failed to fetch)', () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      error: new Error('Failed to fetch'),
      signinRedirect: mockSigninRedirect,
      activeNavigator: null,
      user: null
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(screen.getByText('SSO Authentication Failed')).toBeInTheDocument();
    expect(screen.getByText('Failed to fetch')).toBeInTheDocument();

    const retryButton = screen.getByRole('button', { name: /retry login/i });
    fireEvent.click(retryButton);
    expect(mockSigninRedirect).toHaveBeenCalled();
  });

  it('renders error UI when an error occurs during callback processing even if hasAuthParams is true', () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      error: new Error('missing client_secret on token request'),
      signinRedirect: mockSigninRedirect,
      activeNavigator: null,
      user: null
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    // It should render the error UI and NOT get stuck in the loading spinner
    expect(screen.getByText('SSO Authentication Failed')).toBeInTheDocument();
    expect(screen.getByText('missing client_secret on token request')).toBeInTheDocument();
    expect(screen.queryByText('Authenticating with AgentMesh SSO...')).not.toBeInTheDocument();
  });

  it('renders children when user is authenticated', () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      error: null,
      signinRedirect: mockSigninRedirect,
      activeNavigator: null,
      user: {
        access_token: 'test-token',
        profile: {
          email: 'admin@agentmesh.local'
        }
      }
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(screen.getByText('Protected App Content')).toBeInTheDocument();
    expect(screen.queryByText('SSO Authentication Failed')).not.toBeInTheDocument();
  });

  it('initiates signinRedirect when unauthenticated and not loading', () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      error: null,
      signinRedirect: mockSigninRedirect.mockResolvedValue(undefined),
      activeNavigator: null,
      user: null
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(screen.getByText('Redirecting to Identity Provider...')).toBeInTheDocument();
    expect(mockSigninRedirect).toHaveBeenCalled();
  });

  it('registers unauthorized handler and redirects to login when 401 occurs and silent renew fails', async () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      error: null,
      signinRedirect: mockSigninRedirect,
      signinSilent: mockSigninSilent.mockRejectedValue(new Error('Silent renew failed')),
      removeUser: mockRemoveUser,
      activeNavigator: null,
      user: {
        access_token: 'expired-token',
        profile: { email: 'admin@agentmesh.local' }
      }
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(capturedUnauthorizedHandler).toBeDefined();

    // Trigger the 401 unauthorized handler
    await capturedUnauthorizedHandler!();

    await waitFor(() => {
      expect(mockRemoveUser).toHaveBeenCalled();
      expect(mockSigninRedirect).toHaveBeenCalled();
    });
  });

  it('renews user token when 401 occurs and silent renew succeeds without redirecting', async () => {
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      error: null,
      signinRedirect: mockSigninRedirect,
      signinSilent: mockSigninSilent.mockResolvedValue({
        access_token: 'new-valid-token',
        expired: false
      }),
      removeUser: mockRemoveUser,
      activeNavigator: null,
      user: {
        access_token: 'old-token',
        profile: { email: 'admin@agentmesh.local' }
      }
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(capturedUnauthorizedHandler).toBeDefined();

    // Trigger the 401 unauthorized handler
    await capturedUnauthorizedHandler!();

    await waitFor(() => {
      expect(mockSigninSilent).toHaveBeenCalled();
      expect(mockRemoveUser).not.toHaveBeenCalled();
      expect(mockSigninRedirect).not.toHaveBeenCalled();
    });
  });

  it('preserves return URL in localStorage before redirecting to login on 401', async () => {
    const originalPathname = window.location.pathname;
    try {
      window.history.pushState({}, '', '/?project=proj-123&job=job-456');

      mockUseOidcAuth.mockReturnValue({
        isLoading: false,
        isAuthenticated: true,
        error: null,
        signinRedirect: mockSigninRedirect,
        signinSilent: mockSigninSilent.mockRejectedValue(new Error('Silent renew failed')),
        removeUser: mockRemoveUser,
        activeNavigator: null,
        user: {
          access_token: 'expired-token',
          profile: { email: 'admin@agentmesh.local' }
        }
      });

      render(
        <AppAuthProvider>
          <div>Protected App Content</div>
        </AppAuthProvider>
      );

      await capturedUnauthorizedHandler!();

      await waitFor(() => {
        expect(localStorage.getItem('agentmesh_auth_return_url')).toBe('/?project=proj-123&job=job-456');
        expect(mockSigninRedirect).toHaveBeenCalled();
      });
    } finally {
      window.history.pushState({}, '', originalPathname);
      localStorage.removeItem('agentmesh_auth_return_url');
    }
  });

  it('triggers silent renewal when access token expires without forcefully logging out', async () => {
    let tokenExpiredCallback: (() => void) | null = null;
    mockUseOidcAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      error: null,
      signinRedirect: mockSigninRedirect,
      signinSilent: mockSigninSilent.mockRejectedValue(new Error('IFrame blocked')),
      removeUser: mockRemoveUser,
      activeNavigator: null,
      events: {
        addAccessTokenExpired: vi.fn((cb) => {
          tokenExpiredCallback = cb;
          return () => {};
        }),
        addSilentRenewError: vi.fn(() => () => {})
      },
      user: {
        access_token: 'expiring-token',
        profile: { email: 'admin@agentmesh.local' }
      }
    });

    render(
      <AppAuthProvider>
        <div>Protected App Content</div>
      </AppAuthProvider>
    );

    expect(tokenExpiredCallback).toBeDefined();
    // When token expires and silent renew fails, it should NOT boot user to login redirect
    tokenExpiredCallback!();

    await waitFor(() => {
      expect(mockSigninSilent).toHaveBeenCalled();
      expect(mockRemoveUser).not.toHaveBeenCalled();
      expect(mockSigninRedirect).not.toHaveBeenCalled();
    });
  });
});
