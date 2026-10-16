import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import type { ItemListRow } from '../../api/types';
import { trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { isCompanyUser, useSession } from '../../session/SessionProvider';
import type { SessionState } from '../../session/SessionProvider';

/** Properties of {@link FindItemDialog}. */
export interface FindItemDialogProps {
  /** Called with the row of the item found for the entered code, before `onClose`. */
  onFound: (row: ItemListRow) => void;
  /** Called once when the dialog is done: after Cancel or Escape, or after the lookup and the message it shows. */
  onClose: () => void;
}

interface FindItemPanelProps {
  /** Called with the text of the Item Code box when OK is clicked or Enter is pressed in the box. */
  onOk: (text: string) => void;
  /** Called when Cancel is clicked or Escape is pressed in the dialog. */
  onCancel: () => void;
}

/** Values read by the asynchronous lookup when its answer arrives. */
interface LatestValues {
  onFound: (row: ItemListRow) => void;
  onClose: () => void;
  session: SessionState | null;
}

const DIALOG_CAPTION = 'Find Item';
const ITEM_CODE_CAPTION = 'Item Code';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';
const FIND_PATH = '/api/app/admin/items/find?code=';
const FOCUSABLE_SELECTOR = 'input:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

/** Error number and text of the null-object-reference exception outcome. */
const NULL_REFERENCE_CODE = 1090;
const NULL_REFERENCE_TEXT = 'Null object reference';

const BORDER = '1px solid rgb(128, 128, 128)';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';

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
  gap: '0.625rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '16.0625rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '0.625rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const FIELDS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5625rem',
};

const ROW_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '4.6875rem minmax(0, 1fr)',
  alignItems: 'center',
  columnGap: '0.1875rem',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const INPUT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  display: 'block',
  inlineSize: '100%',
  minInlineSize: 0,
  minBlockSize: '1.3125rem',
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

const INPUT_HOVER_STYLE: CSSProperties = { ...INPUT_STYLE, borderColor: INPUT_HOVER_BORDER_COLOR };

const INPUT_PRESSED_STYLE: CSSProperties = { ...INPUT_STYLE, borderColor: INPUT_PRESSED_BORDER_COLOR };

const INPUT_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

const RULE_STYLE: CSSProperties = {
  boxSizing: 'content-box',
  blockSize: 0,
  margin: 0,
  border: 'none',
  borderBlockStart: '1px solid rgb(160, 160, 160)',
  borderBlockEnd: '1px solid white',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
};

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/**
 * Returns the style of the Item Code box: the pressed, hovered or plain border, in that order of precedence, with
 * the focus outline added when `focusVisible` is true.
 */
function inputStyle(pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = INPUT_STYLE;
  if (pressed) {
    style = INPUT_PRESSED_STYLE;
  } else if (hovered) {
    style = INPUT_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...INPUT_FOCUS_STYLE } : style;
}

/** Keeps the focus where it is when the mouse is pressed on the backdrop itself. */
function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

