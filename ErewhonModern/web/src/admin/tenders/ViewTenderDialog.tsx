import { useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement, ReactNode } from 'react';
import { ApiError, api } from '../../api/client';
import type { OperationResult, TenderDetailsResponse } from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { currencyFormat, dateString, timeStampString } from '../../format';
import { msg } from '../../messages/messages';
import { isAgent, useSession } from '../../session/SessionProvider';

const TENDERS_PATH = '/api/app/admin/tenders';
const ITEMS_PATH = '/api/app/items';

const DIALOG_CAPTION = 'View Tender';
const BID_DETAILS_CAPTION = 'Bid Details';
const BID_DATE_CAPTION = 'Bid Date';
const CLIENT_CAPTION = 'Client';
const OFFER_CAPTION = 'Offer';
const TENDER_ITEM_CAPTION = 'Tender Item';
const PREFIX_CAPTION = 'Prefix';
const ITEM_NO_CAPTION = 'Item No';
const CATEGORY_CAPTION = 'Category';
const NAME_CAPTION = 'Name';
const DESCRIPTION_CAPTION = 'Description';
const RESERVE_CAPTION = 'Reserve';
const CLOSURE_DATE_CAPTION = 'Closure Date';
const AGENT_CAPTION = 'Agent';
const PICTURE_ALT = 'Picture';
const DELETE_CAPTION = 'Delete';
const CLOSE_CAPTION = 'Close';

const PICTURE_WIDTH_PX = 192;
const PICTURE_HEIGHT_PX = 163;

const FOCUSABLE_SELECTOR =
  'button:not(:disabled), input:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const FOCUS_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = `2px solid ${FOCUS_COLOR}`;
const INPUT_BORDER_COLOR = '#AEAEAE';
const GAP = '0.5rem';
const LABEL_COLUMN = '5.5rem';

const OVERLAY_STYLE: CSSProperties = {
  position: 'fixed',
  inset: 0,
  zIndex: 900,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  boxSizing: 'border-box',
  padding: '1rem',
  background: 'rgba(0, 0, 0, 0.3)',
};

const PANEL_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '38rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '1rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const BUSY_PANEL_STYLE: CSSProperties = {
  ...PANEL_STYLE,
  cursor: 'progress',
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
};

const FIELDSET_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GAP,
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInlineStart: '0.625rem',
  paddingInlineEnd: '0.75rem',
};

const ITEM_TOP_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  columnGap: '1rem',
  rowGap: GAP,
};

const ITEM_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GAP,
  flex: '1 1 19.875rem',
  minInlineSize: 0,
};

const ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: GAP,
};

const FIELD_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: GAP,
  minInlineSize: 0,
};

const MULTILINE_FIELD_STYLE: CSSProperties = {
  ...FIELD_STYLE,
  alignItems: 'flex-start',
};

const LABEL_STYLE: CSSProperties = {
  flex: `0 0 ${LABEL_COLUMN}`,
  minInlineSize: 0,
  overflowWrap: 'break-word',
};

const INPUT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 1 auto',
  minInlineSize: 0,
  maxInlineSize: '100%',
  minBlockSize: '1.375rem',
  margin: 0,
  paddingBlock: 0,
  paddingInline: '0.25rem',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: INPUT_BORDER_COLOR,
  backgroundColor: '#FFFFFF',
  color: '#000000',
  font: 'inherit',
};

const SHORT_INPUT_STYLE: CSSProperties = { ...INPUT_STYLE, inlineSize: '4.25rem' };

const MEDIUM_INPUT_STYLE: CSSProperties = { ...INPUT_STYLE, inlineSize: '9rem' };

const TEXT_INPUT_STYLE: CSSProperties = { ...INPUT_STYLE, inlineSize: '13.875rem' };

const LONG_INPUT_STYLE: CSSProperties = { ...INPUT_STYLE, inlineSize: '16.75rem' };

const DESCRIPTION_INPUT_STYLE: CSSProperties = {
  ...TEXT_INPUT_STYLE,
  blockSize: '6.5rem',
  paddingBlock: '0.125rem',
  resize: 'none',
  overflowY: 'auto',
  overflowWrap: 'break-word',
};

const FIELD_FOCUS_STYLE: CSSProperties = {
  outline: FOCUS_OUTLINE,
  outlineOffset: 0,
};

