using System.Text;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Stand-in conformance verdicts over records built here and handed to the check directly, so no file,
/// network or running system is involved. A member passes only on a non-null value of its declared wire
/// type: a JADE Integer is a whole number in the 32-bit range, decided exactly; a collection is valid only
/// when every item has the array's item type and, on SOAP, its item element name; and a body that cannot
/// be decoded contributes nothing instead of aborting the check.
/// </summary>
public sealed class ConformanceCheckTests
{
    private const string Pass = "pass";
    private const string Unobserved = "unobserved";
    private const string UnreachableInSource = "unreachable-in-source";
    private const string WrongNumber = "wrong-type:number";
    private const string WrongText = "wrong-type:text";

    private const string RestResultGuid = "CONF:Rest:DTOResult.guid";
    private const string RestResultAllErrors = "CONF:Rest:DTOResult.allErrors";
    private const string RestResultAllWarnings = "CONF:Rest:DTOResult.allWarnings";
    private const string RestResultStatusCode = "CONF:Rest:DTOResult.statusCode";
    private const string RestAgentName = "CONF:Rest:DTOAgent.name";
    private const string RestAgentStreet = "CONF:Rest:DTOAgent.street";
    private const string RestClientName = "CONF:Rest:DTOClient.name";
    private const string RestCreated = "CONF:Rest:STATUS:HTTP_Created";
    private const string RestBadRequest = "CONF:Rest:STATUS:HTTP_Bad_Request";
    private const string SoapResultAllErrors = "CONF:Soap:DTOResult.allErrors";
    private const string SoapResultStatusCode = "CONF:Soap:DTOResult.statusCode";
    private const string SoapName = "CONF:Soap:DTOName.name";
    private const string SoapBadRequest = "CONF:Soap:STATUS:Error_Bad_Request";
    private const string SoapSuccess = "CONF:Soap:STATUS:Status_Success";

    // The stand-in's first generated GUID.
    private const string CreatedGuid = "{00000000-0000-4000-8000-000000000001}";

    // Where a body template holds this marker, a test puts a lone high surrogate there: escaped as
    // \uD800, or as the raw UTF-16 unit, which theory data cannot carry because serialising it replaces
    // an unpaired surrogate.
    private const string SurrogateMarker = "<lone-surrogate>";

    private readonly ConformanceCheck _check = new(OperationCatalogue.Default);

    /// <summary>
    /// JSON number tokens for a JADE Integer and the result they give: whole numbers from -2147483648 to
    /// 2147483647 in any notation pass, and everything else, however close or long, is a wrong number.
    /// </summary>
    public static TheoryData<string, string> RestIntegerTokens { get; } = new()
    {
        { "2147483647", Pass },
        { "-2147483648", Pass },
        { "-0", Pass },
        { "1.0", Pass },
        { "1e2", Pass },
        { "1E+2", Pass },
        { "10e-1", Pass },
        { "2.147483647e9", Pass },
        { "0e99999999999", Pass },
        { "0e" + new string('9', 40), Pass },
        { "1" + new string('0', 400) + "e-400", Pass },
        { "0." + new string('0', 400) + "1e401", Pass },
        { "2147483648", WrongNumber },
        { "-2147483649", WrongNumber },
        { "2.147483648e9", WrongNumber },
        { "1.5", WrongNumber },
        { "1E-1", WrongNumber },
        { "200.00000000000000000000000000001", WrongNumber },
        { "1e999999999999", WrongNumber },
        { "1e-999999999999", WrongNumber },
        { "1e" + new string('9', 40), WrongNumber },
        { "1" + new string('0', 400), WrongNumber },
    };

    // ---- Positive oracle ------------------------------------------------------------------------

    [Fact]
    public void WellFormedRestResults_PassTheirMembers()
    {
        var result = Run(
            RestRecord("REST-postCreateAgent-01", "Rest.postCreateAgent", 201, RestResultBody(CreatedGuid, "[]", "201")),
            RestRecord("REST-postCreateAgent-02", "Rest.postCreateAgent", 400, RestResultBody("", """["Agent name is required"]""", "400")));

        Assert.Equal(Pass, ResultOf(result, RestResultGuid));
        Assert.Equal(Pass, ResultOf(result, RestResultAllErrors));
        Assert.Equal(Pass, ResultOf(result, RestResultStatusCode));
        Assert.Equal(UnreachableInSource, ResultOf(result, RestResultAllWarnings));
        Assert.Equal(Pass, ResultOf(result, RestCreated));
        Assert.Equal(Pass, ResultOf(result, RestBadRequest));
    }

