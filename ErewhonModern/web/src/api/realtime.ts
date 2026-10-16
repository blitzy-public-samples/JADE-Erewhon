// Server push events of the React app: the hub connection of the logged-on session and the handlers that screens
// register for its events.

import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { getApiConfig } from './client';
import type { EntityChangedMessage, EntityTypeName, RealtimeEventName, RealtimeEvents } from './types';

/** Path of the hub, appended to the `baseUrl` of {@link getApiConfig}. */
export const HUB_PATH = '/hubs/erewhon';

/** Names of the events the hub sends; each is received by every handler registered for it through {@link on}. */
export const REALTIME_EVENTS = [
  'EntityChanged',
  'CartItemDeleted',
  'SaveOutstandingChanges',
  'ClosureTick',
  'ClosureCompleted',
] as const satisfies readonly RealtimeEventName[];

const SESSION_QUERY_KEY = 'session';

type Registration = (payload: unknown) => unknown;

const registrations = new Map<RealtimeEventName, Set<Registration>>();

let connection: HubConnection | null = null;

let connectedSessionId: string | null = null;

let starting: Promise<void> | null = null;

let desiredSessionId: string | null = null;

let generation = 0;

const stopping = new Set<Promise<void>>();

function registrationsOf(event: RealtimeEventName): Set<Registration> {
  const existing = registrations.get(event);
  if (existing !== undefined) {
    return existing;
  }

  const created = new Set<Registration>();
  registrations.set(event, created);
  return created;
}

/**
 * Registers `handler` for `event`, whether or not a connection is open, and returns a function that removes this
 * registration. `handler` may return a promise; its rejection is handled by {@link dispatch} as a thrown error, and any
 * other returned value is ignored. Registering the same handler twice makes two registrations; calling the returned
 * function again does nothing.
 */
export function on<E extends RealtimeEventName>(
  event: E,
  handler: (payload: RealtimeEvents[E]) => unknown,
): () => void {
  const registered = registrationsOf(event);
  const registration: Registration = (payload) => handler(payload as RealtimeEvents[E]);
  registered.add(registration);

  return () => {
    registered.delete(registration);
  };
}

/**
 * Registers `handler` for the EntityChanged events whose `entityType` is one of `entityTypes`, and returns a function
 * that removes this registration. `handler` may return a promise; its rejection is handled as a thrown error, and any
 * other returned value is ignored, as for a handler of {@link on}.
 */
export function onEntityChanged(
  entityTypes: readonly EntityTypeName[],
  handler: (message: EntityChangedMessage) => unknown,
): () => void {
  const accepted: ReadonlySet<EntityTypeName> = new Set(entityTypes);

  return on('EntityChanged', (message) => {
    if (accepted.has(message.entityType)) {
      return handler(message);
    }
  });
}

/** Returns whether `value` is an object or function with a callable `then`. */
function isPromiseLike(value: unknown): value is PromiseLike<unknown> {
  return (
    (typeof value === 'object' || typeof value === 'function') &&
    value !== null &&
    typeof (value as { then?: unknown }).then === 'function'
  );
}

/**
 * Calls `registration` with `payload` and returns its outcome as a promise: rejected with the error it throws, adopting
 * the promise-like value it returns, or resolved for any other return value.
 */
function invokeRegistration(registration: Registration, payload: unknown): Promise<unknown> {
  try {
    const result: unknown = registration(payload);
    return isPromiseLike(result) ? Promise.resolve(result) : Promise.resolve(undefined);
  } catch (error) {
    return Promise.reject(error);
  }
}

/**
 * Calls every handler registered for `event` with `payload`, in order over a copy of the registrations taken when the
 * event arrives, and calls the later handlers when an earlier one throws. The returned promise settles once every
 * handler's outcome has settled: it resolves to `undefined` when no handler threw or returned a promise that rejected,
 * and otherwise rejects with the one error, or an AggregateError holding the errors in call order. The listener of each
 * event on a connection returns this promise to the SignalR client, which logs a rejection at Error level.
 */
async function dispatch(event: RealtimeEventName, payload: unknown): Promise<void> {
  const registered = registrations.get(event);
  if (registered === undefined) {
    return;
  }

  const outcomes: Promise<unknown>[] = [];
  for (const registration of [...registered]) {
    outcomes.push(invokeRegistration(registration, payload));
  }

  const settled = await Promise.allSettled(outcomes);
  const errors = settled.flatMap((outcome) => (outcome.status === 'rejected' ? [outcome.reason as unknown] : []));

  if (errors.length > 0) {
    throw combinedError(errors, `${errors.length} handlers of ${event} failed.`);
  }
}

/** Returns the one error of `errors`, or an AggregateError holding all of them in order with `message`. */
function combinedError(errors: readonly unknown[], message: string): unknown {
  return errors.length === 1 ? errors[0] : new AggregateError(errors, message);
}

/** Removes the event listeners from `target`, stops it and records the stop as in flight until it settles. */
function stopConnection(target: HubConnection): void {
  for (const name of REALTIME_EVENTS) {
    target.off(name);
  }

  const stopped = target.stop();
  stopping.add(stopped);
  stopped.then(
    () => stopping.delete(stopped),
    () => stopping.delete(stopped),
  );
}

