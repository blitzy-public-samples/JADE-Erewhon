import { useEffect, useState } from 'react';
import type { CSSProperties, FocusEvent, KeyboardEvent, MouseEvent, ReactNode } from 'react';

/** One column of a {@link DataTable}. */
export interface DataTableColumn<T> {
  /** Key of the column, unique within the table. */
  key: string;
  /** Header cell content: a string, or any React node such as a sort button. */
  header: ReactNode;
  /** Renders this column's cell for one row. */
  render: (row: T) => ReactNode;
}

/**
 * Text style of a body row: `'closed'` renders red text, `'tenderSale'` and `'sold'` render
 * green text, and `undefined` keeps the table's black text.
 */
export type RowStyle = 'closed' | 'tenderSale' | 'sold' | undefined;

/** Properties of a {@link DataTable}. */
export interface DataTableProps<T> {
  /** Caption of the table, which is also its accessible name. */
  caption?: string;
  /** Columns, rendered left to right in this order. */
  columns: DataTableColumn<T>[];
  /** Body rows, rendered top to bottom in this order. */
  rows: T[];
  /** Returns the row's unique key, which is also compared with `selectedKey`. */
  rowKey: (row: T) => number | string;
  /** Key of the selected row; `null` or `undefined` when no row is selected. */
  selectedKey?: number | string | null;
  /**
   * Called with the row when it is clicked, when Space or Enter is pressed on it, or when it is
   * reached with ArrowUp or ArrowDown.
   */
  onSelect?: (row: T) => void;
  /** Called with the row when it is double-clicked, or when Enter is pressed on it. */
  onDoubleClick?: (row: T) => void;
  /** Returns the row's text style; the single value returned is the style applied. */
  rowStyle?: (row: T) => RowStyle;
  /**
   * When `true`, rows ignore clicks, double-clicks and keys and are not focusable, every body
   * row has `aria-disabled="true"`, and the table is drawn at 0.6 opacity; the selected row,
   * `aria-selected`, `aria-current`, `data-row-style` and text colours are unchanged. Controls
   * that columns render inside the cells keep their own enabled state and remain focusable and
   * operable, while the row's `aria-disabled` also exposes them as disabled to assistive
   * technologies.
   */
  disabled?: boolean;
}

const ROW_HEIGHT = 25;
const CELL_BORDER = '1px solid #ccc';
const HEADER_BACKGROUND = 'rgb(237, 213, 211)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';
const HOVER_BACKGROUND = 'rgb(242, 242, 242)';
const PRESSED_BACKGROUND = 'rgb(230, 230, 230)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const FOCUS_OUTLINE_OFFSET = '-2px';

const ROW_STYLE_COLORS: Record<NonNullable<RowStyle>, string> = {
  closed: 'red',
  tenderSale: 'green',
  sold: 'green',
};

const TABLE_STYLE: CSSProperties = {
  borderCollapse: 'collapse',
  background: 'white',
  color: 'black',
};

const DISABLED_TABLE_STYLE: CSSProperties = {
  ...TABLE_STYLE,
  opacity: 0.6,
};

const HEADER_ROW_STYLE: CSSProperties = {
  background: HEADER_BACKGROUND,
  height: ROW_HEIGHT,
};

const CELL_STYLE: CSSProperties = {
  border: CELL_BORDER,
  paddingBlock: '0.125rem',
  paddingInline: '0.375rem',
  textAlign: 'start',
  fontWeight: 'normal',
};

/** Returns the selected, pressed or hovered row background, in that order, or `undefined`. */
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

/** Returns whether `element` matches `:focus-visible`, and `true` when the selector throws. */
function matchesFocusVisible(element: Element): boolean {
  try {
    return element.matches(':focus-visible');
  } catch {
    return true;
  }
}

/**
 * List table with one header row and one body row per entry of `rows`, in the order given.
 * Whole rows are selected: clicking a row calls `onSelect`, double-clicking calls `onDoubleClick`.
 * The row whose key equals `selectedKey` is highlighted and has `aria-selected="true"` and
 * `aria-current="true"`; every other body row has `aria-selected="false"`. Each body row
 * carries its `rowStyle` value in `data-row-style` and renders that style's text colour.
 * Empty `rows` render the header row only.
 *
 * When `onSelect` or `onDoubleClick` is given and `disabled` is not `true`, one body row is the
 * tab stop with `tabIndex` 0 and every other body row has `tabIndex` -1. The tab stop is the row
 * focused last while its key is still in `rows`, otherwise the selected row, otherwise the first
 * row. On a focused row, Space calls `onSelect`; Enter calls `onSelect` and then `onDoubleClick`;
 * ArrowDown and ArrowUp, pressed without Ctrl, Alt or Meta, focus the next or previous row and
 * call `onSelect` with it, and do nothing on the last or first row. Keys pressed in a control
 * inside a row are left to that control. A row that is not selected has a rgb(230, 230, 230)
 * background while the primary mouse button is held on it and a rgb(242, 242, 242) background
 * while the pointer is over it; a row focused with `:focus-visible` has a 2px rgb(0, 80, 160)
 * outline inset by 2px; text colours stay unchanged. When `disabled` is `true`, rows respond to
 * no input, show none of these states and none is a tab stop.
 */
