using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>The outcome of one git invocation made through <see cref="Git.RunAsync"/>.</summary>
/// <param name="ExitCode">git's exit code; -1 when it timed out or could not be started.</param>
/// <param name="StdOut">Everything git wrote to standard output, decoded as UTF-8.</param>
/// <param name="StdErr">
/// Everything git wrote to standard error, decoded as UTF-8; for a timeout or a missing binary, the
/// harness's own explanation is appended.
/// </param>
/// <param name="TimedOut">True when git did not finish within the timeout and its process tree was killed.</param>
/// <param name="Unavailable">True when git could not be started, for example because it is not installed.</param>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Unavailable)
{
    /// <summary>True when git ran to completion and exited 0.</summary>
    public bool Succeeded => !Unavailable && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs one git invocation for <see cref="JadeUnchangedCheck"/> and <see cref="OrderingEvidence"/>.
/// <see cref="Git.RunAsync"/> is the production implementation; tests pass a scripted one.
/// </summary>
/// <param name="repoRoot">The repository root; also git's working directory.</param>
/// <param name="args">The git arguments, passed as an argument list.</param>
/// <param name="timeout">The limit for the whole invocation.</param>
/// <param name="ct">Cancels the invocation.</param>
/// <returns>The invocation's outcome.</returns>
internal delegate Task<GitResult> GitRunner(string repoRoot, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

/// <summary>
/// The harness's single git runner. Every git invocation the harness makes (the JADE-unchanged and
/// ordering checks, and capture's <c>rev-parse HEAD</c>) goes through <see cref="RunAsync"/>, so each one
/// is bounded, read-only with respect to the index, and independent of the caller's git configuration
/// for path quoting and repository ownership.
/// </summary>
public static class Git
{
    /// <summary>The limit for one git invocation unless a caller passes its own.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const string Executable = "git";
    private const int NoExitCode = -1;

    // How long a killed process, and the pipes it held open, get to wind down. SIGKILL is immediate, so
    // this only bounds the pathological case and keeps every exit path finite.
    private static readonly TimeSpan WindDownLimit = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Returns true when <paramref name="repoRoot"/> holds git metadata: a <c>.git</c> directory, or a
    /// <c>.git</c> file as worktree and submodule checkouts have.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <exception cref="ArgumentException"><paramref name="repoRoot"/> is null, empty or white space.</exception>
    public static bool HasGitMetadata(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var metadata = Path.Combine(repoRoot, ".git");
        return Directory.Exists(metadata) || File.Exists(metadata);
    }

    /// <summary>
    /// Runs <c>git -c safe.directory=&lt;repo root&gt; -c core.quotePath=false &lt;args&gt;</c> in
    /// <paramref name="repoRoot"/> and returns its exit code and output.
    /// </summary>
    /// <remarks>
    /// <para>Arguments are passed as an argument list, never as one command string, so no value is
    /// re-split or interpreted by a shell. <c>safe.directory</c> lets git read a bind-mounted repository
    /// owned by another user (the harness container runs as root against <c>/repo</c>), and
    /// <c>core.quotePath=false</c> keeps non-ASCII paths verbatim.</para>
    /// <para>The environment sets <c>GIT_OPTIONAL_LOCKS=0</c>, so <c>git status</c> never rewrites
    /// <c>.git/index</c>; <c>GIT_TERMINAL_PROMPT=0</c>, so git never waits for credentials;
    /// <c>LC_ALL=C</c>, so messages are stable; and <c>GIT_CEILING_DIRECTORIES</c> set to the parent of
    /// the repository root, so a root without metadata is reported by git as "not a git repository"
    /// rather than resolved against an enclosing repository.</para>
    /// <para>Never throws for a missing git binary or a timeout: those come back as
    /// <see cref="GitResult.Unavailable"/> and <see cref="GitResult.TimedOut"/>, with the process tree
    /// killed in the timeout case.</para>
    /// </remarks>
    /// <param name="repoRoot">The repository root; also git's working directory.</param>
    /// <param name="args">The git arguments after the two <c>-c</c> settings.</param>
    /// <param name="timeout">The limit for the whole invocation; positive, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="ct">Cancels the invocation: the process tree is killed and the cancellation rethrown.</param>
    /// <returns>The invocation's outcome.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="repoRoot"/> is blank, or <paramref name="args"/> contains a null entry.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero or negative and not infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<GitResult> RunAsync(
        string repoRoot,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(args);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The git timeout must be positive or infinite.");
        }

        ct.ThrowIfCancellationRequested();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        using var process = new Process { StartInfo = CreateStartInfo(root, args) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new GitResult(
                NoExitCode,
                string.Empty,
                $"{Executable} could not be started in {root}: {ex.Message}",
                TimedOut: false,
                Unavailable: true);
        }

        // Nothing is ever written to git's stdin; closing it at once gives any read an immediate EOF.
        process.StandardInput.Close();

        // Both pipes are drained concurrently from the start, so a full stderr pipe can never block git
        // while stdout is being read, or the reverse.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.WaitAsync(deadline.Token).ConfigureAwait(false);
            var error = await stderr.WaitAsync(deadline.Token).ConfigureAwait(false);
            return new GitResult(process.ExitCode, output, error, TimedOut: false, Unavailable: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            KillTree(process);
            ct.ThrowIfCancellationRequested();

            var output = await DrainAsync(stdout).ConfigureAwait(false);
            var error = await DrainAsync(stderr).ConfigureAwait(false);
            var explanation = string.Create(
                CultureInfo.InvariantCulture,
                $"{Executable} did not finish within {timeout.TotalSeconds:0.###} s; its process tree was killed.");
            return new GitResult(
                NoExitCode,
                output,
                error.Length == 0 ? explanation : $"{error.TrimEnd()}{Environment.NewLine}{explanation}",
                TimedOut: true,
                Unavailable: false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string root, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("safe.directory=" + root);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args)
        {
            if (arg is null)
            {
                throw new ArgumentException("A git argument list cannot contain a null entry.", nameof(args));
            }

            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        var parent = Path.GetDirectoryName(root);
        if (!string.IsNullOrEmpty(parent))
        {
            startInfo.Environment["GIT_CEILING_DIRECTORIES"] = parent;
        }

        return startInfo;
    }

    // Kills git and everything it spawned, then waits briefly for the exit so no process outlives the
    // call. A process that already exited needs neither.
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the deadline and the kill: nothing is left to stop.
            return;
        }

        process.WaitForExit(WindDownLimit);
    }

    // The output a killed process left in its pipe, or nothing when the pipe is still held open (by a
    // descendant the kill could not reach) past the wind-down limit.
    private static async Task<string> DrainAsync(Task<string> read)
    {
        var finished = await Task.WhenAny(read, Task.Delay(WindDownLimit)).ConfigureAwait(false);
        return finished == read && read.IsCompletedSuccessfully ? await read.ConfigureAwait(false) : string.Empty;
    }
}

