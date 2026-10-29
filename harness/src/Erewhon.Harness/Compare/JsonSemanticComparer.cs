using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;

namespace Erewhon.Harness.Compare;

/// <summary>
/// Compares two JSON bodies semantically: REST response bodies typed by the operation's declared return
/// type, and untyped documents such as the OpenAPI description. Both bodies are walked in parallel.
/// Member order and inter-token whitespace are irrelevant, array order is significant, numbers compare
/// by numeric value and strings compare exactly, with no trimming or case folding.
/// </summary>
/// <remarks>
/// <para>
/// Typing comes only from the catalogue. An object at a position whose declared type is a catalogue DTO
/// is walked as the BASELINE object's runtime class, named by its <c>__type</c> member when that names
/// a catalogue DTO, otherwise as the declared class; a differing <c>__type</c> on the observed side is an
/// ordinary member difference and never changes the typing. Reference members recurse with their DTO
/// type, collection members with their array type, and array items with the array's item type.
/// </para>
/// <para>
/// At a position the <see cref="Normaliser"/> names a rule for (N1 generated identifiers, N2
/// timestamps), the two values are equal under the rule's pairwise test; nothing in either body is
/// rewritten. Every other position is compared exactly.
/// </para>
/// <para>
/// Discrepancy paths start at <c>$</c>: <c>.name</c> for an identifier member, <c>['x y']</c> for any
/// other member name and <c>[i]</c> for a 0-based array index, for example <c>$[3].item.agent.name</c>.
/// Literal values are the compact, deterministic re-encoding of the JSON value (strings keep their
/// quotes and JSON <c>null</c> is <c>null</c>), so identical input always yields an identical list.
/// </para>
/// <para>
/// The instance holds only its two immutable constructor arguments and performs no I/O, so concurrent
/// <see cref="Compare"/> calls are safe.
/// </para>
/// </remarks>
/// <param name="catalogue">The operation catalogue the bodies are typed by.</param>
/// <param name="normaliser">The N1/N2 normaliser applied at targeted positions.</param>
public sealed class JsonSemanticComparer(OperationCatalogue catalogue, Normaliser normaliser)
{
    private const string RootPath = "$";

    private readonly OperationCatalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly Normaliser _normaliser = normaliser ?? throw new ArgumentNullException(nameof(normaliser));