export function DataTable<T>(props: DataTableProps<T>) {
  const { caption, columns, rows, rowKey, selectedKey, onSelect, onDoubleClick, rowStyle, disabled } =
    props;
  const interactive = !disabled && (onSelect !== undefined || onDoubleClick !== undefined);
  const [focusedKey, setFocusedKey] = useState<number | string | null>(null);
  const [hoveredKey, setHoveredKey] = useState<number | string | null>(null);
  const [pressedKey, setPressedKey] = useState<number | string | null>(null);
  const [focusVisibleKey, setFocusVisibleKey] = useState<number | string | null>(null);

  const rowKeys = rows.map((row) => rowKey(row));
  const tabStopKey =
    focusedKey !== null && rowKeys.includes(focusedKey)
      ? focusedKey
      : selectedKey != null && rowKeys.includes(selectedKey)
        ? selectedKey
        : rowKeys[0];
  const hoveredListed = hoveredKey !== null && rowKeys.includes(hoveredKey);
  const pressedListed = pressedKey !== null && rowKeys.includes(pressedKey);
  const focusVisibleListed = focusVisibleKey !== null && rowKeys.includes(focusVisibleKey);

  useEffect(() => {
    if (!interactive || !hoveredListed) {
      setHoveredKey(null);
    }
    if (!interactive || !pressedListed) {
      setPressedKey(null);
    }
    if (!interactive || !focusVisibleListed) {
      setFocusVisibleKey(null);
    }
  }, [interactive, hoveredListed, pressedListed, focusVisibleListed]);

  function handleRowKeyDown(event: KeyboardEvent<HTMLTableRowElement>, row: T, index: number) {
    if (event.target !== event.currentTarget) {
      return;
    }
    if (event.key === ' ') {
      event.preventDefault();
      onSelect?.(row);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      onSelect?.(row);
      onDoubleClick?.(row);
    } else if (
      (event.key === 'ArrowDown' || event.key === 'ArrowUp') &&
      !event.ctrlKey &&
      !event.altKey &&
      !event.metaKey
    ) {
      event.preventDefault();
      const forward = event.key === 'ArrowDown';
      const adjacentIndex = forward ? index + 1 : index - 1;
      const adjacentElement = forward
        ? event.currentTarget.nextElementSibling
        : event.currentTarget.previousElementSibling;
      if (
        adjacentIndex < 0 ||
        adjacentIndex >= rows.length ||
        !(adjacentElement instanceof HTMLTableRowElement)
      ) {
        return;
      }
      adjacentElement.focus();
      onSelect?.(rows[adjacentIndex]);
    }
  }

  function handleRowFocus(event: FocusEvent<HTMLTableRowElement>, key: number | string) {
    if (event.target !== event.currentTarget) {
      return;
    }
    setFocusedKey(key);
    setFocusVisibleKey(matchesFocusVisible(event.currentTarget) ? key : null);
  }

  function handleRowBlur(event: FocusEvent<HTMLTableRowElement>) {
    if (event.target !== event.currentTarget) {
      return;
    }
    setFocusVisibleKey(null);
  }

  function handleRowMouseDown(event: MouseEvent<HTMLTableRowElement>, key: number | string) {
    if (event.button === 0) {
      setPressedKey(key);
    }
  }

  function handleRowMouseLeave() {
    setHoveredKey(null);
    setPressedKey(null);
  }

  return (
    <table style={disabled ? DISABLED_TABLE_STYLE : TABLE_STYLE}>
      {caption !== undefined && <caption>{caption}</caption>}
      <thead>
        <tr style={HEADER_ROW_STYLE}>
          {columns.map((column) => (
            <th key={column.key} scope="col" style={CELL_STYLE}>
              {column.header}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => {
          const key = rowKey(row);
          const selected = selectedKey != null && key === selectedKey;
          const style = rowStyle?.(row);
          const focusVisible = interactive && key === focusVisibleKey;
          return (
            <tr
              key={key}
              aria-selected={selected}
              aria-current={selected ? 'true' : undefined}
              aria-disabled={disabled ? 'true' : undefined}
              data-row-style={style}
              tabIndex={interactive ? (key === tabStopKey ? 0 : -1) : undefined}
              onClick={interactive ? () => onSelect?.(row) : undefined}
              onDoubleClick={interactive ? () => onDoubleClick?.(row) : undefined}
              onKeyDown={interactive ? (event) => handleRowKeyDown(event, row, index) : undefined}
              onFocus={interactive ? (event) => handleRowFocus(event, key) : undefined}
              onBlur={interactive ? handleRowBlur : undefined}
              onMouseEnter={interactive ? () => setHoveredKey(key) : undefined}
              onMouseLeave={interactive ? handleRowMouseLeave : undefined}
              onMouseDown={interactive ? (event) => handleRowMouseDown(event, key) : undefined}
              onMouseUp={interactive ? () => setPressedKey(null) : undefined}
              style={{
                height: ROW_HEIGHT,
                cursor: interactive && onSelect !== undefined ? 'pointer' : undefined,
                background: rowBackground(
                  selected,
                  interactive && key === pressedKey,
                  interactive && key === hoveredKey,
                ),
                color: style !== undefined ? ROW_STYLE_COLORS[style] : undefined,
                outline: focusVisible ? FOCUS_OUTLINE : undefined,
                outlineOffset: focusVisible ? FOCUS_OUTLINE_OFFSET : undefined,
              }}
            >
              {columns.map((column) => (
                <td key={column.key} style={CELL_STYLE}>
                  {column.render(row)}
                </td>
              ))}
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
