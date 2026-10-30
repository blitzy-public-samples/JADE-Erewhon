using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Erewhon.Contracts.Wire;

/// <summary>
/// The JADE 2022 wire forms shared by the stand-in writers and the harness comparers, normaliser and
/// conformance check: Microsoft JSON dates (<c>\/Date(ms)\/</c>), <c>xsd:date</c>, <c>xsd:dateTime</c>,
/// Binary as a JSON byte array or <c>base64Binary</c>, two-decimal <c>Decimal</c> numbers, the
/// <c>__type</c> member and the braced 38-character JADE GUID.
/// </summary>
/// <remarks>
/// Every formatter writes UTC with the invariant culture, because the JADE applications run with
/// <c>_webDefaultTimeZone Etc/UTC</c>. No <c>Try*</c> or <c>Is*</c> member trims, and each returns
/// <see langword="false"/>, never throwing, for invalid wire data: a <see langword="null"/> string, malformed
/// or out-of-range text, a JSON value of the wrong kind, or a default <see cref="JsonElement"/>. A lookalike
/// such as <c>{generated-guid}</c> or <c>{timestamp}</c> is therefore always invalid. The
/// <see langword="string"/> overloads accept <see langword="null"/>. The <see cref="JsonElement"/> overloads
/// take a value type, and a caller error such as passing an element of a disposed <see cref="JsonDocument"/>
/// still throws <see cref="ObjectDisposedException"/>. GUID, byte-array and base64 validity is a matter of
/// form alone. Date validity also covers the values: a Microsoft JSON date needs milliseconds inside the
/// <see cref="DateTimeOffset"/> range and a local time representable at its offset, an <c>xsd:date</c> a real
/// calendar date, and an <c>xsd:dateTime</c> real calendar and clock values inside that range. Every zone
/// offset has minutes at most 59 and is at most 14:00 in total.
/// </remarks>
public static partial class JadeWireFormats
{
    /// <summary>
    /// The JSON member naming the runtime class, written as the first member of an object when the runtime
    /// class differs from the class declared at that position, for example
    /// <c>"__type":"DTORetailItem"</c> for an item inside a <c>DTOItemsArray</c>.
    /// </summary>
    public const string TypeMemberName = "__type";

    /// <summary>
    /// The length of a braced JADE GUID such as <c>{eaf359ee-e216-ee11-9fbd-0050568d0023}</c>. The DTO
    /// <c>guid</c> attribute is <c>String[39]</c>: 38 characters plus the terminator.
    /// </summary>
    public const int JadeGuidLength = 38;

    private const string MicrosoftDatePrefix = "/Date(";
    private const string MicrosoftDateSuffix = ")/";
    private const string MicrosoftDateLiteralPrefix = "\"\\/Date(";
    private const string MicrosoftDateLiteralSuffix = ")\\/\"";

    // Unix-millisecond values of DateTimeOffset.MinValue and DateTimeOffset.MaxValue.
    private const long MinUnixMilliseconds = -62_135_596_800_000;
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    // XML Schema and DateTimeOffset both bound a zone offset to -14:00..+14:00.
    private const int MaxOffsetMinutes = 14 * 60;

