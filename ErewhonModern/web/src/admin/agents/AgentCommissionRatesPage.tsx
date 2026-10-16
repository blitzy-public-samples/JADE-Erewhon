import { useCallback, useEffect, useId, useReducer, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, UnauthorizedError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  AgentCommissionRatesResponse,
  AgentProjection,
  CommissionRateAgents,
  EntityChangedMessage,
  EntityTypeName,
  ItemCategoryRow,
  OperationResult,
  ReplaceRateAgentsRequest,
} from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { DataTableColumn } from '../../components/DataTable';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { decimalString } from '../../format';
import { msg } from '../../messages/messages';

const CATEGORIES_PATH = '/api/app/admin/item-categories';
const AGENTS_PATH = '/api/app/admin/agents';
const AGENT_COMMISSION_RATES_PATH = '/api/app/admin/agent-commission-rates';

const CATEGORY_CAPTION = 'Sale Item Category';
const RATES_CAPTION = 'Commission Rates';
const USING_CAPTION = 'Agents using this Rate';
const NOT_USING_CAPTION = 'Agents not using this Rate';
const ADD_CAPTION = '<';
const ADD_ALL_CAPTION = '<<';
const REMOVE_CAPTION = '>';
const REMOVE_ALL_CAPTION = '>>';
const ADD_HELP = 'Add the selected Agent to the list.';
const ADD_ALL_HELP = 'Add all Agents to the list.';
const REMOVE_HELP = 'Remove the selected Agent from the list.';
const REMOVE_ALL_HELP = 'Remove all Agents from the list.';
const SAVE_CAPTION = 'Save';
const UNDO_CAPTION = 'Undo';
const CLOSE_CAPTION = 'Close';

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['ItemCategory', 'CommissionRate', 'Agent'];

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const BUTTON_ACTIVE_BORDER_COLOR = 'rgb(0, 80, 160)';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.75rem',
  boxSizing: 'border-box',
  maxInlineSize: '64rem',
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
  gap: '0.75rem',
};

const CATEGORY_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.375rem',
  flex: '0 1 13rem',
  minInlineSize: '10rem',
};

const USING_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: '0.5rem',
  flex: '1 1 22rem',
  minInlineSize: 0,
};

const NOT_USING_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flex: '1 1 19rem',
  minInlineSize: 0,
};

const LABEL_STYLE: CSSProperties = {
  overflowWrap: 'break-word',
};

const SELECT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  paddingBlock: '0.125rem',
  font: 'inherit',
};

const LIST_BOX_STYLE: CSSProperties = {
  display: 'grid',
  alignContent: 'start',
  boxSizing: 'border-box',
  overflow: 'auto',
  background: 'white',
  border: LIST_BORDER,
};

const RATES_BOX_STYLE: CSSProperties = {
  ...LIST_BOX_STYLE,
  blockSize: '14.3125rem',
};

const AGENTS_BOX_STYLE: CSSProperties = {
  ...LIST_BOX_STYLE,
  flex: '1 1 auto',
  minInlineSize: 0,
  blockSize: '17.5rem',
};

const MOVE_BUTTONS_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '1.25rem',
  flex: '0 0 auto',
  paddingBlockStart: '4.125rem',
};

const MOVE_BUTTON_GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.3125rem',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
};

const MOVE_BUTTON_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: '2rem',
  minBlockSize: '1.75rem',
  paddingBlock: 0,
  paddingInline: '0.25rem',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: 'rgb(128, 128, 128)',
  borderRadius: '2px',
  backgroundColor: 'rgb(240, 240, 240)',
  color: 'black',
  font: 'inherit',
  cursor: 'pointer',
};

const MOVE_BUTTON_HOVER_STYLE: CSSProperties = {
  ...MOVE_BUTTON_STYLE,
  borderColor: BUTTON_ACTIVE_BORDER_COLOR,
  backgroundColor: 'rgb(229, 241, 251)',
};

const MOVE_BUTTON_PRESSED_STYLE: CSSProperties = {
  ...MOVE_BUTTON_STYLE,
  borderColor: BUTTON_ACTIVE_BORDER_COLOR,
  backgroundColor: 'rgb(204, 228, 247)',
};

