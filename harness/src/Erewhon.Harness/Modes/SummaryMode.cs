using System.Globalization;
using System.Text;
using Erewhon.Harness.Lifecycle;
using Microsoft.Win32.SafeHandles;

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
/// <para>The report is read only strictly below the repository root, as replay writes it, and through
/// <see cref="ConfinedTree"/>: a path outside the root is never opened, a link on the way or at the report
/// is refused and never followed, and a FIFO, a socket, a device or a report over
/// <see cref="HarnessOptions.EvidenceByteLimit"/> fails at once, so no other file's content reaches
/// standard output and nothing blocks the mode.</para>
/// <para>The printed line is the last line of the report that is not blank, with trailing white space
/// removed and otherwise verbatim. The report writer owns the line's format, so it is not checked here.</para>
/// <para>Exit code 0 means the line was printed. Every other outcome (a report path outside the root, no
/// report, a directory at the report path, a link, a special file, a report over the limit or another read
/// failure, a report with no non-blank line, an unwritable standard output or cancellation) is one line
/// naming the path and the reason on standard error, never on <c>stdout</c>, and exit code 1. That line is
/// printed through <see cref="LifecycleConsole.Display"/>, so a path or message cannot break it or drive the
/// terminal. <see cref="RunAsync"/> never throws.</para>
/// <para>When the mode is given <see cref="Console.Out"/> itself on a system other than Windows, and
/// descriptor 1 is not seekable (a pipe, a socket or a terminal), the line goes straight to descriptor 1 as
/// the bytes <see cref="Console.Out"/> would write: the line in its encoding, followed by its line
/// terminator. A pipe whose reader has gone then fails the write with EPIPE, and the mode exits 1, as it
/// does for every other failed write. A full descriptor that another process made non-blocking fails the
/// write with EAGAIN, which is not a failure to print: the mode waits and writes again, as a blocking write
/// would wait, until the descriptor takes the line, the write fails, or the run is cancelled, which exits 1.
/// Once the descriptor is found not seekable, the line never goes to <see cref="Console.Out"/>. A seekable
/// descriptor (a regular file, <c>/dev/null</c> or <c>/dev/full</c>) is written through
/// <see cref="Console.Out"/>, which advances the file offset the descriptor shares with the shell and
/// reports a failed write such as ENOSPC or EBADF. Descriptor 1 is never closed.</para>
/// <para>When the mode is given <see cref="Console.Out"/> itself on Linux, it first checks, before any
/// write, that descriptor 1 is the standard output the harness was started with. A descriptor inherited
/// across exec never carries close-on-exec, because exec closes every descriptor that does, and the .NET
/// runtime opens every descriptor of its own with close-on-exec. So when descriptor 1 is not open at all,
/// or <c>/proc/self/fdinfo/1</c> shows close-on-exec and <c>/proc/self/fd/1</c> names an anonymous pipe, a
/// socket, an anonymous inode or a memfd file, standard output was closed when the harness started, and
/// the number may since belong to a pipe of the runtime's that would take the line. Nothing is then
/// written, and the mode exits 1 with the reason "standard output was closed when the harness started".
/// The flags line adds close-on-exec to the open flags of the file the descriptor refers to, and only those
/// kinds never hold O_CLOEXEC among their open flags; a file opened by path can (the terminal that
/// <c>docker run -t</c> gives a container does), so for one the check cannot tell and is skipped. It is also
/// skipped without <c>/proc/self/fdinfo</c>, or with a flags line that cannot be read. A <c>/dev/null</c>
/// that the parent opened as descriptor 1 is inherited and is printed to.</para>
/// </remarks>
public sealed class SummaryMode
{
    private const string LinePrefix = "summary: ";

    // The POSIX standard output descriptor, which Console.Out writes to on systems other than Windows.
    private const int StandardOutputDescriptor = 1;

    // POSIX's minimum PIPE_BUF. A pipe write of at most this many bytes is all or nothing, and a Unix-domain
    // stream socket queues so few bytes as one buffer or not at all, so on either a chunk whose write fails
    // with EAGAIN has not been written in part: writing it again neither repeats nor drops a byte.
    private const int AtomicWriteBytes = 512;

