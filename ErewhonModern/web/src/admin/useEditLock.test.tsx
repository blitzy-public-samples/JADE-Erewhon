// Component tests of the soft edit lock hook of the admin edit dialogs against the fixture bridge: the prompt shown on
// opening a row that is already locked, the save request sent on Yes, the lock taken on the first change, the
// outstanding-changes prompt on close, the unlock that keeps another session's lock, and the save error box.

import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SESSION_HEADER, api } from '../api/client';
import { connectRealtime, on } from '../api/realtime';
import type {
  AgentDetailsResponse,
  AgentProjection,
  AgentRequest,
  CreateSessionRequest,
  CreateSessionResponse,
  OperationResult,
  SaveOutstandingChangesMessage,
  SoftLockStatus,
} from '../api/types';
import { msg } from '../messages/messages';
import { renderWithSession, useFixture } from '../test/fixtures';
import type { FixtureHandle } from '../test/fixtures';
import { useEditLock } from './useEditLock';

const AGENT_EDIT_LOCKS_PATH = '/api/app/edit-locks/Agent';
const AGENTS_PATH = '/api/app/admin/agents';
const SESSIONS_PATH = '/api/app/sessions';
const JSON_CONTENT_TYPE = 'application/json';

/** Edit dialog of the agent `id` reduced to its lock: the hook's message box, its dirty flag and three actions. */
function Harness({ id, doSave }: { id: number | null; doSave: () => Promise<OperationResult> }) {
  const lock = useEditLock('Agent', id);
  const [closed, setClosed] = useState(false);

  if (closed) {
    return <p>closed</p>;
  }

  return (
    <div>
      {lock.element}
      <output aria-label="dirty">{String(lock.isDirty)}</output>
      <button type="button" onClick={() => lock.markChanged()}>
        Change
      </button>
      <button type="button" onClick={() => void lock.close(doSave).then(setClosed)}>
        Close
      </button>
      <button type="button" onClick={() => void lock.save(doSave)}>
        Save
      </button>
    </div>
  );
}

/** Returns the id of the fixture agent "John Smith". */
function agentId(fixture: FixtureHandle): number {
  const id = fixture.ids.agent;
  if (id === undefined) {
    throw new Error('The AdminGuards fixture returned no agent id');
  }
  return id;
}

/** Returns the edit-lock path of the agent `id`. */
function lockPath(id: number): string {
  return `${AGENT_EDIT_LOCKS_PATH}/${id}`;
}

/** Sends `init` to `path` on the bridge with the session header `sessionId` and returns the parsed JSON body. */
async function sendAs<T>(
  fixture: FixtureHandle,
  path: string,
  sessionId: string | null,
  init: RequestInit,
): Promise<T> {
  const headers: Record<string, string> = { Accept: JSON_CONTENT_TYPE };
  if (init.body !== undefined) {
    headers['Content-Type'] = JSON_CONTENT_TYPE;
  }
  if (sessionId !== null) {
    headers[SESSION_HEADER] = sessionId;
  }

  const response = await fetch(`${fixture.baseUrl}${path}`, { ...init, headers });
  const text = await response.text();
  if (response.status !== 200) {
    throw new Error(`${init.method ?? 'GET'} ${path} answered ${response.status} ${text}`);
  }
  return JSON.parse(text) as T;
}

/** Reads the lock state of the agent `id` as the session `sessionId`, the fixture session when omitted. */
function status(fixture: FixtureHandle, id: number, sessionId: string = fixture.sessionId): Promise<SoftLockStatus> {
  return sendAs<SoftLockStatus>(fixture, lockPath(id), sessionId, { method: 'GET' });
}

/** Opens an administrator session on the fixture company and returns its session id. */
async function otherSession(fixture: FixtureHandle): Promise<string> {
  const request: CreateSessionRequest = { area: 'admin', administrator: true, userName: null };
  const created = await sendAs<CreateSessionResponse>(fixture, SESSIONS_PATH, null, {
    method: 'POST',
    body: JSON.stringify(request),
  });
  if (!created.succeeded || created.sessionId === null) {
    throw new Error(`The administrator logon failed: ${created.message ?? ''}`);
  }
  return created.sessionId;
}

/** Sets the lock of the agent `id` to the session `sessionId` and returns the operation result. */
function lockAs(fixture: FixtureHandle, id: number, sessionId: string): Promise<OperationResult> {
  return sendAs<OperationResult>(fixture, lockPath(id), sessionId, { method: 'POST' });
}

/** Reads the agent `id` through the fixture session. */
async function readAgent(id: number): Promise<AgentProjection> {
  const details = await api.get<AgentDetailsResponse>(`${AGENTS_PATH}/${id}`);
  return details.agent;
}

/** Returns the update request that writes back the current name and address of the agent `id`. */
async function currentAgentRequest(id: number): Promise<AgentRequest> {
  const agent = await readAgent(id);
  return {
    name: agent.name,
    address: {
      street: agent.street,
      city: agent.city,
      country: agent.country,
      phone: agent.phone,
      fax: agent.fax,
      email: agent.email,
      webSite: agent.webSite,
    },
  };
}

