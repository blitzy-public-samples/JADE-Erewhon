import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type { CountryRow, EntityTypeName, OperationResult, RegionRow } from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { EditCountryDialog } from './EditCountryDialog';

/** Properties of {@link CountriesListPage}. */
export interface CountriesListPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** One entry of a list box: the row id it stands for and the text it shows. */
interface ListEntry {
  id: number;
  text: string;
}

/** Country the editor dialog is open for; `countryId` null adds a new country. */
interface EditorTarget {
  countryId: number | null;
}

const COUNTRIES_PATH = '/api/app/admin/countries';

const REGIONS_CAPTION = 'Regions';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['Country', 'Region'];

/** Error number and text of the object-not-found exception outcome. */
const OBJECT_NOT_FOUND_CODE = 4;
const OBJECT_NOT_FOUND_TEXT = 'Object not found';

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.625rem',
  boxSizing: 'border-box',
  maxInlineSize: '31.9375rem',
  padding: '0.625rem',
};

const BUSY_SECTION_STYLE: CSSProperties = {
  ...SECTION_STYLE,
  cursor: 'progress',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const BODY_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  gap: '1.25rem',
};

const COUNTRIES_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '1 1 14.25rem',
  minInlineSize: 0,
};

const REGIONS_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.125rem',
  flex: '1 1 15.1875rem',
  minInlineSize: 0,
};

const REGIONS_LABEL_STYLE: CSSProperties = {
  minBlockSize: '1.3125rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const LIST_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  margin: 0,
  padding: 0,
  overflowY: 'auto',
  listStyle: 'none',
  color: 'black',
  border: LIST_BORDER,
};

const COUNTRIES_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '17.75rem',
  background: 'white',
};

const REGIONS_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '16.3125rem',
  background: 'rgb(240, 240, 240)',
};

const ENTRY_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minBlockSize: '1.3125rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.25rem',
  cursor: 'default',
  overflowWrap: 'anywhere',
  userSelect: 'none',
};

