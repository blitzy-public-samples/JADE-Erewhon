// Shop checkout screen: lists the lines of the session cart with the cart total, removes the selected line, empties
// the cart, checks the cart out to the confirmation page and follows the deletion of a carted item by another user.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, ReactElement } from 'react';
import { useNavigate } from 'react-router-dom';
import { ErrorOutcomeError, UnauthorizedError, api } from '../api/client';
import { on } from '../api/realtime';
import type {
  CartLineView,
  CartOperationResponse,
  CartView,
  CheckoutResponse,
  RemoveCartItemRequest,
} from '../api/types';
import { DataTable } from '../components/DataTable';
import type { DataTableColumn } from '../components/DataTable';
import { displayErrorsOptions, fullErrorText } from '../components/ErrorList';
import { DialogButton, useMessageBox } from '../components/MessageBox';
import { currencyFormat } from '../format';
import { msg } from '../messages/messages';

const CART_PATH = '/api/app/shop/cart';
const REMOVE_PATH = '/api/app/shop/cart/remove';
const EMPTY_PATH = '/api/app/shop/cart/empty';
const CHECKOUT_PATH = '/api/app/shop/checkout';
const SHOP_PATH = '/shop';
const CONFIRMATION_PATH = '/shop/confirmation';

const PAGE_CAPTION = 'Erewhon Investments Inc - Checkout';
const TOTAL_CAPTION = 'Total:';
const PROCEED_CAPTION = 'Proceed';
const REMOVE_CAPTION = 'Remove';
const EMPTY_CAPTION = 'Empty';
const BACK_CAPTION = 'Back';

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

const FOOTER_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: '0.5rem',
};

const TOTAL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  gap: '0.5rem',
  margin: 0,
};

const TOTAL_LABEL_STYLE: CSSProperties = {
  fontWeight: 'bold',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.1875rem',
  marginInlineStart: 'auto',
};

const COLUMNS: DataTableColumn<CartLineView>[] = [
  { key: 'transaction', header: msg('Transaction'), render: (line) => line.transaction },
  { key: 'code', header: msg('Code'), render: (line) => line.code },
  { key: 'product', header: msg('Product'), render: (line) => line.itemName },
  { key: 'price', header: msg('Price'), render: (line) => currencyFormat(line.itemPrice) },
  { key: 'bid', header: msg('Bid'), render: (line) => currencyFormat(line.offer) },
];

/** Returns the item id of the cart line, which keys its table row. */
function lineKey(line: CartLineView): number {
  return line.itemId;
}

/**
 * Checkout screen of the shop area.
 * - On mount it sets the document title to "Erewhon Investments Inc - Checkout", restoring the previous title on
 *   unmount, and reads the session cart through GET /api/app/shop/cart.
 * - It shows the caption as the page heading, the `$CheckoutInstructions` text, one table row per cart line with its
 *   transaction, code, product, item price and offer, both amounts in currency format, the cart total after
 *   "Total:", and the Proceed, Remove, Empty and Back buttons. Clicking a row selects its line. The buttons are enabled
 *   whatever the cart holds; Proceed has `aria-disabled` and ignores clicks while a checkout request is pending.
 * - Remove sends POST /api/app/shop/cart/remove with the selected line's item id, or null with no line selected, and
 *   shows the cart it returns. A rejected remove shows its error text titled `$UnableToRemoveItemFromCart`; a
 *   successful one clears the selection. The page stays.
 * - Empty sends POST /api/app/shop/cart/empty, shows the emptied cart it returns, clears the selection and stays on
 *   the page.
 * - Back navigates to `/shop`.
 * - Proceed sends POST /api/app/shop/checkout. A successful checkout navigates to `/shop/confirmation` with the
 *   returned receipt as the location state `{ receipt }`. A rejected checkout shows its full error text titled
 *   `$CheckoutUnsuccessful` with the error icon, then reads the cart again. A failed request passes an error outcome
 *   or an HTTP 401 to `showError` and shows any other failure as its message titled `$Error` with the error icon;
 *   the cart is then read again unless the failure was an HTTP 401.
 * - A failed cart read, remove or empty request is reported the same way.
 * - A CartItemDeleted event shows `$AnItemInYourCartWasDeletedByAnotherUser` titled `$DeletedItem`, then reads the
 *   cart again, and navigates to `/shop` when the cart holds no lines.
 * - A cart response is shown only while it answers the latest cart request the page sent, and a selected line that
 *   the shown cart no longer holds is deselected.
 */
