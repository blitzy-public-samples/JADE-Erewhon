import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  CommissionRateRow,
  EntityTypeName,
  Id,
  ItemCategoryRow,
  ItemListRow,
  ItemsByCategoryResponse,
  OperationResult,
} from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { itemDisplay, itemDisplayInput } from '../items/itemDisplay';
import { commissionRateDisplay } from './CommissionRatesListPage';
import { EditItemCategoryDialog } from './EditItemCategoryDialog';

/** Properties of {@link ItemCategoryListPage}. */
export interface ItemCategoryListPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** One entry of a list box: the row id it stands for and the text it shows. */
interface ListEntry {
  id: Id;
  text: string;
}

/** Item category the editor dialog is open for; `itemCategoryId` null adds a new item category. */
interface EditorTarget {
  itemCategoryId: Id | null;
}

/** Commission rates and items of one item category, as read together. */
interface CategoryDetails {
  rates: readonly CommissionRateRow[];
  items: readonly ItemListRow[];
}

const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';
const COMMISSION_RATES_PATH = '/api/app/admin/commission-rates';
const ITEMS_BY_CATEGORY_PATH = '/api/app/admin/items/by-category';

const COMMISSION_RATES_CAPTION = 'Commission Rates';
const ITEMS_CAPTION = 'Items';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';

const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['ItemCategory', 'CommissionRate', 'RetailItem', 'TenderItem'];

/** Error number and text of the object-not-found exception outcome. */
const OBJECT_NOT_FOUND_CODE = 4;
const OBJECT_NOT_FOUND_TEXT = 'Object not found';

const NO_DETAILS: CategoryDetails = { rates: [], items: [] };

const LIST_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';
const COLUMN_GAP = '0.375rem';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.625rem',
  boxSizing: 'border-box',
  maxInlineSize: '33rem',
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

const CATEGORIES_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '1 1 11.25rem',
  minInlineSize: 0,
};

const DETAILS_COLUMN_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: COLUMN_GAP,
  flex: '1 1 19.75rem',
  minInlineSize: 0,
};

const DETAIL_GROUP_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: COLUMN_GAP,
};

const LABEL_STYLE: CSSProperties = {
  minBlockSize: '0.8125rem',
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

const CATEGORIES_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '20rem',
  background: 'white',
};

const COMMISSION_RATES_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '7.0625rem',
  background: 'rgb(240, 240, 240)',
};

