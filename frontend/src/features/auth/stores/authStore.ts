import { create } from 'zustand';
import { DesktopBridge } from '../../../shared/services/DesktopBridge';

export type UserRole = 'Requestor' | 'Provider' | 'Admin';

export interface AuthUser {
  id: string;
  email?: string;
  userName?: string;
  firstName?: string;
  lastName?: string;
  role: UserRole;
}

export interface AuthState {
  user: AuthUser | null;
  token: string | null;
  setAuth: (user: AuthUser, token?: string | null) => void;
  clearAuth: () => void;
}

const STORAGE_KEYS = {
  token: 'infinitegpu.auth.token',
  user: 'infinitegpu.auth.user'
} as const;

const FALLBACK_STATE: Pick<AuthState, 'user' | 'token'> = {
  user: null,
  token: null
};

const isBrowserEnvironment =
  typeof window !== 'undefined' && typeof window.localStorage !== 'undefined';

const readStoredAuth = (): Pick<AuthState, 'user' | 'token'> => {
  if (!isBrowserEnvironment) {
    return { ...FALLBACK_STATE };
  }

  try {
    const token = window.localStorage.getItem(STORAGE_KEYS.token);
    const rawUser = window.localStorage.getItem(STORAGE_KEYS.user);
    const user = rawUser ? (JSON.parse(rawUser) as AuthUser) : null;

    return {
      user,
      token
    };
  } catch {
    return { ...FALLBACK_STATE };
  }
};

const DESKTOP_SYNC_MAX_ATTEMPTS = 8;
const DESKTOP_SYNC_DELAY_MS = 250;

const pushTokenToDesktop = (token: string | null, attempt = 0): void => {
  if (!isBrowserEnvironment) {
    return;
  }

  if (!DesktopBridge.isAvailable()) {
    if (attempt >= DESKTOP_SYNC_MAX_ATTEMPTS) {
      return;
    }

    window.setTimeout(() => pushTokenToDesktop(token, attempt + 1), DESKTOP_SYNC_DELAY_MS);
    return;
  }

  try {
    void DesktopBridge.invoke('auth:setToken', { token }).catch(() => {});
  } catch {
    // Ignore bridge invocation issues; desktop integration is optional.
  }
};

const persistAuth = (user: AuthUser | null, token: string | null) => {
  if (!isBrowserEnvironment) {
    return;
  }

  try {
    if (token) {
      window.localStorage.setItem(STORAGE_KEYS.token, token);
    } else {
      window.localStorage.removeItem(STORAGE_KEYS.token);
    }

    if (user) {
      window.localStorage.setItem(STORAGE_KEYS.user, JSON.stringify(user));
    } else {
      window.localStorage.removeItem(STORAGE_KEYS.user);
    }
    pushTokenToDesktop(token);
  } catch {
    // Intentionally swallow storage errors (e.g. quota, privacy mode)
  }
};

const clearPersistedAuth = () => {
  if (!isBrowserEnvironment) {
    return;
  }

  try {
    window.localStorage.removeItem(STORAGE_KEYS.token);
    window.localStorage.removeItem(STORAGE_KEYS.user);
  } catch {
    // Ignore storage clearing issues silently
  }
};

const initialState = readStoredAuth();
pushTokenToDesktop(initialState.token);

export const useAuthStore = create<AuthState>((set) => ({
  ...initialState,
  setAuth: (user, token = null) =>
    set(() => {
      const resolvedToken = token ?? null;
      persistAuth(user, resolvedToken);
      return { user, token: resolvedToken };
    }),
  clearAuth: () =>
    set(() => {
      clearPersistedAuth();
      return { ...FALLBACK_STATE };
    })
}));

export const getAuthUser = () => useAuthStore.getState().user;
export const getAuthToken = () => useAuthStore.getState().token;
export const isProviderUser = () => getAuthUser()?.role === 'Provider';