const MOVE_BUTTON_DISABLED_STYLE: CSSProperties = {
  ...MOVE_BUTTON_STYLE,
  borderColor: 'rgb(191, 191, 191)',
  color: 'rgb(109, 109, 109)',
  cursor: 'default',
};

const MOVE_BUTTON_FOCUS_STYLE: CSSProperties = {
  outline: FOCUS_OUTLINE,
  outlineOffset: '1px',
};

/** Enabled state of the four buttons that move agents between the two agent lists. */
interface MoveButtons {
  /** "<": moves the selected agent not using the rate into the using list. */
  add: boolean;
  /** "<<": moves every agent not using the rate into the using list. */
  addAll: boolean;
  /** ">": moves the selected agent using the rate into the not-using list. */
  remove: boolean;
  /** ">>": moves every agent using the rate into the not-using list. */
  removeAll: boolean;
}

/** Everything the page shows and edits apart from the category and agent lookups. */
interface PageState {
  /** Row id of the chosen item category; null while the empty entry is chosen. */
  categoryId: number | null;
  /** Rates of the chosen category with their agents, and every agent of the company; null while none are loaded. */
  ratesData: AgentCommissionRatesResponse | null;
  /** Row id of the selected commission rate; null while none is selected. */
  rateId: number | null;
  /** Row ids of the agents listed as using the rate, in list order. */
  using: readonly number[];
  /** Row ids of the agents listed as not using the rate, in list order. */
  notUsing: readonly number[];
  /** Row id of the selected agent of the using list; null while none is selected. */
  usingSel: number | null;
  /** Row id of the selected agent of the not-using list; null while none is selected. */
  notUsingSel: number | null;
  /** Row id of the commission rate a save updates; null until a rate has been selected. */
  taRateId: number | null;
  /** Row ids of the agents a save gives the rate. */
  taAgentIds: readonly number[];
  saveEnabled: boolean;
  undoEnabled: boolean;
  buttons: MoveButtons;
}

type PageAction =
  | { type: 'categoryChosen'; categoryId: number | null }
  | { type: 'categoriesLoaded'; categoryIds: readonly number[] }
  | { type: 'ratesLoaded'; categoryId: number; data: AgentCommissionRatesResponse | null }
  | { type: 'rateSelected'; rateId: number }
  | { type: 'usingSelected'; agentId: number }
  | { type: 'notUsingSelected'; agentId: number }
  | { type: 'addSelected' }
  | { type: 'addAll' }
  | { type: 'removeSelected' }
  | { type: 'removeAll' }
  | { type: 'undoneWithoutRate' }
  | {
      type: 'undone';
      categoryId: number;
      rateId: number;
      data: AgentCommissionRatesResponse | null;
      latest: boolean;
    }
  | { type: 'saved' };

/** Outcome of one read of GET /api/app/admin/agent-commission-rates. */
type RatesAnswer =
  | {
      status: 'answered';
      /** The response, or null when the category no longer exists. */
      data: AgentCommissionRatesResponse | null;
      /** True when no later read of the rates had been started when this one answered. */
      latest: boolean;
    }
  | { status: 'failed'; error: unknown };

const INITIAL_STATE: PageState = {
  categoryId: null,
  ratesData: null,
  rateId: null,
  using: [],
  notUsing: [],
  usingSel: null,
  notUsingSel: null,
  taRateId: null,
  taAgentIds: [],
  saveEnabled: false,
  undoEnabled: false,
  buttons: { add: false, addAll: false, remove: false, removeAll: false },
};

/** Returns `state` with each move button enabled as the two agent lists and their selections allow. */
function toggleAddRemoveButtons(state: PageState): PageState {
  return {
    ...state,
    buttons: {
      add: state.notUsingSel !== null,
      addAll: state.notUsing.length > 0,
      remove: state.usingSel !== null,
      removeAll: state.using.length > 0,
    },
  };
}

