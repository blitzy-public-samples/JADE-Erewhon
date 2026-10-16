// Commission rates list of the admin area: the sale item category selector, the rates of the chosen category, the
// Add, Edit, Remove and Close buttons, and the commission rate editor they open.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactElement, ReactNode } from 'react';
import { ApiError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type { CommissionRateRow, EntityTypeName, ItemCategoryRow, OperationResult } from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { useMessageBox } from '../../components/MessageBox';
import { decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { isCompanyUser, useSession } from '../../session/SessionProvider';
import { EditCommissionRateDialog } from './EditCommissionRateDialog';

const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';
const COMMISSION_RATES_PATH = '/api/app/admin/commission-rates';

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['CommissionRate', 'ItemCategory'];

const CATEGORY_CAPTION = 'Sale Item Category';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';

const PERCENTAGE_SCALE = 2;

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const BUTTON_ACTIVE_BORDER_COLOR = 'rgb(0, 80, 160)';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.5rem',
  boxSizing: 'border-box',
  maxInlineSize: '30.75rem',
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

const CATEGORY_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: '0.625rem',
};

const LABEL_STYLE: CSSProperties = {
  flex: '0 0 auto',
  minInlineSize: '6rem',
  overflowWrap: 'break-word',
};

const SELECT_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  flex: '0 1 15.9375rem',
  minInlineSize: 0,
  paddingBlock: '0.125rem',
  font: 'inherit',
};

const LIST_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  blockSize: '15.9375rem',
  margin: 0,
  padding: 0,
  overflowY: 'auto',
  listStyle: 'none',
  background: 'white',
  color: 'black',
  border: LIST_BORDER,
};

const OPTION_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  paddingBlock: '0.0625rem',
  paddingInline: '0.25rem',
  cursor: 'default',
  overflowWrap: 'anywhere',
  userSelect: 'none',
};

const OPTION_FOCUS_STYLE: CSSProperties = {
  outline: FOCUS_OUTLINE,
  outlineOffset: '-2px',
};

const BUTTON_BAR_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.5rem',
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
  font: 'inherit',
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
  outline: FOCUS_OUTLINE,
  outlineOffset: '1px',
};

/** Properties of {@link CommissionRatesListPage}. */
interface CommissionRatesListPageProps {
  /** When true, the Add, Edit and Remove buttons are not rendered. */
  hideAddEditRemove?: boolean;
  /** Called by the Close button. */
  onClose?: () => void;
}

/** Rate the open editor edits; `commissionRateId` null adds a new rate. */
interface EditorTarget {
  commissionRateId: number | null;
}

/** Properties of {@link PushButton}. */
interface PushButtonProps {
  /** Caption of the button, also its accessible name. */
  children: ReactNode;
  /** When true, the button has the `disabled` attribute and its disabled appearance. */
  disabled?: boolean;
  onClick: () => void;
}

/** Returns the list text of a commission rate: its category name, " @ " and its percentage with two decimals. */
export function commissionRateDisplay(row: CommissionRateRow): string {
  return `${row.itemCategoryName} @ ${decimalString(row.percentage, PERCENTAGE_SCALE)}`;
}

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

/** Returns the path of GET /api/app/admin/commission-rates for the rates of the category `categoryId`. */
function ratesOfCategoryPath(categoryId: number): string {
  return `${COMMISSION_RATES_PATH}?category=${encodeURIComponent(String(categoryId))}`;
}

/** Returns the path of DELETE /api/app/admin/commission-rates/{id}. */
function ratePath(rateId: number): string {
  return `${COMMISSION_RATES_PATH}/${encodeURIComponent(String(rateId))}`;
}

/** Returns the style of a rate entry: selected, pressed or hovered background, in that order, and the focus outline. */
function optionStyle(selected: boolean, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let background: string | undefined;
  if (selected) {
    background = SELECTED_BACKGROUND;
  } else if (pressed) {
    background = PRESSED_BACKGROUND;
  } else if (hovered) {
    background = HOVER_BACKGROUND;
  }

  const style: CSSProperties = background === undefined ? OPTION_STYLE : { ...OPTION_STYLE, background };
  return focusVisible ? { ...style, ...OPTION_FOCUS_STYLE } : style;
}

