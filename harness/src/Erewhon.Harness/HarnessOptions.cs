using System.Globalization;
using System.Runtime.InteropServices;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Baseline;
using Microsoft.Win32.SafeHandles;

namespace Erewhon.Harness;

/// <summary>
/// Base URLs of one component the harness talks to over HTTP: its REST, SOAP and admin listeners.
/// </summary>
public sealed class HarnessEndpoints
{
    /// <summary>REST listener base URL, for example <c>http://facade:65000</c>.</summary>
    public string RestBaseUrl { get; set; } = "";

    /// <summary>SOAP listener base URL, for example <c>http://facade:21001</c>.</summary>
    public string SoapBaseUrl { get; set; } = "";

    /// <summary>
    /// Admin listener base URL, for example <c>http://facade:9090</c>. Blank for an external backend,
    /// which has no admin listener.
    /// </summary>
    public string AdminUrl { get; set; } = "";
}

/// <summary>
/// Settings of the equivalence harness, bound from configuration section <see cref="SectionName"/>.
/// This class owns every harness default and every repository path the harness reads or writes, so no
/// other harness file hard-codes a path or a URL.
/// </summary>
/// <remarks>
/// <para>Sources, lowest precedence first: <c>harness/config/harness.json</c> (optional), environment
/// variables without a prefix (<c>Harness__Target__RestBaseUrl</c> is <c>Harness:Target:RestBaseUrl</c>),
/// then <c>--Harness:Key=value</c> arguments. Every default is a property initialiser, so a missing
/// <c>harness.json</c> still yields working values. Binding into <see cref="Target"/> and
/// <see cref="Backend"/> reuses their default instances, so a key that is not set keeps its default.</para>
/// <para>An empty environment value overrides a default: on .NET 10, <c>Harness__Backend__AdminUrl=</c>
/// binds <see cref="HarnessEndpoints.AdminUrl"/> to <c>""</c> (verified with
/// <c>ConfigurationBinder</c> and <c>AddEnvironmentVariables</c>), which makes
/// <see cref="IsExternalBackend"/> true without any post-processing by the caller.</para>
/// <para>No credential, token or header setting exists: no layer authenticates, and the admin listeners
/// are contained by network placement instead.</para>
/// </remarks>
public sealed class HarnessOptions
{
    /// <summary>Configuration section bound to this class.</summary>
    public const string SectionName = "Harness";

    /// <summary>
    /// Harness version recorded as the baseline manifest's <c>harnessVersion</c>. The project file carries
    /// no <c>Version</c>, so this constant is the only place it is stated.
    /// </summary>
    public const string HarnessVersion = "1.0.0";

    /// <summary>
    /// The most bytes the harness takes from one input: one repository file it reads (the configuration, the
    /// case matrix, the register, a baseline file, a checked-in contract or the report) or one HTTP response
    /// body. A larger input fails explicitly and is never truncated. A constant rather than a setting, so no
    /// configuration source can lift it.
    /// </summary>
    public const int EvidenceByteLimit = 32 * 1024 * 1024;

    // Compose service names of the facade and the stand-in on the project networks.
    private const string FacadeHost = "facade";
    private const string StandInHost = "standin";

    // The ErewhonREST and WebServiceHTTP application ports of the JADE system
    // (Erewhon/ErehwhonExternalInterfacesSchema.ddx), which the facade and the stand-in both keep.
    private const int RestPort = 65000;
    private const int SoapPort = 21001;

    private const string DefaultRepoRoot = "/repo";
    private const string DefaultSeedDirectory = "Erewhon/DataFiles";
    private const string DefaultExpectedReferenceDate = "2026-01-01";
    private const string DefaultExecutionMode = "docker";
    private const int DefaultReadinessTimeoutSeconds = 120;
    private const int DefaultReadinessPollIntervalSeconds = 1;
    private const int DefaultRequestTimeoutSeconds = 60;
    private const int DefaultShutdownTimeoutSeconds = 15;
    private const int DefaultShutdownRequestTimeoutSeconds = 5;

    /// <summary>The facade, which replay sends every case to.</summary>
    public HarnessEndpoints Target { get; set; } = DefaultEndpoints(FacadeHost);

    /// <summary>
    /// The backend: the stand-in, which capture records from and replay resets, confirms and fetches the
    /// WSDL from. A blank <see cref="HarnessEndpoints.AdminUrl"/> marks an external backend.
    /// </summary>
    public HarnessEndpoints Backend { get; set; } = DefaultEndpoints(StandInHost);

    /// <summary>
    /// Repository root: the <c>/repo</c> bind mount under Compose, the working tree under
    /// <c>harness/scripts/run-local.sh</c>. Every relative path resolves against it; blank means
    /// <c>/repo</c>.
    /// </summary>
    public string RepoRoot { get; set; } = DefaultRepoRoot;

    /// <summary>
    /// Where <c>RESULTS.md</c> is written; null or blank means <c>&lt;RepoRoot&gt;/RESULTS.md</c>.
    /// Read through <see cref="EffectiveReportPath"/>. It must lie strictly below <see cref="RepoRoot"/>:
    /// replay refuses any other path as a failed report write (<c>CHK-REPORT</c>, exit 1).
    /// </summary>
    public string? ReportPath { get; set; }

    /// <summary>
    /// <c>docker</c> or <c>local-processes</c>, as stated in the report's run facts, which read it through
    /// <see cref="RecognisedExecutionMode"/>. Capture refuses any other value; replay records it as a startup
    /// failure and reports the mode as unknown.
    /// </summary>
    public string ExecutionMode { get; set; } = DefaultExecutionMode;

    /// <summary>
    /// The starting commit ("Add documentation to repo") that the JADE-unchanged check compares
    /// <c>Erewhon/</c> against.
    /// </summary>
    public string StartingCommit { get; set; } = "19a5189e9075349aca54de1fb5fdf2d8727403e7";

    /// <summary>
    /// Expected stand-in reference date (<c>yyyy-MM-dd</c>) at first capture; equal to the stand-in's
    /// <c>StandIn:ReferenceDate</c>. Blank means <c>2026-01-01</c>. Read through
    /// <see cref="EffectiveExpectedReferenceDate"/>.
    /// </summary>
    public string ExpectedReferenceDate { get; set; } = DefaultExpectedReferenceDate;

    /// <summary>
    /// Seed directory fingerprinted at first capture, relative to <see cref="RepoRoot"/> unless absolute;
    /// blank means <c>Erewhon/DataFiles</c>. Read through <see cref="EffectiveSeedDirectory"/>.
    /// </summary>
    public string SeedDirectory { get; set; } = DefaultSeedDirectory;

    /// <summary>How long each component's <c>GET /health</c> is polled before it counts as not ready.</summary>
    public int ReadinessTimeoutSeconds { get; set; } = DefaultReadinessTimeoutSeconds;

    /// <summary>Interval between readiness polls.</summary>
    public int ReadinessPollIntervalSeconds { get; set; } = DefaultReadinessPollIntervalSeconds;

    /// <summary>Limit for one case request.</summary>
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

    /// <summary>Limit for shutting down one component, from its shutdown call to its confirmed stop.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = DefaultShutdownTimeoutSeconds;

    /// <summary>Limit for one <c>POST /admin/shutdown</c> request.</summary>
    public int ShutdownRequestTimeoutSeconds { get; set; } = DefaultShutdownRequestTimeoutSeconds;