/** Returns `state` with both agent lists emptied and their selections cleared. */
function clearAgentLists(state: PageState): PageState {
  return { ...state, using: [], notUsing: [], usingSel: null, notUsingSel: null };
}

/** Returns the rate of `data` with row id `rateId`, or undefined when it has none. */
function findRate(data: AgentCommissionRatesResponse, rateId: number): CommissionRateAgents | undefined {
  return data.rates.find((rate) => rate.commissionRateId === rateId);
}

/**
 * Returns `state` with every agent of `data`, in its order, listed as using `rate` when the rate holds the agent and
 * as not using it otherwise, both selections cleared, and the agents using it and the rate as the ones a save writes.
 */
function loadAgents(state: PageState, data: AgentCommissionRatesResponse, rate: CommissionRateAgents): PageState {
  const holders = new Set(rate.agents.map((agent) => agent.id));
  const using: number[] = [];
  const notUsing: number[] = [];
  for (const agent of data.allAgents) {
    if (holders.has(agent.id)) {
      using.push(agent.id);
    } else {
      notUsing.push(agent.id);
    }
  }
  return {
    ...state,
    using,
    notUsing,
    usingSel: null,
    notUsingSel: null,
    taRateId: rate.commissionRateId,
    taAgentIds: [...using],
  };
}

/**
 * Returns `state` after moving each of `agentIds` that is in the source list, in the order given, to the end of the
 * other list: into the using list and the agents a save writes when `addToUsing` is true, otherwise out of both. Save
 * is enabled when an agent moved, the source list's selection is cleared and the move buttons are toggled.
 */
function moveAgents(state: PageState, agentIds: readonly number[], addToUsing: boolean): PageState {
  const using = [...state.using];
  const notUsing = [...state.notUsing];
  let taAgentIds = [...state.taAgentIds];
  let saveEnabled = state.saveEnabled;

  for (const agentId of agentIds) {
    const source = addToUsing ? notUsing : using;
    const target = addToUsing ? using : notUsing;
    const index = source.indexOf(agentId);
    if (index === -1) {
      continue;
    }
    target.push(agentId);
    source.splice(index, 1);
    if (addToUsing) {
      if (!taAgentIds.includes(agentId)) {
        taAgentIds.push(agentId);
      }
    } else {
      taAgentIds = taAgentIds.filter((id) => id !== agentId);
    }
    saveEnabled = true;
  }

  return toggleAddRemoveButtons({
    ...state,
    using,
    notUsing,
    taAgentIds,
    saveEnabled,
    usingSel: addToUsing ? state.usingSel : null,
    notUsingSel: addToUsing ? null : state.notUsingSel,
  });
}

