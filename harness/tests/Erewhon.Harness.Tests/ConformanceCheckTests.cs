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
/// be decoded, a raw body holding an invalid UTF-8 sequence included, contributes nothing instead of
/// aborting the check.
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
    private const string RestSuccess = "CONF:Rest:STATUS:HTTP_Success";
    private const string SoapPhoto = "CONF:Soap:DTOPhoto.photo";
    private const string SeedPaulineWebsite = "CONF:SEED:pauline-wild-website";
    private const string SeedBrianEmail = "CONF:SEED:brian-olsen-email";
    private const string DiffersAbsent = "differs:absent";
    private const string SoapAgentName = "CONF:Soap:DTOAgent.name";
    private const string SoapItemName = "CONF:Soap:DTOItem.name";
    private const string SoapItemSale = "CONF:Soap:DTOItem.sale";
    private const string SoapRetailItemName = "CONF:Soap:DTORetailItem.name";
    private const string SoapSaleDetailCommission = "CONF:Soap:DTOSaleDetail.agentCommission";
    private const string SoapRetailSaleDetailPrice = "CONF:Soap:DTORetailSaleDetail.price";
    private const string SoapResultAllWarnings = "CONF:Soap:DTOResult.allWarnings";

    // The SOAP message namespace the catalogue names, and a namespace no SOAP evidence may come from.
    private const string TargetNamespace = "urn:JadeWebServices/WebServiceHTTP/";
    private const string ForeignNamespace = "urn:example:foreign";

    // Where an envelope template holds this marker, a test puts the stand-in's getAgent response there.
    private const string MessageMarker = "<message>";

    // Where an xsi:type template holds this marker, a test puts the class the result position declares.
    private const string DeclaredClassMarker = "<declared>";

    // The stand-in's first generated GUID.
    private const string CreatedGuid = "{00000000-0000-4000-8000-000000000001}";

    // Where a body template holds this marker, a test puts a lone high surrogate there: escaped as
    // \uD800, or as the raw UTF-16 unit, which theory data cannot carry because serialising it replaces
    // an unpaired surrogate.
    private const string SurrogateMarker = "<lone-surrogate>";

    // Where a marked record's body holds this marker, a raw-body test puts the bytes of its row there,
    // which no string can carry when they are invalid UTF-8.
    private const string RawBytesMarker = "<raw-bytes>";

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

    /// <summary>
    /// Raw bodies, as a record kept in a body file supplies them: the operation whose marked record is
    /// sent, the bytes put at its marker in hexadecimal, whether a UTF-8 byte-order mark comes first, and
    /// the result each listed member or seed-token assertion must have, as <c>itemId=result</c>. An
    /// invalid UTF-8 sequence (a byte UTF-8 never uses, a truncated or overlong sequence, an encoded
    /// surrogate) leaves the whole body undecodable, so it supplies no body observation while a REST
    /// record's status is still observed; valid bytes, with or without a byte-order mark, are read as text.
    /// </summary>
    public static TheoryData<string, string, bool, string[]> RawBodies { get; } = new()
    {
        { "Rest.getAgent", "FF", false, [$"{RestAgentName}={Unobserved}", $"{RestAgentStreet}={Unobserved}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAgent", "C3", false, [$"{RestAgentName}={Unobserved}", $"{RestAgentStreet}={Unobserved}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAgent", "C0AF", false, [$"{RestAgentName}={Unobserved}", $"{RestAgentStreet}={Unobserved}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAgent", "EDA080", true, [$"{RestAgentName}={Unobserved}", $"{RestAgentStreet}={Unobserved}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAgent", "C3A9", false, [$"{RestAgentName}={Pass}", $"{RestAgentStreet}={Pass}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAgent", "C3A9", true, [$"{RestAgentName}={Pass}", $"{RestAgentStreet}={Pass}", $"{RestSuccess}={Pass}"] },
        { "Soap.updateAgent", "FF", false, [$"{SoapResultAllErrors}={Unobserved}", $"{SoapResultStatusCode}={Unobserved}", $"{SoapBadRequest}={Unobserved}"] },
        { "Soap.updateAgent", "C3A9", false, [$"{SoapResultAllErrors}={Pass}", $"{SoapResultStatusCode}={Pass}", $"{SoapBadRequest}={Pass}"] },
        { "Soap.updateAgent", "C3A9", true, [$"{SoapResultAllErrors}={Pass}", $"{SoapResultStatusCode}={Pass}", $"{SoapBadRequest}={Pass}"] },
        { "Soap.getPhoto", "C0AF", false, [$"{SoapPhoto}={Unobserved}"] },
        { "Soap.getPhoto", "", false, [$"{SoapPhoto}={Pass}"] },
        { "Rest.getAllClients", "C3", false, [$"{SeedPaulineWebsite}={DiffersAbsent}", $"{SeedBrianEmail}={DiffersAbsent}", $"{RestClientName}={Unobserved}", $"{RestSuccess}={Pass}"] },
        { "Rest.getAllClients", "", true, [$"{SeedPaulineWebsite}={Pass}", $"{SeedBrianEmail}={Pass}", $"{RestClientName}={Pass}", $"{RestSuccess}={Pass}"] },
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
    [InlineData("<m:String xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\">Agent Nobody not found</m:String>", Pass)]
    [InlineData("<f:String xmlns:f=\"urn:example:foreign\">Agent Nobody not found</f:String>", "wrong-type:item-name:{urn:example:foreign}String")]
    [InlineData("<String xmlns=\"\">Agent Nobody not found</String>", "wrong-type:item-name:{}String")]
    [InlineData("<String>Agent Nobody not found</String><f:String xmlns:f=\"urn:example:foreign\"/>", "wrong-type:item-name:{urn:example:foreign}String")]
    [InlineData("<f:Bogus xmlns:f=\"urn:example:foreign\">x</f:Bogus>", "wrong-type:item-name:{urn:example:foreign}Bogus")]
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
    [InlineData("<m:DTOName xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><m:name>Hank Williams</m:name></m:DTOName>", Pass)]
    [InlineData("<f:DTOName xmlns:f=\"urn:example:foreign\"><name>Hank Williams</name></f:DTOName>", Unobserved)]
    [InlineData("<DTOName xmlns=\"\"><name xmlns=\"urn:JadeWebServices/WebServiceHTTP/\">Hank Williams</name></DTOName>", Unobserved)]
    [InlineData("<DTOName><f:name xmlns:f=\"urn:example:foreign\">Hank Williams</f:name></DTOName>", Unobserved)]
    [InlineData("<DTOName xsi:type=\"DTOName\"><name>Hank Williams</name></DTOName>", Pass)]
    [InlineData("<DTOName xsi:type=\"f:DTOName\" xmlns:f=\"urn:example:foreign\"><name>Hank Williams</name></DTOName>", Unobserved)]
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
    [InlineData("[true]", "<f:String xmlns:f=\"urn:example:foreign\">x</f:String>", "item:boolean", "item-name:{urn:example:foreign}String")]
    [InlineData("[[]]", "<String xmlns=\"\">x</String>", "item:array", "item-name:{}String")]
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

    // ---- Check result against the register ------------------------------------------------------

    /// <summary>How much of a result's failing items the register in a test documents.</summary>
    public enum RegisterCoverage
    {
        /// <summary>The exact pair of every failing item.</summary>
        All,

        /// <summary>The exact pair of every failing item but the first.</summary>
        AllButFirst,

        /// <summary>Nothing: <see cref="KnownLimitationsRegister.Empty"/>.</summary>
        Empty,
    }

    // {0} is the result's failing-item count; the seed record makes both seed-token assertions hold.
    [Theory]
    [InlineData(RegisterCoverage.All, CheckOutcomes.Pass, "{0} of {0} failing items documented by the register; 2 of 2 seed-token assertions hold")]
    [InlineData(RegisterCoverage.AllButFirst, CheckOutcomes.Fail, "1 of {0} failing items not documented by the register; 2 of 2 seed-token assertions hold")]
    [InlineData(RegisterCoverage.Empty, CheckOutcomes.Fail, "{0} of {0} failing items not documented by the register; 2 of 2 seed-token assertions hold")]
    public void ToCheckResult_PassesOnlyWhenTheRegisterDocumentsEveryFailingItem(RegisterCoverage coverage, string expectedOutcome, string expectedDetail)
    {
        var result = Run(ConformanceSeedRecord());
        Assert.NotEmpty(result.Failures);
        var register = coverage switch
        {
            RegisterCoverage.All => RegisterOf(result.Failures),
            RegisterCoverage.AllButFirst => RegisterOf(result.Failures.Skip(1)),
            _ => KnownLimitationsRegister.Empty,
        };

        var check = result.ToCheckResult(register);

        Assert.Equal(CheckNames.Conformance, check.Name);
        Assert.Equal(expectedOutcome, check.Outcome);
        Assert.Equal(result.Members.Count + result.SeedAssertions.Count, check.Cases);
        Assert.Equal(result.Counts.Passed + result.SeedAssertions.Count, check.Passed);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, expectedDetail, result.Failures.Count), check.Detail);
    }

    [Fact]
    public void ToCheckResult_WithoutFailingItems_PassesAndStatesTheSeedTokenAssertions()
    {
        var result = new ConformanceResult(
            [],
            [new SeedAssertionResult("pauline-wild-website", "CONF:SEED:pauline-wild-website", "\"\"", "\"\"", Pass)]);

        var check = result.ToCheckResult(KnownLimitationsRegister.Empty);

        Assert.Empty(result.Failures);
        Assert.Equal(CheckOutcomes.Pass, check.Outcome);
        Assert.Equal("1 of 1 seed-token assertions hold", check.Detail);
    }

    [Fact]
    public void ToCheckResult_RejectsANullRegister() =>
        Assert.Throws<ArgumentNullException>("register", () => Run(ConformanceSeedRecord()).ToCheckResult(null!));

    private static BaselineRecord ConformanceSeedRecord() => RestRecord(
        ConformanceCheck.SeedAssertionCaseId,
        "Rest.getAllClients",
        200,
        """[{"name":"Brian Olsen","email":"bo@wol.com","website":"www.olsen.com"},{"name":"Pauline Wild","email":"pw@wild.com","website":""}]""");

    // Built through the register's own parser, as the harness loads harness/config/known-limitations.json.
    private static KnownLimitationsRegister RegisterOf(IEnumerable<FailureItem> documented)
    {
        var matches = new JsonArray();
        foreach (var item in documented)
        {
            matches.Add(new JsonObject { ["itemId"] = item.ItemId, ["nature"] = item.Nature });
        }

        var document = new JsonObject
        {
            ["version"] = KnownLimitationsRegister.SupportedVersion,
            ["entries"] = new JsonArray(new JsonObject
            {
                ["id"] = "KL-CONF-TEST",
                ["category"] = RegisterCategories.ConformanceGap,
                ["matches"] = matches,
                ["reason"] = "registered conformance gaps",
                ["attempts"] = 0,
                ["actionTaken"] = "registered",
            }),
        };

        return KnownLimitationsRegister.Parse(document.ToJsonString());
    }

    // ---- Raw bodies -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RawBodies))]
    public void RawBody_IsDecodedAsStrictUtf8(string operation, string bytesHex, bool bom, string[] expected)
    {
        var result = RunRaw(AsRawRecord(MarkedRecord(operation), bytesHex, bom));

        foreach (var expectation in expected)
        {
            var itemId = expectation[..expectation.IndexOf('=', StringComparison.Ordinal)];
            Assert.Equal(expectation, $"{itemId}={MemberOrSeedResultOf(result, itemId)}");
        }
    }

    // ---- SOAP expanded names -------------------------------------------------------------------

    /// <summary>
    /// The envelope and its body are SOAP evidence only as <c>Envelope</c> and <c>Body</c> in the SOAP 1.1
    /// envelope namespace, whatever prefix spells them; the same names in no namespace, in the SOAP 1.2
    /// namespace or in any other give no observation.
    /// </summary>
    [Theory]
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><e:Body><message></e:Body></e:Envelope>", Pass)]
    [InlineData("<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\"><Body><message></Body></Envelope>", Pass)]
    [InlineData("<Envelope><Body><message></Body></Envelope>", Unobserved)]
    [InlineData("<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\"><e:Body><message></e:Body></e:Envelope>", Unobserved)]
    [InlineData("<e:Envelope xmlns:e=\"urn:example:foreign\"><b:Body xmlns:b=\"http://schemas.xmlsoap.org/soap/envelope/\"><message></b:Body></e:Envelope>", Unobserved)]
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><Body><message></Body></e:Envelope>", Unobserved)]
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><b:Body xmlns:b=\"urn:example:foreign\"><message></b:Body></e:Envelope>", Unobserved)]
    public void SoapEnvelope_IsEvidenceOnlyInTheSoap11EnvelopeNamespace(string envelopeTemplate, string expected)
    {
        var message = $"""<getAgentResponse xmlns="{TargetNamespace}"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>""";
        var result = Run(SoapBodyRecord("Soap.getAgent", envelopeTemplate.Replace(MessageMarker, message, StringComparison.Ordinal)));

        Assert.Equal(expected, ResultOf(result, SoapAgentName));
    }

    /// <summary>
    /// The <c>&lt;op&gt;Response</c> wrapper, its <c>&lt;op&gt;Result</c> and every property element are
    /// evidence only in the target namespace: a prefixed form passes like the default-namespace form, and
    /// the same local names in no namespace, in a near-miss namespace or in another namespace give no
    /// observation, so a property found only there stays unobserved and is never judged by its value.
    /// </summary>
    [Theory]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Pass)]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><m:getAgentResult><m:name>Hank Williams</m:name></m:getAgentResult></m:getAgentResponse>", Pass)]
    [InlineData("<getAgentResponse><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:example:foreign\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><m:name>Hank Williams</m:name></getAgentResult></m:getAgentResponse>", Unobserved)]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><f:getAgentResult xmlns:f=\"urn:example:foreign\"><m:name>Hank Williams</m:name></f:getAgentResult></m:getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\">Hank Williams</f:name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><name xmlns=\"\">Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\"><x/></f:name></getAgentResult></getAgentResponse>", Unobserved)]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\">x</f:name><name>Hank Williams</name></getAgentResult></getAgentResponse>", Pass)]
    public void SoapMessageElements_AreEvidenceOnlyInTheTargetNamespace(string message, string expected)
    {
        var result = Run(SoapBodyRecord("Soap.getAgent", SoapEnvelope(message)));

        Assert.Equal(expected, ResultOf(result, SoapAgentName));
    }

    /// <summary>
    /// A <c>DTOResult</c>'s <c>statusCode</c> is evidence of the member and of the status constant it
    /// carries only in the target namespace.
    /// </summary>
    [Theory]
    [InlineData("<statusCode>400</statusCode>", Pass)]
    [InlineData("<m:statusCode xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\">400</m:statusCode>", Pass)]
    [InlineData("<f:statusCode xmlns:f=\"urn:example:foreign\">400</f:statusCode>", Unobserved)]
    [InlineData("<statusCode xmlns=\"\">400</statusCode>", Unobserved)]
    public void SoapStatusCode_IsEvidenceOnlyInTheTargetNamespace(string statusCodeElement, string expected)
    {
        var result = Run(SoapRecord("Soap.updateAgent", $"<allErrors/><allWarnings/>{statusCodeElement}"));

        Assert.Equal(expected, ResultOf(result, SoapResultStatusCode));
        Assert.Equal(expected, ResultOf(result, SoapBadRequest));
    }

    /// <summary>
    /// A result's <c>xsi:type</c> is a QName resolved by its bound prefix, or by the default namespace when
    /// unprefixed; it is valid only when it names a target-namespace DTO of the declared kind, here the
    /// declared class itself, because no SOAP result class of the catalogue has a subclass. Any other
    /// <c>xsi:type</c> makes the result an invalid structure that gives no member observation and no status
    /// code, for a <c>DTOAgent</c> result and a <c>DTOResult</c> alike.
    /// </summary>
    [Theory]
    [InlineData("", Pass)]
    [InlineData("xsi:type=\"<declared>\"", Pass)]
    [InlineData("xsi:type=\"m:<declared>\" xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"", Pass)]
    [InlineData("xsi:type=\" m:<declared>&#10;\" xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"", Pass)]
    [InlineData("xsi:type=\"f:<declared>\" xmlns:f=\"urn:example:foreign\"", Unobserved)]
    [InlineData("xsi:type=\"xsd:<declared>\"", Unobserved)]
    [InlineData("xsi:type=\"<declared>\" xmlns=\"\"", Unobserved)]
    [InlineData("xsi:type=\"unbound:<declared>\"", Unobserved)]
    [InlineData("xsi:type=\"m:<declared>:x\" xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"", Unobserved)]
    [InlineData("xsi:type=\"&#160;<declared>\"", Unobserved)]
    [InlineData("xsi:type=\"\"", Unobserved)]
    [InlineData("xsi:type=\"DTOUnknown\"", Unobserved)]
    [InlineData("xsi:type=\"DTOClient\"", Unobserved)]
    public void SoapResultXsiType_IsValidOnlyForATargetNamespaceDtoOfTheDeclaredKind(string attributesTemplate, string expected)
    {
        // Every message element carries the prefix r, so an xmlns="" undeclaration on the result changes
        // only how an unprefixed xsi:type resolves.
        var agent = SoapBodyRecord(
            "Soap.getAgent",
            SoapEnvelope($"""<r:getAgentResponse xmlns:r="{TargetNamespace}" xmlns="{TargetNamespace}"><r:getAgentResult {attributesTemplate.Replace(DeclaredClassMarker, "DTOAgent", StringComparison.Ordinal)}><r:name>Hank Williams</r:name></r:getAgentResult></r:getAgentResponse>"""));
        var update = SoapBodyRecord(
            "Soap.updateAgent",
            SoapEnvelope($"""<r:updateAgentResponse xmlns:r="{TargetNamespace}" xmlns="{TargetNamespace}"><r:updateAgentResult {attributesTemplate.Replace(DeclaredClassMarker, "DTOResult", StringComparison.Ordinal)}><r:allErrors/><r:statusCode>400</r:statusCode></r:updateAgentResult></r:updateAgentResponse>"""));

        var result = Run(agent, update);

        Assert.Equal(expected, ResultOf(result, SoapAgentName));
        Assert.Equal(expected, ResultOf(result, SoapResultAllErrors));
        Assert.Equal(expected, ResultOf(result, SoapResultStatusCode));
        Assert.Equal(expected, ResultOf(result, SoapBadRequest));
    }

    /// <summary>
    /// <c>xsi:type</c> switching where a SOAP result can hold a subclass. The real catalogue has no such
    /// SOAP result, so these rows run against <see cref="TypedSoapCatalogue"/>, in which <c>getPhoto</c>
    /// returns a <c>DTOItem</c> whose <c>sale</c> is declared <c>DTOSaleDetail</c>. Each row holds the
    /// result's attributes, the sale's attributes and each listed member's result as <c>itemId=result</c>.
    /// A target-namespace subclass, prefixed or by default namespace, switches the walked class; any other
    /// <c>xsi:type</c> on the result gives nothing, and on the sale makes <c>DTOItem.sale</c> a wrong type
    /// named by its resolved Clark name, or <c>xsi-type:unresolved</c>, and leaves the subclass members
    /// unobserved.
    /// </summary>
    public static TheoryData<string, string, string[]> TypedSoapXsiTypes { get; } = new()
    {
        { "", "", [$"{SoapItemName}={Pass}", $"{SoapItemSale}={Pass}", $"{SoapSaleDetailCommission}={Pass}", $"{SoapRetailSaleDetailPrice}={Unobserved}", $"{SoapRetailItemName}={Unobserved}"] },
        { "", "xsi:type=\"DTORetailSaleDetail\"", [$"{SoapItemSale}={Pass}", $"{SoapSaleDetailCommission}={Pass}", $"{SoapRetailSaleDetailPrice}={Pass}"] },
        { "", $"xsi:type=\"m:DTORetailSaleDetail\" xmlns:m=\"{TargetNamespace}\"", [$"{SoapItemSale}={Pass}", $"{SoapRetailSaleDetailPrice}={Pass}"] },
        { "", $"xsi:type=\"f:DTORetailSaleDetail\" xmlns:f=\"{ForeignNamespace}\"", [$"{SoapItemName}={Pass}", $"{SoapItemSale}=wrong-type:xsi-type:{{{ForeignNamespace}}}DTORetailSaleDetail", $"{SoapSaleDetailCommission}={Unobserved}", $"{SoapRetailSaleDetailPrice}={Unobserved}"] },
        { "", "xsi:type=\"unbound:DTORetailSaleDetail\"", [$"{SoapItemSale}=wrong-type:xsi-type:unresolved", $"{SoapRetailSaleDetailPrice}={Unobserved}"] },
        { "", "xsi:type=\"DTORetailItem\"", [$"{SoapItemSale}=wrong-type:xsi-type:{{{TargetNamespace}}}DTORetailItem", $"{SoapSaleDetailCommission}={Unobserved}"] },
        { "xsi:type=\"DTORetailItem\"", "xsi:type=\"DTORetailSaleDetail\"", [$"{SoapRetailItemName}={Pass}", $"{SoapItemName}={Pass}", $"{SoapItemSale}={Pass}", $"{SoapRetailSaleDetailPrice}={Pass}"] },
        { $"xsi:type=\"f:DTORetailItem\" xmlns:f=\"{ForeignNamespace}\"", "xsi:type=\"DTORetailSaleDetail\"", [$"{SoapRetailItemName}={Unobserved}", $"{SoapItemName}={Unobserved}", $"{SoapItemSale}={Unobserved}", $"{SoapRetailSaleDetailPrice}={Unobserved}"] },
    };

    [Theory]
    [MemberData(nameof(TypedSoapXsiTypes))]
    public void TypedSoapXsiType_SwitchesOnlyToATargetNamespaceDtoOfTheDeclaredKind(string resultAttributes, string saleAttributes, string[] expected)
    {
        var check = new ConformanceCheck(TypedSoapCatalogue());
        var record = SoapBodyRecord(
            "Soap.getPhoto",
            SoapEnvelope($"""<getPhotoResponse xmlns="{TargetNamespace}"><getPhotoResult {resultAttributes}><name>Desk</name><sale {saleAttributes}><agentCommission>12.50</agentCommission><price>250.00</price></sale></getPhotoResult></getPhotoResponse>"""));

        var result = check.Run([record], NoBodyFile);

        foreach (var expectation in expected)
        {
            var itemId = expectation[..expectation.IndexOf('=', StringComparison.Ordinal)];
            Assert.Equal(expectation, $"{itemId}={ResultOf(result, itemId)}");
        }

        // Every token, the xsi-type ones included, survives baseline/conformance.json.
        var written = result.ToJsonNode().ToJsonString();
        Assert.Equal(written, ConformanceResult.Parse(JsonNode.Parse(written)).ToJsonNode().ToJsonString());
    }

    /// <summary>
    /// A collection of DTO items is valid only when every item is the array's item element in the target
    /// namespace with no or a valid <c>xsi:type</c>. The real catalogue has no SOAP collection of DTOs, so
    /// these rows run against <see cref="TypedSoapCatalogue"/>, in which <c>DTOResult.allWarnings</c> is a
    /// reachable <c>DTONameArray</c>.
    /// </summary>
    [Theory]
    [InlineData("<DTOName><name>Hank Williams</name></DTOName>", Pass)]
    [InlineData("<DTOName xsi:type=\"DTOName\"><name>Hank Williams</name></DTOName>", Pass)]
    [InlineData("<DTOName xsi:type=\"f:DTOName\" xmlns:f=\"urn:example:foreign\"><name>Hank Williams</name></DTOName>", "wrong-type:item:xsi-type:{urn:example:foreign}DTOName")]
    [InlineData("<DTOName xsi:type=\"unbound:DTOName\"><name>Hank Williams</name></DTOName>", "wrong-type:item:xsi-type:unresolved")]
    [InlineData("<f:DTOName xmlns:f=\"urn:example:foreign\"><name>Hank Williams</name></f:DTOName>", "wrong-type:item-name:{urn:example:foreign}DTOName")]
    public void TypedSoapDtoCollection_IsValidOnlyWithTargetNamespaceItemsOfAValidType(string allWarningsContent, string expected)
    {
        var check = new ConformanceCheck(TypedSoapCatalogue());

        var result = check.Run([SoapRecord("Soap.updateAgent", $"<allErrors/><allWarnings>{allWarningsContent}</allWarnings><statusCode>200</statusCode>")], NoBodyFile);

        Assert.Equal(expected, ResultOf(result, SoapResultAllWarnings));
    }

    [Theory]
    [InlineData("", "envelopeNamespace")]
    [InlineData(" ", "targetNamespace")]
    public void Constructor_RejectsABlankSoapNamespace(string blank, string key)
    {
        var document = CatalogueDocument();
        document["addresses"]!["soap"]![key] = blank;
        var catalogue = OperationCatalogue.Parse(document.ToJsonString());

        var exception = Assert.Throws<InvalidDataException>(() => new ConformanceCheck(catalogue));
        Assert.Contains($"addresses.soap.{key}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The embedded catalogue with SOAP positions that can carry subclasses: <c>getPhoto</c> returns
    /// <c>DTOItem</c>, every SOAP member of the item and sale-detail classes is reachable, and
    /// <c>DTOResult.allWarnings</c> is a reachable <c>DTONameArray</c>.
    /// </summary>
    private static OperationCatalogue TypedSoapCatalogue()
    {
        var document = CatalogueDocument();
        foreach (var operation in document["operations"]!.AsArray())
        {
            if (operation!["id"]!.GetValue<string>() != "Soap.getPhoto")
            {
                continue;
            }

            operation["returnType"] = "DTOItem";
            foreach (var status in operation["statuses"]!.AsArray())
            {
                if (status!["bodyType"]!.GetValue<string>() == "DTOPhoto")
                {
                    status["bodyType"] = "DTOItem";
                }
            }
        }

        string[] typedClasses = ["DTOItem", "DTORetailItem", "DTOTenderItem", "DTOSaleDetail", "DTORetailSaleDetail", "DTOTenderSaleDetail"];
        foreach (var dto in document["dtos"]!.AsArray())
        {
            var name = dto!["name"]!.GetValue<string>();
            foreach (var property in dto["properties"]!.AsArray())
            {
                var reachability = property!["reachability"];
                if (reachability is null)
                {
                    continue;
                }

                if (typedClasses.Contains(name) && reachability["Soap"]!.GetValue<string>() == UnreachableInSource)
                {
                    reachability["Soap"] = "reachable";
                }

                if (name == "DTOResult" && property["name"]!.GetValue<string>() == "allWarnings")
                {
                    property["jadeType"] = "DTONameArray";
                    property["xmlType"] = $"{{{TargetNamespace}}}ArrayOfDTOName";
                    reachability["Soap"] = "reachable";
                }
            }
        }

        return OperationCatalogue.Parse(document.ToJsonString());
    }

    private static JsonObject CatalogueDocument()
    {
        using var stream = typeof(OperationCatalogue).Assembly.GetManifestResourceStream(OperationCatalogue.ResourceName)
            ?? throw new InvalidOperationException($"The contracts assembly embeds no {OperationCatalogue.ResourceName}.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    // A SOAP 1.1 envelope as the stand-in writes it, holding message as the body's only child.
    private static string SoapEnvelope(string message) =>
        $"""<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"><soap:Body>{message}</soap:Body></soap:Envelope>""";

    private static BaselineRecord SoapBodyRecord(string operation, string body) =>
        Record($"SOAP-{operation["Soap.".Length..]}-01", operation, 200, "text/xml; charset=utf-8", body);

    // ---- Helpers --------------------------------------------------------------------------------

    private ConformanceResult Run(params BaselineRecord[] records) => _check.Run(records, NoBodyFile);

    // The check asks for the body bytes of the raw record only, which keeps no body text.
    private ConformanceResult RunRaw((BaselineRecord Record, byte[] Body) raw) =>
        _check.Run([raw.Record], record => ReferenceEquals(record, raw.Record) ? raw.Body : NoBodyFile(record));

    // A record built by Record keeps its body as text, so the check must never ask for its body file.
    private static byte[] NoBodyFile(BaselineRecord record) =>
        throw new InvalidOperationException($"Record {record.CaseId} keeps its body as text, not in a body file.");

    private static string ResultOf(ConformanceResult result, string itemId) =>
        result.Members.Single(member => member.ItemId == itemId).Result;

    private static string MemberOrSeedResultOf(ConformanceResult result, string itemId) =>
        result.SeedAssertions.SingleOrDefault(assertion => assertion.ItemId == itemId)?.Result ?? ResultOf(result, itemId);

    // The record each raw-body row sends, its body holding RawBytesMarker inside a string value or element.
    private static BaselineRecord MarkedRecord(string operation) => operation switch
    {
        "Rest.getAgent" => RestRecord(
            "REST-getAgent-01",
            operation,
            200,
            """{"name":"Hank Williams","street":"9127 Bel Air <raw-bytes>"}"""),
        "Rest.getAllClients" => RestRecord(
            ConformanceCheck.SeedAssertionCaseId,
            operation,
            200,
            """[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":"<raw-bytes>"}]"""),
        "Soap.updateAgent" => SoapRecord(operation, SoapResultContent("<String>Agent name is required<raw-bytes></String>", "400")),
        "Soap.getPhoto" => SoapRecord(operation, "<photo>/9j/4AAQ<raw-bytes></photo>"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "No marked record is defined for this operation."),
    };

    /// <summary>
    /// <paramref name="textRecord"/> as a record kept in a body file: no <c>bodyText</c>, and as its body
    /// the UTF-8 of its text with the bytes of <paramref name="bytesHex"/> in place of
    /// <see cref="RawBytesMarker"/>, after a UTF-8 byte-order mark when <paramref name="bom"/> is set.
    /// </summary>
    private static (BaselineRecord Record, byte[] Body) AsRawRecord(BaselineRecord textRecord, string bytesHex, bool bom)
    {
        var text = textRecord.Response.BodyText
            ?? throw new ArgumentException($"Record {textRecord.CaseId} keeps no body text.", nameof(textRecord));
        var marker = text.IndexOf(RawBytesMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new ArgumentException($"The body of record {textRecord.CaseId} holds no {RawBytesMarker} marker.", nameof(textRecord));
        }

        var body = new List<byte>();
        if (bom)
        {
            body.AddRange(Encoding.UTF8.GetPreamble());
        }

        body.AddRange(Encoding.UTF8.GetBytes(text[..marker]));
        body.AddRange(Convert.FromHexString(bytesHex));
        body.AddRange(Encoding.UTF8.GetBytes(text[(marker + RawBytesMarker.Length)..]));
        var raw = body.ToArray();

        var response = BaselineResponse.FromRawBody(
            textRecord.Response.Status, textRecord.Response.ContentType, raw, BaselineStore.BodyFileName(textRecord));
        return (textRecord with { Response = response }, raw);
    }

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