    [Fact]
    public void WellFormedSoapResult_PassesItsMembers()
    {
        var result = Run(SoapRecord("Soap.updateAgent", SoapResultContent("<String>Agent name is required</String>", "400")));

        Assert.Equal(Pass, ResultOf(result, SoapResultAllErrors));
        Assert.Equal(Pass, ResultOf(result, SoapResultStatusCode));
        Assert.Equal(Pass, ResultOf(result, SoapBadRequest));
        Assert.Equal(Unobserved, ResultOf(result, SoapSuccess));
    }

    // ---- JADE Integer ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RestIntegerTokens))]
    public void RestInteger_IsAWholeNumberInThe32BitRange(string token, string expected)
    {
        var result = Run(RestRecord("REST-postCreateAgent-01", "Rest.postCreateAgent", 201, RestResultBody(CreatedGuid, "[]", token)));

        Assert.Equal(expected, ResultOf(result, RestResultStatusCode));
    }

    [Theory]
    [InlineData("400", Pass, Pass)]
    [InlineData("2147483647", Pass, Unobserved)]
    [InlineData("-2147483648", Pass, Unobserved)]
    [InlineData("2147483648", WrongText, Unobserved)]
    [InlineData("-2147483649", WrongText, Unobserved)]
    [InlineData("4294967696", WrongText, Unobserved)]
    public void SoapInteger_IsAnXsdInt(string statusCode, string expectedMember, string expectedBadRequest)
    {
        // 4294967696 is 2^32 + 400: read past the 32-bit range it would also pass for Error_Bad_Request.
        var result = Run(SoapRecord("Soap.updateAgent", SoapResultContent("", statusCode)));

        Assert.Equal(expectedMember, ResultOf(result, SoapResultStatusCode));
        Assert.Equal(expectedBadRequest, ResultOf(result, SoapBadRequest));
    }

    // ---- Collections ----------------------------------------------------------------------------

    [Theory]
    [InlineData("[]", Pass)]
    [InlineData("""["Agent name is required"]""", Pass)]
    [InlineData("""[null,"Agent name is required"]""", Pass)]
    [InlineData("[123]", "wrong-type:item:number")]
    [InlineData("[1,true]", "wrong-type:item:boolean")]
    [InlineData("[true,1]", "wrong-type:item:boolean")]
    [InlineData("[{}]", "wrong-type:item:object")]
    [InlineData("""[["Agent name is required"]]""", "wrong-type:item:array")]
    [InlineData("{}", "wrong-type:object")]
    [InlineData("\"Agent name is required\"", "wrong-type:string")]
    public void RestStringArray_IsValidOnlyWhenEveryItemIsAString(string allErrors, string expected)
    {
        var result = Run(RestRecord("REST-postCreateAgent-01", "Rest.postCreateAgent", 400, RestResultBody("", allErrors, "400")));

        Assert.Equal(expected, ResultOf(result, RestResultAllErrors));
    }

    [Theory]
    [InlineData("", Pass)]
    [InlineData("<String>Agent Nobody not found</String>", Pass)]
    [InlineData("<String/>", Pass)]
    [InlineData("<String xsi:nil=\"true\"/>", Pass)]
    [InlineData("<String><x/></String>", "wrong-type:item:element")]
    [InlineData("<Bogus>x</Bogus>", "wrong-type:item-name:Bogus")]
    [InlineData("<Bogus xsi:nil=\"true\"/>", "wrong-type:item-name:Bogus")]
    [InlineData("<String><x/></String><Bogus>x</Bogus>", "wrong-type:item-name:Bogus")]
    [InlineData("x", WrongText)]
    [InlineData("x<String>y</String>", "wrong-type:element")]
    public void SoapStringArray_IsValidOnlyWhenEveryItemIsANamedString(string allErrorsContent, string expected)
    {
        var result = Run(SoapRecord("Soap.updateAgent", SoapResultContent(allErrorsContent, "400")));

        Assert.Equal(expected, ResultOf(result, SoapResultAllErrors));
    }

