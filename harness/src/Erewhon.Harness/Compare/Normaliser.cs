using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;

namespace Erewhon.Harness.Compare;

/// <summary>The body syntax a normalised value was read from, which selects the rule's validity form.</summary>
public enum WireSyntax
{
    /// <summary>A REST body: JSON in the Microsoft-JSON conventions.</summary>
    Json,

    /// <summary>A SOAP 1.1 envelope or a WSDL document.</summary>
    Xml,
}

/// <summary>
/// One side's value at a normalisation-targeted position, as a semantic comparer found it.
/// </summary>
/// <remarks>
/// Text convention: for JSON, <see cref="Text"/> is the DECODED string value (the parser has already
/// turned <c>"\/Date(1)\/"</c> into <c>/Date(1)/</c>); for XML it is the exact concatenated text
/// content of the element. Callers build slots only for JSON strings and <c>null</c>; every other JSON
/// value kind is compared by the JSON comparer itself, because it can never be valid under a rule.
/// The properties are get-only, so a <c>with</c> expression cannot build a slot that breaks the
/// invariant the constructor checks: text is held exactly when the slot is present and not null.
/// </remarks>
/// <param name="Present">True when a value exists at the position on this side.</param>
/// <param name="IsNull">True for JSON <c>null</c> or XML <c>xsi:nil="true"</c>; never true for an absent slot.</param>
/// <param name="Text">The value text in the convention above; null for an absent or null slot.</param>
public sealed record NormalisedSlot(bool Present, bool IsNull, string? Text)
{
    /// <summary>True when a value exists at the position on this side.</summary>
    public bool Present { get; } = Present;

    /// <summary>True for JSON <c>null</c> or XML <c>xsi:nil="true"</c>.</summary>
    public bool IsNull { get; } = IsNull && !Present
        ? throw new ArgumentException("An absent slot cannot be null.", nameof(IsNull))
        : IsNull;

    /// <summary>The value text; null exactly when the slot is absent or null.</summary>
    public string? Text { get; } = (Text is not null) == (Present && !IsNull)
        ? Text
        : throw new ArgumentException("A slot holds text exactly when it is present and not null.", nameof(Text));

    /// <summary>Nothing exists at the position on this side.</summary>
    public static NormalisedSlot Absent { get; } = new(false, false, null);

    /// <summary>The position holds JSON <c>null</c> or an XML element with <c>xsi:nil="true"</c>.</summary>
    public static NormalisedSlot Null { get; } = new(true, true, null);

    /// <summary>Returns a present, non-null slot holding <paramref name="text"/> exactly, untrimmed.</summary>
    /// <param name="text">The decoded JSON string value or the exact XML text content.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static NormalisedSlot Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new NormalisedSlot(true, false, text);
    }
}

/// <summary>The ids of the only two normalisation rules, equal to the catalogue's <see cref="NormalisationClasses"/>.</summary>
public static class NormalisationRuleIds
{
    /// <summary>Generated identifiers, validated as braced 38-character JADE GUIDs.</summary>
    public const string N1 = NormalisationClasses.N1;

    /// <summary>Timestamps, validated as Microsoft JSON dates in JSON and <c>xsd:dateTime</c> in XML.</summary>
    public const string N2 = NormalisationClasses.N2;
}

/// <summary>A position a normalisation rule applies to: a property of a DTO class, matched by the runtime class.</summary>
/// <param name="Class">The DTO class name exactly as declared, for example <c>DTOResult</c>.</param>
/// <param name="Property">The JADE property name exactly as declared, for example <c>guid</c>.</param>
public sealed record NormalisationTarget(string Class, string Property);

/// <summary>One normalisation rule as written to <c>baseline/normalisation-rules.json</c>.</summary>
/// <param name="Id">The rule id, <c>N1</c> or <c>N2</c>.</param>
/// <param name="Name">The rule's report name, <c>{generated-guid}</c> or <c>{timestamp}</c>; never a substituted marker.</param>
/// <param name="Description">What the rule targets and how it decides equality.</param>
/// <param name="Targets">The targeted positions, sorted ordinally by class, then property.</param>
public sealed record NormalisationRule(string Id, string Name, string Description, IReadOnlyList<NormalisationTarget> Targets);

