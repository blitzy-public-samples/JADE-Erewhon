// Component tests of the admin sales list against the fixture bridge: the TestViewSale ports of the sale display text
// and makeRow, the company view's cells and totals, and the agent session's fixed Agent option and own-item rows.

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { api } from '../../api/client';
import type { Id, ItemDetailsResponse, SalesListResponse, SalesRowProjection } from '../../api/types';
import { currencyFormat, decimalString, shortFormat } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { SalesListPage } from './SalesListPage';
import { display, makeRow } from './salesRow';

const COMPANY_SALES_PATH = '/api/app/admin/sales?showFor=company';

const TOTAL_RETAIL_CAPTION = 'Total of Retail Sales:';
const TOTAL_TENDER_CAPTION = 'Total of Tenders:';
const TOTAL_COMMISSION_CAPTION = 'Total of Commission:';

const PRODUCT_COLUMN = 1;

/** Returns the id the fixture recorded under `key`, and throws when the fixture returned none. */
function fixtureId(fixture: FixtureHandle, key: keyof FixtureIds): Id {
  const id = fixture.ids[key];
  if (id === undefined) {
    throw new Error(`The fixture returned no ${key} id`);
  }
  return id;
}

/** Returns the row of `rows` for the sale `saleId`, and throws when the list holds none. */
function saleRow(rows: readonly SalesRowProjection[], saleId: Id): SalesRowProjection {
  const row = rows.find((candidate) => candidate.saleId === saleId);
  if (row === undefined) {
    throw new Error(`The sales list holds no row for sale ${saleId}`);
  }
  return row;
}

/** Returns the Product cell text the sales list shows for `row`: "<code> - <name>". */
function productOfRow(row: SalesRowProjection): string {
  return `${row.itemCode ?? ''} - ${row.itemName}`;
}

/** Reads the item `id` through GET /api/app/items/{id} and returns its "<code> - <name>" text. */
async function productOfItem(id: Id): Promise<string> {
  const details = await api.get<ItemDetailsResponse>(`/api/app/items/${id}`);
  return `${details.item.code ?? ''} - ${details.item.name}`;
}

/** Returns the cell texts of each body row of `table`, top to bottom. */
function bodyRows(table: HTMLElement): string[][] {
  return within(table)
    .getAllByRole('row')
    .map((row) => within(row).queryAllByRole('cell').map((cell) => cell.textContent ?? ''))
    .filter((cells) => cells.length > 0);
}

/** Returns the cell texts of the body row of `table` whose Product cell is `product`, and throws when none is. */
function bodyRowOf(table: HTMLElement, product: string): string[] {
  const cells = bodyRows(table).find((row) => row[PRODUCT_COLUMN] === product);
  if (cells === undefined) {
    throw new Error(`The sales table shows no row for ${product}`);
  }
  return cells;
}

/** Returns the Product cell texts of the body rows of `table`, sorted. */
function productCells(table: HTMLElement): string[] {
  return bodyRows(table)
    .map((row) => row[PRODUCT_COLUMN] ?? '')
    .sort();
}

/** Returns the text of the total labelled `caption`. */
function totalText(caption: string): string {
  return screen.getByRole('status', { name: caption }).textContent ?? '';
}

/** Returns the Company option, whose caption is "Company" followed by the company name once loaded. */
function companyRadio(): HTMLElement {
  return screen.getByRole('radio', { name: (name) => name.startsWith(msg('Company')) });
}

describe('TestViewSale', () => {
  const fixture = useFixture('TestViewSale', { session: 'administrator' });

  /** Reads the company sales list and returns the rows of the fixture's retail sale and tender sale. */
  async function loadRows(): Promise<{ retail: SalesRowProjection; tender: SalesRowProjection }> {
    const { rows } = await api.get<SalesListResponse>(COMPANY_SALES_PATH);
    return {
      retail: saleRow(rows, fixtureId(fixture, 'retailSale')),
      tender: saleRow(rows, fixtureId(fixture, 'tenderSale')),
    };
  }

  it('testDisplayRetail', async () => {
    const { retail } = await loadRows();

    expect(retail.salePrice).toBe(555);
    expect(display({ isTender: false, clientName: retail.clientName, amount: retail.salePrice! })).toBe(
      msg('SoldToClient', retail.clientName, currencyFormat(retail.salePrice!)),
    );
  });

  it('testDisplayTender', async () => {
    const { retail, tender } = await loadRows();

    expect(tender.offer).toBe(500);
    expect(display({ isTender: true, clientName: tender.clientName, amount: tender.offer! })).toBe(
      msg('SoldToClient', retail.clientName, currencyFormat(tender.offer!)),
    );
  });

  it('testMakeRow', async () => {
    const { retail, tender } = await loadRows();

    const a = makeRow('<<Dummy Table>>', { retailTotal: 9000, tenderTotal: 3.5 }, retail);
    expect(a.rowString).toBe('<<Dummy Table>>\t');
    expect(a.retailTotal).toBe(9555);
    expect(a.tenderTotal).toBe(3.5);

    const b = makeRow(a.rowString, a, tender);
    expect(b.rowString).toBe('<<Dummy Table>>\t\t500.00');
    expect(b.retailTotal).toBe(9555);
    expect(b.tenderTotal).toBe(503.5);
  });
});

