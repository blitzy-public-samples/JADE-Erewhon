import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../../api/client';
import type {
  AdminItemSearchRequest,
  AgentDetailsResponse,
  Id,
  ItemCategoryRow,
  ItemDetailsResponse,
  ItemListRow,
  SoftLockStatus,
} from '../../api/types';
import { currencyFormat } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { EditRetailItemDialog } from './EditRetailItemDialog';

const RETAIL_ITEM_CAPTION = 'Retail Sale Item';
const AGENT_CAPTION = 'Agent';
const PREFIX_CAPTION = 'Prefix';
const CATEGORY_CAPTION = 'Category';
const COUNTRY_CAPTION = 'Country';
const REGION_CAPTION = 'Region';
const NAME_CAPTION = 'Name';
const DESC_CAPTION = 'Desc';
const RETAIL_PRICE_CAPTION = 'Retail Price';
const OK_CAPTION = 'OK';
const NO_CAPTION = 'No';
const SOLD_TO_START = 'Sold to ';

const SOLD_STATE_CAPTIONS: readonly string[] = [
  CATEGORY_CAPTION,
  COUNTRY_CAPTION,
  REGION_CAPTION,
  NAME_CAPTION,
  DESC_CAPTION,
  RETAIL_PRICE_CAPTION,
];

const ASYNC_TIMEOUT = { timeout: 15_000 };

const SEARCH_PATH = '/api/app/admin/items/search';

interface RenderedDialog {
  readonly user: UserEvent;
  readonly onClose: Mock<(saved: boolean) => void>;
  readonly dialog: HTMLElement;
}

function fixtureId(handle: FixtureHandle, key: keyof FixtureIds): Id {
  const id = handle.ids[key];
  if (id === undefined) {
    throw new Error(`The AdminGuards fixture returned no ${key} id`);
  }
  return id;
}

function itemDetails(id: Id): Promise<ItemDetailsResponse> {
  return api.get<ItemDetailsResponse>(`/api/app/items/${id}`);
}

async function agentLabel(agentId: Id): Promise<string> {
  const { agent } = await api.get<AgentDetailsResponse>(`/api/app/admin/agents/${agentId}`);
  return [agent.name, agent.street, agent.city, agent.country].join(', ');
}

function soldToText(details: ItemDetailsResponse): string {
  const sale = details.item.sale;
  if (sale === null) {
    throw new Error(`Item ${details.item.id} has no sale`);
  }
  return `${SOLD_TO_START}${sale.client.name} for ${currencyFormat(details.item.price)}`;
}

async function dismissLockPromptIfLocked(user: UserEvent, itemId: Id): Promise<void> {
  const status = await api.get<SoftLockStatus>(`/api/app/edit-locks/RetailItem/${itemId}`);
  if (!status.isLocked) {
    return;
  }
  const prompt = await screen.findByRole(
    'dialog',
    { name: msg('ThingAlreadyBeingEdited', status.thing || msg('Thing')) },
    ASYNC_TIMEOUT,
  );
  await user.click(within(prompt).getByRole('button', { name: NO_CAPTION }));
  await waitFor(() => {
    expect(prompt).not.toBeInTheDocument();
  }, ASYNC_TIMEOUT);
}

async function renderDialog(handle: FixtureHandle, itemId?: Id): Promise<RenderedDialog> {
  const user = userEvent.setup();
  const onClose = vi.fn<(saved: boolean) => void>();
  renderWithSession(<EditRetailItemDialog itemId={itemId} onClose={onClose} />, handle);

  const title = `${msg(itemId === undefined ? 'Add' : 'Edit')} ${RETAIL_ITEM_CAPTION}`;
  const dialog = await screen.findByRole('dialog', { name: title }, ASYNC_TIMEOUT);
  await within(dialog).findByLabelText(CATEGORY_CAPTION, {}, ASYNC_TIMEOUT);
  if (itemId !== undefined) {
    await dismissLockPromptIfLocked(user, itemId);
  }
  return { user, onClose, dialog };
}

function field(dialog: HTMLElement, caption: string): HTMLElement {
  return within(dialog).getByLabelText(caption);
}

function agentSelector(dialog: HTMLElement): HTMLElement | null {
  return within(dialog).queryByRole('combobox', { name: AGENT_CAPTION });
}

function expectAgentLabel(dialog: HTMLElement, expected: string): void {
  const group = within(dialog).getByRole('group', { name: AGENT_CAPTION });
  expect(within(group).getByText(expected)).toBeInTheDocument();
}

function expectSoldStateFields(dialog: HTMLElement, enabled: boolean): void {
  for (const caption of SOLD_STATE_CAPTIONS) {
    if (enabled) {
      expect(field(dialog, caption), caption).toBeEnabled();
    } else {
      expect(field(dialog, caption), caption).toBeDisabled();
    }
  }
}