    /// <summary>
    /// True when the backend has no admin URL. Replay then skips the reset and the seed confirmation,
    /// never shuts the backend down, and the report records the substitution KL-SUB-NORESET.
    /// </summary>
    public bool IsExternalBackend => string.IsNullOrWhiteSpace(Backend.AdminUrl);

    /// <summary>
    /// <see cref="ExecutionMode"/> trimmed when it is one of the two ways a run is executed,
    /// <see cref="CaptureExecutionValues.Docker"/> or <see cref="CaptureExecutionValues.LocalProcesses"/>
    /// (compared ordinally, as capture compares it); null for any other value, blank included.
    /// </summary>
    public string? RecognisedExecutionMode
    {
        get
        {
            var mode = ExecutionMode?.Trim();
            return mode is CaptureExecutionValues.Docker or CaptureExecutionValues.LocalProcesses ? mode : null;
        }
    }

    /// <summary>Report path: <see cref="ReportPath"/> resolved, or <c>&lt;RepoRoot&gt;/RESULTS.md</c>.</summary>
    public string EffectiveReportPath =>
        ResolvePath(string.IsNullOrWhiteSpace(ReportPath) ? "RESULTS.md" : ReportPath);

    /// <summary><see cref="SeedDirectory"/> resolved.</summary>
    public string EffectiveSeedDirectory =>
        ResolvePath(string.IsNullOrWhiteSpace(SeedDirectory) ? DefaultSeedDirectory : SeedDirectory);

    /// <summary>
    /// <see cref="ExpectedReferenceDate"/> trimmed, or <c>2026-01-01</c> when it is blank (for example an
    /// empty <c>Harness__ExpectedReferenceDate</c>), so the first-capture date check always has a value.
    /// The value is not validated here; the first capture requires it to be a <c>yyyy-MM-dd</c> date.
    /// </summary>
    public string EffectiveExpectedReferenceDate =>
        string.IsNullOrWhiteSpace(ExpectedReferenceDate) ? DefaultExpectedReferenceDate : ExpectedReferenceDate.Trim();

    /// <summary><c>&lt;RepoRoot&gt;/baseline</c>, the only tree capture writes records into.</summary>
    public string BaselineDirectory => ResolvePath("baseline");

    /// <summary><c>baseline/manifest.json</c>.</summary>
    public string ManifestPath => ResolvePath(Path.Combine("baseline", "manifest.json"));

    /// <summary><c>baseline/records</c>, holding the <c>rest</c> and <c>soap</c> record directories.</summary>
    public string RecordsDirectory => ResolvePath(Path.Combine("baseline", "records"));

    /// <summary><c>baseline/normalisation-rules.json</c>.</summary>
    public string NormalisationRulesPath => ResolvePath(Path.Combine("baseline", "normalisation-rules.json"));

    /// <summary><c>baseline/conformance.json</c>.</summary>
    public string ConformancePath => ResolvePath(Path.Combine("baseline", "conformance.json"));

    /// <summary><c>baseline/wsdl/standin-ErewhonWebService.wsdl</c>, the WSDL bytes the stand-in served.</summary>
    public string BaselineWsdlPath =>
        ResolvePath(Path.Combine("baseline", "wsdl", "standin-ErewhonWebService.wsdl"));

    /// <summary><c>contracts/wsdl/ErewhonWebService.wsdl</c>, the WSDL the facade publishes.</summary>
    public string ContractsWsdlPath => ResolvePath(Path.Combine("contracts", "wsdl", "ErewhonWebService.wsdl"));

    /// <summary><c>contracts/openapi/erewhon-rest.openapi.json</c>, the checked-in OpenAPI description.</summary>
    public string ContractsOpenApiPath =>
        ResolvePath(Path.Combine("contracts", "openapi", "erewhon-rest.openapi.json"));

    /// <summary><c>harness/config/case-matrix.json</c>.</summary>
    public string CaseMatrixPath => ResolvePath(Path.Combine("harness", "config", "case-matrix.json"));

    /// <summary><c>harness/config/known-limitations.json</c>.</summary>
    public string KnownLimitationsPath => ResolvePath(Path.Combine("harness", "config", "known-limitations.json"));

    /// <summary><c>harness/config/harness.json</c>.</summary>
    public string HarnessConfigPath => ResolvePath(Path.Combine("harness", "config", "harness.json"));

    /// <summary><c>artifacts</c>, the per-run output tree.</summary>
    public string ArtifactsDirectory => ResolvePath("artifacts");

    /// <summary>
    /// <c>artifacts/facade-tests</c>, where the facade tests write their TRX, their xunit XML (which records
    /// each failure's exception type) and their exit code.
    /// </summary>
    public string FacadeTestsDirectory => ResolvePath(Path.Combine("artifacts", "facade-tests"));

    /// <summary><see cref="ReadinessTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ReadinessTimeout => Seconds(ReadinessTimeoutSeconds, DefaultReadinessTimeoutSeconds);

    /// <summary><see cref="ReadinessPollIntervalSeconds"/> as a duration.</summary>
    public TimeSpan ReadinessPollInterval =>
        Seconds(ReadinessPollIntervalSeconds, DefaultReadinessPollIntervalSeconds);

    /// <summary><see cref="RequestTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan RequestTimeout => Seconds(RequestTimeoutSeconds, DefaultRequestTimeoutSeconds);

    /// <summary><see cref="ShutdownTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ShutdownTimeout => Seconds(ShutdownTimeoutSeconds, DefaultShutdownTimeoutSeconds);

    /// <summary><see cref="ShutdownRequestTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ShutdownRequestTimeout =>
        Seconds(ShutdownRequestTimeoutSeconds, DefaultShutdownRequestTimeoutSeconds);

    // Blank RepoRoot (for example an empty Harness__RepoRoot) falls back to the default, so a path can
    // never silently resolve against the current directory.
    private string Root => string.IsNullOrWhiteSpace(RepoRoot) ? DefaultRepoRoot : RepoRoot;

    /// <summary>
    /// Returns <paramref name="path"/> unchanged when it is absolute; otherwise resolves it against
    /// <see cref="RepoRoot"/>.
    /// </summary>
    /// <param name="path">An absolute path, or a path relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
    }

    private static HarnessEndpoints DefaultEndpoints(string host) => new()
    {
        RestBaseUrl = $"http://{host}:{RestPort}",
        SoapBaseUrl = $"http://{host}:{SoapPort}",
        AdminUrl = $"http://{host}:{AdminRoutes.DefaultPort}",
    };

    // A zero or negative limit falls back to its default, so every readiness, request and shutdown wait
    // stays bounded and positive.
    private static TimeSpan Seconds(int configured, int fallback) =>
        TimeSpan.FromSeconds(configured > 0 ? configured : fallback);
}

