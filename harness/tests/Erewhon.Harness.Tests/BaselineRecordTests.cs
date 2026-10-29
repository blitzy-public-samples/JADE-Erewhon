using System.Text;
using System.Text.Json;
using Erewhon.Contracts;
using Erewhon.Harness.Baseline;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The baseline record and manifest models without a file system: the surface, method and case number a
/// record derives from its case id and operation, the response half built from raw body bytes, the body
/// hash, the UTF-8 fidelity test that decides whether a body may be stored as text, and the manifest's
/// defaults, seed-fact rule, tolerant reading and fixed key order.
/// </summary>
public sealed class BaselineRecordTests
{
    // SHA-256 test vectors of FIPS 180-2: the empty message, "abc" and the 448-bit two-block message.
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string TwoBlockMessage = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
    private const string TwoBlockSha256 = "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1";

    private const string JsonContentType = "application/json; charset=utf-8";
    private const string SoapContentType = "text/xml; charset=utf-8";
    private const string SoapAction = "\"urn:JadeWebServices/WebServiceHTTP/getAgent\"";

    private const string SurfaceMessage = "starts with neither 'REST-' nor 'SOAP-'";
    private const string OperationMessage = "is not of the form '<Rest|Soap>.<jadeMethod>'";
    private const string CaseNumberMessage = "does not end in '-' followed by ASCII digits";

    // ---- Names derived from the case id and operation ---------------------------------------------

    [Theory]
    [InlineData("REST-getAgent-01", "rest")]
    [InlineData("SOAP-getPhoto-02", "soap")]
    public void Surface_FollowsCaseIdPrefix(string caseId, string expected)
    {
        Assert.Equal(expected, Record(caseId, "Rest.getAgent").Surface);
    }

