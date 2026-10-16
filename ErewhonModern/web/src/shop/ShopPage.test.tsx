// Component tests of the shop search screen against the fixture bridge: the search-result row and the item behind it
// for the retail and the tender item of TestViewSaleItem (B-74, T-18), Checkout with an empty cart (B-78), and the
// message shown when an item in the cart is deleted by another user (B-63).

import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { api } from '../api/client';
import { connectRealtime } from '../api/realtime';
import type {
  AddCartItemRequest,
  CartOperationResponse,
  CartView,
  Id,
  ItemDetailsResponse,
  OperationResult,
  ShopSearchRow,
} from '../api/types';
import { getItem, getSearchResultString } from '../admin/items/itemDisplay';
import type { ItemKind } from '../admin/items/itemDisplay';
import { currencyFormat, shortFormat } from '../format';
import { msg } from '../messages/messages';
import { renderWithSession, useFixture } from '../test/fixtures';
import { ShopPage } from './ShopPage';

const SHOP_ROUTE = '/shop';
const CHECKOUT_ROUTE = '/shop/checkout';
const CHECKOUT_SENTINEL = 'checkout-sentinel';

const SEARCH_HEADING = 'Search';
const CART_HEADING = 'Shopping Cart';
const CHECKOUT_CAPTION = 'Checkout';
const OK_CAPTION = 'OK';
const INFORMATION_ICON = 'information';

const CART_PATH = '/api/app/shop/cart';
const CART_ITEMS_PATH = '/api/app/shop/cart/items';

const ITEM_NAMES: Readonly<Record<ItemKind, string>> = {
  retail: 'Proxy Retail',
  tender: 'Proxy Tender',
};
const FIXTURE_PRICE = 5.5;
const FIXTURE_CODE = /^CATI\d+$/;
const SEARCH_RESULT_CELL_COUNT = 5;
const CELL_SEPARATOR = '\t';
const HUB_DELIVERY_TIMEOUT_MS = 10_000;

const fixture = useFixture('TestViewSaleItem');

/** Returns the id of the fixture's retail or tender item. */
function fixtureItemId(kind: ItemKind): Id {
  const id = kind === 'tender' ? fixture.ids.tenderItem : fixture.ids.retailItem;
  if (id === undefined) {
    throw new Error(`The TestViewSaleItem fixture returned no ${kind} item id`);
  }
  return id;
}

/** Returns the caption of the row button of an item of the kind: `$Bid` for tender, `$Buy` for retail. */
function buyBidCaption(kind: ItemKind): string {
  return kind === 'tender' ? msg('Bid') : msg('Buy');
}

/** Reads the fixture's item of the kind through GET /api/app/items/{id}. */
function readItem(kind: ItemKind): Promise<ItemDetailsResponse> {
  return api.get<ItemDetailsResponse>(`/api/app/items/${fixtureItemId(kind)}`);
}

/** Returns the search-result row of the item read through GET /api/app/items/{id}. */
function searchResultRow(details: ItemDetailsResponse): ShopSearchRow {
  const { item } = details;
  return {
    id: item.id,
    isTender: item.isTender,
    code: item.code,
    name: item.name,
    listedDate: item.listedDate,
    price: item.price,
    isClosed: details.isClosed,
  };
}

/** Renders the shop at `/shop`, with a sentinel heading at `/shop/checkout`, in the fixture session. */
function renderShop(): UserEvent {
  const user = userEvent.setup();
  renderWithSession(
    <Routes>
      <Route path={SHOP_ROUTE} element={<ShopPage />} />
      <Route path={CHECKOUT_ROUTE} element={<h1>{CHECKOUT_SENTINEL}</h1>} />
    </Routes>,
    fixture,
    { route: SHOP_ROUTE },
  );
  return user;
}

/** Waits until the shop has read its reference data, run its first search and read the cart. */
async function waitForShopLoaded(): Promise<void> {
  await waitFor(() => {
    expect(screen.getByRole('main')).toHaveAttribute('aria-busy', 'false');
  });
}

/** Returns a matcher of the accessible names that contain `name`. */
function nameContaining(name: string): (accessibleName: string) => boolean {
  return (accessibleName) => accessibleName.includes(name);
}

/** Waits for the search-result row whose accessible name contains `name`, and returns it. */
function findResultRow(name: string): Promise<HTMLElement> {
  return screen.findByRole('row', { name: nameContaining(name) });
}

/** Returns the search-result row whose accessible name contains `name`, or null when none is shown. */
function queryResultRow(name: string): HTMLElement | null {
  return screen.queryByRole('row', { name: nameContaining(name) });
}

/** Returns the exact text of the element that describes `dialog`. */
function dialogMessage(dialog: HTMLElement): string | null {
  const messageId = dialog.getAttribute('aria-describedby');
  if (messageId === null) {
    return null;
  }
  return document.getElementById(messageId)?.textContent ?? null;
}

/**
 * Checks that the fixture's item of the kind is CATI-coded and priced 5.50, that its shop row shows the five cells
 * `$Buy`/`$Bid`, code, name, listed date in `shortFormat` and price in `currencyFormat`, and that
 * `getSearchResultString` of the item's row gives those cells joined by tabs.
 */