/** Returns a save that updates the agent `id` with its current name and address. */
function resaveAgent(id: number): () => Promise<OperationResult> {
  return async () => api.put<OperationResult>(`${AGENTS_PATH}/${id}`, await currentAgentRequest(id));
}

/** Returns a save that updates the agent `id` with an empty name and its current address. */
function saveWithEmptyName(id: number): () => Promise<OperationResult> {
  return async () => api.put<OperationResult>(`${AGENTS_PATH}/${id}`, { ...(await currentAgentRequest(id)), name: '' });
}

/** Save passed to tests whose dialog is never saved; calling it rejects. */
function unexpectedSave(): Promise<OperationResult> {
  return Promise.reject(new Error('The dialog was saved'));
}

/** Returns the message text of the message box `dialog`. */
function messageOf(dialog: HTMLElement): string {
  const messageId = dialog.getAttribute('aria-describedby');
  const message = messageId === null ? null : document.getElementById(messageId);
  return message?.textContent ?? '';
}

/** Returns the captions of the buttons of the message box `dialog`, in order. */
function buttonCaptions(dialog: HTMLElement): string[] {
  return within(dialog)
    .getAllByRole('button')
    .map((button) => button.textContent ?? '');
}

/** Returns the text of the harness's dirty output. */
function dirtyValue(): string {
  return screen.getByRole('status', { name: 'dirty' }).textContent ?? '';
}

/** Waits until no message box is shown. */
async function noDialog(): Promise<void> {
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
}

/** Waits until the fixture session holds the lock of the agent `id`. */
async function lockedByFixture(fixture: FixtureHandle, id: number): Promise<void> {
  await waitFor(async () => {
    const current = await status(fixture, id);
    expect(current.isLocked).toBe(true);
    expect(current.isLockedByCaller).toBe(true);
  });
}

/** Renders the harness for the unlocked agent `id` and returns once the row reads unlocked. */
async function renderUnlocked(
  fixture: FixtureHandle,
  id: number,
  doSave: () => Promise<OperationResult>,
): Promise<void> {
  renderWithSession(<Harness id={id} doSave={doSave} />, fixture);
  expect((await status(fixture, id)).isLocked).toBe(false);
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
}