const PICTURE_FRAME_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 0 auto',
  inlineSize: '12rem',
  maxInlineSize: '100%',
  blockSize: '10.1875rem',
  border: BORDER,
  backgroundColor: 'rgb(240, 240, 240)',
  overflow: 'hidden',
};

const PICTURE_STYLE: CSSProperties = {
  display: 'block',
  inlineSize: '100%',
  blockSize: '100%',
  objectFit: 'contain',
};

const FOOTER_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: GAP,
};

const SOLD_STYLE: CSSProperties = {
  flex: '1 1 auto',
  minInlineSize: 0,
  margin: 0,
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: GAP,
  marginInlineStart: 'auto',
};

const BUTTON_STYLE: CSSProperties = {
  minInlineSize: '5rem',
  paddingBlock: '0.25rem',
  paddingInline: '0.75rem',
  overflowWrap: 'anywhere',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: 'rgb(128, 128, 128)',
  borderRadius: '2px',
  backgroundColor: 'rgb(240, 240, 240)',
  color: 'black',
  cursor: 'pointer',
};

const BUTTON_HOVER_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: FOCUS_COLOR,
  backgroundColor: 'rgb(229, 241, 251)',
};

const BUTTON_PRESSED_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: FOCUS_COLOR,
  backgroundColor: 'rgb(204, 228, 247)',
};

const BUTTON_DISABLED_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: 'rgb(191, 191, 191)',
  color: 'rgb(109, 109, 109)',
  cursor: 'default',
  pointerEvents: 'none',
};

const BUTTON_FOCUS_STYLE: CSSProperties = {
  outline: FOCUS_OUTLINE,
  outlineOffset: '1px',
};

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the path of GET and DELETE /api/app/admin/tenders/{id}. */
function tenderPath(tenderId: number): string {
  return `${TENDERS_PATH}/${tenderId}`;
}

/** Returns the path of GET /api/app/items/{id}/photo. */
function photoPath(itemId: number): string {
  return `${ITEMS_PATH}/${itemId}/photo`;
}

/** Revokes `url` where the environment can revoke object URLs. */
function revokeObjectUrl(url: string): void {
  if (typeof URL.revokeObjectURL === 'function') {
    URL.revokeObjectURL(url);
  }
}

/**
 * Returns the style of a {@link PushButton}: the disabled, pressed, hovered or base appearance, in that order of
 * precedence, with the focus outline added when `focusVisible` is true.
 */
function pushButtonStyle(disabled: boolean, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = BUTTON_STYLE;
  if (disabled) {
    style = BUTTON_DISABLED_STYLE;
  } else if (pressed) {
    style = BUTTON_PRESSED_STYLE;
  } else if (hovered) {
    style = BUTTON_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...BUTTON_FOCUS_STYLE } : style;
}

/** Properties of {@link PushButton}. */
interface PushButtonProps {
  /** Caption of the button. */
  children: ReactNode;
  /** When true, the button has the `disabled` attribute and shows its disabled appearance. */
  disabled: boolean;
  /** Called when the button is clicked. */
  onClick: () => void;
}

/**
 * Push button of type `button` with the appearance of {@link DialogButton}, disabled through its `disabled`
 * attribute: a rgb(240, 240, 240) background, black text and a 1px rgb(128, 128, 128) border; a rgb(229, 241, 251)
 * background and rgb(0, 80, 160) border while the pointer is over it; a rgb(204, 228, 247) background while the
 * primary mouse button is held on it; a 2px rgb(0, 80, 160) outline 1px outside its border when focused with
 * `:focus-visible`, checked again on every key release; and, disabled, rgb(109, 109, 109) text, a rgb(191, 191, 191)
 * border and no hover, pressed or focus state.
 */
function PushButton({ children, disabled, onClick }: PushButtonProps): ReactElement {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  useEffect(() => {
    if (disabled) {
      setHovered(false);
      setPressed(false);
      setFocusVisible(false);
    }
  }, [disabled]);

  const updateFocusVisible = (event: FocusEvent<HTMLButtonElement> | KeyboardEvent<HTMLButtonElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return (
    <button
      type="button"
      disabled={disabled}
      style={pushButtonStyle(disabled, pressed, hovered, focusVisible)}
      onClick={onClick}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => {
        setHovered(false);
        setPressed(false);
      }}
      onMouseDown={(event: MouseEvent<HTMLButtonElement>) => {
        if (event.button === 0) {
          setPressed(true);
        }
      }}
      onMouseUp={() => setPressed(false)}
      onFocus={updateFocusVisible}
      onKeyUp={updateFocusVisible}
      onBlur={() => setFocusVisible(false)}
    >
      {children}
    </button>
  );
}

