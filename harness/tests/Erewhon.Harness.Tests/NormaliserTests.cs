using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The two normalisation rules (N1 generated identifiers, N2 timestamps) as derived from the contracts
/// catalogue, their validity forms in each wire syntax, and the pairwise rule that decides equality at a
/// targeted position. Every expected class and property name below is checked against the catalogue-derived
/// rules, never used to build them.
/// </summary>
public sealed class NormaliserTests
{
    private const string N1 = NormalisationRuleIds.N1;
    private const string N2 = NormalisationRuleIds.N2;

    private const string ValidGuidA = "{00000000-0000-4000-8000-000000000001}";
    private const string ValidGuidB = "{eaf359ee-e216-ee11-9fbd-0050568d0023}";
    private const string ValidGuidUpperCase = "{EAF359EE-E216-EE11-9FBD-0050568D0023}";
    private const string UnbracedGuid = "00000000-0000-4000-8000-000000000001";
    private const string NonHexGuid = "{00000000-0000-4000-8000-00000000000g}";

    // A JSON slot holds the DECODED string value: the body token "\/Date(1767268800000)\/" reaches the
    // normaliser as /Date(1767268800000)/, so text that still carries the backslashes is invalid.
    private const string ValidJsonDateA = "/Date(1767268800000)/";
    private const string ValidJsonDateB = "/Date(1767268801000)/";
    private const string ValidJsonDateOffset = "/Date(1767268800000+1300)/";
    private const string UndecodedJsonDateToken = @"\/Date(1767268800000)\/";

    private const string ValidXsdDateTimeA = "2026-01-01T12:00:00Z";
    private const string ValidXsdDateTimeB = "2026-01-01T12:00:01Z";
    private const string ValidXsdDateTimeFraction = "2026-01-01T12:00:00.000Z";
    private const string XsdDate = "2026-01-01";

    // The rule names; a body holding either literally holds an ordinary invalid value.
    private const string GuidLookalike = "{generated-guid}";
    private const string TimestampLookalike = "{timestamp}";

    private const string PaddedGuid = " " + ValidGuidA;
    private const string PaddedJsonDate = ValidJsonDateA + " ";
    private const string PaddedXsdDateTime = ValidXsdDateTimeA + "\n";

    // The positions AAP 0.7.5 names: 11 generated guids and 4 TimeStamp properties. The catalogue
    // cross-check requires at least these, so an empty catalogue cannot pass it vacuously.
    private const int MinimumN1Targets = 11;
    private const int MinimumN2Targets = 4;

    private const int ValidSampleCount = 3;

    private readonly NormalisationRules _rules;
    private readonly Normaliser _normaliser;

    public NormaliserTests()
    {
        _rules = NormalisationRules.FromCatalogue(OperationCatalogue.Default);
        _normaliser = new Normaliser(_rules);
    }

    /// <summary>Every rule paired with every wire syntax.</summary>
    public static TheoryData<string, WireSyntax> RulesAndSyntaxes { get; } = new()
    {
        { N1, WireSyntax.Json },
        { N1, WireSyntax.Xml },
        { N2, WireSyntax.Json },
        { N2, WireSyntax.Xml },
    };

    /// <summary>N1 has one valid form, the braced JADE GUID, in both syntaxes.</summary>
    public static TheoryData<WireSyntax, string?, bool> N1Validity()
    {
        var data = new TheoryData<WireSyntax, string?, bool>();
        foreach (var syntax in Enum.GetValues<WireSyntax>())
        {
            data.Add(syntax, ValidGuidA, true);
            data.Add(syntax, ValidGuidB, true);
            data.Add(syntax, ValidGuidUpperCase, true);
            data.Add(syntax, GuidLookalike, false);
            data.Add(syntax, TimestampLookalike, false);
            data.Add(syntax, UnbracedGuid, false);
            data.Add(syntax, NonHexGuid, false);
            data.Add(syntax, PaddedGuid, false);
            data.Add(syntax, "", false);
            data.Add(syntax, null, false);
        }

        return data;
    }

    // ---- Rule inventory -------------------------------------------------------------------------

