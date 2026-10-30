using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The two normalisation rules (N1 generated identifiers, N2 timestamps) as derived from the contracts
/// catalogue, their validity forms in each wire syntax, the pairwise rule that decides equality at a
/// targeted position, and the strict parse and load of the rules document
/// (<c>baseline/normalisation-rules.json</c>). Every expected class and property name below is checked
/// against the catalogue-derived rules, never used to build them. It also covers the stand-in
/// conformance verdicts, which decide whether a captured member holds a non-null value of its declared
/// JADE wire type.
/// </summary>
public sealed partial class NormaliserTests : IDisposable
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

    // The fixed parts of every Parse diagnostic.
    private const string InvalidPrefix = "Invalid normalisation rules: ";
    private const string NotJsonPrefix = "The normalisation rules are not valid JSON: ";

    private const string RulesFileName = "normalisation-rules.json";

    /// <summary>
    /// Edits of a valid rules document that Parse must reject, keyed by the name each theory invocation
    /// shows, with the diagnostic the message must start with. Rule 0 is N1 and rule 1 is N2.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RejectedDocument> RejectedDocuments =
        new Dictionary<string, RejectedDocument>(StringComparer.Ordinal)
        {
            // Syntax: the same strictness as the catalogue loader.
            ["empty text"] = Rewritten(_ => "", NotJsonPrefix + "The input does not contain any JSON tokens."),
            ["truncated JSON"] = Rewritten(text => text[..^1], NotJsonPrefix + "Expected depth to be zero at the end of the JSON payload."),
            ["trailing comma"] = Rewritten(text => text[..^1] + ",}", NotJsonPrefix + "The JSON object contains a trailing comma"),
            ["comment"] = Rewritten(
                text => text.Replace(",\"rules\":", ",/* comment */\"rules\":", StringComparison.Ordinal),
                NotJsonPrefix + "'/' is an invalid start of a property name."),
            ["duplicate root key (version twice)"] = Rewritten(
                text => "{\"version\":1," + text[1..],
                NotJsonPrefix + "Duplicate property 'version'"),
            ["duplicate rule key (id twice)"] = Rewritten(
                text => text.Replace("\"id\":\"N1\"", "\"id\":\"N1\",\"id\":\"N1\"", StringComparison.Ordinal),
                NotJsonPrefix + "Duplicate property 'id'"),
            ["root not an object"] = Rewritten(text => "[" + text + "]", InvalidPrefix + "$ must be an object, but is Array."),

            // Root members.
            ["unknown root member"] = Edited(
                document => document["comment"] = "added",
                InvalidPrefix + "$ has unknown member 'comment'."),
            ["version 2"] = Edited(
                document => document["version"] = 2,
                InvalidPrefix + "$.version must be the integer 1, but is 2."),
            ["version as string \"1\""] = Edited(
                document => document["version"] = "1",
                InvalidPrefix + "$.version must be the integer 1, but is \"1\"."),
            ["version 1.5"] = Edited(
                document => document["version"] = 1.5,
                InvalidPrefix + "$.version must be the integer 1, but is 1.5."),
            ["version missing"] = Edited(
                document => document.Remove("version"),
                InvalidPrefix + "$ has no member 'version'."),
            ["rules missing"] = Edited(
                document => document.Remove("rules"),
                InvalidPrefix + "$ has no member 'rules'."),
            ["rules not an array"] = Edited(
                document => document["rules"] = new JsonObject(),
                InvalidPrefix + "$.rules must be an array of exactly 2 rules, 'N1' then 'N2'."),

            // Rule count, ids and order: exactly N1 then N2.
            ["one rule (N2 removed)"] = Edited(
                document => RulesOf(document).RemoveAt(1),
                InvalidPrefix + "$.rules must be an array of exactly 2 rules, 'N1' then 'N2'."),
            ["three rules (additional id N3)"] = Edited(
                document =>
                {
                    var extra = RuleOf(document, 1).DeepClone().AsObject();
                    extra["id"] = "N3";
                    RulesOf(document).Add(extra);
                },
                InvalidPrefix + "$.rules must be an array of exactly 2 rules, 'N1' then 'N2'."),
            ["ids misordered (N2 then N1)"] = Edited(
                document =>
                {
                    var rules = RulesOf(document);
                    var first = rules[0];
                    rules.RemoveAt(0);
                    rules.Add(first);
                },
                InvalidPrefix + "$.rules[0].id must be 'N1', but is 'N2'."),
            ["id changed (N2 to N3)"] = Edited(
                document => RuleOf(document, 1)["id"] = "N3",
                InvalidPrefix + "$.rules[1].id must be 'N2', but is 'N3'."),
            ["id changed case (N1 to n1)"] = Edited(
                document => RuleOf(document, 0)["id"] = "n1",
                InvalidPrefix + "$.rules[0].id must be 'N1', but is 'n1'."),
            ["id not a string"] = Edited(
                document => RuleOf(document, 0)["id"] = 1,
                InvalidPrefix + "$.rules[0].id must be a string, but is Number."),
            ["rule not an object"] = Edited(
                document => RulesOf(document)[1] = N2,
                InvalidPrefix + "$.rules[1] must be an object, but is String."),

            // Rule members.
            ["unknown rule member"] = Edited(
                document => RuleOf(document, 1)["enabled"] = true,
                InvalidPrefix + "$.rules[1] has unknown member 'enabled'."),
            ["rule id missing"] = Edited(
                document => RuleOf(document, 0).Remove("id"),
                InvalidPrefix + "$.rules[0] has no member 'id'."),
            ["description missing"] = Edited(
                document => RuleOf(document, 0).Remove("description"),
                InvalidPrefix + "$.rules[0] has no member 'description'."),
            ["description not a string"] = Edited(
                document => RuleOf(document, 1)["description"] = null,
                InvalidPrefix + "$.rules[1].description must be a string, but is Null."),
            ["N1 name changed"] = Edited(
                document => RuleOf(document, 0)["name"] = "{guid}",
                InvalidPrefix + "$.rules[0].name must be '{generated-guid}', but is '{guid}'."),
            ["names swapped"] = Edited(
                document =>
                {
                    RuleOf(document, 0)["name"] = TimestampLookalike;
                    RuleOf(document, 1)["name"] = GuidLookalike;
                },
                InvalidPrefix + "$.rules[0].name must be '{generated-guid}', but is '{timestamp}'."),

            // Valid forms: fixed per rule and syntax.
            ["validForm json changed"] = Edited(
                document => ValidFormOf(document, 0)["json"] = "guid",
                InvalidPrefix + "$.rules[0].validForm.json must be 'jade-guid', but is 'guid'."),
            ["validForm xml changed"] = Edited(
                document => ValidFormOf(document, 1)["xml"] = "xsd:date",
                InvalidPrefix + "$.rules[1].validForm.xml must be 'xsd:dateTime', but is 'xsd:date'."),
            ["validForm unknown member"] = Edited(
                document => ValidFormOf(document, 0)["yaml"] = "jade-guid",
                InvalidPrefix + "$.rules[0].validForm has unknown member 'yaml'."),
            ["validForm missing member"] = Edited(
                document => ValidFormOf(document, 1).Remove("json"),
                InvalidPrefix + "$.rules[1].validForm has no member 'json'."),
            ["validForm not an object"] = Edited(
                document => RuleOf(document, 0)["validForm"] = "jade-guid",
                InvalidPrefix + "$.rules[0].validForm must be an object, but is String."),

            // Targets.
            ["targets not an array"] = Edited(
                document => RuleOf(document, 0)["targets"] = new JsonObject(),
                InvalidPrefix + "$.rules[0].targets must be an array, but is Object."),
            ["target not an object"] = Edited(
                document => TargetsOf(document, 0)[0] = "DTOAgent.guid",
                InvalidPrefix + "$.rules[0].targets[0] must be an object, but is String."),
            ["target class empty"] = Edited(
                document => TargetOf(document, 0, 0)["class"] = "",
                InvalidPrefix + "$.rules[0].targets[0].class must not be empty."),
            ["target property empty"] = Edited(
                document => TargetOf(document, 1, 0)["property"] = "",
                InvalidPrefix + "$.rules[1].targets[0].property must not be empty."),
            ["target class not a string"] = Edited(
                document => TargetOf(document, 0, 2)["class"] = 42,
                InvalidPrefix + "$.rules[0].targets[2].class must be a string, but is Number."),
            ["target property missing"] = Edited(
                document => TargetOf(document, 0, 0).Remove("property"),
                InvalidPrefix + "$.rules[0].targets[0] has no member 'property'."),
            ["target unknown member"] = Edited(
                document => TargetOf(document, 1, 1)["rule"] = N2,
                InvalidPrefix + "$.rules[1].targets[1] has unknown member 'rule'."),
            ["target repeated within a rule"] = Edited(
                document => TargetsOf(document, 0).Insert(1, TargetOf(document, 0, 0).DeepClone()),
                InvalidPrefix + "$.rules[0].targets[1] repeats target "),
            ["same position under N1 and N2"] = Edited(
                document => TargetsOf(document, 1).Add(new JsonObject { ["class"] = "DTOResult", ["property"] = "guid" }),
                InvalidPrefix + "DTOResult.guid is targeted by both 'N1' and 'N2'."),
        };

    private readonly NormalisationRules _rules;
    private readonly Normaliser _normaliser;

    // Created on first use, so only the file tests touch the file system; removed in Dispose.
    private string? _temporaryDirectory;

    public NormaliserTests()
    {
        _rules = NormalisationRules.FromCatalogue(OperationCatalogue.Default);
        _normaliser = new Normaliser(_rules);
    }

    /// <summary>An isolated directory for this test instance's files.</summary>
    private string TemporaryDirectory => _temporaryDirectory ??=
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    public void Dispose()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
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

    // ---- Rules document -------------------------------------------------------------------------

    /// <summary>The name of every rejected document, so each edit is its own theory invocation.</summary>
    public static TheoryData<string> RejectedDocumentNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RejectedDocuments.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    // Equal rules must always give the same bytes, since capture writes the document that replay compares.
    [Fact]
    public void Generation_IsDeterministic()
    {
        var first = NormalisationRules.FromCatalogue(OperationCatalogue.Default);
        var second = NormalisationRules.FromCatalogue(OperationCatalogue.Default);

        Assert.Equal(first.ToJsonNode().ToJsonString(), second.ToJsonNode().ToJsonString());
        Assert.Equal(_rules.ToJsonNode().ToJsonString(), _rules.ToJsonNode().ToJsonString());
        Assert.NotSame(_rules.ToJsonNode(), _rules.ToJsonNode());
    }

    [Fact]
    public void Generation_SortsTargetsOrdinally()
    {
        foreach (var rule in _rules.Rules)
        {
            var sorted = rule.Targets
                .OrderBy(target => target.Class, StringComparer.Ordinal)
                .ThenBy(target => target.Property, StringComparer.Ordinal);
            Assert.Equal(sorted, rule.Targets);
        }
    }

    // The ids must be exactly N1 then N2, so a changed, misordered or additional id is rejected, as is
    // every other departure from the fixed two-rule shape, with the offending path in the message.
    [Theory]
    [MemberData(nameof(RejectedDocumentNames))]
    public void Parse_RejectsDocument(string name)
    {
        var rejected = RejectedDocuments[name];
        var document = rejected.Write(_rules.ToJsonNode().AsObject());

        var exception = Assert.Throws<InvalidDataException>(() => NormalisationRules.Parse(document));
        Assert.StartsWith(rejected.ExpectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAndLoad_RejectNullOrEmptyArguments()
    {
        Assert.Throws<ArgumentNullException>(() => NormalisationRules.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => NormalisationRules.Load(null!));
        Assert.Throws<ArgumentException>(() => NormalisationRules.Load(""));
    }

    [Fact]
    public void Parse_KeepsDescriptionsAsStored()
    {
        var document = _rules.ToJsonNode().AsObject();
        RuleOf(document, 0)["description"] = "Edited N1 description.";
        RuleOf(document, 1)["description"] = "";

        var parsed = NormalisationRules.Parse(document.ToJsonString());

        Assert.Equal("Edited N1 description.", parsed.Rules[0].Description);
        Assert.Equal("", parsed.Rules[1].Description);
        Assert.Equal(document.ToJsonString(), parsed.ToJsonNode().ToJsonString());
    }

    [Fact]
    public void Parse_ResortsTargets()
    {
        var document = _rules.ToJsonNode().AsObject();
        for (var rule = 0; rule < _rules.Rules.Count; rule++)
        {
            var targets = TargetsOf(document, rule);
            var reversed = targets.Select(target => target!.DeepClone()).Reverse().ToList();
            targets.Clear();
            foreach (var target in reversed)
            {
                targets.Add(target);
            }
        }

        var parsed = NormalisationRules.Parse(document.ToJsonString());

        for (var rule = 0; rule < _rules.Rules.Count; rule++)
        {
            Assert.Equal(_rules.Rules[rule].Targets, parsed.Rules[rule].Targets);
        }

        Assert.Equal(_rules.ToJsonNode().ToJsonString(), parsed.ToJsonNode().ToJsonString());
    }

    // Encoding.UTF8 strips a leading byte order mark, so a file saved by an editor that adds one still loads.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_ReadsAValidFile(bool byteOrderMark)
    {
        var path = Path.Combine(TemporaryDirectory, RulesFileName);
        File.WriteAllText(path, _rules.ToJsonNode().ToJsonString(), new UTF8Encoding(byteOrderMark));
        Assert.Equal(byteOrderMark, File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble));

        var loaded = NormalisationRules.Load(path);

        Assert.Equal(_rules.ToJsonNode().ToJsonString(), loaded.ToJsonNode().ToJsonString());
    }

    [Fact]
    public void Load_RejectsAnInvalidDocument()
    {
        var path = Path.Combine(TemporaryDirectory, RulesFileName);
        var document = _rules.ToJsonNode().AsObject();
        RuleOf(document, 1)["id"] = "N3";
        File.WriteAllText(path, document.ToJsonString());

        var exception = Assert.Throws<InvalidDataException>(() => NormalisationRules.Load(path));

        // A message that named the file would show this test's temporary directory, so that is replaced by a fixed token.
        Assert.Equal(
            InvalidPrefix + "$.rules[1].id must be 'N2', but is 'N3'.",
            exception.Message.Replace(TemporaryDirectory, "<temporary-directory>", StringComparison.Ordinal));
    }

    // A directory at the path is the portable unreadable case: tests may run as root, which file modes do
    // not stop. .NET reports it as UnauthorizedAccessException, which is not an IOException.
    [Theory]
    [InlineData("missing file", typeof(FileNotFoundException))]
    [InlineData("file under a missing directory", typeof(DirectoryNotFoundException))]
    [InlineData("directory at the path", typeof(UnauthorizedAccessException))]
    public void Load_RejectsAnUnreadablePath(string pathCase, Type expected)
    {
        var path = pathCase switch
        {
            "missing file" => Path.Combine(TemporaryDirectory, RulesFileName),
            "file under a missing directory" => Path.Combine(TemporaryDirectory, "missing", RulesFileName),
            "directory at the path" => Directory.CreateDirectory(Path.Combine(TemporaryDirectory, RulesFileName)).FullName,
            _ => throw new ArgumentOutOfRangeException(nameof(pathCase), pathCase, "No path for this case."),
        };

        Assert.Throws(expected, () => NormalisationRules.Load(path));
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

    // Where a JSON value is classified as an element (the conformance check's Date and TimeStamp wire types),
    // a string token holding an unpaired surrogate escape parses but cannot be decoded, so it is invalid
    // rather than an exception, and every kind other than a string is invalid.
    [Theory]
    [InlineData(@"""\uD800""", false)]
    [InlineData(@"""\uDC00""", false)]
    [InlineData(@"""\uDC00\uD800""", false)]
    [InlineData(@"""\/Date(1\uD800)\/""", false)]
    [InlineData(@"""\/Date(1767268800000)\/""", true)]
    [InlineData(@"""/Date(1767268800000)/""", true)]
    [InlineData(@"""\/Date(1767268800000+1300)\/""", true)]
    [InlineData(@"""not a date""", false)]
    [InlineData(@"""""", false)]
    [InlineData("1767268800000", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public void N2_ValidJsonElement(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, JadeWireFormats.IsMicrosoftJsonDate(document.RootElement));
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

    /// <summary>A rejected document made by editing the valid document's nodes.</summary>
    private static RejectedDocument Edited(Action<JsonObject> edit, string expectedMessage) =>
        new(
            document =>
            {
                edit(document);
                return document.ToJsonString();
            },
            expectedMessage);

    /// <summary>A rejected document made by rewriting the valid document's compact text.</summary>
    private static RejectedDocument Rewritten(Func<string, string> rewrite, string expectedMessage) =>
        new(document => rewrite(document.ToJsonString()), expectedMessage);

    private static JsonArray RulesOf(JsonObject document) => document["rules"]!.AsArray();

    private static JsonObject RuleOf(JsonObject document, int rule) => RulesOf(document)[rule]!.AsObject();

    private static JsonObject ValidFormOf(JsonObject document, int rule) => RuleOf(document, rule)["validForm"]!.AsObject();

    private static JsonArray TargetsOf(JsonObject document, int rule) => RuleOf(document, rule)["targets"]!.AsArray();

    private static JsonObject TargetOf(JsonObject document, int rule, int target) => TargetsOf(document, rule)[target]!.AsObject();

    /// <summary>A rules document Parse must reject, and the start of the message it must give.</summary>
    /// <param name="Write">Turns a fresh valid document into the rejected text.</param>
    /// <param name="ExpectedMessage">The diagnostic the exception message starts with, naming the offending path.</param>
    private sealed record RejectedDocument(Func<JsonObject, string> Write, string ExpectedMessage);
}

// ---- Stand-in conformance verdicts --------------------------------------------------------------
// Stand-in conformance verdicts over records built here and handed to the check directly, so no file,
// network or running system is involved. A member passes only on a non-null value of its declared wire
// type: a JADE Integer is a whole number in the 32-bit range, decided exactly; a collection is valid only
// when every item has the array's item type and, on SOAP, its item element name; and a body that cannot
// be decoded, a raw body holding an invalid UTF-8 sequence included, contributes nothing instead of
// aborting the check.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and conformance asks whether a value is a valid value of its declared JADE wire type, the same
// typed-validity question the N1 and N2 validity forms decide.
public sealed partial class NormaliserTests
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

    // The conformance result capture committed, relative to the repository root.
    private const string CommittedConformancePath = "baseline/conformance.json";

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
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":"<lone-surrogate>"}]""", false, Label = "escaped surrogate in seed website")]
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"<lone-surrogate>","email":"x","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":""}]""", false, Label = "escaped surrogate in another client's name")]
    [InlineData("""[{"name":"Brian Olsen","email":"bo@wol.com","website":""},{"name":"Pauline Wild","email":"pw@wild.com","website":"<lone-surrogate>"}]""", true, Label = "raw surrogate in seed website")]
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
        // Members only, the counts the results report states; the seed-token assertions are in the detail.
        Assert.Equal(result.Members.Count, check.Cases);
        Assert.Equal(result.Counts.Passed, check.Passed);
        Assert.Equal((result.Summary.MembersChecked, result.Summary.Passed), (check.Cases, check.Passed));
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

    // ---- Result read back, held to the catalogue ------------------------------------------------

    /// <summary>
    /// An edit of a written conformance result that <see cref="ConformanceResult.Parse"/> still accepts,
    /// because the counts are recomputed from the edited members, and only
    /// <see cref="ConformanceCheck.Verify"/> can reject.
    /// </summary>
    public enum ConformanceTamper
    {
        /// <summary>The unreachable <c>DTOError.guid</c> claims <c>pass</c>.</summary>
        UnreachableMemberPasses,

        /// <summary>The unreachable REST constant <c>HTTP_Conflict</c> claims <c>unobserved</c>.</summary>
        UnreachableConstantUnobserved,

        /// <summary>The reachable <c>DTOClient.name</c> claims <c>unreachable-in-source</c>.</summary>
        ReachableMemberUnreachable,

        /// <summary>The inventory member <c>DTOError.guid</c> is removed.</summary>
        MemberMissing,

        /// <summary>A member the inventory does not hold is added.</summary>
        MemberUnknown,

        /// <summary>The constant <c>HTTP_Success</c> names a DTO class instead of its service class.</summary>
        ConstantClassChanged,

        /// <summary>The <c>brian-olsen-email</c> assertion is removed.</summary>
        SeedAssertionMissing,

        /// <summary>The <c>pauline-wild-website</c> assertion expects, and observes, another value.</summary>
        SeedAssertionExpectedChanged,

        /// <summary>An assertion the check does not define is added.</summary>
        SeedAssertionUnknown,
    }

    // {0} is the index of the edited or added member or assertion.
    [Theory]
    [InlineData(ConformanceTamper.UnreachableMemberPasses, "$.members[{0}].result is 'pass', but the catalogue records 'CONF:Rest:DTOError.guid' as unreachable in source, so its result must be 'unreachable-in-source'.")]
    [InlineData(ConformanceTamper.UnreachableConstantUnobserved, "$.members[{0}].result is 'unobserved', but the catalogue records 'CONF:Rest:STATUS:HTTP_Conflict' as unreachable in source, so its result must be 'unreachable-in-source'.")]
    [InlineData(ConformanceTamper.ReachableMemberUnreachable, "$.members[{0}].result is 'unreachable-in-source', but the catalogue records 'CONF:Rest:DTOClient.name' as 'reachable'.")]
    [InlineData(ConformanceTamper.MemberMissing, "$.members lacks 'CONF:Rest:DTOError.guid', a member of the catalogue inventory.")]
    [InlineData(ConformanceTamper.MemberUnknown, "$.members[{0}] is 'CONF:Rest:DTOAgent.nickname', which the catalogue inventory does not hold.")]
    [InlineData(ConformanceTamper.ConstantClassChanged, "$.members[{0}].class must be 'ErewhonRestService' for 'CONF:Rest:STATUS:HTTP_Success', found 'DTOResult'.")]
    [InlineData(ConformanceTamper.SeedAssertionMissing, "$.seedAssertions lacks seed-token assertion 'brian-olsen-email'.")]
    [InlineData(ConformanceTamper.SeedAssertionExpectedChanged, "$.seedAssertions[{0}].expected must be '\"\"' for 'CONF:SEED:pauline-wild-website', found '\"www.wild.com\"'.")]
    [InlineData(ConformanceTamper.SeedAssertionUnknown, "$.seedAssertions[{0}] is seed-token assertion 'carol-smith-email', which the check does not define.")]
    public void Verify_RejectsAResultTheCatalogueContradicts(ConformanceTamper tamper, string expectedMessage)
    {
        var document = Run(ConformanceSeedRecord()).ToJsonNode().AsObject();
        var index = Tamper(document, tamper);
        var parsed = ConformanceResult.Parse(JsonNode.Parse(document.ToJsonString()));

        var exception = Assert.Throws<InvalidDataException>(() => _check.Verify(parsed));

        Assert.Equal(
            "baseline/conformance.json: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, expectedMessage, index),
            exception.Message);
    }

    // The committed result, a fresh one and the fresh one read back as replay reads it all agree with the
    // catalogue.
    [Fact]
    public void Verify_AcceptsTheCommittedResultAndAFreshRun()
    {
        var committed = ConformanceResult.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), CommittedConformancePath))));
        var fresh = Run(ConformanceSeedRecord());
        var readBack = ConformanceResult.Parse(JsonNode.Parse(fresh.ToJsonNode().ToJsonString()));

        AssertVerifies(committed);
        AssertVerifies(fresh);
        AssertVerifies(readBack);
    }

    [Fact]
    public void Verify_RejectsANullResult() =>
        Assert.Throws<ArgumentNullException>("result", () => _check.Verify(null!));

    // A rejection is shown by its type and message only: the exception's text would carry its stack trace,
    // whose source paths differ between checkouts. Verify reads no file, so its messages are fixed.
    private void AssertVerifies(ConformanceResult result)
    {
        // Xunit.Record is named in full: this class's own Record builds a baseline record.
        var rejection = Xunit.Record.Exception(() => _check.Verify(result));
        Assert.True(
            rejection is null,
            $"Verify rejected the result with {rejection?.GetType().FullName}: {rejection?.Message}");
    }

    /// <summary>
    /// Applies <paramref name="tamper"/> to a document written by <see cref="ConformanceResult.ToJsonNode"/>
    /// and sets its counts to what the edited members give, so <see cref="ConformanceResult.Parse"/>
    /// accepts it.
    /// </summary>
    /// <returns>The index of the member or assertion edited or added; -1 for a removal.</returns>
    private static int Tamper(JsonObject document, ConformanceTamper tamper)
    {
        var members = document["members"]!.AsArray();
        var assertions = document["seedAssertions"]!.AsArray();
        var index = -1;
        switch (tamper)
        {
            case ConformanceTamper.UnreachableMemberPasses:
                index = Restate(members, "CONF:Rest:DTOError.guid", UnreachableInSource, Pass);
                break;
            case ConformanceTamper.UnreachableConstantUnobserved:
                index = Restate(members, "CONF:Rest:STATUS:HTTP_Conflict", UnreachableInSource, Unobserved);
                break;
            case ConformanceTamper.ReachableMemberUnreachable:
                index = Restate(members, RestClientName, Pass, UnreachableInSource);
                break;
            case ConformanceTamper.MemberMissing:
                members.RemoveAt(IndexOf(members, "itemId", "CONF:Rest:DTOError.guid"));
                break;
            case ConformanceTamper.MemberUnknown:
                members.Add(new JsonObject
                {
                    ["itemId"] = "CONF:Rest:DTOAgent.nickname",
                    ["surface"] = "Rest",
                    ["class"] = "DTOAgent",
                    ["property"] = "nickname",
                    ["result"] = Unobserved,
                });
                index = members.Count - 1;
                break;
            case ConformanceTamper.ConstantClassChanged:
                index = IndexOf(members, "itemId", "CONF:Rest:STATUS:HTTP_Success");
                members[index]!["class"] = "DTOResult";
                break;
            case ConformanceTamper.SeedAssertionMissing:
                assertions.RemoveAt(IndexOf(assertions, "assertionId", "brian-olsen-email"));
                break;
            case ConformanceTamper.SeedAssertionExpectedChanged:
                index = IndexOf(assertions, "assertionId", "pauline-wild-website");
                assertions[index]!["expected"] = "\"www.wild.com\"";
                assertions[index]!["observed"] = "\"www.wild.com\"";
                break;
            case ConformanceTamper.SeedAssertionUnknown:
                assertions.Add(new JsonObject
                {
                    ["assertionId"] = "carol-smith-email",
                    ["itemId"] = "CONF:SEED:carol-smith-email",
                    ["expected"] = "\"cs@smith.com\"",
                    ["observed"] = "\"cs@smith.com\"",
                    ["result"] = Pass,
                });
                index = assertions.Count - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, "Not a defined tamper.");
        }

        var results = members.Select(member => member!["result"]!.GetValue<string>()).ToArray();
        var counts = document["counts"]!.AsObject();
        counts["membersChecked"] = results.Length;
        counts["passed"] = results.Count(result => result == Pass);
        var gaps = counts["gapsByNature"]!.AsObject();
        foreach (var nature in new[] { UnreachableInSource, Unobserved, "null-only" })
        {
            gaps[nature] = results.Count(result => result == nature);
        }

        gaps["wrong-type"] = results.Count(result => result.StartsWith("wrong-type:", StringComparison.Ordinal));
        return index;
    }

    // The fresh result must hold the stated result before the edit, so each row edits what it names.
    private static int Restate(JsonArray members, string itemId, string before, string after)
    {
        var index = IndexOf(members, "itemId", itemId);
        Assert.Equal(before, members[index]!["result"]!.GetValue<string>());
        members[index]!["result"] = after;
        return index;
    }

    private static int IndexOf(JsonArray entries, string key, string value)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index]![key]!.GetValue<string>() == value)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"No entry has {key} '{value}'.");
    }

    // The message is fixed: a path would differ between checkouts and runs.
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, CommittedConformancePath)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("No ancestor of the test assembly holds baseline/conformance.json.");
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
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><e:Body><message></e:Body></e:Envelope>", Pass, Label = "prefixed SOAP 1.1 envelope")]
    [InlineData("<Envelope xmlns=\"http://schemas.xmlsoap.org/soap/envelope/\"><Body><message></Body></Envelope>", Pass, Label = "default-namespace SOAP 1.1 envelope")]
    [InlineData("<Envelope><Body><message></Body></Envelope>", Unobserved, Label = "no namespace")]
    [InlineData("<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\"><e:Body><message></e:Body></e:Envelope>", Unobserved, Label = "SOAP 1.2 namespace")]
    [InlineData("<e:Envelope xmlns:e=\"urn:example:foreign\"><b:Body xmlns:b=\"http://schemas.xmlsoap.org/soap/envelope/\"><message></b:Body></e:Envelope>", Unobserved, Label = "foreign Envelope, SOAP 1.1 Body")]
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><Body><message></Body></e:Envelope>", Unobserved, Label = "SOAP 1.1 Envelope, Body in no namespace")]
    [InlineData("<e:Envelope xmlns:e=\"http://schemas.xmlsoap.org/soap/envelope/\"><b:Body xmlns:b=\"urn:example:foreign\"><message></b:Body></e:Envelope>", Unobserved, Label = "SOAP 1.1 Envelope, foreign Body")]
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
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Pass, Label = "default target namespace")]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><m:getAgentResult><m:name>Hank Williams</m:name></m:getAgentResult></m:getAgentResponse>", Pass, Label = "prefixed target namespace")]
    [InlineData("<getAgentResponse><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved, Label = "no namespace")]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved, Label = "near-miss namespace")]
    [InlineData("<getAgentResponse xmlns=\"urn:example:foreign\"><getAgentResult><name>Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved, Label = "foreign namespace")]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><m:name>Hank Williams</m:name></getAgentResult></m:getAgentResponse>", Unobserved, Label = "Result in no namespace")]
    [InlineData("<m:getAgentResponse xmlns:m=\"urn:JadeWebServices/WebServiceHTTP/\"><f:getAgentResult xmlns:f=\"urn:example:foreign\"><m:name>Hank Williams</m:name></f:getAgentResult></m:getAgentResponse>", Unobserved, Label = "foreign Result")]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\">Hank Williams</f:name></getAgentResult></getAgentResponse>", Unobserved, Label = "foreign name")]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><name xmlns=\"\">Hank Williams</name></getAgentResult></getAgentResponse>", Unobserved, Label = "name in no namespace")]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\"><x/></f:name></getAgentResult></getAgentResponse>", Unobserved, Label = "foreign name with element content")]
    [InlineData("<getAgentResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getAgentResult><f:name xmlns:f=\"urn:example:foreign\">x</f:name><name>Hank Williams</name></getAgentResult></getAgentResponse>", Pass, Label = "foreign name before target name")]
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
