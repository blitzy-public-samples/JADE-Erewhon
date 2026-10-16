// Sales list of the admin area: the Agent, Client and Company options with the agent and client selectors, the
// sales table with its header sort, the retail, tender and commission totals, and the Close button.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, ReactElement } from 'react';
import { api, ApiError } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AgentProjection,
  ClientProjection,
  CompanyProjection,
  EntityTypeName,
  SalesListResponse,
  SalesRowProjection,
  SalesShowFor,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { currencyFormat, decimalString, shortFormat } from '../../format';
import { msg } from '../../messages/messages';
import type { MessageKey } from '../../messages/messages';
import { isCompanyUser, useSession } from '../../session/SessionProvider';
import { makeRow } from './salesRow';

/** Properties of {@link SalesListPage}. */
export interface SalesListPageProps {
  /** Called by the Close button; the button is rendered only when this is given. */
  onClose?: () => void;
}

/** Sort applied by a header click: alphabetic, numeric or by date. */
type SortKind = 'alpha' | 'numeric' | 'date';

/** One column of the sales table. */
interface SalesColumn {
  key: string;
  /** Message key of the header text. */
  header: MessageKey;
  sortKind: SortKind;
  /** Share of the table width given to the column, in percent. */
  widthPercent: number;
}

/** Cell texts of one row: Name, Product, Price, Date Sold, Bid and Commission. */
type SalesCells = readonly [string, string, string, string, string, string];

/** One built row of the sales table. */
interface SalesTableRow {
  saleId: number;
  cells: SalesCells;
  /** Sale date, `yyyy-MM-dd`; null when the sale has none. */
  saleDate: string | null;
}

/** Retail, tender and commission totals of the built rows. */
interface SalesTotals {
  retailTotal: number;
  tenderTotal: number;
  commissionTotal: number;
}

/** Rows and totals shown by the page. */
interface SalesTable {
  rows: SalesTableRow[];
  totals: SalesTotals;
}

/** Option chosen and agent selected when the page loads. */
interface InitialSelection {
  showFor: SalesShowFor;
  agentId: number | null;
}

/** Column of the current header sort, null before the first header click, and its direction. */
interface SortState {
  column: number | null;
  ascending: boolean;
}

const AGENTS_PATH = '/api/app/admin/agents';
const CLIENTS_PATH = '/api/app/admin/clients';
const COMPANY_PATH = '/api/app/admin/company';
const SALES_PATH = '/api/app/admin/sales';

const AGENT_EVENTS: readonly EntityTypeName[] = ['Agent'];
const CLIENT_EVENTS: readonly EntityTypeName[] = ['Client'];

const SHOW_FOR_CAPTION = 'Show Sales for:';
const CLOSE_CAPTION = 'Close';
const TOTAL_RETAIL_CAPTION = 'Total of Retail Sales:';
const TOTAL_TENDER_CAPTION = 'Total of Tenders:';
const TOTAL_COMMISSION_CAPTION = 'Total of Commission:';

const AGENT_FIELD_NAME = 'agentId';
const CLIENT_FIELD_NAME = 'clientId';

const TAB = '\t';

const COLUMNS: readonly SalesColumn[] = [
  { key: 'name', header: 'Name', sortKind: 'alpha', widthPercent: 26 },
  { key: 'product', header: 'Product', sortKind: 'alpha', widthPercent: 26 },
  { key: 'price', header: 'Price', sortKind: 'numeric', widthPercent: 12 },
  { key: 'dateSold', header: 'DateSold', sortKind: 'date', widthPercent: 12 },
  { key: 'bid', header: 'Bid', sortKind: 'numeric', widthPercent: 12 },
  { key: 'commission', header: 'Commission', sortKind: 'numeric', widthPercent: 12 },
];

const ZERO_TOTALS: SalesTotals = { retailTotal: 0, tenderTotal: 0, commissionTotal: 0 };

const EMPTY_TABLE: SalesTable = { rows: [], totals: ZERO_TOTALS };

