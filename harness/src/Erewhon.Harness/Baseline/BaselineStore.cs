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
/// <para>Every path comes from <see cref="HarnessOptions"/>. A write whose path resolves outside
/// <c>baseline/</c> is refused, so this store never writes <c>contracts/</c> or anything else.</para>
/// <para>Files are written straight to their final path with one fixed JSON format and no timestamp, so a
/// re-capture of an unchanged stand-in reproduces every file byte for byte (the manifest's
/// <c>captureCommit</c> aside) and no temporary file is ever left in the tree.</para>
/// <para>Reads verify what they return: every record must sit at the path its own members name, and its
/// body must match its <c>bodySha256</c>, so replay never compares against a damaged baseline.</para>
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

    private static readonly string[] SurfaceDirectoryNames = [RestDirectoryName, SoapDirectoryName];

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
    /// <exception cref="InvalidOperationException">A records directory resolves outside <c>baseline/</c>.</exception>
    public void ClearRecords()
    {
        foreach (var surface in SurfaceDirectoryNames)
        {
            var directory = GuardWritePath(Path.Combine(options.RecordsDirectory, surface));
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(directory))
            {
                File.Delete(GuardWritePath(file));
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
    /// The record is not labelled <see cref="SourceLabels.StandIn"/>, or a path resolves outside <c>baseline/</c>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <c>bodySha256</c> does not cover <paramref name="rawBody"/>; a named body file is not the record's own
    /// or comes with body text; or, with no body file, the body text does not reproduce
    /// <paramref name="rawBody"/> byte for byte.
    /// </exception>
    /// <exception cref="ArgumentException">The record's members are missing, out of range or malformed.</exception>
    public void WriteRecord(BaselineRecord record, ReadOnlySpan<byte> rawBody)
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

        if (bodyPath is not null)
        {
            WriteFile(bodyPath, rawBody);
        }

        WriteFile(recordPath, SerializeDocument(record));
    }

    /// <summary>Writes <c>baseline/manifest.json</c>.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <exception cref="InvalidOperationException">The path resolves outside <c>baseline/</c>.</exception>
    public void WriteManifest(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        WriteFile(options.ManifestPath, SerializeDocument(manifest));
    }

    /// <summary>Writes <c>baseline/normalisation-rules.json</c> from a document the caller produced.</summary>
    /// <param name="rules">The normalisation rules document.</param>
    /// <exception cref="InvalidOperationException">The path resolves outside <c>baseline/</c>.</exception>
    public void WriteNormalisationRules(JsonNode rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        WriteFile(options.NormalisationRulesPath, SerializeDocument(rules));
    }

    /// <summary>Writes <c>baseline/conformance.json</c> from a document the caller produced.</summary>
    /// <param name="conformance">The conformance result document.</param>
    /// <exception cref="InvalidOperationException">The path resolves outside <c>baseline/</c>.</exception>
    public void WriteConformance(JsonNode conformance)
    {
        ArgumentNullException.ThrowIfNull(conformance);
        WriteFile(options.ConformancePath, SerializeDocument(conformance));
    }

    /// <summary>
    /// Writes <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> with exactly the bytes the stand-in served:
    /// no byte-order mark is added or removed and nothing is reformatted.
    /// </summary>
    /// <param name="exactBytes">The WSDL response body.</param>
    /// <exception cref="InvalidOperationException">The path resolves outside <c>baseline/</c>.</exception>
    public void WriteWsdl(byte[] exactBytes)
    {
        ArgumentNullException.ThrowIfNull(exactBytes);
        WriteFile(options.BaselineWsdlPath, exactBytes);
    }

    /// <summary>
    /// Deletes <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> when present, so a capture whose WSDL
    /// fetch failed leaves no stale file contradicting <c>wsdlCaptured: false</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path resolves outside <c>baseline/</c>.</exception>
    public void DeleteWsdl()
    {
        var path = GuardWritePath(options.BaselineWsdlPath);
        if (File.Exists(path))
        {
            File.Delete(path);
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
    /// Reads every record under <c>baseline/records/rest</c> and <c>baseline/records/soap</c>, verified, in
    /// replay order.
    /// </summary>
    /// <remarks>
    /// Each record must sit in the directory of its case id's surface under the file name its order, method
    /// and case number give, and its body (the <c>.body.bin</c> file, or <c>bodyText</c> encoded as UTF-8)
    /// must hash to its <c>bodySha256</c>. No two records may share an order or a case id. A missing records
    /// directory holds no records.
    /// </remarks>
    /// <returns>The records sorted by <see cref="BaselineRecord.Order"/>.</returns>
    /// <exception cref="InvalidDataException">
    /// A record file fails any of these checks; the message names it.
    /// </exception>
    public IReadOnlyList<BaselineRecord> ReadRecords()
    {
        var records = new List<BaselineRecord>();
        var fileByOrder = new Dictionary<int, string>();
        var fileByCaseId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var surface in SurfaceDirectoryNames)
        {
            var directory = Path.Combine(options.RecordsDirectory, surface);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*" + RecordExtension).Order(StringComparer.Ordinal))
            {
                var record = ReadRecord(file, surface);
                if (!fileByOrder.TryAdd(record.Order, file))
                {
                    throw new InvalidDataException(
                        $"Baseline record {Display(file)} repeats replay position {record.Order} of "
                        + $"{Display(fileByOrder[record.Order])}.");
                }

                if (!fileByCaseId.TryAdd(record.CaseId, file))
                {
                    throw new InvalidDataException(
                        $"Baseline record {Display(file)} repeats case {record.CaseId} of "
                        + $"{Display(fileByCaseId[record.CaseId])}.");
                }

                records.Add(record);
            }
        }

        records.Sort(static (left, right) =>
        {
            var byOrder = left.Order.CompareTo(right.Order);
            return byOrder != 0 ? byOrder : string.CompareOrdinal(left.CaseId, right.CaseId);
        });
        return records;
    }

    /// <summary>
    /// The raw response body of <paramref name="record"/>: the bytes of its <c>.body.bin</c> file when it
    /// names one, otherwise its <c>bodyText</c> encoded as UTF-8 with no byte-order mark added (a stored
    /// U+FEFF encodes back to <c>EF BB BF</c>).
    /// </summary>
    /// <param name="record">A record read from or written to this store.</param>
    /// <returns>The raw body bytes.</returns>
    /// <exception cref="InvalidDataException">The named body file is not the record's own.</exception>
    /// <exception cref="IOException">The body file cannot be read.</exception>
    public byte[] ReadBodyBytes(BaselineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Response.BodyFile is null
            ? Encoding.UTF8.GetBytes(record.Response.BodyText ?? "")
            : File.ReadAllBytes(GetBodyFilePath(record));
    }

    /// <summary>
    /// The text of <c>baseline/normalisation-rules.json</c>, decoded as UTF-8 with a leading byte-order mark
    /// removed, for the caller to parse; null when the file is absent.
    /// </summary>
    public string? ReadNormalisationRules() =>
        TryReadAllBytes(options.NormalisationRulesPath, out var bytes)
            ? Encoding.UTF8.GetString(WithoutPreamble(bytes))
            : null;

    /// <summary>The parsed <c>baseline/conformance.json</c>, or null when the file is absent.</summary>
    /// <returns>The document as written by capture.</returns>
    /// <exception cref="InvalidDataException">The file is not JSON, or holds a JSON <c>null</c>.</exception>
    public JsonNode? TryReadConformance()
    {
        var path = options.ConformancePath;
        if (!TryReadAllBytes(path, out var bytes))
        {
            return null;
        }

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

    /// <summary>
    /// The exact bytes of <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c>, or null when the file is absent.
    /// </summary>
    public byte[]? ReadWsdlBytes() => TryReadAllBytes(options.BaselineWsdlPath, out var bytes) ? bytes : null;

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

    private BaselineRecord ReadRecord(string file, string surface)
    {
        var record = Deserialize<BaselineRecord>(File.ReadAllBytes(file), file);
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

            VerifyBody(record);
            return record;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"Baseline record {Display(file)} is invalid: {ex.Message}", ex);
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

    private void VerifyBody(BaselineRecord record)
    {
        var response = record.Response;
        byte[] body;
        if (response.BodyFile is not null)
        {
            if (response.BodyText is not null)
            {
                throw new InvalidDataException("it holds both bodyText and bodyFile.");
            }

            var bodyPath = GetBodyFilePath(record);
            if (!TryReadAllBytes(bodyPath, out var fileBytes))
            {
                throw new InvalidDataException($"its body file {Display(bodyPath)} is missing.");
            }

            body = fileBytes;
        }
        else
        {
            body = Encoding.UTF8.GetBytes(response.BodyText ?? "");
        }

        var sha256 = BaselineResponse.ComputeSha256Hex(body);
        if (!string.Equals(sha256, response.BodySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"its body hashes to {sha256}, not to the recorded bodySha256 {response.BodySha256}.");
        }
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
        var fullPath = GuardWritePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private string GuardWritePath(string path)
    {
        var baselineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BaselineDirectory))
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(baselineRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"BaselineStore never writes outside baseline/: refused '{fullPath}' (baseline is '{baselineRoot}').");
        }

        return fullPath;
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
}

