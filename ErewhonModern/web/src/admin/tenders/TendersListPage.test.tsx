// Component tests of the admin tenders list against the fixture bridge: the View and Delete buttons of an agent's and
// the administrator's session, the list's Delete flow, and the row styles and status words of the listed tenders.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { ApiError, api } from '../../api/client';
import type { AdminTenderSearchRequest, Id, TenderDetailsResponse, TenderListRow } from '../../api/types';
import { currencyFormat } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { TendersListPage } from './TendersListPage';

const TENDER_SEARCH_PATH = '/api/app/admin/tenders/search';
const TENDERS_PATH = '/api/app/admin/tenders';

const VIEW_CAPTION = 'View';
const DELETE_CAPTION = 'Delete';
const APPLY_FILTER_CAPTION = 'Apply Filter';
const STATUS_CHECK_BOX_CAPTIONS: readonly string[] = ['Open Tenders', 'Closed Tenders', 'Successful Tenders'];

const STATUS_COLUMN = 0;

/** Search of every tender of the company, whatever its status. */
const ALL_TENDERS_REQUEST: AdminTenderSearchRequest = {
  agentId: null,
  clientId: null,
  itemId: null,
  includeOpenTenders: true,
  includeClosedTenders: true,
  includeSuccessfulTenders: true,
};

/** Returns the id the fixture recorded under `key`, and throws when the fixture returned none. */
function fixtureId(fixture: FixtureHandle, key: keyof FixtureIds): Id {
  const id = fixture.ids[key];
  if (id === undefined) {
    throw new Error(`The AdminGuards fixture returned no ${key} id`);
  }
  return id;
}

/** Returns the rows of POST /api/app/admin/tenders/search with every status included. */
function allRows(): Promise<TenderListRow[]> {
  return api.post<TenderListRow[]>(TENDER_SEARCH_PATH, ALL_TENDERS_REQUEST);
}

/** Returns the row of `rows` for the tender `tenderId`, and throws when the list holds none. */
function rowOf(rows: readonly TenderListRow[], tenderId: Id): TenderListRow {
  const row = rows.find((candidate) => candidate.id === tenderId);
  if (row === undefined) {
    throw new Error(`The tender search returned no row for tender ${tenderId}`);
  }
  return row;
}

/** Returns the row of `rows` whose tender won its item, and throws when the list holds none. */
function winningRow(rows: readonly TenderListRow[]): TenderListRow {
  const row = rows.find((candidate) => candidate.isWinning);
  if (row === undefined) {
    throw new Error('The tender search returned no winning tender');
  }
  return row;
}

/** Returns the answer of GET /api/app/admin/tenders/{id}. */
function tenderDetails(id: Id): Promise<TenderDetailsResponse> {
  return api.get<TenderDetailsResponse>(`${TENDERS_PATH}/${id}`);
}

/** Resolves to the rejection of GET /api/app/admin/tenders/{id}, or to undefined when the tender is found. */
function lookupFailure(id: Id): Promise<unknown> {
  return tenderDetails(id).then(
    () => undefined,
    (error: unknown) => error,
  );
}

/** Returns the texts of the cells a rendered row shows for `row`'s client, tendered item and offer. */
function identifyingTexts(row: TenderListRow): string[] {
  return [row.clientName, `${row.itemCode ?? ''} : ${row.itemName}`, currencyFormat(row.offer)];
}

/** Returns the cell texts of the table row `rowElement`, left to right. */
function cellTexts(rowElement: HTMLElement): string[] {
  return within(rowElement)
    .queryAllByRole('cell')
    .map((cell) => cell.textContent ?? '');
}

/** Returns the element around the tenders table that carries the search's `aria-busy` state. */
function tableArea(): HTMLElement {
  const area = screen.getByRole('table').closest<HTMLElement>('[aria-busy]');
  if (area === null) {
    throw new Error('The tenders table has no aria-busy container');
  }
  return area;
}

/** Returns the body rows of the tenders table, top to bottom. */
function bodyRows(): HTMLElement[] {
  return within(screen.getByRole('table'))
    .getAllByRole('row')
    .filter((rowElement) => cellTexts(rowElement).length > 0);
}

