// Clients list of the admin area: the company's clients, the retail and tender sales of the selected client, and the
// Add, Edit, Remove and Close buttons.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement, ReactNode } from 'react';
import { api, ApiError } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  ClientDetailsResponse,
  ClientProjection,
  EntityTypeName,
  OperationResult,
  SalesRowProjection,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable';
import { displayErrorsOptions } from '../../components/ErrorList';
import { useMessageBox } from '../../components/MessageBox';
import { currencyFormat, shortFormat } from '../../format';
import { msg } from '../../messages/messages';
import { EditClientDialog } from './EditClientDialog';

/** Properties of {@link ClientsListPage}. */
export interface ClientsListPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** The client edited by the open {@link EditClientDialog}: null for a new client. */
interface EditorTarget {
  clientId: number | null;
}

const CLIENTS_PATH = '/api/app/admin/clients';

const CLIENT_EVENTS: readonly EntityTypeName[] = ['Client'];
const SALE_EVENTS: readonly EntityTypeName[] = ['RetailSale', 'TenderSale'];

const FORM_CAPTION = 'Clients';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';
const RETAIL_SALES_CAPTION = 'Retail Sales';
const TENDER_SALES_CAPTION = 'Tender Sales';

const NO_SALES: readonly SalesRowProjection[] = [];

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const BUTTON_ACTIVE_BORDER_COLOR = 'rgb(0, 80, 160)';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.75rem',
  boxSizing: 'border-box',
  minInlineSize: 0,
  padding: '0.625rem',
  color: 'black',
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

const CLIENT_LIST_BOX_STYLE: CSSProperties = {
  flex: '0 1 13.5625rem',
  minInlineSize: 0,
  blockSize: '19.5625rem',
  boxSizing: 'border-box',
  overflow: 'auto',
  background: 'white',
  border: LIST_BORDER,
};

const SALES_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '1 1 15.875rem',
  minInlineSize: 0,
};

const SALES_LABEL_STYLE: CSSProperties = {
  marginBlockEnd: '0.1875rem',
  overflowWrap: 'break-word',
};

const TENDER_SALES_LABEL_STYLE: CSSProperties = {
  ...SALES_LABEL_STYLE,
  marginBlockStart: '0.625rem',
};

const SALES_LIST_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  margin: 0,
  paddingBlock: '0.125rem',
  paddingInline: '0.25rem',
  listStyle: 'none',
  overflow: 'auto',
  background: 'white',
  border: LIST_BORDER,
};

const RETAIL_SALES_LIST_STYLE: CSSProperties = {
  ...SALES_LIST_STYLE,
  blockSize: '7.75rem',
};

const TENDER_SALES_LIST_STYLE: CSSProperties = {
  ...SALES_LIST_STYLE,
  blockSize: '8.3125rem',
};

const SALES_ENTRY_STYLE: CSSProperties = {
  overflowWrap: 'anywhere',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
};

const BUTTON_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: '5.5rem',
  minBlockSize: '1.375rem',
  paddingBlock: '0.125rem',
  paddingInline: '0.75rem',
  overflowWrap: 'anywhere',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: 'rgb(128, 128, 128)',
  borderRadius: '2px',
  backgroundColor: 'rgb(240, 240, 240)',
  color: 'black',
  cursor: 'pointer',
};

const BUTTON_HOVER_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: BUTTON_ACTIVE_BORDER_COLOR,
  backgroundColor: 'rgb(229, 241, 251)',
};

const BUTTON_PRESSED_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: BUTTON_ACTIVE_BORDER_COLOR,
  backgroundColor: 'rgb(204, 228, 247)',
};

const BUTTON_DISABLED_STYLE: CSSProperties = {
  ...BUTTON_STYLE,
  borderColor: 'rgb(191, 191, 191)',
  color: 'rgb(109, 109, 109)',
  cursor: 'default',
};

const BUTTON_FOCUS_STYLE: CSSProperties = {
  outline: '2px solid rgb(0, 80, 160)',
  outlineOffset: '1px',
};