    [Theory]
    [InlineData("<DTOName><name>Hank Williams</name></DTOName>", Pass)]
    [InlineData("<DTOName xsi:nil=\"true\"/><DTOName><name>Hank Williams</name></DTOName>", Pass)]
    [InlineData("<Bogus><name>Hank Williams</name></Bogus>", Unobserved)]
    [InlineData("<DTOName>Hank<name>Hank Williams</name></DTOName>", Unobserved)]
    public void SoapNameArray_WalksOnlyItsNamedDtoItems(string items, string expected)
    {
        var result = Run(SoapRecord("Soap.getAgentNames", items));

        Assert.Equal(expected, ResultOf(result, SoapName));
    }

    [Theory]
    [InlineData("""[{"name":"Hank Williams"}]""", Pass)]
    [InlineData("""[null,{"name":"Hank Williams"}]""", Pass)]
    [InlineData("""[123,"Hank Williams",[{"name":"Hank Williams"}]]""", Unobserved)]
    public void RestAgentArray_WalksOnlyObjectItems(string body, string expected)
    {
        var result = Run(RestRecord("REST-getAllAgents-01", "Rest.getAllAgents", 200, body));

        Assert.Equal(expected, ResultOf(result, RestAgentName));
    }

    [Theory]
    [InlineData("[123]", "<Bogus>x</Bogus>", "item:number", "item-name:Bogus")]
    [InlineData("""["x",{}]""", "<String><x/></String>", "item:object", "item:element")]
    public void Parse_RoundTripsCollectionItemTokens(string restAllErrors, string soapAllErrorsContent, string restToken, string soapToken)
    {
        var result = Run(
            RestRecord("REST-postCreateAgent-01", "Rest.postCreateAgent", 400, RestResultBody("", restAllErrors, "400")),
            SoapRecord("Soap.updateAgent", SoapResultContent(soapAllErrorsContent, "400")));
        Assert.Equal("wrong-type:" + restToken, ResultOf(result, RestResultAllErrors));
        Assert.Equal("wrong-type:" + soapToken, ResultOf(result, SoapResultAllErrors));

        var written = result.ToJsonNode().ToJsonString();
        var parsed = ConformanceResult.Parse(JsonNode.Parse(written));

        Assert.Equal(written, parsed.ToJsonNode().ToJsonString());
        Assert.Equal(restToken, parsed.Members.Single(member => member.ItemId == RestResultAllErrors).ObservedType);
        Assert.Equal(soapToken, parsed.Members.Single(member => member.ItemId == SoapResultAllErrors).ObservedType);
        Assert.Contains(new FailureItem(RestResultAllErrors, "wrong-type:" + restToken), parsed.Failures);
        Assert.Contains(new FailureItem(SoapResultAllErrors, "wrong-type:" + soapToken), parsed.Failures);
    }

    // ---- Undecodable bodies ---------------------------------------------------------------------

    [Theory]
    [InlineData("""{"__type":"<lone-surrogate>","name":"Hank Williams","street":"9127 Bel Air Drive"}""", false)]
    [InlineData("""{"name":"Hank Williams","street":"9127 Bel Air <lone-surrogate>"}""", false)]
    [InlineData("""{"name":"Hank Williams","street":"9127 Bel Air Drive","<lone-surrogate>":"x"}""", false)]
    [InlineData("""{"__type":"<lone-surrogate>","name":"Hank Williams","street":"9127 Bel Air Drive"}""", true)]
    [InlineData("""{"name":"Hank Williams","street":"9127 Bel Air <lone-surrogate>"}""", true)]
    public void UndecodableRestBody_ContributesNoObservation(string template, bool raw)
    {
        var result = Run(RestRecord("REST-getAgent-01", "Rest.getAgent", 200, WithLoneSurrogate(template, raw)));

        // The decodable name is not observed either: a body that cannot be read whole gives nothing.
        Assert.Equal(Unobserved, ResultOf(result, RestAgentName));
        Assert.Equal(Unobserved, ResultOf(result, RestAgentStreet));
    }

