// End-to-end flow of the admin area's clients: the administrator adds a client through Clients › Add Client, meets the
// Street-required error first, saves it, then edits it from Clients › View Clients, meets the duplicate-name error,
// renames it and reopens it to check the saved values.

import { test, expect, type Page } from '@playwright/test';

type Locator = ReturnType<Page['getByRole']>;

// $ErrorDuringSave
const ErrorDuringSave = 'Error during save';
// $AddressStreetRequired
const AddressStreetRequired = 'Street is required';
// $ClientWithSameNameExists
const ClientWithSameNameExists = 'A client with the same name already exists';
// $ThingAlreadyBeingEdited
const ThingAlreadyBeingEdited = /^.+ is already being edited$/;
// $OutstandingChanges
const OutstandingChanges = 'Outstanding Changes';

const ADMIN_CAPTION = 'Administration - Erewhon Investments Inc';
const MENU_BAR_NAME = 'Administration menu';
const ADD_CLIENT_DIALOG = 'Add Client';
const EDIT_CLIENT_DIALOG = 'Edit Client';
const CLIENTS_TAB = 'Clients';
const EDIT_LOCKS_PATH = '/api/app/edit-locks/';
const SEEDED_CLIENT = 'Pauline Wild';

const STREET = '2 Test Road';
const CITY = 'Auckland';
const COUNTRY = 'New Zealand';
const PHONE = '09 555 0100';
const FAX = '09 555 0101';
const EMAIL = 'e2e.client@example.com';
const WEB_SITE = 'www.example.com';

/** Opens `/admin`, logs on with the Administrator option checked by default, and waits for the shell caption. */
async function adminLogonAsAdministrator(page: Page): Promise<void> {
  await page.goto('/admin');
  const administrator = page.getByRole('radio', { name: 'Administrator', exact: true });
  await expect(administrator).toBeChecked();
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name: ADMIN_CAPTION, exact: true })).toBeVisible();
  await expect(page).toHaveTitle(ADMIN_CAPTION);
}

/** Clicks the menu bar item `menu`, then its entry `item`. */
async function openMenu(page: Page, menu: string, item: string): Promise<void> {
  const menuBar = page.getByRole('menubar', { name: MENU_BAR_NAME, exact: true });
  await menuBar.getByRole('menuitem', { name: menu, exact: true }).click();
  await menuBar
    .getByRole('menu', { name: menu, exact: true })
    .getByRole('menuitem', { name: item, exact: true })
    .click();
}

/** Returns the text box labelled `label` inside the client editor `dialog`. */
function field(dialog: Locator, label: string): Locator {
  return dialog.getByRole('textbox', { name: label, exact: true });
}

/** Returns the message box titled `$ErrorDuringSave`. */
function errorDuringSaveBox(page: Page): Locator {
  return page.getByRole('dialog', { name: ErrorDuringSave, exact: true });
}

/** Waits for the `$ErrorDuringSave` box holding `text`, then closes it with its OK button. */
async function acknowledgeSaveError(page: Page, text: string): Promise<void> {
  const errorBox = errorDuringSaveBox(page);
  await expect(errorBox).toBeVisible();
  await expect(errorBox).toContainText(text);
  await errorBox.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(errorBox).toHaveCount(0);
}

/** Resolves with the next response of `method` to a URL under /api/app/edit-locks/. */
function nextEditLockResponse(page: Page, method: 'GET' | 'DELETE') {
  return page.waitForResponse(
    (response) => response.request().method() === method && response.url().includes(EDIT_LOCKS_PATH),
  );
}

