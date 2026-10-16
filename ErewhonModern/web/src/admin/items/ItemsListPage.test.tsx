// Component tests of the admin View Items list against the fixture bridge in an agent's session: the Agent filter fixed
// to the session's agent, the Edit and Delete buttons of a selected row, and the styles and status words of the rows.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../../api/client';
import type { Id, ItemDetailsResponse, ItemProjection } from '../../api/types';
import type { RowStyle } from '../../components/DataTable';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { ItemsListPage, itemDeleteEnabled } from './ItemsListPage';

const ITEMS_PATH = '/api/app/items';

const SESSION_AGENT_NAME = 'John Smith';
const OTHER_AGENT_NAME = 'Jane Doe';

const APPLY_FILTER_CAPTION = 'Apply Filter';
const CLEAR_FILTERS_CAPTION = 'Clear Filters';
const EDIT_CAPTION = 'Edit';
const DELETE_CAPTION = 'Delete';

const STATUS_COLUMN = 0;
const CODE_COLUMN = 2;
const AGENT_COLUMN = 4;

const ASYNC_TIMEOUT = { timeout: 15_000 };

/** Expected row style and Status cell text of one fixture item's row. */
interface RowAppearance {
  key: keyof FixtureIds;
  rowStyle: RowStyle;
  status: string;
}

/** Returns the id the fixture recorded under `key`, and throws when the fixture returned none. */
function fixtureId(fixture: FixtureHandle, key: keyof FixtureIds): Id {
  const id = fixture.ids[key];
  if (id === undefined) {
    throw new Error(`The AdminGuards fixture returned no ${key} id`);
  }
  return id;
}

/** Returns the item of GET /api/app/items/{id}. */
async function itemOf(id: Id): Promise<ItemProjection> {
  const details = await api.get<ItemDetailsResponse>(`${ITEMS_PATH}/${id}`);
  return details.item;
}

/** Returns the code of the item `id`, and throws when the item has none. */
async function itemCode(id: Id): Promise<string> {
  const { code } = await itemOf(id);
  if (code === null || code === '') {
    throw new Error(`Item ${id} has no code`);
  }
  return code;
}

/** Returns the codes of the fixture items recorded under `keys`, in that order. */
function itemCodes(fixture: FixtureHandle, keys: readonly (keyof FixtureIds)[]): Promise<string[]> {
  return Promise.all(keys.map((key) => itemCode(fixtureId(fixture, key))));
}

/** Returns the cell texts of the table row `rowElement`, left to right. */
function cellTexts(rowElement: HTMLElement): string[] {
  return within(rowElement)
    .queryAllByRole('cell')
    .map((cell) => cell.textContent ?? '');
}

/** Returns the body rows of the items table, top to bottom. */
function bodyRows(): HTMLElement[] {
  return within(screen.getByRole('table'))
    .getAllByRole('row')
    .filter((rowElement) => cellTexts(rowElement).length > 0);
}

/** Returns the body row whose Code cell is `code`, or null when the table shows none; throws when several do. */
function queryRowByCode(code: string): HTMLElement | null {
  const matches = bodyRows().filter((rowElement) => cellTexts(rowElement)[CODE_COLUMN] === code);
  if (matches.length > 1) {
    throw new Error(`The items table shows ${matches.length} rows with the code ${code}`);
  }
  return matches[0] ?? null;
}

/** Waits for the body row whose Code cell is `code`, and fails naming the code when the table never shows it. */
function findRowByCode(code: string): Promise<HTMLElement> {
  return waitFor(() => {
    const rowElement = queryRowByCode(code);
    if (rowElement === null) {
      throw new Error(`The items table shows no row with the code ${code}`);
    }
    return rowElement;
  }, ASYNC_TIMEOUT);
}

/** Returns the element around the items table that carries the search's `aria-busy` state. */
function tableArea(): HTMLElement {
  const area = screen.getByRole('table').closest<HTMLElement>('[aria-busy]');
  if (area === null) {
    throw new Error('The items table has no aria-busy container');
  }
  return area;
}

/** Waits until no item search is outstanding. */
async function searchFinished(): Promise<void> {
  await waitFor(() => {
    expect(tableArea()).toHaveAttribute('aria-busy', 'false');
  }, ASYNC_TIMEOUT);
}

/** Returns the Agent filter selector. */
function agentSelect(): HTMLSelectElement {
  return screen.getByLabelText<HTMLSelectElement>(msg('Agent'));
}

/** Returns the button captioned `caption`. */
function button(caption: string): HTMLElement {
  return screen.getByRole('button', { name: caption });
}

