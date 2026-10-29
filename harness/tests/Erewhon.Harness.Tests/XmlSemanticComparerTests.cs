using System.Collections.Concurrent;
using System.Globalization;
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
/// Semantic comparison of SOAP 1.1 envelopes and WSDL documents: which differences are ignored (attribute
/// order, prefix choice, namespace declarations, comments and processing instructions, whitespace-only text in
/// element-only containers, the order of differently named siblings) and which are significant (array item
/// order, leaf text including whitespace, QName resolution, nil results, unnormalised SOAP values, missing and
/// extra elements), and the N2 pairwise rule at timestamp positions reached through <c>xsi:type</c> or a
/// synthetic declared type. Operation, element and namespace names come from the contracts catalogue, and every
/// equivalence has a neighbouring case that must differ, so a comparer that never reports cannot pass.
/// The response-level cases send SOAP bodies through <see cref="ResponseComparer"/>: XML dispatch, the
/// media type, well-formedness as a SOAP 1.1 envelope, the byte-order mark, the encoding XML detection reads
/// and lossless decoding.
/// It also covers the WSDL contract check, which compares the facade WSDL with the backend WSDL under this
/// XML semantic comparison with the <c>soap:address/@location</c> value excluded, against two in-process
/// loopback stubs in a temporary repository root of its own.
/// </summary>
public sealed partial class XmlSemanticComparerTests : IDisposable
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
    private static readonly OperationDescriptor GetPhoto = Catalogue.GetById("Soap.getPhoto");

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

    // No public SOAP operation returns a class with a TimeStamp property, so the N2 positions are reached through
    // real catalogue classes: a sale whose own property is targeted, and an item whose sale reference switches to
    // the sale detail class that has one.
    private static readonly DtoDefinition RetailSale = Catalogue.GetDto("DTORetailSale");
    private static readonly DtoDefinition RetailItem = Catalogue.GetDto("DTORetailItem");
    private static readonly DtoDefinition RetailSaleDetail = Catalogue.GetDto("DTORetailSaleDetail");

    private static readonly string SaleTimestamp = TimestampProperty(RetailSale);
    private static readonly string SoldDate = TimestampProperty(RetailSaleDetail);

    // The item's reference that the sale detail class can occupy, declared with one of its ancestors.
    private static readonly string ItemSale = RetailItem.SoapProperties
        .Single(property => property.Kind == PropertyKind.Reference && Catalogue.IsKindOf(RetailSaleDetail.Name, property.JadeType))
        .Name;

    /// <summary>
    /// A synthetic, test-only operation that is never in the catalogue: getAgent's message names with the sale
    /// class as the declared return type, so N2 applies by declared type without any <c>xsi:type</c>.
    /// </summary>
    private static readonly OperationDescriptor SyntheticSaleOperation =
        GetAgent with { Id = "Synthetic.getRetailSale", ReturnType = RetailSale.Name };

    private static readonly XmlComparisonContext SyntheticSaleResponse = XmlComparisonContext.SoapResponse(SyntheticSaleOperation);

    private static readonly string SaleTimestampPath = $"{AgentResultPath}/{SaleTimestamp}";

    // How a test reaches the sale's timestamp: getAgent's result switched by xsi:type, or the synthetic declared type.
    private const string ByXsiType = "xsi:type";
    private const string ByDeclaredType = "declared type";

    // Two different valid xsd:dateTime values, and texts N2 never accepts as one.
    private const string StampA = "2026-01-01T12:00:00Z";
    private const string StampB = "2025-06-30T08:15:30.125+02:00";
    private const string SpacedStamp = "2026-01-01 12:00:00";
    private const string MonthThirteen = "2026-13-01T12:00:00Z";
    private const string NilLiteral = "xsi:nil=\"true\"";

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

    // With an equal common prefix, one item fewer or more is a single length entry at the item group.
    [Theory]
    [InlineData("1", "Airi Hamada")]
    [InlineData("3", "Airi Hamada", "Hank Williams", "Peter Smallsmith")]
    public void RepeatedItems_CountDiffers_IsOneLengthDifference(string observedCount, params string[] observedNames)
    {
        var baseline = Envelope(GetAgentNames, Names(indent: true, "Airi Hamada", "Hank Williams"));
        var observed = Envelope(GetAgentNames, Names(indent: true, observedNames));
        var groupPath = $"{AgentNamesResultPath}/{NameItem}";

        Assert.Equal(
            new Discrepancy(groupPath, DiscrepancyKinds.Length, DiscrepancyValue.Literal("2"), DiscrepancyValue.Literal(observedCount)),
            Assert.Single(_comparer.Compare(baseline, observed, AgentNamesResponse)));
        Assert.Equal(
            new Discrepancy(groupPath, DiscrepancyKinds.Length, DiscrepancyValue.Literal(observedCount), DiscrepancyValue.Literal("2")),
            Assert.Single(_comparer.Compare(observed, baseline, AgentNamesResponse)));
    }

    [Fact]
    public void RepeatedItems_CountAndPrefixDiffer_ReportLengthAndItemValue()
    {
        var two = Envelope(GetAgentNames, Names(indent: true, "Airi Hamada", "Hank Williams"));
        var one = Envelope(GetAgentNames, Names(indent: true, "Hank Williams"));
        var groupPath = $"{AgentNamesResultPath}/{NameItem}";

        Discrepancy[] twoThenOne =
        [
            new(groupPath, DiscrepancyKinds.Length, DiscrepancyValue.Literal("2"), DiscrepancyValue.Literal("1")),
            new($"{groupPath}[1]/{NameProperty}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Airi Hamada"), DiscrepancyValue.Literal("Hank Williams")),
        ];
        Assert.Equal(twoThenOne, _comparer.Compare(two, one, AgentNamesResponse));

        Discrepancy[] oneThenTwo =
        [
            new(groupPath, DiscrepancyKinds.Length, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
            new($"{groupPath}[1]/{NameProperty}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Hank Williams"), DiscrepancyValue.Literal("Airi Hamada")),
        ];
        Assert.Equal(oneThenTwo, _comparer.Compare(one, two, AgentNamesResponse));
    }

    // Against a result with no items, each item is missing or extra on its own, at its index.
    [Fact]
    public void RepeatedItems_AgainstAnEmptyArray_AreEachMissingOrExtra()
    {
        var two = Envelope(GetAgentNames, Names(indent: true, "Airi Hamada", "Hank Williams"));
        var none = Envelope(GetAgentNames, Names(indent: true));
        string[] itemPaths = [$"{AgentNamesResultPath}/{NameItem}[1]", $"{AgentNamesResultPath}/{NameItem}[2]"];
        string[] itemMarkup = [$"<{NameProperty}>Airi Hamada</{NameProperty}>", $"<{NameProperty}>Hank Williams</{NameProperty}>"];

        // Indentation inside an empty array is ignored, as it is inside a populated one.
        AssertEquivalent(none, Envelope(GetAgentNames, string.Empty), AgentNamesResponse);

        var missing = _comparer.Compare(two, none, AgentNamesResponse);
        Assert.Equal(itemPaths, missing.Select(discrepancy => discrepancy.Path));
        for (var index = 0; index < missing.Count; index++)
        {
            Assert.Equal(DiscrepancyKinds.Missing, missing[index].Kind);
            Assert.True(missing[index].Observed.IsAbsent);
            Assert.Contains(itemMarkup[index], missing[index].Baseline.Text, StringComparison.Ordinal);
        }

        var extra = _comparer.Compare(none, two, AgentNamesResponse);
        Assert.Equal(itemPaths, extra.Select(discrepancy => discrepancy.Path));
        for (var index = 0; index < extra.Count; index++)
        {
            Assert.Equal(DiscrepancyKinds.Extra, extra[index].Kind);
            Assert.True(extra[index].Baseline.IsAbsent);
            Assert.Contains(itemMarkup[index], extra[index].Observed.Text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("<website> </website>", "<website/>", " ", null)]
    [InlineData("<website/>", "<website> </website>", null, " ")]
    [InlineData("<website> </website>", "<website></website>", " ", null)]
    [InlineData("<website>&#160;</website>", "<website/>", "\u00A0", null)]
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

    // ---- N2 timestamps at targeted positions -----------------------------------------------------

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_DistinctValidValuesAreEqual_RuleNameLiteralDiffers(string route)
    {
        var context = SaleContext(route);

        AssertEquivalent(SaleEnvelope(route, Stamp(StampA)), SaleEnvelope(route, Stamp(StampB)), context);

        // The rule's report name is an ordinary invalid value, never a stand-in for a valid one.
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal("{timestamp}"), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp("{timestamp}")), SaleEnvelope(route, Stamp(StampA)), context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Valid(NormalisationRuleIds.N2), DiscrepancyValue.Literal("{timestamp}")),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp(StampA)), SaleEnvelope(route, Stamp("{timestamp}")), context)));
    }

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_OnOneSideOnly_IsMissingOrExtraWithItsTypedValue(string route)
    {
        var context = SaleContext(route);
        var withoutStamp = SaleEnvelope(route, null);

        AssertEquivalent(withoutStamp, SaleEnvelope(route, null, indent: false), context);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Missing, DiscrepancyValue.Valid(NormalisationRuleIds.N2), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp(StampA)), withoutStamp, context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            Assert.Single(_comparer.Compare(withoutStamp, SaleEnvelope(route, Stamp(StampB)), context)));

        // An invalid one-sided value keeps its exact text.
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal("{timestamp}"), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp("{timestamp}")), withoutStamp, context)));
    }

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_NilNeverEqualsValid(string route)
    {
        var context = SaleContext(route);
        var nil = SaleEnvelope(route, $"<{SaleTimestamp} {NilLiteral}/>");
        var valid = SaleEnvelope(route, Stamp(StampA));
        var nilAttributePath = $"{SaleTimestampPath}/@{{{XsiNamespace}}}nil";

        AssertEquivalent(nil, SaleEnvelope(route, $"<{SaleTimestamp} {NilLiteral}></{SaleTimestamp}>"), context);

        Discrepancy[] nilThenValid =
        [
            new(SaleTimestampPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(NilLiteral), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            new(nilAttributePath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal("true"), DiscrepancyValue.Absent),
        ];
        Assert.Equal(nilThenValid, _comparer.Compare(nil, valid, context));

        Discrepancy[] validThenNil =
        [
            new(SaleTimestampPath, DiscrepancyKinds.Type, DiscrepancyValue.Valid(NormalisationRuleIds.N2), DiscrepancyValue.Literal(NilLiteral)),
            new(nilAttributePath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("true")),
        ];
        Assert.Equal(validThenNil, _comparer.Compare(valid, nil, context));
    }

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_EmptyNeverEqualsValid(string route)
    {
        var context = SaleContext(route);
        var empty = SaleEnvelope(route, $"<{SaleTimestamp}/>");

        AssertEquivalent(empty, SaleEnvelope(route, Stamp(string.Empty)), context);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(string.Empty), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            Assert.Single(_comparer.Compare(empty, SaleEnvelope(route, Stamp(StampA)), context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Valid(NormalisationRuleIds.N2), DiscrepancyValue.Literal(string.Empty)),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp(StampA)), empty, context)));
    }

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_InvalidValues_CompareExactly(string route)
    {
        var context = SaleContext(route);

        AssertEquivalent(SaleEnvelope(route, Stamp(SpacedStamp)), SaleEnvelope(route, Stamp(SpacedStamp)), context);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(SpacedStamp), DiscrepancyValue.Literal(MonthThirteen)),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp(SpacedStamp)), SaleEnvelope(route, Stamp(MonthThirteen)), context)));

        // Padding makes a valid value invalid; nothing is trimmed.
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(" " + StampA), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, Stamp(" " + StampA)), SaleEnvelope(route, Stamp(StampA)), context)));
    }

    // Element content is never a valid xsd:dateTime, so a targeted element with a child is compared exactly.
    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_ComplexContent_IsComparedExactly(string route)
    {
        var context = SaleContext(route);
        var wrappedA = SaleEnvelope(route, Stamp($"<value>{StampA}</value>"));

        AssertEquivalent(wrappedA, SaleEnvelope(route, Stamp($"<value>{StampA}</value>"), indent: false), context);

        // Two valid values inside the child are not normalised.
        Assert.Equal(
            new Discrepancy($"{SaleTimestampPath}/value/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StampA), DiscrepancyValue.Literal(StampB)),
            Assert.Single(_comparer.Compare(wrappedA, SaleEnvelope(route, Stamp($"<value>{StampB}</value>")), context)));

        var wrappedThenSimple = _comparer.Compare(wrappedA, SaleEnvelope(route, Stamp(StampA)), context);
        Assert.Equal(2, wrappedThenSimple.Count);
        Assert.Equal(
            new Discrepancy($"{SaleTimestampPath}/text()[1]", DiscrepancyKinds.Value, DiscrepancyValue.Absent, DiscrepancyValue.Literal(StampA)),
            wrappedThenSimple[0]);
        Assert.Equal($"{SaleTimestampPath}/value", wrappedThenSimple[1].Path);
        Assert.Equal(DiscrepancyKinds.Missing, wrappedThenSimple[1].Kind);
        Assert.True(wrappedThenSimple[1].Observed.IsAbsent);
        Assert.Contains(StampA, wrappedThenSimple[1].Baseline.Text, StringComparison.Ordinal);

        var simpleThenWrapped = _comparer.Compare(SaleEnvelope(route, Stamp(StampA)), wrappedA, context);
        Assert.Equal(2, simpleThenWrapped.Count);
        Assert.Equal(
            new Discrepancy($"{SaleTimestampPath}/text()[1]", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StampA), DiscrepancyValue.Absent),
            simpleThenWrapped[0]);
        Assert.Equal($"{SaleTimestampPath}/value", simpleThenWrapped[1].Path);
        Assert.Equal(DiscrepancyKinds.Extra, simpleThenWrapped[1].Kind);
        Assert.True(simpleThenWrapped[1].Baseline.IsAbsent);
        Assert.Contains(StampA, simpleThenWrapped[1].Observed.Text, StringComparison.Ordinal);
    }

    // An empty CDATA section is no text, and comments and processing instructions are dropped, so each is an empty nil.
    [Theory]
    [InlineData(ByXsiType, "<![CDATA[]]>")]
    [InlineData(ByDeclaredType, "<![CDATA[]]>")]
    [InlineData(ByXsiType, "<!-- c --><?pi x?>")]
    [InlineData(ByDeclaredType, "<!-- c --><?pi x?>")]
    public void TimestampSlot_NilWithNoText_EqualsAnEmptyNil(string route, string content)
    {
        var context = SaleContext(route);
        var emptyNil = SaleEnvelope(route, $"<{SaleTimestamp} {NilLiteral}/>");

        AssertEquivalent(emptyNil, SaleEnvelope(route, NilStamp(content)), context);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(NilLiteral), DiscrepancyValue.Literal(NilStampMarkup("A"))),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, NilStamp(content)), SaleEnvelope(route, NilStamp("A")), context)));
    }

    // A nil element that holds text is neither a valid value nor a plain nil: it equals only the same exact text.
    [Theory]
    [InlineData(ByXsiType, "A", "B")]
    [InlineData(ByDeclaredType, "A", "B")]
    [InlineData(ByXsiType, StampA, StampB)]
    [InlineData(ByDeclaredType, StampA, StampB)]
    [InlineData(ByXsiType, "A", "A ")]
    [InlineData(ByDeclaredType, "A", "A ")]
    public void TimestampSlot_NilWithText_ComparesItsTextExactly(string route, string baselineText, string observedText)
    {
        var context = SaleContext(route);
        var baseline = SaleEnvelope(route, NilStamp(baselineText));
        var observed = SaleEnvelope(route, NilStamp(observedText));

        AssertEquivalent(baseline, SaleEnvelope(route, NilStamp(baselineText), indent: false), context);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(NilStampMarkup(baselineText)), DiscrepancyValue.Literal(NilStampMarkup(observedText))),
            Assert.Single(_comparer.Compare(baseline, observed, context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(NilStampMarkup(observedText)), DiscrepancyValue.Literal(NilStampMarkup(baselineText))),
            Assert.Single(_comparer.Compare(observed, baseline, context)));
    }

    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_NilWithWhitespace_DiffersFromAnEmptyNil(string route)
    {
        var context = SaleContext(route);
        var emptyNil = SaleEnvelope(route, $"<{SaleTimestamp} {NilLiteral} />");
        var spacedNil = SaleEnvelope(route, NilStamp(" "));

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(NilLiteral), DiscrepancyValue.Literal(NilStampMarkup(" "))),
            Assert.Single(_comparer.Compare(emptyNil, spacedNil, context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(NilStampMarkup(" ")), DiscrepancyValue.Literal(NilLiteral)),
            Assert.Single(_comparer.Compare(spacedNil, emptyNil, context)));
    }

    // Each distinct text gives its own discrepancy list, so its own failure signature.
    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_NilWithDistinctTexts_GiveDistinctDiscrepancies(string route)
    {
        var context = SaleContext(route);
        var nilA = SaleEnvelope(route, NilStamp("A"));

        var againstB = _comparer.Compare(nilA, SaleEnvelope(route, NilStamp("B")), context);
        var againstC = _comparer.Compare(nilA, SaleEnvelope(route, NilStamp("C")), context);
        var againstEmpty = _comparer.Compare(nilA, SaleEnvelope(route, NilStamp(string.Empty)), context);

        Assert.NotEmpty(againstB);
        Assert.NotEqual(againstB, againstC);
        Assert.NotEqual(againstB, againstEmpty);
        Assert.NotEqual(againstC, againstEmpty);
    }

    // The same text with and without nil differs; a valid side keeps its normalised value.
    [Theory]
    [InlineData(ByXsiType, StampA)]
    [InlineData(ByDeclaredType, StampA)]
    [InlineData(ByXsiType, StampB)]
    [InlineData(ByDeclaredType, StampB)]
    public void TimestampSlot_NilWithValidText_NeverEqualsValid(string route, string nilText)
    {
        var context = SaleContext(route);
        var valid = SaleEnvelope(route, Stamp(StampA));
        var nil = SaleEnvelope(route, NilStamp(nilText));
        var nilAttributePath = $"{SaleTimestampPath}/@{{{XsiNamespace}}}nil";

        Discrepancy[] validThenNil =
        [
            new(SaleTimestampPath, DiscrepancyKinds.Type, DiscrepancyValue.Valid(NormalisationRuleIds.N2), DiscrepancyValue.Literal(NilStampMarkup(nilText))),
            new(nilAttributePath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("true")),
        ];
        Assert.Equal(validThenNil, _comparer.Compare(valid, nil, context));

        Discrepancy[] nilThenValid =
        [
            new(SaleTimestampPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(NilStampMarkup(nilText)), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            new(nilAttributePath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal("true"), DiscrepancyValue.Absent),
        ];
        Assert.Equal(nilThenValid, _comparer.Compare(nil, valid, context));
    }

    // A nil timestamp on one side only reports its markup when it holds text, and the nil literal when it holds none.
    [Theory]
    [InlineData(ByXsiType)]
    [InlineData(ByDeclaredType)]
    public void TimestampSlot_NilOnOneSideOnly_IsMissingOrExtraWithItsContent(string route)
    {
        var context = SaleContext(route);
        var withoutStamp = SaleEnvelope(route, null);

        var missingA = Assert.Single(_comparer.Compare(SaleEnvelope(route, NilStamp("A")), withoutStamp, context));
        var missingB = Assert.Single(_comparer.Compare(SaleEnvelope(route, NilStamp("B")), withoutStamp, context));
        Assert.Equal(new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(NilStampMarkup("A")), DiscrepancyValue.Absent), missingA);
        Assert.Equal(new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(NilStampMarkup("B")), DiscrepancyValue.Absent), missingB);

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(NilStampMarkup("B"))),
            Assert.Single(_comparer.Compare(withoutStamp, SaleEnvelope(route, NilStamp("B")), context)));

        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(NilLiteral), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(SaleEnvelope(route, $"<{SaleTimestamp} {NilLiteral}/>"), withoutStamp, context)));
        Assert.Equal(
            new Discrepancy(SaleTimestampPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(NilLiteral)),
            Assert.Single(_comparer.Compare(withoutStamp, SaleEnvelope(route, NilStamp(string.Empty)), context)));
    }

    [Fact]
    public void XsiTypeOfANestedReference_SwitchesItsClass()
    {
        var itemType = $" xsi:type=\"{RetailItem.Name}\"";
        var soldDatePath = $"{AgentResultPath}/{ItemSale}/{SoldDate}";

        string Item(string saleAttributes, string soldDate) =>
            Envelope(GetAgent, Join([$"<{ItemSale}{saleAttributes}><{SoldDate}>{soldDate}</{SoldDate}></{ItemSale}>"]), resultAttributes: itemType);

        var detailType = $" xsi:type=\"{RetailSaleDetail.Name}\"";
        AssertEquivalent(Item(detailType, StampA), Item(detailType, StampB), AgentResponse);
        Assert.Equal(
            new Discrepancy(soldDatePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal("{timestamp}"), DiscrepancyValue.Valid(NormalisationRuleIds.N2)),
            Assert.Single(_comparer.Compare(Item(detailType, "{timestamp}"), Item(detailType, StampA), AgentResponse)));

        // Without the nested xsi:type the reference keeps its declared class, which has no TimeStamp property.
        Assert.Equal(
            new Discrepancy($"{soldDatePath}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StampA), DiscrepancyValue.Literal(StampB)),
            Assert.Single(_comparer.Compare(Item(string.Empty, StampA), Item(string.Empty, StampB), AgentResponse)));
    }

    // The baseline's xsi:type decides the class both sides are walked as; an observed one that differs is reported.
    [Fact]
    public void ObservedXsiTypeThatDiffers_IsAnAttributeDifference()
    {
        var typePath = $"{AgentResultPath}/@{{{XsiNamespace}}}type";
        var sale = Envelope(GetAgent, Join([Stamp(StampA)]), resultAttributes: $" xsi:type=\"{RetailSale.Name}\"");
        var agent = Envelope(GetAgent, Join([Stamp(StampB)]), resultAttributes: $" xsi:type=\"{GetAgent.ReturnType}\"");
        var untyped = Envelope(GetAgent, Join([Stamp(StampB)]));

        Assert.Equal(
            new Discrepancy(
                typePath,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{RetailSale.Name}"),
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{GetAgent.ReturnType}")),
            Assert.Single(_comparer.Compare(sale, agent, AgentResponse)));

        Discrepancy[] agentThenSale =
        [
            new(
                typePath,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{GetAgent.ReturnType}"),
                DiscrepancyValue.Literal($"{{{TargetNamespace}}}{RetailSale.Name}")),
            new($"{SaleTimestampPath}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StampB), DiscrepancyValue.Literal(StampA)),
        ];
        Assert.Equal(agentThenSale, _comparer.Compare(agent, sale, AgentResponse));

        Assert.Equal(
            new Discrepancy(typePath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal($"{{{TargetNamespace}}}{RetailSale.Name}"), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(sale, untyped, AgentResponse)));
    }

    // Under the declared DTOAgent, which has no TimeStamp property, timestamp-looking text is compared exactly.
    [Theory]
    [InlineData(StampA, StampB)]
    [InlineData("{timestamp}", StampA)]
    [InlineData(StampA, " " + StampA)]
    public void TimestampElement_WithoutTypeSwitch_IsComparedExactly(string baselineStamp, string observedStamp)
    {
        AssertEquivalent(Envelope(GetAgent, Join([Stamp(baselineStamp)])), Envelope(GetAgent, Join([Stamp(baselineStamp)], indent: false)), AgentResponse);

        Assert.Equal(
            new Discrepancy($"{SaleTimestampPath}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineStamp), DiscrepancyValue.Literal(observedStamp)),
            Assert.Single(_comparer.Compare(Envelope(GetAgent, Join([Stamp(baselineStamp)])), Envelope(GetAgent, Join([Stamp(observedStamp)])), AgentResponse)));
    }

    // ---- Text, QName and container edges ---------------------------------------------------------

    [Fact]
    public void CdataSections_AreText()
    {
        AssertEquivalent(Envelope(GetAgent, Agent("<website><![CDATA[www.it.com]]></website>")), Envelope(GetAgent, Agent()), AgentResponse);

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/website/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("www.it.com"), DiscrepancyValue.Literal("www.it.com/")),
            Assert.Single(_comparer.Compare(
                Envelope(GetAgent, Agent("<website><![CDATA[www.it.com]]></website>")),
                Envelope(GetAgent, Agent("<website><![CDATA[www.it.com/]]></website>")),
                AgentResponse)));

        // An empty CDATA section holds no text, like <website/>; a CDATA space is text.
        AssertEquivalent(Envelope(GetAgent, Agent("<website><![CDATA[]]></website>")), Envelope(GetAgent, Agent("<website/>")), AgentResponse);
        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/website/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(" "), DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(Envelope(GetAgent, Agent("<website><![CDATA[ ]]></website>")), Envelope(GetAgent, Agent("<website/>")), AgentResponse)));
    }

    [Theory]
    [InlineData("Hank <!-- c -->Williams")]
    [InlineData("Hank <?pi x?>Williams")]
    [InlineData("Hank <![CDATA[Williams]]>")]
    [InlineData("<![CDATA[Hank ]]><!-- c --><?pi x?>Williams")]
    public void TextSplitByCommentInstructionOrCdata_IsOneRun(string nameContent)
    {
        var split = Envelope(GetAgent, AgentWith("name", $"<name>{nameContent}</name>"));

        AssertEquivalent(split, Envelope(GetAgent, Agent()), AgentResponse);

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/name/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Hank Williams"), DiscrepancyValue.Literal("HankWilliams")),
            Assert.Single(_comparer.Compare(split, Envelope(GetAgent, AgentWith("name", "<name>HankWilliams</name>")), AgentResponse)));
    }

    // A QName with an unbound prefix, or text that is not a QName, is raw text: it never switches the class and
    // never equals a resolved name, even one that reads the same.
    [Fact]
    public void UnresolvedXsiType_IsRawText_AndKeepsTheDeclaredClass()
    {
        var typePath = $"{AgentResultPath}/@{{{XsiNamespace}}}type";
        var unbound = $" xsi:type=\"u:{RetailSale.Name}\"";
        var bound = $" xsi:type=\"u:{RetailSale.Name}\" xmlns:u=\"{TargetNamespace}\"";
        var resolvedText = $"{{{TargetNamespace}}}{RetailSale.Name}";

        string Sale(string resultAttributes, string stamp) => Envelope(GetAgent, Join([Stamp(stamp)]), resultAttributes: resultAttributes);

        AssertEquivalent(Sale(unbound, StampA), Sale(unbound, StampA), AgentResponse);

        // No switch: under DTOAgent two valid timestamps are compared exactly.
        Assert.Equal(
            new Discrepancy($"{SaleTimestampPath}/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StampA), DiscrepancyValue.Literal(StampB)),
            Assert.Single(_comparer.Compare(Sale(unbound, StampA), Sale(unbound, StampB), AgentResponse)));

        Assert.Equal(
            new Discrepancy(typePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal($"u:{RetailSale.Name}"), DiscrepancyValue.Literal(resolvedText)),
            Assert.Single(_comparer.Compare(Sale(unbound, StampA), Sale(bound, StampA), AgentResponse)));
        Assert.Equal(
            new Discrepancy(typePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(resolvedText), DiscrepancyValue.Literal($"u:{RetailSale.Name}")),
            Assert.Single(_comparer.Compare(Sale(bound, StampA), Sale(unbound, StampA), AgentResponse)));

        // Text in expanded-name form is not a QName: it differs from the resolved name although both read the same.
        var expandedText = $" xsi:type=\"{resolvedText}\"";
        AssertEquivalent(Sale(expandedText, StampA), Sale(expandedText, StampA), AgentResponse);
        Assert.Equal(
            new Discrepancy(typePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(resolvedText), DiscrepancyValue.Literal(resolvedText)),
            Assert.Single(_comparer.Compare(Sale(expandedText, StampA), Sale(bound, StampA), AgentResponse)));
    }

    // Only space, tab, carriage return and line feed are XML white space; a no-break space in a DTO element is text.
    [Fact]
    public void NoBreakSpaceInElementOnlyContainer_IsText()
    {
        var compact = Envelope(GetAgent, Agent(indent: false), indent: false);

        AssertEquivalent(compact, InsertAfter(compact, "</name>", " \t\n"), AgentResponse);

        Assert.Equal(
            new Discrepancy($"{AgentResultPath}/text()[2]", DiscrepancyKinds.Value, DiscrepancyValue.Absent, DiscrepancyValue.Literal("\u00A0")),
            Assert.Single(_comparer.Compare(compact, InsertAfter(compact, "</name>", "&#160;"), AgentResponse)));
    }

    [Fact]
    public void SoapHeader_IndentationIgnored_TextKept()
    {
        const string Trace = "<t:trace xmlns:t=\"urn:test\">a1</t:trace>";
        var compact = WithHeader(Envelope(GetAgent, Agent(indent: false), indent: false), Trace, indent: false);

        AssertEquivalent(compact, WithHeader(Envelope(GetAgent, Agent()), Trace, indent: true), AgentResponse);

        Assert.Equal(
            new Discrepancy("/Envelope/Header/text()[1]", DiscrepancyKinds.Value, DiscrepancyValue.Absent, DiscrepancyValue.Literal("stray")),
            Assert.Single(_comparer.Compare(
                compact,
                WithHeader(Envelope(GetAgent, Agent(indent: false), indent: false), "stray" + Trace, indent: false),
                AgentResponse)));
    }

    [Fact]
    public void SoapFault_IndentationIgnored_FaultStringCompared()
    {
        AssertEquivalent(FaultEnvelope("Backend unavailable", indent: false), FaultEnvelope("Backend unavailable"), AgentResponse);

        Assert.Equal(
            new Discrepancy("/Envelope/Body/Fault/faultstring/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("Backend unavailable"), DiscrepancyValue.Literal("Backend timed out")),
            Assert.Single(_comparer.Compare(FaultEnvelope("Backend unavailable"), FaultEnvelope("Backend timed out"), AgentResponse)));
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

    // The excluded location must still be present on both sides. A location on one side only is covered by
    // Wsdl_LocationMissing_IsReported.

    // Where the fixture's port holds its soap:address.
    private const string AddressPath = "/definitions/service/port/address";

    [Theory]
    [InlineData(true, LocationPath)]
    [InlineData(false, AddressPath)]
    public void Wsdl_LocationAbsentOnBothSides_IsReportedMissing(bool addressPresent, string path)
    {
        var document = addressPresent ? WsdlDocument(location: null) : WsdlDocumentWithoutAddress();

        Assert.Equal(
            new Discrepancy(path, DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(document, document, XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wsdl_AddressOnOneSideOnly_IsReportedAsTheElement(bool withLocation)
    {
        var context = XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true);
        var withAddress = WsdlDocument(withLocation ? StandInLocation : null);
        var locationMarkup = withLocation
            ? $" location=\"{StandInLocation.Replace("&", "&amp;", StringComparison.Ordinal)}\""
            : string.Empty;
        var addressMarkup = DiscrepancyValue.Literal($"<soap:address{locationMarkup} xmlns:soap=\"{WsdlSoapNamespace}\" />");

        // The element difference is the whole report: with an address on one side, the presence rule adds nothing.
        Assert.Equal(
            new Discrepancy(AddressPath, DiscrepancyKinds.Missing, addressMarkup, DiscrepancyValue.Absent),
            Assert.Single(_comparer.Compare(withAddress, WsdlDocumentWithoutAddress(), context)));
        Assert.Equal(
            new Discrepancy(AddressPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, addressMarkup),
            Assert.Single(_comparer.Compare(WsdlDocumentWithoutAddress(), withAddress, context)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wsdl_PresenceNotRequired_WhenLocationCompared(bool addressPresent)
    {
        // The presence requirement belongs to the exclusion: a location compared like any other attribute
        // may be absent on both sides.
        var document = addressPresent ? WsdlDocument(location: null) : WsdlDocumentWithoutAddress();

        AssertEquivalent(document, document, XmlComparisonContext.Wsdl(excludeSoapAddressLocation: false));
    }

    /// <summary>The <see cref="WsdlDocument"/> fixture with no <c>soap:address</c> in its port.</summary>
    private static string WsdlDocumentWithoutAddress()
    {
        const string Address = "<soap:address/>";
        var document = WsdlDocument(location: null);
        var index = document.IndexOf(Address, StringComparison.Ordinal);
        Assert.True(index >= 0, "The fixture must hold the address element.");
        Assert.True(document.IndexOf(Address, index + 1, StringComparison.Ordinal) < 0, "The fixture must hold only one address element.");
        return document.Remove(index, Address.Length);
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

    // The encodings Encoded writes a body in.
    private const string Utf8 = "utf-8";
    private const string Utf16LittleEndian = "utf-16 little-endian";
    private const string Utf16BigEndian = "utf-16 big-endian";

    // A SOAP body in UTF-16, with its byte-order mark and declaration, is the same XML as in UTF-8: in every
    // pairing, equal envelopes are equivalent and well formed, and a leaf difference is the one discrepancy two
    // UTF-8 bodies give.
    [Theory]
    [InlineData(Utf8, Utf16LittleEndian)]
    [InlineData(Utf8, Utf16BigEndian)]
    [InlineData(Utf16LittleEndian, Utf8)]
    [InlineData(Utf16BigEndian, Utf8)]
    [InlineData(Utf16LittleEndian, Utf16LittleEndian)]
    [InlineData(Utf16BigEndian, Utf16BigEndian)]
    [InlineData(Utf16LittleEndian, Utf16BigEndian)]
    public void Response_Utf16Soap_IsComparedAsXml(string baselineEncoding, string observedEncoding)
    {
        var baseline = SoapSnapshot(Encoded(Envelope(GetAgent, Agent()), baselineEncoding));

        AssertResponse(
            _responses.Compare(GetAgent, baseline, SoapSnapshot(Encoded(Envelope(GetAgent, Agent(indent: false), indent: false), observedEncoding))),
            [],
            wellFormed: true,
            statusMatched: true);
        AssertResponse(
            _responses.Compare(GetAgent, baseline, SoapSnapshot(Encoded(Envelope(GetAgent, AgentWith("email", "<email>hank@it.com </email>")), observedEncoding))),
            [new($"{AgentResultPath}/email/text()", DiscrepancyKinds.Value, DiscrepancyValue.Literal("hank@it.com"), DiscrepancyValue.Literal("hank@it.com "))],
            wellFormed: true,
            statusMatched: true);
    }

    // getPhoto compares raw bytes, so the same envelope in UTF-16 is well formed but differs from its UTF-8
    // baseline in length and from the first byte, its byte-order mark.
    [Theory]
    [InlineData(Utf16LittleEndian, "ff")]
    [InlineData(Utf16BigEndian, "fe")]
    public void Response_Utf16GetPhoto_IsWellFormedAndComparedAsBytes(string observedEncoding, string observedFirstByte)
    {
        var photo = Catalogue.GetDto(GetPhoto.ReturnType).SoapProperties.Single().Name;
        var envelope = Envelope(GetPhoto, $"<{photo}>/9j/4AAQSkZJRgABAgAAZABkAAD/2Q==</{photo}>", indent: false);
        var baseline = Encoded(envelope, Utf8);
        var observed = Encoded(envelope, observedEncoding);

        AssertResponse(
            _responses.Compare(GetPhoto, SoapSnapshot(baseline), SoapSnapshot(observed)),
            [
                new("$body", DiscrepancyKinds.Length, CountLiteral(baseline.Length), CountLiteral(observed.Length)),
                new("$body[0]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("3c"), DiscrepancyValue.Literal(observedFirstByte)),
            ],
            wellFormed: true,
            statusMatched: true);
    }

    // A SOAP body that does not decode losslessly in the encoding XML detection reads does not load, and
    // comparing it never throws: it is not well formed and is compared exactly, as UTF-8 text when it is valid
    // UTF-8 and by its bytes otherwise. The XML reader alone would load the bodies with a mark the declaration
    // contradicts, a truncated end, an odd length, a us-ascii or utf-32 declaration over bytes invalid there,
    // or a UCS-4 byte order, dropping or replacing what does not decode.
    [Theory]
    [InlineData("unsupported declared encoding", DiscrepancyKinds.Value)]
    [InlineData("unknown declared encoding", DiscrepancyKinds.Value)]
    [InlineData("utf-8 mark with latin-1 declared", DiscrepancyKinds.Value)]
    [InlineData("utf-16 mark with utf-8 declared", DiscrepancyKinds.Type)]
    [InlineData("utf-8 truncated at the end", DiscrepancyKinds.Type)]
    [InlineData("utf-16 of odd length", DiscrepancyKinds.Type)]
    [InlineData("utf-16 lone surrogate", DiscrepancyKinds.Type)]
    [InlineData("us-ascii declared over a non-ascii character", DiscrepancyKinds.Value)]
    [InlineData("utf-32 declared over an invalid code point", DiscrepancyKinds.Value)]
    [InlineData("ucs-4 in the 2143 byte order", DiscrepancyKinds.Value)]
    [InlineData("empty", DiscrepancyKinds.Value)]
    public void Response_SoapThatDoesNotDecodeLosslessly_IsMalformed(string variant, string exactKind)
    {
        var baselineBody = Envelope(GetAgent, Agent());
        var observed = UndecodableBody(variant, baselineBody);
        var afterMark = observed.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? observed[Encoding.UTF8.Preamble.Length..] : observed;
        var exact = exactKind == DiscrepancyKinds.Value ? Encoding.UTF8.GetString(afterMark) : Convert.ToHexStringLower(afterMark);

        AssertResponse(
            _responses.Compare(GetAgent, SoapSnapshot(baselineBody), SoapSnapshot(observed)),
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", exactKind, DiscrepancyValue.Literal(baselineBody), DiscrepancyValue.Literal(exact)),
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

    /// <summary>An XML declaration naming <paramref name="encoding"/>.</summary>
    private static string XmlDeclaration(string encoding) => $"<?xml version=\"1.0\" encoding=\"{encoding}\"?>";

    /// <summary>
    /// <paramref name="xml"/> after its XML declaration in <paramref name="encoding"/>: <see cref="Utf8"/> without a
    /// byte-order mark, declared <c>utf-8</c>, as the stand-in writes a body; <see cref="Utf16LittleEndian"/> or
    /// <see cref="Utf16BigEndian"/> after that byte order's mark, declared <c>utf-16</c>.
    /// </summary>
    private static byte[] Encoded(string xml, string encoding)
    {
        (Encoding Text, string Declared) form = encoding switch
        {
            Utf8 => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "utf-8"),
            Utf16LittleEndian => (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), "utf-16"),
            Utf16BigEndian => (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), "utf-16"),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unknown test encoding."),
        };

        return [.. form.Text.Preamble, .. form.Text.GetBytes(XmlDeclaration(form.Declared) + xml)];
    }

    /// <summary>
    /// The body of <paramref name="variant"/> in <see cref="Response_SoapThatDoesNotDecodeLosslessly_IsMalformed"/>,
    /// built from <paramref name="envelope"/>, the ASCII getAgent envelope that holds the agent's name once.
    /// </summary>
    private static byte[] UndecodableBody(string variant, string envelope)
    {
        const string AgentName = "Hank Williams";
        Assert.True(Ascii.IsValid(envelope), "The envelope fixture must be ASCII, one code unit per character.");
        var name = envelope.IndexOf(AgentName, StringComparison.Ordinal);
        Assert.True(name >= 0, "The envelope fixture must hold the agent's name.");

        // Overwrites the code unit of the text's character at index, which follows a mark of markLength bytes,
        // with unit, which the encoding does not allow there.
        static byte[] WithUnitAt(byte[] bytes, int markLength, int index, byte[] unit)
        {
            unit.CopyTo(bytes, markLength + (index * unit.Length));
            return bytes;
        }

        return variant switch
        {
            "unsupported declared encoding" => Encoding.UTF8.GetBytes(XmlDeclaration("windows-1252") + envelope),
            "unknown declared encoding" => Encoding.UTF8.GetBytes(XmlDeclaration("bogus") + envelope),
            "utf-8 mark with latin-1 declared" => [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(XmlDeclaration("iso-8859-1") + envelope)],
            "utf-16 mark with utf-8 declared" => [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(XmlDeclaration("utf-8") + envelope)],
            "utf-8 truncated at the end" => [.. Encoding.UTF8.GetBytes(envelope), 0xE2, 0x82],
            "utf-16 of odd length" => [.. Encoded(envelope, Utf16LittleEndian), 0x20],
            "utf-16 lone surrogate" => WithUnitAt(
                [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(envelope)], Encoding.Unicode.Preamble.Length, name, [0x00, 0xD8]),
            "us-ascii declared over a non-ascii character" =>
                Encoding.UTF8.GetBytes(XmlDeclaration("us-ascii") + envelope.Replace(AgentName, "Hank W\u00EDlliams", StringComparison.Ordinal)),
            "utf-32 declared over an invalid code point" => WithUnitAt(
                new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes(XmlDeclaration("utf-32") + envelope),
                0,
                XmlDeclaration("utf-32").Length + name,
                [0x00, 0x00, 0x11, 0x00]),
            "ucs-4 in the 2143 byte order" => [.. envelope.SelectMany(character => new byte[] { 0x00, 0x00, (byte)character, 0x00 })],
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown undecodable body variant."),
        };
    }

    /// <summary>The literal of a byte count, as a binary <c>length</c> discrepancy holds it.</summary>
    private static DiscrepancyValue CountLiteral(int count) =>
        DiscrepancyValue.Literal(count.ToString(CultureInfo.InvariantCulture));

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

    /// <summary>The one SOAP-exposed property of <paramref name="dto"/> that rule N2 targets.</summary>
    private static string TimestampProperty(DtoDefinition dto) =>
        dto.SoapProperties.Single(property => string.Equals(property.Normalisation, NormalisationClasses.N2, StringComparison.Ordinal)).Name;

    /// <summary>The sale's timestamp element holding <paramref name="content"/>, which may be markup.</summary>
    private static string Stamp(string content) => $"<{SaleTimestamp}>{content}</{SaleTimestamp}>";

    /// <summary>The sale's timestamp element carrying <c>xsi:nil="true"</c> and holding <paramref name="content"/>, which may be markup.</summary>
    private static string NilStamp(string content) => $"<{SaleTimestamp} {NilLiteral}>{content}</{SaleTimestamp}>";

    /// <summary>
    /// The literal a discrepancy reports for <see cref="NilStamp"/> holding <paramref name="text"/> in a sale envelope:
    /// the element's unformatted markup with the declarations of the namespaces it inherits, xsi and the body's default.
    /// </summary>
    private static string NilStampMarkup(string text) =>
        $"<{SaleTimestamp} {NilLiteral} xmlns:xsi=\"{XsiNamespace}\" xmlns=\"{TargetNamespace}\">{text}</{SaleTimestamp}>";

    /// <summary>The comparison context of a typing route: getAgent's for <see cref="ByXsiType"/>, the synthetic operation's otherwise.</summary>
    private static XmlComparisonContext SaleContext(string route) => route switch
    {
        ByXsiType => AgentResponse,
        ByDeclaredType => SyntheticSaleResponse,
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown typing route."),
    };

    /// <summary>
    /// A response whose result is typed as the sale class by <paramref name="route"/>: getAgent's result carrying
    /// the sale class as <c>xsi:type</c>, or the synthetic operation's result with no <c>xsi:type</c>. The result
    /// holds <paramref name="stampElement"/> as its only child, or nothing when it is null.
    /// </summary>
    private static string SaleEnvelope(string route, string? stampElement, bool indent = true)
    {
        string[] children = stampElement is null ? [] : [stampElement];
        return route switch
        {
            ByXsiType => Envelope(GetAgent, Join(children, indent), indent: indent, resultAttributes: $" xsi:type=\"{RetailSale.Name}\""),
            ByDeclaredType => Envelope(SyntheticSaleOperation, Join(children, indent), indent: indent),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown typing route."),
        };
    }

    /// <summary>
    /// Adds a <c>soap:Header</c> holding <paramref name="headerContent"/> before the <c>Body</c> of an envelope
    /// built with no extra envelope attributes, each on its own indented line when <paramref name="indent"/> is true.
    /// </summary>
    private static string WithHeader(string envelope, string headerContent, bool indent) =>
        InsertAfter(
            envelope,
            $" xmlns:xsd=\"{XsdNamespace}\">",
            $"{Break(1, indent)}<soap:Header>{Break(2, indent)}{headerContent}{Break(1, indent)}</soap:Header>");

    /// <summary>A SOAP 1.1 server Fault envelope whose <c>faultstring</c> is <paramref name="faultString"/>.</summary>
    private static string FaultEnvelope(string faultString, bool indent = true) =>
        SoapEnvelope(
            $"<soap:Fault>{Break(3, indent)}<faultcode>soap:Server</faultcode>{Break(3, indent)}<faultstring>{faultString}</faultstring>{Break(2, indent)}</soap:Fault>",
            "soap",
            indent,
            string.Empty);

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

// ---- The WSDL contract check --------------------------------------------------------------------
// The WSDL contract check against two in-process loopback stubs standing in for the backend and the facade,
// in a temporary repository root of its own: an absent contracts WSDL or an unusable SOAP base URL is not-run
// with no request sent; documents that differ only in the soap:address/@location value, comments, indentation
// or a byte order mark pass; a location or address that is absent on either side or on both, a changed
// operation, an unreachable side, a status other than 200 and an undecodable or non-XML body each fail with
// exactly the discrepancies the signature is computed over, an undecodable body at $body and a non-XML one at
// /, and undecodable bodies that differ in an invalid byte, in their decodable peer or in which side decodes,
// or from non-XML texts that spell their hexadecimal, give different natures; and the check writes only the
// two artifacts/wsdl/** files, removing stale ones and reporting a failed save in its detail, never the
// contract.
// These members live in this class because the harness test project keeps its fixed set of test classes,
// and the check compares the facade WSDL with the backend WSDL under the XML semantic comparison this class
// covers.
public sealed partial class XmlSemanticComparerTests
{
    // Catalogue, SoapAddressing, WsdlNamespace, WsdlSoapNamespace, GetAgent, GetAgentNames, BindingName,
    // StandInHeaderComment, StandInLocation, FacadeLocation, LocationPath, AddressPath, Break and _comparer are
    // the class's own members above, which hold the values the check tests use.

    private const string BackendSide = "backend";
    private const string FacadeSide = "facade";

    private const string SoapHttpTransport = "http://schemas.xmlsoap.org/soap/http";

    private const string PortTypeOperationNamePath = "/definitions/portType/operation/@name";
    private const string BindingOperationNamePath = "/definitions/binding/operation/@name";
    private const string XmlRootPath = "/";

    // The body as a whole, where the check reports a body that is not UTF-8.
    private const string BodyPath = "$body";

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

    // A malformed body on both sides, and how a malformed body is malformed.
    private const string BothSides = "both";
    private const string InvalidUtf8 = "invalid UTF-8";
    private const string NotXml = "not XML";

    // How the second of two failing runs with an undecodable body differs from the first.
    private const string ChangedInvalidByte = "changed invalid byte";
    private const string ChangedValidPeer = "changed valid peer";
    private const string SwappedTextAndBytes = "swapped text and bytes";
    private const string HexLookalikeText = "hex lookalike text";

    private const string NotFoundBody = "Not Found";
    private const string NotXmlBody = "Service Unavailable";

    // A property, so it reads SoapAddressing from the other part of the class whatever order the parts'
    // static fields are initialised in.
    private static string WsdlPathAndQuery => SoapAddressing.WsdlPathAndQuery;

    private static readonly string NoResponseConnect = DiscrepancyKinds.NoResponse(NoResponseReasons.Connect);

    private static readonly byte[] StaleArtifact = Encoding.UTF8.GetBytes("stale artifact of an earlier run");

    // Created on first use, so only the WSDL contract check tests touch the file system or hold a runner;
    // removed in Dispose.
    private string? _root;
    private HarnessOptions? _options;
    private CaseRunner? _http;

    /// <summary>The test's own repository root, which every contract and artifact path resolves inside.</summary>
    private string Root => _root ??=
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"erewhon-wsdl-check-{Guid.NewGuid():N}")).FullName;

    /// <summary>The check's settings, rooted at <see cref="Root"/>.</summary>
    private HarnessOptions Options => _options ??= new HarnessOptions { RepoRoot = Root, RequestTimeoutSeconds = RequestLimitSeconds };

    /// <summary>The runner the check fetches both WSDL documents with.</summary>
    private CaseRunner Http => _http ??= new(TimeSpan.FromSeconds(RequestLimitSeconds));

    /// <summary>Disposes the runner and deletes the repository root, where a WSDL contract check test created them.</summary>
    public void Dispose()
    {
        _http?.Dispose();
        if (_root is not null && Directory.Exists(_root))
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
        await using var backend = await ServeAsync(WsdlCheckDocument(StandInLocation));
        await using var facade = await ServeAsync(WsdlCheckDocument(FacadeLocation));
        if (directoryAtContractPath)
        {
            Directory.CreateDirectory(Options.ContractsWsdlPath);
        }

        WriteStaleArtifacts();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertNotRun(result);
        Assert.Empty(backend.Requests);
        Assert.Empty(facade.Requests);
        Assert.False(File.Exists(ArtifactPath(BackendSide)), "A stale backend artifact must be removed.");
        Assert.False(File.Exists(ArtifactPath(FacadeSide)), "A stale facade artifact must be removed.");
        Assert.False(File.Exists(Options.ContractsWsdlPath), "The check must never create the contracts WSDL.");
        Assert.Equal(directoryAtContractPath, Directory.Exists(Options.ContractsWsdlPath));
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
        await using var backend = await ServeAsync(WsdlCheckDocument(StandInLocation));
        await using var facade = await ServeAsync(WsdlCheckDocument(FacadeLocation));
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
        var backendBody = Encoding.UTF8.GetBytes(WsdlCheckDocument(StandInLocation));
        byte[] facadeBody = variant switch
        {
            LocationOnly => Encoding.UTF8.GetBytes(WsdlCheckDocument(FacadeLocation)),
            Unindented => Encoding.UTF8.GetBytes(WsdlCheckDocument(FacadeLocation, indent: false)),
            HeaderComment => Encoding.UTF8.GetBytes(WithHeaderComment(WsdlCheckDocument(FacadeLocation))),
            ByteOrderMark => [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(WithHeaderComment(WsdlCheckDocument(FacadeLocation)))],
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
        var backendBody = Encoding.UTF8.GetBytes(WsdlCheckDocument(StandInLocation));
        await using var backend = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, backendBody, TestContext.Current.CancellationToken);
        await using var facade = await ServeAsync(WsdlCheckDocument(FacadeLocation));
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
                WsdlCheckDocument(StandInLocation),
                WsdlCheckDocument(location: null),
                new Discrepancy(LocationPath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(StandInLocation), DiscrepancyValue.Absent)),
            BackendLocationAbsent => (
                WsdlCheckDocument(location: null),
                WsdlCheckDocument(FacadeLocation),
                new Discrepancy(LocationPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(FacadeLocation))),
            BothLocationsAbsent => (
                WsdlCheckDocument(location: null),
                WsdlCheckDocument(location: null),
                new Discrepancy(LocationPath, DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent)),
            FacadeAddressAbsent => (
                WsdlCheckDocument(StandInLocation),
                WsdlCheckDocument(FacadeLocation, address: false),
                new Discrepancy(AddressPath, DiscrepancyKinds.Missing, AddressMarkup(StandInLocation), DiscrepancyValue.Absent)),
            BackendAddressAbsent => (
                WsdlCheckDocument(StandInLocation, address: false),
                WsdlCheckDocument(FacadeLocation),
                new Discrepancy(AddressPath, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, AddressMarkup(FacadeLocation))),
            BothAddressesAbsent => (
                WsdlCheckDocument(StandInLocation, address: false),
                WsdlCheckDocument(FacadeLocation, address: false),
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
            await using var backend = await ServeAsync(WsdlCheckDocument(StandInLocation));
            await using var facade = await ServeAsync(WsdlCheckDocument(FacadeLocation, operationName: GetAgentNames.JadeMethod));

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
        var otherBody = Encoding.UTF8.GetBytes(WsdlCheckDocument(side == BackendSide ? FacadeLocation : StandInLocation));
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
        var otherBody = Encoding.UTF8.GetBytes(WsdlCheckDocument(side == BackendSide ? FacadeLocation : StandInLocation));
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
    [InlineData(BothSides, InvalidUtf8)]
    [InlineData(BackendSide, NotXml)]
    [InlineData(FacadeSide, NotXml)]
    public async Task MalformedSide_Fails_WithTheExactDiscrepancies(string side, string defect)
    {
        var backendText = WsdlCheckDocument(StandInLocation);
        var facadeText = WsdlCheckDocument(FacadeLocation);

        // An invalid byte inside the portType name. An undecodable body is the hex of its bytes after the byte order
        // mark, which the facade side carries when both are undecodable, reported at the body as a whole; the bytes
        // are kept as served. A decodable body that is not XML is the comparer's entry at the root.
        var undecodableBackend = WithInvalidByte(backendText, 0xFF);
        var undecodableFacade = WithInvalidByte(facadeText, 0xFF);
        byte[] markedUndecodableFacade = [.. Encoding.UTF8.Preamble, .. undecodableFacade];
        var (backendBody, facadeBody) = (side, defect) switch
        {
            (BackendSide, InvalidUtf8) => (undecodableBackend, Encoding.UTF8.GetBytes(facadeText)),
            (FacadeSide, InvalidUtf8) => (Encoding.UTF8.GetBytes(backendText), undecodableFacade),
            (BothSides, InvalidUtf8) => (undecodableBackend, markedUndecodableFacade),
            (BackendSide, NotXml) => (Encoding.UTF8.GetBytes(NotXmlBody), Encoding.UTF8.GetBytes(facadeText)),
            (FacadeSide, NotXml) => (Encoding.UTF8.GetBytes(backendText), Encoding.UTF8.GetBytes(NotXmlBody)),
            _ => throw new ArgumentOutOfRangeException(nameof(side), $"{side}, {defect}", "Unknown malformed side or defect."),
        };
        Discrepancy[] expected = (side, defect) switch
        {
            (BackendSide, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableBackend), DiscrepancyValue.Absent),
                new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableBackend), DiscrepancyValue.Literal(facadeText)),
            ],
            (FacadeSide, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(undecodableFacade)),
                new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(backendText), Hex(undecodableFacade)),
            ],
            (BothSides, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableBackend), Hex(undecodableFacade)),
            ],
            (BackendSide, NotXml) =>
            [
                new(XmlRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Literal(NotXmlBody), DiscrepancyValue.Absent),
            ],
            (FacadeSide, NotXml) =>
            [
                new(XmlRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Literal(NotXmlBody)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(side), $"{side}, {defect}", "Unknown malformed side or defect."),
        };

        await using var backend = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, backendBody, TestContext.Current.CancellationToken);
        await using var facade = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, facadeBody, TestContext.Current.CancellationToken);
        var contract = WriteContract();

        var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

        AssertDifference(result, expected);
        Assert.Equal(backendBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
        Assert.Equal(facadeBody, File.ReadAllBytes(ArtifactPath(FacadeSide)));
        AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
    }

    [Theory]
    [InlineData(ChangedInvalidByte)]
    [InlineData(ChangedValidPeer)]
    [InlineData(SwappedTextAndBytes)]
    [InlineData(HexLookalikeText)]
    public async Task DifferentUndecodableDocuments_GiveDifferentNatures(string change)
    {
        var backendText = WsdlCheckDocument(StandInLocation);
        var facadeText = WsdlCheckDocument(FacadeLocation);
        var changedFacadeText = WsdlCheckDocument(FacadeLocation, operationName: GetAgentNames.JadeMethod);
        var undecodableBackend = WithInvalidByte(backendText, 0xFF);
        var facadeWithFf = WithInvalidByte(facadeText, 0xFF);
        var facadeWithFe = WithInvalidByte(facadeText, 0xFE);
        byte[] textFf = "ff"u8.ToArray();
        byte[] textFe = "fe"u8.ToArray();
        byte[] text80 = "80"u8.ToArray();
        byte[] bytesFf = [0xFF];
        byte[] bytesFe = [0xFE];
        byte[] bytes80 = [0x80];

        // Two runs per row, each with its own stubs and contract; the type entries of the swapped pair are equal,
        // so only the malformed entry, which names the undecodable side, tells the two apart. The hex lookalike
        // pair has equal kinds and values, so only the path, the body as a whole against the comparer's root,
        // tells two undecodable bodies from two decodable non-XML texts that spell their hexadecimal.
        (byte[] Backend, byte[] Facade, Discrepancy[] Expected)[] runs = change switch
        {
            ChangedInvalidByte =>
            [
                (Encoding.UTF8.GetBytes(backendText), facadeWithFf,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(facadeWithFf)),
                    new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(backendText), Hex(facadeWithFf)),
                ]),
                (Encoding.UTF8.GetBytes(backendText), facadeWithFe,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(facadeWithFe)),
                    new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(backendText), Hex(facadeWithFe)),
                ]),
            ],
            ChangedValidPeer =>
            [
                (undecodableBackend, Encoding.UTF8.GetBytes(facadeText),
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableBackend), DiscrepancyValue.Absent),
                    new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableBackend), DiscrepancyValue.Literal(facadeText)),
                ]),
                (undecodableBackend, Encoding.UTF8.GetBytes(changedFacadeText),
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableBackend), DiscrepancyValue.Absent),
                    new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableBackend), DiscrepancyValue.Literal(changedFacadeText)),
                ]),
            ],
            SwappedTextAndBytes =>
            [
                (textFf, bytes80,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(bytes80)),
                    new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal("ff"), Hex(bytes80)),
                ]),
                (bytesFf, text80,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(bytesFf), DiscrepancyValue.Absent),
                    new(BodyPath, DiscrepancyKinds.Type, Hex(bytesFf), DiscrepancyValue.Literal("80")),
                ]),
            ],
            HexLookalikeText =>
            [
                (bytesFf, bytesFe,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(bytesFf), Hex(bytesFe)),
                ]),
                (textFf, textFe,
                [
                    new(XmlRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Literal("ff"), DiscrepancyValue.Literal("fe")),
                ]),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown change."),
        };

        var natures = new List<string>();
        foreach (var (backendBody, facadeBody, expected) in runs)
        {
            await using var backend = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, backendBody, TestContext.Current.CancellationToken);
            await using var facade = await LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, facadeBody, TestContext.Current.CancellationToken);
            var contract = WriteContract();

            var result = await RunAsync(backend.BaseUrl, facade.BaseUrl);

            AssertDifference(result, expected);
            Assert.Equal(backendBody, File.ReadAllBytes(ArtifactPath(BackendSide)));
            Assert.Equal(facadeBody, File.ReadAllBytes(ArtifactPath(FacadeSide)));
            AssertOnlyArtifactsWritten(contract, BackendSide, FacadeSide);
            natures.Add(result.Failure!.Nature);
        }

        Assert.NotEqual(natures[0], natures[1]);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private Task<ContractCheckResult> RunAsync(string backendSoapBaseUrl, string facadeSoapBaseUrl)
    {
        Options.Backend.SoapBaseUrl = backendSoapBaseUrl;
        Options.Target.SoapBaseUrl = facadeSoapBaseUrl;
        return new WsdlContractCheck(Options, Catalogue, _comparer, Http).RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a stub that answers the catalogue's WSDL request with 200 and <paramref name="document"/> as UTF-8.</summary>
    private static Task<LoopbackStub> ServeAsync(string document) =>
        LoopbackStub.StartAsync(WsdlPathAndQuery, StatusCodes.Status200OK, Encoding.UTF8.GetBytes(document), TestContext.Current.CancellationToken);

    /// <summary>Writes the contracts WSDL, which only has to exist, and returns its bytes.</summary>
    private byte[] WriteContract()
    {
        var bytes = Encoding.UTF8.GetBytes(WithHeaderComment(WsdlCheckDocument(FacadeLocation)));
        Directory.CreateDirectory(Path.GetDirectoryName(Options.ContractsWsdlPath)!);
        File.WriteAllBytes(Options.ContractsWsdlPath, bytes);
        return bytes;
    }

    /// <summary>Leaves both artifacts of an earlier run in place.</summary>
    private void WriteStaleArtifacts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPath(BackendSide))!);
        File.WriteAllBytes(ArtifactPath(BackendSide), StaleArtifact);
        File.WriteAllBytes(ArtifactPath(FacadeSide), StaleArtifact);
    }

    private string ArtifactPath(string side) => Path.Combine(Options.ArtifactsDirectory, "wsdl", $"{side}.wsdl");

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
            .Append(Options.ContractsWsdlPath)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(contract, File.ReadAllBytes(Options.ContractsWsdlPath));
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

    /// <summary>The UTF-8 bytes of <paramref name="document"/> with <paramref name="invalid"/>, a byte UTF-8 never uses, inside the portType name.</summary>
    private static byte[] WithInvalidByte(string document, byte invalid)
    {
        var marker = $"<wsdl:portType name=\"{BindingName}";
        var split = document.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return [.. Encoding.UTF8.GetBytes(document[..split]), invalid, .. Encoding.UTF8.GetBytes(document[split..])];
    }

    /// <summary>The literal of an undecodable body: the lowercase hexadecimal of <paramref name="bytes"/>.</summary>
    private static DiscrepancyValue Hex(byte[] bytes) => DiscrepancyValue.Literal(Convert.ToHexStringLower(bytes));

    /// <summary>The literal of a <c>soap:address</c> reported on one side only: the element as written on its own, namespace declared.</summary>
    private static DiscrepancyValue AddressMarkup(string location) =>
        DiscrepancyValue.Literal($"<soap:address location=\"{SecurityElement.Escape(location)}\" xmlns:soap=\"{WsdlSoapNamespace}\" />");

    /// <summary>
    /// A WSDL 1.1 document in the stand-in's shape: one portType operation, its document/literal binding and the
    /// service port whose <c>soap:address</c> carries <paramref name="location"/>, XML-escaped. The address has no
    /// <c>location</c> attribute when <paramref name="location"/> is null, and the port has no address when
    /// <paramref name="address"/> is false.
    /// </summary>
    private static string WsdlCheckDocument(string? location, bool address = true, string? operationName = null, bool indent = true)
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

// ---- Support types of the contract check tests --------------------------------------------------
// The loopback stub and the closed ports serve the WSDL contract check tests above and the OpenAPI drift
// check tests in JsonSemanticComparerTests; they are internal support types, not test classes.

/// <summary>
/// An in-process HTTP/1.1 server on 127.0.0.1 and an ephemeral port that answers one exact request target with a
/// chosen status and exact body bytes, and any other target with 404 and no body. It records every request
/// target it receives, before answering, so a caller that has its response also sees its request recorded.
/// Shared by the WSDL contract check tests of <see cref="XmlSemanticComparerTests"/> and the OpenAPI drift check
/// tests of <see cref="JsonSemanticComparerTests"/>.
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
