// Shop item details: shows one item's code, description, photo, listed date, price and agent, and for a tender item
// its closure date and offer box, and adds the item to the session cart with Buy or Bid.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, ReactElement } from 'react';
import { ApiError, api } from '../api/client';
import type { AddCartItemRequest, CartOperationResponse, ItemDetailsResponse, OwnTenderOffer } from '../api/types';
import { displayErrorsOptions } from '../components/ErrorList';
import { useMessageBox } from '../components/MessageBox';
import { currencyFormat, shortFormat } from '../format';
import { msg } from '../messages/messages';

/** Properties of {@link ItemDetails}. */
export interface ItemDetailsProps {
  /** Internal id of the item to show. */
  itemId: number;
  /** Called when List is clicked, and after the deleted-item message of an item that no longer exists is closed. */
  onClose(): void;
  /** Called after the item has been added to the session cart. */
  onAdded(): void;
}

/** A photo read for an item: the item's id and the object URL of its bytes, null where none can be created. */
interface LoadedPhoto {
  itemId: number;
  url: string | null;
}

const CODE_CAPTION = 'Code:';
const DESCRIPTION_CAPTION = 'Description:';
const LISTED_DATE_CAPTION = 'Listed date:';
const CLOSE_DATE_CAPTION = 'Close date:';
const PRICE_CAPTION = 'Price:';
const AGENT_CAPTION = 'Agent:';
const OFFER_CAPTION = 'Enter the amount of your Tender :';

const CART_ITEMS_PATH = '/api/app/shop/cart/items';
const DECIMAL_TEXT = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/;

const PHOTO_WIDTH = 174;
const PHOTO_HEIGHT = 155;

const BUTTON_FACE = 'rgb(240, 240, 240)';
const INPUT_BORDER = '1px solid #AEAEAE';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '27.0625rem',
  minInlineSize: 0,
  color: 'black',
  overflowWrap: 'break-word',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  paddingInline: '0.375rem',
  fontSize: '1rem',
  fontWeight: 'bold',
};

const CODE_LIST_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '2.5rem minmax(0, 1fr)',
  alignItems: 'baseline',
  margin: 0,
  paddingInline: '0.375rem',
};

const FIELD_LIST_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '4.625rem minmax(0, 1fr)',
  alignItems: 'baseline',
  rowGap: '0.5rem',
  margin: 0,
  paddingInline: '0.375rem',
};

const TERM_STYLE: CSSProperties = {
  margin: 0,
  fontWeight: 'bold',
};

const VALUE_STYLE: CSSProperties = {
  margin: 0,
  minInlineSize: 0,
  fontWeight: 'normal',
  color: 'black',
};

/* BLITZY [A11Y]: red text is 4.0:1 on white, below WCAG AA 4.5:1; flagged for designer review in D-196. */
const CLOSED_VALUE_STYLE: CSSProperties = {
  ...VALUE_STYLE,
  fontWeight: 'bold',
  color: 'red',
};

const DESCRIPTION_GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.125rem',
};

const DESCRIPTION_LABEL_STYLE: CSSProperties = {
  paddingInline: '0.375rem',
  fontWeight: 'bold',
};

const MEDIA_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  gap: '0.5625rem',
};

const DESCRIPTION_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 1 15.5rem',
  minInlineSize: 0,
  blockSize: '9.375rem',
  margin: 0,
  marginInlineStart: '0.125rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.25rem',
  border: INPUT_BORDER,
  background: BUTTON_FACE,
  color: 'black',
  font: 'inherit',
  whiteSpace: 'pre-wrap',
  overflowWrap: 'break-word',
  resize: 'none',
};

const PHOTO_BOX_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  flex: '0 0 auto',
  inlineSize: '10.875rem',
  blockSize: '9.6875rem',
  overflow: 'hidden',
  border: '1px solid black',
  background: 'white',
};

const PHOTO_STYLE: CSSProperties = {
  display: 'block',
  inlineSize: '100%',
  blockSize: '100%',
  objectFit: 'contain',
  background: 'white',
};

const OFFER_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'flex-end',
  columnGap: '0.875rem',
  rowGap: '0.25rem',
};

const OFFER_LABEL_STYLE: CSSProperties = {
  fontWeight: 'bold',
  textAlign: 'end',
};