/// <summary>
/// The two normalisation rules applied to the baseline, N1 generated identifiers and N2 timestamps, with
/// the positions each targets. Neither rule rewrites a body; <see cref="Normaliser"/> uses them only to
/// decide pairwise equality at targeted positions.
/// </summary>
/// <remarks>
/// Instances come only from <see cref="FromCatalogue"/>, <see cref="Parse"/>, <see cref="Load"/> and
/// <see cref="LoadAsync"/>, and always hold exactly two rules, <c>N1</c> then <c>N2</c>, with no position
/// targeted twice. Instances are immutable.
/// </remarks>
public sealed class NormalisationRules
{
    private const int DocumentVersion = 1;

    private const string VersionKey = "version";
    private const string RulesKey = "rules";
    private const string IdKey = "id";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ValidFormKey = "validForm";
    private const string JsonFormKey = "json";
    private const string XmlFormKey = "xml";
    private const string TargetsKey = "targets";
    private const string ClassKey = "class";
    private const string PropertyKey = "property";

    private static readonly string[] RootKeys = [VersionKey, RulesKey];
    private static readonly string[] RuleKeys = [IdKey, NameKey, DescriptionKey, ValidFormKey, TargetsKey];
    private static readonly string[] ValidFormKeys = [JsonFormKey, XmlFormKey];
    private static readonly string[] TargetKeys = [ClassKey, PropertyKey];

    // The same strictness as the catalogue loader: no comments, no trailing commas, and a repeated key
    // is an error rather than silently keeping its last value.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private static readonly RuleSpec[] Specs =
    [
        new(
            NormalisationRuleIds.N1,
            "{generated-guid}",
            "Generated identifiers: the guid of every DTO whose source object's identifier is generated at load or "
            + "request time. Values are equal when both are valid braced 38-character JADE GUIDs, or when neither is "
            + "valid and both are exactly equal.",
            JsonForm: "jade-guid",
            XmlForm: "jade-guid"),
        new(
            NormalisationRuleIds.N2,
            "{timestamp}",
            "Timestamps: every TimeStamp-typed value. Values are equal when both are valid (Microsoft JSON date in "
            + "JSON, xsd:dateTime in XML), or when neither is valid and both are exactly equal.",
            JsonForm: "microsoft-json-date",
            XmlForm: "xsd:dateTime"),
    ];

    private NormalisationRules(IReadOnlyList<NormalisationRule> rules) => Rules = rules;

    /// <summary>The two rules, always <c>N1</c> then <c>N2</c>, each with ordinally sorted targets.</summary>
    public IReadOnlyList<NormalisationRule> Rules { get; }

    /// <summary>
    /// Derives the rules from the catalogue: every DTO property, inherited ones included, whose
    /// <see cref="DtoProperty.Normalisation"/> names a rule becomes a target of that rule.
    /// </summary>
    /// <param name="catalogue">The operation catalogue.</param>
    /// <returns>The rules, with de-duplicated targets sorted ordinally by class, then property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidDataException">A property names a normalisation other than <c>N1</c> or <c>N2</c>.</exception>
    public static NormalisationRules FromCatalogue(OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var n1Targets = new HashSet<NormalisationTarget>();
        var n2Targets = new HashSet<NormalisationTarget>();
        foreach (var dto in catalogue.Dtos)
        {
            foreach (var property in dto.Properties)
            {
                switch (property.Normalisation)
                {
                    case null:
                        break;
                    case NormalisationClasses.N1:
                        n1Targets.Add(new NormalisationTarget(dto.Name, property.Name));
                        break;
                    case NormalisationClasses.N2:
                        n2Targets.Add(new NormalisationTarget(dto.Name, property.Name));
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Catalogue property {dto.Name}.{property.Name} names normalisation '{property.Normalisation}', "
                            + $"which is neither '{NormalisationRuleIds.N1}' nor '{NormalisationRuleIds.N2}'.");
                }
            }
        }

