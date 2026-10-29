using System.Text;

namespace Erewhon.Harness.Modes;

/// <summary>
/// The <c>summary</c> mode: prints the final line of <c>RESULTS.md</c>
/// (<c>N of 22 operations equivalent, M documented differences</c>) to standard output.
/// </summary>
/// <remarks>
/// <para>The Compose service <c>run-complete</c> runs this mode once replay has exited 0, with the
/// repository mounted read-only and no network, so that <c>docker compose up --build</c> ends by
/// printing the result and returns 0. The mode therefore only reads
/// <see cref="HarnessOptions.EffectiveReportPath"/>: it never opens a file for writing, creates a
/// directory or touches the network.</para>
/// <para>The printed line is the last line of the report that is not blank, with trailing white space
/// removed and otherwise verbatim. The report writer owns the line's format, so it is not checked here.</para>
/// <para>Exit code 0 means the line was printed. Every other outcome (no report, a directory at the
/// report path, a read failure, a report with no non-blank line, an unwritable standard output or
/// cancellation) is one line naming the path and the reason on standard error, never on
/// <c>stdout</c>, and exit code 1. <see cref="RunAsync"/> never throws.</para>
/// </remarks>
/// <param name="options">The harness settings, which supply the report path.</param>
/// <param name="stdout">Where the final line is printed.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="stdout"/> is null.</exception>
public sealed class SummaryMode(HarnessOptions options, TextWriter stdout)
{
    private const string LinePrefix = "summary: ";

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));

    /// <summary>Reads the report and prints its final line.</summary>
    /// <param name="ct">Cancels the read; cancellation exits 1.</param>
    /// <returns>0 when the final line was printed; 1 otherwise.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string path;
        try
        {
            path = _options.EffectiveReportPath;
        }
        catch (Exception ex)
        {
            ReportFailure($"the report path could not be resolved: {Describe(ex)}");
            return 1;
        }

        string? finalLine;
        try
        {
            // On Linux a directory opens as "access denied", which would hide the real cause.
            if (Directory.Exists(path))
            {
                ReportFailure($"'{path}' is a directory, not a report file");
                return 1;
            }

            finalLine = await ReadFinalLineAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFailure($"'{path}' {ReadFailureReason(ex)}");
            return 1;
        }

        if (finalLine is null)
        {
            ReportFailure($"'{path}' contains no non-blank line");
            return 1;
        }

        try
        {
            _stdout.WriteLine(finalLine);
            _stdout.Flush();
        }
        catch (Exception ex)
        {
            ReportFailure($"the final line of '{path}' could not be written to standard output: {Describe(ex)}");
            return 1;
        }

        return 0;
    }

    // Streams the file so only the last non-blank line is held. ReadLine splits on "\n", "\r\n" and "\r",
    // so a hand-written CRLF report reads the same as the writer's LF output, and a BOM is skipped.
    private static async Task<string?> ReadFinalLineAsync(string path, CancellationToken ct)
    {
        var readOnly = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
        };

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, readOnly);
        string? last = null;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                last = line;
            }
        }

        return last?.TrimEnd();
    }

    private static string ReadFailureReason(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "does not exist",
        UnauthorizedAccessException => $"cannot be read (access denied): {ex.Message}",
        IOException => $"cannot be read: {ex.Message}",
        OperationCanceledException => "was not read: the run was cancelled",
        _ => $"could not be read: {Describe(ex)}",
    };

    private static string Describe(Exception ex) => $"{ex.GetType().FullName}: {ex.Message}";

    private static void ReportFailure(string reason)
    {
        // Exception messages and paths can hold line breaks; the reason must stay on one line.
        var line = (LinePrefix + reason)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception writeFailure) when (writeFailure is IOException or ObjectDisposedException)
        {
            // Standard error is the only channel for the reason. When it is unusable, exit code 1 still
            // reports the failure, and RunAsync must not throw.
        }
    }
}
