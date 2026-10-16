// The tender item editor of the administration area, built on the shared item editor dialog.

import { useRef, useState } from 'react';
import type { ReactElement } from 'react';
import { api } from '../../api/client';
import type { ClosureScheduleStateResponse, IsoDate } from '../../api/types';
import { decimalString } from '../../format';
import { ItemEditorDialog, ItemEditorInput, parseItemPrice } from './EditRetailItemDialog';

const CLOSURE_SCHEDULE_PATH = '/api/app/admin/tenders/closure-schedule';

const TENDER_ITEM_CAPTION = 'Tender Sale Item';
const TENDER_DETAILS_CAPTION = 'Tender Details';
const RESERVE_CAPTION = 'Reserve';
const CLOSURE_DATE_CAPTION = 'Closure Date';

const RESERVE_MAX_LENGTH = 12;

/** Days after the server date of the closure date a new tender item starts with. */
const NEW_ITEM_CLOSURE_DAYS = 7;

const FOCUS_DATE = 'FOCUS: Date';
const FOCUS_PRICE = 'FOCUS: Price';

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * Returns the calendar date `days` days after the `yyyy-MM-dd` date `iso`, as `yyyy-MM-dd`, counted on the calendar
 * alone with no time zone, e.g. `2024-02-26` and 7 → `2024-03-04`.
 *
 * @throws Error when `iso` is not a `yyyy-MM-dd` date.
 */
function addDays(iso: IsoDate, days: number): IsoDate {
  const match = ISO_DATE.exec(iso);
  if (match === null) {
    throw new Error(`The server date '${iso}' is not a yyyy-MM-dd date.`);
  }

  const date = new Date(0);
  date.setUTCFullYear(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + days);

  const year = String(date.getUTCFullYear()).padStart(4, '0');
  const month = String(date.getUTCMonth() + 1).padStart(2, '0');
  const day = String(date.getUTCDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** Properties of {@link EditTenderItemDialog}. */
export interface EditTenderItemDialogProps {
  /** Internal id of the tender item edited; null or undefined adds a new tender item. */
  itemId?: number | null;
  /** Internal id of the category pre-selected for a new item. */
  itemCategoryId?: number | null;
  /** Called once with true after a successful OK, and with false when the dialog closes otherwise. */
  onClose: (saved: boolean) => void;
}

/**
 * The "Tender Sale Item" editor: {@link ItemEditorDialog} for a tender item with the "Tender Details" group holding
 * "Reserve" and "Closure Date".
 *
 * - For an existing item, Reserve shows the item's reserve price with 2 decimals and Closure Date its closure date;
 *   both are disabled when the item is sold.
 * - For a new item, Reserve is empty and Closure Date is 7 days after the server date read from GET
 *   /api/app/admin/tenders/closure-schedule before the fields are shown.
 * - OK sends Reserve read as a decimal number, 0 when empty or not a number, and the Closure Date, null when none is
 *   entered.
 * - A failed save whose focus field is `FOCUS: Date` focuses Closure Date, and one whose focus field is
 *   `FOCUS: Price` focuses Reserve.
 */
export function EditTenderItemDialog({ itemId, itemCategoryId, onClose }: EditTenderItemDialogProps): ReactElement {
  const [reserveText, setReserveText] = useState('');
  const [closureDateText, setClosureDateText] = useState('');
  const reserveRef = useRef<HTMLInputElement>(null);
  const closureDateRef = useRef<HTMLInputElement>(null);

  return (
    <ItemEditorDialog
      kind="tender"
      itemId={itemId}
      itemCategoryId={itemCategoryId}
      caption={TENDER_ITEM_CAPTION}
      kindGroupCaption={TENDER_DETAILS_CAPTION}
      onClose={onClose}
      onLoad={async (item) => {
        if (item !== null) {
          setReserveText(decimalString(item.price));
          setClosureDateText(item.closureDate ?? '');
          return;
        }
        const schedule = await api.get<ClosureScheduleStateResponse>(CLOSURE_SCHEDULE_PATH);
        setReserveText('');
        setClosureDateText(addDays(schedule.serverDate, NEW_ITEM_CLOSURE_DAYS));
      }}
      renderKindFields={({ sold, markChanged }) => (
        <>
          <ItemEditorInput
            label={RESERVE_CAPTION}
            value={reserveText}
            disabled={sold}
            maxLength={RESERVE_MAX_LENGTH}
            inputMode="decimal"
            mandatory
            inputRef={reserveRef}
            onChange={(text) => {
              setReserveText(text);
              markChanged();
            }}
          />
          <ItemEditorInput
            label={CLOSURE_DATE_CAPTION}
            value={closureDateText}
            disabled={sold}
            type="date"
            mandatory
            inputRef={closureDateRef}
            onChange={(text) => {
              setClosureDateText(text);
              markChanged();
            }}
          />
        </>
      )}
      buildKindFields={() => ({
        price: parseItemPrice(reserveText),
        closureDate: closureDateText === '' ? null : closureDateText,
      })}
      focusKindField={(focusField) => {
        if (focusField === FOCUS_DATE) {
          closureDateRef.current?.focus();
          return true;
        }
        if (focusField === FOCUS_PRICE) {
          reserveRef.current?.focus();
          return true;
        }
        return false;
      }}
    />
  );
}