/** Clears the current connection, its session id and its pending start, and stops the connection when one existed. */
function releaseConnection(): void {
  const current = connection;
  connection = null;
  connectedSessionId = null;
  starting = null;

  if (current !== null) {
    stopConnection(current);
  }
}

/**
 * Resolves once every stop in flight when it is called has settled. When stops rejected, it then rejects with the one
 * error, or an AggregateError of the errors in the order the stops began.
 */
async function stopsSettled(): Promise<void> {
  const outcomes = await Promise.allSettled([...stopping]);
  const errors = outcomes.flatMap((outcome) => (outcome.status === 'rejected' ? [outcome.reason as unknown] : []));

  if (errors.length > 0) {
    throw combinedError(errors, `${errors.length} hub connections failed to stop.`);
  }
}

/**
 * Starts `target` for the call holding the generation `owner`. When the start rejects, the generation is checked as
 * the rejection is handled: while `owner` is the current generation, the connection and its session id are cleared if
 * `target` is still the current connection, and the error is rethrown; after `owner` was superseded, the promise
 * resolves and the module state is left as it is.
 */
async function startOwnedConnection(target: HubConnection, owner: number): Promise<void> {
  try {
    await target.start();
  } catch (error) {
    if (owner !== generation) {
      return;
    }

    if (connection === target) {
      connection = null;
      connectedSessionId = null;
    }

    throw error;
  }
}

/**
 * Starts `target` through {@link startOwnedConnection} and records the returned promise as the pending start until it
 * settles.
 */
function startConnection(target: HubConnection, owner: number): Promise<void> {
  const pending = startOwnedConnection(target, owner).finally(() => {
    if (starting === pending) {
      starting = null;
    }
  });
  starting = pending;

  return pending;
}

function buildConnection(sessionId: string): HubConnection {
  const url = `${getApiConfig().baseUrl}${HUB_PATH}?${SESSION_QUERY_KEY}=${encodeURIComponent(sessionId)}`;
  const built = new HubConnectionBuilder()
    .withUrl(url)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  for (const name of REALTIME_EVENTS) {
    built.on(name, (payload: unknown) => dispatch(name, payload));
  }

  return built;
}

/**
 * Opens the hub connection of `sessionId` at the `baseUrl` of {@link getApiConfig} followed by {@link HUB_PATH}, with
 * the session id in the `session` query value, and resolves once it is connected.
 * - A call for a session other than the one requested since the last {@link disconnectRealtime}, or the first call
 *   after it, begins a new generation, which supersedes every earlier pending call; further calls for the same session
 *   share that generation.
 * - With a connection of the same session: resolves with its pending start, resolves at once while it is connected,
 *   connecting or reconnecting, and starts it again when it is disconnected or, after its stop completes, when it is
 *   disconnecting.
 * - With a connection of another session: that connection's listeners are removed and it is stopped.
 * - Without a connection: every stop in flight is awaited before the connection is created and started.
 * - The call checks its generation in the same continuation that ends each wait, and again before each step that
 *   creates, starts, restarts or stops a connection, with no wait between the check and the step; a superseded call
 *   resolves without taking the step. A start or an awaited stop that rejects after its call was superseded resolves,
 *   and leaves the current connection as it is.
 * A start that rejects while its call holds the current generation clears the connection and rejects with the start's
 * error; a stop it awaited that rejected makes it reject with that error.
 */
export async function connectRealtime(sessionId: string): Promise<void> {
  if (desiredSessionId !== sessionId) {
    desiredSessionId = sessionId;
    generation += 1;
  }

  const owner = generation;

  for (;;) {
    if (owner !== generation) {
      return;
    }

    const current = connection;

    if (current === null) {
      if (stopping.size > 0) {
        try {
          await stopsSettled();
        } catch (error) {
          if (owner !== generation) {
            return;
          }

          throw error;
        }

        continue;
      }

      const created = buildConnection(sessionId);
      connection = created;
      connectedSessionId = sessionId;

      return startConnection(created, owner);
    }

    if (connectedSessionId !== sessionId) {
      releaseConnection();
      continue;
    }

    if (starting !== null) {
      return starting;
    }

    switch (current.state) {
      case HubConnectionState.Connected:
      case HubConnectionState.Connecting:
      case HubConnectionState.Reconnecting:
        return;
      case HubConnectionState.Disconnecting:
        try {
          await current.stop();
        } catch (error) {
          if (owner !== generation) {
            return;
          }

          throw error;
        }

        continue;
      case HubConnectionState.Disconnected:
        return startConnection(current, owner);
    }
  }
}

/**
 * Begins a new generation, which supersedes every pending {@link connectRealtime} call, and closes the current hub
 * connection when one exists: the module state is cleared, the connection's listeners are removed, so it delivers no
 * further events to the registered handlers, and it is stopped. The returned promise resolves once every stop in
 * flight when it is called, its own included, has settled; when stops rejected, it then rejects with the one error, or
 * an AggregateError of their errors. Registered handlers are kept.
 */
export async function disconnectRealtime(): Promise<void> {
  desiredSessionId = null;
  generation += 1;
  releaseConnection();

  await stopsSettled();
}
