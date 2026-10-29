using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The WSDL contract check against two in-process loopback stubs standing in for the backend and the facade,
/// in a temporary repository root of its own: an absent contracts WSDL or an unusable SOAP base URL is
/// <c>not-run</c> with no request sent; documents that differ only in the <c>soap:address/@location</c> value,
/// comments, indentation or a byte order mark pass; a location or address that is absent on either side or on
/// both, a changed operation, an unreachable side, a status other than 200 and an undecodable or non-XML body
/// each fail with exactly the discrepancies the signature is computed over; and the check writes only the two
/// <c>artifacts/wsdl/**</c> files, removing stale ones and reporting a failed save in its detail, never the contract.
/// </summary>
public sealed class WsdlContractCheckTests : IDisposable
{
    private const string BackendSide = "backend";
    private const string FacadeSide = "facade";

    // The portType, binding and port share this name, as in the stand-in's WSDL.
    private const string BindingName = "ErewhonWebServiceSoap";
    private const string SoapHttpTransport = "http://schemas.xmlsoap.org/soap/http";
    private const string StandInHeaderComment =
        "<!-- Modelled by the JADE stand-in on JADE 2022 default conventions; not captured from live JADE -->";

    private const string LocationPath = "/definitions/service/port/address/@location";
    private const string AddressPath = "/definitions/service/port/address";
    private const string PortTypeOperationNamePath = "/definitions/portType/operation/@name";
    private const string BindingOperationNamePath = "/definitions/binding/operation/@name";
    private const string XmlRootPath = "/";

    // The token a test's base URL holds in place of the authority of that side's running stub.
    private const string AuthorityToken = "{authority}";
    private const string UserInfoPassword = "user-info-password";

    // Request limit and connect limit: generous for a loaded host, never reached by a passing test.
    private const int RequestLimitSeconds = 10;

    // How the backend and facade documents of a presence test differ.
    private const string FacadeLocationAbsent = "facade location absent";
    private const string BackendLocationAbsent = "backend location absent";
    private const string BothLocationsAbsent = "both locations absent";
    private const string FacadeAddressAbsent = "facade address absent";
    private const string BackendAddressAbsent = "backend address absent";
    private const string BothAddressesAbsent = "both addresses absent";

    // How a facade document that must pass differs from the backend's beyond the location.
    private const string LocationOnly = "location only";
    private const string Unindented = "unindented";
    private const string HeaderComment = "declaration and header comment";
    private const string ByteOrderMark = "byte order mark";

    // How a malformed body is malformed.
    private const string InvalidUtf8 = "invalid UTF-8";
    private const string NotXml = "not XML";

    private const string NotFoundBody = "Not Found";
    private const string NotXmlBody = "Service Unavailable";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;
    private static readonly SoapAddress SoapAddressing = Catalogue.Addresses.Soap;
    private static readonly string WsdlPathAndQuery = SoapAddressing.WsdlPathAndQuery;
    private static readonly string WsdlNamespace = SoapAddressing.WsdlNamespaces["wsdl"];
    private static readonly string WsdlSoapNamespace = SoapAddressing.WsdlNamespaces["soap"];

    private static readonly OperationDescriptor GetAgent = Catalogue.GetById("Soap.getAgent");
    private static readonly OperationDescriptor GetAgentNames = Catalogue.GetById("Soap.getAgentNames");

    private static readonly string StandInLocation =
        $"http://standin:{SoapAddressing.Port}{SoapAddressing.EndpointPathAndQuery}";

    private static readonly string FacadeLocation =
        $"http://localhost:{SoapAddressing.Port}{SoapAddressing.EndpointPathAndQuery}";

    private static readonly string NoResponseConnect = DiscrepancyKinds.NoResponse(NoResponseReasons.Connect);

    private static readonly byte[] StaleArtifact = Encoding.UTF8.GetBytes("stale artifact of an earlier run");