describe('SalesListPage', () => {
  describe('as the company user', () => {
    const fixture = useFixture('TestViewSale', { session: 'administrator' });

    it('company view lists rows with makeRow cells and totals', async () => {
      const response = await api.get<SalesListResponse>(COMPANY_SALES_PATH);
      const retail = saleRow(response.rows, fixtureId(fixture, 'retailSale'));
      const tender = saleRow(response.rows, fixtureId(fixture, 'tenderSale'));
      renderWithSession(<SalesListPage />, fixture);

      const table = await screen.findByRole('table', { name: msg('Sales') });
      await waitFor(() => {
        expect(bodyRows(table)).toHaveLength(response.rows.length);
      });

      expect(companyRadio()).toBeChecked();
      expect(companyRadio()).toBeEnabled();
      expect(screen.getByRole('radio', { name: msg('Agent') })).not.toBeChecked();
      expect(screen.getByRole('radio', { name: msg('Client') })).not.toBeChecked();

      expect(
        within(table)
          .getAllByRole('columnheader')
          .map((header) => header.textContent),
      ).toEqual([msg('Name'), msg('Product'), msg('Price'), msg('DateSold'), msg('Bid'), msg('Commission')]);

      const retailCells = bodyRowOf(table, productOfRow(retail));
      expect(retailCells).toEqual([
        retail.clientName,
        productOfRow(retail),
        decimalString(retail.itemPrice),
        shortFormat(retail.saleDate),
        '',
        decimalString(retail.agentCommission),
      ]);
      expect(retailCells[2]).toBe('5.50');

      const tenderCells = bodyRowOf(table, productOfRow(tender));
      expect(tenderCells).toEqual([
        tender.clientName,
        productOfRow(tender),
        decimalString(tender.itemPrice),
        shortFormat(tender.saleDate),
        '500.00',
        decimalString(tender.agentCommission),
      ]);

      expect(currencyFormat(response.totals.retailTotal)).toBe('$555.00');
      expect(currencyFormat(response.totals.tenderTotal)).toBe('$500.00');
      expect(totalText(TOTAL_RETAIL_CAPTION)).toBe(currencyFormat(response.totals.retailTotal));
      expect(totalText(TOTAL_TENDER_CAPTION)).toBe(currencyFormat(response.totals.tenderTotal));
      expect(totalText(TOTAL_COMMISSION_CAPTION)).toBe(currencyFormat(response.totals.commissionTotal));
    });
  });

  describe('as an agent', () => {
    const fixture = useFixture('AdminGuards', { session: 'agent' });

    it('agent session fixes the agent option and lists own items only', async () => {
      const ownProducts = [
        await productOfItem(fixtureId(fixture, 'soldRetailItem')),
        await productOfItem(fixtureId(fixture, 'soldTenderItem')),
      ].sort();
      const otherProduct = await productOfItem(fixtureId(fixture, 'otherSoldRetailItem'));
      const user = userEvent.setup();
      renderWithSession(<SalesListPage />, fixture);

      const table = await screen.findByRole('table', { name: msg('Sales') });
      await waitFor(() => {
        expect(productCells(table)).toEqual(ownProducts);
      });
      expect(productCells(table)).not.toContain(otherProduct);

      const agentRadio = screen.getByRole('radio', { name: msg('Agent') });
      const clientRadio = screen.getByRole('radio', { name: msg('Client') });
      const agentSelect = screen.getByRole('combobox', { name: msg('Agent') });
      const clientSelect = screen.getByRole('combobox', { name: msg('Client') });

      expect(agentRadio).toBeChecked();
      expect(companyRadio()).toBeDisabled();
      expect(agentSelect).toBeDisabled();
      await waitFor(() => {
        expect(agentSelect).toHaveDisplayValue(/^John Smith, /);
      });
      expect(agentSelect).toHaveValue(String(fixtureId(fixture, 'agent')));
      expect(clientRadio).toBeEnabled();
      expect(clientSelect).toBeEnabled();

      await user.click(clientRadio);

      expect(clientRadio).toBeChecked();
      expect(bodyRows(table)).toHaveLength(0);

      const clientId = String(fixtureId(fixture, 'client'));
      await within(clientSelect).findByRole('option', { name: /^Clive Entworth, / });
      await user.selectOptions(clientSelect, clientId);

      expect(clientSelect).toHaveValue(clientId);
      await waitFor(() => {
        expect(productCells(table)).toEqual(ownProducts);
      });
      expect(productCells(table)).not.toContain(otherProduct);
      expect(clientRadio).toBeChecked();
      expect(agentSelect).toBeDisabled();
      expect(companyRadio()).toBeDisabled();
    });
  });
});