    // Linux lists each open descriptor of the process as a file here; the "flags:" line of a descriptor's
    // file holds, in octal, the open flags of the file it refers to, with O_CLOEXEC added when the
    // descriptor carries close-on-exec.
    private const string FdInfoDirectory = "/proc/self/fdinfo";
    private const string FdInfoFlagsKey = "flags:";

    // Linux lists each open descriptor of the process here as a link that names what it refers to.
    private const string FdDirectory = "/proc/self/fd";

    // O_CLOEXEC, 02000000 in octal, on every architecture .NET runs on Linux.
    private const long CloseOnExecFlag = 0x80000;

    // The kernel's open flags are 32 bits, which take at most 11 octal digits after the leading 0 it prints.
    private const int MaxFlagsDigits = 12;

    private const string ClosedAtStartReason = "standard output was closed when the harness started";

    private const string CancelledWhileFullReason = "the run was cancelled while standard output was full";

    // The link prefixes of anonymous pipes, sockets, anonymous inodes and memfd files: the kernel gives
    // these files their open flags without O_CLOEXEC, so a set bit in the "flags:" line is the descriptor's
    // own close-on-exec. A file opened by path can keep O_CLOEXEC among its open flags (on kernels before
    // 5.6, and for the terminal that `docker run -t` gives a container), so its bit tells nothing.
    private static readonly string[] KindsWithoutCloseOnExecInOpenFlags = ["pipe:[", "socket:[", "anon_inode:", "/memfd:"];

    // EAGAIN, which is also EWOULDBLOCK: 35 on macOS and FreeBSD, 11 on Linux. On these systems the HResult of
    // the IOException that a failed write throws is the errno.
    private static readonly int WouldBlockErrno = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35 : 11;

    // The wait before a chunk that failed with EAGAIN is written again: the first, doubled after each failed
    // attempt up to the cap, so a descriptor that frees up is written within the cap.
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly HarnessOptions _options;
    private readonly TextWriter _stdout;
    private readonly nint? _descriptor;

    /// <summary>Creates the mode.</summary>
    /// <param name="options">The harness settings, which supply the report path.</param>
    /// <param name="stdout">Where the final line is printed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="stdout"/> is null.</exception>
    public SummaryMode(HarnessOptions options, TextWriter stdout)
        : this(
            options,
            stdout,
            ReferenceEquals(stdout, Console.Out) && !OperatingSystem.IsWindows() ? StandardOutputDescriptor : null)
    {
    }

    /// <summary>Creates the mode with the descriptor that <paramref name="stdout"/> writes to.</summary>
    /// <param name="options">The harness settings, which supply the report path.</param>
    /// <param name="stdout">
    /// Where the final line is printed when <paramref name="descriptor"/> is null or seekable; otherwise only
    /// its encoding and line terminator are used.
    /// </param>
    /// <param name="descriptor">
    /// An open descriptor that the final line is written to directly when it is not seekable, or null to
    /// always print through <paramref name="stdout"/>. The mode never closes it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="stdout"/> is null.</exception>
    internal SummaryMode(HarnessOptions options, TextWriter stdout, nint? descriptor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _descriptor = descriptor;
    }

    /// <summary>Reads the report and prints its final line.</summary>
    /// <param name="ct">
    /// Cancels the read, and the wait for a full non-blocking standard output; cancellation exits 1.
    /// </param>
    /// <returns>0 when the final line was printed; 1 otherwise.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string path;
        ConfinedTree tree;
        bool contained;
        try
        {
            path = _options.EffectiveReportPath;
            tree = new ConfinedTree(_options.ResolvePath("."));
            contained = tree.Contains(path);
        }
        catch (Exception ex)
        {
            ReportFailure($"the report path could not be resolved: {Describe(ex)}");
            return 1;
        }

        // The report is the repository's own, as replay writes it only below the root; any other file is never
        // opened, so its content cannot reach standard output.
        if (!contained)
        {
            ReportFailure($"'{path}' is not below the repository root '{tree.Root}', so it is not read");
            return 1;
        }

