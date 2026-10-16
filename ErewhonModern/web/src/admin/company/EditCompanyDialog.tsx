import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { flushSync } from 'react-dom';
import { api } from '../../api/client';
import type {
  AddressFields as AddressFieldsValue,
  CompanyProjection,
  CompanyRequest,
  OperationResult,
} from '../../api/types';
import { AddressFields, trimAddress, trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditCompanyDialog}. */
export interface EditCompanyDialogProps {
  /** Called once when the dialog closes; `saved` is true when a save of the company succeeded while it was open. */
  onClose: (saved: boolean) => void;
}

const COMPANY_PATH = '/api/app/admin/company';

const FORM_CAPTION = 'Company';
const NAME_GROUP_CAPTION = 'Name';
const NAME_CAPTION = 'Name';
const MANDATORY_FIELDS_CAPTION = 'Fields marked with a top-left red corner are mandatory';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const NAME_MAX_LENGTH = 80;
const FOCUS_NAME = 'FOCUS: Name';

const FOCUSABLE_SELECTOR = 'input:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

const EMPTY_ADDRESS: AddressFieldsValue = {
  street: '',
  city: '',
  country: '',
  phone: '',
  fax: '',
  email: '',
  webSite: '',
};

const GAP = '0.1875rem';
const BORDER = '1px solid rgb(128, 128, 128)';
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
  gap: '0.625rem',
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

const BUSY_PANEL_STYLE: CSSProperties = { ...PANEL_STYLE, cursor: 'progress' };

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const GROUPS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: GAP,
  minInlineSize: 0,
};

const NAME_FIELDSET_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '3.75rem minmax(0, 1fr)',
  alignItems: 'center',
  columnGap: GAP,
  rowGap: GAP,
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

/* BLITZY [A11Y]: disabled text #808080 on white is 3.9:1, below WCAG AA 4.5:1, as in the address inputs; flagged
   for designer review. */
const INPUT_DISABLED_STYLE: CSSProperties = { ...INPUT_STYLE, color: '#808080' };

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