/** Properties of {@link ReadOnlyField}. */
interface ReadOnlyFieldProps {
  /** Caption of the field's label. */
  caption: string;
  /** Text shown in the field. */
  value: string;
  /** Style of the input or text area. */
  inputStyle: CSSProperties;
  /** When true, the value is shown in a text area with the label aligned to its top. */
  multiline?: boolean;
}

/**
 * A label in the 5.5rem label column and, beside it, the read-only single-line input, or text area, it names; the
 * input or text area shrinks to the width left beside the label. It keeps its tab stop and, focused with
 * `:focus-visible` (checked again on every key release), has a 2px rgb(0, 80, 160) outline drawn against its border.
 */
function ReadOnlyField(props: ReadOnlyFieldProps): ReactElement {
  const { caption, value, inputStyle, multiline = false } = props;
  const id = useId();
  const [focusVisible, setFocusVisible] = useState(false);

  const updateFocusVisible = (
    event:
      | FocusEvent<HTMLInputElement | HTMLTextAreaElement>
      | KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>,
  ): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const style = focusVisible ? { ...inputStyle, ...FIELD_FOCUS_STYLE } : inputStyle;

  return (
    <div style={multiline ? MULTILINE_FIELD_STYLE : FIELD_STYLE}>
      <label htmlFor={id} style={LABEL_STYLE}>
        {caption}
      </label>
      {multiline ? (
        <textarea
          id={id}
          readOnly
          value={value}
          style={style}
          onFocus={updateFocusVisible}
          onKeyUp={updateFocusVisible}
          onBlur={() => setFocusVisible(false)}
        />
      ) : (
        <input
          id={id}
          type="text"
          readOnly
          value={value}
          autoComplete="off"
          style={style}
          onFocus={updateFocusVisible}
          onKeyUp={updateFocusVisible}
          onBlur={() => setFocusVisible(false)}
        />
      )}
    </div>
  );
}

/** Properties of {@link ViewTenderDialog}. */
export interface ViewTenderDialogProps {
  /** Internal id of the tender shown. */
  tenderId: number;
  /** Called with `true` once the tender has been deleted, and with `false` when the dialog closes without deleting. */
  onClose: (deleted: boolean) => void;
}

/** Details read for one tender id. */
interface LoadedTender {
  tenderId: number;
  details: TenderDetailsResponse;
}

/** Object URL of the photo read for one item id. */
interface LoadedPhoto {
  itemId: number;
  url: string;
}

/**
 * The modal "View Tender" dialog of the administration area: one tender's bid details, its tender item with the
 * item's picture, the item's sale, and the deletion of the tender.
 *
 * - On mount, and whenever `tenderId` changes, the fields are emptied and GET /api/app/admin/tenders/{id} is read; an
 *   answer to an earlier request is ignored. Until it answers the panel has `aria-busy="true"` and the progress
 *   cursor, and Delete is disabled. A 404 calls `onClose(false)` and shows nothing; an error outcome is shown in a
 *   message box, an HTTP 401 shows nothing, and any other failure rejects unhandled.
 * - "Bid Details" shows the bid date (`dd MMMM yyyy, HH:mm:ss`), the client's name and the offer (`$#,##0.00`).
 *   "Tender Item" shows the category prefix, the item number, the category name, the item name, the description in a
 *   text area, the reserve price (`$#,##0.00`), the closure date (`dd MMMM yyyy`), the agent's name, and the item's
 *   photo read from GET /api/app/items/{id}/photo; an HTTP error status for the photo leaves the picture frame empty,
 *   and the photo's object URL is revoked when the item changes or the dialog unmounts.
 * - When the item is sold, a bold status line shows the `SoldToClientFor` message with the buyer's name and the
 *   winning offer; otherwise the line is empty.
 * - Delete is disabled while the details load or a delete request is outstanding, for the winning tender, and for an
 *   agent's session. Otherwise it asks the `ConfirmDeleteTender` message titled `DeleteTender` with Yes and No; Yes
 *   sends DELETE /api/app/admin/tenders/{id}. A succeeded result, or a 404, calls `onClose(true)`; a failed result
 *   shows its errors titled `ErrorDuringSave` and leaves the dialog open; an error outcome is shown in a message box.
 * - Close and Escape call `onClose(false)`, except while a delete request is outstanding.
 *
 * The Close button is focused on mount without scrolling the panel, Tab and Shift+Tab wrap within the dialog, and the
 * element focused before the dialog opened gets the focus back when the dialog closes and nothing else has taken it.
 */