async function checkSearchResultString(kind: ItemKind): Promise<void> {
  const details = await readItem(kind);
  const { item } = details;
  expect(item.isTender).toBe(kind === 'tender');
  expect(item.name).toBe(ITEM_NAMES[kind]);
  expect(item.price).toBe(FIXTURE_PRICE);
  expect(item.code).toMatch(FIXTURE_CODE);

  const expected = [
    buyBidCaption(kind),
    item.code,
    ITEM_NAMES[kind],
    shortFormat(item.listedDate),
    currencyFormat(FIXTURE_PRICE),
  ].join(CELL_SEPARATOR);

  renderShop();
  const row = await findResultRow(ITEM_NAMES[kind]);
  const cells = within(row).getAllByRole('cell');
  expect(cells).toHaveLength(SEARCH_RESULT_CELL_COUNT);
  expect(within(cells[0]).getByRole('button', { name: buyBidCaption(kind) })).toBeInTheDocument();
  expect(cells.map((cell) => cell.textContent ?? '').join(CELL_SEPARATOR)).toBe(expected);

  expect(getSearchResultString(searchResultRow(details))).toBe(expected);
}

/**
 * Checks that the `$Buy`/`$Bid` button of the item's shop row opens the `$Details` dialog of that item in place of
 * the list, and that `getItem` of the item's row returns that row, with the item's id and kind.
 */
async function checkGetItem(kind: ItemKind): Promise<void> {
  const itemId = fixtureItemId(kind);
  const details = await readItem(kind);
  const code = details.item.code;
  expect(code).toMatch(FIXTURE_CODE);

  const user = renderShop();
  const row = await findResultRow(ITEM_NAMES[kind]);
  await user.click(within(row).getByRole('button', { name: buyBidCaption(kind) }));

  const dialog = await screen.findByRole('dialog', { name: msg('Details') });
  expect(await within(dialog).findByRole('heading', { name: ITEM_NAMES[kind] })).toBeInTheDocument();
  expect(within(dialog).getByText(code ?? '')).toBeInTheDocument();
  expect(queryResultRow(ITEM_NAMES[kind])).toBeNull();

  const shownRow = searchResultRow(details);
  const found = getItem(shownRow);
  expect(found).not.toBeNull();
  expect(found).toBe(shownRow);
  expect(found.id).toBe(itemId);
  expect(found.isTender).toBe(kind === 'tender');
}

describe('TestViewRetailSaleItem', () => {
  it('testGetSearchResultString', async () => {
    await checkSearchResultString('retail');
  });

  it('testGetItem', async () => {
    await checkGetItem('retail');
  });
});

describe('TestViewTenderSaleItem', () => {
  it('testGetSearchResultString', async () => {
    await checkSearchResultString('tender');
  });

  it('testGetItem', async () => {
    await checkGetItem('tender');
  });
});

describe('ShopPage', () => {
  // Checkout with the empty fixture cart shows `$ShoppingCartEmptyPleaseBuyBid` titled `$CartEmpty` with the
  // information icon; OK closes it and the shop stays shown without navigating to the checkout page.
  it('B78_EmptyCartCheckoutStaysOnShop', async () => {
    expect((await api.get<CartView>(CART_PATH)).lines).toHaveLength(0);

    const user = renderShop();
    await waitForShopLoaded();
    expect(await findResultRow(ITEM_NAMES.retail)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: CHECKOUT_CAPTION }));

    const box = await screen.findByRole('dialog', { name: msg('CartEmpty') });
    expect(dialogMessage(box)).toBe(msg('ShoppingCartEmptyPleaseBuyBid'));
    expect(within(box).getByRole('img', { name: INFORMATION_ICON })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: CHECKOUT_SENTINEL })).not.toBeInTheDocument();

    await user.click(within(box).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: msg('CartEmpty') })).not.toBeInTheDocument();
    });
    expect(screen.queryByRole('heading', { name: CHECKOUT_SENTINEL })).not.toBeInTheDocument();
    expect(screen.getByRole('region', { name: SEARCH_HEADING })).toBeInTheDocument();
    expect(queryResultRow(ITEM_NAMES.retail)).toBeInTheDocument();
  });

  // Deleting the retail item held in the session cart shows `$ItemDeletedByAnotherUserRemoveFromCart` titled
  // `$DeletedItem`; after OK the cart plinth no longer lists the item and its total is $0.00.
  it('B63_DeletedCartItemShowsShopMessage', async () => {
    const itemId = fixtureItemId('retail');
    const request: AddCartItemRequest = { itemId, offer: 0 };
    const added = await api.post<CartOperationResponse>(CART_ITEMS_PATH, request);
    expect(added.result.succeeded, added.result.fullErrorText).toBe(true);
    expect(added.cart.lines.map((line) => line.itemId)).toEqual([itemId]);

    // Opens the fixture session's hub connection before the page renders and the item is deleted.
    await connectRealtime(fixture.sessionId);
    const user = renderShop();
    const cart = screen.getByRole('region', { name: CART_HEADING });
    expect(await within(cart).findByRole('cell', { name: ITEM_NAMES.retail })).toBeInTheDocument();
    await waitForShopLoaded();

    const deleted = await api.del<OperationResult>(`/api/app/admin/items/${itemId}`);
    expect(deleted.succeeded, deleted.fullErrorText).toBe(true);

    const box = await screen.findByRole(
      'dialog',
      { name: msg('DeletedItem') },
      { timeout: HUB_DELIVERY_TIMEOUT_MS },
    );
    expect(dialogMessage(box)).toBe(msg('ItemDeletedByAnotherUserRemoveFromCart'));

    await user.click(within(box).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(within(cart).queryByRole('cell', { name: ITEM_NAMES.retail })).not.toBeInTheDocument();
      expect(within(cart).getByText(currencyFormat(0))).toBeInTheDocument();
    });
    expect(screen.queryByRole('dialog', { name: msg('DeletedItem') })).not.toBeInTheDocument();
    expect((await api.get<CartView>(CART_PATH)).lines).toHaveLength(0);
  });
});
