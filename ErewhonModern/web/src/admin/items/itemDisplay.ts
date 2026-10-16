// Display text of retail and tender items: list entries, list headers and shop search-result rows.

import type { ItemProjection } from '../../api/types';
import { currencyFormat, shortFormat } from '../../format';
import { msg } from '../../messages/messages';

/** Kind of an item: retail or tender. */
export type ItemKind = 'retail' | 'tender';

/** Values of an item that its list entry is built from. */
export interface ItemDisplayInput {
  isTender: boolean;
  /** Category prefix followed by the code number; null when the item has no category. */
  code: string | null;
  name: string;
  /** Retail price, or the reserve price of a tender item. */
  price: number;
  /**
   * Buyer and amount of the item's sale, the sale price of a retail sale or the winning offer of a tender sale;
   * null when the item is unsold.
   */
  sold: null | { clientName: string; amount: number };
  /** True for a tender item holding at least one tender. */
  hasTenders: boolean;
}

/** Values of an item that its search-result row is built from. */
export interface SearchResultRow {
  isTender: boolean;
  /** Category prefix followed by the code number; null when the item has no category. */
  code: string | null;
  name: string;
  /** Listed date, `yyyy-MM-dd`. */
  listedDate: string;
  /** Retail price, or the reserve price of a tender item. */
  price: number;
}

const SPACE = ' ';
const NAME_SEPARATOR = ': ';
const STATUS_SEPARATOR = ' - ';
const CELL_SEPARATOR = '\t';

/**
 * Returns the list entry of an item, `<code> <name>: ` followed by:
 * - an unsold retail item: `<price> - Unsold`;
 * - a sold retail item: `Sold to <client> for <sale price>`;
 * - a tender item: `<reserve price> - ` and then `Sold to <client> for <winning offer>` when sold, otherwise
 *   `Tendered` or `Untendered` by whether it holds tenders.
 *
 * Amounts are in `currencyFormat`, and a null code is written as `''`.
 */
export function itemDisplay(item: ItemDisplayInput): string {
  const prefix = `${item.code ?? ''}${SPACE}${item.name}${NAME_SEPARATOR}`;

  if (!item.isTender) {
    return item.sold === null
      ? prefix + currencyFormat(item.price) + STATUS_SEPARATOR + msg('Unsold')
      : prefix + msg('SoldToClient', item.sold.clientName, currencyFormat(item.sold.amount));
  }

  let status: string;
  if (item.sold !== null) {
    status = msg('SoldToClientFor', item.sold.clientName, currencyFormat(item.sold.amount));
  } else {
    status = item.hasTenders ? msg('Tendered') : msg('Untendered');
  }
  return prefix + currencyFormat(item.price) + STATUS_SEPARATOR + status;
}

/** Returns the header listing items of the kind: `Items for Sale` for retail, `Items for Tender` for tender. */
export function getHeader(kind: ItemKind): string {
  return kind === 'tender' ? msg('ItemsForTender') : msg('ItemsForSale');
}

/**
 * Returns true only when the text equals the header of the kind exactly; null, undefined and texts differing in
 * case or spacing give false.
 */
export function verifyHeader(kind: ItemKind, text: string | null | undefined): boolean {
  return text === getHeader(kind);
}

/**
 * Returns the cells of a search-result row: `Bid` for a tender item or `Buy` for a retail item, the code (`''`
 * when null), the name, the listed date in `shortFormat` and the price in `currencyFormat`.
 */
export function getSearchResultCells(row: SearchResultRow): string[] {
  return [
    row.isTender ? msg('Bid') : msg('Buy'),
    row.code ?? '',
    row.name,
    shortFormat(row.listedDate),
    currencyFormat(row.price),
  ];
}

/** Returns the cells of {@link getSearchResultCells} joined by tab characters. */
export function getSearchResultString(row: SearchResultRow): string {
  return getSearchResultCells(row).join(CELL_SEPARATOR);
}

/** Returns the item of a displayed row, which is the row itself. */
export function getItem<T>(row: T): T {
  return row;
}

/** Returns `'tender'` for a tender item and `'retail'` for a retail item. */
export function itemKind(item: { isTender: boolean }): ItemKind {
  return item.isTender ? 'tender' : 'retail';
}

/**
 * Returns the display input of an item read model: its kind, code, name, price and tender flag, and, when it
 * has a sale, the sale's client name with the retail sale price or the winning tender offer (0 when absent).
 */
export function itemDisplayInput(item: ItemProjection): ItemDisplayInput {
  return {
    isTender: item.isTender,
    code: item.code,
    name: item.name,
    price: item.price,
    sold: item.sale
      ? {
          clientName: item.sale.client.name,
          amount: item.isTender ? (item.sale.offer ?? 0) : (item.sale.price ?? 0),
        }
      : null,
    hasTenders: item.hasTenders,
  };
}