/**
 * Returns the body row whose cells hold the client, "<code> : <name>" and offer texts of `row`, or null when the table
 * shows none; throws when more than one row holds them.
 */
function queryRenderedRow(row: TenderListRow): HTMLElement | null {
  const expected = identifyingTexts(row);
  const matches = bodyRows().filter((rowElement) => {
    const cells = cellTexts(rowElement);
    return expected.every((text) => cells.includes(text));
  });
  if (matches.length > 1) {
    throw new Error(`The tenders table shows ${matches.length} rows for ${expected.join(', ')}`);
  }
  return matches[0] ?? null;
}

/** Waits for the body row showing `row`, and fails naming the tender when the table never shows it. */
function findRenderedRow(row: TenderListRow): Promise<HTMLElement> {
  return waitFor(() => {
    const rowElement = queryRenderedRow(row);
    if (rowElement === null) {
      throw new Error(`The tenders table shows no row for tender ${row.id} (${identifyingTexts(row).join(', ')})`);
    }
    return rowElement;
  });
}

/** Returns the text of the Status cell of the table row `rowElement`. */
function statusText(rowElement: HTMLElement): string {
  return cellTexts(rowElement)[STATUS_COLUMN] ?? '';
}

/** Returns the button captioned `caption`. */
function button(caption: string): HTMLElement {
  return screen.getByRole('button', { name: caption });
}

/** Returns the text of the element that describes `dialog`, and throws when it has none. */
function descriptionText(dialog: HTMLElement): string {
  const descriptionId = dialog.getAttribute('aria-describedby');
  const description = descriptionId === null ? null : document.getElementById(descriptionId);
  if (description === null) {
    throw new Error('The message box has no description element');
  }
  return description.textContent ?? '';
}

/** Renders the tenders list in the fixture's session and returns the user-event instance driving it. */
function renderPage(fixture: FixtureHandle): UserEvent {
  const user = userEvent.setup();
  renderWithSession(<TendersListPage />, fixture);
  return user;
}

/** Sets the Open, Closed and Successful Tenders check boxes to `checked`, then clicks Apply Filter. */
async function applyStatusFilter(user: UserEvent, checked: boolean): Promise<void> {
  for (const caption of STATUS_CHECK_BOX_CAPTIONS) {
    const checkBox = screen.getByRole<HTMLInputElement>('checkbox', { name: caption });
    if (checkBox.checked !== checked) {
      await user.click(checkBox);
    }
    expect(checkBox.checked).toBe(checked);
  }
  await user.click(button(APPLY_FILTER_CAPTION));
}

