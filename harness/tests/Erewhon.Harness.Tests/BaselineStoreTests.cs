using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Baseline persistence against real files: every test owns a fresh temporary repository root, writes and
/// reads it only through <see cref="BaselineStore"/>, and checks file names, byte-exact and deterministic
/// output, verified reads, the refusal of inconsistent, malformed and damaged records, and physical
/// containment: no write or delete reaches outside <c>baseline/</c> through a symbolic link, whether the link
/// sits on <c>baseline/</c> itself, on a directory below it or at the target, dangles, or is swapped in for a
/// directory while the store works. Links point into a simulated <c>Erewhon/DataFiles</c> inside the
/// temporary root; the repository's own tree is never touched.
/// </summary>
public sealed class BaselineStoreTests : IDisposable
{
    private const string RestRecordPath = "baseline/records/rest/001-getAgent-01.json";
    private const string PhotoRecordPath = "baseline/records/soap/002-getPhoto-02.json";
    private const string PhotoBodyPath = "baseline/records/soap/002-getPhoto-02.body.bin";
    private const string ManifestFile = "baseline/manifest.json";
    private const string RulesFile = "baseline/normalisation-rules.json";
    private const string ConformanceFile = "baseline/conformance.json";
    private const string WsdlFile = "baseline/wsdl/standin-ErewhonWebService.wsdl";

    private const string JsonContentType = "application/json; charset=utf-8";
    private const string SoapContentType = "text/xml; charset=utf-8";
    private const string SoapPath = "/jade/jadehttp.dll?WebServiceHTTP&serviceName=ErewhonWebService&listName=WebServiceHTTP";
    private const string SeedSha256 = "5d41402abc4b2a76b9719d911017c592ae0e8f6b1a6cd0b1e6a0e7c5f8b1c9d2";
    private const string StartingCommit = "19a5189e9075349aca54de1fb5fdf2d8727403e7";

    private const string LinkRefusal = "BaselineStore never writes or deletes through a link";
    private const string SentinelName = "Clients.txt";
    private const string ProtectedPath = "Erewhon/DataFiles";
    private const string SentinelPath = ProtectedPath + "/" + SentinelName;

    // Rounds of every write and delete the parent-swap test runs while its directories are swapped.
    private const int SwapIterations = 300;

    private static readonly string[] DocumentNames =
        ["record", "body-file", "manifest", "normalisation-rules", "conformance", "wsdl"];

    private static readonly string[] ReaderNames = ["ReadRecords", "ReadRecordsAsync", "ReadEntries", "ReadEntriesAsync"];

    // The directories the parent-swap test replaces with links, and the protected files an escape through
    // them would replace or delete.
    private static readonly string[] SwappedDirectories = ["baseline/records/rest", "baseline/wsdl"];

    private static readonly string[] SwapSentinelNames = ["001-getAgent-01.json", "standin-ErewhonWebService.wsdl"];

    private readonly string _root;
    private readonly HarnessOptions _options;
    private readonly BaselineStore _store;

