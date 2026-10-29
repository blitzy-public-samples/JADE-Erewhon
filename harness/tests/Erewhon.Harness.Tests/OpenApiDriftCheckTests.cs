using System.Net;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;
using Microsoft.AspNetCore.Http;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The OpenAPI drift check against an in-process loopback stub standing in for the facade admin listener, in a
/// temporary repository root of its own: an unusable admin URL, no response or a status other than 200 is
/// <c>not-run</c>; an absent checked-in contract is <c>missing</c> and is never created; documents that differ
/// only in member order, whitespace or a byte order mark pass; changed, missing and extra members and an
/// undecodable or non-JSON document fail with exactly the discrepancies the signature is computed over; and the
/// check writes only <c>artifacts/openapi/live.json</c>, byte for byte as served, removing a stale one when
/// nothing was answered and reporting a failed save in its detail, and never rewrites the contract.
/// </summary>
public sealed class OpenApiDriftCheckTests : IDisposable
{
    private const string OpenApiVersion = "3.1.1";
    private const string Title = "Erewhon REST";
    private const string DocumentVersion = "v1";
    private const string Summary = "Facade";
    private const string OkDescription = "OK";

    private const string VersionPath = "$.info.version";
    private const string SummaryPath = "$.info.summary";
    private const string JsonRootPath = "$";

    // The token a test's admin URL holds in place of the authority of the running stub.
    private const string AuthorityToken = "{authority}";
    private const string UserInfoPassword = "user-info-password";

    // Request limit and connect limit: generous for a loaded host, never reached by a passing test.
    private const int RequestLimitSeconds = 10;

    // How an equal live document differs from the checked-in one.
    private const string Reordered = "reordered and compact";
    private const string LiveByteOrderMark = "byte order mark on the live document";
    private const string ContractByteOrderMark = "byte order mark on the contract";

    // How a drifted live document differs from the checked-in one.
    private const string ChangedValue = "changed value";
    private const string MissingMember = "missing member";
    private const string ExtraMember = "extra member";

    // Which side of the comparison a malformed document is on, and how it is malformed.
    private const string ContractSide = "contract";
    private const string LiveSide = "live";
    private const string InvalidUtf8 = "invalid UTF-8";
    private const string NotJson = "not JSON";
    private const string NotJsonBody = "Service Unavailable";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;
    private static readonly OperationDescriptor GetAgent = Catalogue.GetById("Rest.getAgent");
    private static readonly OperationDescriptor GetAllAgents = Catalogue.GetById("Rest.getAllAgents");

    private static readonly string OperationIdPath =
        $"$.paths['{GetAgent.Rest!.PathTemplate}'].{GetAgent.Rest.Verb.ToLowerInvariant()}.operationId";

    private static readonly string NoResponseConnect = DiscrepancyKinds.NoResponse(NoResponseReasons.Connect);

    private static readonly byte[] StaleLiveDocument = Encoding.UTF8.GetBytes("stale live document of an earlier run");

    private readonly string _root;
    private readonly HarnessOptions _options;
    private readonly CaseRunner _http = new(TimeSpan.FromSeconds(RequestLimitSeconds));
    private readonly JsonSemanticComparer _comparer =
        new(Catalogue, new Normaliser(NormalisationRules.FromCatalogue(Catalogue)));