describe('TendersListPage', () => {
  describe('agent session', () => {
    const fixture = useFixture('AdminGuards', { session: 'agent' });

    it('enables View but leaves Delete disabled when a tender row is clicked', async () => {
      const open = rowOf(await allRows(), fixtureId(fixture, 'openTender'));
      const user = renderPage(fixture);

      const rowElement = await findRenderedRow(open);
      expect(button(VIEW_CAPTION)).toHaveAttribute('aria-disabled', 'true');
      expect(button(DELETE_CAPTION)).toHaveAttribute('aria-disabled', 'true');

      await user.click(rowElement);

      expect(rowElement).toHaveAttribute('aria-selected', 'true');
      expect(button(VIEW_CAPTION)).not.toHaveAttribute('aria-disabled');
      expect(button(DELETE_CAPTION)).toHaveAttribute('aria-disabled', 'true');
    });

    it('keeps the selected tender and shows no message when Delete is clicked', async () => {
      const openId = fixtureId(fixture, 'openTender');
      const open = rowOf(await allRows(), openId);
      const user = renderPage(fixture);

      const rowElement = await findRenderedRow(open);
      await user.click(rowElement);
      await user.click(button(DELETE_CAPTION));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

      await user.click(button(APPLY_FILTER_CAPTION));
      await waitFor(() => {
        expect(tableArea()).toHaveAttribute('aria-busy', 'false');
      });

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      expect(queryRenderedRow(open)).toBe(rowElement);
      expect(rowElement).toHaveAttribute('aria-selected', 'true');
      await expect(tenderDetails(openId)).resolves.toMatchObject({ tender: { id: openId } });
    });
  });

  describe('administrator session', () => {
    const fixture = useFixture('AdminGuards', { session: 'administrator' });

    it('asks for a tender to be selected when Delete is clicked with no tender row selected', async () => {
      const open = rowOf(await allRows(), fixtureId(fixture, 'openTender'));
      const user = renderPage(fixture);

      await user.click(await findRenderedRow(open));
      expect(button(DELETE_CAPTION)).not.toHaveAttribute('aria-disabled');

      await applyStatusFilter(user, false);
      await waitFor(() => {
        expect(bodyRows()).toHaveLength(0);
      });
      expect(button(DELETE_CAPTION)).not.toHaveAttribute('aria-disabled');

      await user.click(button(DELETE_CAPTION));

      const dialog = await screen.findByRole('dialog', { name: msg('Error') });
      expect(descriptionText(dialog)).toBe(msg('SelectItemToDelete'));
    });

    it('refuses to delete the winning tender and keeps it', async () => {
      const winning = winningRow(await allRows());
      expect(winning.id).toBe(fixtureId(fixture, 'tender'));
      const user = renderPage(fixture);

      await applyStatusFilter(user, true);
      await user.click(await findRenderedRow(winning));
      expect(button(DELETE_CAPTION)).not.toHaveAttribute('aria-disabled');
      await user.click(button(DELETE_CAPTION));

      const dialog = await screen.findByRole('dialog', { name: msg('Error') });
      expect(descriptionText(dialog)).toBe(msg('CannotDeleteTenderItemSold'));
      await expect(tenderDetails(winning.id)).resolves.toMatchObject({
        tender: { id: winning.id },
        isWinning: true,
      });
    });

    it('deletes a tender on an unsold item at once, without a confirmation', async () => {
      const openId = fixtureId(fixture, 'openTender');
      const rows = await allRows();
      const open = rowOf(rows, openId);
      const otherOpenRows = rows.filter((row) => row.status === 'open' && row.id !== openId);
      const user = renderPage(fixture);

      await user.click(await findRenderedRow(open));
      await user.click(button(DELETE_CAPTION));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

      await waitFor(() => {
        expect(queryRenderedRow(open)).toBeNull();
      });
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      for (const other of otherOpenRows) {
        expect(queryRenderedRow(other)).not.toBeNull();
      }

      const failure = await lookupFailure(openId);
      expect(failure).toBeInstanceOf(ApiError);
      expect(failure).toMatchObject({ status: 404 });
    });

    it('colours the closed tender red and the winning tender green, and the open tender neither', async () => {
      const rows = await allRows();
      const closed = rowOf(rows, fixtureId(fixture, 'closedTender'));
      const winning = winningRow(rows);
      const open = rowOf(rows, fixtureId(fixture, 'openTender'));
      const user = renderPage(fixture);

      await applyStatusFilter(user, true);
      const closedElement = await findRenderedRow(closed);
      const winningElement = await findRenderedRow(winning);
      const openElement = await findRenderedRow(open);

      expect(closedElement).toHaveAttribute('data-row-style', 'closed');
      expect(closedElement.style.color).toBe('red');
      expect(winningElement).toHaveAttribute('data-row-style', 'tenderSale');
      expect(winningElement.style.color).toBe('green');
      expect(openElement).not.toHaveAttribute('data-row-style');
      expect(openElement.style.color).toBe('');
    });

    it('shows Closed, Successful and Open in the Status column', async () => {
      const rows = await allRows();
      const closed = rowOf(rows, fixtureId(fixture, 'closedTender'));
      const winning = winningRow(rows);
      const open = rowOf(rows, fixtureId(fixture, 'openTender'));
      expect([closed.status, winning.status, open.status]).toEqual(['closed', 'successful', 'open']);
      const user = renderPage(fixture);

      await applyStatusFilter(user, true);

      expect(statusText(await findRenderedRow(closed))).toBe('Closed');
      expect(statusText(await findRenderedRow(winning))).toBe('Successful');
      expect(statusText(await findRenderedRow(open))).toBe('Open');
    });
  });
});
