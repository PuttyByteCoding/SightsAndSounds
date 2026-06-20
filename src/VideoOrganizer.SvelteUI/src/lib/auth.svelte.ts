// Auth store (#124, Phase 3 — Approach A: SPA public client + Bearer).
//
// On startup it asks the API whether auth is on (GET /api/auth/config, public).
// If off, the app runs exactly as before (no login, no token). If on, it drives
// Authorization Code + PKCE against Keycloak via oidc-client-ts, exposes the
// signed-in user + role, and hands api.ts a fresh access token to send as a
// Bearer. The viewer role is read-only; admin is full control (the API enforces
// it — see AuthRules — this just shapes the UI and carries the token).
import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';
import { rolesFromAccessToken } from './jwt';

type Status = 'loading' | 'disabled' | 'anonymous' | 'authenticated';

interface AuthConfig {
  enabled: boolean;
  authority: string;
  clientId: string;
  audience: string;
}

const RETURN_TO_KEY = 'auth.returnTo';

function _Auth() {
  let status = $state<Status>('loading');
  let username = $state<string | null>(null);
  let roles = $state<string[]>([]);
  let accessToken: string | null = null; // not reactive — read on demand by api.ts
  let manager: UserManager | null = null;

  const isAuthenticated = $derived(status === 'authenticated');
  const isAdmin = $derived(roles.includes('admin'));
  // Read-only when auth is on and the user lacks the admin role.
  const isReadOnly = $derived(status === 'authenticated' && !roles.includes('admin'));

  function rolesFromUser(u: User): string[] {
    return rolesFromAccessToken(u.access_token);
  }

  function applyUser(u: User | null) {
    if (u && !u.expired) {
      status = 'authenticated';
      username = (u.profile.preferred_username as string) ?? u.profile.name ?? 'user';
      roles = rolesFromUser(u);
      accessToken = u.access_token;
    } else {
      status = 'anonymous';
      username = null;
      roles = [];
      accessToken = null;
    }
  }

  // Mirror the current access token into an HttpOnly cookie (#124). Browser
  // media elements (<video>/<img>/<track>/CSS url()) can't send a Bearer
  // header, so the API reads the token from this cookie for same-origin media
  // GETs. Re-run whenever the user (re)loads — including silent renews — so the
  // cookie tracks token refreshes. Non-fatal: media just 401s until next sync.
  async function syncSession() {
    if (!accessToken) return;
    try {
      await fetch('/api/auth/session', {
        method: 'POST',
        headers: { Authorization: `Bearer ${accessToken}` },
      });
    } catch { /* non-fatal */ }
  }

  // Clear the media cookie on sign-out so a shared browser can't keep streaming.
  async function clearSession() {
    try {
      await fetch('/api/auth/session', {
        method: 'DELETE',
        headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
      });
    } catch { /* non-fatal */ }
  }

  // Called once on app start (browser only). Idempotent — repeat calls return
  // the same in-flight promise so getAccessToken can await readiness.
  let initPromise: Promise<void> | null = null;
  function init(): Promise<void> {
    initPromise ??= initInner();
    return initPromise;
  }

  async function initInner() {
    let config: AuthConfig;
    try {
      const res = await fetch('/api/auth/config');
      config = await res.json();
    } catch {
      // API unreachable — treat as no-auth so the app still renders an error
      // surface rather than a blank login wall.
      status = 'disabled';
      return;
    }
    if (!config.enabled) { status = 'disabled'; return; }

    manager = new UserManager({
      authority: config.authority,
      client_id: config.clientId,
      redirect_uri: `${location.origin}/auth/callback`,
      post_logout_redirect_uri: location.origin,
      response_type: 'code',
      scope: 'openid profile email',
      userStore: new WebStorageStateStore({ store: window.sessionStorage }),
      // Renew the token in the background before it expires so long video
      // playback (and the media cookie that rides on it) doesn't lapse (#124).
      automaticSilentRenew: true,
    });
    // On (re)load and on each silent renew, refresh the media cookie too.
    manager.events.addUserLoaded((u) => { applyUser(u); void syncSession(); });
    manager.events.addUserUnloaded(() => applyUser(null));

    applyUser(await manager.getUser());
    if (status === 'authenticated') await syncSession();
  }

  async function login(returnTo?: string) {
    if (!manager) return;
    try { sessionStorage.setItem(RETURN_TO_KEY, returnTo ?? location.pathname + location.search); }
    catch { /* private mode */ }
    await manager.signinRedirect();
  }

  // Completes the redirect on /auth/callback. Returns where to go next.
  async function completeLogin(): Promise<string> {
    if (!manager) await init();
    const u = await manager!.signinRedirectCallback();
    applyUser(u);
    // Set the media cookie before we navigate into the app so the first video
    // the user opens can stream immediately (#124).
    await syncSession();
    let returnTo = '/';
    try { returnTo = sessionStorage.getItem(RETURN_TO_KEY) || '/'; sessionStorage.removeItem(RETURN_TO_KEY); }
    catch { /* ignore */ }
    return returnTo.startsWith('/') ? returnTo : '/';
  }

  async function logout() {
    if (!manager) return;
    await clearSession();
    try { await manager.signoutRedirect(); }
    catch { await manager.removeUser(); applyUser(null); }
  }

  // Fresh Bearer for api.ts. Refreshes silently (refresh-token grant) when the
  // cached token has expired; returns null when auth is off or sign-in is gone.
  async function getAccessToken(): Promise<string | null> {
    // Wait for init so requests fired during startup don't go out token-less.
    if (initPromise) await initPromise;
    if (status === 'disabled' || !manager) return null;
    let u = await manager.getUser();
    if (u && !u.expired) { accessToken = u.access_token; return accessToken; }
    try {
      u = await manager.signinSilent();
      applyUser(u);
      return u?.access_token ?? null;
    } catch {
      applyUser(null);
      return null;
    }
  }

  return {
    get status() { return status; },
    get username() { return username; },
    get roles() { return roles; },
    get isAuthenticated() { return isAuthenticated; },
    get isAdmin() { return isAdmin; },
    get isReadOnly() { return isReadOnly; },
    init,
    login,
    completeLogin,
    logout,
    getAccessToken,
  };
}

export const auth = _Auth();
