using System.Collections;
using System.Globalization;
using System.Text;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;

namespace Erewhon.Persistence.Seed;

/// <summary>
/// Reads the data lines of a seed data file, splits a line into tab-delimited tokens and converts
/// token text to decimals, dates and times.
/// </summary>
public static class DataFileReader
{
    /// <summary>The first character of a line that is skipped.</summary>
    public const char LineSkipDelimiter = '#';

    /// <summary>The character that separates the tokens of a line.</summary>
    public const char TokenDelimiter = '\t';

    /// <summary>The 1-based position of the first character of a line.</summary>
    public const int FirstIndexInLine = 1;

    private const char Blank = ' ';

    private const int NotFoundIndex = 0;

    private const NumberStyles DecimalStyles =
        NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint
        | NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite;

    private static readonly string[] DateFormats = { "MMMM d yyyy", "d MMMM yyyy", "dd MMMM yyyy" };

    private static readonly string[] TimeFormats = { "HH:mm:ss", "H:mm:ss" };

    /// <summary>
    /// Returns the data lines of a file read as Latin-1 text: each line with leading and trailing spaces
    /// removed, omitting lines that are empty after that or that start with <see cref="LineSkipDelimiter"/>.
    /// </summary>
    /// <param name="filePath">The path of the file to read.</param>
    /// <returns>
    /// The data lines in file order. LF, CR LF and CR end a line, and a last line without a line end is
    /// returned. The file is opened by this call and is owned by the first enumerator of the sequence: it is
    /// closed when that enumeration completes or that enumerator is disposed, whether or not the enumeration
    /// has started. Each later enumerator opens the file again and owns that opening in the same way.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory of the file does not exist.</exception>
    public static IEnumerable<string> ReadDataLines(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return new DataLineSequence(filePath);
    }

    /// <summary>
    /// Returns the next tab-delimited token of a line starting at a 1-based position and advances the
    /// position past the token.
    /// </summary>
    /// <param name="str">The line to read the token from.</param>
    /// <param name="pos">
    /// The 1-based position to start at; a value below 1 starts at the first character. On return it is
    /// the position after the token's delimiting tab, <c>0</c> when the token ended the line, or a
    /// position beyond the end of the line when the token was the last character or no token remained.
    /// </param>
    /// <returns>
    /// The token with leading and trailing spaces removed; leading tabs and spaces before it, and so empty
    /// fields, are skipped. A token that is only the last character of the line is returned as it is.
    /// Returns <see langword="null"/> when no token remains at or after the position, or when the token is
    /// the <see cref="MessageKeys.Null"/> text.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="str"/> is <see langword="null"/>.</exception>
    public static string? GetNextToken(string str, ref int pos)
    {
        ArgumentNullException.ThrowIfNull(str);

        int len = str.Length;
        pos = Math.Max(pos, FirstIndexInLine);

        if (pos > len)
        {
            return null;
        }

        int idx = pos;
        while (str[idx - 1] == TokenDelimiter || str[idx - 1] == Blank)
        {
            idx++;
            if (idx > len)
            {
                pos = idx;
                return null;
            }
        }

        if (idx == len)
        {
            pos = len + 1;
            return str[len - 1].ToString();
        }

        pos = idx;
        idx = str.IndexOf(TokenDelimiter, pos - 1) + 1;

        string token;
        if (idx == NotFoundIndex)
        {
            token = JadeValues.TrimBlanks(str.Substring(pos - 1));
            pos = idx;
        }
        else
        {
            token = JadeValues.TrimBlanks(str.Substring(pos - 1, idx - pos));
            pos = idx + 1;
        }

        if (string.Equals(token, MessageCatalog.Get(MessageKeys.Null), StringComparison.Ordinal))
        {
            return null;
        }

        return token;
    }

    /// <summary>Converts token text such as <c>250</c>, <c>1299.00</c> or <c>3.5</c> to a decimal, unrounded.</summary>
    /// <param name="text">The text to convert: an optional sign, digits and an optional decimal point, with optional surrounding white space.</param>
    /// <returns>The decimal value; <c>0</c> when <paramref name="text"/> is <see langword="null"/>, empty or not a decimal number.</returns>
    public static decimal ToDecimal(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0m;
        }

