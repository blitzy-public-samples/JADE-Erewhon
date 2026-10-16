import { useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, api } from '../../api/client';
import type {
  AddressFields as AddressFieldsValue,
  ClientDetailsResponse,
  ClientRequest,
  OperationResult,
} from '../../api/types';
import { AddressFields, trimAddress, trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditClientDialog}. */
export interface EditClientDialogProps {
  /** Internal id of the client edited; null or omitted adds a new client. */
  clientId?: number | null;
  /** Called once when the dialog is done, with true when the client was saved and false otherwise. */
  onClose: (saved: boolean) => void;
}

const CLIENTS_PATH = '/api/app/admin/clients';

const FORM_CAPTION = 'Client';
const NAME_GROUP_CAPTION = 'Name';
const NAME_CAPTION = 'Name';
const MANDATORY_FIELDS_CAPTION = 'Fields marked with a top-left red corner are mandatory';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const NAME_MAX_LENGTH = 80;

const FOCUS_NAME = 'FOCUS: Name';

const FOCUSABLE_SELECTOR =
  'input:not(:disabled), button:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

const EMPTY_ADDRESS: AddressFieldsValue = {
  street: '',
  city: '',
  country: '',
  phone: '',
  fax: '',
  email: '',
  webSite: '',
};

const BORDER = '1px solid rgb(128, 128, 128)';
const GAP = '0.1875rem';
const LABEL_COLUMN = '3.75rem';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const MANDATORY_CORNER_COLOR = '#ED1C24';
const MANDATORY_CORNER_SIZE = '6px';

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
  gap: '0.5rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '30.0625rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '0.625rem',
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
  overflowWrap: 'break-word',
};

const FIELDS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GAP,
  minInlineSize: 0,
};