const MANDATORY_FIELDS_STYLE: CSSProperties = {
  flex: '0 1 16.25rem',
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

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/**
 * Returns the style of the Name box: the disabled appearance, else the pressed, hovered or plain border in that
 * order of precedence, with the focus outline added when `focusVisible` is true.
 */
function nameInputStyle(disabled: boolean, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  if (disabled) {
    return INPUT_DISABLED_STYLE;
  }
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

/** Returns the seven address fields of `company`, each null value as the empty string. */
function addressOf(company: CompanyProjection): AddressFieldsValue {
  return {
    street: company.street ?? '',
    city: company.city ?? '',
    country: company.country ?? '',
    phone: company.phone ?? '',
    fax: company.fax ?? '',
    email: company.email ?? '',
    webSite: company.webSite ?? '',
  };
}

/** Returns true when the seven fields of `left` and `right` hold the same values. */
function sameAddress(left: AddressFieldsValue, right: AddressFieldsValue): boolean {
  return (
    left.street === right.street &&
    left.city === right.city &&
    left.country === right.country &&
    left.phone === right.phone &&
    left.fax === right.fax &&
    left.email === right.email &&
    left.webSite === right.webSite
  );
}

/** Returns the body of PUT /api/app/admin/company: `name` and the seven address fields after `trimWhiteSpace`. */
function companyRequest(name: string, address: AddressFieldsValue): CompanyRequest {
  return { name: trimWhiteSpace(name), address: trimAddress(address) };
}

/** Name and address text of the dialog's inputs, read by the save handlers when a request answers. */
interface FieldValues {
  name: string;
  address: AddressFieldsValue;
}

/**
 * The "Edit Company" dialog opened by Company › Company Details: the bound company's name in the "Name" group, its
 * address in the Address, Contact and Internet Web Site groups, the mandatory-fields line, and the OK and Cancel
 * buttons, in a modal overlay named "Edit Company".
 *
 * On mount the panel takes the focus and GET /api/app/admin/company is sent; until it answers with the company, the
 * inputs are disabled and OK has `aria-disabled`. The answer fills the eight inputs and moves the focus to Name when
 * the focus is still on the panel. An `ErrorOutcomeError` from that request is shown in a message box, an
 * `UnauthorizedError` shows nothing, any other failure rejects unhandled, and an answer after unmount is ignored.
 * The company's soft edit lock is handled by {@link useEditLock} with the type `Company` and the loaded company id;
 * every edit of an input marks the dialog changed.
 *
 * OK, and Enter pressed in the dialog outside its buttons, send PUT /api/app/admin/company with the name and the
 * address after `trimWhiteSpace` through the hook's `save`. A failed result shows its full error text titled
 * `$ErrorDuringSave` and keeps the dialog open, with the focus on Name for `FOCUS: Name` and on the matching address
 * input for an address focus field; a succeeded result calls `onClose(true)`. Ctrl+S sends the same save and keeps the
 * dialog open; after a success the inputs show the trimmed values when they were not edited while the save ran.
 * Cancel and Escape run the hook's `close`: with unsaved changes it asks `$ChangesSinceOpeningForm`, Yes saving as OK
 * does, and a closing dialog calls `onClose` with true when a save succeeded while it was open, else false. Presses
 * that arrive while a save or close is running are ignored, and the panel shows the progress cursor meanwhile.
 *
 * Tab and Shift+Tab wrap within the dialog, a press on the backdrop keeps the focus where it is, and on unmount the
 * element focused before the dialog opened gets the focus back when nothing else has taken it.
 */
export function EditCompanyDialog({ onClose }: EditCompanyDialogProps): ReactElement {
  const { element: messageBox, showError } = useMessageBox();
  const [company, setCompany] = useState<CompanyProjection | null>(null);
  const [loading, setLoading] = useState(true);
  const [name, setName] = useState('');
  const [address, setAddress] = useState<AddressFieldsValue>(EMPTY_ADDRESS);
  const [focusField, setFocusField] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [nameHovered, setNameHovered] = useState(false);
  const [namePressed, setNamePressed] = useState(false);
  const [nameFocusVisible, setNameFocusVisible] = useState(false);
  const lock = useEditLock('Company', company?.id ?? null);
  const nameId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const nameInputRef = useRef<HTMLInputElement>(null);
  const valuesRef = useRef<FieldValues>({ name: '', address: EMPTY_ADDRESS });
  const onCloseRef = useRef(onClose);
  const busyRef = useRef(false);
  const savedRef = useRef(false);
  const mountedRef = useRef(false);
  const editable = company !== null;
  const caption = `${msg('Edit')} ${FORM_CAPTION}`;

  useEffect(() => {
    onCloseRef.current = onClose;
  });

  useEffect(() => {
    mountedRef.current = true;
    const previous = document.activeElement;
    const panel = panelRef.current;
    panel?.focus();

    return () => {
      mountedRef.current = false;
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useEffect(() => {
    let active = true;

    api.get<CompanyProjection | undefined>(COMPANY_PATH).then(
      (loaded: CompanyProjection | undefined) => {
        if (!active) {
          return;
        }
        setLoading(false);
        if (typeof loaded?.id !== 'number') {
          return;
        }
        const loadedValues: FieldValues = { name: loaded.name ?? '', address: addressOf(loaded) };
        valuesRef.current = loadedValues;
        setName(loadedValues.name);
        setAddress(loadedValues.address);
        setCompany(loaded);
      },
      (error: unknown) => {
        if (!active) {
          return undefined;
        }
        setLoading(false);
        return showError(error);
      },
    );

    return () => {
      active = false;
    };
  }, [showError]);

  useEffect(() => {
    if (!editable) {
      return;
    }
    const current = document.activeElement;
    if (current === null || current === document.body || current === panelRef.current) {
      nameInputRef.current?.focus();
    }
  }, [editable]);

  useLayoutEffect(() => {
    if (focusField === FOCUS_NAME) {
      nameInputRef.current?.focus();
    }
  }, [focusField]);

  const putCompany = useCallback(async (request: CompanyRequest): Promise<OperationResult> => {
    flushSync(() => setFocusField(null));
    const result = await api.put<OperationResult>(COMPANY_PATH, request);
    if (!result.succeeded && mountedRef.current) {
      setFocusField(result.focusField);
    }
    return result;
  }, []);

  /** Runs `action` unless another save or close is running, with the progress cursor shown meanwhile. */
  const runExclusive = async (action: () => Promise<void>): Promise<void> => {
    if (busyRef.current) {
      return;
    }
    busyRef.current = true;
    setBusy(true);
    try {
      await action();
    } finally {
      busyRef.current = false;
      if (mountedRef.current) {
        setBusy(false);
      }
    }
  };

  const handleOk = async (): Promise<void> => {
    if (!editable) {
      return;
    }
    await runExclusive(async () => {
      const { name: sentName, address: sentAddress } = valuesRef.current;
      const request = companyRequest(sentName, sentAddress);
      if (await lock.save(() => putCompany(request))) {
        savedRef.current = true;
        if (mountedRef.current) {
          onCloseRef.current(true);
        }
      }
    });
  };

  const handleSaveShortcut = async (): Promise<void> => {
    if (!editable) {
      return;
    }
    await runExclusive(async () => {
      const sent = valuesRef.current;
      const request = companyRequest(sent.name, sent.address);
      if (!(await lock.save(() => putCompany(request)))) {
        return;
      }
      savedRef.current = true;
      const current = valuesRef.current;
      if (!mountedRef.current || current.name !== sent.name || !sameAddress(current.address, sent.address)) {
        return;
      }
      const stored: FieldValues = { name: request.name ?? '', address: request.address ?? EMPTY_ADDRESS };
      valuesRef.current = stored;
      setName(stored.name);
      setAddress(stored.address);
    });
  };

  const handleCancel = (): Promise<void> =>
    runExclusive(async () => {
      const { name: sentName, address: sentAddress } = valuesRef.current;
      const request = companyRequest(sentName, sentAddress);
      const outcome = { saved: false };
      const closed = await lock.close(async () => {
        const result = await putCompany(request);
        outcome.saved = result.succeeded;
        return result;
      });
      if (!closed || !mountedRef.current) {
        return;
      }
      if (outcome.saved) {
        savedRef.current = true;
      }
      onCloseRef.current(savedRef.current);
    });

  const handleNameChange = (event: ChangeEvent<HTMLInputElement>): void => {
    const next = event.target.value;
    valuesRef.current = { ...valuesRef.current, name: next };
    setName(next);
    lock.markChanged();
  };

  const handleAddressChange = (next: AddressFieldsValue): void => {
    valuesRef.current = { ...valuesRef.current, address: next };
    setAddress(next);
    lock.markChanged();
  };

  const updateNameFocusVisible = (event: FocusEvent<HTMLInputElement> | KeyboardEvent<HTMLInputElement>): void => {
    setNameFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const handlePanelKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void handleCancel();
      return;
    }

    if (event.key === 'Enter' && !event.nativeEvent.isComposing && !(event.target instanceof HTMLButtonElement)) {
      event.preventDefault();
      event.stopPropagation();
      void handleOk();
      return;
    }

    if (event.key.toLowerCase() === 's' && event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      event.stopPropagation();
      void handleSaveShortcut();
      return;
    }

    const panel = panelRef.current;
    if (event.key !== 'Tab' || panel === null) {
      return;
    }

    const focusable = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
    if (focusable.length === 0) {
      event.preventDefault();
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
        style={busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handlePanelKeyDown}
      >
        <h2 style={TITLE_STYLE}>{caption}</h2>
        <div style={GROUPS_STYLE} aria-busy={loading ? 'true' : 'false'}>
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
                disabled={!editable}
                aria-required="true"
                style={nameInputStyle(!editable, namePressed, nameHovered, nameFocusVisible)}
                onChange={handleNameChange}
                onMouseEnter={editable ? () => setNameHovered(true) : undefined}
                onMouseLeave={
                  editable
                    ? () => {
                        setNameHovered(false);
                        setNamePressed(false);
                      }
                    : undefined
                }
                onMouseDown={
                  editable
                    ? (event: MouseEvent<HTMLInputElement>) => {
                        if (event.button === 0) {
                          setNamePressed(true);
                        }
                      }
                    : undefined
                }
                onMouseUp={editable ? () => setNamePressed(false) : undefined}
                onFocus={editable ? updateNameFocusVisible : undefined}
                onKeyUp={editable ? updateNameFocusVisible : undefined}
                onBlur={() => setNameFocusVisible(false)}
              />
              <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />
            </span>
          </fieldset>
          <AddressFields
            value={address}
            onChange={handleAddressChange}
            disabled={!editable}
            focusField={focusField}
          />
        </div>
        <hr aria-hidden="true" style={RULE_STYLE} />
        <div style={FOOTER_STYLE}>
          <p style={MANDATORY_FIELDS_STYLE}>{MANDATORY_FIELDS_CAPTION}</p>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={!editable} onClick={() => void handleOk()}>
              {OK_CAPTION}
            </DialogButton>
            <DialogButton onClick={() => void handleCancel()}>{CANCEL_CAPTION}</DialogButton>
          </div>
        </div>
      </div>
      {lock.element ?? messageBox}
    </div>
  );
}

