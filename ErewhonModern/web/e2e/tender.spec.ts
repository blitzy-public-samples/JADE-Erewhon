// End-to-end tender bidding in the shop on the seeded database: an offer below the reserve is refused, a tender at
// the reserve is placed through checkout and confirmation, and a new offer that does not exceed one of the client's
// own tenders on the item is blocked with one message per such tender and nothing added to the cart.

import { test, expect, type Page, type Locator } from '@playwright/test';

// TenderOfferLessThanReserve
const TenderOfferLessThanReserve = 'The offer price cannot be less than the reserve price';
// CannotAddItemToCart
const CannotAddItemToCart = 'Cannot add item to cart';
// AlreadyTenderedAHigherOrSameOffer
const AlreadyTenderedAHigherOrSameOffer = 'You have already tendered a higher or same offer for this item';
// AlreadyTendered
const AlreadyTendered = 'Already Tendered';

const LOGON_HEADING = 'logon';
const CLIENT_CAPTION = 'Client';
const SHOP_HEADING = 'Erewhon Investments Inc';
const CHECKOUT_HEADING = 'Erewhon Investments Inc - Checkout';
const CONFIRMATION_HEADING = 'Erewhon Investments Inc - Confirmation';
const SEARCH_PLINTH = 'Search';
const SELECTION_PLINTH = 'Selection';
const CART_PLINTH = 'Shopping Cart';
const CATEGORY_CAPTION = 'Product Category:';
const DETAILS_CAPTION = 'Details';
const OFFER_CAPTION = 'Enter the amount of your Tender :';
const TOTAL_CAPTION = 'Total:';
const TENDER_TRANSACTION = 'Tender';
const ANTIQUES = 'Antiques';
const ANTIQUES_CODE = /^ANQU\d+$/;
const NO_TOTAL = '$0.00';

/** Item kind options of the search plinth. */
type ItemKind = 'Retail Items' | 'Items for Tender';

/** Returns the body rows of the table inside `scope` whose cells include one named exactly `itemName`. */
function rowNamed(scope: Locator, itemName: string): Locator {
  return scope.getByRole('row').filter({ has: scope.page().getByRole('cell', { name: itemName, exact: true }) });
}

/** Returns the message box titled `title`. */
function messageBox(page: Page, title: string): Locator {
  return page.getByRole('dialog', { name: title, exact: true });
}

/** Returns the item details pane of the shop page. */
function itemDetails(page: Page): Locator {
  return page.getByRole('dialog', { name: DETAILS_CAPTION, exact: true });
}

/** Returns the offer box of the item details pane. */
function offerField(page: Page): Locator {
  return itemDetails(page).getByLabel(OFFER_CAPTION, { exact: true });
}

/** Returns the Shopping Cart plinth of the shop page. */
function shoppingCart(page: Page): Locator {
  return page.getByRole('region', { name: CART_PLINTH, exact: true });
}

/** Returns the cart rows of the item named `itemName`. */
function cartLine(page: Page, itemName: string): Locator {
  return rowNamed(shoppingCart(page), itemName);
}

/** Returns the search-result rows of the item named `itemName`. */
function resultRow(page: Page, itemName: string): Locator {
  return rowNamed(page.getByRole('region', { name: SELECTION_PLINTH, exact: true }), itemName);
}

/** Waits until the shop page shows its heading and has loaded its search plinth, first search and cart. */
async function waitForShop(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { level: 1, name: SHOP_HEADING, exact: true })).toBeVisible();
  await expect(page.getByRole('main')).toHaveAttribute('aria-busy', 'false');
}

/** Expects the Shopping Cart plinth to show `amount` after "Total:". */
async function expectCartTotal(page: Page, amount: string): Promise<void> {
  const total = shoppingCart(page).getByRole('paragraph').filter({ hasText: TOTAL_CAPTION });
  await expect(total.getByText(amount, { exact: true })).toBeVisible();
}

/** Opens /shop, logs on as the client named `clientName` with the Client option and waits for the shop page. */
async function shopLogon(page: Page, clientName: string): Promise<void> {
  await page.goto('/shop');
  await expect(page.getByRole('heading', { level: 1, name: LOGON_HEADING, exact: true })).toBeVisible();
  await expect(page.getByRole('radio', { name: CLIENT_CAPTION, exact: true })).toBeChecked();
  await page.getByRole('combobox', { name: CLIENT_CAPTION, exact: true }).selectOption({ label: clientName });
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await waitForShop(page);
}

/** Searches the shop for items of `kind` in the product category named `category`. */
async function search(page: Page, kind: ItemKind, category: string): Promise<void> {
  const plinth = page.getByRole('region', { name: SEARCH_PLINTH, exact: true });
  await plinth.getByRole('radio', { name: kind, exact: true }).check();
  await plinth.getByLabel(CATEGORY_CAPTION, { exact: true }).selectOption({ label: category });
  await plinth.getByRole('button', { name: 'Search', exact: true }).click();
}

/** Types `offer` into the open item details pane and clicks its Bid button. */
async function offerInDetails(page: Page, offer: string): Promise<void> {
  const details = itemDetails(page);
  await offerField(page).fill(offer);
  await details.getByRole('button', { name: 'Bid', exact: true }).click();
}

