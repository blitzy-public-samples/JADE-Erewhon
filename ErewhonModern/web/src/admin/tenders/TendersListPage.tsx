// Tenders list of the admin area: the agent, client and tender-item filters with the three status check boxes, the
// tenders table coloured by status, and the View, Delete and Close buttons.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, MouseEvent, ReactElement } from 'react';
import { api, ApiError } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AdminItemSearchRequest,
  AdminTenderSearchRequest,
  AgentProjection,
  ClientProjection,
  EntityTypeName,
  ItemListRow,
  ItemProjection,
  OperationResult,
  TenderListRow,
  TenderStatus,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn, RowStyle } from '../../components/DataTable';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { currencyFormat, dateString } from '../../format';
import { msg } from '../../messages/messages';
import { isAgent, useSession } from '../../session/SessionProvider';
import { ViewTenderDialog } from './ViewTenderDialog';

/** Properties of {@link TendersListPage}. */
export interface TendersListPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** One column of the tenders table. */
interface TenderColumn {
  key: string;
  header: string;
  /** Painted width of the column, in pixels. */
  widthPx: number;
  /** True when the cell text is kept on one line. */
  noWrap: boolean;
  render: (row: TenderListRow) => string;
}

const AGENTS_PATH = '/api/app/admin/agents';
const CLIENTS_PATH = '/api/app/admin/clients';
const ITEM_SEARCH_PATH = '/api/app/admin/items/search';
const TENDER_SEARCH_PATH = '/api/app/admin/tenders/search';
const TENDERS_PATH = '/api/app/admin/tenders';

const TENDER_EVENTS: readonly EntityTypeName[] = ['Tender'];
const AGENT_EVENTS: readonly EntityTypeName[] = ['Agent'];
const CLIENT_EVENTS: readonly EntityTypeName[] = ['Client'];
const TENDER_ITEM_EVENTS: readonly EntityTypeName[] = ['TenderItem'];

const FILTERS_CAPTION = 'Filters';
const ITEM_CAPTION = 'Item';
const OPEN_TENDERS_CAPTION = 'Open Tenders';
const CLOSED_TENDERS_CAPTION = 'Closed Tenders';
const SUCCESSFUL_TENDERS_CAPTION = 'Successful Tenders';
const APPLY_FILTER_CAPTION = 'Apply Filter';
const RESET_FILTERS_CAPTION = 'Reset Filters';
const VIEW_CAPTION = 'View';
const DELETE_CAPTION = 'Delete';
const CLOSE_CAPTION = 'Close';

/** Text of the first, empty entry of each filter selector. */
const NO_SELECTION_TEXT = ' ';

/** Filter values of the form when it loads and after Reset Filters. */
const INITIAL_FILTERS: AdminTenderSearchRequest = {
  agentId: null,
  clientId: null,
  itemId: null,
  includeOpenTenders: true,
  includeClosedTenders: false,
  includeSuccessfulTenders: false,
};

/** Item search that lists every tender item of the company, sold and closed ones included. */
const TENDER_ITEMS_REQUEST: AdminItemSearchRequest = {
  agentId: null,
  clientId: null,
  countryId: null,
  regionId: null,
  itemCategoryId: null,
  onlyRetailItems: false,
  onlyTenderItems: true,
  priceLow: 0,
  priceHigh: 0,
  includeSoldItems: true,
  includeClosedTenders: true,
};

const STATUS_TEXT: Record<TenderStatus, string> = {
  open: 'Open',
  closed: 'Closed',
  successful: 'Successful',
};

