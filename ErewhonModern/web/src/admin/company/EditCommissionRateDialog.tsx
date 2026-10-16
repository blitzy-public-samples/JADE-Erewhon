import { useEffect, useId, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement } from 'react';
import { ApiError, api } from '../../api/client';
import type { CommissionRateRequest, CommissionRateRow, Id, ItemCategoryRow, OperationResult } from '../../api/types';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

const COMMISSION_RATES_PATH = '/api/app/admin/commission-rates';
const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';

const DIALOG_CAPTION = 'Commission Rate';
const ITEM_CATEGORY_CAPTION = 'Item Category';
const PERCENTAGE_CAPTION = 'Percentage';
const PERCENT_CHAR_CAPTION = '%';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const FOCUS_ITEM_CATEGORY = 'FOCUS: ItemCategory';
const FOCUS_PERCENTAGE = 'FOCUS: Percentage';

const PERCENTAGE_MAX_LENGTH = 6;
const PERCENTAGE_SCALE = 2;
const PERCENTAGE_READ_LIMIT = 1e15;

const FOCUSABLE_SELECTOR =
  'select:not(:disabled), input:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const LABEL_GAP = '0.1875rem';

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
  maxInlineSize: '21.625rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '0.625rem',
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
  display: 'grid',
  gridTemplateColumns: 'minmax(6rem, max-content) minmax(0, 1fr)',
  alignItems: 'center',
  columnGap: LABEL_GAP,
  rowGap: '0.25rem',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const CONTROL_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  display: 'block',
  minInlineSize: 0,
  minBlockSize: '1.3125rem',
  margin: 0,
  paddingBlock: 0,
  paddingInline: '0.25rem',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: INPUT_BORDER_COLOR,
  borderRadius: 0,
  backgroundColor: '#FFFFFF',
  color: '#000000',
  font: 'inherit',
};

const SELECT_STYLE: CSSProperties = {
  ...CONTROL_STYLE,
  inlineSize: '100%',
};

const PERCENTAGE_INPUT_STYLE: CSSProperties = {
  ...CONTROL_STYLE,
  flex: '0 0 auto',
  inlineSize: '4rem',
};

const CONTROL_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

const PERCENTAGE_ROW_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: LABEL_GAP,
  minInlineSize: 0,
};

const PERCENT_CHAR_STYLE: CSSProperties = {
  flex: '0 0 auto',
  inlineSize: '1.5rem',
  textAlign: 'end',
};

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

/** Properties of {@link EditCommissionRateDialog}. */
export interface EditCommissionRateDialogProps {
  /** Internal id of the commission rate edited; null, undefined or not a positive integer adds a new rate. */
  commissionRateId?: number | null;
  /** Internal id of the item category selected when a new rate is added and the category is listed. */
  itemCategoryId?: number | null;
  /** Called once when the dialog is done, with true when a save of this dialog succeeded. */
  onClose: (saved: boolean) => void;
}

/** How the categories and the edited rate were read for the current row. */
type LoadStatus = 'loading' | 'ready' | 'failed';

/** Action that sends the dialog's fields: OK, the Ctrl+S shortcut, or Cancel and Escape. */
type SaveAction = 'ok' | 'shortcut' | 'cancel';

/** Result of the last request a save of the dialog sent; null until one answered. */
interface SaveOutcome {
  result: OperationResult | null;
}

/** Pointer and keyboard-focus state of one input or select, with the handlers that keep it. */
interface ControlInteraction<T extends HTMLElement> {
  hovered: boolean;
  pressed: boolean;
  focusVisible: boolean;
  handlers: {
    onMouseEnter: () => void;
    onMouseLeave: () => void;
    onMouseDown: (event: MouseEvent<T>) => void;
    onMouseUp: () => void;
    onFocus: (event: FocusEvent<T>) => void;
    onKeyUp: (event: KeyboardEvent<T>) => void;
    onBlur: () => void;
  };
}

/** Returns true when `id` is a positive safe integer, the id of an existing row. */
function isRowId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the path of GET and PUT /api/app/admin/commission-rates/{id}. */
function ratePath(rateId: Id): string {
  return `${COMMISSION_RATES_PATH}/${encodeURIComponent(String(rateId))}`;
}

/** Returns the option value of the category `categoryId` when `rows` lists it, otherwise the empty option's value. */
function categoryOptionValue(rows: readonly ItemCategoryRow[], categoryId: number | null | undefined): string {
  return rows.some((row) => row.id === categoryId) ? String(categoryId) : '';
}

/**
 * Returns the percentage text as a decimal number: the trimmed text read with `Number`, or 0 when the trimmed text is
 * empty, does not read as a finite number, or reads as a number whose magnitude is {@link PERCENTAGE_READ_LIMIT} or
 * more.
 */