        return Create([Specs[0].Description, Specs[1].Description], [n1Targets, n2Targets]);
    }

    /// <summary>
    /// Parses a document written by <see cref="ToJsonNode"/>. The parse is strict: the version must be
    /// <c>1</c>; there must be exactly two rules with ids <c>N1</c> then <c>N2</c>, whose names and valid
    /// forms equal the fixed ones; every target needs a non-empty <c>class</c> and <c>property</c>; and
    /// unknown, missing or repeated members, repeated targets and a target under both rules are errors.
    /// Descriptions are kept as stored, and targets are re-sorted.
    /// </summary>
    /// <param name="json">The document text.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="InvalidDataException">The text is not JSON or breaks one of the conditions above.</exception>
    public static NormalisationRules Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The normalisation rules are not valid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            return FromDocument(document.RootElement);
        }
    }

    /// <summary>Reads and parses a normalisation rules file, as <see cref="Parse"/> does.</summary>
    /// <param name="path">The file path, normally <c>baseline/normalisation-rules.json</c>.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The file content is not a valid rules document.</exception>
    /// <exception cref="IOException">
    /// The file cannot be read, including <see cref="FileNotFoundException"/> for a missing file and
    /// <see cref="DirectoryNotFoundException"/> for a missing directory.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The path names a directory, or reading the file is denied.</exception>
    public static NormalisationRules Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// Reads and parses a normalisation rules file without blocking, as <see cref="Load"/> does: the text is
    /// decoded as UTF-8 unless a byte-order mark names another encoding, the mark is dropped, and
    /// <see cref="Parse"/> applies.
    /// </summary>
    /// <param name="path">The file path, normally <c>baseline/normalisation-rules.json</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="InvalidDataException">The file content is not a valid rules document.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<NormalisationRules> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns <c>{"version":1,"rules":[{"id","name","description","validForm":{"json","xml"},"targets":[{"class","property"}]}]}</c>
    /// with the rules in <c>N1</c>, <c>N2</c> order and sorted targets, so equal rules always serialise to the
    /// same bytes. A new node is returned on every call.
    /// </summary>
    public JsonNode ToJsonNode()
    {
        var rules = new JsonArray();
        for (var index = 0; index < Specs.Length; index++)
        {
            var spec = Specs[index];
            var rule = Rules[index];
            var targets = new JsonArray();
            foreach (var target in rule.Targets)
            {
                targets.Add(new JsonObject
                {
                    [ClassKey] = target.Class,
                    [PropertyKey] = target.Property,
                });
            }

            rules.Add(new JsonObject
            {
                [IdKey] = rule.Id,
                [NameKey] = rule.Name,
                [DescriptionKey] = rule.Description,
                [ValidFormKey] = new JsonObject
                {
                    [JsonFormKey] = spec.JsonForm,
                    [XmlFormKey] = spec.XmlForm,
                },
                [TargetsKey] = targets,
            });
        }

        return new JsonObject
        {
            [VersionKey] = DocumentVersion,
            [RulesKey] = rules,
        };
    }

    /// <summary>
    /// Builds the two rules from their descriptions and targets, in <see cref="Specs"/> order, sorting each
    /// rule's targets and rejecting a position targeted twice, since a position can follow only one rule.
    /// </summary>
    private static NormalisationRules Create(
        IReadOnlyList<string> descriptions,
        IReadOnlyList<IEnumerable<NormalisationTarget>> targets)
    {
        var ruleByTarget = new Dictionary<NormalisationTarget, string>();
        var rules = new NormalisationRule[Specs.Length];
        for (var index = 0; index < Specs.Length; index++)
        {
            var spec = Specs[index];
            var sorted = targets[index].ToList();
            sorted.Sort(CompareTargets);
            foreach (var target in sorted)
            {
                if (!ruleByTarget.TryAdd(target, spec.Id))
                {
                    throw Invalid(
                        $"{target.Class}.{target.Property} is targeted by both '{ruleByTarget[target]}' and '{spec.Id}'.");
                }
            }

            rules[index] = new NormalisationRule(spec.Id, spec.Name, descriptions[index], sorted.AsReadOnly());
        }

        return new NormalisationRules(Array.AsReadOnly(rules));
    }

    private static NormalisationRules FromDocument(JsonElement root)
    {
        RequireObject(root, "$", RootKeys);

        var version = root.GetProperty(VersionKey);
        if (version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != DocumentVersion)
        {
            throw Invalid($"$.{VersionKey} must be the integer {DocumentVersion}, but is {version.GetRawText()}.");
        }

        var rules = root.GetProperty(RulesKey);
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() != Specs.Length)
        {
            throw Invalid(
                $"$.{RulesKey} must be an array of exactly {Specs.Length} rules, "
                + $"'{NormalisationRuleIds.N1}' then '{NormalisationRuleIds.N2}'.");
        }

        var descriptions = new string[Specs.Length];
        var targets = new IEnumerable<NormalisationTarget>[Specs.Length];
        for (var index = 0; index < Specs.Length; index++)
        {
            (descriptions[index], targets[index]) = ReadRule(rules[index], Specs[index], $"$.{RulesKey}[{index}]");
        }

        return Create(descriptions, targets);
    }

    private static (string Description, IReadOnlyList<NormalisationTarget> Targets) ReadRule(
        JsonElement rule,
        RuleSpec spec,
        string path)
    {
        RequireObject(rule, path, RuleKeys);
        RequireExact(rule, IdKey, path, spec.Id);
        RequireExact(rule, NameKey, path, spec.Name);
        var description = RequireString(rule, DescriptionKey, path);

        var validFormPath = $"{path}.{ValidFormKey}";
        var validForm = rule.GetProperty(ValidFormKey);
        RequireObject(validForm, validFormPath, ValidFormKeys);
        RequireExact(validForm, JsonFormKey, validFormPath, spec.JsonForm);
        RequireExact(validForm, XmlFormKey, validFormPath, spec.XmlForm);

        var targetsPath = $"{path}.{TargetsKey}";
        var targetsElement = rule.GetProperty(TargetsKey);
        if (targetsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{targetsPath} must be an array, but is {targetsElement.ValueKind}.");
        }

        var targets = new List<NormalisationTarget>(targetsElement.GetArrayLength());
        var seen = new HashSet<NormalisationTarget>();
        foreach (var item in targetsElement.EnumerateArray())
        {
            var itemPath = $"{targetsPath}[{targets.Count}]";
            RequireObject(item, itemPath, TargetKeys);
            var target = new NormalisationTarget(
                RequireNonEmptyString(item, ClassKey, itemPath),
                RequireNonEmptyString(item, PropertyKey, itemPath));
            if (!seen.Add(target))
            {
                throw Invalid($"{itemPath} repeats target {target.Class}.{target.Property}.");
            }

            targets.Add(target);
        }

        return (description, targets);
    }

    // Unknown and missing members are both rejected, as the catalogue loader rejects them, so a key a
    // later edit adds can never be silently ignored.
    private static void RequireObject(JsonElement element, string path, string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{path} must be an object, but is {element.ValueKind}.");
        }

        foreach (var member in element.EnumerateObject())
        {
            if (Array.IndexOf(keys, member.Name) < 0)
            {
                throw Invalid($"{path} has unknown member '{member.Name}'.");
            }
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out _))
            {
                throw Invalid($"{path} has no member '{key}'.");
            }
        }
    }

    private static string RequireString(JsonElement parent, string key, string path)
    {
        var value = parent.GetProperty(key);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid($"{path}.{key} must be a string, but is {value.ValueKind}.");
    }

    private static string RequireNonEmptyString(JsonElement parent, string key, string path)
    {
        var value = RequireString(parent, key, path);
        return value.Length > 0 ? value : throw Invalid($"{path}.{key} must not be empty.");
    }

    private static void RequireExact(JsonElement parent, string key, string path, string expected)
    {
        var value = RequireString(parent, key, path);
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw Invalid($"{path}.{key} must be '{expected}', but is '{value}'.");
        }
    }

    private static int CompareTargets(NormalisationTarget x, NormalisationTarget y)
    {
        var order = string.CompareOrdinal(x.Class, y.Class);
        return order != 0 ? order : string.CompareOrdinal(x.Property, y.Property);
    }

    private static InvalidDataException Invalid(string message) =>
        new("Invalid normalisation rules: " + message);

    /// <summary>The fixed id, report name, description and per-syntax valid form of one rule.</summary>
    private sealed record RuleSpec(string Id, string Name, string Description, string JsonForm, string XmlForm);
}