/// <summary>The outcome of <see cref="JadeUnchangedCheck.RunAsync"/>.</summary>
/// <param name="Result">The check's report row: name <c>jade-unchanged</c>, outcome <c>pass</c>, <c>fail</c> or <c>not-run</c>.</param>
/// <param name="Failures">
/// One <c>CHK-JADE-UNCHANGED:&lt;path&gt;</c> item per changed path, sorted by item id; or the single
/// <c>CHK-JADE-UNCHANGED</c> <c>not-run</c> item; empty on a pass.
/// </param>
/// <param name="Summary">
/// Paths checked and paths changed; null when the check did not run, so the report shows "not run"
/// rather than zero counts that were never observed.
/// </param>
public sealed record JadeUnchangedResult(CheckResult Result, IReadOnlyList<FailureItem> Failures, JadeUnchangedSummary? Summary);

/// <summary>
/// Proves that nothing under <c>Erewhon/</c> changed since the starting commit: no difference between
/// the working tree and <see cref="HarnessOptions.StartingCommit"/>, and no working-tree change git
/// status reports, untracked files included.
/// </summary>
/// <remarks>
/// <para>The check only reads. It runs <c>diff</c>, <c>status</c>, <c>ls-tree</c>, <c>hash-object</c>
/// (without <c>-w</c>) and <c>rev-parse --show-object-format</c> through <see cref="Git.RunAsync"/> and
/// never a command that writes the index, the object store or the working tree. It never reads file
/// content itself: the one thing it reads from the working tree is a symbolic link's own text, without
/// following the link.</para>
/// <para>Each changed path becomes a <c>CHK-JADE-UNCHANGED:&lt;path&gt;</c> item with nature
/// <c>modified:&lt;blob&gt;</c>, <c>added:&lt;blob&gt;</c> or <c>deleted</c>, where the blob is the id
/// git would store for the current content, so the same change yields the same item on every run and
/// different content never shares one. For a regular file that is the id <c>hash-object</c> computes,
/// filters applied. git stores a symbolic link as a blob of its link text, so for a link the blob is the
/// id of that text in the repository's object format, whether the link points to a file, to a directory
/// or to nothing; the target's content never enters it. No metadata, a starting commit git cannot
/// resolve (for example in a shallow clone), git itself unavailable, a changed path whose content git
/// cannot hash (for example a nested repository's directory), whose hash output is not an object id or
/// which cannot be checked for a symbolic link, or a symbolic link whose blob cannot be computed (its
/// text holds U+FFFD, which the runtime puts in place of bytes that are not valid UTF-8, or git does not
/// report <c>sha1</c> or <c>sha256</c> as the object format) yields the single
/// <c>CHK-JADE-UNCHANGED</c> <c>not-run</c> item instead, its detail stating the reason, including any
/// failing git command and what it printed.</para>
/// </remarks>
/// <param name="options">The harness settings: <see cref="HarnessOptions.RepoRoot"/> and <see cref="HarnessOptions.StartingCommit"/>.</param>
public sealed class JadeUnchangedCheck(HarnessOptions options)
{
    // The pathspec of the JADE project; every schema, .ddx, .ddxx, .mul, data and test file lives under it.
    private const string JadePathspec = "Erewhon/";