        string? finalLine;
        try
        {
            // The read finds nothing at a directory, which would read as a missing report, so it is named first.
            if (Directory.Exists(path))
            {
                ReportFailure($"'{path}' is a directory, not a report file");
                return 1;
            }

            finalLine = await ReadFinalLineAsync(tree, path, ct).ConfigureAwait(false);
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
            // An inherited descriptor never carries close-on-exec, and every descriptor the runtime opens
            // does. Descriptor 1 with close-on-exec, or not open, is therefore not the standard output the
            // harness was started with, and a line written to it would count as printed without reaching
            // anyone. The bit is trusted only for kinds whose open flags never hold O_CLOEXEC; for any other
            // kind the check cannot tell and is skipped. Only the process's own standard output is checked:
            // a descriptor handed to the internal constructor was opened by .NET and carries close-on-exec
            // by design.
            if (_descriptor == StandardOutputDescriptor && IsOpenWithoutCloseOnExec(StandardOutputDescriptor) is false)
            {
                ReportFailure($"the final line of '{path}' could not be written to standard output: {ClosedAtStartReason}");
                return 1;
            }

            await WriteFinalLineAsync(finalLine, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReportFailure($"the final line of '{path}' could not be written to standard output: {CancelledWhileFullReason}");
            return 1;
        }
        catch (Exception ex)
        {
            ReportFailure($"the final line of '{path}' could not be written to standard output: {Describe(ex)}");
            return 1;
        }

        return 0;
    }

    private async Task WriteFinalLineAsync(string finalLine, CancellationToken ct)
    {
        if (_descriptor is { } descriptor && await TryWriteDirectlyAsync(descriptor, finalLine, ct).ConfigureAwait(false))
        {
            return;
        }

        _stdout.WriteLine(finalLine);
        _stdout.Flush();
    }

    // Console.Out cannot be trusted with a pipe: the Unix console stream treats EPIPE as a successful write,
    // so a line written to a pipe whose reader has gone would count as printed. A FileStream over the same
    // descriptor reports EPIPE, and every other failed write, as an exception, which exits 1. A seekable
    // descriptor stays with the console writer, because the FileStream writes at an offset of its own and
    // would leave the offset that the descriptor shares with the shell unmoved, so the shell's next write
    // would overwrite the line. Returns false only for a seekable descriptor, before anything is written.
    private async Task<bool> TryWriteDirectlyAsync(nint descriptor, string finalLine, CancellationToken ct)
    {
        // Not owned, so disposing the handle and the stream leaves the descriptor open.
        using var handle = new SafeFileHandle(descriptor, ownsHandle: false);
        using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
        if (stream.CanSeek)
        {
            return false;
        }

        var bytes = _stdout.Encoding.GetBytes(finalLine + _stdout.NewLine);
        for (var offset = 0; offset < bytes.Length; offset += AtomicWriteBytes)
        {
            await WriteChunkAsync(stream, bytes.AsMemory(offset, Math.Min(AtomicWriteBytes, bytes.Length - offset)), ct)
                .ConfigureAwait(false);
        }

        return true;
    }

    // A full descriptor that another process made non-blocking fails the write with EAGAIN instead of
    // blocking. The chunk is written again after a wait, until it is taken, the write fails otherwise (EPIPE
    // once the reader has gone), or the run is cancelled (SIGTERM and SIGINT cancel it), which throws
    // OperationCanceledException. A blocking write would wait for the reader without limit as well, so the
    // wait has no limit of its own.
    private static async Task WriteChunkAsync(FileStream stream, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        var delay = FirstRetryDelay;
        while (true)
        {
            try
            {
                stream.Write(chunk.Span);
                return;
            }
            catch (IOException ex) when (ex.HResult == WouldBlockErrno)
            {
                // Nothing of the chunk was written, so it is written again whole after the wait.
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = delay * 2 < MaxRetryDelay ? delay * 2 : MaxRetryDelay;
        }
    }

    /// <summary>Whether a descriptor of this process is open without close-on-exec, as every descriptor
    /// inherited across exec is.</summary>
    /// <param name="descriptor">The descriptor number.</param>
    /// <returns>
    /// True when the descriptor is open without close-on-exec; false when it is not open, or carries
    /// close-on-exec and is a pipe, a socket, an anonymous inode or a memfd file; null when the system cannot
    /// tell: not Linux, no <c>/proc/self/fdinfo</c>, a read failure, a flags line that cannot be parsed, or
    /// close-on-exec among the flags of any other kind of file, whose open flags can hold it.
    /// </returns>
    internal static bool? IsOpenWithoutCloseOnExec(int descriptor)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(FdInfoDirectory))
        {
            return null;
        }