export function ViewTenderDialog({ tenderId, onClose }: ViewTenderDialogProps): ReactElement {
  const { session } = useSession();
  const { element: messageBox, show, showError } = useMessageBox();
  const titleId = useId();

  const [loaded, setLoaded] = useState<LoadedTender | null>(null);
  const [photo, setPhoto] = useState<LoadedPhoto | null>(null);
  const [pending, setPending] = useState(false);

  const onCloseRef = useRef(onClose);
  const mountedRef = useRef(false);
  const deletingRef = useRef(false);
  const pendingRef = useRef(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    let active = true;
    setLoaded(null);

    api.get<TenderDetailsResponse>(tenderPath(tenderId)).then(
      (response) => {
        if (active) {
          setLoaded({ tenderId, details: response });
        }
      },
      (error: unknown) => {
        if (!active) {
          return undefined;
        }
        if (isNotFound(error)) {
          onCloseRef.current(false);
          return undefined;
        }
        return showError(error);
      },
    );

    return () => {
      active = false;
    };
  }, [tenderId, showError]);

  const details = loaded !== null && loaded.tenderId === tenderId ? loaded.details : null;
  const photoItemId = details === null ? null : details.tender.tenderItem.id;

  useEffect(() => {
    if (photoItemId === null || typeof URL.createObjectURL !== 'function') {
      return undefined;
    }

    let active = true;
    let url: string | null = null;

    api.blob(photoPath(photoItemId)).then(
      (blob) => {
        if (!active || blob.size === 0) {
          return;
        }
        const created = URL.createObjectURL(blob);
        url = created;
        setPhoto({ itemId: photoItemId, url: created });
      },
      (error: unknown) => {
        if (!active || error instanceof ApiError) {
          return undefined;
        }
        return showError(error);
      },
    );

    return () => {
      active = false;
      const created = url;
      if (created !== null) {
        setPhoto((current) => (current !== null && current.url === created ? null : current));
        revokeObjectUrl(created);
      }
    };
  }, [photoItemId, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    closeButtonRef.current?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  const loading = details === null;
  const photoUrl = photo !== null && photo.itemId === photoItemId ? photo.url : null;
  const deleteDisabled = loading || pending || details.isWinning || isAgent(session);

  const setPendingState = (next: boolean): void => {
    pendingRef.current = next;
    setPending(next);
  };

  const requestClose = (): void => {
    if (!pendingRef.current) {
      onClose(false);
    }
  };

  const handleDelete = async (): Promise<void> => {
    if (deleteDisabled || deletingRef.current) {
      return;
    }

    deletingRef.current = true;
    try {
      const answer = await show({
        title: msg('DeleteTender'),
        message: msg('ConfirmDeleteTender'),
        icon: 'question',
        buttons: 'yesNo',
      });
      if (answer !== 'yes' || !mountedRef.current) {
        return;
      }

      panelRef.current?.focus();
      setPendingState(true);

      let result: OperationResult;
      try {
        result = await api.del<OperationResult>(tenderPath(tenderId));
      } catch (error: unknown) {
        if (!mountedRef.current) {
          return;
        }
        if (isNotFound(error)) {
          onCloseRef.current(true);
          return;
        }
        setPendingState(false);
        await showError(error);
        return;
      }

      if (!mountedRef.current) {
        return;
      }
      if (result.succeeded) {
        onCloseRef.current(true);
        return;
      }

      setPendingState(false);
      await show(displayErrorsOptions(result));
    } finally {
      deletingRef.current = false;
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      requestClose();
      return;
    }

    const panel = panelRef.current;
    if (event.key !== 'Tab' || panel === null) {
      return;
    }

    const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
    if (focusable.length === 0) {
      return;
    }

    const last = focusable.length - 1;
    const index = focusable.findIndex((element) => element === document.activeElement);
    let next: number | null = null;
    if (index === -1) {
      next = event.shiftKey ? last : 0;
    } else if (event.shiftKey && index === 0) {
      next = last;
    } else if (!event.shiftKey && index === last) {
      next = 0;
    }

    if (next !== null) {
      event.preventDefault();
      focusable[next]?.focus();
    }
  };

  const tender = details === null ? null : details.tender;
  const item = tender === null ? null : tender.tenderItem;
  const soldText =
    details !== null && details.itemSold
      ? msg('SoldToClientFor', details.winnerClientName ?? '', currencyFormat(details.winningOffer ?? 0))
      : '';

  return (
    <div style={OVERLAY_STYLE}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-busy={loading}
        tabIndex={-1}
        style={loading || pending ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 id={titleId} style={TITLE_STYLE}>
          {DIALOG_CAPTION}
        </h2>
        <fieldset style={FIELDSET_STYLE}>
          <legend>{BID_DETAILS_CAPTION}</legend>
          <ReadOnlyField
            caption={BID_DATE_CAPTION}
            value={tender === null ? '' : timeStampString(tender.timestamp)}
            inputStyle={TEXT_INPUT_STYLE}
          />
          <ReadOnlyField
            caption={CLIENT_CAPTION}
            value={tender === null ? '' : tender.client.name}
            inputStyle={LONG_INPUT_STYLE}
          />
          <ReadOnlyField
            caption={OFFER_CAPTION}
            value={tender === null ? '' : currencyFormat(tender.offer)}
            inputStyle={MEDIUM_INPUT_STYLE}
          />
        </fieldset>
        <fieldset style={FIELDSET_STYLE}>
          <legend>{TENDER_ITEM_CAPTION}</legend>
          <div style={ITEM_TOP_STYLE}>
            <div style={ITEM_COLUMN_STYLE}>
              <div style={ROW_STYLE}>
                <ReadOnlyField
                  caption={PREFIX_CAPTION}
                  value={item === null ? '' : (item.categoryPrefix ?? '')}
                  inputStyle={SHORT_INPUT_STYLE}
                />
                <ReadOnlyField
                  caption={ITEM_NO_CAPTION}
                  value={item === null ? '' : String(item.codeNumber)}
                  inputStyle={SHORT_INPUT_STYLE}
                />
              </div>
              <ReadOnlyField
                caption={CATEGORY_CAPTION}
                value={item === null ? '' : (item.itemCategoryName ?? '')}
                inputStyle={TEXT_INPUT_STYLE}
              />
              <ReadOnlyField
                caption={NAME_CAPTION}
                value={item === null ? '' : item.name}
                inputStyle={TEXT_INPUT_STYLE}
              />
              <ReadOnlyField
                caption={DESCRIPTION_CAPTION}
                value={item === null ? '' : item.description}
                inputStyle={DESCRIPTION_INPUT_STYLE}
                multiline
              />
            </div>
            <div style={PICTURE_FRAME_STYLE}>
              {photoUrl !== null && (
                <img
                  src={photoUrl}
                  alt={PICTURE_ALT}
                  width={PICTURE_WIDTH_PX}
                  height={PICTURE_HEIGHT_PX}
                  style={PICTURE_STYLE}
                />
              )}
            </div>
          </div>
          <div style={ROW_STYLE}>
            <ReadOnlyField
              caption={RESERVE_CAPTION}
              value={item === null ? '' : currencyFormat(item.price)}
              inputStyle={MEDIUM_INPUT_STYLE}
            />
            <ReadOnlyField
              caption={CLOSURE_DATE_CAPTION}
              value={item === null ? '' : dateString(item.closureDate)}
              inputStyle={MEDIUM_INPUT_STYLE}
            />
          </div>
          <ReadOnlyField
            caption={AGENT_CAPTION}
            value={item === null ? '' : (item.agent?.name ?? '')}
            inputStyle={LONG_INPUT_STYLE}
          />
        </fieldset>
        <div style={FOOTER_STYLE}>
          <p role="status" style={SOLD_STYLE}>
            {soldText}
          </p>
          <div style={BUTTON_ROW_STYLE}>
            <PushButton disabled={deleteDisabled} onClick={() => void handleDelete()}>
              {DELETE_CAPTION}
            </PushButton>
            <DialogButton ref={closeButtonRef} aria-disabled={pending} onClick={requestClose}>
              {CLOSE_CAPTION}
            </DialogButton>
          </div>
        </div>
      </div>
      {messageBox}
    </div>
  );
}
