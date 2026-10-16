// Items list of the admin area: the item-type, client, agent, location, price and item-state filters, the items table
// coloured by item state, and the Add Retail Item, Add Tender Item, Edit, Delete and Close buttons.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, MouseEvent, MutableRefObject, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, UnauthorizedError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AdminItemSearchRequest,
  AgentProjection,
  ClientProjection,
  CountryRow,
  EntityTypeName,
  ItemListRow,
  ItemProjection,
  OperationResult,
  RegionRow,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn, RowStyle } from '../../components/DataTable';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { currencyFormat } from '../../format';
import { msg } from '../../messages/messages';
import { isAgent, useSession } from '../../session/SessionProvider';
import type { SessionState } from '../../session/SessionProvider';
import { EditRetailItemDialog, parseItemPrice } from './EditRetailItemDialog';
import { EditTenderItemDialog } from './EditTenderItemDialog';

/** Properties of {@link ItemsListPage}. */
export interface ItemsListPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** Item-type option of the filters: All Items, Retail Items or Tender Items. */
type ItemTypeOption = 'all' | 'retail' | 'tender';

/** Values held by the filter controls. */
interface ItemFilters {
  itemType: ItemTypeOption;
  agentId: number | null;
  clientId: number | null;
  countryId: number | null;
  regionId: number | null;
  /** Text of the Lower Price box. */
  lowerPriceText: string;
  /** Text of the Upper Price box. */
  upperPriceText: string;
  includeClosedTenders: boolean;
  includeSoldItems: boolean;
}

/** Item editor open over the list: a tender or retail item, and its id or null for a new item. */
interface OpenEditor {
  isTender: boolean;
  itemId: number | null;
}

/** One column of the items table. */
interface ItemColumn {
  key: string;
  header: string;
  /** Painted width of the column, in pixels. */
  widthPx: number;
  /** True when the cell text is kept on one line. */
  noWrap: boolean;
  render: (row: ItemListRow) => string;
}

const AGENTS_PATH = '/api/app/admin/agents';
const CLIENTS_PATH = '/api/app/admin/clients';
const COUNTRIES_PATH = '/api/app/admin/countries';
const ITEM_SEARCH_PATH = '/api/app/admin/items/search';
const ITEMS_PATH = '/api/app/admin/items';

/** Item changes re-run the search; agent, client and country changes re-read their filter lists. */
const ITEM_EVENTS: readonly EntityTypeName[] = ['RetailItem', 'TenderItem'];
const AGENT_EVENTS: readonly EntityTypeName[] = ['Agent'];
const CLIENT_EVENTS: readonly EntityTypeName[] = ['Client'];
const COUNTRY_EVENTS: readonly EntityTypeName[] = ['Country'];

const FILTERS_CAPTION = 'Filters';
const ALL_ITEMS_CAPTION = 'All Items';
const RETAIL_ITEMS_CAPTION = 'Retail Items';
const TENDER_ITEMS_CAPTION = 'Tender Items';
const LOWER_PRICE_CAPTION = 'Lower Price';
const UPPER_PRICE_CAPTION = 'Upper Price';
const CLOSED_TENDERS_CAPTION = 'Closed Tenders';
const SOLD_ITEMS_CAPTION = 'Sold Items';
const APPLY_FILTER_CAPTION = 'Apply Filter';
const CLEAR_FILTERS_CAPTION = 'Clear Filters';
const ADD_RETAIL_ITEM_CAPTION = 'Add Retail Item';
const ADD_TENDER_ITEM_CAPTION = 'Add Tender Item';
const EDIT_CAPTION = 'Edit';
const DELETE_CAPTION = 'Delete';
const CLOSE_CAPTION = 'Close';

/** Text of the first, empty entry of each filter selector. */
const NO_SELECTION_TEXT = ' ';