    [Fact]
    public void FromCatalogue_HasExactlyTwoRules()
    {
        Assert.Equal(new[] { N1, N2 }, _rules.Rules.Select(rule => rule.Id));
        Assert.Equal(new[] { GuidLookalike, TimestampLookalike }, _rules.Rules.Select(rule => rule.Name));
    }

    [Theory]
    [InlineData("DTOAgent")]
    [InlineData("DTOClient")]
    [InlineData("DTOCompany")]
    [InlineData("DTOSale")]
    [InlineData("DTORetailSale")]
    [InlineData("DTOTenderSale")]
    [InlineData("DTOSaleDetail")]
    [InlineData("DTORetailSaleDetail")]
    [InlineData("DTOTenderSaleDetail")]
    [InlineData("DTOTender")]
    [InlineData("DTOResult")]
    public void N1_TargetsGeneratedGuids(string dtoClass)
    {
        Assert.Equal(N1, _normaliser.RuleFor(dtoClass, "guid"));
    }

    // Item guids come from the seed files, so they are compared exactly.
    [Theory]
    [InlineData("DTOItem")]
    [InlineData("DTORetailItem")]
    [InlineData("DTOTenderItem")]
    public void N1_DoesNotTargetSeedItemGuids(string dtoClass)
    {
        Assert.Null(_normaliser.RuleFor(dtoClass, "guid"));
    }

    [Theory]
    [InlineData("DTORetailSale", "timestamp")]
    [InlineData("DTOTender", "timestamp")]
    [InlineData("DTORetailSaleDetail", "soldDate")]
    [InlineData("DTOTenderSaleDetail", "tenderDate")]
    public void N2_TargetsTimeStamps(string dtoClass, string property)
    {
        Assert.Equal(N2, _normaliser.RuleFor(dtoClass, property));
    }

    // Date-typed values are reproducible because the stand-in pins its load date, so they are compared exactly.
    [Theory]
    [InlineData("DTOItem", "listedDate")]
    [InlineData("DTORetailItem", "listedDate")]
    [InlineData("DTOTenderItem", "listedDate")]
    [InlineData("DTOTenderItem", "closureDate")]
    public void N2_DoesNotTargetDates(string dtoClass, string property)
    {
        Assert.Null(_normaliser.RuleFor(dtoClass, property));
    }

    // Class and property must match a target exactly and case-sensitively, as JADE names do; a missing
    // class (no declared or runtime type at the position) never selects a rule.
    [Theory]
    [InlineData("DTOAgent", "name")]
    [InlineData("DTOResult", "statusCode")]
    [InlineData("DTOItem", "price")]
    [InlineData("DTOAgent", "Guid")]
    [InlineData("dtoagent", "guid")]
    [InlineData("DTOUnknown", "guid")]
    [InlineData("", "guid")]
    [InlineData(null, "guid")]
    public void NonTargetedProperties_HaveNoRule(string? dtoClass, string property)
    {
        Assert.Null(_normaliser.RuleFor(dtoClass, property));
    }

    [Fact]
    public void RuleFor_FollowsTheCatalogueNormalisationOfEveryProperty()
    {
        var mismatches = new List<string>();
        var normalisedCounts = new Dictionary<string, int>(StringComparer.Ordinal) { [N1] = 0, [N2] = 0 };

        foreach (var dto in OperationCatalogue.Default.Dtos)
        {
            foreach (var property in dto.Properties)
            {
                var rule = _normaliser.RuleFor(dto.Name, property.Name);
                if (!string.Equals(rule, property.Normalisation, StringComparison.Ordinal))
                {
                    mismatches.Add(
                        $"{dto.Name}.{property.Name}: catalogue {property.Normalisation ?? "(none)"}, normaliser {rule ?? "(none)"}");
                }

                if (property.Normalisation is { } normalisation)
                {
                    normalisedCounts[normalisation]++;
                }
            }
        }

        Assert.Empty(mismatches);
        Assert.True(normalisedCounts[N1] >= MinimumN1Targets, "The catalogue marks fewer N1 positions than the 11 generated guids.");
        Assert.True(normalisedCounts[N2] >= MinimumN2Targets, "The catalogue marks fewer N2 positions than the 4 TimeStamp properties.");
    }

