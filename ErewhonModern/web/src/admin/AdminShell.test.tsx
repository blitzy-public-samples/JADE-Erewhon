// Component tests of the admin shell against the fixture bridge: the caption and the menus and entries rendered in an
// agent session and in the company user's session, Agents › My Details, one View Items tab per choice, and
// Help › About.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../api/client';
import type { CompanyProjection } from '../api/types';
import { msg } from '../messages/messages';
import { renderWithSession, useFixture } from '../test/fixtures';
import type { FixtureHandle } from '../test/fixtures';
import { AdminShell } from './AdminShell';

const COMPANY_PATH = '/api/app/admin/company';

const FIXTURE_AGENT_NAME = 'John Smith';
const FIXTURE_COMPANY_NAME = 'Jade Software Corporation';

const MENU_BAR_NAME = 'Administration menu';
const AGENT_EDITOR_CAPTION = 'Agent';
const ABOUT_DIALOG_NAME = 'About Erewhon Investments';
const ABOUT_VERSION = '2.0';

const HIDDEN_HELP_ENTRIES: readonly string[] = ['Erewhon Documentation', 'Jade Information Library'];

const AGENT_MENUS: readonly string[] = ['File', 'Agents', 'Items', 'Tenders', 'Sales', 'Company', 'Help'];
const COMPANY_MENUS: readonly string[] = ['File', 'Agents', 'Clients', 'Items', 'Tenders', 'Sales', 'Company', 'Help'];

const FILE_ENTRIES: readonly string[] = ['Logout', 'Exit'];
const ITEMS_ENTRIES: readonly string[] = [
  'View Items',
  'View Items (By Category)',
  'Add Retail Item',
  'Add Tender Item',
];
const SALES_ENTRIES: readonly string[] = ['View Sales'];
const HELP_ENTRIES: readonly string[] = ['About'];

/** Entries of each menu rendered in an agent session, keyed by the menu's caption. */
const AGENT_MENU_ENTRIES: Readonly<Record<string, readonly string[]>> = {
  File: FILE_ENTRIES,
  Agents: ['My Details'],
  Items: ITEMS_ENTRIES,
  Tenders: ['View Tenders'],
  Sales: SALES_ENTRIES,
  Company: ['Commission Rates', 'Show Details via Interface'],
  Help: HELP_ENTRIES,
};

/** Entries of each menu rendered in the company user's session, keyed by the menu's caption. */
const COMPANY_MENU_ENTRIES: Readonly<Record<string, readonly string[]>> = {
  File: FILE_ENTRIES,
  Agents: ['View Agents', 'Add Agent', 'Agent Commission Rates'],
  Clients: ['View Clients', 'Add Client'],
  Items: ITEMS_ENTRIES,
  Tenders: ['View Tenders', 'Close Tenders'],
  Sales: SALES_ENTRIES,
  Company: [
    'Company Details',
    'Commission Rates',
    'Item Categories',
    'Countries',
    'Regions',
    'Show Details via Interface',
  ],
  Help: HELP_ENTRIES,
};

const ASYNC_TIMEOUT = { timeout: 15_000 };

/** Renders the shell in the fixture's session and returns the test's user-event instance. */
function renderShell(handle: FixtureHandle): UserEvent {
  const user = userEvent.setup();
  renderWithSession(<AdminShell />, handle);
  return user;
}

/** Returns the text of the shell's caption heading. */
function shellCaption(): string {
  return screen.getByRole('heading', { level: 1 }).textContent ?? '';
}

/** Returns the caption of a menu bar item or a menu entry. */
function captionOf(element: HTMLElement): string {
  return element.textContent ?? '';
}

/** Returns the top-level items of the menu bar in display order, without the entries of an open menu. */
function topLevelItems(): HTMLElement[] {
  return within(screen.getByRole('menubar', { name: MENU_BAR_NAME }))
    .getAllByRole('menuitem')
    .filter((item) => item.closest('[role="menu"]') === null);
}

/** Returns the top-level item captioned `top`; throws when the menu bar holds none. */
function topLevelItem(top: string): HTMLElement {
  const item = topLevelItems().find((candidate) => captionOf(candidate) === top);
  if (item === undefined) {
    throw new Error(`The menu bar holds no ${top} menu`);
  }
  return item;
}

/** Clicks the top-level item `top` and returns the menu it opens. */
async function openMenu(user: UserEvent, top: string): Promise<HTMLElement> {
  await user.click(topLevelItem(top));
  return screen.findByRole('menu', { name: top });
}

/** Closes the open menu with Escape and waits until no menu is rendered. */
async function closeMenu(user: UserEvent): Promise<void> {
  await user.keyboard('{Escape}');
  await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
}

/**
 * Clicks the top-level item `top`, reads the captions of the entries of the menu it opens in display order, closes
 * that menu with Escape and returns the captions.
 */
async function menuEntries(top: string): Promise<string[]> {
  const user = userEvent.setup();
  const menu = await openMenu(user, top);
  const entries = within(menu).getAllByRole('menuitem').map(captionOf);
  await closeMenu(user);
  return entries;
}

