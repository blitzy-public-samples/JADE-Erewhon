using System.Text;

namespace Erewhon.StandIn.Seed;

/// <summary>
/// Port of the way JADE's <c>InitialDataLoader</c> reads the seed files in
/// <c>Erewhon/DataFiles</c>: the line rule shared by every loader and the
/// <c>getNextToken</c> method (<c>Erewhon/ErewhonModelSchema.scm:7983-8050</c>).
/// </summary>
/// <remarks>
/// Splitting a line on tab characters is not equivalent to this rule: runs of tabs and spaces
/// before a token are skipped, so empty fields collapse, and a token equal to <c>null</c>
/// becomes the null string. Positions are 1-based, as in JADE.
/// </remarks>
public static class SeedTokenizer
{
    /// <summary>
    /// JADE <c>FileConstants.FirstIndexInLine</c> (<c>ErewhonModelSchema.scm:78</c>): the position
    /// every loader starts a line from.
    /// </summary>
    public const int FirstIndexInLine = 1;

    /// <summary>
    /// JADE <c>FileConstants.LineSkipDelimiter</c> (<c>ErewhonModelSchema.scm:79</c>): a line whose
    /// first character, after trimming blanks, is this character is skipped.
    /// </summary>
    public const char LineSkipDelimiter = '#';

    /// <summary>
    /// JADE translatable string <c>$Null</c>, <c>Null="null"</c> (<c>ErewhonModelSchema.scm:2818</c>):
    /// a token with exactly this text is read as the null string.
    /// </summary>
    public const string NullToken = "null";

    /// <summary>
    /// JADE's null string. The stand-in holds it as the empty string.
    /// </summary>
    public const string JadeNullString = "";

    private const int ArrayNullIndex = 0;
    private const char Tab = '\t';
    private const char Space = ' ';
    private const int Windows1252CodePage = 1252;

    static SeedTokenizer()
    {
        // Registration is idempotent; doing it here keeps the tokenizer usable without relying
        // on Program.cs having registered the provider first.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SeedEncoding = Encoding.GetEncoding(Windows1252CodePage);
    }

    /// <summary>
    /// Windows-1252, the decoding of JADE's <c>File.Kind_ANSI</c> input files. Taken from the
    /// code-pages provider rather than <see cref="Encoding.Default"/>, so a local process and the
    /// Linux container decode the seed files identically.
    /// </summary>
    public static Encoding SeedEncoding { get; }

    /// <summary>
    /// Reads every data line of a seed file, applying the loaders' line rule
    /// (<c>ErewhonModelSchema.scm:8198-8203</c>, the same at 8130-8133, 8721-8724 and 8893-8896):
    /// <c>line := inputFile.readLine.trimBlanks()</c>, then the line is used only when it is not
    /// the null string and its first character is not <see cref="LineSkipDelimiter"/>.
    /// </summary>
    /// <param name="path">The seed file to read.</param>
    /// <returns>
    /// The data lines in file order, each trimmed of leading and trailing spaces and carrying its
    /// 1-based physical line number. Tabs are kept; the token rule skips leading ones.
    /// </returns>
    /// <remarks>
    /// <c>\n</c>, <c>\r\n</c> and <c>\r</c> terminators are all accepted, as with JADE's automatic
    /// end-of-line handling (<c>endOfLine := null</c>), and a final line without a terminator is
    /// read. No byte-order mark is interpreted, because JADE reads these files as ANSI.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The file does not exist, as JADE's <c>openInput</c> fails.</exception>
    /// <exception cref="DirectoryNotFoundException">The file's directory does not exist.</exception>
    public static IReadOnlyList<SeedLine> ReadLines(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var lines = new List<SeedLine>();
        using var reader = new StreamReader(path, SeedEncoding, detectEncodingFromByteOrderMarks: false);

        var lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var text = TrimBlanks(raw);
            if (text.Length == 0 || text[FirstIndexInLine - 1] == LineSkipDelimiter)
            {
                continue;
            }

            lines.Add(new SeedLine(lineNumber, text));
        }