    [Fact]
    public void EveryRuleTarget_IsACatalogueProperty_MarkedWithThatRule()
    {
        var strays = new List<string>();
        foreach (var rule in _rules.Rules)
        {
            foreach (var target in rule.Targets)
            {
                if (!OperationCatalogue.Default.TryGetDto(target.Class, out var dto)
                    || !dto.TryGetProperty(target.Property, out var property)
                    || !string.Equals(property.Normalisation, rule.Id, StringComparison.Ordinal))
                {
                    strays.Add($"{rule.Id}: {target.Class}.{target.Property}");
                }
            }
        }

        Assert.Empty(strays);
    }

    // Guards the shape of baseline/normalisation-rules.json, which capture writes and replay reads back.
    [Fact]
    public void RulesRoundTrip()
    {
        var document = _rules.ToJsonNode().ToJsonString();
        var parsed = NormalisationRules.Parse(document);

        Assert.Equal(_rules.Rules.Select(rule => rule.Id), parsed.Rules.Select(rule => rule.Id));
        for (var index = 0; index < _rules.Rules.Count; index++)
        {
            Assert.Equal(_rules.Rules[index].Name, parsed.Rules[index].Name);
            Assert.Equal(_rules.Rules[index].Description, parsed.Rules[index].Description);
            Assert.Equal(_rules.Rules[index].Targets, parsed.Rules[index].Targets);
        }

        Assert.Equal(document, parsed.ToJsonNode().ToJsonString());
    }

    // ---- Validity forms -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(N1Validity))]
    public void N1_ValidForm(WireSyntax syntax, string? text, bool expected)
    {
        Assert.Equal(expected, _normaliser.IsValid(N1, text, syntax));
    }