export function CheckoutPage(): ReactElement {
  const navigate = useNavigate();
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();
  const [cart, setCart] = useState<CartView | null>(null);
  const [selectedItemId, setSelectedItemId] = useState<number | null>(null);
  const [proceeding, setProceeding] = useState(false);
  const mountedRef = useRef(false);
  const proceedingRef = useRef(false);
  const cartRequestRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    const previousTitle = document.title;
    document.title = PAGE_CAPTION;
    return () => {
      document.title = previousTitle;
    };
  }, []);

  const reportFailure = useCallback(
    async (error: unknown): Promise<void> => {
      if (error instanceof ErrorOutcomeError || error instanceof UnauthorizedError) {
        await showError(error);
        return;
      }

      await show({
        title: msg('Error'),
        message: error instanceof Error ? error.message : String(error),
        icon: 'error',
        buttons: 'ok',
      });
    },
    [show, showError],
  );

  /** Numbers a new cart request; only the response of the latest numbered request is shown. */
  const startCartRequest = useCallback((): number => {
    cartRequestRef.current += 1;
    return cartRequestRef.current;
  }, []);

  /** Shows `next` when `requestId` is the latest cart request and the page is mounted. */
  const commitCart = useCallback((requestId: number, next: CartView): void => {
    if (!mountedRef.current || requestId !== cartRequestRef.current) {
      return;
    }

    setCart(next);
    setSelectedItemId((current) =>
      current !== null && next.lines.some((line) => line.itemId === current) ? current : null,
    );
  }, []);

  /** Reads the session cart, shows it, and resolves to it; resolves to null after reporting a failed read. */
  const loadCart = useCallback(async (): Promise<CartView | null> => {
    const requestId = startCartRequest();
    try {
      const loaded = await api.get<CartView>(CART_PATH);
      commitCart(requestId, loaded);
      return loaded;
    } catch (error: unknown) {
      if (mountedRef.current) {
        await reportFailure(error);
      }
      return null;
    }
  }, [commitCart, reportFailure, startCartRequest]);

  useEffect(() => {
    void loadCart();
  }, [loadCart]);

  useEffect(
    () =>
      on('CartItemDeleted', async () => {
        await show({
          title: msg('DeletedItem'),
          message: msg('AnItemInYourCartWasDeletedByAnotherUser'),
          buttons: 'ok',
        });
        if (!mountedRef.current) {
          return;
        }

        const loaded = await loadCart();
        if (mountedRef.current && loaded !== null && loaded.lines.length === 0) {
          navigate(SHOP_PATH);
        }
      }),
    [loadCart, navigate, show],
  );

  const handleRemove = async (): Promise<void> => {
    const body: RemoveCartItemRequest = { itemId: selectedItemId };
    const requestId = startCartRequest();
    let response: CartOperationResponse;
    try {
      response = await api.post<CartOperationResponse>(REMOVE_PATH, body);
    } catch (error: unknown) {
      if (mountedRef.current) {
        await reportFailure(error);
      }
      return;
    }

    if (!mountedRef.current) {
      return;
    }

    commitCart(requestId, response.cart);
    if (!response.result.succeeded) {
      await show(displayErrorsOptions(response.result, msg('UnableToRemoveItemFromCart')));
      return;
    }

    setSelectedItemId(null);
  };

  const handleEmpty = async (): Promise<void> => {
    const requestId = startCartRequest();
    let emptied: CartView;
    try {
      emptied = await api.post<CartView>(EMPTY_PATH);
    } catch (error: unknown) {
      if (mountedRef.current) {
        await reportFailure(error);
      }
      return;
    }

    if (!mountedRef.current) {
      return;
    }

    commitCart(requestId, emptied);
    setSelectedItemId(null);
  };

  const handleProceed = async (): Promise<void> => {
    if (proceedingRef.current) {
      return;
    }

    const endProceeding = (): void => {
      proceedingRef.current = false;
      if (mountedRef.current) {
        setProceeding(false);
      }
    };

    proceedingRef.current = true;
    setProceeding(true);
    let response: CheckoutResponse;
    try {
      response = await api.post<CheckoutResponse>(CHECKOUT_PATH);
    } catch (error: unknown) {
      endProceeding();
      if (!mountedRef.current) {
        return;
      }

      await reportFailure(error);
      if (mountedRef.current && !(error instanceof UnauthorizedError)) {
        await loadCart();
      }
      return;
    }

    endProceeding();
    if (!mountedRef.current) {
      return;
    }

    if (response.result.succeeded) {
      navigate(CONFIRMATION_PATH, { state: { receipt: response.receipt } });
      return;
    }

    await show({
      title: msg('CheckoutUnsuccessful'),
      message: fullErrorText(response.result),
      icon: 'error',
      buttons: 'ok',
    });
    if (mountedRef.current) {
      await loadCart();
    }
  };

  const lines = cart?.lines ?? [];
  const totalText = cart === null ? '' : currencyFormat(cart.total);

  return (
    <main style={PAGE_STYLE} aria-busy={cart === null || proceeding}>
      <h1 id={headingId} style={HEADING_STYLE}>
        {PAGE_CAPTION}
      </h1>
      <p style={MESSAGE_STYLE}>{msg('CheckoutInstructions')}</p>
      <div style={PLINTH_STYLE}>
        <div role="region" aria-labelledby={headingId} style={TABLE_AREA_STYLE}>
          <DataTable
            columns={COLUMNS}
            rows={lines}
            rowKey={lineKey}
            selectedKey={selectedItemId}
            onSelect={(line) => setSelectedItemId(line.itemId)}
          />
        </div>
        <div style={FOOTER_ROW_STYLE}>
          <p style={TOTAL_STYLE}>
            <span style={TOTAL_LABEL_STYLE}>{TOTAL_CAPTION}</span>
            <span>{totalText}</span>
          </p>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={proceeding} onClick={() => void handleProceed()}>
              {PROCEED_CAPTION}
            </DialogButton>
            <DialogButton onClick={() => void handleRemove()}>{REMOVE_CAPTION}</DialogButton>
            <DialogButton onClick={() => void handleEmpty()}>{EMPTY_CAPTION}</DialogButton>
            <DialogButton onClick={() => navigate(SHOP_PATH)}>{BACK_CAPTION}</DialogButton>
          </div>
        </div>
      </div>
      {messageBox}
    </main>
  );
}
