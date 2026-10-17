import type { UserManagerSettings } from 'oidc-client-ts';
import { WebStorageStateStore } from 'oidc-client-ts';

export const oidcConfig: UserManagerSettings = {
  authority: import.meta.env.VITE_OIDC_AUTHORITY || 'http://localhost:8085/dex',
  client_id: import.meta.env.VITE_OIDC_CLIENT_ID || 'agentmesh-dashboard',
  redirect_uri: `${typeof window !== 'undefined' ? window.location.origin : 'http://localhost:3000'}/auth/callback`,
  post_logout_redirect_uri: typeof window !== 'undefined' ? window.location.origin : 'http://localhost:3000',
  response_type: 'code',
  scope: 'openid profile email',
  userStore: typeof window !== 'undefined' ? new WebStorageStateStore({ store: window.localStorage }) : undefined,
  automaticSilentRenew: true,
  loadUserInfo: true
};