/** Returns the page state after `action`. */
function pageReducer(state: PageState, action: PageAction): PageState {
  switch (action.type) {
    case 'categoryChosen':
      return toggleAddRemoveButtons(
        clearAgentLists({
          ...state,
          categoryId: action.categoryId,
          ratesData: null,
          rateId: null,
          undoEnabled: false,
        }),
      );

    case 'categoriesLoaded':
      if (state.categoryId === null || action.categoryIds.includes(state.categoryId)) {
        return state;
      }
      return pageReducer(state, { type: 'categoryChosen', categoryId: null });

    case 'ratesLoaded': {
      if (state.categoryId !== action.categoryId) {
        return state;
      }
      const next: PageState = { ...state, ratesData: action.data };
      if (state.rateId !== null && (action.data === null || findRate(action.data, state.rateId) === undefined)) {
        return toggleAddRemoveButtons(clearAgentLists({ ...next, rateId: null }));
      }
      return next;
    }

    case 'rateSelected': {
      const data = state.ratesData;
      const rate = data === null ? undefined : findRate(data, action.rateId);
      if (data === null || rate === undefined) {
        return state;
      }
      return toggleAddRemoveButtons({
        ...loadAgents(state, data, rate),
        rateId: rate.commissionRateId,
        undoEnabled: false,
      });
    }

    case 'usingSelected':
      if (!state.using.includes(action.agentId)) {
        return state;
      }
      return toggleAddRemoveButtons({ ...state, usingSel: action.agentId });

    case 'notUsingSelected':
      if (!state.notUsing.includes(action.agentId)) {
        return state;
      }
      return toggleAddRemoveButtons({ ...state, notUsingSel: action.agentId });

    case 'addSelected':
      if (state.notUsingSel === null) {
        return state;
      }
      return { ...moveAgents(state, [state.notUsingSel], true), undoEnabled: true };

    case 'addAll':
      return { ...moveAgents(state, state.notUsing, true), undoEnabled: true };

    case 'removeSelected':
      if (state.usingSel === null) {
        return state;
      }
      return { ...moveAgents(state, [state.usingSel], false), undoEnabled: true };

    case 'removeAll':
      return { ...moveAgents(state, state.using, false), undoEnabled: true };

    case 'undoneWithoutRate':
      return { ...clearAgentLists(state), undoEnabled: false };

    case 'undone': {
      if (state.categoryId !== action.categoryId || state.rateId !== action.rateId) {
        return state;
      }
      const base: PageState = action.latest ? { ...state, ratesData: action.data } : state;
      const rate = action.data === null ? undefined : findRate(action.data, action.rateId);
      if (action.data === null || rate === undefined) {
        return { ...clearAgentLists({ ...base, rateId: null }), undoEnabled: false };
      }
      return { ...loadAgents(base, action.data, rate), saveEnabled: false, undoEnabled: false };
    }

    case 'saved':
      return { ...state, saveEnabled: false, undoEnabled: false };
  }
}

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns the style of a {@link MoveButton}: disabled, pressed, hovered or base, with the focus outline when shown. */
function moveButtonStyle(disabled: boolean, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = MOVE_BUTTON_STYLE;
  if (disabled) {
    style = MOVE_BUTTON_DISABLED_STYLE;
  } else if (pressed) {
    style = MOVE_BUTTON_PRESSED_STYLE;
  } else if (hovered) {
    style = MOVE_BUTTON_HOVER_STYLE;
  }
  return focusVisible ? { ...style, ...MOVE_BUTTON_FOCUS_STYLE } : style;
}

/** Properties of {@link MoveButton}. */
interface MoveButtonProps {
  /** Caption of the button, which is also its `aria-label`. */
  caption: string;
  /** Tooltip of the button. */
  help: string;
  /** When false, the button keeps its tab stop, ignores clicks, is `aria-disabled` and shows its disabled look. */
  enabled: boolean;
  /** Called when the button is clicked while enabled. */
  onClick: () => void;
}

/**
 * Small push button of type `button` between the agent lists, with the colours, hover, pressed and `:focus-visible`
 * states of the dialog buttons.
 */