test.describe('admin clients', () => {
  test('administrator creates and updates a client, with validation errors', async ({ page }) => {
    const suffix = Date.now().toString(36);
    const clientName = `E2E Client ${suffix}`;
    const renamedClient = `E2E Client ${suffix} Renamed`;

    await adminLogonAsAdministrator(page);

    // Add Client with Street left empty.
    await openMenu(page, 'Clients', 'Add Client');
    const addDialog = page.getByRole('dialog', { name: ADD_CLIENT_DIALOG, exact: true });
    await expect(addDialog).toBeVisible();

    await field(addDialog, 'Name').fill(clientName);
    await field(addDialog, 'City').fill(CITY);
    await field(addDialog, 'Country').fill(COUNTRY);
    await field(addDialog, 'Phone').fill(PHONE);
    await field(addDialog, 'Fax').fill(FAX);
    await field(addDialog, 'Email').fill(EMAIL);
    await field(addDialog, 'URL').fill(WEB_SITE);
    await expect(field(addDialog, 'Street')).toHaveValue('');
    await addDialog.getByRole('button', { name: 'OK', exact: true }).click();

    await acknowledgeSaveError(page, AddressStreetRequired);
    await expect(addDialog).toBeVisible();

    // Add Client with Street filled in.
    await field(addDialog, 'Street').fill(STREET);
    await addDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(addDialog).toHaveCount(0);
    await expect(errorDuringSaveBox(page)).toHaveCount(0);

    // The new client in the Clients list.
    await openMenu(page, 'Clients', 'View Clients');
    const clientsTab = page.getByRole('tabpanel', { name: CLIENTS_TAB, exact: true });
    const clientsTable = clientsTab.getByRole('table', { name: CLIENTS_TAB, exact: true });
    const newClientRow = clientsTable.getByRole('row', { name: clientName, exact: true });
    await expect(newClientRow).toBeVisible();

    // Edit Client opened on the new client, with no edit-lock prompt.
    await newClientRow.click();
    await expect(newClientRow).toHaveAttribute('aria-selected', 'true');
    const firstLockStatus = nextEditLockResponse(page, 'GET');
    await clientsTab.getByRole('button', { name: 'Edit', exact: true }).click();
    const editDialog = page.getByRole('dialog', { name: EDIT_CLIENT_DIALOG, exact: true });
    await expect(editDialog).toBeVisible();
    expect((await firstLockStatus).status()).toBe(200);
    await expect(field(editDialog, 'Name')).toHaveValue(clientName);
    await expect(page.getByRole('dialog', { name: ThingAlreadyBeingEdited })).toHaveCount(0);

    // Rename to the seeded client's name.
    await field(editDialog, 'Name').fill(SEEDED_CLIENT);
    await editDialog.getByRole('button', { name: 'OK', exact: true }).click();

    await acknowledgeSaveError(page, ClientWithSameNameExists);
    await expect(editDialog).toBeVisible();
    await expect(field(editDialog, 'Name')).toHaveValue(SEEDED_CLIENT);

    // Rename to the unique name; the save clears the edit lock before the editor closes.
    await field(editDialog, 'Name').fill(renamedClient);
    const unlockAfterSave = nextEditLockResponse(page, 'DELETE');
    await editDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(editDialog).toHaveCount(0);
    expect((await unlockAfterSave).status()).toBe(200);
    await expect(errorDuringSaveBox(page)).toHaveCount(0);

    // The list shows the new name only.
    const renamedRow = clientsTable.getByRole('row', { name: renamedClient, exact: true });
    await expect(renamedRow).toBeVisible();
    await expect(clientsTable.getByRole('row', { name: clientName, exact: true })).toHaveCount(0);
    await expect(clientsTable.getByRole('row', { name: SEEDED_CLIENT, exact: true })).toHaveCount(1);

    // Reopen the renamed client and close it unchanged.
    await renamedRow.click();
    await expect(renamedRow).toHaveAttribute('aria-selected', 'true');
    const secondLockStatus = nextEditLockResponse(page, 'GET');
    await clientsTab.getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(editDialog).toBeVisible();
    expect((await secondLockStatus).status()).toBe(200);
    await expect(field(editDialog, 'Name')).toHaveValue(renamedClient);
    await expect(field(editDialog, 'Street')).toHaveValue(STREET);
    await expect(page.getByRole('dialog', { name: ThingAlreadyBeingEdited })).toHaveCount(0);

    const unlockAfterCancel = nextEditLockResponse(page, 'DELETE');
    await editDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(editDialog).toHaveCount(0);
    expect((await unlockAfterCancel).status()).toBe(200);
    await expect(page.getByRole('dialog', { name: OutstandingChanges, exact: true })).toHaveCount(0);
  });
});
