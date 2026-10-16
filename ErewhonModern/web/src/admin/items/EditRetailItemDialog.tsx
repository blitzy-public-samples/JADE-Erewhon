// The item editors of the administration area: the shared item editor dialog for retail and tender items, and the
// retail item editor built on it.

import { useEffect, useId, useRef, useState } from 'react';
import type {
  ChangeEvent,
  CSSProperties,
  FocusEvent,
  KeyboardEvent,
  MouseEvent,
  ReactElement,
  ReactNode,
  Ref,
} from 'react';
import { ApiError, ErrorOutcomeError, api } from '../../api/client';
import type {
  AdminTenderSearchRequest,
  AgentDetailsResponse,
  AgentProjection,
  CountryRow,
  EditLockType,
  IsoDate,
  ItemCategoryRow,
  ItemDetailsResponse,
  ItemProjection,
  OperationResult,
  RegionRow,
  RetailItemRequest,
  TenderItemRequest,
  TenderListRow,
} from '../../api/types';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { PictureField } from '../../components/PictureField';
import { currencyFormat, decimalString } from '../../format';
import { msg } from '../../messages/messages';
import { useSession } from '../../session/SessionProvider';
import { useEditLock } from '../useEditLock';

/** Kind of item an {@link ItemEditorDialog} edits. */
export type ItemKind = 'retail' | 'tender';

/** Values of the kind-specific group sent with the shared item values on OK. */
export interface ItemKindValues {
  /** Retail price, or the reserve price of a tender item; 0 when the text is empty or not a decimal number. */
  price: number;
  /** Closure date of a tender item; null when no valid date is entered. Ignored for a retail item. */
  closureDate?: IsoDate | null;
}

/** Values passed to {@link ItemEditorDialogProps.renderKindFields}. */
export interface ItemKindFieldsContext {
  /** The item being edited; null for a new item. */
  item: ItemProjection | null;
  /** True when the item being edited has been sold. */
  sold: boolean;
  /** Marks the dialog changed; called on every user edit of a kind-specific field. */
  markChanged: () => void;
}

/** Properties of {@link ItemEditorDialog}. */
export interface ItemEditorDialogProps {
  /** Kind of item edited: selects the item endpoints and the soft edit lock type. */
  kind: ItemKind;
  /** Internal id of the item edited; null, undefined or not a positive integer adds a new item. */
  itemId?: number | null;
  /** Internal id of the category pre-selected for a new item; ignored for an existing item. */
  itemCategoryId?: number | null;
  /** Form caption, shown after `$Add` or `$Edit` in the dialog title. */
  caption: string;
  /** Caption of the group holding the kind-specific fields. */
  kindGroupCaption: string;
  /** Called once with true after a successful OK, and with false when the dialog closes otherwise. */
  onClose: (saved: boolean) => void;
  /**
   * Called once the item (null for a new item) and the reference lists have been read, before the fields are shown;
   * the dialog shows its fields once the returned promise resolves.
   */
  onLoad?: (item: ItemProjection | null) => void | Promise<void>;
  /** Renders the kind-specific fields inside the kind group. */
  renderKindFields: (context: ItemKindFieldsContext) => ReactNode;
  /** Returns the kind-specific values of the save request. */
  buildKindFields: () => ItemKindValues;
  /** Focuses the kind-specific field of a save result's focus field and returns true, or returns false. */
  focusKindField?: (focusField: string) => boolean;
}

/** Properties of {@link ItemEditorInput}. */
export interface ItemEditorInputProps {
  /** Caption of the label of the input. */
  label: string;
  /** Text of the input. */
  value: string;
  /** Receives the text as typed. */
  onChange: (value: string) => void;
  /** Disables the input. */
  disabled?: boolean;
  /** Maximum number of characters typed. */
  maxLength?: number;
  /** Input type; `'text'` when omitted. */
  type?: 'text' | 'date';
  /** Virtual keyboard hint of a text input. */
  inputMode?: 'text' | 'decimal' | 'numeric';
  /** Marks the input mandatory with `aria-required` and the red top-left corner. */
  mandatory?: boolean;
  /** Ref of the input element. */
  inputRef?: Ref<HTMLInputElement>;
}

/** Reference lists of the editor's selectors. */
interface EditorReference {
  /** The company's item categories in name order. */
  categories: ItemCategoryRow[];
  /** The company's countries in name order, each with its regions in name order. */
  countries: CountryRow[];
  /** The company's agents in name order; empty for an agent session. */
  agents: AgentProjection[];
}

/** Picture stored for the edited item. */
interface ExistingPhoto {
  blob: Blob;
  base64: string;
}

/** Everything read when the dialog opens. */
interface LoadedEditor {
  /** The edited item; null for a new item. */
  item: ItemProjection | null;
  reference: EditorReference;
  /** Agent label of the agent area of an agent session; empty for the administrator. */
  agentLabel: string;
  /** Text of the sold-state label. */
  dateSoldText: string;
  existingPhoto: ExistingPhoto | null;
}

/** Values of the shared fields, as shown. */
interface ItemFormValues {
  categoryId: number | null;
  prefix: string;
  codeNumber: string;
  countryId: number | null;
  regionId: number | null;
  /** Agent selected in the administrator's agent selector. */
  agentId: number | null;
  name: string;
  description: string;
  /** Base64 content of the picture chosen in this dialog; null when none has been chosen. */
  photoBase64: string | null;
}

/** One option of a selector. */
interface SelectOption {
  id: number;
  text: string;
}

const ITEMS_PATH = '/api/app/items';
const AGENTS_PATH = '/api/app/admin/agents';
const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';
const COUNTRIES_PATH = '/api/app/admin/countries';
const TENDER_SEARCH_PATH = '/api/app/admin/tenders/search';

const SAVE_PATHS: Readonly<Record<ItemKind, string>> = {
  retail: '/api/app/admin/retail-items',
  tender: '/api/app/admin/tender-items',
};