/** Returns whether `error` is the {@link ApiError} of an HTTP 404 response. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the path of the client `id` under /api/app/admin/clients. */
function clientPath(id: number): string {
  return `${CLIENTS_PATH}/${id}`;
}

/** Returns the Retail Sales entry of `sale`: "<item name>: <sale price in currency format>". */
function retailSaleEntry(sale: SalesRowProjection): string {
  return `${sale.itemName}: ${currencyFormat(sale.salePrice ?? 0)}`;
}

/**
 * Returns the Tender Sales entry of `sale`: "<item name>: <offer in currency format> @ <sale date d/MM/yyyy>"; the
 * entry holds no time of day.
 */
function tenderSaleEntry(sale: SalesRowProjection): string {
  return `${sale.itemName}: ${currencyFormat(sale.offer ?? 0)} @ ${shortFormat(sale.saleDate)}`;
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
 * Returns the style of a {@link PushButton}: the disabled, pressed, hovered or base appearance, in that order of
 * precedence, with the focus outline added when `focusVisible` is true.
 */
function pushButtonStyle(disabled: boolean, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = BUTTON_STYLE;
  if (disabled) {
    style = BUTTON_DISABLED_STYLE;
  } else if (pressed) {
    style = BUTTON_PRESSED_STYLE;
  } else if (hovered) {
    style = BUTTON_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...BUTTON_FOCUS_STYLE } : style;
}

/** Properties of {@link PushButton}. */
interface PushButtonProps {
  /** Caption of the button. */
  children: ReactNode;
  /** When true, the button has the `disabled` attribute and shows its disabled appearance. */
  disabled?: boolean;
  /** Called when the button is clicked. */
  onClick: () => void;
}

/**
 * Push button of type `button`, disabled through its `disabled` attribute: a rgb(240, 240, 240) background, black
 * text and a 1px rgb(128, 128, 128) border; a rgb(229, 241, 251) background and rgb(0, 80, 160) border while the
 * pointer is over it; a rgb(204, 228, 247) background while the primary mouse button is held on it; a 2px
 * rgb(0, 80, 160) outline 1px outside its border when focused with `:focus-visible`, checked again on every key
 * release; and, disabled, rgb(109, 109, 109) text, a rgb(191, 191, 191) border and no hover, pressed or focus state.
 */
function PushButton({ children, disabled = false, onClick }: PushButtonProps): ReactElement {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  useEffect(() => {
    if (disabled) {
      setHovered(false);
      setPressed(false);
      setFocusVisible(false);
    }
  }, [disabled]);

  const updateFocusVisible = (event: FocusEvent<HTMLButtonElement> | KeyboardEvent<HTMLButtonElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return (
    <button
      type="button"
      disabled={disabled}
      style={pushButtonStyle(disabled, pressed, hovered, focusVisible)}
      onClick={onClick}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => {
        setHovered(false);
        setPressed(false);
      }}
      onMouseDown={(event: MouseEvent<HTMLButtonElement>) => {
        if (event.button === 0) {
          setPressed(true);
        }
      }}
      onMouseUp={() => setPressed(false)}
      onFocus={updateFocusVisible}
      onKeyUp={updateFocusVisible}
      onBlur={() => setFocusVisible(false)}
    >
      {children}
    </button>
  );
}

/**
 * The "Clients" tab of the admin area: the company's clients in the order received, which is name order, and the
 * retail and tender sales of the selected client.
 *
 * - On mount it reads GET /api/app/admin/clients; every EntityChanged event for a client reads the list again. When
 *   the list no longer holds the selected client, the selection and both sales lists are cleared.
 * - Edit and Remove are disabled while no client is selected. Selecting a client reads GET
 *   /api/app/admin/clients/{id} and lists its retail sales, each as "<item>: <sale price>", and its tender sales,
 *   each as "<item>: <offer> @ <d/MM/yyyy>", amounts in currency format, in the order received; only the answer for
 *   the latest selection is shown. Every EntityChanged event for a retail or tender sale reads the selected client's
 *   sales again. A 404 clears the selection and reads the client list again.
 * - Add opens {@link EditClientDialog} for a new client; Edit, a double-click or Enter on a row opens it for that
 *   client. Closing the dialog after a save reads the client list and the selected client's sales again.
 * - Remove sends DELETE /api/app/admin/clients/{id} for the selected client at once. A failed result is shown with its
 *   full error text titled `$ErrorDuringSave`; a succeeded one clears the selection and both sales lists and reads
 *   the client list again, as does a 404. Remove is ignored while a delete is outstanding.
 * - Close calls `onClose`.
 * - An error outcome is shown in a message box, an UnauthorizedError shows nothing, and any other failure rejects
 *   unhandled. The page has `aria-busy="true"` and the progress cursor while a request is outstanding.
 */
export function ClientsListPage({ onClose }: ClientsListPageProps = {}): ReactElement {
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();

  const mountedRef = useRef(false);
  const selectedRef = useRef<number | null>(null);
  const clientsRequestRef = useRef(0);
  const salesRequestRef = useRef(0);
  const removingRef = useRef(false);

  const [clients, setClients] = useState<ClientProjection[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [retailSales, setRetailSales] = useState<readonly SalesRowProjection[]>(NO_SALES);
  const [tenderSales, setTenderSales] = useState<readonly SalesRowProjection[]>(NO_SALES);
  const [clientsLoading, setClientsLoading] = useState(false);
  const [salesLoading, setSalesLoading] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [editor, setEditor] = useState<EditorTarget | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Clears the selection and both sales lists, and drops the answer of any outstanding sales read. */
  const clearSelection = useCallback((): void => {
    selectedRef.current = null;
    salesRequestRef.current += 1;
    setSelectedId(null);
    setRetailSales(NO_SALES);
    setTenderSales(NO_SALES);
    setSalesLoading(false);
  }, []);

  /** Reads the client list, clearing the selection when the list no longer holds the selected client. */
  const loadClients = useCallback(async (): Promise<void> => {
    clientsRequestRef.current += 1;
    const request = clientsRequestRef.current;
    const isCurrent = (): boolean => mountedRef.current && request === clientsRequestRef.current;

    setClientsLoading(true);
    let list: ClientProjection[] | undefined;
    try {
      list = await api.get<ClientProjection[] | undefined>(CLIENTS_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        setClientsLoading(false);
        await showError(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    const received = list ?? [];
    setClientsLoading(false);
    setClients(received);
    const selected = selectedRef.current;
    if (selected !== null && !received.some((client) => client.id === selected)) {
      clearSelection();
    }
  }, [clearSelection, showError]);

  /** Reads the retail and tender sales of the client `id` and lists them while that client stays selected. */
  const loadSales = useCallback(
    async (id: number): Promise<void> => {
      salesRequestRef.current += 1;
      const request = salesRequestRef.current;
      const isCurrent = (): boolean =>
        mountedRef.current && request === salesRequestRef.current && selectedRef.current === id;

      setSalesLoading(true);
      let details: ClientDetailsResponse | undefined;
      try {
        details = await api.get<ClientDetailsResponse | undefined>(clientPath(id));
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        setSalesLoading(false);
        if (isNotFound(error)) {
          clearSelection();
          await loadClients();
          return;
        }
        await showError(error);
        return;
      }

      if (!isCurrent()) {
        return;
      }
      setSalesLoading(false);
      setRetailSales(details?.retailSales ?? NO_SALES);
      setTenderSales(details?.tenderSales ?? NO_SALES);
    },
    [clearSelection, loadClients, showError],
  );

  /** Reads the selected client's sales again; does nothing while no client is selected. */
  const reloadSelectedSales = useCallback(async (): Promise<void> => {
    const selected = selectedRef.current;
    if (selected !== null) {
      await loadSales(selected);
    }
  }, [loadSales]);

  useEffect(() => {
    void loadClients();
  }, [loadClients]);

  useEffect(() => {
    const stopClients = onEntityChanged(CLIENT_EVENTS, () => loadClients());
    const stopSales = onEntityChanged(SALE_EVENTS, () => reloadSelectedSales());
    return () => {
      stopClients();
      stopSales();
    };
  }, [loadClients, reloadSelectedSales]);

  /** Selects `client` and reads its sales; selecting another client empties both sales lists first. */
  const selectClient = (client: ClientProjection): void => {
    if (selectedRef.current !== client.id) {
      setRetailSales(NO_SALES);
      setTenderSales(NO_SALES);
    }
    selectedRef.current = client.id;
    setSelectedId(client.id);
    void loadSales(client.id);
  };

  /** Opens the client editor for the selected client; does nothing while no client is selected. */
  const editSelected = (): void => {
    const selected = selectedRef.current;
    if (selected !== null) {
      setEditor({ clientId: selected });
    }
  };

  /** Closes the client editor, reading the client list and the selected client's sales again after a save. */
  const closeEditor = (saved: boolean): void => {
    setEditor(null);
    if (saved) {
      void loadClients();
      void reloadSelectedSales();
    }
  };

  /** Deletes the selected client; does nothing while no client is selected or a delete is outstanding. */
  const removeSelected = async (): Promise<void> => {
    const selected = selectedRef.current;
    if (selected === null || removingRef.current) {
      return;
    }

    removingRef.current = true;
    setRemoving(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(clientPath(selected));
    } catch (error: unknown) {
      removingRef.current = false;
      if (!mountedRef.current) {
        return;
      }
      setRemoving(false);
      if (isNotFound(error)) {
        clearSelection();
        await loadClients();
        return;
      }
      await showError(error);
      return;
    }

    removingRef.current = false;
    if (!mountedRef.current) {
      return;
    }
    setRemoving(false);

    if (result === undefined) {
      await loadClients();
      return;
    }
    if (!result.succeeded) {
      await show(displayErrorsOptions(result));
      return;
    }
    clearSelection();
    await loadClients();
  };

  const columns: DataTableColumn<ClientProjection>[] = [
    { key: 'name', header: msg('Client'), render: (client) => client.name },
  ];

  const busy = clientsLoading || salesLoading || removing;
  const nothingSelected = selectedId === null;

  return (
    <section aria-labelledby={headingId} aria-busy={busy} style={busy ? BUSY_SECTION_STYLE : SECTION_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {FORM_CAPTION}
      </h2>
      <div style={BODY_STYLE}>
        <div style={CLIENT_LIST_BOX_STYLE}>
          <DataTable
            caption={msg('Clients')}
            columns={columns}
            rows={clients}
            rowKey={(client) => client.id}
            selectedKey={selectedId}
            onSelect={selectClient}
            onDoubleClick={(client) => setEditor({ clientId: client.id })}
          />
        </div>
        <div style={SALES_COLUMN_STYLE}>
          <span style={SALES_LABEL_STYLE}>{RETAIL_SALES_CAPTION}</span>
          <ul aria-label={RETAIL_SALES_CAPTION} style={RETAIL_SALES_LIST_STYLE}>
            {retailSales.map((sale) => (
              <li key={sale.saleId} style={SALES_ENTRY_STYLE}>
                {retailSaleEntry(sale)}
              </li>
            ))}
          </ul>
          <span style={TENDER_SALES_LABEL_STYLE}>{TENDER_SALES_CAPTION}</span>
          <ul aria-label={TENDER_SALES_CAPTION} style={TENDER_SALES_LIST_STYLE}>
            {tenderSales.map((sale) => (
              <li key={sale.saleId} style={SALES_ENTRY_STYLE}>
                {tenderSaleEntry(sale)}
              </li>
            ))}
          </ul>
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <PushButton onClick={() => setEditor({ clientId: null })}>{ADD_CAPTION}</PushButton>
        <PushButton disabled={nothingSelected} onClick={editSelected}>
          {EDIT_CAPTION}
        </PushButton>
        <PushButton disabled={nothingSelected} onClick={() => void removeSelected()}>
          {REMOVE_CAPTION}
        </PushButton>
        <PushButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</PushButton>
      </div>
      {editor !== null && <EditClientDialog clientId={editor.clientId} onClose={closeEditor} />}
      {messageBox}
    </section>
  );
}

