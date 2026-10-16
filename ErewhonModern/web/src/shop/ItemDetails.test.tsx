// Component tests of the shop item details against the fixture bridge: the own-offer check of a bid on a tender item
// that already holds the session client's tenders of 500 and 600, and the zero or empty offer that skips it (B-31).

import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../api/client';
import type {
  AddCartItemRequest,
  CartOperationResponse,
  CartView,
  CheckoutResponse,
  Id,
  ItemDetailsResponse,
} from '../api/types';
import { msg } from '../messages/messages';
import { renderWithSession, useFixture } from '../test/fixtures';
import { ItemDetails } from './ItemDetails';

const OFFER_CAPTION = 'Enter the amount of your Tender :';
const OK_CAPTION = 'OK';

const CART_PATH = '/api/app/shop/cart';
const CART_ITEMS_PATH = '/api/app/shop/cart/items';
const CHECKOUT_PATH = '/api/app/shop/checkout';

const FIXTURE_OFFER = 500;
const SECOND_OWN_OFFER = 600;

/** Returns the request path of GET /api/app/items/{id}. */
function itemPath(itemId: Id): string {
  return `/api/app/items/${itemId}`;
}

/** Returns every message box titled `$AlreadyTendered` currently shown. */
function alreadyTenderedBoxes(): HTMLElement[] {
  return screen.queryAllByRole('dialog', { name: msg('AlreadyTendered') });
}

describe('ItemDetails', () => {
  const fixture = useFixture('TestViewSaleItem');

  /** Returns the id of the fixture's open tender item. */
  function tenderItemId(): Id {
    const id = fixture.ids.tenderItem;
    if (id === undefined) {
      throw new Error('The TestViewSaleItem fixture returned no tenderItem id');
    }
    return id;
  }

  /** Renders the details of the tender item inside the fixture session, with mock callbacks. */
  function renderTenderItemDetails(): { user: UserEvent; onAdded: Mock<() => void>; onClose: Mock<() => void> } {
    const user = userEvent.setup();
    const onAdded = vi.fn<() => void>();
    const onClose = vi.fn<() => void>();
    renderWithSession(<ItemDetails itemId={tenderItemId()} onClose={onClose} onAdded={onAdded} />, fixture);
    return { user, onAdded, onClose };
  }

  /**
   * Waits for the item to be shown, types `offer` in the offer box unless it is empty, and clicks Bid; returns the
   * details dialog.
   */
  async function bid(user: UserEvent, offer: string): Promise<HTMLElement> {
    const details = screen.getByRole('dialog', { name: msg('Details') });
    const offerBox = await within(details).findByLabelText(OFFER_CAPTION);
    if (offer !== '') {
      await user.type(offerBox, offer);
    }
    expect(offerBox).toHaveValue(offer);
    await user.click(within(details).getByRole('button', { name: msg('Bid') }));
    return details;
  }

  /** Returns the session cart. */
  function readCart(): Promise<CartView> {
    return api.get<CartView>(CART_PATH);
  }

  // Places a second tender of 600 by the session client on the tender item through the cart and checkout, and checks
  // that the item then lists the client's own offers 500 and 600 and the cart is empty.
  beforeEach(async () => {
    const itemId = tenderItemId();

    const request: AddCartItemRequest = { itemId, offer: SECOND_OWN_OFFER };
    const added = await api.post<CartOperationResponse>(CART_ITEMS_PATH, request);
    expect(added.result.succeeded, added.result.fullErrorText).toBe(true);

    const checkout = await api.post<CheckoutResponse>(CHECKOUT_PATH, {});
    expect(checkout.result.succeeded, checkout.result.fullErrorText).toBe(true);

    const details = await api.get<ItemDetailsResponse>(itemPath(itemId));
    const ownOffers = details.ownTenders.map((tender) => tender.offer).sort((a, b) => a - b);
    expect(ownOffers).toEqual([FIXTURE_OFFER, SECOND_OWN_OFFER]);

    expect((await readCart()).lines).toHaveLength(0);
  });

  // A bid of 550 shows one `$AlreadyTenderedAHigherOrSameOffer` box, for the own offer of 600 only, then clears the
  // offer box and adds nothing to the cart.
  it('B31_OwnHigherOrSameOfferBlocksAdd', async () => {
    const { user, onAdded, onClose } = renderTenderItemDetails();

    const details = await bid(user, '550');

    const shown = await screen.findAllByRole('dialog', { name: msg('AlreadyTendered') });
    expect(shown).toHaveLength(1);
    const box = screen.getByRole('dialog', { name: msg('AlreadyTendered') });
    expect(box).toHaveAccessibleDescription(msg('AlreadyTenderedAHigherOrSameOffer'));
    expect(within(box).getByRole('img', { name: 'error' })).toBeInTheDocument();

    await user.click(within(box).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(alreadyTenderedBoxes()).toHaveLength(0);
    });

    const bidButton = within(details).getByRole('button', { name: msg('Bid') });
    await waitFor(() => {
      expect(within(details).getByLabelText(OFFER_CAPTION)).toHaveValue('');
      expect(bidButton).not.toHaveAttribute('aria-disabled');
    });

    expect(alreadyTenderedBoxes()).toHaveLength(0);
    expect(screen.queryByRole('dialog', { name: msg('CannotAddItemToCart') })).not.toBeInTheDocument();
    expect(onAdded).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    expect((await readCart()).lines).toHaveLength(0);
  });

  // A bid of 700, above both own offers, shows no `$AlreadyTendered` box and adds one tender line of 700 to the cart.
  it('B31_HigherOfferIsAdded', async () => {
    const itemId = tenderItemId();
    const { user, onAdded, onClose } = renderTenderItemDetails();

    const details = await bid(user, '700');

    await waitFor(() => {
      expect(onAdded).toHaveBeenCalledTimes(1);
    });

    expect(alreadyTenderedBoxes()).toHaveLength(0);
    expect(screen.queryByRole('dialog', { name: msg('CannotAddItemToCart') })).not.toBeInTheDocument();
    expect(within(details).getByLabelText(OFFER_CAPTION)).toHaveValue('');
    expect(onClose).not.toHaveBeenCalled();

    const cart = await readCart();
    expect(cart.lines).toHaveLength(1);
    expect(cart.lines[0]).toMatchObject({ itemId, isTender: true, offer: 700 });
  });

  // An empty offer, then an offer of 0, each show no `$AlreadyTendered` box and get the cart's
  // `$TenderOfferLessThanReserve` error titled `$CannotAddItemToCart`, and nothing is added to the cart.
  it('B31_ZeroOrEmptyOfferSkipsOwnOfferCheck', async () => {
    const { user, onAdded, onClose } = renderTenderItemDetails();

    for (const offer of ['', '0']) {
      const details = await bid(user, offer);

      const box = await screen.findByRole('dialog', { name: msg('CannotAddItemToCart') });
      expect(box).toHaveAccessibleDescription(msg('TenderOfferLessThanReserve'));
      expect(alreadyTenderedBoxes()).toHaveLength(0);

      await user.click(within(box).getByRole('button', { name: OK_CAPTION }));

      await waitFor(() => {
        expect(screen.queryByRole('dialog', { name: msg('CannotAddItemToCart') })).not.toBeInTheDocument();
        expect(within(details).getByRole('button', { name: msg('Bid') })).not.toHaveAttribute('aria-disabled');
      });
    }

    expect(alreadyTenderedBoxes()).toHaveLength(0);
    expect(onAdded).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    expect((await readCart()).lines).toHaveLength(0);
  });
});