const ITEM_TYPE_OPTIONS: readonly { value: ItemTypeOption; caption: string }[] = [
  { value: 'all', caption: ALL_ITEMS_CAPTION },
  { value: 'retail', caption: RETAIL_ITEMS_CAPTION },
  { value: 'tender', caption: TENDER_ITEMS_CAPTION },
];

/** Filter values of the form when it loads and after Clear Filters, apart from the agent. */
const CLEARED_FILTERS: ItemFilters = {
  itemType: 'all',
  agentId: null,
  clientId: null,
  countryId: null,
  regionId: null,
  lowerPriceText: '',
  upperPriceText: '',
  includeClosedTenders: true,
  includeSoldItems: true,
};

/** Returns the Status cell text: `$Sold` for a sold item, else `$Closed` for a closed tender item, else empty. */
function statusText(row: ItemListRow): string {
  if (row.item.isSold) {
    return msg('Sold');
  }
  return row.isClosed ? msg('Closed') : '';
}

/** Returns the winning offer of a sold tender item or the sale price of a sold retail item, else 0. */
function soldAmount(item: ItemProjection): number {
  if (!item.isSold || item.sale === null) {
    return 0;
  }
  return (item.isTender ? item.sale.offer : item.sale.price) ?? 0;
}

/** Returns the Sold Price cell text: the sold amount as currency when it is above 0, else empty. */
function soldPriceText(item: ItemProjection): string {
  const amount = soldAmount(item);
  return amount > 0 ? currencyFormat(amount) : '';
}

const COLUMNS: readonly ItemColumn[] = [
  { key: 'status', header: msg('Status'), widthPx: 50, noWrap: true, render: statusText },
  {
    key: 'itemType',
    header: msg('ItemType'),
    widthPx: 60,
    noWrap: true,
    render: (row) => (row.item.isTender ? msg('TenderItemType') : msg('RetailItemType')),
  },
  { key: 'code', header: msg('Code'), widthPx: 60, noWrap: true, render: (row) => row.item.code ?? '' },
  { key: 'product', header: msg('Product'), widthPx: 150, noWrap: false, render: (row) => row.item.name },
  {
    key: 'agent',
    header: msg('Agent'),
    widthPx: 150,
    noWrap: false,
    render: (row) => row.item.agent?.name ?? '',
  },
  {
    key: 'itemPrice',
    header: msg('ItemPrice'),
    widthPx: 100,
    noWrap: true,
    render: (row) => currencyFormat(row.item.price),
  },
  { key: 'soldPrice', header: msg('SoldPrice'), widthPx: 100, noWrap: true, render: (row) => soldPriceText(row.item) },
  {
    key: 'soldTo',
    header: msg('SoldToClientBrackets'),
    widthPx: 150,
    noWrap: false,
    render: (row) => (row.item.isSold ? (row.item.sale?.client.name ?? '') : ''),
  },
  { key: 'country', header: msg('Country'), widthPx: 100, noWrap: false, render: (row) => row.item.countryName ?? '' },
  { key: 'region', header: msg('Region'), widthPx: 100, noWrap: false, render: (row) => row.item.regionName ?? '' },
];

const CELL_PADDING_AND_BORDER_PX = 13;
const ROOT_FONT_PX = 16;

const PAGE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.625rem',
  boxSizing: 'border-box',
  minInlineSize: 0,
  paddingBlock: '0.5625rem',
  paddingInline: '0.625rem',
  color: 'black',
};

const HEADING_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
};

const FILTERS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInline: '0.9375rem',
};

const LEGEND_STYLE: CSSProperties = {
  paddingInline: '0.25rem',
  fontWeight: 'bold',
};

const FILTER_COLUMNS_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  columnGap: '1.5rem',
  rowGap: '0.75rem',
  minInlineSize: 0,
};

const FILTER_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  minInlineSize: 0,
};

const OPTIONS_COLUMN_STYLE: CSSProperties = {
  ...FILTER_COLUMN_STYLE,
  gap: '0.375rem',
  paddingBlockStart: '1rem',
};

