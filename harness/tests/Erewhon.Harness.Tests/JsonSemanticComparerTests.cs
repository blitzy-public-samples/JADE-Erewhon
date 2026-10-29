using System.Globalization;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Type-directed comparison of REST bodies: structure is compared with JSON semantics (member order and
/// whitespace ignored, array order kept, numbers by value, strings exactly), the walk type follows the
/// catalogue and the baseline's <c>__type</c> member, and the N1/N2 rules apply pairwise only at the
/// positions the catalogue marks. Which property follows which rule is never restated here: every typed
/// comparison goes through the comparer built on <see cref="OperationCatalogue.Default"/>.
/// The response-level cases send REST responses through <see cref="ResponseComparer"/>: status,
/// <c>Content-Type</c>, no-response outcomes, typing by the baseline status, the byte-order mark,
/// malformed bodies and strict UTF-8 decoding, each with its complete discrepancy list and flags.
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

    // The JSON string holding U+FFFD, whose UTF-8 bytes are 22 EF BF BD 22.
    private const string ReplacementCharacterJson = "\"\uFFFD\"";

    // The REST operation the response-level cases compare and the content type its responses carry; its
    // statuses come from the catalogue too.
    private static readonly OperationDescriptor RestGetAgent = OperationCatalogue.Default.Find(Surface.Rest, "getAgent");
    private static readonly string RestContentType = OperationCatalogue.Default.Addresses.Rest.ContentType;

    private readonly JsonSemanticComparer _comparer =
        new(OperationCatalogue.Default, new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)));

    private readonly ResponseComparer _responses =
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

    [Fact]
    public void ExtraArrayItem_IsALengthDifference_ThenCommonItemsCompareByIndex()
    {
        var hank = AgentJson(ValidGuidA);
        var mary = AgentJson(ValidGuidA, name: "Mary Jones");
        var baseline = $"[{hank},{AgentJson(ValidGuidA, name: "Peter Smallsmith")},{mary}]";

        // Every common item is walked as a DTOAgent at its own index: the second agent's regenerated guid
        // compares equal, the third agent's changed website is reported after the count, and the extra
        // fourth agent is not compared item by item.
        var observed =
            $"[{hank},{AgentJson(ValidGuidB, name: "Peter Smallsmith")},{AgentJson(ValidGuidA, website: "www.mary.example", name: "Mary Jones")},{hank}]";

        Discrepancy[] expected =
        [
            new("$", DiscrepancyKinds.Length, DiscrepancyValue.Literal("3"), DiscrepancyValue.Literal("4")),
            new("$[2].website", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Quoted("www.example.com")), DiscrepancyValue.Literal(Quoted("www.mary.example"))),
        ];
        Assert.Equal(expected, Compare(baseline, observed, DtoAgentArray));
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

    /// <summary>Every targeted position with every non-string JSON kind and its compact literal, in both directions.</summary>
    public static TheoryData<string, string, string, bool> TargetedNonStringValues()
    {
        // A bare number, even a valid timestamp's milliseconds; whitespace inside a value never reaches its literal.
        (string Raw, string Literal)[] values =
        [
            ("1767268800000", "1767268800000"),
            ("true", "true"),
            ("false", "false"),
            ("""{ "a" : 1 }""", """{"a":1}"""),
            ("""[ 1 , "x" ]""", """[1,"x"]"""),
        ];

        var data = new TheoryData<string, string, string, bool>();
        foreach (var position in TargetedPositions)
        {
            foreach (var (raw, literal) in values)
            {
                data.Add(position.Name, raw, literal, true);
                data.Add(position.Name, raw, literal, false);
            }
        }

        return data;
    }

    // A number, boolean, object or array is never valid under N1 or N2, so against a valid value it is a
    // type difference: the valid side shows only its rule, the other side its compact literal.
    [Theory]
    [MemberData(nameof(TargetedNonStringValues))]
    public void TargetedNonStringValue_NeverEqualsAValidOne(string target, string rawValue, string expectedLiteral, bool baselineIsValid)
    {
        var position = Targeted(target);
        var valid = DiscrepancyValue.Valid(position.Rule);
        var literal = DiscrepancyValue.Literal(expectedLiteral);

        var discrepancies = baselineIsValid
            ? Compare(position.Body(position.ValidValue), position.Body(rawValue), position.DeclaredType)
            : Compare(position.Body(rawValue), position.Body(position.ValidValue), position.DeclaredType);

        Discrepancy[] expected =
        [
            new(position.Path, DiscrepancyKinds.Type, baselineIsValid ? valid : literal, baselineIsValid ? literal : valid),
        ];
        Assert.Equal(expected, discrepancies);
    }

    /// <summary>
    /// Every N2 position missing (a null raw value), null or empty against a valid timestamp, in both
    /// directions, with the kind and the literal of the invalid side (null for the absent side).
    /// </summary>
    public static TheoryData<string, string?, bool, string, string?> MissingNullAndEmptyTimestamps()
    {
        var data = new TheoryData<string, string?, bool, string, string?>();
        foreach (var position in TargetedPositions.Where(position => position.Rule == N2))
        {
            data.Add(position.Name, null, true, DiscrepancyKinds.Missing, null);
            data.Add(position.Name, null, false, DiscrepancyKinds.Extra, null);
            data.Add(position.Name, "null", true, DiscrepancyKinds.Type, "null");
            data.Add(position.Name, "null", false, DiscrepancyKinds.Type, "null");
            data.Add(position.Name, "\"\"", true, DiscrepancyKinds.Value, "\"\"");
            data.Add(position.Name, "\"\"", false, DiscrepancyKinds.Value, "\"\"");
        }

        return data;
    }

    // A missing, null or empty timestamp never equals a valid one: a missing member is missing or extra,
    // null is a type difference and the empty string a value difference.
    [Theory]
    [MemberData(nameof(MissingNullAndEmptyTimestamps))]
    public void TargetedTimestamp_MissingNullOrEmpty_NeverEqualsAValidOne(string target, string? rawValue, bool baselineIsValid, string expectedKind, string? expectedLiteral)
    {
        var position = Targeted(target);
        var valid = DiscrepancyValue.Valid(N2);
        var invalid = DiscrepancyValue.Literal(expectedLiteral);

        var discrepancies = baselineIsValid
            ? Compare(position.Body(position.ValidValue), position.Body(rawValue), position.DeclaredType)
            : Compare(position.Body(rawValue), position.Body(position.ValidValue), position.DeclaredType);

        Discrepancy[] expected =
        [
            new(position.Path, expectedKind, baselineIsValid ? valid : invalid, baselineIsValid ? invalid : valid),
        ];
        Assert.Equal(expected, discrepancies);
    }

    /// <summary>Every targeted position with each value valid under neither rule; a null raw value omits the member.</summary>
    public static TheoryData<string, string?> IdenticalInvalidTargetedValues()
    {
        string?[] values =
        [
            null,
            "null",
            "\"\"",
            "1767268800000",
            "true",
            "false",
            """{"a":1}""",
            """[1,"x"]""",
            Quoted(GuidLookalike),
            Quoted(TimestampLookalike),
            Quoted("""\/Date(abc)\/"""),
            Quoted(ValidGuidA + " "),
            Quoted(TimestampA + " "),
        ];

        var data = new TheoryData<string, string?>();
        foreach (var position in TargetedPositions)
        {
            foreach (var value in values)
            {
                data.Add(position.Name, value);
            }
        }

        return data;
    }

    // Two values neither of which is valid are equal when they are identical: both missing, both null, or
    // the same number, boolean, object, array or invalid text, the rules' own names and padded values included.
    [Theory]
    [MemberData(nameof(IdenticalInvalidTargetedValues))]
    public void IdenticalInvalidTargetedValues_AreEqual(string target, string? rawValue)
    {
        var position = Targeted(target);

        AssertEquivalent(position.Body(rawValue), position.Body(rawValue), position.DeclaredType);
    }

    /// <summary>
    /// Every targeted position with pairs of different values valid under neither rule (a null raw value
    /// omits the member), the path below the position, the kind and both literals (null for an absent side).
    /// </summary>
    public static TheoryData<string, string?, string?, string, string, string?, string?> DifferentInvalidTargetedValues()
    {
        (string? Baseline, string? Observed, string SubPath, string Kind, string? BaselineLiteral, string? ObservedLiteral)[] pairs =
        [
            ("42", "43", "", DiscrepancyKinds.Value, "42", "43"),
            ("9007199254740993", "9007199254740992", "", DiscrepancyKinds.Value, "9007199254740993", "9007199254740992"),
            ("true", "false", "", DiscrepancyKinds.Value, "true", "false"),
            ("true", "1", "", DiscrepancyKinds.Type, "true", "1"),
            ("42", "\"42\"", "", DiscrepancyKinds.Type, "42", "\"42\""),
            ("\"42\"", "42", "", DiscrepancyKinds.Type, "\"42\"", "42"),
            (Quoted(GuidLookalike), Quoted(TimestampLookalike), "", DiscrepancyKinds.Value, Quoted(GuidLookalike), Quoted(TimestampLookalike)),
            (Quoted(ValidGuidA + " "), Quoted(" " + ValidGuidA), "", DiscrepancyKinds.Value, Quoted(ValidGuidA + " "), Quoted(" " + ValidGuidA)),

            // A string's literal is its decoded value re-encoded, so the escaped solidus appears as "/".
            (Quoted("""\/Date(abc)\/"""), Quoted("""\/Date(abd)\/"""), "", DiscrepancyKinds.Value, Quoted("/Date(abc)/"), Quoted("/Date(abd)/")),
            ("null", "\"\"", "", DiscrepancyKinds.Type, "null", "\"\""),
            ("\"\"", "null", "", DiscrepancyKinds.Type, "\"\"", "null"),
            ("null", null, "", DiscrepancyKinds.Missing, "null", null),
            (null, "null", "", DiscrepancyKinds.Extra, null, "null"),
            ("42", null, "", DiscrepancyKinds.Missing, "42", null),
            (null, """{"a":1}""", "", DiscrepancyKinds.Extra, null, """{"a":1}"""),
            ("""{"a":1}""", "[1]", "", DiscrepancyKinds.Type, """{"a":1}""", "[1]"),
            ("[1]", """{"a":1}""", "", DiscrepancyKinds.Type, "[1]", """{"a":1}"""),
            ("""{"a":1}""", "null", "", DiscrepancyKinds.Type, """{"a":1}""", "null"),

            // Two values of one non-string kind are compared exactly by the untyped walk, below the position.
            ("""{"a":1}""", """{"a":2}""", ".a", DiscrepancyKinds.Value, "1", "2"),
            ("[1]", "[1,2]", "", DiscrepancyKinds.Length, "1", "2"),
        ];

        var data = new TheoryData<string, string?, string?, string, string, string?, string?>();
        foreach (var position in TargetedPositions)
        {
            foreach (var pair in pairs)
            {
                data.Add(position.Name, pair.Baseline, pair.Observed, pair.SubPath, pair.Kind, pair.BaselineLiteral, pair.ObservedLiteral);
            }
        }

        return data;
    }

    // Two different values neither of which is valid differ, each side shown by its exact literal.
    [Theory]
    [MemberData(nameof(DifferentInvalidTargetedValues))]
    public void DifferentInvalidTargetedValues_Differ(string target, string? baselineValue, string? observedValue, string subPath, string expectedKind, string? expectedBaselineLiteral, string? expectedObservedLiteral)
    {
        var position = Targeted(target);

        Discrepancy[] expected = [LiteralDiscrepancy(position.Path + subPath, expectedKind, expectedBaselineLiteral, expectedObservedLiteral)];
        Assert.Equal(expected, Compare(position.Body(baselineValue), position.Body(observedValue), position.DeclaredType));
    }

    // Numbers compare by exact value: digits beyond double or decimal precision, and magnitudes beyond
    // either type's range, still tell two numbers apart, and each literal keeps the number's exact text.
    [Theory]
    [InlineData("0.1000000000000000000000000001", "0.1")]
    [InlineData("0.10000000000000000000000000001", "0.1")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("79228162514264337593543950336", "79228162514264337593543950337")]
    [InlineData("1e400", "2e400")]
    public void PrecisionDistinctNumbers_Differ(string baselinePrice, string observedPrice)
    {
        Discrepancy[] expected = [LiteralDiscrepancy("$.price", DiscrepancyKinds.Value, baselinePrice, observedPrice)];
        Assert.Equal(expected, Compare(ItemJson(SeedItemGuid, baselinePrice), ItemJson(SeedItemGuid, observedPrice), DtoItem));
    }

    // Strings compare by decoded value: an escape sequence, in any of its forms, equals the character it
    // stands for.
    [Theory]
    [InlineData("caf\\u00e9", "caf\u00E9")]
    [InlineData("caf\\u00e9", "caf\\u00E9")]
    [InlineData("\\uD83D\\uDE00", "\U0001F600")]
    [InlineData("www.wol\\/olsen.com", "www.wol/olsen.com")]
    [InlineData("a\\tb", "a\\u0009b")]
    [InlineData("say \\\"hi\\\"", "say \\u0022hi\\u0022")]
    [InlineData("back\\\\slash", "back\\u005Cslash")]
    public void EscapedAndLiteralFormsOfOneString_AreEqual(string baselineWebsite, string observedWebsite)
    {
        AssertEquivalent(ClientWith("website", baselineWebsite), ClientWith("website", observedWebsite), DtoClient);
    }

    // Different code points differ even where they look alike or are canonically equivalent: nothing is
    // Unicode-normalised. A literal is the decoded value re-encoded with the default encoder, which
    // escapes every non-ASCII and HTML-sensitive character in upper-case hex.
    [Theory]
    [InlineData("caf\\u00e9", "cafe\\u0301", "\"caf\\u00E9\"", "\"cafe\\u0301\"")]
    [InlineData("\u212B", "\u00C5", "\"\\u212B\"", "\"\\u00C5\"")]
    [InlineData("a\u00A0b", "a b", "\"a\\u00A0b\"", "\"a b\"")]
    [InlineData("\U0001F600", "\U0001F601", "\"\\uD83D\\uDE00\"", "\"\\uD83D\\uDE01\"")]
    [InlineData("a+b<c>", "a+b<c>&", "\"a\\u002Bb\\u003Cc\\u003E\"", "\"a\\u002Bb\\u003Cc\\u003E\\u0026\"")]
    [InlineData("say \\\"hi\\\"", "say \\\\hi", "\"say \\u0022hi\\u0022\"", "\"say \\\\hi\"")]
    public void DifferentCodePoints_Differ(string baselineWebsite, string observedWebsite, string expectedBaselineLiteral, string expectedObservedLiteral)
    {
        Discrepancy[] expected = [LiteralDiscrepancy("$.website", DiscrepancyKinds.Value, expectedBaselineLiteral, expectedObservedLiteral)];
        Assert.Equal(expected, Compare(ClientWith("website", baselineWebsite), ClientWith("website", observedWebsite), DtoClient));
    }

    [Fact]
    public void EscapedMemberNames_AreTheSameMembers()
    {
        // Member names are matched decoded, so an escaped guid is still the N1 position.
        var baseline = $$"""{"gu\u0069d":"{{ValidGuidA}}","n\u0061me":"Hank Williams"}""";
        var observed = $$"""{"guid":"{{ValidGuidB}}","name":"Hank Williams"}""";

        AssertEquivalent(baseline, observed, DtoAgent);
    }

    /// <summary>DTOAgent bodies that repeat a member name, with every discrepancy each pair gives, in walk order.</summary>
    public static TheoryData<string, string, Discrepancy[]> RepeatedMemberNames() => new()
    {
        { """{"name":"a","city":"x","name":"b"}""", """{"city":"x","name":"a","name":"b"}""", [] },
        {
            """{"name":"a","name":"b"}""",
            """{"name":"b","name":"a"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Value, Quoted("a"), Quoted("b")),
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Value, Quoted("b"), Quoted("a")),
            ]
        },
        { """{"name":"a","name":"b"}""", """{"name":"a"}""", [LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "2", "1")] },
        { """{"name":"a"}""", """{"name":"a","name":"b"}""", [LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "1", "2")] },
        {
            """{"name":"a","name":"b"}""",
            """{"name":"c"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "2", "1"),
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Value, Quoted("a"), Quoted("c")),
            ]
        },
        {
            """{"nickname":"a","nickname":"b"}""",
            "{}",
            [
                LiteralDiscrepancy("$.nickname", DiscrepancyKinds.Missing, Quoted("a"), null),
                LiteralDiscrepancy("$.nickname", DiscrepancyKinds.Missing, Quoted("b"), null),
            ]
        },
        {
            "{}",
            """{"nickname":1,"nickname":[2]}""",
            [
                LiteralDiscrepancy("$.nickname", DiscrepancyKinds.Extra, null, "1"),
                LiteralDiscrepancy("$.nickname", DiscrepancyKinds.Extra, null, "[2]"),
            ]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":"{{ValidGuidA}}"}""",
            $$"""{"guid":"{{ValidGuidB}}"}""",
            [LiteralDiscrepancy("$.guid", DiscrepancyKinds.Length, "2", "1")]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":"{{ValidGuidA}}"}""",
            $$"""{"guid":"{{ValidGuidB}}","guid":"{{GuidLookalike}}"}""",
            [new Discrepancy("$.guid", DiscrepancyKinds.Value, DiscrepancyValue.Valid(N1), DiscrepancyValue.Literal(Quoted(GuidLookalike)))]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":null}""",
            "{}",
            [
                new Discrepancy("$.guid", DiscrepancyKinds.Missing, DiscrepancyValue.Valid(N1), DiscrepancyValue.Absent),
                LiteralDiscrepancy("$.guid", DiscrepancyKinds.Missing, "null", null),
            ]
        },
    };

    // A name repeated within one object is compared occurrence by occurrence in document order: a count
    // difference is a length discrepancy followed by the common occurrences, a name only one side holds
    // gives one missing or extra discrepancy per occurrence, and at a targeted position every occurrence
    // follows the rule.
    [Theory]
    [MemberData(nameof(RepeatedMemberNames))]
    public void RepeatedMemberNames_CompareOccurrenceByOccurrence(string baseline, string observed, Discrepancy[] expected)
    {
        Assert.Equal(expected, Compare(baseline, observed, DtoAgent));
    }

    // An identifier member name is a dotted step; any other name is bracketed and single-quoted, with
    // backslash and single quote escaped. Names appear decoded.
    [Theory]
    [InlineData("_id9", "$.outer[0]._id9")]
    [InlineData("x y", "$.outer[0]['x y']")]
    [InlineData("x\\u0020y", "$.outer[0]['x y']")]
    [InlineData("1st", "$.outer[0]['1st']")]
    [InlineData("a.b", "$.outer[0]['a.b']")]
    [InlineData("a-b", "$.outer[0]['a-b']")]
    [InlineData("", "$.outer[0]['']")]
    [InlineData("it's", "$.outer[0]['it\\'s']")]
    [InlineData("back\\\\slash", "$.outer[0]['back\\\\slash']")]
    [InlineData("\\\"q\\\"", "$.outer[0]['\"q\"']")]
    [InlineData("caf\\u00e9", "$.outer[0]['caf\u00E9']")]
    public void MemberNamePaths_BracketAndEscapeNonIdentifiers(string jsonName, string expectedPath)
    {
        var baseline = "{\"outer\":[{\"" + jsonName + "\":1}]}";
        var observed = "{\"outer\":[{\"" + jsonName + "\":2}]}";

        Discrepancy[] expected = [LiteralDiscrepancy(expectedPath, DiscrepancyKinds.Value, "1", "2")];
        Assert.Equal(expected, Compare(baseline, observed, declaredType: null));
    }

    // When the baseline, or both bodies, fail to parse, the result is one malformed discrepancy at the
    // root holding the raw text of each side that failed and the absent value for a side that parsed.
    // Identical unparseable bodies still give that discrepancy.
    [Theory]
    [InlineData("""{"name":""", """{"name":"Hank Williams"}""", """{"name":""", null)]
    [InlineData("[1,]", "[1]", "[1,]", null)]
    [InlineData("""{"a":1} // comment""", """{"a":1}""", """{"a":1} // comment""", null)]
    [InlineData("""{"a":1}{"a":1}""", """{"a":1}""", """{"a":1}{"a":1}""", null)]
    [InlineData(" ", "null", " ", null)]
    [InlineData("""{"name":""", """{"name":""", """{"name":""", """{"name":""")]
    [InlineData("""{"name":""", "[1,]", """{"name":""", "[1,]")]
    [InlineData("", "", "", "")]
    public void MalformedBaselineOrBoth_IsOneDiscrepancyAtTheRoot(string baseline, string observed, string? expectedBaselineLiteral, string? expectedObservedLiteral)
    {
        Discrepancy[] expected = [LiteralDiscrepancy("$", DiscrepancyKinds.Malformed, expectedBaselineLiteral, expectedObservedLiteral)];
        Assert.Equal(expected, Compare(baseline, observed, DtoAgent));
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

    // ---- Response level: status, Content-Type, no-response outcomes and REST body dispatch ----------

    [Fact]
    public void Response_IdenticalSnapshots_AreEquivalent()
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, AgentJson(ValidGuidA)),
            RestSnapshot(status, AgentJson(ValidGuidA)));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // Media type and charset are compared case-insensitively, and a quoted charset equals the bare one.
    [Theory]
    [InlineData("application/json; charset=utf-8", "Application/JSON; charset=\"UTF-8\"")]
    [InlineData("application/json; charset=utf-8", "application/json;charset=UTF-8")]
    public void Response_ContentTypeCaseAndQuotedCharset_AreEqual(string baselineContentType, string observedContentType)
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, AgentJson(ValidGuidA), baselineContentType),
            RestSnapshot(status, AgentJson(ValidGuidA), observedContentType));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // A header present on one side only differs; headers that are not both media types are compared as
    // trimmed raw text, case-insensitively.
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("application/json; charset=utf-8", null, false)]
    [InlineData(null, "application/json; charset=utf-8", false)]
    [InlineData("json", " JSON ", true)]
    [InlineData("json", "application/json; charset=utf-8", false)]
    public void Response_AbsentOrUnparsableContentType_IsComparedAsText(string? baselineContentType, string? observedContentType, bool equal)
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, AgentJson(ValidGuidA), baselineContentType),
            RestSnapshot(status, AgentJson(ValidGuidA), observedContentType));

        Discrepancy[] expected = equal
            ? []
            : [new("$content-type", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineContentType), DiscrepancyValue.Literal(observedContentType))];
        AssertResponse(comparison, expected, wellFormed: true, statusMatched: true);
    }

    [Fact]
    public void Response_DifferentStatus_IsAStatusDiscrepancy()
    {
        var baselineStatus = ReturnTypeStatus(RestGetAgent);
        var observedStatus = NullBodyStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(baselineStatus, AgentJson(ValidGuidA)),
            RestSnapshot(observedStatus, AgentJson(ValidGuidA)));

        AssertResponse(
            comparison,
            [new("$status", DiscrepancyKinds.Value, DiscrepancyValue.Literal(StatusLiteral(baselineStatus)), DiscrepancyValue.Literal(StatusLiteral(observedStatus)))],
            wellFormed: true,
            statusMatched: false);
    }

    // A case with no HTTP response has exactly one discrepancy, holding the baseline status, and no body.
    [Theory]
    [InlineData(NoResponseReasons.Connect, "no-response:connect")]
    [InlineData(NoResponseReasons.Timeout, "no-response:timeout")]
    [InlineData(NoResponseReasons.Reset, "no-response:reset")]
    public void Response_ObservedWithoutResponse_IsOneNoResponseDiscrepancy(string reason, string expectedKind)
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(RestGetAgent, RestSnapshot(status, AgentJson(ValidGuidA)), NoResponseSnapshot(reason));

        AssertResponse(
            comparison,
            [new("$response", expectedKind, DiscrepancyValue.Literal(StatusLiteral(status)), DiscrepancyValue.Absent)],
            wellFormed: false,
            statusMatched: false);
    }

    [Fact]
    public void Response_NullArguments_Throw()
    {
        var snapshot = RestSnapshot(ReturnTypeStatus(RestGetAgent), AgentJson(ValidGuidA));
        var normaliser = new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default));

        Assert.Equal("catalogue", Assert.Throws<ArgumentNullException>(() => new ResponseComparer(null!, normaliser)).ParamName);
        Assert.Equal("normaliser", Assert.Throws<ArgumentNullException>(() => new ResponseComparer(OperationCatalogue.Default, null!)).ParamName);
        Assert.Equal("operation", Assert.Throws<ArgumentNullException>(() => _responses.Compare(null!, snapshot, snapshot)).ParamName);
        Assert.Equal("baseline", Assert.Throws<ArgumentNullException>(() => _responses.Compare(RestGetAgent, null!, snapshot)).ParamName);
        Assert.Equal("observed", Assert.Throws<ArgumentNullException>(() => _responses.Compare(RestGetAgent, snapshot, null!)).ParamName);
    }

    [Fact]
    public void Response_BaselineWithoutResponse_Throws()
    {
        var observed = RestSnapshot(ReturnTypeStatus(RestGetAgent), AgentJson(ValidGuidA));

        var exception = Assert.Throws<ArgumentException>(
            () => _responses.Compare(RestGetAgent, NoResponseSnapshot(NoResponseReasons.Timeout), observed));

        Assert.Equal("baseline", exception.ParamName);
    }

    // Reasons are compared ordinally, so a differently cased reason is unknown too.
    [Theory]
    [InlineData("")]
    [InlineData("Timeout")]
    [InlineData("dns")]
    public void Response_UnknownNoResponseReason_Throws(string reason)
    {
        var baseline = RestSnapshot(ReturnTypeStatus(RestGetAgent), AgentJson(ValidGuidA));

        Assert.Throws<ArgumentException>(() => _responses.Compare(RestGetAgent, baseline, NoResponseSnapshot(reason)));
    }

    // An empty body is not JSON, so it is malformed against the literal-null body and differs from it as text.
    [Fact]
    public void Response_EmptyBodyAgainstNull_IsMalformedAndDiffers()
    {
        var status = NullBodyStatus(RestGetAgent);

        var comparison = _responses.Compare(RestGetAgent, RestSnapshot(status, "null"), RestSnapshot(status, string.Empty));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Value, DiscrepancyValue.Literal("null"), DiscrepancyValue.Literal(string.Empty)),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    [Fact]
    public void Response_MalformedObservedJson_IsMalformedAndDiffers()
    {
        const string Truncated = """{"name":""";
        var status = ReturnTypeStatus(RestGetAgent);
        var baselineBody = AgentJson(ValidGuidA);

        var comparison = _responses.Compare(RestGetAgent, RestSnapshot(status, baselineBody), RestSnapshot(status, Truncated));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineBody), DiscrepancyValue.Literal(Truncated)),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    // Equivalence and well-formedness are separate: the same malformed body on both sides is equivalent,
    // and still not well formed.
    [Fact]
    public void Response_IdenticalMalformedBodies_AreEquivalentButNotWellFormed()
    {
        const string Truncated = """{"name":""";
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(RestGetAgent, RestSnapshot(status, Truncated), RestSnapshot(status, Truncated));

        AssertResponse(comparison, [], wellFormed: false, statusMatched: true);
    }

    // One leading UTF-8 byte-order mark is not part of a REST body.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Response_ByteOrderMarkOnOneSide_IsEquivalent(bool markOnBaseline)
    {
        var status = ReturnTypeStatus(RestGetAgent);
        var plain = Encoding.UTF8.GetBytes(AgentJson(ValidGuidA));
        byte[] marked = [0xEF, 0xBB, 0xBF, .. plain];

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, markOnBaseline ? marked : plain),
            RestSnapshot(status, markOnBaseline ? plain : marked));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // The baseline status selects the catalogue outcome whose body type the walk uses, so a DTO outcome
    // normalises its generated guid whichever operation declares it.
    [Theory]
    [InlineData("getAgent")]
    [InlineData("postCreateAgent")]
    public void Response_BaselineStatusWithDtoBody_NormalisesGeneratedGuid(string jadeMethod)
    {
        var operation = OperationCatalogue.Default.Find(Surface.Rest, jadeMethod);
        var outcome = operation.Statuses.First(status => status.BodyType == operation.ReturnType);

        var comparison = _responses.Compare(
            operation,
            RestSnapshot(outcome.HttpStatus, GeneratedGuidBody(outcome, ValidGuidA)),
            RestSnapshot(outcome.HttpStatus, GeneratedGuidBody(outcome, ValidGuidB)));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // The literal-null outcome names no DTO, so a body recorded under its status is compared untyped.
    [Fact]
    public void Response_NullBodyStatus_ComparesUntyped()
    {
        var status = NullBodyStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, AgentJson(ValidGuidA)),
            RestSnapshot(status, AgentJson(ValidGuidB)));

        AssertResponse(
            comparison,
            [new("$.guid", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Quoted(ValidGuidA)), DiscrepancyValue.Literal(Quoted(ValidGuidB)))],
            wellFormed: true,
            statusMatched: true);
    }

    // A status no catalogue outcome lists falls back to the declared return type.
    [Fact]
    public void Response_StatusOutsideTheCatalogue_IsTypedByTheReturnType()
    {
        const int BadGateway = 502;
        Assert.DoesNotContain(RestGetAgent.Statuses, status => status.HttpStatus == BadGateway);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(BadGateway, AgentJson(ValidGuidA)),
            RestSnapshot(BadGateway, AgentJson(ValidGuidB)));

        AssertResponse(comparison, [], wellFormed: true, statusMatched: true);
    }

    // Replacement decoding would turn each of these corrupt bodies into the baseline's own text. Decoded
    // strictly, a corrupt body has no text: it is not JSON, it is a different kind of value from a text
    // body, and its literal is the lowercase hex of its bytes, so each corruption is reported as itself.
    [Theory]
    [InlineData("22ff22")]
    [InlineData("22fe22")]
    [InlineData("22c0a222")]
    [InlineData("22eda08022")]
    [InlineData("22f490808022")]
    [InlineData("22efbf22")]
    public void Response_InvalidUtf8AgainstReplacementCharacter_IsMalformedTypeDifference(string observedHex)
    {
        var status = ReturnTypeStatus(RestGetAgent);
        Assert.Equal(Convert.FromHexString("22efbfbd22"), Encoding.UTF8.GetBytes(ReplacementCharacterJson));

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, ReplacementCharacterJson),
            RestSnapshot(status, Convert.FromHexString(observedHex)));

        AssertResponse(
            comparison,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Type, DiscrepancyValue.Literal(ReplacementCharacterJson), DiscrepancyValue.Literal(observedHex)),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    // The bytes 22 FF 22 and the text "22ff22" share a literal; the kind keeps their discrepancy lists apart,
    // and two different corruptions never share one.
    [Fact]
    public void Response_DifferentObservedBodies_GiveDifferentDiscrepancies()
    {
        var status = ReturnTypeStatus(RestGetAgent);
        var baseline = RestSnapshot(status, ReplacementCharacterJson);

        var corrupt = _responses.Compare(RestGetAgent, baseline, RestSnapshot(status, Convert.FromHexString("22ff22")));
        var otherCorrupt = _responses.Compare(RestGetAgent, baseline, RestSnapshot(status, Convert.FromHexString("22fe22")));
        var hexText = _responses.Compare(RestGetAgent, baseline, RestSnapshot(status, "22ff22"));

        AssertResponse(
            hexText,
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new("$body", DiscrepancyKinds.Value, DiscrepancyValue.Literal(ReplacementCharacterJson), DiscrepancyValue.Literal("22ff22")),
            ],
            wellFormed: false,
            statusMatched: true);
        Assert.NotEqual(corrupt.Discrepancies, hexText.Discrepancies);
        Assert.NotEqual(corrupt.Discrepancies, otherCorrupt.Discrepancies);
    }

    // Two bodies that are not valid UTF-8 are compared by their bytes after one leading byte-order mark.
    [Theory]
    [InlineData("22ff22", "22ff22")]
    [InlineData("efbbbf22ff22", "22ff22")]
    [InlineData("22ff22", "efbbbf22ff22")]
    public void Response_IdenticalInvalidUtf8_GiveNoBodyDifference(string baselineHex, string observedHex)
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, Convert.FromHexString(baselineHex)),
            RestSnapshot(status, Convert.FromHexString(observedHex)));

        AssertResponse(comparison, [], wellFormed: false, statusMatched: true);
    }

    // The baseline is not well formed either, so no malformed discrepancy is added.
    [Theory]
    [InlineData("22ff22", "22fe22", "22ff22")]
    [InlineData("efbbbf22ff22", "22fe22", "22ff22")]
    public void Response_DifferentInvalidUtf8_IsAValueDifference(string baselineHex, string observedHex, string expectedBaselineLiteral)
    {
        var status = ReturnTypeStatus(RestGetAgent);

        var comparison = _responses.Compare(
            RestGetAgent,
            RestSnapshot(status, Convert.FromHexString(baselineHex)),
            RestSnapshot(status, Convert.FromHexString(observedHex)));

        AssertResponse(
            comparison,
            [new("$body", DiscrepancyKinds.Value, DiscrepancyValue.Literal(expectedBaselineLiteral), DiscrepancyValue.Literal(observedHex))],
            wellFormed: false,
            statusMatched: true);
    }

    /// <summary>A REST response with the catalogue content type whose body is <paramref name="body"/> in UTF-8, without a byte-order mark.</summary>
    private static ResponseSnapshot RestSnapshot(int status, string body) => RestSnapshot(status, body, RestContentType);

    /// <summary>A REST response with the catalogue content type whose body is exactly <paramref name="body"/>.</summary>
    private static ResponseSnapshot RestSnapshot(int status, byte[] body) => new(status, RestContentType, body);

    /// <summary>A REST response with <paramref name="contentType"/>, null for none, whose body is <paramref name="body"/> in UTF-8.</summary>
    private static ResponseSnapshot RestSnapshot(int status, string body, string? contentType) =>
        new(status, contentType, Encoding.UTF8.GetBytes(body));

    /// <summary>An observed case that received no HTTP response, for <paramref name="reason"/>.</summary>
    private static ResponseSnapshot NoResponseSnapshot(string reason) =>
        new(Status: null, ContentType: null, ReadOnlyMemory<byte>.Empty, reason);

    /// <summary>The HTTP status of the operation's first catalogue outcome whose body is its declared return type.</summary>
    private static int ReturnTypeStatus(OperationDescriptor operation) =>
        operation.Statuses.First(status => status.BodyType == operation.ReturnType).HttpStatus;

    /// <summary>The HTTP status of the operation's first catalogue outcome with the literal-null body.</summary>
    private static int NullBodyStatus(OperationDescriptor operation) =>
        operation.Statuses.First(status => status.IsNullBody).HttpStatus;

    private static string StatusLiteral(int status) => status.ToString(CultureInfo.InvariantCulture);

    /// <summary>A body of <paramref name="outcome"/>'s DTO type that carries the generated <paramref name="guid"/>.</summary>
    private static string GeneratedGuidBody(OperationStatus outcome, string guid) => outcome.BodyType switch
    {
        DtoAgent => AgentJson(guid),
        DtoResult => ResultJson(
            Quoted(guid),
            outcome.ResultStatusCode ?? throw new InvalidOperationException("A DTOResult outcome carries its result status code.")),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.BodyType, "The fixture builds only DTOAgent and DTOResult bodies."),
    };

    /// <summary>Asserts the complete, ordered discrepancy list and all three flags of a response comparison.</summary>
    private static void AssertResponse(ResponseComparison comparison, Discrepancy[] expected, bool wellFormed, bool statusMatched)
    {
        Assert.Equal(expected, comparison.Discrepancies);
        Assert.Equal(expected.Length == 0, comparison.Equivalent);
        Assert.Equal(wellFormed, comparison.WellFormed);
        Assert.Equal(statusMatched, comparison.StatusMatched);
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

    /// <summary>
    /// The N1 and N2 positions the targeted-value theories run at: generated guids of a <c>DTOAgent</c> and
    /// a <c>DTOResult</c>, the timestamp of a <c>DTORetailSale</c> and, typed through <c>__type</c> inside
    /// an items array, the soldDate of a <c>DTORetailSaleDetail</c>.
    /// </summary>
    private static readonly TargetedPosition[] TargetedPositions =
    [
        new("DTOAgent.guid", DtoAgent, "$.guid", N1, Quoted(ValidGuidA), AgentJsonWithRawGuid),
        new("DTOResult.guid", DtoResult, "$.guid", N1, Quoted(ValidGuidA), guidMember => ResultJson(guidMember, 201)),
        new("DTORetailSale.timestamp", DtoRetailSale, "$.timestamp", N2, Quoted(TimestampA), RetailSaleJsonWithRawTimestamp),
        new("DTORetailSaleDetail.soldDate", DtoItemsArray, "$[0].sale.soldDate", N2, Quoted(TimestampA), ItemsArrayJsonWithRawSoldDate),
    ];

    private static TargetedPosition Targeted(string name) =>
        TargetedPositions.SingleOrDefault(position => position.Name == name)
        ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Not a targeted-position fixture.");

    /// <summary>A discrepancy whose two sides are literals; a null literal is the absent side.</summary>
    private static Discrepancy LiteralDiscrepancy(string path, string kind, string? baselineLiteral, string? observedLiteral) =>
        new(path, kind, DiscrepancyValue.Literal(baselineLiteral), DiscrepancyValue.Literal(observedLiteral));

    /// <summary>A <c>DTOAgent</c>; <paramref name="guidMember"/> is the guid's raw JSON value, or null to omit the member.</summary>
    private static string AgentJsonWithRawGuid(string? guidMember)
    {
        var guid = guidMember is null ? string.Empty : $"\"guid\":{guidMember},";
        return $$"""{{{guid}}"name":"Hank Williams","street":"12 Music Row","city":"Nashville","country":"United States of America","email":"hank@example.com","fax":"{{HankFax}}","phone":"1 8 2938 4756","website":"www.example.com"}""";
    }

    /// <summary>A <c>DTORetailSale</c>; <paramref name="timestampMember"/> is the timestamp's raw JSON value, or null to omit the member.</summary>
    private static string RetailSaleJsonWithRawTimestamp(string? timestampMember)
    {
        var timestamp = timestampMember is null ? string.Empty : $",\"timestamp\":{timestampMember}";
        return $$"""{"__type":"{{DtoRetailSale}}","guid":"{{ValidGuidA}}","agentCommission":20.00,"client":{{ClientJson()}},"item":null{{timestamp}}}""";
    }

    /// <summary>
    /// A one-element <c>DTOItemsArray</c> body: a sold <c>DTORetailItem</c> whose <c>DTORetailSaleDetail</c>
    /// sale holds <paramref name="soldDateMember"/>, the soldDate's raw JSON value, or omits the member when it is null.
    /// </summary>
    private static string ItemsArrayJsonWithRawSoldDate(string? soldDateMember)
    {
        var soldDate = soldDateMember is null ? string.Empty : $",\"soldDate\":{soldDateMember}";
        var sale = $$"""{"__type":"{{DtoRetailSaleDetail}}","guid":"{{ValidGuidA}}","agentCommission":20.00,"client":{{ClientJson()}},"price":400.00{{soldDate}}}""";
        return $$"""[{"__type":"{{DtoRetailItem}}","guid":"{{SeedItemGuid}}","agent":{{AgentJson(ValidGuidA)}},"name":"Queenstown Retreat","price":400.00,"sale":{{sale}}}]""";
    }

    /// <summary>
    /// One N1 or N2 position: its fixture name, the declared type of the body built around it, its path, the
    /// rule it follows, a raw JSON value valid under that rule, and the body builder, which places a raw JSON
    /// value at the position or omits the member when given null.
    /// </summary>
    private sealed record TargetedPosition(string Name, string DeclaredType, string Path, string Rule, string ValidValue, Func<string?, string> Body);
}
