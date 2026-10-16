// Component tests of the admin commission rates list against the fixture bridge: the Add, Edit and Remove buttons and
// the double-click on a rate, in an agent's session and in the company user's, with the buttons shown and hidden.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../../api/client';
import type { CommissionRateRow, Id } from '../../api/types';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { CommissionRatesListPage } from './CommissionRatesListPage';

const COMMISSION_RATES_PATH = '/api/app/admin/commission-rates';

const CATEGORY_CAPTION = 'Sale Item Category';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';
const DIALOG_CATEGORY_CAPTION = 'Item Category';
const RATE_SEPARATOR = ' @ ';

const ADD_EDIT_REMOVE_CAPTIONS: readonly string[] = [ADD_CAPTION, EDIT_CAPTION, REMOVE_CAPTION];

const ASYNC_TIMEOUT = { timeout: 15_000 };

/** Returns the id the fixture recorded under `key`, and throws when the fixture returned none. */
function fixtureId(handle: FixtureHandle, key: keyof FixtureIds): Id {
  const id = handle.ids[key];
  if (id === undefined) {
    throw new Error(`The AdminGuards fixture returned no ${key} id`);
  }
  return id;
}

/** Returns the accessible name of the commission rate editor opened for an existing rate. */
function editDialogName(): string {
  return `${msg('Edit')} Commission Rate`;
}

/** Renders the page in the fixture's session, with the Add, Edit and Remove buttons hidden when asked. */
function renderPage(handle: FixtureHandle, hideAddEditRemove = false): UserEvent {
  const user = userEvent.setup();
  renderWithSession(<CommissionRatesListPage hideAddEditRemove={hideAddEditRemove} />, handle);
  return user;
}

/** Returns the Add, Edit and Remove buttons, in that order. */
function addEditRemoveButtons(): HTMLElement[] {
  return ADD_EDIT_REMOVE_CAPTIONS.map((caption) => screen.getByRole('button', { name: caption }));
}

/**
 * Reads the fixture's commission rate, chooses its item category in "Sale Item Category" once that list holds it, and
 * returns the rate's entry in the rates list once it is listed.
 */
async function selectFixtureRate(handle: FixtureHandle, user: UserEvent): Promise<HTMLElement> {
  const rate = await api.get<CommissionRateRow>(`${COMMISSION_RATES_PATH}/${fixtureId(handle, 'commissionRate')}`);
  const categoryValue = String(rate.itemCategoryId);

  const select = screen.getByLabelText<HTMLSelectElement>(CATEGORY_CAPTION);
  await waitFor(() => {
    expect(Array.from(select.options, (option) => option.value)).toContain(categoryValue);
  }, ASYNC_TIMEOUT);
  await user.selectOptions(select, categoryValue);

  const listbox = await screen.findByRole('listbox', { name: msg('CommissionRates') });
  const entryStart = `${rate.itemCategoryName}${RATE_SEPARATOR}`;
  return within(listbox).findByRole(
    'option',
    { name: (accessibleName) => accessibleName.startsWith(entryStart) },
    ASYNC_TIMEOUT,
  );
}

/**
 * Waits until the commission rate editor is open and has read the rate, and returns it after checking that its "Item
 * Category" list holds the fixture's item category.
 */
async function openedEditor(handle: FixtureHandle): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog', { name: editDialogName() }, ASYNC_TIMEOUT);
  await waitFor(() => {
    expect(dialog).toHaveAttribute('aria-busy', 'false');
  }, ASYNC_TIMEOUT);
  expect(within(dialog).getByLabelText(DIALOG_CATEGORY_CAPTION)).toHaveValue(
    String(fixtureId(handle, 'itemCategory')),
  );
  return dialog;
}

describe('CommissionRatesListPage guards for an agent session', () => {
  const fixture = useFixture('AdminGuards', { session: 'agent' });

  it('keeps Add, Edit and Remove disabled for an agent before and after a rate is selected', async () => {
    const user = renderPage(fixture);

    for (const button of addEditRemoveButtons()) {
      expect(button).toBeDisabled();
    }

    const option = await selectFixtureRate(fixture, user);
    await user.click(option);

    expect(option).toHaveAttribute('aria-selected', 'true');
    for (const button of addEditRemoveButtons()) {
      expect(button).toBeDisabled();
    }
  });

  it('opens no editor when an agent double-clicks a rate', async () => {
    const user = renderPage(fixture);

    const option = await selectFixtureRate(fixture, user);
    await user.dblClick(option);

    expect(option).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('does not render Add, Edit and Remove when hideAddEditRemove is set', async () => {
    const user = renderPage(fixture, true);

    const option = await selectFixtureRate(fixture, user);

    expect(option).toBeInTheDocument();
    for (const caption of ADD_EDIT_REMOVE_CAPTIONS) {
      expect(screen.queryByRole('button', { name: caption })).toBeNull();
    }
    expect(screen.getByRole('button', { name: CLOSE_CAPTION })).toBeEnabled();
  });
});

describe('CommissionRatesListPage guards for the company user', () => {
  const fixture = useFixture('AdminGuards', { session: 'administrator' });

  it('enables Add at once and Edit and Remove only after a rate is selected', async () => {
    const user = renderPage(fixture);

    const [addButton, editButton, removeButton] = addEditRemoveButtons();
    expect(addButton).toBeEnabled();
    expect(editButton).toBeDisabled();
    expect(removeButton).toBeDisabled();

    const option = await selectFixtureRate(fixture, user);
    expect(editButton).toBeDisabled();
    expect(removeButton).toBeDisabled();

    await user.click(option);

    expect(option).toHaveAttribute('aria-selected', 'true');
    expect(addButton).toBeEnabled();
    expect(editButton).toBeEnabled();
    expect(removeButton).toBeEnabled();
  });

  it('opens the commission rate editor when the company user double-clicks a rate', async () => {
    const user = renderPage(fixture);

    const option = await selectFixtureRate(fixture, user);
    await user.dblClick(option);

    expect(await openedEditor(fixture)).toBeInTheDocument();
  });

  it('still opens the editor on double-click when the buttons are hidden', async () => {
    const user = renderPage(fixture, true);

    const option = await selectFixtureRate(fixture, user);
    for (const caption of ADD_EDIT_REMOVE_CAPTIONS) {
      expect(screen.queryByRole('button', { name: caption })).toBeNull();
    }

    await user.dblClick(option);

    expect(await openedEditor(fixture)).toBeInTheDocument();
  });
});
