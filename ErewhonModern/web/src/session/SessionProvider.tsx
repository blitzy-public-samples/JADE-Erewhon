// Session identity of the React app: the logged-on session held in memory and in session storage, the logon, logout
// and clear actions that change it, and the hub connection opened for it.

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, configureApiClient, UnauthorizedError } from '../api/client';
import { connectRealtime, disconnectRealtime } from '../api/realtime';
import type { CreateSessionRequest, CreateSessionResponse, LogonArea, SessionRole } from '../api/types';

/** Session of the logged-on user. */
export interface SessionState {
  /** Session id returned by POST /api/app/sessions. */
  sessionId: string;
  /** Area the user logged on to. */
  area: LogonArea;
  /** Role of the user. */
  role: SessionRole;
  /** Internal id of the session's agent; null when the user is not an agent. */
  agentId: number | null;
  /** Internal id of the session's client; null when the user is not a client. */
  clientId: number | null;
  /** Logon user code: the client name, the agent name, or the company name for the administrator. */
  userName: string | null;
}

/** Value returned by {@link useSession}. */
export interface SessionContextValue {
  /** Current session; null when no user is logged on. */
  session: SessionState | null;
  /**
   * Sends `request` to POST /api/app/sessions and resolves to the response unchanged. A response with `succeeded` and
   * a non-empty `sessionId` replaces the current session; any other response leaves it as it is. A rejected request
   * rejects unchanged and leaves the session as it is.
   */
  logon: (request: CreateSessionRequest) => Promise<CreateSessionResponse>;
  /**
   * Ends the current session through DELETE /api/app/sessions/current and then clears it. An HTTP 401 resolves; any
   * other error rejects after the session is cleared. With no current session, clears it without a request.
   */
  logout: () => Promise<void>;
  /** Clears the current session without a request. */
  clearSession: () => void;
}

/** Session storage key under which the current {@link SessionState} is stored as JSON. */
export const SESSION_STORAGE_KEY = 'erewhon.session';

const LOGON_AREAS: readonly LogonArea[] = ['shop', 'admin'];

const SESSION_ROLES: readonly SessionRole[] = ['none', 'client', 'agent', 'administrator'];

const SessionContext = createContext<SessionContextValue | undefined>(undefined);

interface SessionProviderProps {
  children: ReactNode;
  /** Session to start with; null starts with no session, and undefined restores the session from session storage. */
  initialSession?: SessionState | null;
}

function isLogonArea(value: unknown): value is LogonArea {
  return LOGON_AREAS.some((area) => area === value);
}

function isSessionRole(value: unknown): value is SessionRole {
  return SESSION_ROLES.some((role) => role === value);
}

function isIdOrNull(value: unknown): value is number | null {
  return value === null || typeof value === 'number';
}

function isStringOrNull(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

/** Returns the six {@link SessionState} fields of `value`, or null when any of them is missing or invalid. */
function toSessionState(value: unknown): SessionState | null {
  if (
    typeof value === 'object' &&
    value !== null &&
    'sessionId' in value &&
    typeof value.sessionId === 'string' &&
    value.sessionId !== '' &&
    'area' in value &&
    isLogonArea(value.area) &&
    'role' in value &&
    isSessionRole(value.role) &&
    'agentId' in value &&
    isIdOrNull(value.agentId) &&
    'clientId' in value &&
    isIdOrNull(value.clientId) &&
    'userName' in value &&
    isStringOrNull(value.userName)
  ) {
    return {
      sessionId: value.sessionId,
      area: value.area,
      role: value.role,
      agentId: value.agentId,
      clientId: value.clientId,
      userName: value.userName,
    };
  }

  return null;
}

/** Removes the stored session; does nothing when session storage is unavailable. */
function removeStoredSession(): void {
  try {
    sessionStorage.removeItem(SESSION_STORAGE_KEY);
  } catch {
    // Session storage is unavailable; nothing is removed.
  }
}

/** Returns the {@link SessionState} held in the JSON `text`, or null when `text` is not JSON or not a valid session. */
function parseStoredSession(text: string): SessionState | null {
  try {
    return toSessionState(JSON.parse(text));
  } catch {
    return null;
  }
}

/**
 * Returns the stored session, or null when none is stored or session storage is unavailable. A stored value that is
 * not a valid {@link SessionState} is removed.
 */
function readStoredSession(): SessionState | null {
  let text: string | null;
  try {
    text = sessionStorage.getItem(SESSION_STORAGE_KEY);
  } catch {
    return null;
  }

  if (text === null) {
    return null;
  }

  const stored = parseStoredSession(text);
  if (stored === null) {
    removeStoredSession();
  }

  return stored;
}

/** Stores the six fields of `session` as JSON, or removes the stored session when `session` is null. */
function writeStoredSession(session: SessionState | null): void {
  if (session === null) {
    removeStoredSession();
    return;
  }

  const stored: SessionState = {
    sessionId: session.sessionId,
    area: session.area,
    role: session.role,
    agentId: session.agentId,
    clientId: session.clientId,
    userName: session.userName,
  };

  try {
    sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(stored));
  } catch {
    // Session storage is unavailable or full; the session is then held in memory only.
  }
}