const COLUMNS: readonly TenderColumn[] = [
  { key: 'status', header: 'Status', widthPx: 80, noWrap: true, render: (row) => STATUS_TEXT[row.status] },
  {
    key: 'tenderDate',
    header: 'Tender Date',
    widthPx: 120,
    noWrap: true,
    render: (row) => dateString(row.tenderDate),
  },
  {
    key: 'closureDate',
    header: 'Closure Date',
    widthPx: 120,
    noWrap: true,
    render: (row) => dateString(row.closureDate),
  },
  { key: 'client', header: 'Client', widthPx: 150, noWrap: false, render: (row) => row.clientName },
  {
    key: 'item',
    header: 'Item Tendered',
    widthPx: 200,
    noWrap: false,
    render: (row) => `${row.itemCode ?? ''} : ${row.itemName}`,
  },
  {
    key: 'reserve',
    header: 'Reserve',
    widthPx: 100,
    noWrap: true,
    render: (row) => currencyFormat(row.itemPrice),
  },
  {
    key: 'offer',
    header: 'Tender Amount',
    widthPx: 100,
    noWrap: true,
    render: (row) => currencyFormat(row.offer),
  },
  { key: 'agent', header: 'Agent', widthPx: 150, noWrap: false, render: (row) => row.agentName ?? '' },
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

const BODY_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  gap: '0.625rem',
  minInlineSize: 0,
};

const FILTERS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  flex: '0 0 auto',
  boxSizing: 'border-box',
  maxInlineSize: '100%',
  minInlineSize: '14.375rem',
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInline: '0.9375rem',
};

const LEGEND_STYLE: CSSProperties = {
  paddingInline: '0.25rem',
  fontWeight: 'bold',
};

const FIELD_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.375rem',
  minInlineSize: 0,
};

const SELECT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '11.875rem',
  maxInlineSize: '100%',
  margin: 0,
  font: 'inherit',
};

const CHECK_BOXES_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.3125rem',
  paddingBlockStart: '0.5rem',
};

const CHECK_BOX_LABEL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.375rem',
  overflowWrap: 'anywhere',
};

const CHECK_BOX_STYLE: CSSProperties = {
  flex: '0 0 auto',
  margin: 0,
};

const FILTER_BUTTONS_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '0.625rem',
  paddingBlockStart: '0.5rem',
};

