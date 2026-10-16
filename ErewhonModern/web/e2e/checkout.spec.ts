// End-to-end checkout failures of the shop: Checkout with an empty cart, Proceed after the cart was emptied on the
// checkout page, and Proceed with a cart line whose item another session has bought.

import { test, expect, type Page, type Locator } from '@playwright/test';

// $ShoppingCartEmptyPleaseBuyBid
const ShoppingCartEmptyPleaseBuyBid = 'Shopping Cart is empty, please buy/bid on an item';
// $CartEmpty
const CartEmpty = 'Cart Empty';
// $ShoppingCartHasNoItems
const ShoppingCartHasNoItems = 'There are no items in the shopping cart';
// $CheckoutUnsuccessful
const CheckoutUnsuccessful = 'Checkout Unsuccessful';
// $ItemSold
const ItemSold = 'The Item has already been sold';

const SHOP_CAPTION = 'Erewhon Investments Inc';
const CHECKOUT_CAPTION = 'Erewhon Investments Inc - Checkout';
const CONFIRMATION_CAPTION = 'Erewhon Investments Inc - Confirmation';

const SHOP_URL = /\/shop$/;
const CHECKOUT_URL = /\/shop\/checkout$/;
const CONFIRMATION_URL = /\/shop\/confirmation$/;

/** Item kind option of the shop's search plinth. */
type ItemKind = 'Retail Items' | 'Items for Tender' | 'All Items';

/** Returns the `<main>` area whose page heading is exactly `caption`. */
function pageMain(page: Page, caption: string): Locator {
  return page.getByRole('main').filter({ has: page.getByRole('heading', { level: 1, name: caption, exact: true }) });
}

/** Returns the shop page's Search plinth. */
function searchPlinth(page: Page): Locator {
  return page.getByRole('region', { name: 'Search', exact: true });
}

/** Returns the shop page's Selection plinth, which holds the search results or one item's details. */
function selectionPlinth(page: Page): Locator {
  return page.getByRole('region', { name: 'Selection', exact: true });
}

/** Returns the shop page's Shopping Cart plinth. */
function cartPlinth(page: Page): Locator {
  return page.getByRole('region', { name: 'Shopping Cart', exact: true });
}

/** Returns the two spans of the "Total:" line inside `scope`: the caption and the amount. */
function totalParts(scope: Locator): Locator {
  return scope.locator('p').filter({ hasText: 'Total:' }).locator('span');
}

/** Returns the table rows inside `scope` that hold a cell whose text is exactly `itemName`. */
function itemRow(page: Page, scope: Locator, itemName: string): Locator {
  return scope.getByRole('row').filter({ has: page.getByRole('cell', { name: itemName, exact: true }) });
}

/** Returns the search-result row of the item named exactly `itemName`. */
function resultRow(page: Page, itemName: string): Locator {
  return itemRow(page, selectionPlinth(page), itemName);
}

/** Returns the checkout page's row of the cart line of the item named exactly `itemName`. */
function checkoutRow(page: Page, itemName: string): Locator {
  return itemRow(page, page.getByRole('region', { name: CHECKOUT_CAPTION, exact: true }), itemName);
}

/** Returns the confirmation page's receipt row of the item named exactly `itemName`. */
function receiptRow(page: Page, itemName: string): Locator {
  return itemRow(page, page.getByRole('region', { name: CONFIRMATION_CAPTION, exact: true }), itemName);
}

/** Returns the message box titled exactly `title`. */
function messageBox(page: Page, title: string): Locator {
  return page.getByRole('dialog', { name: title, exact: true });
}

/**
 * Opens `/shop`, logs `clientName` on with the Client option, and waits until the shop page has loaded its search
 * plinth, results and cart.
 */
async function shopLogon(page: Page, clientName: string): Promise<void> {
  await page.goto('/shop');
  await page.getByRole('radio', { name: 'Client', exact: true }).check();
  await page.getByRole('combobox', { name: 'Client', exact: true }).selectOption({ label: clientName });
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(page.getByRole('button', { name: 'OK', exact: true })).toHaveCount(0);
  await expect(pageMain(page, SHOP_CAPTION)).toHaveAttribute('aria-busy', 'false');
  await expect(searchPlinth(page).getByRole('button', { name: 'Search', exact: true })).toBeVisible();
}

/** Chooses the item kind and the product category on the Search plinth, then clicks Search. */
async function search(page: Page, kind: ItemKind, category: string): Promise<void> {
  const plinth = searchPlinth(page);
  await plinth.getByRole('radio', { name: kind, exact: true }).check();
  await plinth.getByLabel('Product Category:', { exact: true }).selectOption({ label: category });
  await plinth.getByRole('button', { name: 'Search', exact: true }).click();
}

