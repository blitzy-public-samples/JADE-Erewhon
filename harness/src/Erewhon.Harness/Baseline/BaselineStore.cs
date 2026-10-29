using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Erewhon.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Erewhon.Harness.Baseline;

/// <summary>
/// Reads and writes the behaviour baseline under <c>baseline/</c>: the case records with the raw
/// <c>getPhoto</c> bodies beside them, the manifest, the normalisation rules, the conformance result and the
/// WSDL the stand-in served. It is the only harness type that writes <c>baseline/**</c>; only
/// <c>capture</c> writes, and <c>replay</c> only reads.
/// </summary>
/// <remarks>
/// <para>Every path comes from <see cref="HarnessOptions"/>. A write or delete whose path resolves outside
/// <c>baseline/</c> is refused, so this store never writes <c>contracts/</c> or anything else.</para>
/// <para>Containment is physical as well as lexical: this store never writes or deletes through a link, even
/// while another process renames or replaces directories under it. Components above <c>baseline/</c> belong
/// to the repository root the caller chose and are trusted. From <c>baseline/</c> itself down to the target's
/// directory, each component is opened as a directory handle relative to the handle of the one above it,
/// without following a link, and is created first when a write needs it and it is missing. The write or
/// delete then names only the final entry inside the last handle, so no directory on the way is looked up
/// again once checked: one swapped for a link after it was opened cannot redirect the operation. A symbolic
/// link or other reparse point on that chain or at the target, dangling or not, refuses the operation and is
/// left in place. <see cref="ClearRecords(CancellationToken)"/> also lists each records directory through its
/// handle. A file is replaced, never rewritten in place: the old entry is removed and the new file created
/// exclusively without following a link, so neither a link planted at the final name after the check nor a
/// hard link to a file elsewhere is written through.</para>
/// <para>Directory handles of this kind are a Linux facility, so every write and delete throws
/// <see cref="PlatformNotSupportedException"/> on any other operating system, and on a processor architecture
/// whose Linux open flags this store does not know. Reads use ordinary paths and run on every platform.</para>
/// <para>Files are written with one fixed JSON format and no timestamp, so a re-capture of an unchanged
/// stand-in reproduces every file byte for byte (the manifest's <c>captureCommit</c> aside). Every file but
/// the manifest is written straight to its final path. The manifest is the capture's completion record, so
/// capture publishes it last, and atomically: its bytes go to the fixed temporary file
/// <c>baseline/.manifest.json.tmp</c>, created as every other file is, which is then renamed onto
/// <c>manifest.json</c> inside the same directory handle. The manifest is therefore either the previous
/// complete file or the new complete one, never a part of either. A publication that fails or is cancelled
/// removes its temporary file; one a crash interrupts leaves at most that one file, which the next
/// publication replaces and <see cref="DeleteManifest"/> removes. No other temporary file is ever left in
/// the tree.</para>
/// <para>Reads verify what they return: every record must sit at the path its own members name, and its
/// body must match its <c>bodySha256</c>, so replay never compares against a damaged baseline. Each body is
/// read once: <see cref="ReadEntries"/> returns the verified bytes with their record, and
/// <see cref="ReadBodyBytes"/> verifies the bytes it returns. <see cref="ReadRecordIndexAsync"/> and
/// <see cref="ReadEntryAsync(BaselineRecordKey, CancellationToken)"/> split that work for a caller that must
/// hold one case at a time: the index checks every record and keeps only its <see cref="BaselineRecordKey"/>,
/// which holds no request or response, and the per-case reader later reads one key's record and body once and
/// verifies both. A <c>bodyText</c> is unescaped into memory its record alone references, never into a pooled
/// buffer, so dropping a record or entry releases its body.</para>
/// <para>Every file read and write also has an asynchronous form that takes a
/// <see cref="CancellationToken"/>, passes it to the file I/O and observes it before each record file.
/// Deletes stay synchronous, because file deletion has no asynchronous form;
/// <see cref="ClearRecords(CancellationToken)"/> observes a token before each delete. Cancellation surfaces as
/// <see cref="OperationCanceledException"/>; every other outcome, exceptions and messages included, is that of
/// the synchronous form, and both forms write the same bytes.</para>
/// </remarks>
public sealed class BaselineStore
{
    private const string RecordExtension = ".json";
    private const string BodyFileExtension = ".body.bin";
    private const string RestDirectoryName = "rest";
    private const string SoapDirectoryName = "soap";
    private const string RestCasePrefix = "REST-";
    private const string SoapCasePrefix = "SOAP-";
    private const int MinPosition = 1;
    private const int MaxPosition = 999;

    // The JSON member of a request and of a response that holds its body as text.
    private const string BodyTextMember = "bodyText";

    // The one temporary file the manifest is published through, beside it in baseline/. Its name is fixed,
    // so an interrupted publication leaves at most this file, which the next one replaces.
    private const string ManifestTemporaryFileName = ".manifest.json.tmp";

    // FileSystemInfo.Attributes reports a path that does not exist as -1.
    private const FileAttributes MissingEntryAttributes = (FileAttributes)(-1);

    private static readonly string[] SurfaceDirectoryNames = [RestDirectoryName, SoapDirectoryName];

    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    // Same comment and trailing-comma tolerance as SerializerOptions. Node options stay at their
    // case-sensitive default, so a parsed JsonObject behaves as its producer wrote it.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    // The options every read uses: SerializerOptions, except that each bodyText is read by
    // UnpooledTextConverter. The built-in converter unescapes a string into an array rented from
    // ArrayPool<byte>.Shared, and the pool keeps a body-sized array on every thread that parsed a large body,
    // so the memory of a body would outlive the record that held it.
    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    private readonly HarnessOptions options;

    /// <summary>Creates a store over the baseline paths of <paramref name="options"/>.</summary>
    /// <param name="options">The harness settings that own every baseline path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public BaselineStore(HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>
    /// The one JSON format of every baseline JSON file: camel-case property names, dictionary keys (header
    /// names) as sent, null members written, two-space indentation and LF line ends. Reading is
    /// case-insensitive, skips comments and unknown members and rejects trailing commas, so a hand-written
    /// <c>not-run</c> manifest still parses. The instance is read-only.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>Whether <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> exists.</summary>
    public bool WsdlExists => File.Exists(options.BaselineWsdlPath);