const TABLE_AREA_STYLE: CSSProperties = {
  flex: '1 1 30rem',
  minInlineSize: 0,
  maxInlineSize: '100%',
  overflowX: 'auto',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
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

/** Returns the path of DELETE /api/app/admin/tenders/{id}. */
function tenderPath(tenderId: number): string {
  return `${TENDERS_PATH}/${tenderId}`;
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

/** Returns the text style of a tenders row: green for the winning tender, otherwise red for a closed tender. */
function tenderRowStyle(row: TenderListRow): RowStyle {
  if (row.isWinning) {
    return 'tenderSale';
  }
  return row.status === 'closed' ? 'closed' : undefined;
}

/** Returns the selector entry text of a tender item: its code and its name separated by a space. */
function tenderItemText(item: ItemProjection): string {
  return `${item.code ?? ''} ${item.name}`;
}

/**
 * The "View Tenders" tab of the admin area: the tenders matching the agent, client and tender-item filters and the
 * Open, Closed and Successful status check boxes, one row per tender in the order the server returns.
 *
 * - On mount the agent, client and tender-item selectors are filled, each after an empty entry: agents and clients
 *   in the order received, tender items in the item search order, each shown as "<code> <name>". Every EntityChanged
 *   event for an agent, a client or a tender item reads that selector's list again; a selected entry the new list no
 *   longer holds is deselected.
 * - The tenders are searched on mount with the initial filters (Open Tenders only), on Apply Filter, after a delete,
 *   and on every EntityChanged event for a tender; each search sends the filter values the form holds at that moment,
 *   and only the answer to the latest search is shown. A selected tender the answer no longer holds is deselected.
 *   Reset Filters restores the initial filter values without searching.
 * - Each row shows the status, the tender date, the item's closure date, the client, "<code> : <name>", the reserve,
 *   the offer and the item's agent. The winning tender's row is green and a closed tender's row red.
 * - View and Delete are disabled until a row is clicked; clicking a row enables View, and enables Delete unless the
 *   session is an agent's; clicking the header row deselects and disables both. Double-clicking a row, or View with a
 *   row selected, opens {@link ViewTenderDialog}; View with no row selected shows `$PleaseSelectAnItem`.
 * - Delete does nothing in an agent's session. Otherwise, with no row selected it shows `$SelectItemToDelete`, for
 *   the winning tender `$CannotDeleteTenderItemSold`, and else it sends DELETE /api/app/admin/tenders/{id} at once: a
 *   succeeded result or a 404 deselects and searches again, and a failed result shows its errors.
 * - An error outcome is shown in a message box, an UnauthorizedError shows nothing, and any other failure rejects
 *   unhandled. Close calls `onClose`.
 */
export function TendersListPage({ onClose }: TendersListPageProps = {}): ReactElement {
  const { session } = useSession();
  const agentSession = isAgent(session);
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();
  const agentSelectId = useId();
  const clientSelectId = useId();
  const itemSelectId = useId();

  const mountedRef = useRef(false);
  const filtersRef = useRef<AdminTenderSearchRequest>(INITIAL_FILTERS);
  const agentsRequestRef = useRef(0);
  const clientsRequestRef = useRef(0);
  const tenderItemsRequestRef = useRef(0);
  const searchRequestRef = useRef(0);
  const deletingRef = useRef(false);

  const [filters, setFilters] = useState<AdminTenderSearchRequest>(INITIAL_FILTERS);
  const [agents, setAgents] = useState<readonly AgentProjection[]>([]);
  const [clients, setClients] = useState<readonly ClientProjection[]>([]);
  const [tenderItems, setTenderItems] = useState<readonly ItemProjection[]>([]);
  const [rows, setRows] = useState<TenderListRow[]>([]);
  const [searching, setSearching] = useState(false);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [viewEnabled, setViewEnabled] = useState(false);
  const [deleteEnabled, setDeleteEnabled] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [viewId, setViewId] = useState<number | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Replaces the filter values with `change` applied to the current ones. */
  const updateFilters = useCallback(
    (change: (current: AdminTenderSearchRequest) => AdminTenderSearchRequest): void => {
      const next = change(filtersRef.current);
      filtersRef.current = next;
      setFilters(next);
    },
    [],
  );

  /** Reads the agents into the Agent selector, in the order received. */
  const loadAgents = useCallback(async (): Promise<void> => {
    agentsRequestRef.current += 1;
    const request = agentsRequestRef.current;
    const isCurrent = () => mountedRef.current && request === agentsRequestRef.current;

    let list: AgentProjection[];
    try {
      list = asList(await api.get<AgentProjection[] | undefined>(AGENTS_PATH));
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setAgents(list);
      updateFilters((current) => ({ ...current, agentId: keepIfListed(list, current.agentId) }));
    }
  }, [showError, updateFilters]);

  /** Reads the clients into the Client selector, in the order received. */
  const loadClients = useCallback(async (): Promise<void> => {
    clientsRequestRef.current += 1;
    const request = clientsRequestRef.current;
    const isCurrent = () => mountedRef.current && request === clientsRequestRef.current;

    let list: ClientProjection[];
    try {
      list = asList(await api.get<ClientProjection[] | undefined>(CLIENTS_PATH));
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setClients(list);
      updateFilters((current) => ({ ...current, clientId: keepIfListed(list, current.clientId) }));
    }
  }, [showError, updateFilters]);

  /** Reads the company's tender items into the Item selector, in the item search order. */
  const loadTenderItems = useCallback(async (): Promise<void> => {
    tenderItemsRequestRef.current += 1;
    const request = tenderItemsRequestRef.current;
    const isCurrent = () => mountedRef.current && request === tenderItemsRequestRef.current;

    let list: ItemProjection[];
    try {
      const found = asList(await api.post<ItemListRow[] | undefined>(ITEM_SEARCH_PATH, TENDER_ITEMS_REQUEST));
      list = found.map((row) => row.item);
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setTenderItems(list);
      updateFilters((current) => ({ ...current, itemId: keepIfListed(list, current.itemId) }));
    }
  }, [showError, updateFilters]);

  /** Port of TendersListForm.doApplyFilter: searches with the filter values the form holds and shows the tenders. */
  const applyFilter = useCallback(async (): Promise<void> => {
    searchRequestRef.current += 1;
    const request = searchRequestRef.current;
    const isCurrent = () => mountedRef.current && request === searchRequestRef.current;
    const criteria: AdminTenderSearchRequest = { ...filtersRef.current };

    setSearching(true);
    let found: TenderListRow[];
    try {
      found = asList(await api.post<TenderListRow[] | undefined>(TENDER_SEARCH_PATH, criteria));
    } catch (error: unknown) {
      if (isCurrent()) {
        setSearching(false);
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setSearching(false);
      setRows(found);
      setSelectedId((current) => keepIfListed(found, current));
    }
  }, [showError]);

  useEffect(() => {
    void loadAgents();
    void loadClients();
    void loadTenderItems();
    void applyFilter();
  }, [loadAgents, loadClients, loadTenderItems, applyFilter]);

  useEffect(() => {
    const stops = [
      onEntityChanged(TENDER_EVENTS, () => applyFilter()),
      onEntityChanged(AGENT_EVENTS, () => loadAgents()),
      onEntityChanged(CLIENT_EVENTS, () => loadClients()),
      onEntityChanged(TENDER_ITEM_EVENTS, () => loadTenderItems()),
    ];
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, [applyFilter, loadAgents, loadClients, loadTenderItems]);

  const selectedRow = rows.find((row) => row.id === selectedId) ?? null;

  /** Port of TendersListForm.doResetFilters: restores the initial filter values. */
  const resetFilters = (): void => {
    updateFilters(() => INITIAL_FILTERS);
  };

  /** Port of the body-row branch of TendersListForm.tblTenders_click. */
  const selectRow = (row: TenderListRow): void => {
    setSelectedId(row.id);
    setViewEnabled(true);
    setDeleteEnabled(!agentSession);
  };

  /** Port of the header-row branch of TendersListForm.tblTenders_click. */
  const handleTableAreaClick = (event: MouseEvent<HTMLDivElement>): void => {
    if (event.target instanceof Element && event.target.closest('thead') !== null) {
      setSelectedId(null);
      setViewEnabled(false);
      setDeleteEnabled(false);
    }
  };

  /** Port of TendersListForm.doViewTender. */
  const viewTender = (): void => {
    if (selectedRow === null) {
      void show({ title: msg('Error'), message: msg('PleaseSelectAnItem'), icon: 'exclamation', buttons: 'ok' });
      return;
    }
    setViewId(selectedRow.id);
  };

  /** Port of TendersListForm.doDeleteTender followed by the inherited doDelete. */
  const deleteTender = async (): Promise<void> => {
    if (agentSession || deletingRef.current) {
      return;
    }

    const row = selectedRow;
    if (row === null) {
      void show({ title: msg('Error'), message: msg('SelectItemToDelete'), icon: 'exclamation', buttons: 'ok' });
      return;
    }

    if (row.isWinning) {
      void show({
        title: msg('Error'),
        message: msg('CannotDeleteTenderItemSold'),
        icon: 'exclamation',
        buttons: 'ok',
      });
      return;
    }

    deletingRef.current = true;
    setDeleting(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(tenderPath(row.id));
    } catch (error: unknown) {
      deletingRef.current = false;
      if (!mountedRef.current) {
        return;
      }
      setDeleting(false);
      if (isNotFound(error)) {
        setSelectedId(null);
        await applyFilter();
        return;
      }
      await showError(error);
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
    if (result.succeeded) {
      setSelectedId(null);
      await applyFilter();
      return;
    }
    void show(displayErrorsOptions(result));
  };

  /** Closes the view dialog, searching again when it deleted the tender. */
  const closeViewDialog = (deleted: boolean): void => {
    setViewId(null);
    if (deleted) {
      void applyFilter();
    }
  };

  const columns: DataTableColumn<TenderListRow>[] = COLUMNS.map((column) => ({
    key: column.key,
    header: <span style={headerStyle(column.widthPx)}>{column.header}</span>,
    render: (row: TenderListRow) =>
      column.noWrap ? <span style={NO_WRAP_STYLE}>{column.render(row)}</span> : column.render(row),
  }));

  return (
    <section aria-labelledby={headingId} style={PAGE_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {msg('TendersListFormCaption')}
      </h2>
      <div style={BODY_STYLE}>
        <fieldset style={FILTERS_STYLE}>
          <legend style={LEGEND_STYLE}>{FILTERS_CAPTION}</legend>
          <div style={FIELD_STYLE}>
            <label htmlFor={agentSelectId}>{msg('Agent')}</label>
            <select
              id={agentSelectId}
              autoComplete="off"
              value={selectValueOf(filters.agentId)}
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
            <label htmlFor={itemSelectId}>{ITEM_CAPTION}</label>
            <select
              id={itemSelectId}
              autoComplete="off"
              value={selectValueOf(filters.itemId)}
              style={SELECT_STYLE}
              onChange={(event) => {
                const itemId = selectedIdOf(event.target.value);
                updateFilters((current) => ({ ...current, itemId }));
              }}
            >
              <option value="">{NO_SELECTION_TEXT}</option>
              {tenderItems.map((item) => (
                <option key={item.id} value={String(item.id)}>
                  {tenderItemText(item)}
                </option>
              ))}
            </select>
          </div>
          <div style={CHECK_BOXES_STYLE}>
            <label style={CHECK_BOX_LABEL_STYLE}>
              <input
                type="checkbox"
                checked={filters.includeOpenTenders}
                style={CHECK_BOX_STYLE}
                onChange={(event) => {
                  const includeOpenTenders = event.target.checked;
                  updateFilters((current) => ({ ...current, includeOpenTenders }));
                }}
              />
              {OPEN_TENDERS_CAPTION}
            </label>
            <label style={CHECK_BOX_LABEL_STYLE}>
              <input
                type="checkbox"
                checked={filters.includeClosedTenders}
                style={CHECK_BOX_STYLE}
                onChange={(event) => {
                  const includeClosedTenders = event.target.checked;
                  updateFilters((current) => ({ ...current, includeClosedTenders }));
                }}
              />
              {CLOSED_TENDERS_CAPTION}
            </label>
            <label style={CHECK_BOX_LABEL_STYLE}>
              <input
                type="checkbox"
                checked={filters.includeSuccessfulTenders}
                style={CHECK_BOX_STYLE}
                onChange={(event) => {
                  const includeSuccessfulTenders = event.target.checked;
                  updateFilters((current) => ({ ...current, includeSuccessfulTenders }));
                }}
              />
              {SUCCESSFUL_TENDERS_CAPTION}
            </label>
          </div>
          <div style={FILTER_BUTTONS_STYLE}>
            <DialogButton onClick={() => void applyFilter()}>{APPLY_FILTER_CAPTION}</DialogButton>
            <DialogButton onClick={resetFilters}>{RESET_FILTERS_CAPTION}</DialogButton>
          </div>
        </fieldset>
        <div aria-busy={searching} style={TABLE_AREA_STYLE} onClick={handleTableAreaClick}>
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            selectedKey={selectedId}
            onSelect={selectRow}
            onDoubleClick={(row) => {
              setSelectedId(row.id);
              setViewId(row.id);
            }}
            rowStyle={tenderRowStyle}
          />
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton aria-disabled={!viewEnabled} onClick={viewTender}>
          {VIEW_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!deleteEnabled || deleting} onClick={() => void deleteTender()}>
          {DELETE_CAPTION}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {messageBox}
      {viewId !== null && <ViewTenderDialog tenderId={viewId} onClose={closeViewDialog} />}
    </section>
  );
}
