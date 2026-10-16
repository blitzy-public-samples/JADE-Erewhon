import { useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, api } from '../../api/client';
import type { CountryRow, Id, OperationResult, RegionRequest, RegionRow } from '../../api/types';
import { trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditRegionDialog}. */
export interface EditRegionDialogProps {
  /** Internal id of the region edited; null, undefined or not a positive integer adds a new region. */
  regionId?: number | null;
  /** Internal id of the country selected when a new region is added; ignored when editing. */
  countryId?: number | null;
  /** Called with `true` after a save from OK succeeded, and with `false` when the dialog closes otherwise. */
  onClose: (saved: boolean) => void;
}

/** One of the two editable fields of the dialog. */
type Field = 'country' | 'name';

const COUNTRIES_PATH = '/api/app/admin/countries';
const REGIONS_PATH = '/api/app/admin/regions';

const FORM_CAPTION = 'Region';
const COUNTRY_CAPTION = 'Country';
const NAME_CAPTION = 'Name';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const NAME_MAX_LENGTH = 80;

const FOCUS_NAME = 'FOCUS: Name';
const FOCUS_COUNTRY = 'FOCUS: Country';

const FOCUSABLE_SELECTOR =
  'input:not(:disabled), select:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

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
  maxInlineSize: '18.625rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  paddingBlock: '0.625rem',
  paddingInlineStart: '0.625rem',
  paddingInlineEnd: '0.9375rem',
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
  gap: '0.5625rem',
};

const GRID_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'minmax(2.625rem, max-content) minmax(0, 14.4375rem)',
  alignItems: 'center',
  columnGap: '0.1875rem',
  rowGap: '0.25rem',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const FIELD_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  display: 'block',
  inlineSize: '100%',
  minInlineSize: 0,
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

const BASE_FIELD_STYLES: Readonly<Record<Field, CSSProperties>> = {
  country: { ...FIELD_STYLE, minBlockSize: '1.125rem' },
  name: { ...FIELD_STYLE, minBlockSize: '1.3125rem' },
};

const FIELD_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

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
 * Returns the style of `field`: the pressed, hovered or plain border, in that order of precedence, with the focus
 * outline added when `focusVisible` is true.
 */
function fieldStyle(field: Field, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = BASE_FIELD_STYLES[field];
  if (pressed) {
    style = { ...style, borderColor: INPUT_PRESSED_BORDER_COLOR };
  } else if (hovered) {
    style = { ...style, borderColor: INPUT_HOVER_BORDER_COLOR };
  }
  return focusVisible ? { ...style, ...FIELD_FOCUS_STYLE } : style;
}

/** Keeps the focus where it is when the mouse is pressed on the backdrop itself. */
function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

/** Returns true when `id` is a positive safe integer, the id of an existing row. */
function isRowId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the path of GET and PUT /api/app/admin/regions/{id}. */
function regionPath(regionId: Id): string {
  return `${REGIONS_PATH}/${regionId}`;
}

/** Returns the id of the option value `value` of the Country list, or null for the empty option. */
function parseCountryId(value: string): Id | null {
  if (value === '') {
    return null;
  }
  const id = Number(value);
  return isRowId(id) ? id : null;
}

/**
 * The modal "Region" editor of the administration area: the "Country" list and the "Name" box of one region, with
 * OK and Cancel. The dialog is labelled `Add Region` when adding and `Edit Region` when editing.
 *
 * - On mount, and whenever `regionId` or `countryId` changes, the fields are emptied and GET
 *   /api/app/admin/countries is read, with GET /api/app/admin/regions/{id} when editing; an answer to an earlier
 *   request is ignored. Until they answer the panel has `aria-busy="true"` and the progress cursor, and OK does
 *   nothing. The Country list then offers an empty entry followed by the countries in the order read. When adding,
 *   the country `countryId` is selected when the list holds it; when editing, the region's name fills Name and the
 *   region's country is selected. A field the user changed before the values arrive keeps its value. A 404
 *   calls `onClose(false)` and shows nothing; an error outcome is shown in a message box, an HTTP 401 shows nothing,
 *   and any other failure rejects unhandled.
 * - Name is focused on mount when a new region is added with `countryId` given, and Country otherwise. Tab and
 *   Shift+Tab wrap within the dialog, and the element focused before the dialog opened gets the focus back when the
 *   dialog closes and nothing else has taken it.
 * - The soft edit lock of the region is held through `useEditLock('Region', regionId)`: every change to either field
 *   marks the dialog changed, and the first one locks an existing region.
 * - OK, or Enter in either field, sends `{ name, countryId }` with Name trimmed of CR, LF, Tab, Space and NUL and the
 *   selected country's id, or null for the empty entry: POST /api/app/admin/regions when adding, PUT
 *   /api/app/admin/regions/{id} when editing, through the lock's save. A failed result focuses Name for `FOCUS: Name`
 *   or Country for `FOCUS: Country`, then shows its errors titled `$ErrorDuringSave`, and the dialog stays open; a
 *   succeeded result calls `onClose(true)`. While the request is outstanding the panel is busy, OK is disabled, and
 *   field changes, OK and Cancel are ignored. A 404 calls `onClose(false)`.
 * - Cancel, or Escape, closes through the lock's close path: with changes it asks `$ChangesSinceOpeningForm` with Yes,
 *   No and Cancel, and `onClose(false)` is called once closing is allowed.
 */