function textAsDecimal(text: string): number {
  const trimmed = text.trim();
  if (trimmed === '') {
    return 0;
  }
  const value = Number(trimmed);
  return Number.isFinite(value) && Math.abs(value) < PERCENTAGE_READ_LIMIT ? value : 0;
}

/** Returns true for Ctrl+S, with or without Shift, pressed without Alt or Meta. */
function isSaveShortcut(event: KeyboardEvent<HTMLElement>): boolean {
  return event.ctrlKey && !event.altKey && !event.metaKey && event.key.toLowerCase() === 's';
}

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Keeps the focus where it is when the mouse is pressed on the backdrop itself. */
function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

/**
 * Returns the hover, pressed and `:focus-visible` state of one input or select and the handlers that keep it: the
 * pointer entering and leaving, the primary mouse button pressed and released, and focus, blur and every key release,
 * on which `:focus-visible` is read again.
 */
function useControlInteraction<T extends HTMLElement>(): ControlInteraction<T> {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const updateFocusVisible = (event: FocusEvent<T> | KeyboardEvent<T>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return {
    hovered,
    pressed,
    focusVisible,
    handlers: {
      onMouseEnter: () => setHovered(true),
      onMouseLeave: () => {
        setHovered(false);
        setPressed(false);
      },
      onMouseDown: (event: MouseEvent<T>) => {
        if (event.button === 0) {
          setPressed(true);
        }
      },
      onMouseUp: () => setPressed(false),
      onFocus: updateFocusVisible,
      onKeyUp: updateFocusVisible,
      onBlur: () => setFocusVisible(false),
    },
  };
}

/**
 * Returns `base` with the pressed border (rgb(0, 80, 160)) or, failing that, the hovered border (rgb(128, 128, 128)),
 * and with the 2px rgb(0, 80, 160) outline when the control is focused with `:focus-visible`.
 */
function controlStyle<T extends HTMLElement>(base: CSSProperties, interaction: ControlInteraction<T>): CSSProperties {
  let style = base;
  if (interaction.pressed) {
    style = { ...style, borderColor: INPUT_PRESSED_BORDER_COLOR };
  } else if (interaction.hovered) {
    style = { ...style, borderColor: INPUT_HOVER_BORDER_COLOR };
  }
  return interaction.focusVisible ? { ...style, ...CONTROL_FOCUS_STYLE } : style;
}

/**
 * The modal "Add Commission Rate" or "Edit Commission Rate" dialog of the administration area: the "Item Category"
 * drop-down list, the "Percentage" box followed by "%", a rule, and the OK and Cancel buttons.
 *
 * - The dialog is a `role="dialog"` with `aria-modal="true"` named `$Add` or `$Edit` and " Commission Rate", rendered
 *   at once. On mount, whenever the edited row changes and after a Ctrl+S save, it reads the item categories (GET
 *   /api/app/admin/item-categories, in name order) and, for an existing rate, the rate (GET
 *   /api/app/admin/commission-rates/{id}). Until both answer the panel has `aria-busy="true"` and the progress cursor,
 *   changes are ignored and OK does nothing. The list holds an empty first entry and one entry per category; an
 *   existing rate selects its category and shows its percentage with two decimals, and a new rate selects the
 *   `itemCategoryId` category when it is listed and shows an empty percentage. A 404 calls `onClose`; an error outcome
 *   is shown in a message box, after which only Cancel and Escape work; a 401 shows nothing.
 * - The soft edit lock is `useEditLock('CommissionRate', id)`: its prompt for a row already being edited, the lock on
 *   the first change, and the unlock after a save and on close.
 * - OK, and Enter pressed outside the buttons, send the selected category id (null for the empty entry), the
 *   percentage text read as a decimal (0 when empty, not a number, or 1e15 or more in magnitude) and the rate's agent
 *   ids as read (none for a new rate): POST /api/app/admin/commission-rates for a new rate, PUT
 *   /api/app/admin/commission-rates/{id} otherwise.
 *   A PUT answered 404 reads the rate again and is sent once more with the agent ids then read. A succeeded result
 *   calls `onClose(true)`; a failed result focuses the category list for `FOCUS: ItemCategory` and the percentage box
 *   for `FOCUS: Percentage`, then shows its errors joined with CR LF titled `$ErrorDuringSave`, which hands the focus
 *   back to that field when it closes, and keeps the dialog open. A rate that no longer exists calls `onClose`.
 * - Ctrl+S saves the same way without closing; after a succeeded save of a new rate the dialog edits the created rate,
 *   and every succeeded save reads the categories and the rate again.
 * - Cancel and Escape close through the lock's outstanding-changes question: Cancel keeps the dialog open, Yes saves
 *   as OK does and No discards the changes. `onClose` receives true when a save of this dialog succeeded.
 * - While a save or close is outstanding, OK, Cancel, Enter, Escape and Ctrl+S do nothing, and the buttons have
 *   `aria-disabled="true"`. Failures other than a 404, an error outcome or a 401 reject unhandled.
 *
 * The category list is focused on mount, Tab and Shift+Tab wrap within the dialog, and the element focused before the
 * dialog opened gets the focus back when the dialog unmounts and nothing else has taken it.
 */
export function EditCommissionRateDialog({
  commissionRateId = null,
  itemCategoryId = null,
  onClose,
}: EditCommissionRateDialogProps): ReactElement {
  const initialRateId = isRowId(commissionRateId) ? commissionRateId : null;
  const caption = `${msg(initialRateId === null ? 'Add' : 'Edit')} ${DIALOG_CAPTION}`;

  const [rateId, setRateId] = useState<Id | null>(initialRateId);
  const [reloadToken, setReloadToken] = useState(0);
  const [status, setStatus] = useState<LoadStatus>('loading');
  const [categories, setCategories] = useState<readonly ItemCategoryRow[]>([]);
  const [categoryValue, setCategoryValue] = useState('');
  const [percentageText, setPercentageText] = useState('');
  const [busy, setBusy] = useState(false);

  const lock = useEditLock('CommissionRate', rateId);
  const { element: messageBox, showError } = useMessageBox();
  const categoryInteraction = useControlInteraction<HTMLSelectElement>();
  const percentageInteraction = useControlInteraction<HTMLInputElement>();

  const categoryId = useId();
  const percentageId = useId();
  const percentCharId = useId();

  const onCloseRef = useRef(onClose);
  const presetCategoryIdRef = useRef(itemCategoryId);
  const agentIdsRef = useRef<Id[]>([]);
  const savedRef = useRef(false);
  const busyRef = useRef(false);
  const mountedRef = useRef(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const categoryRef = useRef<HTMLSelectElement>(null);
  const percentageRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    presetCategoryIdRef.current = itemCategoryId;
  }, [itemCategoryId]);

  useEffect(() => {
    setRateId(initialRateId);
  }, [initialRateId]);

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    categoryRef.current?.focus({ preventScroll: true });

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
    setStatus('loading');

    const categoriesRequest = api.get<ItemCategoryRow[]>(ITEM_CATEGORIES_PATH);
    const rateRequest: Promise<CommissionRateRow | null> =
      rateId === null ? Promise.resolve(null) : api.get<CommissionRateRow>(ratePath(rateId));

    Promise.all([categoriesRequest, rateRequest]).then(
      ([categoryRows, rate]) => {
        if (!active) {
          return;
        }
        const rows = Array.isArray(categoryRows) ? categoryRows : [];
        setCategories(rows);
        if (rate) {
          agentIdsRef.current = rate.agents.map((agent) => agent.id);
          setCategoryValue(categoryOptionValue(rows, rate.itemCategoryId));
          setPercentageText(decimalString(rate.percentage, PERCENTAGE_SCALE));
        } else {
          agentIdsRef.current = [];
          setCategoryValue(categoryOptionValue(rows, presetCategoryIdRef.current));
          setPercentageText('');
        }
        setStatus('ready');
      },
      (error: unknown) => {
        if (!active) {
          return undefined;
        }
        if (isNotFound(error)) {
          onCloseRef.current(savedRef.current);
          return undefined;
        }
        setStatus('failed');
        return showError(error);
      },
    );

    return () => {
      active = false;
    };
  }, [rateId, reloadToken, showError]);

  const editable = status === 'ready' && !busy;

  const setBusyState = (next: boolean): void => {
    busyRef.current = next;
    setBusy(next);
  };

  const handleCategoryChange = (event: ChangeEvent<HTMLSelectElement>): void => {
    if (!editable || busyRef.current) {
      return;
    }
    setCategoryValue(event.currentTarget.value);
    lock.markChanged();
  };

  const handlePercentageChange = (event: ChangeEvent<HTMLInputElement>): void => {
    if (!editable || busyRef.current) {
      return;
    }
    setPercentageText(event.currentTarget.value);
    lock.markChanged();
  };

  /** Sends `request` for the rate `targetId`: POST for a new rate, PUT otherwise, sent again once after a 404. */
  const sendRequest = async (targetId: Id | null, request: CommissionRateRequest): Promise<OperationResult> => {
    if (targetId === null) {
      return api.post<OperationResult>(COMMISSION_RATES_PATH, request);
    }

    try {
      return await api.put<OperationResult>(ratePath(targetId), request);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
      const current = await api.get<CommissionRateRow>(ratePath(targetId));
      const agentIds = current.agents.map((agent) => agent.id);
      agentIdsRef.current = agentIds;
      return api.put<OperationResult>(ratePath(targetId), { ...request, agentIds });
    }
  };

  /** Focuses the category list for `FOCUS: ItemCategory` and the percentage box for `FOCUS: Percentage`. */
  const focusField = (field: string | null): void => {
    if (field === FOCUS_ITEM_CATEGORY) {
      categoryRef.current?.focus();
    } else if (field === FOCUS_PERCENTAGE) {
      percentageRef.current?.focus();
    }
  };

  /**
   * Returns the save the lock hook runs: the request built from the fields now, keeping its result in `outcome` and,
   * for a failed result, focusing its focus field before the hook shows the errors.
   */
  const makeSave = (outcome: SaveOutcome): (() => Promise<OperationResult>) => {
    const targetId = rateId;
    const request: CommissionRateRequest = {
      itemCategoryId: categoryValue === '' ? null : Number(categoryValue),
      percentage: textAsDecimal(percentageText),
      agentIds: targetId === null ? [] : [...agentIdsRef.current],
    };

    return async (): Promise<OperationResult> => {
      const result = await sendRequest(targetId, request);
      outcome.result = result;
      if (result.succeeded) {
        savedRef.current = true;
      } else if (mountedRef.current) {
        focusField(result.focusField);
      }
      return result;
    };
  };

  const perform = async (action: SaveAction): Promise<void> => {
    if (busyRef.current || (action !== 'cancel' && status !== 'ready')) {
      return;
    }

    const targetId = rateId;
    const outcome: SaveOutcome = { result: null };
    const doSave = makeSave(outcome);
    setBusyState(true);

    let finished: boolean;
    try {
      finished = action === 'cancel' ? await lock.close(doSave) : await lock.save(doSave);
    } catch (error: unknown) {
      if (!mountedRef.current) {
        return;
      }
      setBusyState(false);
      if (isNotFound(error)) {
        onCloseRef.current(savedRef.current);
        return;
      }
      throw error;
    }

    if (!mountedRef.current) {
      return;
    }
    setBusyState(false);

    if (finished) {
      if (action !== 'shortcut') {
        onCloseRef.current(savedRef.current);
        return;
      }
      const createdId = outcome.result?.id ?? null;
      if (targetId === null && isRowId(createdId)) {
        setRateId(createdId);
      }
      setReloadToken((token) => token + 1);
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void perform('cancel');
      return;
    }

    if (isSaveShortcut(event)) {
      event.preventDefault();
      event.stopPropagation();
      void perform('shortcut');
      return;
    }

    if (event.key === 'Enter') {
      if (event.nativeEvent.isComposing || event.target instanceof HTMLButtonElement) {
        return;
      }
      event.preventDefault();
      void perform('ok');
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

  const loading = status === 'loading';

  return (
    <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={caption}
        aria-busy={loading || busy}
        tabIndex={-1}
        style={loading || busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 style={TITLE_STYLE}>{caption}</h2>
        <div style={FIELDS_STYLE}>
          <label htmlFor={categoryId} style={LABEL_STYLE}>
            {ITEM_CATEGORY_CAPTION}
          </label>
          <select
            ref={categoryRef}
            id={categoryId}
            value={categoryValue}
            style={controlStyle(SELECT_STYLE, categoryInteraction)}
            onChange={handleCategoryChange}
            {...categoryInteraction.handlers}
          >
            <option value="" />
            {categories.map((row) => (
              <option key={row.id} value={String(row.id)}>
                {row.name}
              </option>
            ))}
          </select>
          <label htmlFor={percentageId} style={LABEL_STYLE}>
            {PERCENTAGE_CAPTION}
          </label>
          <div style={PERCENTAGE_ROW_STYLE}>
            <input
              ref={percentageRef}
              id={percentageId}
              type="text"
              value={percentageText}
              maxLength={PERCENTAGE_MAX_LENGTH}
              readOnly={!editable}
              autoComplete="off"
              aria-describedby={percentCharId}
              style={controlStyle(PERCENTAGE_INPUT_STYLE, percentageInteraction)}
              onChange={handlePercentageChange}
              {...percentageInteraction.handlers}
            />
            <span id={percentCharId} style={PERCENT_CHAR_STYLE}>
              {PERCENT_CHAR_CAPTION}
            </span>
          </div>
        </div>
        <hr style={RULE_STYLE} />
        <div style={BUTTON_ROW_STYLE}>
          <DialogButton aria-disabled={!editable} onClick={() => void perform('ok')}>
            {OK_CAPTION}
          </DialogButton>
          <DialogButton aria-disabled={busy} onClick={() => void perform('cancel')}>
            {CANCEL_CAPTION}
          </DialogButton>
        </div>
        {lock.element}
        {messageBox}
      </div>
    </div>
  );
}

