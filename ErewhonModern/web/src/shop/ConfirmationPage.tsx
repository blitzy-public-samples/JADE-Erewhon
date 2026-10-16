import { useId } from 'react';
import type { CSSProperties } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import type { CheckoutReceipt, CheckoutReceiptLine } from '../api/types';
import { DataTable } from '../components/DataTable';
import type { DataTableColumn } from '../components/DataTable';
import { currencyFormat } from '../format';
import { msg } from '../messages/messages';

/** One body row of the confirmation table: a receipt line and its position in the receipt. */
interface ConfirmationRow {
  index: number;
  line: CheckoutReceiptLine;
}

const PAGE_CAPTION = 'Erewhon Investments Inc - Confirmation';
const CONTINUE_CAPTION = 'Continue';
const SHOP_PATH = '/shop';

const PAGE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1rem',
  boxSizing: 'border-box',
  maxInlineSize: '50rem',
  marginInline: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1.25rem',
  fontWeight: 'bold',
};

const MESSAGE_STYLE: CSSProperties = {
  margin: 0,
  whiteSpace: 'pre-wrap',
  overflowWrap: 'break-word',
};

const PLINTH_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  padding: '0.5rem',
  background: 'rgb(255, 255, 128)',
};

const TABLE_AREA_STYLE: CSSProperties = {
  overflowX: 'auto',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
};

const BUTTON_STYLE: CSSProperties = {
  minInlineSize: '4.1875rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.75rem',
};

const COLUMNS: DataTableColumn<ConfirmationRow>[] = [
  { key: 'transaction', header: msg('Transaction'), render: (row) => row.line.transaction },
  { key: 'code', header: msg('Code'), render: (row) => row.line.code },
  { key: 'product', header: msg('Product'), render: (row) => row.line.itemName },
  { key: 'price', header: msg('Price'), render: (row) => currencyFormat(row.line.itemPrice) },
  { key: 'bid', header: msg('Bid'), render: (row) => currencyFormat(row.line.offer) },
];

/** Returns the receipt line's position in the receipt, which keys its table row. */
function rowKey(row: ConfirmationRow): number {
  return row.index;
}

/**
 * Purchase confirmation of a successful checkout. Shows the checkout receipt carried in the router
 * location state as `{ receipt }`: one table row per receipt line, in receipt order, with its
 * transaction, code, product, item price and offer, both amounts in currency format. Without a
 * receipt the table shows its header row only. The instruction line reads `$ConfirmationInstructions`
 * and the footer `$PurchaseConfirmation`. Continue navigates to `/shop`.
 */
export function ConfirmationPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const headingId = useId();
  const receipt = (location.state as { receipt?: CheckoutReceipt } | null)?.receipt;
  const rows: ConfirmationRow[] = (receipt?.lines ?? []).map((line, index) => ({ index, line }));

  return (
    <main style={PAGE_STYLE}>
      <h1 id={headingId} style={HEADING_STYLE}>
        {PAGE_CAPTION}
      </h1>
      <p style={MESSAGE_STYLE}>{msg('ConfirmationInstructions')}</p>
      <div style={PLINTH_STYLE}>
        <div role="region" aria-labelledby={headingId} style={TABLE_AREA_STYLE}>
          <DataTable columns={COLUMNS} rows={rows} rowKey={rowKey} />
        </div>
        <div style={BUTTON_ROW_STYLE}>
          <button type="button" style={BUTTON_STYLE} onClick={() => navigate(SHOP_PATH)}>
            {CONTINUE_CAPTION}
          </button>
        </div>
      </div>
      <footer>
        <p style={MESSAGE_STYLE}>{msg('PurchaseConfirmation')}</p>
      </footer>
    </main>
  );
}
