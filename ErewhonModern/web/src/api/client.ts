// HTTP access to Erewhon.Api for the React app: the shared request configuration, the session header, the request
// methods and the errors they throw for responses other than 2xx.

import type { ErrorOutcome, ErrorOutcomeKind } from './types';

/** Settings applied to every request sent through {@link api}. */
export interface ApiConfig {
  /** Prefix of every request path, without a trailing `/`; `''` sends requests to the page's own origin. */
  baseUrl: string;
  /** Session id sent in the {@link SESSION_HEADER} header; null sends no session header. */
  sessionId: string | null;
  /** Called on every HTTP 401 response before {@link UnauthorizedError} is thrown; null calls nothing. */
  onUnauthorized: (() => void) | null;
}

/** Name of the request header that carries the session id. */
export const SESSION_HEADER = 'X-Erewhon-Session';

const JSON_CONTENT_TYPE = 'application/json';

const ERROR_OUTCOME_KINDS: readonly ErrorOutcomeKind[] = ['exception', 'lock', 'deadlock'];

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

type ResponseMode = 'json' | 'blob';

let config: ApiConfig = { baseUrl: '', sessionId: null, onUnauthorized: null };

/**
 * Merges `patch` into the current configuration. A key holding a value replaces the current value, a key holding
 * null clears it, and an absent or undefined key leaves the current value unchanged.
 */
export function configureApiClient(patch: Partial<ApiConfig>): void {
  config = {
    baseUrl: patch.baseUrl === undefined ? config.baseUrl : patch.baseUrl,
    sessionId: patch.sessionId === undefined ? config.sessionId : patch.sessionId,
    onUnauthorized: patch.onUnauthorized === undefined ? config.onUnauthorized : patch.onUnauthorized,
  };
}

/** Returns the current configuration. */
export function getApiConfig(): Readonly<ApiConfig> {
  return config;
}

/** Thrown for a response status other than 2xx, 401 and a 500 carrying an {@link ErrorOutcome}. */
export class ApiError extends Error {
  /** HTTP status of the response. */
  readonly status: number;
  /** Response body parsed as JSON, else the body text, else undefined when the body is empty. */
  readonly body: unknown;

  constructor(status: number, body: unknown, method: string, path: string) {
    super(`HTTP ${status} for ${method} ${path}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

/** Thrown for an HTTP 401 response, after {@link ApiConfig.onUnauthorized} has been called. */
export class UnauthorizedError extends Error {
  /** HTTP status of the response. */
  readonly status = 401;

  constructor() {
    super('HTTP 401');
    this.name = 'UnauthorizedError';
  }
}

/** Thrown for an HTTP 500 response whose body is an {@link ErrorOutcome}; the message is the outcome body. */
export class ErrorOutcomeError extends Error {
  /** HTTP status of the response. */
  readonly status = 500;
  /** Kind, heading and body of the message to show. */
  readonly outcome: ErrorOutcome;

  constructor(outcome: ErrorOutcome) {
    super(outcome.body);
    this.name = 'ErrorOutcomeError';
    this.outcome = outcome;
  }
}

function isErrorOutcomeKind(value: unknown): value is ErrorOutcomeKind {
  return ERROR_OUTCOME_KINDS.some((kind) => kind === value);
}

function isErrorOutcome(value: unknown): value is ErrorOutcome {
  return (
    typeof value === 'object' &&
    value !== null &&
    'kind' in value &&
    isErrorOutcomeKind(value.kind) &&
    'heading' in value &&
    typeof value.heading === 'string' &&
    'body' in value &&
    typeof value.body === 'string'
  );
}

function parseErrorBody(text: string): unknown {
  if (text === '') {
    return undefined;
  }

  try {
    const parsed: unknown = JSON.parse(text);
    return parsed;
  } catch {
    return text;
  }
}

function send(method: HttpMethod, path: string, body: unknown, mode: 'json'): Promise<unknown>;
function send(method: HttpMethod, path: string, body: unknown, mode: 'blob'): Promise<Blob>;
async function send(method: HttpMethod, path: string, body: unknown, mode: ResponseMode): Promise<unknown> {
  const { baseUrl, sessionId } = getApiConfig();
  const headers: Record<string, string> = { Accept: JSON_CONTENT_TYPE };
  const init: RequestInit = { method, headers };

  if (body !== undefined) {
    headers['Content-Type'] = JSON_CONTENT_TYPE;
    init.body = JSON.stringify(body);
  }

  if (typeof sessionId === 'string' && sessionId !== '') {
    headers[SESSION_HEADER] = sessionId;
  }

  const response = await fetch(`${baseUrl}${path}`, init);

  if (response.status === 401) {
    getApiConfig().onUnauthorized?.();
    throw new UnauthorizedError();
  }

  if (response.ok) {
    if (mode === 'blob') {
      return response.blob();
    }

    if (response.status === 204) {
      return undefined;
    }

    const text = await response.text();
    if (text === '') {
      return undefined;
    }

    const parsed: unknown = JSON.parse(text);
    return parsed;
  }

  const errorBody = parseErrorBody(await response.text());

  if (response.status === 500 && isErrorOutcome(errorBody)) {
    throw new ErrorOutcomeError({ kind: errorBody.kind, heading: errorBody.heading, body: errorBody.body });
  }

  throw new ApiError(response.status, errorBody, method, path);
}

/**
 * Request methods for `/api/app/**`. Each takes an absolute path that already holds its encoded query string, sends
 * it to {@link ApiConfig.baseUrl} with `Accept: application/json` and, when the configuration holds a session id,
 * the {@link SESSION_HEADER} header. A body argument other than undefined is sent as JSON with
 * `Content-Type: application/json`. A rejected `fetch` rejects unchanged. On HTTP 401 the method calls
 * {@link ApiConfig.onUnauthorized} and throws {@link UnauthorizedError}; on HTTP 500 with an {@link ErrorOutcome}
 * body it throws {@link ErrorOutcomeError}; on every other status outside 2xx it throws {@link ApiError}.
 */
export const api = {
  /** Sends GET and resolves to the parsed JSON body, or undefined for HTTP 204 or an empty body. */
  async get<T>(path: string): Promise<T> {
    return (await send('GET', path, undefined, 'json')) as T;
  },

  /** Sends POST with `body` as JSON and resolves to the parsed JSON body, or undefined for HTTP 204 or an empty body. */
  async post<T = void>(path: string, body?: unknown): Promise<T> {
    return (await send('POST', path, body, 'json')) as T;
  },

  /** Sends PUT with `body` as JSON and resolves to the parsed JSON body, or undefined for HTTP 204 or an empty body. */
  async put<T = void>(path: string, body?: unknown): Promise<T> {
    return (await send('PUT', path, body, 'json')) as T;
  },

  /** Sends DELETE and resolves to the parsed JSON body, or undefined for HTTP 204 or an empty body. */
  async del<T = void>(path: string): Promise<T> {
    return (await send('DELETE', path, undefined, 'json')) as T;
  },

  /** Sends GET and resolves to the response body as a Blob. */
  async blob(path: string): Promise<Blob> {
    return send('GET', path, undefined, 'blob');
  },
};
