// Retail purchase through the shop area on the seeded database: a client logs on, searches the Antiques retail items,
// adds Lamp to the cart from its details, checks out, reads the confirmation receipt and finds Lamp no longer listed.

import { test, expect, type Page, type Locator } from '@playwright/test';

/** Caption of an item-kind option on the shop search plinth. */
type ItemKind = 'Retail Items' | 'Items for Tender' | 'All Items';

// PurchaseConfirmation
const PurchaseConfirmationFirstLine =
  'The above Retail items have been charged to your Account and will be sent to you within 24 hours.';
// CheckoutInstructions
const CheckoutInstructionsFirstSentence = "To proceed with your purchases and/or bids, click the 'Proceed' button.";
// CheckoutUnsuccessful
const CheckoutUnsuccessful = 'Checkout Unsuccessful';
// CannotAddItemToCart
const CannotAddItemToCart = 'Cannot add item to cart';

const CLIENT_NAME = 'Peggy Simpson';
const CATEGORY = 'Antiques';
const ITEM_NAME = 'Lamp';
const ITEM_PRICE = '$250.00';
const ITEM_CODE = /^ANQU\d+$/;
// Unsold Antiques retail item that is only read, asserted listed in the same answer as each absence check of Lamp.
const LISTED_ITEM_NAME = 'Chair';
const LISTED_ITEM_PRICE = '$300.00';
const NO_AMOUNT = '$0.00';

const CHECKOUT_CAPTION = 'Erewhon Investments Inc - Checkout';
const CONFIRMATION_CAPTION = 'Erewhon Investments Inc - Confirmation';

/** Waits until the shop page has read its search reference, run its first search and read the cart. */
async function expectShopReady(page: Page): Promise<void> {
  await expect(page.getByRole('button', { name: 'Search', exact: true })).toBeVisible();
  await expect(page.getByRole('main')).toHaveAttribute('aria-busy', 'false');
}

/** Opens `/shop`, logs `clientName` on with the Client option and waits for the shop page. */
async function shopLogon(page: Page, clientName: string): Promise<void> {
  await page.goto('/shop');
  const logonForm = page.getByRole('form', { name: 'logon', exact: true });
  const okButton = logonForm.getByRole('button', { name: 'OK', exact: true });

  await logonForm.getByRole('radio', { name: 'Client', exact: true }).check();
  await logonForm.getByRole('combobox', { name: 'Client', exact: true }).selectOption({ label: clientName });
  await okButton.click();

  await expect(okButton).toHaveCount(0);
  await expectShopReady(page);
}

/** Sets the item kind and the product category on the search plinth and clicks Search. */
async function search(page: Page, kind: ItemKind, category: string): Promise<void> {
  const searchPlinth = page.getByRole('region', { name: 'Search', exact: true });
  await searchPlinth.getByLabel('Product Category:', { exact: true }).selectOption({ label: category });
  await searchPlinth.getByRole('radio', { name: kind, exact: true }).check();
  await searchPlinth.getByRole('button', { name: 'Search', exact: true }).click();
}

/** Returns the rows of `scope` that hold a cell whose text is exactly `text`. */
function rowWithCell(scope: Locator, text: string): Locator {
  return scope.getByRole('row').filter({ has: scope.page().getByRole('cell', { name: text, exact: true }) });
}

/** Returns the selection-table row whose name cell is exactly `itemName`. */
function resultRow(page: Page, itemName: string): Locator {
  return rowWithCell(page.getByRole('region', { name: 'Selection', exact: true }), itemName);
}

/** Returns the Shopping Cart plinth of the shop page. */
function cartPlinth(page: Page): Locator {
  return page.getByRole('region', { name: 'Shopping Cart', exact: true });
}

/** Expects the "Total:" line inside `scope` to show `amount`. */
async function expectTotal(scope: Locator, amount: string): Promise<void> {
  const totalLine = scope.getByRole('paragraph').filter({ hasText: /^Total:/ });
  await expect(totalLine.getByText(amount, { exact: true })).toBeVisible();
}

/** Returns the message box titled `title`. */
function messageBox(page: Page, title: string): Locator {
  return page.getByRole('dialog', { name: title, exact: true });
}

