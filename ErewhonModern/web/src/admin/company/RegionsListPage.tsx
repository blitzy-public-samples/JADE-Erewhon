import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, UnauthorizedError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AdminItemSearchRequest,
  CountryRow,
  EntityTypeName,
  Id,
  ItemListRow,
  OperationResult,
  RegionRow,
} from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { EditRegionDialog } from './EditRegionDialog';

/** Properties of {@link RegionsListPage}. */
export interface RegionsListPageProps {
  /** Called when Close is clicked. */
  onClose?: () => void;
}

/** Region and preset country of the open {@link EditRegionDialog}. */
interface DialogTarget {
  /** React key of the dialog, distinct for each opening. */
  key: number;
  /** Region edited; null adds a new region. */
  regionId: Id | null;
  /** Country preset for a new region; null for none. */
  countryId: Id | null;
}

const COUNTRIES_PATH = '/api/app/admin/countries';
const REGIONS_PATH = '/api/app/admin/regions';
const ITEM_SEARCH_PATH = '/api/app/admin/items/search';

const REGION_ITEMS_CAPTION = 'Region Items';
const CLOSE_CAPTION = 'Close';

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = [
  'Country',
  'Region',
  'ItemCategory',
  'RetailItem',
  'TenderItem',
];

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  maxInlineSize: '31.9375rem',
  paddingBlock: '0.625rem',
};

const BUSY_SECTION_STYLE: CSSProperties = {
  ...SECTION_STYLE,
  cursor: 'progress',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  paddingInline: '0.625rem',
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const BODY_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'stretch',
};

const COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  boxSizing: 'border-box',
  minInlineSize: 0,
  paddingBlockStart: '0.125rem',
  paddingInline: '0.625rem',
};

const LEFT_COLUMN_STYLE: CSSProperties = {
  ...COLUMN_STYLE,
  flex: '1 1 15.9375rem',
};

const RIGHT_COLUMN_STYLE: CSSProperties = {
  ...COLUMN_STYLE,
  flex: '1 1 16rem',
};

const CAPTION_STYLE: CSSProperties = {
  display: 'block',
  minBlockSize: '1.3125rem',
  overflowWrap: 'break-word',
};

const REGION_CAPTION_STYLE: CSSProperties = {
  ...CAPTION_STYLE,
  paddingBlockStart: '0.1875rem',
};

const ITEMS_CAPTION_STYLE: CSSProperties = {
  ...CAPTION_STYLE,
  textAlign: 'center',
};

const SELECT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  minBlockSize: '1.125rem',
  margin: 0,
  font: 'inherit',
};

const LIST_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  margin: 0,
  padding: 0,
  overflowY: 'auto',
  listStyle: 'none',
  background: 'white',
  color: 'black',
  border: LIST_BORDER,
};

const REGIONS_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '13.6875rem',
};

const ITEMS_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  flex: '1 1 auto',
  minBlockSize: '16.3125rem',
};

const ITEMS_LIST_FOCUS_STYLE: CSSProperties = {
  ...ITEMS_LIST_STYLE,
  outline: FOCUS_OUTLINE,
  outlineOffset: FOCUS_OUTLINE_OFFSET,
};

const ENTRY_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  paddingBlock: '0.0625rem',
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
  paddingInline: '0.625rem',
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

