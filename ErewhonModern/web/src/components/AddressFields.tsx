import { Fragment, useEffect, useId, useRef } from 'react';
import type { ChangeEvent, CSSProperties } from 'react';
import type { AddressFields as AddressValue } from '../api/types';

export type { AddressValue };

/** Name of one of the seven address fields. */
export type AddressField = keyof AddressValue;

/** Properties of {@link AddressFields}. */
export interface AddressFieldsProps {
  /** Text of the seven inputs; a null field shows as an empty input. */
  value: AddressValue;
  /** Receives a copy of `value` in which the edited field holds the text as typed. */
  onChange: (next: AddressValue) => void;
  /** Disables all seven inputs. */
  disabled?: boolean;
  /**
   * Focus field of the last save result. When it changes to one of the address focus values
   * ("FOCUS: Street", "FOCUS: City", "FOCUS: Country", "FOCUS: Phone", "FOCUS: Fax",
   * "FOCUS: Email", "FOCUS: WebSite"), the matching input receives focus; any other value is
   * ignored.
   */
  focusField?: string | null;
}

interface FieldSpec {
  label: string;
  maxLength: number;
  mandatory: boolean;
}

const FIELD_SPECS: Readonly<Record<AddressField, FieldSpec>> = {
  street: { label: 'Street', maxLength: 50, mandatory: true },
  city: { label: 'City', maxLength: 50, mandatory: true },
  country: { label: 'Country', maxLength: 50, mandatory: true },
  phone: { label: 'Phone', maxLength: 25, mandatory: true },
  fax: { label: 'Fax', maxLength: 25, mandatory: false },
  email: { label: 'Email', maxLength: 60, mandatory: true },
  webSite: { label: 'URL', maxLength: 60, mandatory: false },
};

const FOCUS_FIELDS: ReadonlyMap<string, AddressField> = new Map<string, AddressField>([
  ['FOCUS: Street', 'street'],
  ['FOCUS: City', 'city'],
  ['FOCUS: Country', 'country'],
  ['FOCUS: Phone', 'phone'],
  ['FOCUS: Fax', 'fax'],
  ['FOCUS: Email', 'email'],
  ['FOCUS: WebSite', 'webSite'],
]);

const WHITE_SPACE_CHARACTERS = '\r\n\t \u0000';

const GAP = '0.1875rem';
const LABEL_COLUMN = '3.75rem';
const INPUT_BORDER_COLOR = '#AEAEAE';
const MANDATORY_CORNER_COLOR = '#ED1C24';
const MANDATORY_CORNER_SIZE = '6px';

const fieldsetStyle: CSSProperties = {
  display: 'grid',
  alignItems: 'center',
  columnGap: GAP,
  rowGap: GAP,
  minInlineSize: 0,
  margin: 0,
  paddingBlockStart: '0.25rem',
  paddingBlockEnd: '0.75rem',
  paddingInlineStart: '0.75rem',
  paddingInlineEnd: '0.9375rem',
};

const inputStyle: CSSProperties = {
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

const styles = {
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: GAP,
    minInlineSize: 0,
  },
  singleColumnFieldset: {
    ...fieldsetStyle,
    gridTemplateColumns: `${LABEL_COLUMN} minmax(0, 1fr)`,
  },
  contactFieldset: {
    ...fieldsetStyle,
    gridTemplateColumns: `${LABEL_COLUMN} minmax(0, 194fr) 2.25rem minmax(0, 135fr)`,
  },
  label: {
    textAlign: 'end',
    overflowWrap: 'break-word',
    minInlineSize: 0,
  },
  inputCell: {
    position: 'relative',
    minInlineSize: 0,
  },
  wideInputCell: {
    position: 'relative',
    minInlineSize: 0,
    gridColumn: '2 / -1',
  },
  input: inputStyle,
  inputDisabled: { ...inputStyle, color: '#808080' },
  mandatoryCorner: {
    position: 'absolute',
    insetBlockStart: 0,
    insetInlineStart: 0,
    inlineSize: 0,
    blockSize: 0,
    borderBlockStart: `${MANDATORY_CORNER_SIZE} solid ${MANDATORY_CORNER_COLOR}`,
    borderInlineEnd: `${MANDATORY_CORNER_SIZE} solid transparent`,
    pointerEvents: 'none',
  },
} satisfies Record<string, CSSProperties>;