    /// <summary>Creates the test's own repository root, which the contract and artifact paths resolve inside.</summary>
    public OpenApiDriftCheckTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"erewhon-openapi-check-{Guid.NewGuid():N}")).FullName;
        _options = new HarnessOptions { RepoRoot = _root, RequestTimeoutSeconds = RequestLimitSeconds };
    }

    /// <summary>Disposes the runner and deletes the repository root.</summary>
    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- Not run -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://" + AuthorityToken + "/")]
    [InlineData("http://harness:" + UserInfoPassword + "@" + AuthorityToken)]
    public async Task UnusableAdminUrl_IsNotRun_WithNoRequest_AndRemovesTheStaleLiveDocument(string adminUrl)
    {
        await using var facade = await ServeAsync(StatusCodes.Status200OK, Encoding.UTF8.GetBytes(LiveDocument()));
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));
        WriteStaleLiveDocument();

        // A URL that names the running stub proves that no request is sent even where one could be answered.
        var result = await RunAsync(adminUrl.Replace(AuthorityToken, facade.Authority, StringComparison.Ordinal));

        AssertNotRun(result);
        Assert.Contains(AdminUrlSetting, result.Result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(UserInfoPassword, result.Result.Detail, StringComparison.Ordinal);
        Assert.Empty(facade.Requests);
        Assert.False(File.Exists(LiveDocumentPath), "A stale live document must be removed.");
        AssertOnlyFilesWritten(contract, liveDocument: null);
    }

    [Fact]
    public async Task NoResponse_IsNotRun_AndRemovesTheStaleLiveDocument()
    {
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));
        WriteStaleLiveDocument();

        var result = await RunAsync($"http://{IPAddress.Loopback}:{ClosedPorts.Take(1).Single()}");

        AssertNotRun(result);
        Assert.Contains(NoResponseConnect, result.Result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(LiveDocumentPath), "A stale live document must be removed.");
        AssertOnlyFilesWritten(contract, liveDocument: null);
    }

    [Theory]
    [InlineData(StatusCodes.Status201Created)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task NonOkStatus_IsNotRun_AndSavesTheServedBytes(int status)
    {
        // The served document equals the contract, so only the status can make the check not run.
        var served = Encoding.UTF8.GetBytes(LiveDocument());
        await using var facade = await ServeAsync(status, served);
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));
        WriteStaleLiveDocument();

        var result = await RunAsync(facade.BaseUrl);

        AssertNotRun(result);
        Assert.Equal(AdminRoutes.OpenApiDocument, Assert.Single(facade.Requests));
        AssertOnlyFilesWritten(contract, served);
    }

    // ---- Missing contract --------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentContract_IsMissing_AndIsNeverCreated(bool directoryAtContractPath)
    {
        var served = Encoding.UTF8.GetBytes(LiveDocument());
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        if (directoryAtContractPath)
        {
            Directory.CreateDirectory(_options.ContractsOpenApiPath);
        }

        var result = await RunAsync(facade.BaseUrl);

        Assert.Equal(CheckNames.OpenApiDrift, result.Result.Name);
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(1, result.Result.Cases);
        Assert.Equal(0, result.Result.Passed);
        var failure = Assert.IsType<FailureItem>(result.Failure);
        Assert.Equal(FailureItemIds.OpenApiDrift, failure.ItemId);
        Assert.Equal(FailureItemIds.Missing, failure.Nature);
        Assert.Null(failure.Operation);
        Assert.Equal(result.Result.Detail, failure.Detail);
        Assert.Empty(result.Discrepancies);
        Assert.Equal(AdminRoutes.OpenApiDocument, Assert.Single(facade.Requests));
        Assert.False(File.Exists(_options.ContractsOpenApiPath), "The check must never create the checked-in contract.");
        Assert.Equal(directoryAtContractPath, Directory.Exists(_options.ContractsOpenApiPath));
        AssertOnlyFilesWritten(contract: null, served);
    }

    // ---- Pass --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Reordered)]
    [InlineData(LiveByteOrderMark)]
    [InlineData(ContractByteOrderMark)]
    public async Task EqualDocuments_Pass_AndSaveTheServedBytes(string variant)
    {
        var contractText = Encoding.UTF8.GetBytes(ContractDocument());
        var liveText = Encoding.UTF8.GetBytes(LiveDocument());
        var (contractBytes, served) = variant switch
        {
            Reordered => (contractText, liveText),
            LiveByteOrderMark => (contractText, WithByteOrderMark(liveText)),
            ContractByteOrderMark => (WithByteOrderMark(contractText), liveText),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown document variant."),
        };
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        var contract = WriteContract(contractBytes);
        WriteStaleLiveDocument();

        var result = await RunAsync(facade.BaseUrl);

        Assert.Equal(new CheckResult(CheckNames.OpenApiDrift, CheckOutcomes.Pass, Cases: 1, Passed: 1, Detail: null), result.Result);
        Assert.Null(result.Failure);
        Assert.Empty(result.Discrepancies);
        Assert.Equal(AdminRoutes.OpenApiDocument, Assert.Single(facade.Requests));
        AssertOnlyFilesWritten(contract, served);
    }

    [Fact]
    public async Task UnsavableLiveDocument_IsReportedInTheDetail_AndNeverThrown()
    {
        await using var facade = await ServeAsync(StatusCodes.Status200OK, Encoding.UTF8.GetBytes(LiveDocument()));
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));

        // A directory where live.json belongs makes its save fail; the comparison is unaffected.
        Directory.CreateDirectory(LiveDocumentPath);

        var result = await RunAsync(facade.BaseUrl);

        Assert.Equal(CheckNames.OpenApiDrift, result.Result.Name);
        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Equal(1, result.Result.Cases);
        Assert.Equal(1, result.Result.Passed);
        Assert.Contains(LiveDocumentPath, result.Result.Detail, StringComparison.Ordinal);
        Assert.Null(result.Failure);
        Assert.Empty(result.Discrepancies);
        Assert.True(Directory.Exists(LiveDocumentPath), "The check must leave a directory in the artifact's place untouched.");
        AssertOnlyFilesWritten(contract, liveDocument: null);
    }

    // ---- Drift -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ChangedValue)]
    [InlineData(MissingMember)]
    [InlineData(ExtraMember)]
    public async Task Drift_Fails_WithTheExactDiscrepancy(string drift)
    {
        var (live, expected) = drift switch
        {
            ChangedValue => (
                LiveDocument(operationId: GetAllAgents.JadeMethod),
                new Discrepancy(OperationIdPath, DiscrepancyKinds.Value, JsonString(GetAgent.JadeMethod), JsonString(GetAllAgents.JadeMethod))),
            MissingMember => (
                LiveDocument(withVersion: false),
                new Discrepancy(VersionPath, DiscrepancyKinds.Missing, JsonString(DocumentVersion), DiscrepancyValue.Absent)),
            ExtraMember => (
                LiveDocument(summary: Summary),
                new Discrepancy(SummaryPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, JsonString(Summary))),
            _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, "Unknown drift."),
        };
        var served = Encoding.UTF8.GetBytes(live);
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));

        var result = await RunAsync(facade.BaseUrl);

        AssertDifference(result, [expected]);
        AssertOnlyFilesWritten(contract, served);
    }

    [Fact]
    public async Task SameDrift_GivesTheSameSignatureOnEveryRun_AndLeavesTheContractUnchanged()
    {
        Discrepancy[] expected =
        [
            new(SummaryPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, JsonString(Summary)),
            new(VersionPath, DiscrepancyKinds.Missing, JsonString(DocumentVersion), DiscrepancyValue.Absent),
            new(OperationIdPath, DiscrepancyKinds.Value, JsonString(GetAgent.JadeMethod), JsonString(GetAllAgents.JadeMethod)),
        ];
        var served = Encoding.UTF8.GetBytes(LiveDocument(operationId: GetAllAgents.JadeMethod, withVersion: false, summary: Summary));
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));

        // Each run fetches from a stub on another port, which must not reach the signature.
        var natures = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            await using var facade = await ServeAsync(StatusCodes.Status200OK, served);

            var result = await RunAsync(facade.BaseUrl);

            AssertDifference(result, expected);
            natures.Add(result.Failure!.Nature);
            AssertOnlyFilesWritten(contract, served);
        }

        Assert.Equal(natures[0], natures[1]);
    }

    // ---- Malformed documents -----------------------------------------------------------------------

    [Theory]
    [InlineData(ContractSide, InvalidUtf8)]
    [InlineData(LiveSide, InvalidUtf8)]
    [InlineData(ContractSide, NotJson)]
    [InlineData(LiveSide, NotJson)]
    public async Task MalformedSide_Fails_WithOneMalformedDiscrepancyAtTheRoot(string side, string defect)
    {
        // An invalid byte inside the title; the literal shows it as U+FFFD, the bytes are kept as they are.
        var document = side == ContractSide ? ContractDocument() : LiveDocument();
        var split = document.IndexOf(Title, StringComparison.Ordinal) + Title.Length;
        byte[] invalidUtf8Body = [.. Encoding.UTF8.GetBytes(document[..split]), 0xFF, .. Encoding.UTF8.GetBytes(document[split..])];
        var (malformedBytes, malformedText) = defect switch
        {
            InvalidUtf8 => (invalidUtf8Body, document[..split] + "\uFFFD" + document[split..]),
            NotJson => (Encoding.UTF8.GetBytes(NotJsonBody), NotJsonBody),
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown defect."),
        };
        var served = side == LiveSide ? malformedBytes : Encoding.UTF8.GetBytes(LiveDocument());
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        var contract = WriteContract(side == ContractSide ? malformedBytes : Encoding.UTF8.GetBytes(ContractDocument()));

        var result = await RunAsync(facade.BaseUrl);

        AssertDifference(
            result,
            [
                new(
                    JsonRootPath,
                    DiscrepancyKinds.Malformed,
                    side == ContractSide ? DiscrepancyValue.Literal(malformedText) : DiscrepancyValue.Absent,
                    side == LiveSide ? DiscrepancyValue.Literal(malformedText) : DiscrepancyValue.Absent),
            ]);
        AssertOnlyFilesWritten(contract, served);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>The configuration key of the facade admin URL, <c>Harness:Target:AdminUrl</c>.</summary>
    private static string AdminUrlSetting =>
        $"{HarnessOptions.SectionName}:{nameof(HarnessOptions.Target)}:{nameof(HarnessEndpoints.AdminUrl)}";

    private string LiveDocumentPath => Path.Combine(_options.ArtifactsDirectory, "openapi", "live.json");

    private Task<ContractCheckResult> RunAsync(string adminUrl)
    {
        _options.Target.AdminUrl = adminUrl;
        return new OpenApiDriftCheck(_options, _comparer, _http).RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a stub that answers the admin OpenAPI route with <paramref name="status"/> and <paramref name="body"/>.</summary>
    private static Task<LoopbackStub> ServeAsync(int status, byte[] body) =>
        LoopbackStub.StartAsync(AdminRoutes.OpenApiDocument, status, body, TestContext.Current.CancellationToken);

    /// <summary>Writes the checked-in contract and returns its bytes.</summary>
    private byte[] WriteContract(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.ContractsOpenApiPath)!);
        File.WriteAllBytes(_options.ContractsOpenApiPath, bytes);
        return bytes;
    }

    /// <summary>Leaves the live document of an earlier run in place.</summary>
    private void WriteStaleLiveDocument()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LiveDocumentPath)!);
        File.WriteAllBytes(LiveDocumentPath, StaleLiveDocument);
    }

    /// <summary>
    /// Asserts that the repository root holds exactly the contract, when there is one, with its bytes unchanged,
    /// and <c>live.json</c> holding <paramref name="liveDocument"/>, when given: the check creates and rewrites
    /// nothing else.
    /// </summary>
    private void AssertOnlyFilesWritten(byte[]? contract, byte[]? liveDocument)
    {
        var expected = new List<string>();
        if (contract is not null)
        {
            expected.Add(_options.ContractsOpenApiPath);
        }

        if (liveDocument is not null)
        {
            expected.Add(LiveDocumentPath);
        }

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        if (contract is not null)
        {
            Assert.Equal(contract, File.ReadAllBytes(_options.ContractsOpenApiPath));
        }

        if (liveDocument is not null)
        {
            Assert.Equal(liveDocument, File.ReadAllBytes(LiveDocumentPath));
        }
    }

    private static void AssertNotRun(ContractCheckResult result)
    {
        Assert.Equal(CheckNames.OpenApiDrift, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        Assert.Equal(0, result.Result.Passed);
        Assert.False(string.IsNullOrWhiteSpace(result.Result.Detail), "A not-run check must state its reason.");
        var failure = Assert.IsType<FailureItem>(result.Failure);
        Assert.Equal(FailureItemIds.OpenApiDrift, failure.ItemId);
        Assert.Equal(FailureItemIds.NotRun, failure.Nature);
        Assert.Null(failure.Operation);
        Assert.Equal(result.Result.Detail, failure.Detail);
        Assert.Empty(result.Discrepancies);
    }

    /// <summary>
    /// Asserts a failed check whose nature is <c>d:&lt;signature&gt;</c> over exactly <paramref name="expected"/>,
    /// which is given in canonical order.
    /// </summary>
    private static void AssertDifference(ContractCheckResult result, IReadOnlyList<Discrepancy> expected)
    {
        Assert.Equal(CheckNames.OpenApiDrift, result.Result.Name);
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(1, result.Result.Cases);
        Assert.Equal(0, result.Result.Passed);
        Assert.Equal(expected, result.Discrepancies);
        var failure = Assert.IsType<FailureItem>(result.Failure);
        Assert.Equal(FailureItemIds.OpenApiDrift, failure.ItemId);
        Assert.Equal(FailureItemIds.Diff(DiscrepancySignature.Compute(expected)), failure.Nature);
        Assert.Null(failure.Operation);
        Assert.Equal(result.Result.Detail, failure.Detail);
    }

    private static byte[] WithByteOrderMark(byte[] bytes) => [.. Encoding.UTF8.Preamble, .. bytes];

    /// <summary>The comparer's literal of a JSON string value: its compact encoding, quotes included.</summary>
    private static DiscrepancyValue JsonString(string value) => DiscrepancyValue.Literal(JsonSerializer.Serialize(value));

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// The checked-in contract: an indented OpenAPI document describing the catalogue's REST getAgent route,
    /// members in the order <c>harness export-openapi</c> writes them.
    /// </summary>
    private static string ContractDocument()
    {
        var rest = GetAgent.Rest!;
        return $$"""
            {
              "openapi": {{Quote(OpenApiVersion)}},
              "info": {
                "title": {{Quote(Title)}},
                "version": {{Quote(DocumentVersion)}}
              },
              "paths": {
                {{Quote(rest.PathTemplate)}}: {
                  {{Quote(rest.Verb.ToLowerInvariant())}}: {
                    "operationId": {{Quote(GetAgent.JadeMethod)}},
                    "responses": {
                      "200": {
                        "description": {{Quote(OkDescription)}}
                      }
                    }
                  }
                }
              }
            }
            """;
    }

    /// <summary>
    /// The live document: the contract's content written compactly with every object's members in reverse order,
    /// with <paramref name="operationId"/> in place of the operation id, without <c>info.version</c> when
    /// <paramref name="withVersion"/> is false, and with an <c>info.summary</c> when <paramref name="summary"/> is given.
    /// </summary>
    private static string LiveDocument(string? operationId = null, bool withVersion = true, string? summary = null)
    {
        var rest = GetAgent.Rest!;
        var version = withVersion ? $"\"version\":{Quote(DocumentVersion)}," : string.Empty;
        var summaryMember = summary is null ? string.Empty : $"\"summary\":{Quote(summary)},";
        return $"{{\"paths\":{{{Quote(rest.PathTemplate)}:{{{Quote(rest.Verb.ToLowerInvariant())}:{{"
            + $"\"responses\":{{\"200\":{{\"description\":{Quote(OkDescription)}}}}},\"operationId\":{Quote(operationId ?? GetAgent.JadeMethod)}}}}}}},"
            + $"\"info\":{{{summaryMember}{version}\"title\":{Quote(Title)}}},\"openapi\":{Quote(OpenApiVersion)}}}";
    }
}
