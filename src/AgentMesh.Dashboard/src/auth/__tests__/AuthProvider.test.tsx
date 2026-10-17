import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import React from 'react';

// Mock react-oidc-context
const mockSigninRedirect = vi.fn();
const mockUseOidcAuth = vi.fn();

vi.mock('react-oidc-context', () => ({
  useAuth: () => mockUseOidcAuth(),
  hasAuthParams: vi.fn(() => false),
  AuthProvider: ({ children }: { children: React.ReactNode }) => children
}));

// Import AppAuthProvider after mocking
import { AppAuthProvider } from '../AuthProvider';

describe('AppAuthProvider & AuthGate', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
});