describe('EditRetailItemDialog', () => {
  describe('in an agent session', () => {
    const fixture = useFixture('AdminGuards');

    it('disables a sold item for the agent', async () => {
      const id = fixtureId(fixture, 'soldRetailItem');
      const [details, expectedLabel] = await Promise.all([
        itemDetails(id),
        agentLabel(fixtureId(fixture, 'agent')),
      ]);
      expect(details.item.isSold).toBe(true);

      const { onClose, dialog } = await renderDialog(fixture, id);

      expectSoldStateFields(dialog, false);
      expect(agentSelector(dialog)).not.toBeInTheDocument();
      expectAgentLabel(dialog, expectedLabel);
      const soldState = within(dialog).getByText((content) => content.startsWith(SOLD_TO_START));
      expect(soldState.textContent).toBe(soldToText(details));
      expect(onClose).not.toHaveBeenCalled();
    });

    it('keeps an unsold own item editable', async () => {
      const agentId = fixtureId(fixture, 'agent');
      const id = fixtureId(fixture, 'retailItem');
      const [details, expectedLabel] = await Promise.all([itemDetails(id), agentLabel(agentId)]);
      expect(details.item.isSold).toBe(false);
      expect(details.item.agentId).toBe(agentId);

      const { onClose, dialog } = await renderDialog(fixture, id);

      expectSoldStateFields(dialog, true);
      expect(field(dialog, COUNTRY_CAPTION)).toHaveValue(String(details.item.countryId));
      expect(field(dialog, REGION_CAPTION)).toBeEnabled();
      expect(agentSelector(dialog)).not.toBeInTheDocument();
      expectAgentLabel(dialog, expectedLabel);
      expect(within(dialog).getByText(msg('NotYetSold'))).toBeInTheDocument();
      expect(onClose).not.toHaveBeenCalled();
    });

    it('assigns the session agent to a new item', async () => {
      const agentId = fixtureId(fixture, 'agent');
      const categoryId = fixtureId(fixture, 'itemCategory');
      const countryId = fixtureId(fixture, 'country');
      const regionId = fixtureId(fixture, 'region');
      const [expectedLabel, category] = await Promise.all([
        agentLabel(agentId),
        api.get<ItemCategoryRow>(`/api/app/admin/item-categories/${categoryId}`),
      ]);
      const name = `Agent Lamp ${Date.now().toString(36)}`;

      const { user, onClose, dialog } = await renderDialog(fixture);

      expect(agentSelector(dialog)).not.toBeInTheDocument();
      expectAgentLabel(dialog, expectedLabel);
      expect(field(dialog, REGION_CAPTION)).toBeEnabled();

      await user.selectOptions(field(dialog, CATEGORY_CAPTION), String(categoryId));
      expect(field(dialog, PREFIX_CAPTION)).toHaveValue(category.prefix);
      await user.selectOptions(field(dialog, COUNTRY_CAPTION), String(countryId));
      await user.selectOptions(field(dialog, REGION_CAPTION), String(regionId));
      await user.type(field(dialog, NAME_CAPTION), name);
      await user.type(field(dialog, DESC_CAPTION), 'A lamp listed by its agent');
      await user.type(field(dialog, RETAIL_PRICE_CAPTION), '123.45');
      await user.click(within(dialog).getByRole('button', { name: OK_CAPTION }));

      await waitFor(() => {
        expect(onClose).toHaveBeenCalledTimes(1);
      }, ASYNC_TIMEOUT);
      expect(onClose).toHaveBeenCalledWith(true);

      const search: AdminItemSearchRequest = {
        agentId,
        clientId: null,
        countryId: null,
        regionId: null,
        itemCategoryId: null,
        onlyRetailItems: false,
        onlyTenderItems: false,
        priceLow: 0,
        priceHigh: 0,
        includeSoldItems: true,
        includeClosedTenders: true,
      };
      const rows = await api.post<ItemListRow[]>(SEARCH_PATH, search);
      const added = rows.filter((row) => row.item.name === name);
      expect(added).toHaveLength(1);
      const [row] = added;
      expect(row?.item.agentId).toBe(agentId);
      expect(row?.item.isTender).toBe(false);
      expect(row?.item.itemCategoryId).toBe(categoryId);
      expect(row?.item.regionId).toBe(regionId);
      expect(row?.item.price).toBe(123.45);
    });
  });

  describe('in an administrator session', () => {
    const fixture = useFixture('AdminGuards', { session: 'administrator' });

    it('disables the agent selector for a sold item for the administrator', async () => {
      const id = fixtureId(fixture, 'soldRetailItem');
      const details = await itemDetails(id);
      const itemAgent = details.item.agent;
      if (itemAgent === null) {
        throw new Error(`Item ${id} has no agent`);
      }
      expect(details.item.isSold).toBe(true);

      const { onClose, dialog } = await renderDialog(fixture, id);

      const selector = agentSelector(dialog);
      expect(selector).toBeInTheDocument();
      expect(selector).toHaveValue(String(itemAgent.id));
      expect(selector).toHaveDisplayValue(itemAgent.name);
      expect(selector).toBeDisabled();
      expectSoldStateFields(dialog, false);
      expect(onClose).not.toHaveBeenCalled();
    });

    it('enables the agent selector for an unsold item for the administrator', async () => {
      const id = fixtureId(fixture, 'retailItem');
      const details = await itemDetails(id);
      expect(details.item.isSold).toBe(false);

      const { onClose, dialog } = await renderDialog(fixture, id);

      const selector = agentSelector(dialog);
      expect(selector).toBeInTheDocument();
      expect(selector).toHaveValue(String(details.item.agentId));
      expect(selector).toBeEnabled();
      expectSoldStateFields(dialog, true);
      expect(onClose).not.toHaveBeenCalled();
    });
  });
});