/** Opens the details of the item named `itemName` through its result row's Bid cell and bids `offer` on it. */
async function bid(page: Page, itemName: string, offer: string): Promise<void> {
  await resultRow(page, itemName).getByRole('button', { name: 'Bid', exact: true }).click();
  await expect(itemDetails(page).getByRole('heading', { name: itemName, exact: true })).toBeVisible();
  await offerInDetails(page, offer);
}

/** Expects exactly one message box titled `title` that contains `message`, and closes it with OK. */
async function closeSingleMessage(page: Page, title: string, message: string): Promise<void> {
  const box = messageBox(page, title);
  await expect(box).toHaveCount(1);
  await expect(box).toContainText(message);
  await box.getByRole('button', { name: 'OK', exact: true }).click();
}

/** Expects one already-tendered message for the bid just made, closes it, and expects no further one. */
async function expectSingleAlreadyTendered(page: Page): Promise<void> {
  await closeSingleMessage(page, AlreadyTendered, AlreadyTenderedAHigherOrSameOffer);
  await expect(offerField(page)).toHaveValue('');
  await expect(messageBox(page, AlreadyTendered)).toHaveCount(0);
}

test.describe('tender bidding', () => {
  test('B31 offer below reserve is refused, then a tender is placed and an equal re-bid is blocked', async ({
    page,
  }) => {
    await shopLogon(page, 'Sean Hill');
    await search(page, 'Items for Tender', ANTIQUES);
    await expect(resultRow(page, 'Wooden')).toContainText('$225.00');

    // An offer one cent below the reserve is refused and the details stay open.
    await bid(page, 'Wooden', '224.99');
    await closeSingleMessage(page, CannotAddItemToCart, TenderOfferLessThanReserve);
    await expect(messageBox(page, CannotAddItemToCart)).toHaveCount(0);
    await expect(itemDetails(page).getByRole('heading', { name: 'Wooden', exact: true })).toBeVisible();
    await expect(cartLine(page, 'Wooden')).toHaveCount(0);
    await expectCartTotal(page, NO_TOTAL);

    // An offer equal to the reserve is added to the cart.
    await offerInDetails(page, '225');
    await expect(cartLine(page, 'Wooden')).toHaveCount(1);
    await expect(cartLine(page, 'Wooden').getByRole('cell')).toHaveText(['Wooden', '$225.00']);
    await expectCartTotal(page, '$225.00');
    await expect(itemDetails(page)).toHaveCount(0);
    await expect(messageBox(page, CannotAddItemToCart)).toHaveCount(0);

    // Checkout lists the tender line with the item price and the offer.
    await shoppingCart(page).getByRole('button', { name: 'Checkout', exact: true }).click();
    await expect(page).toHaveURL(/\/shop\/checkout$/);
    const checkoutLines = page.getByRole('region', { name: CHECKOUT_HEADING, exact: true });
    await expect(rowNamed(checkoutLines, 'Wooden').getByRole('cell')).toHaveText([
      TENDER_TRANSACTION,
      ANTIQUES_CODE,
      'Wooden',
      '$225.00',
      '$225.00',
    ]);

    // Proceed places the tender and the confirmation lists it.
    await page.getByRole('button', { name: 'Proceed', exact: true }).click();
    await expect(page).toHaveURL(/\/shop\/confirmation$/);
    const receipt = page.getByRole('region', { name: CONFIRMATION_HEADING, exact: true });
    await expect(rowNamed(receipt, 'Wooden').getByRole('cell')).toHaveText([
      TENDER_TRANSACTION,
      ANTIQUES_CODE,
      'Wooden',
      '$225.00',
      '$225.00',
    ]);

    // Continue returns to the shop with an empty cart.
    await page.getByRole('button', { name: 'Continue', exact: true }).click();
    await expect(page).toHaveURL(/\/shop$/);
    await waitForShop(page);
    await expect(shoppingCart(page).getByRole('row')).toHaveCount(1);
    await expectCartTotal(page, NO_TOTAL);

    // The unsold item is still listed, and a re-bid equal to the placed tender is blocked.
    await search(page, 'Items for Tender', ANTIQUES);
    await expect(resultRow(page, 'Wooden')).toContainText('$225.00');
    await bid(page, 'Wooden', '225');
    await expectSingleAlreadyTendered(page);
    await expect(cartLine(page, 'Wooden')).toHaveCount(0);
    await expectCartTotal(page, NO_TOTAL);
  });

  test('B31 an own seeded higher offer blocks a lower new offer', async ({ page }) => {
    await shopLogon(page, 'Pauline Wild');
    await search(page, 'Items for Tender', ANTIQUES);
    await expect(resultRow(page, 'Bookends')).toContainText('$1,299.00');

    // The seeded own tender of 1413.00 blocks a new offer of 1400.
    await bid(page, 'Bookends', '1400');
    await expectSingleAlreadyTendered(page);
    await expect(cartLine(page, 'Bookends')).toHaveCount(0);
    await expectCartTotal(page, NO_TOTAL);
  });
});