    /// <summary>
    /// Compares <paramref name="observed"/> with <paramref name="baseline"/> and returns every difference
    /// in walk order: baseline members in document order, then observed-only members in document order,
    /// array items by index.
    /// </summary>
    /// <param name="baseline">The baseline body text.</param>
    /// <param name="observed">The observed body text.</param>
    /// <param name="declaredType">
    /// The catalogue DTO or array name the root is declared as, for example <c>DTOResult</c> or
    /// <c>DTOItemsArray</c>; null, or a name that is neither a DTO nor an array, compares untyped.
    /// </param>
    /// <returns>
    /// The discrepancies, empty when the bodies are equivalent. When either body is not JSON, exactly one
    /// <c>malformed</c> discrepancy at <c>$</c>, holding the raw text of each side that failed to parse and
    /// <see cref="DiscrepancyValue.Absent"/> for a side that parsed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseline"/> or <paramref name="observed"/> is null.</exception>
    public IReadOnlyList<Discrepancy> Compare(string baseline, string observed, string? declaredType)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observed);

        using var baselineDocument = TryParse(baseline);
        using var observedDocument = TryParse(observed);

        if (baselineDocument is null || observedDocument is null)
        {
            return
            [
                new Discrepancy(
                    RootPath,
                    DiscrepancyKinds.Malformed,
                    baselineDocument is null ? DiscrepancyValue.Literal(baseline) : DiscrepancyValue.Absent,
                    observedDocument is null ? DiscrepancyValue.Literal(observed) : DiscrepancyValue.Absent),
            ];
        }

        var baselineRoot = baselineDocument.RootElement;
        var observedRoot = observedDocument.RootElement;

        // The parser accepts escaped lone surrogates such as "\uD800", which cannot be decoded to a string
        // or re-encoded. A body holding one is compared as exact text, so an odd response from the system
        // under test yields a discrepancy instead of an exception that would end the whole replay.
        if (!IsDecodable(baselineRoot) || !IsDecodable(observedRoot))
        {
            return string.Equals(baseline, observed, StringComparison.Ordinal)
                ? []
                : [new Discrepancy(RootPath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(baseline), DiscrepancyValue.Literal(observed))];
        }

        var discrepancies = new List<Discrepancy>();
        Walk(RootPath, baselineRoot, observedRoot, declaredType, discrepancies);
        return discrepancies.AsReadOnly();
    }

    /// <summary>
    /// Compares two values at <paramref name="path"/>, typed by <paramref name="type"/>: a catalogue DTO
    /// name for an object, a catalogue array name for an array, or null for an untyped value.
    /// </summary>
    private void Walk(string path, JsonElement baseline, JsonElement observed, string? type, List<Discrepancy> discrepancies)
    {
        if (KindClass(baseline.ValueKind) != KindClass(observed.ValueKind))
        {
            discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Type, Compact(baseline), Compact(observed)));
            return;
        }

        switch (baseline.ValueKind)
        {
            case JsonValueKind.Object:
                WalkObject(path, baseline, observed, type, discrepancies);
                break;

            case JsonValueKind.Array:
                WalkArray(path, baseline, observed, type, discrepancies);
                break;

            case JsonValueKind.Number:
                // Numbers compare by exact numeric value (1 == 1.0 == 1e0), the JsonNode.DeepEquals
                // semantics the comparison is specified by. Converting to decimal or double first would
                // round digits beyond their precision and make distinct numbers equal.
                if (!JsonElement.DeepEquals(baseline, observed))
                {
                    discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Value, Compact(baseline), Compact(observed)));
                }

                break;

            case JsonValueKind.String:
                // Decoded values, compared ordinally: "\/" and "/" are the same value, but no trimming
                // or case folding applies, so "bo@wol.com " differs from "bo@wol.com".
                if (!string.Equals(baseline.GetString(), observed.GetString(), StringComparison.Ordinal))
                {
                    discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Value, Compact(baseline), Compact(observed)));
                }

                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (baseline.ValueKind != observed.ValueKind)
                {
                    discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Value, Compact(baseline), Compact(observed)));
                }

                break;

            case JsonValueKind.Null:
                break;

            default:
                throw new InvalidOperationException(
                    $"A parsed JSON document holds a value of kind '{baseline.ValueKind}' at {path}.");
        }
    }

    /// <summary>
    /// Compares two objects member by member, matched by name ordinally. A name repeated within one object
    /// is compared occurrence by occurrence in document order.
    /// </summary>
    private void WalkObject(string path, JsonElement baseline, JsonElement observed, string? type, List<Discrepancy> discrepancies)
    {
        var baselineMembers = MemberGroups.Of(baseline);
        var observedMembers = MemberGroups.Of(observed);
        var runtimeDto = ResolveRuntimeDto(baselineMembers, type);

        foreach (var name in baselineMembers.Names)
        {
            CompareMember(path, runtimeDto, name, baselineMembers.ValuesOf(name), observedMembers.ValuesOf(name), discrepancies);
        }

        foreach (var name in observedMembers.Names)
        {
            if (!baselineMembers.Contains(name))
            {
                CompareMember(path, runtimeDto, name, [], observedMembers.ValuesOf(name), discrepancies);
            }
        }
    }

    /// <summary>
    /// Compares the occurrences of one member name. A count difference between two sides that both hold
    /// the name is a <c>length</c> discrepancy, followed by the comparison of the common occurrences; a
    /// name only one side holds gives one <c>missing</c> or <c>extra</c> discrepancy per occurrence.
    /// </summary>
    private void CompareMember(
        string objectPath,
        DtoDefinition? runtimeDto,
        string name,
        IReadOnlyList<JsonElement> baselineValues,
        IReadOnlyList<JsonElement> observedValues,
        List<Discrepancy> discrepancies)
    {
        var path = MemberPath(objectPath, name);
        var rule = runtimeDto is null ? null : _normaliser.RuleFor(runtimeDto.Name, name);

        if (baselineValues.Count == 0 || observedValues.Count == 0)
        {
            foreach (var value in baselineValues)
            {
                discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Missing, MemberValue(rule, value), DiscrepancyValue.Absent));
            }

            foreach (var value in observedValues)
            {
                discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, MemberValue(rule, value)));
            }

            return;
        }

        if (baselineValues.Count != observedValues.Count)
        {
            discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Length, CountValue(baselineValues.Count), CountValue(observedValues.Count)));
        }

        var memberType = rule is null ? MemberType(runtimeDto, name) : null;
        var common = Math.Min(baselineValues.Count, observedValues.Count);
        for (var index = 0; index < common; index++)
        {
            if (rule is null)
            {
                Walk(path, baselineValues[index], observedValues[index], memberType, discrepancies);
            }
            else
            {
                CompareNormalised(path, rule, baselineValues[index], observedValues[index], discrepancies);
            }
        }
    }

    /// <summary>
    /// Compares two values at a position <paramref name="rule"/> targets. Two strings are equal under the
    /// rule's pairwise test on their decoded values, and two nulls are equal. Two values of the same other
    /// kind can never be valid under a rule, so they are compared exactly by the untyped walk. Any other
    /// pair is a <c>type</c> discrepancy.
    /// </summary>
    private void CompareNormalised(string path, string rule, JsonElement baseline, JsonElement observed, List<Discrepancy> discrepancies)
    {
        var baselineKind = baseline.ValueKind;
        var observedKind = observed.ValueKind;

        if (baselineKind == JsonValueKind.String && observedKind == JsonValueKind.String)
        {
            var baselineSlot = NormalisedSlot.Of(baseline.GetString()!);
            var observedSlot = NormalisedSlot.Of(observed.GetString()!);
            if (!_normaliser.PairEquals(rule, baselineSlot, observedSlot, WireSyntax.Json))
            {
                discrepancies.Add(new Discrepancy(
                    path,
                    DiscrepancyKinds.Value,
                    _normaliser.ToValue(rule, baselineSlot, WireSyntax.Json),
                    _normaliser.ToValue(rule, observedSlot, WireSyntax.Json)));
            }

            return;
        }

        if (baselineKind == JsonValueKind.Null && observedKind == JsonValueKind.Null)
        {
            return;
        }

        if (baselineKind is not (JsonValueKind.String or JsonValueKind.Null)
            && KindClass(baselineKind) == KindClass(observedKind))
        {
            Walk(path, baseline, observed, type: null, discrepancies);
            return;
        }

        discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Type, MemberValue(rule, baseline), MemberValue(rule, observed)));
    }

    /// <summary>Compares two arrays: a count difference first, then the common prefix item by item, in order.</summary>
    private void WalkArray(string path, JsonElement baseline, JsonElement observed, string? type, List<Discrepancy> discrepancies)
    {
        var itemType = type is not null && _catalogue.TryGetArray(type, out var array) ? array.ItemType : null;

        var baselineCount = baseline.GetArrayLength();
        var observedCount = observed.GetArrayLength();
        if (baselineCount != observedCount)
        {
            discrepancies.Add(new Discrepancy(path, DiscrepancyKinds.Length, CountValue(baselineCount), CountValue(observedCount)));
        }

        var common = Math.Min(baselineCount, observedCount);
        for (var index = 0; index < common; index++)
        {
            Walk(IndexPath(path, index), baseline[index], observed[index], itemType, discrepancies);
        }
    }

    /// <summary>
    /// The DTO an object is walked as: null when <paramref name="type"/> is not a catalogue DTO; otherwise
    /// the class the baseline's first <c>__type</c> member names, when it is a string naming a catalogue
    /// DTO, and the declared class when it is not. A <c>Name:#Namespace</c> value is looked up by the text
    /// before its first colon.
    /// </summary>
    private DtoDefinition? ResolveRuntimeDto(MemberGroups baselineMembers, string? type)
    {
        if (type is null || !_catalogue.TryGetDto(type, out var declared))
        {
            return null;
        }

        var typeMembers = baselineMembers.ValuesOf(JadeWireFormats.TypeMemberName);
        if (typeMembers.Count > 0 && typeMembers[0].ValueKind == JsonValueKind.String)
        {
            var runtimeClass = typeMembers[0].GetString()!;
            var colon = runtimeClass.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                runtimeClass = runtimeClass[..colon];
            }

            if (_catalogue.TryGetDto(runtimeClass, out var runtime))
            {
                return runtime;
            }
        }

        return declared;
    }

    /// <summary>
    /// The type a member's value is walked as: the DTO name of a reference, the array name of a collection,
    /// and null for a primitive attribute or a member the class does not declare.
    /// </summary>
    private static string? MemberType(DtoDefinition? runtimeDto, string name)
    {
        if (runtimeDto is null || !runtimeDto.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.Kind switch
        {
            PropertyKind.Reference or PropertyKind.Collection => property.JadeType,
            _ => null,
        };
    }

    /// <summary>
    /// How a member value appears in a discrepancy. At a normalised position a string or null is shown
    /// through the normaliser, so a valid value is <c>{"valid":"N1"}</c> rather than its content; every
    /// other value is its compact literal.
    /// </summary>
    private DiscrepancyValue MemberValue(string? rule, JsonElement value)
    {
        if (rule is null)
        {
            return Compact(value);
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => _normaliser.ToValue(rule, NormalisedSlot.Of(value.GetString()!), WireSyntax.Json),
            JsonValueKind.Null => _normaliser.ToValue(rule, NormalisedSlot.Null, WireSyntax.Json),
            _ => Compact(value),
        };
    }

    /// <summary>The compact literal of a value: the deterministic re-encoding with the default encoder.</summary>
    /// <remarks>
    /// The output equals <c>JsonSerializer.Serialize(value)</c> and uses the same encoder as
    /// <see cref="Normaliser.ToValue"/>, so one string value always gives one signature token.
    /// </remarks>
    private static DiscrepancyValue Compact(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            value.WriteTo(writer);
        }

        return DiscrepancyValue.Literal(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static DiscrepancyValue CountValue(int count) =>
        DiscrepancyValue.Literal(count.ToString(CultureInfo.InvariantCulture));

    /// <summary>The kind two values must share to be compared by value; <c>true</c> and <c>false</c> are one kind.</summary>
    private static JsonValueKind KindClass(JsonValueKind kind) =>
        kind == JsonValueKind.False ? JsonValueKind.True : kind;

    private static string MemberPath(string objectPath, string name) =>
        IsIdentifier(name)
            ? objectPath + "." + name
            : objectPath + "['" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']";

    private static string IndexPath(string arrayPath, int index) =>
        arrayPath + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>True when <paramref name="name"/> matches <c>^[A-Za-z_][A-Za-z0-9_]*$</c>.</summary>
    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!(char.IsAsciiLetterOrDigit(name[index]) || name[index] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses with the default options (no comments, no trailing commas); null when the text is not JSON.</summary>
    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when every member name and string value under <paramref name="element"/> decodes to a string.</summary>
    private static bool IsDecodable(JsonElement element)
    {
        try
        {
            DecodeAll(element);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DecodeAll(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    _ = member.Name;
                    DecodeAll(member.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    DecodeAll(item);
                }

                break;

            case JsonValueKind.String:
                _ = element.GetString();
                break;
        }
    }

    /// <summary>
    /// The members of one object grouped by name, ordinally: names in first-occurrence document order and,
    /// for each name, its values in document order.
    /// </summary>
    private sealed class MemberGroups
    {
        private readonly Dictionary<string, List<JsonElement>> _values;

        private MemberGroups(List<string> names, Dictionary<string, List<JsonElement>> values)
        {
            Names = names;
            _values = values;
        }

        public IReadOnlyList<string> Names { get; }

        public static MemberGroups Of(JsonElement element)
        {
            var names = new List<string>();
            var values = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
            {
                if (!values.TryGetValue(member.Name, out var occurrences))
                {
                    occurrences = [];
                    values.Add(member.Name, occurrences);
                    names.Add(member.Name);
                }

                occurrences.Add(member.Value);
            }

            return new MemberGroups(names, values);
        }

        public bool Contains(string name) => _values.ContainsKey(name);

        public IReadOnlyList<JsonElement> ValuesOf(string name) =>
            _values.TryGetValue(name, out var occurrences) ? occurrences : Array.Empty<JsonElement>();
    }
}
