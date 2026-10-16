import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { ApiError, api } from '../../api/client';
import type { Id, TenderDetailsResponse } from '../../api/types';
import { currencyFormat } from '../../format';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import type { FixtureHandle, FixtureIds } from '../../test/fixtures';
import { ViewTenderDialog } from './ViewTenderDialog';

const VIEW_TENDER_CAPTION = 'View Tender';
const DELETE_CAPTION = 'Delete';
const CLOSE_CAPTION = 'Close';
const CLIENT_CAPTION = 'Client';
const OFFER_CAPTION = 'Offer';
const YES_CAPTION = 'Yes';
const NO_CAPTION = 'No';

type TenderKey = 'tender' | 'otherTender' | 'openTender' | 'closedTender';

const TENDER_KEYS: readonly TenderKey[] = ['tender', 'otherTender', 'openTender', 'closedTender'];

type TenderKind = 'winning' | 'soldOther' | 'open';

const TENDER_KIND_DESCRIPTIONS: Readonly<Record<TenderKind, string>> = {
  winning: 'winning tender (isWinning)',
  soldOther: 'losing tender on a sold item (itemSold and not isWinning)',
  open: 'tender on an unsold item (not itemSold)',
};

interface ResolvedTender {
  readonly key: TenderKey;
  readonly id: Id;
  readonly details: TenderDetailsResponse;
}

type ResolvedTenders = Partial<Record<TenderKind, ResolvedTender>>;

interface RenderedDialog {
  readonly user: UserEvent;
  readonly onClose: Mock<(deleted: boolean) => void>;
  readonly dialog: HTMLElement;
}

function details(id: Id): Promise<TenderDetailsResponse> {
  return api.get<TenderDetailsResponse>(`/api/app/admin/tenders/${id}`);
}

async function resolveTenders(ids: FixtureIds): Promise<ResolvedTenders> {
  const loaded: ResolvedTender[] = [];
  for (const key of TENDER_KEYS) {
    const id = ids[key];
    if (id !== undefined) {
      loaded.push({ key, id, details: await details(id) });
    }
  }

  const unsold = loaded.filter((tender) => !tender.details.itemSold);
  return {
    winning: loaded.find((tender) => tender.details.isWinning),
    soldOther: loaded.find((tender) => tender.details.itemSold && !tender.details.isWinning),
    open: unsold.find((tender) => tender.key === 'openTender') ?? unsold.at(0),
  };
}

function need(resolved: ResolvedTenders, kind: TenderKind): ResolvedTender {
  const tender = resolved[kind];
  if (tender === undefined) {
    throw new Error(
      `The AdminGuards fixture returned no ${TENDER_KIND_DESCRIPTIONS[kind]} among the ids ${TENDER_KEYS.join(', ')}`,
    );
  }
  return tender;
}

async function tenderOf(handle: FixtureHandle, kind: TenderKind): Promise<ResolvedTender> {
  return need(await resolveTenders(handle.ids), kind);
}

function soldText(tender: TenderDetailsResponse): string {
  const { winnerClientName, winningOffer } = tender;
  if (winnerClientName === null || winningOffer === null) {
    throw new Error(`Tender ${tender.tender.id} is on a sold item but has no buyer or winning offer`);
  }
  return msg('SoldToClientFor', winnerClientName, currencyFormat(winningOffer));
}

async function lookupFailure(id: Id): Promise<unknown> {
  return details(id).then(
    () => undefined,
    (error: unknown) => error,
  );
}

async function renderDialog(handle: FixtureHandle, tenderId: Id): Promise<RenderedDialog> {
  const user = userEvent.setup();
  const onClose = vi.fn<(deleted: boolean) => void>();
  renderWithSession(<ViewTenderDialog tenderId={tenderId} onClose={onClose} />, handle);

  const dialog = screen.getByRole('dialog', { name: VIEW_TENDER_CAPTION });
  await waitFor(() => {
    expect(dialog).toHaveAttribute('aria-busy', 'false');
  });
  return { user, onClose, dialog };
}

function deleteButton(dialog: HTMLElement): HTMLElement {
  return within(dialog).getByRole('button', { name: DELETE_CAPTION });
}

function soldStatus(dialog: HTMLElement): HTMLElement {
  return within(dialog).getByRole('status');
}

async function openDeleteConfirmation(user: UserEvent, dialog: HTMLElement): Promise<HTMLElement> {
  await user.click(deleteButton(dialog));

  const confirmation = await screen.findByRole('dialog', { name: msg('DeleteTender') });
  expect(confirmation).toHaveAccessibleDescription(msg('ConfirmDeleteTender'));
  expect(within(confirmation).getByText(msg('ConfirmDeleteTender'))).toBeInTheDocument();
  expect(within(confirmation).getByRole('button', { name: YES_CAPTION })).toBeInTheDocument();
  expect(within(confirmation).getByRole('button', { name: NO_CAPTION })).toBeInTheDocument();
  return confirmation;
}