    private readonly string _root;
    private readonly HarnessOptions _options;
    private readonly CaseRunner _http = new(TimeSpan.FromSeconds(RequestLimitSeconds));
    private readonly XmlSemanticComparer _comparer =
        new(Catalogue, new Normaliser(NormalisationRules.FromCatalogue(Catalogue)));

    /// <summary>Creates the test's own repository root, which every contract and artifact path resolves inside.</summary>
    public WsdlContractCheckTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"erewhon-wsdl-check-{Guid.NewGuid():N}")).FullName;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentContract_IsNotRun_WithNoRequest_AndRemovesStaleArtifacts(bool directoryAtContractPath)
    {
        await using var backend = await ServeAsync(WsdlDocument(StandInLocation));
        await using var facade = await ServeAsync(WsdlDocument(FacadeLocation));
        if (directoryAtContractPath)
        {
            Directory.CreateDirectory(_options.ContractsWsdlPath);
        }

        WriteStaleArtifacts();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertNotRun(result);
        Assert.Empty(backend.Requests);
        Assert.Empty(facade.Requests);
        Assert.False(File.Exists(ArtifactPath(BackendSide)), "A stale backend artifact must be removed.");
        Assert.False(File.Exists(ArtifactPath(FacadeSide)), "A stale facade artifact must be removed.");
        Assert.False(File.Exists(_options.ContractsWsdlPath), "The check must never create the contracts WSDL.");
        Assert.Equal(directoryAtContractPath, Directory.Exists(_options.ContractsWsdlPath));
    }

    [Theory]
    [InlineData(BackendSide, "")]
    [InlineData(FacadeSide, " ")]
    [InlineData(BackendSide, "not a url")]
    [InlineData(FacadeSide, "/relative/path")]
    [InlineData(BackendSide, "ftp://" + AuthorityToken + "/")]
    [InlineData(BackendSide, "http://harness:" + UserInfoPassword + "@" + AuthorityToken)]
    [InlineData(FacadeSide, "http://harness:" + UserInfoPassword + "@" + AuthorityToken)]
    public async Task UnusableSoapBaseUrl_IsNotRun_WithNoRequest(string side, string baseUrl)
    {
        await using var backend = await ServeAsync(WsdlDocument(StandInLocation));
        await using var facade = await ServeAsync(WsdlDocument(FacadeLocation));
        var contract = WriteContract();
        WriteStaleArtifacts();

        // A URL that names a running stub proves that no request is sent even where one could be answered.
        var unusable = baseUrl.Replace(AuthorityToken, (side == BackendSide ? backend : facade).Authority, StringComparison.Ordinal);
        var result = await RunAsync(
            side == BackendSide ? unusable : backend.BaseUrl,
            side == FacadeSide ? unusable : facade.BaseUrl);

        AssertNotRun(result);
        Assert.Contains(SettingName(side), result.Result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(UserInfoPassword, result.Result.Detail, StringComparison.Ordinal);
        Assert.Empty(backend.Requests);
        Assert.Empty(facade.Requests);
        Assert.False(File.Exists(ArtifactPath(BackendSide)), "A stale backend artifact must be removed.");
        Assert.False(File.Exists(ArtifactPath(FacadeSide)), "A stale facade artifact must be removed.");
        AssertOnlyArtifactsWritten(contract);
    }

    // ---- Pass ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(LocationOnly)]
    [InlineData(Unindented)]
    [InlineData(HeaderComment)]
    [InlineData(ByteOrderMark)]
    public async Task EqualExceptLocation_Passes_AndKeepsBothDocumentsByteForByte(string variant)
    {
        var backendBody = Encoding.UTF8.GetBytes(WsdlDocument(StandInLocation));
        byte[] facadeBody = variant switch
        {
            LocationOnly => Encoding.UTF8.GetBytes(WsdlDocument(FacadeLocation)),
            Unindented => Encoding.UTF8.GetBytes(WsdlDocument(FacadeLocation, indent: false)),
            HeaderComment => Encoding.UTF8.GetBytes(WithHeaderComment(WsdlDocument(FacadeLocation))),
            ByteOrderMark => [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(WithHeaderComment(WsdlDocument(FacadeLocation)))],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown facade document variant."),
        };

        await using var backend = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, backendBody, TestContext.Current.CancellationToken);
        await using var facade = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, facadeBody, TestContext.Current.CancellationToken);
        var contract = WriteContract();
        WriteStaleArtifacts();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        Assert.Equal(new CheckResult(CheckNames.WsdlContract, CheckOutcomes.Pass, Cases: 1, Passed: 1, Detail: null), result.Result);
        Assert.Null(result.Failure);
        Assert.Empty(result.Discrepancies);
        Assert.Equal(WsdlPathAndQuery, Assert.Single(backend.Requests));
        Assert.Equal(WsdlPathAndQuery, Assert.Single(facade.Requests));
        Assert.Equal(backendBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
        Assert.Equal(facadeBody, File.ReadAllBytes(ArtifactPath(FacadeSide)));
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    [Fact]
    public async Task UnsavableArtifact_IsReportedInTheDetail_AndNeverThrown()
    {
        var backendBody = Encoding.UTF8.GetBytes(WsdlDocument(StandInLocation));
        await using var backend = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, backendBody, TestContext.Current.CancellationToken);
        await using var facade = await ServeAsync(WsdlDocument(FacadeLocation));
        var contract = WriteContract();

        // A directory where the facade artifact belongs makes its save fail; the comparison is unaffected.
        Directory.CreateDirectory(ArtifactPath(FacadeSide));

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        Assert.Equal(CheckNames.WsdlContract, result.Result.Name);
        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Equal(1, result.Result.Cases);
        Assert.Equal(1, result.Result.Passed);
        Assert.Contains(ArtifactPath(FacadeSide), result.Result.Detail, StringComparison.Ordinal);
        Assert.Null(result.Failure);
        Assert.Empty(result.Discrepancies);
        Assert.True(Directory.Exists(ArtifactPath(FacadeSide)), "The check must leave a directory in an artifact's place untouched.");
        Assert.Equal(backendBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
        AssertOnlyArtifactsWritten(contract, BackendSide);
    }

    // ---- Location presence ---------------------------------------------------------------------------

    [Theory]
    [InlineData(FacadeLocationAbsent)]
    [InlineData(BackendLocationAbsent)]
    [InlineData(BothLocationsAbsent)]
    [InlineData(FacadeAddressAbsent)]
    [InlineData(BackendAddressAbsent)]
    [InlineData(BothAddressesAbsent)]
    public async Task AbsentLocationOrAddress_Fails_WithThePresenceDiscrepancy(string absence)
    {
        var (backendDocument, facadeDocument, expected) = absence switch
        {
            FacadeLocationAbsent => (
                WsdlDocument(StandInLocation),
                WsdlDocument(location: null),
                new Discrepancy(LocationPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(StandInLocation), DiscrepancyValue.Absent)),
            BackendLocationAbsent => (
                WsdlDocument(location: null),
                WsdlDocument(FacadeLocation),
                new Discrepancy(LocationPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(FacadeLocation))),
            BothLocationsAbsent => (
                WsdlDocument(location: null),
                WsdlDocument(location: null),
                new Discrepancy(LocationPath, DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent)),
            FacadeAddressAbsent => (
                WsdlDocument(StandInLocation),
                WsdlDocument(FacadeLocation, address: false),
                new Discrepancy(AddressPath, DiscrepancyKinds.Missing, AddressMarkup(StandInLocation), DiscrepancyValue.Absent)),
            BackendAddressAbsent => (
                WsdlDocument(StandInLocation, address: false),
                WsdlDocument(FacadeLocation),
                new Discrepancy(AddressPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, AddressMarkup(FacadeLocation))),
            BothAddressesAbsent => (
                WsdlDocument(StandInLocation, address: false),
                WsdlDocument(FacadeLocation, address: false),
                new Discrepancy(AddressPath, DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent)),
            _ => throw new ArgumentOutOfRangeException(nameof(absence), absence, "Unknown absence."),
        };

        await using var backend = await ServeAsync(backendDocument);
        await using var facade = await ServeAsync(facadeDocument);
        var contract = WriteContract();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertDifference(result, [expected]);
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    // ---- Document difference ---------------------------------------------------------------------------

    [Fact]
    public async Task ChangedOperation_Fails_WithTheSameSignatureOnEveryRun()
    {
        Discrepancy[] expected =
        [
            new(BindingOperationNamePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(GetAgent.JadeMethod), DiscrepancyValue.Literal(GetAgentNames.JadeMethod)),
            new(PortTypeOperationNamePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(GetAgent.JadeMethod), DiscrepancyValue.Literal(GetAgentNames.JadeMethod)),
        ];
        var contract = WriteContract();

        // Each run fetches from stubs on other ports, which must not reach the signature.
        var natures = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            await using var backend = await ServeAsync(WsdlDocument(StandInLocation));
            await using var facade = await ServeAsync(WsdlDocument(FacadeLocation, operationName: GetAgentNames.JadeMethod));

            var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

            AssertDifference(result, expected);
            natures.Add(result.Failure!.Nature);
        }

        Assert.Equal(natures[0], natures[1]);
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    // ---- Fetch failures --------------------------------------------------------------------------------

    [Theory]
    [InlineData(BackendSide)]
    [InlineData(FacadeSide)]
    public async Task UnreachableSide_Fails_WithTheFixedNoResponseDiscrepancy(string side)
    {
        var otherSide = side == BackendSide ? FacadeSide : BackendSide;
        var otherBody = Encoding.UTF8.GetBytes(WsdlDocument(side == BackendSide ? FacadeLocation : StandInLocation));
        await using var other = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, otherBody, TestContext.Current.CancellationToken);
        var contract = WriteContract();
        Discrepancy[] expected =
        [
            new($"$response.{side}", NoResponseConnect, DiscrepancyValue.Literal("response"), DiscrepancyValue.Literal(NoResponseConnect)),
        ];

        // Two different closed ports: the port appears in the detail only, never in the signature.
        var natures = new List<string>();
        foreach (var port in ClosedPorts.Take(2))
        {
            WriteStaleArtifacts();
            var unreachable = $"http://{IPAddress.Loopback}:{port}";

            var result = await RunAsync(
                side == BackendSide ? unreachable : other.BaseUrl,
                side == FacadeSide ? unreachable : other.BaseUrl);

            AssertDifference(result, expected);
            natures.Add(result.Failure!.Nature);
            Assert.False(File.Exists(ArtifactPath(side)), "The stale artifact of the unanswered side must be removed.");
            Assert.Equal(otherBody, File.ReadAllBytes(ArtifactPath(otherSide)));
        }

        Assert.Equal(natures[0], natures[1]);
        Assert.Equal(new[] { WsdlPathAndQuery, WsdlPathAndQuery }, other.Requests);
        AssertOnlyArtifactsWritten(contract, otherSide);
    }

    [Theory]
    [InlineData(BackendSide)]
    [InlineData(FacadeSide)]
    public async Task NotFoundSide_Fails_WithTheStatusDiscrepancy_AndKeepsItsBody(string side)
    {
        // The 404 body is not XML, so any comparison of the documents would add a malformed discrepancy.
        var notFoundBody = Encoding.UTF8.GetBytes(NotFoundBody);
        var otherBody = Encoding.UTF8.GetBytes(WsdlDocument(side == BackendSide ? FacadeLocation : StandInLocation));
        await using var backend = await LoopbackStub.StartAsync(
            WsdlPathAndQuery,
            side == BackendSide ? StatusCodes.Status404NotFound : StatusCodes.Status200OK,
            side == BackendSide ? notFoundBody : otherBody,
            TestContext.Current.CancellationToken);
        await using var facade = await LoopbackStub.StartAsync(
            WsdlPathAndQuery,
            side == FacadeSide ? StatusCodes.Status404NotFound : StatusCodes.Status200OK,
            side == FacadeSide ? notFoundBody : otherBody,
            TestContext.Current.CancellationToken);
        var contract = WriteContract();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertDifference(result, [new($"$status.{side}", DiscrepancyKinds.Value, DiscrepancyValue.Literal("200"), DiscrepancyValue.Literal("404"))]);
        Assert.Equal(side == BackendSide ? notFoundBody : otherBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
        Assert.Equal(side == FacadeSide ? notFoundBody : otherBody, File.ReadAllBytes(ArtifactPath(FacadeSide)));
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    // ---- Malformed bodies ------------------------------------------------------------------------------

    [Theory]
    [InlineData(BackendSide, InvalidUtf8)]
    [InlineData(FacadeSide, InvalidUtf8)]
    [InlineData(BackendSide, NotXml)]
    [InlineData(FacadeSide, NotXml)]
    public async Task MalformedSide_Fails_WithOneMalformedDiscrepancyAtTheRoot(string side, string defect)
    {
        // An invalid byte inside the portType name; the literal shows it as U+FFFD, the bytes are kept as served.
        var document = WsdlDocument(side == BackendSide ? StandInLocation : FacadeLocation);
        var marker = $"<wsdl:portType name=\"{BindingName}";
        var split = document.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        byte[] invalidUtf8Body = [.. Encoding.UTF8.GetBytes(document[..split]), 0xFF, .. Encoding.UTF8.GetBytes(document[split..])];
        var (malformedBody, malformedText) = defect switch
        {
            InvalidUtf8 => (invalidUtf8Body, document[..split] + "\uFFFD" + document[split..]),
            NotXml => (Encoding.UTF8.GetBytes(NotXmlBody), NotXmlBody),
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown defect."),
        };
        var wellFormedBody = Encoding.UTF8.GetBytes(WsdlDocument(side == BackendSide ? FacadeLocation : StandInLocation));

        await using var backend = await LoopbackStub.StartAsync(
            WsdlPathAndQuery, StatusCodes.Status200OK, side == BackendSide ? malformedBody : wellFormedBody, TestContext.Current.CancellationToken);
        await using var facade = await LoopbackStub.StartAsync(
            WsdlPathAndQuery, StatusCodes.Status200OK, side == FacadeSide ? malformedBody : wellFormedBody, TestContext.Current.CancellationToken);
        var contract = WriteContract();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertDifference(
            result,
            [
                new(
                    XmlRootPath,
                    DiscrepancyKinds.Malformed,
                    side == BackendSide ? DiscrepancyValue.Literal(malformedText) : DiscrepancyValue.Absent,
                    side == FacadeSide ? DiscrepancyValue.Literal(malformedText) : DiscrepancyValue.Absent),
            ]);
        Assert.Equal(side == BackendSide ? malformedBody : wellFormedBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
        Assert.Equal(side == FacadeSide ? malformedBody : wellFormedBody, File.ReadAllBytes(ArtifactPath(FacadeSide)));
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private Task<ContractCheckResult> RunAsync(string backendSoapBaseUrl, string facadeSoapBaseUrl)
    {
        _options.Backend.SoapBaseUrl = backendSoapBaseUrl;
        _options.Target.SoapBaseUrl = facadeSoapBaseUrl;
        return new WsdlContractCheck(_options, Catalogue, _comparer, _http).RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a stub that answers the catalogue's WSDL request with 200 and <paramref name="document"/> as UTF-8.</summary>
    private static Task<LoopbackStub> ServeAsync(string document) =>
        LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, Encoding.UTF8.GetBytes(document), TestContext.Current.CancellationToken);

    /// <summary>Writes the contracts WSDL, which only has to exist, and returns its bytes.</summary>
    private byte[] WriteContract()
    {
        var bytes = Encoding.UTF8.GetBytes(WithHeaderComment(WsdlDocument(FacadeLocation)));
        Directory.CreateDirectory(Path.GetDirectoryName(_options.ContractsWsdlPath)!);
        File.WriteAllBytes(_options.ContractsWsdlPath, bytes);
        return bytes;
    }

    /// <summary>Leaves both artifacts of an earlier run in place.</summary>
    private void WriteStaleArtifacts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPath(BackendSide))!);
        File.WriteAllBytes(ArtifactPath(BackendSide), StaleArtifact);
        File.WriteAllBytes(ArtifactPath(FacadeSide), StaleArtifact);
    }

    private string ArtifactPath(string side) => Path.Combine(_options.ArtifactsDirectory, "wsdl", $"{side}.wsdl");

    /// <summary>The configuration key of a side's SOAP base URL, for example <c>Harness:Backend:SoapBaseUrl</c>.</summary>
    private static string SettingName(string side) =>
        $"{HarnessOptions.SectionName}:{(side == BackendSide ? nameof(HarnessOptions.Backend) : nameof(HarnessOptions.Target))}:{nameof(HarnessEndpoints.SoapBaseUrl)}";

    /// <summary>
    /// Asserts that the repository root holds exactly the contracts WSDL, with its bytes unchanged, and the
    /// artifacts of <paramref name="artifactSides"/>: the check creates and rewrites nothing else.
    /// </summary>
    private void AssertOnlyArtifactsWritten(byte[] contract, params string[] artifactSides)
    {
        var expected = artifactSides
            .Select(ArtifactPath)
            .Append(_options.ContractsWsdlPath)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(contract, File.ReadAllBytes(_options.ContractsWsdlPath));
    }

    private static void AssertNotRun(ContractCheckResult result)
    {
        Assert.Equal(CheckNames.WsdlContract, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        Assert.Equal(0, result.Result.Passed);
        Assert.False(string.IsNullOrWhiteSpace(result.Result.Detail), "A not-run check must state its reason.");
        var failure = Assert.IsType<FailureItem>(result.Failure);
        Assert.Equal(FailureItemIds.WsdlContract, failure.ItemId);
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
        Assert.Equal(CheckNames.WsdlContract, result.Result.Name);
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(1, result.Result.Cases);
        Assert.Equal(0, result.Result.Passed);
        Assert.Equal(expected, result.Discrepancies);
        var failure = Assert.IsType<FailureItem>(result.Failure);
        Assert.Equal(FailureItemIds.WsdlContract, failure.ItemId);
        Assert.Equal(FailureItemIds.Diff(DiscrepancySignature.Compute(expected)), failure.Nature);
        Assert.Null(failure.Operation);
        Assert.Equal(result.Result.Detail, failure.Detail);
    }

    /// <summary>The XML declaration and the stand-in's label comment above <paramref name="document"/>, as the contracts WSDL carries them.</summary>
    private static string WithHeaderComment(string document) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n{StandInHeaderComment}\n{document}";

    /// <summary>The literal of a <c>soap:address</c> reported on one side only: the element as written on its own, namespace declared.</summary>
    private static DiscrepancyValue AddressMarkup(string location) =>
        DiscrepancyValue.Literal($"<soap:address location=\"{SecurityElement.Escape(location)}\" xmlns:soap=\"{WsdlSoapNamespace}\" />");

    /// <summary>A newline and two spaces per level when <paramref name="indent"/> is true; otherwise nothing.</summary>
    private static string Break(int depth, bool indent) => indent ? "\n" + new string(' ', 2 * depth) : string.Empty;

    /// <summary>
    /// A WSDL 1.1 document in the stand-in's shape: one portType operation, its document/literal binding and the
    /// service port whose <c>soap:address</c> carries <paramref name="location"/>, XML-escaped. The address has no
    /// <c>location</c> attribute when <paramref name="location"/> is null, and the port has no address when
    /// <paramref name="address"/> is false.
    /// </summary>
    private static string WsdlDocument(string? location, bool address = true, string? operationName = null, bool indent = true)
    {
        var operation = operationName ?? GetAgent.JadeMethod;
        var addressElement = !address
            ? string.Empty
            : location is null
                ? $"{Break(3, indent)}<soap:address/>"
                : $"{Break(3, indent)}<soap:address location=\"{SecurityElement.Escape(location)}\"/>";

        return $"<wsdl:definitions xmlns:wsdl=\"{WsdlNamespace}\" xmlns:soap=\"{WsdlSoapNamespace}\" xmlns:tns=\"{SoapAddressing.TargetNamespace}\" targetNamespace=\"{SoapAddressing.TargetNamespace}\">"
            + $"{Break(1, indent)}<wsdl:portType name=\"{BindingName}\">"
            + $"{Break(2, indent)}<wsdl:operation name=\"{operation}\"/>"
            + $"{Break(1, indent)}</wsdl:portType>"
            + $"{Break(1, indent)}<wsdl:binding name=\"{BindingName}\" type=\"tns:{BindingName}\">"
            + $"{Break(2, indent)}<soap:binding style=\"{SoapAddressing.Style}\" transport=\"{SoapHttpTransport}\"/>"
            + $"{Break(2, indent)}<wsdl:operation name=\"{operation}\">"
            + $"{Break(3, indent)}<soap:operation soapAction=\"{GetAgent.Soap!.Action}\" style=\"{SoapAddressing.Style}\"/>"
            + $"{Break(2, indent)}</wsdl:operation>"
            + $"{Break(1, indent)}</wsdl:binding>"
            + $"{Break(1, indent)}<wsdl:service name=\"{SoapAddressing.ServiceName}\">"
            + $"{Break(2, indent)}<wsdl:port name=\"{BindingName}\" binding=\"tns:{BindingName}\">"
            + addressElement
            + $"{Break(2, indent)}</wsdl:port>"
            + $"{Break(1, indent)}</wsdl:service>"
            + $"{Break(0, indent)}</wsdl:definitions>";
    }
}

/// <summary>
/// An in-process HTTP/1.1 server on 127.0.0.1 and an ephemeral port that answers one exact request target with a
/// chosen status and exact body bytes, and any other target with 404 and no body. It records every request
/// target it receives, before answering, so a caller that has its response also sees its request recorded.
/// Shared by <see cref="WsdlContractCheckTests"/> and <see cref="OpenApiDriftCheckTests"/>.
/// </summary>
internal sealed class LoopbackStub : IAsyncDisposable
{
    // Bounds the graceful stop; idle keep-alive connections close at once, so it is never reached.
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _requests;

    private LoopbackStub(WebApplication app, ConcurrentQueue<string> requests, Uri baseUrl)
    {
        _app = app;
        _requests = requests;
        BaseUrl = baseUrl.GetLeftPart(UriPartial.Authority);
        Authority = baseUrl.Authority;
    }

    /// <summary>The stub's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>The stub's authority, <c>127.0.0.1:&lt;port&gt;</c>.</summary>
    public string Authority { get; }

    /// <summary>Every request target received so far, raw and in arrival order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Starts a stub that answers <paramref name="pathAndQuery"/> with <paramref name="status"/> and <paramref name="body"/>.</summary>
    /// <param name="pathAndQuery">The exact raw request target answered.</param>
    /// <param name="status">The status code of that answer.</param>
    /// <param name="body">The exact body bytes of that answer.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running stub, which the caller disposes.</returns>
    public static async Task<LoopbackStub> StartAsync(string pathAndQuery, int status, byte[] body, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownLimit);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        var requests = new ConcurrentQueue<string>();
        app.Run(async context =>
        {
            var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
            requests.Enqueue(target);
            if (!string.Equals(target, pathAndQuery, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = status;
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        });

        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
            return new LoopbackStub(app, requests, new Uri(address));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops the server and releases its port.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}

/// <summary>Loopback ports that were just bound and released, so a connection to them is refused.</summary>
internal static class ClosedPorts
{
    /// <summary>
    /// Returns <paramref name="count"/> distinct closed ports: each is bound on 127.0.0.1 with port 0 while the
    /// others are still held, so the system assigns different ones, and all are released before returning.
    /// </summary>
    public static int[] Take(int count)
    {
        var listeners = new List<TcpListener>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listeners.Add(listener);
                listener.Start();
            }

            return [.. listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Dispose();
            }
        }
    }
}