    [Theory]
    [InlineData(ValidJsonDateA, true)]
    [InlineData(ValidJsonDateB, true)]
    [InlineData(ValidJsonDateOffset, true)]
    [InlineData(UndecodedJsonDateToken, false)]
    [InlineData(TimestampLookalike, false)]
    [InlineData(ValidXsdDateTimeA, false)]
    [InlineData(PaddedJsonDate, false)]
    [InlineData(ValidGuidA, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void N2_ValidJson(string? text, bool expected)
    {
        Assert.Equal(expected, _normaliser.IsValid(N2, text, WireSyntax.Json));
    }

    [Theory]
    [InlineData(ValidXsdDateTimeA, true)]
    [InlineData(ValidXsdDateTimeB, true)]
    [InlineData(ValidXsdDateTimeFraction, true)]
    [InlineData(TimestampLookalike, false)]
    [InlineData(XsdDate, false)]
    [InlineData(ValidJsonDateA, false)]
    [InlineData(PaddedXsdDateTime, false)]
    [InlineData(ValidGuidA, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void N2_ValidXml(string? text, bool expected)
    {
        Assert.Equal(expected, _normaliser.IsValid(N2, text, WireSyntax.Xml));
    }

    [Fact]
    public void UnknownRulesAndSyntaxes_AreRejected()
    {
        var slot = NormalisedSlot.Of(ValidGuidA);
        const WireSyntax undefinedSyntax = (WireSyntax)99;

        Assert.Throws<ArgumentException>(() => _normaliser.IsValid("N3", ValidGuidA, WireSyntax.Json));
        Assert.Throws<ArgumentException>(() => _normaliser.PairEquals("n1", slot, slot, WireSyntax.Json));
        Assert.Throws<ArgumentOutOfRangeException>(() => _normaliser.IsValid(N1, ValidGuidA, undefinedSyntax));
        Assert.Throws<ArgumentOutOfRangeException>(() => _normaliser.PairEquals(N1, slot, slot, undefinedSyntax));
        Assert.Throws<ArgumentNullException>(() => _normaliser.PairEquals(N1, null!, slot, WireSyntax.Json));
        Assert.Throws<ArgumentNullException>(() => _normaliser.RuleFor("DTOAgent", null!));
    }

    // A slot that is absent yet null, holds text while absent or null, or is a present non-null value without
    // text would let the pairwise rule compare states that cannot occur in a body.
    [Theory]
    [InlineData(false, true, null)]
    [InlineData(false, false, "text")]
    [InlineData(true, true, "text")]
    [InlineData(true, false, null)]
    public void NormalisedSlot_RejectsInconsistentState(bool present, bool isNull, string? text)
    {
        Assert.Throws<ArgumentException>(() => new NormalisedSlot(present, isNull, text));
    }

    [Fact]
    public void NormalisedSlot_Factories_HoldTheirState()
    {
        Assert.Equal(new NormalisedSlot(false, false, null), NormalisedSlot.Absent);
        Assert.Equal(new NormalisedSlot(true, true, null), NormalisedSlot.Null);
        Assert.Equal(new NormalisedSlot(true, false, PaddedGuid), NormalisedSlot.Of(PaddedGuid));
        Assert.Throws<ArgumentNullException>(() => NormalisedSlot.Of(null!));
    }

    // ---- Pairwise rule --------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void TwoValidValues_AreEqual_WhateverTheirContent(string rule, WireSyntax syntax)
    {
        for (var left = 0; left < ValidSampleCount; left++)
        {
            for (var right = 0; right < ValidSampleCount; right++)
            {
                Assert.True(
                    _normaliser.PairEquals(rule, Slot(ValidSample(rule, syntax, left)), Slot(ValidSample(rule, syntax, right)), syntax),
                    "Two valid values must compare equal whatever their content.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromAbsent(string rule, WireSyntax syntax)
    {
        AssertUnequalBothWays(rule, syntax, NormalisedSlot.Absent);
    }

    // In XML the null slot stands for an element carrying xsi:nil="true".
    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromNull(string rule, WireSyntax syntax)
    {
        AssertUnequalBothWays(rule, syntax, NormalisedSlot.Null);
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromEmptyText(string rule, WireSyntax syntax)
    {
        AssertUnequalBothWays(rule, syntax, Slot(""));
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromWronglyTypedText(string rule, WireSyntax syntax)
    {
        foreach (var text in WronglyTypedTexts(rule, syntax))
        {
            AssertUnequalBothWays(rule, syntax, Slot(text));
        }
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromLiteralLookalikes(string rule, WireSyntax syntax)
    {
        AssertUnequalBothWays(rule, syntax, Slot(GuidLookalike));
        AssertUnequalBothWays(rule, syntax, Slot(TimestampLookalike));
    }

    // Nothing is trimmed: a valid value with a blank or newline added is no longer valid.
    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ValidValue_DiffersFromItsPaddedCopy(string rule, WireSyntax syntax)
    {
        var valid = ValidSample(rule, syntax, 0);
        AssertUnequalBothWays(rule, syntax, Slot(" " + valid));
        AssertUnequalBothWays(rule, syntax, Slot(valid + " "));
        AssertUnequalBothWays(rule, syntax, Slot(valid + "\n"));
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void IdenticalInvalidValues_AreEqual(string rule, WireSyntax syntax)
    {
        NormalisedSlot[] slots =
        [
            NormalisedSlot.Absent,
            NormalisedSlot.Null,
            Slot(GuidLookalike),
            Slot(TimestampLookalike),
            Slot(""),
            Slot("42"),
        ];

        foreach (var slot in slots)
        {
            Assert.True(
                _normaliser.PairEquals(rule, slot, slot, syntax),
                "Two invalid values that are exactly equal must compare equal.");
            Assert.True(
                _normaliser.PairEquals(rule, slot, CopyOf(slot), syntax),
                "Equality of invalid values must not depend on instance identity.");
        }
    }

    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void DifferentInvalidValues_AreUnequal(string rule, WireSyntax syntax)
    {
        (NormalisedSlot Left, NormalisedSlot Right)[] pairs =
        [
            (Slot("abc"), Slot("abd")),
            (Slot("abc"), Slot("ABC")),
            (NormalisedSlot.Absent, NormalisedSlot.Null),
            (Slot(""), NormalisedSlot.Absent),
            (Slot(""), NormalisedSlot.Null),
            (Slot(""), Slot(" ")),
            (Slot(GuidLookalike), Slot(TimestampLookalike)),
        ];

        foreach (var (left, right) in pairs)
        {
            Assert.False(_normaliser.PairEquals(rule, left, right, syntax), "Different invalid values must compare unequal.");
            Assert.False(_normaliser.PairEquals(rule, right, left, syntax), "Different invalid values must compare unequal.");
        }
    }

    // ---- Discrepancy values ---------------------------------------------------------------------

    // A valid value enters a failure signature as the rule token only, so two different valid values give
    // one token, and no literal, a lookalike included, can give that token.
    [Theory]
    [MemberData(nameof(RulesAndSyntaxes))]
    public void ToValue_KeepsValidContentOutOfSignatures(string rule, WireSyntax syntax)
    {
        var validToken = DiscrepancyValue.Valid(rule).ToToken().ToJsonString();
        for (var variant = 0; variant < ValidSampleCount; variant++)
        {
            Assert.Equal(DiscrepancyValue.Valid(rule), _normaliser.ToValue(rule, Slot(ValidSample(rule, syntax, variant)), syntax));
        }

        foreach (var slot in new[] { NormalisedSlot.Null, Slot(""), Slot(GuidLookalike), Slot(TimestampLookalike) })
        {
            var value = _normaliser.ToValue(rule, slot, syntax);
            Assert.False(value.IsValid, "An invalid value must enter a signature as a literal.");
            Assert.NotEqual(validToken, value.ToToken().ToJsonString());
        }

        Assert.Equal(DiscrepancyValue.Absent, _normaliser.ToValue(rule, NormalisedSlot.Absent, syntax));
    }

    [Theory]
    [InlineData(WireSyntax.Json, "\"{generated-guid}\"", "null")]
    [InlineData(WireSyntax.Xml, "{generated-guid}", "xsi:nil=\"true\"")]
    public void ToValue_WritesLiteralsInTheSyntaxConvention(WireSyntax syntax, string lookalikeLiteral, string nullLiteral)
    {
        Assert.Equal(DiscrepancyValue.Literal(lookalikeLiteral), _normaliser.ToValue(N1, Slot(GuidLookalike), syntax));
        Assert.Equal(DiscrepancyValue.Literal(nullLiteral), _normaliser.ToValue(N1, NormalisedSlot.Null, syntax));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void AssertUnequalBothWays(string rule, WireSyntax syntax, NormalisedSlot other)
    {
        for (var variant = 0; variant < ValidSampleCount; variant++)
        {
            var valid = Slot(ValidSample(rule, syntax, variant));
            Assert.False(
                _normaliser.PairEquals(rule, valid, other, syntax),
                "A valid baseline value must differ from an invalid observed one.");
            Assert.False(
                _normaliser.PairEquals(rule, other, valid, syntax),
                "An invalid baseline value must differ from a valid observed one.");
        }
    }

    /// <summary>Returns one of <see cref="ValidSampleCount"/> distinct values valid for the rule in the syntax.</summary>
    private static string ValidSample(string rule, WireSyntax syntax, int variant)
    {
        string[] samples = (rule, syntax) switch
        {
            (N1, _) => [ValidGuidA, ValidGuidB, ValidGuidUpperCase],
            (N2, WireSyntax.Json) => [ValidJsonDateA, ValidJsonDateB, ValidJsonDateOffset],
            (N2, WireSyntax.Xml) => [ValidXsdDateTimeA, ValidXsdDateTimeB, ValidXsdDateTimeFraction],
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No samples for this rule and syntax."),
        };

        return samples[variant];
    }

    /// <summary>
    /// Text that is a well-formed value of some other type: a number, the other rule's valid form, and for N1
    /// an unbraced GUID or for N2 the valid form of the other syntax.
    /// </summary>
    private static IEnumerable<string> WronglyTypedTexts(string rule, WireSyntax syntax)
    {
        yield return "42";
        if (string.Equals(rule, N1, StringComparison.Ordinal))
        {
            yield return ValidSample(N2, syntax, 0);
            yield return UnbracedGuid;
        }
        else
        {
            yield return ValidSample(N1, syntax, 0);
            yield return ValidSample(N2, syntax == WireSyntax.Json ? WireSyntax.Xml : WireSyntax.Json, 0);
        }
    }

    private static NormalisedSlot Slot(string text) => NormalisedSlot.Of(text);

    // A distinct string instance, so equality cannot pass on reference identity alone.
    private static NormalisedSlot CopyOf(NormalisedSlot slot) =>
        new(slot.Present, slot.IsNull, slot.Text is null ? null : new string(slot.Text.AsSpan()));
}
