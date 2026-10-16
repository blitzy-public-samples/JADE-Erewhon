import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import type {
  ChangeEvent,
  CSSProperties,
  FocusEvent,
  KeyboardEvent,
  MouseEvent,
  ReactElement,
  RefObject,
} from 'react';
import { ApiError, api } from '../../api/client';
import type { ItemCategoryRequest, ItemCategoryRow, OperationResult } from '../../api/types';
import { trimWhiteSpace } from '../../components/AddressFields';
import { DialogButton, useMessageBox } from '../../components/MessageBox';
import { msg } from '../../messages/messages';
import { useEditLock } from '../useEditLock';

/** Properties of {@link EditItemCategoryDialog}. */
export interface EditItemCategoryDialogProps {
  /**
   * Internal id of the item category to edit; null, undefined or a value that is not a positive safe integer adds a
   * new item category.
   */
  itemCategoryId?: number | null;
  /** Called once when the dialog closes, with true when it saved the item category at least once. */
  onClose: (saved: boolean) => void;
}

/** One of the three text boxes of the dialog. */
type FieldName = 'prefix' | 'name' | 'description';

/** Text of the three text boxes. */
type FieldValues = Readonly<Record<FieldName, string>>;

const ITEM_CATEGORIES_PATH = '/api/app/admin/item-categories';

const FORM_CAPTION = 'Sale Item Category';
const PREFIX_CAPTION = 'Prefix';
const NAME_CAPTION = 'Name';
const DESCRIPTION_CAPTION = 'Description';
const OK_CAPTION = 'OK';
const CANCEL_CAPTION = 'Cancel';

const PREFIX_MAX_LENGTH = 4;
const NAME_MAX_LENGTH = 40;

const EMPTY_VALUES: FieldValues = { prefix: '', name: '', description: '' };

/** Focus fields of an item category save result, mapped to the text box each one focuses. */
const FOCUS_FIELDS: ReadonlyMap<string, FieldName> = new Map<string, FieldName>([
  ['FOCUS: CodePrefix', 'prefix'],
  ['FOCUS: Name', 'name'],
  ['FOCUS: Description', 'description'],
]);

const FOCUSABLE_SELECTOR =
  'input:not(:disabled), textarea:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex="-1"])';

const BORDER = '1px solid rgb(128, 128, 128)';
const INPUT_BORDER_COLOR = '#AEAEAE';
const INPUT_HOVER_BORDER_COLOR = 'rgb(128, 128, 128)';
const INPUT_PRESSED_BORDER_COLOR = 'rgb(0, 80, 160)';
const FOCUS_OUTLINE = '2px solid rgb(0, 80, 160)';
const MANDATORY_CORNER_COLOR = '#ED1C24';
const MANDATORY_CORNER_SIZE = '6px';
const LABEL_COLUMN = '5.5rem';
const INPUT_COLUMN = '17.0625rem';

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
  maxInlineSize: '24.125rem',
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
  gridTemplateColumns: `${LABEL_COLUMN} minmax(0, ${INPUT_COLUMN})`,
  alignItems: 'center',
  columnGap: '0.1875rem',
  rowGap: '0.5625rem',
};

const LABEL_STYLE: CSSProperties = {
  minInlineSize: 0,
  textAlign: 'end',
  overflowWrap: 'break-word',
};

const TOP_LABEL_STYLE: CSSProperties = {
  ...LABEL_STYLE,
  alignSelf: 'start',
};

const CELL_STYLE: CSSProperties = {
  position: 'relative',
  minInlineSize: 0,
};

const PREFIX_CELL_STYLE: CSSProperties = {
  ...CELL_STYLE,
  inlineSize: '4.875rem',
  maxInlineSize: '100%',
};

