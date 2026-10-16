// End-to-end flow of the admin item screens in an agent session: Items › Add Retail Item creates a retail item after a
// zero-price save error, Items › View Items lists it with its allocated code, and Edit renames and reprices it after a
// missing-name save error, releasing its edit lock on save.

import { test, expect, type Page, type Locator } from '@playwright/test';

// $ErrorDuringSave
const ErrorDuringSave = 'Error during save';
// $ItemPriceNotValid
const ItemPriceNotValid = 'Item price must be greater than 0';
// $ItemNameRequired
const ItemNameRequired = 'A Name for the item is required';

const AGENT_NAME = 'Peter Smallsmith';
const CATEGORY_NAME = 'Antiques';
const CATEGORY_PREFIX = 'ANQU';
const COUNTRY_NAME = 'UK';
const REGION_NAME = 'London';
const ITEM_DESCRIPTION = 'Created by the end-to-end test.';
const ITEM_NAME_MAX_LENGTH = 31;

const ADD_EDITOR_NAME = 'Add Retail Sale Item';
const EDIT_EDITOR_NAME = 'Edit Retail Sale Item';
const VIEW_ITEMS_NAME = 'View Items';
const EDIT_LOCKS_PATH = '/api/app/edit-locks/';
const ALREADY_BEING_EDITED = /is already being edited$/;
const ITEM_CODE = new RegExp(`${CATEGORY_PREFIX}\\d+`);
const CODE_NUMBER = /^[1-9]\d*$/;

/** Logs on to the admin area as the agent `agentName` and waits for the shell caption naming that agent. */
async function adminLogonAsAgent(page: Page, agentName: string): Promise<void> {
  await page.goto('/admin');
  await page.getByRole('radio', { name: 'Agent', exact: true }).check();
  const userSelect = page.getByRole('combobox', { name: 'Agent', exact: true });
  await expect(userSelect).toBeEnabled();
  await userSelect.selectOption({ label: agentName });
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toContainText(`Administration - ${agentName}`);
}

/** Opens the menu `menu` of the admin menu bar and chooses its entry `item`. */
async function openMenu(page: Page, menu: string, item: string): Promise<void> {
  const menuBar = page.getByRole('menubar');
  await menuBar.getByRole('menuitem', { name: menu, exact: true }).click();
  await menuBar
    .getByRole('menu', { name: menu, exact: true })
    .getByRole('menuitem', { name: item, exact: true })
    .click();
}

/** Returns the View Items page of the admin shell. */
function viewItemsPage(page: Page): Locator {
  return page.getByRole('region', { name: VIEW_ITEMS_NAME, exact: true });
}

/** Returns the View Items table row whose name cell is exactly `name`. */
function itemRow(page: Page, name: string): Locator {
  return viewItemsPage(page)
    .getByRole('row')
    .filter({ has: page.getByRole('cell', { name, exact: true }) });
}

/** Expects the `$ErrorDuringSave` box to show `message`, then closes it with OK. */
async function dismissSaveError(page: Page, message: string): Promise<void> {
  const errorBox = page.getByRole('dialog', { name: ErrorDuringSave, exact: true });
  await expect(errorBox).toContainText(message);
  await errorBox.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(errorBox).toHaveCount(0);
}