/** Returns the request path of GET /api/app/admin/items/find for `code`. */
function findPath(code: string): string {
  return `${FIND_PATH}${encodeURIComponent(code)}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the exception outcome of error 1090, "Null object reference", with the web exception body. */
function nullReferenceOutcome(): ErrorOutcomeError {
  return new ErrorOutcomeError({
    kind: 'exception',
    heading: msg('BaseExceptionHeading', NULL_REFERENCE_CODE),
    body: msg('BaseExceptionBodyWeb', NULL_REFERENCE_CODE, NULL_REFERENCE_TEXT),
  });
}

/**
 * The "Find Item" form: the "Item Code" box, a rule, and the OK and Cancel buttons, in a modal overlay. Typed text is
 * converted to upper case with the selection kept. The box is focused on mount, Tab and Shift+Tab wrap within the
 * dialog, and on unmount the element focused before the form opened gets the focus back when nothing else has taken
 * it. Enter in the box acts as OK, except while an input method is composing; Escape anywhere in the form acts as
 * Cancel.
 */
function FindItemPanel({ onOk, onCancel }: FindItemPanelProps): ReactElement {
  const titleId = useId();
  const inputId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [text, setText] = useState('');
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    inputRef.current?.focus();

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  const updateFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const handleChange = (event: ChangeEvent<HTMLInputElement>): void => {
    const input = event.currentTarget;
    const typed = input.value;
    const upper = typed.toUpperCase();

    if (upper !== typed) {
      const { selectionStart, selectionEnd, selectionDirection } = input;
      input.value = upper;
      if (selectionStart !== null && selectionEnd !== null) {
        input.setSelectionRange(
          typed.slice(0, selectionStart).toUpperCase().length,
          typed.slice(0, selectionEnd).toUpperCase().length,
          selectionDirection ?? undefined,
        );
      }
    }

    setText(upper);
  };

  const handleInputKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (event.key === 'Enter' && !event.nativeEvent.isComposing) {
      event.preventDefault();
      onOk(event.currentTarget.value);
    }
  };

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onCancel();
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

  return (
    <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        style={PANEL_STYLE}
        onKeyDown={handlePanelKeyDown}
      >
        <h2 id={titleId} style={TITLE_STYLE}>
          {DIALOG_CAPTION}
        </h2>
        <div style={FIELDS_STYLE}>
          <div style={ROW_STYLE}>
            <label htmlFor={inputId} style={LABEL_STYLE}>
              {ITEM_CODE_CAPTION}
            </label>
            <input
              ref={inputRef}
              id={inputId}
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={text}
              style={inputStyle(pressed, hovered, focusVisible)}
              onChange={handleChange}
              onKeyDown={handleInputKeyDown}
              onKeyUp={updateFocusVisible}
              onFocus={updateFocusVisible}
              onBlur={() => setFocusVisible(false)}
              onMouseEnter={() => setHovered(true)}
              onMouseLeave={() => {
                setHovered(false);
                setPressed(false);
              }}
              onMouseDown={(event: MouseEvent<HTMLInputElement>) => {
                if (event.button === 0) {
                  setPressed(true);
                }
              }}
              onMouseUp={() => setPressed(false)}
            />
          </div>
          <hr aria-hidden="true" style={RULE_STYLE} />
        </div>
        <div style={BUTTON_ROW_STYLE}>
          <DialogButton onClick={() => onOk(text)}>{OK_CAPTION}</DialogButton>
          <DialogButton onClick={() => onCancel()}>{CANCEL_CAPTION}</DialogButton>
        </div>
      </div>
    </div>
  );
}

/**
 * The "Find Item" dialog of the items-by-category list: looks an item up by the code entered in "Item Code".
 *
 * OK, or Enter in the box, trims CR, LF, Tab, Space and NUL from both ends of the code. An empty code shows
 * `$ItemNumberRequired` titled `$InvalidCode` with the stop icon and keeps the dialog open. Any other code hides the
 * form and sends the trimmed code unchanged to GET /api/app/admin/items/find, which matches on the code's number
 * alone; further OK presses are ignored until that lookup ends. Then:
 * - a row whose item belongs to the session agent, or any row for the company user, is passed to `onFound`;
 * - a row of another agent's item, for a session that is not the company user, and a 404 for the company user show
 *   `$ItemNotFound` titled `$NotFound` with the stop icon;
 * - a 404 for a session that is not the company user shows the exception outcome of error 1090;
 * - an `ErrorOutcomeError` is shown in its message box, an `UnauthorizedError` shows nothing, and any other failure
 *   rejects unhandled.
 * `onClose` is called once the shown message is closed, after `onFound` for a found item, and also when the lookup
 * fails without a message. Cancel and Escape call `onClose` without a request. After unmount neither callback is
 * called.
 */
export function FindItemDialog({ onFound, onClose }: FindItemDialogProps): ReactElement {
  const { session } = useSession();
  const { element: messageBox, show, showError } = useMessageBox();
  const [accepted, setAccepted] = useState(false);
  const busyRef = useRef(false);
  const doneRef = useRef(false);
  const mountedRef = useRef(false);
  const latestRef = useRef<LatestValues>({ onFound, onClose, session });

  useEffect(() => {
    latestRef.current = { onFound, onClose, session };
  });

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const finish = useCallback((): void => {
    if (doneRef.current || !mountedRef.current) {
      return;
    }
    doneRef.current = true;
    latestRef.current.onClose();
  }, []);

  const showNotFound = useCallback(async (): Promise<void> => {
    await show({ title: msg('NotFound'), message: msg('ItemNotFound'), icon: 'error', buttons: 'ok' });
  }, [show]);

  const findItem = useCallback(
    async (code: string): Promise<void> => {
      let row: ItemListRow | null = null;
      try {
        row = await api.get<ItemListRow>(findPath(code));
      } catch (error: unknown) {
        if (!mountedRef.current) {
          return;
        }
        if (!isNotFound(error)) {
          try {
            await showError(error);
          } finally {
            finish();
          }
          return;
        }
      }

      if (!mountedRef.current) {
        return;
      }

      const { session: current, onFound: found } = latestRef.current;
      const companyUser = isCompanyUser(current);

      if (row === null) {
        if (companyUser) {
          await showNotFound();
        } else {
          await showError(nullReferenceOutcome());
        }
        finish();
        return;
      }

      if (!companyUser && row.item.agentId !== (current?.agentId ?? null)) {
        await showNotFound();
        finish();
        return;
      }

      found(row);
      finish();
    },
    [finish, showError, showNotFound],
  );

  const handleOk = useCallback(
    async (text: string): Promise<void> => {
      if (busyRef.current || doneRef.current) {
        return;
      }
      busyRef.current = true;

      try {
        const code = trimWhiteSpace(text);
        if (code === '') {
          await show({ title: msg('InvalidCode'), message: msg('ItemNumberRequired'), icon: 'error', buttons: 'ok' });
          return;
        }

        setAccepted(true);
        await findItem(code);
      } finally {
        busyRef.current = false;
      }
    },
    [findItem, show],
  );

  const handleCancel = useCallback((): void => {
    if (busyRef.current) {
      return;
    }
    finish();
  }, [finish]);

  return (
    <>
      {!accepted && <FindItemPanel onOk={(text) => void handleOk(text)} onCancel={handleCancel} />}
      {messageBox}
    </>
  );
}