        return decimal.TryParse(text, DecimalStyles, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : 0m;
    }

    /// <summary>
    /// Converts token text such as <c>October 1 1999</c>, <c>22 January 2000</c> or <c>02 August 2000</c>
    /// to a date, with English month names.
    /// </summary>
    /// <param name="text">The text to convert, in the form <c>MMMM d yyyy</c>, <c>d MMMM yyyy</c> or <c>dd MMMM yyyy</c>; leading, trailing and extra inner white space is accepted.</param>
    /// <returns>The date; <see langword="null"/> when <paramref name="text"/> is <see langword="null"/> or is not a date in one of those forms.</returns>
    public static DateOnly? ToDate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateOnly date)
            ? date
            : null;
    }

    /// <summary>Converts token text such as <c>07:36:03</c> to a time of day.</summary>
    /// <param name="text">The text to convert, in the form <c>HH:mm:ss</c> or <c>H:mm:ss</c>.</param>
    /// <returns>The time of day; <see langword="null"/> when <paramref name="text"/> is <see langword="null"/> or is not a time in one of those forms.</returns>
    public static TimeOnly? ToTime(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return TimeOnly.TryParseExact(
            text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time)
            ? time
            : null;
    }

    /// <summary>Opens a file as Latin-1 text and returns an enumerator of its lines that owns the open file.</summary>
    /// <param name="filePath">The path of the file to open.</param>
    /// <returns>The enumerator of the file's lines; disposing it closes the file.</returns>
    private static IEnumerator<string> OpenRawLines(string filePath) =>
        File.ReadLines(filePath, Encoding.Latin1).GetEnumerator();

    /// <summary>The data lines of one file, which is opened when the sequence is created.</summary>
    private sealed class DataLineSequence : IEnumerable<string>
    {
        private readonly string _filePath;
        private IEnumerator<string>? _openedRawLines;

        /// <summary>Opens <paramref name="filePath"/> as Latin-1 text.</summary>
        /// <param name="filePath">The path of the file to open.</param>
        public DataLineSequence(string filePath)
        {
            _filePath = filePath;
            _openedRawLines = OpenRawLines(filePath);
        }

        /// <summary>
        /// Returns an enumerator of the data lines that owns an open file: the first call takes the file opened by the
        /// constructor, each later call opens the file again.
        /// </summary>
        /// <returns>The enumerator; disposing it closes its file.</returns>
        public IEnumerator<string> GetEnumerator()
        {
            IEnumerator<string> rawLines = _openedRawLines ?? OpenRawLines(_filePath);
            _openedRawLines = null;
            return new DataLineEnumerator(rawLines);
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Enumerates the data lines of an open file's lines: each line with leading and trailing spaces removed,
    /// omitting lines that are empty after that or that start with <see cref="LineSkipDelimiter"/>.
    /// </summary>
    /// <param name="rawLines">The enumerator of the file's lines, owned and disposed by this enumerator.</param>
    private sealed class DataLineEnumerator(IEnumerator<string> rawLines) : IEnumerator<string>
    {
        /// <summary>The current data line; empty before the first data line.</summary>
        public string Current { get; private set; } = string.Empty;

        /// <inheritdoc/>
        object IEnumerator.Current => Current;

        /// <summary>Advances to the next data line.</summary>
        /// <returns><see langword="true"/> when a data line was read; <see langword="false"/> at the end of the file.</returns>
        public bool MoveNext()
        {
            while (rawLines.MoveNext())
            {
                string line = JadeValues.TrimBlanks(rawLines.Current);
                if (line.Length == 0 || line[FirstIndexInLine - 1] == LineSkipDelimiter)
                {
                    continue;
                }

                Current = line;
                return true;
            }

            return false;
        }

        /// <summary>Not supported: the lines are read once.</summary>
        /// <exception cref="NotSupportedException">Always.</exception>
        public void Reset() => throw new NotSupportedException();

        /// <summary>Closes the file.</summary>
        public void Dispose() => rawLines.Dispose();
    }
}
