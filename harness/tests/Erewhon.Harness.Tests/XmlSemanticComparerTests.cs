using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Semantic comparison of SOAP 1.1 envelopes and WSDL documents: which differences are ignored (attribute
/// order, prefix choice, namespace declarations, comments and processing instructions, whitespace-only text in
/// element-only containers, the order of differently named siblings) and which are significant (array item
/// order, leaf text including whitespace, QName resolution, nil results, unnormalised SOAP values, missing and
/// extra elements). Operation, element and namespace names come from the contracts catalogue, and every
/// equivalence has a neighbouring case that must differ, so a comparer that never reports cannot pass.
/// The response-level cases send SOAP bodies through <see cref="ResponseComparer"/>: XML dispatch, the
/// media type, well-formedness as a SOAP 1.1 envelope, the byte-order mark and strict UTF-8 decoding.
/// </summary>
public sealed class XmlSemanticComparerTests
{
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";
    private const string OtherNamespace = "urn:other";

    // The depth of a result's children: Envelope 0, Body 1, the <op>Response wrapper 2, the <op>Result 3.
    private const int ResultChildDepth = 4;

    // The portType, binding and port share this name, as in the stand-in's WSDL.
    private const string BindingName = "ErewhonWebServiceSoap";
    private const string Documentation = "Modelled";
    private const string StandInHeaderComment =
        "<!-- Modelled by the JADE stand-in on JADE 2022 default conventions; not captured from live JADE -->";
    private const string LocationPath = "/definitions/service/port/address/@location";

    private const string GuidA = "{00000000-0000-4000-8000-000000000001}";
    private const string GuidB = "{eaf359ee-e216-ee11-9fbd-0050568d0023}";

    // A SOAP 1.2 envelope is XML, but not the SOAP 1.1 envelope a well-formed response must be.
    private const string Soap12EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;
    private static readonly SoapAddress SoapAddressing = Catalogue.Addresses.Soap;
    private static readonly string TargetNamespace = SoapAddressing.TargetNamespace;
    private static readonly string EnvelopeNamespace = SoapAddressing.EnvelopeNamespace;
    private static readonly string WsdlNamespace = SoapAddressing.WsdlNamespaces["wsdl"];
    private static readonly string WsdlSoapNamespace = SoapAddressing.WsdlNamespaces["soap"];

    private static readonly OperationDescriptor GetAgent = Catalogue.GetById("Soap.getAgent");
    private static readonly OperationDescriptor GetAgentNames = Catalogue.GetById("Soap.getAgentNames");

    private static readonly ArrayDefinition NameArray = Catalogue.TryGetArray(GetAgentNames.ReturnType, out var array)
        ? array
        : throw new InvalidOperationException("The catalogue declares no array for the getAgentNames return type.");

    private static readonly string NameItem = NameArray.SoapItemElement
        ?? throw new InvalidOperationException("The getAgentNames array has no SOAP item element.");

    private static readonly string NameProperty = Catalogue.GetDto(NameArray.ItemType).SoapProperties.Single().Name;

    private static readonly XmlComparisonContext AgentResponse = XmlComparisonContext.SoapResponse(GetAgent);
    private static readonly XmlComparisonContext AgentNamesResponse = XmlComparisonContext.SoapResponse(GetAgentNames);

    private static readonly string AgentResultPath = ResultPath(GetAgent);
    private static readonly string AgentNamesResultPath = ResultPath(GetAgentNames);

    private static readonly string StandInLocation =
        $"http://standin:{SoapAddressing.Port}{SoapAddressing.EndpointPathAndQuery}";

    private static readonly string FacadeLocation =
        $"http://localhost:{SoapAddressing.Port}{SoapAddressing.EndpointPathAndQuery}";

    // Every SOAP outcome of getAgent shares one HTTP status; business errors travel in the result.
    private static readonly int SoapStatus = GetAgent.Statuses.Select(status => status.HttpStatus).Distinct().Single();
    private static readonly string SoapContentType = SoapAddressing.ContentType;

    /// <summary>
    /// Hank Williams as the seed loads him (Erewhon/DataFiles/Agents.txt:4): the eight properties the
    /// <c>WebServiceHTTP</c> list exposes for <c>DTOAgent</c>, in loader field order, fax before phone.
    /// </summary>
    private static readonly (string Name, string Text)[] HankWilliams =
    [
        ("name", "Hank Williams"),
        ("street", "9127 Bel Air Drive"),
        ("city", "Los Angeles"),
        ("country", "United States of America"),
        ("email", "hank@it.com"),
        ("fax", "1 8 6354 7364"),
        ("phone", "1 8 2938 4756"),
        ("website", "www.it.com"),
    ];

    private readonly XmlSemanticComparer _comparer = new(
        OperationCatalogue.Default,
        new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)));

    private readonly ResponseComparer _responses = new(
        OperationCatalogue.Default,
        new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)));

    // ---- Fixture ---------------------------------------------------------------------------------

    [Fact]
    public void AgentFixture_HoldsExactlyTheSoapExposedDtoAgentProperties()
    {
        var exposed = Catalogue.GetDto(GetAgent.ReturnType).SoapProperties.Select(property => property.Name);

        Assert.Equal(exposed.Order(StringComparer.Ordinal), HankWilliams.Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    // ---- Ignored differences ---------------------------------------------------------------------

    [Fact]
    public void AttributeOrder_IsIgnored()
    {
        var forward = $" xsi:type=\"tns:{GetAgent.ReturnType}\" xmlns:tns=\"{TargetNamespace}\" x:flag=\"1\" xmlns:x=\"urn:test\"";
        var reversed = $" xmlns:x=\"urn:test\" x:flag=\"1\" xmlns:tns=\"{TargetNamespace}\" xsi:type=\"tns:{GetAgent.ReturnType}\"";
        var changed = $" xmlns:x=\"urn:test\" x:flag=\"2\" xmlns:tns=\"{TargetNamespace}\" xsi:type=\"tns:{GetAgent.ReturnType}\"";
        var baseline = Envelope(GetAgent, Agent(), resultAttributes: forward);

        AssertEquivalent(baseline, Envelope(GetAgent, Agent(), resultAttributes: reversed), AgentResponse);

        var discrepancy = Assert.Single(_comparer.Compare(baseline, Envelope(GetAgent, Agent(), resultAttributes: changed), AgentResponse));
        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/@{{urn:test}}flag", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
            discrepancy);
    }

    [Fact]
    public void PrefixChoice_IsIgnored()
    {
        var baseline = Envelope(GetAgent, Agent());

        AssertEquivalent(baseline, Envelope(GetAgent, Agent(), envPrefix: "s"), AgentResponse);
        AssertEquivalent(baseline, Envelope(GetAgent, Agent(prefix: "tns"), bodyPrefix: "tns"), AgentResponse);

        // The same local names in another namespace are other elements.
        var discrepancies = _comparer.Compare(baseline, Envelope(GetAgent, Agent(), bodyNamespace: OtherNamespace), AgentResponse);
        Assert.Equal(2, discrepancies.Count);
        Assert.Contains(
            discrepancies,
            d => d.Kind == DiscrepancyKinds.Missing && d.Path == $"/Envelope/Body/{{{TargetNamespace}}}{GetAgent.Soap!.ResponseElement}");
        Assert.Contains(
            discrepancies,
            d => d.Kind == DiscrepancyKinds.Extra && d.Path == $"/Envelope/Body/{{{OtherNamespace}}}{GetAgent.Soap!.ResponseElement}");
    }

    [Fact]
    public void XmlnsDeclarations_AreIgnored()
    {
        var baseline = Envelope(GetAgent, Agent());

        AssertEquivalent(baseline, Envelope(GetAgent, Agent(), envelopeAttributes: " xmlns:extra=\"urn:extra\""), AgentResponse);

        var discrepancy = Assert.Single(
            _comparer.Compare(baseline, Envelope(GetAgent, Agent(), envelopeAttributes: " xmlns:extra=\"urn:extra\" extra:flag=\"1\""), AgentResponse));
        Assert.Equal(
            new Discrepancy("/Envelope/@{urn:extra}flag", DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("1")),
            discrepancy);
    }

    [Fact]
    public void CommentsAndProcessingInstructions_AreDropped()
    {
        var baseline = Envelope(GetAgent, Agent());

        AssertEquivalent(baseline, InsertAfter(Envelope(GetAgent, "<?trace id?>" + Agent()), "<soap:Body>", "<!-- note -->"), AgentResponse);

        // A comment inside a leaf joins the text on either side of it into one value.
        AssertEquivalent(baseline, Envelope(GetAgent, AgentWith("name", "<name>Hank<!-- split --> Williams</name>")), AgentResponse);

        var discrepancy = Assert.Single(_comparer.Compare(baseline, InsertAfter(baseline, "<soap:Body>", "<note/>"), AgentResponse));
        Assert.Equal(DiscrepancyKinds.Extra, discrepancy.Kind);
        Assert.Equal("/Envelope/Body/note", discrepancy.Path);
    }

    [Fact]
    public void IndentationInsideElementOnlyContainers_IsIgnored()
    {
        var compact = Envelope(GetAgent, Agent(indent: false), indent: false);
        var indented = Envelope(GetAgent, Agent());
        Assert.DoesNotContain('\n', compact);
        Assert.Contains('\n', indented);

        AssertEquivalent(compact, indented, AgentResponse);

        // The same indentation inside a leaf is part of its value.
        const string IndentedName = "\n        Hank Williams\n      ";
        var discrepancy = Assert.Single(
            _comparer.Compare(indented, Envelope(GetAgent, AgentWith("name", $"<name>{IndentedName}</name>")), AgentResponse));
        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/name/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Hank Williams"), DiscrepancyValue.Literal(IndentedName)),
            discrepancy);
    }

    [Fact]
    public void RequestWrapperIndentation_IsIgnored_ParameterTextKept()
    {
        var request = XmlComparisonContext.SoapRequest(GetAgent);
        var parameter = GetAgent.Parameters.Single().Name;
        var baseline = RequestEnvelope(GetAgent, "Hank Williams", indent: false);

        AssertEquivalent(baseline, RequestEnvelope(GetAgent, "Hank Williams"), request);

        var discrepancy = Assert.Single(_comparer.Compare(baseline, RequestEnvelope(GetAgent, "Hank Williams "), request));
        Assert.Equal(
            new Discrepancy(
                $"/Envelope/Body/{GetAgent.Soap!.RequestElement}/{parameter}/text()",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal("Hank Williams"),
                DiscrepancyValue.Literal("Hank Williams ")),
            discrepancy);
    }

    [Fact]
    public void DifferentlyNamedSiblings_MatchInAnyOrder()
    {
        var baseline = Envelope(GetAgent, Agent());
        var reordered = AgentElements().ToList();
        var city = reordered.FindIndex(element => element.Name == "city");
        var country = reordered.FindIndex(element => element.Name == "country");
        (reordered[city], reordered[country]) = (reordered[country], reordered[city]);

        AssertEquivalent(baseline, Envelope(GetAgent, Join(reordered.Select(element => element.Markup))), AgentResponse);

        // Exchanging the values instead of the elements is a difference at both names.
        var exchanged = Envelope(
            GetAgent,
            Join(AgentElements().Select(element => element.Name switch
            {
                "city" => "<city>United States of America</city>",
                "country" => "<country>Los Angeles</country>",
                _ => element.Markup,
            })));
        Discrepancy[] expected =
        [
            new($"{AgentResultPath}/city/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Los Angeles"), DiscrepancyValue.Literal("United States of America")),
            new($"{AgentResultPath}/country/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("United States of America"), DiscrepancyValue.Literal("Los Angeles")),
        ];
        Assert.Equal(expected, _comparer.Compare(baseline, exchanged, AgentResponse));
    }

    // ---- Significant differences -----------------------------------------------------------------

    [Fact]
    public void RepeatedItems_CompareInDocumentOrder()
    {
        var baseline = Envelope(GetAgentNames, Names(indent: true, "Airi Hamada", "Hank Williams"));

        AssertEquivalent(baseline, Envelope(GetAgentNames, Names(indent: false, "Airi Hamada", "Hank Williams"), indent: false), AgentNamesResponse);

        var swapped = Envelope(GetAgentNames, Names(indent: true, "Hank Williams", "Airi Hamada"));
        Discrepancy[] expected =
        [
            new($"{AgentNamesResultPath}/{NameItem}[1]/{NameProperty}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Airi Hamada"), DiscrepancyValue.Literal("Hank Williams")),
            new($"{AgentNamesResultPath}/{NameItem}[2]/{NameProperty}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Hank Williams"), DiscrepancyValue.Literal("Airi Hamada")),
        ];
        Assert.Equal(expected, _comparer.Compare(baseline, swapped, AgentNamesResponse));
    }

    [Theory]
    [InlineData("<website> </website>", "<website/>", " ", null)]
    [InlineData("<website/>", "<website> </website>", null, " ")]
    [InlineData("<website> </website>", "<website></website>", " ", null)]
    public void WhitespaceOnlyLeaf_Differs(string baselineWebsite, string observedWebsite, string? baselineText, string? observedText)
    {
        var discrepancy = Assert.Single(
            _comparer.Compare(Envelope(GetAgent, Agent(baselineWebsite)), Envelope(GetAgent, Agent(observedWebsite)), AgentResponse));

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/website/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineText), DiscrepancyValue.Literal(observedText)),
            discrepancy);
    }

    [Fact]
    public void EmptyElementForms_AreEqual()
    {
        AssertEquivalent(Envelope(GetAgent, Agent("<website></website>")), Envelope(GetAgent, Agent("<website/>")), AgentResponse);

        // An empty website is still a value: it differs from the populated one.
        Assert.NotEmpty(_comparer.Compare(Envelope(GetAgent, Agent()), Envelope(GetAgent, Agent("<website/>")), AgentResponse));
    }

    [Fact]
    public void PaddedLeafText_Differs()
    {
        var baseline = Envelope(GetAgent, AgentWith("email", "<email>bo@wol.com</email>"));

        var discrepancy = Assert.Single(_comparer.Compare(baseline, Envelope(GetAgent, AgentWith("email", "<email>bo@wol.com </email>")), AgentResponse));

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/email/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("bo@wol.com"), DiscrepancyValue.Literal("bo@wol.com ")),
            discrepancy);
    }

    [Fact]
    public void MixedContentWhitespace_IsKept()
    {
        const string Kept = "<v>a<b/> </v>";
        const string Bare = "<v>a<b/></v>";

        var discrepancy = Assert.Single(_comparer.Compare(Kept, Bare, XmlComparisonContext.Untyped));
        Assert.Equal(
            new Discrepancy("/v/text()[2]", DiscrepancyKinds.Value, DiscrepancyValue.Literal(" "), DiscrepancyValue.Absent),
            discrepancy);

        AssertEquivalent(Bare, Bare, XmlComparisonContext.Untyped);
        AssertEquivalent(Bare, "<v>a<b></b></v>", XmlComparisonContext.Untyped);
    }

    [Fact]
    public void XsiType_ComparesByResolvedName()
    {
        var type = GetAgent.ReturnType;
        var baseline = Envelope(GetAgent, Agent(), resultAttributes: $" xsi:type=\"a:{type}\" xmlns:a=\"{TargetNamespace}\"");

        AssertEquivalent(baseline, Envelope(GetAgent, Agent(), resultAttributes: $" xsi:type=\"b:{type}\" xmlns:b=\"{TargetNamespace}\""), AgentResponse);

        var discrepancy = Assert.Single(
            _comparer.Compare(baseline, Envelope(GetAgent, Agent(), resultAttributes: $" xsi:type=\"b:{type}\" xmlns:b=\"{OtherNamespace}\""), AgentResponse));
        Assert.Equal(
            new Discrepancy(
                $"{AgentResultPath}/@{{{XsiNamespace}}}type",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{type}"),
                DiscrepancyValue.Literal($"{{{OtherNamespace}}}{type}")),
            discrepancy);
    }

    [Fact]
    public void NilResult_NeverEqualsPopulated()
    {
        const string Nil = " xsi:nil=\"true\"";
        var nil = Envelope(GetAgent, string.Empty, resultAttributes: Nil);
        var populated = Envelope(GetAgent, Agent());
        var nilAttributePath = $"{AgentResultPath}/@{{{XsiNamespace}}}nil";

        AssertEquivalent(nil, Envelope(GetAgent, string.Empty, indent: false, resultAttributes: Nil), AgentResponse);

        var nilThenPopulated = _comparer.Compare(nil, populated, AgentResponse);
        Assert.Contains(new Discrepancy(nilAttributePath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal("true"), DiscrepancyValue.Absent), nilThenPopulated);
        Assert.Contains(nilThenPopulated, d => d.Kind == DiscrepancyKinds.Extra && d.Path == $"{AgentResultPath}/name");

        var populatedThenNil = _comparer.Compare(populated, nil, AgentResponse);
        Assert.Contains(new Discrepancy(nilAttributePath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("true")), populatedThenNil);
        Assert.Contains(populatedThenNil, d => d.Kind == DiscrepancyKinds.Missing && d.Path == $"{AgentResultPath}/name");
    }

    // website is a String property: neither the rule name nor a valid xsd:dateTime is normalised there.
    [Theory]
    [InlineData("{timestamp}", "2026-01-01T12:00:00Z")]
    [InlineData("2026-01-01T12:00:00Z", "2026-01-01T12:00:01Z")]
    public void TimestampLookalike_Differs(string baselineWebsite, string observedWebsite)
    {
        var discrepancy = Assert.Single(_comparer.Compare(
            Envelope(GetAgent, Agent($"<website>{baselineWebsite}</website>")),
            Envelope(GetAgent, Agent($"<website>{observedWebsite}</website>")),
            AgentResponse));

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/website/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineWebsite), DiscrepancyValue.Literal(observedWebsite)),
            discrepancy);
    }

    // The WebServiceHTTP list never exposes guid, so a guid element over SOAP is compared exactly.
    [Fact]
    public void GuidElement_IsNotNormalisedOverSoap()
    {
        var plain = Envelope(GetAgent, Agent());
        var withGuidA = Envelope(GetAgent, Join([$"<guid>{GuidA}</guid>", .. AgentElements().Select(element => element.Markup)]));
        var withGuidB = Envelope(GetAgent, Join([$"<guid>{GuidB}</guid>", .. AgentElements().Select(element => element.Markup)]));

        var changed = Assert.Single(_comparer.Compare(withGuidA, withGuidB, AgentResponse));
        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/guid/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(GuidA), DiscrepancyValue.Literal(GuidB)),
            changed);

        var extra = Assert.Single(_comparer.Compare(plain, withGuidA, AgentResponse));
        Assert.Equal(DiscrepancyKinds.Extra, extra.Kind);
        Assert.Equal($"{AgentResultPath}/guid", extra.Path);
        Assert.True(extra.Baseline.IsAbsent);
        Assert.False(extra.Observed.IsValid);
        Assert.Contains(GuidA, extra.Observed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingElement_IsReported()
    {
        var withoutFax = Envelope(GetAgent, Join(AgentElements().Where(element => element.Name != "fax").Select(element => element.Markup)));

        var discrepancy = Assert.Single(_comparer.Compare(Envelope(GetAgent, Agent()), withoutFax, AgentResponse));

        Assert.Equal(DiscrepancyKinds.Missing, discrepancy.Kind);
        Assert.Equal($"{AgentResultPath}/fax", discrepancy.Path);
        Assert.True(discrepancy.Observed.IsAbsent);
        Assert.Contains("1 8 6354 7364", discrepancy.Baseline.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedBody_IsReportedOnceAtTheRoot()
    {
        const string Truncated = "<soap:Envelope";

        var discrepancy = Assert.Single(_comparer.Compare(Envelope(GetAgent, Agent()), Truncated, AgentResponse));

        Assert.Equal(
            new Discrepancy("/", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Literal(Truncated)),
            discrepancy);
    }

    // ---- WSDL ------------------------------------------------------------------------------------

    [Fact]
    public void Wsdl_LocationExcluded()
    {
        var context = XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true);

        AssertEquivalent(WsdlDocument(StandInLocation), WsdlDocument(FacadeLocation), context);

        // Only the location value is excluded; any other difference in the same documents is still reported.
        var discrepancy = Assert.Single(_comparer.Compare(WsdlDocument(StandInLocation), WsdlDocument(FacadeLocation, documentation: "Other"), context));
        Assert.Equal("/definitions/documentation/text()", discrepancy.Path);
    }

    [Fact]
    public void Wsdl_LocationMissing_IsReported()
    {
        var context = XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true);

        Assert.Equal(
            new Discrepancy(LocationPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(StandInLocation), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(WsdlDocument(StandInLocation), WsdlDocument(location: null), context)));
        Assert.Equal(
            new Discrepancy(LocationPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(FacadeLocation)),
            Assert.Single(_comparer.Compare(WsdlDocument(location: null), WsdlDocument(FacadeLocation), context)));
    }

    [Fact]
    public void Wsdl_LocationCompared_WhenNotExcluded()
    {
        var discrepancy = Assert.Single(
            _comparer.Compare(WsdlDocument(StandInLocation), WsdlDocument(FacadeLocation), XmlComparisonContext.Wsdl(excludeSoapAddressLocation: false)));

        Assert.Equal(
            new Discrepancy(LocationPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(StandInLocation), DiscrepancyValue.Literal(FacadeLocation)),
            discrepancy);
    }

    [Fact]
    public void Wsdl_QNameAttributes_ByResolvedName()
    {
        var context = XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true);
        var baseline = WsdlDocument(StandInLocation);

        AssertEquivalent(
            baseline,
            WsdlDocument(StandInLocation, bindingQName: $"w:{BindingName}", portAttributes: $" xmlns:w=\"{TargetNamespace}\""),
            context);

        var discrepancy = Assert.Single(_comparer.Compare(
            baseline,
            WsdlDocument(StandInLocation, bindingQName: $"w:{BindingName}", portAttributes: $" xmlns:w=\"{OtherNamespace}\""),
            context));
        Assert.Equal(
            new Discrepancy(
                "/definitions/service/port/@binding",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{BindingName}"),
                DiscrepancyValue.Literal($"{{{OtherNamespace}}}{BindingName}")),
            discrepancy);
    }

    [Fact]
    public void Wsdl_IndentationIgnored_DocumentationTextKept()
    {
        var context = XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true);
        var baseline = WsdlDocument(StandInLocation);

        AssertEquivalent(baseline, WsdlDocument(StandInLocation, indent: false), context);

        // The header comment is written above the root, after the XML declaration the stand-in emits.
        AssertEquivalent(
            baseline,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n{StandInHeaderComment}\n{InsertAfter(baseline, $"targetNamespace=\"{TargetNamespace}\">", StandInHeaderComment)}",
            context);

        var discrepancy = Assert.Single(_comparer.Compare(baseline, WsdlDocument(StandInLocation, documentation: Documentation + " "), context));
        Assert.Equal(
            new Discrepancy("/definitions/documentation/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Documentation), DiscrepancyValue.Literal(Documentation + " ")),
            discrepancy);
    }

    // ---- Response level --------------------------------------------------------------------------

    // A SOAP operation other than getPhoto goes to the XML comparer: layout inside the envelope is
    // ignored, and a leaf difference is reported at its XML path.
    [Fact]
    public void Response_SoapBody_IsComparedAsXml()
    {
        var indented = SoapSnapshot(Envelope(GetAgent, Agent()));

        AssertResponse(
            _responses.Compare(GetAgent, indented, SoapSnapshot(Envelope(GetAgent, Agent(indent: false), indent: false))),
            [],
            wellFormed: true,
            statusMatched: true);
        AssertResponse(
            _responses.Compare(GetAgent, indented, SoapSnapshot(Envelope(GetAgent, AgentWith("email", "<email>hank@it.com </email>")))),
            [new($"{AgentResultPath}/email/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("hank@it.com"), DiscrepancyValue.Literal("hank@it.com "))],
            wellFormed: true,
            statusMatched: true);
    }

    [Fact]
    public void Response_DifferentMediaType_IsOneContentTypeDiscrepancy()
    {
        var body = Envelope(GetAgent, Agent());

        var comparison = _responses.Compare(GetAgent, SoapSnapshot(body, "text/xml"), SoapSnapshot(body, "application/xml"));

        AssertResponse(
            comparison,
            [new("$content-type", DiscrepancyKinds.Value, DiscrepancyValue.Literal("text/xml"), DiscrepancyValue.Literal("application/xml"))],
            wellFormed: true,
            statusMatched: true);
    }

    [Fact]
    public void Response_UnloadableObservedSoap_IsMalformedAndDiffersAsText()
    {
        const string Truncated = "<soap:Envelope";
        var baselineBody = Envelope(GetAgent, Agent());

        var comparison = _responses.Compare(GetAgent, SoapSnapshot(baselineBody), SoapSnapshot(Truncated));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineBody), DiscrepancyValue.Literal(Truncated)),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    // XML that loads but is not a SOAP 1.1 envelope is still compared as XML, and is not well formed.
    [Theory]
    [InlineData("<v>a</v>", "v")]
    [InlineData("<env:Envelope xmlns:env=\"" + Soap12EnvelopeNamespace + "\"><env:Body/></env:Envelope>", "{" + Soap12EnvelopeNamespace + "}Envelope")]
    public void Response_XmlThatIsNotASoapEnvelope_IsMalformed(string observedBody, string observedRootName)
    {
        var comparison = _responses.Compare(GetAgent, SoapSnapshot(Envelope(GetAgent, Agent())), SoapSnapshot(observedBody));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("/", DiscrepancyKinds.Value, DiscrepancyValue.Literal($"{{{EnvelopeNamespace}}}Envelope"), DiscrepancyValue.Literal(observedRootName)),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    // One leading UTF-8 byte-order mark is not part of a SOAP body that is compared as XML.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Response_ByteOrderMarkOnOneSide_IsEquivalent(bool markOnBaseline)
    {
        var plain = Encoding.UTF8.GetBytes(Envelope(GetAgent, Agent()));
        byte[] marked = [0xEF, 0xBB, 0xBF, .. plain];

        var comparison = _responses.Compare(
            GetAgent,
            SoapSnapshot(markOnBaseline ? marked : plain),
            SoapSnapshot(markOnBaseline ? plain : marked));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // Replacement decoding would load the corrupt envelope with U+FFFD in the agent's name. Decoded
    // strictly, it has no text, so it does not load, is not well formed and is reported by its bytes.
    [Fact]
    public void Response_InvalidUtf8Soap_IsMalformedTypeDifference()
    {
        var baselineBody = Envelope(GetAgent, Agent());
        var observed = Encoding.UTF8.GetBytes(baselineBody);
        observed[baselineBody.IndexOf("Hank Williams", StringComparison.Ordinal)] = 0xFF;

        var comparison = _responses.Compare(GetAgent, SoapSnapshot(baselineBody), SoapSnapshot(observed));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Type, DiscrepancyValue.Literal(baselineBody), DiscrepancyValue.Literal(Convert.ToHexStringLower(observed))),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>A SOAP response with the catalogue status and content type whose body is <paramref name="body"/> in UTF-8, without a byte-order mark.</summary>
    private static ResponseSnapshot SoapSnapshot(string body) => SoapSnapshot(body, SoapContentType);

    /// <summary>A SOAP response with the catalogue status and content type whose body is exactly <paramref name="body"/>.</summary>
    private static ResponseSnapshot SoapSnapshot(byte[] body) => new(SoapStatus, SoapContentType, body);

    /// <summary>A SOAP response with the catalogue status and <paramref name="contentType"/>, null for none, whose body is <paramref name="body"/> in UTF-8.</summary>
    private static ResponseSnapshot SoapSnapshot(string body, string? contentType) =>
        new(SoapStatus, contentType, Encoding.UTF8.GetBytes(body));

    /// <summary>Asserts the complete, ordered discrepancy list and all three flags of a response comparison.</summary>
    private static void AssertResponse(ResponseComparison comparison, Discrepancy[] expected, bool wellFormed, bool statusMatched)
    {
        Assert.Equal(expected, comparison.Discrepancies);
        Assert.Equal(expected.Length == 0, comparison.Equivalent);
        Assert.Equal(wellFormed, comparison.WellFormed);
        Assert.Equal(statusMatched, comparison.StatusMatched);
    }

    /// <summary>Asserts that the two bodies are equal in <paramref name="context"/>, compared in both directions.</summary>
    private void AssertEquivalent(string baseline, string observed, XmlComparisonContext context)
    {
        Assert.Empty(_comparer.Compare(baseline, observed, context));
        Assert.Empty(_comparer.Compare(observed, baseline, context));
    }

    /// <summary>The path of the <c>&lt;op&gt;Result</c> element of a SOAP response.</summary>
    private static string ResultPath(OperationDescriptor operation) =>
        $"/Envelope/Body/{operation.Soap!.ResponseElement}/{operation.Soap.ResultElement}";

    /// <summary>A newline and two spaces per level when <paramref name="indent"/> is true; otherwise nothing.</summary>
    private static string Break(int depth, bool indent) => indent ? "\n" + new string(' ', 2 * depth) : string.Empty;

    private static string Qualify(string prefix, string localName) => prefix.Length == 0 ? localName : $"{prefix}:{localName}";

    /// <summary>
    /// Concatenates result children, each on its own indented line when <paramref name="indent"/> is true,
    /// with the closing tag of the result on the next line.
    /// </summary>
    private static string Join(IEnumerable<string> elements, bool indent = true) =>
        indent
            ? string.Concat(elements.Select(element => Break(ResultChildDepth, indent: true) + element)) + Break(ResultChildDepth - 1, indent: true)
            : string.Concat(elements);

    /// <summary>
    /// Inserts <paramref name="insertion"/> after <paramref name="marker"/>, which must occur exactly once, so a
    /// fixture change cannot silently leave the insertion out.
    /// </summary>
    private static string InsertAfter(string xml, string marker, string insertion)
    {
        var index = xml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The insertion marker must occur in the fixture.");
        Assert.True(xml.IndexOf(marker, index + 1, StringComparison.Ordinal) < 0, "The insertion marker must occur only once in the fixture.");
        return xml.Insert(index + marker.Length, insertion);
    }

    /// <summary>Hank Williams's property elements, each qualified with <paramref name="prefix"/> when one is given.</summary>
    private static IEnumerable<(string Name, string Markup)> AgentElements(string prefix = "") =>
        HankWilliams.Select(property =>
        {
            var name = Qualify(prefix, property.Name);
            return (property.Name, $"<{name}>{property.Text}</{name}>");
        });

    /// <summary>The <c>DTOAgent</c> result content, with <paramref name="websiteElement"/> in place of the website when given.</summary>
    private static string Agent(string? websiteElement = null, bool indent = true, string prefix = "") =>
        websiteElement is null
            ? Join(AgentElements(prefix).Select(element => element.Markup), indent)
            : AgentWith("website", websiteElement, indent);

    /// <summary>The <c>DTOAgent</c> result content with the element of <paramref name="property"/> replaced by <paramref name="elementMarkup"/>.</summary>
    private static string AgentWith(string property, string elementMarkup, bool indent = true) =>
        Join(AgentElements().Select(element => element.Name == property ? elementMarkup : element.Markup), indent);

    /// <summary>The <c>DTONameArray</c> result content: one catalogue item element per name, in the order given.</summary>
    private static string Names(bool indent, params string[] names) =>
        Join(
            names.Select(name =>
                $"<{NameItem}>{Break(ResultChildDepth + 1, indent)}<{NameProperty}>{name}</{NameProperty}>{Break(ResultChildDepth, indent)}</{NameItem}>"),
            indent);

    /// <summary>A SOAP 1.1 envelope whose <c>Body</c> holds <paramref name="bodyChild"/>.</summary>
    private static string SoapEnvelope(string bodyChild, string envPrefix, bool indent, string envelopeAttributes) =>
        $"<{envPrefix}:Envelope xmlns:{envPrefix}=\"{EnvelopeNamespace}\" xmlns:xsi=\"{XsiNamespace}\" xmlns:xsd=\"{XsdNamespace}\"{envelopeAttributes}>"
        + $"{Break(1, indent)}<{envPrefix}:Body>{Break(2, indent)}{bodyChild}{Break(1, indent)}</{envPrefix}:Body>{Break(0, indent)}</{envPrefix}:Envelope>";

    /// <summary>
    /// A document/literal wrapped response envelope of <paramref name="operation"/>: the <c>&lt;op&gt;Response</c>
    /// wrapper in <paramref name="bodyNamespace"/> (the catalogue's target namespace by default), declared as the
    /// default namespace or bound to <paramref name="bodyPrefix"/>, holding <c>&lt;op&gt;Result</c>, which is
    /// self-closing when <paramref name="resultInner"/> is empty.
    /// </summary>
    private static string Envelope(
        OperationDescriptor operation,
        string resultInner,
        string envPrefix = "soap",
        bool indent = true,
        string resultAttributes = "",
        string bodyPrefix = "",
        string? bodyNamespace = null,
        string envelopeAttributes = "")
    {
        var binding = operation.Soap!;
        var response = Qualify(bodyPrefix, binding.ResponseElement);
        var result = Qualify(bodyPrefix, binding.ResultElement);
        var declaration = bodyPrefix.Length == 0 ? "xmlns" : $"xmlns:{bodyPrefix}";
        var resultElement = resultInner.Length == 0
            ? $"<{result}{resultAttributes}/>"
            : $"<{result}{resultAttributes}>{resultInner}</{result}>";

        return SoapEnvelope(
            $"<{response} {declaration}=\"{bodyNamespace ?? TargetNamespace}\">{Break(3, indent)}{resultElement}{Break(2, indent)}</{response}>",
            envPrefix,
            indent,
            envelopeAttributes);
    }

    /// <summary>A request envelope of a one-parameter operation: the <c>&lt;op&gt;</c> wrapper holding the parameter element.</summary>
    private static string RequestEnvelope(OperationDescriptor operation, string parameterText, bool indent = true)
    {
        var wrapper = operation.Soap!.RequestElement;
        var parameter = operation.Parameters.Single().Name;

        return SoapEnvelope(
            $"<{wrapper} xmlns=\"{TargetNamespace}\">{Break(3, indent)}<{parameter}>{parameterText}</{parameter}>{Break(2, indent)}</{wrapper}>",
            "soap",
            indent,
            string.Empty);
    }

    /// <summary>
    /// A minimal WSDL 1.1 document with one documentation element, one binding and the service port whose
    /// <c>soap:address</c> carries <paramref name="location"/>, XML-escaped; no <c>location</c> attribute when it is null.
    /// </summary>
    private static string WsdlDocument(
        string? location,
        bool indent = true,
        string documentation = Documentation,
        string bindingQName = "tns:" + BindingName,
        string portAttributes = "")
    {
        var address = location is null
            ? "<soap:address/>"
            : $"<soap:address location=\"{location.Replace("&", "&amp;", StringComparison.Ordinal)}\"/>";

        return $"<wsdl:definitions xmlns:wsdl=\"{WsdlNamespace}\" xmlns:soap=\"{WsdlSoapNamespace}\" xmlns:tns=\"{TargetNamespace}\" targetNamespace=\"{TargetNamespace}\">"
            + $"{Break(1, indent)}<wsdl:documentation>{documentation}</wsdl:documentation>"
            + $"{Break(1, indent)}<wsdl:portType name=\"{BindingName}\"/>"
            + $"{Break(1, indent)}<wsdl:binding name=\"{BindingName}\" type=\"tns:{BindingName}\">"
            + $"{Break(2, indent)}<soap:binding style=\"document\" transport=\"http://schemas.xmlsoap.org/soap/http\"/>"
            + $"{Break(1, indent)}</wsdl:binding>"
            + $"{Break(1, indent)}<wsdl:service name=\"{SoapAddressing.ServiceName}\">"
            + $"{Break(2, indent)}<wsdl:port name=\"{BindingName}\" binding=\"{bindingQName}\"{portAttributes}>"
            + $"{Break(3, indent)}{address}"
            + $"{Break(2, indent)}</wsdl:port>"
            + $"{Break(1, indent)}</wsdl:service>"
            + $"{Break(0, indent)}</wsdl:definitions>";
    }
}