const PRICE_GRID_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(2, minmax(0, auto))',
  alignItems: 'start',
  columnGap: '1.5rem',
  rowGap: '0.75rem',
  minInlineSize: 0,
};

const FIELD_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.25rem',
  minInlineSize: 0,
};

const SELECT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '10.25rem',
  maxInlineSize: '100%',
  margin: 0,
  font: 'inherit',
};

const PRICE_INPUT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '6.875rem',
  maxInlineSize: '100%',
  margin: 0,
  font: 'inherit',
};

const CHOICE_LABEL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.375rem',
  overflowWrap: 'anywhere',
};

const CHOICE_INPUT_STYLE: CSSProperties = {
  flex: '0 0 auto',
  margin: 0,
};

const FILTER_BUTTONS_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
};

const TABLE_AREA_STYLE: CSSProperties = {
  minInlineSize: 0,
  maxInlineSize: '100%',
  overflowX: 'auto',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'space-between',
  gap: '0.375rem',
};

const BUTTON_GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '0.375rem',
};

const NO_WRAP_STYLE: CSSProperties = {
  whiteSpace: 'nowrap',
};

/**
 * Returns the style of the header of a column painted `widthPx` wide: at least that width less the cell's padding and
 * border, on one line.
 */
function headerStyle(widthPx: number): CSSProperties {
  return {
    display: 'block',
    minInlineSize: `${((widthPx - CELL_PADDING_AND_BORDER_PX) / ROOT_FONT_PX).toFixed(4)}rem`,
    whiteSpace: 'nowrap',
  };
}

const TABLE_COLUMNS: DataTableColumn<ItemListRow>[] = COLUMNS.map((column) => ({
  key: column.key,
  header: <span style={headerStyle(column.widthPx)}>{column.header}</span>,
  render: (row: ItemListRow) =>
    column.noWrap ? <span style={NO_WRAP_STYLE}>{column.render(row)}</span> : column.render(row),
}));

/** Returns the text style of an items row: green for a sold item, otherwise red for a closed tender item. */
function itemRowStyle(row: ItemListRow): RowStyle {
  if (row.item.isSold) {
    return 'sold';
  }
  return row.isClosed ? 'closed' : undefined;
}

/** Returns the internal id of the session's agent, or null when the session is not an agent's. */
function sessionAgentIdOf(session: SessionState | null): number | null {
  return isAgent(session) ? (session?.agentId ?? null) : null;
}

/**
 * Returns whether Delete is enabled for `item` in `session`: false for a sold item, false in an agent's session for
 * an item of another agent, and true otherwise.
 */
export function itemDeleteEnabled(item: ItemProjection, session: SessionState | null): boolean {
  if (item.isSold) {
    return false;
  }
  const agentId = sessionAgentIdOf(session);
  return agentId === null || item.agentId === agentId;
}

/**
 * Returns the item search of `filters`: the session's agent in an agent's session, else the Agent filter; the
 * item-type option as retail-only or tender-only, neither for All Items; each price box read as a decimal number, 0
 * for an empty or non-numeric text; and no category.
 */
function searchRequestOf(filters: ItemFilters, sessionAgentId: number | null): AdminItemSearchRequest {
  return {
    agentId: sessionAgentId ?? filters.agentId,
    clientId: filters.clientId,
    countryId: filters.countryId,
    regionId: filters.regionId,
    itemCategoryId: null,
    onlyRetailItems: filters.itemType === 'retail',
    onlyTenderItems: filters.itemType === 'tender',
    priceLow: parseItemPrice(filters.lowerPriceText),
    priceHigh: parseItemPrice(filters.upperPriceText),
    includeSoldItems: filters.includeSoldItems,
    includeClosedTenders: filters.includeClosedTenders,
  };
}

/** Returns the path of GET /api/app/admin/countries/{id}. */
function countryPath(countryId: number): string {
  return `${COUNTRIES_PATH}/${countryId}`;
}