/// <summary>
/// Applies the N1 and N2 normalisation rules at targeted positions: it says which rule, if any, a DTO
/// property follows, whether a value is valid for that rule's form, whether two values are equal under the
/// pairwise rule, and how a value appears in a discrepancy. It never rewrites a body.
/// </summary>
/// <remarks>
/// Pairwise rule: two values are equal when both are valid for the rule's form, whatever their content, or
/// when neither is valid and they are exactly equal (both absent, both null, or the same text). A valid
/// value never equals an absent, null, empty or invalid one. The rule names <c>{generated-guid}</c> and
/// <c>{timestamp}</c> are never substituted into a body, so a body that literally holds either holds an
/// ordinary invalid value. Instances are immutable and perform no I/O, so they are safe to share across
/// threads.
/// </remarks>
/// <param name="rules">The rules to apply, normally <see cref="NormalisationRules.FromCatalogue"/> or the baseline's file.</param>
public sealed class Normaliser(NormalisationRules rules)
{
    private const string JsonNullLiteral = "null";
    private const string XmlNilLiteral = "xsi:nil=\"true\"";

    /// <summary>The rules this normaliser applies.</summary>
    public NormalisationRules Rules { get; } = rules ?? throw new ArgumentNullException(nameof(rules));

    // Declared after Rules: member initializers run in declaration order, so a null argument is rejected
    // before it is indexed.
    private readonly FrozenDictionary<(string Class, string Property), string> _ruleByTarget = Index(rules);

