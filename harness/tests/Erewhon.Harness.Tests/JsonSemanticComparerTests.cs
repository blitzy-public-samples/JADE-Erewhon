using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Modes;
using Erewhon.Harness.Report;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

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
/// Evidence that would outgrow its budget, through a long path repeated by many differences, is refused, and
/// a comparison within the budget is unchanged.
/// It also covers the OpenAPI drift check, which compares the live OpenAPI document with the checked-in
/// contract under this JSON semantic comparison, against an in-process loopback stub in a temporary
/// repository root of its own. It also covers the <c>export-openapi</c> mode, which writes that checked-in
/// contract: its repository-root guard, the readiness wait's ready and not-ready lines on the mode's own writer,
/// the creation of only the <c>contracts/openapi</c> leaf, the refusal of a link on the way to the contract, and
/// the served document written byte for byte over a previous contract that a failed fetch keeps, in a temporary
/// directory of its own.
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

    // A missing item is recorded in full after the count, typed as a DTOAgent: its generated guid as the
    // rule's token, every other member as its literal.
    [Fact]
    public void MissingArrayItem_IsALengthDifference_ThenTheWholeMissingItem()
    {
        var hank = AgentJson(ValidGuidA);
        var peter = AgentJson(ValidGuidB, name: "Peter Smallsmith");

        Discrepancy[] expected =
        [
            new("$", DiscrepancyKinds.Length, DiscrepancyValue.Literal("2"), DiscrepancyValue.Literal("1")),
            .. AgentEvidence("$[1]", DiscrepancyKinds.Missing, name: "Peter Smallsmith"),
        ];
        Assert.Equal(expected, Compare($"[{hank},{peter}]", $"[{hank}]", DtoAgentArray));
    }

    [Fact]
    public void ExtraArrayItem_IsALengthDifference_ThenCommonItemsByIndex_ThenTheWholeExtraItem()
    {
        var hank = AgentJson(ValidGuidA);
        var mary = AgentJson(ValidGuidA, name: "Mary Jones");
        var baseline = $"[{hank},{AgentJson(ValidGuidA, name: "Peter Smallsmith")},{mary}]";

        // Every common item is walked as a DTOAgent at its own index: the second agent's regenerated guid
        // compares equal, and the third agent's changed website is reported after the count. The extra
        // fourth agent is then recorded in full at its own index.
        var observed =
            $"[{hank},{AgentJson(ValidGuidB, name: "Peter Smallsmith")},{AgentJson(ValidGuidA, website: "www.mary.example", name: "Mary Jones")},{hank}]";

        Discrepancy[] expected =
        [
            new("$", DiscrepancyKinds.Length, DiscrepancyValue.Literal("3"), DiscrepancyValue.Literal("4")),
            new("$[2].website", DiscrepancyKinds.Value, DiscrepancyValue.Literal(Quoted("www.example.com")), DiscrepancyValue.Literal(Quoted("www.mary.example"))),
            .. AgentEvidence("$[3]", DiscrepancyKinds.Extra),
        ];
        Assert.Equal(expected, Compare(baseline, observed, DtoAgentArray));
    }

    // The report's example: the same count difference with different unmatched items gives different lists.
    [Theory]
    [InlineData("[1,2]", "2")]
    [InlineData("[1,3]", "3")]
    public void ExtraArrayItem_IsRecordedWithItsValue(string observed, string expectedExtraLiteral)
    {
        Discrepancy[] expected =
        [
            LiteralDiscrepancy("$", DiscrepancyKinds.Length, "1", "2"),
            LiteralDiscrepancy("$[1]", DiscrepancyKinds.Extra, null, expectedExtraLiteral),
        ];
        Assert.Equal(expected, Compare("[1]", observed, declaredType: null));
    }

    /// <summary>
    /// A baseline and two observations that share their count difference, but not their unmatched
    /// content outside normalised positions, with the declared type the bodies are compared as.
    /// </summary>
    public static TheoryData<string, string, string, string?> SameCountDifferenceOtherUnmatchedContent() => new()
    {
        { "[1]", "[1,2]", "[1,3]", null },
        { "[1]", "[1,[2]]", "[1,[3]]", null },
        { "[1]", """[1,{"a":2}]""", """[1,{"b":2}]""", null },
        { "[1]", "[1,{}]", "[1,[]]", null },
        {
            $"[{AgentJson(ValidGuidA)}]",
            $"[{AgentJson(ValidGuidA)},{AgentJson(ValidGuidB, name: "Peter Smallsmith")}]",
            $"[{AgentJson(ValidGuidA)},{AgentJson(ValidGuidB, name: "Mary Jones")}]",
            DtoAgentArray
        },
        {
            ResultJson(null, 400, Errors("Street is required")),
            ResultJson(null, 400, Errors("Street is required", "City is required")),
            ResultJson(null, 400, Errors("Street is required", "Country is required")),
            DtoResult
        },
        { """{"name":"a"}""", """{"name":"a","name":"b"}""", """{"name":"a","name":"c"}""", DtoAgent },
    };

    // A changed unmatched item or occurrence is a different nature, so a documented difference never covers it.
    [Theory]
    [MemberData(nameof(SameCountDifferenceOtherUnmatchedContent))]
    public void SameCountDifference_WithOtherUnmatchedContent_GivesAnotherSignature(string baseline, string first, string second, string? declaredType)
    {
        var firstDiscrepancies = Compare(baseline, first, declaredType);
        var secondDiscrepancies = Compare(baseline, second, declaredType);

        Assert.Equal(firstDiscrepancies[0], secondDiscrepancies[0]);
        Assert.Equal(DiscrepancyKinds.Length, firstDiscrepancies[0].Kind);
        Assert.NotEqual(DiscrepancySignature.Compute(firstDiscrepancies), DiscrepancySignature.Compute(secondDiscrepancies));
        Assert.NotEqual(DiscrepancySignature.Compute(Compare(first, baseline, declaredType)), DiscrepancySignature.Compute(Compare(second, baseline, declaredType)));
    }

    /// <summary>
    /// Unmatched array items of the declared array type, extra and missing: an item, the same item with
    /// other valid N1 and N2 values, and the same item with an exactly compared member also changed.
    /// </summary>
    public static TheoryData<string, string, string, string, bool> UnmatchedItemVariants()
    {
        (string Item, string SameUnderRules, string Changed, string ArrayType)[] variants =
        [
            (
                AgentJson(ValidGuidA, name: "Peter Smallsmith"),
                AgentJson(ValidGuidB, name: "Peter Smallsmith"),
                AgentJson(ValidGuidB, website: "www.peter.example", name: "Peter Smallsmith"),
                DtoAgentArray),
            (
                SaleJson(DtoRetailSale, ValidGuidA, TimestampA),
                SaleJson(DtoRetailSale, ValidGuidB, TimestampB),
                SaleJson(DtoRetailSale, ValidGuidB, TimestampLookalike),
                DtoSaleArray),
        ];

        var data = new TheoryData<string, string, string, string, bool>();
        foreach (var (item, sameUnderRules, changed, arrayType) in variants)
        {
            data.Add(item, sameUnderRules, changed, arrayType, true);
            data.Add(item, sameUnderRules, changed, arrayType, false);
        }

        return data;
    }

    // An unmatched item is typed by the array's item type, so its valid generated guids and timestamps
    // contribute only their rule's token: they never make a new nature, while any other change does.
    [Theory]
    [MemberData(nameof(UnmatchedItemVariants))]
    public void UnmatchedItem_HonoursTheRules_AndRecordsEveryOtherMember(string item, string sameUnderRules, string changed, string arrayType, bool observedIsLonger)
    {
        IReadOnlyList<Discrepancy> Unmatched(string tail) =>
            observedIsLonger
                ? Compare($"[{item}]", $"[{item},{tail}]", arrayType)
                : Compare($"[{item},{tail}]", $"[{item}]", arrayType);

        var original = Unmatched(item);

        Assert.Equal(original, Unmatched(sameUnderRules));
        Assert.NotEqual(DiscrepancySignature.Compute(original), DiscrepancySignature.Compute(Unmatched(changed)));
        Assert.Contains(
            OneSidedEvidence("$[1].guid", observedIsLonger ? DiscrepancyKinds.Extra : DiscrepancyKinds.Missing, DiscrepancyValue.Valid(N1)),
            original);
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

    /// <summary>Every targeted position, with the longer of two arrays on the observed side and on the baseline side.</summary>
    public static TheoryData<string, bool> TargetedArraysOfDifferentLengths()
    {
        var data = new TheoryData<string, bool>();
        foreach (var position in TargetedPositions)
        {
            data.Add(position.Name, true);
            data.Add(position.Name, false);
        }

        return data;
    }

    // Two arrays at a targeted position are compared by the untyped walk, so the unmatched items of the
    // longer one are recorded in full and untyped: a valid GUID inside them is an ordinary literal.
    [Theory]
    [MemberData(nameof(TargetedArraysOfDifferentLengths))]
    public void TargetedArraysOfDifferentLengths_RecordTheUnmatchedItemsUntyped(string target, bool observedIsLonger)
    {
        var position = Targeted(target);
        const string Shorter = "[1]";
        var longer = $$"""[1,2,{"guid":"{{ValidGuidA}}"}]""";
        var kind = observedIsLonger ? DiscrepancyKinds.Extra : DiscrepancyKinds.Missing;

        Discrepancy[] expected =
        [
            LiteralDiscrepancy(position.Path, DiscrepancyKinds.Length, observedIsLonger ? "1" : "3", observedIsLonger ? "3" : "1"),
            OneSidedEvidence(position.Path + "[1]", kind, DiscrepancyValue.Literal("2")),
            OneSidedEvidence(position.Path + "[2]", kind, DiscrepancyValue.Literal("{}")),
            OneSidedEvidence(position.Path + "[2].guid", kind, DiscrepancyValue.Literal(Quoted(ValidGuidA))),
        ];
        Assert.Equal(
            expected,
            observedIsLonger
                ? Compare(position.Body(Shorter), position.Body(longer), position.DeclaredType)
                : Compare(position.Body(longer), position.Body(Shorter), position.DeclaredType));
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
                LiteralDiscrepancy("$.name#1", DiscrepancyKinds.Value, Quoted("a"), Quoted("b")),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Value, Quoted("b"), Quoted("a")),
            ]
        },
        {
            """{"name":"a","name":"b"}""",
            """{"name":"a"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "2", "1"),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Missing, Quoted("b"), null),
            ]
        },
        {
            """{"name":"a"}""",
            """{"name":"a","name":"b"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "1", "2"),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Extra, null, Quoted("b")),
            ]
        },
        {
            """{"name":"a"}""",
            """{"name":"a","name":"c"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "1", "2"),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Extra, null, Quoted("c")),
            ]
        },
        {
            """{"name":"a","name":"b"}""",
            """{"name":"c"}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "2", "1"),
                LiteralDiscrepancy("$.name#1", DiscrepancyKinds.Value, Quoted("a"), Quoted("c")),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Missing, Quoted("b"), null),
            ]
        },
        {
            """{"name":"a"}""",
            """{"name":"a","name":{"x":[1]}}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "1", "2"),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Extra, null, "{}"),
                LiteralDiscrepancy("$.name#2.x", DiscrepancyKinds.Extra, null, "[]"),
                LiteralDiscrepancy("$.name#2.x[0]", DiscrepancyKinds.Extra, null, "1"),
            ]
        },
        {
            """{"name":"a"}""",
            """{"name":"a","name":{"x":1,"y":2,"x":3}}""",
            [
                LiteralDiscrepancy("$.name", DiscrepancyKinds.Length, "1", "2"),
                LiteralDiscrepancy("$.name#2", DiscrepancyKinds.Extra, null, "{}"),
                LiteralDiscrepancy("$.name#2.x#1", DiscrepancyKinds.Extra, null, "1"),
                LiteralDiscrepancy("$.name#2.x#2", DiscrepancyKinds.Extra, null, "3"),
                LiteralDiscrepancy("$.name#2.y", DiscrepancyKinds.Extra, null, "2"),
            ]
        },
        {
            """{"nickname":"a","nickname":"b"}""",
            "{}",
            [
                LiteralDiscrepancy("$.nickname#1", DiscrepancyKinds.Missing, Quoted("a"), null),
                LiteralDiscrepancy("$.nickname#2", DiscrepancyKinds.Missing, Quoted("b"), null),
            ]
        },
        {
            "{}",
            """{"nickname":1,"nickname":[2]}""",
            [
                LiteralDiscrepancy("$.nickname#1", DiscrepancyKinds.Extra, null, "1"),
                LiteralDiscrepancy("$.nickname#2", DiscrepancyKinds.Extra, null, "[2]"),
            ]
        },
        {
            """{"x y":1,"x y":2}""",
            """{"x y":1,"x y":3}""",
            [LiteralDiscrepancy("$['x y']#2", DiscrepancyKinds.Value, "2", "3")]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":"{{ValidGuidA}}"}""",
            $$"""{"guid":"{{ValidGuidB}}"}""",
            [
                LiteralDiscrepancy("$.guid", DiscrepancyKinds.Length, "2", "1"),
                new Discrepancy("$.guid#2", DiscrepancyKinds.Missing, DiscrepancyValue.Valid(N1), DiscrepancyValue.Absent),
            ]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}"}""",
            $$$"""{"guid":"{{{ValidGuidB}}}","guid":"{{{GuidLookalike}}}","guid":null,"guid":{"guid":"{{{ValidGuidA}}}"}}""",
            [
                LiteralDiscrepancy("$.guid", DiscrepancyKinds.Length, "1", "4"),
                LiteralDiscrepancy("$.guid#2", DiscrepancyKinds.Extra, null, Quoted(GuidLookalike)),
                LiteralDiscrepancy("$.guid#3", DiscrepancyKinds.Extra, null, "null"),
                LiteralDiscrepancy("$.guid#4", DiscrepancyKinds.Extra, null, "{}"),
                LiteralDiscrepancy("$.guid#4.guid", DiscrepancyKinds.Extra, null, Quoted(ValidGuidA)),
            ]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":"{{ValidGuidA}}"}""",
            $$"""{"guid":"{{ValidGuidB}}","guid":"{{GuidLookalike}}"}""",
            [new Discrepancy("$.guid#2", DiscrepancyKinds.Value, DiscrepancyValue.Valid(N1), DiscrepancyValue.Literal(Quoted(GuidLookalike)))]
        },
        {
            $$"""{"guid":"{{ValidGuidA}}","guid":null}""",
            "{}",
            [
                new Discrepancy("$.guid#1", DiscrepancyKinds.Missing, DiscrepancyValue.Valid(N1), DiscrepancyValue.Absent),
                LiteralDiscrepancy("$.guid#2", DiscrepancyKinds.Missing, "null", null),
            ]
        },
    };

    // A name repeated within one object is compared occurrence by occurrence in document order: a count
    // difference is a length discrepancy followed by the common occurrences and then by each unmatched
    // occurrence in full, a name only one side holds gives one missing or extra discrepancy per occurrence,
    // and at a targeted position every occurrence follows the rule, an object or array there untyped. Once
    // either side repeats the name, each occurrence is reported at the member path with #n, its 1-based
    // position on its side, and only the length stays at the member path.
    [Theory]
    [MemberData(nameof(RepeatedMemberNames))]
    public void RepeatedMemberNames_CompareOccurrenceByOccurrence(string baseline, string observed, Discrepancy[] expected)
    {
        Assert.Equal(expected, Compare(baseline, observed, DtoAgent));
    }

    /// <summary>
    /// A baseline and two observations that hold the same occurrences of a repeated member name, or the
    /// same entries below them, in another order, so the comparison of the two observations differs.
    /// </summary>
    public static TheoryData<string, string, string> ReorderedOccurrences() => new()
    {
        { "[1]", """[1,{"a":1,"a":2}]""", """[1,{"a":2,"a":1}]""" },
        { "[1]", """[1,{"a":{"x":1},"a":{"y":2}}]""", """[1,{"a":{"y":2},"a":{"x":1}}]""" },
        { """{"a":1,"a":1}""", """{"a":2,"a":3}""", """{"a":3,"a":2}""" },
        { "{}", """{"a":{"x":1},"a":{"y":2}}""", """{"a":{"y":2},"a":{"x":1}}""" },
        { """{"a":1}""", """{"a":1,"a":{"x":1},"a":{"y":2}}""", """{"a":1,"a":{"y":2},"a":{"x":1}}""" },
    };

    // Which occurrence a value or a nested entry belongs to is part of the evidence: reordered occurrences
    // give another signature, whether paired, unmatched or held by one side only, in either direction.
    [Theory]
    [MemberData(nameof(ReorderedOccurrences))]
    public void ReorderedOccurrences_GiveAnotherSignature(string baseline, string first, string second)
    {
        Assert.NotEmpty(Compare(first, second, declaredType: null));
        Assert.NotEqual(
            DiscrepancySignature.Compute(Compare(baseline, first, declaredType: null)),
            DiscrepancySignature.Compute(Compare(baseline, second, declaredType: null)));
        Assert.NotEqual(
            DiscrepancySignature.Compute(Compare(first, baseline, declaredType: null)),
            DiscrepancySignature.Compute(Compare(second, baseline, declaredType: null)));
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

    // ---- Discrepancy evidence budget -------------------------------------------------------------

    // Every discrepancy carries its whole path, so one more differing leaf below a 65,536-character member name
    // than EvidenceByteLimit characters hold copies of it, about 70 KB a side, would retain more evidence than
    // the limit allows. The comparison is refused with a message that names the limit and quotes nothing from
    // the bodies.
    [Fact]
    public void ManyDifferingLeaves_BelowALongMemberName_AreRefused()
    {
        var name = new string('n', 64 * 1024);
        var leaves = Enumerable.Range(0, (HarnessOptions.EvidenceByteLimit / name.Length) + 1).ToArray();
        string Body(int value) =>
            "{\"" + name + "\":{"
            + string.Join(",", leaves.Select(leaf => string.Create(CultureInfo.InvariantCulture, $"\"a{leaf}\":{value}")))
            + "}}";

        var refusal = Assert.Throws<InvalidDataException>(() => Compare(Body(1), Body(2), declaredType: null));

        Assert.Equal(EvidenceRefusal(HarnessOptions.EvidenceByteLimit), refusal.Message);
    }

    // Within the budget a comparison gives exactly the list an unlimited one gives: with the limit set to that
    // list's evidence, each path plus each value as the report shows it, an absent side as "(absent)" and a valid
    // one as "{valid:N1}", the list is returned unchanged, and one character less refuses it.
    [Theory]
    [InlineData("""{"name":"Hank Williams","website":"www.it.com"}""", """{"name":"Hank Williams","website":"www.it.com "}""", 0)]
    [InlineData("""{"name":"Hank Williams","website":"www.it.com"}""", """{"name":"Hank Williams","website":"www.it.com "}""", 1)]
    [InlineData("""{"name":"Hank Williams"}""", """{"name":"Hank Williams","nicknames":["Hank","H"]}""", 0)]
    [InlineData("""{"name":"Hank Williams"}""", """{"name":"Hank Williams","nicknames":["Hank","H"]}""", 1)]
    [InlineData($$"""{"guid":"{{ValidGuidA}}","name":"Hank Williams"}""", """{"name":"Hank Williams"}""", 0)]
    [InlineData($$"""{"guid":"{{ValidGuidA}}","name":"Hank Williams"}""", """{"name":"Hank Williams"}""", 1)]
    public void Comparison_AtTheEvidenceLimit_IsUnchanged_OneCharacterLessIsRefused(string baseline, string observed, int shortfall)
    {
        var unlimited = Compare(baseline, observed, DtoAgent);
        var limit = unlimited.Sum(discrepancy => discrepancy.Path.Length + discrepancy.Baseline.ToString().Length + discrepancy.Observed.ToString().Length) - shortfall;
        var comparer = new JsonSemanticComparer(OperationCatalogue.Default, new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)), limit);

        Assert.NotEmpty(unlimited);
        if (shortfall == 0)
        {
            Assert.Equal(unlimited, comparer.Compare(baseline, observed, DtoAgent));
        }
        else
        {
            var refusal = Assert.Throws<InvalidDataException>(() => comparer.Compare(baseline, observed, DtoAgent));
            Assert.Equal(EvidenceRefusal(limit), refusal.Message);
        }
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

    /// <summary>The message of a comparison refused because its discrepancy evidence exceeds <paramref name="limit"/> characters.</summary>
    private static string EvidenceRefusal(int limit) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The discrepancy evidence of one comparison exceeds the {limit}-character limit, so the comparison is refused rather than truncated.");

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

    /// <summary>
    /// A <c>missing</c> or <c>extra</c> discrepancy whose one side is <paramref name="value"/>: the baseline
    /// side for missing, the observed side for extra, the other side absent.
    /// </summary>
    private static Discrepancy OneSidedEvidence(string path, string kind, DiscrepancyValue value) =>
        kind == DiscrepancyKinds.Missing
            ? new(path, kind, value, DiscrepancyValue.Absent)
            : new(path, kind, DiscrepancyValue.Absent, value);

    /// <summary>
    /// The evidence of an <see cref="AgentJson"/> agent with a valid generated guid that only one side holds
    /// at <paramref name="path"/>, in walk order: the object, then each member in document order, the guid
    /// as the N1 token and every other member as its literal.
    /// </summary>
    private static Discrepancy[] AgentEvidence(string path, string kind, string website = "www.example.com", string name = "Hank Williams")
    {
        (string Member, DiscrepancyValue Value)[] members =
        [
            ("guid", DiscrepancyValue.Valid(N1)),
            ("name", DiscrepancyValue.Literal(Quoted(name))),
            ("street", DiscrepancyValue.Literal(Quoted("12 Music Row"))),
            ("city", DiscrepancyValue.Literal(Quoted("Nashville"))),
            ("country", DiscrepancyValue.Literal(Quoted("United States of America"))),
            ("email", DiscrepancyValue.Literal(Quoted("hank@example.com"))),
            ("fax", DiscrepancyValue.Literal(Quoted(HankFax))),
            ("phone", DiscrepancyValue.Literal(Quoted("1 8 2938 4756"))),
            ("website", DiscrepancyValue.Literal(Quoted(website))),
        ];

        return
        [
            OneSidedEvidence(path, kind, DiscrepancyValue.Literal("{}")),
            .. members.Select(member => OneSidedEvidence($"{path}.{member.Member}", kind, member.Value)),
        ];
    }

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
// not-run; an absent checked-in contract is missing and is never created, and a FIFO or a link in its place is
// refused at once; documents that differ only in
// member order, whitespace or a byte order mark pass; changed, missing and extra members and an undecodable or
// non-JSON document fail with exactly the discrepancies the signature is computed over, an undecodable one at
// $body and a non-JSON one at $, and undecodable documents that differ in an invalid byte, in their decodable
// peer or in which side decodes, or from non-JSON texts that spell their hexadecimal, give different natures;
// and the check writes only artifacts/openapi/live.json, byte for byte as served, removing a stale one when
// nothing was answered or the document was over the runner's limit and reporting a failed save in its detail,
// and never rewrites the contract. The runner the check fetches with returns a body at its byte limit whole,
// framed by its length or chunked, and refuses one over it, a declared length over it before any body byte.
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

    // Stand for the test's temporary root and a loopback port, which differ in every run, in what a failure
    // message shows; the check prints neither token.
    private const string RootToken = "<root>";
    private const string PortToken = "<port>";

    // A detail names the stub's port and paths below the root, so the failing item's copy is compared unshown.
    private const string FailureDetailMessage = "The failing item must carry the check's detail.";

    // Request limit and connect limit: generous for a loaded host, never reached by a passing test.
    private const int RequestLimitSeconds = 10;

    // A refused contract read ends well within this; one that blocked on a FIFO fails the test instead of hanging.
    private static readonly TimeSpan ContractReadLimit = TimeSpan.FromSeconds(10);

    // The body limit the response body tests give their runner: past its first read buffer, far below the
    // evidence limit, so no test sends 32 MiB.
    private const int ResponseLimit = 200_000;

    // The size of each chunk of a chunked test body.
    private const int ChunkBytes = 16 * 1024;

    // A request limit far beyond ContractReadLimit, so only a prompt refusal ends a call within that limit.
    private static readonly TimeSpan SlowRequestLimit = TimeSpan.FromSeconds(60);

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

    // The port of a loopback URL, as a detail names the stub it fetched from.
    private static readonly Regex LoopbackPort = new(@"(?<=127\.0\.0\.1:)[0-9]+", RegexOptions.CultureInvariant);

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
        Assert.Contains(AdminUrlSetting, Redacted(result.Result.Detail), StringComparison.Ordinal);
        Assert.DoesNotContain(UserInfoPassword, Redacted(result.Result.Detail), StringComparison.Ordinal);
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
        Assert.Contains(NoResponseConnect, Redacted(result.Result.Detail), StringComparison.Ordinal);
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
        Assert.True(string.Equals(result.Result.Detail, failure.Detail, StringComparison.Ordinal), FailureDetailMessage);
        Assert.Empty(result.Discrepancies);
        Assert.Equal(AdminRoutes.OpenApiDocument, Assert.Single(facade.Requests));
        Assert.False(File.Exists(Options.ContractsOpenApiPath), "The check must never create the checked-in contract.");
        Assert.Equal(directoryAtContractPath, Directory.Exists(Options.ContractsOpenApiPath));
        AssertOnlyFilesWritten(contract: null, served);
    }

    // A FIFO nobody writes to at the contract path, and a link there to an equal contract outside the root, are
    // neither missing nor followed: the read is refused at once and the refusal propagates, as an unreadable
    // contract does.
    [Theory]
    [InlineData("fifo", "ConfinedTree could not read 'contracts/openapi/erewhon-rest.openapi.json': it is not a regular file (a FIFO).")]
    [InlineData(
        "link",
        "ConfinedTree never reads through a link: refused 'contracts/openapi/erewhon-rest.openapi.json', because "
            + "'contracts/openapi/erewhon-rest.openapi.json' is a symbolic link.")]
    public async Task ContractThatIsAFifoOrALink_IsRefusedPromptly(string entry, string expectedRefusal)
    {
        var ct = TestContext.Current.CancellationToken;
        using var outside = new TempDirectory();
        await using var facade = await ServeAsync(StatusCodes.Status200OK, Encoding.UTF8.GetBytes(LiveDocument()));
        var path = Options.ContractsOpenApiPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entry == "fifo")
        {
            SpecialFileSupport.CreateFifo(path);
        }
        else
        {
            File.CreateSymbolicLink(path, outside.Write("erewhon-rest.openapi.json", ContractDocument()));
        }

        var refusal = await Assert.ThrowsAsync<IOException>(
            () => Task.Run(() => RunAsync(facade.BaseUrl), ct).WaitAsync(ContractReadLimit, ct));

        Assert.Equal(expectedRefusal, refusal.Message);
        Assert.True(new FileInfo(path).LinkTarget is not null || SpecialFileSupport.IsFifo(path), "The refused entry must stay in place.");
    }

    // A live document over the runner's limit is refused: the refusal propagates, and a live.json an earlier
    // run left is removed, so it never passes for this run's evidence.
    [Fact]
    public async Task OversizedLiveDocument_IsRefused_AndRemovesTheStaleLiveDocument()
    {
        var served = Encoding.UTF8.GetBytes(LiveDocument());
        await using var facade = await ServeAsync(StatusCodes.Status200OK, served);
        var contract = WriteContract(Encoding.UTF8.GetBytes(ContractDocument()));
        WriteStaleLiveDocument();
        Options.Target.AdminUrl = facade.BaseUrl;
        using var limited = new CaseRunner(TimeSpan.FromSeconds(RequestLimitSeconds), served.Length - 1);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new OpenApiDriftCheck(Options, _comparer, limited).RunAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(LiveDocumentPath), "A stale live document must be removed.");
        AssertOnlyFilesWritten(contract, liveDocument: null);
    }

    // ---- Response body limit -------------------------------------------------------------------------

    // A body at the runner's limit is returned whole, framed by its length or chunked; one byte more is refused
    // either way, as is a declared length over the limit whose body never comes, which is refused before any
    // body byte is read: the server holds that connection open, so a runner that read the body would wait for
    // its whole request limit. The refusal names the limit only, never the target.
    [Theory]
    [InlineData("content-length", ResponseLimit, false)]
    [InlineData("chunked", ResponseLimit, false)]
    [InlineData("content-length", ResponseLimit + 1, true)]
    [InlineData("chunked", ResponseLimit + 1, true)]
    [InlineData("declared-only", 1024 * 1024 * 1024, true)]
    public async Task ResponseBody_AtTheLimitIsWhole_OverItIsRefused(string framing, int length, bool refused)
    {
        var ct = TestContext.Current.CancellationToken;
        var (response, body) = RawResponse(framing, length);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serving = ServeOnceAsync(listener, response, ct);
        using var runner = new CaseRunner(SlowRequestLimit, ResponseLimit);
        var uri = new Uri(string.Create(
            CultureInfo.InvariantCulture, $"http://{IPAddress.Loopback}:{((IPEndPoint)listener.LocalEndpoint).Port}{AdminRoutes.OpenApiDocument}"));

        var call = runner.GetAsync(uri, SlowRequestLimit, ct).WaitAsync(ContractReadLimit, ct);
        if (refused)
        {
            var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => call);
            Assert.Equal(
                $"The response body exceeds the {ResponseLimit}-byte limit on one response, so the response is refused rather than truncated.",
                refusal.Message);
        }
        else
        {
            var answered = await call;
            Assert.Equal(StatusCodes.Status200OK, answered.Status);
            Assert.Equal(body, answered.Body);
        }

        await serving.WaitAsync(ContractReadLimit, ct);
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

        Assert.Equal(
            new CheckResult(CheckNames.OpenApiDrift, CheckOutcomes.Pass, Cases: 1, Passed: 1, Detail: null),
            result.Result with { Detail = Redacted(result.Result.Detail) });
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
        Assert.Contains(Redacted(LiveDocumentPath), Redacted(result.Result.Detail), StringComparison.Ordinal);
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

    /// <summary>The configuration key of the facade admin URL, <c>Harness:Target:AdminUrl</c>.</summary>
    private static string AdminUrlSetting =>
        $"{HarnessOptions.SectionName}:{nameof(HarnessOptions.Target)}:{nameof(HarnessEndpoints.AdminUrl)}";

    private string LiveDocumentPath => Path.Combine(Options.ArtifactsDirectory, "openapi", "live.json");

    /// <summary>
    /// <paramref name="text"/> with the test's root replaced by <see cref="RootToken"/> and every loopback port by
    /// <see cref="PortToken"/>, so a failure message reads the same in every run. A comparison with a literal that
    /// holds neither holds exactly when the raw one does, and so does one of two paths below the root, which hold
    /// no loopback URL.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    private string? Redacted(string? text) =>
        text is null ? null : LoopbackPort.Replace(text.Replace(Root, RootToken, StringComparison.Ordinal), PortToken);

    private Task<ContractCheckResult> RunAsync(string adminUrl)
    {
        Options.Target.AdminUrl = adminUrl;
        return new OpenApiDriftCheck(Options, _comparer, Http).RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts a stub that answers the admin OpenAPI route with <paramref name="status"/> and <paramref name="body"/>.</summary>
    private static Task<LoopbackStub> ServeAsync(int status, byte[] body) =>
        LoopbackStub.StartAsync(AdminRoutes.OpenApiDocument, status, body, TestContext.Current.CancellationToken);

    /// <summary>
    /// The exact bytes of a 200 response whose body is <paramref name="length"/> deterministic bytes, framed by
    /// <c>Content-Length</c> or chunked, with the body; <c>declared-only</c> declares the length and sends no body.
    /// </summary>
    private static (byte[] Response, byte[] Body) RawResponse(string framing, int length)
    {
        byte[] body = framing == "declared-only" ? [] : [.. Enumerable.Range(0, length).Select(static index => (byte)(index % 251))];
        var head = framing switch
        {
            "content-length" or "declared-only" => string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"),
            "chunked" => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n",
            _ => throw new ArgumentOutOfRangeException(nameof(framing), framing, "Unknown response framing."),
        };

        using var response = new MemoryStream();
        response.Write(Encoding.ASCII.GetBytes(head));
        if (framing == "chunked")
        {
            for (var offset = 0; offset < body.Length; offset += ChunkBytes)
            {
                var size = Math.Min(ChunkBytes, body.Length - offset);
                response.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{size:x}\r\n")));
                response.Write(body, offset, size);
                response.Write("\r\n"u8);
            }

            response.Write("0\r\n\r\n"u8);
        }
        else
        {
            response.Write(body);
        }

        return (response.ToArray(), body);
    }

    /// <summary>
    /// Accepts one connection, reads the request head, sends <paramref name="response"/> exactly, then waits until
    /// the client closes the connection, so the client alone decides when it stops reading. A client that
    /// closes while the response is still being written ends the write.
    /// </summary>
    private static async Task ServeOnceAsync(TcpListener listener, byte[] response, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var head = new StringBuilder();
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        try
        {
            await stream.WriteAsync(response, ct);
            while (await stream.ReadAsync(buffer, ct) > 0)
            {
                // The client sends nothing more; its close ends the wait.
            }
        }
        catch (IOException)
        {
            // The client closed the connection before the whole response was written: it refused the body.
        }
    }

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
            expected.Select(Redacted).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(Redacted).Order(StringComparer.Ordinal));
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
        Assert.True(string.Equals(result.Result.Detail, failure.Detail, StringComparison.Ordinal), FailureDetailMessage);
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
        Assert.True(string.Equals(result.Result.Detail, failure.Detail, StringComparison.Ordinal), FailureDetailMessage);
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
// recreated. A link at contracts, contracts/openapi or the contract is refused and left in place, and the
// outside file it reaches is untouched; a previous contract is replaced by exactly the served bytes, leaving
// no temporary file, or kept byte for byte when the fetched document is unusable. Every line goes to the mode's
// own writer, the readiness wait's ready or not-ready line included, and a facade admin listener that never
// answers fails the export once the wait expires, writing nothing. The facade admin listener is a loopback
// port nothing listens on, or a stub the test owns.
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

    // Printed once the root is accepted, before the readiness wait of ReadinessLimitSeconds; a refusal never prints it.
    private const string WaitLine = "export-openapi: waiting up to 30 s for the facade admin listener";

    // Stands for the measured wait of the facade's ready line, which differs in every run, in ExportConsoleText;
    // the mode prints no such token.
    private const string ElapsedToken = "<elapsed>";

    // Printed by the readiness wait, unprefixed and on the mode's own writer, once the facade answers /health.
    private const string ReadyLine = "ready: facade (" + ElapsedToken + " ms)";

    // The not-ready test's readiness limit: the closed port refuses the first probe, and the wait expires
    // after this limit with no second probe.
    private const int NotReadyLimitSeconds = 1;

    // Stands for ExportScratch, which differs in every run, in the console text and entries a failure message
    // shows; the mode prints no such token.
    private const string ExportScratchToken = "<export-scratch>";

    // The contract's file name, and its path below the repository root as a refusal of the write names it.
    private const string ContractName = "erewhon-rest.openapi.json";
    private const string TargetBelowRoot = "contracts/openapi/" + ContractName;

    // Well inside the readiness limit, and generous for a loaded host.
    private static readonly TimeSpan RefusalLimit = TimeSpan.FromSeconds(10);

    // The digits of the measured wait in a whole line that is exactly the facade's ready line.
    private static readonly Regex ReadyMilliseconds =
        new(@"(?<=^ready: facade \()[0-9]+(?= ms\)\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // Compact, with irregular spacing and a trailing newline, so a re-serialised write would differ.
    private static readonly byte[] Document =
        Encoding.UTF8.GetBytes("{\"openapi\":\"3.1.1\",  \"info\":{\"version\":\"v1\",\"title\":\"Erewhon REST\"},\"paths\":{}}\n");

    private static readonly byte[] ContractsFile = Encoding.UTF8.GetBytes("a file where the contracts directory belongs");

    // A contract already in place, and a file of the same name outside the repository root that a link reaches;
    // neither is a document the stub serves.
    private static readonly byte[] PreviousContract = Encoding.UTF8.GetBytes("{\"openapi\":\"3.1.1\",\"info\":{\"version\":\"previous\"}}\n");
    private static readonly byte[] OutsideContract = Encoding.UTF8.GetBytes("{\"openapi\":\"3.1.1\",\"info\":{\"version\":\"outside the root\"}}\n");

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

    /// <summary>Which entry on the way to the contract is a symbolic link to a place outside the repository root.</summary>
    public enum LinkedComponent
    {
        /// <summary><c>contracts</c> links to an outside directory that holds <c>openapi/erewhon-rest.openapi.json</c>.</summary>
        Contracts,

        /// <summary><c>contracts/openapi</c> links to an outside directory that holds <c>erewhon-rest.openapi.json</c>.</summary>
        OpenApiDirectory,

        /// <summary><c>contracts/openapi/erewhon-rest.openapi.json</c> links to an outside file.</summary>
        Contract,
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
            ExportRedacted($"{LinePrefix}no contracts directory at {Path.Combine(repoRoot, "contracts")}; {Refusal}{Environment.NewLine}"),
            ExportRedacted(ExportConsole.ToString()));
        Assert.True(stopwatch.Elapsed < RefusalLimit, "The refusal must come before the readiness wait.");
        Assert.Equal(before, Entries());
        if (root == NotRepository.ContractsIsAFile)
        {
            Assert.Equal(ContractsFile, File.ReadAllBytes(Path.Combine(repoRoot, "contracts")));
        }
    }

    // The root holds contracts/, so the mode waits on the facade admin listener, a closed loopback port. The
    // readiness wait's not-ready line, which names the probe URL and the last refusal, reaches the mode's own
    // writer between the announcement and the mode's outcome, and nothing is written.
    [Fact]
    public async Task FacadeAdminNeverReady_PrintsTheNotReadyLineOnTheModeWriter_AndWritesNothing()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, "contracts"));
        var before = Entries();
        var adminUrl = string.Create(CultureInfo.InvariantCulture, $"http://{IPAddress.Loopback}:{ClosedPorts.Take(1).Single()}");
        var options = ExportOptions(repoRoot, adminUrl);
        options.ReadinessTimeoutSeconds = NotReadyLimitSeconds;

        var exitCode = await new ExportOpenApiMode(options, ExportConsole).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var lines = ExportConsoleText().Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.Equal("", lines[3]);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{LinePrefix}waiting up to {NotReadyLimitSeconds} s for the facade admin listener"),
            lines[0]);
        Assert.StartsWith(
            ExportRedacted(string.Create(
                CultureInfo.InvariantCulture,
                $"not ready: facade after {NotReadyLimitSeconds} s (no 200 from {adminUrl}{AdminRoutes.Health} within {NotReadyLimitSeconds} s; "
                + $"last: {nameof(HttpRequestException)} ({HttpRequestError.ConnectionError}): ")),
            lines[1],
            StringComparison.Ordinal);
        Assert.EndsWith(")", lines[1], StringComparison.Ordinal);
        Assert.Equal(
            ExportRedacted($"{LinePrefix}facade admin not ready at {new Uri(adminUrl)} ({FailureItemIds.Ready("facade")} {FailureItemIds.Timeout})"),
            lines[2]);
        Assert.Equal(before, Entries());
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
            ExportRedacted(string.Create(
                CultureInfo.InvariantCulture,
                $"{WaitLine}{Environment.NewLine}{ReadyLine}{Environment.NewLine}{LinePrefix}wrote {Document.Length} bytes to {target}{Environment.NewLine}")),
            ExportConsoleText());
        string[] created = [repoRoot, Path.Combine(repoRoot, "contracts"), Path.GetDirectoryName(target)!, target];
        Assert.Equal(created.Select(ExportRedacted), Entries());
        Assert.Equal(Document, File.ReadAllBytes(target));
        Assert.Equal(AdminRoutes.OpenApiDocument, facade.Requests[^1]);
        Assert.All(facade.Requests.SkipLast(1), request => Assert.Equal(AdminRoutes.Health, request));
    }

    [Fact]
    public async Task ContractsDirectoryRemovedBeforeTheWrite_FailsWithExitOne_AndIsNotRecreated()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        var contracts = Directory.CreateDirectory(Path.Combine(repoRoot, "contracts")).FullName;
        await using var facade = await FacadeAdminStub.StartAsync(
            Document,
            beforeDocument: () => Directory.Delete(contracts, recursive: true),
            TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ExportRedacted(
                $"{WaitLine}{Environment.NewLine}{ReadyLine}{Environment.NewLine}{LinePrefix}failed: {typeof(IOException).FullName}: "
                + $"ConfinedTree could not publish '{TargetBelowRoot}': the directory 'contracts' does not exist, and only the file's "
                + "own directory is ever created."
                + Environment.NewLine),
            ExportConsoleText());
        Assert.Equal(new[] { ExportRedacted(repoRoot) }, Entries());
    }

    // The root holds the link, so the repository-root guard passes and the document is fetched; the write is
    // refused at the link, which stays as it is, and the outside file the link reaches is never touched.
    [Theory]
    [InlineData(
        LinkedComponent.Contracts,
        "ConfinedTree never writes or deletes through a link: refused '" + TargetBelowRoot + "', because 'contracts' is a symbolic link.")]
    [InlineData(
        LinkedComponent.OpenApiDirectory,
        "ConfinedTree never writes or deletes through a link: refused '" + TargetBelowRoot + "', because 'contracts/openapi' is a symbolic link.")]
    [InlineData(
        LinkedComponent.Contract,
        "ConfinedTree could not publish '" + TargetBelowRoot + "': it is not a regular file, so it is left in place.")]
    public async Task LinkOnTheWayToTheContract_IsRefusedWithExitOne_AndTheOutsideFileIsUntouched(LinkedComponent linked, string refusal)
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(ExportScratch, "outside")).FullName;
        var contracts = Path.Combine(repoRoot, "contracts");
        var openApiDirectory = Path.Combine(contracts, "openapi");
        string link;
        string linkTarget;
        string outsideContract;
        switch (linked)
        {
            case LinkedComponent.Contracts:
                outsideContract = Path.Combine(Directory.CreateDirectory(Path.Combine(outside, "openapi")).FullName, ContractName);
                (link, linkTarget) = (contracts, outside);
                Directory.CreateSymbolicLink(link, linkTarget);
                break;
            case LinkedComponent.OpenApiDirectory:
                outsideContract = Path.Combine(outside, ContractName);
                Directory.CreateDirectory(contracts);
                (link, linkTarget) = (openApiDirectory, outside);
                Directory.CreateSymbolicLink(link, linkTarget);
                break;
            case LinkedComponent.Contract:
                outsideContract = Path.Combine(outside, ContractName);
                Directory.CreateDirectory(openApiDirectory);
                (link, linkTarget) = (Path.Combine(openApiDirectory, ContractName), outsideContract);
                File.CreateSymbolicLink(link, linkTarget);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(linked), linked, "not a linked component this test builds");
        }

        File.WriteAllBytes(outsideContract, OutsideContract);
        var before = Entries();
        await using var facade = await FacadeAdminStub.StartAsync(Document, beforeDocument: null, TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ExportRedacted(
                $"{WaitLine}{Environment.NewLine}{ReadyLine}{Environment.NewLine}{LinePrefix}failed: {typeof(IOException).FullName}: "
                + $"{refusal}{Environment.NewLine}"),
            ExportConsoleText());
        Assert.Equal(AdminRoutes.OpenApiDocument, facade.Requests[^1]);
        Assert.Equal(OutsideContract, File.ReadAllBytes(outsideContract));
        Assert.Equal(before, Entries());
        Assert.Equal(linkTarget, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task PreviousContract_IsReplacedByExactlyTheServedBytes_AndNoTemporaryFileRemains()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        var openApiDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, "contracts", "openapi")).FullName;
        var target = Path.Combine(openApiDirectory, ContractName);
        File.WriteAllBytes(target, PreviousContract);
        await using var facade = await FacadeAdminStub.StartAsync(Document, beforeDocument: null, TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ExportRedacted(string.Create(
                CultureInfo.InvariantCulture,
                $"{WaitLine}{Environment.NewLine}{ReadyLine}{Environment.NewLine}{LinePrefix}wrote {Document.Length} bytes to {target}{Environment.NewLine}")),
            ExportConsoleText());
        Assert.Equal(Document, File.ReadAllBytes(target));
        string[] entries = [repoRoot, Path.Combine(repoRoot, "contracts"), openApiDirectory, target];
        Assert.Equal(entries.Select(ExportRedacted), Entries());
    }

    [Fact]
    public async Task PreviousContract_IsKeptByteForByte_WhenTheFetchedDocumentIsNotAJsonObject()
    {
        var repoRoot = Directory.CreateDirectory(Path.Combine(ExportScratch, "root")).FullName;
        var openApiDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, "contracts", "openapi")).FullName;
        var target = Path.Combine(openApiDirectory, ContractName);
        File.WriteAllBytes(target, PreviousContract);
        await using var facade = await FacadeAdminStub.StartAsync("[]"u8.ToArray(), beforeDocument: null, TestContext.Current.CancellationToken);

        var exitCode = await new ExportOpenApiMode(ExportOptions(repoRoot, facade.BaseUrl), ExportConsole)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ExportRedacted(
                $"{WaitLine}{Environment.NewLine}{ReadyLine}{Environment.NewLine}{LinePrefix}{facade.BaseUrl}{AdminRoutes.OpenApiDocument} "
                + $"did not return a JSON object (root is Array). Nothing written.{Environment.NewLine}"),
            ExportConsoleText());
        Assert.Equal(PreviousContract, File.ReadAllBytes(target));
        string[] entries = [repoRoot, Path.Combine(repoRoot, "contracts"), openApiDirectory, target];
        Assert.Equal(entries.Select(ExportRedacted), Entries());
    }

    private static HarnessOptions ExportOptions(string repoRoot, string adminUrl) => new()
    {
        RepoRoot = repoRoot,
        Target = new HarnessEndpoints { AdminUrl = adminUrl },
        ReadinessTimeoutSeconds = ReadinessLimitSeconds,
        ReadinessPollIntervalSeconds = 1,
        RequestTimeoutSeconds = RequestLimitSeconds,
    };

    /// <summary>
    /// Every file and directory under the export-openapi test's directory, redacted by <see cref="ExportRedacted"/>,
    /// in ordinal order.
    /// </summary>
    private string[] Entries() =>
        [.. Directory.EnumerateFileSystemEntries(ExportScratch, "*", SearchOption.AllDirectories).Select(ExportRedacted).Order(StringComparer.Ordinal)];

    /// <summary>
    /// <paramref name="text"/> with <see cref="ExportScratch"/> replaced by <see cref="ExportScratchToken"/> and every
    /// loopback port by <see cref="PortToken"/>, so a failure message reads the same in every run. Both sides of a
    /// comparison go through it, and neither the expected lines nor the paths below the directory hold a loopback
    /// URL, so it holds exactly when the raw comparison does.
    /// </summary>
    private string ExportRedacted(string text) =>
        LoopbackPort.Replace(text.Replace(ExportScratch, ExportScratchToken, StringComparison.Ordinal), PortToken);

    /// <summary>
    /// The text the mode printed to <see cref="ExportConsole"/>, through <see cref="ExportRedacted"/>, with the
    /// measured wait of a line that is exactly the facade's ready line shown as <see cref="ElapsedToken"/>, so an
    /// expectation holds <see cref="ReadyLine"/> where the console holds that line. Only those digits change.
    /// </summary>
    private string ExportConsoleText() => ReadyMilliseconds.Replace(ExportRedacted(ExportConsole.ToString()), ElapsedToken);

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
            var builder = LoopbackHost.CreateBuilder(ShutdownLimit);

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
                return new FacadeAdminStub(app, requests, LoopbackHost.BoundAddress(app));
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
