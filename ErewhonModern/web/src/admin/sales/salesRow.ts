// Sale display helpers of the sales list: ports of RetailSale/TenderSale display and makeRow.

import { currencyFormat, decimalString } from '../../format';
import { msg } from '../../messages/messages';

/** A sale as `display` reads it. */
export interface SaleDisplayInput {
  /** True for a tender sale, false for a retail sale. */
  isTender: boolean;
  clientName: string;
  /** Sale price of a retail sale, or the winning offer of a tender sale. */
  amount: number;
}

/**
 * Port of RetailSale.display and TenderSale.display: `$SoldToClient(client, amount currencyFormat)` for a
 * retail sale and `$SoldToClientFor(client, amount currencyFormat)` for a tender sale,
 * e.g. "Sold to X for $555.00".
 */
export function display(row: SaleDisplayInput): string {
  const amount = currencyFormat(row.amount);
  return row.isTender
    ? msg('SoldToClientFor', row.clientName, amount)
    : msg('SoldToClient', row.clientName, amount);
}

/** Running retail and tender totals of a sales list. */
export interface MakeRowTotals {
  retailTotal: number;
  tenderTotal: number;
}

/** A sale as `makeRow` reads it; a `SalesRowProjection` can be passed as is. */
export interface MakeRowSale {
  /** True for a tender sale, false for a retail sale. */
  isTender: boolean;
  /** Sale price of a retail sale; null or absent for a tender sale. */
  salePrice?: number | null;
  /** Winning offer of a tender sale; null or absent for a retail sale. */
  offer?: number | null;
}

/** The row text and totals after `makeRow`. */
export interface MakeRowResult extends MakeRowTotals {
  rowString: string;
}

/** Returns the sum of two amounts held to 2 decimal places, computed in whole cents. */
function add(a: number, b: number): number {
  return (Math.round(a * 100) + Math.round(b * 100)) / 100;
}

/**
 * Port of RetailSale.makeRow and TenderSale.makeRow, returning the updated row text and totals as a new
 * object and leaving its arguments unchanged. A retail sale appends a Tab to the row and adds its sale price
 * to the retail total. A tender sale appends a Tab and its offer with 2 decimal places, e.g. "500.00", and
 * adds the offer to the tender total. A null or absent amount counts as 0.
 */
export function makeRow(rowString: string, totals: MakeRowTotals, sale: MakeRowSale): MakeRowResult {
  if (sale.isTender) {
    const offer = sale.offer ?? 0;
    return {
      rowString: `${rowString}\t${decimalString(offer)}`,
      retailTotal: totals.retailTotal,
      tenderTotal: add(totals.tenderTotal, offer),
    };
  }
  return {
    rowString: `${rowString}\t`,
    retailTotal: add(totals.retailTotal, sale.salePrice ?? 0),
    tenderTotal: totals.tenderTotal,
  };
}
