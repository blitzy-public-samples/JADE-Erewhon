// Component-test helpers over the Erewhon.FixtureHost bridge: one fixture company with an open session per test,
// created before the test and deleted after it, and rendering of screens inside that session.

import { afterEach, beforeEach, inject } from 'vitest';
import { createElement } from 'react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { cleanup, render } from '@testing-library/react';
import type { RenderResult } from '@testing-library/react';
import { configureApiClient } from '../api/client';
import { disconnectRealtime } from '../api/realtime';
import type { LogonArea, SessionRole } from '../api/types';
import { SessionProvider } from '../session/SessionProvider';
import type { SessionState } from '../session/SessionProvider';

/** Name of a fixture the bridge builds. */
export type FixtureName = 'TestViewApp' | 'TestViewSale' | 'TestViewSaleItem' | 'AdminGuards';

/** Role of the session the bridge opens on a fixture; also the `session` query value of the create request. */
export type FixtureSessionKind = 'client' | 'agent' | 'administrator';

/** Internal ids of the rows a fixture created, keyed as the bridge returns them; a key is absent when not created. */
export interface FixtureIds {
  /** The fixture company. */
  readonly company?: number;
  /** The agent "John Smith". */
  readonly agent?: number;
  /** The agent "Jane Doe"; AdminGuards only. */
  readonly otherAgent?: number;
  /** The client "Clive Entworth". */
  readonly client?: number;
  /** The country "France". */
  readonly country?: number;
  /** The region "Paris". */
  readonly region?: number;
  /** The fixture's item category. */
  readonly itemCategory?: number;
  /** The commission rate held by "John Smith"; AdminGuards only. */
  readonly commissionRate?: number;
  /** The retail item of "John Smith". */
  readonly retailItem?: number;
  /** The sold retail item of "John Smith"; AdminGuards only. */
  readonly soldRetailItem?: number;
  /** The tender item of "John Smith". */
  readonly tenderItem?: number;
  /** The tender item whose closure date is the day before the server date; AdminGuards only. */
  readonly closedTenderItem?: number;
  /** The fixture's tender: the tender of 500 on the tender item, or in AdminGuards the winning tender of 600.00. */
  readonly tender?: number;
  /** The fixture's retail sale to the client. */
  readonly retailSale?: number;
  /** The fixture's tender sale to the client. */
  readonly tenderSale?: number;
  /** The unsold retail item of "Jane Doe"; AdminGuards only. */
  readonly otherRetailItem?: number;
  /** The sold retail item of "Jane Doe"; AdminGuards only. */
  readonly otherSoldRetailItem?: number;
  /** The retail sale of the sold retail item of "Jane Doe"; AdminGuards only. */
  readonly otherRetailSale?: number;
  /** The tender item sold through the winning tender; AdminGuards only. */
  readonly soldTenderItem?: number;
  /** The tender of 500.00 on the tender item; AdminGuards only. */
  readonly openTender?: number;
  /** The tender of 510.00 on the closed tender item; AdminGuards only. */
  readonly closedTender?: number;
  /** The tender of 550.00 on the sold tender item; AdminGuards only. */
  readonly otherTender?: number;
}

/** Values of the fixture created for the current test, read through getters. */
export interface FixtureHandle {
  /** Base URL of the bridge, without a trailing `/`. */
  readonly baseUrl: string;
  /** Internal id of the fixture company. */
  readonly companyId: number;
  /** Internal ids of the rows the fixture created. */
  readonly ids: FixtureIds;
  /** Id of the session the bridge opened on the fixture. */
  readonly sessionId: string;
  /** Session state of that session, as {@link SessionProvider} holds it. */
  readonly session: SessionState;
}

/** Body of the 201 answer to `POST /__fixtures/{name}`. */
interface FixtureCreatedResponse {
  companyId: number;
  ids: Record<string, number>;
  sessionId: string;
}

interface FixtureState {
  readonly baseUrl: string;
  readonly companyId: number;
  readonly ids: FixtureIds;
  readonly sessionId: string;
  readonly session: SessionState;
}

const DEFAULT_SESSION: Record<FixtureName, FixtureSessionKind> = {
  TestViewApp: 'client',
  TestViewSale: 'administrator',
  TestViewSaleItem: 'client',
  AdminGuards: 'agent',
};

const DEFAULT_USER_NAME: Partial<Record<FixtureName, Partial<Record<FixtureSessionKind, string>>>> = {
  AdminGuards: { agent: 'John Smith' },
};

/** Returns whether `value` is an object holding a number `companyId`, number `ids` values and a string `sessionId`. */
function isFixtureCreatedResponse(value: unknown): value is FixtureCreatedResponse {
  return (
    typeof value === 'object' &&
    value !== null &&
    'companyId' in value &&
    typeof value.companyId === 'number' &&
    'sessionId' in value &&
    typeof value.sessionId === 'string' &&
    'ids' in value &&
    typeof value.ids === 'object' &&
    value.ids !== null &&
    Object.values(value.ids).every((id) => typeof id === 'number')
  );
}

/** Returns `failures` as one error: the only failure, or an AggregateError of all of them in order. */
function combineFailures(failures: readonly unknown[], message: string): unknown {
  return failures.length === 1 ? failures[0] : new AggregateError(failures, message);
}