describe('ViewTenderDialog', () => {
  describe('agent session', () => {
    const fixture = useFixture('AdminGuards', { session: 'agent' });

    it('shows the sale of a sold item and disables Delete on a losing tender', async () => {
      const resolved = await resolveTenders(fixture.ids);
      const winning = need(resolved, 'winning');
      const soldOther = need(resolved, 'soldOther');
      expect(soldOther.details.winningOffer).toBe(winning.details.tender.offer);

      const { dialog, onClose } = await renderDialog(fixture, soldOther.id);

      expect(soldStatus(dialog)).toHaveTextContent(soldText(soldOther.details));
      expect(deleteButton(dialog)).toBeDisabled();
      expect(onClose).not.toHaveBeenCalled();
    });

    it('shows no sale and disables Delete on a tender of an unsold item', async () => {
      const open = await tenderOf(fixture, 'open');

      const { dialog, onClose } = await renderDialog(fixture, open.id);

      expect(soldStatus(dialog)).toBeEmptyDOMElement();
      expect(deleteButton(dialog)).toBeDisabled();
      expect(onClose).not.toHaveBeenCalled();
    });
  });

  describe('administrator session', () => {
    const fixture = useFixture('AdminGuards', { session: 'administrator' });

    it('shows the sale and disables Delete on the winning tender', async () => {
      const winning = await tenderOf(fixture, 'winning');

      const { dialog, onClose } = await renderDialog(fixture, winning.id);

      expect(soldStatus(dialog)).toHaveTextContent(soldText(winning.details));
      expect(deleteButton(dialog)).toBeDisabled();
      expect(onClose).not.toHaveBeenCalled();
    });

    it('keeps the tender when the deletion is declined', async () => {
      const soldOther = await tenderOf(fixture, 'soldOther');
      const { user, dialog, onClose } = await renderDialog(fixture, soldOther.id);
      expect(deleteButton(dialog)).toBeEnabled();

      const confirmation = await openDeleteConfirmation(user, dialog);
      await user.click(within(confirmation).getByRole('button', { name: NO_CAPTION }));

      await waitFor(() => {
        expect(screen.queryByRole('dialog', { name: msg('DeleteTender') })).not.toBeInTheDocument();
      });
      expect(screen.getByRole('dialog', { name: VIEW_TENDER_CAPTION })).toBeInTheDocument();
      expect(deleteButton(dialog)).toBeEnabled();
      expect(onClose).not.toHaveBeenCalled();
      await expect(details(soldOther.id)).resolves.toMatchObject({ tender: { id: soldOther.id } });
    });

    it('deletes a losing tender once the deletion is confirmed', async () => {
      const soldOther = await tenderOf(fixture, 'soldOther');
      const { user, dialog, onClose } = await renderDialog(fixture, soldOther.id);
      expect(deleteButton(dialog)).toBeEnabled();

      const confirmation = await openDeleteConfirmation(user, dialog);
      await user.click(within(confirmation).getByRole('button', { name: YES_CAPTION }));

      await waitFor(() => {
        expect(onClose).toHaveBeenCalledTimes(1);
      });
      expect(onClose).toHaveBeenCalledWith(true);
      expect(screen.queryByRole('dialog', { name: msg('DeleteTender') })).not.toBeInTheDocument();

      const failure = await lookupFailure(soldOther.id);
      expect(failure).toBeInstanceOf(ApiError);
      expect(failure).toMatchObject({ status: 404 });
    });

    it('closes without deleting from Close', async () => {
      const winning = await tenderOf(fixture, 'winning');
      const { user, dialog, onClose } = await renderDialog(fixture, winning.id);

      await user.click(within(dialog).getByRole('button', { name: CLOSE_CAPTION }));

      expect(onClose).toHaveBeenCalledTimes(1);
      expect(onClose).toHaveBeenCalledWith(false);
      await expect(details(winning.id)).resolves.toMatchObject({ tender: { id: winning.id } });
    });

    it('shows the client and offer of the bid', async () => {
      const open = await tenderOf(fixture, 'open');

      const { dialog } = await renderDialog(fixture, open.id);

      expect(within(dialog).getByLabelText(CLIENT_CAPTION)).toHaveValue(open.details.tender.client.name);
      expect(within(dialog).getByLabelText(OFFER_CAPTION)).toHaveValue(currencyFormat(open.details.tender.offer));
    });
  });
});