/** Returns the entries of every top-level menu, read with {@link menuEntries} and keyed by the menu's caption. */
async function allMenuEntries(): Promise<Record<string, string[]>> {
  const entries: Record<string, string[]> = {};
  for (const top of topLevelItems().map(captionOf)) {
    entries[top] = await menuEntries(top);
  }
  return entries;
}

/** Opens the top-level menu `top`, clicks its entry `entry` and waits until the menu is closed. */
async function chooseEntry(user: UserEvent, top: string, entry: string): Promise<void> {
  const menu = await openMenu(user, top);
  await user.click(within(menu).getByRole('menuitem', { name: entry }));
  await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
}

/** Opens Help, asserts that it renders About and neither hidden Help entry, and closes it. */
async function expectHiddenHelpEntriesAbsent(user: UserEvent): Promise<void> {
  const menu = await openMenu(user, 'Help');
  expect(within(menu).getAllByRole('menuitem').map(captionOf)).toEqual(HELP_ENTRIES);
  for (const caption of HIDDEN_HELP_ENTRIES) {
    expect(screen.queryByRole('menuitem', { name: caption })).toBeNull();
  }
  await closeMenu(user);
}

describe('AdminShell', () => {
  describe('agent session', () => {
    const fixture = useFixture('AdminGuards', { session: 'agent' });

    it('captions the shell with the agent name and renders the agent menus in menu bar order', () => {
      renderShell(fixture);

      expect(shellCaption()).toBe(`${msg('Administration')} - ${FIXTURE_AGENT_NAME}`);
      expect(topLevelItems().map(captionOf)).toEqual(AGENT_MENUS);
    });

    it('lists only the agent entries of every menu and renders neither hidden Help entry', async () => {
      const user = renderShell(fixture);

      expect(await allMenuEntries()).toEqual(AGENT_MENU_ENTRIES);
      await expectHiddenHelpEntriesAbsent(user);
    });

    it('opens the session agent in the agent editor from Agents › My Details', async () => {
      expect(fixture.session.agentId).toBe(fixture.ids.agent);
      const user = renderShell(fixture);

      await chooseEntry(user, 'Agents', 'My Details');

      const dialog = await screen.findByRole('dialog', { name: `${msg('Edit')} ${AGENT_EDITOR_CAPTION}` });
      expect(
        await within(dialog).findByDisplayValue(FIXTURE_AGENT_NAME, undefined, ASYNC_TIMEOUT),
      ).toBeInTheDocument();
    });
  });

  describe('administrator session', () => {
    // The company user's session carries the fixture company's name as its user name (D-217).
    const fixture = useFixture('AdminGuards', { session: 'administrator', userName: FIXTURE_COMPANY_NAME });

    it('captions the shell with the company name and renders every menu in menu bar order', async () => {
      const company = await api.get<CompanyProjection>(COMPANY_PATH);
      renderShell(fixture);

      expect(fixture.session.userName).toBe(company.name);
      expect(shellCaption()).toBe(`${msg('Administration')} - ${fixture.session.userName}`);
      expect(shellCaption()).toBe(`${msg('Administration')} - ${company.name}`);
      expect(topLevelItems().map(captionOf)).toEqual(COMPANY_MENUS);
    });

    it('lists every company-user entry without My Details and renders neither hidden Help entry', async () => {
      const user = renderShell(fixture);

      expect(await allMenuEntries()).toEqual(COMPANY_MENU_ENTRIES);
      await expectHiddenHelpEntriesAbsent(user);
    });

    it('adds a new selected View Items tab on every choice of Items › View Items', async () => {
      const tabCaption = msg('ViewItems');
      const user = renderShell(fixture);

      await chooseEntry(user, 'Items', 'View Items');

      const [first] = screen.getAllByRole('tab');
      expect(screen.getAllByRole('tab')).toHaveLength(1);
      expect(first).toHaveAccessibleName(tabCaption);
      expect(first).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByRole('tabpanel', { name: tabCaption })).toHaveAttribute('aria-labelledby', first.id);

      await chooseEntry(user, 'Items', 'View Items');

      const tabs = screen.getAllByRole('tab');
      expect(tabs).toHaveLength(2);
      const [firstAgain, second] = tabs;
      expect(firstAgain).toBe(first);
      expect(second).toHaveAccessibleName(tabCaption);
      expect(first).toHaveAttribute('aria-selected', 'false');
      expect(second).toHaveAttribute('aria-selected', 'true');
      expect(screen.getAllByRole('tabpanel', { hidden: true })).toHaveLength(2);
      expect(screen.getByRole('tabpanel', { name: tabCaption })).toHaveAttribute('aria-labelledby', second.id);
    });

    it('shows the About dialog with the version from Help › About', async () => {
      const user = renderShell(fixture);

      await chooseEntry(user, 'Help', 'About');

      const dialog = await screen.findByRole('dialog', { name: ABOUT_DIALOG_NAME });
      expect(
        await within(dialog).findByText(`${msg('BaseVersion')} ${ABOUT_VERSION}`, undefined, ASYNC_TIMEOUT),
      ).toBeInTheDocument();
    });
  });
});