        var name = descriptor.ToString(CultureInfo.InvariantCulture);
        string? linkTarget;
        try
        {
            var closeOnExec = CarriesCloseOnExec(File.ReadAllText(Path.Combine(FdInfoDirectory, name)));
            if (closeOnExec is not true)
            {
                // Open without close-on-exec, or a flags line that cannot be parsed (null).
                return !closeOnExec;
            }

            linkTarget = new FileInfo(Path.Combine(FdDirectory, name)).LinkTarget;
        }
        catch (FileNotFoundException)
        {
            // The directory lists every open descriptor, so a missing entry is a descriptor that is not open.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return OpenFlagsNeverHoldCloseOnExec(linkTarget) ? false : null;
    }

    /// <summary>Whether the kernel gives a file of this kind its open flags without O_CLOEXEC.</summary>
    /// <param name="linkTarget">
    /// What the descriptor's <c>/proc/self/fd</c> link names, such as <c>pipe:[4026]</c>; null when unknown.
    /// </param>
    /// <returns>
    /// True for an anonymous pipe, a socket, an anonymous inode or a memfd file, whose close-on-exec bit in
    /// <c>/proc/self/fdinfo</c> is the descriptor's own; false for a file opened by path, or an unknown one.
    /// </returns>
    internal static bool OpenFlagsNeverHoldCloseOnExec(string? linkTarget) =>
        linkTarget is not null
        && Array.Exists(KindsWithoutCloseOnExecInOpenFlags, kind => linkTarget.StartsWith(kind, StringComparison.Ordinal));

    /// <summary>Reads close-on-exec from the text of a Linux <c>/proc/&lt;pid&gt;/fdinfo/&lt;fd&gt;</c> file.</summary>
    /// <param name="fdInfo">The file's text, whose <c>flags:</c> line holds the open flags in octal.</param>
    /// <returns>Whether O_CLOEXEC is set, or null when the text has no flags line of octal digits.</returns>
    internal static bool? CarriesCloseOnExec(string fdInfo)
    {
        ArgumentNullException.ThrowIfNull(fdInfo);
        foreach (var line in fdInfo.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith(FdInfoFlagsKey, StringComparison.Ordinal))
            {
                continue;
            }

            var digits = line[FdInfoFlagsKey.Length..].Trim();
            if (digits.IsEmpty || digits.Length > MaxFlagsDigits)
            {
                return null;
            }

            long flags = 0;
            foreach (var digit in digits)
            {
                if (digit is < '0' or > '7')
                {
                    return null;
                }

                flags = (flags * 8) + (digit - '0');
            }

            return (flags & CloseOnExecFlag) != 0;
        }

        return null;
    }

    // Reads the report once through the repository root's confined reader, which refuses a link on its way or
    // at it, a FIFO, a socket, a device and a file over HarnessOptions.EvidenceByteLimit at once, then keeps the
    // last non-blank line of those bytes. ReadLine splits on "\n", "\r\n" and "\r", so a hand-written CRLF
    // report reads the same as the writer's LF output, and a BOM is skipped. Nothing there to read is a missing
    // report; null means the report holds no non-blank line.
    private static async Task<string?> ReadFinalLineAsync(ConfinedTree tree, string path, CancellationToken ct)
    {
        var bytes = await tree.ReadFileAsync(path, HarnessOptions.EvidenceByteLimit, ct).ConfigureAwait(false)
            ?? throw ConfinedTree.NotFound(path);
        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
        // Exception messages and paths can hold line breaks, terminal escapes and bidirectional controls;
        // Display escapes each of them, so the reason stays one inert line. Only failure reasons are escaped:
        // the final line on stdout is the report's own and is printed verbatim.
        var line = LifecycleConsole.Display(LinePrefix + reason);
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
