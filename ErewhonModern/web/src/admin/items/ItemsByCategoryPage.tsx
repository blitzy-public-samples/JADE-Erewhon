// The "Items (By Category)" tab of the admin area: the tree of item categories with their "Items for Sale" and
// "Items for Tender" folders, and the Find, Add, Edit, Remove and Close buttons.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, ReactElement } from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import { onEntityChanged } from '../../api/realtime';
import type {
  EntityChangedMessage,
  EntityTypeName,
  ItemCategoryRow,
  ItemListRow,
  ItemsByCategoryResponse,
  OperationResult,
} from '../../api/types';
import { displayErrorsOptions } from '../../components/ErrorList';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { isCompanyUser, useSession } from '../../session/SessionProvider';
import type { SessionState } from '../../session/SessionProvider';
import { EditRetailItemDialog } from './EditRetailItemDialog';
import { EditTenderItemDialog } from './EditTenderItemDialog';
import { FindItemDialog } from './FindItemDialog';
import { getHeader, itemDisplay, itemDisplayInput, itemKind, verifyHeader } from './itemDisplay';
import type { ItemKind } from './itemDisplay';

/** Properties of {@link ItemsByCategoryPage}. */
export interface ItemsByCategoryPageProps {
  /** Called by the Close button. */
  onClose?: () => void;
}

/** One entry under an item folder: the "No Items available." placeholder or an item row. */
type FolderEntry = { type: 'placeholder' } | { type: 'item'; row: ItemListRow };

/** The "Items for Sale" or "Items for Tender" folder of a category. */
interface FolderState {
  /** True while the folder is open. */
  expanded: boolean;
  /** True while a read of the folder's items is outstanding. */
  loading: boolean;
  /** Entries listed under the folder, in display order. */
  entries: readonly FolderEntry[];
}

/** A category of the tree with its two item folders. */
interface CategoryNode {
  id: number;
  name: string;
  /** True while the category shows its two folders. */
  expanded: boolean;
  folders: Readonly<Record<ItemKind, FolderState>>;
}

/** The selected tree entry. */
type TreeSelection =
  | { type: 'category'; categoryId: number }
  | { type: 'folder'; categoryId: number; kind: ItemKind }
  | { type: 'placeholder'; categoryId: number; kind: ItemKind }
  | { type: 'item'; categoryId: number; kind: ItemKind; row: ItemListRow };

/** Enabled state of the Add, Edit and Remove buttons. */
interface ButtonStates {
  add: boolean;
  edit: boolean;
  remove: boolean;
}

/** One visible row of the tree. */
interface TreeRow {
  key: string;
  /** Key of the row's parent; null for a category. */
  parentKey: string | null;
  /** 1 for a category, 2 for an item folder, 3 for an item or the placeholder. */
  level: number;
  /** Position of the row among its siblings, from 1. */
  posInSet: number;
  /** Number of siblings of the row, the row included. */
  setSize: number;
  text: string;
  /** Open state of a category or folder row; undefined for an item or the placeholder. */
  expanded: boolean | undefined;
  selection: TreeSelection;
}

/** Editor dialog open for an item, or for a new item of a folder's kind. */
type EditorTarget =
  | { mode: 'add'; kind: ItemKind; categoryId: number }
  | { mode: 'edit'; kind: ItemKind; itemId: number };

const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';
const ITEMS_BY_CATEGORY_PATH = '/api/app/admin/items/by-category?category=';
const ITEMS_PATH = '/api/app/admin/items/';

const FIND_CAPTION = 'Find';
const ADD_CAPTION = 'Add';
const EDIT_CAPTION = 'Edit';
const REMOVE_CAPTION = 'Remove';
const CLOSE_CAPTION = 'Close';

const FOLDER_KINDS: readonly ItemKind[] = ['retail', 'tender'];

/** EntityChanged types the page acts on: retail and tender items, and item categories, whose deletions alone count. */
const WATCHED_ENTITY_TYPES: readonly EntityTypeName[] = ['RetailItem', 'TenderItem', 'ItemCategory'];

const ITEM_ENTITY_TYPES: ReadonlySet<EntityTypeName> = new Set<EntityTypeName>(['RetailItem', 'TenderItem']);

const NO_BUTTONS: ButtonStates = { add: false, edit: false, remove: false };

const PLACEHOLDER_ENTRIES: readonly FolderEntry[] = [{ type: 'placeholder' }];

const CLOSED_FOLDER: FolderState = { expanded: false, loading: false, entries: PLACEHOLDER_ENTRIES };

/** Error number and text of the object-not-found exception outcome. */
const OBJECT_NOT_FOUND_CODE = 4;
const OBJECT_NOT_FOUND_TEXT = 'Object not found';