    /// <summary>
    /// Returns the rule a property of a DTO class follows: <c>N1</c>, <c>N2</c> or null when its value is
    /// compared exactly. The class must match a target exactly; callers pass the runtime class from
    /// <c>__type</c> or <c>xsi:type</c> where present, otherwise the declared class.
    /// </summary>
    /// <param name="dtoClass">The DTO class name; null or empty gives null.</param>
    /// <param name="property">The JADE property name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="property"/> is null.</exception>
    public string? RuleFor(string? dtoClass, string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (string.IsNullOrEmpty(dtoClass))
        {
            return null;
        }

        return _ruleByTarget.TryGetValue((dtoClass, property), out var rule) ? rule : null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is valid for <paramref name="rule"/> in <paramref name="syntax"/>:
    /// a braced 38-character JADE GUID for <c>N1</c>; for <c>N2</c> a decoded Microsoft JSON date
    /// (<c>/Date(&lt;ms&gt;[±hhmm])/</c>) in JSON or an <c>xsd:dateTime</c> in XML. Null, empty and
    /// whitespace-padded text is never valid; nothing is trimmed.
    /// </summary>
    /// <param name="rule">The rule id, <c>N1</c> or <c>N2</c>.</param>
    /// <param name="text">The slot text, in the <see cref="NormalisedSlot"/> text convention.</param>
    /// <param name="syntax">The syntax the text was read from.</param>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is neither <c>N1</c> nor <c>N2</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntax"/> is not a defined value.</exception>
    public bool IsValid(string rule, string? text, WireSyntax syntax)
    {
        RequireRule(rule);
        RequireSyntax(syntax);

        if (string.IsNullOrEmpty(text) || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
        {
            return false;
        }

        if (string.Equals(rule, NormalisationRuleIds.N1, StringComparison.Ordinal))
        {
            return JadeWireFormats.IsValidJadeGuid(text);
        }

        return syntax == WireSyntax.Json
            ? JadeWireFormats.IsMicrosoftJsonDate(text)
            : JadeWireFormats.IsXsdDateTime(text);
    }

    /// <summary>
    /// Applies the pairwise rule: true when both slots are valid, false when exactly one is, and otherwise
    /// true only when both are equally present, equally null and hold the same text, compared ordinally.
    /// </summary>
    /// <param name="rule">The rule id, <c>N1</c> or <c>N2</c>.</param>
    /// <param name="baseline">The baseline side's slot.</param>
    /// <param name="observed">The observed side's slot.</param>
    /// <param name="syntax">The syntax both slots were read from.</param>
    /// <exception cref="ArgumentNullException">A slot is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is neither <c>N1</c> nor <c>N2</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntax"/> is not a defined value.</exception>
    public bool PairEquals(string rule, NormalisedSlot baseline, NormalisedSlot observed, WireSyntax syntax)
    {
        RequireRule(rule);
        RequireSyntax(syntax);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observed);

        var baselineValid = IsValidSlot(rule, baseline, syntax);
        var observedValid = IsValidSlot(rule, observed, syntax);
        if (baselineValid || observedValid)
        {
            return baselineValid && observedValid;
        }

        return baseline.Present == observed.Present
            && baseline.IsNull == observed.IsNull
            && string.Equals(baseline.Text, observed.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns how a slot appears in a discrepancy: <see cref="DiscrepancyValue.Valid"/> for a valid value,
    /// so its content never enters a signature; <see cref="DiscrepancyValue.Absent"/> for an absent one;
    /// otherwise a literal: <c>null</c> or <c>xsi:nil="true"</c> for a null slot, the value as a compact JSON
    /// string literal, quotes included, in JSON, and the exact text in XML.
    /// </summary>
    /// <param name="rule">The rule id, <c>N1</c> or <c>N2</c>.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="syntax">The syntax the slot was read from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is neither <c>N1</c> nor <c>N2</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syntax"/> is not a defined value.</exception>
    public DiscrepancyValue ToValue(string rule, NormalisedSlot slot, WireSyntax syntax)
    {
        RequireRule(rule);
        RequireSyntax(syntax);
        ArgumentNullException.ThrowIfNull(slot);

        if (IsValidSlot(rule, slot, syntax))
        {
            return DiscrepancyValue.Valid(rule);
        }

        if (!slot.Present)
        {
            return DiscrepancyValue.Absent;
        }

        if (slot.IsNull)
        {
            return DiscrepancyValue.Literal(syntax == WireSyntax.Json ? JsonNullLiteral : XmlNilLiteral);
        }

        // The JSON literal is re-encoded with the default encoder, the same convention the JSON comparer
        // uses for its literals, so one value always gives one signature token.
        return DiscrepancyValue.Literal(
            syntax == WireSyntax.Json ? JsonValue.Create(slot.Text)!.ToJsonString() : slot.Text);
    }

    private bool IsValidSlot(string rule, NormalisedSlot slot, WireSyntax syntax) =>
        slot.Present && !slot.IsNull && IsValid(rule, slot.Text, syntax);

    private static FrozenDictionary<(string Class, string Property), string> Index(NormalisationRules rules)
    {
        var index = new Dictionary<(string Class, string Property), string>();
        foreach (var rule in rules.Rules)
        {
            foreach (var target in rule.Targets)
            {
                index.Add((target.Class, target.Property), rule.Id);
            }
        }

        return index.ToFrozenDictionary();
    }

    private static void RequireRule(string rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!string.Equals(rule, NormalisationRuleIds.N1, StringComparison.Ordinal)
            && !string.Equals(rule, NormalisationRuleIds.N2, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{rule}' is not a normalisation rule; expected '{NormalisationRuleIds.N1}' or '{NormalisationRuleIds.N2}'.",
                nameof(rule));
        }
    }

    private static void RequireSyntax(WireSyntax syntax)
    {
        if (syntax is not (WireSyntax.Json or WireSyntax.Xml))
        {
            throw new ArgumentOutOfRangeException(nameof(syntax), syntax, "Unknown wire syntax.");
        }
    }
}
