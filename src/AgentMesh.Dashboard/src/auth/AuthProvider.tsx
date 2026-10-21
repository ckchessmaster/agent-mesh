import { useEffect, type ReactNode } from 'react';
import { AuthProvider as OidcAuthProvider, useAuth as useOidcAuth, hasAuthParams } from 'react-oidc-context';
import { oidcConfig } from './authConfig';
import { api, setAuthTokenProvider, setUnauthorizedHandler } from '../services/api';
import { RefreshCw, ShieldAlert } from 'lucide-react';

interface AuthGateProps {
  children: ReactNode;
}

function AuthGate({ children }: AuthGateProps) {
  const auth = useOidcAuth();

  useEffect(() => {
    // Dynamically inject the active OIDC access token into the API client
    setAuthTokenProvider(() => auth.user?.access_token || auth.user?.id_token || null);
  }, [auth.user]);

  const isCallback = typeof window !== 'undefined' && window.location.pathname === '/auth/callback';

  // Handle 401 Unauthorized responses from the API
  useEffect(() => {
    let isHandling401 = false;
    const handleUnauthorized = async () => {
      if (isHandling401) return;
      isHandling401 = true;
      console.warn('API returned 401 Unauthorized. Attempting session renewal or redirecting to login...');
      try {
        if (auth.signinSilent) {
          const renewedUser = await auth.signinSilent();
          if (renewedUser && !renewedUser.expired) {
            isHandling401 = false;
            return;
          }
        }
      } catch (err) {
        console.warn('Silent session renewal failed after 401:', err);
      }

      try {
        await auth.removeUser?.();
      } catch (err) {
        console.error('Failed to remove user after 401:', err);
      }

      auth.signinRedirect?.()?.catch((err: unknown) => {
        console.error('Failed to redirect to login after 401:', err);
      });
    };

    setUnauthorizedHandler(handleUnauthorized);
    return () => {
      setUnauthorizedHandler(null);
    };
  }, [auth]);

  // Listen to token expiration and renewal error events
  useEffect(() => {
    if (!auth.events) return;

    const onTokenExpired = () => {
      console.warn('OIDC access token expired. Triggering renewal or redirect.');
      if (auth.signinSilent) {
        auth.signinSilent().catch(() => {
          auth.removeUser?.().finally(() => {
            auth.signinRedirect?.();
          });
        });
      } else {
        auth.removeUser?.().finally(() => {
          auth.signinRedirect?.();
        });
      }
    };

    const removeTokenExpired = auth.events.addAccessTokenExpired?.(onTokenExpired);
    const removeRenewError = auth.events.addSilentRenewError?.(onTokenExpired);

    return () => {
      removeTokenExpired?.();
      removeRenewError?.();
    };
  }, [auth]);

  // Verify stored token against server on initial mount / user load
  useEffect(() => {
    if (!auth.isAuthenticated || !auth.user || isCallback) {
      return;
    }

    let isMounted = true;
    api.verifyAuth?.().catch((err: unknown) => {
      if (isMounted) {
        console.warn('Initial session verification failed with server:', err);
        // apiFetch already triggers unauthorizedHandler if status was 401
      }
    });

    return () => {
      isMounted = false;
    };
  }, [auth.isAuthenticated, auth.user, isCallback]);

  useEffect(() => {
    // If not authenticated, not loading, no error, and not handling callback, automatically redirect to OIDC provider
    if (!auth.isAuthenticated && !auth.isLoading && !auth.error && !auth.activeNavigator && !hasAuthParams() && !isCallback) {
      auth.signinRedirect?.()?.catch((err: unknown) => {
        console.error('Failed to initiate OIDC login redirect:', err);
      });
    }
  }, [auth.isAuthenticated, auth.isLoading, auth.activeNavigator, isCallback, auth]);

  if (auth.error) {
    return (
      <div className="min-h-screen bg-slate-950 flex flex-col items-center justify-center text-slate-300 font-sans p-6">
        <div className="bg-rose-950/40 border border-rose-500/40 rounded-2xl p-8 max-w-md w-full text-center shadow-2xl">
          <ShieldAlert className="w-10 h-10 text-rose-400 mx-auto mb-3" />
          <h2 className="text-lg font-bold text-white">SSO Authentication Failed</h2>
          <p className="text-xs text-rose-300/80 mt-2 leading-relaxed">{auth.error.message}</p>
          <button
            onClick={() => {
              if (typeof window !== 'undefined' && window.location.search) {
                window.history.replaceState({}, document.title, window.location.pathname.replace('/auth/callback', '') || '/');
              }
              auth.signinRedirect?.();
            }}
            className="mt-5 w-full bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs py-2 rounded-lg transition"
          >
            Retry Login
          </button>
        </div>
      </div>
    );
  }

  if (auth.isLoading || (isCallback && hasAuthParams())) {
    return (
      <div className="min-h-screen bg-slate-950 flex flex-col items-center justify-center text-slate-300 font-sans">
        <RefreshCw className="w-8 h-8 text-sky-400 animate-spin mb-3" />
        <h3 className="text-base font-semibold">Authenticating with AgentMesh SSO...</h3>
        <p className="text-xs text-slate-500 mt-1 font-mono">Verifying OpenID Connect tokens</p>
      </div>
    );
  }

  if (!auth.isAuthenticated && !isCallback) {
    return (
      <div className="min-h-screen bg-slate-950 flex flex-col items-center justify-center text-slate-300 font-sans">
        <RefreshCw className="w-8 h-8 text-sky-400 animate-spin mb-3" />
        <h3 className="text-base font-semibold">Redirecting to Identity Provider...</h3>
      </div>
    );
  }

  return <>{children}</>;
}

const handleSigninCallback = () => {
  if (typeof window !== 'undefined') {
    window.history.replaceState({}, document.title, window.location.pathname.replace('/auth/callback', '') || '/');
  }
};

export function AppAuthProvider({ children }: { children: ReactNode }) {
  return (
    <OidcAuthProvider {...oidcConfig} onSigninCallback={handleSigninCallback}>
      <AuthGate>{children}</AuthGate>
    </OidcAuthProvider>
  );
}

export { useOidcAuth as useAuth };
