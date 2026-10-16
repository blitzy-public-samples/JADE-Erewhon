// Agents list of the admin area: the company's agents, the items and commission rates of the selected agent, and the
// Add, Edit, Remove and Close buttons.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ApiError, ErrorOutcomeError, UnauthorizedError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AgentDetailsResponse,
  AgentProjection,
  AgentRateRow,
  EntityChangedMessage,
  EntityTypeName,
  ItemProjection,
  OperationResult,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { EditAgentDialog } from './EditAgentDialog';

const AGENTS_PATH = '/api/app/admin/agents';

const ITEMS_CAPTION = 'Items';
const COMMISSION_RATES_CAPTION = 'Commission Rates';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';

/** Entity types whose EntityChanged events refresh the page. */
const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = [
  'Agent',
  'RetailItem',
  'TenderItem',
  'CommissionRate',
  'ItemCategory',
];

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const COLUMN_SIZE = '23.75rem';

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

const BUSY_PAGE_STYLE: CSSProperties = {
  ...PAGE_STYLE,
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
  minInlineSize: 0,
};

const AGENTS_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: `0 1 ${COLUMN_SIZE}`,
  minInlineSize: 0,
  maxInlineSize: '100%',
};

const DETAILS_COLUMN_STYLE: CSSProperties = {
  ...AGENTS_COLUMN_STYLE,
  gap: '0.5rem',
};

const GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.25rem',
  minInlineSize: 0,
};

const LABEL_STYLE: CSSProperties = {
  overflowWrap: 'break-word',
};

const LIST_BOX_STYLE: CSSProperties = {
  display: 'grid',
  alignContent: 'start',
  boxSizing: 'border-box',
  inlineSize: '100%',
  overflow: 'auto',
  overflowWrap: 'break-word',
  background: 'white',
  border: LIST_BORDER,
};

const AGENTS_BOX_STYLE: CSSProperties = {
  ...LIST_BOX_STYLE,
  blockSize: '32.6875rem',
};

const ITEMS_BOX_STYLE: CSSProperties = {
  ...LIST_BOX_STYLE,
  blockSize: '13.4375rem',
};

const RATES_BOX_STYLE: CSSProperties = {
  ...LIST_BOX_STYLE,
  blockSize: '15.4375rem',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
};