const ITEMS_LIST_STYLE: CSSProperties = {
  ...LIST_STYLE,
  blockSize: '10.1875rem',
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
  gap: COLUMN_GAP,
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

/** Returns the request path of the item category with row id `id`. */
function itemCategoryPath(id: Id): string {
  return `${ITEM_CATEGORIES_PATH}/${encodeURIComponent(String(id))}`;
}

/** Returns the path of GET /api/app/admin/commission-rates for the rates of the category `categoryId`. */
function ratesOfCategoryPath(categoryId: Id): string {
  return `${COMMISSION_RATES_PATH}?category=${encodeURIComponent(String(categoryId))}`;
}

/** Returns the path of GET /api/app/admin/items/by-category for the items of the category `categoryId`. */
function itemsOfCategoryPath(categoryId: Id): string {
  return `${ITEMS_BY_CATEGORY_PATH}?category=${encodeURIComponent(String(categoryId))}`;
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

/** Returns the list entry of `category`: its row id and name. */
function categoryEntry(category: ItemCategoryRow): ListEntry {
  return { id: category.id, text: category.name };
}

/** Returns the list entry of `rate`: its row id and {@link commissionRateDisplay} text. */
function rateEntry(rate: CommissionRateRow): ListEntry {
  return { id: rate.id, text: commissionRateDisplay(rate) };
}

/** Returns the list entry of `row`: the item's row id and its {@link itemDisplay} text. */
function itemEntry(row: ItemListRow): ListEntry {
  return { id: row.item.id, text: itemDisplay(itemDisplayInput(row.item)) };
}

/** Properties of {@link EntryList}. */
interface EntryListProps {
  /** Accessible name of the list box. */
  label: string;
  /** Entries in display order. */
  entries: readonly ListEntry[];
  /** Row id of the selected entry; null while none is selected. */
  selectedId: Id | null;
  /** True while the entries are being read. */
  busy: boolean;
  /** Style of the list box. */
  listStyle: CSSProperties;
  /** Called with the entry a click, Space, Enter or a navigation key selects; omitted for a read-only list. */
  onSelect?: (id: Id) => void;
  /** Called with the entry a double-click or Enter opens. */
  onActivate?: (id: Id) => void;
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
  const [focusedId, setFocusedId] = useState<Id | null>(null);
  const [hoveredId, setHoveredId] = useState<Id | null>(null);
  const [pressedId, setPressedId] = useState<Id | null>(null);
  const [focusVisibleId, setFocusVisibleId] = useState<Id | null>(null);
  const entryElements = useRef(new Map<Id, HTMLLIElement>());

  const isListed = (id: Id | null): id is Id => id !== null && entries.some((entry) => entry.id === id);

  let tabStopId: Id | null = entries[0]?.id ?? null;
  if (isListed(focusedId)) {
    tabStopId = focusedId;
  } else if (!readOnly && isListed(selectedId)) {
    tabStopId = selectedId;
  }

  const focusEntry = (id: Id): void => {
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

  const updateFocusVisible = (event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>, id: Id): void => {
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
 * The "Item Categories" tab of the admin area: the item categories of the bound company, the commission rates and the
 * items of the selected category, and the Add, Edit, Remove and Close buttons. No control depends on the session's
 * role.
 *
 * - On mount GET /api/app/admin/item-categories reads the categories, listed by name in the order received; the list
 *   has `aria-busy="true"` until the first answer. Edit and Remove are `aria-disabled` while no category is selected.
 * - Selecting a category reads its commission rates (GET /api/app/admin/commission-rates?category={id}, in percentage
 *   order) and its items (GET /api/app/admin/items/by-category?category={id}, in code-number order) into the read-only
 *   "Commission Rates" and "Items" lists, shown as {@link commissionRateDisplay} and {@link itemDisplay}, and enables
 *   Edit and Remove. Only the answer for the category still selected, from its latest read, is shown; a 404 deselects
 *   the category and reads the categories again.
 * - Add opens {@link EditItemCategoryDialog} for a new category. Edit, a double-click on a category or Enter on it
 *   opens the dialog for the selected category. When the dialog closes it is unmounted and the categories and the
 *   selected category's rates and items are read again.
 * - Remove sends DELETE /api/app/admin/item-categories/{id} for the selected category at once, with no confirmation;
 *   the server deletes its commission rates and items with it. A succeeded result deselects the category, empties both
 *   lists and reads the categories again; a failed result shows its errors joined with CR LF in an OK box with the
 *   information icon titled `$ErrorDuringSave` and keeps the selection; a 404 deselects the category, reads the
 *   categories again and shows the exception outcome of error 4, "Object not found". While the request is outstanding
 *   the page is `aria-busy` and ignores every control except Close, and change events read the categories only; a
 *   Remove that does not succeed reads the selected category's rates and items again.
 * - Every EntityChanged event for an item category, a commission rate, a retail item or a tender item reads the
 *   categories and the selected category's rates and items again; events arriving while that read is outstanding are
 *   answered by one more read after it. A selected category the answer no longer holds is deselected, which empties
 *   both lists and disables Edit and Remove.
 * - Close calls `onClose`.
 * - An error outcome is shown in its message box, an HTTP 401 shows nothing, and any other failure shows its message
 *   in an OK box with the error icon titled `$Error`.
 */
export function ItemCategoryListPage({ onClose }: ItemCategoryListPageProps = {}): ReactElement {
  const { element: messageBox, show, showError } = useMessageBox();
  const [categories, setCategories] = useState<readonly ItemCategoryRow[] | null>(null);
  const [selectedId, setSelectedId] = useState<Id | null>(null);
  const [details, setDetails] = useState<CategoryDetails>(NO_DETAILS);
  const [editor, setEditor] = useState<EditorTarget | null>(null);
  const [removing, setRemoving] = useState(false);
  const mountedRef = useRef(false);
  const selectedIdRef = useRef<Id | null>(null);
  const categoriesSeqRef = useRef(0);
  const detailsSeqRef = useRef(0);
  const removingRef = useRef(false);
  const changeReloadRef = useRef({ running: false, again: false });

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

  /** Port of ItemCategoryListForm.lstCategories_paint with no list object: deselects and empties both lists. */
  const clearSelection = useCallback((): void => {
    detailsSeqRef.current += 1;
    selectedIdRef.current = null;
    setSelectedId(null);
    setDetails(NO_DETAILS);
  }, []);

  /** Reads the item categories and deselects the selected category when it is no longer listed. */
  const loadCategories = useCallback(async (): Promise<void> => {
    categoriesSeqRef.current += 1;
    const seq = categoriesSeqRef.current;
    const isCurrent = (): boolean => mountedRef.current && seq === categoriesSeqRef.current;

    let rows: ItemCategoryRow[] | undefined;
    try {
      rows = await api.get<ItemCategoryRow[] | undefined>(ITEM_CATEGORIES_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        setCategories((current) => current ?? []);
        await reportFailure(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    const list = Array.isArray(rows) ? rows : [];
    setCategories(list);
    const selected = selectedIdRef.current;
    if (selected !== null && !list.some((category) => category.id === selected)) {
      clearSelection();
    }
  }, [clearSelection, reportFailure]);

  /**
   * Reads the commission rates and the items of the category `categoryId` together. The answer is applied only while
   * that category is still selected and no later read of them has started; a category that no longer exists is
   * deselected and the categories are read again.
   */
  const loadDetails = useCallback(
    async (categoryId: Id): Promise<void> => {
      detailsSeqRef.current += 1;
      const seq = detailsSeqRef.current;
      const isCurrent = (): boolean =>
        mountedRef.current && seq === detailsSeqRef.current && selectedIdRef.current === categoryId;

      let rates: CommissionRateRow[] | undefined;
      let itemsResponse: ItemsByCategoryResponse | undefined;
      try {
        [rates, itemsResponse] = await Promise.all([
          api.get<CommissionRateRow[] | undefined>(ratesOfCategoryPath(categoryId)),
          api.get<ItemsByCategoryResponse | undefined>(itemsOfCategoryPath(categoryId)),
        ]);
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        if (isNotFound(error)) {
          clearSelection();
          await loadCategories();
          return;
        }
        await reportFailure(error);
        return;
      }

      if (!isCurrent()) {
        return;
      }
      setDetails({
        rates: Array.isArray(rates) ? rates : [],
        items: Array.isArray(itemsResponse?.items) ? itemsResponse.items : [],
      });
    },
    [clearSelection, loadCategories, reportFailure],
  );

  /**
   * Reads the categories again and, unless a Remove is outstanding, the selected category's commission rates and
   * items.
   */
  const reloadAll = useCallback((): Promise<unknown> => {
    const reloads: Promise<void>[] = [loadCategories()];
    const selected = selectedIdRef.current;
    if (selected !== null && !removingRef.current) {
      reloads.push(loadDetails(selected));
    }
    return Promise.all(reloads);
  }, [loadCategories, loadDetails]);

  /** Reads the selected category's commission rates and items again, when a category is selected. */
  const reloadSelectedDetails = (): void => {
    const selected = selectedIdRef.current;
    if (selected !== null) {
      void loadDetails(selected);
    }
  };

  /**
   * Runs {@link reloadAll} for an EntityChanged event. While a reload is outstanding further events start none; once it
   * settles, one more reload runs when any event arrived meanwhile.
   */
  const reloadForChange = useCallback(async (): Promise<void> => {
    const state = changeReloadRef.current;
    if (state.running) {
      state.again = true;
      return;
    }
    state.running = true;
    try {
      do {
        state.again = false;
        await reloadAll();
      } while (state.again && mountedRef.current);
    } finally {
      state.running = false;
    }
  }, [reloadAll]);

  useEffect(() => {
    void loadCategories();
  }, [loadCategories]);

  useEffect(() => onEntityChanged(WATCHED_ENTITY_TYPES, () => reloadForChange()), [reloadForChange]);

  const selectedCategory = categories?.find((category) => category.id === selectedId) ?? null;
  const selectionActionsEnabled = selectedCategory !== null && !removing;

  /** Port of ItemCategoryListForm.lstCategories_click for a listed category. */
  const selectCategory = (id: Id): void => {
    if (removingRef.current) {
      return;
    }
    if (selectedIdRef.current !== id) {
      selectedIdRef.current = id;
      setSelectedId(id);
      setDetails(NO_DETAILS);
    }
    void loadDetails(id);
  };

  /** Port of ItemCategoryListForm.lstCategories_dblClick: opens the editor for the category. */
  const openCategory = (id: Id): void => {
    if (!removingRef.current) {
      setEditor({ itemCategoryId: id });
    }
  };

  /** Port of ItemCategoryListForm.btnAdd_click. */
  const addCategory = (): void => {
    if (!removingRef.current) {
      setEditor({ itemCategoryId: null });
    }
  };

  /** Port of ItemCategoryListForm.btnEdit_click. */
  const editCategory = (): void => {
    if (!removingRef.current && selectedCategory !== null) {
      setEditor({ itemCategoryId: selectedCategory.id });
    }
  };

  /** Unmounts the editor and reads the categories and the selected category's rates and items again. */
  const closeEditor = (): void => {
    setEditor(null);
    void reloadAll();
  };

  const endRemove = (): void => {
    removingRef.current = false;
    if (mountedRef.current) {
      setRemoving(false);
    }
  };

  /** Port of ItemCategoryListForm.btnRemove_click followed by ErewhonBaseForm.doDelete. */
  const removeCategory = async (): Promise<void> => {
    if (removingRef.current || selectedCategory === null) {
      return;
    }
    const { id } = selectedCategory;

    removingRef.current = true;
    setRemoving(true);
    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(itemCategoryPath(id));
    } catch (error: unknown) {
      endRemove();
      if (!mountedRef.current) {
        return;
      }
      if (isNotFound(error)) {
        if (selectedIdRef.current === id) {
          clearSelection();
        }
        await Promise.all([loadCategories(), reportFailure(objectNotFoundOutcome())]);
        return;
      }
      reloadSelectedDetails();
      await reportFailure(error);
      return;
    }

    endRemove();
    if (!mountedRef.current) {
      return;
    }
    if (result !== undefined && !result.succeeded) {
      reloadSelectedDetails();
      await show(displayErrorsOptions(result));
      return;
    }
    if (result !== undefined && selectedIdRef.current === id) {
      clearSelection();
    }
    await loadCategories();
  };

  return (
    <section
      aria-label={msg('ItemCategories')}
      aria-busy={removing}
      style={removing ? BUSY_SECTION_STYLE : SECTION_STYLE}
    >
      <h2 style={HEADING_STYLE}>{msg('ItemCategories')}</h2>
      <div style={BODY_STYLE}>
        <div style={CATEGORIES_COLUMN_STYLE}>
          <EntryList
            label={msg('ItemCategories')}
            entries={(categories ?? []).map(categoryEntry)}
            selectedId={selectedCategory?.id ?? null}
            busy={categories === null}
            listStyle={CATEGORIES_LIST_STYLE}
            onSelect={selectCategory}
            onActivate={openCategory}
          />
        </div>
        <div style={DETAILS_COLUMN_STYLE}>
          <div style={DETAIL_GROUP_STYLE}>
            <div style={LABEL_STYLE}>{COMMISSION_RATES_CAPTION}</div>
            <EntryList
              label={COMMISSION_RATES_CAPTION}
              entries={details.rates.map(rateEntry)}
              selectedId={null}
              busy={false}
              listStyle={COMMISSION_RATES_LIST_STYLE}
            />
          </div>
          <div style={DETAIL_GROUP_STYLE}>
            <div style={LABEL_STYLE}>{ITEMS_CAPTION}</div>
            <EntryList
              label={ITEMS_CAPTION}
              entries={details.items.map(itemEntry)}
              selectedId={null}
              busy={false}
              listStyle={ITEMS_LIST_STYLE}
            />
          </div>
        </div>
      </div>
      <div style={BUTTON_BAR_STYLE}>
        <DialogButton aria-disabled={removing} onClick={addCategory}>
          {ADD_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!selectionActionsEnabled} onClick={editCategory}>
          {EDIT_CAPTION}
        </DialogButton>
        <DialogButton aria-disabled={!selectionActionsEnabled} onClick={() => void removeCategory()}>
          {REMOVE_CAPTION}
        </DialogButton>
        <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
      </div>
      {editor !== null && <EditItemCategoryDialog itemCategoryId={editor.itemCategoryId} onClose={closeEditor} />}
      {messageBox}
    </section>
  );
}