export function EditRegionDialog({ regionId, countryId, onClose }: EditRegionDialogProps): ReactElement {
  const editId = isRowId(regionId) ? regionId : null;
  const presetCountryId = editId === null && isRowId(countryId) ? countryId : null;

  const lock = useEditLock('Region', regionId ?? null);
  const { element: messageBox, showError } = useMessageBox();

  const countryInputId = useId();
  const nameInputId = useId();

  const [countries, setCountries] = useState<readonly CountryRow[]>([]);
  const [name, setName] = useState('');
  const [selectedCountryId, setSelectedCountryId] = useState<Id | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [hoveredField, setHoveredField] = useState<Field | null>(null);
  const [pressedField, setPressedField] = useState<Field | null>(null);
  const [focusVisibleField, setFocusVisibleField] = useState<Field | null>(null);

  const onCloseRef = useRef(onClose);
  const mountedRef = useRef(false);
  const busyRef = useRef(false);
  const changedRef = useRef<Set<Field>>(new Set());
  const initialFocusRef = useRef<Field>(presetCountryId === null ? 'country' : 'name');
  const panelRef = useRef<HTMLDivElement>(null);
  const countryRef = useRef<HTMLSelectElement>(null);
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
    const previous = document.activeElement;
    const panel = panelRef.current;
    const initial = initialFocusRef.current === 'name' ? nameRef.current : countryRef.current;
    initial?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useEffect(() => {
    let active = true;
    changedRef.current = new Set();
    setName('');
    setSelectedCountryId(null);
    setLoading(true);

    const load = async (): Promise<void> => {
      let rows: CountryRow[] | undefined;
      let region: RegionRow | undefined;
      try {
        [rows, region] = await Promise.all([
          api.get<CountryRow[] | undefined>(COUNTRIES_PATH),
          editId === null ? Promise.resolve(undefined) : api.get<RegionRow | undefined>(regionPath(editId)),
        ]);
      } catch (error: unknown) {
        if (!active) {
          return;
        }
        if (isNotFound(error)) {
          onCloseRef.current(false);
          return;
        }
        setLoading(false);
        await showError(error);
        return;
      }

      if (!active) {
        return;
      }

      const list = Array.isArray(rows) ? rows : [];
      setCountries(list);

      if (!changedRef.current.has('country')) {
        const wanted = editId === null ? presetCountryId : (region?.countryId ?? null);
        setSelectedCountryId(wanted !== null && list.some((country) => country.id === wanted) ? wanted : null);
      }
      if (editId !== null && !changedRef.current.has('name')) {
        setName(region?.name ?? '');
      }

      setLoading(false);
    };

    void load();

    return () => {
      active = false;
    };
  }, [editId, presetCountryId, showError]);

  const focusField = (field: string | null): void => {
    if (field === FOCUS_NAME) {
      nameRef.current?.focus();
    } else if (field === FOCUS_COUNTRY) {
      countryRef.current?.focus();
    }
  };

  const doSave = async (): Promise<OperationResult> => {
    const request: RegionRequest = { name: trimWhiteSpace(name), countryId: selectedCountryId };
    const result =
      editId === null
        ? await api.post<OperationResult>(REGIONS_PATH, request)
        : await api.put<OperationResult>(regionPath(editId), request);

    if (!result.succeeded && mountedRef.current) {
      focusField(result.focusField);
    }
    return result;
  };

  const noteChange = (field: Field): void => {
    changedRef.current.add(field);
    lock.markChanged();
  };

  const handleCountryChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    if (busyRef.current) {
      return;
    }
    setSelectedCountryId(parseCountryId(event.target.value));
    noteChange('country');
  };

  const handleNameChange = (event: ChangeEvent<HTMLInputElement>): void => {
    if (busyRef.current) {
      return;
    }
    setName(event.target.value);
    noteChange('name');
  };

  const handleOk = async (): Promise<void> => {
    if (busyRef.current || loading) {
      return;
    }
    busyRef.current = true;
    setSaving(true);

    let saved: boolean;
    try {
      saved = await lock.save(doSave);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
      if (mountedRef.current) {
        onCloseRef.current(false);
      }
      return;
    } finally {
      busyRef.current = false;
      if (mountedRef.current) {
        setSaving(false);
      }
    }

    if (saved && mountedRef.current) {
      onCloseRef.current(true);
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (busyRef.current) {
      return;
    }
    busyRef.current = true;

    let allowed: boolean;
    try {
      allowed = await lock.close(doSave);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
      allowed = true;
    } finally {
      busyRef.current = false;
    }

    if (allowed && mountedRef.current) {
      onCloseRef.current(false);
    }
  };

  const handleFieldKeyDown = (event: KeyboardEvent<HTMLInputElement | HTMLSelectElement>): void => {
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

  const fieldHandlers = (field: Field) => ({
    onMouseEnter: () => setHoveredField(field),
    onMouseLeave: () => {
      setHoveredField((current) => (current === field ? null : current));
      setPressedField((current) => (current === field ? null : current));
    },
    onMouseDown: (event: MouseEvent<HTMLElement>) => {
      if (event.button === 0) {
        setPressedField(field);
      }
    },
    onMouseUp: () => setPressedField(null),
    onFocus: (event: FocusEvent<HTMLElement>) => {
      setFocusVisibleField(matchesFocusVisible(event.currentTarget) ? field : null);
    },
    onKeyUp: (event: KeyboardEvent<HTMLElement>) => {
      setFocusVisibleField(matchesFocusVisible(event.currentTarget) ? field : null);
    },
    onBlur: () => setFocusVisibleField((current) => (current === field ? null : current)),
  });

  const styleOf = (field: Field): CSSProperties =>
    fieldStyle(field, pressedField === field, hoveredField === field, focusVisibleField === field);

  const busy = loading || saving;
  const caption = `${msg(editId === null ? 'Add' : 'Edit')} ${FORM_CAPTION}`;

  return (
    <>
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
            <div style={GRID_STYLE}>
              <label htmlFor={countryInputId} style={LABEL_STYLE}>
                {COUNTRY_CAPTION}
              </label>
              <select
                ref={countryRef}
                id={countryInputId}
                name="country"
                autoComplete="off"
                value={selectedCountryId === null ? '' : String(selectedCountryId)}
                style={styleOf('country')}
                onChange={handleCountryChange}
                onKeyDown={handleFieldKeyDown}
                {...fieldHandlers('country')}
              >
                <option value="" />
                {countries.map((country) => (
                  <option key={country.id} value={String(country.id)}>
                    {country.name}
                  </option>
                ))}
              </select>
              <label htmlFor={nameInputId} style={LABEL_STYLE}>
                {NAME_CAPTION}
              </label>
              <input
                ref={nameRef}
                id={nameInputId}
                name="name"
                type="text"
                autoComplete="off"
                spellCheck={false}
                maxLength={NAME_MAX_LENGTH}
                value={name}
                style={styleOf('name')}
                onChange={handleNameChange}
                onKeyDown={handleFieldKeyDown}
                {...fieldHandlers('name')}
              />
            </div>
            <hr aria-hidden="true" style={RULE_STYLE} />
          </div>
          <div style={BUTTON_ROW_STYLE}>
            <DialogButton aria-disabled={busy} onClick={() => void handleOk()}>
              {OK_CAPTION}
            </DialogButton>
            <DialogButton onClick={() => void handleCancel()}>{CANCEL_CAPTION}</DialogButton>
          </div>
        </div>
      </div>
      {lock.element}
      {messageBox}
    </>
  );
}