/** Returns the path of /api/app/admin/agents/{id}. */
function agentPath(agentId: number): string {
  return `${AGENTS_PATH}/${agentId}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns `value` when it is an array, otherwise an empty list. */
function asList<T>(value: T[] | null | undefined): T[] {
  return Array.isArray(value) ? value : [];
}

/** Returns `value`, or the empty string for null or undefined. */
function text(value: string | null | undefined): string {
  return value ?? '';
}

/** Returns the commission-rate entry of `rate`: "<percentage>% (<item category name>)", e.g. `3.50% (Destinations)`. */
function commissionRateEntry(rate: AgentRateRow): string {
  return `${decimalString(rate.percentage)}% (${text(rate.itemCategoryName)})`;
}

const AGENT_COLUMNS: DataTableColumn<AgentProjection>[] = [
  { key: 'name', header: msg('Agents'), render: (agent) => text(agent.name) },
];

const ITEM_COLUMNS: DataTableColumn<ItemProjection>[] = [
  { key: 'name', header: ITEMS_CAPTION, render: (item) => text(item.name) },
];

const RATE_COLUMNS: DataTableColumn<AgentRateRow>[] = [
  { key: 'rate', header: COMMISSION_RATES_CAPTION, render: commissionRateEntry },
];

/**
 * The "Agents" tab of the admin area: the company's agents in the order GET /api/app/admin/agents returns them, each
 * shown by name, and beside them the "Items" and "Commission Rates" lists of the selected agent.
 *
 * - Edit and Remove are disabled while no agent is selected. Clicking an agent, or reaching it with the keyboard,
 *   selects it, enables Edit and Remove, and reads GET /api/app/admin/agents/{id}: the "Items" list shows each item's
 *   name and the "Commission Rates" list each rate as "<percentage>% (<item category name>)", both in the order
 *   received. With no agent selected both lists are empty. Only the answer to the latest request is shown, and a 404
 *   deselects the agent and reads the agents again.
 * - Add opens {@link EditAgentDialog} for a new agent; Edit, or double-clicking an agent, opens it for the selected
 *   agent. When the dialog closes after a save, the agents and the selected agent's lists are read again.
 * - Remove sends DELETE /api/app/admin/agents/{id} at once, with no confirmation. A failed result shows its errors
 *   titled `$ErrorDuringSave`; a succeeded result or a 404 deselects the agent and reads the agents again. Edit and
 *   Remove are disabled while the request is outstanding.
 * - Every EntityChanged event for an agent reads the agents again; every EntityChanged event for an agent, a retail
 *   or tender item, a commission rate or an item category reads the selected agent's lists again. A deleted selected
 *   agent, and a selected agent the agents list no longer holds, is deselected; otherwise the selection is kept.
 * - An error outcome is shown in its message box, an UnauthorizedError shows nothing, and any other failure shows
 *   its message titled `$Error`. Close calls `onClose` when it is given.
 */
export function AgentsListPage(props: { onClose?: () => void } = {}): JSX.Element {
  const { onClose } = props;
  const { element: messageBox, show, showError } = useMessageBox();
  const headingId = useId();
  const itemsLabelId = useId();
  const ratesLabelId = useId();

  const mountedRef = useRef(false);
  const selectedIdRef = useRef<number | null>(null);
  const agentsSeqRef = useRef(0);
  const detailsSeqRef = useRef(0);
  const removingRef = useRef(false);

  const [agents, setAgents] = useState<AgentProjection[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [details, setDetails] = useState<AgentDetailsResponse | null>(null);
  const [editing, setEditing] = useState<{ agentId: number | null } | null>(null);
  const [removing, setRemoving] = useState(false);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
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

  /** Makes `agentId` the selected agent; selecting another agent, or none, empties the side lists. */
  const changeSelection = useCallback((agentId: number | null): void => {
    if (selectedIdRef.current !== agentId) {
      detailsSeqRef.current += 1;
      setDetails(null);
    }
    selectedIdRef.current = agentId;
    setSelectedId(agentId);
  }, []);

  /** Reads the agents in the order received, deselecting a selected agent the answer no longer holds. */
  const loadAgents = useCallback(async (): Promise<void> => {
    agentsSeqRef.current += 1;
    const seq = agentsSeqRef.current;
    const isCurrent = (): boolean => mountedRef.current && seq === agentsSeqRef.current;

    let list: AgentProjection[];
    try {
      list = asList(await api.get<AgentProjection[] | undefined>(AGENTS_PATH));
    } catch (error: unknown) {
      if (isCurrent()) {
        await reportFailure(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    setAgents(list);
    const selected = selectedIdRef.current;
    if (selected !== null && !list.some((agent) => agent.id === selected)) {
      changeSelection(null);
    }
  }, [changeSelection, reportFailure]);

  /** Reads the items and commission rates of the agent `agentId` while it is the selected agent. */
  const loadDetails = useCallback(
    async (agentId: number): Promise<void> => {
      detailsSeqRef.current += 1;
      const seq = detailsSeqRef.current;
      const isCurrent = (): boolean =>
        mountedRef.current && seq === detailsSeqRef.current && selectedIdRef.current === agentId;

      let response: AgentDetailsResponse | undefined;
      try {
        response = await api.get<AgentDetailsResponse | undefined>(agentPath(agentId));
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        if (isNotFound(error)) {
          changeSelection(null);
          await loadAgents();
          return;
        }
        await reportFailure(error);
        return;
      }

      if (isCurrent()) {
        setDetails(response ?? null);
      }
    },
    [changeSelection, loadAgents, reportFailure],
  );

  useEffect(() => {
    void loadAgents();
  }, [loadAgents]);

  useEffect(
    () =>
      onEntityChanged(WATCHED_ENTITY_TYPES, (message: EntityChangedMessage): Promise<void> => {
        const tasks: Promise<void>[] = [];
        const selected = selectedIdRef.current;

        if (message.entityType === 'Agent') {
          if (message.kind === 'deleted' && message.id === selected) {
            changeSelection(null);
          }
          tasks.push(loadAgents());
        }

        const current = selectedIdRef.current;
        if (current !== null) {
          tasks.push(loadDetails(current));
        }

        return Promise.all(tasks).then(() => undefined);
      }),
    [changeSelection, loadAgents, loadDetails],
  );

  /** Port of AgentsListForm.lstAgents_click for a listed agent. */
  const selectAgent = (agent: AgentProjection): void => {
    changeSelection(agent.id);
    void loadDetails(agent.id);
  };

  /** Port of AgentsListForm.btnAdd_click. */
  const addAgent = (): void => {
    setEditing({ agentId: null });
  };

  /** Port of AgentsListForm.btnEdit_click. */
  const editAgent = (): void => {
    const agentId = selectedIdRef.current;
    if (agentId !== null && !removingRef.current) {
      setEditing({ agentId });
    }
  };

  /** Port of AgentsListForm.lstAgents_dblClick. */
  const editListedAgent = (agent: AgentProjection): void => {
    if (removingRef.current) {
      return;
    }
    if (selectedIdRef.current !== agent.id) {
      selectAgent(agent);
    }
    setEditing({ agentId: agent.id });
  };

  /** Port of AgentsListForm.btnRemove_click followed by the inherited doDelete. */
  const removeAgent = async (): Promise<void> => {
    const agentId = selectedIdRef.current;
    if (agentId === null || removingRef.current) {
      return;
    }

    removingRef.current = true;
    setRemoving(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(agentPath(agentId));
    } catch (error: unknown) {
      removingRef.current = false;
      if (!mountedRef.current) {
        return;
      }
      setRemoving(false);
      if (isNotFound(error)) {
        if (selectedIdRef.current === agentId) {
          changeSelection(null);
        }
        await loadAgents();
        return;
      }
      await reportFailure(error);
      return;
    }

    removingRef.current = false;
    if (!mountedRef.current) {
      return;
    }
    setRemoving(false);

    if (result !== undefined && !result.succeeded) {
      await show(displayErrorsOptions(result));
      return;
    }
    if (result !== undefined && selectedIdRef.current === agentId) {
      changeSelection(null);
    }
    await loadAgents();
  };

  /** Closes the agent editor, reading the agents and the selected agent's lists again after a save. */
  const closeEditor = (saved: boolean): void => {
    setEditing(null);
    if (!saved) {
      return;
    }
    void loadAgents();
    const current = selectedIdRef.current;
    if (current !== null) {
      void loadDetails(current);
    }
  };

  const noSelection = selectedId === null;

  return (
    <section aria-labelledby={headingId} aria-busy={removing} style={removing ? BUSY_PAGE_STYLE : PAGE_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {msg('Agents')}
      </h2>
      <div style={BODY_STYLE}>
        <div style={AGENTS_COLUMN_STYLE}>
          <div style={AGENTS_BOX_STYLE}>
            <DataTable
              columns={AGENT_COLUMNS}
              rows={agents}
              rowKey={(agent) => agent.id}
              selectedKey={selectedId}
              onSelect={selectAgent}
              onDoubleClick={editListedAgent}
            />
          </div>
        </div>
        <div style={DETAILS_COLUMN_STYLE}>
          <div role="group" aria-labelledby={itemsLabelId} style={GROUP_STYLE}>
            <span id={itemsLabelId} style={LABEL_STYLE}>
              {ITEMS_CAPTION}
            </span>
            <div style={ITEMS_BOX_STYLE}>
              <DataTable columns={ITEM_COLUMNS} rows={asList(details?.items)} rowKey={(item) => item.id} />
            </div>
          </div>
          <div role="group" aria-labelledby={ratesLabelId} style={GROUP_STYLE}>
            <span id={ratesLabelId} style={LABEL_STYLE}>
              {COMMISSION_RATES_CAPTION}
            </span>
            <div style={RATES_BOX_STYLE}>
              <DataTable
                columns={RATE_COLUMNS}
                rows={asList(details?.commissionRates)}
                rowKey={(rate) => rate.commissionRateId}
              />
            </div>
          </div>
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton onClick={addAgent}>{ADD_CAPTION}</DialogButton>
        <DialogButton aria-disabled={noSelection || removing} onClick={editAgent}>
          {EDIT_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={noSelection || removing} onClick={() => void removeAgent()}>
          {REMOVE_CAPTION}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {messageBox}
      {editing !== null && <EditAgentDialog agentId={editing.agentId} onClose={closeEditor} />}
    </section>
  );
}

