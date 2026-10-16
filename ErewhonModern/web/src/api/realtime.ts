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

type Registration = (payload: unknown) => void;

const registrations = new Map<RealtimeEventName, Set<Registration>>();

let connection: HubConnection | null = null;

let connectedSessionId: string | null = null;

let starting: Promise<void> | null = null;

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
 * registration. Registering the same handler twice makes two registrations; calling the returned function again does
 * nothing.
 */
export function on<E extends RealtimeEventName>(event: E, handler: (payload: RealtimeEvents[E]) => void): () => void {
  const registered = registrationsOf(event);
  const registration: Registration = (payload) => handler(payload as RealtimeEvents[E]);
  registered.add(registration);

  return () => {
    registered.delete(registration);
  };
}

/**
 * Registers `handler` for the EntityChanged events whose `entityType` is one of `entityTypes`, and returns a function
 * that removes this registration.
 */
export function onEntityChanged(
  entityTypes: readonly EntityTypeName[],
  handler: (message: EntityChangedMessage) => void,
): () => void {
  const accepted: ReadonlySet<EntityTypeName> = new Set(entityTypes);

  return on('EntityChanged', (message) => {
    if (accepted.has(message.entityType)) {
      handler(message);
    }
  });
}

/**
 * Calls every handler registered for `event` with `payload`, over a copy of the registrations taken when the event
 * arrives. After every handler has run, the error thrown by a handler is rethrown; when several handlers threw, an
 * AggregateError holding their errors in call order is thrown.
 */
function dispatch(event: RealtimeEventName, payload: unknown): void {
  const registered = registrations.get(event);
  if (registered === undefined) {
    return;
  }

  const errors: unknown[] = [];
  for (const registration of [...registered]) {
    try {
      registration(payload);
    } catch (error) {
      errors.push(error);
    }
  }

  if (errors.length === 1) {
    throw errors[0];
  }

  if (errors.length > 1) {
    throw new AggregateError(errors, `${errors.length} handlers of ${event} threw.`);
  }
}

/**
 * Starts `target` and records the start as pending until it settles. When the start rejects and `target` is still
 * the current connection, the connection and its session id are cleared before the error is rethrown.
 */
async function startConnection(target: HubConnection): Promise<void> {
  const pending = target.start();
  starting = pending;

  try {
    await pending;
  } catch (error) {
    if (connection === target) {
      connection = null;
      connectedSessionId = null;
    }

    throw error;
  } finally {
    if (starting === pending) {
      starting = null;
    }
  }
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
 * - With a connection of the same session: resolves with its pending start, resolves at once while it is connected,
 *   connecting or reconnecting, and starts it again when it is disconnected or, after its stop completes, when it is
 *   disconnecting.
 * - With a connection of another session: that connection is closed first through {@link disconnectRealtime}.
 * A rejected start clears the connection and rejects with the start's error.
 */
export async function connectRealtime(sessionId: string): Promise<void> {
  while (connection !== null) {
    const current = connection;

    if (connectedSessionId !== sessionId) {
      await disconnectRealtime();
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
        await current.stop();
        continue;
      case HubConnectionState.Disconnected:
        return startConnection(current);
    }
  }

  const created = buildConnection(sessionId);
  connection = created;
  connectedSessionId = sessionId;

  return startConnection(created);
}

/**
 * Closes the current hub connection when one exists: the module state is cleared, the connection stops delivering
 * events to the registered handlers, and the returned promise resolves once the connection has stopped. Registered
 * handlers are kept.
 */
export async function disconnectRealtime(): Promise<void> {
  const current = connection;
  connection = null;
  connectedSessionId = null;
  starting = null;

  if (current === null) {
    return;
  }

  for (const name of REALTIME_EVENTS) {
    current.off(name);
  }

  await current.stop();
}
