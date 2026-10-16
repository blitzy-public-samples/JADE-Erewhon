import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import type { CountryRequest, CountryRow, OperationResult } from '../../api/types';
import { trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditCountryDialog}. */
export interface EditCountryDialogProps {
  /** Row id of the country to edit; null or undefined adds a new country. */
  countryId?: number | null;
  /** Called once when the dialog is done, with true when a save of the country succeeded while it was open. */
  onClose: (saved: boolean) => void;
}

const COUNTRIES_PATH = '/api/app/admin/countries';
const FORM_CAPTION = 'Country';
const NAME_CAPTION = 'Name';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';
const NAME_MAX_LENGTH = 80;
const FOCUS_NAME = 'FOCUS: Name';
const FOCUSABLE_SELECTOR = 'input:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

/** Error number and text of the object-not-found exception outcome. */
const OBJECT_NOT_FOUND_CODE = 4;
const OBJECT_NOT_FOUND_TEXT = 'Object not found';

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
  maxInlineSize: '18.25rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  paddingBlock: '0.625rem',
  paddingInlineStart: '0.625rem',
  paddingInlineEnd: '0.5625rem',
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
  gridTemplateColumns: 'minmax(2.0625rem, max-content) minmax(0, 1fr)',
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
 * Returns the style of the Name box: the pressed, hovered or plain border, in that order of precedence, with the
 * focus outline added when `focusVisible` is true.
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