const TREE_BORDER = '1px solid rgb(128, 128, 128)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';
const LEVEL_INDENT_REM = 1.25;
const ROW_INLINE_PADDING_REM = 0.25;
const EXPANDER_SIZE = '0.6875rem';

const SECTION_STYLE: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '0.625rem',
  boxSizing: 'border-box',
  maxInlineSize: '40.0625rem',
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

const TREE_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: '100%',
  blockSize: '24rem',
  margin: 0,
  padding: 0,
  overflowY: 'auto',
  listStyle: 'none',
  color: 'black',
  background: 'white',
  border: TREE_BORDER,
};

const ROW_STYLE: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: '0.375rem',
  boxSizing: 'border-box',
  minBlockSize: '1.3125rem',
  paddingBlock: '0.125rem',
  paddingInlineEnd: `${ROW_INLINE_PADDING_REM}rem`,
  cursor: 'default',
  userSelect: 'none',
};

const EXPANDER_STYLE: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  flex: 'none',
  boxSizing: 'border-box',
  inlineSize: EXPANDER_SIZE,
  blockSize: EXPANDER_SIZE,
  border: TREE_BORDER,
  background: 'white',
  fontSize: '0.625rem',
  lineHeight: 1,
};

const LEAF_SPACER_STYLE: CSSProperties = {
  flex: 'none',
  inlineSize: EXPANDER_SIZE,
  blockSize: EXPANDER_SIZE,
};

