import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent } from 'react';
import { ApiError, api } from '../../api/client';
import type { AgentDetailsResponse, AgentRequest, OperationResult } from '../../api/types';
import { AddressFields, trimAddress, trimWhiteSpace } from '../../components/AddressFields';
import type { AddressValue } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditAgentDialog}. */
export interface EditAgentDialogProps {
  /** Internal id of the agent edited; null, undefined or not a positive integer adds a new agent. */
  agentId?: number | null;
  /** Called once when the dialog is done: with `true` after a successful save, otherwise with `false`. */
  onClose: (saved: boolean) => void;
}

const AGENTS_PATH = '/api/app/admin/agents';

const FORM_CAPTION = 'Agent';
const NAME_GROUP_CAPTION = 'Name';
const NAME_CAPTION = 'Name';
const MANDATORY_FIELDS_CAPTION = 'Fields marked with a top-left red corner are mandatory';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const NAME_FOCUS_FIELD = 'FOCUS: Name';
const NAME_MAX_LENGTH = 80;

const EMPTY_ADDRESS: AddressValue = {
  street: '',
  city: '',
  country: '',
  phone: '',
  fax: '',
  email: '',
  webSite: '',
};

const FOCUSABLE_SELECTOR =
  'input:not(:disabled), button:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const GROUP_GAP = '0.1875rem';
const SECTION_GAP = '0.5625rem';
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
  gap: SECTION_GAP,
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

const GROUPS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GROUP_GAP,
  minInlineSize: 0,
};

const NAME_FIELDSET_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, 1fr)`,
  alignItems: 'center',
  columnGap: GROUP_GAP,
  rowGap: GROUP_GAP,
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
  justifyContent: 'space-between',
  gap: '0.375rem',
};

const MANDATORY_NOTE_STYLE: CSSProperties = {
  flex: '1 1 12rem',
  minInlineSize: 0,
  margin: 0,
  overflowWrap: 'break-word',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
  marginInlineStart: 'auto',
};

/** Returns true when `id` is a positive safe integer, the id of an existing agent. */
function isAgentId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns the request path of the agent with internal id `id`. */
function agentPath(id: number): string {
  return `${AGENTS_PATH}/${encodeURIComponent(String(id))}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/**
 * Returns the style of the Name box: the pressed, hovered or plain border, in that order of precedence, with the
 * focus outline added when `focusVisible` is true.
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

/**
 * The modal agent editor of the administration area: the "Name" group, the address groups of
 * {@link AddressFields}, the mandatory-fields note, and the OK and Cancel buttons.
 *
 * - The caption is `$Add` and "Agent" for a new agent, and `$Edit` and "Agent" for an existing one. For an existing
 *   agent, GET /api/app/admin/agents/{id} fills the name and the seven address fields; until it answers the panel
 *   has `aria-busy="true"` and OK does nothing. An answer to an earlier request is ignored. An HTTP error status
 *   calls `onClose(false)`; an error outcome is shown in its message box and then calls `onClose(false)`; a 401
 *   calls `onClose(false)` without a box; any other failure rejects unhandled.
 * - The soft edit lock of the agent is held by {@link useEditLock}: the prompt shown when the agent is already being
 *   edited, the lock taken on the first change, and the unlock after a save, on close and on unmount. Every edit of
 *   the name or an address field marks the dialog changed.
 * - OK, or Enter in a text box, sends the name and the seven address fields, each after `trimWhiteSpace`, with POST
 *   /api/app/admin/agents for a new agent or PUT /api/app/admin/agents/{id} for an existing one. A succeeded result
 *   calls `onClose(true)`. A failed result is shown titled `$ErrorDuringSave` and, once that box is closed, its
 *   focus field receives the focus: "FOCUS: Name" the Name box, an address focus value the matching address box. An
 *   error outcome is shown in its message box and keeps the dialog open; a 404 calls `onClose(false)`; any other
 *   failure rejects unhandled. OK and Cancel are disabled while a save request is outstanding.
 * - Cancel, or Escape, closes the dialog through the edit lock's close: when the dialog holds changes it asks whether
 *   to save them, Cancel keeps the dialog open, and Yes saves as OK does, keeping the dialog open when the save
 *   fails. The dialog then calls `onClose(true)` when that save succeeded, otherwise `onClose(false)`.
 *
 * The Name box is focused on mount without scrolling the panel, Tab and Shift+Tab wrap within the dialog, and the
 * element focused before the dialog opened gets the focus back when the dialog closes and nothing else has taken it.
 * After unmount `onClose` is not called.
 */