/** Returns the request path of the country with row id `id`. */
function countryPath(id: number): string {
  return `${COUNTRIES_PATH}/${encodeURIComponent(String(id))}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the exception outcome of error 4, "Object not found", with the web exception body. */
function objectNotFoundOutcome(): ErrorOutcomeError {
  return new ErrorOutcomeError({
    kind: 'exception',
    heading: msg('BaseExceptionHeading', OBJECT_NOT_FOUND_CODE),
    body: msg('BaseExceptionBodyWeb', OBJECT_NOT_FOUND_CODE, OBJECT_NOT_FOUND_TEXT),
  });
}

/**
 * The country editor of the countries list, captioned "Add Country" for a new country (`countryId` null or
 * undefined) and "Edit Country" for an existing one; the caption is the dialog's accessible name and its heading.
 *
 * The dialog renders at once, with the "Name" box (at most 80 characters) focused. For an existing country, GET
 * /api/app/admin/countries/{id} fills the box; until it answers the box is read-only, the fields have
 * `aria-busy="true"` and OK does nothing. A 404 shows the exception outcome of error 4, "Object not found", and any
 * other failure goes to `showError`; the dialog then calls `onClose(false)`. The soft edit lock of the country
 * follows {@link useEditLock}: its prompt on open, the lock on the first change typed in the box, and the unlock.
 *
 * OK, and Enter in the box except while an input method is composing, sends `{ name }` with CR, LF, Tab, Space and
 * NUL trimmed from both ends: POST /api/app/admin/countries for a new country, PUT /api/app/admin/countries/{id} for an
 * existing one, through the hook's `save`. A failed result is shown titled `$ErrorDuringSave` and the dialog stays
 * open, with the focus moved to the box when the result's focus field is `FOCUS: Name`; a 404 shows the error-4
 * outcome and also keeps the dialog open. A succeeded result calls `onClose(true)`.
 *
 * Cancel and Escape go through the hook's `close`, which asks about outstanding changes; when it allows closing, the
 * dialog calls `onClose` with true when a save succeeded, false otherwise. OK, Enter, Cancel and Escape are ignored
 * while a save or close is pending. Tab and Shift+Tab wrap within the dialog, a mouse press on the backdrop keeps the
 * focus, and on unmount the element focused before the dialog opened gets the focus back when nothing else has taken
 * it. `onClose` is called at most once and never after unmount.
 */
export function EditCountryDialog({ countryId, onClose }: EditCountryDialogProps): ReactElement {
  const editId = typeof countryId === 'number' ? countryId : null;
  const lock = useEditLock('Country', editId);
  const { lastResult, markChanged, save, close } = lock;
  const { element: messageBox, showError } = useMessageBox();
  const inputId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const nameRef = useRef('');
  const [loadedId, setLoadedId] = useState<number | null>(null);
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);
  const busyRef = useRef(false);
  const doneRef = useRef(false);
  const mountedRef = useRef(false);
  const savedRef = useRef(false);
  const onCloseRef = useRef(onClose);
  const loading = editId !== null && loadedId !== editId;
  const caption = `${msg(editId === null ? 'Add' : 'Edit')} ${FORM_CAPTION}`;

  useEffect(() => {
    onCloseRef.current = onClose;
  });

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

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

  const finish = useCallback((saved: boolean): void => {
    if (doneRef.current || !mountedRef.current) {
      return;
    }
    doneRef.current = true;
    onCloseRef.current(saved);
  }, []);

  useEffect(() => {
    if (editId === null) {
      return undefined;
    }

    let active = true;
    nameRef.current = '';
    setName('');

    const loadCountry = async (): Promise<void> => {
      let row: CountryRow | undefined;
      try {
        row = await api.get<CountryRow | undefined>(countryPath(editId));
      } catch (error: unknown) {
        if (!active) {
          return;
        }
        try {
          await showError(isNotFound(error) ? objectNotFoundOutcome() : error);
        } finally {
          if (active) {
            finish(false);
          }
        }
        return;
      }

      if (!active) {
        return;
      }
      const loadedName = row?.name ?? '';
      nameRef.current = loadedName;
      setName(loadedName);
      setLoadedId(editId);
    };

    void loadCountry();

    return () => {
      active = false;
    };
  }, [editId, finish, showError]);

  useLayoutEffect(() => {
    if (lastResult?.focusField === FOCUS_NAME) {
      inputRef.current?.focus();
    }
  }, [lastResult]);

  const doSave = async (): Promise<OperationResult> => {
    const request: CountryRequest = { name: trimWhiteSpace(nameRef.current) };
    let result: OperationResult;
    try {
      result =
        editId === null
          ? await api.post<OperationResult>(COUNTRIES_PATH, request)
          : await api.put<OperationResult>(countryPath(editId), request);
    } catch (error: unknown) {
      throw isNotFound(error) ? objectNotFoundOutcome() : error;
    }
    if (result.succeeded) {
      savedRef.current = true;
    }
    return result;
  };

  const handleOk = async (): Promise<void> => {
    if (busyRef.current || doneRef.current || loading) {
      return;
    }
    busyRef.current = true;
    try {
      if (await save(doSave)) {
        finish(true);
      }
    } finally {
      busyRef.current = false;
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (busyRef.current || doneRef.current) {
      return;
    }
    busyRef.current = true;
    try {
      if (await close(doSave)) {
        finish(savedRef.current);
      }
    } finally {
      busyRef.current = false;
    }
  };

  const updateFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const handleNameChange = (event: ChangeEvent<HTMLInputElement>): void => {
    const typed = event.currentTarget.value;
    nameRef.current = typed;
    setName(typed);
    markChanged();
  };

  const handleInputKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    if (event.key === 'Enter' && !event.nativeEvent.isComposing) {
      event.preventDefault();
      void handleOk();
    }
  };

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void handleCancel();
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
        aria-label={caption}
        tabIndex={-1}
        style={PANEL_STYLE}
        onKeyDown={handlePanelKeyDown}
      >
        <h2 style={TITLE_STYLE}>{caption}</h2>
        <div style={FIELDS_STYLE} aria-busy={loading}>
          <div style={ROW_STYLE}>
            <label htmlFor={inputId} style={LABEL_STYLE}>
              {NAME_CAPTION}
            </label>
            <input
              ref={inputRef}
              id={inputId}
              type="text"
              autoComplete="off"
              maxLength={NAME_MAX_LENGTH}
              readOnly={loading}
              value={name}
              style={inputStyle(pressed, hovered, focusVisible)}
              onChange={handleNameChange}
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
          <DialogButton aria-disabled={loading} onClick={() => void handleOk()}>
            {OK_CAPTION}
          </DialogButton>
          <DialogButton onClick={() => void handleCancel()}>{CANCEL_CAPTION}</DialogButton>
        </div>
      </div>
      {lock.element}
      {messageBox}
    </div>
  );
}
