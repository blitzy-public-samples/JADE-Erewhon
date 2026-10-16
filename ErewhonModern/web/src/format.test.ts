import { describe, expect, it } from 'vitest';
import { currencyFormat, dateString, decimalString, shortFormat, timeStampString } from './format';

describe('format', () => {
  describe('currencyFormat', () => {
    it.each([
      [1234.56, '$1,234.56'],
      [9555, '$9,555.00'],
      [0, '$0.00'],
      [0.5, '$0.50'],
      [24895781, '$24,895,781.00'],
      [999.99, '$999.99'],
    ])('formats %s as %s with a dollar sign, thousands separators and two decimal places', (value, expected) => {
      expect(currencyFormat(value)).toBe(expected);
    });

    it.each([
      [-3.5, '-$3.50'],
      [-1234.56, '-$1,234.56'],
    ])('formats the negative amount %s as %s with the minus sign before the dollar sign', (value, expected) => {
      expect(currencyFormat(value)).toBe(expected);
    });

    it.each([
      [0.805, '$0.81'],
      [1.005, '$1.01'],
      [-0.805, '-$0.81'],
    ])('rounds the half-cent amount %s away from zero to %s', (value, expected) => {
      expect(currencyFormat(value)).toBe(expected);
    });
  });

  describe('shortFormat', () => {
    it.each([
      ['2024-03-07', '7/03/2024'],
      ['2024-12-25', '25/12/2024'],
    ])('formats the date %s as %s with an unpadded day and a two-digit month', (iso, expected) => {
      expect(shortFormat(iso)).toBe(expected);
    });

    it('formats the date part of a timestamp and drops the time', () => {
      expect(shortFormat('2000-01-01T09:30:00.000')).toBe('1/01/2000');
    });

    it.each([
      { label: 'null', value: null },
      { label: 'undefined', value: undefined },
      { label: 'an empty text', value: '' },
    ])('returns an empty text for $label', ({ value }) => {
      expect(shortFormat(value)).toBe('');
    });
  });

  describe('decimalString', () => {
    it.each([
      [500, '500.00'],
      [11, '11.00'],
      [3.5, '3.50'],
      [9555, '9555.00'],
      [-2.5, '-2.50'],
    ])('formats %s as %s with two decimal places and no thousands separators', (value, expected) => {
      expect(decimalString(value)).toBe(expected);
    });

    it('rounds 0.805 away from zero to 0.81 at the default scale', () => {
      expect(decimalString(0.805)).toBe('0.81');
    });

    it('rounds 12.5 away from zero to 13 with no decimal point at scale 0', () => {
      expect(decimalString(12.5, 0)).toBe('13');
    });
  });

  describe('dateString', () => {
    it('formats a date as a two-digit day, the full month name and the year', () => {
      expect(dateString('2024-03-07')).toBe('07 March 2024');
    });

    it('formats the date part of a timestamp and drops the time', () => {
      expect(dateString('2023-11-30T23:59:59.999')).toBe('30 November 2023');
    });

    it.each([
      ['2024-01-15', '15 January 2024'],
      ['2024-02-15', '15 February 2024'],
      ['2024-03-15', '15 March 2024'],
      ['2024-04-15', '15 April 2024'],
      ['2024-05-15', '15 May 2024'],
      ['2024-06-15', '15 June 2024'],
      ['2024-07-15', '15 July 2024'],
      ['2024-08-15', '15 August 2024'],
      ['2024-09-15', '15 September 2024'],
      ['2024-10-15', '15 October 2024'],
      ['2024-11-15', '15 November 2024'],
      ['2024-12-15', '15 December 2024'],
    ])('names the month of %s in %s', (iso, expected) => {
      expect(dateString(iso)).toBe(expected);
    });

    it('returns an empty text for null', () => {
      expect(dateString(null)).toBe('');
    });
  });

  describe('timeStampString', () => {
    it.each([
      ['2024-03-07T14:05:09.123', '07 March 2024, 14:05:09'],
      ['2024-03-07T00:00:00', '07 March 2024, 00:00:00'],
    ])('formats the timestamp %s as %s on the 24-hour clock', (iso, expected) => {
      expect(timeStampString(iso)).toBe(expected);
    });

    it('drops the milliseconds without rounding into the next second', () => {
      expect(timeStampString('2024-03-07T23:59:59.999')).toBe('07 March 2024, 23:59:59');
    });

    it('formats a date without a time as midnight', () => {
      expect(timeStampString('2024-03-07')).toBe('07 March 2024, 00:00:00');
    });

    it('returns an empty text for an empty text', () => {
      expect(timeStampString('')).toBe('');
    });
  });
});