/**
 * Returns the style of a {@link PushButton}: the disabled, pressed, hovered or base appearance, in that order of
 * precedence, with the focus outline added when `focusVisible` is true and the button is enabled.
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
  return focusVisible && !disabled ? { ...style, ...BUTTON_FOCUS_STYLE } : style;
}

/**
 * Push button of type `button` with the native `disabled` attribute. It has a rgb(240, 240, 240) background and a
 * 1px rgb(128, 128, 128) border; while the pointer is over it the background is rgb(229, 241, 251) and the border
 * rgb(0, 80, 160), and while the primary mouse button is held on it the background is rgb(204, 228, 247). Focused with
 * `:focus-visible` it has a 2px rgb(0, 80, 160) outline 1px outside its border. Disabled, it shows rgb(109, 109, 109)
 * text and a rgb(191, 191, 191) border.
 */
function PushButton({ children, disabled = false, onClick }: PushButtonProps): ReactElement {
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

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
 * The "Commission Rates" page of the administration area.
 *
 * - On mount it reads the item categories (GET /api/app/admin/item-categories, in name order) into the "Sale Item
 *   Category" list, whose first entry is empty and chosen. Add is enabled for the company user only; Edit and Remove
 *   are disabled.
 * - Choosing the empty entry empties the rates list; choosing a category reads its rates (GET
 *   /api/app/admin/commission-rates?category={id}, in percentage order), each listed as {@link commissionRateDisplay}.
 *   Either choice clears the rate selection.
 * - Clicking a rate, or moving to it with the arrow, Home or End keys or Space, selects it. Edit and Remove are enabled
 *   only while a rate is selected and the session is the company user's.
 * - Double-clicking a rate, or pressing Enter on it, selects it and, for the company user only, opens
 *   {@link EditCommissionRateDialog} for it, also when the buttons are hidden. Add opens the dialog for a new rate with
 *   no category chosen; Edit opens it for the selected rate. Closing the dialog reads the chosen category's rates
 *   again.
 * - Remove sends DELETE /api/app/admin/commission-rates/{id} for the selected rate at once. A failed result shows its
 *   errors titled `$ErrorDuringSave` and then, once that box is closed, titled `$UnableToDelete`, both OK-only with the
 *   information icon, and keeps the selection; a succeeded result, or a rate that no longer exists, clears the
 *   selection and reads the rates again.
 * - Every EntityChanged event for a commission rate or an item category reads the categories and the chosen
 *   category's rates again, keeping the chosen category and the selected rate while they are still listed and clearing
 *   them otherwise.
 * - Close calls `onClose`. Error outcomes are shown in a message box; a 401 shows nothing; other failures reject.
 *
 * The role checks are presentation rules only: the endpoints apply none.
 */
export function CommissionRatesListPage({
  hideAddEditRemove = false,
  onClose,
}: CommissionRatesListPageProps): ReactElement {
  const { session } = useSession();
  const companyUser = isCompanyUser(session);
  const { element: messageBox, show, showError } = useMessageBox();
  const categorySelectId = useId();

  const [categories, setCategories] = useState<readonly ItemCategoryRow[]>([]);
  const [categoryId, setCategoryId] = useState<number | null>(null);
  const [rates, setRates] = useState<readonly CommissionRateRow[]>([]);
  const [selectedRateId, setSelectedRateId] = useState<number | null>(null);
  const [editor, setEditor] = useState<EditorTarget | null>(null);
  const [removing, setRemoving] = useState(false);
  const [focusedId, setFocusedId] = useState<number | null>(null);
  const [hoveredId, setHoveredId] = useState<number | null>(null);
  const [pressedId, setPressedId] = useState<number | null>(null);
  const [focusVisibleId, setFocusVisibleId] = useState<number | null>(null);

  const mountedRef = useRef(false);
  const categoryIdRef = useRef<number | null>(null);
  const categoriesSeqRef = useRef(0);
  const ratesSeqRef = useRef(0);
  const removingRef = useRef(false);
  const optionElements = useRef(new Map<number, HTMLLIElement>());

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Clears the chosen category, its rates and the rate selection, and discards every pending read of rates. */
  const clearCategory = useCallback((): void => {
    ratesSeqRef.current += 1;
    categoryIdRef.current = null;
    setCategoryId(null);
    setRates([]);
    setSelectedRateId(null);
  }, []);

  /** Reads the item categories, clearing the chosen category when it is no longer listed. */
  const loadCategories = useCallback(async (): Promise<void> => {
    categoriesSeqRef.current += 1;
    const seq = categoriesSeqRef.current;
    const isCurrent = (): boolean => mountedRef.current && seq === categoriesSeqRef.current;

    let rows: ItemCategoryRow[] | undefined;
    try {
      rows = await api.get<ItemCategoryRow[] | undefined>(ITEM_CATEGORIES_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        await showError(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    const list = Array.isArray(rows) ? rows : [];
    setCategories(list);
    const chosen = categoryIdRef.current;
    if (chosen !== null && !list.some((category) => category.id === chosen)) {
      clearCategory();
    }
  }, [clearCategory, showError]);

  /**
   * Reads the rates of the category `forCategoryId`, keeping the selected rate while it is listed. The answer is
   * applied only while that category is still chosen and no later read of rates has started; a category that no
   * longer exists is cleared and the categories are read again.
   */
  const loadRates = useCallback(
    async (forCategoryId: number): Promise<void> => {
      ratesSeqRef.current += 1;
      const seq = ratesSeqRef.current;
      const isCurrent = (): boolean =>
        mountedRef.current && seq === ratesSeqRef.current && categoryIdRef.current === forCategoryId;

      let rows: CommissionRateRow[] | undefined;
      try {
        rows = await api.get<CommissionRateRow[] | undefined>(ratesOfCategoryPath(forCategoryId));
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        if (isNotFound(error)) {
          clearCategory();
          await loadCategories();
          return;
        }
        await showError(error);
        return;
      }

      if (!isCurrent()) {
        return;
      }
      const list = Array.isArray(rows) ? rows : [];
      setRates(list);
      setSelectedRateId((current) => (current !== null && list.some((row) => row.id === current) ? current : null));
    },
    [clearCategory, loadCategories, showError],
  );

  /** Reads the categories and the chosen category's rates again. */
  const reload = useCallback((): Promise<unknown> => {
    const reloads: Promise<void>[] = [loadCategories()];
    const chosen = categoryIdRef.current;
    if (chosen !== null) {
      reloads.push(loadRates(chosen));
    }
    return Promise.all(reloads);
  }, [loadCategories, loadRates]);

  useEffect(() => {
    void loadCategories();
  }, [loadCategories]);

  useEffect(() => onEntityChanged(WATCHED_ENTITY_TYPES, () => reload()), [reload]);

  const selectedRate = rates.find((row) => row.id === selectedRateId) ?? null;
  const editRemoveEnabled = companyUser && selectedRate !== null;

  const chooseCategory = (value: string): void => {
    if (value === '') {
      clearCategory();
      return;
    }
    const chosen = Number(value);
    categoryIdRef.current = chosen;
    setCategoryId(chosen);
    setRates([]);
    setSelectedRateId(null);
    void loadRates(chosen);
  };

  const selectRate = (row: CommissionRateRow): void => {
    setSelectedRateId(row.id);
  };

  /** Selects `row` and, for the company user, opens the editor for it. */
  const openRateFromList = (row: CommissionRateRow): void => {
    setSelectedRateId(row.id);
    if (companyUser) {
      setEditor({ commissionRateId: row.id });
    }
  };

  const addRate = (): void => {
    setEditor({ commissionRateId: null });
  };

  const editSelectedRate = (): void => {
    if (selectedRate !== null) {
      setEditor({ commissionRateId: selectedRate.id });
    }
  };

  const closeEditor = (): void => {
    setEditor(null);
    const chosen = categoryIdRef.current;
    if (chosen !== null) {
      void loadRates(chosen);
    }
  };

  /** Clears the selection of the rate `rateId` and reads the rates of `forCategoryId` again while it is chosen. */
  const afterRateRemoved = async (rateId: number, forCategoryId: number | null): Promise<void> => {
    setSelectedRateId((current) => (current === rateId ? null : current));
    if (forCategoryId !== null && categoryIdRef.current === forCategoryId) {
      await loadRates(forCategoryId);
    }
  };

  const removeSelectedRate = async (): Promise<void> => {
    const row = selectedRate;
    if (row === null || removingRef.current) {
      return;
    }
    const forCategoryId = categoryIdRef.current;

    removingRef.current = true;
    setRemoving(true);
    let result: OperationResult | undefined;
    let failure: { error: unknown } | null = null;
    try {
      result = await api.del<OperationResult | undefined>(ratePath(row.id));
    } catch (error: unknown) {
      failure = { error };
    } finally {
      removingRef.current = false;
      if (mountedRef.current) {
        setRemoving(false);
      }
    }

    if (!mountedRef.current) {
      return;
    }
    if (failure !== null) {
      if (isNotFound(failure.error)) {
        await afterRateRemoved(row.id, forCategoryId);
        return;
      }
      await showError(failure.error);
      return;
    }
    if (result === undefined) {
      return;
    }
    if (result.succeeded) {
      await afterRateRemoved(row.id, forCategoryId);
      return;
    }
    await show(displayErrorsOptions(result));
    await show(displayErrorsOptions(result, msg('UnableToDelete')));
  };

  const focusOption = (rateId: number): void => {
    setFocusedId(rateId);
    optionElements.current.get(rateId)?.focus();
  };

  const handleOptionKeyDown = (event: KeyboardEvent<HTMLLIElement>, position: number): void => {
    const row = rates[position];
    if (row === undefined) {
      return;
    }

    switch (event.key) {
      case ' ':
        event.preventDefault();
        selectRate(row);
        return;
      case 'Enter':
        event.preventDefault();
        openRateFromList(row);
        return;
      case 'ArrowDown':
      case 'ArrowUp':
      case 'Home':
      case 'End': {
        if (event.ctrlKey || event.altKey || event.metaKey) {
          return;
        }
        event.preventDefault();
        let targetPosition = rates.length - 1;
        if (event.key === 'ArrowDown') {
          targetPosition = position + 1;
        } else if (event.key === 'ArrowUp') {
          targetPosition = position - 1;
        } else if (event.key === 'Home') {
          targetPosition = 0;
        }
        const target = rates[targetPosition];
        if (target !== undefined) {
          focusOption(target.id);
          selectRate(target);
        }
        return;
      }
      default:
        return;
    }
  };

  const updateFocusVisible = (
    event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>,
    rateId: number,
  ): void => {
    setFocusVisibleId(matchesFocusVisible(event.currentTarget) ? rateId : null);
  };

  const isListed = (rateId: number | null): rateId is number =>
    rateId !== null && rates.some((row) => row.id === rateId);

  let tabStopId: number | null = rates[0]?.id ?? null;
  if (isListed(focusedId)) {
    tabStopId = focusedId;
  } else if (selectedRate !== null) {
    tabStopId = selectedRate.id;
  }

  return (
    <section
      aria-label={msg('CommissionRates')}
      aria-busy={removing}
      style={removing ? BUSY_SECTION_STYLE : SECTION_STYLE}
    >
      <h2 style={HEADING_STYLE}>{msg('CommissionRates')}</h2>
      <div style={CATEGORY_ROW_STYLE}>
        <label htmlFor={categorySelectId} style={LABEL_STYLE}>
          {CATEGORY_CAPTION}
        </label>
        <select
          id={categorySelectId}
          autoComplete="off"
          value={categoryId === null ? '' : String(categoryId)}
          style={SELECT_STYLE}
          onChange={(event) => chooseCategory(event.target.value)}
        >
          <option value="" />
          {categories.map((category) => (
            <option key={category.id} value={String(category.id)}>
              {category.name}
            </option>
          ))}
        </select>
      </div>
      <ul role="listbox" aria-label={msg('CommissionRates')} style={LIST_STYLE}>
        {rates.map((row, position) => {
          const selected = row.id === selectedRateId;
          return (
            <li
              key={row.id}
              ref={(element) => {
                if (element === null) {
                  optionElements.current.delete(row.id);
                } else {
                  optionElements.current.set(row.id, element);
                }
              }}
              role="option"
              aria-selected={selected}
              tabIndex={row.id === tabStopId ? 0 : -1}
              style={optionStyle(selected, row.id === pressedId, row.id === hoveredId, row.id === focusVisibleId)}
              onClick={() => selectRate(row)}
              onDoubleClick={() => openRateFromList(row)}
              onKeyDown={(event) => handleOptionKeyDown(event, position)}
              onKeyUp={(event) => updateFocusVisible(event, row.id)}
              onFocus={(event) => {
                setFocusedId(row.id);
                updateFocusVisible(event, row.id);
              }}
              onBlur={() => setFocusVisibleId(null)}
              onMouseEnter={() => setHoveredId(row.id)}
              onMouseLeave={() => {
                setHoveredId(null);
                setPressedId(null);
              }}
              onMouseDown={(event) => {
                if (event.button === 0) {
                  setPressedId(row.id);
                }
              }}
              onMouseUp={() => setPressedId(null)}
            >
              {commissionRateDisplay(row)}
            </li>
          );
        })}
      </ul>
      <div style={BUTTON_BAR_STYLE}>
        {!hideAddEditRemove && (
          <>
            <PushButton disabled={!companyUser} onClick={addRate}>
              {ADD_CAPTION}
            </PushButton>
            <PushButton disabled={!editRemoveEnabled} onClick={editSelectedRate}>
              {EDIT_CAPTION}
            </PushButton>
            <PushButton disabled={!editRemoveEnabled} onClick={() => void removeSelectedRate()}>
              {REMOVE_CAPTION}
            </PushButton>
          </>
        )}
        <PushButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</PushButton>
      </div>
      {messageBox}
      {editor !== null && (
        <EditCommissionRateDialog commissionRateId={editor.commissionRateId} onClose={closeEditor} />
      )}
    </section>
  );
}