const INITIAL_SORT: SortState = { column: null, ascending: true };

const TABLE_WIDTH_PX = 655;
const CELL_PADDING_AND_BORDER_PX = 13;
const ROOT_FONT_PX = 16;

const DISABLED_TEXT_COLOR = 'rgb(109, 109, 109)';

const PAGE_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.75rem',
  boxSizing: 'border-box',
  minInlineSize: 0,
  paddingBlock: '0.5625rem',
  paddingInline: '0.625rem',
  color: 'black',
};

const BUSY_PAGE_STYLE: CSSProperties = {
  ...PAGE_STYLE,
  cursor: 'wait',
};

const LIST_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.375rem',
  minInlineSize: 0,
};

const SHOW_FOR_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'auto 5.4375rem minmax(0, 25.5625rem)',
  gridTemplateRows: 'repeat(3, minmax(1.3125rem, auto))',
  columnGap: '0.3125rem',
  alignItems: 'center',
  justifyContent: 'start',
  minInlineSize: 0,
};

const SHOW_FOR_LABEL_STYLE: CSSProperties = {
  gridColumn: 1,
  gridRow: 1,
  textAlign: 'end',
  whiteSpace: 'nowrap',
};

const OPTION_GROUP_STYLE: CSSProperties = {
  gridColumn: '2 / 4',
  gridRow: '1 / 4',
  display: 'grid',
  gridTemplateRows: 'subgrid',
  alignItems: 'center',
  minInlineSize: 0,
};

const OPTION_LABEL_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.375rem',
  justifySelf: 'start',
  minInlineSize: 0,
  overflowWrap: 'anywhere',
};

const DISABLED_OPTION_LABEL_STYLE: CSSProperties = {
  ...OPTION_LABEL_STYLE,
  color: DISABLED_TEXT_COLOR,
};

const RADIO_STYLE: CSSProperties = {
  flex: '0 0 auto',
  margin: 0,
};

const SELECT_STYLE: CSSProperties = {
  gridColumn: 3,
  boxSizing: 'border-box',
  inlineSize: '100%',
  minInlineSize: 0,
  margin: 0,
  font: 'inherit',
};

const AGENT_SELECT_STYLE: CSSProperties = { ...SELECT_STYLE, gridRow: 1 };

const CLIENT_SELECT_STYLE: CSSProperties = { ...SELECT_STYLE, gridRow: 2 };

const TABLE_AREA_STYLE: CSSProperties = {
  maxInlineSize: '100%',
  overflowX: 'auto',
};

const HEADER_BUTTON_STYLE: CSSProperties = {
  display: 'block',
  boxSizing: 'border-box',
  inlineSize: '100%',
  margin: 0,
  padding: 0,
  border: 0,
  background: 'transparent',
  color: 'inherit',
  font: 'inherit',
  textAlign: 'start',
  cursor: 'pointer',
};

const TOTALS_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'max-content minmax(0, 9.5625rem)',
  gridAutoRows: 'minmax(1.3125rem, auto)',
  columnGap: '0.1875rem',
  rowGap: '0.375rem',
  alignItems: 'center',
  justifyContent: 'start',
};

const TOTAL_LABEL_STYLE: CSSProperties = {
  overflowWrap: 'break-word',
};

const TOTAL_VALUE_STYLE: CSSProperties = {
  fontWeight: 'bold',
  textAlign: 'end',
  overflowWrap: 'anywhere',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
};

/**
 * Returns the minimum content width of a header that takes `percent` of the 655px table, less the cell's padding and
 * border, in rem.
 */
function columnContentWidth(percent: number): string {
  const contentPx = (TABLE_WIDTH_PX * percent) / 100 - CELL_PADDING_AND_BORDER_PX;
  return `${(contentPx / ROOT_FONT_PX).toFixed(4)}rem`;
}

/** Returns the style of the sort button heading a column of `percent` width. */
function headerButtonStyle(percent: number): CSSProperties {
  return { ...HEADER_BUTTON_STYLE, minInlineSize: columnContentWidth(percent) };
}