// ConfinedTree sits beside the options because the harness roots every instance at Harness:RepoRoot, as
// HarnessOptions.ResolvePath(".") resolves it.
/// <summary>
/// Reads, writes and deletes files and directories strictly below one trusted root directory, the repository
/// root, through Linux directory handles, so that no symbolic link below the root can redirect a read, a write
/// or a delete out of the tree, and no special file can block a read.
/// </summary>
/// <remarks>
/// <para>The root and the directories above it belong to the caller and are trusted, so the root is opened by
/// its path. Every component below it is opened relative to the handle of the one above it with
/// <c>O_DIRECTORY | O_NOFOLLOW</c>, so a link on the way is refused, never followed, and a directory renamed or
/// replaced by a link after it was opened cannot redirect the rest of the call, which acts on the directory
/// the handle holds.</para>
/// <para>A read opens its file inside the last handle without following a link, without waiting for a FIFO's
/// writer and without becoming a controlling terminal, then takes the file's type from the opened descriptor
/// itself, so what is read is exactly what was opened. Only a regular file is read, and at most a byte limit
/// the caller sets: a FIFO, a socket, a device or a file over the limit fails at once and is never read in
/// part. A missing directory on the way, a missing file or a directory at the name is nothing to read.</para>
/// <para>A target must lie strictly below the root once <see cref="Path.GetFullPath(string)"/> has resolved it;
/// the root itself and anything outside it are refused. Every refusal and operating-system error is an
/// <see cref="IOException"/>, or an <see cref="UnauthorizedAccessException"/> where the operating system denies
/// access, and its message names the path relative to the root, so it reads the same in every checkout and
/// container. Reads, writes and deletes run only on Linux.</para>
/// <para>An instance holds only the root's path and may be shared. Concurrent calls on the same entries race
/// as the file system lets them, but none of them leaves the tree.</para>
/// </remarks>
internal sealed class ConfinedTree
{
    // The first buffer of a read, grown by doubling up to the limit plus one byte, so a read never allocates
    // by a length the file reports.
    private const int InitialReadBytes = 64 * 1024;

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    // Every entry, dot names included; an entry that cannot be read fails the listing rather than being skipped.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string _root;
    private readonly string _rootPrefix;

    /// <summary>Anchors the tree at <paramref name="root"/>.</summary>
    /// <param name="root">
    /// The trusted root directory, absolute or relative to the current directory; it need not exist yet.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty, white space or not a usable path.</exception>
    public ConfinedTree(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
    }

    /// <summary>The root's full path, with no trailing separator unless it is the file-system root.</summary>
    public string Root => _root;

    /// <summary>
    /// Replaces whatever is at <paramref name="path"/> with a new, empty directory, creating the missing
    /// directories above it.
    /// </summary>
    /// <remarks>
    /// Every directory between the root and the target is opened before anything is deleted, so a link or a
    /// non-directory there refuses the call and leaves the tree as it is. At the target, a link or any other
    /// non-directory is removed itself, never followed. A directory is cleared through handles: each
    /// subdirectory is opened relative to its parent without following a link, cleared and removed, and every
    /// other entry, a link included, is unlinked as an entry, so an entry swapped for a link between the listing
    /// and its removal is removed as that link and never followed.
    /// </remarks>
    /// <param name="path">The directory to recreate, strictly below the root.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory above the target is a symbolic link or
    /// not a directory, or an entry cannot be opened, listed, removed or created.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    public void RecreateDirectory(string path)
    {
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        var name = target.Segments[^1];
        using var parent = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        RemoveEntry(parent, name, target.FullPath, flags);
        if (Native.MkDirAt(parent, name, Native.NewDirectoryMode) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == Native.EEXIST ? Appeared(target.FullPath) : Failure(errno, "create the directory", target.FullPath);
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> as a new file, creating the missing directories
    /// above it and replacing the file that was there.
    /// </summary>
    /// <remarks>
    /// A link at the target is refused. The entry the file replaces is removed with <c>unlinkat</c>, so a hard
    /// link to a file elsewhere is dropped, never written through, and a directory there fails the call. The
    /// new file is created exclusively without following a link, so a link planted at the name after the check
    /// is removed rather than followed, and anything that reappears there fails the create. A write that fails
    /// part-way leaves its file incomplete, as <see cref="File.WriteAllBytes(string, byte[])"/> does.
    /// </remarks>
    /// <param name="path">The file to write, strictly below the root.</param>
    /// <param name="bytes">The file's complete content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory on the way or the target is a symbolic
    /// link, a directory on the way is not a directory, the target is a directory, or the file cannot be
    /// created or written.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    public void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var handle = CreateReplacementFile(path);
        RandomAccess.Write(handle, bytes, fileOffset: 0);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> without blocking, as
    /// <see cref="WriteFile(string, ReadOnlySpan{byte})"/> does.
    /// </summary>
    /// <remarks>
    /// A token cancelled before the write starts leaves the tree untouched; a write cancelled while in progress
    /// can leave its file incomplete, as <see cref="File.WriteAllBytesAsync(string, byte[], CancellationToken)"/>
    /// can.
    /// </remarks>
    /// <param name="path">The file to write, strictly below the root.</param>
    /// <param name="bytes">The file's complete content.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">As for <see cref="WriteFile(string, ReadOnlySpan{byte})"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task WriteFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateReplacementFile(path);
        await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes <paramref name="bytes"/> at <paramref name="path"/> as one complete regular file, creating the
    /// missing directories above it and replacing the regular file that was there, without ever opening the
    /// entry at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// <para>The entry at the target is examined with <c>statx</c> without following a link and is never opened,
    /// so a FIFO, a device or a link there can neither block the call nor redirect it. Only a missing entry or a
    /// regular file is replaced; a directory, a link or any other kind of entry is refused and left in
    /// place.</para>
    /// <para>The bytes go to a new temporary file in the target's directory, a dot-prefixed unique name created
    /// exclusively without following a link, which <c>renameat</c> then moves over the target. A reader sees the
    /// previous file or the complete new one, never a part-written one. A hard link to a file elsewhere is
    /// replaced, never written through; an entry swapped in at the name after the check is replaced as the entry
    /// it is rather than opened or followed, and a directory swapped in fails the rename. Whatever fails after
    /// the temporary file was created, it is removed again.</para>
    /// </remarks>
    /// <param name="path">The file to publish, strictly below the root.</param>
    /// <param name="bytes">The file's complete content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory on the way is a symbolic link or not a
    /// directory, the target is a directory or another entry that is not a regular file, or the file cannot be
    /// examined, created, written or moved into place.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    public void PublishFile(string path, ReadOnlySpan<byte> bytes)
    {
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        var name = target.Segments[^1];
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        RequireRegularFileOrNothing(directory, name, target.FullPath);

        // A fixed-length name, so a target name of any allowed length leaves a valid temporary name.
        var temporaryName = string.Create(CultureInfo.InvariantCulture, $".{Guid.NewGuid():N}.publishing");
        var descriptor = Native.OpenAt(directory, temporaryName, flags.CreateFile, Native.NewFileMode);
        if (descriptor < 0)
        {
            throw Failure(Marshal.GetLastPInvokeError(), "create the temporary file for", target.FullPath);
        }

        var published = false;
        try
        {
            using (var handle = new SafeFileHandle(descriptor, ownsHandle: true))
            {
                RandomAccess.Write(handle, bytes, fileOffset: 0);
            }

            if (Native.RenameAt(directory, temporaryName, directory, name) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw errno == Native.EISDIR ? IsDirectory(target.FullPath) : Failure(errno, "publish", target.FullPath);
            }

            published = true;
        }
        finally
        {
            if (!published)
            {
                // The temporary file is this call's own entry in a directory it holds open, so the removal only
                // fails when the directory itself became unwritable; the failure already being raised is the
                // one reported either way.
                _ = Native.UnlinkAt(directory, temporaryName, flags: 0);
            }
        }
    }

    /// <summary>
    /// Publishes <paramref name="bytes"/> at <paramref name="path"/> as one complete regular file without blocking,
    /// replacing the regular file that was there as <see cref="PublishFile(string, ReadOnlySpan{byte})"/> does, but
    /// creating no directory except the file's own.
    /// </summary>
    /// <remarks>
    /// <para>The root and every directory between it and the file's own directory must already exist: each is
    /// opened without being created, so a missing one fails the call and nothing is built in its place. Only the
    /// file's own directory is made, through its parent's handle, when it is missing; a file directly below the
    /// root creates no directory at all. A link or a non-directory on the way is refused, never followed.</para>
    /// <para>The entry at the target is examined with <c>statx</c> without following a link and is never opened:
    /// only a missing entry or a regular file is replaced, and a directory, a link or any other kind of entry is
    /// refused and left in place. The bytes go to a new temporary file in the target's directory, a dot-prefixed
    /// unique name created exclusively without following a link, are flushed to disk, and <c>renameat</c> then
    /// moves the file over the target inside the same directory handle. A reader sees the previous file or the
    /// complete new one, never a part-written one. Whatever fails after the temporary file was created, it is
    /// removed again through that handle and the previous file stays as it was.</para>
    /// <para>A token cancelled before the call leaves the tree untouched; one cancelled before the rename leaves
    /// the previous file in place.</para>
    /// </remarks>
    /// <param name="path">The file to publish, strictly below the root.</param>
    /// <param name="bytes">The file's complete content.</param>
    /// <param name="cancellationToken">Cancels the call before the write, the write itself and the publication.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, the root or a directory above the file's own
    /// directory does not exist, a directory on the way is a symbolic link or not a directory, the target is a
    /// directory or another entry that is not a regular file, or the file cannot be examined, created, written,
    /// flushed or moved into place.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task PublishFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        var name = target.Segments[^1];
        using var directory = OpenOwnDirectory(target, flags);
        RequireRegularFileOrNothing(directory, name, target.FullPath);

        // A fixed-length name, so a target name of any allowed length leaves a valid temporary name.
        var temporaryName = string.Create(CultureInfo.InvariantCulture, $".{Guid.NewGuid():N}.publishing");
        var descriptor = Native.OpenAt(directory, temporaryName, flags.CreateFile, Native.NewFileMode);
        if (descriptor < 0)
        {
            throw Failure(Marshal.GetLastPInvokeError(), "create the temporary file for", target.FullPath);
        }

        var published = false;
        try
        {
            using (var handle = new SafeFileHandle(descriptor, ownsHandle: true))
            {
                await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);

                // On disk before the rename, so the name never holds a file whose content a crash could still lose.
                RandomAccess.FlushToDisk(handle);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Native.RenameAt(directory, temporaryName, directory, name) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw errno == Native.EISDIR ? IsDirectory(target.FullPath) : Failure(errno, "publish", target.FullPath);
            }

            published = true;
        }
        finally
        {
            if (!published)
            {
                // As in PublishFile: the call's own entry in a directory it holds open, so the removal only fails
                // when the directory itself became unwritable, and the failure already being raised stands.
                _ = Native.UnlinkAt(directory, temporaryName, flags: 0);
            }
        }
    }