test.describe('admin items', () => {
  test('agent creates and updates a retail item, with validation errors', async ({ page }) => {
    const suffix = Date.now().toString(36);
    const itemName = `E2E Item ${suffix}`;
    const renamedItem = `E2E Item ${suffix} v2`;
    expect(renamedItem.length).toBeLessThanOrEqual(ITEM_NAME_MAX_LENGTH);

    await adminLogonAsAgent(page, AGENT_NAME);

    // Add Retail Item: price 0 is rejected, then 123.45 is saved.
    await openMenu(page, 'Items', 'Add Retail Item');
    const addEditor = page.getByRole('dialog', { name: ADD_EDITOR_NAME, exact: true });
    await expect(addEditor).toBeVisible();
    await expect(addEditor.getByRole('group', { name: 'Agent', exact: true })).toContainText(AGENT_NAME);

    await addEditor.getByLabel('Category', { exact: true }).selectOption({ label: CATEGORY_NAME });
    await expect(addEditor.getByLabel('Prefix', { exact: true })).toHaveValue(CATEGORY_PREFIX);
    await addEditor.getByLabel('Country', { exact: true }).selectOption({ label: COUNTRY_NAME });
    const regionSelect = addEditor.getByLabel('Region', { exact: true });
    await expect(regionSelect.getByRole('option', { name: REGION_NAME, exact: true })).toBeAttached();
    await regionSelect.selectOption({ label: REGION_NAME });
    await addEditor.getByLabel('Name', { exact: true }).fill(itemName);
    await addEditor.getByLabel('Desc', { exact: true }).fill(ITEM_DESCRIPTION);
    await addEditor.getByLabel('Retail Price', { exact: true }).fill('0');
    await addEditor.getByRole('button', { name: 'OK', exact: true }).click();

    await dismissSaveError(page, ItemPriceNotValid);
    await expect(addEditor).toBeVisible();

    await addEditor.getByLabel('Retail Price', { exact: true }).fill('123.45');
    await addEditor.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(addEditor).toHaveCount(0);
    await expect(page.getByRole('dialog', { name: ErrorDuringSave, exact: true })).toHaveCount(0);

    // View Items lists the new item with its allocated code, the session agent and its price.
    await openMenu(page, 'Items', VIEW_ITEMS_NAME);
    const createdRow = itemRow(page, itemName);
    await expect(createdRow).toBeVisible();
    await expect(createdRow).toContainText(ITEM_CODE);
    await expect(createdRow).toContainText(AGENT_NAME);
    await expect(createdRow).toContainText('$123.45');

    // Edit opens the editor on the new item without the already-being-edited prompt.
    const lockStatusRead = page.waitForResponse(
      (response) => response.request().method() === 'GET' && response.url().includes(EDIT_LOCKS_PATH),
    );
    const editButton = viewItemsPage(page).getByRole('button', { name: 'Edit', exact: true });
    await expect(async () => {
      await createdRow.click();
      await expect(createdRow).toHaveAttribute('aria-selected', 'true');
      await editButton.click({ timeout: 2_000 });
    }).toPass({ timeout: 15_000 });

    const editEditor = page.getByRole('dialog', { name: EDIT_EDITOR_NAME, exact: true });
    await expect(editEditor).toBeVisible();
    const nameInput = editEditor.getByLabel('Name', { exact: true });
    await expect(nameInput).toHaveValue(itemName);
    await expect(editEditor.getByLabel('Prefix', { exact: true })).toHaveValue(CATEGORY_PREFIX);
    await expect(editEditor.getByLabel('Number', { exact: true })).toHaveValue(CODE_NUMBER);
    await expect(editEditor.getByLabel('Retail Price', { exact: true })).toHaveValue('123.45');
    expect((await lockStatusRead).ok()).toBe(true);
    await expect(page.getByRole('dialog', { name: ALREADY_BEING_EDITED })).toHaveCount(0);

    // An empty name is rejected.
    await nameInput.fill('');
    await editEditor.getByRole('button', { name: 'OK', exact: true }).click();
    await dismissSaveError(page, ItemNameRequired);
    await expect(editEditor).toBeVisible();

    // The renamed and repriced item is saved and its edit lock released.
    await nameInput.fill(renamedItem);
    await editEditor.getByLabel('Retail Price', { exact: true }).fill('150');
    const unlock = page.waitForResponse(
      (response) => response.request().method() === 'DELETE' && response.url().includes(EDIT_LOCKS_PATH),
    );
    await editEditor.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(editEditor).toHaveCount(0);
    expect((await unlock).ok()).toBe(true);
    await expect(page.getByRole('dialog', { name: ErrorDuringSave, exact: true })).toHaveCount(0);

    // View Items searches again on the item's change event and shows the item under its new name and price only.
    const renamedRow = itemRow(page, renamedItem);
    await expect(renamedRow).toBeVisible();
    await expect(renamedRow).toContainText(ITEM_CODE);
    await expect(renamedRow).toContainText('$150.00');
    await expect(itemRow(page, itemName)).toHaveCount(0);
  });
});