/** Returns the request path of the sales list for `showFor` and the agent or client `id`. */
function salesPath(showFor: SalesShowFor, id: number | null): string {
  return `${SALES_PATH}?showFor=${showFor}${id !== null ? `&id=${id}` : ''}`;
}

/** Reads the agent or client list at `path`, in the order received; an empty body gives an empty list. */
async function readList<T>(path: string): Promise<T[]> {
  const loaded = await api.get<T[] | undefined>(path);
  return Array.isArray(loaded) ? loaded : [];
}

/** Returns the state update that keeps a selected id only while `list` holds a row with that id. */
function keepIfListed(list: readonly { id: number }[]): (selected: number | null) => number | null {
  return (selected) => (selected !== null && list.some((row) => row.id === selected) ? selected : null);
}

/** Returns the selector entry text of an agent or client: name, street, city and country separated by `, `. */
function nameAndAddress(party: AgentProjection | ClientProjection): string {
  return `${party.name}, ${party.street}, ${party.city}, ${party.country}`;
}

/**
 * Port of SalesListForm.buildSalesTable over the listed sales, in the order given: keeps each sale whose item agent
 * is the session agent, or every sale for the company user; builds the client, "<code> - <name>", item price and
 * sale date cells, appends the Bid cell with makeRow, which also adds to the retail or tender total, and the
 * commission cell; sums the commissions in whole cents.
 */
function buildSalesTable(
  sales: readonly SalesRowProjection[],
  companyUser: boolean,
  sessionAgentId: number | null,
): SalesTable {
  const rows: SalesTableRow[] = [];
  let totals = { retailTotal: 0, tenderTotal: 0 };
  let commissionCents = 0;

  for (const sale of sales) {
    if (!companyUser && sale.agentId !== sessionAgentId) {
      continue;
    }

    const product = `${sale.itemCode ?? ''} - ${sale.itemName}`;
    const price = decimalString(sale.itemPrice);
    const dateSold = shortFormat(sale.saleDate);
    const prefix = [sale.clientName, product, price, dateSold].join(TAB);
    const made = makeRow(prefix, totals, sale);
    totals = { retailTotal: made.retailTotal, tenderTotal: made.tenderTotal };
    commissionCents += Math.round(sale.agentCommission * 100);

    rows.push({
      saleId: sale.saleId,
      cells: [
        sale.clientName,
        product,
        price,
        dateSold,
        made.rowString.slice(prefix.length + TAB.length),
        decimalString(sale.agentCommission),
      ],
      saleDate: sale.saleDate,
    });
  }

  return { rows, totals: { ...totals, commissionTotal: commissionCents / 100 } };
}

/** Returns the number a numeric cell holds; an empty or non-numeric cell counts as 0. */
function numericValue(text: string): number {
  const value = Number(text);
  return Number.isFinite(value) ? value : 0;
}

/** Compares two texts by UTF-16 code unit. */
function compareText(a: string, b: string): number {
  if (a < b) {
    return -1;
  }
  return a > b ? 1 : 0;
}

/**
 * Compares two rows on `column`: numerically for a numeric column, by sale date for the date column with a missing
 * date first, and by cell text otherwise.
 */
function compareRows(a: SalesTableRow, b: SalesTableRow, column: number, kind: SortKind): number {
  switch (kind) {
    case 'numeric':
      return numericValue(a.cells[column]) - numericValue(b.cells[column]);
    case 'date':
      return compareText(a.saleDate ?? '', b.saleDate ?? '');
    default:
      return compareText(a.cells[column], b.cells[column]);
  }
}

/** Returns `rows` sorted on `column`, ascending or descending, keeping the order of rows that compare equal. */
function sortRows(
  rows: SalesTableRow[],
  column: number,
  kind: SortKind,
  ascending: boolean,
): SalesTableRow[] {
  return [...rows].sort((a, b) => {
    const order = compareRows(a, b, column, kind);
    return ascending ? order : -order;
  });
}