const ROW_TEXT_STYLE: CSSProperties = {
  flex: '1 1 auto',
  minInlineSize: 0,
  overflowWrap: 'anywhere',
  whiteSpace: 'pre-wrap',
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

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/** Returns the selected, pressed or hovered background of a row, in that order, or `undefined`. */
function rowBackground(selected: boolean, pressed: boolean, hovered: boolean): string | undefined {
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

/** Returns the style of a row at `level` with `background`, and the inset focus outline when `focusVisible` is true. */
function rowStyle(level: number, background: string | undefined, focusVisible: boolean): CSSProperties {
  const style: CSSProperties = {
    ...ROW_STYLE,
    paddingInlineStart: `${ROW_INLINE_PADDING_REM + (level - 1) * LEVEL_INDENT_REM}rem`,
  };
  if (background !== undefined) {
    style.background = background;
  }
  if (focusVisible) {
    style.outline = FOCUS_OUTLINE;
    style.outlineOffset = FOCUS_OUTLINE_OFFSET;
  }
  return style;
}

/** Returns the request path of the items of the category with row id `categoryId`. */
function itemsByCategoryPath(categoryId: number): string {
  return `${ITEMS_BY_CATEGORY_PATH}${encodeURIComponent(String(categoryId))}`;
}

/** Returns the request path of the item with row id `itemId`. */
function itemPath(itemId: number): string {
  return `${ITEMS_PATH}${encodeURIComponent(String(itemId))}`;
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

/** Returns the row key of `selection`. */
function selectionKey(selection: TreeSelection): string {
  switch (selection.type) {
    case 'category':
      return `c:${selection.categoryId}`;
    case 'folder':
      return `f:${selection.categoryId}:${selection.kind}`;
    case 'placeholder':
      return `p:${selection.categoryId}:${selection.kind}`;
    case 'item':
      return `i:${selection.categoryId}:${selection.kind}:${selection.row.item.id}`;
  }
}

/** Returns the key under which the outstanding read of a folder is counted. */
function folderKey(categoryId: number, kind: ItemKind): string {
  return `${categoryId}:${kind}`;
}

/** Returns true when `selection` is an entry under the folder `kind` of the category `categoryId`. */
function isInFolder(selection: TreeSelection | null, categoryId: number, kind: ItemKind): boolean {
  return (
    selection !== null &&
    (selection.type === 'item' || selection.type === 'placeholder') &&
    selection.categoryId === categoryId &&
    selection.kind === kind
  );
}

/**
 * Port of ItemsListByCategoryForm.doSelectListItem: Add is enabled for a selected item folder or item, Edit and
 * Remove for a selected item only.
 */
function buttonStatesFor(selection: TreeSelection): ButtonStates {
  const itemSelected = selection.type === 'item';
  return { add: selection.type === 'folder' || itemSelected, edit: itemSelected, remove: itemSelected };
}

/** Returns a closed category node for `row`, with both folders closed and holding the placeholder. */
function newCategoryNode(row: ItemCategoryRow): CategoryNode {
  return { id: row.id, name: row.name, expanded: false, folders: { retail: CLOSED_FOLDER, tender: CLOSED_FOLDER } };
}

/**
 * Port of ItemsListByCategoryForm.loadItemsInList: the entries of the folder `kind` from the category's rows in the
 * order received, keeping the rows of that kind whose item belongs to the session agent, or every such row for the
 * company user; the placeholder alone when none is kept.
 */
function folderEntries(rows: readonly ItemListRow[], kind: ItemKind, session: SessionState | null): FolderEntry[] {
  const header = getHeader(kind);
  const companyUser = isCompanyUser(session);
  const agentId = session?.agentId ?? null;

  const entries: FolderEntry[] = [];
  for (const row of rows) {
    const itemOk = verifyHeader(itemKind(row.item), header);
    const userOk = companyUser || (agentId !== null && row.item.agentId === agentId);
    if (itemOk && userOk) {
      entries.push({ type: 'item', row });
    }
  }
  return entries.length > 0 ? entries : [{ type: 'placeholder' }];
}

/** Returns `tree` with the folder `kind` of the category `categoryId` replaced by `update` of it. */
function updateFolderIn(
  tree: readonly CategoryNode[],
  categoryId: number,
  kind: ItemKind,
  update: (folder: FolderState) => FolderState,
): readonly CategoryNode[] {
  return tree.map((node) =>
    node.id === categoryId ? { ...node, folders: { ...node.folders, [kind]: update(node.folders[kind]) } } : node,
  );
}

/** Returns `tree` without the entries of the item `itemId`, or `tree` itself when no folder lists it. */
function withoutItem(tree: readonly CategoryNode[], itemId: number): readonly CategoryNode[] {
  let changed = false;
  const next = tree.map((node) => {
    let folders = node.folders;
    for (const kind of FOLDER_KINDS) {
      const folder = folders[kind];
      const entries = folder.entries.filter((entry) => entry.type !== 'item' || entry.row.item.id !== itemId);
      if (entries.length !== folder.entries.length) {
        folders = { ...folders, [kind]: { ...folder, entries } };
        changed = true;
      }
    }
    return folders === node.folders ? node : { ...node, folders };
  });
  return changed ? next : tree;
}

/** Returns the text of a folder entry: the item's list entry, or `$NoItemsAvailable` for the placeholder. */
function entryText(entry: FolderEntry): string {
  return entry.type === 'item' ? itemDisplay(itemDisplayInput(entry.row.item)) : msg('NoItemsAvailable');
}

/** Returns the visible rows of `tree` in display order: each category, then its open folders' entries. */
function visibleRows(tree: readonly CategoryNode[]): TreeRow[] {
  const rows: TreeRow[] = [];
  tree.forEach((node, categoryPosition) => {
    const categorySelection: TreeSelection = { type: 'category', categoryId: node.id };
    const categoryRowKey = selectionKey(categorySelection);
    rows.push({
      key: categoryRowKey,
      parentKey: null,
      level: 1,
      posInSet: categoryPosition + 1,
      setSize: tree.length,
      text: node.name,
      expanded: node.expanded,
      selection: categorySelection,
    });
    if (!node.expanded) {
      return;
    }

    FOLDER_KINDS.forEach((kind, folderPosition) => {
      const folder = node.folders[kind];
      const folderSelection: TreeSelection = { type: 'folder', categoryId: node.id, kind };
      const folderRowKey = selectionKey(folderSelection);
      rows.push({
        key: folderRowKey,
        parentKey: categoryRowKey,
        level: 2,
        posInSet: folderPosition + 1,
        setSize: FOLDER_KINDS.length,
        text: getHeader(kind),
        expanded: folder.expanded,
        selection: folderSelection,
      });
      if (!folder.expanded) {
        return;
      }

      folder.entries.forEach((entry, entryPosition) => {
        const selection: TreeSelection =
          entry.type === 'item'
            ? { type: 'item', categoryId: node.id, kind, row: entry.row }
            : { type: 'placeholder', categoryId: node.id, kind };
        rows.push({
          key: selectionKey(selection),
          parentKey: folderRowKey,
          level: 3,
          posInSet: entryPosition + 1,
          setSize: folder.entries.length,
          text: entryText(entry),
          expanded: undefined,
          selection,
        });
      });
    });
  });
  return rows;
}

/** Properties of {@link CategoryTree}. */
interface CategoryTreeProps {
  /** Id of the element naming the tree. */
  labelledBy: string;
  /** Visible rows in display order. */
  rows: readonly TreeRow[];
  /** Key of the selected row; null while none is selected. */
  selectedKey: string | null;
  /** True while the categories or a folder's items are being read. */
  busy: boolean;
  /** Called when a row is clicked or chosen with the arrow, Home, End or Space keys, and for Enter on an item row. */
  onSelect: (row: TreeRow) => void;
  /**
   * Called for a category or folder row after `onSelect` when it is clicked or Space is pressed on it, and when Enter,
   * ArrowRight on a closed row or ArrowLeft on an open row is pressed on it.
   */
  onToggle: (row: TreeRow) => void;
  /** Called after `onSelect` when an item row is double-clicked or Enter is pressed on it. */
  onActivate: (row: TreeRow) => void;
}

/**
 * The tree of categories, item folders and items, as a `role="tree"` list of `role="treeitem"` rows carrying their
 * level, position and set size. Each row's accessible name is its text; category and folder rows carry
 * `aria-expanded`, and the selected row `aria-selected="true"`; the selected row is scrolled into view. One row is in
 * the tab order: the selected row while it is visible, otherwise the first row.
 *
 * Keys on a row: ArrowDown, ArrowUp, Home and End select the next, previous, first or last row; ArrowRight opens a
 * closed category or folder and on an open one selects its first child; ArrowLeft closes an open category or folder
 * and on any other row selects its parent; Space selects the row and opens or closes a category or folder; Enter
 * opens or closes a category or folder and activates an item.
 */
function CategoryTree({
  labelledBy,
  rows,
  selectedKey,
  busy,
  onSelect,
  onToggle,
  onActivate,
}: CategoryTreeProps): ReactElement {
  const rowElements = useRef(new Map<string, HTMLLIElement>());
  const [hoveredKey, setHoveredKey] = useState<string | null>(null);
  const [pressedKey, setPressedKey] = useState<string | null>(null);
  const [focusVisibleKey, setFocusVisibleKey] = useState<string | null>(null);

  const tabStopKey = rows.some((row) => row.key === selectedKey) ? selectedKey : (rows[0]?.key ?? null);

  useEffect(() => {
    if (selectedKey !== null) {
      rowElements.current.get(selectedKey)?.scrollIntoView?.({ block: 'nearest' });
    }
  }, [selectedKey]);

  const focusRow = (key: string): void => {
    rowElements.current.get(key)?.focus();
  };

  const selectAndFocus = (row: TreeRow | undefined): void => {
    if (row !== undefined) {
      focusRow(row.key);
      onSelect(row);
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLLIElement>, position: number): void => {
    const row = rows[position];
    if (row === undefined) {
      return;
    }
    const isFolder = row.expanded !== undefined;

    switch (event.key) {
      case 'ArrowDown':
        selectAndFocus(rows[position + 1]);
        break;
      case 'ArrowUp':
        selectAndFocus(rows[position - 1]);
        break;
      case 'Home':
        selectAndFocus(rows[0]);
        break;
      case 'End':
        selectAndFocus(rows[rows.length - 1]);
        break;
      case 'ArrowRight':
        if (row.expanded === false) {
          onToggle(row);
        } else if (row.expanded === true) {
          const child = rows[position + 1];
          selectAndFocus(child?.parentKey === row.key ? child : undefined);
        }
        break;
      case 'ArrowLeft':
        if (row.expanded === true) {
          onToggle(row);
        } else {
          selectAndFocus(rows.find((candidate) => candidate.key === row.parentKey));
        }
        break;
      case ' ':
        onSelect(row);
        if (isFolder) {
          onToggle(row);
        }
        break;
      case 'Enter':
        if (isFolder) {
          onToggle(row);
        } else if (row.selection.type === 'item') {
          onSelect(row);
          onActivate(row);
        }
        break;
      default:
        return;
    }
    event.preventDefault();
  };

  const updateFocusVisible = (event: FocusEvent<HTMLLIElement> | KeyboardEvent<HTMLLIElement>, key: string): void => {
    setFocusVisibleKey(matchesFocusVisible(event.currentTarget) ? key : null);
  };

  return (
    <ul role="tree" aria-labelledby={labelledBy} aria-busy={busy} style={TREE_STYLE}>
      {rows.map((row, position) => {
        const { key } = row;
        const selected = key === selectedKey;
        const background = rowBackground(selected, key === pressedKey, key === hoveredKey);

        return (
          <li
            key={key}
            ref={(element) => {
              if (element === null) {
                rowElements.current.delete(key);
              } else {
                rowElements.current.set(key, element);
              }
            }}
            role="treeitem"
            aria-level={row.level}
            aria-posinset={row.posInSet}
            aria-setsize={row.setSize}
            aria-expanded={row.expanded}
            aria-selected={selected}
            tabIndex={key === tabStopKey ? 0 : -1}
            style={rowStyle(row.level, background, key === focusVisibleKey)}
            onClick={() => {
              onSelect(row);
              if (row.expanded !== undefined) {
                onToggle(row);
              }
            }}
            onDoubleClick={() => {
              if (row.selection.type === 'item') {
                onActivate(row);
              }
            }}
            onKeyDown={(event) => handleKeyDown(event, position)}
            onKeyUp={(event) => updateFocusVisible(event, key)}
            onFocus={(event) => updateFocusVisible(event, key)}
            onBlur={() => setFocusVisibleKey(null)}
            onMouseEnter={() => setHoveredKey(key)}
            onMouseLeave={() => {
              setHoveredKey(null);
              setPressedKey(null);
            }}
            onMouseDown={(event) => {
              if (event.button === 0) {
                setPressedKey(key);
              }
            }}
            onMouseUp={() => setPressedKey(null)}
          >
            {row.expanded === undefined ? (
              <span aria-hidden="true" style={LEAF_SPACER_STYLE} />
            ) : (
              <span aria-hidden="true" style={EXPANDER_STYLE}>
                {row.expanded ? '\u2212' : '+'}
              </span>
            )}
            <span style={ROW_TEXT_STYLE}>{row.text}</span>
          </li>
        );
      })}
    </ul>
  );
}

/**
 * The "Items (By Category)" tab of the admin area, the port of ItemsListByCategoryForm.
 *
 * - On mount GET /api/app/admin/item-categories reads the categories; each is listed, in the order received, as a
 *   closed category holding the closed folders "Items for Sale" and "Items for Tender", each holding
 *   `$NoItemsAvailable`. The tree has `aria-busy="true"` while the categories or a folder's items are being read.
 * - Clicking a category or folder selects it and opens or closes it. Opening a folder sends GET
 *   /api/app/admin/items/by-category for its category and lists, in the order received, the items of the folder's
 *   kind that belong to the session agent, or every such item for the company user, each as its `itemDisplay` text;
 *   `$NoItemsAvailable` when none is listed. Closing a folder restores `$NoItemsAvailable`. Replacing a folder's
 *   entries deselects an entry under it. Clicking an item selects it, and double-clicking it edits it.
 * - The Add, Edit and Remove buttons are disabled on load. Clicking or keyboard-selecting a row enables Add for a
 *   folder or an item, and Edit and Remove for an item only; selecting a row by Find, or deselecting it, leaves them
 *   as they are.
 * - Add opens {@link EditRetailItemDialog} under "Items for Sale" or {@link EditTenderItemDialog} under "Items for
 *   Tender" with the selected folder's category; a successful OK reads that folder again when it is open.
 * - Edit opens the editor of the selected item's kind; a successful OK reads every open folder again and disables
 *   Add, Edit and Remove.
 * - Remove takes the selected item off the tree and sends DELETE /api/app/admin/items/{id} at once, with no
 *   confirmation. A failed result shows its errors in an OK box with the information icon titled `$ErrorDuringSave`;
 *   then the item's folder is read again and left open. A 404 shows the exception outcome of error 4, "Object not
 *   found"; after a request that fails the folder is not read again. While the request is outstanding the page is
 *   `aria-busy` and ignores every control except Close.
 * - Find opens {@link FindItemDialog}; the item it finds has its category and its folder opened, the folder's items
 *   read when it was closed, and is selected when the folder lists it.
 * - EntityChanged events: an added or updated retail or tender item reads every open folder again; a deleted one is
 *   taken off the tree; a deleted item category is taken off the tree with its folders.
 * - Close calls `onClose`.
 * - An error outcome is shown in its message box, an HTTP 401 shows nothing, and any other failure shows its message
 *   in an OK box with the error icon titled `$Error`.
 */
export function ItemsByCategoryPage({ onClose }: ItemsByCategoryPageProps = {}): ReactElement {
  const headingId = useId();
  const { session } = useSession();
  const { element: messageBox, show, showError } = useMessageBox();
  const [tree, setTreeState] = useState<readonly CategoryNode[] | null>(null);
  const [selection, setSelectionState] = useState<TreeSelection | null>(null);
  const [buttons, setButtons] = useState<ButtonStates>(NO_BUTTONS);
  const [editor, setEditor] = useState<EditorTarget | null>(null);
  const [finding, setFinding] = useState(false);
  const [removing, setRemoving] = useState(false);
  const treeRef = useRef<readonly CategoryNode[] | null>(null);
  const selectionRef = useRef<TreeSelection | null>(null);
  const sessionRef = useRef<SessionState | null>(session);
  const mountedRef = useRef(false);
  const categoriesSeqRef = useRef(0);
  const folderSeqRef = useRef(new Map<string, number>());
  const removingRef = useRef(false);

  useEffect(() => {
    sessionRef.current = session;
  });

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  /** Replaces the tree with `update` of it; does nothing before the categories are read. */
  const updateTree = useCallback(
    (update: (current: readonly CategoryNode[]) => readonly CategoryNode[]): void => {
      const current = treeRef.current;
      if (current === null) {
        return;
      }
      const next = update(current);
      if (next !== current) {
        treeRef.current = next;
        setTreeState(next);
      }
    },
    [],
  );

  const setSelection = useCallback((next: TreeSelection | null): void => {
    selectionRef.current = next;
    setSelectionState(next);
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

  /** Port of ItemsListByCategoryForm.loadCategoriesInList. */
  const loadCategories = useCallback(async (): Promise<void> => {
    categoriesSeqRef.current += 1;
    const seq = categoriesSeqRef.current;
    const isCurrent = (): boolean => mountedRef.current && seq === categoriesSeqRef.current;

    let rows: ItemCategoryRow[] | undefined;
    try {
      rows = await api.get<ItemCategoryRow[] | undefined>(ITEM_CATEGORIES_PATH);
    } catch (error: unknown) {
      if (isCurrent()) {
        if (treeRef.current === null) {
          treeRef.current = [];
          setTreeState(treeRef.current);
        }
        await reportFailure(error);
      }
      return;
    }

    if (!isCurrent()) {
      return;
    }
    treeRef.current = (Array.isArray(rows) ? rows : []).map(newCategoryNode);
    setTreeState(treeRef.current);
    setSelection(null);
  }, [reportFailure, setSelection]);

  useEffect(() => {
    void loadCategories();
  }, [loadCategories]);

  /** Deselects an entry under the folder `kind` of the category `categoryId`. */
  const deselectInFolder = useCallback(
    (categoryId: number, kind: ItemKind): void => {
      if (isInFolder(selectionRef.current, categoryId, kind)) {
        setSelection(null);
      }
    },
    [setSelection],
  );

  /** Returns the next read number of the folder, which makes the answers to earlier reads of it ignored. */
  const nextFolderSeq = useCallback((categoryId: number, kind: ItemKind): number => {
    const key = folderKey(categoryId, kind);
    const seq = (folderSeqRef.current.get(key) ?? 0) + 1;
    folderSeqRef.current.set(key, seq);
    return seq;
  }, []);

  /**
   * Port of ItemsListByCategoryForm.loadItemsInList: opens the folder and reads its category's items; only the answer
   * to the latest read of the folder is listed. A failed read closes the folder and reports the failure, a 404 as the
   * exception outcome of error 4.
   */
  const loadFolder = useCallback(
    async (categoryId: number, kind: ItemKind): Promise<void> => {
      const seq = nextFolderSeq(categoryId, kind);
      const isCurrent = (): boolean =>
        mountedRef.current && folderSeqRef.current.get(folderKey(categoryId, kind)) === seq;

      updateTree((current) =>
        updateFolderIn(current, categoryId, kind, (folder) => ({
          expanded: true,
          loading: true,
          entries: folder.expanded ? folder.entries : [],
        })),
      );

      let response: ItemsByCategoryResponse | undefined;
      try {
        response = await api.get<ItemsByCategoryResponse | undefined>(itemsByCategoryPath(categoryId));
      } catch (error: unknown) {
        if (!isCurrent()) {
          return;
        }
        updateTree((current) => updateFolderIn(current, categoryId, kind, () => CLOSED_FOLDER));
        deselectInFolder(categoryId, kind);
        await reportFailure(isNotFound(error) ? objectNotFoundOutcome() : error);
        return;
      }

      if (!isCurrent()) {
        return;
      }
      const entries = folderEntries(response?.items ?? [], kind, sessionRef.current);
      updateTree((current) =>
        updateFolderIn(current, categoryId, kind, () => ({ expanded: true, loading: false, entries })),
      );
      deselectInFolder(categoryId, kind);
    },
    [deselectInFolder, nextFolderSeq, reportFailure, updateTree],
  );

  /** Port of ItemsListByCategoryForm.removeItemsFromList: closes the folder, which then holds `$NoItemsAvailable`. */
  const closeFolder = useCallback(
    (categoryId: number, kind: ItemKind): void => {
      nextFolderSeq(categoryId, kind);
      updateTree((current) => updateFolderIn(current, categoryId, kind, () => CLOSED_FOLDER));
      deselectInFolder(categoryId, kind);
    },
    [deselectInFolder, nextFolderSeq, updateTree],
  );

  /** Reads the folder's items again when the folder is open; does nothing for a closed folder. */
  const refreshFolder = useCallback(
    async (categoryId: number, kind: ItemKind): Promise<void> => {
      const folder = treeRef.current?.find((node) => node.id === categoryId)?.folders[kind];
      if (folder?.expanded === true) {
        await loadFolder(categoryId, kind);
      }
    },
    [loadFolder],
  );

  /** Port of ItemsListByCategoryForm.doRefreshList: reads every open folder again, under open or closed categories. */
  const refreshOpenFolders = useCallback(async (): Promise<void> => {
    const reads: Promise<void>[] = [];
    for (const node of treeRef.current ?? []) {
      for (const kind of FOLDER_KINDS) {
        if (node.folders[kind].expanded) {
          reads.push(loadFolder(node.id, kind));
        }
      }
    }
    await Promise.all(reads);
  }, [loadFolder]);

  /** Takes the entries of the item `itemId` off the tree and deselects it. */
  const takeItemOff = useCallback(
    (itemId: number): void => {
      updateTree((current) => withoutItem(current, itemId));
      const current = selectionRef.current;
      if (current?.type === 'item' && current.row.item.id === itemId) {
        setSelection(null);
      }
    },
    [setSelection, updateTree],
  );

  /** Takes the category `categoryId` off the tree with its folders, and deselects a row under it. */
  const takeCategoryOff = useCallback(
    (categoryId: number): void => {
      for (const kind of FOLDER_KINDS) {
        nextFolderSeq(categoryId, kind);
      }
      updateTree((current) =>
        current.some((node) => node.id === categoryId) ? current.filter((node) => node.id !== categoryId) : current,
      );
      if (selectionRef.current?.categoryId === categoryId) {
        setSelection(null);
      }
    },
    [nextFolderSeq, setSelection, updateTree],
  );

  /** Port of ItemsListByCategoryForm.synchronizeForm. */
  const handleEntityChanged = useCallback(
    (message: EntityChangedMessage): Promise<void> | undefined => {
      if (ITEM_ENTITY_TYPES.has(message.entityType)) {
        if (message.kind === 'deleted') {
          takeItemOff(message.id);
          return undefined;
        }
        return refreshOpenFolders();
      }
      if (message.entityType === 'ItemCategory' && message.kind === 'deleted') {
        takeCategoryOff(message.id);
      }
      return undefined;
    },
    [refreshOpenFolders, takeCategoryOff, takeItemOff],
  );

  useEffect(() => onEntityChanged(WATCHED_ENTITY_TYPES, handleEntityChanged), [handleEntityChanged]);

  /** Port of ItemsListByCategoryForm.lstItems_click. */
  const selectRow = (row: TreeRow): void => {
    if (removingRef.current) {
      return;
    }
    setSelection(row.selection);
    setButtons(buttonStatesFor(row.selection));
  };

  /** Port of ItemsListByCategoryForm.doOpenCloseItemFolder for a category or folder row. */
  const toggleRow = (row: TreeRow): void => {
    if (removingRef.current) {
      return;
    }
    const target = row.selection;
    if (target.type === 'category') {
      updateTree((current) =>
        current.map((node) => (node.id === target.categoryId ? { ...node, expanded: !node.expanded } : node)),
      );
      return;
    }
    if (target.type !== 'folder') {
      return;
    }
    const folder = treeRef.current?.find((node) => node.id === target.categoryId)?.folders[target.kind];
    if (folder === undefined) {
      return;
    }
    if (folder.expanded) {
      closeFolder(target.categoryId, target.kind);
    } else {
      void loadFolder(target.categoryId, target.kind);
    }
  };

  /** Port of ItemsListByCategoryForm.doAdd: opens the editor of the selected folder's kind with its category. */
  const addItem = (): void => {
    const current = selectionRef.current;
    if (removingRef.current || current === null || (current.type !== 'folder' && current.type !== 'item')) {
      return;
    }
    setEditor({ mode: 'add', kind: current.kind, categoryId: current.categoryId });
  };

  /** Port of ItemsListByCategoryForm.doEdit: opens the editor of the item's kind for the item. */
  const editItem = (target: TreeSelection | null): void => {
    if (removingRef.current || target?.type !== 'item') {
      return;
    }
    setEditor({ mode: 'edit', kind: itemKind(target.row.item), itemId: target.row.item.id });
  };

  /** Port of ItemsListByCategoryForm.lstItems_dblClick for an item row. */
  const activateRow = (row: TreeRow): void => {
    editItem(row.selection);
  };

  /** Unmounts the editor; a successful OK refreshes the folders as ItemsListByCategoryForm.doAdd or doEdit does. */
  const closeEditor = (target: EditorTarget, saved: boolean): void => {
    setEditor(null);
    if (!saved) {
      return;
    }
    if (target.mode === 'add') {
      void refreshFolder(target.categoryId, target.kind);
      return;
    }
    setButtons(NO_BUTTONS);
    void refreshOpenFolders();
  };

  const endRemove = (): void => {
    removingRef.current = false;
    if (mountedRef.current) {
      setRemoving(false);
    }
  };

  /** Port of ItemsListByCategoryForm.btnRemove_click followed by ErewhonBaseForm.doDelete. */
  const removeItem = async (): Promise<void> => {
    const current = selectionRef.current;
    if (removingRef.current || current?.type !== 'item') {
      return;
    }
    const { categoryId, kind } = current;
    const itemId = current.row.item.id;

    removingRef.current = true;
    setRemoving(true);
    takeItemOff(itemId);

    let result: OperationResult | undefined;
    try {
      result = await api.del<OperationResult | undefined>(itemPath(itemId));
    } catch (error: unknown) {
      endRemove();
      await reportFailure(isNotFound(error) ? objectNotFoundOutcome() : error);
      return;
    }

    endRemove();
    if (!mountedRef.current) {
      return;
    }
    if (result !== undefined && !result.succeeded) {
      await show(displayErrorsOptions(result));
      if (!mountedRef.current) {
        return;
      }
    }
    await loadFolder(categoryId, kind);
  };

  /** Port of ItemsListByCategoryForm.btnFind_click. */
  const openFind = (): void => {
    if (!removingRef.current) {
      setFinding(true);
    }
  };

  /** Port of the part of ItemsListByCategoryForm.doFindItem that follows a found item. */
  const revealItem = async (row: ItemListRow): Promise<void> => {
    const categoryId = row.item.itemCategoryId;
    const kind = itemKind(row.item);
    const node = treeRef.current?.find((candidate) => candidate.id === categoryId);
    if (node === undefined) {
      return;
    }

    if (!node.expanded) {
      updateTree((current) =>
        current.map((candidate) => (candidate.id === categoryId ? { ...candidate, expanded: true } : candidate)),
      );
    }
    if (!node.folders[kind].expanded) {
      await loadFolder(categoryId, kind);
    }
    if (!mountedRef.current) {
      return;
    }

    const folder = treeRef.current?.find((candidate) => candidate.id === categoryId)?.folders[kind];
    const entry = folder?.entries.find(
      (candidate) => candidate.type === 'item' && candidate.row.item.id === row.item.id,
    );
    setSelection(entry?.type === 'item' ? { type: 'item', categoryId, kind, row: entry.row } : null);
  };

  const rows = tree === null ? [] : visibleRows(tree);
  const busy =
    tree === null || tree.some((node) => FOLDER_KINDS.some((kind) => node.folders[kind].loading));
  const selectedKey = selection === null ? null : selectionKey(selection);

  return (
    <section aria-labelledby={headingId} aria-busy={removing} style={removing ? BUSY_SECTION_STYLE : SECTION_STYLE}>
      <h2 id={headingId} style={HEADING_STYLE}>
        {msg('ItemsByCategory')}
      </h2>
      <CategoryTree
        labelledBy={headingId}
        rows={rows}
        selectedKey={selectedKey}
        busy={busy}
        onSelect={selectRow}
        onToggle={toggleRow}
        onActivate={activateRow}
      />
      <div style={BUTTON_BAR_STYLE}>
        <div style={BUTTON_GROUP_STYLE}>
          <DialogButton aria-disabled={removing} onClick={openFind}>
            {FIND_CAPTION}
          </DialogButton>
        </div>
        <div style={BUTTON_GROUP_STYLE}>
          <DialogButton aria-disabled={removing || !buttons.add} onClick={addItem}>
            {ADD_CAPTION}
          </DialogButton>
          <DialogButton aria-disabled={removing || !buttons.edit} onClick={() => editItem(selectionRef.current)}>
            {EDIT_CAPTION}
          </DialogButton>
          <DialogButton aria-disabled={removing || !buttons.remove} onClick={() => void removeItem()}>
            {REMOVE_CAPTION}
          </DialogButton>
          <DialogButton onClick={() => onClose?.()}>{CLOSE_CAPTION}</DialogButton>
        </div>
      </div>
      {finding && <FindItemDialog onFound={(row) => void revealItem(row)} onClose={() => setFinding(false)} />}
      {editor !== null &&
        (editor.kind === 'retail' ? (
          <EditRetailItemDialog
            itemId={editor.mode === 'edit' ? editor.itemId : null}
            itemCategoryId={editor.mode === 'add' ? editor.categoryId : null}
            onClose={(saved) => closeEditor(editor, saved)}
          />
        ) : (
          <EditTenderItemDialog
            itemId={editor.mode === 'edit' ? editor.itemId : null}
            itemCategoryId={editor.mode === 'add' ? editor.categoryId : null}
            onClose={(saved) => closeEditor(editor, saved)}
          />
        ))}
      {messageBox}
    </section>
  );
}