function MoveButton({ caption, help, enabled, onClick }: MoveButtonProps): ReactElement {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const updateFocusVisible = (event: FocusEvent<HTMLButtonElement> | KeyboardEvent<HTMLButtonElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return (
    <button
      type="button"
      aria-label={caption}
      title={help}
      aria-disabled={enabled ? undefined : 'true'}
      style={moveButtonStyle(!enabled, pressed, hovered, focusVisible)}
      onClick={() => {
        if (enabled) {
          onClick();
        }
      }}
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
      {caption}
    </button>
  );
}

/** Returns `value`, or the empty string for null or undefined. */
function text(value: string | null | undefined): string {
  return value ?? '';
}

/**
 * The "Agent Commission Rates" page: the item category chosen in "Sale Item Category", its commission rates, and the
 * agents using and not using the selected rate, with the buttons that move agents between those two lists and save
 * the result as the agents holding the rate.
 *
 * - On mount it reads the item categories (GET /api/app/admin/item-categories) and the agents with their addresses
 *   (GET /api/app/admin/agents). The category list starts with an empty entry, which is chosen.
 * - Choosing a category empties both agent lists, clears the rate selection, disables Undo and reads the category's
 *   rates (GET /api/app/admin/agent-commission-rates?category={id}); choosing the empty entry empties the rates. Each
 *   rate reads `<category name> @ <percentage with 2 decimals>`.
 * - Selecting a rate lists every agent of the company in name order, as using the rate when it holds it and as not
 *   using it otherwise, each as `<name>, <street>, <city>, <country>`, makes those using it and the rate the ones a
 *   save writes, and disables Undo. Save keeps its state through category and rate changes.
 * - "<" and ">" move the selected agent, and "<<" and ">>" every listed agent in list order, to the end of the other
 *   list; double-clicking an agent, or pressing Enter on it, moves it as "<" or ">" does. Each move enables Save and
 *   Undo. "<" is enabled while an agent not using the rate is selected, "<<" while that list has agents, ">" while an
 *   agent using the rate is selected and ">>" while that list has agents; these states are set when a category or
 *   rate is chosen, an agent is selected or moved, and the selected rate disappears, and are kept by Undo.
 * - Undo empties both lists when no rate is selected; otherwise it reads the rates again, lists the agents of the
 *   selected rate as now stored and disables Save. Undo is then disabled.
 * - Save sends PUT /api/app/admin/agent-commission-rates with the rate and agents to write. A failed save shows its
 *   errors joined with CR LF in an OK box titled `Error during save`; a successful one disables Save and Undo and
 *   reads the rates again, leaving both agent lists as they are.
 * - Close calls `onClose`.
 * - Item category changes reload the categories, agent changes reload the agents, and item category, commission
 *   rate and agent changes read the chosen category's rates again; when the selected rate is no longer among them,
 *   the rate selection and both agent lists are cleared. A chosen category that no longer exists is unchosen.
 * - While a save or Undo request is outstanding the page is `aria-busy` and ignores every control except Close. An
 *   error outcome is shown in its message box, an HTTP 401 shows nothing, and any other failure shows its message
 *   titled `Error`.
 */
export function AgentCommissionRatesPage(props: { onClose?: () => void } = {}): JSX.Element {
  const { onClose } = props;
  const { element: messageBox, show, showError } = useMessageBox();
  const [state, dispatch] = useReducer(pageReducer, INITIAL_STATE);
  const [categories, setCategories] = useState<readonly ItemCategoryRow[]>([]);
  const [agentsById, setAgentsById] = useState<ReadonlyMap<number, AgentProjection>>(() => new Map());
  const [busy, setBusy] = useState(false);
  const busyRef = useRef(false);
  const mountedRef = useRef(false);
  const categoryIdRef = useRef<number | null>(null);
  const categoriesSeqRef = useRef(0);
  const agentsSeqRef = useRef(0);
  const ratesSeqRef = useRef(0);
  const headingId = useId();
  const categorySelectId = useId();

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    categoryIdRef.current = state.categoryId;
  }, [state.categoryId]);

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

  const loadCategories = useCallback(async (): Promise<void> => {
    categoriesSeqRef.current += 1;
    const seq = categoriesSeqRef.current;
    try {
      const rows = await api.get<ItemCategoryRow[] | undefined>(CATEGORIES_PATH);
      if (!mountedRef.current || seq !== categoriesSeqRef.current) {
        return;
      }
      const list = Array.isArray(rows) ? rows : [];
      setCategories(list);
      dispatch({ type: 'categoriesLoaded', categoryIds: list.map((category) => category.id) });
    } catch (error: unknown) {
      await reportFailure(error);
    }
  }, [reportFailure]);

  const loadAgentLookup = useCallback(async (): Promise<void> => {
    agentsSeqRef.current += 1;
    const seq = agentsSeqRef.current;
    try {
      const rows = await api.get<AgentProjection[] | undefined>(AGENTS_PATH);
      if (!mountedRef.current || seq !== agentsSeqRef.current) {
        return;
      }
      const list = Array.isArray(rows) ? rows : [];
      setAgentsById(new Map(list.map((agent) => [agent.id, agent])));
    } catch (error: unknown) {
      await reportFailure(error);
    }
  }, [reportFailure]);

  const requestRates = useCallback(async (categoryId: number): Promise<RatesAnswer> => {
    ratesSeqRef.current += 1;
    const seq = ratesSeqRef.current;
    try {
      const data = await api.get<AgentCommissionRatesResponse | undefined>(
        `${AGENT_COMMISSION_RATES_PATH}?category=${encodeURIComponent(String(categoryId))}`,
      );
      return { status: 'answered', data: data ?? null, latest: seq === ratesSeqRef.current };
    } catch (error: unknown) {
      if (error instanceof ApiError && error.status === 404) {
        return { status: 'answered', data: null, latest: seq === ratesSeqRef.current };
      }
      return { status: 'failed', error };
    }
  }, []);

  const refreshRates = useCallback(
    async (categoryId: number): Promise<void> => {
      const answer = await requestRates(categoryId);
      if (!mountedRef.current) {
        return;
      }
      if (answer.status === 'failed') {
        await reportFailure(answer.error);
        return;
      }
      if (answer.latest) {
        dispatch({ type: 'ratesLoaded', categoryId, data: answer.data });
      }
    },
    [requestRates, reportFailure],
  );

  useEffect(() => {
    void loadCategories();
    void loadAgentLookup();
  }, [loadCategories, loadAgentLookup]);

  useEffect(
    () =>
      onEntityChanged(WATCHED_ENTITY_TYPES, (message: EntityChangedMessage) => {
        const reloads: Promise<void>[] = [];
        if (message.entityType === 'ItemCategory') {
          reloads.push(loadCategories());
        }
        if (message.entityType === 'Agent') {
          reloads.push(loadAgentLookup());
        }
        const categoryId = categoryIdRef.current;
        if (categoryId !== null) {
          reloads.push(refreshRates(categoryId));
        }
        return Promise.all(reloads);
      }),
    [loadCategories, loadAgentLookup, refreshRates],
  );

  const setBusyState = (value: boolean): void => {
    busyRef.current = value;
    if (mountedRef.current) {
      setBusy(value);
    }
  };

  /** Dispatches `action` unless a save or Undo request is outstanding. */
  const act = (action: PageAction): void => {
    if (!busyRef.current) {
      dispatch(action);
    }
  };

  const chooseCategory = (value: string): void => {
    if (busyRef.current) {
      return;
    }
    const categoryId = value === '' ? null : Number(value);
    categoryIdRef.current = categoryId;
    dispatch({ type: 'categoryChosen', categoryId });
    if (categoryId !== null) {
      void refreshRates(categoryId);
    }
  };

  const undo = async (): Promise<void> => {
    if (busyRef.current || !state.undoEnabled) {
      return;
    }
    const { categoryId, rateId } = state;
    if (categoryId === null || rateId === null) {
      dispatch({ type: 'undoneWithoutRate' });
      return;
    }

    setBusyState(true);
    try {
      const answer = await requestRates(categoryId);
      if (!mountedRef.current) {
        return;
      }
      if (answer.status === 'failed') {
        await reportFailure(answer.error);
        return;
      }
      dispatch({ type: 'undone', categoryId, rateId, data: answer.data, latest: answer.latest });
    } finally {
      setBusyState(false);
    }
  };

  const save = async (): Promise<void> => {
    if (busyRef.current || !state.saveEnabled || state.taRateId === null) {
      return;
    }
    const { categoryId, taRateId, taAgentIds } = state;

    setBusyState(true);
    try {
      const result = await api.put<OperationResult>(AGENT_COMMISSION_RATES_PATH, {
        commissionRateId: taRateId,
        agentIds: [...taAgentIds],
      } satisfies ReplaceRateAgentsRequest);
      if (!mountedRef.current) {
        return;
      }
      if (!result.succeeded) {
        await show(displayErrorsOptions(result));
        return;
      }
      dispatch({ type: 'saved' });
      if (categoryId !== null) {
        await refreshRates(categoryId);
      }
    } catch (error: unknown) {
      await reportFailure(error);
    } finally {
      setBusyState(false);
    }
  };

  const ratesData = state.ratesData;
  const categoryName = text(categories.find((category) => category.id === ratesData?.itemCategoryId)?.name);

  const agentText = (agentId: number): string => {
    const agent = agentsById.get(agentId);
    if (agent === undefined) {
      return text(ratesData?.allAgents.find((entry) => entry.id === agentId)?.name);
    }
    return `${text(agent.name)}, ${text(agent.street)}, ${text(agent.city)}, ${text(agent.country)}`;
  };

  const rateColumns: DataTableColumn<CommissionRateAgents>[] = [
    {
      key: 'rate',
      header: RATES_CAPTION,
      render: (rate) => `${categoryName} @ ${decimalString(rate.percentage)}`,
    },
  ];

  const usingColumns: DataTableColumn<number>[] = [{ key: 'agent', header: USING_CAPTION, render: agentText }];
  const notUsingColumns: DataTableColumn<number>[] = [{ key: 'agent', header: NOT_USING_CAPTION, render: agentText }];

  return (
    <section aria-labelledby={headingId} aria-busy={busy} style={busy ? BUSY_SECTION_STYLE : SECTION_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {msg('AgentCommissionRates')}
      </h2>
      <div style={BODY_STYLE}>
        <div style={CATEGORY_COLUMN_STYLE}>
          <label htmlFor={categorySelectId} style={LABEL_STYLE}>
            {CATEGORY_CAPTION}
          </label>
          <select
            id={categorySelectId}
            value={state.categoryId === null ? '' : String(state.categoryId)}
            onChange={(event) => chooseCategory(event.target.value)}
            style={SELECT_STYLE}
          >
            <option value="" />
            {categories.map((category) => (
              <option key={category.id} value={String(category.id)}>
                {category.name}
              </option>
            ))}
          </select>
          <div style={RATES_BOX_STYLE}>
            <DataTable
              columns={rateColumns}
              rows={ratesData?.rates ?? []}
              rowKey={(rate) => rate.commissionRateId}
              selectedKey={state.rateId}
              onSelect={(rate) => act({ type: 'rateSelected', rateId: rate.commissionRateId })}
            />
          </div>
        </div>
        <div style={USING_COLUMN_STYLE}>
          <div style={AGENTS_BOX_STYLE}>
            <DataTable
              columns={usingColumns}
              rows={[...state.using]}
              rowKey={(agentId) => agentId}
              selectedKey={state.usingSel}
              onSelect={(agentId) => act({ type: 'usingSelected', agentId })}
              onDoubleClick={() => act({ type: 'removeSelected' })}
            />
          </div>
          <div style={MOVE_BUTTONS_STYLE}>
            <div style={MOVE_BUTTON_GROUP_STYLE}>
              <MoveButton
                caption={ADD_CAPTION}
                help={ADD_HELP}
                enabled={state.buttons.add && !busy}
                onClick={() => act({ type: 'addSelected' })}
              />
              <MoveButton
                caption={ADD_ALL_CAPTION}
                help={ADD_ALL_HELP}
                enabled={state.buttons.addAll && !busy}
                onClick={() => act({ type: 'addAll' })}
              />
            </div>
            <div style={MOVE_BUTTON_GROUP_STYLE}>
              <MoveButton
                caption={REMOVE_CAPTION}
                help={REMOVE_HELP}
                enabled={state.buttons.remove && !busy}
                onClick={() => act({ type: 'removeSelected' })}
              />
              <MoveButton
                caption={REMOVE_ALL_CAPTION}
                help={REMOVE_ALL_HELP}
                enabled={state.buttons.removeAll && !busy}
                onClick={() => act({ type: 'removeAll' })}
              />
            </div>
          </div>
        </div>
        <div style={NOT_USING_COLUMN_STYLE}>
          <div style={AGENTS_BOX_STYLE}>
            <DataTable
              columns={notUsingColumns}
              rows={[...state.notUsing]}
              rowKey={(agentId) => agentId}
              selectedKey={state.notUsingSel}
              onSelect={(agentId) => act({ type: 'notUsingSelected', agentId })}
              onDoubleClick={() => act({ type: 'addSelected' })}
            />
          </div>
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton aria-disabled={!state.saveEnabled || busy} onClick={() => void save()}>
          {SAVE_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!state.undoEnabled || busy} onClick={() => void undo()}>
          {UNDO_CAPTION}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {messageBox}
    </section>
  );
}
