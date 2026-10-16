import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { screen, within } from '@testing-library/react';
import { api } from '../../api/client';
import type {
  AgentProjection,
  ClosureScheduleStateResponse,
  Id,
  IsoDate,
  ItemDetailsResponse,
  TenderDetailsResponse,
} from '../../api/types';
import { currencyFormat, decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { EditTenderItemDialog } from './EditTenderItemDialog';

const TENDER_ITEM_CAPTION = 'Tender Sale Item';
const AGENT_CAPTION = 'Agent';
const CATEGORY_CAPTION = 'Category';
const COUNTRY_CAPTION = 'Country';
const REGION_CAPTION = 'Region';
const NAME_CAPTION = 'Name';
const DESC_CAPTION = 'Desc';
const RESERVE_CAPTION = 'Reserve';
const CLOSURE_DATE_CAPTION = 'Closure Date';
const SOLD_TO_START = 'Sold to ';

const SOLD_STATE_CAPTIONS: readonly string[] = [
  CATEGORY_CAPTION,
  COUNTRY_CAPTION,
  REGION_CAPTION,
  NAME_CAPTION,
  DESC_CAPTION,
  RESERVE_CAPTION,
  CLOSURE_DATE_CAPTION,
];

const NEW_ITEM_CLOSURE_DAYS = 7;
const MS_PER_DAY = 86_400_000;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
const ISO_DATE_LENGTH = 'yyyy-MM-dd'.length;

const ASYNC_TIMEOUT = { timeout: 15_000 };

const CLOSURE_SCHEDULE_PATH = '/api/app/admin/tenders/closure-schedule';

interface RenderedDialog {
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

function tenderDetails(id: Id): Promise<TenderDetailsResponse> {
  return api.get<TenderDetailsResponse>(`/api/app/admin/tenders/${id}`);
}

function itemAgent(details: ItemDetailsResponse): AgentProjection {
  const { agent } = details.item;
  if (agent === null) {
    throw new Error(`Item ${details.item.id} has no agent`);
  }
  return agent;
}

function agentLabel(agent: AgentProjection): string {
  return [agent.name, agent.street, agent.city, agent.country].join(', ');
}

function soldToText(details: ItemDetailsResponse): string {
  const { sale } = details.item;
  if (sale === null || sale.offer === null) {
    throw new Error(`Item ${details.item.id} has no tender sale`);
  }
  return `${SOLD_TO_START}${sale.client.name} for ${currencyFormat(sale.offer)}`;
}

function highestBidText(highest: TenderDetailsResponse): string {
  const tendered = msg('TenderedByClient', highest.tender.client.name, currencyFormat(highest.tender.offer));
  return `${msg('NotYetSold')}. ${msg('HighestBid', tendered)}`;
}

function daysAfter(iso: IsoDate, days: number): IsoDate {
  if (!ISO_DATE.test(iso)) {
    throw new Error(`The server date '${iso}' is not a yyyy-MM-dd date`);
  }
  const start = Date.parse(`${iso}T00:00:00Z`);
  return new Date(start + days * MS_PER_DAY).toISOString().slice(0, ISO_DATE_LENGTH);
}

async function renderDialog(handle: FixtureHandle, itemId?: Id): Promise<RenderedDialog> {
  const onClose = vi.fn<(saved: boolean) => void>();
  renderWithSession(<EditTenderItemDialog itemId={itemId} onClose={onClose} />, handle);

  const title = `${msg(itemId === undefined ? 'Add' : 'Edit')} ${TENDER_ITEM_CAPTION}`;
  const dialog = await screen.findByRole('dialog', { name: title }, ASYNC_TIMEOUT);
  await within(dialog).findByLabelText(CLOSURE_DATE_CAPTION, {}, ASYNC_TIMEOUT);
  return { onClose, dialog };
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

function expectStoredTenderValues(dialog: HTMLElement, details: ItemDetailsResponse): void {
  expect(field(dialog, RESERVE_CAPTION)).toHaveValue(decimalString(details.item.price));
  expect(field(dialog, CLOSURE_DATE_CAPTION)).toHaveValue(details.item.closureDate);
}

async function expectUnsoldItemEditable(
  handle: FixtureHandle,
  itemKey: keyof FixtureIds,
  tenderKey: keyof FixtureIds,
  closed: boolean,
): Promise<void> {
  const agentId = fixtureId(handle, 'agent');
  const id = fixtureId(handle, itemKey);
  const [details, highest] = await Promise.all([itemDetails(id), tenderDetails(fixtureId(handle, tenderKey))]);
  expect(details.item.isTender).toBe(true);
  expect(details.item.isSold).toBe(false);
  expect(details.isClosed).toBe(closed);
  expect(details.item.agentId).toBe(agentId);
  expect(highest.tender.tenderItem.id).toBe(id);

  const { onClose, dialog } = await renderDialog(handle, id);

  expectSoldStateFields(dialog, true);
  expect(field(dialog, REGION_CAPTION)).toHaveValue(String(details.item.regionId));
  expectStoredTenderValues(dialog, details);
  expect(agentSelector(dialog)).not.toBeInTheDocument();
  expectAgentLabel(dialog, agentLabel(itemAgent(details)));
  expect(within(dialog).getByText(highestBidText(highest))).toBeInTheDocument();
  expect(onClose).not.toHaveBeenCalled();
}

describe('EditTenderItemDialog', () => {
  describe('in an agent session', () => {
    const fixture = useFixture('AdminGuards');

    it('disables a sold tender item for the agent', async () => {
      const id = fixtureId(fixture, 'soldTenderItem');
      const details = await itemDetails(id);
      expect(details.item.isTender).toBe(true);
      expect(details.item.isSold).toBe(true);
      expect(details.item.agentId).toBe(fixtureId(fixture, 'agent'));

      const { onClose, dialog } = await renderDialog(fixture, id);

      expectSoldStateFields(dialog, false);
      expectStoredTenderValues(dialog, details);
      expect(agentSelector(dialog)).not.toBeInTheDocument();
      expectAgentLabel(dialog, agentLabel(itemAgent(details)));
      const soldState = within(dialog).getByText((content) => content.startsWith(SOLD_TO_START));
      expect(soldState.textContent).toBe(soldToText(details));
      expect(onClose).not.toHaveBeenCalled();
    });

    it('keeps an unsold tender item editable', async () => {
      await expectUnsoldItemEditable(fixture, 'tenderItem', 'openTender', false);
    });

    it('keeps a closed unsold tender item editable', async () => {
      await expectUnsoldItemEditable(fixture, 'closedTenderItem', 'closedTender', true);
    });

    it('defaults a new closure date to a week after the server date', async () => {
      const { onClose, dialog } = await renderDialog(fixture);
      const { serverDate } = await api.get<ClosureScheduleStateResponse>(CLOSURE_SCHEDULE_PATH);

      const closureDate = field(dialog, CLOSURE_DATE_CAPTION);
      expect(closureDate).toBeEnabled();
      expect(closureDate).toHaveValue(daysAfter(serverDate, NEW_ITEM_CLOSURE_DAYS));
      const reserve = field(dialog, RESERVE_CAPTION);
      expect(reserve).toBeEnabled();
      expect(reserve).toHaveValue('');
      expect(field(dialog, REGION_CAPTION)).toBeEnabled();
      expect(onClose).not.toHaveBeenCalled();
    });
  });

  describe('in an administrator session', () => {
    const fixture = useFixture('AdminGuards', { session: 'administrator' });

    it('disables the agent selector for a sold tender item for the administrator', async () => {
      const id = fixtureId(fixture, 'soldTenderItem');
      const details = await itemDetails(id);
      const agent = itemAgent(details);
      expect(details.item.isTender).toBe(true);
      expect(details.item.isSold).toBe(true);

      const { onClose, dialog } = await renderDialog(fixture, id);

      const selector = agentSelector(dialog);
      expect(selector).toBeInTheDocument();
      expect(selector).toHaveValue(String(agent.id));
      expect(selector).toHaveDisplayValue(agent.name);
      expect(selector).toBeDisabled();
      expectSoldStateFields(dialog, false);
      expectStoredTenderValues(dialog, details);
      expect(onClose).not.toHaveBeenCalled();
    });
  });
});