/**
 * Finds the retail item named `itemName` among the Antiques, opens its details through the row's Buy cell, clicks the
 * details' Buy button, and waits until the cart plinth lists the item.
 */
async function addRetailItemToCart(page: Page, itemName: string): Promise<void> {
  await search(page, 'Retail Items', 'Antiques');
  const row = resultRow(page, itemName);
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Buy', exact: true }).click();

  const details = page.getByRole('dialog', { name: 'Details', exact: true });
  await expect(details.getByRole('heading', { name: itemName, exact: true })).toBeVisible();
  await details.getByRole('button', { name: 'Buy', exact: true }).click();

  await expect(details).toHaveCount(0);
  await expect(itemRow(page, cartPlinth(page), itemName)).toBeVisible();
}

/** Clicks the shop cart plinth's Checkout and waits for the checkout page listing the item named `itemName`. */
async function openCheckout(page: Page, itemName: string): Promise<void> {
  await cartPlinth(page).getByRole('button', { name: 'Checkout', exact: true }).click();
  await expect(page).toHaveURL(CHECKOUT_URL);
  await expect(checkoutRow(page, itemName)).toBeVisible();
}

/** Clicks the checkout page's button captioned exactly `caption`. */
async function clickCheckoutButton(page: Page, caption: 'Proceed' | 'Empty'): Promise<void> {
  await pageMain(page, CHECKOUT_CAPTION).getByRole('button', { name: caption, exact: true }).click();
}

/** Expects the message box titled `title` to show `text`, then closes it with its OK button. */
async function acknowledge(page: Page, title: string, text: string): Promise<void> {
  const box = messageBox(page, title);
  await expect(box).toContainText(text);
  await box.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(box).toHaveCount(0);
}

test.describe('checkout', () => {
  test('B78 Checkout with an empty cart shows the cart-empty message and stays on the shop', async ({ page }) => {
    await shopLogon(page, 'Christine Ronaldo');
    const cart = cartPlinth(page);
    await expect(totalParts(cart)).toHaveText(['Total:', '$0.00']);

    await cart.getByRole('button', { name: 'Checkout', exact: true }).click();
    await acknowledge(page, CartEmpty, ShoppingCartEmptyPleaseBuyBid);

    await expect(page).toHaveURL(SHOP_URL);
    await expect(pageMain(page, SHOP_CAPTION)).toBeVisible();
    await expect(messageBox(page, CheckoutUnsuccessful)).toHaveCount(0);
  });

  test('B78 Proceed after Empty on the checkout page reports no items', async ({ page }) => {
    await shopLogon(page, 'Levi Muir');
    await addRetailItemToCart(page, 'Stamps');
    await openCheckout(page, 'Stamps');

    await clickCheckoutButton(page, 'Empty');
    await expect(checkoutRow(page, 'Stamps')).toHaveCount(0);
    await expect(page).toHaveURL(CHECKOUT_URL);

    await clickCheckoutButton(page, 'Proceed');
    await acknowledge(page, CheckoutUnsuccessful, ShoppingCartHasNoItems);

    await expect(page).toHaveURL(CHECKOUT_URL);
    await expect(pageMain(page, CHECKOUT_CAPTION)).toBeVisible();
  });

  test('B27 checking out an item another session bought fails with ItemSold', async ({ browser, baseURL }) => {
    const contextA = await browser.newContext({ baseURL });
    const contextB = await browser.newContext({ baseURL });
    try {
      const pageA = await contextA.newPage();
      const pageB = await contextB.newPage();

      await shopLogon(pageA, 'Howard Ellis');
      await addRetailItemToCart(pageA, 'Wash Jug');

      await shopLogon(pageB, 'Roger Boeing');
      await addRetailItemToCart(pageB, 'Wash Jug');
      await openCheckout(pageB, 'Wash Jug');
      await clickCheckoutButton(pageB, 'Proceed');
      await expect(pageB).toHaveURL(CONFIRMATION_URL);
      await expect(receiptRow(pageB, 'Wash Jug')).toContainText('$350.00');

      await openCheckout(pageA, 'Wash Jug');
      await clickCheckoutButton(pageA, 'Proceed');
      await acknowledge(pageA, CheckoutUnsuccessful, ItemSold);

      await expect(pageA).toHaveURL(CHECKOUT_URL);
      await expect(checkoutRow(pageA, 'Wash Jug')).toBeVisible();
    } finally {
      await Promise.all([contextA.close(), contextB.close()]);
    }
  });
});