    /// <summary>
    /// Serialises <paramref name="value"/> in the baseline format as UTF-8 without a byte-order mark,
    /// followed by a single LF.
    /// </summary>
    /// <typeparam name="T">
    /// The document type, for example <see cref="BaselineRecord"/> or <see cref="BaselineManifest"/>.
    /// </typeparam>
    /// <param name="value">The document.</param>
    /// <returns>The exact bytes of the file.</returns>
    public static byte[] SerializeDocument<T>(T value) =>
        WithTrailingNewLine(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));

    /// <summary>
    /// Serialises a JSON document produced elsewhere, such as the normalisation rules or the conformance
    /// result, in the baseline format as UTF-8 without a byte-order mark, followed by a single LF.
    /// </summary>
    /// <param name="node">The document; null is written as <c>null</c>.</param>
    /// <returns>The exact bytes of the file.</returns>
    public static byte[] SerializeDocument(JsonNode? node) =>
        WithTrailingNewLine(JsonSerializer.SerializeToUtf8Bytes(node, SerializerOptions));

    /// <summary>
    /// The record file name <c>NNN-&lt;jadeMethod&gt;-&lt;caseNumber&gt;.json</c>, where <c>NNN</c> is the
    /// three-digit replay position.
    /// </summary>
    /// <param name="position">The 1-based replay position, 1 to 999.</param>
    /// <param name="jadeMethod">The JADE method name, ASCII letters and digits only.</param>
    /// <param name="caseNumber">The case number, ASCII digits only, for example <c>01</c>.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentException">An argument is out of range, empty or holds another character.</exception>
    public static string RecordFileName(int position, string jadeMethod, string caseNumber) =>
        FileStem(position, jadeMethod, caseNumber) + RecordExtension;

    /// <summary>
    /// The raw-body file name <c>NNN-&lt;jadeMethod&gt;-&lt;caseNumber&gt;.body.bin</c> that sits beside the
    /// record of the same stem.
    /// </summary>
    /// <param name="position">The 1-based replay position, 1 to 999.</param>
    /// <param name="jadeMethod">The JADE method name, ASCII letters and digits only.</param>
    /// <param name="caseNumber">The case number, ASCII digits only, for example <c>02</c>.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentException">An argument is out of range, empty or holds another character.</exception>
    public static string BodyFileName(int position, string jadeMethod, string caseNumber) =>
        FileStem(position, jadeMethod, caseNumber) + BodyFileExtension;

    /// <summary>The record file name of <paramref name="record"/>, from its order, method and case number.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentException">
    /// The record's order is out of range or its names hold other characters.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The record's case id and operation disagree on the surface or the JADE method, or either is malformed.
    /// </exception>
    public static string RecordFileName(BaselineRecord record) =>
        RecordFileName(record.Order, ConsistentJadeMethod(record), record.CaseNumber);

    /// <summary>The raw-body file name of <paramref name="record"/>, from its order, method and case number.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The file name, without a directory.</returns>
    /// <exception cref="ArgumentException">
    /// The record's order is out of range or its names hold other characters.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The record's case id and operation disagree on the surface or the JADE method, or either is malformed.
    /// </exception>
    public static string BodyFileName(BaselineRecord record) =>
        BodyFileName(record.Order, ConsistentJadeMethod(record), record.CaseNumber);

    /// <summary>
    /// The surface directory of a case: <c>rest</c> for a <c>REST-</c> case id and <c>soap</c> for a
    /// <c>SOAP-</c> one (ordinal).
    /// </summary>
    /// <param name="caseId">The case id, for example <c>SOAP-getPhoto-02</c>.</param>
    /// <returns><c>rest</c> or <c>soap</c>.</returns>
    /// <exception cref="ArgumentException">The case id is null or has neither prefix.</exception>
    public static string SurfaceDirectoryName(string caseId)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        if (caseId.StartsWith(RestCasePrefix, StringComparison.Ordinal))
        {
            return RestDirectoryName;
        }

        if (caseId.StartsWith(SoapCasePrefix, StringComparison.Ordinal))
        {
            return SoapDirectoryName;
        }

        throw new ArgumentException(
            $"Case id '{caseId}' starts with neither '{RestCasePrefix}' nor '{SoapCasePrefix}'.", nameof(caseId));
    }

    /// <summary>
    /// The path of the record file of <paramref name="record"/>:
    /// <c>baseline/records/&lt;rest|soap&gt;/NNN-&lt;jadeMethod&gt;-&lt;NN&gt;.json</c>.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">The record's case id, order or names are invalid.</exception>
    /// <exception cref="InvalidDataException">The record's case id and operation disagree.</exception>
    public string GetRecordPath(BaselineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Path.Combine(options.RecordsDirectory, SurfaceDirectoryName(record.CaseId), RecordFileName(record));
    }

    /// <summary>
    /// The path of the raw-body file of <paramref name="record"/>, the sibling <c>.body.bin</c> of its record
    /// file.
    /// </summary>
    /// <param name="record">A record whose response names a body file.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">
    /// The record's response names no body file, or its names are invalid.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The named body file is not the one the record's order, method and case number give, or the record's
    /// case id and operation disagree.
    /// </exception>
    public string GetBodyFilePath(BaselineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bodyFile = record.Response?.BodyFile
            ?? throw new ArgumentException($"Baseline case {record.CaseId} names no body file.", nameof(record));
        var expected = BodyFileName(record);
        if (!string.Equals(bodyFile, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Baseline case {record.CaseId} names body file '{bodyFile}', but its order, method and case "
                + $"number give '{expected}'.");
        }

        return Path.Combine(options.RecordsDirectory, SurfaceDirectoryName(record.CaseId), expected);
    }

    /// <summary>
    /// Deletes every file directly inside <c>baseline/records/rest</c> and <c>baseline/records/soap</c>,
    /// stale <c>.body.bin</c> files included, creating either directory when it is missing. Directories and
    /// every other file under <c>baseline/</c> are kept.
    /// </summary>
    /// <remarks>
    /// Both directories and every entry to delete are checked before the first delete, so a refusal leaves
    /// the records untouched; each entry is checked again immediately before it is deleted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A records directory resolves outside <c>baseline/</c>, or a records directory, an entry in it or a
    /// directory above them at or below <c>baseline/</c> is a symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">A directory cannot be created or a file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void ClearRecords() => ClearRecords(CancellationToken.None);

    /// <summary>
    /// Deletes every file directly inside <c>baseline/records/rest</c> and <c>baseline/records/soap</c>, as
    /// <see cref="ClearRecords()"/> does, stopping when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// The clear stays synchronous because file deletion has no asynchronous form. The token is observed before
    /// each directory and before each delete, so a cancelled clear stops between two files; the checks of
    /// <see cref="ClearRecords()"/> all apply.
    /// </remarks>
    /// <param name="cancellationToken">Stops the clear before its next directory or delete.</param>
    /// <exception cref="InvalidOperationException">
    /// A records directory resolves outside <c>baseline/</c>, or a records directory, an entry in it or a
    /// directory above them at or below <c>baseline/</c> is a symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">A directory cannot be created or a file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void ClearRecords(CancellationToken cancellationToken)
    {
        // Both directories stay open from their listing to their last delete, so every delete lands in the
        // directory that was listed and checked, whatever is renamed or linked in its place meanwhile.
        var flags = RequireLinuxTraversal();
        var surfaces = new List<(DirectoryHandle Directory, string Path, List<string> Files)>();
        try
        {
            foreach (var surface in SurfaceDirectoryNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveWritePath(Path.Combine(options.RecordsDirectory, surface));
                var directory = OpenDirectory(path, path.Segments.Length, create: true, flags)!;
                var files = new List<string>();
                surfaces.Add((directory, path.FullPath, files));
                ListFiles(directory, path.FullPath, files);
            }

            foreach (var (directory, path, files) in surfaces)
            {
                foreach (var name in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeleteFile(directory, name, Path.Join(path, name));
                }
            }
        }
        finally
        {
            foreach (var (directory, _, _) in surfaces)
            {
                directory.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes one captured case: its raw body to the sibling <c>.body.bin</c> when the record names a body
    /// file, then the record itself.
    /// </summary>
    /// <param name="record">The record, labelled <see cref="SourceLabels.StandIn"/>.</param>
    /// <param name="rawBody">The raw response body the record describes.</param>
    /// <exception cref="InvalidOperationException">
    /// The record is not labelled <see cref="SourceLabels.StandIn"/>, or a path resolves outside <c>baseline/</c>
    /// or reaches its file through a symbolic link or reparse point at or below <c>baseline/</c>.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <c>bodySha256</c> does not cover <paramref name="rawBody"/>; a named body file is not the record's own
    /// or comes with body text; or, with no body file, the body text does not reproduce
    /// <paramref name="rawBody"/> byte for byte.
    /// </exception>
    /// <exception cref="ArgumentException">The record's members are missing, out of range or malformed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void WriteRecord(BaselineRecord record, ReadOnlySpan<byte> rawBody)
    {
        var (recordPath, bodyPath) = CheckedRecordPaths(record, rawBody);
        if (bodyPath is not null)
        {
            WriteFile(bodyPath, rawBody);
        }

        WriteFile(recordPath, SerializeDocument(record));
    }

    /// <summary>
    /// Writes one captured case without blocking, as <see cref="WriteRecord"/> does: its raw body to the sibling
    /// <c>.body.bin</c> when the record names a body file, then the record itself.
    /// </summary>
    /// <param name="record">The record, labelled <see cref="SourceLabels.StandIn"/>.</param>
    /// <param name="rawBody">The raw response body the record describes.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>A task that completes when both files are written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The record is not labelled <see cref="SourceLabels.StandIn"/>, or a path resolves outside <c>baseline/</c>
    /// or reaches its file through a symbolic link or reparse point at or below <c>baseline/</c>.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <c>bodySha256</c> does not cover <paramref name="rawBody"/>; a named body file is not the record's own
    /// or comes with body text; or, with no body file, the body text does not reproduce
    /// <paramref name="rawBody"/> byte for byte.
    /// </exception>
    /// <exception cref="ArgumentException">The record's members are missing, out of range or malformed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task WriteRecordAsync(
        BaselineRecord record, ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
    {
        var (recordPath, bodyPath) = CheckedRecordPaths(record, rawBody.Span);
        if (bodyPath is not null)
        {
            await WriteFileAsync(bodyPath, rawBody, cancellationToken).ConfigureAwait(false);
        }

        await WriteFileAsync(recordPath, SerializeDocument(record), cancellationToken).ConfigureAwait(false);
    }

    // Checks a record about to be written against its raw body, and returns the record's path and the path of
    // its body file, null when its bodyText holds the body.
    private (string RecordPath, string? BodyPath) CheckedRecordPaths(BaselineRecord record, ReadOnlySpan<byte> rawBody)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.Source, SourceLabels.StandIn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Baseline case {record.CaseId} has source '{record.Source}'; every record must be labelled "
                + $"'{SourceLabels.StandIn}'.");
        }

        if (record.Request is null || record.Response is null)
        {
            throw new ArgumentException(
                $"Baseline case {record.CaseId} has no request or no response.", nameof(record));
        }

        var response = record.Response;
        var recordPath = GetRecordPath(record);
        var sha256 = BaselineResponse.ComputeSha256Hex(rawBody);
        if (!string.Equals(sha256, response.BodySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Baseline case {record.CaseId} states bodySha256 {response.BodySha256}, but the raw body "
                + $"hashes to {sha256}.");
        }

        string? bodyPath = null;
        if (response.BodyFile is not null)
        {
            if (response.BodyText is not null)
            {
                throw new InvalidDataException(
                    $"Baseline case {record.CaseId} holds both bodyText and bodyFile; a body is stored one way only.");
            }

            bodyPath = GetBodyFilePath(record);
        }
        else if (!IsFaithfulText(response.BodyText ?? "", rawBody))
        {
            throw new InvalidDataException(
                $"Baseline case {record.CaseId}: bodyText does not reproduce the raw body byte for byte; "
                + $"store the body in the body file '{BodyFileName(record)}' instead.");
        }

        return (recordPath, bodyPath);
    }

    /// <summary>
    /// Publishes <c>baseline/manifest.json</c> atomically: the bytes are written to the fixed temporary file
    /// <c>baseline/.manifest.json.tmp</c>, which then replaces the manifest in one rename, so the manifest is
    /// either the previous complete file or this one. Capture publishes it last, as its completion record.
    /// </summary>
    /// <remarks>
    /// A stale temporary file is replaced. A link at either name refuses the write and is left in place; a hard
    /// link at the manifest is replaced, so its other name keeps its content. A write that fails removes its
    /// temporary file and leaves the manifest as it was.
    /// </remarks>
    /// <param name="manifest">The manifest.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it, its temporary file or a directory on it at or below
    /// <c>baseline/</c> is a symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written or renamed into place.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void WriteManifest(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        PublishManifest(SerializeDocument(manifest));
    }

    /// <summary>
    /// Publishes <c>baseline/manifest.json</c> atomically without blocking, as <see cref="WriteManifest"/> does:
    /// through the fixed temporary file <c>baseline/.manifest.json.tmp</c> and one rename.
    /// </summary>
    /// <remarks>
    /// The token is observed before the temporary file is created, passed to its write and observed again
    /// before the rename, so a cancelled write never publishes: it removes its temporary file and leaves the
    /// manifest as it was. No synchronous disk flush is made; the rename alone makes the publication atomic.
    /// </remarks>
    /// <param name="manifest">The manifest.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the manifest is published.</returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it, its temporary file or a directory on it at or below
    /// <c>baseline/</c> is a symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written or renamed into place.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task WriteManifestAsync(BaselineManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return PublishManifestAsync(SerializeDocument(manifest), cancellationToken);
    }

    /// <summary>Writes <c>baseline/normalisation-rules.json</c> from a document the caller produced.</summary>
    /// <param name="rules">The normalisation rules document.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void WriteNormalisationRules(JsonNode rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        WriteFile(options.NormalisationRulesPath, SerializeDocument(rules));
    }

    /// <summary>
    /// Writes <c>baseline/normalisation-rules.json</c> without blocking, as <see cref="WriteNormalisationRules"/>
    /// does.
    /// </summary>
    /// <param name="rules">The normalisation rules document.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task WriteNormalisationRulesAsync(JsonNode rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return WriteFileAsync(options.NormalisationRulesPath, SerializeDocument(rules), cancellationToken);
    }

    /// <summary>Writes <c>baseline/conformance.json</c> from a document the caller produced.</summary>
    /// <param name="conformance">The conformance result document.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void WriteConformance(JsonNode conformance)
    {
        ArgumentNullException.ThrowIfNull(conformance);
        WriteFile(options.ConformancePath, SerializeDocument(conformance));
    }

    /// <summary>
    /// Writes <c>baseline/conformance.json</c> without blocking, as <see cref="WriteConformance"/> does.
    /// </summary>
    /// <param name="conformance">The conformance result document.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task WriteConformanceAsync(JsonNode conformance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conformance);
        return WriteFileAsync(options.ConformancePath, SerializeDocument(conformance), cancellationToken);
    }

    /// <summary>
    /// Writes <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> with exactly the bytes the stand-in served:
    /// no byte-order mark is added or removed and nothing is reformatted.
    /// </summary>
    /// <param name="exactBytes">The WSDL response body.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void WriteWsdl(byte[] exactBytes)
    {
        ArgumentNullException.ThrowIfNull(exactBytes);
        WriteFile(options.BaselineWsdlPath, exactBytes);
    }

    /// <summary>
    /// Writes <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> without blocking, as <see cref="WriteWsdl"/>
    /// does: exactly the bytes the stand-in served.
    /// </summary>
    /// <param name="exactBytes">The WSDL response body.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task WriteWsdlAsync(byte[] exactBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactBytes);
        return WriteFileAsync(options.BaselineWsdlPath, exactBytes, cancellationToken);
    }

    /// <summary>
    /// Deletes <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> when present, so a capture whose WSDL
    /// fetch failed leaves no stale file contradicting <c>wsdlCaptured: false</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be deleted, or a component on the way is not a directory.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void DeleteWsdl()
    {
        var flags = RequireLinuxTraversal();
        var path = ResolveWritePath(options.BaselineWsdlPath);

        // Nothing is created: a missing directory on the way holds no WSDL to delete.
        using var directory = OpenDirectory(path, path.Segments.Length - 1, create: false, flags);
        if (directory is not null)
        {
            DeleteFile(directory, path.Segments[^1], path.FullPath);
        }
    }

    /// <summary>
    /// Deletes <c>baseline/manifest.json</c> when present, together with the temporary file
    /// <c>baseline/.manifest.json.tmp</c> an interrupted publication left, so a capture withdraws the old
    /// completion record before it clears or rewrites any other baseline file, and a capture that does not
    /// finish leaves no manifest beside a partial baseline.
    /// </summary>
    /// <remarks>
    /// Both names are checked before the first delete, so a refusal deletes neither. A missing directory or
    /// file is nothing to delete, and nothing is created.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or the manifest, its temporary file or a directory on the
    /// way at or below <c>baseline/</c> is a symbolic link or reparse point.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown to this store.
    /// </exception>
    /// <exception cref="IOException">
    /// A file cannot be deleted, or a component on the way is not a directory.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The operating system denies access to a directory or file on the way.
    /// </exception>
    public void DeleteManifest()
    {
        var (flags, target, temporary) = ManifestPaths();

        // Nothing is created: a missing directory on the way holds no manifest to delete.
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: false, flags);
        if (directory is null)
        {
            return;
        }

        foreach (var entry in (BaselinePath[])[target, temporary])
        {
            if (IsLink(directory, entry.Segments[^1], entry.FullPath))
            {
                throw LinkRefusal(entry.FullPath, entry.FullPath);
            }
        }

        DeleteFile(directory, target.Segments[^1], target.FullPath);
        DeleteFile(directory, temporary.Segments[^1], temporary.FullPath);
    }


    /// <summary>Reads <c>baseline/manifest.json</c>, including a hand-written <c>not-run</c> manifest.</summary>
    /// <param name="manifest">The manifest, or null when the file is absent.</param>
    /// <returns>True when the file exists and was read.</returns>
    /// <exception cref="InvalidDataException">The file is not a JSON manifest, or holds a JSON <c>null</c>.</exception>
    public bool TryReadManifest([NotNullWhen(true)] out BaselineManifest? manifest)
    {
        var path = options.ManifestPath;
        if (!TryReadAllBytes(path, out var bytes))
        {
            manifest = null;
            return false;
        }

        manifest = Deserialize<BaselineManifest>(bytes, path);
        return true;
    }

    /// <summary>
    /// Reads <c>baseline/manifest.json</c> without blocking, as <see cref="TryReadManifest"/> does, including a
    /// hand-written <c>not-run</c> manifest.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The manifest, or null when the file is absent.</returns>
    /// <exception cref="InvalidDataException">The file is not a JSON manifest, or holds a JSON <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<BaselineManifest?> TryReadManifestAsync(CancellationToken cancellationToken = default)
    {
        var path = options.ManifestPath;
        var bytes = await TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Deserialize<BaselineManifest>(bytes, path);
    }

    /// <summary>
    /// Reads every record under <c>baseline/records/rest</c> and <c>baseline/records/soap</c>, verified, in
    /// replay order.
    /// </summary>
    /// <remarks>
    /// Each record must sit in the directory of its case id's surface under the file name its order, method
    /// and case number give, and its body (the <c>.body.bin</c> file, or <c>bodyText</c> encoded as UTF-8)
    /// must hash to its <c>bodySha256</c>. No two records may share an order or a case id. A missing records
    /// directory holds no records. Each body is verified and then dropped, so only one is held at a time; use
    /// <see cref="ReadEntries"/> to keep the verified bodies.
    /// </remarks>
    /// <returns>The records sorted by <see cref="BaselineRecord.Order"/>.</returns>
    /// <exception cref="InvalidDataException">
    /// A record file fails any of these checks; the message names it.
    /// </exception>
    /// <exception cref="IOException">A record or body file cannot be read.</exception>
    public IReadOnlyList<BaselineRecord> ReadRecords()
    {
        var entries = new EntrySet(this);
        foreach (var (file, surface) in ListRecordFiles())
        {
            entries.Add(file, ReadEntry(file, surface, keepBody: false));
        }

        return entries.SortedRecords();
    }

    /// <summary>
    /// Reads every record under <c>baseline/records/rest</c> and <c>baseline/records/soap</c> without blocking,
    /// verified, in replay order, with exactly the checks of <see cref="ReadRecords"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads; it is also observed before each record file.</param>
    /// <returns>The records sorted by <see cref="BaselineRecord.Order"/>.</returns>
    /// <exception cref="InvalidDataException">A record file fails any check; the message names it.</exception>
    /// <exception cref="IOException">A record or body file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<BaselineRecord>> ReadRecordsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new EntrySet(this);
        foreach (var (file, surface) in ListRecordFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await ReadEntryAsync(file, surface, keepBody: false, cancellationToken).ConfigureAwait(false);
            entries.Add(file, entry);
        }

        return entries.SortedRecords();
    }

    /// <summary>
    /// Reads every record under <c>baseline/records/rest</c> and <c>baseline/records/soap</c> together with its
    /// raw response body, verified, in replay order.
    /// </summary>
    /// <remarks>
    /// The records and their checks are exactly those of <see cref="ReadRecords"/>. Each body is read from its
    /// <c>.body.bin</c> file, or encoded from <c>bodyText</c> as UTF-8, exactly once, and the bytes that were
    /// hashed against <c>bodySha256</c> are the bytes the entry holds, so no second read through
    /// <see cref="ReadBodyBytes"/> is needed. The store keeps no reference to the entries it returns.
    /// </remarks>
    /// <returns>The entries sorted by <see cref="BaselineRecord.Order"/>.</returns>
    /// <exception cref="InvalidDataException">A record file fails any check; the message names it.</exception>
    /// <exception cref="IOException">A record or body file cannot be read.</exception>
    public IReadOnlyList<BaselineEntry> ReadEntries()
    {
        var entries = new EntrySet(this);
        foreach (var (file, surface) in ListRecordFiles())
        {
            entries.Add(file, ReadEntry(file, surface, keepBody: true));
        }

        return entries.Sorted();
    }

    /// <summary>
    /// Reads every record with its raw response body without blocking, as <see cref="ReadEntries"/> does: the
    /// same verified entries in replay order, each body read or encoded exactly once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads; it is also observed before each record file.</param>
    /// <returns>The entries sorted by <see cref="BaselineRecord.Order"/>.</returns>
    /// <exception cref="InvalidDataException">A record file fails any check; the message names it.</exception>
    /// <exception cref="IOException">A record or body file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<BaselineEntry>> ReadEntriesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new EntrySet(this);
        foreach (var (file, surface) in ListRecordFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await ReadEntryAsync(file, surface, keepBody: true, cancellationToken).ConfigureAwait(false);
            entries.Add(file, entry);
        }

        return entries.Sorted();
    }

    /// <summary>
    /// The key of every record under <c>baseline/records/rest</c> and <c>baseline/records/soap</c>, read without
    /// blocking, in replay order, after every record check of <see cref="ReadRecords"/> but without reading or
    /// hashing a body.
    /// </summary>
    /// <remarks>
    /// <para>Each record must hold every required member and sit in the directory of its case id's surface under
    /// the file name its order, method and case number give. It holds its body either as <c>bodyText</c> or as
    /// a <c>bodyFile</c> naming its own <c>.body.bin</c>, never both, and no two records may share an order or a
    /// case id. A missing records directory holds no records.</para>
    /// <para>Each record file is parsed and checked on its own and reduced to its key at once, so one parsed
    /// record, with its <c>bodyText</c>, is held at a time and none is held once this returns. No
    /// <c>.body.bin</c> file is read and no body is encoded or hashed, so a missing or damaged body does not stop
    /// its record from being listed. The caller reads each case when it needs it with
    /// <see cref="ReadEntryAsync(BaselineRecordKey, CancellationToken)"/>, which checks the record again and
    /// verifies its body exactly once.</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the reads; it is also observed before each record file.</param>
    /// <returns>The keys sorted by <see cref="BaselineRecordKey.Order"/>, then by case id (ordinal).</returns>
    /// <exception cref="InvalidDataException">A record file fails any check; the message names it.</exception>
    /// <exception cref="IOException">A record file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<BaselineRecordKey>> ReadRecordIndexAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new KeySet(this);
        foreach (var (file, surface) in ListRecordFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            keys.Add(file, await ReadRecordKeyAsync(file, surface, cancellationToken).ConfigureAwait(false));
        }

        return keys.Sorted();
    }

    /// <summary>
    /// Reads the one case <paramref name="key"/> names without blocking: its record file, checked as
    /// <see cref="ReadRecordIndexAsync"/> checked it, and its raw response body, verified, as
    /// <see cref="ReadEntriesAsync"/> returns them.
    /// </summary>
    /// <remarks>
    /// <para>The record file is the one the index read the key from: the file name the key's order, the JADE
    /// method of its operation and its case number give, in the directory of its case id's surface. It is read
    /// again, so a record rewritten or removed since it was indexed is refused rather than replayed: it must
    /// pass every record check and still hold the key's order, case id and operation.</para>
    /// <para>The body is read from the <c>.body.bin</c> file, or encoded from <c>bodyText</c> as UTF-8, exactly
    /// once, and the bytes hashed against <c>bodySha256</c> are the bytes the entry holds. The store keeps no
    /// reference to the entry, so dropping it releases the record and its body.</para>
    /// </remarks>
    /// <param name="key">A key <see cref="ReadRecordIndexAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The verified record and its raw body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or one of its names is null.</exception>
    /// <exception cref="ArgumentException">
    /// The key's case id, order or names do not form a record file name.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The record file is missing, fails any record check, no longer holds the key's case, or its body is
    /// missing or does not hash to <c>bodySha256</c>; the message names the record file.
    /// </exception>
    /// <exception cref="IOException">The record or body file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<BaselineEntry> ReadEntryAsync(
        BaselineRecordKey key, CancellationToken cancellationToken = default)
    {
        var (file, surface) = RecordFileOf(key);
        cancellationToken.ThrowIfCancellationRequested();
        var json = await TryReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Baseline record {Display(file)} of case {key.CaseId} is missing.");
        var record = Deserialize<BaselineRecord>(json, file);
        var bodyPath = CheckedBodyPath(record, file, surface);
        if (record.Order != key.Order
            || !string.Equals(record.CaseId, key.CaseId, StringComparison.Ordinal)
            || !string.Equals(record.Operation, key.Operation, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Baseline record {Display(file)} is invalid: it holds case {record.CaseId} of operation "
                + $"{record.Operation} at position {record.Order}, not the indexed case {key.CaseId} of operation "
                + $"{key.Operation} at position {key.Order}.");
        }

        var fileBytes = bodyPath is null
            ? null
            : await TryReadAllBytesAsync(bodyPath, cancellationToken).ConfigureAwait(false);
        return new BaselineEntry(record, VerifiedRecordBody(record, file, bodyPath, fileBytes));
    }

    /// <summary>
    /// The verified raw response body of <paramref name="record"/>: the bytes of its <c>.body.bin</c> file when
    /// it names one, otherwise its <c>bodyText</c> encoded as UTF-8 with no byte-order mark added (a stored
    /// U+FEFF encodes back to <c>EF BB BF</c>). The body is read or encoded once, and the bytes returned are the
    /// bytes that were hashed against the record's <c>bodySha256</c>.
    /// </summary>
    /// <param name="record">A record read from or written to this store.</param>
    /// <returns>The raw body bytes.</returns>
    /// <exception cref="ArgumentException">The record has no response, or its names are invalid.</exception>
    /// <exception cref="InvalidDataException">
    /// The named body file is not the record's own, comes with body text or is missing, or the body does not
    /// hash to <c>bodySha256</c>.
    /// </exception>
    /// <exception cref="IOException">The body file cannot be read.</exception>
    public byte[] ReadBodyBytes(BaselineRecord record)
    {
        var bodyPath = CaseBodyPath(record);
        var fileBytes = bodyPath is not null && TryReadAllBytes(bodyPath, out var bytes) ? bytes : null;
        return VerifiedCaseBody(record, bodyPath, fileBytes);
    }

    /// <summary>
    /// The verified raw response body of <paramref name="record"/>, read without blocking, as
    /// <see cref="ReadBodyBytes"/> returns it: read or encoded once, and hashed against <c>bodySha256</c>.
    /// </summary>
    /// <param name="record">A record read from or written to this store.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The raw body bytes.</returns>
    /// <exception cref="ArgumentException">The record has no response, or its names are invalid.</exception>
    /// <exception cref="InvalidDataException">
    /// The named body file is not the record's own, comes with body text or is missing, or the body does not
    /// hash to <c>bodySha256</c>.
    /// </exception>
    /// <exception cref="IOException">The body file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<byte[]> ReadBodyBytesAsync(BaselineRecord record, CancellationToken cancellationToken = default)
    {
        var bodyPath = CaseBodyPath(record);
        cancellationToken.ThrowIfCancellationRequested();
        var fileBytes = bodyPath is null
            ? null
            : await TryReadAllBytesAsync(bodyPath, cancellationToken).ConfigureAwait(false);
        return VerifiedCaseBody(record, bodyPath, fileBytes);
    }

    /// <summary>
    /// The text of <c>baseline/normalisation-rules.json</c>, decoded as UTF-8 with a leading byte-order mark
    /// removed, for the caller to parse; null when the file is absent.
    /// </summary>
    public string? ReadNormalisationRules() =>
        TryReadAllBytes(options.NormalisationRulesPath, out var bytes)
            ? DecodeText(bytes)
            : null;

    /// <summary>
    /// The text of <c>baseline/normalisation-rules.json</c>, read without blocking and decoded as
    /// <see cref="ReadNormalisationRules"/> decodes it; null when the file is absent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file text, or null.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<string?> ReadNormalisationRulesAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await TryReadAllBytesAsync(options.NormalisationRulesPath, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : DecodeText(bytes);
    }

    /// <summary>The parsed <c>baseline/conformance.json</c>, or null when the file is absent.</summary>
    /// <returns>The document as written by capture.</returns>
    /// <exception cref="InvalidDataException">The file is not JSON, or holds a JSON <c>null</c>.</exception>
    public JsonNode? TryReadConformance()
    {
        var path = options.ConformancePath;
        return TryReadAllBytes(path, out var bytes) ? ParseConformance(bytes, path) : null;
    }

    /// <summary>
    /// The parsed <c>baseline/conformance.json</c>, read without blocking, or null when the file is absent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document as written by capture.</returns>
    /// <exception cref="InvalidDataException">The file is not JSON, or holds a JSON <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<JsonNode?> TryReadConformanceAsync(CancellationToken cancellationToken = default)
    {
        var path = options.ConformancePath;
        var bytes = await TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : ParseConformance(bytes, path);
    }

    /// <summary>
    /// The exact bytes of <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c>, or null when the file is absent.
    /// </summary>
    public byte[]? ReadWsdlBytes() => TryReadAllBytes(options.BaselineWsdlPath, out var bytes) ? bytes : null;

    /// <summary>
    /// The exact bytes of <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c>, read without blocking, or null
    /// when the file is absent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file bytes, or null.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<byte[]?> ReadWsdlBytesAsync(CancellationToken cancellationToken = default) =>
        TryReadAllBytesAsync(options.BaselineWsdlPath, cancellationToken);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = true,
            IndentSize = 2,
            IndentCharacter = ' ',
            NewLine = "\n",

            // Baseline files are read by people and by the harness and are never embedded in HTML, so the
            // relaxed encoder only spares recorded bodies the \u003C-style escapes of markup and non-ASCII
            // text. Quotes, backslashes and control characters are still escaped, and output stays stable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false,
        };
        serializerOptions.MakeReadOnly(populateMissingResolver: true);
        return serializerOptions;
    }

    // Built from its own copy of the baseline format, so it does not depend on the order in which the static
    // members are initialised.
    private static JsonSerializerOptions CreateReadOptions()
    {
        var readOptions = new JsonSerializerOptions(CreateSerializerOptions());
        readOptions.TypeInfoResolver = (readOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(static typeInfo =>
            {
                if (typeInfo.Type != typeof(BaselineRequest) && typeInfo.Type != typeof(BaselineResponse))
                {
                    return;
                }

                foreach (var property in typeInfo.Properties)
                {
                    if (property.PropertyType == typeof(string)
                        && string.Equals(property.Name, BodyTextMember, StringComparison.Ordinal))
                    {
                        property.CustomConverter = UnpooledTextConverter.Instance;
                    }
                }
            });
        readOptions.MakeReadOnly();
        return readOptions;
    }

    private static byte[] WithTrailingNewLine(byte[] json)
    {
        var document = new byte[json.Length + 1];
        json.CopyTo(document, 0);
        document[^1] = (byte)'\n';
        return document;
    }

    // A hand-written file may start with a UTF-8 byte-order mark, which the JSON reader rejects.
    private static ReadOnlySpan<byte> WithoutPreamble(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        return span.StartsWith(Encoding.UTF8.Preamble) ? span[Encoding.UTF8.Preamble.Length..] : span;
    }

    private static string DecodeText(byte[] bytes) => Encoding.UTF8.GetString(WithoutPreamble(bytes));

    private static string FileStem(int position, string jadeMethod, string caseNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, MinPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, MaxPosition);
        ArgumentException.ThrowIfNullOrEmpty(jadeMethod);
        ArgumentException.ThrowIfNullOrEmpty(caseNumber);

        // Both names become part of a path, so nothing but ASCII letters and digits may reach one.
        if (!jadeMethod.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"JADE method '{jadeMethod}' may hold only ASCII letters and digits.", nameof(jadeMethod));
        }

        if (!caseNumber.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"Case number '{caseNumber}' may hold only ASCII digits.", nameof(caseNumber));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{position:D3}-{jadeMethod}-{caseNumber}");
    }

    // The case id <REST|SOAP>-<jadeMethod>-<NN> restates the surface and method of the operation
    // <Rest|Soap>.<jadeMethod>. A record whose two disagree would be filed under one operation and replayed
    // as another, so it is refused before any file name is formed from it.
    private static string ConsistentJadeMethod(BaselineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var jadeMethod = record.JadeMethod;
        var caseId = record.CaseId;
        var firstDash = caseId?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        var lastDash = caseId?.LastIndexOf('-') ?? -1;
        if (caseId is null
            || firstDash < 0
            || lastDash <= firstDash
            || !caseId.AsSpan(firstDash + 1, lastDash - firstDash - 1).SequenceEqual(jadeMethod))
        {
            throw new InvalidDataException(
                $"Baseline case id '{caseId}' does not name the method '{jadeMethod}' of operation "
                + $"'{record.Operation}'.");
        }

        var operationSurface = record.Operation.AsSpan(0, record.Operation.IndexOf('.', StringComparison.Ordinal));
        if (!operationSurface.Equals(caseId.AsSpan(0, firstDash), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Baseline case id '{caseId}' and operation '{record.Operation}' name different surfaces.");
        }

        return jadeMethod;
    }

    // True when rawBody is valid UTF-8 whose decode is exactly text and re-encodes to rawBody, so the record's
    // bodyText alone reproduces the body.
    private static bool IsFaithfulText(string text, ReadOnlySpan<byte> rawBody) =>
        BaselineResponse.IsTextRoundTrippable(rawBody)
        && string.Equals(Encoding.UTF8.GetString(rawBody), text, StringComparison.Ordinal);

    private static bool TryReadAllBytes(string path, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    // The file's bytes, or null when it is absent, as TryReadAllBytes reads them.
    private static async Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private JsonNode ParseConformance(byte[] bytes, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(WithoutPreamble(bytes), nodeOptions: null, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Display(path)} is not valid JSON: {ex.Message}", ex);
        }

        return node ?? throw new InvalidDataException($"{Display(path)} holds a JSON null instead of a document.");
    }

    // Every record file under records/rest and records/soap, each with the surface directory it sits in, in
    // ordinal file-name order within each directory. A missing directory holds no records.
    private IEnumerable<(string File, string Surface)> ListRecordFiles()
    {
        foreach (var surface in SurfaceDirectoryNames)
        {
            var directory = Path.Combine(options.RecordsDirectory, surface);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*" + RecordExtension).Order(StringComparer.Ordinal))
            {
                yield return (file, surface);
            }
        }
    }

    // Reads one record file and its body once each. keepBody false drops the verified body, so ReadRecords
    // holds one body at a time.
    private BaselineEntry ReadEntry(string file, string surface, bool keepBody)
    {
        var record = Deserialize<BaselineRecord>(File.ReadAllBytes(file), file);
        var bodyPath = CheckedBodyPath(record, file, surface);
        var fileBytes = bodyPath is not null && TryReadAllBytes(bodyPath, out var bytes) ? bytes : null;
        var body = VerifiedRecordBody(record, file, bodyPath, fileBytes);
        return new BaselineEntry(record, keepBody ? body : ReadOnlyMemory<byte>.Empty);
    }

    // ReadEntry without blocking: the same checks over the same single read of each file.
    private async Task<BaselineEntry> ReadEntryAsync(
        string file, string surface, bool keepBody, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var record = Deserialize<BaselineRecord>(json, file);
        var bodyPath = CheckedBodyPath(record, file, surface);
        var fileBytes = bodyPath is null
            ? null
            : await TryReadAllBytesAsync(bodyPath, cancellationToken).ConfigureAwait(false);
        var body = VerifiedRecordBody(record, file, bodyPath, fileBytes);
        return new BaselineEntry(record, keepBody ? body : ReadOnlyMemory<byte>.Empty);
    }

    // Reads and checks one record file as every record reader does before it reads the body, and keeps only
    // its key, so the parsed record and its bodyText are unreachable once this returns.
    private async Task<BaselineRecordKey> ReadRecordKeyAsync(
        string file, string surface, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var record = Deserialize<BaselineRecord>(json, file);
        _ = CheckedBodyPath(record, file, surface);
        return new BaselineRecordKey(record.Order, record.CaseId, record.Operation);
    }

    // The record file the index read a key from, with its surface directory, as GetRecordPath forms it for the
    // record itself: the file name of the key's order, the JADE method after the first '.' of its operation and
    // the case number after the last '-' of its case id, in the directory of its case id's surface. Each name
    // is checked before it reaches the path, so no key reaches a file outside records/rest or records/soap.
    private (string File, string Surface) RecordFileOf(BaselineRecordKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(key.CaseId);
        ArgumentNullException.ThrowIfNull(key.Operation);
        var surface = SurfaceDirectoryName(key.CaseId);
        var dot = key.Operation.IndexOf('.', StringComparison.Ordinal);
        var dash = key.CaseId.LastIndexOf('-');
        var name = RecordFileName(
            key.Order,
            dot < 0 ? "" : key.Operation[(dot + 1)..],
            dash < 0 ? "" : key.CaseId[(dash + 1)..]);
        return (Path.Combine(options.RecordsDirectory, surface, name), surface);
    }

    // Checks a parsed record against the file it was read from, and returns the path of its body file, or
    // null when its bodyText holds the body.
    private string? CheckedBodyPath(BaselineRecord record, string file, string surface)
    {
        try
        {
            RequireMembers(record);
            var recordSurface = SurfaceDirectoryName(record.CaseId);
            if (!string.Equals(recordSurface, surface, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"case {record.CaseId} belongs in records/{recordSurface}, not records/{surface}.");
            }

            var expectedName = RecordFileName(record);
            if (!string.Equals(Path.GetFileName(file), expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"case {record.CaseId} at position {record.Order} must be stored as {expectedName}.");
            }

            if (record.Response.BodyFile is null)
            {
                return null;
            }

            if (record.Response.BodyText is not null)
            {
                throw new InvalidDataException("it holds both bodyText and bodyFile.");
            }

            return GetBodyFilePath(record);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            throw InvalidRecord(file, ex);
        }
    }

    private byte[] VerifiedRecordBody(BaselineRecord record, string file, string? bodyPath, byte[]? fileBytes)
    {
        try
        {
            return VerifiedBody(record, bodyPath, fileBytes);
        }
        catch (InvalidDataException ex)
        {
            throw InvalidRecord(file, ex);
        }
    }

    private InvalidDataException InvalidRecord(string file, Exception ex) =>
        new($"Baseline record {Display(file)} is invalid: {ex.Message}", ex);

    // The body file ReadBodyBytes and ReadBodyBytesAsync read for a record, or null when its bodyText holds the
    // body.
    private string? CaseBodyPath(BaselineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var response = record.Response
            ?? throw new ArgumentException($"Baseline case {record.CaseId} has no response.", nameof(record));
        if (response.BodyFile is null)
        {
            return null;
        }

        if (response.BodyText is not null)
        {
            throw new InvalidDataException(
                $"Baseline case {record.CaseId} holds both bodyText and bodyFile; a body is stored one way only.");
        }

        return GetBodyFilePath(record);
    }

    private byte[] VerifiedCaseBody(BaselineRecord record, string? bodyPath, byte[]? fileBytes)
    {
        try
        {
            return VerifiedBody(record, bodyPath, fileBytes);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Baseline case {record.CaseId} is invalid: {ex.Message}", ex);
        }
    }

    // A malformed file can deserialise with null members the types declare non-nullable.
    private static void RequireMembers(BaselineRecord record)
    {
        string? missing = record switch
        {
            { CaseId: null } => "caseId",
            { Operation: null } => "operation",
            { Request: null } => "request",
            { Request.Method: null } => "request.method",
            { Request.PathAndQuery: null } => "request.pathAndQuery",
            { Request.Headers: null } => "request.headers",
            { Response: null } => "response",
            { Response.BodySha256: null } => "response.bodySha256",
            _ => null,
        };
        if (missing is not null)
        {
            throw new InvalidDataException($"member {missing} is missing.");
        }
    }

    // The raw body of a record, verified: the body file's bytes as read (null when the file was missing), or,
    // with no body file, bodyText encoded once as UTF-8. The returned bytes are the ones hashed against
    // bodySha256. Messages are fragments the caller prefixes with the record or case they concern.
    private byte[] VerifiedBody(BaselineRecord record, string? bodyPath, byte[]? fileBytes)
    {
        var response = record.Response;
        var body = bodyPath is null
            ? Encoding.UTF8.GetBytes(response.BodyText ?? "")
            : fileBytes ?? throw new InvalidDataException($"its body file {Display(bodyPath)} is missing.");
        var sha256 = BaselineResponse.ComputeSha256Hex(body);
        if (!string.Equals(sha256, response.BodySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"its body hashes to {sha256}, not to the recorded bodySha256 {response.BodySha256}.");
        }

        return body;
    }

    private T Deserialize<T>(byte[] bytes, string path)
        where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(WithoutPreamble(bytes), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{Display(path)} is not a valid {typeof(T).Name} document: {ex.Message}", ex);
        }

        return value
            ?? throw new InvalidDataException($"{Display(path)} holds a JSON null instead of a {typeof(T).Name}.");
    }

    private void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var handle = CreateReplacementFile(path);
        RandomAccess.Write(handle, bytes, fileOffset: 0);
    }

    // WriteFile without blocking. A token cancelled before the write starts leaves the tree untouched; a write
    // cancelled while in progress can leave its file incomplete, as File.WriteAllBytesAsync can, and an
    // incomplete record or body file fails the next read's verification.
    private async Task WriteFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateReplacementFile(path);
        await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }

    // Opens the target's directory through handles, creating what is missing, then works on the final name
    // inside that one directory only: a link there is refused, the entry the file replaces is removed, and the
    // new file is created exclusively without following a link. A link planted at the name after the check is
    // removed by that unlink rather than followed, and anything that reappears there fails the exclusive
    // create, so the bytes land in a new file inside baseline/ and nowhere else. Returns the file open for
    // writing.
    private SafeFileHandle CreateReplacementFile(string path)
    {
        var flags = RequireLinuxTraversal();
        var target = ResolveWritePath(path);
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        return CreateReplacementFile(directory, target, flags);
    }

    // CreateReplacementFile for the final name of target inside the directory handle that holds it, which the
    // caller keeps open.
    private SafeFileHandle CreateReplacementFile(DirectoryHandle directory, BaselinePath target, TraversalFlags flags)
    {
        var name = target.Segments[^1];
        if (IsLink(directory, name, target.FullPath))
        {
            throw LinkRefusal(target.FullPath, target.FullPath);
        }

        // unlinkat removes the entry itself, so a hard link to a file elsewhere is dropped, never written through.
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
            throw errno == Native.EEXIST
                ? new IOException(
                    $"BaselineStore could not create '{Display(target.FullPath)}': an entry appeared there after "
                    + "the one it replaces was removed.")
                : Failure(errno, "create", target.FullPath);
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    // The manifest and its fixed temporary file, which sit in the same directory, with this process's open flags.
    private (TraversalFlags Flags, BaselinePath Target, BaselinePath Temporary) ManifestPaths()
    {
        var flags = RequireLinuxTraversal();
        var target = ResolveWritePath(options.ManifestPath);
        var temporary = ResolveWritePath(
            Path.Join(Path.GetDirectoryName(target.FullPath), ManifestTemporaryFileName));
        return (flags, target, temporary);
    }

    // Writes the manifest's bytes to its temporary file, created as CreateReplacementFile creates every file,
    // and renames that complete file onto the manifest inside the same directory handle. Whatever ends the
    // publication early, the temporary file this call created is removed, and the manifest is left as it was.
    private void PublishManifest(ReadOnlySpan<byte> bytes)
    {
        var (flags, target, temporary) = ManifestPaths();
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        var created = false;
        var published = false;
        try
        {
            using (var handle = CreateReplacementFile(directory, temporary, flags))
            {
                created = true;
                RandomAccess.Write(handle, bytes, fileOffset: 0);
            }

            RenameOntoManifest(directory, temporary, target);
            published = true;
        }
        finally
        {
            if (created && !published)
            {
                DiscardTemporary(directory, temporary.Segments[^1]);
            }
        }
    }

    // PublishManifest without blocking. The token is observed before anything is created and again before the
    // rename, so a cancelled publication leaves the manifest as it was. There is no synchronous disk flush,
    // which would block this continuation: the rename alone makes the publication atomic.
    private async Task PublishManifestAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (flags, target, temporary) = ManifestPaths();
        using var directory = OpenDirectory(target, target.Segments.Length - 1, create: true, flags)!;
        var created = false;
        var published = false;
        try
        {
            using (var handle = CreateReplacementFile(directory, temporary, flags))
            {
                created = true;
                await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RenameOntoManifest(directory, temporary, target);
            published = true;
        }
        finally
        {
            if (created && !published)
            {
                DiscardTemporary(directory, temporary.Segments[^1]);
            }
        }
    }

    // Replaces the manifest's entry with the complete temporary file in one renameat inside one directory
    // handle, so no reader ever sees part of a manifest, and a hard link at the name keeps its other name's
    // content. A link at the name is refused and left in place, as it is for every other write.
    private void RenameOntoManifest(DirectoryHandle directory, BaselinePath temporary, BaselinePath target)
    {
        var name = target.Segments[^1];
        if (IsLink(directory, name, target.FullPath))
        {
            throw LinkRefusal(target.FullPath, target.FullPath);
        }

        if (Native.RenameAt(directory, temporary.Segments[^1], directory, name) != 0)
        {
            throw Failure(Marshal.GetLastPInvokeError(), "publish", target.FullPath);
        }
    }

    // Removes the temporary file of a publication that did not complete. It is best effort and never throws,
    // so the failure that ended the publication is the one reported; a file it cannot remove is replaced by
    // the next publication. A link found at the name instead is left in place, as every link is.
    private static void DiscardTemporary(DirectoryHandle directory, string name)
    {
        byte firstTargetByte = 0;
        if (Native.ReadLinkAt(directory, name, ref firstTargetByte, size: 1) < 0)
        {
            _ = Native.UnlinkAt(directory, name, flags: 0);
        }
    }

    // Deletes the entry name inside a directory handle: a link there is refused, a missing entry is nothing to
    // delete, and a directory is kept. unlinkat removes the entry itself, never what a link names.
    private void DeleteFile(DirectoryHandle directory, string name, string path)
    {
        if (IsLink(directory, name, path))
        {
            throw LinkRefusal(path, path);
        }

        if (Native.UnlinkAt(directory, name, flags: 0) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is not (Native.ENOENT or Native.EISDIR))
            {
                throw Failure(errno, "delete", path);
            }
        }
    }

    // Adds to files the name of every file directly inside a records directory handle. The listing goes
    // through /proc/self/fd, which the kernel resolves to the directory the handle holds, not to whatever sits
    // at its path now. Attributes come from lstat, so a link carries ReparsePoint even when it dangles; a link
    // refuses the clear before anything is deleted, and a directory is kept.
    private void ListFiles(DirectoryHandle directory, string path, List<string> files)
    {
        var listing = new DirectoryInfo(
            string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{directory.DangerousGetHandle()}"));
        List<(string Name, FileAttributes Attributes)> entries;
        try
        {
            entries = [.. listing.EnumerateFileSystemInfos().Select(static entry => (entry.Name, entry.Attributes))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The listing path names a descriptor, so the message names the records directory instead.
            var message = $"BaselineStore could not list '{Display(path)}'.";
            throw ex is UnauthorizedAccessException
                ? new UnauthorizedAccessException(message, ex)
                : new IOException(message, ex);
        }

        foreach (var (name, attributes) in entries)
        {
            // An entry removed since the directory was read has nothing left to delete.
            if (attributes == MissingEntryAttributes)
            {
                continue;
            }

            var entryPath = Path.Join(path, name);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || IsLink(directory, name, entryPath))
            {
                throw LinkRefusal(entryPath, entryPath);
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                files.Add(name);
            }
        }
    }

    // Refuses a path that leaves baseline/ lexically or names baseline/ itself, and splits the rest into the
    // components below baseline/ that the directory handles walk.
    private BaselinePath ResolveWritePath(string path)
    {
        var baselineDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BaselineDirectory));
        var baselineRoot = baselineDirectory + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        string[] segments = fullPath.StartsWith(baselineRoot, StringComparison.Ordinal)
            ? fullPath[baselineRoot.Length..].Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (segments.Length == 0)
        {
            throw new InvalidOperationException(
                $"BaselineStore never writes outside baseline/: refused '{fullPath}' (baseline is '{baselineRoot}').");
        }

        return new BaselinePath(baselineDirectory, fullPath, segments);
    }

    // Opens the directory that the first depth segments of target name, starting at baseline/ itself. Each
    // component is opened relative to the handle of the one above it, never through a link, and with create
    // it is made first when it is missing. Without create a missing component leaves nothing to act on, and
    // null is returned. The caller disposes the handle.
    private DirectoryHandle? OpenDirectory(BaselinePath target, int depth, bool create, TraversalFlags flags)
    {
        if (create)
        {
            // The repository root and the directories above it are the caller's and trusted, so they are made
            // the ordinary way.
            Directory.CreateDirectory(Path.GetDirectoryName(target.BaselineDirectory)!);
        }

        DirectoryHandle? directory;
        using (var trusted = DirectoryHandle.CurrentDirectory())
        {
            directory = OpenComponent(
                trusted, target.BaselineDirectory, target.BaselineDirectory, target.FullPath, create, flags);
        }

        var component = target.BaselineDirectory;
        for (var index = 0; directory is not null && index < depth; index++)
        {
            component = Path.Join(component, target.Segments[index]);
            using var parent = directory;
            directory = OpenComponent(parent, target.Segments[index], component, target.FullPath, create, flags);
        }

        return directory;
    }

    // The directory name inside parent, opened without following a link at name and, with create, made first
    // when it is missing; null when it is missing without create. component is the path name stands for, and
    // refusedPath the target a refusal names.
    private DirectoryHandle? OpenComponent(
        DirectoryHandle parent, string name, string component, string refusedPath, bool create, TraversalFlags flags)
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
            throw IsLink(parent, name, component)
                ? LinkRefusal(refusedPath, component)
                : new IOException($"BaselineStore could not open '{Display(component)}': it is not a directory.");
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

    private InvalidOperationException LinkRefusal(string refusedPath, string component) =>
        new($"BaselineStore never writes or deletes through a link: refused '{Display(refusedPath)}', because "
            + $"'{Display(component)}' is a symbolic link or reparse point.");

    // An operating-system error as the exception .NET raises for it, naming the path relative to the
    // repository root.
    private Exception Failure(int errno, string action, string path)
    {
        var message = $"BaselineStore could not {action} '{Display(path)}': {Marshal.GetPInvokeErrorMessage(errno)}.";
        return errno is Native.EACCES or Native.EPERM
            ? new UnauthorizedAccessException(message)
            : new IOException(message);
    }

    // The open(2) flags of every write and delete for this process, refusing a platform without Linux directory
    // handles.
    private static TraversalFlags RequireLinuxTraversal()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "BaselineStore writes and deletes only on Linux: it confines them to baseline/ through Linux "
                + "directory handles, which this operating system does not provide.");
        }

        // O_DIRECTORY and O_NOFOLLOW are the only flags used here whose values differ between architectures.
        var (directory, noFollow) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 or Architecture.S390x or Architecture.RiscV64
                or Architecture.LoongArch64 => (0x10000, 0x20000),
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => (0x4000, 0x8000),
            var architecture => throw new PlatformNotSupportedException(
                $"BaselineStore writes and deletes only where it knows the Linux open flags, which it does not for "
                + $"{architecture}."),
        };
        return new TraversalFlags(
            Native.O_RDONLY | directory | noFollow | Native.O_CLOEXEC,
            Native.O_WRONLY | Native.O_CREAT | Native.O_EXCL | noFollow | Native.O_CLOEXEC);
    }

    // Messages name paths relative to the repository root, so an error quoted in a report reads the same in
    // every checkout and container.
    private string Display(string path)
    {
        var repositoryRoot = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BaselineDirectory)));
        return repositoryRoot is null
            ? path
            : Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    // Collects what one read takes from each record file, refusing a repeated replay position or case id, and
    // returns it in replay order: by position, then by case id (ordinal).
    private abstract class ReplayOrderSet<T>(BaselineStore store)
    {
        private readonly List<T> items = [];
        private readonly Dictionary<int, string> fileByOrder = [];
        private readonly Dictionary<string, string> fileByCaseId = new(StringComparer.Ordinal);

        public void Add(string file, T item)
        {
            var order = OrderOf(item);
            var caseId = CaseIdOf(item);
            if (!fileByOrder.TryAdd(order, file))
            {
                throw new InvalidDataException(
                    $"Baseline record {store.Display(file)} repeats replay position {order} of "
                    + $"{store.Display(fileByOrder[order])}.");
            }

            if (!fileByCaseId.TryAdd(caseId, file))
            {
                throw new InvalidDataException(
                    $"Baseline record {store.Display(file)} repeats case {caseId} of "
                    + $"{store.Display(fileByCaseId[caseId])}.");
            }

            items.Add(item);
        }

        public List<T> Sorted()
        {
            items.Sort((left, right) =>
            {
                var byOrder = OrderOf(left).CompareTo(OrderOf(right));
                return byOrder != 0 ? byOrder : string.CompareOrdinal(CaseIdOf(left), CaseIdOf(right));
            });
            return items;
        }

        protected abstract int OrderOf(T item);

        protected abstract string CaseIdOf(T item);
    }

    // The verified entries of ReadRecords, ReadEntries and their asynchronous forms.
    private sealed class EntrySet(BaselineStore store) : ReplayOrderSet<BaselineEntry>(store)
    {
        public List<BaselineRecord> SortedRecords() => Sorted().ConvertAll(static entry => entry.Record);

        protected override int OrderOf(BaselineEntry item) => item.Record.Order;

        protected override string CaseIdOf(BaselineEntry item) => item.Record.CaseId;
    }

    // The keys of ReadRecordIndexAsync, refused and ordered exactly as EntrySet refuses and orders entries.
    private sealed class KeySet(BaselineStore store) : ReplayOrderSet<BaselineRecordKey>(store)
    {
        protected override int OrderOf(BaselineRecordKey item) => item.Order;

        protected override string CaseIdOf(BaselineRecordKey item) => item.CaseId;
    }

    // Reads a bodyText to the same string the built-in string converter returns, but unescapes an escaped value
    // into an array of its own, which is garbage once the string is made, rather than into one rented from
    // ArrayPool<byte>.Shared. Any other token, an unescaped value and a value that is not valid UTF-8 are left
    // to Utf8JsonReader.GetString, so every failure is the built-in one. Write is the built-in write, although
    // ReadOptions never writes.
    private sealed class UnpooledTextConverter : JsonConverter<string>
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static UnpooledTextConverter Instance { get; } = new();

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !reader.ValueIsEscaped || reader.HasValueSequence)
            {
                return reader.GetString();
            }

            // An escape is never shorter than what it stands for, so the escaped length always suffices.
            var unescaped = new byte[reader.ValueSpan.Length];
            var length = reader.CopyString(unescaped);
            try
            {
                return StrictUtf8.GetString(unescaped, 0, length);
            }
            catch (DecoderFallbackException)
            {
                return reader.GetString();
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    // A write or delete target inside baseline/: the baseline directory, the target's full path and the
    // components below baseline/ that lead to it, the target's own name last.
    private readonly record struct BaselinePath(string BaselineDirectory, string FullPath, string[] Segments);

    // The open(2) flags of one write or delete: a directory opened read-only, and a file created write-only and
    // exclusively, both without following a link and both closed on exec.
    private readonly record struct TraversalFlags(int OpenDirectory, int CreateFile);

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

        public const int O_RDONLY = 0;
        public const int O_WRONLY = 1;
        public const int O_CREAT = 0x40;
        public const int O_EXCL = 0x80;
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
        public const int ELOOP = 40;

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

        [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
        public static extern int RenameAt(
            DirectoryHandle oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            DirectoryHandle newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
    }
}

/// <summary>
/// One verified case of the baseline together with its raw response body, as
/// <see cref="BaselineStore.ReadEntries"/> returns it for every record, or as
/// <see cref="BaselineStore.ReadEntryAsync(BaselineRecordKey, CancellationToken)"/> returns it for the one record
/// a key of <see cref="BaselineStore.ReadRecordIndexAsync"/> names. The body was read from the record's
/// <c>.body.bin</c> file, or encoded from its <c>bodyText</c> as UTF-8, exactly once, and those bytes hash to the
/// record's <c>bodySha256</c>.
/// </summary>
/// <remarks>
/// The body belongs to the entry alone: the store keeps no cache, so dropping an entry releases its body.
/// Equality compares <see cref="Body"/> by the memory it wraps, not by its bytes.
/// </remarks>
/// <param name="Record">The verified record.</param>
/// <param name="Body">The raw response body the record describes, byte for byte.</param>
public sealed record BaselineEntry(BaselineRecord Record, ReadOnlyMemory<byte> Body);

/// <summary>
/// The identity of one checked baseline record, as <see cref="BaselineStore.ReadRecordIndexAsync"/> lists it:
/// enough to count the captured cases and operations, to replay them in order and to read each one with
/// <see cref="BaselineStore.ReadEntryAsync(BaselineRecordKey, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// A key holds no request, response or body, so a list of keys stays small however large the recorded
/// bodies are.
/// </remarks>
/// <param name="Order">The record's 1-based replay position, the <c>NNN</c> of its file name.</param>
/// <param name="CaseId">The record's case id, for example <c>SOAP-getPhoto-02</c>.</param>
/// <param name="Operation">The record's catalogue operation id, for example <c>Soap.getPhoto</c>.</param>
public sealed record BaselineRecordKey(int Order, string CaseId, string Operation);