/**
 * Strips CR, LF, Tab, Space and NUL from both ends of `text`; characters between the first and
 * last other character are kept. Returns `''` for null, undefined and text made only of those
 * characters.
 */
export function trimWhiteSpace(text: string | null | undefined): string {
  if (text === null || text === undefined) {
    return '';
  }
  let start = 0;
  let end = text.length;
  while (start < end && WHITE_SPACE_CHARACTERS.includes(text.charAt(start))) {
    start += 1;
  }
  while (end > start && WHITE_SPACE_CHARACTERS.includes(text.charAt(end - 1))) {
    end -= 1;
  }
  return text.slice(start, end);
}

/** Returns a new address whose seven fields are the fields of `address` after `trimWhiteSpace`. */
export function trimAddress(address: AddressValue): AddressValue {
  return {
    street: trimWhiteSpace(address.street),
    city: trimWhiteSpace(address.city),
    country: trimWhiteSpace(address.country),
    phone: trimWhiteSpace(address.phone),
    fax: trimWhiteSpace(address.fax),
    email: trimWhiteSpace(address.email),
    webSite: trimWhiteSpace(address.webSite),
  };
}

/**
 * Renders the seven address inputs in three groups: "Address" (Street, City, Country),
 * "Contact" (Phone and Fax on one row, then Email) and "Internet Web Site" (URL), in that tab
 * order. Each input is labelled by its caption, shows its `value` field (null as empty), limits
 * typing to its maximum length (Street, City and Country 50; Phone and Fax 25; Email and URL 60)
 * and reports every edit through `onChange` with the text as typed. Street, City, Country, Phone
 * and Email have `aria-required="true"` and a red top-left corner; Fax and URL have neither.
 * `disabled` disables every input and greys its text. A change of `focusField` to an address
 * focus value focuses the matching input.
 */
export function AddressFields(props: AddressFieldsProps) {
  const { value, onChange, disabled = false, focusField = null } = props;
  const idPrefix = useId();
  const inputRefs = {
    street: useRef<HTMLInputElement>(null),
    city: useRef<HTMLInputElement>(null),
    country: useRef<HTMLInputElement>(null),
    phone: useRef<HTMLInputElement>(null),
    fax: useRef<HTMLInputElement>(null),
    email: useRef<HTMLInputElement>(null),
    webSite: useRef<HTMLInputElement>(null),
  };

  useEffect(() => {
    if (focusField === null) {
      return;
    }
    const field = FOCUS_FIELDS.get(focusField);
    if (field !== undefined) {
      inputRefs[field].current?.focus();
    }
  }, [focusField]);

  function renderField(field: AddressField, cellStyle: CSSProperties = styles.inputCell) {
    const spec = FIELD_SPECS[field];
    const id = `${idPrefix}${field}`;
    return (
      <Fragment key={field}>
        <label htmlFor={id} style={styles.label}>
          {spec.label}
        </label>
        <span style={cellStyle}>
          <input
            ref={inputRefs[field]}
            id={id}
            name={field}
            type="text"
            autoComplete="off"
            value={value[field] ?? ''}
            maxLength={spec.maxLength}
            disabled={disabled}
            aria-required={spec.mandatory ? 'true' : undefined}
            style={disabled ? styles.inputDisabled : styles.input}
            onChange={(event: ChangeEvent<HTMLInputElement>) => {
              onChange({ ...value, [field]: event.target.value });
            }}
          />
          {spec.mandatory && <span aria-hidden="true" style={styles.mandatoryCorner} />}
        </span>
      </Fragment>
    );
  }

  return (
    <div style={styles.root}>
      <fieldset style={styles.singleColumnFieldset}>
        <legend>Address</legend>
        {renderField('street')}
        {renderField('city')}
        {renderField('country')}
      </fieldset>
      <fieldset style={styles.contactFieldset}>
        <legend>Contact</legend>
        {renderField('phone')}
        {renderField('fax')}
        {renderField('email', styles.wideInputCell)}
      </fieldset>
      <fieldset style={styles.singleColumnFieldset}>
        <legend>Internet Web Site</legend>
        {renderField('webSite')}
      </fieldset>
    </div>
  );
}