/**
 * Registers, in the enclosing `describe`, a `beforeEach` that creates the fixture `name` and an `afterEach` that
 * deletes it, and returns the handle of the current test's fixture.
 *
 * Before each test: sends `POST {fixtureBaseUrl}/__fixtures/{name}?session={kind}` with no session header, where
 * `kind` is `options.session` or the fixture's default (TestViewApp and TestViewSaleItem `client`, TestViewSale
 * `administrator`, AdminGuards `agent`); configures the API client with the bridge base URL and the returned session
 * id; and builds the handle's session: area `shop` for a client and `admin` otherwise, role `kind`, the fixture agent
 * id for an agent, the fixture client id for a client, and user name `options.userName`, else `John Smith` for an
 * AdminGuards agent session, else null. Any answer other than 201 fails the test with its status and body text.
 *
 * After each test: unmounts every rendered tree, closes the hub connection, sends `DELETE
 * {fixtureBaseUrl}/__fixtures/{companyId}` when the fixture was created, then clears the API client's session id and
 * 401 callback and the whole of session storage. Every step runs even when an earlier one fails; the hook then fails
 * with the one failure, or an AggregateError of all of them, and a delete answered with a status other than 204 fails
 * it with that status and body text.
 *
 * @param name Fixture to create.
 * @param options `session`: role of the session to open; `userName`: user name of the handle's session.
 * @returns Handle whose getters return the current test's values, and throw while no fixture has been created.
 */
export function useFixture(
  name: FixtureName,
  options?: { session?: FixtureSessionKind; userName?: string },
): FixtureHandle {
  let state: FixtureState | undefined;

  beforeEach(async () => {
    const baseUrl = inject('fixtureBaseUrl').replace(/\/+$/, '');
    const kind = options?.session ?? DEFAULT_SESSION[name];

    const response = await fetch(
      `${baseUrl}/__fixtures/${encodeURIComponent(name)}?session=${encodeURIComponent(kind)}`,
      { method: 'POST' },
    );
    const text = await response.text();
    if (response.status !== 201) {
      throw new Error(`Fixture ${name} (${kind}) failed: ${response.status} ${text}`);
    }

    const body: unknown = JSON.parse(text);
    if (!isFixtureCreatedResponse(body)) {
      throw new Error(`Fixture ${name} (${kind}) returned an unexpected body: ${text}`);
    }

    const ids: FixtureIds = { ...body.ids };
    const { companyId, sessionId } = body;

    configureApiClient({ baseUrl, sessionId });

    const area: LogonArea = kind === 'client' ? 'shop' : 'admin';
    const role: SessionRole = kind;
    const session: SessionState = {
      sessionId,
      area,
      role,
      agentId: kind === 'agent' ? (ids.agent ?? null) : null,
      clientId: kind === 'client' ? (ids.client ?? null) : null,
      userName: options?.userName ?? DEFAULT_USER_NAME[name]?.[kind] ?? null,
    };

    state = { baseUrl, companyId, ids, sessionId, session };
  });

  afterEach(async () => {
    const created = state;
    const failures: unknown[] = [];
    const attempt = async (step: () => unknown): Promise<void> => {
      try {
        await step();
      } catch (error: unknown) {
        failures.push(error);
      }
    };

    try {
      await attempt(() => cleanup());
      await attempt(() => disconnectRealtime());
      if (created !== undefined) {
        await attempt(async () => {
          const response = await fetch(`${created.baseUrl}/__fixtures/${created.companyId}`, { method: 'DELETE' });
          if (response.status !== 204) {
            throw new Error(
              `Fixture ${name} company ${created.companyId} was not deleted: ${response.status} ${await response.text()}`,
            );
          }
        });
      }
    } finally {
      configureApiClient({ sessionId: null, onUnauthorized: null });
      sessionStorage.clear();
      state = undefined;
    }

    if (failures.length > 0) {
      throw combineFailures(failures, `${failures.length} steps of the fixture ${name} clean-up failed.`);
    }
  });

  const current = (): FixtureState => {
    if (state === undefined) {
      throw new Error(`useFixture('${name}'): handle read before beforeEach ran`);
    }
    return state;
  };

  return {
    get baseUrl() {
      return current().baseUrl;
    },
    get companyId() {
      return current().companyId;
    },
    get ids() {
      return current().ids;
    },
    get sessionId() {
      return current().sessionId;
    },
    get session() {
      return current().session;
    },
  };
}

/**
 * Renders `ui` inside a `MemoryRouter` at `options.route` (`/` when omitted) and a {@link SessionProvider} started
 * with `handle.session`.
 *
 * @param ui Element to render.
 * @param handle Fixture handle of the current test, from {@link useFixture}; reading it before its `beforeEach` throws.
 * @param options `route`: initial router location.
 * @returns The Testing Library render result.
 */
export function renderWithSession(
  ui: ReactElement,
  handle: FixtureHandle,
  options?: { route?: string },
): RenderResult {
  return render(
    createElement(MemoryRouter, {
      initialEntries: [options?.route ?? '/'],
      children: createElement(SessionProvider, { initialSession: handle.session, children: ui }),
    }),
  );
}