const BUTTON_BAR_STYLE: CSSProperties = {
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

/** Returns the selected, pressed or hovered background of an entry, in that order, or `undefined`. */
function entryBackground(selected: boolean, pressed: boolean, hovered: boolean): string | undefined {
  if (selected) {
    return SELECTED_BACKGROUND;
  }
  if (pressed) {
    return PRESSED_BACKGROUND;
  }
  if (hovered) {
    return HOVER_BACKGROUND;
  }
  return undefined;
}

/** Returns the style of an entry with `background` and, when `focusVisible` is true, the inset focus outline. */
function entryStyle(background: string | undefined, focusVisible: boolean): CSSProperties {
  const style: CSSProperties = { ...ENTRY_STYLE };
  if (background !== undefined) {
    style.background = background;
  }
  if (focusVisible) {
    style.outline = FOCUS_OUTLINE;
    style.outlineOffset = FOCUS_OUTLINE_OFFSET;
  }
  return style;
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

/** Returns the list entry of `country`: its row id and name. */
function countryEntry(country: CountryRow): ListEntry {
  return { id: country.id, text: country.name };
}

/** Returns the list entry of `region`: its row id and name. */
function regionEntry(region: RegionRow): ListEntry {
  return { id: region.id, text: region.name };
}

/** Properties of {@link EntryList}. */
interface EntryListProps {
  /** Accessible name of the list box. */
  label: string;
  /** Entries in display order. */
  entries: readonly ListEntry[];
  /** Row id of the selected entry; null while none is selected. */
  selectedId: number | null;
  /** True while the entries are being read. */
  busy: boolean;
  /** Style of the list box. */
  listStyle: CSSProperties;
  /** Called with the entry a click, Space, Enter or a navigation key selects; omitted for a read-only list. */
  onSelect?: (id: number) => void;
  /** Called with the entry a double-click or Enter opens. */
  onActivate?: (id: number) => void;
}

/**
 * Single-selection list box of `role="listbox"` whose entries are `role="option"` elements showing their text.
 *
 * One entry is the tab stop: the entry focused last while it is listed, otherwise the selected entry while it is
 * listed, otherwise the first entry. ArrowDown, ArrowUp, Home and End, pressed without Ctrl, Alt or Meta, focus the
 * next, previous, first or last entry and select it; Space selects the focused entry, and Enter selects and opens it.
 * A click selects an entry and a double-click selects and opens it.
 *
 * Without `onSelect` the list is read-only: `aria-readonly="true"`, no entry is ever selected, and the navigation keys
 * only move the focus. The selected entry has a rgb(204, 228, 247) background; in a selectable list an entry that is
 * not selected has a rgb(230, 230, 230) background while the primary mouse button is held on it and a
 * rgb(242, 242, 242) background while the pointer is over it. An entry focused with `:focus-visible`, checked again on
 * every key release, has a 2px rgb(0, 80, 160) outline inset by 2px.
 */
function EntryList({ label, entries, selectedId, busy, listStyle, onSelect, onActivate }: EntryListProps): ReactElement {
  const readOnly = onSelect === undefined;
  const [focusedId, setFocusedId] = useState<number | null>(null);
  const [hoveredId, setHoveredId] = useState<number | null>(null);
  const [pressedId, setPressedId] = useState<number | null>(null);
  const [focusVisibleId, setFocusVisibleId] = useState<number | null>(null);
  const entryElements = useRef(new Map<number, HTMLLIElement>());

  const isListed = (id: number | null): id is number => id !== null && entries.some((entry) => entry.id === id);

  let tabStopId: number | null = entries[0]?.id ?? null;
  if (isListed(focusedId)) {
    tabStopId = focusedId;
  } else if (!readOnly && isListed(selectedId)) {
    tabStopId = selectedId;
  }

  const focusEntry = (id: number): void => {
    setFocusedId(id);
    entryElements.current.get(id)?.focus();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLLIElement>, position: number): void => {
    const entry = entries[position];
    if (entry === undefined || event.ctrlKey || event.altKey || event.metaKey) {
      return;
    }

    let target: ListEntry | undefined;
    switch (event.key) {
      case ' ':
        event.preventDefault();
        onSelect?.(entry.id);
        return;
      case 'Enter':
        event.preventDefault();
        onSelect?.(entry.id);
        onActivate?.(entry.id);
        return;
      case 'ArrowDown':
        target = entries[position + 1];
        break;
      case 'ArrowUp':
        target = entries[position - 1];
        break;
      case 'Home':
        target = entries[0];
        break;
      case 'End':
        target = entries[entries.length - 1];
        break;
      default:
        return;
    }

    event.preventDefault();
    if (target !== undefined) {
      focusEntry(target.id);
      onSelect?.(target.id);
    }
  };

  const updateFocusVisible = (event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>, id: number): void => {
    setFocusVisibleId(matchesFocusVisible(event.currentTarget) ? id : null);
  };

  return (
    <ul
      role="listbox"
      aria-label={label}
      aria-readonly={readOnly ? true : undefined}
      aria-busy={busy}
      style={listStyle}
    >
      {entries.map((entry, position) => {
        const { id } = entry;
        const selected = !readOnly && id === selectedId;
        const background = entryBackground(
          selected,
          !readOnly && id === pressedId,
          !readOnly && id === hoveredId,
        );

        return (
          <li
            key={id}
            ref={(element) => {
              if (element === null) {
                entryElements.current.delete(id);
              } else {
                entryElements.current.set(id, element);
              }
            }}
            role="option"
            aria-selected={selected}
            tabIndex={id === tabStopId ? 0 : -1}
            style={entryStyle(background, id === focusVisibleId)}
            onClick={() => onSelect?.(id)}
            onDoubleClick={() => {
              onSelect?.(id);
              onActivate?.(id);
            }}
            onKeyDown={(event) => handleKeyDown(event, position)}
            onKeyUp={(event) => updateFocusVisible(event, id)}
            onFocus={(event) => {
              setFocusedId(id);
              updateFocusVisible(event, id);
            }}
            onBlur={() => setFocusVisibleId(null)}
            onMouseEnter={() => setHoveredId(id)}
            onMouseLeave={() => {
              setHoveredId(null);
              setPressedId(null);
            }}
            onMouseDown={(event) => {
              if (event.button === 0) {
                setPressedId(id);
              }
            }}
            onMouseUp={() => setPressedId(null)}
          >
            {entry.text}
          </li>
        );
      })}
    </ul>
  );
}

/**
 * The "Countries" tab of the admin area: the countries of the bound company, the regions of the selected country,
 * and the Add, Edit, Remove and Close buttons. No control depends on the session's role.
 *
 * - On mount GET /api/app/admin/countries reads the countries, each with its regions; the countries are listed by name
 *   in the order received and the list has `aria-busy="true"` until the first answer. Every EntityChanged event for a
 *   country or a region, every close of the editor and every Remove reads them again; only the answer to the latest
 *   read is shown, and a selected country the answer no longer holds is deselected.
 * - Selecting a country lists the names of its regions in the read-only "Regions" list, in the order received, and
 *   enables Edit and Remove; while no country is selected the regions list is empty and Edit and Remove are
 *   `aria-disabled`.
 * - Add opens {@link EditCountryDialog} for a new country. Edit, a double-click on a country or Enter on it opens the
 *   dialog for the selected country. When the dialog closes it is unmounted and the countries are read again.
 * - Remove sends DELETE /api/app/admin/countries/{id} for the selected country at once, with no confirmation. A
 *   succeeded result deselects the country; a failed result shows its errors joined with CR LF in an OK box with the
 *   information icon titled `$ErrorDuringSave`; a 404 shows the exception outcome of error 4, "Object not found".
 *   While the request is outstanding the page is `aria-busy` and ignores every control except Close.
 * - Close calls `onClose`.
 * - An error outcome is shown in its message box, an HTTP 401 shows nothing, and any other failure shows its message
 *   in an OK box with the error icon titled `$Error`.
 */
export function CountriesListPage({ onClose }: CountriesListPageProps = {}): ReactElement {
  const { element: messageBox, show, showError } = useMessageBox();
  const [countries, setCountries] = useState<readonly CountryRow[] | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [editor, setEditor] = useState<EditorTarget | null>(null);
  const [deleting, setDeleting] = useState(false);
  const mountedRef = useRef(false);
  const loadSeqRef = useRef(0);
  const deletingRef = useRef(false);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Shows `error`: an error outcome in its box, nothing for a 401, and any other failure titled `$Error`. */
  const reportFailure = useCallback(
    async (error: unknown): Promise<void> => {
      if (!mountedRef.current) {
        return;
      }
      try {
        await showError(error);
      } catch {
        await show({
          title: msg('Error'),
          message: error instanceof Error ? error.message : String(error),
          icon: 'error',
          buttons: 'ok',
        });
      }
    },
    [show, showError],
  );

  /** Reads the countries with their regions and keeps the selection while the selected country is still listed. */
  const loadCountries = useCallback(async (): Promise<void> => {
    loadSeqRef.current += 1;
    const seq = loadSeqRef.current;
    const isCurrent = (): boolean => mountedRef.current && seq === loadSeqRef.current;

    let rows: CountryRow[] | undefined;
    try {
      rows = await api.get<CountryRow[] | undefined>(COUNTRIES_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        setCountries((current) => current ?? []);
        await reportFailure(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    const list = Array.isArray(rows) ? rows : [];
    setCountries(list);
    setSelectedId((current) => (current !== null && list.some((country) => country.id === current) ? current : null));
  }, [reportFailure]);

  useEffect(() => {
    void loadCountries();
  }, [loadCountries]);

  useEffect(() => onEntityChanged(WATCHED_ENTITY_TYPES, () => loadCountries()), [loadCountries]);

  const selectedCountry = countries?.find((country) => country.id === selectedId) ?? null;
  const regions: readonly RegionRow[] = selectedCountry?.regions ?? [];
  const selectionActionsEnabled = selectedCountry !== null && !deleting;

  /** Port of CountriesListForm.lstCountries_click for a listed country. */
  const selectCountry = (id: number): void => {
    if (!deletingRef.current) {
      setSelectedId(id);
    }
  };

  /** Port of CountriesListForm.lstCountries_dblClick: opens the editor for the country. */
  const openCountry = (id: number): void => {
    if (!deletingRef.current) {
      setSelectedId(id);
      setEditor({ countryId: id });
    }
  };

  /** Port of CountriesListForm.btnAdd_click. */
  const addCountry = (): void => {
    if (!deletingRef.current) {
      setEditor({ countryId: null });
    }
  };

  /** Port of CountriesListForm.btnEdit_click. */
  const editCountry = (): void => {
    if (!deletingRef.current && selectedCountry !== null) {
      setEditor({ countryId: selectedCountry.id });
    }
  };

  /** Unmounts the editor and reads the countries again. */
  const closeEditor = (): void => {
    setEditor(null);
    void loadCountries();
  };

  const endDelete = (): void => {
    deletingRef.current = false;
    if (mountedRef.current) {
      setDeleting(false);
    }
  };

  /** Port of CountriesListForm.btnRemove_click followed by ErewhonBaseForm.doDelete. */
  const removeCountry = async (): Promise<void> => {
    if (deletingRef.current || selectedCountry === null) {
      return;
    }
    const { id } = selectedCountry;

    deletingRef.current = true;
    setDeleting(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(countryPath(id));
    } catch (error: unknown) {
      endDelete();
      if (!mountedRef.current) {
        return;
      }
      if (isNotFound(error)) {
        await Promise.all([loadCountries(), reportFailure(objectNotFoundOutcome())]);
        return;
      }
      await reportFailure(error);
      return;
    }

    endDelete();
    if (!mountedRef.current) {
      return;
    }
    if (result !== undefined && !result.succeeded) {
      await show(displayErrorsOptions(result));
      return;
    }
    if (result !== undefined) {
      setSelectedId((current) => (current === id ? null : current));
    }
    await loadCountries();
  };

  return (
    <section aria-label={msg('Countries')} aria-busy={deleting} style={deleting ? BUSY_SECTION_STYLE : SECTION_STYLE}>
      <h2 style={HEADING_STYLE}>{msg('Countries')}</h2>
      <div style={BODY_STYLE}>
        <div style={COUNTRIES_COLUMN_STYLE}>
          <EntryList
            label={msg('Countries')}
            entries={(countries ?? []).map(countryEntry)}
            selectedId={selectedCountry?.id ?? null}
            busy={countries === null}
            listStyle={COUNTRIES_LIST_STYLE}
            onSelect={selectCountry}
            onActivate={openCountry}
          />
        </div>
        <div style={REGIONS_COLUMN_STYLE}>
          <div style={REGIONS_LABEL_STYLE}>{REGIONS_CAPTION}</div>
          <EntryList
            label={REGIONS_CAPTION}
            entries={regions.map(regionEntry)}
            selectedId={null}
            busy={false}
            listStyle={REGIONS_LIST_STYLE}
          />
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton aria-disabled={deleting} onClick={addCountry}>
          {ADD_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!selectionActionsEnabled} onClick={editCountry}>
          {EDIT_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!selectionActionsEnabled} onClick={() => void removeCountry()}>
          {REMOVE_CAPTION}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {editor !== null && <EditCountryDialog countryId={editor.countryId} onClose={closeEditor} />}
      {messageBox}
    </section>
  );
}