const INPUT_STYLE: CSSProperties = {
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

const TEXTAREA_STYLE: CSSProperties = {
  ...INPUT_STYLE,
  blockSize: '9.8125rem',
  paddingBlock: '0.125rem',
  overflowY: 'scroll',
  resize: 'none',
};

const INPUT_FOCUS_STYLE: CSSProperties = { outline: FOCUS_OUTLINE, outlineOffset: 0 };

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

/** Returns true when `id` is a positive safe integer, the id of an existing row. */
function isRowId(id: number | null | undefined): id is number {
  return typeof id === 'number' && Number.isSafeInteger(id) && id > 0;
}

/** Returns the request path of the item category with row id `id`. */
function itemCategoryPath(id: number): string {
  return `${ITEM_CATEGORIES_PATH}/${encodeURIComponent(String(id))}`;
}

/** Returns true for an {@link ApiError} with HTTP status 404. */
function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
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
 * Returns the style of a text box: `base` with the pressed or hovered border, in that order of precedence, and the
 * focus outline added when `focusVisible` is true.
 */
function textBoxStyle(base: CSSProperties, pressed: boolean, hovered: boolean, focusVisible: boolean): CSSProperties {
  let style = base;
  if (pressed) {
    style = { ...base, borderColor: INPUT_PRESSED_BORDER_COLOR };
  } else if (hovered) {
    style = { ...base, borderColor: INPUT_HOVER_BORDER_COLOR };
  }
  return focusVisible ? { ...style, ...INPUT_FOCUS_STYLE } : style;
}

/** Keeps the focus where it is when the mouse is pressed on the backdrop itself. */
function handleOverlayMouseDown(event: MouseEvent<HTMLDivElement>): void {
  if (event.target === event.currentTarget) {
    event.preventDefault();
  }
}

/** Returns true for Ctrl+S, with or without Shift, pressed without Alt or Meta. */
function isSaveShortcut(event: KeyboardEvent<HTMLElement>): boolean {
  return (
    event.ctrlKey && !event.altKey && !event.metaKey && (event.code === 'KeyS' || event.key.toLowerCase() === 's')
  );
}

/** Properties shared by the single-line and the multi-line {@link TextField}. */
interface TextFieldCommonProps {
  /** Id of the text box, referenced by its label. */
  id: string;
  /** Text shown. */
  value: string;
  /** Makes the text box read-only. */
  readOnly: boolean;
  /** Receives the text as typed after each edit. */
  onChange: (value: string) => void;
}

/** Properties of a single-line {@link TextField}. */
interface SingleLineTextFieldProps extends TextFieldCommonProps {
  multiline?: false;
  /** Maximum number of characters typing accepts. */
  maxLength: number;
  /** Style of the cell holding the text box and its mandatory corner. */
  cellStyle: CSSProperties;
  fieldRef: RefObject<HTMLInputElement>;
}

/** Properties of a multi-line {@link TextField}, which accepts any number of characters. */
interface MultiLineTextFieldProps extends TextFieldCommonProps {
  multiline: true;
  fieldRef: RefObject<HTMLTextAreaElement>;
}

type TextFieldProps = SingleLineTextFieldProps | MultiLineTextFieldProps;

/**
 * Mandatory text box: an `<input>`, or a `<textarea>` 157px high with a vertical scroll bar when `multiline` is true,
 * with `aria-required="true"`, `autoComplete="off"` and a 6px red top-left corner. It has a 1px #AEAEAE border on
 * white; while the pointer is over it the border is rgb(128, 128, 128) and while the primary mouse button is held on
 * it rgb(0, 80, 160). Focused with `:focus-visible`, checked again on every key release in it, it has a 2px
 * rgb(0, 80, 160) outline drawn against its border.
 */
function TextField(props: TextFieldProps): ReactElement {
  const { id, value, readOnly, onChange } = props;
  const [hovered, setHovered] = useState(false);
  const [pressed, setPressed] = useState(false);
  const [focusVisible, setFocusVisible] = useState(false);

  const updateFocusVisible = (event: FocusEvent<HTMLElement> | KeyboardEvent<HTMLElement>): void => {
    setFocusVisible(matchesFocusVisible(event.currentTarget));
  };

  const common = {
    id,
    value,
    readOnly,
    autoComplete: 'off',
    'aria-required': true,
    onChange: (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => onChange(event.target.value),
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
    onBlur: () => setFocusVisible(false),
  };

  if (props.multiline === true) {
    return (
      <div style={CELL_STYLE}>
        <textarea
          ref={props.fieldRef}
          {...common}
          style={textBoxStyle(TEXTAREA_STYLE, pressed, hovered, focusVisible)}
        />
        <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />
      </div>
    );
  }

  return (
    <div style={props.cellStyle}>
      <input
        ref={props.fieldRef}
        type="text"
        maxLength={props.maxLength}
        {...common}
        style={textBoxStyle(INPUT_STYLE, pressed, hovered, focusVisible)}
      />
      <span aria-hidden="true" style={MANDATORY_CORNER_STYLE} />
    </div>
  );
}

/**
 * The modal "Sale Item Category" editor of the item category list: the Prefix, Name and Description of one item
 * category, created through POST /api/app/admin/item-categories or updated through PUT
 * /api/app/admin/item-categories/{id}.
 *
 * - The dialog is named "Add Sale Item Category" when `itemCategoryId` is not a row id and "Edit Sale Item Category"
 *   otherwise, and shows that name as its heading. For an existing category, GET /api/app/admin/item-categories/{id}
 *   fills the three text boxes; until it answers the panel has `aria-busy="true"` and the progress cursor, the text
 *   boxes are read-only and OK is unavailable. A 404 closes the dialog without a message; an error outcome is shown
 *   and then closes it; an HTTP 401 closes it without a message; any other failure closes it and rejects unhandled.
 * - The row's soft edit lock goes through `useEditLock('ItemCategory', id)`: the prompt when the opened row is
 *   locked, the lock taken on the first edit, and the unlock after a successful save and on close.
 * - OK sends the Prefix, Name and Description texts after `trimWhiteSpace`, with no other change. A failed result
 *   focuses the text box of its focus field (`FOCUS: CodePrefix` Prefix, `FOCUS: Name` Name, `FOCUS: Description`
 *   Description) and shows its errors joined with CR LF titled `$ErrorDuringSave`, and the dialog stays open; a
 *   succeeded result calls `onClose(true)`.
 * - Ctrl+S saves the same way and keeps the dialog open. After a success the text boxes show the category as read
 *   back, and the dialog of a new category edits the created row from then on; the heading is unchanged.
 * - Cancel and Escape take the edit lock's close path: with unsaved changes it asks `$ChangesSinceOpeningForm`
 *   (Yes saves as OK does, No discards, Cancel keeps the dialog open); the dialog then calls `onClose` with true when
 *   any save of this dialog succeeded, otherwise false.
 * - A save answered with 404 closes the dialog. An error outcome during a save is shown and the dialog stays open.
 * - While a save or close is pending, OK and Cancel are unavailable, the text boxes are read-only, the panel shows the
 *   progress cursor, and Escape and Ctrl+S are ignored.
 *
 * Prefix accepts at most 4 characters and Name at most 40; Description is a multi-line box without a limit. The Prefix
 * box is focused on mount, Tab and Shift+Tab wrap within the dialog, a mouse press on the backdrop keeps the focus,
 * and the element focused before the dialog opened gets the focus back when the dialog closes and nothing else has
 * taken it. `onClose` is called at most once, and not after unmount.
 */
export function EditItemCategoryDialog({ itemCategoryId, onClose }: EditItemCategoryDialogProps): ReactElement {
  const propId = isRowId(itemCategoryId) ? itemCategoryId : null;
  const caption = `${msg(propId === null ? 'Add' : 'Edit')} ${FORM_CAPTION}`;

  const { element: messageBox, showError } = useMessageBox();
  const [currentId, setCurrentId] = useState<number | null>(propId);
  const [reload, setReload] = useState(0);
  const [loadedKey, setLoadedKey] = useState<string | null>(null);
  const [values, setValues] = useState<FieldValues>(EMPTY_VALUES);
  const [pending, setPending] = useState(false);
  const lock = useEditLock('ItemCategory', currentId);

  const prefixId = useId();
  const nameId = useId();
  const descriptionId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const prefixRef = useRef<HTMLInputElement>(null);
  const nameRef = useRef<HTMLInputElement>(null);
  const descriptionRef = useRef<HTMLTextAreaElement>(null);
  const valuesRef = useRef<FieldValues>(EMPTY_VALUES);
  const onCloseRef = useRef(onClose);
  const mountedRef = useRef(false);
  const doneRef = useRef(false);
  const busyRef = useRef(false);
  const savedRef = useRef(false);
  const savedIdRef = useRef<number | null>(null);

  const loadKey = currentId === null ? null : `${currentId}:${reload}`;
  const loading = loadKey !== null && loadedKey !== loadKey;
  const readOnly = loading || pending;
  const lastResult = lock.lastResult;

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    mountedRef.current = true;

    return () => {
      mountedRef.current = false;
    };
  }, []);

  const finish = useCallback((): void => {
    if (doneRef.current || !mountedRef.current) {
      return;
    }
    doneRef.current = true;
    onCloseRef.current(savedRef.current);
  }, []);

  const replaceValues = useCallback((next: FieldValues): void => {
    valuesRef.current = next;
    setValues(next);
  }, []);

  useEffect(() => {
    setCurrentId(propId);
    replaceValues(EMPTY_VALUES);
  }, [propId, replaceValues]);

  useEffect(() => {
    if (currentId === null) {
      return undefined;
    }

    let active = true;
    const key = `${currentId}:${reload}`;

    api.get<ItemCategoryRow>(itemCategoryPath(currentId)).then(
      (row) => {
        if (!active) {
          return;
        }
        replaceValues({ prefix: row.prefix ?? '', name: row.name ?? '', description: row.description ?? '' });
        setLoadedKey(key);
      },
      async (error: unknown) => {
        if (!active) {
          return;
        }
        try {
          if (!isNotFound(error)) {
            await showError(error);
          }
        } finally {
          finish();
        }
      },
    );

    return () => {
      active = false;
    };
  }, [currentId, reload, finish, replaceValues, showError]);

  useEffect(() => {
    const previous = document.activeElement;
    const panel = panelRef.current;
    prefixRef.current?.focus({ preventScroll: true });

    return () => {
      const current = document.activeElement;
      const focusLost = current === null || current === document.body || (panel !== null && panel.contains(current));
      if (focusLost && previous instanceof HTMLElement && previous.isConnected) {
        previous.focus();
      }
    };
  }, []);

  useLayoutEffect(() => {
    const focusField = lastResult?.focusField;
    if (focusField === null || focusField === undefined) {
      return;
    }

    switch (FOCUS_FIELDS.get(focusField)) {
      case 'prefix':
        prefixRef.current?.focus();
        break;
      case 'name':
        nameRef.current?.focus();
        break;
      case 'description':
        descriptionRef.current?.focus();
        break;
      default:
        break;
    }
  }, [lastResult]);

  const handleChange =
    (field: FieldName) =>
    (value: string): void => {
      replaceValues({ ...valuesRef.current, [field]: value });
      lock.markChanged();
    };

  const doSave = async (): Promise<OperationResult> => {
    const current = valuesRef.current;
    const body: ItemCategoryRequest = {
      prefix: trimWhiteSpace(current.prefix),
      name: trimWhiteSpace(current.name),
      description: trimWhiteSpace(current.description),
    };

    const result =
      currentId === null
        ? await api.post<OperationResult>(ITEM_CATEGORIES_PATH, body)
        : await api.put<OperationResult>(itemCategoryPath(currentId), body);

    if (result.succeeded) {
      savedRef.current = true;
      savedIdRef.current = result.id;
    }
    return result;
  };

  const runSave = async (closeAfter: boolean): Promise<void> => {
    if (busyRef.current || doneRef.current || loading) {
      return;
    }
    busyRef.current = true;
    setPending(true);
    savedIdRef.current = null;

    let saved = false;
    try {
      saved = await lock.save(doSave);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
      finish();
      return;
    } finally {
      busyRef.current = false;
      if (mountedRef.current) {
        setPending(false);
      }
    }

    if (!saved || !mountedRef.current) {
      return;
    }
    if (closeAfter) {
      finish();
      return;
    }

    const savedId = savedIdRef.current;
    if (currentId === null && isRowId(savedId)) {
      setCurrentId(savedId);
    } else {
      setReload((count) => count + 1);
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (busyRef.current || doneRef.current) {
      return;
    }
    busyRef.current = true;
    setPending(true);

    let canClose = false;
    try {
      canClose = await lock.close(doSave);
    } catch (error: unknown) {
      if (!isNotFound(error)) {
        throw error;
      }
      finish();
      return;
    } finally {
      busyRef.current = false;
      if (mountedRef.current) {
        setPending(false);
      }
    }

    if (canClose) {
      finish();
    }
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.nativeEvent.isComposing) {
      return;
    }

    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      void handleCancel();
      return;
    }

    if (isSaveShortcut(event)) {
      event.preventDefault();
      event.stopPropagation();
      void runSave(false);
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

  return (
    <div style={OVERLAY_STYLE} onMouseDown={handleOverlayMouseDown}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={caption}
        aria-busy={loading}
        tabIndex={-1}
        style={readOnly ? BUSY_PANEL_STYLE : PANEL_STYLE}
        onKeyDown={handleKeyDown}
      >
        <h2 style={TITLE_STYLE}>{caption}</h2>
        <div style={FIELDS_STYLE}>
          <label htmlFor={prefixId} style={LABEL_STYLE}>
            {PREFIX_CAPTION}
          </label>
          <TextField
            id={prefixId}
            fieldRef={prefixRef}
            value={values.prefix}
            maxLength={PREFIX_MAX_LENGTH}
            cellStyle={PREFIX_CELL_STYLE}
            readOnly={readOnly}
            onChange={handleChange('prefix')}
          />
          <label htmlFor={nameId} style={LABEL_STYLE}>
            {NAME_CAPTION}
          </label>
          <TextField
            id={nameId}
            fieldRef={nameRef}
            value={values.name}
            maxLength={NAME_MAX_LENGTH}
            cellStyle={CELL_STYLE}
            readOnly={readOnly}
            onChange={handleChange('name')}
          />
          <label htmlFor={descriptionId} style={TOP_LABEL_STYLE}>
            {DESCRIPTION_CAPTION}
          </label>
          <TextField
            multiline
            id={descriptionId}
            fieldRef={descriptionRef}
            value={values.description}
            readOnly={readOnly}
            onChange={handleChange('description')}
          />
        </div>
        <hr aria-hidden="true" style={RULE_STYLE} />
        <div style={BUTTON_ROW_STYLE}>
          <DialogButton aria-disabled={readOnly} onClick={() => void runSave(true)}>
            {OK_CAPTION}
          </DialogButton>
          <DialogButton aria-disabled={pending} onClick={() => void handleCancel()}>
            {CANCEL_CAPTION}
          </DialogButton>
        </div>
      </div>
      {lock.element}
      {messageBox}
    </div>
  );
}