/** Renders the items list in the fixture's session and returns the user-event instance driving it. */
function renderPage(fixture: FixtureHandle): UserEvent {
  const user = userEvent.setup();
  renderWithSession(<ItemsListPage />, fixture);
  return user;
}

describe('ItemsListPage', () => {
  const fixture = useFixture('AdminGuards');

  it('fixes the agent filter to the session agent', async () => {
    const ownCodes = await itemCodes(fixture, ['retailItem', 'soldRetailItem']);
    const otherCodes = await itemCodes(fixture, ['otherRetailItem', 'otherSoldRetailItem']);
    const user = renderPage(fixture);

    const select = agentSelect();
    await waitFor(() => {
      expect(select).toHaveDisplayValue(SESSION_AGENT_NAME);
    }, ASYNC_TIMEOUT);
    expect(Array.from(select.options, (option) => option.text)).toContain(OTHER_AGENT_NAME);
    expect(select).toBeDisabled();

    await user.click(button(CLEAR_FILTERS_CAPTION));

    expect(select).toHaveDisplayValue(SESSION_AGENT_NAME);
    expect(select).toBeDisabled();

    await user.click(button(APPLY_FILTER_CAPTION));
    await searchFinished();

    for (const code of ownCodes) {
      expect(queryRowByCode(code)).not.toBeNull();
    }
    for (const code of otherCodes) {
      expect(queryRowByCode(code)).toBeNull();
    }
    const agentCells = bodyRows().map((rowElement) => cellTexts(rowElement)[AGENT_COLUMN]);
    expect(agentCells.length).toBeGreaterThanOrEqual(ownCodes.length);
    for (const agentName of agentCells) {
      expect(agentName).toBe(SESSION_AGENT_NAME);
    }
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('disables Delete for a sold item and keeps Edit enabled', async () => {
    const [unsoldCode, soldCode] = await itemCodes(fixture, ['retailItem', 'soldRetailItem']);
    const user = renderPage(fixture);

    const soldRow = await findRowByCode(soldCode);
    expect(button(EDIT_CAPTION)).toHaveAttribute('aria-disabled', 'true');
    expect(button(DELETE_CAPTION)).toHaveAttribute('aria-disabled', 'true');

    await user.click(soldRow);

    expect(soldRow).toHaveAttribute('aria-selected', 'true');
    expect(button(DELETE_CAPTION)).toHaveAttribute('aria-disabled', 'true');
    expect(button(EDIT_CAPTION)).not.toHaveAttribute('aria-disabled');

    const unsoldRow = await findRowByCode(unsoldCode);
    await user.click(unsoldRow);

    expect(unsoldRow).toHaveAttribute('aria-selected', 'true');
    expect(soldRow).toHaveAttribute('aria-selected', 'false');
    expect(button(DELETE_CAPTION)).not.toHaveAttribute('aria-disabled');
    expect(button(EDIT_CAPTION)).not.toHaveAttribute('aria-disabled');
  });

  it('disables Delete for another agent item in an agent session', async () => {
    const otherItem = await itemOf(fixtureId(fixture, 'otherRetailItem'));
    const ownItem = await itemOf(fixtureId(fixture, 'retailItem'));

    expect(fixture.session.role).toBe('agent');
    expect(otherItem.isSold).toBe(false);
    expect(otherItem.agentId).toBe(fixtureId(fixture, 'otherAgent'));
    expect(ownItem.isSold).toBe(false);
    expect(ownItem.agentId).toBe(fixture.session.agentId);

    expect(itemDeleteEnabled(otherItem, fixture.session)).toBe(false);
    expect(itemDeleteEnabled(ownItem, fixture.session)).toBe(true);
  });

  it('styles sold and closed rows', async () => {
    const appearances: readonly RowAppearance[] = [
      { key: 'soldRetailItem', rowStyle: 'sold', status: msg('Sold') },
      { key: 'soldTenderItem', rowStyle: 'sold', status: msg('Sold') },
      { key: 'closedTenderItem', rowStyle: 'closed', status: msg('Closed') },
      { key: 'retailItem', rowStyle: undefined, status: '' },
    ];
    const expectedRows = await Promise.all(
      appearances.map(async (appearance) => ({
        ...appearance,
        code: await itemCode(fixtureId(fixture, appearance.key)),
      })),
    );
    renderPage(fixture);

    for (const appearance of expectedRows) {
      const rowElement = await findRowByCode(appearance.code);
      if (appearance.rowStyle === undefined) {
        expect(rowElement).not.toHaveAttribute('data-row-style');
      } else {
        expect(rowElement).toHaveAttribute('data-row-style', appearance.rowStyle);
      }
      expect(cellTexts(rowElement)[STATUS_COLUMN]).toBe(appearance.status);
    }
  });
});