const LOCK_TYPES: Readonly<Record<ItemKind, EditLockType>> = {
  retail: 'RetailItem',
  tender: 'TenderItem',
};

const ITEM_DETAILS_CAPTION = 'Item Details';
const DESCRIPTION_CAPTION = 'Description';
const AGENT_CAPTION = 'Agent';
const AGENT_DETAILS_CAPTION = 'Agent details';
const CATEGORY_CAPTION = 'Category';
const PREFIX_CAPTION = 'Prefix';
const NUMBER_CAPTION = 'Number';
const COUNTRY_CAPTION = 'Country';
const REGION_CAPTION = 'Region';
const NAME_CAPTION = 'Name';
const DESC_CAPTION = 'Desc';
const DATE_SOLD_CAPTION = 'Date Sold';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';
const RETAIL_ITEM_CAPTION = 'Retail Sale Item';
const SALE_DETAILS_CAPTION = 'Sale Details';
const RETAIL_PRICE_CAPTION = 'Retail Price';

const PREFIX_MAX_LENGTH = 4;
const NUMBER_MAX_LENGTH = 10;
const NAME_MAX_LENGTH = 30;
const RETAIL_PRICE_MAX_LENGTH = 10;

const COMMA_SPACE = ', ';
const PERIOD_SPACE = '. ';
const SPACE = ' ';

/** Error number and text of the null-object-reference exception outcome. */
const NULL_REFERENCE_CODE = 1090;
const NULL_REFERENCE_TEXT = 'Null object reference';

const FOCUS_CODE_PREFIX = 'FOCUS: CodePrefix';
const FOCUS_CODE_NUMBER = 'FOCUS: CodeNumber';
const FOCUS_ITEM_CATEGORY = 'FOCUS: ItemCategory';
const FOCUS_REGION = 'FOCUS: Region';
const FOCUS_ITEM_NAME = 'FOCUS: ItemName';
const FOCUS_ITEM_DESCRIPTION = 'FOCUS: ItemDescription';
const FOCUS_AGENT = 'FOCUS: Agent';

const DECIMAL_TEXT = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/;

const FOCUSABLE_SELECTOR =
  'input:not(:disabled), select:not(:disabled), textarea:not(:disabled), button:not(:disabled), ' +
  '[tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const DISABLED_TEXT_COLOR = '#808080';
const MANDATORY_CORNER_COLOR = '#ED1C24';
const MANDATORY_CORNER_SIZE = '6px';
const GAP = '0.1875rem';
const GROUP_GAP = '0.5625rem';
const LABEL_COLUMN = 'minmax(3.75rem, max-content)';
const NUMBER_LABEL_COLUMN = 'minmax(3rem, max-content)';
const DESCRIPTION_LABEL_COLUMN = 'minmax(2.0625rem, max-content)';
const COMBO_WIDTH = '15.9375rem';

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
  gap: '0.5rem',
  boxSizing: 'border-box',
  inlineSize: '100%',
  maxInlineSize: '38rem',
  maxBlockSize: '100%',
  overflowY: 'auto',
  padding: '0.625rem',
  background: 'white',
  color: 'black',
  border: BORDER,
  borderRadius: '2px',
  boxShadow: '0 4px 16px rgba(0, 0, 0, 0.3)',
};

const BUSY_PANEL_STYLE: CSSProperties = { ...PANEL_STYLE, cursor: 'progress' };

const TITLE_STYLE: CSSProperties = {
  margin: 0,
  fontSize: '1rem',
  fontWeight: 'bold',
  overflowWrap: 'break-word',
};

const FIELDSET_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInlineStart: '0.75rem',
  paddingInlineEnd: '0.75rem',
};

const AGENT_GROUP_STYLE: CSSProperties = {
  ...FIELDSET_STYLE,
  display: 'grid',
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, ${COMBO_WIDTH})`,
  alignItems: 'center',
  columnGap: GAP,
};

const AGENT_LABEL_STYLE: CSSProperties = {
  margin: 0,
  minInlineSize: 0,
  overflowWrap: 'break-word',
};

const BODY_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  gap: GROUP_GAP,
};

const DETAILS_GROUP_STYLE: CSSProperties = {
  ...FIELDSET_STYLE,
  flex: '1 1 21.1875rem',
  display: 'grid',
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, ${COMBO_WIDTH})`,
  alignItems: 'center',
  columnGap: GAP,
  rowGap: GAP,
};

const CODE_ROW_STYLE: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: `3.5625rem minmax(0, 1fr) ${NUMBER_LABEL_COLUMN} minmax(0, 8.0625rem)`,
  alignItems: 'center',
  columnGap: GAP,
  minInlineSize: 0,
};

const DESCRIPTION_GROUP_STYLE: CSSProperties = {
  ...FIELDSET_STYLE,
  gridColumn: '1 / -1',
  display: 'grid',
  gridTemplateColumns: `${DESCRIPTION_LABEL_COLUMN} minmax(0, ${COMBO_WIDTH})`,
  alignItems: 'start',
  columnGap: GAP,
  rowGap: GAP,
};

const SIDE_COLUMN_STYLE: CSSProperties = {
  flex: '0 0 auto',
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'stretch',
  gap: GROUP_GAP,
  minInlineSize: 0,
};