export function EditAgentDialog(props: EditAgentDialogProps): JSX.Element {
  const { agentId, onClose } = props;
  const rowId = isAgentId(agentId) ? agentId : null;
  const caption = `${msg(rowId === null ? 'Add' : 'Edit')} ${FORM_CAPTION}`;

  const titleId = useId();
  const nameId = useId();
  const { element: messageBox, showError } = useMessageBox();
  const { element: lockElement, markChanged, save, close } = useEditLock('Agent', agentId ?? null);

  const [name, setName] = useState('');
  const [address, setAddress] = useState<AddressValue>(EMPTY_ADDRESS);
  const [loadedId, setLoadedId] = useState<number | null>(null);
  const [pending, setPending] = useState(false);
  const [focusRequest, setFocusRequest] = useState<string | null>(null);
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const onCloseRef = useRef(onClose);
  const mountedRef = useRef(false);
  const doneRef = useRef(false);
  const actionRef = useRef(false);
  const savedRef = useRef(false);
  const pendingFocusRef = useRef<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const nameInputRef = useRef<HTMLInputElement>(null);

  const loading = rowId !== null && loadedId !== rowId;

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  const finish = useCallback((saved: boolean): void => {
    if (doneRef.current || !mountedRef.current) {
      return;
    }
    doneRef.current = true;
    onCloseRef.current(saved);
  }, []);

  useEffect(() => {
    setLoadedId(null);
    setName('');
    setAddress(EMPTY_ADDRESS);

    if (rowId === null) {
      return undefined;
    }

    let active = true;

    api.get<AgentDetailsResponse>(agentPath(rowId)).then(
      (response) => {
        if (!active) {
          return;
        }
        const { agent } = response;
        setName(agent.name ?? '');
        setAddress({
          street: agent.street ?? '',
          city: agent.city ?? '',
          country: agent.country ?? '',
          phone: agent.phone ?? '',
          fax: agent.fax ?? '',
          email: agent.email ?? '',
          webSite: agent.webSite ?? '',
        });
        setLoadedId(rowId);
      },
      async (error: unknown): Promise<void> => {
        if (!active) {
          return;
        }
        if (error instanceof ApiError) {
          finish(false);
          return;
        }
        await showError(error);
        if (active) {
          finish(false);
        }
      },
    );

    return () => {
      active = false;
    };
  }, [rowId, finish, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    nameInputRef.current?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useEffect(() => {
    if (focusRequest === null) {
      return;
    }
    if (focusRequest === NAME_FOCUS_FIELD) {
      nameInputRef.current?.focus();
    }
    setFocusRequest(null);
  }, [focusRequest]);

  const doSave = async (): Promise<OperationResult> => {
    pendingFocusRef.current = null;
    const body: AgentRequest = { name: trimWhiteSpace(name), address: trimAddress(address) };

    setPending(true);
    try {
      const result =
        rowId === null
          ? await api.post<OperationResult>(AGENTS_PATH, body)
          : await api.put<OperationResult>(agentPath(rowId), body);

      if (result.succeeded) {
        savedRef.current = true;
      } else {
        pendingFocusRef.current = result.focusField;
      }
      return result;
    } finally {
      if (mountedRef.current) {
        setPending(false);
      }
    }
  };

  const focusFailedField = (): void => {
    const field = pendingFocusRef.current;
    pendingFocusRef.current = null;
    if (field !== null && field !== '' && mountedRef.current) {
      setFocusRequest(field);
    }
  };

  const runAction = async (action: () => Promise<boolean>, closedResult: () => boolean): Promise<void> => {
    if (actionRef.current || doneRef.current) {
      return;
    }
    actionRef.current = true;

    try {
      let closed: boolean;
      try {
        closed = await action();
      } catch (error: unknown) {
        if (isNotFound(error)) {
          finish(false);
          return;
        }
        throw error;
      }

      if (closed) {
        finish(closedResult());
        return;
      }
      focusFailedField();
    } finally {
      actionRef.current = false;
    }
  };

  const handleOk = (): void => {
    if (loading) {
      return;
    }
    void runAction(
      () => save(doSave),
      () => true,
    );
  };

  const handleCancel = (): void => {
    void runAction(
      () => close(doSave),
      () => savedRef.current,
    );
  };

  const handleNameChange = (event: ChangeEvent<HTMLInputElement>): void => {
    setName(event.target.value);
    markChanged();
  };

  const handleAddressChange = (next: AddressValue): void => {
    setAddress(next);
    markChanged();
  };

  const updateFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      handleCancel();
      return;
    }

    if (event.key === 'Enter') {
      if (event.target instanceof HTMLInputElement && !event.nativeEvent.isComposing) {
        event.preventDefault();
        handleOk();
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

  const busy = loading || pending;

  return (
    <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-busy={busy}
        tabIndex={-1}
        style={busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handlePanelKeyDown}
      >
        <h2 id={titleId} style={TITLE_STYLE}>
          {caption}
        </h2>
        <div style={GROUPS_STYLE}>
          <fieldset style={NAME_FIELDSET_STYLE}>
            <legend>{NAME_GROUP_CAPTION}</legend>
            <label htmlFor={nameId} style={LABEL_STYLE}>
              {NAME_CAPTION}
            </label>
            <span style={INPUT_CELL_STYLE}>
              <input
                ref={nameInputRef}
                id={nameId}
                name="name"
                type="text"
                autoComplete="off"
                value={name}
                maxLength={NAME_MAX_LENGTH}
                aria-required="true"
                style={nameInputStyle(pressed, hovered, focusVisible)}
                onChange={handleNameChange}
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
                onFocus={updateFocusVisible}
                onKeyUp={updateFocusVisible}
                onBlur={() => setFocusVisible(false)}
              />
              <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />
            </span>
          </fieldset>
          <AddressFields value={address} onChange={handleAddressChange} focusField={focusRequest} />
        </div>
        <hr aria-hidden="true" style={RULE_STYLE} />
        <div style={FOOTER_STYLE}>
          <p style={MANDATORY_NOTE_STYLE}>{MANDATORY_FIELDS_CAPTION}</p>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={busy} onClick={handleOk}>
              {OK_CAPTION}
            </DialogButton>
            <DialogButton aria-disabled={pending} onClick={handleCancel}>
              {CANCEL_CAPTION}
            </DialogButton>
          </div>
        </div>
      </div>
      {lockElement}
      {messageBox}
    </div>
  );
}