    public BaselineStoreTests()
    {
        // The run sets TMPDIR to a per-clone directory, so parallel checkouts never share a root.
        _root = Path.Combine(Path.GetTempPath(), "erewhon-baseline-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _options = new HarnessOptions { RepoRoot = _root };
        _store = new BaselineStore(_options);
    }

    /// <summary>The four record readers, which share every check.</summary>
    public static TheoryData<string> Readers { get; } = [.. ReaderNames];

    /// <summary>
    /// Each damage to a written baseline, the file the error must name and the reason it must give, for every
    /// record reader.
    /// </summary>
    public static TheoryData<string, string, string, string> DamagedRecords()
    {
        (string Damage, string File, string Reason)[] damages =
        [
            ("tampered-body-file", PhotoRecordPath, "is invalid: its body hashes to"),
            ("tampered-body-text", RestRecordPath, "is invalid: its body hashes to"),
            ("missing-body-file", PhotoRecordPath, $"is invalid: its body file {PhotoBodyPath} is missing."),
            ("malformed-json", RestRecordPath, "is not a valid BaselineRecord document"),
            ("json-null", RestRecordPath, "holds a JSON null instead of a BaselineRecord"),
            ("missing-case-id", RestRecordPath, "is invalid: member caseId is missing."),
            ("missing-request-headers", RestRecordPath, "is invalid: member request.headers is missing."),
            ("missing-body-sha", PhotoRecordPath, "is invalid: member response.bodySha256 is missing."),
            ("wrong-surface-directory", "baseline/records/soap/001-getAgent-01.json",
                "belongs in records/rest, not records/soap"),
            ("wrong-file-name", "baseline/records/rest/007-getAgent-01.json",
                "must be stored as 001-getAgent-01.json"),
            ("duplicate-order", "baseline/records/rest/001-getClient-01.json",
                $"repeats replay position 1 of {RestRecordPath}"),
            ("duplicate-case-id", "baseline/records/rest/003-getAgent-01.json",
                $"repeats case REST-getAgent-01 of {RestRecordPath}"),
            ("text-and-file", PhotoRecordPath, "is invalid: it holds both bodyText and bodyFile."),
            ("foreign-body-file", PhotoRecordPath, "names body file '002-getPhoto-03.body.bin'"),
        ];

        var data = new TheoryData<string, string, string, string>();
        foreach (var (damage, file, reason) in damages)
        {
            foreach (var reader in ReaderNames)
            {
                data.Add(damage, reader, file, reason);
            }
        }

        return data;
    }

    /// <summary>Each damage <see cref="BaselineStore.ReadBodyBytes"/> must detect, in both forms.</summary>
    public static TheoryData<string, bool, string> DamagedBodies()
    {
        (string Damage, string Message)[] damages =
        [
            ("tampered-body-file", "Baseline case SOAP-getPhoto-02 is invalid: its body hashes to"),
            ("missing-body-file", $"Baseline case SOAP-getPhoto-02 is invalid: its body file {PhotoBodyPath} is missing."),
            ("tampered-body-text", "Baseline case REST-getAgent-01 is invalid: its body hashes to"),
            ("text-and-file", "Baseline case SOAP-getPhoto-02 holds both bodyText and bodyFile"),
            ("foreign-body-file", "Baseline case SOAP-getPhoto-02 names body file '002-getPhoto-03.body.bin'"),
        ];

        var data = new TheoryData<string, bool, string>();
        foreach (var (damage, message) in damages)
        {
            data.Add(damage, false, message);
            data.Add(damage, true, message);
        }

        return data;
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    // A BOM, non-ASCII text, markup characters and a trailing blank: every byte must survive as bodyText.
    private static byte[] AgentBody =>
        [.. Encoding.UTF8.Preamble, .. """{"name":"Hank Williams","city":"Tāmaki <Makaurau>","fax":"64 3 923 1010 "}"""u8];

    // Not valid UTF-8, with a CR LF pair, so only a body file can hold it.
    private static byte[] PhotoBody =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x0D, 0x0A, 0xFF, 0xD9];

    private static byte[] SoapBody => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body/></soap:Envelope>");

    // A BOM, CR LF and no final newline: the WSDL is stored exactly as served.
    private static byte[] WsdlBytes =>
    [
        .. Encoding.UTF8.Preamble,
        .. "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"u8,
        .. "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>"u8,
    ];

    private static byte[] SentinelBytes => "Hank Williams\t1 Main St\tWellington\n"u8.ToArray();

    private static BaselineRecord AgentRecord => NewRecord(1, "REST-getAgent-01", "Rest.getAgent", AgentBody);

    private static BaselineRecord PhotoRecord =>
        NewRecord(2, "SOAP-getPhoto-02", "Soap.getPhoto", PhotoBody, "002-getPhoto-02.body.bin");

    private static BaselineManifest Manifest => new()
    {
        StandIn = new StandInInfo(SeedSha256, "2026-01-01", 2),
        SeedSha256 = SeedSha256,
        ReferenceDate = "2026-01-01",
        Counts = new ManifestCounts(22, 56, 22, 55),
        NotCaptured = ["SOAP-getPhoto-03"],
        WsdlCaptured = true,
        StartingCommit = StartingCommit,
        CaptureCommit = "0123456789abcdef0123456789abcdef01234567",
        CaptureExecution = CaptureExecutionValues.LocalProcesses,
    };

    private string ProtectedDirectory => Full(ProtectedPath);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- File names ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "getAgent", "01", "001-getAgent-01")]
    [InlineData(42, "postCreateTender", "3", "042-postCreateTender-3")]
    [InlineData(999, "getPhoto", "12", "999-getPhoto-12")]
    public void FileNames_AreZeroPaddedPositionMethodAndCaseNumber(
        int position, string jadeMethod, string caseNumber, string stem)
    {
        Assert.Equal(stem + ".json", BaselineStore.RecordFileName(position, jadeMethod, caseNumber));
        Assert.Equal(stem + ".body.bin", BaselineStore.BodyFileName(position, jadeMethod, caseNumber));
    }

    [Theory]
    [InlineData(0, "getAgent", "01", "position")]
    [InlineData(-1, "getAgent", "01", "position")]
    [InlineData(1000, "getAgent", "01", "position")]
    [InlineData(1, "../x", "01", "jadeMethod")]
    [InlineData(1, "get Agent", "01", "jadeMethod")]
    [InlineData(1, "getAgént", "01", "jadeMethod")]
    [InlineData(1, "", "01", "jadeMethod")]
    [InlineData(1, "getAgent", "0a", "caseNumber")]
    [InlineData(1, "getAgent", "01/..", "caseNumber")]
    [InlineData(1, "getAgent", "\u0661", "caseNumber")]
    [InlineData(1, "getAgent", "", "caseNumber")]
    public void FileNames_RejectOutOfRangeOrUnsafeParts(int position, string jadeMethod, string caseNumber, string parameter)
    {
        var recordError = Assert.ThrowsAny<ArgumentException>(
            () => BaselineStore.RecordFileName(position, jadeMethod, caseNumber));
        var bodyError = Assert.ThrowsAny<ArgumentException>(
            () => BaselineStore.BodyFileName(position, jadeMethod, caseNumber));

        Assert.Equal(parameter, recordError.ParamName);
        Assert.Equal(parameter, bodyError.ParamName);
    }

    [Theory]
    [InlineData("REST-getAgent-01", "rest")]
    [InlineData("SOAP-getPhoto-02", "soap")]
    public void SurfaceDirectoryName_FollowsCaseIdPrefix(string caseId, string expected)
    {
        Assert.Equal(expected, BaselineStore.SurfaceDirectoryName(caseId));
    }

    [Theory]
    [InlineData("Rest-getAgent-01")]
    [InlineData("soap-getPhoto-02")]
    [InlineData("")]
    [InlineData(null)]
    public void SurfaceDirectoryName_RejectsAnyOtherPrefix(string? caseId)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => BaselineStore.SurfaceDirectoryName(caseId!));

        Assert.Equal("caseId", ex.ParamName);
    }

    [Fact]
    public void RecordPaths_SitInTheSurfaceDirectory()
    {
        Assert.Equal(PhotoRecordPath, Relative(_store.GetRecordPath(PhotoRecord)));
        Assert.Equal(PhotoBodyPath, Relative(_store.GetBodyFilePath(PhotoRecord)));
        Assert.Equal(RestRecordPath, Relative(_store.GetRecordPath(AgentRecord)));
        Assert.Equal("record", Assert.Throws<ArgumentException>(() => _store.GetBodyFilePath(AgentRecord)).ParamName);
    }

    // ---- Round trip and file format -----------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrittenDocuments_ReadBackUnchanged(bool useAsync)
    {
        await WriteFixture(_store, useAsync);

        var records = useAsync ? await _store.ReadRecordsAsync(TestCancellation) : _store.ReadRecords();
        var manifest = useAsync ? await _store.TryReadManifestAsync(TestCancellation) : ReadManifest();
        var rulesText = useAsync
            ? await _store.ReadNormalisationRulesAsync(TestCancellation)
            : _store.ReadNormalisationRules();
        var rules = useAsync
            ? await NormalisationRules.LoadAsync(_options.NormalisationRulesPath, TestCancellation)
            : NormalisationRules.Load(_options.NormalisationRulesPath);
        var conformance = useAsync
            ? await _store.TryReadConformanceAsync(TestCancellation)
            : _store.TryReadConformance();
        var wsdl = useAsync ? await _store.ReadWsdlBytesAsync(TestCancellation) : _store.ReadWsdlBytes();

        Assert.Equal([Text(AgentRecord), Text(PhotoRecord)], records.Select(static record => Text(record)));
        Assert.NotNull(manifest);
        Assert.Equal(Text(Manifest), Text(manifest));
        Assert.Equal(["SOAP-getPhoto-03"], manifest.NotCaptured);
        Assert.NotNull(rulesText);
        Assert.Equal(RulesNode().ToJsonString(), NormalisationRules.Parse(rulesText).ToJsonNode().ToJsonString());
        Assert.Equal(RulesNode().ToJsonString(), rules.ToJsonNode().ToJsonString());
        Assert.NotNull(conformance);
        Assert.Equal(ConformanceNode().ToJsonString(), conformance.ToJsonString());
        Assert.Equal(WsdlBytes, wsdl);
        Assert.True(_store.WsdlExists);
    }

    [Theory]
    [InlineData(RestRecordPath)]
    [InlineData(PhotoRecordPath)]
    [InlineData(ManifestFile)]
    [InlineData(RulesFile)]
    [InlineData(ConformanceFile)]
    public async Task JsonFiles_AreUtf8WithoutBomWithLfOnlyAndOneTrailingLf(string file)
    {
        await WriteFixture(_store, useAsync: false);

        var bytes = await File.ReadAllBytesAsync(Full(file), TestCancellation);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(BaselineResponse.IsTextRoundTrippable(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public async Task RecordAndRawFiles_HoldExactlyTheirBytes()
    {
        await WriteFixture(_store, useAsync: false);

        Assert.Equal(BaselineStore.SerializeDocument(AgentRecord), await ReadBytes(RestRecordPath));
        Assert.Equal(BaselineStore.SerializeDocument(Manifest), await ReadBytes(ManifestFile));
        Assert.Equal(PhotoBody, await ReadBytes(PhotoBodyPath));
        Assert.Equal(WsdlBytes, await ReadBytes(WsdlFile));
    }

    [Fact]
    public async Task Rewrite_AndAsyncWrite_ProduceByteIdenticalTrees()
    {
        var syncRoot = Full("sync");
        var asyncRoot = Full("async");
        var syncStore = new BaselineStore(new HarnessOptions { RepoRoot = syncRoot });
        var asyncStore = new BaselineStore(new HarnessOptions { RepoRoot = asyncRoot });

        await WriteFixture(syncStore, useAsync: false);
        var first = Snapshot(syncRoot);
        await WriteFixture(syncStore, useAsync: false);
        await WriteFixture(asyncStore, useAsync: true);

        Assert.Equal(7, first.Count);
        Assert.Equal(first, Snapshot(syncRoot));
        Assert.Equal(first, Snapshot(asyncRoot));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Readers_ReturnReplayOrderAcrossSurfaces(string reader)
    {
        _store.WriteRecord(NewRecord(3, "REST-getClient-01", "Rest.getClient", AgentBody), AgentBody);
        _store.WriteRecord(
            NewRecord(1, "SOAP-getPhoto-01", "Soap.getPhoto", PhotoBody, "001-getPhoto-01.body.bin"), PhotoBody);
        _store.WriteRecord(NewRecord(2, "REST-getAgent-02", "Rest.getAgent", AgentBody), AgentBody);
        _store.WriteRecord(NewRecord(4, "SOAP-getAgentNames-01", "Soap.getAgentNames", SoapBody), SoapBody);

        var records = await ReadWith(reader);

        Assert.Equal([1, 2, 3, 4], records.Select(static record => record.Order));
        Assert.Equal(
            ["SOAP-getPhoto-01", "REST-getAgent-02", "REST-getClient-01", "SOAP-getAgentNames-01"],
            records.Select(static record => record.CaseId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntriesAndBodyBytes_AreTheVerifiedRawBodies(bool useAsync)
    {
        await WriteFixture(_store, useAsync: false);

        var entries = useAsync ? await _store.ReadEntriesAsync(TestCancellation) : _store.ReadEntries();

        Assert.Equal(2, entries.Count);
        var (agent, photo) = (entries[0], entries[1]);
        Assert.Equal(AgentBody, agent.Body.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(agent.Record.Response.BodyText!), agent.Body.ToArray());
        Assert.Equal(PhotoBody, photo.Body.ToArray());
        Assert.Null(photo.Record.Response.BodyText);
        foreach (var entry in entries)
        {
            var body = useAsync
                ? await _store.ReadBodyBytesAsync(entry.Record, TestCancellation)
                : _store.ReadBodyBytes(entry.Record);
            Assert.Equal(entry.Body.ToArray(), body);
        }
    }

    [Fact]
    public async Task AbsentBaseline_ReadsAsAbsentAndCreatesNothing()
    {
        Assert.False(_store.TryReadManifest(out var manifest));
        Assert.Null(manifest);
        Assert.Null(await _store.TryReadManifestAsync(TestCancellation));
        Assert.Null(_store.TryReadConformance());
        Assert.Null(await _store.TryReadConformanceAsync(TestCancellation));
        Assert.Null(_store.ReadNormalisationRules());
        Assert.Null(await _store.ReadNormalisationRulesAsync(TestCancellation));
        Assert.Null(_store.ReadWsdlBytes());
        Assert.Null(await _store.ReadWsdlBytesAsync(TestCancellation));
        Assert.False(_store.WsdlExists);
        Assert.Empty(_store.ReadRecords());
        Assert.Empty(await _store.ReadRecordsAsync(TestCancellation));
        Assert.Empty(_store.ReadEntries());
        Assert.Empty(await _store.ReadEntriesAsync(TestCancellation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    // ---- Write refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task WriteRecord_NotLabelledStandIn_IsRefusedAndWritesNothing()
    {
        var record = NewRecord(1, "REST-getAgent-01", "Rest.getAgent", AgentBody, source: "live-jade");

        var ex = Assert.Throws<InvalidOperationException>(() => _store.WriteRecord(record, AgentBody));
        var asyncEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.WriteRecordAsync(record, AgentBody, TestCancellation));

        Assert.Contains("every record must be labelled 'jade-standin'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("sha-mismatch", "states bodySha256")]
    [InlineData("text-and-file", "holds both bodyText and bodyFile")]
    [InlineData("foreign-body-file", "names body file '001-getPhoto-02.body.bin'")]
    [InlineData("lossy-text", "bodyText does not reproduce the raw body byte for byte")]
    [InlineData("edited-text", "bodyText does not reproduce the raw body byte for byte")]
    [InlineData("surface-mismatch", "name different surfaces")]
    [InlineData("method-mismatch", "does not name the method 'getAgent'")]
    public async Task WriteRecord_InconsistentRecord_IsInvalidDataAndWritesNothing(string defect, string reason)
    {
        var (record, rawBody) = InconsistentRecord(defect);

        var ex = Assert.Throws<InvalidDataException>(() => _store.WriteRecord(record, rawBody));
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.WriteRecordAsync(record, rawBody, TestCancellation));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    // A method name that climbs out of baseline/ is refused before any path is formed from it.
    [Theory]
    [InlineData("path-traversal", "jadeMethod")]
    [InlineData("order-zero", "position")]
    [InlineData("order-1000", "position")]
    [InlineData("no-request", "record")]
    [InlineData("null-record", "record")]
    public async Task WriteRecord_MalformedRecord_IsArgumentErrorAndWritesNothing(string defect, string parameter)
    {
        var record = MalformedRecord(defect);

        var ex = Assert.ThrowsAny<ArgumentException>(() => _store.WriteRecord(record, AgentBody));
        var asyncEx = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _store.WriteRecordAsync(record, AgentBody, TestCancellation));

        Assert.Equal(parameter, ex.ParamName);
        Assert.Equal(parameter, asyncEx.ParamName);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    // ---- Read refusals ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DamagedRecords))]
    public async Task Readers_DamagedRecord_IsInvalidDataNamingTheFile(
        string damage, string reader, string file, string reason)
    {
        await WriteFixture(_store, useAsync: false);
        await Damage(damage);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadWith(reader));

        Assert.Contains(file, ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DamagedBodies))]
    public async Task ReadBodyBytes_DamagedBody_IsInvalidData(string damage, bool useAsync, string message)
    {
        await WriteFixture(_store, useAsync: false);
        var record = await DamagedBodyRecord(damage);

        var ex = useAsync
            ? await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadBodyBytesAsync(record, TestCancellation))
            : Assert.Throws<InvalidDataException>(() => _store.ReadBodyBytes(record));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBodyBytes_RecordWithoutResponse_IsArgumentError()
    {
        var record = AgentRecord with { Response = null! };

        var ex = Assert.Throws<ArgumentException>(() => _store.ReadBodyBytes(record));
        var asyncEx = await Assert.ThrowsAsync<ArgumentException>(
            () => _store.ReadBodyBytesAsync(record, TestCancellation));

        Assert.Equal("record", ex.ParamName);
        Assert.Equal("record", asyncEx.ParamName);
    }

    [Theory]
    [InlineData("{", "is not a valid BaselineManifest document")]
    [InlineData("[]", "is not a valid BaselineManifest document")]
    [InlineData("""{"source":"jade-standin",}""", "is not a valid BaselineManifest document")]
    [InlineData("""{"counts":{"plannedCases":"56"}}""", "is not a valid BaselineManifest document")]
    [InlineData("null", "holds a JSON null instead of a BaselineManifest")]
    public async Task TryReadManifest_MalformedFile_IsInvalidDataNamingTheFile(string json, string reason)
    {
        await WriteRaw(ManifestFile, Encoding.UTF8.GetBytes(json));

        var ex = Assert.Throws<InvalidDataException>(() => _store.TryReadManifest(out _));
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.TryReadManifestAsync(TestCancellation));

        Assert.StartsWith(ManifestFile + " ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
    }

    [Fact]
    public async Task TryReadManifest_HandWrittenFileWithByteOrderMark_Parses()
    {
        const string json = """
            {
              "captureExecution": "not-run",
              "counts": { "plannedOperations": 22, "plannedCases": 56 },
              "notCaptured": ["REST-getAgent-01", "SOAP-getPhoto-02"],
              "wsdlCaptured": false,
              "startingCommit": "19a5189e9075349aca54de1fb5fdf2d8727403e7",
              "blockingError": "capture could not start"
            }
            """;
        await WriteRaw(ManifestFile, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(json)]);

        Assert.True(_store.TryReadManifest(out var manifest));
        var asyncManifest = await _store.TryReadManifestAsync(TestCancellation);

        Assert.True(manifest.IsNotRun);
        Assert.False(manifest.HasSeedFacts);
        Assert.Equal(["REST-getAgent-01", "SOAP-getPhoto-02"], manifest.NotCaptured);
        Assert.Equal(new ManifestCounts(22, 56, 0, 0), manifest.Counts);
        Assert.Equal("capture could not start", manifest.BlockingError);
        Assert.NotNull(asyncManifest);
        Assert.Equal(Text(manifest), Text(asyncManifest));
    }

    [Theory]
    [InlineData("null", "holds a JSON null instead of a document")]
    [InlineData("{", "is not valid JSON")]
    [InlineData("""{"operations":22,}""", "is not valid JSON")]
    public async Task TryReadConformance_NullOrMalformedFile_IsInvalidDataNamingTheFile(string json, string reason)
    {
        await WriteRaw(ConformanceFile, Encoding.UTF8.GetBytes(json));

        var ex = Assert.Throws<InvalidDataException>(() => _store.TryReadConformance());
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.TryReadConformanceAsync(TestCancellation));

        Assert.StartsWith(ConformanceFile + " ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
    }

    // ---- Clearing records and deleting the WSDL -----------------------------------------------------

    [Fact]
    public async Task ClearRecords_DeletesEveryFileInBothSurfaceDirectoriesAndKeepsTheRest()
    {
        await WriteFixture(_store, useAsync: false);
        await WriteRaw("baseline/records/soap/009-getPhoto-09.body.bin", PhotoBody);
        await WriteRaw("baseline/records/rest/notes.txt", "stray"u8.ToArray());
        await WriteRaw("baseline/records/rest/kept/inner.json", "{}"u8.ToArray());
        await WriteRaw("baseline/records/index.txt", "kept"u8.ToArray());
        var kept = Snapshot(Full("baseline"))
            .Where(static entry => !IsDirectlyIn(entry, "records/rest") && !IsDirectlyIn(entry, "records/soap"))
            .ToList();

        _store.ClearRecords(TestCancellation);

        Assert.Empty(Directory.GetFiles(Full("baseline/records/rest")));
        Assert.Empty(Directory.GetFiles(Full("baseline/records/soap")));
        Assert.Equal(6, kept.Count);
        Assert.Equal(kept, Snapshot(Full("baseline")));
    }

    [Fact]
    public void ClearRecords_MissingSurfaceDirectories_AreCreatedEmpty()
    {
        _store.ClearRecords(TestCancellation);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("baseline/records/rest")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("baseline/records/soap")));
    }

    [Fact]
    public void DeleteWsdl_RemovesTheFileAndIsANoOpWhenAbsent()
    {
        _store.DeleteWsdl();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));

        _store.WriteManifest(Manifest);
        _store.WriteWsdl(WsdlBytes);
        _store.DeleteWsdl();

        Assert.False(_store.WsdlExists);
        Assert.False(File.Exists(Full(WsdlFile)));
        Assert.True(File.Exists(Full(ManifestFile)));
    }

    // ---- Containment through links ------------------------------------------------------------------

    [Fact]
    public async Task LinkedSurfaceDirectory_RefusesClearAndWrite()
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline/records"));
        Directory.CreateSymbolicLink(Full("baseline/records/rest"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline/records/rest");
        var before = Snapshot(ProtectedDirectory);

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => _store.ClearRecords(TestCancellation)),
            "baseline/records/rest");
        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => _store.WriteRecord(AgentRecord, AgentBody)),
            "baseline/records/rest");
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _store.WriteRecordAsync(AgentRecord, AgentBody, TestCancellation)),
            "baseline/records/rest");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    [Fact]
    public async Task LinkedRecordEntry_RefusesClearAndDeletesNothing()
    {
        await WriteFixture(_store, useAsync: false);
        CreateProtectedDirectory();
        const string link = "baseline/records/rest/005-getAgent-05.json";
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        File.CreateSymbolicLink(Full(link), target);
        var before = Snapshot(_root);

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => _store.ClearRecords(TestCancellation)), link);

        Assert.Equal(before, Snapshot(_root));
        Assert.Equal(SentinelPath, LinkTargetOf(link));
    }

    [Fact]
    public async Task LinkedManifest_RefusesSyncAndAsyncWrites()
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline"));
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        File.CreateSymbolicLink(Full(ManifestFile), target);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => _store.WriteManifest(Manifest)), ManifestFile);
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _store.WriteManifestAsync(Manifest, TestCancellation)),
            ManifestFile);

        Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(target, TestCancellation));
        Assert.Equal(SentinelPath, LinkTargetOf(ManifestFile));
    }

    [Fact]
    public async Task LinkedWsdlDirectory_RefusesWriteAndDelete()
    {
        CreateProtectedDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(ProtectedDirectory, "standin-ErewhonWebService.wsdl"), SentinelBytes, TestCancellation);
        Directory.CreateDirectory(Full("baseline"));
        Directory.CreateSymbolicLink(Full("baseline/wsdl"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline/wsdl");
        var before = Snapshot(ProtectedDirectory);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => _store.WriteWsdl(WsdlBytes)), "baseline/wsdl");
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _store.WriteWsdlAsync(WsdlBytes, TestCancellation)),
            "baseline/wsdl");
        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => _store.DeleteWsdl()), "baseline/wsdl");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("body-file")]
    [InlineData("manifest")]
    [InlineData("normalisation-rules")]
    [InlineData("conformance")]
    [InlineData("wsdl")]
    public async Task DanglingLinkAtWriteTarget_IsRefusedAndItsTargetNeverCreated(string document)
    {
        CreateProtectedDirectory();
        var writer = Writer(document);
        var link = Full(writer.File);
        var target = Path.Combine(ProtectedDirectory, "written-through-link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => writer.Write(_store)), writer.File);
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(_store, TestCancellation)),
            writer.File);

        Assert.False(File.Exists(target));
        Assert.Equal(ProtectedPath + "/written-through-link", LinkTargetOf(writer.File));
        Assert.Equal(
            [writer.File], Directory.EnumerateFiles(Full("baseline"), "*", SearchOption.AllDirectories).Select(Relative));
    }

    [Fact]
    public async Task LinkedBaselineDirectory_RefusesEveryWriteAndDelete()
    {
        CreateProtectedDirectory();
        Directory.CreateSymbolicLink(Full("baseline"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline");
        var before = Snapshot(ProtectedDirectory);

        foreach (var document in DocumentNames)
        {
            var writer = Writer(document);
            AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => writer.Write(_store)), "baseline");
            AssertLinkRefusal(
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(_store, TestCancellation)),
                "baseline");
        }

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => _store.ClearRecords(TestCancellation)), "baseline");
        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => _store.DeleteWsdl()), "baseline");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    // A hard link is no reparse point, so the write is allowed; it must replace the link, never write through it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardLinkAtManifest_IsReplacedWithoutChangingItsOtherName(bool useAsync)
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline"));
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        CreateHardLink(Full(ManifestFile), target);
        await File.AppendAllTextAsync(Full(ManifestFile), "linked\n", TestCancellation);
        var before = await File.ReadAllBytesAsync(target, TestCancellation);
        Assert.Equal([.. SentinelBytes, .. "linked\n"u8], before);

        if (useAsync)
        {
            await _store.WriteManifestAsync(Manifest, TestCancellation);
        }
        else
        {
            _store.WriteManifest(Manifest);
        }

        Assert.Equal(BaselineStore.SerializeDocument(Manifest), await ReadBytes(ManifestFile));
        Assert.Equal(before, await File.ReadAllBytesAsync(target, TestCancellation));
    }

    // A directory swapped for a link while the store works must never redirect it. A background thread keeps
    // moving baseline/records/rest and baseline/wsdl aside, putting a link to the protected directory in each
    // place and moving the directory back, while every write and delete runs against the moving tree. Which
    // operations succeed and which are refused depends on timing, so only that each one succeeds or is
    // refused, and that the protected directory is unchanged, is asserted.
    [Fact]
    public async Task ConcurrentParentSwap_NeverWritesOrDeletesThroughTheSwappedInLink()
    {
        CreateProtectedDirectory();
        foreach (var name in SwapSentinelNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(ProtectedDirectory, name), SentinelBytes, TestCancellation);
        }

        await WriteFixture(_store, useAsync: false);
        var before = Snapshot(ProtectedDirectory);
        Func<Task>[] operations =
        [
            () => Synchronously(() => _store.WriteRecord(AgentRecord, AgentBody)),
            () => _store.WriteRecordAsync(AgentRecord, AgentBody, TestCancellation),
            () => Synchronously(() => _store.ClearRecords(TestCancellation)),
            () => Synchronously(() => _store.WriteWsdl(WsdlBytes)),
            () => _store.WriteWsdlAsync(WsdlBytes, TestCancellation),
            () => Synchronously(_store.DeleteWsdl),
        ];

        Exception? swapFailure = null;
        using var stop = new CancellationTokenSource();
        var swapper = new Thread(() => swapFailure = SwapForLinks(stop.Token)) { IsBackground = true };
        swapper.Start();
        try
        {
            for (var iteration = 0; iteration < SwapIterations; iteration++)
            {
                foreach (var operation in operations)
                {
                    AssertSucceededOrRefused(await Outcome(operation));
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            swapper.Join();
            RemoveSwappedInLinks();
        }

        Assert.Null(swapFailure);
        Assert.Equal(3, before.Count);
        Assert.Equal(before, Snapshot(ProtectedDirectory));
        _store.WriteRecord(AgentRecord, AgentBody);
        Assert.Equal(BaselineStore.SerializeDocument(AgentRecord), await ReadBytes(RestRecordPath));
    }

    // ---- Cancellation -------------------------------------------------------------------------------

    [Theory]
    [InlineData("WriteRecordAsync")]
    [InlineData("WriteManifestAsync")]
    [InlineData("WriteNormalisationRulesAsync")]
    [InlineData("WriteConformanceAsync")]
    [InlineData("WriteWsdlAsync")]
    [InlineData("TryReadManifestAsync")]
    [InlineData("ReadRecordsAsync")]
    [InlineData("ReadEntriesAsync")]
    [InlineData("ReadBodyBytesAsync:file")]
    [InlineData("ReadBodyBytesAsync:text")]
    [InlineData("ReadNormalisationRulesAsync")]
    [InlineData("TryReadConformanceAsync")]
    [InlineData("ReadWsdlBytesAsync")]
    [InlineData("ClearRecords")]
    [InlineData("NormalisationRules.LoadAsync")]
    public async Task PreCancelledToken_CancelsAndLeavesTheTreeUnchanged(string api)
    {
        await WriteFixture(_store, useAsync: false);
        var before = Snapshot(_root);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancellableCall(api, cancellation.Token));

        Assert.Equal(before, Snapshot(_root));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static BaselineRecord NewRecord(
        int order,
        string caseId,
        string operation,
        byte[] body,
        string? bodyFile = null,
        string source = SourceLabels.StandIn)
    {
        var soap = operation.StartsWith("Soap.", StringComparison.Ordinal);
        var request = soap
            ? new BaselineRequest(
                "POST",
                SoapPath,
                new Dictionary<string, string>
                {
                    ["Content-Type"] = SoapContentType,
                    ["SOAPAction"] = "\"urn:JadeWebServices/WebServiceHTTP/" + operation["Soap.".Length..] + "\"",
                },
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body/></soap:Envelope>")
            : new BaselineRequest(
                "GET", "/agent/Hank%20Williams", new Dictionary<string, string> { ["Accept"] = "application/json" }, null);
        return new BaselineRecord(
            order,
            caseId,
            operation,
            "success",
            false,
            "seed",
            source,
            request,
            BaselineResponse.FromRawBody(200, soap ? SoapContentType : JsonContentType, body, bodyFile));
    }

    private static (BaselineRecord Record, byte[] RawBody) InconsistentRecord(string defect) => defect switch
    {
        "sha-mismatch" => (AgentRecord, SoapBody),
        "text-and-file" => (PhotoRecord with { Response = PhotoRecord.Response with { BodyText = "photo" } }, PhotoBody),
        "foreign-body-file" => (
            NewRecord(2, "SOAP-getPhoto-02", "Soap.getPhoto", PhotoBody, "001-getPhoto-02.body.bin"), PhotoBody),
        "lossy-text" => (NewRecord(1, "REST-getAgent-01", "Rest.getAgent", PhotoBody), PhotoBody),
        "edited-text" => (AgentRecord with { Response = AgentRecord.Response with { BodyText = "{}" } }, AgentBody),
        "surface-mismatch" => (NewRecord(1, "REST-getAgent-01", "Soap.getAgent", AgentBody), AgentBody),
        "method-mismatch" => (NewRecord(1, "REST-getClient-01", "Rest.getAgent", AgentBody), AgentBody),
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null),
    };

    // path-traversal names a method that, were it accepted, would place the record at <root>/escaped-01.json.
    private static BaselineRecord MalformedRecord(string defect) => defect switch
    {
        "path-traversal" => NewRecord(
            1, "REST-/../../../../escaped-01", "Rest./../../../../escaped", AgentBody),
        "order-zero" => AgentRecord with { Order = 0 },
        "order-1000" => AgentRecord with { Order = 1000 },
        "no-request" => AgentRecord with { Request = null! },
        "null-record" => null!,
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null),
    };

    private static JsonNode RulesNode() => NormalisationRules.FromCatalogue(OperationCatalogue.Default).ToJsonNode();

    private static JsonNode ConformanceNode() => JsonNode.Parse(
        """{"operations":22,"gaps":[{"class":"DTOTenderSaleDetail","property":"tenderDate"}],"note":"<é>"}""")!;

    private static DocumentWriter Writer(string document) => document switch
    {
        "record" => new(
            RestRecordPath,
            static store => store.WriteRecord(AgentRecord, AgentBody),
            static (store, cancellationToken) => store.WriteRecordAsync(AgentRecord, AgentBody, cancellationToken)),
        "body-file" => new(
            PhotoBodyPath,
            static store => store.WriteRecord(PhotoRecord, PhotoBody),
            static (store, cancellationToken) => store.WriteRecordAsync(PhotoRecord, PhotoBody, cancellationToken)),
        "manifest" => new(
            ManifestFile,
            static store => store.WriteManifest(Manifest),
            static (store, cancellationToken) => store.WriteManifestAsync(Manifest, cancellationToken)),
        "normalisation-rules" => new(
            RulesFile,
            static store => store.WriteNormalisationRules(RulesNode()),
            static (store, cancellationToken) => store.WriteNormalisationRulesAsync(RulesNode(), cancellationToken)),
        "conformance" => new(
            ConformanceFile,
            static store => store.WriteConformance(ConformanceNode()),
            static (store, cancellationToken) => store.WriteConformanceAsync(ConformanceNode(), cancellationToken)),
        "wsdl" => new(
            WsdlFile,
            static store => store.WriteWsdl(WsdlBytes),
            static (store, cancellationToken) => store.WriteWsdlAsync(WsdlBytes, cancellationToken)),
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, null),
    };

    // Writes the two records (the REST one with its body as text, the getPhoto one with a body file), the
    // manifest, the normalisation rules, the conformance result and the WSDL.
    private static async Task WriteFixture(BaselineStore store, bool useAsync)
    {
        foreach (var document in DocumentNames)
        {
            var writer = Writer(document);
            if (useAsync)
            {
                await writer.WriteAsync(store, TestCancellation);
            }
            else
            {
                writer.Write(store);
            }
        }
    }

    private static string Text<T>(T document) => Encoding.UTF8.GetString(BaselineStore.SerializeDocument(document));

    private static void AssertLinkRefusal(InvalidOperationException refusal, string linkedComponent)
    {
        Assert.Contains(LinkRefusal, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"because '{linkedComponent}' is a symbolic link or reparse point", refusal.Message, StringComparison.Ordinal);
    }

    // Every regular file below a directory, as "relative/path=hex" in ordinal order.
    private static List<string> Snapshot(string directory) =>
    [
        .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')
                + "=" + Convert.ToHexStringLower(File.ReadAllBytes(file)))
            .Order(StringComparer.Ordinal),
    ];

    private static bool IsDirectlyIn(string entry, string directory)
    {
        var path = entry[..entry.IndexOf('=', StringComparison.Ordinal)];
        return path.StartsWith(directory + "/", StringComparison.Ordinal)
            && path.IndexOf('/', directory.Length + 1) < 0;
    }

    // .NET has no hard-link API, so ln(1) creates it.
    private static void CreateHardLink(string link, string existingFile)
    {
        var startInfo = new ProcessStartInfo("ln") { UseShellExecute = false };
        startInfo.ArgumentList.Add(existingFile);
        startInfo.ArgumentList.Add(link);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "ln did not finish within 30 s");
        Assert.Equal(0, process.ExitCode);
    }

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    // Paths are asserted relative to the temporary root, so a failure reads the same in every run.
    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    private string LinkTargetOf(string relative)
    {
        var target = new FileInfo(Full(relative)).LinkTarget;
        Assert.NotNull(target);
        return Relative(target);
    }

    private Task<byte[]> ReadBytes(string relative) => File.ReadAllBytesAsync(Full(relative), TestCancellation);

    private async Task WriteRaw(string relative, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        await File.WriteAllBytesAsync(Full(relative), bytes, TestCancellation);
    }

    private void CreateProtectedDirectory()
    {
        Directory.CreateDirectory(ProtectedDirectory);
        File.WriteAllBytes(Path.Combine(ProtectedDirectory, SentinelName), SentinelBytes);
    }

    // The setup is a real escape route: the sentinel is visible through the linked directory.
    private void AssertLinkReachesProtectedDirectory(string link) =>
        Assert.True(File.Exists(Path.Combine(Full(link), SentinelName)));

    private static Task Synchronously(Action operation)
    {
        operation();
        return Task.CompletedTask;
    }

    // The exception an operation ends with, or null when it succeeds.
    private static async Task<Exception?> Outcome(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // An operation racing a parent swap succeeds, or meets the swapped-in link and is refused, or finds the
    // entry it reached changed under it and fails with an I/O error.
    private static void AssertSucceededOrRefused(Exception? outcome)
    {
        if (outcome is InvalidOperationException refusal)
        {
            Assert.Contains(LinkRefusal, refusal.Message, StringComparison.Ordinal);
        }
        else if (outcome is not null)
        {
            Assert.True(outcome is IOException, outcome.ToString());
        }
    }

    // Until stop, moves each swapped directory aside, links its path to the protected directory, removes that
    // link and moves the directory back. A step the store's own work rules out, such as linking a path where
    // the store has just recreated the directory, is skipped and leaves the directory aside inside baseline/.
    // No step reaches into the protected directory. Returns the first other failure, or null.
    private Exception? SwapForLinks(CancellationToken stop)
    {
        try
        {
            for (var cycle = 0; !stop.IsCancellationRequested; cycle++)
            {
                foreach (var directory in SwappedDirectories)
                {
                    var path = Full(directory);
                    var aside = $"{path}.aside-{cycle}";
                    if (!Attempt(() => Directory.Move(path, aside)))
                    {
                        continue;
                    }

                    if (Attempt(() => Directory.CreateSymbolicLink(path, ProtectedDirectory)))
                    {
                        File.Delete(path);
                    }

                    Attempt(() => Directory.Move(aside, path));
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Whether a swap step succeeded; it fails only with the I/O error of a path the store changed first.
    private static bool Attempt(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // A link the swapper left behind is removed, so the store's final write and the cleanup see directories.
    private void RemoveSwappedInLinks()
    {
        foreach (var directory in SwappedDirectories)
        {
            var path = Full(directory);
            if (new FileInfo(path).LinkTarget is not null)
            {
                File.Delete(path);
            }
        }
    }

    private BaselineManifest? ReadManifest() => _store.TryReadManifest(out var manifest) ? manifest : null;

    private async Task<IReadOnlyList<BaselineRecord>> ReadWith(string reader) => reader switch
    {
        "ReadRecords" => _store.ReadRecords(),
        "ReadRecordsAsync" => await _store.ReadRecordsAsync(TestCancellation),
        "ReadEntries" => [.. _store.ReadEntries().Select(static entry => entry.Record)],
        "ReadEntriesAsync" => [.. (await _store.ReadEntriesAsync(TestCancellation)).Select(static entry => entry.Record)],
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null),
    };

    private async Task Damage(string damage)
    {
        switch (damage)
        {
            case "tampered-body-file":
                var photo = await ReadBytes(PhotoBodyPath);
                photo[^1] ^= 0x01;
                await WriteRaw(PhotoBodyPath, photo);
                break;
            case "tampered-body-text":
                await EditRecord(
                    RestRecordPath, static json => json["response"]!["bodyText"] = "{\"name\":\"Hank Williams\"}");
                break;
            case "missing-body-file":
                File.Delete(Full(PhotoBodyPath));
                break;
            case "malformed-json":
                await WriteRaw(RestRecordPath, "{\"order\":1,"u8.ToArray());
                break;
            case "json-null":
                await WriteRaw(RestRecordPath, "null"u8.ToArray());
                break;
            case "missing-case-id":
                await EditRecord(RestRecordPath, static json => json.Remove("caseId"));
                break;
            case "missing-request-headers":
                await EditRecord(RestRecordPath, static json => json["request"]!.AsObject().Remove("headers"));
                break;
            case "missing-body-sha":
                await EditRecord(PhotoRecordPath, static json => json["response"]!.AsObject().Remove("bodySha256"));
                break;
            case "wrong-surface-directory":
                File.Move(Full(RestRecordPath), Full("baseline/records/soap/001-getAgent-01.json"));
                break;
            case "wrong-file-name":
                File.Move(Full(RestRecordPath), Full("baseline/records/rest/007-getAgent-01.json"));
                break;
            case "duplicate-order":
                _store.WriteRecord(NewRecord(1, "REST-getClient-01", "Rest.getClient", AgentBody), AgentBody);
                break;
            case "duplicate-case-id":
                _store.WriteRecord(NewRecord(3, "REST-getAgent-01", "Rest.getAgent", AgentBody), AgentBody);
                break;
            case "text-and-file":
                await EditRecord(PhotoRecordPath, static json => json["response"]!["bodyText"] = "photo");
                break;
            case "foreign-body-file":
                await EditRecord(
                    PhotoRecordPath, static json => json["response"]!["bodyFile"] = "002-getPhoto-03.body.bin");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    // The record ReadBodyBytes is given after the damage: the written record when the damage is on disk,
    // otherwise an altered copy of it.
    private async Task<BaselineRecord> DamagedBodyRecord(string damage)
    {
        switch (damage)
        {
            case "tampered-body-file":
            case "missing-body-file":
                await Damage(damage);
                return PhotoRecord;
            case "tampered-body-text":
                return AgentRecord with { Response = AgentRecord.Response with { BodyText = "{}" } };
            case "text-and-file":
                return PhotoRecord with { Response = PhotoRecord.Response with { BodyText = "photo" } };
            case "foreign-body-file":
                return PhotoRecord with
                {
                    Response = PhotoRecord.Response with { BodyFile = "002-getPhoto-03.body.bin" },
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    private async Task EditRecord(string relative, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(await ReadBytes(relative))!.AsObject();
        edit(json);
        await WriteRaw(relative, BaselineStore.SerializeDocument(json));
    }

    private Task CancellableCall(string api, CancellationToken cancellationToken)
    {
        switch (api)
        {
            case "WriteRecordAsync":
                return _store.WriteRecordAsync(PhotoRecord, PhotoBody, cancellationToken);
            case "WriteManifestAsync":
                return _store.WriteManifestAsync(Manifest, cancellationToken);
            case "WriteNormalisationRulesAsync":
                return _store.WriteNormalisationRulesAsync(RulesNode(), cancellationToken);
            case "WriteConformanceAsync":
                return _store.WriteConformanceAsync(ConformanceNode(), cancellationToken);
            case "WriteWsdlAsync":
                return _store.WriteWsdlAsync(WsdlBytes, cancellationToken);
            case "TryReadManifestAsync":
                return _store.TryReadManifestAsync(cancellationToken);
            case "ReadRecordsAsync":
                return _store.ReadRecordsAsync(cancellationToken);
            case "ReadEntriesAsync":
                return _store.ReadEntriesAsync(cancellationToken);
            case "ReadBodyBytesAsync:file":
                return _store.ReadBodyBytesAsync(PhotoRecord, cancellationToken);
            case "ReadBodyBytesAsync:text":
                return _store.ReadBodyBytesAsync(AgentRecord, cancellationToken);
            case "ReadNormalisationRulesAsync":
                return _store.ReadNormalisationRulesAsync(cancellationToken);
            case "TryReadConformanceAsync":
                return _store.TryReadConformanceAsync(cancellationToken);
            case "ReadWsdlBytesAsync":
                return _store.ReadWsdlBytesAsync(cancellationToken);
            case "ClearRecords":
                _store.ClearRecords(cancellationToken);
                return Task.CompletedTask;
            case "NormalisationRules.LoadAsync":
                return NormalisationRules.LoadAsync(_options.NormalisationRulesPath, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(api), api, null);
        }
    }

    private sealed record DocumentWriter(
        string File, Action<BaselineStore> Write, Func<BaselineStore, CancellationToken, Task> WriteAsync);
}