/**
 * The "Sales" tab of the admin area: the sales of the selected agent's items, of the selected client or of the
 * company, one row per sale, with the retail, tender and commission totals.
 *
 * - On mount it reads the agents and the clients, listed in the order received in the Agent and Client selectors
 *   after an empty entry, each entry showing the name, street, city and country, and the company, whose name is
 *   added to the Company option's caption. Every EntityChanged event for an agent or a client reads that list again;
 *   a selected agent or client the new list no longer holds is deselected.
 * - For an agent session the Agent option is chosen and the Agent selector shows the session agent; that selector
 *   and the Company option are disabled. Otherwise the Company option is chosen.
 * - The table is built on mount, when an option is chosen or clicked again, and when an agent or client is chosen in
 *   its selector, which also chooses its option; choosing the empty entry deselects without building. Each build
 *   empties the table and sets the totals to $0.00, then, unless the Agent or Client option is chosen with nothing
 *   selected, reads GET /api/app/admin/sales and fills the table; only the answer to the latest build is shown.
 *   Unless the session is the company user's, only sales of the session agent's items are listed.
 * - Each row shows the client, "<code> - <name>", the item price, the sale date (d/MM/yyyy), the winning offer of a
 *   tender sale (empty for a retail sale) and the commission; prices, offers and commissions have two decimals and
 *   no currency sign, and the totals are shown in currency format.
 * - Clicking a header sorts the rows on that column: numerically for Price, Bid and Commission, by date for Date Sold
 *   and by text otherwise. The column already sorted toggles between ascending and descending; another column keeps
 *   the current direction, ascending at first. A build lists the rows in the order received.
 * - A 404 for the selected agent or client leaves the table empty and reads both selector lists again. An error
 *   outcome is shown in a message box, an UnauthorizedError shows nothing, and any other failure rejects unhandled.
 * - The Close button, rendered only when `onClose` is given, calls it.
 */