    /// <summary>
    /// Publishes <paramref name="bytes"/> at <paramref name="path"/> as one complete new regular file, creating the
    /// missing directories above it, unless a regular file is already there, which is kept as it is.
    /// </summary>
    /// <remarks>
    /// <para>The entry at the target is examined with <c>statx</c> without following a link and is never opened,
    /// as <see cref="PublishFile(string, ReadOnlySpan{byte})"/> examines it: a missing entry is published, a
    /// regular file is kept untouched, and a directory, a link or any other kind of entry is refused and left in
    /// place.</para>
    /// <para>The bytes go to a new temporary file in the target's directory, a dot-prefixed unique name created
    /// exclusively without following a link, which <c>renameat2</c> with <c>RENAME_NOREPLACE</c> then moves to the
    /// name. Where the file system or the kernel lacks that flag, or the C library lacks <c>renameat2</c>,
    /// <c>linkat</c> links the temporary file at the name instead. Neither ever replaces an entry, so a reader
    /// sees no file or the complete one, and an entry that appeared at the name after the check is kept and
    /// examined again: a regular file is kept as present, anything else is refused. Whatever happens after the
    /// temporary file was created, its name is removed again unless the rename moved it; after a link the
    /// published name keeps the file.</para>
    /// <para>A token cancelled before the call leaves the tree untouched; one cancelled before the publication
    /// leaves no file at the name.</para>
    /// </remarks>
    /// <param name="path">The file to publish, strictly below the root.</param>
    /// <param name="bytes">The file's complete content.</param>
    /// <param name="cancellationToken">Cancels the call before the write, the write itself and the publication.</param>
    /// <returns>True when the file was published; false when a regular file was already at the name and is kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory on the way is a symbolic link or not a
    /// directory, the target is a directory or another entry that is not a regular file, or the file cannot be
    /// examined, created, written or published.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> PublishNewFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        var name = target.Segments[^1];
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        if (RequireRegularFileOrNothing(directory, name, target.FullPath))
        {
            return false;
        }

        // A fixed-length name, so a target name of any allowed length leaves a valid temporary name.
        var temporaryName = string.Create(CultureInfo.InvariantCulture, $".{Guid.NewGuid():N}.publishing");
        var descriptor = Native.OpenAt(directory, temporaryName, flags.CreateFile, Native.NewFileMode);
        if (descriptor < 0)
        {
            throw Failure(Marshal.GetLastPInvokeError(), "create the temporary file for", target.FullPath);
        }

        var renamed = false;
        try
        {
            using (var handle = new SafeFileHandle(descriptor, ownsHandle: true))
            {
                await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (RenameWithoutReplacing(directory, temporaryName, name) == 0)
            {
                renamed = true;
                return true;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno is Native.EINVAL or Native.ENOSYS)
            {
                // A hard link fails on an existing entry exactly as RENAME_NOREPLACE does; the temporary name is
                // removed below, and the published name keeps the file.
                if (Native.LinkAt(directory, temporaryName, directory, name, flags: 0) == 0)
                {
                    return true;
                }

                errno = Marshal.GetLastPInvokeError();
            }

            // Neither call replaces an entry of any kind, a directory included, so every entry at the name fails
            // as EEXIST.
            if (errno != Native.EEXIST)
            {
                throw Failure(errno, "publish", target.FullPath);
            }

            // An entry appeared at the name after the check and is kept: a regular file is one already present,
            // anything else is refused as the check refuses it. One that vanished again meanwhile is no entry
            // to keep, so the refused publication is reported as the error it met.
            if (RequireRegularFileOrNothing(directory, name, target.FullPath))
            {
                return false;
            }

            throw Failure(errno, "publish", target.FullPath);
        }
        finally
        {
            if (!renamed)
            {
                // As in PublishFile: the call's own entry in a directory it holds open, so the removal only fails
                // when the directory itself became unwritable, and the outcome already reached stands.
                _ = Native.UnlinkAt(directory, temporaryName, flags: 0);
            }
        }
    }