/** Settles a rejected hub start or stop, leaving the session unchanged. */
function ignoreRealtimeFailure(): undefined {
  return undefined;
}

/**
 * Holds the current session for its children, read through {@link useSession}.
 * - The first render starts with `initialSession` when it is given, null included, and otherwise with the session
 *   restored from session storage, and configures the API client with that session id and the 401 callback before
 *   any child renders.
 * - Every change of the session configures the API client's session id first, then stores the session in session
 *   storage, then re-renders.
 * - An HTTP 401 from any request through the API client clears the session.
 * - While a session exists, the hub connection of its id is open; with no session it is closed, and it is closed when
 *   the provider unmounts.
 */
export function SessionProvider({ children, initialSession }: SessionProviderProps) {
  const commitRef = useRef<((next: SessionState | null) => void) | null>(null);

  const onUnauthorized = useCallback((): void => {
    commitRef.current?.(null);
  }, []);

  const [session, setSession] = useState<SessionState | null>(() => {
    const initial = initialSession !== undefined ? initialSession : readStoredSession();
    configureApiClient({ sessionId: initial?.sessionId ?? null, onUnauthorized });
    return initial;
  });

  const currentSessionIdRef = useRef<string | null>(session?.sessionId ?? null);

  const commit = useCallback(
    (next: SessionState | null): void => {
      configureApiClient({ sessionId: next?.sessionId ?? null, onUnauthorized });
      writeStoredSession(next);
      currentSessionIdRef.current = next?.sessionId ?? null;
      setSession(next);
    },
    [onUnauthorized],
  );

  useEffect(() => {
    commitRef.current = commit;
  }, [commit]);

  useEffect(() => {
    configureApiClient({ sessionId: session?.sessionId ?? null, onUnauthorized });
    writeStoredSession(session);
  }, [session, onUnauthorized]);

  const sessionId = session?.sessionId ?? null;

  useEffect(() => {
    if (sessionId === null) {
      disconnectRealtime().catch(ignoreRealtimeFailure);
    } else {
      connectRealtime(sessionId).catch(ignoreRealtimeFailure);
    }
  }, [sessionId]);

  useEffect(
    () => () => {
      disconnectRealtime().catch(ignoreRealtimeFailure);
    },
    [],
  );

  const logon = useCallback(
    async (request: CreateSessionRequest): Promise<CreateSessionResponse> => {
      const response = await api.post<CreateSessionResponse>('/api/app/sessions', request);

      if (response.succeeded && typeof response.sessionId === 'string' && response.sessionId !== '') {
        commit({
          sessionId: response.sessionId,
          area: response.area,
          role: response.role,
          agentId: response.agentId ?? null,
          clientId: response.clientId ?? null,
          userName: response.userName ?? null,
        });
      }

      return response;
    },
    [commit],
  );

  const logout = useCallback(async (): Promise<void> => {
    if (currentSessionIdRef.current === null) {
      commit(null);
      return;
    }

    try {
      await api.del('/api/app/sessions/current');
    } catch (error) {
      if (!(error instanceof UnauthorizedError)) {
        throw error;
      }
    } finally {
      commit(null);
    }
  }, [commit]);

  const clearSession = useCallback((): void => {
    commit(null);
  }, [commit]);

  const value = useMemo<SessionContextValue>(
    () => ({ session, logon, logout, clearSession }),
    [session, logon, logout, clearSession],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

/**
 * Returns the current session and its actions from the nearest {@link SessionProvider}.
 * @throws Error when no {@link SessionProvider} encloses the calling component.
 */
export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (value === undefined) {
    throw new Error('useSession must be used within a SessionProvider');
  }

  return value;
}

/** Returns true when `session` is the administrator's, the company user with no agent and no client. */
export function isCompanyUser(session: SessionState | null): boolean {
  return session?.role === 'administrator';
}

/** Returns true when `session` is an agent's. */
export function isAgent(session: SessionState | null): boolean {
  return session?.role === 'agent';
}