/** Returns the path of DELETE /api/app/admin/items/{id}. */
function itemPath(itemId: number): string {
  return `${ITEMS_PATH}/${itemId}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns `value` when it is an array, otherwise an empty list. */
function asList<T>(value: T[] | undefined): T[] {
  return Array.isArray(value) ? value : [];
}

/** Returns `selected` while `list` holds a row with that id, otherwise null. */
function keepIfListed(list: readonly { id: number }[], selected: number | null): number | null {
  return selected !== null && list.some((row) => row.id === selected) ? selected : null;
}

/** Returns the id a selector value names: null for the empty entry. */
function selectedIdOf(value: string): number | null {
  return value === '' ? null : Number(value);
}

/** Returns the selector value of `id`: the empty entry for null. */
function selectValueOf(id: number | null): string {
  return id === null ? '' : String(id);
}


/**
 * The "View Items" tab of the admin area: the company's items matching the filters, one row per item in the order the
 * server returns.
 *
 * - On mount the Client, Agent and Country selectors are filled, each after an empty entry and in the order received,
 *   and are read again on every EntityChanged event for a client, an agent or a country; a selected entry the new
 *   list no longer holds is deselected. In an agent's session the Agent selector shows the session's agent and is
 *   disabled. Choosing a country reads its regions into the Region selector and enables it; choosing the empty entry
 *   empties and disables Region.
 * - The items are searched once on mount, on Apply Filter, and on every EntityChanged event for a retail or tender
 *   item, which first deselects the selected row. Each search sends the filter values held at that moment, with the
 *   session's agent in an agent's session, and only the answer to the latest search is shown. An answer replaces the
 *   rows and deselects; an empty answer, except on mount, shows `$NoItemsMatchingCriteria` titled `$NoItemsFound`. A
 *   failed search keeps the rows and the selection and shows its error outcome, such as `$MinPriceGreaterThanMaxError`
 *   titled `$Error` for a lower price above the upper price.
 * - Clear Filters selects All Items, empties Client, Country, Region and both price boxes, disables Region, checks
 *   Closed Tenders and Sold Items, and empties Agent only outside an agent's session; it does not search.
 * - Each row shows the status, item type, code, name, agent, item price, sold price, buying client, country and
 *   region. A sold item's row shows `$Sold` in green; an unsold closed tender item's row shows `$Closed` in red.
 * - Edit and Delete are disabled until a row is clicked; clicking the header row deselects. With a row selected,
 *   Edit is enabled and Delete is enabled as {@link itemDeleteEnabled} decides. Edit, or double-clicking a row, opens
 *   the retail or tender item editor for that item; Add Retail Item and Add Tender Item open it for a new item.
 * - Delete sends DELETE /api/app/admin/items/{id} at once, with no confirmation: a failed result shows its errors
 *   titled `$ErrorDuringSave`, a succeeded result deselects, and a 404 deselects and searches again.
 * - An error outcome is shown in its message box, an UnauthorizedError shows nothing, and any other failure shows its
 *   message titled `$Error`. Close calls `onClose`.
 */
export function ItemsListPage({ onClose }: ItemsListPageProps = {}): ReactElement {
  const { session } = useSession();
  const sessionAgentId = sessionAgentIdOf(session);
  const agentSession = sessionAgentId !== null;
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();
  const itemTypeName = useId();
  const clientSelectId = useId();
  const agentSelectId = useId();
  const countrySelectId = useId();
  const regionSelectId = useId();
  const lowerPriceId = useId();
  const upperPriceId = useId();

  const mountedRef = useRef(false);
  const filtersRef = useRef<ItemFilters>({ ...CLEARED_FILTERS, agentId: sessionAgentId });
  const agentsRequestRef = useRef(0);
  const clientsRequestRef = useRef(0);
  const countriesRequestRef = useRef(0);
  const regionsRequestRef = useRef(0);
  const searchRequestRef = useRef(0);
  const deletingRef = useRef(false);

  const [filters, setFilters] = useState<ItemFilters>(filtersRef.current);
  const [agents, setAgents] = useState<readonly AgentProjection[]>([]);
  const [clients, setClients] = useState<readonly ClientProjection[]>([]);
  const [countries, setCountries] = useState<readonly CountryRow[]>([]);
  const [regions, setRegions] = useState<readonly RegionRow[]>([]);
  const [regionEnabled, setRegionEnabled] = useState(false);
  const [rows, setRows] = useState<ItemListRow[]>([]);
  const [searching, setSearching] = useState(false);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [editor, setEditor] = useState<OpenEditor | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Replaces the filter values with `change` applied to the current ones. */
  const updateFilters = useCallback((change: (current: ItemFilters) => ItemFilters): void => {
    const next = change(filtersRef.current);
    filtersRef.current = next;
    setFilters(next);
  }, []);

  /** Shows `error` in a message box while the page is mounted. */
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

  /**
   * Reads the list at `path` as the latest request counted by `requestRef`; resolves to the list while that request
   * is still the latest and the page is mounted, and to null otherwise or after showing a failure.
   */
  const readList = useCallback(
    async <T,>(path: string, requestRef: MutableRefObject<number>): Promise<T[] | null> => {
      requestRef.current += 1;
      const request = requestRef.current;
      const isCurrent = (): boolean => mountedRef.current && request === requestRef.current;

      try {
        const list = asList(await api.get<T[] | undefined>(path));
        return isCurrent() ? list : null;
      } catch (error: unknown) {
        if (isCurrent()) {
          await reportFailure(error);
        }
        return null;
      }
    },
    [reportFailure],
  );

  /** Empties and disables the Region selector, discarding a regions request still outstanding. */
  const clearRegions = useCallback((): void => {
    regionsRequestRef.current += 1;
    setRegions([]);
    setRegionEnabled(false);
  }, []);

  /** Reads the agents into the Agent selector, in the order received. */
  const loadAgents = useCallback(async (): Promise<void> => {
    const list = await readList<AgentProjection>(AGENTS_PATH, agentsRequestRef);
    if (list !== null) {
      setAgents(list);
      updateFilters((current) => ({ ...current, agentId: keepIfListed(list, current.agentId) }));
    }
  }, [readList, updateFilters]);

  /** Reads the clients into the Client selector, in the order received. */
  const loadClients = useCallback(async (): Promise<void> => {
    const list = await readList<ClientProjection>(CLIENTS_PATH, clientsRequestRef);
    if (list !== null) {
      setClients(list);
      updateFilters((current) => ({ ...current, clientId: keepIfListed(list, current.clientId) }));
    }
  }, [readList, updateFilters]);

  /** Reads the countries into the Country selector, in the order received. */
  const loadCountries = useCallback(async (): Promise<void> => {
    const list = await readList<CountryRow>(COUNTRIES_PATH, countriesRequestRef);
    if (list === null) {
      return;
    }
    setCountries(list);
    const selected = filtersRef.current.countryId;
    if (selected !== null && keepIfListed(list, selected) === null) {
      updateFilters((current) => ({ ...current, countryId: null, regionId: null }));
      clearRegions();
    }
  }, [clearRegions, readList, updateFilters]);

  /** Port of ItemsListForm.cmbCountry_click for a chosen country: reads its regions and enables Region. */
  const loadRegions = useCallback(
    async (countryId: number): Promise<void> => {
      regionsRequestRef.current += 1;
      const request = regionsRequestRef.current;
      const isCurrent = (): boolean =>
        mountedRef.current && request === regionsRequestRef.current && filtersRef.current.countryId === countryId;

      let country: CountryRow | undefined;
      try {
        country = await api.get<CountryRow | undefined>(countryPath(countryId));
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        if (isNotFound(error)) {
          updateFilters((current) => ({ ...current, countryId: null, regionId: null }));
          clearRegions();
          await loadCountries();
          return;
        }
        await reportFailure(error);
        return;
      }

      if (isCurrent()) {
        setRegions(asList(country?.regions));
        setRegionEnabled(true);
      }
    },
    [clearRegions, loadCountries, reportFailure, updateFilters],
  );

  /**
   * Port of ItemsListForm.doApplyFilter: searches with the filter values the form holds and shows the items, telling
   * the user when none matches if `reportEmpty` is true.
   */
  const applyFilter = useCallback(
    async (reportEmpty: boolean): Promise<void> => {
      searchRequestRef.current += 1;
      const request = searchRequestRef.current;
      const isCurrent = (): boolean => mountedRef.current && request === searchRequestRef.current;
      const criteria = searchRequestOf(filtersRef.current, sessionAgentId);

      setSearching(true);
      let found: ItemListRow[];
      try {
        found = asList(await api.post<ItemListRow[] | undefined>(ITEM_SEARCH_PATH, criteria));
      } catch (error: unknown) {
        if (isCurrent()) {
          setSearching(false);
          await reportFailure(error);
        }
        return;
      }

      if (!isCurrent()) {
        return;
      }
      setSearching(false);
      setRows(found);
      setSelectedId(null);
      if (reportEmpty && found.length === 0) {
        await show({
          title: msg('NoItemsFound'),
          message: msg('NoItemsMatchingCriteria'),
          icon: 'information',
          buttons: 'ok',
        });
      }
    },
    [reportFailure, sessionAgentId, show],
  );

  useEffect(() => {
    void loadClients();
    void loadAgents();
    void loadCountries();
    void applyFilter(false);
  }, [loadAgents, loadClients, loadCountries, applyFilter]);

  useEffect(() => {
    const stops = [
      onEntityChanged(ITEM_EVENTS, () => {
        setSelectedId(null);
        return applyFilter(true);
      }),
      onEntityChanged(AGENT_EVENTS, () => loadAgents()),
      onEntityChanged(CLIENT_EVENTS, () => loadClients()),
      onEntityChanged(COUNTRY_EVENTS, () => loadCountries()),
    ];
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, [applyFilter, loadAgents, loadClients, loadCountries]);

  const selectedRow = rows.find((row) => row.item.id === selectedId) ?? null;
  const editEnabled = selectedRow !== null && !deleting;
  const deleteEnabled = selectedRow !== null && !deleting && itemDeleteEnabled(selectedRow.item, session);

  /** Port of ItemsListForm.cmbCountry_click. */
  const changeCountry = (countryId: number | null): void => {
    updateFilters((current) => ({ ...current, countryId, regionId: null }));
    clearRegions();
    if (countryId !== null) {
      void loadRegions(countryId);
    }
  };

  /** Port of ItemsListForm.doClearFilters. */
  const clearFilters = (): void => {
    updateFilters(() => ({ ...CLEARED_FILTERS, agentId: sessionAgentId }));
    clearRegions();
  };

  /** Port of the header-row branch of ItemsListForm.tblItems_rowColumnChg. */
  const handleTableAreaClick = (event: MouseEvent<HTMLDivElement>): void => {
    if (event.target instanceof Element && event.target.closest('thead') !== null) {
      setSelectedId(null);
    }
  };

  /** Port of ItemsListForm.doEdit. */
  const editItem = (row: ItemListRow | null): void => {
    if (row === null || deletingRef.current) {
      return;
    }
    setEditor({ isTender: row.item.isTender, itemId: row.item.id });
  };

  /** Port of ItemsListForm.tblItems_dblClick. */
  const editListedItem = (row: ItemListRow): void => {
    if (deletingRef.current) {
      return;
    }
    setSelectedId(row.item.id);
    editItem(row);
  };

  /** Port of ItemsListForm.btnDelete_click followed by the inherited doDelete. */
  const deleteItem = async (): Promise<void> => {
    const row = selectedRow;
    if (row === null || deletingRef.current) {
      return;
    }

    const itemId = row.item.id;
    deletingRef.current = true;
    setDeleting(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(itemPath(itemId));
    } catch (error: unknown) {
      deletingRef.current = false;
      if (!mountedRef.current) {
        return;
      }
      setDeleting(false);
      if (isNotFound(error)) {
        setSelectedId(null);
        await applyFilter(true);
        return;
      }
      await reportFailure(error);
      return;
    }

    deletingRef.current = false;
    if (!mountedRef.current) {
      return;
    }
    setDeleting(false);

    if (result === undefined) {
      return;
    }
    if (!result.succeeded) {
      await show(displayErrorsOptions(result));
      return;
    }
    setSelectedId((current) => (current === itemId ? null : current));
  };


  return (
    <section aria-labelledby={headingId} style={PAGE_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {msg('ViewItems')}
      </h2>
      <fieldset style={FILTERS_STYLE}>
        <legend style={LEGEND_STYLE}>{FILTERS_CAPTION}</legend>
        <div style={FILTER_COLUMNS_STYLE}>
          <div style={OPTIONS_COLUMN_STYLE}>
            {ITEM_TYPE_OPTIONS.map((option) => (
              <label key={option.value} style={CHOICE_LABEL_STYLE}>
                <input
                  type="radio"
                  name={itemTypeName}
                  value={option.value}
                  checked={filters.itemType === option.value}
                  style={CHOICE_INPUT_STYLE}
                  onChange={() => updateFilters((current) => ({ ...current, itemType: option.value }))}
                />
                {option.caption}
              </label>
            ))}
          </div>
          <div style={FILTER_COLUMN_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={clientSelectId}>{msg('Client')}</label>
              <select
                id={clientSelectId}
                autoComplete="off"
                value={selectValueOf(filters.clientId)}
                style={SELECT_STYLE}
                onChange={(event) => {
                  const clientId = selectedIdOf(event.target.value);
                  updateFilters((current) => ({ ...current, clientId }));
                }}
              >
                <option value="">{NO_SELECTION_TEXT}</option>
                {clients.map((client) => (
                  <option key={client.id} value={String(client.id)}>
                    {client.name}
                  </option>
                ))}
              </select>
            </div>
            <div style={FIELD_STYLE}>
              <label htmlFor={agentSelectId}>{msg('Agent')}</label>
              <select
                id={agentSelectId}
                autoComplete="off"
                value={selectValueOf(sessionAgentId ?? filters.agentId)}
                disabled={agentSession}
                style={SELECT_STYLE}
                onChange={(event) => {
                  const agentId = selectedIdOf(event.target.value);
                  updateFilters((current) => ({ ...current, agentId }));
                }}
              >
                <option value="">{NO_SELECTION_TEXT}</option>
                {agents.map((agent) => (
                  <option key={agent.id} value={String(agent.id)}>
                    {agent.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div style={FILTER_COLUMN_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={countrySelectId}>{msg('Country')}</label>
              <select
                id={countrySelectId}
                autoComplete="off"
                value={selectValueOf(filters.countryId)}
                style={SELECT_STYLE}
                onChange={(event) => changeCountry(selectedIdOf(event.target.value))}
              >
                <option value="">{NO_SELECTION_TEXT}</option>
                {countries.map((country) => (
                  <option key={country.id} value={String(country.id)}>
                    {country.name}
                  </option>
                ))}
              </select>
            </div>
            <div style={FIELD_STYLE}>
              <label htmlFor={regionSelectId}>{msg('Region')}</label>
              <select
                id={regionSelectId}
                autoComplete="off"
                value={selectValueOf(filters.regionId)}
                disabled={!regionEnabled}
                style={SELECT_STYLE}
                onChange={(event) => {
                  const regionId = selectedIdOf(event.target.value);
                  updateFilters((current) => ({ ...current, regionId }));
                }}
              >
                <option value="">{NO_SELECTION_TEXT}</option>
                {regions.map((region) => (
                  <option key={region.id} value={String(region.id)}>
                    {region.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div style={PRICE_GRID_STYLE}>
            <div style={FIELD_STYLE}>
              <label htmlFor={lowerPriceId}>{LOWER_PRICE_CAPTION}</label>
              <input
                id={lowerPriceId}
                type="text"
                inputMode="decimal"
                autoComplete="off"
                value={filters.lowerPriceText}
                style={PRICE_INPUT_STYLE}
                onChange={(event) => {
                  const lowerPriceText = event.target.value;
                  updateFilters((current) => ({ ...current, lowerPriceText }));
                }}
              />
            </div>
            <div style={FIELD_STYLE}>
              <label htmlFor={upperPriceId}>{UPPER_PRICE_CAPTION}</label>
              <input
                id={upperPriceId}
                type="text"
                inputMode="decimal"
                autoComplete="off"
                value={filters.upperPriceText}
                style={PRICE_INPUT_STYLE}
                onChange={(event) => {
                  const upperPriceText = event.target.value;
                  updateFilters((current) => ({ ...current, upperPriceText }));
                }}
              />
            </div>
            <label style={CHOICE_LABEL_STYLE}>
              <input
                type="checkbox"
                checked={filters.includeClosedTenders}
                style={CHOICE_INPUT_STYLE}
                onChange={(event) => {
                  const includeClosedTenders = event.target.checked;
                  updateFilters((current) => ({ ...current, includeClosedTenders }));
                }}
              />
              {CLOSED_TENDERS_CAPTION}
            </label>
            <label style={CHOICE_LABEL_STYLE}>
              <input
                type="checkbox"
                checked={filters.includeSoldItems}
                style={CHOICE_INPUT_STYLE}
                onChange={(event) => {
                  const includeSoldItems = event.target.checked;
                  updateFilters((current) => ({ ...current, includeSoldItems }));
                }}
              />
              {SOLD_ITEMS_CAPTION}
            </label>
          </div>
        </div>
        <div style={FILTER_BUTTONS_STYLE}>
          <DialogButton onClick={() => void applyFilter(true)}>{APPLY_FILTER_CAPTION}</DialogButton>
          <DialogButton onClick={clearFilters}>{CLEAR_FILTERS_CAPTION}</DialogButton>
        </div>
      </fieldset>
      <div aria-busy={searching} style={TABLE_AREA_STYLE} onClick={handleTableAreaClick}>
        <DataTable
          columns={TABLE_COLUMNS}
          rows={rows}
          rowKey={(row) => row.item.id}
          selectedKey={selectedId}
          onSelect={(row) => setSelectedId(row.item.id)}
          onDoubleClick={editListedItem}
          rowStyle={itemRowStyle}
        />
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <div style={BUTTON_GROUP_STYLE}>
          <DialogButton onClick={() => setEditor({ isTender: false, itemId: null })}>
            {ADD_RETAIL_ITEM_CAPTION}
          </DialogButton>
          <DialogButton onClick={() => setEditor({ isTender: true, itemId: null })}>
            {ADD_TENDER_ITEM_CAPTION}
          </DialogButton>
        </div>
        <div style={BUTTON_GROUP_STYLE}>
          <DialogButton aria-disabled={!editEnabled} onClick={() => editItem(selectedRow)}>
            {EDIT_CAPTION}
          </DialogButton>
          <DialogButton aria-disabled={!deleteEnabled} onClick={() => void deleteItem()}>
            {DELETE_CAPTION}
          </DialogButton>
          <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
        </div>
      </div>
      {messageBox}
      {editor !== null &&
        (editor.isTender ? (
          <EditTenderItemDialog itemId={editor.itemId} onClose={() => setEditor(null)} />
        ) : (
          <EditRetailItemDialog itemId={editor.itemId} onClose={() => setEditor(null)} />
        ))}
    </section>
  );
}