test.describe('shop purchase', () => {
  test('client buys a retail item and sees the receipt', async ({ page }) => {
    await test.step('log on and search the Antiques retail items', async () => {
      await shopLogon(page, CLIENT_NAME);
      await search(page, 'Retail Items', CATEGORY);

      await expect(resultRow(page, ITEM_NAME)).toBeVisible();
      await expect(resultRow(page, ITEM_NAME)).toContainText(ITEM_PRICE);
    });

    await test.step('open the item details and buy the item', async () => {
      await resultRow(page, ITEM_NAME).getByRole('button', { name: 'Buy', exact: true }).click();

      const details = page.getByRole('dialog', { name: 'Details', exact: true });
      await expect(details.getByRole('heading', { name: ITEM_NAME, exact: true })).toBeVisible();
      await expect(details.getByText(ITEM_PRICE, { exact: true })).toBeVisible();

      await details.getByRole('button', { name: 'Buy', exact: true }).click();
      await expect(details).toHaveCount(0);
      await expect(messageBox(page, CannotAddItemToCart)).toHaveCount(0);
    });

    await test.step('see the item in the cart and no longer in the search results', async () => {
      const cartRow = rowWithCell(cartPlinth(page), ITEM_NAME);
      await expect(cartRow).toBeVisible();
      await expect(cartRow).toContainText(ITEM_PRICE);
      await expectTotal(cartPlinth(page), ITEM_PRICE);

      await expect(resultRow(page, ITEM_NAME)).toHaveCount(0);
      await expect(resultRow(page, LISTED_ITEM_NAME)).toContainText(LISTED_ITEM_PRICE);
    });

    await test.step('open the checkout page from the cart', async () => {
      await cartPlinth(page).getByRole('button', { name: 'Checkout', exact: true }).click();
      await expect(page).toHaveURL(/\/shop\/checkout$/);

      const checkoutLines = page.getByRole('region', { name: CHECKOUT_CAPTION, exact: true });
      const checkoutRow = rowWithCell(checkoutLines, ITEM_NAME);
      await expect(checkoutRow).toBeVisible();
      await expect(checkoutRow).toContainText(ITEM_PRICE);
      await expectTotal(page.getByRole('main'), ITEM_PRICE);
      await expect(page.getByRole('main')).toContainText(CheckoutInstructionsFirstSentence);
    });

    await test.step('proceed and read the confirmation receipt', async () => {
      await page.getByRole('button', { name: 'Proceed', exact: true }).click();
      await expect(page).toHaveURL(/\/shop\/confirmation$/);
      await expect(messageBox(page, CheckoutUnsuccessful)).toHaveCount(0);
      await expect(messageBox(page, CannotAddItemToCart)).toHaveCount(0);

      const receiptLines = page.getByRole('region', { name: CONFIRMATION_CAPTION, exact: true });
      const receiptRow = rowWithCell(receiptLines, ITEM_NAME);
      await expect(receiptRow).toHaveCount(1);
      await expect(receiptRow.getByRole('cell', { name: ITEM_CODE })).toBeVisible();
      await expect(receiptRow.getByRole('cell', { name: ITEM_PRICE, exact: true })).toBeVisible();
      await expect(receiptRow.getByRole('cell', { name: NO_AMOUNT, exact: true })).toBeVisible();
      await expect(page.getByRole('main')).toContainText(PurchaseConfirmationFirstLine);
    });

    await test.step('continue to the shop with an empty cart', async () => {
      await page.getByRole('button', { name: 'Continue', exact: true }).click();
      await expect(page).toHaveURL(/\/shop$/);
      await expectShopReady(page);

      await expectTotal(cartPlinth(page), NO_AMOUNT);
      await expect(rowWithCell(cartPlinth(page), ITEM_NAME)).toHaveCount(0);
    });

    await test.step('search again and find the sold item no longer listed', async () => {
      await search(page, 'Retail Items', CATEGORY);

      await expect(resultRow(page, LISTED_ITEM_NAME)).toContainText(LISTED_ITEM_PRICE);
      await expect(resultRow(page, ITEM_NAME)).toHaveCount(0);
    });
  });
});
