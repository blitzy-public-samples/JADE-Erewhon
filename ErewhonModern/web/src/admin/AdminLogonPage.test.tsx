// Component tests of the admin-area logon screen against the fixture bridge: the default Administrator option and
// version label, the agent list, the no-user message, and the agent and administrator logons.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { api } from '../api/client';
import type { AgentProjection, CompanyProjection, LogonArea, SessionRole } from '../api/types';
import { msg } from '../messages/messages';
import { useSession } from '../session/SessionProvider';
import { renderWithSession, useFixture } from '../test/fixtures';
import type { FixtureHandle } from '../test/fixtures';
import { AdminLogonPage } from './AdminLogonPage';

const ADMIN_AREA: LogonArea = 'admin';
const AGENT_ROLE: SessionRole = 'agent';
const ADMINISTRATOR_ROLE: SessionRole = 'administrator';

/** Shows the fields of the session the provider currently holds, one labelled output per field. */
function Probe() {
  const { session } = useSession();
  return (
    <>
      <output aria-label="role">{session?.role}</output>
      <output aria-label="area">{session?.area}</output>
      <output aria-label="user name">{session?.userName}</output>
      <output aria-label="agent id">{session?.agentId ?? ''}</output>
      <output aria-label="session id">{session?.sessionId}</output>
    </>
  );
}

/** Returns the text of the probe output labelled `name`. */
function probeValue(name: string): string {
  return screen.getByRole('status', { name }).textContent ?? '';
}

/** Renders the logon page with the probe beside it inside the fixture session. */
function renderLogonPage(fixture: FixtureHandle) {
  return renderWithSession(
    <>
      <AdminLogonPage />
      <Probe />
    </>,
    fixture,
  );
}

describe('AdminLogonPage', () => {
  const fixture = useFixture('AdminGuards');

  it('starts with Administrator checked, the agent list disabled and the version label shown', async () => {
    renderLogonPage(fixture);

    const administrator = await screen.findByRole('radio', { name: 'Administrator' });
    expect(administrator).toBeChecked();
    expect(screen.getByRole('radio', { name: msg('Agent') })).not.toBeChecked();
    expect(screen.getByRole('combobox', { name: msg('Agent') })).toBeDisabled();
    expect(screen.getByText(`${msg('BaseVersion')} 2.0`)).toBeInTheDocument();
  });

  it('enables the agent list on choosing Agent and lists the company agents in name order', async () => {
    const agents = await api.get<AgentProjection[]>('/api/app/admin/agents');
    const expectedNames = agents.map((agent) => agent.name);
    const user = userEvent.setup();
    renderLogonPage(fixture);

    await user.click(await screen.findByRole('radio', { name: msg('Agent') }));

    const select = screen.getByRole('combobox', { name: msg('Agent') });
    expect(select).toBeEnabled();
    expect(select).toHaveValue('');
    const listedNames = within(select)
      .getAllByRole('option')
      .map((option) => option.textContent ?? '')
      .filter((name) => name !== '');
    expect(listedNames).toEqual(expectedNames);
    expect(listedNames).toContain('John Smith');
    expect(listedNames).toContain('Jane Doe');
  });

  it('shows the user-not-selected message and keeps the page and session when no agent is chosen', async () => {
    const user = userEvent.setup();
    renderLogonPage(fixture);

    await user.click(await screen.findByRole('radio', { name: msg('Agent') }));
    expect(screen.getByRole('combobox', { name: msg('Agent') })).toHaveValue('');
    await user.click(screen.getByRole('button', { name: 'OK' }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain(msg('UserNotSelected'));
    expect(within(dialog).getByRole('heading', { name: msg('Error') })).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'OK' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    expect(screen.getByRole('heading', { name: 'logon' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: msg('Agent') })).toBeChecked();
    expect(screen.getByRole('button', { name: 'OK' })).toBeInTheDocument();
    expect(probeValue('session id')).toBe(fixture.sessionId);
    expect(probeValue('role')).toBe(fixture.session.role);
    expect(probeValue('user name')).toBe(fixture.session.userName ?? '');
  });

  it('opens an admin agent session for the chosen agent', async () => {
    const user = userEvent.setup();
    renderLogonPage(fixture);

    await user.click(await screen.findByRole('radio', { name: msg('Agent') }));
    await user.selectOptions(screen.getByRole('combobox', { name: msg('Agent') }), 'John Smith');
    await user.click(screen.getByRole('button', { name: 'OK' }));

    await waitFor(() => {
      const sessionId = probeValue('session id');
      expect(sessionId).not.toBe('');
      expect(sessionId).not.toBe(fixture.sessionId);
    });
    expect(probeValue('role')).toBe(AGENT_ROLE);
    expect(probeValue('area')).toBe(ADMIN_AREA);
    expect(probeValue('user name')).toBe('John Smith');
    expect(probeValue('agent id')).toBe(String(fixture.ids.agent));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('opens an administrator session named after the company when Administrator is kept', async () => {
    const company = await api.get<CompanyProjection>('/api/app/admin/company');
    const user = userEvent.setup();
    renderLogonPage(fixture);

    expect(await screen.findByRole('radio', { name: 'Administrator' })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'OK' }));

    await waitFor(() => expect(probeValue('role')).toBe(ADMINISTRATOR_ROLE));
    expect(probeValue('session id')).not.toBe(fixture.sessionId);
    expect(probeValue('area')).toBe(ADMIN_AREA);
    expect(probeValue('user name')).toBe(company.name);
    expect(probeValue('agent id')).toBe('');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