    private const string XsdDateFormat = "yyyy-MM-dd";
    private const string XsdDateTimeUtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    // The patterns end in \z, not $: in .NET, $ also matches before a final "\n", which would let a value
    // with a trailing newline pass. [0-9] keeps non-ASCII digits out, which \d would admit.
    [GeneratedRegex(@"^/Date\((?<ms>-?[0-9]+)(?<zone>[+-][0-9]{4})?\)/\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex MicrosoftJsonDateRegex();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}(?<zone>Z|[+-][0-9]{2}:[0-9]{2})?\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex XsdDateRegex();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(?<zone>Z|[+-][0-9]{2}:[0-9]{2})?\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex XsdDateTimeRegex();

    /// <summary>
    /// Formats an instant as the raw Microsoft JSON date token, quotes and <c>\/</c> escapes included:
    /// <c>"\/Date(&lt;milliseconds since 1970-01-01T00:00:00Z&gt;)\/"</c>, for example
    /// <c>"\/Date(1767268800000)\/"</c> for 2026-01-01T12:00:00Z. No offset suffix is ever written. The token
    /// is valid JSON, intended for <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/>.
    /// </summary>
    /// <param name="value">The instant to write; its offset does not change the milliseconds.</param>
    /// <returns>The JSON string token, including its surrounding quotes.</returns>
    public static string FormatMicrosoftJsonDateLiteral(DateTimeOffset value) =>
        MicrosoftDateLiteralPrefix + FormatUnixMilliseconds(value) + MicrosoftDateLiteralSuffix;

    /// <summary>
    /// Formats a JADE <c>TimeStamp</c> held as a <see cref="DateTime"/> as the raw Microsoft JSON date token,
    /// exactly as <see cref="FormatMicrosoftJsonDateLiteral(DateTimeOffset)"/> does. A
    /// <see cref="DateTimeKind.Unspecified"/> value is taken as UTC.
    /// </summary>
    /// <param name="value">The timestamp to write.</param>
    /// <returns>The JSON string token, including its surrounding quotes.</returns>
    public static string FormatMicrosoftJsonDateLiteral(DateTime value) =>
        FormatMicrosoftJsonDateLiteral(ToUtcInstant(value));

    /// <summary>
    /// Formats a JADE <c>Date</c> (for example <c>listedDate</c> or <c>closureDate</c>) as the raw Microsoft
    /// JSON date token for 00:00:00 UTC of that date, for example <c>"\/Date(1767225600000)\/"</c> for
    /// 2026-01-01.
    /// </summary>
    /// <param name="value">The date to write.</param>
    /// <returns>The JSON string token, including its surrounding quotes.</returns>
    public static string FormatMicrosoftJsonDateLiteral(DateOnly value) =>
        FormatMicrosoftJsonDateLiteral(ToUtcInstant(value));

    /// <summary>
    /// Formats an instant as the decoded Microsoft JSON date value <c>/Date(&lt;milliseconds&gt;)/</c>, which
    /// is what <see cref="JsonElement.GetString"/> returns for the token
    /// <see cref="FormatMicrosoftJsonDateLiteral(DateTimeOffset)"/> writes.
    /// </summary>
    /// <param name="value">The instant to format.</param>
    /// <returns>The decoded string value, without quotes or escapes.</returns>
    public static string FormatMicrosoftJsonDateValue(DateTimeOffset value) =>
        MicrosoftDatePrefix + FormatUnixMilliseconds(value) + MicrosoftDateSuffix;

    /// <summary>
    /// Formats a JADE <c>TimeStamp</c> held as a <see cref="DateTime"/> as the decoded Microsoft JSON date
    /// value <c>/Date(&lt;milliseconds&gt;)/</c>. A <see cref="DateTimeKind.Unspecified"/> value is taken as UTC.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <returns>The decoded string value, without quotes or escapes.</returns>
    public static string FormatMicrosoftJsonDateValue(DateTime value) =>
        FormatMicrosoftJsonDateValue(ToUtcInstant(value));

    /// <summary>
    /// Formats a JADE <c>Date</c> as the decoded Microsoft JSON date value for 00:00:00 UTC of that date,
    /// for example <c>/Date(1767225600000)/</c> for 2026-01-01.
    /// </summary>
    /// <param name="value">The date to format.</param>
    /// <returns>The decoded string value, without quotes or escapes.</returns>
    public static string FormatMicrosoftJsonDateValue(DateOnly value) =>
        FormatMicrosoftJsonDateValue(ToUtcInstant(value));

    /// <summary>
    /// Parses a decoded Microsoft JSON date value: exactly <c>/Date(</c>, an optional <c>-</c>, one or more
    /// ASCII digits, an optional offset <c>+hhmm</c> or <c>-hhmm</c> (four digits, minutes at most 59, at most
    /// 14:00 in total), then <c>)/</c>. The milliseconds must fit the <see cref="DateTimeOffset"/> range.
    /// </summary>
    /// <remarks>
    /// The input is the string value after JSON decoding, so <c>"\/Date(1)\/"</c> and <c>"/Date(1)/"</c> in a
    /// body both arrive as <c>/Date(1)/</c>; text that still holds the backslashes is invalid. The
    /// milliseconds always denote the UTC instant; an offset only changes the offset of the result.
    /// </remarks>
    /// <param name="value">The decoded string value.</param>
    /// <param name="result">The instant, at the parsed offset or UTC; <see langword="default"/> when invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has the form above.</returns>
    public static bool TryParseMicrosoftJsonDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        Match match = MicrosoftJsonDateRegex().Match(value);
        if (!match.Success
            || !long.TryParse(match.Groups["ms"].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long milliseconds)
            || milliseconds < MinUnixMilliseconds
            || milliseconds > MaxUnixMilliseconds)
        {
            return false;
        }

        DateTimeOffset instant = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        Group zone = match.Groups["zone"];
        if (!zone.Success)
        {
            result = instant;
            return true;
        }

        ReadOnlySpan<char> offsetText = zone.ValueSpan;
        int hours = TwoDigits(offsetText, 1);
        int minutes = TwoDigits(offsetText, 3);
        if (!IsValidOffset(hours, minutes))
        {
            return false;
        }

        int totalMinutes = (hours * 60) + minutes;
        TimeSpan offset = TimeSpan.FromMinutes(offsetText[0] == '-' ? -totalMinutes : totalMinutes);
        try
        {
            result = instant.ToOffset(offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // The instant is representable in UTC but its local time at this offset is not.
            return false;
        }
    }

    /// <summary>
    /// Whether a decoded JSON string value is a valid Microsoft JSON date, as defined by
    /// <see cref="TryParseMicrosoftJsonDate(string?, out DateTimeOffset)"/>.
    /// </summary>
    /// <param name="value">The decoded string value.</param>
    /// <returns><see langword="true"/> when the value is valid.</returns>
    public static bool IsMicrosoftJsonDate(string? value) => TryParseMicrosoftJsonDate(value, out _);

    /// <summary>
    /// Whether a JSON element is a string whose decoded value is a valid Microsoft JSON date. Numbers,
    /// <c>null</c> and every other kind are invalid, and so is a string that cannot be decoded, such as one
    /// holding an unpaired surrogate escape like <c>"\uD800"</c>.
    /// </summary>
    /// <param name="element">The element to test.</param>
    /// <returns><see langword="true"/> when the element is a valid Microsoft JSON date string.</returns>
    public static bool IsMicrosoftJsonDate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value;
        try
        {
            value = element.GetString();
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            // A string token holding an unpaired surrogate escape such as \uD800 parses, but its value cannot
            // be decoded, so it is not a valid date. A disposed document is a caller error and still throws.
            return false;
        }

        return IsMicrosoftJsonDate(value);
    }

    /// <summary>Formats a JADE <c>Date</c> as an <c>xsd:date</c> with no timezone: <c>yyyy-MM-dd</c>.</summary>
    /// <param name="value">The date to format.</param>
    /// <returns>The date text, for example <c>2026-01-01</c>.</returns>
    public static string FormatXsdDate(DateOnly value) =>
        value.ToString(XsdDateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an <c>xsd:date</c>: <c>yyyy-MM-dd</c> with ASCII digits and a real calendar date, optionally
    /// followed by <c>Z</c> or <c>+hh:mm</c>/<c>-hh:mm</c> (minutes at most 59, at most 14:00 in total). The
    /// timezone is permitted by XML Schema and does not change the result. A dateTime, unpadded parts such
    /// as <c>2026-1-1</c> and impossible dates such as <c>2026-02-30</c> are invalid.
    /// </summary>
    /// <param name="value">The element text.</param>
    /// <param name="result">The date; <see langword="default"/> when invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has the form above.</returns>
    public static bool TryParseXsdDate(string? value, out DateOnly result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        Match match = XsdDateRegex().Match(value);
        return match.Success
            && IsValidXsdTimeZone(match.Groups["zone"])
            && DateOnly.TryParseExact(value.AsSpan(0, XsdDateFormat.Length), XsdDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>
    /// Whether the text is a valid <c>xsd:date</c>, as defined by
    /// <see cref="TryParseXsdDate(string?, out DateOnly)"/>.
    /// </summary>
    /// <param name="value">The element text.</param>
    /// <returns><see langword="true"/> when the value is valid.</returns>
    public static bool IsXsdDate(string? value) => TryParseXsdDate(value, out _);

    /// <summary>
    /// Formats a JADE <c>TimeStamp</c> as an <c>xsd:dateTime</c> in UTC with exactly three fraction digits:
    /// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>, for example <c>2025-12-31T23:00:00.000Z</c> for
    /// 2026-01-01T12:00:00+13:00. JADE timestamps have millisecond precision, so sub-millisecond ticks are
    /// truncated. This SOAP form is assumed rather than recorded from JADE (KL-SOAP-SHAPES).
    /// </summary>
    /// <param name="value">The instant to format.</param>
    /// <returns>The UTC dateTime text.</returns>
    public static string FormatXsdDateTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(XsdDateTimeUtcFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a JADE <c>TimeStamp</c> held as a <see cref="DateTime"/> exactly as
    /// <see cref="FormatXsdDateTime(DateTimeOffset)"/> does. A <see cref="DateTimeKind.Unspecified"/> value is
    /// taken as UTC.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <returns>The UTC dateTime text.</returns>
    public static string FormatXsdDateTime(DateTime value) => FormatXsdDateTime(ToUtcInstant(value));

    /// <summary>
    /// Parses an <c>xsd:dateTime</c>: <c>yyyy-MM-ddTHH:mm:ss</c> with ASCII digits, an optional fraction of
    /// one or more digits, and an optional <c>Z</c> or <c>+hh:mm</c>/<c>-hh:mm</c> (minutes at most 59, at
    /// most 14:00 in total). The calendar and clock values are then checked by
    /// <see cref="XmlConvert.ToDateTimeOffset(string)"/>. A value without a timezone is taken as UTC, so the
    /// result never depends on the host's local zone.
    /// </summary>
    /// <remarks>
    /// The lexical check comes first because <see cref="XmlConvert.ToDateTimeOffset(string)"/> on its own also
    /// accepts date-only values and the other XML Schema date and time forms.
    /// </remarks>
    /// <param name="value">The element text.</param>
    /// <param name="result">The instant at its parsed offset; <see langword="default"/> when invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has the form above.</returns>
    public static bool TryParseXsdDateTime(string? value, out DateTimeOffset result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        Match match = XsdDateTimeRegex().Match(value);
        Group zone = match.Groups["zone"];
        if (!match.Success || !IsValidXsdTimeZone(zone))
        {
            return false;
        }

        try
        {
            result = XmlConvert.ToDateTimeOffset(zone.Success ? value : value + "Z");
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Whether the text is a valid <c>xsd:dateTime</c>, as defined by
    /// <see cref="TryParseXsdDateTime(string?, out DateTimeOffset)"/>.
    /// </summary>
    /// <param name="value">The element text.</param>
    /// <returns><see langword="true"/> when the value is valid.</returns>
    public static bool IsXsdDateTime(string? value) => TryParseXsdDateTime(value, out _);

    /// <summary>
    /// Formats a GUID in the braced 38-character JADE form with lowercase hex digits, for example
    /// <c>{00000000-0000-4000-8000-000000000001}</c>, the form every GUID in the seed files takes.
    /// </summary>
    /// <param name="value">The GUID to format.</param>
    /// <returns>The braced GUID text.</returns>
    public static string FormatJadeGuid(Guid value) => value.ToString("B");

    /// <summary>
    /// Whether the text is a braced JADE GUID: exactly <see cref="JadeGuidLength"/> characters, <c>{</c> at
    /// index 0, <c>}</c> at 37, <c>-</c> at 9, 14, 19 and 24, and an ASCII hex digit at every other position,
    /// for example <c>{eaf359ee-e216-ee11-9fbd-0050568d0023}</c>.
    /// </summary>
    /// <remarks>
    /// Hex digits of either case are accepted: a generated identifier is compared by its form only, never by
    /// its content, while <see cref="FormatJadeGuid(Guid)"/> always writes lowercase. The check is written
    /// out rather than delegated to <see cref="Guid.TryParse(string?, out Guid)"/>, which trims whitespace and
    /// accepts other layouts. Unbraced, parenthesised and whitespace-padded values are therefore invalid, and
    /// so is <c>{generated-guid}</c>.
    /// </remarks>
    /// <param name="value">The text to test.</param>
    /// <returns><see langword="true"/> when the value has the form above.</returns>
    public static bool IsValidJadeGuid(string? value)
    {
        if (value is null || value.Length != JadeGuidLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool valid = index switch
            {
                0 => character == '{',
                JadeGuidLength - 1 => character == '}',
                9 or 14 or 19 or 24 => character == '-',
                _ => char.IsAsciiHexDigit(character),
            };
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes a JADE <c>Binary</c> as a JSON array of byte values, never as base64 text: the bytes
    /// <c>FF D8 FF E0</c> become <c>[255,216,255,224]</c>, and no bytes become <c>[]</c>.
    /// </summary>
    /// <param name="writer">The writer, positioned where a value may be written.</param>
    /// <param name="bytes">The bytes to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void WriteByteArray(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        WriteByteValues(writer, bytes);
    }

    /// <summary>
    /// Writes a property whose value is a JADE <c>Binary</c> as a JSON array of byte values, as
    /// <see cref="WriteByteArray(Utf8JsonWriter, ReadOnlySpan{byte})"/> does.
    /// </summary>
    /// <param name="writer">The writer, positioned inside an object.</param>
    /// <param name="propertyName">The property name, written as given.</param>
    /// <param name="bytes">The bytes to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="propertyName"/> is <see langword="null"/>.</exception>
    public static void WriteByteArray(Utf8JsonWriter writer, string propertyName, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(propertyName);
        writer.WriteStartArray(propertyName);
        WriteByteValues(writer, bytes);
    }

    /// <summary>
    /// Reads a JADE <c>Binary</c> written as a JSON array of byte values. Only an array whose every item is a
    /// JSON number holding an integer from 0 to 255 is valid; <c>[]</c> gives an empty array. Strings,
    /// base64 text included, <c>null</c>, objects and items such as <c>256</c>, <c>-1</c>, <c>1.5</c> or
    /// <c>"1"</c> are invalid.
    /// </summary>
    /// <param name="element">The element to read.</param>
    /// <param name="bytes">The bytes; <see langword="null"/> when invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="element"/> has the form above.</returns>
    public static bool TryReadByteArray(JsonElement element, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        byte[] values = new byte[element.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetByte(out values[index]))
            {
                return false;
            }

            index++;
        }

        bytes = values;
        return true;
    }

    /// <summary>
    /// Formats a JADE <c>Binary</c> as SOAP <c>base64Binary</c> text with padding and no line breaks, for
    /// example <c>/9j/4A==</c> for <c>FF D8 FF E0</c>.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The base64 text; empty for no bytes.</returns>
    public static string FormatBase64Binary(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>
    /// Parses SOAP <c>base64Binary</c> text. The empty string gives an empty array. Text that starts or
    /// ends with whitespace is invalid; whitespace between base64 characters is accepted, as
    /// <see cref="Convert.TryFromBase64String(string, Span{byte}, out int)"/> accepts it.
    /// </summary>
    /// <param name="value">The element text.</param>
    /// <param name="bytes">The decoded bytes; <see langword="null"/> when invalid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid base64.</returns>
    public static bool TryParseBase64Binary(string? value, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (value is null)
        {
            return false;
        }

        if (value.Length == 0)
        {
            bytes = [];
            return true;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        // Convert skips ' ', '\t', '\r' and '\n' and decodes whole four-character groups ending in at most two
        // '=', so counting the other characters gives the exact decoded length and one array receives the
        // bytes. Base64.IsValid would not do: it rejects nonzero unused bits, as in "AB==", that Convert accepts.
        ReadOnlySpan<char> text = value.AsSpan();
        int significant = text.Length;
        if (text.ContainsAny(" \t\r\n"))
        {
            significant -= text.Count(' ') + text.Count('\t') + text.Count('\r') + text.Count('\n');
        }

        int padding = 0;
        for (int index = text.Length - 1; index >= 0 && padding <= 2; index--)
        {
            if (text[index] == '=')
            {
                padding++;
            }
            else if (text[index] is not (' ' or '\t' or '\r' or '\n'))
            {
                break;
            }
        }

        if (significant % 4 != 0 || padding > 2)
        {
            return false;
        }

        byte[] decoded = new byte[(significant / 4 * 3) - padding];
        if (!Convert.TryFromBase64String(value, decoded, out int written) || written != decoded.Length)
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    /// <summary>
    /// Formats a JADE <c>Decimal</c> with exactly two decimals, rounded half away from zero, with the
    /// invariant culture: <c>2600000</c> gives <c>2600000.00</c>, <c>899</c> gives <c>899.00</c>,
    /// <c>12.345</c> gives <c>12.35</c> and <c>-12.345</c> gives <c>-12.35</c>. A value that rounds to zero
    /// gives <c>0.00</c>, never <c>-0.00</c>. The rounding rule is assumed (KL-STANDIN-ROUNDING).
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The text, valid both as a JSON number and as XML element text.</returns>
    public static string FormatDecimal2(decimal value)
    {
        decimal rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);

        // A negative value that rounds to zero keeps its sign bit; replacing it with 0m drops the sign.
        if (rounded == decimal.Zero)
        {
            rounded = decimal.Zero;
        }

        return rounded.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a JADE <c>Decimal</c> as a JSON number with exactly two decimals, as
    /// <see cref="FormatDecimal2(decimal)"/> formats it, whatever the scale of <paramref name="value"/>.
    /// <see cref="Utf8JsonWriter.WriteNumberValue(decimal)"/> would follow the scale and write <c>899</c>
    /// for <c>899m</c>.
    /// </summary>
    /// <param name="writer">The writer, positioned where a value may be written.</param>
    /// <param name="value">The value to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void WriteDecimal2(Utf8JsonWriter writer, decimal value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(FormatDecimal2(value), skipInputValidation: false);
    }

    private static string FormatUnixMilliseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset ToUtcInstant(DateOnly value) =>
        new(value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);

    // The stand-in holds TimeStamps as UTC DateTime values. Converting a DateTime to DateTimeOffset
    // implicitly reads an Unspecified kind as host-local time, so it is taken as UTC here instead, keeping
    // the output independent of the host's zone.
    private static DateTimeOffset ToUtcInstant(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    };

    private static void WriteByteValues(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            writer.WriteNumberValue((int)value);
        }

        writer.WriteEndArray();
    }

    // A zone group matched by the XML Schema patterns: absent, "Z", or "+hh:mm"/"-hh:mm".
    private static bool IsValidXsdTimeZone(Group zone)
    {
        if (!zone.Success)
        {
            return true;
        }

        ReadOnlySpan<char> text = zone.ValueSpan;
        return text is "Z" || IsValidOffset(TwoDigits(text, 1), TwoDigits(text, 4));
    }

    private static bool IsValidOffset(int hours, int minutes) =>
        minutes <= 59 && (hours * 60) + minutes <= MaxOffsetMinutes;

    // The callers' patterns guarantee two ASCII digits at the index.
    private static int TwoDigits(ReadOnlySpan<char> text, int index) =>
        ((text[index] - '0') * 10) + (text[index + 1] - '0');
}
