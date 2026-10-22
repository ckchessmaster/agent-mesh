import type { UserManagerSettings } from 'oidc-client-ts';
import { WebStorageStateStore } from 'oidc-client-ts';

const origin = typeof window !== 'undefined' ? window.location.origin : 'http://localhost:3000';
const configuredAuthority = import.meta.env.VITE_OIDC_AUTHORITY;
const isLocalDex = !configuredAuthority || configuredAuthority.includes('localhost:8085');

export const oidcConfig: UserManagerSettings = {
  authority: configuredAuthority || 'http://localhost:8085/dex',
  client_id: import.meta.env.VITE_OIDC_CLIENT_ID || 'agentmesh-dashboard',
  redirect_uri: `${origin}/auth/callback`,
  post_logout_redirect_uri: origin,
  response_type: 'code',
  scope: 'openid profile email offline_access',
  userStore: typeof window !== 'undefined' ? new WebStorageStateStore({ store: window.localStorage }) : undefined,
  automaticSilentRenew: true,
  loadUserInfo: true,
  // When running with local Dex, route OIDC endpoints through Vite's /dex proxy on the current origin (e.g. LAN IP)
  // while preserving the expected Dex issuer ('http://localhost:8085/dex') for token signature and API validation.
  ...(isLocalDex
    ? {
        metadata: {
          issuer: 'http://localhost:8085/dex',
          authorization_endpoint: `${origin}/dex/auth`,
          token_endpoint: `${origin}/dex/token`,
          jwks_uri: `${origin}/dex/keys`,
          userinfo_endpoint: `${origin}/dex/userinfo`
        }
      }
    : {})
};