    [Fact]
    public void SeedAssertions_HoldOnTheSeedTokens()
    {
        var result = Run(RestRecord(
            ConformanceCheck.SeedAssertionCaseId,
            "Rest.getAllClients",
            200,
            """[{"name":"Brian Olsen","email":"bo@wol.com","website":"www.olsen.com"},{"name":"Pauline Wild","email":"pw@wild.com","website":""}]"""));

        Assert.Equal(new[] { "pauline-wild-website", "brian-olsen-email" }, result.SeedAssertions.Select(assertion => assertion.AssertionId));
        Assert.Equal(new[] { "\"\"", "\"bo@wol.com\"" }, result.SeedAssertions.Select(assertion => assertion.Observed));
        Assert.All(result.SeedAssertions, assertion => Assert.Equal(Pass, assertion.Result));
        Assert.Equal(Pass, ResultOf(result, RestClientName));
    }

    [Theory]
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":"<lone-surrogate>"}]""", false)]
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"<lone-surrogate>","email":"x","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":""}]""", false)]
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":"<lone-surrogate>"}]""", true)]
    public void UndecodableSeedRecord_ObservesEverySeedValueAsAbsent(string template, bool raw)
    {
        var result = Run(RestRecord(ConformanceCheck.SeedAssertionCaseId, "Rest.getAllClients", 200, WithLoneSurrogate(template, raw)));

        Assert.Equal(new[] { "pauline-wild-website", "brian-olsen-email" }, result.SeedAssertions.Select(assertion => assertion.AssertionId));
        Assert.All(result.SeedAssertions, assertion =>
        {
            Assert.Equal("absent", assertion.Observed);
            Assert.Equal("differs:absent", assertion.Result);
        });
        Assert.Equal(Unobserved, ResultOf(result, RestClientName));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private ConformanceResult Run(params BaselineRecord[] records) => _check.Run(records, NoBodyFile);

    // Every record here keeps its body as text, so the check must never ask for a body file.
    private static byte[] NoBodyFile(BaselineRecord record) =>
        throw new InvalidOperationException($"Record {record.CaseId} keeps its body as text, not in a body file.");

    private static string ResultOf(ConformanceResult result, string itemId) =>
        result.Members.Single(member => member.ItemId == itemId).Result;

    private static string RestResultBody(string guid, string allErrors, string statusCode) =>
        $$"""{"guid":"{{guid}}","allErrors":{{allErrors}},"allWarnings":[],"statusCode":{{statusCode}}}""";

    private static string SoapResultContent(string allErrorsContent, string statusCode) =>
        $"<allErrors>{allErrorsContent}</allErrors><allWarnings/><statusCode>{statusCode}</statusCode>";

    private static string WithLoneSurrogate(string template, bool raw) =>
        template.Replace(SurrogateMarker, raw ? "\uD800" : "\\uD800", StringComparison.Ordinal);

    private static BaselineRecord RestRecord(string caseId, string operation, int status, string body) =>
        Record(caseId, operation, status, "application/json; charset=utf-8", body);

    // A SOAP envelope as the stand-in writes it: document/literal wrapped, xsi bound on the envelope.
    private static BaselineRecord SoapRecord(string operation, string resultContent)
    {
        var method = operation["Soap.".Length..];
        var body =
            $"""<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"><soap:Body><{method}Response xmlns="urn:JadeWebServices/WebServiceHTTP/"><{method}Result>{resultContent}</{method}Result></{method}Response></soap:Body></soap:Envelope>""";
        return Record($"SOAP-{method}-01", operation, 200, "text/xml; charset=utf-8", body);
    }

    private static BaselineRecord Record(string caseId, string operation, int status, string contentType, string body) =>
        new(
            Order: 1,
            CaseId: caseId,
            Operation: operation,
            Kind: "success",
            StateChanging: false,
            PreState: "seed",
            Source: SourceLabels.StandIn,
            Request: new BaselineRequest("POST", "/", new Dictionary<string, string>(), null),
            Response: new BaselineResponse(
                status,
                contentType,
                body,
                BaselineResponse.ComputeSha256Hex(Encoding.UTF8.GetBytes(body)),
                null));
}