/** Returns the id of the Country option value `value`, or null for the empty option or a value that is not an id. */
function parseCountryId(value: string): Id | null {
  if (value === '') {
    return null;
  }
  const id = Number(value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

/** Returns the path of GET /api/app/admin/regions for the regions of `countryId`. */
function regionsOfCountryPath(countryId: Id): string {
  return `${REGIONS_PATH}?country=${encodeURIComponent(String(countryId))}`;
}

/** Returns the search request that lists every item of `regionId`, sold items and closed tenders included. */
function regionItemsRequest(regionId: Id): AdminItemSearchRequest {
  return {
    agentId: null,
    clientId: null,
    countryId: null,
    regionId,
    itemCategoryId: null,
    onlyRetailItems: false,
    onlyTenderItems: false,
    priceLow: 0,
    priceHigh: 0,
    includeSoldItems: true,
    includeClosedTenders: true,
  };
}

/** Compares `a` and `b` by UTF-16 code unit. */
function compareOrdinal(a: string, b: string): number {
  if (a < b) {
    return -1;
  }
  return a > b ? 1 : 0;
}

/** Returns `rows` ordered by category prefix, compared by UTF-16 code unit with no prefix first, then by code number. */
function inPrefixNumberOrder(rows: readonly ItemListRow[]): ItemListRow[] {
  return [...rows].sort(
    (a, b) =>
      compareOrdinal(a.item.categoryPrefix ?? '', b.item.categoryPrefix ?? '') || a.item.codeNumber - b.item.codeNumber,
  );
}

/** Returns the selected, pressed or hovered background of a region entry, in that order, or `undefined`. */
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

/** Returns the style of a region entry with `background` and, when `focusVisible`, the inset focus outline. */
function regionEntryStyle(background: string | undefined, focusVisible: boolean): CSSProperties {
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

/**
 * The "Regions" page: the countries of the bound company in "Country", the regions of the chosen country in
 * "Region", and the names of the items listed in the selected region in "Region Items", with Add, Edit, Remove and
 * Close.
 *
 * - On mount it reads the countries (GET /api/app/admin/countries) in name order after an empty entry, which is
 *   chosen; Edit and Remove are disabled.
 * - Choosing a country empties "Region Items", clears the region selection, disables Edit and Remove and reads the
 *   country's regions in name order (GET /api/app/admin/regions?country={id}); choosing the empty entry empties
 *   "Region".
 * - Clicking a region, pressing Space or Enter on it, or reaching it with ArrowDown, ArrowUp, Home or End selects it,
 *   enables Edit and Remove and lists the region's items, sold items and closed tenders included
 *   (POST /api/app/admin/items/search), ordered by category prefix and then code number. "Region Items" is read-only.
 * - Edit, a double-click on a region and Enter on a region open {@link EditRegionDialog} on the selected region; Edit
 *   does nothing while no region is selected. Add opens it for a new region with the chosen country, or none, preset.
 *   When the dialog closes it is unmounted and the chosen country's regions are read again.
 * - Remove sends DELETE /api/app/admin/regions/{id} for the selected region at once. A failed delete shows its errors
 *   joined with CR LF in an OK box titled `$ErrorDuringSave`; a successful one clears the region selection, empties
 *   "Region Items", disables Edit and Remove and reads the regions again. A 404 is handled as a successful delete.
 * - Close calls `onClose`.
 * - Every `EntityChanged` event for a country, region, item category, retail item or tender item reads the countries,
 *   the chosen country's regions and the selected region's items again, keeping only the latest answer to each. A
 *   chosen country or selected region that is no longer listed is unchosen, as choosing the empty entry or clearing
 *   the region selection does. A 404 for the regions reads the countries again.
 * - While a request is outstanding the page is `aria-busy` and shows the wait cursor. An error outcome is shown in
 *   its message box, an HTTP 401 shows nothing, and any other failure shows its message titled `$Error`.
 */
export function RegionsListPage({ onClose }: RegionsListPageProps = {}): ReactElement {
  const { element: messageBox, show, showError } = useMessageBox();
  const [countries, setCountries] = useState<readonly CountryRow[]>([]);
  const [countryId, setCountryId] = useState<Id | null>(null);
  const [regions, setRegions] = useState<readonly RegionRow[]>([]);
  const [regionId, setRegionId] = useState<Id | null>(null);
  const [items, setItems] = useState<readonly ItemListRow[]>([]);
  const [dialog, setDialog] = useState<DialogTarget | null>(null);
  const [pending, setPending] = useState(0);
  const [focusedRegionId, setFocusedRegionId] = useState<Id | null>(null);
  const [hoveredRegionId, setHoveredRegionId] = useState<Id | null>(null);
  const [pressedRegionId, setPressedRegionId] = useState<Id | null>(null);
  const [focusVisibleRegionId, setFocusVisibleRegionId] = useState<Id | null>(null);
  const [itemsFocusVisible, setItemsFocusVisible] = useState(false);
  const mountedRef = useRef(false);
  const countryIdRef = useRef<Id | null>(null);
  const regionIdRef = useRef<Id | null>(null);
  const countriesSeqRef = useRef(0);
  const regionsSeqRef = useRef(0);
  const itemsSeqRef = useRef(0);
  const dialogKeyRef = useRef(0);
  const removingRef = useRef(false);
  const regionElements = useRef(new Map<Id, HTMLLIElement>());
  const countrySelectId = useId();

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const track = useCallback(async <T,>(request: Promise<T>): Promise<T> => {
    setPending((count) => count + 1);
    try {
      return await request;
    } finally {
      if (mountedRef.current) {
        setPending((count) => count - 1);
      }
    }
  }, []);

  const reportFailure = useCallback(
    async (error: unknown): Promise<void> => {
      if (!mountedRef.current) {
        return;
      }
      if (error instanceof ErrorOutcomeError || error instanceof UnauthorizedError) {
        await showError(error);
        return;
      }
      await show({
        title: msg('Error'),
        message: error instanceof Error ? error.message : String(error),
        icon: 'error',
        buttons: 'ok',
      });
    },
    [show, showError],
  );

  /** Sets the selected region, empties "Region Items" and drops every outstanding items answer. */
  const applyRegion = useCallback((id: Id | null): void => {
    regionIdRef.current = id;
    itemsSeqRef.current += 1;
    setRegionId(id);
    setItems([]);
  }, []);

  /** Sets the chosen country, empties "Region", drops every outstanding regions answer and clears the region. */
  const applyCountry = useCallback(
    (id: Id | null): void => {
      countryIdRef.current = id;
      regionsSeqRef.current += 1;
      setCountryId(id);
      setRegions([]);
      applyRegion(null);
    },
    [applyRegion],
  );

  const loadCountries = useCallback(async (): Promise<void> => {
    countriesSeqRef.current += 1;
    const seq = countriesSeqRef.current;
    let rows: CountryRow[] | undefined;
    try {
      rows = await track(api.get<CountryRow[] | undefined>(COUNTRIES_PATH));
    } catch (error: unknown) {
      await reportFailure(error);
      return;
    }
    if (!mountedRef.current || seq !== countriesSeqRef.current) {
      return;
    }

    const list = Array.isArray(rows) ? rows : [];
    setCountries(list);
    const chosen = countryIdRef.current;
    if (chosen !== null && !list.some((country) => country.id === chosen)) {
      applyCountry(null);
    }
  }, [track, reportFailure, applyCountry]);

  const loadItems = useCallback(
    async (id: Id): Promise<void> => {
      itemsSeqRef.current += 1;
      const seq = itemsSeqRef.current;
      let rows: ItemListRow[] | undefined;
      try {
        rows = await track(api.post<ItemListRow[] | undefined>(ITEM_SEARCH_PATH, regionItemsRequest(id)));
      } catch (error: unknown) {
        await reportFailure(error);
        return;
      }
      if (!mountedRef.current || seq !== itemsSeqRef.current || regionIdRef.current !== id) {
        return;
      }

      setItems(inPrefixNumberOrder(Array.isArray(rows) ? rows : []));
    },
    [track, reportFailure],
  );

  const loadRegions = useCallback(
    async (id: Id): Promise<void> => {
      regionsSeqRef.current += 1;
      const seq = regionsSeqRef.current;
      let rows: RegionRow[] | undefined;
      try {
        rows = await track(api.get<RegionRow[] | undefined>(regionsOfCountryPath(id)));
      } catch (error: unknown) {
        if (!isNotFound(error)) {
          await reportFailure(error);
          return;
        }
        if (mountedRef.current && seq === regionsSeqRef.current && countryIdRef.current === id) {
          await loadCountries();
        }
        return;
      }
      if (!mountedRef.current || seq !== regionsSeqRef.current || countryIdRef.current !== id) {
        return;
      }

      const list = Array.isArray(rows) ? rows : [];
      setRegions(list);
      const selected = regionIdRef.current;
      if (selected !== null && !list.some((region) => region.id === selected)) {
        applyRegion(null);
      }
    },
    [track, reportFailure, loadCountries, applyRegion],
  );

  const refreshAll = useCallback((): Promise<unknown> => {
    const reloads: Promise<void>[] = [loadCountries()];
    const chosen = countryIdRef.current;
    if (chosen !== null) {
      reloads.push(loadRegions(chosen));
    }
    const selected = regionIdRef.current;
    if (selected !== null) {
      reloads.push(loadItems(selected));
    }
    return Promise.all(reloads);
  }, [loadCountries, loadRegions, loadItems]);

  useEffect(() => {
    void loadCountries();
  }, [loadCountries]);

  useEffect(() => onEntityChanged(WATCHED_ENTITY_TYPES, () => refreshAll()), [refreshAll]);

  const chooseCountry = (value: string): void => {
    const id = parseCountryId(value);
    applyCountry(id);
    if (id !== null) {
      void loadRegions(id);
    }
  };

  const selectRegion = (id: Id): void => {
    if (regionIdRef.current !== id) {
      applyRegion(id);
    }
    void loadItems(id);
  };

  const openEdit = (id: Id | null): void => {
    if (id === null) {
      return;
    }
    dialogKeyRef.current += 1;
    setDialog({ key: dialogKeyRef.current, regionId: id, countryId: null });
  };

  const openAdd = (): void => {
    dialogKeyRef.current += 1;
    setDialog({ key: dialogKeyRef.current, regionId: null, countryId: countryIdRef.current });
  };

  const closeDialog = (): void => {
    setDialog(null);
    const chosen = countryIdRef.current;
    if (chosen !== null) {
      void loadRegions(chosen);
    }
  };

  const remove = async (): Promise<void> => {
    const id = regionIdRef.current;
    if (id === null || removingRef.current) {
      return;
    }
    removingRef.current = true;
    try {
      let result: OperationResult | null = null;
      try {
        result = await track(api.del<OperationResult>(`${REGIONS_PATH}/${id}`));
      } catch (error: unknown) {
        if (!isNotFound(error)) {
          await reportFailure(error);
          return;
        }
      }
      if (!mountedRef.current) {
        return;
      }
      if (result !== null && !result.succeeded) {
        await show(displayErrorsOptions(result));
        return;
      }

      if (regionIdRef.current === id) {
        applyRegion(null);
      }
      const chosen = countryIdRef.current;
      if (chosen !== null) {
        await loadRegions(chosen);
      }
    } finally {
      removingRef.current = false;
    }
  };

  const focusRegion = (id: Id): void => {
    setFocusedRegionId(id);
    regionElements.current.get(id)?.focus();
  };

  const handleRegionKeyDown = (event: KeyboardEvent<HTMLLIElement>, position: number): void => {
    const region = regions[position];
    if (region === undefined) {
      return;
    }

    let target: RegionRow | undefined;
    switch (event.key) {
      case ' ':
        event.preventDefault();
        selectRegion(region.id);
        return;
      case 'Enter':
        event.preventDefault();
        selectRegion(region.id);
        openEdit(region.id);
        return;
      case 'ArrowDown':
        target = regions[position + 1];
        break;
      case 'ArrowUp':
        target = regions[position - 1];
        break;
      case 'Home':
        target = regions[0];
        break;
      case 'End':
        target = regions[regions.length - 1];
        break;
      default:
        return;
    }

    if (event.ctrlKey || event.altKey || event.metaKey) {
      return;
    }
    event.preventDefault();
    if (target !== undefined && target.id !== region.id) {
      focusRegion(target.id);
      selectRegion(target.id);
    }
  };

  const updateRegionFocusVisible = (event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>, id: Id): void => {
    setFocusVisibleRegionId(matchesFocusVisible(event.currentTarget) ? id : null);
  };

  const updateItemsFocusVisible = (event: FocusEvent<HTMLUListElement> | KeyboardEvent<HTMLUListElement>): void => {
    setItemsFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const isListed = (id: Id | null): id is Id => id !== null && regions.some((region) => region.id === id);
  let tabStopRegionId: Id | null = regions[0]?.id ?? null;
  if (isListed(focusedRegionId)) {
    tabStopRegionId = focusedRegionId;
  } else if (isListed(regionId)) {
    tabStopRegionId = regionId;
  }

  const busy = pending > 0;
  const regionSelected = regionId !== null;

  return (
    <section aria-label={msg('Regions')} aria-busy={busy} style={busy ? BUSY_SECTION_STYLE : SECTION_STYLE}>
      <h2 style={HEADING_STYLE}>{msg('Regions')}</h2>
      <div style={BODY_STYLE}>
        <div style={LEFT_COLUMN_STYLE}>
          <label htmlFor={countrySelectId} style={CAPTION_STYLE}>
            {msg('Country')}
          </label>
          <select
            id={countrySelectId}
            value={countryId === null ? '' : String(countryId)}
            onChange={(event) => chooseCountry(event.target.value)}
            style={SELECT_STYLE}
          >
            <option value="" />
            {countries.map((country) => (
              <option key={country.id} value={String(country.id)}>
                {country.name}
              </option>
            ))}
          </select>
          <span style={REGION_CAPTION_STYLE}>{msg('Region')}</span>
          <ul role="listbox" aria-label={msg('Region')} style={REGIONS_LIST_STYLE}>
            {regions.map((region, position) => {
              const selected = region.id === regionId;
              const background = entryBackground(
                selected,
                region.id === pressedRegionId,
                region.id === hoveredRegionId,
              );

              return (
                <li
                  key={region.id}
                  ref={(element) => {
                    if (element === null) {
                      regionElements.current.delete(region.id);
                    } else {
                      regionElements.current.set(region.id, element);
                    }
                  }}
                  role="option"
                  aria-selected={selected}
                  tabIndex={region.id === tabStopRegionId ? 0 : -1}
                  style={regionEntryStyle(background, region.id === focusVisibleRegionId)}
                  onClick={() => selectRegion(region.id)}
                  onDoubleClick={() => openEdit(region.id)}
                  onKeyDown={(event) => handleRegionKeyDown(event, position)}
                  onKeyUp={(event) => updateRegionFocusVisible(event, region.id)}
                  onFocus={(event) => {
                    setFocusedRegionId(region.id);
                    updateRegionFocusVisible(event, region.id);
                  }}
                  onBlur={() => setFocusVisibleRegionId(null)}
                  onMouseEnter={() => setHoveredRegionId(region.id)}
                  onMouseLeave={() => {
                    setHoveredRegionId(null);
                    setPressedRegionId(null);
                  }}
                  onMouseDown={(event: MouseEvent<HTMLLIElement>) => {
                    if (event.button === 0) {
                      setPressedRegionId(region.id);
                    }
                  }}
                  onMouseUp={() => setPressedRegionId(null)}
                >
                  {region.name}
                </li>
              );
            })}
          </ul>
        </div>
        <div style={RIGHT_COLUMN_STYLE}>
          <span style={ITEMS_CAPTION_STYLE}>{REGION_ITEMS_CAPTION}</span>
          <ul
            role="listbox"
            aria-label={REGION_ITEMS_CAPTION}
            aria-readonly="true"
            tabIndex={0}
            style={itemsFocusVisible ? ITEMS_LIST_FOCUS_STYLE : ITEMS_LIST_STYLE}
            onFocus={updateItemsFocusVisible}
            onKeyUp={updateItemsFocusVisible}
            onBlur={() => setItemsFocusVisible(false)}
          >
            {items.map((row) => (
              <li key={row.item.id} role="option" aria-selected={false} style={ENTRY_STYLE}>
                {row.item.name}
              </li>
            ))}
          </ul>
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton onClick={openAdd}>{msg('Add')}</DialogButton>
        <DialogButton aria-disabled={!regionSelected} onClick={() => openEdit(regionIdRef.current)}>
          {msg('Edit')}
        </DialogButton>
        <DialogButton aria-disabled={!regionSelected} onClick={() => void remove()}>
          {msg('Remove')}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {dialog !== null && (
        <EditRegionDialog
          key={dialog.key}
          regionId={dialog.regionId}
          countryId={dialog.countryId}
          onClose={closeDialog}
        />
      )}
      {messageBox}
    </section>
  );
}
