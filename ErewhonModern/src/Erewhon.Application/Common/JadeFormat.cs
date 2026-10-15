using System.Globalization;

namespace Erewhon.Application.Common;

/// <summary>
/// String conversions of timestamps, dates, times and decimals, always in the invariant culture
/// (English month names, <c>.</c> decimal point, <c>,</c> digit grouping).
/// </summary>
public static class JadeFormat
{
    private const int MaxScale = 28;

    private const string TimeStampPattern = "dd MMMM yyyy, HH:mm:ss";
    private const string DatePattern = "dd MMMM yyyy";
    private const string TimePattern = "HH:mm:ss";
    private const string ShortDatePattern = "d/MM/yyyy";
    private const string CurrencyNumberPattern = "#,##0.00";
    private const string CurrencySymbol = "$";
    private const string NegativeSign = "-";

    /// <summary>
    /// Returns the timestamp as <c>dd MMMM yyyy, HH:mm:ss</c>, e.g. <c>05 March 2024, 14:07:09</c>.
    /// Milliseconds are not printed and <see cref="DateTime.Kind"/> is ignored.
    /// </summary>
    /// <param name="value">The timestamp to convert.</param>
    /// <returns>The timestamp text.</returns>
    public static string TimeStampString(DateTime value) =>
        value.ToString(TimeStampPattern, CultureInfo.InvariantCulture);

    /// <summary>Returns the date as <c>dd MMMM yyyy</c>, e.g. <c>05 March 2024</c>.</summary>
    /// <param name="value">The date to convert.</param>
    /// <returns>The date text.</returns>
    public static string DateString(DateOnly value) =>
        value.ToString(DatePattern, CultureInfo.InvariantCulture);

    /// <summary>Returns the date part of the timestamp as <c>dd MMMM yyyy</c>, e.g. <c>05 March 2024</c>.</summary>
    /// <param name="value">The timestamp whose date is converted.</param>
    /// <returns>The date text.</returns>
    public static string DateString(DateTime value) => DateString(DateOnly.FromDateTime(value));

    /// <summary>Returns the time as <c>HH:mm:ss</c> on the 24-hour clock, e.g. <c>09:05:03</c>.</summary>
    /// <param name="value">The time to convert.</param>
    /// <returns>The time text.</returns>
    public static string TimeString(TimeOnly value) =>
        value.ToString(TimePattern, CultureInfo.InvariantCulture);

    /// <summary>Returns the time part of the timestamp as <c>HH:mm:ss</c> on the 24-hour clock, e.g. <c>09:05:03</c>.</summary>
    /// <param name="value">The timestamp whose time is converted.</param>
    /// <returns>The time text.</returns>
    public static string TimeString(DateTime value) => TimeString(TimeOnly.FromDateTime(value));

    /// <summary>Returns the date as <c>d/MM/yyyy</c>, e.g. <c>5/03/2024</c>.</summary>
    /// <param name="value">The date to convert.</param>
    /// <returns>The short date text.</returns>
    public static string ShortFormat(DateOnly value) =>
        value.ToString(ShortDatePattern, CultureInfo.InvariantCulture);

    /// <summary>Returns the date part of the timestamp as <c>d/MM/yyyy</c>, e.g. <c>5/03/2024</c>.</summary>
    /// <param name="value">The timestamp whose date is converted.</param>
    /// <returns>The short date text.</returns>
    public static string ShortFormat(DateTime value) => ShortFormat(DateOnly.FromDateTime(value));

    /// <summary>
    /// Returns the value rounded to <paramref name="scale"/> decimal places, ties away from zero, and printed
    /// with exactly that many decimal places and no digit grouping, e.g. <c>11</c> → <c>11.00</c>,
    /// <c>-5.5</c> → <c>-5.50</c>, <c>9555</c> → <c>9555.00</c>. A value that rounds to zero prints without a sign.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="scale">The number of decimal places, from 0 to 28.</param>
    /// <returns>The decimal text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is below 0 or above 28.</exception>
    public static string DecimalString(decimal value, int scale = DecimalRules.Scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MaxScale);

        var rounded = Math.Round(value, scale, MidpointRounding.AwayFromZero);
        return rounded.ToString("F" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the value rounded to 2 decimal places, ties away from zero, as <c>$#,##0.00</c> with a leading
    /// <c>-</c> when negative, e.g. <c>1234.56</c> → <c>$1,234.56</c>, <c>0</c> → <c>$0.00</c>,
    /// <c>-12.5</c> → <c>-$12.50</c>. A value that rounds to zero prints without a sign.
    /// </summary>
    /// <param name="value">The amount to convert.</param>
    /// <returns>The currency text.</returns>
    public static string CurrencyFormat(decimal value)
    {
        var rounded = DecimalRules.Round2(value);
        var text = CurrencySymbol + Math.Abs(rounded).ToString(CurrencyNumberPattern, CultureInfo.InvariantCulture);
        return rounded < 0m ? NegativeSign + text : text;
    }
}