const KIND_GROUP_STYLE: CSSProperties = {
  ...FIELDSET_STYLE,
  display: 'grid',
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, 6.9375rem)`,
  alignItems: 'center',
  columnGap: GAP,
  rowGap: GAP,
};

/** One kind-specific field: a row of the kind group's grid, its label and input in the group's two columns. */
const KIND_FIELD_STYLE: CSSProperties = {
  display: 'grid',
  gridColumn: '1 / -1',
  gridTemplateColumns: 'subgrid',
  alignItems: 'center',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const NUMBER_LABEL_STYLE: CSSProperties = { ...LABEL_STYLE, gridColumn: 3 };

const FOOTER_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: GROUP_GAP,
};

const DATE_SOLD_STYLE: CSSProperties = {
  flex: '1 1 21.1875rem',
  minInlineSize: 0,
  minBlockSize: '1.9375rem',
  margin: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const BUTTON_ROW_STYLE: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  justifyContent: 'flex-end',
  gap: '0.375rem',
  marginInlineStart: 'auto',
};

const CONTROL_CELL_STYLE: CSSProperties = {
  position: 'relative',
  minInlineSize: 0,
};

const CONTROL_STYLE: CSSProperties = {
  boxSizing: 'border-box',
  display: 'block',
  inlineSize: '100%',
  minInlineSize: 0,
  minBlockSize: '1.3125rem',
  margin: 0,
  paddingBlock: 0,
  paddingInline: '0.25rem',
  borderWidth: '1px',
  borderStyle: 'solid',
  borderColor: INPUT_BORDER_COLOR,
  backgroundColor: '#FFFFFF',
  color: '#000000',
  font: 'inherit',
};

const TEXT_AREA_STYLE: CSSProperties = {
  ...CONTROL_STYLE,
  blockSize: '6rem',
  paddingBlock: '0.125rem',
  resize: 'none',
  overflowY: 'auto',
  overflowWrap: 'break-word',
};

const CONTROL_HOVER_STYLE: CSSProperties = { borderColor: INPUT_HOVER_BORDER_COLOR };

const CONTROL_PRESSED_STYLE: CSSProperties = { borderColor: INPUT_PRESSED_BORDER_COLOR };

const CONTROL_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

const CONTROL_DISABLED_STYLE: CSSProperties = { color: DISABLED_TEXT_COLOR };

const MANDATORY_CORNER_STYLE: CSSProperties = {
  position: 'absolute',
  insetBlockStart: 0,
  insetInlineStart: 0,
  inlineSize: 0,
  blockSize: 0,
  borderBlockStart: `${MANDATORY_CORNER_SIZE} solid ${MANDATORY_CORNER_COLOR}`,
  borderInlineEnd: `${MANDATORY_CORNER_SIZE} solid transparent`,
  pointerEvents: 'none',
};

/** Event handlers of a control that track its hover, pressed and focus-visible states. */
interface ControlHandlers {
  onMouseEnter?: () => void;
  onMouseLeave?: () => void;
  onMouseDown?: (event: MouseEvent<HTMLElement>) => void;
  onMouseUp?: () => void;
  onFocus?: (event: FocusEvent<HTMLElement>) => void;
  onKeyUp?: (event: KeyboardEvent<HTMLElement>) => void;
  onBlur: () => void;
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
 * Tracks the hover, pressed and focus-visible states of one control and returns its style and handlers. An enabled
 * control has a rgb(128, 128, 128) border while the pointer is over it, a rgb(0, 80, 160) border while the primary
 * mouse button is held on it, and a 2px rgb(0, 80, 160) outline when focused with `:focus-visible`, checked again on
 * every key release; a disabled control shows grey text and none of these states.
 */
function useControlState(disabled: boolean): {
  style: (base: CSSProperties) => CSSProperties;
  handlers: ControlHandlers;
} {
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

  const style = (base: CSSProperties): CSSProperties => {
    if (disabled) {
      return { ...base, ...CONTROL_DISABLED_STYLE };
    }
    let next = base;
    if (pressed) {
      next = { ...base, ...CONTROL_PRESSED_STYLE };
    } else if (hovered) {
      next = { ...base, ...CONTROL_HOVER_STYLE };
    }
    return focusVisible ? { ...next, ...CONTROL_FOCUS_STYLE } : next;
  };

  const onBlur = (): void => setFocusVisible(false);

  if (disabled) {
    return { style, handlers: { onBlur } };
  }

  const updateFocusVisible = (event: FocusEvent<HTMLElement> | KeyboardEvent<HTMLElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  return {
    style,
    handlers: {
      onMouseEnter: () => setHovered(true),
      onMouseLeave: () => {
        setHovered(false);
        setPressed(false);
      },
      onMouseDown: (event: MouseEvent<HTMLElement>) => {
        if (event.button === 0) {
          setPressed(true);
        }
      },
      onMouseUp: () => setPressed(false),
      onFocus: updateFocusVisible,
      onKeyUp: updateFocusVisible,
      onBlur,
    },
  };
}

/** Grid cell of a control, with the red top-left corner of a mandatory control. */
function ControlCell({ mandatory, children }: { mandatory: boolean; children: ReactNode }): ReactElement {
  return (
    <span style={CONTROL_CELL_STYLE}>
      {children}
      {mandatory && <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />}
    </span>
  );
}

/** Properties of {@link TextInput}. */
interface TextInputProps {
  id: string;
  value: string;
  /** Receives the text as typed; absent for a read-only input. */
  onChange?: (value: string) => void;
  disabled: boolean;
  readOnly?: boolean;
  maxLength?: number;
  type?: 'text' | 'date';
  inputMode?: 'text' | 'decimal' | 'numeric';
  mandatory?: boolean;
  inputRef?: Ref<HTMLInputElement>;
}

/** Single-line input in its grid cell. */
function TextInput(props: TextInputProps): ReactElement {
  const {
    id,
    value,
    onChange,
    disabled,
    readOnly = false,
    maxLength,
    type = 'text',
    inputMode,
    mandatory = false,
    inputRef,
  } = props;
  const { style, handlers } = useControlState(disabled);

  return (
    <ControlCell mandatory={mandatory}>
      <input
        ref={inputRef}
        id={id}
        type={type}
        autoComplete="off"
        spellCheck={false}
        value={value}
        maxLength={maxLength}
        inputMode={inputMode}
        disabled={disabled}
        readOnly={readOnly}
        aria-required={mandatory ? 'true' : undefined}
        style={style(CONTROL_STYLE)}
        onChange={(event: ChangeEvent<HTMLInputElement>) => onChange?.(event.target.value)}
        {...handlers}
      />
    </ControlCell>
  );
}

/** Properties of {@link TextArea}. */
interface TextAreaProps {
  id: string;
  value: string;
  onChange: (value: string) => void;
  disabled: boolean;
  mandatory: boolean;
  textAreaRef?: Ref<HTMLTextAreaElement>;
}

/** Multi-line input in its grid cell. */
function TextArea({ id, value, onChange, disabled, mandatory, textAreaRef }: TextAreaProps): ReactElement {
  const { style, handlers } = useControlState(disabled);

  return (
    <ControlCell mandatory={mandatory}>
      <textarea
        ref={textAreaRef}
        id={id}
        value={value}
        disabled={disabled}
        aria-required={mandatory ? 'true' : undefined}
        style={style(TEXT_AREA_STYLE)}
        onChange={(event: ChangeEvent<HTMLTextAreaElement>) => onChange(event.target.value)}
        {...handlers}
      />
    </ControlCell>
  );
}

/** Properties of {@link SelectInput}. */
interface SelectInputProps {
  id: string;
  /** Id of the selected entry; null selects the empty entry. */
  value: number | null;
  /** Entries after the empty entry, in display order. */
  options: readonly SelectOption[];
  /** Receives the id of the chosen entry, or null for the empty entry. */
  onChange: (id: number | null) => void;
  disabled: boolean;
  selectRef?: Ref<HTMLSelectElement>;
}

/** Drop-down list with an empty first entry, in its grid cell. */
function SelectInput({ id, value, options, onChange, disabled, selectRef }: SelectInputProps): ReactElement {
  const { style, handlers } = useControlState(disabled);

  return (
    <ControlCell mandatory={false}>
      <select
        ref={selectRef}
        id={id}
        value={value === null ? '' : String(value)}
        disabled={disabled}
        style={style(CONTROL_STYLE)}
        onChange={(event: ChangeEvent<HTMLSelectElement>) => {
          const text = event.target.value;
          onChange(text === '' ? null : Number(text));
        }}
        {...handlers}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.id} value={String(option.id)}>
            {option.text}
          </option>
        ))}
      </select>
    </ControlCell>
  );
}

/**
 * One labelled input of the kind-specific group of an item editor: the right-aligned caption beside the input, with
 * the shared item editor control appearance.
 */
export function ItemEditorInput(props: ItemEditorInputProps): ReactElement {
  const { label, value, onChange, disabled = false, maxLength, type, inputMode, mandatory, inputRef } = props;
  const id = useId();

  return (
    <div style={KIND_FIELD_STYLE}>
      <label htmlFor={id} style={LABEL_STYLE}>
        {label}
      </label>
      <TextInput
        id={id}
        value={value}
        onChange={onChange}
        disabled={disabled}
        maxLength={maxLength}
        type={type}
        inputMode={inputMode}
        mandatory={mandatory}
        inputRef={inputRef}
      />
    </div>
  );
}

/** Returns true when `id` is a positive safe integer, the id of an existing row. */
function isRowId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** Returns the request path of GET /api/app/items/{id}. */
function itemPath(itemId: number): string {
  return `${ITEMS_PATH}/${itemId}`;
}

/** Returns the request path of GET /api/app/items/{id}/photo. */
function photoPath(itemId: number): string {
  return `${itemPath(itemId)}/photo`;
}

/**
 * Returns the price typed in `text`: the trimmed text read as a decimal number with an optional sign and no digit
 * grouping, e.g. ` 123.45 ` → 123.45, or 0 for empty or non-numeric text such as `1,000` or `abc`.
 */
export function parseItemPrice(text: string): number {
  const trimmed = text.trim();
  return DECIMAL_TEXT.test(trimmed) ? Number(trimmed) : 0;
}

/** Returns the integer at the start of `text`, or 0 when it holds none, e.g. `''` → 0, `'42'` → 42. */
function parseCodeNumber(text: string): number {
  const value = Number.parseInt(text, 10);
  return Number.isSafeInteger(value) ? value : 0;
}

/** Returns `text` with its first character in upper case. */
function firstCharToUpper(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** Returns the agent label "<name>, <street>, <city>, <country>". */
function agentLabelText(agent: AgentProjection): string {
  return [agent.name, agent.street, agent.city, agent.country].join(COMMA_SPACE);
}

/** Returns the base64 text of the bytes of `blob`. */
async function blobToBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  const chunkSize = 0x8000;
  let binary = '';
  for (let start = 0; start < bytes.length; start += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(start, start + chunkSize));
  }
  return btoa(binary);
}

/**
 * Returns the first tender by highest offer, then earliest tender time, or null when `rows` is empty.
 */
function highestTender(rows: readonly TenderListRow[]): TenderListRow | null {
  let best: TenderListRow | null = null;
  for (const row of rows) {
    if (
      best === null ||
      row.offer > best.offer ||
      (row.offer === best.offer && row.dateTendered < best.dateTendered)
    ) {
      best = row;
    }
  }
  return best;
}

/**
 * Returns the sold-state text of an existing item:
 * - unsold: `$NotYetSold`, followed for a tender item with a highest tender by ". " and `$HighestBid` of
 *   `$TenderedByClient` with the tender's client and offer;
 * - sold retail item: "Sold to <client> for <item price>";
 * - sold tender item: "Sold to <client> for <highest offer>", or "Date Sold" when it holds no tender.
 */
function soldStateText(item: ItemProjection, highest: TenderListRow | null): string {
  if (!item.isSold) {
    const notSold = msg('NotYetSold');
    if (!item.isTender || highest === null) {
      return notSold;
    }
    return `${notSold}${PERIOD_SPACE}${msg('HighestBid', msg('TenderedByClient', highest.clientName, currencyFormat(highest.offer)))}`;
  }

  let amount: number;
  if (item.isTender) {
    if (highest === null) {
      return DATE_SOLD_CAPTION;
    }
    amount = highest.offer;
  } else {
    amount = item.price;
  }

  const clientName = item.sale?.client.name ?? '';
  return firstCharToUpper(
    [msg('SoldTo'), clientName, msg('For'), currencyFormat(amount)].join(SPACE),
  );
}

/** Returns the exception outcome of error 1090, "Null object reference", with the web exception body. */
function nullReferenceOutcome(): ErrorOutcomeError {
  return new ErrorOutcomeError({
    kind: 'exception',
    heading: msg('BaseExceptionHeading', NULL_REFERENCE_CODE),
    body: msg('BaseExceptionBodyWeb', NULL_REFERENCE_CODE, NULL_REFERENCE_TEXT),
  });
}

/** Returns the selector entries of named rows, in their order. */
function toOptions(rows: readonly { id: number; name: string }[]): SelectOption[] {
  return rows.map((row) => ({ id: row.id, text: row.name }));
}

/**
 * Returns the field values shown when the dialog opens: for a new item, the category `itemCategoryId` with its prefix
 * when the company holds it and every other field empty; for an existing item, its category and category prefix, code
 * number, country, region, name, description and, for the administrator, its agent.
 */
function initialValues(
  item: ItemProjection | null,
  reference: EditorReference,
  itemCategoryId: number | null,
  administrator: boolean,
): ItemFormValues {
  if (item === null) {
    const category = reference.categories.find((row) => row.id === itemCategoryId) ?? null;
    return {
      categoryId: category?.id ?? null,
      prefix: category?.prefix ?? '',
      codeNumber: '',
      countryId: null,
      regionId: null,
      agentId: null,
      name: '',
      description: '',
      photoBase64: null,
    };
  }

  return {
    categoryId: item.itemCategoryId,
    prefix: item.categoryPrefix ?? '',
    codeNumber: String(item.codeNumber),
    countryId: item.countryId,
    regionId: item.regionId,
    agentId: administrator ? item.agentId : null,
    name: item.name,
    description: item.description,
    photoBase64: null,
  };
}

/** Keeps the focus where it is when the mouse is pressed on the backdrop itself. */
function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

/** What the dialog reads when it opens. */
interface LoadResult {
  editor: LoadedEditor;
  values: ItemFormValues;
}

/** Inputs of {@link loadEditor}. */
interface LoadRequest {
  /** Id of the edited item; null for a new item. */
  editId: number | null;
  /** Agent of the session; null for the administrator. */
  sessionAgentId: number | null;
  itemCategoryId: number | null;
  onLoad: ((item: ItemProjection | null) => void | Promise<void>) | undefined;
}

/**
 * Reads, in parallel, the edited item (GET /api/app/items/{id}), the item categories, the countries with their regions,
 * for the administrator the agents, and for an agent session adding an item the session agent. Then reads the stored
 * picture of an item that has one (a 404 reads as no picture) and, for a tender item holding tenders, its tenders
 * (POST /api/app/admin/tenders/search), and calls `onLoad`. An existing item without an agent or a region rejects
 * with the exception outcome of error 1090; any failed request rejects with its error.
 */
async function loadEditor(request: LoadRequest): Promise<LoadResult> {
  const { editId, sessionAgentId, itemCategoryId, onLoad } = request;
  const administrator = sessionAgentId === null;

  const [details, categories, countries, agents, sessionAgent] = await Promise.all([
    editId === null ? Promise.resolve(null) : api.get<ItemDetailsResponse>(itemPath(editId)),
    api.get<ItemCategoryRow[]>(ITEM_CATEGORIES_PATH),
    api.get<CountryRow[]>(COUNTRIES_PATH),
    administrator ? api.get<AgentProjection[]>(AGENTS_PATH) : Promise.resolve<AgentProjection[]>([]),
    !administrator && editId === null
      ? api.get<AgentDetailsResponse>(`${AGENTS_PATH}/${sessionAgentId}`)
      : Promise.resolve(null),
  ]);

  const item = details?.item ?? null;
  if (item !== null && (item.agent === null || item.regionId === null)) {
    throw nullReferenceOutcome();
  }

  let existingPhoto: ExistingPhoto | null = null;
  if (item !== null && details?.hasPhoto === true) {
    try {
      const blob = await api.blob(photoPath(item.id));
      if (blob.size > 0) {
        existingPhoto = { blob, base64: await blobToBase64(blob) };
      }
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
    }
  }

  let highest: TenderListRow | null = null;
  if (item !== null && item.isTender && item.hasTenders) {
    const search: AdminTenderSearchRequest = {
      agentId: null,
      clientId: null,
      itemId: item.id,
      includeOpenTenders: true,
      includeClosedTenders: true,
      includeSuccessfulTenders: true,
    };
    highest = highestTender(await api.post<TenderListRow[]>(TENDER_SEARCH_PATH, search));
  }

  let agentLabel = AGENT_DETAILS_CAPTION;
  if (!administrator) {
    const shownAgent = item === null ? (sessionAgent?.agent ?? null) : item.agent;
    if (shownAgent !== null) {
      agentLabel = agentLabelText(shownAgent);
    }
  }

  const reference: EditorReference = { categories, countries, agents };

  await onLoad?.(item);

  return {
    editor: {
      item,
      reference,
      agentLabel,
      dateSoldText: item === null ? '' : soldStateText(item, highest),
      existingPhoto,
    },
    values: initialValues(item, reference, itemCategoryId, administrator),
  };
}

/**
 * The modal item editor of the administration area, shared by the retail and tender item editors: the agent area,
 * "Item Details" (Prefix, Number, Category, Country, Region and the "Description" group with Name and Desc), the
 * "Picture" group, the kind-specific group, the sold-state label and the OK and Cancel buttons.
 *
 * - On open, reads the item and the reference lists ({@link loadEditor}); until they answer only the title and the
 *   buttons are shown, with `aria-busy="true"`, and OK does nothing. A 404 calls `onClose(false)`; an error outcome,
 *   the error 1090 outcome included, is shown in a message box and then calls `onClose(false)`.
 * - The title is `$Add` or `$Edit`, a space and `caption`. Category lists the company's categories and Country its
 *   countries; Region lists the regions of the chosen country. Choosing a category sets Prefix to its prefix; choosing
 *   a country lists its regions with none chosen, and the empty entry empties the Region list.
 * - The administrator chooses the agent in the "Agent" selector; an agent session shows the agent label "<name>,
 *   <street>, <city>, <country>" of the session agent for a new item and of the item's agent otherwise.
 * - Prefix and Number are always disabled. For a sold item, Category, Country, Region, Name, Desc and the agent
 *   selector are disabled. Region is enabled for a new item, and for an unsold item while a country is chosen.
 * - The sold-state label is empty for a new item and otherwise shows {@link soldStateText}.
 * - Every user edit calls the soft edit lock's `markChanged`. OK sends the values to POST
 *   /api/app/admin/{kind}-items, or PUT /api/app/admin/{kind}-items/{id}, through the lock's `save`: the item's agent
 *   for an agent session editing an item, the session agent for an agent session adding one, and the selected agent
 *   for the administrator; Number read as an integer, 0 when empty; Name and Desc as typed; the item's listed date;
 *   the chosen picture, else the stored one, else null; and the values of `buildKindFields`. A failed result first
 *   focuses the field of its focus field and then shows its errors titled `$ErrorDuringSave`, and the dialog stays
 *   open; a succeeded result calls `onClose(true)`; a 404 calls `onClose(false)`.
 * - Cancel and Escape close through the lock's `close` and then call `onClose(false)`; while a request is outstanding
 *   OK, Cancel and Escape do nothing.
 *
 * The panel is focused on open and the first enabled control once the fields are shown, unless the focus has moved;
 * Tab and Shift+Tab wrap within the dialog, and on unmount the element focused before the dialog opened gets the
 * focus back when nothing else has taken it.
 */
export function ItemEditorDialog(props: ItemEditorDialogProps): ReactElement {
  const { kind, itemId, itemCategoryId = null, caption, kindGroupCaption, renderKindFields } = props;
  const { session } = useSession();
  const sessionAgentId = session?.agentId ?? null;
  const administrator = sessionAgentId === null;
  const editId = isRowId(itemId) ? itemId : null;

  const lock = useEditLock(LOCK_TYPES[kind], editId);
  const { markChanged } = lock;
  const { element: messageBox, showError } = useMessageBox();

  const titleId = useId();
  const idPrefix = useId();

  const [loaded, setLoaded] = useState<LoadedEditor | null>(null);
  const [values, setValues] = useState<ItemFormValues | null>(null);
  const [busy, setBusy] = useState(false);

  const latestRef = useRef(props);
  const loadedRef = useRef<LoadedEditor | null>(null);
  const valuesRef = useRef<ItemFormValues | null>(null);
  const busyRef = useRef(false);
  const mountedRef = useRef(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const agentRef = useRef<HTMLSelectElement>(null);
  const categoryRef = useRef<HTMLSelectElement>(null);
  const regionRef = useRef<HTMLSelectElement>(null);
  const nameRef = useRef<HTMLInputElement>(null);
  const descriptionRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    latestRef.current = props;
    loadedRef.current = loaded;
    valuesRef.current = values;
  });

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    let active = true;
    setLoaded(null);
    setValues(null);

    loadEditor({ editId, sessionAgentId, itemCategoryId, onLoad: latestRef.current.onLoad }).then(
      (result) => {
        if (active) {
          loadedRef.current = result.editor;
          valuesRef.current = result.values;
          setLoaded(result.editor);
          setValues(result.values);
        }
      },
      async (error: unknown) => {
        if (!active) {
          return;
        }
        if (isNotFound(error)) {
          latestRef.current.onClose(false);
          return;
        }
        await showError(error);
        if (active) {
          latestRef.current.onClose(false);
        }
      },
    );

    return () => {
      active = false;
    };
  }, [editId, sessionAgentId, itemCategoryId, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    panel?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useEffect(() => {
    const panel = panelRef.current;
    if (loaded === null || panel === null || document.activeElement !== panel) {
      return;
    }
    panel.querySelector<HTMLElement>(FOCUSABLE_SELECTOR)?.focus({ preventScroll: true });
  }, [loaded]);

  const setBusyState = (next: boolean): void => {
    busyRef.current = next;
    if (mountedRef.current) {
      setBusy(next);
    }
  };

  const change = (patch: Partial<ItemFormValues>): void => {
    setValues((current) => (current === null ? current : { ...current, ...patch }));
    markChanged();
  };

  const focusErrorField = (focusField: string | null): void => {
    if (focusField === null || latestRef.current.focusKindField?.(focusField) === true) {
      return;
    }
    switch (focusField) {
      case FOCUS_CODE_PREFIX:
      case FOCUS_CODE_NUMBER:
      case FOCUS_ITEM_CATEGORY:
        categoryRef.current?.focus();
        break;
      case FOCUS_REGION:
        regionRef.current?.focus();
        break;
      case FOCUS_ITEM_NAME:
        nameRef.current?.focus();
        break;
      case FOCUS_ITEM_DESCRIPTION:
        descriptionRef.current?.focus();
        break;
      case FOCUS_AGENT:
        agentRef.current?.focus();
        break;
      default:
        break;
    }
  };

  const buildRequest = (): RetailItemRequest | TenderItemRequest => {
    const editor = loadedRef.current;
    const current = valuesRef.current;
    if (editor === null || current === null) {
      throw new Error('The item editor has not finished loading.');
    }

    const item = editor.item;
    let agentId: number | null;
    if (sessionAgentId === null) {
      agentId = current.agentId;
    } else {
      agentId = item === null ? sessionAgentId : item.agentId;
    }

    const kindValues = latestRef.current.buildKindFields();
    const shared: RetailItemRequest = {
      codeNumber: parseCodeNumber(current.codeNumber),
      name: current.name,
      description: current.description,
      listedDate: item?.listedDate ?? null,
      photoBase64: current.photoBase64 ?? editor.existingPhoto?.base64 ?? null,
      price: kindValues.price,
      agentId,
      itemCategoryId: current.categoryId,
      regionId: current.regionId,
    };

    if (kind === 'tender') {
      const tenderRequest: TenderItemRequest = { ...shared, closureDate: kindValues.closureDate ?? null };
      return tenderRequest;
    }
    return shared;
  };

  const sendSave = async (): Promise<OperationResult> => {
    const request = buildRequest();
    const path = SAVE_PATHS[kind];
    const result =
      editId === null
        ? await api.post<OperationResult>(path, request)
        : await api.put<OperationResult>(`${path}/${editId}`, request);
    if (!result.succeeded) {
      focusErrorField(result.focusField);
    }
    return result;
  };

  const handleOk = async (): Promise<void> => {
    if (busyRef.current || loadedRef.current === null) {
      return;
    }

    setBusyState(true);
    try {
      let saved: boolean;
      try {
        saved = await lock.save(sendSave);
      } catch (error: unknown) {
        if (!isNotFound(error)) {
          throw error;
        }
        if (mountedRef.current) {
          latestRef.current.onClose(false);
        }
        return;
      }
      if (saved && mountedRef.current) {
        latestRef.current.onClose(true);
      }
    } finally {
      setBusyState(false);
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (busyRef.current) {
      return;
    }

    setBusyState(true);
    try {
      let closing: boolean;
      try {
        closing = await lock.close(sendSave);
      } catch (error: unknown) {
        if (!isNotFound(error)) {
          throw error;
        }
        closing = true;
      }
      if (closing && mountedRef.current) {
        latestRef.current.onClose(false);
      }
    } finally {
      setBusyState(false);
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void handleCancel();
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

  const loading = loaded === null || values === null;
  const title = `${msg(editId === null ? 'Add' : 'Edit')}${SPACE}${caption}`;

  return (
    <>
      <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
        <div
          ref={panelRef}
          role="dialog"
          aria-modal="true"
          aria-labelledby={titleId}
          aria-busy={loading || busy}
          tabIndex={-1}
          style={loading || busy ? BUSY_PANEL_STYLE : PANEL_STYLE}
          onKeyDown={handleKeyDown}
        >
          <h2 id={titleId} style={TITLE_STYLE}>
            {title}
          </h2>
          {loaded !== null && values !== null && (
            <ItemEditorFields
              idPrefix={idPrefix}
              editor={loaded}
              values={values}
              administrator={administrator}
              kindGroupCaption={kindGroupCaption}
              kindFields={renderKindFields({
                item: loaded.item,
                sold: loaded.item?.isSold === true,
                markChanged,
              })}
              onChange={change}
              refs={{ agentRef, categoryRef, regionRef, nameRef, descriptionRef }}
            />
          )}
          <div style={FOOTER_STYLE}>
            <p style={DATE_SOLD_STYLE}>{loaded?.dateSoldText ?? ''}</p>
            <div style={BUTTON_ROW_STYLE}>
              <DialogButton aria-disabled={loading || busy} onClick={() => void handleOk()}>
                {OK_CAPTION}
              </DialogButton>
              <DialogButton aria-disabled={busy} onClick={() => void handleCancel()}>
                {CANCEL_CAPTION}
              </DialogButton>
            </div>
          </div>
        </div>
      </div>
      {lock.element}
      {messageBox}
    </>
  );
}

/** Refs of the controls a save result's focus field can focus. */
interface ItemEditorFieldRefs {
  agentRef: Ref<HTMLSelectElement>;
  categoryRef: Ref<HTMLSelectElement>;
  regionRef: Ref<HTMLSelectElement>;
  nameRef: Ref<HTMLInputElement>;
  descriptionRef: Ref<HTMLTextAreaElement>;
}

/** Properties of {@link ItemEditorFields}. */
interface ItemEditorFieldsProps {
  /** Prefix of the control ids. */
  idPrefix: string;
  editor: LoadedEditor;
  values: ItemFormValues;
  /** True for the administrator, who chooses the agent; false for an agent session, which shows the agent label. */
  administrator: boolean;
  kindGroupCaption: string;
  /** Content of the kind-specific group. */
  kindFields: ReactNode;
  /** Receives each user edit as the changed values. */
  onChange: (patch: Partial<ItemFormValues>) => void;
  refs: ItemEditorFieldRefs;
}

/**
 * The fields of {@link ItemEditorDialog}: the agent area, "Item Details" with its "Description" group, the "Picture"
 * group and the kind-specific group, with the enabled states of the edited item's sold state.
 */
function ItemEditorFields(props: ItemEditorFieldsProps): ReactElement {
  const { idPrefix, editor, values, administrator, kindGroupCaption, kindFields, onChange, refs } = props;
  const { item, reference, existingPhoto } = editor;

  const isNew = item === null;
  const sold = item !== null && item.isSold;
  const regionEnabled = isNew || (!sold && values.countryId !== null);

  const regions: readonly RegionRow[] =
    reference.countries.find((country) => country.id === values.countryId)?.regions ?? [];

  const ids = {
    agent: `${idPrefix}agent`,
    prefix: `${idPrefix}prefix`,
    number: `${idPrefix}number`,
    category: `${idPrefix}category`,
    country: `${idPrefix}country`,
    region: `${idPrefix}region`,
    name: `${idPrefix}name`,
    description: `${idPrefix}description`,
  };

  const chooseCategory = (categoryId: number | null): void => {
    const category = reference.categories.find((row) => row.id === categoryId);
    onChange(category === undefined ? { categoryId } : { categoryId, prefix: category.prefix });
  };

  return (
    <>
      {administrator ? (
        <fieldset style={AGENT_GROUP_STYLE}>
          <label htmlFor={ids.agent} style={LABEL_STYLE}>
            {AGENT_CAPTION}
          </label>
          <SelectInput
            id={ids.agent}
            value={values.agentId}
            options={toOptions(reference.agents)}
            disabled={sold}
            selectRef={refs.agentRef}
            onChange={(agentId) => onChange({ agentId })}
          />
        </fieldset>
      ) : (
        <fieldset style={FIELDSET_STYLE}>
          <legend>{AGENT_CAPTION}</legend>
          <p style={AGENT_LABEL_STYLE}>{editor.agentLabel}</p>
        </fieldset>
      )}
      <div style={BODY_STYLE}>
        <fieldset style={DETAILS_GROUP_STYLE}>
          <legend>{ITEM_DETAILS_CAPTION}</legend>
          <label htmlFor={ids.prefix} style={LABEL_STYLE}>
            {PREFIX_CAPTION}
          </label>
          <span style={CODE_ROW_STYLE}>
            <TextInput id={ids.prefix} value={values.prefix} disabled readOnly maxLength={PREFIX_MAX_LENGTH} />
            <label htmlFor={ids.number} style={NUMBER_LABEL_STYLE}>
              {NUMBER_CAPTION}
            </label>
            <TextInput id={ids.number} value={values.codeNumber} disabled readOnly maxLength={NUMBER_MAX_LENGTH} />
          </span>
          <label htmlFor={ids.category} style={LABEL_STYLE}>
            {CATEGORY_CAPTION}
          </label>
          <SelectInput
            id={ids.category}
            value={values.categoryId}
            options={toOptions(reference.categories)}
            disabled={sold}
            selectRef={refs.categoryRef}
            onChange={chooseCategory}
          />
          <label htmlFor={ids.country} style={LABEL_STYLE}>
            {COUNTRY_CAPTION}
          </label>
          <SelectInput
            id={ids.country}
            value={values.countryId}
            options={toOptions(reference.countries)}
            disabled={sold}
            onChange={(countryId) => onChange({ countryId, regionId: null })}
          />
          <label htmlFor={ids.region} style={LABEL_STYLE}>
            {REGION_CAPTION}
          </label>
          <SelectInput
            id={ids.region}
            value={values.regionId}
            options={toOptions(regions)}
            disabled={!regionEnabled}
            selectRef={refs.regionRef}
            onChange={(regionId) => onChange({ regionId })}
          />
          <fieldset style={DESCRIPTION_GROUP_STYLE}>
            <legend>{DESCRIPTION_CAPTION}</legend>
            <label htmlFor={ids.name} style={LABEL_STYLE}>
              {NAME_CAPTION}
            </label>
            <TextInput
              id={ids.name}
              value={values.name}
              disabled={sold}
              maxLength={NAME_MAX_LENGTH}
              mandatory
              inputRef={refs.nameRef}
              onChange={(name) => onChange({ name })}
            />
            <label htmlFor={ids.description} style={LABEL_STYLE}>
              {DESC_CAPTION}
            </label>
            <TextArea
              id={ids.description}
              value={values.description}
              disabled={sold}
              mandatory
              textAreaRef={refs.descriptionRef}
              onChange={(description) => onChange({ description })}
            />
          </fieldset>
        </fieldset>
        <div style={SIDE_COLUMN_STYLE}>
          {/* A chosen picture file is kept and sent as chosen; the save result reports a file that is not a picture. */}
          <PictureField
            base64={values.photoBase64}
            loadExisting={existingPhoto === null ? undefined : () => Promise.resolve(existingPhoto.blob)}
            onChange={(photoBase64) => onChange({ photoBase64 })}
          />
          <fieldset style={KIND_GROUP_STYLE}>
            <legend>{kindGroupCaption}</legend>
            {kindFields}
          </fieldset>
        </div>
      </div>
    </>
  );
}

/** Properties of {@link EditRetailItemDialog}. */
export interface EditRetailItemDialogProps {
  /** Internal id of the retail item edited; null or undefined adds a new retail item. */
  itemId?: number | null;
  /** Internal id of the category pre-selected for a new item. */
  itemCategoryId?: number | null;
  /** Called once with true after a successful OK, and with false when the dialog closes otherwise. */
  onClose: (saved: boolean) => void;
}

/**
 * The "Retail Sale Item" editor: {@link ItemEditorDialog} for a retail item with the "Sale Details" group holding
 * "Retail Price". Retail Price shows the item's price with 2 decimals for an existing item and is empty for a new one;
 * it is disabled for a sold item, and OK sends its text read as a decimal number, 0 when empty or not a number.
 */
export function EditRetailItemDialog({ itemId, itemCategoryId, onClose }: EditRetailItemDialogProps): ReactElement {
  const [priceText, setPriceText] = useState('');

  return (
    <ItemEditorDialog
      kind="retail"
      itemId={itemId}
      itemCategoryId={itemCategoryId}
      caption={RETAIL_ITEM_CAPTION}
      kindGroupCaption={SALE_DETAILS_CAPTION}
      onClose={onClose}
      onLoad={(item) => {
        setPriceText(item === null ? '' : decimalString(item.price));
      }}
      renderKindFields={({ sold, markChanged }) => (
        <ItemEditorInput
          label={RETAIL_PRICE_CAPTION}
          value={priceText}
          disabled={sold}
          maxLength={RETAIL_PRICE_MAX_LENGTH}
          inputMode="decimal"
          mandatory
          onChange={(text) => {
            setPriceText(text);
            markChanged();
          }}
        />
      )}
      buildKindFields={() => ({ price: parseItemPrice(priceText) })}
    />
  );
}