    [Theory]
    [InlineData("rest-getAgent-01")]
    [InlineData("Soap-getPhoto-02")]
    [InlineData("RESTgetAgent-01")]
    [InlineData("getAgent-01")]
    [InlineData("")]
    [InlineData(null)]
    public void Surface_WithoutCaseIdPrefix_IsInvalidData(string? caseId)
    {
        var record = Record(caseId!, "Rest.getAgent");

        var ex = Assert.Throws<InvalidDataException>(() => record.Surface);

        Assert.Contains(SurfaceMessage, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rest.getAgent", "getAgent")]
    [InlineData("Soap.getPhoto", "getPhoto")]
    [InlineData("Rest.get.Agent", "get.Agent")]
    public void JadeMethod_IsTextAfterFirstDot(string operation, string expected)
    {
        Assert.Equal(expected, Record("REST-getAgent-01", operation).JadeMethod);
    }

    [Theory]
    [InlineData("Rest.")]
    [InlineData("RestgetAgent")]
    [InlineData("")]
    [InlineData(null)]
    public void JadeMethod_WithoutDotOrMethod_IsInvalidData(string? operation)
    {
        var record = Record("REST-getAgent-01", operation!);

        var ex = Assert.Throws<InvalidDataException>(() => record.JadeMethod);

        Assert.Contains(OperationMessage, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REST-getAgent-01", "01")]
    [InlineData("SOAP-getPhoto-123", "123")]
    [InlineData("REST-get-Agent-07", "07")]
    public void CaseNumber_IsDigitsAfterLastDash(string caseId, string expected)
    {
        Assert.Equal(expected, Record(caseId, "Rest.getAgent").CaseNumber);
    }

    [Theory]
    [InlineData("REST-getAgent-")]
    [InlineData("REST-getAgent-0a")]
    [InlineData("REST-getAgent- 1")]
    [InlineData("REST-getAgent-\u0660\u0661")]
    [InlineData("RESTgetAgent01")]
    [InlineData("")]
    [InlineData(null)]
    public void CaseNumber_NotAsciiDigitsAfterLastDash_IsInvalidData(string? caseId)
    {
        var record = Record(caseId!, "Rest.getAgent");

        var ex = Assert.Throws<InvalidDataException>(() => record.CaseNumber);

        Assert.Contains(CaseNumberMessage, ex.Message, StringComparison.Ordinal);
    }

    // ---- Response half and body hash ---------------------------------------------------------------

    [Theory]
    [InlineData("", "", EmptySha256)]
    [InlineData("616263", "abc", AbcSha256)]
    [InlineData("efbbbf616263", "\uFEFFabc", "1c28dc3f1f804a1ad9c9b4b4cf5e2658d16ad4ed08e3020d04a8d2865018947c")]
    [InlineData("e282ac", "\u20AC", "c4cc90ed3d26f12d4b08a75140970a7904035c31cbb4515a83f19b9003c00d1d")]
    public void FromRawBody_WithoutBodyFile_KeepsExactTextAndHashesRawBytes(
        string rawHex, string expectedText, string expectedSha256)
    {
        var response = BaselineResponse.FromRawBody(200, JsonContentType, Convert.FromHexString(rawHex), bodyFile: null);

        Assert.Equal(new BaselineResponse(200, JsonContentType, expectedText, expectedSha256, null), response);
    }

    [Fact]
    public void FromRawBody_WithBodyFile_HoldsNoTextAndHashesRawBytes()
    {
        var response = BaselineResponse.FromRawBody(
            500, contentType: null, Convert.FromHexString("ffd8ffe0"), "002-getPhoto-02.body.bin");

        Assert.Equal(
            new BaselineResponse(
                500,
                null,
                null,
                "ba4f25bf16ba4be6bc7d3276fafeb67f9eb3c5df042bc3a405e1af15b921eed7",
                "002-getPhoto-02.body.bin"),
            response);
    }

    [Theory]
    [InlineData("", EmptySha256)]
    [InlineData("abc", AbcSha256)]
    [InlineData(TwoBlockMessage, TwoBlockSha256)]
    public void ComputeSha256Hex_MatchesKnownVectorInLowercase(string message, string expected)
    {
        Assert.Equal(expected, BaselineResponse.ComputeSha256Hex(Encoding.ASCII.GetBytes(message)));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("7b7d", true)]
    [InlineData("efbbbf7b7d", true)]
    [InlineData("c3a96e61", true)]
    [InlineData("e282ac", true)]
    [InlineData("f09f9880", true)]
    [InlineData("ff", false)]
    [InlineData("80", false)]
    [InlineData("c3", false)]
    [InlineData("c080", false)]
    [InlineData("eda080", false)]
    [InlineData("edb080", false)]
    [InlineData("eda0bdedb880", false)]
    [InlineData("f4908080", false)]
    public void IsTextRoundTrippable_AcceptsOnlyFaithfulUtf8(string rawHex, bool expected)
    {
        Assert.Equal(expected, BaselineResponse.IsTextRoundTrippable(Convert.FromHexString(rawHex)));
    }

    // ---- Manifest ----------------------------------------------------------------------------------

    [Fact]
    public void Manifest_Defaults_AreLabelledStandInWithNoObservedFacts()
    {
        var manifest = new BaselineManifest();

        Assert.Equal("jade-standin", manifest.Source);
        Assert.Equal("Recorded from the JADE stand-in, not from live JADE", manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Null(manifest.SeedSha256);
        Assert.Null(manifest.ReferenceDate);
        Assert.Equal(new ManifestCounts(0, 0, 0, 0), manifest.Counts);
        Assert.Empty(manifest.NotCaptured);
        Assert.False(manifest.WsdlCaptured);
        Assert.Equal(HarnessOptions.HarnessVersion, manifest.HarnessVersion);
        Assert.Null(manifest.StartingCommit);
        Assert.Null(manifest.CaptureCommit);
        Assert.Null(manifest.CaptureExecution);
        Assert.Null(manifest.BlockingError);
        Assert.False(manifest.HasSeedFacts);
        Assert.False(manifest.IsNotRun);
    }

    [Theory]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.LocalProcesses, true, false)]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.Docker, true, false)]
    [InlineData("ab12", "2026-01-01", null, true, false)]
    [InlineData("ab12", "2026-01-01", "Not-Run", true, false)]
    [InlineData(null, "2026-01-01", CaptureExecutionValues.LocalProcesses, false, false)]
    [InlineData("ab12", null, CaptureExecutionValues.LocalProcesses, false, false)]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.NotRun, false, true)]
    [InlineData(null, null, CaptureExecutionValues.NotRun, false, true)]
    public void Manifest_SeedFacts_NeedHashAndDateOutsideNotRun(
        string? seedSha256, string? referenceDate, string? captureExecution, bool hasSeedFacts, bool isNotRun)
    {
        var manifest = new BaselineManifest
        {
            SeedSha256 = seedSha256,
            ReferenceDate = referenceDate,
            CaptureExecution = captureExecution,
        };

        Assert.Equal(hasSeedFacts, manifest.HasSeedFacts);
        Assert.Equal(isNotRun, manifest.IsNotRun);
    }

    [Fact]
    public void Manifest_JsonNulls_ReadAsEmptyValues()
    {
        const string json = """
            {"source":null,"label":null,"standIn":null,"counts":null,"notCaptured":null,"harnessVersion":null}
            """;

        var manifest = JsonSerializer.Deserialize<BaselineManifest>(json, BaselineStore.SerializerOptions);

        Assert.NotNull(manifest);
        Assert.Equal("", manifest.Source);
        Assert.Equal("", manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Equal(new ManifestCounts(0, 0, 0, 0), manifest.Counts);
        Assert.Empty(manifest.NotCaptured);
        Assert.Null(manifest.HarnessVersion);
    }

    [Fact]
    public void Manifest_HandWrittenNotRun_ParsesWithUnknownMemberCommentAndAnyCase()
    {
        const string json = """
            {
              // Written by the execution agent because capture could not start.
              "Source": "jade-standin",
              "CAPTUREEXECUTION": "not-run",
              "counts": { "plannedOperations": 22, "plannedCases": 56, "capturedOperations": 0, "capturedCases": 0 },
              "notCaptured": ["REST-getAgent-01", "SOAP-getPhoto-02"],
              "wsdlCaptured": false,
              "startingCommit": "19a5189e9075349aca54de1fb5fdf2d8727403e7",
              "blockingError": "error NU1101: Unable to find package xunit.v3",
              "writtenBy": "execution agent"
            }
            """;

        var manifest = JsonSerializer.Deserialize<BaselineManifest>(json, BaselineStore.SerializerOptions);

        Assert.NotNull(manifest);
        Assert.True(manifest.IsNotRun);
        Assert.False(manifest.HasSeedFacts);
        Assert.Equal("jade-standin", manifest.Source);
        Assert.Equal(SourceLabels.BaselineLabel, manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Null(manifest.SeedSha256);
        Assert.Null(manifest.ReferenceDate);
        Assert.Equal(new ManifestCounts(22, 56, 0, 0), manifest.Counts);
        Assert.Equal(["REST-getAgent-01", "SOAP-getPhoto-02"], manifest.NotCaptured);
        Assert.False(manifest.WsdlCaptured);
        Assert.Equal(HarnessOptions.HarnessVersion, manifest.HarnessVersion);
        Assert.Equal("19a5189e9075349aca54de1fb5fdf2d8727403e7", manifest.StartingCommit);
        Assert.Null(manifest.CaptureCommit);
        Assert.Equal("error NU1101: Unable to find package xunit.v3", manifest.BlockingError);
    }

    [Fact]
    public void Manifest_Serialises_InDeclarationOrder_WithNullsWritten()
    {
        var manifest = new BaselineManifest
        {
            StandIn = new StandInInfo(null, "2026-01-01", 3),
            ReferenceDate = "2026-01-01",
            Counts = new ManifestCounts(22, 56, 21, 55),
            NotCaptured = ["SOAP-getPhoto-02"],
            CaptureExecution = CaptureExecutionValues.LocalProcesses,
        };

        using var document = JsonDocument.Parse(BaselineStore.SerializeDocument(manifest));
        var root = document.RootElement;

        Assert.Equal(
            [
                "source", "label", "standIn", "seedSha256", "referenceDate", "counts", "notCaptured",
                "wsdlCaptured", "harnessVersion", "startingCommit", "captureCommit", "captureExecution",
                "blockingError",
            ],
            PropertyNames(root));
        Assert.Equal(["seedSha256", "referenceDate", "generation"], PropertyNames(root.GetProperty("standIn")));
        Assert.Equal(
            ["plannedOperations", "plannedCases", "capturedOperations", "capturedCases"],
            PropertyNames(root.GetProperty("counts")));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("standIn").GetProperty("seedSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("seedSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("startingCommit").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("blockingError").ValueKind);
    }

    // ---- Record JSON shape -------------------------------------------------------------------------

    [Fact]
    public void Record_Serialises_InDeclarationOrder_WithHeaderNamesAsSentAndNoComputedMembers()
    {
        var headers = new Dictionary<string, string>
        {
            ["SOAPAction"] = SoapAction,
            ["Content-Type"] = SoapContentType,
        };
        var record = new BaselineRecord(
            4,
            "SOAP-getAgent-01",
            "Soap.getAgent",
            "success",
            false,
            "seed",
            SourceLabels.StandIn,
            new BaselineRequest("POST", "/jade/jadehttp.dll?WebServiceHTTP", headers, null),
            BaselineResponse.FromRawBody(200, SoapContentType, "<a/>"u8, bodyFile: null));

        using var document = JsonDocument.Parse(BaselineStore.SerializeDocument(record));
        var root = document.RootElement;
        var request = root.GetProperty("request");
        var response = root.GetProperty("response");

        Assert.Equal(
            ["order", "caseId", "operation", "kind", "stateChanging", "preState", "source", "request", "response"],
            PropertyNames(root));
        Assert.Equal(["method", "pathAndQuery", "headers", "bodyText"], PropertyNames(request));
        Assert.Equal(["SOAPAction", "Content-Type"], PropertyNames(request.GetProperty("headers")));
        Assert.Equal(SoapAction, request.GetProperty("headers").GetProperty("SOAPAction").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("bodyText").ValueKind);
        Assert.Equal(["status", "contentType", "bodyText", "bodySha256", "bodyFile"], PropertyNames(response));
        Assert.Equal("<a/>", response.GetProperty("bodyText").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("bodyFile").ValueKind);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static BaselineRecord Record(string caseId, string operation) => new(
        1,
        caseId,
        operation,
        "success",
        false,
        "seed",
        SourceLabels.StandIn,
        new BaselineRequest("GET", "/agent/Hank%20Williams", new Dictionary<string, string>(), null),
        BaselineResponse.FromRawBody(200, JsonContentType, "{}"u8, bodyFile: null));

    private static List<string> PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(static property => property.Name)];
}
