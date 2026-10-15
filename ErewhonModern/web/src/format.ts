// Display formats for money, decimal, date and timestamp values, matching JadeFormat in Erewhon.Application.

const MONTH_NAMES: readonly string[] = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

const ISO_DATE_TIME = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}):(\d{2})(?:\.\d+)?)?/;

const CURRENCY_SCALE = 2;
const DEFAULT_SCALE = 2;
const MAX_SCALE = 28;
const MAX_EXACT_UNITS = 1e15;

interface DateTimeParts {
  readonly year: string;
  readonly month: string;
  readonly monthNumber: number;
  readonly day: string;
  readonly dayNumber: number;
  readonly hour: string;
  readonly minute: string;
  readonly second: string;
}

interface DecimalParts {
  readonly negative: boolean;
  readonly integerPart: string;
  readonly fractionPart: string;
}

/** Returns the number of days in the month of the given year on the Gregorian calendar. */
function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    const leapYear = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
    return leapYear ? 29 : 28;
  }
  return month === 4 || month === 6 || month === 9 || month === 11 ? 30 : 31;
}

/**
 * Returns the date and time fields of a `yyyy-MM-dd` or `yyyy-MM-ddTHH:mm:ss[.fff]` text, with the time
 * `00:00:00` for a date-only text, or `null` when the text is not a valid date or timestamp.
 */
function parseDateTime(iso: string): DateTimeParts | null {
  const match = ISO_DATE_TIME.exec(iso);
  if (match === null) {
    return null;
  }

  const [, year, month, day, hour = '00', minute = '00', second = '00'] = match;
  const monthNumber = Number(month);
  const dayNumber = Number(day);
  const validDate =
    monthNumber >= 1 &&
    monthNumber <= 12 &&
    dayNumber >= 1 &&
    dayNumber <= daysInMonth(Number(year), monthNumber);
  const validTime = Number(hour) <= 23 && Number(minute) <= 59 && Number(second) <= 59;
  if (!validDate || !validTime) {
    return null;
  }

  return { year, month, monthNumber, day, dayNumber, hour, minute, second };
}

/**
 * Returns `''` for a null, undefined or empty text, the text unchanged when it is not a valid date or
 * timestamp, and otherwise the text that `render` builds from its fields.
 */
function formatDateTime(
  iso: string | null | undefined,
  render: (parts: DateTimeParts) => string,
): string {
  if (iso === null || iso === undefined || iso === '') {
    return '';
  }
  const parts = parseDateTime(iso);
  return parts === null ? iso : render(parts);
}

/** Returns the `dd MMMM yyyy` text of the date fields, e.g. `07 March 2024`. */
function longDate(parts: DateTimeParts): string {
  return `${parts.day} ${MONTH_NAMES[parts.monthNumber - 1]} ${parts.year}`;
}

/**
 * Returns `value × 10^scale` rounded to an integer, ties away from zero.
 *
 * @throws RangeError when the value is not finite or the result has 16 or more digits.
 */
function roundHalfAwayFromZero(value: number, scale: number): number {
  const magnitude = Math.abs(value) * 10 ** scale;
  if (!(magnitude < MAX_EXACT_UNITS)) {
    throw new RangeError(`${value} cannot be formatted with ${scale} decimal places.`);
  }
  const units = Math.round(Number(magnitude.toPrecision(15)));
  return value < 0 ? -units : units;
}

/** Returns the sign, integer digits and `scale` fraction digits of an integer count of `10^-scale` units. */
function splitUnits(units: number, scale: number): DecimalParts {
  const digits = String(Math.abs(units)).padStart(scale + 1, '0');
  const cut = digits.length - scale;
  return {
    negative: units < 0,
    integerPart: digits.slice(0, cut),
    fractionPart: digits.slice(cut),
  };
}

/** Returns the digits with a `,` between every group of three, counted from the right. */
function groupThousands(digits: string): string {
  const groups: string[] = [];
  for (let end = digits.length; end > 0; end -= 3) {
    groups.unshift(digits.slice(Math.max(0, end - 3), end));
  }
  return groups.join(',');
}

/**
 * Returns the amount rounded to 2 decimal places, ties away from zero, as `$#,##0.00` with a leading `-`
 * when negative, e.g. `1234.56` → `$1,234.56`, `0` → `$0.00`, `-1234.56` → `-$1,234.56`.
 * An amount that rounds to zero has no sign.
 *
 * @throws RangeError when the amount is not finite or has 14 or more integer digits.
 */
export function currencyFormat(value: number): string {
  const { negative, integerPart, fractionPart } = splitUnits(
    roundHalfAwayFromZero(value, CURRENCY_SCALE),
    CURRENCY_SCALE,
  );
  const text = `$${groupThousands(integerPart)}.${fractionPart}`;
  return negative ? `-${text}` : text;
}

/**
 * Returns the date part of a `yyyy-MM-dd` or `yyyy-MM-ddTHH:mm:ss[.fff]` text as `d/MM/yyyy`,
 * e.g. `2024-03-07` → `7/03/2024`. Returns `''` for null, undefined or `''`, and any other text that is
 * not a valid date unchanged.
 */
export function shortFormat(iso: string | null | undefined): string {
  return formatDateTime(iso, (parts) => `${parts.dayNumber}/${parts.month}/${parts.year}`);
}

/**
 * Returns the value rounded to `scale` decimal places, ties away from zero, with exactly `scale` decimal
 * places, no digit grouping and a leading `-` when negative, e.g. `11` → `11.00`, `-2.5` → `-2.50`,
 * `9555` → `9555.00`. With `scale` 0 there is no decimal point. A value that rounds to zero has no sign.
 *
 * @throws RangeError when `scale` is not an integer from 0 to 28, the value is not finite, or the value
 * scaled to `scale` decimal places has 16 or more digits.
 */
export function decimalString(value: number, scale: number = DEFAULT_SCALE): string {
  if (!Number.isInteger(scale) || scale < 0 || scale > MAX_SCALE) {
    throw new RangeError(`The scale ${scale} is not an integer from 0 to ${MAX_SCALE}.`);
  }
  const { negative, integerPart, fractionPart } = splitUnits(roundHalfAwayFromZero(value, scale), scale);
  const text = scale === 0 ? integerPart : `${integerPart}.${fractionPart}`;
  return negative ? `-${text}` : text;
}

/**
 * Returns the date part of a `yyyy-MM-dd` or `yyyy-MM-ddTHH:mm:ss[.fff]` text as `dd MMMM yyyy` with the
 * English month name, e.g. `2024-03-07` → `07 March 2024`. Returns `''` for null, undefined or `''`, and
 * any other text that is not a valid date unchanged.
 */
export function dateString(iso: string | null | undefined): string {
  return formatDateTime(iso, longDate);
}

/**
 * Returns a `yyyy-MM-ddTHH:mm:ss[.fff]` text as `dd MMMM yyyy, HH:mm:ss` on the 24-hour clock without
 * milliseconds, e.g. `2024-03-07T14:05:09.123` → `07 March 2024, 14:05:09`. A `yyyy-MM-dd` text gives the
 * time `00:00:00`. Returns `''` for null, undefined or `''`, and any other text that is not a valid date or
 * timestamp unchanged.
 */
export function timeStampString(iso: string | null | undefined): string {
  return formatDateTime(iso, (parts) => `${longDate(parts)}, ${parts.hour}:${parts.minute}:${parts.second}`);
}
