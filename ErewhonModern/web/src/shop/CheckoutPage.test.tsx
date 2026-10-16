import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { api } from '../api/client';
import { connectRealtime } from '../api/realtime';
import type { AddCartItemRequest, CartOperationResponse, Id, OperationResult } from '../api/types';
import { msg } from '../messages/messages';
import { renderWithSession, useFixture } from '../test/fixtures';
import { CheckoutPage } from './CheckoutPage';

const CHECKOUT_ROUTE = '/shop/checkout';
const SHOP_ROUTE = '/shop';
const SHOP_SENTINEL = 'shop-sentinel';
const CHECKOUT_CAPTION = 'Erewhon Investments Inc - Checkout';
const RETAIL_ITEM_NAME = 'Proxy Retail';
const PROCEED_CAPTION = 'Proceed';
const EMPTY_CAPTION = 'Empty';
const OK_CAPTION = 'OK';
const ADD_CART_ITEM_PATH = '/api/app/shop/cart/items';
const HUB_DELIVERY_TIMEOUT_MS = 10_000;

describe('CheckoutPage', () => {
  const fixture = useFixture('TestViewSaleItem');

  function retailItemId(): Id {
    const id = fixture.ids.retailItem;
    if (id === undefined) {
      throw new Error('The TestViewSaleItem fixture returned no retailItem id');
    }
    return id;
  }

  /** Adds the fixture's retail item to the session cart and checks that the add succeeded. */
  async function addRetailItemToCart(): Promise<Id> {
    const itemId = retailItemId();
    const body: AddCartItemRequest = { itemId, offer: 0 };
    const response = await api.post<CartOperationResponse>(ADD_CART_ITEM_PATH, body);
    expect(response.result.succeeded).toBe(true);
    expect(response.cart.lines.map((line) => line.itemId)).toEqual([itemId]);
    return itemId;
  }

  /** Renders the checkout page at `/shop/checkout`, with a sentinel heading at `/shop`, in the fixture session. */
  function renderCheckout(): UserEvent {
    const user = userEvent.setup();
    renderWithSession(
      <Routes>
        <Route path={CHECKOUT_ROUTE} element={<CheckoutPage />} />
        <Route path={SHOP_ROUTE} element={<h1>{SHOP_SENTINEL}</h1>} />
      </Routes>,
      fixture,
      { route: CHECKOUT_ROUTE },
    );
    return user;
  }

  /** Returns the exact text of the element that describes `dialog`. */
  function dialogMessage(dialog: HTMLElement): string | null {
    const messageId = dialog.getAttribute('aria-describedby');
    if (messageId === null) {
      return null;
    }
    return document.getElementById(messageId)?.textContent ?? null;
  }

  function expectCheckoutPageShown(): void {
    expect(screen.getByRole('heading', { name: CHECKOUT_CAPTION })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: SHOP_SENTINEL })).not.toBeInTheDocument();
  }

  it('B63_DeletedCartItemReturnsToShop', async () => {
    const itemId = await addRetailItemToCart();
    await connectRealtime(fixture.sessionId);
    const user = renderCheckout();

    expect(await screen.findByRole('cell', { name: RETAIL_ITEM_NAME })).toBeInTheDocument();
    expectCheckoutPageShown();

    const deleted = await api.del<OperationResult>(`/api/app/admin/items/${itemId}`);
    expect(deleted.succeeded).toBe(true);

    const message = await screen.findByRole(
      'dialog',
      { name: msg('DeletedItem') },
      { timeout: HUB_DELIVERY_TIMEOUT_MS },
    );
    expect(dialogMessage(message)).toBe(msg('AnItemInYourCartWasDeletedByAnotherUser'));

    await user.click(within(message).getByRole('button', { name: OK_CAPTION }));

    expect(
      await screen.findByRole('heading', { name: SHOP_SENTINEL }, { timeout: HUB_DELIVERY_TIMEOUT_MS }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: CHECKOUT_CAPTION })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: msg('DeletedItem') })).not.toBeInTheDocument();
  });

  it('B78_EmptyThenSubmitShowsNoItems', async () => {
    await addRetailItemToCart();
    const user = renderCheckout();

    expect(await screen.findByRole('cell', { name: RETAIL_ITEM_NAME })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: EMPTY_CAPTION }));

    await waitFor(() => {
      expect(screen.queryByRole('cell', { name: RETAIL_ITEM_NAME })).not.toBeInTheDocument();
    });
    expectCheckoutPageShown();

    await user.click(screen.getByRole('button', { name: PROCEED_CAPTION }));

    const message = await screen.findByRole('dialog', { name: msg('CheckoutUnsuccessful') });
    expect(dialogMessage(message)).toBe(msg('ShoppingCartHasNoItems'));
    expectCheckoutPageShown();

    await user.click(within(message).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: msg('CheckoutUnsuccessful') })).not.toBeInTheDocument();
    });
    expectCheckoutPageShown();
    expect(screen.queryByRole('cell', { name: RETAIL_ITEM_NAME })).not.toBeInTheDocument();
  });
});