describe('useEditLock', () => {
  const fixture = useFixture('AdminGuards', { session: 'agent' });

  it('prompts with Yes and No on opening a row that its own session has locked', async () => {
    const id = agentId(fixture);
    expect((await api.post<OperationResult>(lockPath(id))).succeeded).toBe(true);

    renderWithSession(<Harness id={id} doSave={unexpectedSave} />, fixture);

    const dialog = await screen.findByRole('dialog', { name: msg('ThingAlreadyBeingEdited', msg('Thing')) });
    expect(messageOf(dialog)).toBe(msg('ThingCurrentlyEdited', msg('Thing')));
    expect(buttonCaptions(dialog)).toEqual(['Yes', 'No']);
    expect(within(dialog).getByRole('img', { name: 'question' })).toBeInTheDocument();
  });

  it('sends the save request to the lock holder on Yes and leaves the row, lock and dialog unchanged', async () => {
    const id = agentId(fixture);
    expect((await api.post<OperationResult>(lockPath(id))).succeeded).toBe(true);
    const before = await readAgent(id);
    let received: (message: SaveOutstandingChangesMessage) => void = () => undefined;
    const delivered = new Promise<SaveOutstandingChangesMessage>((resolve) => {
      received = resolve;
    });
    const stopListening = on('SaveOutstandingChanges', (message) => received(message));

    try {
      await connectRealtime(fixture.sessionId);
      const user = userEvent.setup();
      renderWithSession(<Harness id={id} doSave={unexpectedSave} />, fixture);

      const dialog = await screen.findByRole('dialog', { name: msg('ThingAlreadyBeingEdited', msg('Thing')) });
      await user.click(within(dialog).getByRole('button', { name: 'Yes' }));
      await noDialog();

      expect(await delivered).toEqual({ entityType: 'Agent', id });
      expect(screen.getByRole('button', { name: 'Change' })).toBeInTheDocument();
      expect(screen.queryByText('closed')).not.toBeInTheDocument();
      expect(dirtyValue()).toBe('false');

      const after = await status(fixture, id);
      expect(after.isLocked).toBe(true);
      expect(after.isLockedByCaller).toBe(true);
      expect(await readAgent(id)).toEqual(before);
    } finally {
      stopListening();
    }
  });

  it('locks the row for its session on the first change', async () => {
    const id = agentId(fixture);
    const user = userEvent.setup();
    await renderUnlocked(fixture, id, unexpectedSave);

    await user.click(screen.getByRole('button', { name: 'Change' }));

    expect(dirtyValue()).toBe('true');
    await lockedByFixture(fixture, id);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('asks about outstanding changes on close and keeps the dialog and its lock on Cancel', async () => {
    const id = agentId(fixture);
    const user = userEvent.setup();
    await renderUnlocked(fixture, id, unexpectedSave);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    await lockedByFixture(fixture, id);
    await user.click(screen.getByRole('button', { name: 'Close' }));

    const dialog = await screen.findByRole('dialog', { name: msg('OutstandingChanges') });
    expect(messageOf(dialog)).toBe(msg('ChangesSinceOpeningForm'));
    expect(buttonCaptions(dialog)).toEqual(['Yes', 'No', 'Cancel']);

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await noDialog();

    expect(screen.getByRole('button', { name: 'Change' })).toBeInTheDocument();
    expect(screen.queryByText('closed')).not.toBeInTheDocument();
    expect(dirtyValue()).toBe('true');
    const current = await status(fixture, id);
    expect(current.isLocked).toBe(true);
    expect(current.isLockedByCaller).toBe(true);
  });

  it('closes without saving and clears the lock when outstanding changes are answered No', async () => {
    const id = agentId(fixture);
    const user = userEvent.setup();
    await renderUnlocked(fixture, id, unexpectedSave);
    const before = await readAgent(id);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    await lockedByFixture(fixture, id);
    await user.click(screen.getByRole('button', { name: 'Close' }));

    const dialog = await screen.findByRole('dialog', { name: msg('OutstandingChanges') });
    await user.click(within(dialog).getByRole('button', { name: 'No' }));

    expect(await screen.findByText('closed')).toBeInTheDocument();
    expect((await status(fixture, id)).isLocked).toBe(false);
    expect(await readAgent(id)).toEqual(before);
  });

  it('saves, closes and clears the lock when outstanding changes are answered Yes', async () => {
    const id = agentId(fixture);
    const user = userEvent.setup();
    await renderUnlocked(fixture, id, resaveAgent(id));
    const before = await readAgent(id);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    await lockedByFixture(fixture, id);
    await user.click(screen.getByRole('button', { name: 'Close' }));

    const dialog = await screen.findByRole('dialog', { name: msg('OutstandingChanges') });
    await user.click(within(dialog).getByRole('button', { name: 'Yes' }));

    expect(await screen.findByText('closed')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect((await status(fixture, id)).isLocked).toBe(false);
    expect(await readAgent(id)).toEqual(before);
  });

  it('keeps the lock another session holds when the dialog closes', async () => {
    const id = agentId(fixture);
    const other = await otherSession(fixture);
    expect((await lockAs(fixture, id, other)).succeeded).toBe(true);
    const user = userEvent.setup();

    renderWithSession(<Harness id={id} doSave={unexpectedSave} />, fixture);

    const dialog = await screen.findByRole('dialog', { name: msg('ThingAlreadyBeingEdited', msg('Thing')) });
    await user.click(within(dialog).getByRole('button', { name: 'No' }));
    await noDialog();
    // Closed with no change, so the only lock request this session sends is the unlock.
    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(await screen.findByText('closed')).toBeInTheDocument();
    const asHolder = await status(fixture, id, other);
    expect(asHolder.isLocked).toBe(true);
    expect(asHolder.isLockedByCaller).toBe(true);
    const asFixture = await status(fixture, id);
    expect(asFixture.isLocked).toBe(true);
    expect(asFixture.isLockedByCaller).toBe(false);
  });

  it('shows a failed save in the error-during-save box and keeps the lock and the changes', async () => {
    const id = agentId(fixture);
    const user = userEvent.setup();
    await renderUnlocked(fixture, id, saveWithEmptyName(id));
    const before = await readAgent(id);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    await lockedByFixture(fixture, id);
    await user.click(screen.getByRole('button', { name: 'Save' }));

    const dialog = await screen.findByRole('dialog', { name: msg('ErrorDuringSave') });
    expect(messageOf(dialog)).toContain(msg('AgentNameRequired'));
    expect(buttonCaptions(dialog)).toEqual(['OK']);

    await user.click(within(dialog).getByRole('button', { name: 'OK' }));
    await noDialog();

    expect(dirtyValue()).toBe('true');
    const current = await status(fixture, id);
    expect(current.isLocked).toBe(true);
    expect(current.isLockedByCaller).toBe(true);
    expect(await readAgent(id)).toEqual(before);
  });

  it('takes no lock for a new row and still asks about outstanding changes on close', async () => {
    const user = userEvent.setup();
    renderWithSession(<Harness id={null} doSave={unexpectedSave} />, fixture);

    await user.click(screen.getByRole('button', { name: 'Change' }));
    expect(dirtyValue()).toBe('true');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect((await status(fixture, agentId(fixture))).isLocked).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Close' }));
    const dialog = await screen.findByRole('dialog', { name: msg('OutstandingChanges') });
    expect(messageOf(dialog)).toBe(msg('ChangesSinceOpeningForm'));
    await user.click(within(dialog).getByRole('button', { name: 'No' }));

    expect(await screen.findByText('closed')).toBeInTheDocument();
  });
});
