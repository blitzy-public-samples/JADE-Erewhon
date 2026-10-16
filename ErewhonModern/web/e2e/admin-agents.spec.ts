// End-to-end flow of the admin agent screens: administrator logon, Agents › Add Agent with an invalid e-mail and
// then a valid one, Agents › View Agents, Edit with a duplicate name and then a valid update, and reopening the
// saved agent.

import { test, expect, type Page } from '@playwright/test';

// $ErrorDuringSave
const ErrorDuringSave = 'Error during save';
// $AddressEmailInvalidFormat
const AddressEmailInvalidFormat = 'Email must be in a valid format e.g. user@domain.com';
// $AgentWithSameNameExists
const AgentWithSameNameExists = 'An agent with the same name already exists';
// $Administration - $CompanyName
const AdministrationCaption = 'Administration - Erewhon Investments Inc';
// $OutstandingChanges
const OutstandingChanges = 'Outstanding Changes';
// $ThingAlreadyBeingEdited
const AlreadyBeingEdited = / is already being edited$/;

const SeededAgentName = 'Peter Smallsmith';
const EditLocksPath = '/api/app/edit-locks/';

/** Opens /admin, logs on with the Administrator option and waits for the shell caption. */
async function adminLogonAsAdministrator(page: Page): Promise<void> {
  await page.goto('/admin');
  const administrator = page.getByRole('radio', { name: 'Administrator', exact: true });
  await expect(administrator).toBeVisible();
  if (!(await administrator.isChecked())) {
    await administrator.check();
  }
  await expect(administrator).toBeChecked();
  await page.getByRole('button', { name: 'OK', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1 })).toContainText(AdministrationCaption);
}

/** Clicks the menu bar's `menu`, then its entry `item`. */
async function openMenu(page: Page, menu: string, item: string): Promise<void> {
  const menuBar = page.getByRole('menubar');
  await menuBar.getByRole('menuitem', { name: menu, exact: true }).click();
  await menuBar
    .getByRole('menu', { name: menu, exact: true })
    .getByRole('menuitem', { name: item, exact: true })
    .click();
}

/** Returns the "Agents" list page of the admin shell. */
function agentsListOf(page: Page) {
  return page.getByRole('tabpanel', { name: 'Agents', exact: true });
}

/**
 * Selects the row `agentName` of the agents list, clicks Edit and returns the "Edit Agent" dialog once its Name shows
 * `agentName`, after checking that the agent's edit lock was reported free and no "already being edited" prompt shows.
 */
async function openAgentEditor(page: Page, agentName: string) {
  const agentsList = agentsListOf(page);
  const row = agentsList.getByRole('row', { name: agentName, exact: true });
  await row.click();
  await expect(row).toHaveAttribute('aria-selected', 'true');

  const lockStatus = page.waitForResponse(
    (response) => response.request().method() === 'GET' && response.url().includes(EditLocksPath),
  );
  await agentsList.getByRole('button', { name: 'Edit', exact: true }).click();

  const editDialog = page.getByRole('dialog', { name: 'Edit Agent', exact: true });
  await expect(editDialog).toBeVisible();
  await expect(editDialog.getByLabel('Name', { exact: true })).toHaveValue(agentName);

  const statusResponse = await lockStatus;
  expect(statusResponse.ok()).toBe(true);
  const status: { isLocked?: boolean } = await statusResponse.json();
  expect(status.isLocked).toBe(false);
  await expect(page.getByRole('dialog', { name: AlreadyBeingEdited })).toHaveCount(0);

  return editDialog;
}

test.describe('admin agents', () => {
  test('administrator creates and updates an agent, with validation errors', async ({ page }) => {
    const suffix = Date.now().toString(36);
    const agentName = `E2E Agent ${suffix}`;
    const saveErrorDialog = page.getByRole('dialog', { name: ErrorDuringSave, exact: true });

    await adminLogonAsAdministrator(page);

    // Agents › Add Agent: an e-mail without a domain suffix is rejected and the editor stays open.
    await openMenu(page, 'Agents', 'Add Agent');
    const addDialog = page.getByRole('dialog', { name: 'Add Agent', exact: true });
    await expect(addDialog).toBeVisible();
    await addDialog.getByLabel('Name', { exact: true }).fill(agentName);
    await addDialog.getByLabel('Street', { exact: true }).fill('1 Test Street');
    await addDialog.getByLabel('City', { exact: true }).fill('Christchurch');
    await addDialog.getByLabel('Country', { exact: true }).fill('New Zealand');
    await addDialog.getByLabel('Phone', { exact: true }).fill('03 555 0100');
    await addDialog.getByLabel('Fax', { exact: true }).fill('03 555 0101');
    await addDialog.getByLabel('Email', { exact: true }).fill('e2e@agent');
    await addDialog.getByLabel('URL', { exact: true }).fill('www.example.com');
    await addDialog.getByRole('button', { name: 'OK', exact: true }).click();

    await expect(saveErrorDialog).toBeVisible();
    await expect(saveErrorDialog).toContainText(AddressEmailInvalidFormat);
    await saveErrorDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(saveErrorDialog).toHaveCount(0);
    await expect(addDialog).toBeVisible();

    // A valid e-mail saves the new agent and closes the editor.
    await addDialog.getByLabel('Email', { exact: true }).fill('e2e.agent@example.com');
    await addDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(addDialog).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Agents › View Agents lists the new agent.
    await openMenu(page, 'Agents', 'View Agents');
    await expect(agentsListOf(page).getByRole('row', { name: agentName, exact: true })).toBeVisible();

    // Edit: the name of another agent is rejected and the editor stays open.
    const editDialog = await openAgentEditor(page, agentName);
    const nameField = editDialog.getByLabel('Name', { exact: true });
    await nameField.fill(SeededAgentName);
    await editDialog.getByRole('button', { name: 'OK', exact: true }).click();

    await expect(saveErrorDialog).toBeVisible();
    await expect(saveErrorDialog).toContainText(AgentWithSameNameExists);
    await saveErrorDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(saveErrorDialog).toHaveCount(0);
    await expect(editDialog).toBeVisible();

    // The agent's own name and a new city save, close the editor and release the edit lock.
    await nameField.fill(agentName);
    await editDialog.getByLabel('City', { exact: true }).fill('Dunedin');
    const unlock = page.waitForResponse(
      (response) => response.request().method() === 'DELETE' && response.url().includes(EditLocksPath),
    );
    await editDialog.getByRole('button', { name: 'OK', exact: true }).click();
    await expect(editDialog).toHaveCount(0);
    expect((await unlock).ok()).toBe(true);
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Reopening shows the saved values; Cancel without changes closes with no outstanding-changes question.
    const reopenedDialog = await openAgentEditor(page, agentName);
    await expect(reopenedDialog.getByLabel('City', { exact: true })).toHaveValue('Dunedin');
    await expect(reopenedDialog.getByLabel('Name', { exact: true })).toHaveValue(agentName);
    await reopenedDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(reopenedDialog).toHaveCount(0);
    await expect(page.getByRole('dialog', { name: OutstandingChanges, exact: true })).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
