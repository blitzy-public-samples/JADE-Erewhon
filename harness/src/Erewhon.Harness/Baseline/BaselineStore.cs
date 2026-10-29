using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erewhon.Contracts;

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
/// <para>Containment is physical as well as lexical: this store never writes or deletes through a link.
/// Every existing component from <c>baseline/</c> itself down to the target is examined without following
/// links before the target's directories are created, again after they are, and immediately before each write
/// or delete. A symbolic link, junction or other reparse point there, dangling or not, refuses the operation.
/// Components above <c>baseline/</c> belong to the repository root the caller chose and are trusted. A file
/// is replaced, never rewritten in place: the old file is deleted and the new one created exclusively, so
/// neither a link planted at the final path after the last check nor a hard link to a file elsewhere is
/// written through.</para>
/// <para>Files are written straight to their final path with one fixed JSON format and no timestamp, so a
/// re-capture of an unchanged stand-in reproduces every file byte for byte (the manifest's
/// <c>captureCommit</c> aside) and no temporary file is ever left in the tree.</para>
/// <para>Reads verify what they return: every record must sit at the path its own members name, and its
/// body must match its <c>bodySha256</c>, so replay never compares against a damaged baseline. Each body is
/// read once: <see cref="ReadEntries"/> returns the verified bytes with their record, and
/// <see cref="ReadBodyBytes"/> verifies the bytes it returns.</para>
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
    /// <exception cref="IOException">A directory cannot be created or a file cannot be deleted.</exception>
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
    /// <exception cref="IOException">A directory cannot be created or a file cannot be deleted.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void ClearRecords(CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var surface in SurfaceDirectoryNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = GuardWritePath(Path.Combine(options.RecordsDirectory, surface));
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(GuardWritePath(directory)))
            {
                files.Add(GuardWritePath(file));
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(GuardWritePath(file));
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
    /// <exception cref="InvalidDataException">
    /// <c>bodySha256</c> does not cover <paramref name="rawBody"/>; a named body file is not the record's own
    /// or comes with body text; or, with no body file, the body text does not reproduce
    /// <paramref name="rawBody"/> byte for byte.
    /// </exception>
    /// <exception cref="ArgumentException">The record's members are missing, out of range or malformed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
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
    /// <exception cref="InvalidDataException">
    /// <c>bodySha256</c> does not cover <paramref name="rawBody"/>; a named body file is not the record's own
    /// or comes with body text; or, with no body file, the body text does not reproduce
    /// <paramref name="rawBody"/> byte for byte.
    /// </exception>
    /// <exception cref="ArgumentException">The record's members are missing, out of range or malformed.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
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

    /// <summary>Writes <c>baseline/manifest.json</c>.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void WriteManifest(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        WriteFile(options.ManifestPath, SerializeDocument(manifest));
    }

    /// <summary>Writes <c>baseline/manifest.json</c> without blocking, as <see cref="WriteManifest"/> does.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task WriteManifestAsync(BaselineManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return WriteFileAsync(options.ManifestPath, SerializeDocument(manifest), cancellationToken);
    }

    /// <summary>Writes <c>baseline/normalisation-rules.json</c> from a document the caller produced.</summary>
    /// <param name="rules">The normalisation rules document.</param>
    /// <exception cref="InvalidOperationException">
    /// The path resolves outside <c>baseline/</c>, or it or a directory on it at or below <c>baseline/</c> is a
    /// symbolic link or reparse point.
    /// </exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    /// <exception cref="IOException">The file cannot be written.</exception>
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
    public void DeleteWsdl()
    {
        var path = GuardWritePath(options.BaselineWsdlPath);
        if (File.Exists(path))
        {
            File.Delete(GuardWritePath(path));
        }
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
            value = JsonSerializer.Deserialize<T>(WithoutPreamble(bytes), SerializerOptions);
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
        var fullPath = PrepareWrite(path);
        using var handle = File.OpenHandle(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None, bytes.Length);
        RandomAccess.Write(handle, bytes, fileOffset: 0);
    }

    // WriteFile without blocking. A token cancelled before the write starts leaves the tree untouched; a write
    // cancelled while in progress can leave its file incomplete, as File.WriteAllBytesAsync can, and an
    // incomplete record or body file fails the next read's verification.
    private async Task WriteFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = PrepareWrite(path);
        using var handle = File.OpenHandle(
            fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous, bytes.Length);
        await RandomAccess.WriteAsync(handle, bytes, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }

    // Creates the target's directories and removes the file it replaces, guarding before and after the
    // directories are created and immediately before the delete and the write. The caller then creates the
    // file with FileMode.CreateNew, which never follows a link at the final path and fails if anything
    // reappeared there, so the bytes land in a new file inside baseline/ and nowhere else.
    private string PrepareWrite(string path)
    {
        var fullPath = GuardWritePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.Delete(GuardWritePath(fullPath));
        return GuardWritePath(fullPath);
    }

    // Refuses a path that leaves baseline/ lexically, then one that reaches its target through a link: every
    // existing component from baseline/ itself down to the target is examined, stopping at the first missing
    // one, because nothing exists below a missing directory.
    private string GuardWritePath(string path)
    {
        var baselineDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BaselineDirectory));
        var baselineRoot = baselineDirectory + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(baselineRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"BaselineStore never writes outside baseline/: refused '{fullPath}' (baseline is '{baselineRoot}').");
        }

        var segments = fullPath[baselineRoot.Length..].Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        var component = baselineDirectory;
        var next = 0;
        while (ExistsWithoutLink(component, fullPath) && next < segments.Length)
        {
            component = Path.Join(component, segments[next++]);
        }

        return fullPath;
    }

    // False when the component is missing; throws when it is a link. Neither FileInfo member follows a link:
    // Attributes reports the entry itself, so a symbolic link carries ReparsePoint even when it dangles, and
    // LinkTarget names the target of any symbolic link or junction, dangling or not.
    private bool ExistsWithoutLink(string component, string refusedPath)
    {
        var entry = new FileInfo(component);
        var attributes = entry.Attributes;
        if (attributes == MissingEntryAttributes)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"BaselineStore never writes or deletes through a link: refused '{Display(refusedPath)}', because "
                + $"'{Display(component)}' is a symbolic link or reparse point.");
        }

        return true;
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

    // Collects verified entries, refusing a repeated replay position or case id, and returns them in replay
    // order.
    private sealed class EntrySet(BaselineStore store)
    {
        private readonly List<BaselineEntry> entries = [];
        private readonly Dictionary<int, string> fileByOrder = [];
        private readonly Dictionary<string, string> fileByCaseId = new(StringComparer.Ordinal);

        public void Add(string file, BaselineEntry entry)
        {
            var record = entry.Record;
            if (!fileByOrder.TryAdd(record.Order, file))
            {
                throw new InvalidDataException(
                    $"Baseline record {store.Display(file)} repeats replay position {record.Order} of "
                    + $"{store.Display(fileByOrder[record.Order])}.");
            }

            if (!fileByCaseId.TryAdd(record.CaseId, file))
            {
                throw new InvalidDataException(
                    $"Baseline record {store.Display(file)} repeats case {record.CaseId} of "
                    + $"{store.Display(fileByCaseId[record.CaseId])}.");
            }

            entries.Add(entry);
        }

        public List<BaselineEntry> Sorted()
        {
            entries.Sort(static (left, right) =>
            {
                var byOrder = left.Record.Order.CompareTo(right.Record.Order);
                return byOrder != 0 ? byOrder : string.CompareOrdinal(left.Record.CaseId, right.Record.CaseId);
            });
            return entries;
        }

        public List<BaselineRecord> SortedRecords() => Sorted().ConvertAll(static entry => entry.Record);
    }
}

/// <summary>
/// One verified case of the baseline together with its raw response body, as
/// <see cref="BaselineStore.ReadEntries"/> returns it. The body was read from the record's <c>.body.bin</c>
/// file, or encoded from its <c>bodyText</c> as UTF-8, exactly once, and those bytes hash to the record's
/// <c>bodySha256</c>.
/// </summary>
/// <remarks>
/// The body belongs to the entry alone: the store keeps no cache, so dropping an entry releases its body.
/// Equality compares <see cref="Body"/> by the memory it wraps, not by its bytes.
/// </remarks>
/// <param name="Record">The verified record.</param>
/// <param name="Body">The raw response body the record describes, byte for byte.</param>
public sealed record BaselineEntry(BaselineRecord Record, ReadOnlyMemory<byte> Body);
