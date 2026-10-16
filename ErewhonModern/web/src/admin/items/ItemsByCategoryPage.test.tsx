// Component tests of the admin items-by-category list against the fixture bridge: the TestViewRetailSaleItem and
// TestViewTenderSaleItem ports of the item list entry, its folder header and the header check (B-74, T-18), and the
// agent session's list of its own items only (B-75). The ports create their sales and tenders through the shop cart
// and checkout and award tenders through close-now (D-215).

import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { api } from '../../api/client';
import type {
  AddCartItemRequest,
  CartOperationResponse,
  CheckoutResponse,
  CloseNowRequest,
  CloseNowResponse,
  ClosureScheduleStateResponse,
  Id,
  IsoDate,
  ItemCategoryRow,
  ItemDetailsResponse,
  ItemProjection,
  OperationResult,
  RetailItemRequest,
  SaleDetailProjection,
  TenderItemRequest,
} from '../../api/types';
import { decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { ItemsByCategoryPage } from './ItemsByCategoryPage';
import { getHeader, itemDisplay, itemDisplayInput, itemKind, verifyHeader } from './itemDisplay';

const ITEMS_PATH = '/api/app/items/';
const RETAIL_ITEMS_PATH = '/api/app/admin/retail-items';
const TENDER_ITEMS_PATH = '/api/app/admin/tender-items';
const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';
const CART_ITEMS_PATH = '/api/app/shop/cart/items';
const CHECKOUT_PATH = '/api/app/shop/checkout';
const CLOSURE_SCHEDULE_PATH = '/api/app/admin/tenders/closure-schedule';
const CLOSE_NOW_PATH = '/api/app/admin/tenders/close-now';

const RETAIL_PRODUCT_CODE_NUMBER = 8080;
const RETAIL_PRODUCT_NAME = 'Retail Product';
const RETAIL_PRODUCT_DESCRIPTION = 'Some Retail Description';
const RETAIL_PRODUCT_PRICE = 800;

const TENDERED_ITEM_CODE_NUMBER = 7890;
const UNTENDERED_ITEM_CODE_NUMBER = 6543;
const TENDER_ITEM_NAME = 'Tender Item';
const TENDER_ITEM_DESCRIPTION = 'Tender Item Description';
const TENDER_ITEM_PRICE = 951.15;
const TENDER_OFFER_ABOVE_PRICE = 10;

const RETAIL_OFFER = 0;

/** Returns `value`, and throws naming `description` when it is null or undefined. */
function present<T>(value: T | null | undefined, description: string): T {
  if (value === null || value === undefined) {
    throw new Error(`${description} is missing`);
  }
  return value;
}

/** Returns the id the fixture recorded under `key`, and throws when the fixture returned none. */
function fixtureId(fixture: FixtureHandle, key: keyof FixtureIds): Id {
  return present(fixture.ids[key], `The fixture's ${key} id`);
}

/** Reads the item `id` through GET /api/app/items/{id}. */
async function readItem(id: Id): Promise<ItemProjection> {
  const details = await api.get<ItemDetailsResponse>(`${ITEMS_PATH}${id}`);
  return details.item;
}

/** Returns the sale of `item`, and throws when the item is unsold. */
function saleOf(item: ItemProjection): SaleDetailProjection {
  return present(item.sale, `The sale of item ${item.id}`);
}

/** Checks that `result` succeeded, reporting its error text when it did not. */
function expectSucceeded(result: OperationResult): void {
  expect(result.succeeded, result.fullErrorText).toBe(true);
}

/** Puts the item `itemId` with `offer` in the session client's cart and checks the cart out, both successfully. */
async function checkOutItem(itemId: Id, offer: number): Promise<void> {
  const line: AddCartItemRequest = { itemId, offer };
  const added = await api.post<CartOperationResponse>(CART_ITEMS_PATH, line);
  expectSucceeded(added.result);

  const checkout = await api.post<CheckoutResponse>(CHECKOUT_PATH);
  expectSucceeded(checkout.result);
}

/** Creates an item through POST `path` with `request`, checks that it succeeded, and reads the created item. */
async function createItem(path: string, request: RetailItemRequest | TenderItemRequest): Promise<ItemProjection> {
  const result = await api.post<OperationResult>(path, request);
  expectSucceeded(result);
  return readItem(present(result.id, `The id of the item created through ${path}`));
}

/** Returns the start of an item's list entry, `<code> <name>: `, with a null code written as `''`. */
function entryPrefix(item: ItemProjection): string {
  return `${item.code ?? ''} ${item.name}: `;
}

/** Returns the request of a tender item "Tender Item" of the fixture's agent, category and region. */
function tenderItemRequest(fixture: FixtureHandle, codeNumber: number, closureDate: IsoDate): TenderItemRequest {
  return {
    codeNumber,
    name: TENDER_ITEM_NAME,
    description: TENDER_ITEM_DESCRIPTION,
    listedDate: null,
    photoBase64: null,
    price: TENDER_ITEM_PRICE,
    agentId: fixtureId(fixture, 'agent'),
    itemCategoryId: fixtureId(fixture, 'itemCategory'),
    regionId: fixtureId(fixture, 'region'),
    closureDate,
  };
}

/** Returns the database server date. */
async function serverDate(): Promise<IsoDate> {
  const state = await api.get<ClosureScheduleStateResponse>(CLOSURE_SCHEDULE_PATH);
  return state.serverDate;
}

describe('TestViewRetailSaleItem', () => {
  const fixture = useFixture('TestViewSaleItem');

  it('testDisplay', async () => {
    const id = fixtureId(fixture, 'retailItem');
    await checkOutItem(id, RETAIL_OFFER);

    const item = await readItem(id);
    const sale = saleOf(item);
    const expected =
      `${entryPrefix(item)}Sold to ${sale.client.name} for ` +
      `$${decimalString(present(sale.price, 'The retail sale price'))}`;

    expect(itemDisplay(itemDisplayInput(item))).toBe(expected);
  });

  it('testDisplayUnsold', async () => {
    const item = await createItem(RETAIL_ITEMS_PATH, {
      codeNumber: RETAIL_PRODUCT_CODE_NUMBER,
      name: RETAIL_PRODUCT_NAME,
      description: RETAIL_PRODUCT_DESCRIPTION,
      listedDate: null,
      photoBase64: null,
      price: RETAIL_PRODUCT_PRICE,
      agentId: fixtureId(fixture, 'agent'),
      itemCategoryId: fixtureId(fixture, 'itemCategory'),
      regionId: fixtureId(fixture, 'region'),
    } satisfies RetailItemRequest);
    const expected = `${entryPrefix(item)}$${decimalString(item.price)} - Unsold`;

    expect(expected).toBe('CATI8080 Retail Product: $800.00 - Unsold');
    expect(itemDisplay(itemDisplayInput(item))).toBe(expected);
  });

  it('testGetHeader', async () => {
    const item = await readItem(fixtureId(fixture, 'retailItem'));

    expect(getHeader(itemKind(item))).toBe(msg('ItemsForSale'));
  });

  it('testVerifyHeader', async () => {
    const kind = itemKind(await readItem(fixtureId(fixture, 'retailItem')));

    expect(verifyHeader(kind, msg('ItemsForSale'))).toBe(true);
    expect(verifyHeader(kind, null)).toBe(false);
    expect(verifyHeader(kind, 'Items For Sale')).toBe(false);
  });
});

describe('TestViewTenderSaleItem', () => {
  const fixture = useFixture('TestViewSaleItem');

  it('testDisplay', async () => {
    const id = fixtureId(fixture, 'tenderItem');
    const unsold = await readItem(id);
    const closeNow: CloseNowRequest = { date: present(unsold.closureDate, 'The tender item closure date') };
    const closed = await api.post<CloseNowResponse>(CLOSE_NOW_PATH, closeNow);
    expect(closed.numClosed).toBe(1);

    const item = await readItem(id);
    const sale = saleOf(item);
    const expected =
      `${entryPrefix(item)}$${decimalString(item.price)} - Sold to ${sale.client.name} for ` +
      `$${decimalString(present(sale.offer, 'The winning tender offer'))}`;

    expect(itemDisplay(itemDisplayInput(item))).toBe(expected);
  });

  it('testDisplayUnsold', async () => {
    const created = await createItem(
      TENDER_ITEMS_PATH,
      tenderItemRequest(fixture, TENDERED_ITEM_CODE_NUMBER, await serverDate()),
    );
    await checkOutItem(created.id, created.price + TENDER_OFFER_ABOVE_PRICE);

    const item = await readItem(created.id);
    const expected = `${entryPrefix(item)}$${decimalString(item.price)} - Tendered`;

    expect(expected).toBe('CATI7890 Tender Item: $951.15 - Tendered');
    expect(itemDisplay(itemDisplayInput(item))).toBe(expected);
  });

  it('testDisplayUntendered', async () => {
    const item = await createItem(
      TENDER_ITEMS_PATH,
      tenderItemRequest(fixture, UNTENDERED_ITEM_CODE_NUMBER, await serverDate()),
    );
    const expected = `${entryPrefix(item)}$${decimalString(item.price)} - Untendered`;

    expect(expected).toBe('CATI6543 Tender Item: $951.15 - Untendered');
    expect(itemDisplay(itemDisplayInput(item))).toBe(expected);
  });

  it('testGetHeader', async () => {
    const item = await readItem(fixtureId(fixture, 'tenderItem'));

    expect(getHeader(itemKind(item))).toBe(msg('ItemsForTender'));
  });

  it('testVerifyHeader', async () => {
    const kind = itemKind(await readItem(fixtureId(fixture, 'tenderItem')));

    expect(verifyHeader(kind, msg('ItemsForTender'))).toBe(true);
    expect(verifyHeader(kind, null)).toBe(false);
    expect(verifyHeader(kind, 'Items For Tender')).toBe(false);
  });
});

describe('ItemsByCategoryPage', () => {
  const fixture = useFixture('AdminGuards');

  it('lists only the session agent items', async () => {
    const categoryId = fixtureId(fixture, 'itemCategory');
    const categories = await api.get<ItemCategoryRow[]>(ITEM_CATEGORIES_PATH);
    const category = present(
      categories.find((row) => row.id === categoryId),
      `The item category ${categoryId} in GET ${ITEM_CATEGORIES_PATH}`,
    );

    const ownItems = [
      await readItem(fixtureId(fixture, 'retailItem')),
      await readItem(fixtureId(fixture, 'soldRetailItem')),
    ];
    const otherAgentItems = [
      await readItem(fixtureId(fixture, 'otherRetailItem')),
      await readItem(fixtureId(fixture, 'otherSoldRetailItem')),
    ];
    for (const item of ownItems) {
      expect(item.agentId).toBe(fixtureId(fixture, 'agent'));
    }
    for (const item of otherAgentItems) {
      expect(item.agentId).toBe(fixtureId(fixture, 'otherAgent'));
    }

    const user = userEvent.setup();
    renderWithSession(<ItemsByCategoryPage />, fixture);

    await user.click(await screen.findByRole('treeitem', { name: category.name }));
    await user.click(await screen.findByRole('treeitem', { name: msg('ItemsForSale') }));

    for (const item of ownItems) {
      expect(await screen.findByRole('treeitem', { name: itemDisplay(itemDisplayInput(item)) })).toBeInTheDocument();
    }
    for (const item of otherAgentItems) {
      expect(screen.queryByRole('treeitem', { name: itemDisplay(itemDisplayInput(item)) })).not.toBeInTheDocument();
    }
  });
});