const OFFER_INPUT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '6.9375rem',
  blockSize: '1.5625rem',
  margin: 0,
  paddingBlock: 0,
  paddingInline: '0.25rem',
  border: INPUT_BORDER,
  font: 'inherit',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '0.125rem',
};

const BUTTON_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: '3.625rem',
  minBlockSize: '1.25rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.5rem',
  font: 'inherit',
};

const BUSY_BUTTON_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  color: 'rgb(109, 109, 109)',
  cursor: 'default',
};

/** Returns the request path of GET /api/app/items/{id}. */
function itemPath(itemId: number): string {
  return `/api/app/items/${itemId}`;
}

/** Returns the request path of GET /api/app/items/{id}/photo. */
function photoPath(itemId: number): string {
  return `${itemPath(itemId)}/photo`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/**
 * Returns the offer typed in `text`: the trimmed text read as a decimal number with an optional sign and no digit
 * grouping, e.g. ` 550.50 ` → 550.5, or 0 for empty or non-numeric text such as `1,000` or `abc`.
 */
function parseOffer(text: string): number {
  const trimmed = text.trim();
  return DECIMAL_TEXT.test(trimmed) ? Number(trimmed) : 0;
}

/** Orders own tenders by offer, highest first, then by the time they were tendered, earliest first. */
function compareOwnTenders(a: OwnTenderOffer, b: OwnTenderOffer): number {
  if (a.offer !== b.offer) {
    return b.offer - a.offer;
  }
  if (a.dateTendered < b.dateTendered) {
    return -1;
  }
  return a.dateTendered > b.dateTendered ? 1 : 0;
}

/**
 * Details of one shop item, shown as a non-modal `role="dialog"` named `$Details` with the item name as its heading.
 *
 * On mount and whenever `itemId` changes, GET /api/app/items/{id} is read and shown: "Code:", "Description:" in a
 * read-only box, the photo, "Listed date:" in `shortFormat`, "Price:" in `currencyFormat` and "Agent:". A tender item
 * also shows "Close date:" in `shortFormat`, red and bold with `data-closed="true"` when the item is closed, and the
 * offer box "Enter the amount of your Tender :", empty on open and disabled when the item is closed. The section has
 * `aria-busy="true"` until the item is read or its read has failed. A 404 shows `$ItemHasBeenDeleted` titled
 * `$DeletedItem` with the `error` icon and then calls `onClose`; an `ErrorOutcomeError` is shown in its message box.
 * When the item has a photo, GET /api/app/items/{id}/photo is read and shown as an image named after the item,
 * through an object URL created only where `URL.createObjectURL` exists and revoked on unmount or item change; a 404
 * for the photo leaves the picture box empty.
 *
 * Buy (retail item) or Bid (tender item, disabled when the item is closed) adds the item to the session cart, a retail
 * item with offer 0 and a tender item with the typed offer: the trimmed text read as a decimal, or 0 for empty or
 * non-numeric text. For a tender item with a non-zero offer the item is read again first, and each of the client's
 * own tenders, highest offer first and then earliest first, whose offer is at least the new offer shows
 * `$AlreadyTenderedAHigherOrSameOffer` titled `$AlreadyTendered` with the `error` icon, one box after another; when any
 * is shown, the offer box is cleared and nothing is added. Otherwise POST /api/app/shop/cart/items sends the item and
 * offer: a failed result shows its full error text titled `$CannotAddItemToCart` and keeps the details open, and a
 * successful one clears the offer box and calls `onAdded`. While an add is in progress the Buy or Bid button has
 * `aria-disabled="true"`, keeps the focus, shows grey text and ignores clicks. List calls `onClose`. After unmount,
 * or once another item is shown, a pending request shows no message and calls neither callback.
 */
export function ItemDetails({ itemId, onClose, onAdded }: ItemDetailsProps): ReactElement {
  const { element: messageBox, show, showError } = useMessageBox();
  const [details, setDetails] = useState<ItemDetailsResponse | null>(null);
  const [photo, setPhoto] = useState<LoadedPhoto | null>(null);
  const [offerText, setOfferText] = useState('');
  const [failedItemId, setFailedItemId] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const busyRef = useRef(false);
  const viewRef = useRef(0);
  const callbacksRef = useRef({ onClose, onAdded });
  const fieldIdPrefix = useId();

  const codeTermId = `${fieldIdPrefix}code`;
  const descriptionId = `${fieldIdPrefix}description`;
  const listedTermId = `${fieldIdPrefix}listed`;
  const closeTermId = `${fieldIdPrefix}close`;
  const priceTermId = `${fieldIdPrefix}price`;
  const agentTermId = `${fieldIdPrefix}agent`;
  const offerId = `${fieldIdPrefix}offer`;

  const current = details !== null && details.item.id === itemId ? details : null;
  const hasPhoto = current !== null && current.hasPhoto;
  const shownPhoto = hasPhoto && photo !== null && photo.itemId === itemId ? photo : null;

  useEffect(() => {
    callbacksRef.current = { onClose, onAdded };
  });

  const closeDeleted = useCallback(
    async (view: number): Promise<void> => {
      await show({ title: msg('DeletedItem'), message: msg('ItemHasBeenDeleted'), icon: 'error', buttons: 'ok' });
      if (viewRef.current === view) {
        callbacksRef.current.onClose();
      }
    },
    [show],
  );

  useEffect(() => {
    viewRef.current += 1;
    const view = viewRef.current;
    setOfferText('');

    const load = async (): Promise<void> => {
      let loaded: ItemDetailsResponse;
      try {
        loaded = await api.get<ItemDetailsResponse>(itemPath(itemId));
      } catch (error: unknown) {
        if (viewRef.current !== view) {
          return;
        }
        setFailedItemId(itemId);
        if (isNotFound(error)) {
          await closeDeleted(view);
        } else {
          await showError(error);
        }
        return;
      }

      if (viewRef.current === view) {
        setDetails(loaded);
      }
    };

    setFailedItemId(null);
    void load();

    return () => {
      viewRef.current += 1;
    };
  }, [itemId, closeDeleted, showError]);

  useEffect(() => {
    if (!hasPhoto) {
      return undefined;
    }

    let active = true;
    let url: string | null = null;

    const loadPhoto = async (): Promise<void> => {
      let blob: Blob;
      try {
        blob = await api.blob(photoPath(itemId));
      } catch (error: unknown) {
        if (active && !isNotFound(error)) {
          await showError(error);
        }
        return;
      }

      if (!active) {
        return;
      }
      if (typeof URL.createObjectURL === 'function') {
        url = URL.createObjectURL(blob);
      }
      setPhoto({ itemId, url });
    };

    void loadPhoto();

    return () => {
      active = false;
      if (url !== null && typeof URL.revokeObjectURL === 'function') {
        URL.revokeObjectURL(url);
      }
    };
  }, [hasPhoto, itemId, showError]);

  const addToCart = async (shown: ItemDetailsResponse): Promise<void> => {
    if (busyRef.current) {
      return;
    }

    const view = viewRef.current;
    const isTender = shown.item.isTender;
    const offer = isTender ? parseOffer(offerText) : 0;
    busyRef.current = true;
    setBusy(true);

    try {
      if (isTender && offer !== 0) {
        let latest: ItemDetailsResponse;
        try {
          latest = await api.get<ItemDetailsResponse>(itemPath(itemId));
        } catch (error: unknown) {
          if (viewRef.current !== view) {
            return;
          }
          if (isNotFound(error)) {
            await closeDeleted(view);
          } else {
            await showError(error);
          }
          return;
        }

        if (viewRef.current !== view) {
          return;
        }

        const matches = [...latest.ownTenders].sort(compareOwnTenders).filter((tender) => offer <= tender.offer);
        for (let shownCount = 0; shownCount < matches.length; shownCount += 1) {
          await show({
            title: msg('AlreadyTendered'),
            message: msg('AlreadyTenderedAHigherOrSameOffer'),
            icon: 'error',
            buttons: 'ok',
          });
          if (viewRef.current !== view) {
            return;
          }
        }

        if (matches.length > 0) {
          setOfferText('');
          return;
        }
      }

      const request: AddCartItemRequest = { itemId, offer };
      let response: CartOperationResponse;
      try {
        response = await api.post<CartOperationResponse>(CART_ITEMS_PATH, request);
      } catch (error: unknown) {
        if (viewRef.current === view) {
          await showError(error);
        }
        return;
      }

      if (viewRef.current !== view) {
        return;
      }

      if (!response.result.succeeded) {
        await show(displayErrorsOptions(response.result, msg('CannotAddItemToCart')));
        return;
      }

      setOfferText('');
      callbacksRef.current.onAdded();
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  };

  const isTender = current !== null && current.item.isTender;
  const isClosed = current !== null && current.isClosed;

  return (
    <section
      role="dialog"
      aria-label={msg('Details')}
      aria-busy={current === null && failedItemId !== itemId}
      style={SECTION_STYLE}
    >
      {current !== null && (
        <>
          <h3 style={HEADING_STYLE}>{current.item.name}</h3>
          <dl style={CODE_LIST_STYLE}>
            <dt id={codeTermId} style={TERM_STYLE}>
              {CODE_CAPTION}
            </dt>
            <dd aria-labelledby={codeTermId} style={VALUE_STYLE}>
              {current.item.code ?? ''}
            </dd>
          </dl>
          <div style={DESCRIPTION_GROUP_STYLE}>
            <label htmlFor={descriptionId} style={DESCRIPTION_LABEL_STYLE}>
              {DESCRIPTION_CAPTION}
            </label>
            <div style={MEDIA_ROW_STYLE}>
              <textarea
                id={descriptionId}
                readOnly
                value={current.item.description ?? ''}
                style={DESCRIPTION_STYLE}
              />
              <div style={PHOTO_BOX_STYLE}>
                {shownPhoto !== null && (
                  <img
                    src={shownPhoto.url ?? undefined}
                    alt={current.item.name}
                    width={PHOTO_WIDTH}
                    height={PHOTO_HEIGHT}
                    decoding="async"
                    style={PHOTO_STYLE}
                  />
                )}
              </div>
            </div>
          </div>
          <dl style={FIELD_LIST_STYLE}>
            <dt id={listedTermId} style={TERM_STYLE}>
              {LISTED_DATE_CAPTION}
            </dt>
            <dd aria-labelledby={listedTermId} style={VALUE_STYLE}>
              {shortFormat(current.item.listedDate)}
            </dd>
            {isTender && (
              <>
                <dt id={closeTermId} style={TERM_STYLE}>
                  {CLOSE_DATE_CAPTION}
                </dt>
                <dd
                  aria-labelledby={closeTermId}
                  data-closed={isClosed ? 'true' : undefined}
                  style={isClosed ? CLOSED_VALUE_STYLE : VALUE_STYLE}
                >
                  {shortFormat(current.item.closureDate)}
                </dd>
              </>
            )}
            <dt id={priceTermId} style={TERM_STYLE}>
              {PRICE_CAPTION}
            </dt>
            <dd aria-labelledby={priceTermId} style={VALUE_STYLE}>
              {currencyFormat(current.item.price)}
            </dd>
            <dt id={agentTermId} style={TERM_STYLE}>
              {AGENT_CAPTION}
            </dt>
            <dd aria-labelledby={agentTermId} style={VALUE_STYLE}>
              {current.item.agent?.name ?? ''}
            </dd>
          </dl>
          {isTender && (
            <div style={OFFER_ROW_STYLE}>
              <label htmlFor={offerId} style={OFFER_LABEL_STYLE}>
                {OFFER_CAPTION}
              </label>
              <input
                id={offerId}
                type="text"
                inputMode="decimal"
                autoComplete="off"
                value={offerText}
                disabled={isClosed}
                onChange={(event: ChangeEvent<HTMLInputElement>) => setOfferText(event.target.value)}
                style={OFFER_INPUT_STYLE}
              />
            </div>
          )}
        </>
      )}
      <div style={BUTTON_ROW_STYLE}>
        <button type="button" style={BUTTON_STYLE} onClick={() => onClose()}>
          {msg('List')}
        </button>
        {current !== null && (
          <button
            type="button"
            style={busy ? BUSY_BUTTON_STYLE : BUTTON_STYLE}
            disabled={isTender && isClosed}
            aria-disabled={busy ? 'true' : undefined}
            onClick={() => void addToCart(current)}
          >
            {isTender ? msg('Bid') : msg('Buy')}
          </button>
        )}
      </div>
      {messageBox}
    </section>
  );
}
