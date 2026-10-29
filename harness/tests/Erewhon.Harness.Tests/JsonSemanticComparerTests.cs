using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Type-directed comparison of REST bodies: structure is compared with JSON semantics (member order and
/// whitespace ignored, array order kept, numbers by value, strings exactly), the walk type follows the
/// catalogue and the baseline's <c>__type</c> member, and the N1/N2 rules apply pairwise only at the
/// positions the catalogue marks. Which property follows which rule is never restated here: every typed
/// comparison goes through the comparer built on <see cref="OperationCatalogue.Default"/>.
/// </summary>
public sealed class JsonSemanticComparerTests
{
    private const string N1 = NormalisationRuleIds.N1;
    private const string N2 = NormalisationRuleIds.N2;

    private const string DtoAgent = "DTOAgent";
    private const string DtoAgentArray = "DTOAgentArray";
    private const string DtoClient = "DTOClient";
    private const string DtoItem = "DTOItem";
    private const string DtoItemsArray = "DTOItemsArray";
    private const string DtoResult = "DTOResult";
    private const string DtoRetailItem = "DTORetailItem";
    private const string DtoRetailSale = "DTORetailSale";
    private const string DtoRetailSaleDetail = "DTORetailSaleDetail";
    private const string DtoSaleArray = "DTOSaleArray";
    private const string DtoTenderItem = "DTOTenderItem";
    private const string DtoTenderSale = "DTOTenderSale";

    // The stand-in's deterministic generated-GUID form.
    private const string ValidGuidA = "{00000000-0000-4000-8000-000000000001}";
    private const string ValidGuidB = "{00000000-0000-4000-8000-000000000002}";

    // An item GUID as the seed files supply it; item GUIDs are compared exactly.
    private const string SeedItemGuid = "{eaf359ee-e216-ee11-9fbd-0050568d0023}";

    // Microsoft-JSON dates exactly as they appear on the wire, escaped solidus included.
    private const string TimestampA = """\/Date(1767268800000)\/""";
    private const string TimestampB = """\/Date(1767268801000)\/""";
    private const string ListedDateA = """\/Date(1767225600000)\/""";
    private const string ListedDateB = """\/Date(1767139200000)\/""";

    // The rule names; a body holding either literally holds an ordinary invalid value.
    private const string GuidLookalike = "{generated-guid}";
    private const string TimestampLookalike = "{timestamp}";

    private const string HankFax = "1 8 6354 7364";

    private readonly JsonSemanticComparer _comparer =
        new(OperationCatalogue.Default, new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)));

    [Fact]
    public void PropertyOrderAndWhitespace_AreIgnored()
    {
        var observed = $$"""
            {
              "website" : "www.example.com",
              "phone" : "1 8 2938 4756",
              "fax" : "{{HankFax}}",
              "email" : "hank@example.com",
              "country" : "United States of America",
              "city" : "Nashville",
              "street" : "12 Music Row",
              "name" : "Hank Williams",
              "guid" : "{{ValidGuidA}}"
            }
            """;

        // The same GUID on both sides, so the untyped comparison proves order and layout alone are ignored.
        AssertEquivalent(AgentJson(ValidGuidA), observed, DtoAgent);
        AssertEquivalent(AgentJson(ValidGuidA), observed, declaredType: null);
    }

    [Fact]
    public void ArrayOrder_IsSignificant()
    {
        var hank = AgentJson(ValidGuidA);
        var peter = AgentJson(ValidGuidB, name: "Peter Smallsmith");

        var swapped = Compare($"[{hank},{peter}]", $"[{peter},{hank}]", DtoAgentArray);

        AssertHas(swapped, DiscrepancyKinds.Value, "$[0].name");
        AssertHas(swapped, DiscrepancyKinds.Value, "$[1].name");
        AssertEquivalent($"[{hank},{peter}]", $"[{hank},{peter}]", DtoAgentArray);
    }

    [Fact]
    public void ArrayItems_AreTypedByTheArrayItemType()
    {
        AssertEquivalent($"[{AgentJson(ValidGuidA)}]", $"[{AgentJson(ValidGuidB)}]", DtoAgentArray);
    }

    [Fact]
    public void MissingArrayItem_IsALengthDifference()
    {
        var hank = AgentJson(ValidGuidA);
        var peter = AgentJson(ValidGuidB, name: "Peter Smallsmith");

        var discrepancy = Assert.Single(Compare($"[{hank},{peter}]", $"[{hank}]", DtoAgentArray));

        Assert.Equal(
            new Discrepancy("$", DiscrepancyKinds.Length, DiscrepancyValue.Literal("2"), DiscrepancyValue.Literal("1")),
            discrepancy);
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("899", "899.00")]
    [InlineData("899", "8.99e2")]
    public void Numbers_CompareByValue(string baselinePrice, string observedPrice)
    {
        AssertEquivalent(ItemJson(SeedItemGuid, baselinePrice), ItemJson(SeedItemGuid, observedPrice), DtoItem);
    }

    [Fact]
    public void DifferentNumbers_AreReported()
    {
        var discrepancy = Assert.Single(Compare(ItemJson(SeedItemGuid, "1.50"), ItemJson(SeedItemGuid, "1.51"), DtoItem));

        Assert.Equal(
            new Discrepancy("$.price", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1.50"), DiscrepancyValue.Literal("1.51")),
            discrepancy);
    }

    // Brian Olsen's seed email carries a trailing blank (Clients.txt:2) that the stand-in trims, and
    // Pauline Wild's literal "null" website loads as the empty null string (Clients.txt:1). Neither a
    // padded nor a whitespace-only value may compare equal to the trimmed or empty one.
    [Theory]
    [InlineData("email", "bo@wol.com", "bo@wol.com ")]
    [InlineData("email", "bo@wol.com", " bo@wol.com")]
    [InlineData("website", "", " ")]
    public void PaddedString_Differs(string member, string baselineValue, string observedValue)
    {
        var discrepancy = Assert.Single(Compare(ClientWith(member, baselineValue), ClientWith(member, observedValue), DtoClient));

        Assert.Equal(
            new Discrepancy(
                "$." + member,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(Quoted(baselineValue)),
                DiscrepancyValue.Literal(Quoted(observedValue))),
            discrepancy);
    }

    [Fact]
    public void MissingProperty_IsReported()
    {
        var baseline = $$"""{"name":"Hank Williams","fax":"{{HankFax}}","phone":"1 8 2938 4756"}""";
        var observed = """{"name":"Hank Williams","phone":"1 8 2938 4756"}""";

        var discrepancy = Assert.Single(Compare(baseline, observed, DtoAgent));

        Assert.Equal(
            new Discrepancy("$.fax", DiscrepancyKinds.Missing, DiscrepancyValue.Literal(Quoted(HankFax)), DiscrepancyValue.Absent),
            discrepancy);
    }

    [Fact]
    public void ExtraProperty_IsReported()
    {
        var baseline = """{"name":"Hank Williams","phone":"1 8 2938 4756"}""";
        var observed = """{"name":"Hank Williams","phone":"1 8 2938 4756","nickname":"x"}""";

        var discrepancy = Assert.Single(Compare(baseline, observed, DtoAgent));

        Assert.Equal(
            new Discrepancy("$.nickname", DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(Quoted("x"))),
            discrepancy);
    }

    [Fact]
    public void TypeChange_IsReported()
    {
        var observed = $$"""{"guid":"{{ValidGuidA}}","allErrors":[],"allWarnings":[],"statusCode":"201"}""";

        var discrepancy = Assert.Single(Compare(ResultJson(Quoted(ValidGuidA), 201), observed, DtoResult));

        Assert.Equal(
            new Discrepancy("$.statusCode", DiscrepancyKinds.Type, DiscrepancyValue.Literal("201"), DiscrepancyValue.Literal(Quoted("201"))),
            discrepancy);
    }

    [Fact]
    public void NullBodies_AreEqual()
    {
        // A REST 404 or 400 body is the literal JSON token null.
        AssertEquivalent("null", "null", DtoAgent);

        var discrepancy = Assert.Single(Compare("null", AgentJson(ValidGuidA), DtoAgent));
        Assert.Equal(DiscrepancyKinds.Type, discrepancy.Kind);
        Assert.Equal("$", discrepancy.Path);
    }

    [Fact]
    public void MalformedObserved_IsReported()
    {
        const string truncated = """{"name":""";

        var discrepancy = Assert.Single(Compare(AgentJson(ValidGuidA), truncated, DtoAgent));

        Assert.Equal(
            new Discrepancy("$", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Literal(truncated)),
            discrepancy);
    }

    [Fact]
    public void TypeMember_SwitchesWalkType_InItemsArray()
    {
        // The array declares DTOItem items whose sale is a DTOSaleDetail; soldDate is a TimeStamp only on
        // DTORetailSaleDetail, so it is normalised only because the sale's __type names that class.
        var baseline = ItemsArrayJson(DtoRetailItem, DtoRetailSaleDetail, ValidGuidA, TimestampA);
        var observed = ItemsArrayJson(DtoRetailItem, DtoRetailSaleDetail, ValidGuidB, TimestampB);

        AssertEquivalent(baseline, observed, DtoItemsArray);
    }

    [Fact]
    public void WithoutTypeMember_SaleIsWalkedAsItsDeclaredClass()
    {
        var baseline = ItemsArrayJson(DtoRetailItem, saleType: null, ValidGuidA, TimestampA);
        var observed = ItemsArrayJson(DtoRetailItem, saleType: null, ValidGuidB, TimestampB);

        // The declared DTOSaleDetail still normalises its generated guid, but declares no soldDate.
        var discrepancy = Assert.Single(Compare(baseline, observed, DtoItemsArray));
        Assert.Equal(DiscrepancyKinds.Value, discrepancy.Kind);
        Assert.Equal("$[0].sale.soldDate", discrepancy.Path);
        Assert.False(discrepancy.Baseline.IsValid);
        Assert.False(discrepancy.Observed.IsValid);
    }

    [Fact]
    public void ObservedTypeMember_IsAnOrdinaryMemberDifference()
    {
        // The baseline's __type alone selects the walk type, so the regenerated sale guid and soldDate
        // still compare equal and the changed runtime class is the only difference.
        var baseline = ItemsArrayJson(DtoRetailItem, DtoRetailSaleDetail, ValidGuidA, TimestampA);
        var observed = ItemsArrayJson(DtoTenderItem, DtoRetailSaleDetail, ValidGuidB, TimestampB);

        var discrepancy = Assert.Single(Compare(baseline, observed, DtoItemsArray));

        Assert.Equal(
            new Discrepancy(
                "$[0].__type",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(Quoted(DtoRetailItem)),
                DiscrepancyValue.Literal(Quoted(DtoTenderItem))),
            discrepancy);
    }

    [Fact]
    public void TypeMember_SwitchesWalkType_InSaleArray()
    {
        AssertEquivalent(
            $"[{SaleJson(DtoRetailSale, ValidGuidA, TimestampA)}]",
            $"[{SaleJson(DtoRetailSale, ValidGuidB, TimestampB)}]",
            DtoSaleArray);

        // DTOTenderSale declares no timestamp: its guid is still normalised, the timestamp is not.
        var discrepancy = Assert.Single(Compare(
            $"[{SaleJson(DtoTenderSale, ValidGuidA, TimestampA)}]",
            $"[{SaleJson(DtoTenderSale, ValidGuidB, TimestampB)}]",
            DtoSaleArray));
        Assert.Equal(DiscrepancyKinds.Value, discrepancy.Kind);
        Assert.Equal("$[0].timestamp", discrepancy.Path);
        Assert.False(discrepancy.Baseline.IsValid);
        Assert.False(discrepancy.Observed.IsValid);
    }

    [Fact]
    public void TwoValidGeneratedGuids_AreEqual()
    {
        AssertEquivalent(AgentJson(ValidGuidA), AgentJson(ValidGuidB), DtoAgent);
    }

    [Theory]
    [InlineData(ValidGuidA, GuidLookalike, true)]
    [InlineData(GuidLookalike, ValidGuidA, false)]
    public void LiteralGuidLookalike_Differs(string baselineGuid, string observedGuid, bool baselineIsValid)
    {
        var discrepancy = Assert.Single(Compare(AgentJson(baselineGuid), AgentJson(observedGuid), DtoAgent));

        var valid = DiscrepancyValue.Valid(N1);
        var lookalike = DiscrepancyValue.Literal(Quoted(GuidLookalike));
        Assert.Equal(
            new Discrepancy(
                "$.guid",
                DiscrepancyKinds.Value,
                baselineIsValid ? valid : lookalike,
                baselineIsValid ? lookalike : valid),
            discrepancy);
    }

    [Theory]
    [InlineData(TimestampLookalike)]
    [InlineData("""\/Date(abc)\/""")]
    [InlineData("""\/Date(1767268800000)\/ """)]
    public void LiteralTimestampLookalike_Differs(string observedTimestamp)
    {
        var discrepancy = Assert.Single(Compare(
            SaleJson(DtoRetailSale, ValidGuidA, TimestampA),
            SaleJson(DtoRetailSale, ValidGuidA, observedTimestamp),
            DtoRetailSale));

        Assert.Equal(DiscrepancyKinds.Value, discrepancy.Kind);
        Assert.Equal("$.timestamp", discrepancy.Path);
        Assert.Equal(DiscrepancyValue.Valid(N2), discrepancy.Baseline);
        Assert.False(discrepancy.Observed.IsValid);
    }

    // A successful DTOResult carries the generated guid of the created object; losing it, or carrying it
    // null or empty, is a difference whatever the rest of the body holds.
    [Theory]
    [InlineData(null, DiscrepancyKinds.Missing, null)]
    [InlineData("null", DiscrepancyKinds.Type, "null")]
    [InlineData("\"\"", DiscrepancyKinds.Value, "\"\"")]
    public void SuccessResultWithoutValidGuid_Differs(string? observedGuidMember, string expectedKind, string? expectedObservedLiteral)
    {
        var discrepancy = Assert.Single(Compare(ResultJson(Quoted(ValidGuidA), 201), ResultJson(observedGuidMember, 201), DtoResult));

        Assert.Equal(
            new Discrepancy("$.guid", expectedKind, DiscrepancyValue.Valid(N1), DiscrepancyValue.Literal(expectedObservedLiteral)),
            discrepancy);
    }

    [Fact]
    public void ErrorResultsWithoutGuid_AreEqual()
    {
        var errors = Errors("Street is required", "City is required");

        AssertEquivalent(ResultJson(null, 400, errors), ResultJson(null, 400, errors), DtoResult);
    }

    [Fact]
    public void ErrorResults_WithDifferentErrors_Differ()
    {
        var discrepancy = Assert.Single(Compare(
            ResultJson(null, 400, Errors("Street is required", "City is required")),
            ResultJson(null, 400, Errors("Street is required", "Country is required")),
            DtoResult));

        Assert.Equal(
            new Discrepancy(
                "$.allErrors[1]",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(Quoted("City is required")),
                DiscrepancyValue.Literal(Quoted("Country is required"))),
            discrepancy);
    }

    // Item guids come from the seed files and are compared exactly, even when both sides hold a valid
    // braced GUID of the generated form.
    [Theory]
    [InlineData(DtoItem)]
    [InlineData(DtoRetailItem)]
    [InlineData(DtoTenderItem)]
    public void ItemGuid_IsNotNormalised(string declaredType)
    {
        var discrepancy = Assert.Single(Compare(ItemJson(ValidGuidA), ItemJson(ValidGuidB), declaredType));

        Assert.Equal(
            new Discrepancy("$.guid", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Quoted(ValidGuidA)), DiscrepancyValue.Literal(Quoted(ValidGuidB))),
            discrepancy);
    }

    [Fact]
    public void ListedDate_IsNotNormalised()
    {
        var discrepancy = Assert.Single(Compare(
            ItemJson(SeedItemGuid, listedDate: ListedDateA),
            ItemJson(SeedItemGuid, listedDate: ListedDateB),
            DtoTenderItem));

        Assert.Equal(DiscrepancyKinds.Value, discrepancy.Kind);
        Assert.Equal("$.listedDate", discrepancy.Path);
        Assert.False(discrepancy.Baseline.IsValid);
        Assert.False(discrepancy.Observed.IsValid);
    }

    // No declared type, or a name the catalogue does not define, compares untyped: no rule applies.
    [Theory]
    [InlineData(null)]
    [InlineData("NotACatalogueType")]
    public void Untyped_DoesNotNormalise(string? declaredType)
    {
        var discrepancy = Assert.Single(Compare(AgentJson(ValidGuidA), AgentJson(ValidGuidB), declaredType));

        Assert.Equal(
            new Discrepancy("$.guid", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Quoted(ValidGuidA)), DiscrepancyValue.Literal(Quoted(ValidGuidB))),
            discrepancy);
    }

    private IReadOnlyList<Discrepancy> Compare(string baseline, string observed, string? declaredType) =>
        _comparer.Compare(baseline, observed, declaredType);

    private void AssertEquivalent(string baseline, string observed, string? declaredType) =>
        Assert.Empty(Compare(baseline, observed, declaredType));

    private static void AssertHas(IReadOnlyList<Discrepancy> result, string kind, string path) =>
        Assert.Contains(result, discrepancy => discrepancy.Kind == kind && discrepancy.Path == path);

    /// <summary>The compact JSON string literal of an ASCII text that needs no escaping.</summary>
    private static string Quoted(string text) => "\"" + text + "\"";

    private static string Errors(params string[] messages) =>
        "[" + string.Join(",", messages.Select(Quoted)) + "]";

    private static string AgentJson(string guid, string website = "www.example.com", string name = "Hank Williams") => $$"""
        {"guid":"{{guid}}","name":"{{name}}","street":"12 Music Row","city":"Nashville","country":"United States of America","email":"hank@example.com","fax":"{{HankFax}}","phone":"1 8 2938 4756","website":"{{website}}"}
        """;

    private static string ClientJson(string email = "bo@wol.com", string website = "www.wol/olsen.com") => $$"""
        {"guid":"{{ValidGuidA}}","name":"Brian Olsen","street":"2834 The Palace","city":"San Diego","country":"United States of America","email":"{{email}}","fax":"1 2 3848 8384","phone":"64 2 2930 9393","website":"{{website}}"}
        """;

    private static string ClientWith(string member, string value) => member switch
    {
        "email" => ClientJson(email: value),
        "website" => ClientJson(website: value),
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "The client fixture parameterises only email and website."),
    };

    /// <summary>A <c>DTOResult</c>; <paramref name="guidMember"/> is the guid's raw JSON value, or null to omit the member.</summary>
    private static string ResultJson(string? guidMember, int statusCode, string allErrors = "[]")
    {
        var guid = guidMember is null ? string.Empty : $"\"guid\":{guidMember},";
        return $$"""{{{guid}}"allErrors":{{allErrors}},"allWarnings":[],"statusCode":{{statusCode}}}""";
    }

    private static string ItemJson(string guid, string price = "899.00", string listedDate = ListedDateA) => $$"""
        {"guid":"{{guid}}","name":"Cabinet","description":"Kauri cabinet","itemCategory":"Antiques","number":85,"price":{{price}},"listedDate":"{{listedDate}}","region":"Canterbury"}
        """;

    /// <summary>
    /// A one-element <c>DTOItemsArray</c> body: a sold item whose sale has the given runtime class
    /// (null omits the sale's <c>__type</c>), generated guid and soldDate.
    /// </summary>
    private static string ItemsArrayJson(string itemType, string? saleType, string saleGuid, string soldDate)
    {
        var saleTypeMember = saleType is null ? string.Empty : $"\"__type\": \"{saleType}\",";
        return $$"""
            [
              {
                "__type": "{{itemType}}",
                "guid": "{{SeedItemGuid}}",
                "agent": {{AgentJson(ValidGuidA)}},
                "categoryPrefix": "",
                "description": "Ten nights in Queenstown",
                "itemCategory": "Holidays",
                "listedDate": "{{ListedDateA}}",
                "name": "Queenstown Retreat",
                "number": 18,
                "photo": null,
                "price": 400.00,
                "region": "Otago",
                "sale": {
                  {{saleTypeMember}}
                  "guid": "{{saleGuid}}",
                  "agentCommission": 20.00,
                  "client": {{ClientJson()}},
                  "price": 400.00,
                  "soldDate": "{{soldDate}}"
                }
              }
            ]
            """;
    }

    /// <summary>A sale object of the given runtime class, generated guid and timestamp.</summary>
    private static string SaleJson(string saleType, string guid, string timestamp) => $$"""
        {
          "__type": "{{saleType}}",
          "guid": "{{guid}}",
          "agentCommission": 20.00,
          "client": {{ClientJson()}},
          "item": null,
          "timestamp": "{{timestamp}}"
        }
        """;
}
