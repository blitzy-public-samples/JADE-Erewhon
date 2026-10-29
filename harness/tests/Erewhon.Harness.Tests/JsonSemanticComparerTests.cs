using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Modes;
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
/// Type-directed comparison of REST bodies: structure is compared with JSON semantics (member order and
/// whitespace ignored, array order kept, numbers by value, strings exactly), the walk type follows the
/// catalogue and the baseline's <c>__type</c> member, and the N1/N2 rules apply pairwise only at the
/// positions the catalogue marks. Which property follows which rule is never restated here: every typed
/// comparison goes through the comparer built on <see cref="OperationCatalogue.Default"/>.
/// The response-level cases send REST responses through <see cref="ResponseComparer"/>: status,
/// <c>Content-Type</c>, no-response outcomes, typing by the baseline status, the byte-order mark,
/// malformed bodies and strict UTF-8 decoding, each with its complete discrepancy list and flags.
/// It also covers the OpenAPI drift check, which compares the live OpenAPI document with the checked-in
/// contract under this JSON semantic comparison, against an in-process loopback stub in a temporary
/// repository root of its own. It also covers the <c>export-openapi</c> mode, which writes that checked-in
/// contract: its repository-root guard, the creation of only the <c>contracts/openapi</c> leaf, and the
/// served document written byte for byte, in a temporary directory of its own.
/// </summary>
public sealed partial class JsonSemanticComparerTests : IDisposable
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

// ---- The OpenAPI drift check --------------------------------------------------------------------
// The OpenAPI drift check against an in-process loopback stub standing in for the facade admin listener, in a
// temporary repository root of its own: an unusable admin URL, no response or a status other than 200 is
// not-run; an absent checked-in contract is missing and is never created; documents that differ only in
// member order, whitespace or a byte order mark pass; changed, missing and extra members and an undecodable or
// non-JSON document fail with exactly the discrepancies the signature is computed over, an undecodable one at
// $body and a non-JSON one at $, and undecodable documents that differ in an invalid byte, in their decodable
// peer or in which side decodes, or from non-JSON texts that spell their hexadecimal, give different natures;
// and the check writes only artifacts/openapi/live.json, byte for byte as served, removing a stale one when
// nothing was answered and reporting a failed save in its detail, and never rewrites the contract.
// These members live in this class because the harness test project keeps its fixed set of test classes,
// and the drift check compares the live document with the checked-in contract under the JSON semantic
// comparison this class covers.
public sealed partial class JsonSemanticComparerTests
{
    private const string OpenApiVersion = "3.1.1";
    private const string Title = "Erewhon REST";
    private const string DocumentVersion = "v1";
    private const string Summary = "Facade";
    private const string OkDescription = "OK";

    private const string VersionPath = "$.info.version";
    private const string SummaryPath = "$.info.summary";
    private const string JsonRootPath = "$";

    // The body as a whole, where the check reports a document that is not UTF-8.
    private const string BodyPath = "$body";

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
    private const string BothSides = "both";
    private const string InvalidUtf8 = "invalid UTF-8";
    private const string NotJson = "not JSON";
    private const string NotJsonBody = "Service Unavailable";

    // How the second of two failing runs with an undecodable document differs from the first.
    private const string ChangedInvalidByte = "changed invalid byte";
    private const string ChangedValidPeer = "changed valid peer";
    private const string SwappedTextAndBytes = "swapped text and bytes";
    private const string HexLookalikeText = "hex lookalike text";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;
    private static readonly OperationDescriptor GetAgent = Catalogue.GetById("Rest.getAgent");
    private static readonly OperationDescriptor GetAllAgents = Catalogue.GetById("Rest.getAllAgents");

    private static readonly string OperationIdPath =
        $"$.paths['{GetAgent.Rest!.PathTemplate}'].{GetAgent.Rest.Verb.ToLowerInvariant()}.operationId";

    private static readonly string NoResponseConnect = DiscrepancyKinds.NoResponse(NoResponseReasons.Connect);

    private static readonly byte[] StaleLiveDocument = Encoding.UTF8.GetBytes("stale live document of an earlier run");

    // Created on first use, so only the drift check tests create this root or hold a runner; removed in
    // Dispose. The check compares with the class's _comparer, built on the same catalogue and rules.
    private string? _root;
    private HarnessOptions? _options;
    private CaseRunner? _http;

