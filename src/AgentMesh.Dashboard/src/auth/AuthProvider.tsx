import { useEffect, type ReactNode } from 'react';
import { AuthProvider as OidcAuthProvider, useAuth as useOidcAuth, hasAuthParams } from 'react-oidc-context';
import { oidcConfig } from './authConfig';
import { setAuthTokenProvider } from '../services/api';
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