    // SHA-1 and SHA-256 object ids, as hash-object prints them.
    private const int Sha1HexLength = 40;
    private const int Sha256HexLength = 64;

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly GitRunner _git = Git.RunAsync;

    /// <summary>Creates the check with the git runner every git invocation goes through.</summary>
    /// <param name="options">The harness settings: <see cref="HarnessOptions.RepoRoot"/> and <see cref="HarnessOptions.StartingCommit"/>.</param>
    /// <param name="git">Runs each git invocation in place of <see cref="Git.RunAsync"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="git"/> is null.</exception>
    internal JadeUnchangedCheck(HarnessOptions options, GitRunner git)
        : this(options)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    private enum ChangeKind
    {
        // Ordered by precedence when the diff and the status disagree about one path.
        Modified = 1,
        Added = 2,
        Deleted = 3,
    }

    /// <summary>Runs the check.</summary>
    /// <param name="ct">Cancels the check; the running git process is killed and the cancellation rethrown.</param>
    /// <returns>The report row, the failing items and the counts.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<JadeUnchangedResult> RunAsync(CancellationToken ct)
    {
        var repoRoot = _options.ResolvePath(".");
        var commit = _options.StartingCommit?.Trim() ?? string.Empty;

        // A value starting with '-' would be read by git as an option, so it is never passed on.
        if (commit.Length == 0 || commit.StartsWith('-'))
        {
            return NotRun($"Harness:StartingCommit '{commit}' is not a commit id");
        }

        if (!Git.HasGitMetadata(repoRoot))
        {
            return NotRun($"no .git metadata at {repoRoot}");
        }

        string[] diffArgs = ["diff", "--name-status", "--no-renames", "-z", commit, "--", JadePathspec];
        string[] statusArgs = ["status", "--porcelain", "--no-renames", "--untracked-files=all", "-z", "--", JadePathspec];
        string[] treeArgs = ["ls-tree", "-r", "--name-only", "-z", commit, "--", JadePathspec];

        var diff = await _git(repoRoot, diffArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!diff.Succeeded)
        {
            return NotRun(CommandFailure(diffArgs, diff));
        }

        var status = await _git(repoRoot, statusArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return NotRun(CommandFailure(statusArgs, status));
        }

        var tree = await _git(repoRoot, treeArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!tree.Succeeded)
        {
            return NotRun(CommandFailure(treeArgs, tree));
        }

        var trackedPaths = SplitNul(tree.StdOut);
        if (string.IsNullOrWhiteSpace(diff.StdOut) && string.IsNullOrWhiteSpace(status.StdOut))
        {
            return new JadeUnchangedResult(
                new CheckResult(
                    CheckNames.JadeUnchanged,
                    CheckOutcomes.Pass,
                    trackedPaths.Count,
                    trackedPaths.Count,
                    $"no change under {JadePathspec} since starting commit {commit}"),
                [],
                new JadeUnchangedSummary(trackedPaths.Count, 0));
        }

        var changes = new SortedDictionary<string, ChangeKind>(StringComparer.Ordinal);
        ParseDiff(diff.StdOut, changes);
        ParseStatus(status.StdOut, changes);
        if (changes.Count == 0)
        {
            // Output that names no path cannot be attributed to an item; reporting it as not run keeps
            // the failure visible instead of passing or failing without evidence.
            return NotRun($"git printed changes under {JadePathspec} that name no path: {OneLine(diff.StdOut + status.StdOut)}");
        }

        var failures = new List<FailureItem>(changes.Count);
        foreach (var (path, kind) in changes)
        {
            // A deleted path has no content to hash.
            var nature = FailureItemIds.Deleted;
            if (kind != ChangeKind.Deleted)
            {
                var blob = await BlobIdAsync(repoRoot, path, ct).ConfigureAwait(false);
                if (!blob.Hashed)
                {
                    return NotRun(blob.Value);
                }

                nature = kind == ChangeKind.Added ? FailureItemIds.Added(blob.Value) : FailureItemIds.Modified(blob.Value);
            }

            failures.Add(new FailureItem(FailureItemIds.JadeUnchangedPath(path), nature, Operation: null));
        }

        failures.Sort(static (x, y) => string.CompareOrdinal(x.ItemId, y.ItemId));

        var unchangedTracked = trackedPaths.Count(path => !changes.ContainsKey(path));
        return new JadeUnchangedResult(
            new CheckResult(
                CheckNames.JadeUnchanged,
                CheckOutcomes.Fail,
                trackedPaths.Count,
                unchangedTracked,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{changes.Count} path(s) under {JadePathspec} changed since starting commit {commit}")),
            failures,
            new JadeUnchangedSummary(trackedPaths.Count, changes.Count));
    }

    // `diff --name-status --no-renames -z` prints <status>NUL<path>NUL per path; without rename
    // detection no entry carries a second path.
    private static void ParseDiff(string output, IDictionary<string, ChangeKind> changes)
    {
        var fields = output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var code = fields[i];
            var path = fields[i + 1];
            if (code.Length == 0 || path.Length == 0)
            {
                continue;
            }

            Record(changes, path, code[0] switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                _ => ChangeKind.Modified,
            });
        }
    }

    // `status --porcelain --no-renames -z` prints "XY <path>" NUL per path, the path relative to the
    // repository root.
    private static void ParseStatus(string output, IDictionary<string, ChangeKind> changes)
    {
        foreach (var entry in output.Split('\0'))
        {
            if (entry.Length < 4 || entry[2] != ' ')
            {
                continue;
            }

            var (x, y) = (entry[0], entry[1]);
            var kind = (x, y) switch
            {
                ('?', '?') => ChangeKind.Added,
                _ when x == 'D' || y == 'D' => ChangeKind.Deleted,
                _ when x == 'A' || y == 'A' => ChangeKind.Added,
                _ => ChangeKind.Modified,
            };
            Record(changes, entry[3..], kind);
        }
    }

    // A path deleted in either output is deleted; otherwise added beats modified.
    private static void Record(IDictionary<string, ChangeKind> changes, string path, ChangeKind kind)
    {
        if (!changes.TryGetValue(path, out var existing) || kind > existing)
        {
            changes[path] = kind;
        }
    }

    // The blob id git would store for the path's current content, nothing written. hash-object opens
    // the path, so for a symbolic link it would hash the target's content, or fail when the link
    // dangles or names a directory, while git stores the link as a blob of its link text. A path is
    // therefore first checked for a symbolic link without following it: a link is hashed from its text
    // (LinkBlobIdAsync), and every other path, a nested repository's directory included, by
    // hash-object with the repository's filters applied. When git cannot hash the content (git is
    // missing, times out, or exits non-zero, as it does for a nested repository's directory) or prints
    // anything but one object id, the lookup carries the failing command and its output instead. No
    // placeholder id is ever reported, because one would give different, unverified contents the same
    // item.
    private async Task<BlobLookup> BlobIdAsync(string repoRoot, string path, CancellationToken ct)
    {
        string? linkText;
        try
        {
            // LinkTarget reads the link itself and never follows it; it is null for anything that is
            // not a symbolic link, a path that no longer exists included.
            linkText = new FileInfo(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BlobLookup(Hashed: false, $"{path} could not be checked for a symbolic link: {OneLine(ex.Message)}");
        }

        if (linkText is not null)
        {
            return await LinkBlobIdAsync(repoRoot, path, linkText, ct).ConfigureAwait(false);
        }

        string[] hashArgs = ["hash-object", "--", path];
        var hash = await _git(repoRoot, hashArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!hash.Succeeded)
        {
            return new BlobLookup(Hashed: false, CommandFailure(hashArgs, hash));
        }

        var blob = hash.StdOut.Trim();
        return IsObjectId(blob)
            ? new BlobLookup(Hashed: true, blob)
            : new BlobLookup(
                Hashed: false,
                $"git {string.Join(' ', hashArgs)} printed '{OneLine(hash.StdOut)}', which is not an object id");
    }

    // The id git stores for a symbolic link (mode 120000): the hash of "blob <length>NUL" followed by
    // the link text's bytes, in the object format git reports for the repository. The runtime decodes
    // the link text as UTF-8 and replaces bytes that are not valid UTF-8 with U+FFFD, so a text holding
    // that character may no longer give the bytes git hashes, and no id is computed for it.
    private async Task<BlobLookup> LinkBlobIdAsync(string repoRoot, string path, string linkText, CancellationToken ct)
    {
        if (linkText.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return new BlobLookup(
                Hashed: false,
                $"symbolic link {path} has link text '{OneLine(linkText)}' that may not be valid UTF-8, so the blob git stores for it cannot be computed");
        }

        string[] formatArgs = ["rev-parse", "--show-object-format"];
        var format = await _git(repoRoot, formatArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!format.Succeeded)
        {
            return new BlobLookup(Hashed: false, CommandFailure(formatArgs, format));
        }

        var text = Encoding.UTF8.GetBytes(linkText);
        var blob = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"blob {text.Length}\0{linkText}"));
        var digest = format.StdOut.Trim() switch
        {
            "sha1" => SHA1.HashData(blob),
            "sha256" => SHA256.HashData(blob),
            _ => null,
        };
        return digest is null
            ? new BlobLookup(
                Hashed: false,
                $"git {string.Join(' ', formatArgs)} printed '{OneLine(format.StdOut)}', which is not an object format")
            : new BlobLookup(Hashed: true, Convert.ToHexStringLower(digest));
    }

    private static bool IsObjectId(string value) =>
        value.Length is Sha1HexLength or Sha256HexLength && value.All(char.IsAsciiHexDigitLower);

    private static List<string> SplitNul(string output) =>
        [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

    private static JadeUnchangedResult NotRun(string detail) =>
        new(
            new CheckResult(CheckNames.JadeUnchanged, CheckOutcomes.NotRun, 0, 0, detail),
            [new FailureItem(FailureItemIds.JadeUnchanged, FailureItemIds.NotRun, Operation: null, Detail: detail)],
            Summary: null);

    private static string CommandFailure(IReadOnlyList<string> args, GitResult result)
    {
        var outcome = result switch
        {
            { Unavailable: true } => "could not start",
            { TimedOut: true } => "timed out",
            _ => string.Create(CultureInfo.InvariantCulture, $"exited {result.ExitCode}"),
        };
        var stderr = OneLine(result.StdErr);
        return stderr.Length == 0
            ? $"git {string.Join(' ', args)} {outcome}"
            : $"git {string.Join(' ', args)} {outcome}: {stderr}";
    }

    // Report details are single lines; NUL separators and line breaks become spaces.
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\0', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // When Hashed, Value is the blob id git printed for the content, or computed for a symbolic link's
    // text; otherwise Value is the not-run detail saying why there is none.
    private readonly record struct BlobLookup(bool Hashed, string Value);
}