    /// <summary>The test's own repository root, which the contract and artifact paths resolve inside.</summary>
    private string Root => _root ??=
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"erewhon-openapi-check-{Guid.NewGuid():N}")).FullName;

    /// <summary>The check's settings, rooted at <see cref="Root"/>.</summary>
    private HarnessOptions Options => _options ??= new HarnessOptions { RepoRoot = Root, RequestTimeoutSeconds = RequestLimitSeconds };

    /// <summary>The runner the check fetches the live document with.</summary>
    private CaseRunner Http => _http ??= new(TimeSpan.FromSeconds(RequestLimitSeconds));

    /// <summary>
    /// Disposes the runner and deletes the repository root, where a drift check test created them, and disposes
    /// the console and deletes the directory, where an <c>export-openapi</c> test created them.
    /// </summary>
    public void Dispose()
    {
        _http?.Dispose();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        _exportConsole?.Dispose();
        if (_exportScratch is not null && Directory.Exists(_exportScratch))
        {
            Directory.Delete(_exportScratch, recursive: true);
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
            Directory.CreateDirectory(Options.ContractsOpenApiPath);
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
        Assert.False(File.Exists(Options.ContractsOpenApiPath), "The check must never create the checked-in contract.");
        Assert.Equal(directoryAtContractPath, Directory.Exists(Options.ContractsOpenApiPath));
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
    [InlineData(BothSides, InvalidUtf8)]
    [InlineData(ContractSide, NotJson)]
    [InlineData(LiveSide, NotJson)]
    public async Task MalformedSide_Fails_WithTheExactDiscrepancies(string side, string defect)
    {
        var contractText = ContractDocument();
        var liveText = LiveDocument();

        // An invalid byte inside the title. An undecodable document is the hex of its bytes after the byte order
        // mark, which the live side carries when both are undecodable, reported at the body as a whole; the bytes
        // are saved as served. A decodable document that is not JSON is the comparer's entry at the root.
        var undecodableContract = WithInvalidByte(contractText, 0xFF);
        var undecodableLive = WithInvalidByte(liveText, 0xFF);
        var (contractBytes, served) = (side, defect) switch
        {
            (ContractSide, InvalidUtf8) => (undecodableContract, Encoding.UTF8.GetBytes(liveText)),
            (LiveSide, InvalidUtf8) => (Encoding.UTF8.GetBytes(contractText), undecodableLive),
            (BothSides, InvalidUtf8) => (undecodableContract, WithByteOrderMark(undecodableLive)),
            (ContractSide, NotJson) => (Encoding.UTF8.GetBytes(NotJsonBody), Encoding.UTF8.GetBytes(liveText)),
            (LiveSide, NotJson) => (Encoding.UTF8.GetBytes(contractText), Encoding.UTF8.GetBytes(NotJsonBody)),
            _ => throw new ArgumentOutOfRangeException(nameof(side), $"{side}, {defect}", "Unknown malformed side or defect."),
        };
        Discrepancy[] expected = (side, defect) switch
        {
            (ContractSide, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableContract), DiscrepancyValue.Absent),
                new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableContract), DiscrepancyValue.Literal(liveText)),
            ],
            (LiveSide, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(undecodableLive)),
                new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(contractText), Hex(undecodableLive)),
            ],
            (BothSides, InvalidUtf8) =>
            [
                new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableContract), Hex(undecodableLive)),
            ],
            (ContractSide, NotJson) =>
            [
                new(JsonRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Literal(NotJsonBody), DiscrepancyValue.Absent),
            ],
            (LiveSide, NotJson) =>
            [
                new(JsonRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Literal(NotJsonBody)),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(side), $"{side}, {defect}", "Unknown malformed side or defect."),
        };
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        var contract = WriteContract(contractBytes);

        var result = await RunAsync(facade.BaseUrl);

        AssertDifference(result, expected);
        AssertOnlyFilesWritten(contract, served);
    }

    [Theory]
    [InlineData(ChangedInvalidByte)]
    [InlineData(ChangedValidPeer)]
    [InlineData(SwappedTextAndBytes)]
    [InlineData(HexLookalikeText)]
    public async Task DifferentUndecodableDocuments_GiveDifferentNatures(string change)
    {
        var contractText = ContractDocument();
        var liveText = LiveDocument();
        var changedLiveText = LiveDocument(operationId: GetAllAgents.JadeMethod);
        var undecodableContract = WithInvalidByte(contractText, 0xFF);
        var liveWithFf = WithInvalidByte(liveText, 0xFF);
        var liveWithFe = WithInvalidByte(liveText, 0xFE);
        byte[] textFf = "ff"u8.ToArray();
        byte[] textFe = "fe"u8.ToArray();
        byte[] text80 = "80"u8.ToArray();
        byte[] bytesFf = [0xFF];
        byte[] bytesFe = [0xFE];
        byte[] bytes80 = [0x80];

        // Two runs per row, each with its own stub and contract; the type entries of the swapped pair are equal,
        // so only the malformed entry, which names the undecodable side, tells the two apart. The hex lookalike
        // pair has equal kinds and values, so only the path, the body as a whole against the comparer's root,
        // tells two undecodable documents from two decodable non-JSON texts that spell their hexadecimal.
        (byte[] Contract, byte[] Served, Discrepancy[] Expected)[] runs = change switch
        {
            ChangedInvalidByte =>
            [
                (Encoding.UTF8.GetBytes(contractText), liveWithFf,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(liveWithFf)),
                    new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(contractText), Hex(liveWithFf)),
                ]),
                (Encoding.UTF8.GetBytes(contractText), liveWithFe,
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, Hex(liveWithFe)),
                    new(BodyPath, DiscrepancyKinds.Type, DiscrepancyValue.Literal(contractText), Hex(liveWithFe)),
                ]),
            ],
            ChangedValidPeer =>
            [
                (undecodableContract, Encoding.UTF8.GetBytes(liveText),
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableContract), DiscrepancyValue.Absent),
                    new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableContract), DiscrepancyValue.Literal(liveText)),
                ]),
                (undecodableContract, Encoding.UTF8.GetBytes(changedLiveText),
                [
                    new(BodyPath, DiscrepancyKinds.Malformed, Hex(undecodableContract), DiscrepancyValue.Absent),
                    new(BodyPath, DiscrepancyKinds.Type, Hex(undecodableContract), DiscrepancyValue.Literal(changedLiveText)),
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
                    new(JsonRootPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Literal("ff"), DiscrepancyValue.Literal("fe")),
                ]),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown change."),
        };

        var natures = new List<string>();
        foreach (var (contractBytes, served, expected) in runs)
        {
            await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
            var contract = WriteContract(contractBytes);

            var result = await RunAsync(facade.BaseUrl);

            AssertDifference(result, expected);
            AssertOnlyFilesWritten(contract, served);
            natures.Add(result.Failure!.Nature);
        }

        Assert.NotEqual(natures[0], natures[1]);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>The configuration key of the facade admin URL, <c>Harness:Target:AdminUrl</c>.</summary>
    private static string AdminUrlSetting =>
        $"{HarnessOptions.SectionName}:{nameof(HarnessOptions.Target)}:{nameof(HarnessEndpoints.AdminUrl)}";

    private string LiveDocumentPath => Path.Combine(Options.ArtifactsDirectory, "openapi", "live.json");

    private Task<ContractCheckResult> RunAsync(string adminUrl)
    {
        Options.Target.AdminUrl = adminUrl;
        return new OpenApiDriftCheck(Options, _comparer, Http).RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a stub that answers the admin OpenAPI route with <paramref name="status"/> and <paramref name="body"/>.</summary>
    private static Task<LoopbackStub> ServeAsync(int status, byte[] body) =>
        LoopbackStub.StartAsync(AdminRoutes.OpenApiDocument, status, body, TestContext.Current.CancellationToken);

    /// <summary>Writes the checked-in contract and returns its bytes.</summary>
    private byte[] WriteContract(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Options.ContractsOpenApiPath)!);
        File.WriteAllBytes(Options.ContractsOpenApiPath, bytes);
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
            expected.Add(Options.ContractsOpenApiPath);
        }

        if (liveDocument is not null)
        {
            expected.Add(LiveDocumentPath);
        }

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        if (contract is not null)
        {
            Assert.Equal(contract, File.ReadAllBytes(Options.ContractsOpenApiPath));
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

    /// <summary>The UTF-8 bytes of <paramref name="document"/> with <paramref name="invalid"/>, a byte UTF-8 never uses, right after the title.</summary>
    private static byte[] WithInvalidByte(string document, byte invalid)
    {
        var split = document.IndexOf(Title, StringComparison.Ordinal) + Title.Length;
        return [.. Encoding.UTF8.GetBytes(document[..split]), invalid, .. Encoding.UTF8.GetBytes(document[split..])];
    }

    /// <summary>The literal of an undecodable document: the lowercase hexadecimal of <paramref name="bytes"/>.</summary>
    private static DiscrepancyValue Hex(byte[] bytes) => DiscrepancyValue.Literal(Convert.ToHexStringLower(bytes));

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

// ---- The export-openapi mode --------------------------------------------------------------------
// The export-openapi mode's repository-root guard, in a temporary directory of its own: a root with no
// contracts directory, because the root is absent or contracts is a regular file, is refused with exit code 1
// before readiness, creating nothing; a root with contracts/ but no contracts/openapi gets only that leaf,
// holding the served document byte for byte; and a contracts directory removed after the check is never
// recreated. The facade admin listener is a loopback port nothing listens on, or a stub the test owns.
// These members live in this class because the harness test project keeps its fixed set of test classes,
// and the export writes the checked-in contract that the OpenAPI drift check above compares under the JSON
// semantic comparison this class covers.
public sealed partial class JsonSemanticComparerTests
{
    private const string LinePrefix = "export-openapi: ";
    private const string Refusal = "Harness:RepoRoot does not name the repository root. Nothing written.";

    // A refusal made after readiness would wait the whole limit; one made before returns at once. The request
    // limit is RequestLimitSeconds, which the drift check tests above declare with the same value.
    private const int ReadinessLimitSeconds = 30;

    // Well inside the readiness limit, and generous for a loaded host.
    private static readonly TimeSpan RefusalLimit = TimeSpan.FromSeconds(10);

    // Compact, with irregular spacing and a trailing newline, so a re-serialised write would differ.
    private static readonly byte[] Document =
        Encoding.UTF8.GetBytes("{\"openapi\":\"3.1.1\",  \"info\":{\"version\":\"v1\",\"title\":\"Erewhon REST\"},\"paths\":{}}\n");

    private static readonly byte[] ContractsFile = Encoding.UTF8.GetBytes("a file where the contracts directory belongs");

    // Created on first use, so only the export-openapi tests create this directory or console; released in
    // Dispose.
    private string? _exportScratch;
    private StringWriter? _exportConsole;

    /// <summary>How the configured repository root lacks a <c>contracts</c> directory.</summary>
    public enum NotRepository
    {
        /// <summary>The root and its parent do not exist.</summary>
        AbsentRoot,

        /// <summary>The root exists and its <c>contracts</c> entry is a regular file.</summary>
        ContractsIsAFile,
    }

    /// <summary>The export-openapi test's own directory, which every repository root of the test sits inside.</summary>
    private string ExportScratch => _exportScratch ??=
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"erewhon-export-openapi-{Guid.NewGuid():N}")).FullName;

    /// <summary>The console the export-openapi mode writes its line to.</summary>
    private StringWriter ExportConsole => _exportConsole ??= new(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(NotRepository.AbsentRoot)]
    [InlineData(NotRepository.ContractsIsAFile)]
    public async Task RootWithoutContractsDirectory_IsRefusedBeforeReadiness_AndCreatesNothing(NotRepository root)
    {
        var repoRoot = Path.Combine(ExportScratch, "absent", "root");
        if (root == NotRepository.ContractsIsAFile)
        {
            repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
            File.WriteAllBytes(Path.Combine(repoRoot, "contracts"), ContractsFile);
        }

        var before = Entries();
        var options = ExportOptions(repoRoot, string.Create(CultureInfo.InvariantCulture, $"http://{IPAddress.Loopback}:{ClosedPorts.Take(1).Single()}"));

        var stopwatch = Stopwatch.StartNew();
        var exitCode = await new ExportOpenApiMode(options, ExportConsole).RunAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"{LinePrefix}no contracts directory at {Path.Combine(repoRoot, "contracts")}; {Refusal}{Environment.NewLine}",
            ExportConsole.ToString());
        Assert.True(stopwatch.Elapsed < RefusalLimit, "The refusal must come before the readiness wait.");
        Assert.Equal(before, Entries());
        if (root == NotRepository.ContractsIsAFile)
        {
            Assert.Equal(ContractsFile, File.ReadAllBytes(Path.Combine(repoRoot, "contracts")));
        }
    }

    [Fact]
    public async Task RootWithContractsButNoOpenApiDirectory_CreatesOnlyTheLeaf_AndWritesTheServedBytes()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, "contracts"));
        var target = Path.Combine(repoRoot, "contracts", "openapi", "erewhon-rest.openapi.json");
        await using var facade = await FacadeAdminStub.StartAsync(Document, beforeDocument: null, TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{LinePrefix}wrote {Document.Length} bytes to {target}{Environment.NewLine}"),
            ExportConsole.ToString());
        string[] created = [repoRoot, Path.Combine(repoRoot, "contracts"), Path.GetDirectoryName(target)!, target];
        Assert.Equal(created, Entries());
        Assert.Equal(Document, File.ReadAllBytes(target));
        Assert.Equal(AdminRoutes.OpenApiDocument, facade.Requests[^1]);
        Assert.All(facade.Requests.SkipLast(1), request => Assert.Equal(AdminRoutes.Health, request));
    }

    [Fact]
    public async Task ContractsDirectoryRemovedBeforeTheWrite_FailsWithExitOne_AndIsNotRecreated()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        var contracts = Directory.CreateDirectory(Path.Combine(repoRoot, "contracts")).FullName;
        var openApiDirectory = Path.Combine(contracts, "openapi");
        await using var facade = await FacadeAdminStub.StartAsync(
            Document,
            beforeDocument: () => Directory.Delete(contracts, recursive: true),
            TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"{LinePrefix}failed: {typeof(IOException).FullName}: The contract directory '{openApiDirectory}' cannot be created: "
                + $"its parent '{contracts}' is not a directory.{Environment.NewLine}",
            ExportConsole.ToString());
        Assert.Equal(new[] { repoRoot }, Entries());
    }

    private static HarnessOptions ExportOptions(string repoRoot, string adminUrl) => new()
    {
        RepoRoot = repoRoot,
        Target = new HarnessEndpoints { AdminUrl = adminUrl },
        ReadinessTimeoutSeconds = ReadinessLimitSeconds,
        ReadinessPollIntervalSeconds = 1,
        RequestTimeoutSeconds = RequestLimitSeconds,
    };

    /// <summary>Every file and directory under the export-openapi test's directory, in ordinal order.</summary>
    private string[] Entries() =>
        [.. Directory.EnumerateFileSystemEntries(ExportScratch, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>
    /// An in-process facade admin listener on 127.0.0.1 and an ephemeral port: <c>GET /health</c> answers 200, and
    /// <c>GET /openapi/v1.json</c> runs an optional action and then answers 200 with the exact document bytes; anything else
    /// answers 404. It records every request target it receives, before answering.
    /// </summary>
    private sealed class FacadeAdminStub : IAsyncDisposable
    {
        // Bounds the graceful stop; idle keep-alive connections close at once, so it is never reached.
        private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _requests;

        private FacadeAdminStub(WebApplication app, ConcurrentQueue<string> requests, Uri baseUrl)
        {
            _app = app;
            _requests = requests;
            BaseUrl = baseUrl.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>The stub's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
        public string BaseUrl { get; }

        /// <summary>Every request target received so far, raw and in arrival order.</summary>
        public IReadOnlyList<string> Requests => [.. _requests];

        /// <summary>Starts a stub that serves <paramref name="document"/>, after running <paramref name="beforeDocument"/> when given.</summary>
        public static async Task<FacadeAdminStub> StartAsync(byte[] document, Action? beforeDocument, CancellationToken ct)
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
                if (!HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (string.Equals(target, AdminRoutes.Health, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                if (!string.Equals(target, AdminRoutes.OpenApiDocument, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                beforeDocument?.Invoke();
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = document.Length;
                await context.Response.Body.WriteAsync(document, context.RequestAborted);
            });

            try
            {
                await app.StartAsync(ct);
                var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
                return new FacadeAdminStub(app, requests, new Uri(address));
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
}
