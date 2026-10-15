namespace Erewhon.Application.Common;

/// <summary>
/// JADE primitive null tests, date validity tests and string trimming.
/// </summary>
public static class JadeValues
{
    private static readonly char[] WhiteSpaceCharacters = { '\r', '\n', '\t', ' ', '\0' };

    /// <summary>Returns true for 0, the JADE null value of an Integer.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is 0.</returns>
    public static bool IsNull(int value) => value == 0;

    /// <summary>Returns true for 0, the JADE null value of an Integer64.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is 0.</returns>
    public static bool IsNull(long value) => value == 0L;

    /// <summary>Returns true for a numeric zero of any scale, the JADE null value of a Decimal.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> equals 0, including 0.00 and -0.00.</returns>
    public static bool IsNull(decimal value) => value == 0m;

    /// <summary>Returns true for <see langword="null"/> or 0.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has no value or is 0.</returns>
    public static bool IsNull(int? value) => value is null || IsNull(value.Value);

    /// <summary>Returns true for <see langword="null"/> or 0.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has no value or is 0.</returns>
    public static bool IsNull(long? value) => value is null || IsNull(value.Value);

    /// <summary>Returns true for <see langword="null"/> or a numeric zero of any scale.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has no value or equals 0.</returns>
    public static bool IsNull(decimal? value) => value is null || IsNull(value.Value);

    /// <summary>Returns true for <see langword="null"/> or the empty string, the JADE null value of a String.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is <see langword="null"/> or has length 0; a string of spaces returns <see langword="false"/>.</returns>
    public static bool IsNull(string? value) => string.IsNullOrEmpty(value);

    /// <summary>Returns true for <see langword="null"/> or the default date, the JADE null value of a Date.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has no value or equals <c>default(DateOnly)</c>.</returns>
    public static bool IsNull(DateOnly? value) => value is null || value.Value == default;

    /// <summary>Returns true for <see langword="null"/> or the default timestamp, the JADE null value of a TimeStamp.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has no value or equals <c>default(DateTime)</c>.</returns>
    public static bool IsNull(DateTime? value) => value is null || value.Value == default;

    /// <summary>Returns true for <see langword="null"/> or an empty array, the JADE null value of a Binary.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is <see langword="null"/> or has length 0.</returns>
    public static bool IsNull(byte[]? value) => value is null || value.Length == 0;

    /// <summary>Returns true for a date that is not the JADE null date.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> has no value or equals <c>default(DateOnly)</c>; otherwise <see langword="true"/>.</returns>
    public static bool IsValid(DateOnly? value) => !IsNull(value);

    /// <summary>Returns true for a timestamp that is not the JADE null timestamp.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="false"/> when <paramref name="value"/> has no value or equals <c>default(DateTime)</c>; otherwise <see langword="true"/>.</returns>
    public static bool IsValid(DateTime? value) => !IsNull(value);

    /// <summary>Returns the value with leading and trailing space characters removed.</summary>
    /// <param name="value">The string to trim.</param>
    /// <returns>The value without leading or trailing <c>' '</c> characters; tabs, carriage returns and line feeds are kept; <see langword="null"/> returns the empty string.</returns>
    public static string TrimBlanks(string? value) => (value ?? string.Empty).Trim(' ');

    /// <summary>Returns the value with leading and trailing carriage return, line feed, tab, space and NUL characters removed.</summary>
    /// <param name="value">The string to trim.</param>
    /// <returns>The value without leading or trailing <c>'\r'</c>, <c>'\n'</c>, <c>'\t'</c>, <c>' '</c> or <c>'\0'</c> characters; interior characters are kept; <see langword="null"/> or a string made only of those characters returns the empty string.</returns>
    public static string TrimWhiteSpace(string? value) => (value ?? string.Empty).Trim(WhiteSpaceCharacters);
}