const NAME_FIELDSET_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, 1fr)`,
  alignItems: 'center',
  columnGap: GAP,
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInlineStart: '0.75rem',
  paddingInlineEnd: '0.9375rem',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const INPUT_CELL_STYLE: CSSProperties = {
  position: 'relative',
  minInlineSize: 0,
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

const INPUT_DISABLED_STYLE: CSSProperties = { ...INPUT_STYLE, color: '#808080' };

const INPUT_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

const MANDATORY_CORNER_STYLE: CSSProperties = {
  position: 'absolute',
  insetBlockStart: 0,
  insetInlineStart: 0,
  inlineSize: 0,
  blockSize: 0,
  borderBlockStart: `${MANDATORY_CORNER_SIZE} solid ${MANDATORY_CORNER_COLOR}`,
  borderInlineEnd: `${MANDATORY_CORNER_SIZE} solid transparent`,
  pointerEvents: 'none',
};

const RULE_STYLE: CSSProperties = {
  boxSizing: 'content-box',
  blockSize: 0,
  margin: 0,
  border: 'none',
  borderBlockStart: '1px solid rgb(160, 160, 160)',
  borderBlockEnd: '1px solid white',
};

const FOOTER_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'flex-end',
  columnGap: '0.875rem',
  rowGap: '0.375rem',
};

const NOTE_STYLE: CSSProperties = {
  flex: '1 1 16.25rem',
  minInlineSize: 0,
  margin: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
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
 * Returns the style of the enabled Name box: the pressed, hovered or plain border, in that order of precedence, with
 * the focus outline added when `focusVisible` is true.
 */
function nameInputStyle(pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
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

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the request path of the client with internal id `id`. */
function clientPath(id: number): string {
  return `${CLIENTS_PATH}/${encodeURIComponent(String(id))}`;
}

/** Returns the seven address fields of the client in `response`. */
function addressOf(response: ClientDetailsResponse): AddressFieldsValue {
  const { client } = response;
  return {
    street: client.street,
    city: client.city,
    country: client.country,
    phone: client.phone,
    fax: client.fax,
    email: client.email,
    webSite: client.webSite,
  };
}

/**
 * The modal client editor of the administration area, captioned "Add Client" for a new client and "Edit Client" for
 * an existing one: the "Name" group with the mandatory Name box (at most 80 characters), the Address, Contact and
 * Internet Web Site groups of {@link AddressFields}, the mandatory-fields note, and the OK and Cancel buttons.
 *
 * - For an existing client, GET /api/app/admin/clients/{id} fills the Name box and the seven address fields; until it
 *   answers, the inputs and OK are disabled and the panel has `aria-busy="true"` and the progress cursor. A 404 calls
 *   `onClose(false)` without a message, an error outcome is shown in a message box and then calls `onClose(false)`, an
 *   HTTP 401 calls `onClose(false)` without a message, and any other failure calls `onClose(false)` and rejects
 *   unhandled.
 * - The client's soft edit lock is kept by {@link useEditLock}: the prompt shown when the client is already being
 *   edited, the lock taken on the first change of an existing client, the outstanding-changes prompt on close and the
 *   unlocks. Every edit of the Name box or an address field marks the dialog changed before the field is updated.
 * - OK, or Enter in a text box, sends the name and the seven address fields, each with CR, LF, Tab, Space and NUL
 *   trimmed from both ends, as a {@link ClientRequest}: POST /api/app/admin/clients for a new client, PUT
 *   /api/app/admin/clients/{id} otherwise. A succeeded result calls `onClose(true)`. A failed result is shown with its
 *   full error text titled `$ErrorDuringSave` and keeps the dialog open; once that box is closed, the field named by
 *   the result's focus field is focused: the Name box for "FOCUS: Name", the matching address input for an address
 *   focus value. A 404 calls `onClose(false)`.
 * - Cancel and Escape close the dialog after the outstanding-changes check: `onClose(true)` when its Yes answer saved
 *   the client, `onClose(false)` when the dialog closed without saving. Its Cancel answer, or a Yes whose save failed,
 *   keeps the dialog open, and a failed save focuses its focus field as OK does.
 * - OK, Enter, Cancel and Escape are ignored while a save or a close is in progress.
 *
 * The panel is focused on mount and the Name box once the client is shown, unless a message box has taken the focus.
 * Tab and Shift+Tab wrap within the dialog, and on unmount the element focused before the dialog opened gets the focus
 * back when nothing else has taken it. After unmount `onClose` is not called.
 */
export function EditClientDialog({ clientId = null, onClose }: EditClientDialogProps): ReactElement {
  const { element: lockMessageBox, markChanged, save, close } = useEditLock('Client', clientId);
  const { element: messageBox, showError } = useMessageBox();
  const nameId = useId();

  const [name, setName] = useState('');
  const [address, setAddress] = useState<AddressFieldsValue>(EMPTY_ADDRESS);
  const [loaded, setLoaded] = useState(clientId == null);
  const [saving, setSaving] = useState(false);
  const [addressFocus, setAddressFocus] = useState<string | null>(null);
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const onCloseRef = useRef(onClose);
  const mountedRef = useRef(false);
  const busyRef = useRef(false);
  const failedResultRef = useRef<OperationResult | null>(null);
  const savedRef = useRef(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const nameRef = useRef<HTMLInputElement>(null);

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
    setName('');
    setAddress(EMPTY_ADDRESS);
    setAddressFocus(null);

    if (clientId == null) {
      setLoaded(true);
      return undefined;
    }

    setLoaded(false);
    let active = true;

    const load = async (): Promise<void> => {
      let response: ClientDetailsResponse;
      try {
        response = await api.get<ClientDetailsResponse>(clientPath(clientId));
      } catch (error: unknown) {
        if (!active) {
          return;
        }
        try {
          if (!isNotFound(error)) {
            await showError(error);
          }
        } finally {
          if (active) {
            onCloseRef.current(false);
          }
        }
        return;
      }

      if (!active) {
        return;
      }
      setName(response.client.name);
      setAddress(addressOf(response));
      setLoaded(true);
    };

    void load();

    return () => {
      active = false;
    };
  }, [clientId, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    panel?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useEffect(() => {
    if (!loaded) {
      return;
    }
    const current = document.activeElement;
    if (current === null || current === document.body || current === panelRef.current) {
      nameRef.current?.focus();
    }
  }, [loaded]);

  const submit = async (): Promise<OperationResult> => {
    const request: ClientRequest = { name: trimWhiteSpace(name), address: trimAddress(address) };
    const result =
      clientId == null
        ? await api.post<OperationResult>(CLIENTS_PATH, request)
        : await api.put<OperationResult>(clientPath(clientId), request);
    failedResultRef.current = result.succeeded ? null : result;
    savedRef.current = result.succeeded;
    return result;
  };

  const focusFailedField = (): void => {
    const failed = failedResultRef.current;
    if (failed === null) {
      return;
    }
    if (failed.focusField === FOCUS_NAME) {
      nameRef.current?.focus();
      return;
    }
    setAddressFocus(failed.focusField);
  };

  const beginAction = (): boolean => {
    if (busyRef.current) {
      return false;
    }
    busyRef.current = true;
    failedResultRef.current = null;
    savedRef.current = false;
    setAddressFocus(null);
    return true;
  };

  const handleOk = async (): Promise<void> => {
    if (!loaded || !beginAction()) {
      return;
    }
    setSaving(true);

    try {
      const saved = await save(submit);
      if (!mountedRef.current) {
        return;
      }
      if (saved) {
        onCloseRef.current(true);
        return;
      }
      focusFailedField();
    } catch (error: unknown) {
      if (!mountedRef.current) {
        return;
      }
      if (isNotFound(error)) {
        onCloseRef.current(false);
        return;
      }
      throw error;
    } finally {
      busyRef.current = false;
      if (mountedRef.current) {
        setSaving(false);
      }
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (!beginAction()) {
      return;
    }

    try {
      const proceed = await close(submit);
      if (!mountedRef.current) {
        return;
      }
      if (proceed) {
        onCloseRef.current(savedRef.current);
        return;
      }
      focusFailedField();
    } catch (error: unknown) {
      if (!mountedRef.current) {
        return;
      }
      if (isNotFound(error)) {
        onCloseRef.current(false);
        return;
      }
      throw error;
    } finally {
      busyRef.current = false;
    }
  };


  const handleNameChange = (event: ChangeEvent<HTMLInputElement>): void => {
    markChanged();
    setName(event.target.value);
  };

  const handleAddressChange = (next: AddressFieldsValue): void => {
    markChanged();
    setAddress(next);
  };

  const updateFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void handleCancel();
      return;
    }

    if (event.key === 'Enter') {
      if (event.target instanceof HTMLInputElement && !event.nativeEvent.isComposing) {
        event.preventDefault();
        void handleOk();
      }
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

  const caption = `${msg(clientId == null ? 'Add' : 'Edit')} ${FORM_CAPTION}`;
  const inputsDisabled = !loaded;
  const busy = !loaded || saving;

  return (
    <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={caption}
        aria-busy={busy}
        tabIndex={-1}
        style={busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handlePanelKeyDown}
      >
        <h2 style={TITLE_STYLE}>{caption}</h2>
        <div style={FIELDS_STYLE}>
          <fieldset style={NAME_FIELDSET_STYLE}>
            <legend>{NAME_GROUP_CAPTION}</legend>
            <label htmlFor={nameId} style={LABEL_STYLE}>
              {NAME_CAPTION}
            </label>
            <span style={INPUT_CELL_STYLE}>
              <input
                ref={nameRef}
                id={nameId}
                name="name"
                type="text"
                autoComplete="off"
                value={name}
                maxLength={NAME_MAX_LENGTH}
                disabled={inputsDisabled}
                aria-required="true"
                style={inputsDisabled ? INPUT_DISABLED_STYLE : nameInputStyle(pressed, hovered, focusVisible)}
                onChange={handleNameChange}
                onMouseEnter={inputsDisabled ? undefined : () => setHovered(true)}
                onMouseLeave={
                  inputsDisabled
                    ? undefined
                    : () => {
                        setHovered(false);
                        setPressed(false);
                      }
                }
                onMouseDown={
                  inputsDisabled
                    ? undefined
                    : (event: MouseEvent<HTMLInputElement>) => {
                        if (event.button === 0) {
                          setPressed(true);
                        }
                      }
                }
                onMouseUp={inputsDisabled ? undefined : () => setPressed(false)}
                onFocus={inputsDisabled ? undefined : updateFocusVisible}
                onKeyUp={inputsDisabled ? undefined : updateFocusVisible}
                onBlur={() => setFocusVisible(false)}
              />
              <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />
            </span>
          </fieldset>
          <AddressFields
            value={address}
            onChange={handleAddressChange}
            disabled={inputsDisabled}
            focusField={addressFocus}
          />
        </div>
        <hr aria-hidden="true" style={RULE_STYLE} />
        <div style={FOOTER_STYLE}>
          <p style={NOTE_STYLE}>{MANDATORY_FIELDS_CAPTION}</p>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={!loaded} onClick={() => void handleOk()}>
              {OK_CAPTION}
            </DialogButton>
            <DialogButton onClick={() => void handleCancel()}>{CANCEL_CAPTION}</DialogButton>
          </div>
        </div>
      </div>
      {lockMessageBox}
      {messageBox}
    </div>
  );
}