    // renameat2 with RENAME_NOREPLACE inside one directory, returning its result with errno set. A C library
    // without renameat2 sets errno to ENOSYS, as a kernel without the system call does, so the caller falls back
    // the same way for both.
    private static int RenameWithoutReplacing(DirectoryHandle directory, string oldName, string newName)
    {
        try
        {
            return Native.RenameAt2(directory, oldName, directory, newName, Native.RENAME_NOREPLACE);
        }
        catch (EntryPointNotFoundException)
        {
            Marshal.SetLastPInvokeError(Native.ENOSYS);
            return -1;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/>, once <see cref="Path.GetFullPath(string)"/> has resolved it, lies strictly
    /// below the root, which is what every read, write and delete requires. Nothing is opened or examined.
    /// </summary>
    /// <param name="path">The path to test, absolute or relative to the current directory.</param>
    /// <returns>True when the path names an entry below the root; false for the root itself and anything outside.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    public bool Contains(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return SegmentsBelowRoot(Path.GetFullPath(path)).Length > 0;
    }

    /// <summary>
    /// Reads the whole regular file at <paramref name="path"/>, at most <paramref name="byteLimit"/> bytes,
    /// creating nothing on the way.
    /// </summary>
    /// <remarks>
    /// <para>A missing directory on the way, the root included, a missing file and a directory at the name
    /// are nothing to read, as <see cref="File.Exists(string)"/> reports them, and give null. A link on the way
    /// or at the name is refused and left in place.</para>
    /// <para>The file is opened with <c>O_NOFOLLOW | O_NONBLOCK | O_NOCTTY</c>, so a FIFO without a writer opens
    /// at once, and its type is read from the opened descriptor with <c>statx</c>: a FIFO, a socket or a device
    /// is refused before any byte is read. A file whose reported length exceeds the limit is refused before
    /// any byte is read, and the bytes go into a buffer that starts small and grows, never one sized from that
    /// length, until the file ends or one byte past the limit is in, so a file that grows past the limit is
    /// refused too, without the rest of it being read. A file is never returned in part.</para>
    /// </remarks>
    /// <param name="path">The file to read, strictly below the root.</param>
    /// <param name="byteLimit">The most bytes the file may hold, at least 0 and below <see cref="Array.MaxLength"/>.</param>
    /// <returns>The file's bytes, or null when nothing is there to read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLimit"/> is out of range.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory on the way or the file is a symbolic
    /// link, a component on the way is not a directory, the file is not a regular file or exceeds
    /// <paramref name="byteLimit"/>, or it cannot be opened, examined or read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    public byte[]? ReadFile(string path, int byteLimit)
    {
        RequireByteLimit(byteLimit);
        var flags = RequireLinuxTraversal();
        var target = Resolve(path, read: true);
        using var file = OpenRegularFile(target, flags);
        return file is null ? null : ReadAtMost(file, target.FullPath, byteLimit);
    }

    /// <summary>
    /// Reads the whole regular file at <paramref name="path"/> without blocking, as
    /// <see cref="ReadFile(string, int)"/> does.
    /// </summary>
    /// <remarks>
    /// The token is observed before the file is opened and passed to every read, so a cancelled read returns no
    /// bytes. Every other outcome, exceptions and messages included, is that of the synchronous form.
    /// </remarks>
    /// <param name="path">The file to read, strictly below the root.</param>
    /// <param name="byteLimit">The most bytes the file may hold, at least 0 and below <see cref="Array.MaxLength"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file's bytes, or null when nothing is there to read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLimit"/> is out of range.</exception>
    /// <exception cref="IOException">As for <see cref="ReadFile(string, int)"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<byte[]?> ReadFileAsync(string path, int byteLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireByteLimit(byteLimit);
        var flags = RequireLinuxTraversal();
        var target = Resolve(path, read: true);
        using var file = OpenRegularFile(target, flags);
        return file is null
            ? null
            : await ReadAtMostAsync(file, target.FullPath, byteLimit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The exception .NET raises for a file it cannot find, for a caller that requires the file
    /// <see cref="ReadFile(string, int)"/> found nothing at: <see cref="DirectoryNotFoundException"/> when the
    /// directory that would hold the file does not exist, otherwise <see cref="FileNotFoundException"/>, each
    /// with the message .NET gives it.
    /// </summary>
    /// <param name="path">The file that was not found.</param>
    /// <returns>The exception to throw.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    public static IOException NotFound(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        return Path.GetDirectoryName(fullPath) is { } directory && !Directory.Exists(directory)
            ? new DirectoryNotFoundException($"Could not find a part of the path '{fullPath}'.")
            : new FileNotFoundException($"Could not find file '{fullPath}'.", fullPath);
    }

    /// <summary>
    /// Deletes the file or link at <paramref name="path"/>, creating nothing on the way.
    /// </summary>
    /// <remarks>
    /// A missing directory on the way or a missing target leaves nothing to delete. A link above the target is
    /// refused. The target is removed with <c>unlinkat</c>, so a link there is removed itself, never followed,
    /// and a directory there is left as it is.
    /// </remarks>
    /// <param name="path">The file to delete, strictly below the root.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or not a usable path.</exception>
    /// <exception cref="IOException">
    /// <paramref name="path"/> is not strictly below the root, a directory above the target is a symbolic link or
    /// not a directory, or the target cannot be removed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    public void DeleteFile(string path)
    {
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: false, flags);
        if (directory is null)
        {
            return;
        }

        if (Native.UnlinkAt(directory, target.Segments[^1], flags: 0) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is not (Native.ENOENT or Native.EISDIR))
            {
                throw Failure(errno, "delete", target.FullPath);
            }
        }
    }

    // Opens the target's directory through handles, creating what is missing, then works on the final name
    // inside that one directory only: a link there is refused, the entry the file replaces is removed, and the
    // new file is created exclusively without following a link. Returns the file open for writing.
    private SafeFileHandle CreateReplacementFile(string path)
    {
        var flags = RequireLinuxTraversal();
        var target = Resolve(path);
        var name = target.Segments[^1];
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        if (IsLink(directory, name, target.FullPath))
        {
            throw LinkRefusal(target.FullPath, target.FullPath);
        }

        if (Native.UnlinkAt(directory, name, flags: 0) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno != Native.ENOENT)
            {
                throw Failure(errno, "replace", target.FullPath);
            }
        }

        var descriptor = Native.OpenAt(directory, name, flags.CreateFile, Native.NewFileMode);
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == Native.EEXIST ? Appeared(target.FullPath) : Failure(errno, "create", target.FullPath);
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static void RequireByteLimit(int byteLimit)
    {
        // One byte past the limit is read to tell a longer file apart, so the limit plus one must fit an array.
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(byteLimit, Array.MaxLength);
    }

    // Opens the read target's directory through handles, creating nothing, then opens the final name inside it
    // without following a link and without blocking, and keeps it only when the opened descriptor is a regular
    // file. Returns null when a directory on the way or the name is missing, or a directory stands at the name.
    // The caller disposes the handle.
    private SafeFileHandle? OpenRegularFile(ConfinedPath target, TraversalFlags flags)
    {
        var name = target.Segments[^1];
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: false, flags);
        if (directory is null)
        {
            return null;
        }

        var descriptor = Native.OpenAt(directory, name, flags.OpenFile, mode: 0);
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Native.ENOENT)
            {
                return null;
            }

            // With O_NOFOLLOW a link at the name, dangling or not, fails as ELOOP, so only reading the entry as a
            // link tells it apart from another failure.
            if (errno == Native.ELOOP && IsLink(directory, name, target.FullPath))
            {
                throw LinkRefusal(target, target.FullPath);
            }

            throw Failure(errno, "read", target.FullPath);
        }

        var file = new SafeFileHandle(descriptor, ownsHandle: true);
        var kept = false;
        try
        {
            var type = OpenedFileType(file, target.FullPath);
            if (type == Native.S_IFDIR)
            {
                return null;
            }

            if (type != Native.S_IFREG)
            {
                throw new IOException(
                    $"ConfinedTree could not read '{Display(target.FullPath)}': it is not a regular file "
                    + $"({FileKind(type)}).");
            }

            kept = true;
            return file;
        }
        finally
        {
            if (!kept)
            {
                file.Dispose();
            }
        }
    }

    // The S_IFMT bits of the file an opened descriptor holds, read with statx and AT_EMPTY_PATH from the
    // descriptor itself, never from the path, so nothing swapped in at the name since the open counts.
    private int OpenedFileType(SafeFileHandle file, string path)
    {
        Span<byte> status = stackalloc byte[Native.StatxSize];
        if (Native.Statx(file, string.Empty, Native.AT_EMPTY_PATH, Native.STATX_TYPE, ref MemoryMarshal.GetReference(status)) != 0)
        {
            throw Failure(Marshal.GetLastPInvokeError(), "examine", path);
        }

        // The kernel writes the structure in the process's own byte order.
        if ((MemoryMarshal.Read<uint>(status) & Native.STATX_TYPE) == 0)
        {
            throw new IOException($"ConfinedTree could not examine '{Display(path)}': its file type was not reported.");
        }

        return MemoryMarshal.Read<ushort>(status[Native.StatxModeOffset..]) & Native.S_IFMT;
    }

    private static string FileKind(int type) => type switch
    {
        Native.S_IFIFO => "a FIFO",
        Native.S_IFSOCK => "a socket",
        Native.S_IFCHR => "a character device",
        Native.S_IFBLK => "a block device",
        _ => string.Create(CultureInfo.InvariantCulture, $"file type 0x{type:x4}"),
    };

    // The whole regular file, at most byteLimit bytes: a reported length over the limit is refused before any
    // read, and the bytes go into a buffer that grows by doubling up to the limit plus one byte, so a file that
    // grew past the limit is told apart without reading the rest of it.
    private byte[] ReadAtMost(SafeFileHandle file, string path, int byteLimit)
    {
        RequireWithinLimit(RandomAccess.GetLength(file), path, byteLimit);
        var content = new byte[Math.Min(InitialReadBytes, byteLimit + 1)];
        var length = 0;
        while (true)
        {
            if (length == content.Length)
            {
                content = GrownBuffer(content, path, byteLimit);
            }

            var read = RandomAccess.Read(file, content.AsSpan(length), fileOffset: length);
            if (read == 0)
            {
                return Trimmed(content, length);
            }

            length += read;
        }
    }

    // ReadAtMost without blocking, passing the token to every read.
    private async Task<byte[]> ReadAtMostAsync(SafeFileHandle file, string path, int byteLimit, CancellationToken cancellationToken)
    {
        RequireWithinLimit(RandomAccess.GetLength(file), path, byteLimit);
        var content = new byte[Math.Min(InitialReadBytes, byteLimit + 1)];
        var length = 0;
        while (true)
        {
            if (length == content.Length)
            {
                content = GrownBuffer(content, path, byteLimit);
            }

            var read = await RandomAccess.ReadAsync(file, content.AsMemory(length), fileOffset: length, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return Trimmed(content, length);
            }

            length += read;
        }
    }

    private void RequireWithinLimit(long length, string path, int byteLimit)
    {
        if (length > byteLimit)
        {
            throw ExceedsLimit(path, byteLimit);
        }
    }

    // A full buffer doubled, at most to the limit plus one byte; one already that long holds a byte past the
    // limit, so the file is refused.
    private byte[] GrownBuffer(byte[] content, string path, int byteLimit)
    {
        if (content.Length > byteLimit)
        {
            throw ExceedsLimit(path, byteLimit);
        }

        var grown = content;
        Array.Resize(ref grown, (int)Math.Min(2L * content.Length, byteLimit + 1L));
        return grown;
    }

    private static byte[] Trimmed(byte[] content, int length)
    {
        if (length == content.Length)
        {
            return content;
        }

        var exact = content;
        Array.Resize(ref exact, length);
        return exact;
    }

    private IOException ExceedsLimit(string path, int byteLimit) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"ConfinedTree could not read '{Display(path)}': it exceeds the {byteLimit}-byte limit."));

    // Removes the entry name inside directory, whatever it is, without following a link. A directory is
    // opened relative to its parent with O_NOFOLLOW, cleared through that handle and removed; anything else,
    // a link included, is unlinked as an entry. A missing entry is nothing to remove.
    private void RemoveEntry(DirectoryHandle directory, string name, string path, TraversalFlags flags)
    {
        var descriptor = Native.OpenAt(directory, name, flags.OpenDirectory, mode: 0);
        if (descriptor >= 0)
        {
            using (var child = DirectoryHandle.Own(descriptor))
            {
                ClearDirectory(child, path, flags);
            }

            // AT_REMOVEDIR removes an empty directory only and never follows a link, so whatever was swapped in
            // under the name since the clear fails the call instead of being removed through.
            Unlink(directory, name, Native.AT_REMOVEDIR, "remove the directory", path);
            return;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == Native.ENOENT)
        {
            return;
        }

        // With O_DIRECTORY and O_NOFOLLOW, a link, dangling or not, fails as ENOTDIR (or ELOOP) exactly as a file
        // does, and either is removed as the entry it is.
        if (errno is not (Native.ENOTDIR or Native.ELOOP))
        {
            throw Failure(errno, "open the directory", path);
        }

        Unlink(directory, name, flags: 0, "remove", path);
    }

    // Removes every entry inside directory. The listing goes through /proc/self/fd, which the kernel resolves
    // to the directory the handle holds, not to whatever sits at its path now, and it is read in full before
    // the first removal.
    private void ClearDirectory(DirectoryHandle directory, string path, TraversalFlags flags)
    {
        var listing = new DirectoryInfo(
            string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{directory.DangerousGetHandle()}"));
        string[] names;
        try
        {
            names = [.. listing.EnumerateFileSystemInfos("*", ListingOptions).Select(static entry => entry.Name)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The listing path names a descriptor, so the message names the directory instead.
            var message = $"ConfinedTree could not list '{Display(path)}'.";
            throw ex is UnauthorizedAccessException
                ? new UnauthorizedAccessException(message, ex)
                : new IOException(message, ex);
        }

        foreach (var name in names)
        {
            RemoveEntry(directory, name, Path.Join(path, name), flags);
        }
    }

    // unlinkat with flags; an entry already gone is nothing to remove.
    private void Unlink(DirectoryHandle directory, string name, int flags, string action, string path)
    {
        if (Native.UnlinkAt(directory, name, flags) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno != Native.ENOENT)
            {
                throw Failure(errno, action, path);
            }
        }
    }

    // A write or delete target, resolved as Resolve(string, bool) resolves it.
    private ConfinedPath Resolve(string path) => Resolve(path, read: false);

    // Refuses a path that leaves the tree lexically or names the root itself, and splits the rest into the
    // components below the root that the directory handles walk. read marks a read target, whose refusals
    // name a read.
    private ConfinedPath Resolve(string path, bool read)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var segments = SegmentsBelowRoot(fullPath);
        if (segments.Length == 0)
        {
            throw new IOException(read
                ? $"ConfinedTree reads only below its root: refused '{Display(fullPath)}'."
                : $"ConfinedTree writes and deletes only below its root: refused '{Display(fullPath)}'.");
        }

        return new ConfinedPath(fullPath, segments, read);
    }

    // The components of a full path below the root; none for the root itself or a path outside it.
    private string[] SegmentsBelowRoot(string fullPath) =>
        fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? fullPath[_rootPrefix.Length..].Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
            : [];

    // Examines a publication's target entry, refusing a directory or any other entry that is not a regular file,
    // a link included: PublishFile and PublishFileAsync replace only a regular file, and PublishNewFileAsync keeps
    // only one. statx with AT_SYMLINK_NOFOLLOW reads the entry's type without following a link or opening the
    // entry, so a FIFO or a device there cannot block the call. Returns true for a regular file and false for a
    // missing entry.
    private bool RequireRegularFileOrNothing(DirectoryHandle directory, string name, string path)
    {
        Span<byte> status = stackalloc byte[Native.StatxSize];
        if (Native.Statx(directory, name, Native.AT_SYMLINK_NOFOLLOW, Native.STATX_TYPE, ref MemoryMarshal.GetReference(status)) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Native.ENOENT)
            {
                return false;
            }

            throw Failure(errno, "examine", path);
        }

        // The kernel writes the structure in the process's own byte order.
        if ((MemoryMarshal.Read<uint>(status) & Native.STATX_TYPE) == 0)
        {
            throw new IOException($"ConfinedTree could not examine '{Display(path)}': its file type was not reported.");
        }

        var type = MemoryMarshal.Read<ushort>(status[Native.StatxModeOffset..]) & Native.S_IFMT;
        if (type == Native.S_IFREG)
        {
            return true;
        }

        throw type == Native.S_IFDIR ? IsDirectory(path) : NotRegularFile(path);
    }

    // Opens the directory that the first depth segments of target name, starting at the root. Each component
    // below the root is opened relative to the handle of the one above it, never through a link, and with
    // create it is made first when it is missing. Without create a missing directory leaves nothing to act on,
    // and null is returned. The caller disposes the handle.
    private DirectoryHandle? OpenDirectory(ConfinedPath target, int depth, bool create, TraversalFlags flags)
    {
        var directory = OpenRoot(create, flags);
        var component = _root;
        for (var index = 0; directory is not null && index < depth; index++)
        {
            component = Path.Join(component, target.Segments[index]);
            using var parent = directory;
            directory = OpenComponent(parent, target.Segments[index], component, target, create, flags);
        }

        return directory;
    }

    // Opens the directory that holds target for PublishFileAsync, as OpenDirectory opens it, but creating only that
    // directory: the root and every directory between it and the target's own are opened without being created,
    // so a missing one fails the call, and the target's own directory, when it lies below the root, is made
    // through its parent's handle when it is missing. The caller disposes the handle.
    private DirectoryHandle OpenOwnDirectory(ConfinedPath target, TraversalFlags flags)
    {
        var depth = target.Segments.Length - 1;
        var directory = OpenRoot(create: false, flags) ?? throw Missing(target.FullPath, _root);
        var component = _root;
        for (var index = 0; index < depth; index++)
        {
            component = Path.Join(component, target.Segments[index]);
            using var parent = directory;
            directory = OpenComponent(parent, target.Segments[index], component, target, create: index == depth - 1, flags)
                ?? throw Missing(target.FullPath, component);
        }

        return directory;
    }

    // The root is the caller's and trusted, so it is opened by its path, following any link in it, and with
    // create it is made the ordinary way first when it is missing; null when it is missing without create.
    private DirectoryHandle? OpenRoot(bool create, TraversalFlags flags)
    {
        if (create)
        {
            Directory.CreateDirectory(_root);
        }

        int descriptor;
        using (var current = DirectoryHandle.CurrentDirectory())
        {
            descriptor = Native.OpenAt(current, _root, flags.OpenRoot, mode: 0);
        }

        if (descriptor >= 0)
        {
            return DirectoryHandle.Own(descriptor);
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == Native.ENOENT && !create)
        {
            return null;
        }

        throw errno == Native.ENOTDIR ? NotDirectory(_root) : Failure(errno, "open the directory", _root);
    }

    // The directory name inside parent, opened without following a link at name and, with create, made first
    // when it is missing; null when it is missing without create. component is the path name stands for, and
    // target the read, write or delete a refusal names.
    private DirectoryHandle? OpenComponent(
        DirectoryHandle parent, string name, string component, ConfinedPath target, bool create, TraversalFlags flags)
    {
        var descriptor = Native.OpenAt(parent, name, flags.OpenDirectory, mode: 0);
        var errno = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        if (errno == Native.ENOENT)
        {
            if (!create)
            {
                return null;
            }

            if (Native.MkDirAt(parent, name, Native.NewDirectoryMode) != 0)
            {
                errno = Marshal.GetLastPInvokeError();
                if (errno != Native.EEXIST)
                {
                    throw Failure(errno, "create the directory", component);
                }
            }

            descriptor = Native.OpenAt(parent, name, flags.OpenDirectory, mode: 0);
            errno = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        }

        if (descriptor >= 0)
        {
            return DirectoryHandle.Own(descriptor);
        }

        // With O_DIRECTORY and O_NOFOLLOW, a link, dangling or not, fails as ENOTDIR (or ELOOP) exactly as a file
        // does, so only reading the entry as a link tells the two apart.
        if (errno is Native.ENOTDIR or Native.ELOOP)
        {
            throw IsLink(parent, name, component) ? LinkRefusal(target, component) : NotDirectory(component);
        }

        throw Failure(errno, "open the directory", component);
    }

    // Whether the entry name inside directory is a symbolic link, dangling or not; readlinkat never follows it.
    // A missing entry is no link.
    private bool IsLink(DirectoryHandle directory, string name, string path)
    {
        byte firstTargetByte = 0;
        if (Native.ReadLinkAt(directory, name, ref firstTargetByte, size: 1) >= 0)
        {
            return true;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno is Native.EINVAL or Native.ENOENT)
        {
            return false;
        }

        throw Failure(errno, "examine", path);
    }

    private IOException LinkRefusal(string refusedPath, string component) =>
        new($"ConfinedTree never writes or deletes through a link: refused '{Display(refusedPath)}', because "
            + $"'{Display(component)}' is a symbolic link.");

    // The refusal of a link at component on the way to target, worded for a read or for a write or delete.
    private IOException LinkRefusal(ConfinedPath target, string component) =>
        target.Read
            ? new($"ConfinedTree never reads through a link: refused '{Display(target.FullPath)}', because "
                + $"'{Display(component)}' is a symbolic link.")
            : LinkRefusal(target.FullPath, component);

    private IOException NotDirectory(string component) =>
        new($"ConfinedTree could not open '{Display(component)}': it is not a directory.");

    private IOException Appeared(string path) =>
        new($"ConfinedTree could not create '{Display(path)}': an entry appeared there after the one it replaces "
            + "was removed.");

    private IOException IsDirectory(string path) =>
        new($"ConfinedTree could not publish '{Display(path)}': it is a directory.");

    private IOException NotRegularFile(string path) =>
        new($"ConfinedTree could not publish '{Display(path)}': it is not a regular file, so it is left in place.");

    private IOException Missing(string path, string directory) =>
        new($"ConfinedTree could not publish '{Display(path)}': the directory '{Display(directory)}' does not exist, "
            + "and only the file's own directory is ever created.");

    // An operating-system error as the exception .NET raises for it, naming the path relative to the root.
    private Exception Failure(int errno, string action, string path)
    {
        var message = $"ConfinedTree could not {action} '{Display(path)}': {Marshal.GetPInvokeErrorMessage(errno)}.";
        return errno is Native.EACCES or Native.EPERM
            ? new UnauthorizedAccessException(message)
            : new IOException(message);
    }

    // Messages name paths relative to the root, so an error quoted in a report reads the same in every
    // checkout and container; the root itself reads as '.'.
    private string Display(string path) =>
        Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    // The open(2) flags of every read, write and delete for this process, refusing a platform without Linux
    // directory handles.
    private static TraversalFlags RequireLinuxTraversal()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "ConfinedTree reads, writes and deletes only on Linux: it confines them below its root through Linux "
                + "directory handles, which this operating system does not provide.");
        }

        // O_DIRECTORY and O_NOFOLLOW are the only flags used here whose values differ between architectures.
        var (directory, noFollow) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 or Architecture.S390x or Architecture.RiscV64
                or Architecture.LoongArch64 => (0x10000, 0x20000),
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => (0x4000, 0x8000),
            var architecture => throw new PlatformNotSupportedException(
                $"ConfinedTree reads, writes and deletes only where it knows the Linux open flags, which it does not "
                + $"for {architecture}."),
        };
        return new TraversalFlags(
            Native.O_RDONLY | directory | Native.O_CLOEXEC,
            Native.O_RDONLY | directory | noFollow | Native.O_CLOEXEC,
            Native.O_WRONLY | Native.O_CREAT | Native.O_EXCL | noFollow | Native.O_CLOEXEC,
            Native.O_RDONLY | noFollow | Native.O_NONBLOCK | Native.O_NOCTTY | Native.O_CLOEXEC);
    }

    // A read, write or delete target: its full path and the components below the root that lead to it, the
    // target's own name last. Read marks a read target, whose refusals name a read.
    private readonly record struct ConfinedPath(string FullPath, string[] Segments, bool Read);

    // The open(2) flags of one read, write or delete: the trusted root opened read-only by its path, a directory
    // below it opened read-only without following a link, a file created write-only and exclusively without
    // following a link, and a file opened read-only without following a link, without waiting on a FIFO's writer
    // and without becoming a controlling terminal, all closed on exec.
    private readonly record struct TraversalFlags(int OpenRoot, int OpenDirectory, int CreateFile, int OpenFile);

    // A directory held open by its file descriptor, so a call relative to it acts on that directory whatever is
    // later renamed, removed or linked at the path it was opened by.
    private sealed class DirectoryHandle : SafeHandleMinusOneIsInvalid
    {
        private DirectoryHandle(int descriptor, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(descriptor);
        }

        // AT_FDCWD, which resolves an absolute path as given; it owns no descriptor and closes nothing.
        public static DirectoryHandle CurrentDirectory() => new(Native.AT_FDCWD, ownsHandle: false);

        public static DirectoryHandle Own(int descriptor) => new(descriptor, ownsHandle: true);

        protected override bool ReleaseHandle() => Native.Close((int)handle) == 0;
    }

    // The Linux C library calls behind the directory handles, with the flag, mode and errno values they use.
    // Paths are passed as UTF-8, and a directory handle stays open for the whole of each call it is passed to.
    // A handle is passed pointer-sized; its value is the descriptor sign-extended, the form in which every
    // Linux calling convention passes the C int the calls declare. The calls use DllImport, not the
    // LibraryImport that SYSLIB1054 suggests, because its generated marshalling needs unsafe code, which this
    // project does not enable; and strings stay UTF-8 despite CA2101, which asks for the UTF-16 of Windows
    // APIs, because a Linux path is the UTF-8 bytes the kernel reads.
    private static class Native
    {
        public const int AT_FDCWD = -100;
        public const int AT_SYMLINK_NOFOLLOW = 0x100;
        public const int AT_REMOVEDIR = 0x200;
        public const int AT_EMPTY_PATH = 0x1000;

        // struct statx has one layout on every architecture: 256 bytes, with the u32 stx_mask at byte 0 and the
        // u16 stx_mode at byte 28, whose S_IFMT bits give the entry's type.
        public const uint STATX_TYPE = 0x1;
        public const int StatxSize = 256;
        public const int StatxModeOffset = 28;
        public const int S_IFMT = 0xF000;
        public const int S_IFIFO = 0x1000;
        public const int S_IFCHR = 0x2000;
        public const int S_IFDIR = 0x4000;
        public const int S_IFBLK = 0x6000;
        public const int S_IFREG = 0x8000;
        public const int S_IFSOCK = 0xC000;

        public const int O_RDONLY = 0;
        public const int O_WRONLY = 1;
        public const int O_CREAT = 0x40;
        public const int O_EXCL = 0x80;
        public const int O_NOCTTY = 0x100;
        public const int O_NONBLOCK = 0x800;
        public const int O_CLOEXEC = 0x80000;

        // rw-rw-rw- and rwxrwxrwx before the umask, the modes .NET itself creates files and directories with.
        public const uint NewFileMode = 0b110_110_110;
        public const uint NewDirectoryMode = 0b111_111_111;

        public const int EPERM = 1;
        public const int ENOENT = 2;
        public const int EACCES = 13;
        public const int EEXIST = 17;
        public const int ENOTDIR = 20;
        public const int EISDIR = 21;
        public const int EINVAL = 22;
        public const int ENOSYS = 38;
        public const int ELOOP = 40;

        // renameat2's flag that fails with EEXIST instead of replacing an entry at the new name.
        public const uint RENAME_NOREPLACE = 0x1;

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        public static extern int OpenAt(
            DirectoryHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        public static extern int MkDirAt(
            DirectoryHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
        public static extern nint ReadLinkAt(
            DirectoryHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref byte buffer, nuint size);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        public static extern int UnlinkAt(
            DirectoryHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        // Takes a directory handle with a name below it, or an opened file with an empty name and AT_EMPTY_PATH.
        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        public static extern int Statx(
            SafeHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, ref byte buffer);

        [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
        public static extern int RenameAt(
            DirectoryHandle oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            DirectoryHandle newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        public static extern int RenameAt2(
            DirectoryHandle oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            DirectoryHandle newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
            uint flags);

        [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
        public static extern int LinkAt(
            DirectoryHandle oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            DirectoryHandle newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
            int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
    }
}