        return lines;
    }

    /// <summary>
    /// Port of <c>InitialDataLoader.getNextToken(str : String; pos : Integer io) : String</c>
    /// (<c>ErewhonModelSchema.scm:7983-8050</c>): returns the next tab-separated token of
    /// <paramref name="line"/> starting at the 1-based position <paramref name="pos"/>, and
    /// advances <paramref name="pos"/> past it.
    /// </summary>
    /// <param name="line">A seed line, normally a <see cref="SeedLine.Text"/>.</param>
    /// <param name="pos">
    /// The 1-based position to start from; values below 1 are treated as 1. On return it is the
    /// position after the token's delimiter, or as described below.
    /// </param>
    /// <returns>The token, trimmed of leading and trailing spaces, or <see cref="JadeNullString"/>.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// Tabs and spaces before a token are skipped, so an empty field between two tabs collapses
    /// and later fields shift left (<c>"a\t\tb"</c> gives <c>a</c>, then <c>b</c>). No current
    /// seed line has an empty field.
    /// </description></item>
    /// <item><description>
    /// A token equal to <see cref="NullToken"/> (ordinal) and a token past the end of the line
    /// both become <see cref="JadeNullString"/>: <c>Clients.txt:1</c>'s website <c>null</c> is read
    /// as <c>""</c>.
    /// </description></item>
    /// <item><description>
    /// Tokens are trimmed of spaces only: <c>Clients.txt:2</c>'s email <c>"bo@wol.com "</c> is read
    /// as <c>bo@wol.com</c>, and so are the trailing blanks at <c>Clients.txt:4</c> and <c>:19</c>,
    /// <c>RetailItems.txt:49</c> and <c>:56</c> and <c>TenderItems.txt:32</c>. Spaces inside a
    /// token are kept (<c>"September 9  1999"</c>).
    /// </description></item>
    /// <item><description>
    /// When the first non-blank character is the line's last character, that character is
    /// returned as is and <paramref name="pos"/> becomes the line length plus one, so the next
    /// call returns <see cref="JadeNullString"/>.
    /// </description></item>
    /// <item><description>
    /// When no tab follows the token, <paramref name="pos"/> becomes 0 (JADE
    /// <c>ArrayNullIndex</c>), so a further call restarts at position 1 and returns the line's
    /// first token again: <c>"a\tbc"</c> gives <c>a</c>, <c>bc</c>, then <c>a</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static string NextToken(string line, ref int pos)
    {
        ArgumentNullException.ThrowIfNull(line);

        var len = line.Length;
        pos = Math.Max(pos, FirstIndexInLine);

        if (pos > len)
        {
            return JadeNullString;
        }

        var idx = pos;
        while (CharAt(line, idx) is Tab or Space)
        {
            idx++;
            if (idx > len)
            {
                pos = idx;
                return JadeNullString;
            }
        }

        if (idx == len)
        {
            // JADE returns str[len] directly: no trimming and no $Null comparison.
            pos = len + 1;
            return CharAt(line, len).ToString();
        }

        pos = idx;

        // JADE str.pos(Tab, pos): 1-based index of the next tab, ArrayNullIndex (0) when absent.
        var tabIdx = line.IndexOf(Tab, pos - 1) + 1;

        string token;
        if (tabIdx == ArrayNullIndex)
        {
            token = TrimBlanks(line.Substring(pos - 1));

            // Deliberately ArrayNullIndex, as in JADE: a further read restarts at the first token
            // instead of returning the null string. Loaders observe this when they read more
            // fields than a line holds, so it is part of the behaviour being reproduced.
            pos = tabIdx;
        }
        else
        {
            token = TrimBlanks(line.Substring(pos - 1, tabIdx - pos));
            pos = tabIdx + 1;
        }

        if (string.Equals(token, NullToken, StringComparison.Ordinal))
        {
            return JadeNullString;
        }

        return token;
    }

    /// <summary>
    /// JADE <c>String.trimBlanks</c>: removes leading and trailing space characters (U+0020) only.
    /// Tabs and other whitespace are kept.
    /// </summary>
    /// <param name="value">The string to trim.</param>
    /// <returns><paramref name="value"/> without leading and trailing spaces.</returns>
    public static string TrimBlanks(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim(Space);
    }

    // JADE str[idx] is 1-based.
    private static char CharAt(string line, int idx) => line[idx - 1];
}

/// <summary>
/// One data line of a seed file, as returned by <see cref="SeedTokenizer.ReadLines"/>.
/// </summary>
/// <param name="LineNumber">The 1-based physical line number in the file, for diagnostics.</param>
/// <param name="Text">The line trimmed of leading and trailing spaces.</param>
public readonly record struct SeedLine(int LineNumber, string Text);

/// <summary>
/// Reads the tokens of one seed line in order, as a loader does with
/// <c>pos := FirstIndexInLine</c> followed by successive <c>getNextToken(line, pos)</c> calls.
/// </summary>
public sealed class SeedTokenCursor
{
    /// <summary>
    /// Starts a cursor at <see cref="SeedTokenizer.FirstIndexInLine"/> of <paramref name="line"/>.
    /// </summary>
    /// <param name="line">The seed line to read.</param>
    public SeedTokenCursor(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Line = line;
        Position = SeedTokenizer.FirstIndexInLine;
    }

    /// <summary>The line being read.</summary>
    public string Line { get; }

    /// <summary>The 1-based position the next token is read from, with JADE's semantics.</summary>
    public int Position { get; private set; }

    /// <summary>Reads the next token with <see cref="SeedTokenizer.NextToken"/>.</summary>
    /// <returns>The token, or <see cref="SeedTokenizer.JadeNullString"/>.</returns>
    public string Next()
    {
        var position = Position;
        var token = SeedTokenizer.NextToken(Line, ref position);
        Position = position;
        return token;
    }
}
