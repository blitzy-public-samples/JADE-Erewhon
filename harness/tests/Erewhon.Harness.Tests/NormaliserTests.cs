using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The two normalisation rules (N1 generated identifiers, N2 timestamps) as derived from the contracts
/// catalogue, their validity forms in each wire syntax, the pairwise rule that decides equality at a
/// targeted position, and the strict parse and load of the rules document
/// (<c>baseline/normalisation-rules.json</c>). Every expected class and property name below is checked
/// against the catalogue-derived rules, never used to build them.
/// </summary>
public sealed class NormaliserTests : IDisposable
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
        Assert.Equal(InvalidPrefix + "$.rules[1].id must be 'N2', but is 'N3'.", exception.Message);
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