export function SalesListPage({ onClose }: SalesListPageProps = {}): ReactElement {
  const { session } = useSession();
  const companyUser = isCompanyUser(session);
  const sessionAgentId = session?.agentId ?? null;
  const { element: messageBox, showError } = useMessageBox();
  const showForLabelId = useId();
  const optionGroupName = useId();
  const retailTotalId = useId();
  const tenderTotalId = useId();
  const commissionTotalId = useId();

  const mountedRef = useRef(false);
  const agentsRequestRef = useRef(0);
  const clientsRequestRef = useRef(0);
  const salesRequestRef = useRef(0);

  const [initial] = useState<InitialSelection>(() => ({
    showFor: sessionAgentId !== null ? 'agent' : 'company',
    agentId: sessionAgentId,
  }));
  const [agents, setAgents] = useState<readonly AgentProjection[]>([]);
  const [clients, setClients] = useState<readonly ClientProjection[]>([]);
  const [companyName, setCompanyName] = useState<string | null>(null);
  const [showFor, setShowFor] = useState<SalesShowFor>(initial.showFor);
  const [agentId, setAgentId] = useState<number | null>(initial.agentId);
  const [clientId, setClientId] = useState<number | null>(null);
  const [table, setTable] = useState<SalesTable>(EMPTY_TABLE);
  const [loading, setLoading] = useState(false);
  const [sort, setSort] = useState<SortState>(INITIAL_SORT);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const loadAgents = useCallback(async (): Promise<void> => {
    agentsRequestRef.current += 1;
    const request = agentsRequestRef.current;
    const isCurrent = () => mountedRef.current && request === agentsRequestRef.current;

    let list: AgentProjection[];
    try {
      list = await readList<AgentProjection>(AGENTS_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setAgents(list);
      setAgentId(keepIfListed(list));
    }
  }, [showError]);

  const loadClients = useCallback(async (): Promise<void> => {
    clientsRequestRef.current += 1;
    const request = clientsRequestRef.current;
    const isCurrent = () => mountedRef.current && request === clientsRequestRef.current;

    let list: ClientProjection[];
    try {
      list = await readList<ClientProjection>(CLIENTS_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (isCurrent()) {
      setClients(list);
      setClientId(keepIfListed(list));
    }
  }, [showError]);

  /** Port of SalesListForm.initializeSalesTable followed by buildSalesTable for the given option and selections. */
  const rebuild = async (
    option: SalesShowFor,
    selectedAgentId: number | null,
    selectedClientId: number | null,
  ): Promise<void> => {
    salesRequestRef.current += 1;
    const request = salesRequestRef.current;
    const isCurrent = () => mountedRef.current && request === salesRequestRef.current;

    setTable(EMPTY_TABLE);
    const entityId = option === 'agent' ? selectedAgentId : option === 'client' ? selectedClientId : null;
    if (option !== 'company' && entityId === null) {
      setLoading(false);
      return;
    }

    setLoading(true);
    let response: SalesListResponse | undefined;
    try {
      response = await api.get<SalesListResponse | undefined>(salesPath(option, entityId));
    } catch (error: unknown) {
      if (!isCurrent()) {
        return;
      }
      setLoading(false);
      if (error instanceof ApiError && error.status === 404) {
        await Promise.all([loadAgents(), loadClients()]);
        return;
      }
      await showError(error);
      return;
    }

    if (!isCurrent()) {
      return;
    }
    setLoading(false);
    setTable(buildSalesTable(response?.rows ?? [], companyUser, sessionAgentId));
  };

  const rebuildRef = useRef(rebuild);

  useEffect(() => {
    rebuildRef.current = rebuild;
  });

  useEffect(() => {
    void loadAgents();
    void loadClients();
  }, [loadAgents, loadClients]);

  useEffect(() => {
    let active = true;
    api.get<CompanyProjection | undefined>(COMPANY_PATH).then(
      (company) => {
        if (active && company !== undefined) {
          setCompanyName(company.name);
        }
      },
      (error: unknown) => (active ? showError(error) : undefined),
    );
    return () => {
      active = false;
    };
  }, [showError]);

  useEffect(() => {
    void rebuildRef.current(initial.showFor, initial.agentId, null);
  }, [initial]);

  useEffect(() => {
    const stopAgents = onEntityChanged(AGENT_EVENTS, () => loadAgents());
    const stopClients = onEntityChanged(CLIENT_EVENTS, () => loadClients());
    return () => {
      stopAgents();
      stopClients();
    };
  }, [loadAgents, loadClients]);

  /** Port of optAgent_click, optClient_click and optCompany_click: chooses `option` and builds the table. */
  const chooseOption = (option: SalesShowFor): void => {
    setShowFor(option);
    void rebuild(option, agentId, clientId);
  };

  /** Port of cmbAgent_click: an agent chooses the Agent option and builds the table; the empty entry deselects. */
  const chooseAgent = (value: string): void => {
    if (value === '') {
      setAgentId(null);
      return;
    }
    const id = Number(value);
    setAgentId(id);
    setShowFor('agent');
    void rebuild('agent', id, clientId);
  };

  /** Port of cmbClient_click: a client chooses the Client option and builds the table; the empty entry deselects. */
  const chooseClient = (value: string): void => {
    if (value === '') {
      setClientId(null);
      return;
    }
    const id = Number(value);
    setClientId(id);
    setShowFor('client');
    void rebuild('client', agentId, id);
  };

  /** Port of tblSales_click on a header: sorts the listed rows on `column`. */
  const sortBy = (column: number, kind: SortKind): void => {
    const ascending = sort.column === column ? !sort.ascending : sort.ascending;
    setSort({ column, ascending });
    setTable((current) => ({ ...current, rows: sortRows(current.rows, column, kind, ascending) }));
  };

  const optionInputProps = (option: SalesShowFor) => ({
    type: 'radio' as const,
    name: optionGroupName,
    value: option,
    checked: showFor === option,
    style: RADIO_STYLE,
    onChange: () => {
      if (showFor !== option) {
        chooseOption(option);
      }
    },
    onClick: () => {
      if (showFor === option) {
        chooseOption(option);
      }
    },
  });

  const columns: DataTableColumn<SalesTableRow>[] = COLUMNS.map((column, index) => ({
    key: column.key,
    header: (
      <button
        type="button"
        style={headerButtonStyle(column.widthPercent)}
        onClick={() => sortBy(index, column.sortKind)}
      >
        {msg(column.header)}
      </button>
    ),
    render: (row: SalesTableRow) => row.cells[index],
  }));

  const agentSession = initial.agentId !== null;
  const companyCaption = companyName === null ? msg('Company') : `${msg('Company')} (${companyName})`;

  return (
    <section aria-label={msg('Sales')} style={loading ? BUSY_PAGE_STYLE : PAGE_STYLE}>
      <div style={LIST_STYLE}>
        <div style={SHOW_FOR_STYLE}>
          <span id={showForLabelId} style={SHOW_FOR_LABEL_STYLE}>
            {SHOW_FOR_CAPTION}
          </span>
          <div role="radiogroup" aria-labelledby={showForLabelId} style={OPTION_GROUP_STYLE}>
            <label style={OPTION_LABEL_STYLE}>
              <input {...optionInputProps('agent')} />
              {msg('Agent')}
            </label>
            <label style={OPTION_LABEL_STYLE}>
              <input {...optionInputProps('client')} />
              {msg('Client')}
            </label>
            <label style={agentSession ? DISABLED_OPTION_LABEL_STYLE : OPTION_LABEL_STYLE}>
              <input {...optionInputProps('company')} disabled={agentSession} />
              {companyCaption}
            </label>
          </div>
          <select
            name={AGENT_FIELD_NAME}
            autoComplete="off"
            aria-label={msg('Agent')}
            value={agentId === null ? '' : String(agentId)}
            disabled={agentSession}
            style={AGENT_SELECT_STYLE}
            onChange={(event) => chooseAgent(event.target.value)}
          >
            <option value="" />
            {agents.map((agent) => (
              <option key={agent.id} value={String(agent.id)}>
                {nameAndAddress(agent)}
              </option>
            ))}
          </select>
          <select
            name={CLIENT_FIELD_NAME}
            autoComplete="off"
            aria-label={msg('Client')}
            value={clientId === null ? '' : String(clientId)}
            style={CLIENT_SELECT_STYLE}
            onChange={(event) => chooseClient(event.target.value)}
          >
            <option value="" />
            {clients.map((client) => (
              <option key={client.id} value={String(client.id)}>
                {nameAndAddress(client)}
              </option>
            ))}
          </select>
        </div>
        <div aria-busy={loading} style={TABLE_AREA_STYLE}>
          <DataTable
            caption={msg('Sales')}
            columns={columns}
            rows={table.rows}
            rowKey={(row) => row.saleId}
          />
        </div>
      </div>
      <div style={TOTALS_STYLE}>
        <label htmlFor={retailTotalId} style={TOTAL_LABEL_STYLE}>
          {TOTAL_RETAIL_CAPTION}
        </label>
        <output id={retailTotalId} style={TOTAL_VALUE_STYLE}>
          {currencyFormat(table.totals.retailTotal)}
        </output>
        <label htmlFor={tenderTotalId} style={TOTAL_LABEL_STYLE}>
          {TOTAL_TENDER_CAPTION}
        </label>
        <output id={tenderTotalId} style={TOTAL_VALUE_STYLE}>
          {currencyFormat(table.totals.tenderTotal)}
        </output>
        <label htmlFor={commissionTotalId} style={TOTAL_LABEL_STYLE}>
          {TOTAL_COMMISSION_CAPTION}
        </label>
        <output id={commissionTotalId} style={TOTAL_VALUE_STYLE}>
          {currencyFormat(table.totals.commissionTotal)}
        </output>
      </div>
      {onClose !== undefined && (
        <div style={BUTTON_BAR_STYLE}>
          <DialogButton onClick={onClose}>{CLOSE_CAPTION}</DialogButton>
        </div>
      )}
      {messageBox}
    </section>
  );
}
