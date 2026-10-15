import type { CSSProperties, KeyboardEvent, ReactNode } from 'react';

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
  /** Called with the row when it is clicked, or when Space or Enter is pressed on it. */
  onSelect?: (row: T) => void;
  /** Called with the row when it is double-clicked, or when Enter is pressed on it. */
  onDoubleClick?: (row: T) => void;
  /** Returns the row's text style; the single value returned is the style applied. */
  rowStyle?: (row: T) => RowStyle;
}

const ROW_HEIGHT = 25;
const CELL_BORDER = '1px solid #ccc';
const HEADER_BACKGROUND = 'rgb(237, 213, 211)';
const SELECTED_BACKGROUND = 'rgb(204, 228, 247)';

/*
 * BLITZY [A11Y]: red text measures 4.0:1 on white and 3.0:1 on the selected background, and green
 * text 3.9:1 on the selected background, below WCAG AA 4.5:1; flagged for designer review.
 */
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

/**
 * List table with one header row and one body row per entry of `rows`, in the order given.
 * Whole rows are selected: clicking a row calls `onSelect`, double-clicking calls `onDoubleClick`.
 * The row whose key equals `selectedKey` is highlighted and has `aria-selected="true"` and
 * `aria-current="true"`; every other body row has `aria-selected="false"`. Each body row
 * carries its `rowStyle` value in `data-row-style` and renders that style's text colour.
 * Empty `rows` render the header row only.
 */
export function DataTable<T>(props: DataTableProps<T>) {
  const { caption, columns, rows, rowKey, selectedKey, onSelect, onDoubleClick, rowStyle } = props;
  const interactive = onSelect !== undefined || onDoubleClick !== undefined;

  function handleRowKeyDown(event: KeyboardEvent<HTMLTableRowElement>, row: T) {
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
    }
  }

  return (
    <table style={TABLE_STYLE}>
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
        {rows.map((row) => {
          const key = rowKey(row);
          const selected = selectedKey != null && key === selectedKey;
          const style = rowStyle?.(row);
          return (
            <tr
              key={key}
              aria-selected={selected}
              aria-current={selected ? 'true' : undefined}
              data-row-style={style}
              tabIndex={interactive ? 0 : undefined}
              onClick={() => onSelect?.(row)}
              onDoubleClick={() => onDoubleClick?.(row)}
              onKeyDown={interactive ? (event) => handleRowKeyDown(event, row) : undefined}
              style={{
                height: ROW_HEIGHT,
                cursor: onSelect !== undefined ? 'pointer' : undefined,
                background: selected ? SELECTED_BACKGROUND : undefined,
                color: style !== undefined ? ROW_STYLE_COLORS[style] : undefined,
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
