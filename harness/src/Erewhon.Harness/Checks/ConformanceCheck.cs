using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>
/// One inventoried member of the stand-in conformance check: a DTO property on one surface, or a status
/// constant of one surface, with the result the captured records gave it.
/// </summary>
/// <param name="ItemId">
/// The failure item id, <c>CONF:&lt;Rest|Soap&gt;:&lt;Class&gt;.&lt;property&gt;</c> or
/// <c>CONF:&lt;Rest|Soap&gt;:STATUS:&lt;constant&gt;</c>, spelled by <see cref="FailureItemIds"/>.
/// </param>
/// <param name="Surface"><c>Rest</c> or <c>Soap</c>.</param>
/// <param name="Class">
/// The DTO class for a property member, for example <c>DTOAgent</c>; for a constant, the JADE service
/// class that declares it (<c>ErewhonRestService</c> or <c>ErewhonWebService</c>).
/// </param>
/// <param name="Property">The property name for a property member; null for a constant.</param>
/// <param name="Constant">The constant name for a constant member; null for a property.</param>
/// <param name="Result">
/// <see cref="ConformanceResult.PassResult"/>, <c>unreachable-in-source</c>, <c>unobserved</c>,
/// <c>null-only</c> or <c>wrong-type:&lt;observed type&gt;</c>; a non-pass result is the failure item's
/// nature.
/// </param>
/// <param name="ObservedType">The wrong wire-type token of a <c>wrong-type</c> result; null otherwise.</param>
public sealed record ConformanceMember(
    string ItemId,
    string Surface,
    string Class,
    string? Property,
    string? Constant,
    string Result,
    string? ObservedType);

/// <summary>
/// One seed-token assertion on the captured REST <c>getAllClients</c> record (AAP 0.3.2.3).
/// </summary>
/// <param name="AssertionId">The assertion id, for example <c>pauline-wild-website</c>.</param>
/// <param name="ItemId">The failure item id <c>CONF:SEED:&lt;assertionId&gt;</c>.</param>
/// <param name="Expected">The expected value as compact JSON text, for example <c>""</c>.</param>
/// <param name="Observed">
/// The observed value as compact JSON text, or <c>absent</c> when the record, the client or the property
/// is missing.
/// </param>
/// <param name="Result">
/// <see cref="ConformanceResult.PassResult"/> when <paramref name="Observed"/> equals
/// <paramref name="Expected"/>, otherwise <c>differs:&lt;observed&gt;</c>, the failure item's nature.
/// </param>
public sealed record SeedAssertionResult(
    string AssertionId,
    string ItemId,
    string Expected,
    string Observed,
    string Result);

/// <summary>The counts of a conformance result, over its members only (seed assertions are separate).</summary>
/// <param name="MembersChecked">Every inventoried member, properties and constants of both surfaces.</param>
/// <param name="Passed">The members whose result is <see cref="ConformanceResult.PassResult"/>.</param>
/// <param name="GapsByNature">
/// The non-passing members per nature, always with the four keys <c>unreachable-in-source</c>,
/// <c>unobserved</c>, <c>null-only</c> and <c>wrong-type</c> (counted without its suffix), in that order.
/// </param>
public sealed record ConformanceCounts(int MembersChecked, int Passed, IReadOnlyDictionary<string, int> GapsByNature);

/// <summary>
/// The stand-in conformance check (AAP 0.7.8): over the records <c>capture</c> has just written, it decides
/// for every reachable catalogue member whether some captured response holds a non-null value of its
/// declared wire type, registers every unreachable member as a gap, checks that some case returns every
/// reachable status constant, and runs the seed-token assertions.
/// </summary>
/// <remarks>
/// <para>The check is pure: it reads no file and opens no connection, and every inventory, wire-type and
/// reachability fact comes from the embedded <see cref="OperationCatalogue"/>, never from the JADE
/// schema files. Bodies reach it through the caller's delegate.</para>
/// <para>The inventory is, in this order: every REST property of every catalogue DTO (flattened, so the
/// abstract <c>DTOObject</c> and every inherited <c>guid</c> are members), the REST constants, every SOAP
/// property the SOAP surface emits, and the SOAP constants. The same records always give byte-identical
/// <see cref="ConformanceResult.ToJsonNode"/> output.</para>
/// <para>An instance is immutable once constructed and <see cref="Run"/> keeps its state local, so one
/// instance may run concurrently.</para>
/// </remarks>
public sealed class ConformanceCheck
{
    /// <summary>The case whose captured REST <c>getAllClients</c> body the seed-token assertions read.</summary>
    public const string SeedAssertionCaseId = "REST-getAllClients-01";

    // DTOResult.statusCode carries the SOAP status constants (scm:1139-1146); the catalogue records which
    // body types carry a result status code, but not the property name.
    private const string ResultStatusCodeProperty = "statusCode";

    private const string AbsentObservation = "absent";

    // The SOAP 1.1 envelope's local names; they are evidence only in the catalogue's envelope namespace.
    private const string SoapEnvelopeElement = "Envelope";
    private const string SoapBodyElement = "Body";

    // Wrong wire-type tokens (AAP 0.7.7 nature wrong-type:<observed type>).
    private const string JsonObjectToken = "object";
    private const string JsonArrayToken = "array";
    private const string JsonStringToken = "string";
    private const string JsonNumberToken = "number";
    private const string JsonBooleanToken = "boolean";
    private const string XmlElementToken = "element";
    private const string XmlTextToken = "text";

    // Wrong wire-type tokens of a collection's contents: the offending item's own token after the item
    // prefix, or the name of an XML item element the array does not name after the item-name prefix (its
    // local name in the target namespace, its Clark name {namespace}local in any other).
    private const string CollectionItemPrefix = "item:";
    private const string CollectionItemNamePrefix = "item-name:";

    // Wrong wire-type tokens of a DTO element whose xsi:type names no catalogue DTO of the declared kind
    // in the target namespace: the Clark name {namespace}local its QName resolves to after the prefix, or
    // the unresolved token when it is no QName or its prefix is unbound. A Clark name always starts with
    // '{', so the two forms never collide.
    private const string XsiTypeTokenPrefix = "xsi-type:";
    private const string UnresolvedXsiTypeToken = XsiTypeTokenPrefix + "unresolved";

    // The JSON wire token each JADE primitive must carry in OperationCatalogue.WireTypes.
    private const string StringJsonToken = "string";
    private const string IntegerJsonToken = "integer";
    private const string DecimalJsonToken = "decimal";
    private const string MicrosoftDateJsonToken = "msDate";
    private const string ByteArrayJsonToken = "byteArray";

    private static readonly XNamespace XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XName XsiNil = XsiNamespace + "nil";
    private static readonly XName XsiType = XsiNamespace + "type";

    // The XML whitespace characters, which the whitespace facet of an xsd:QName value removes around it.
    private static readonly char[] XmlWhitespace = [' ', '\t', '\r', '\n'];

    // xsd:decimal admits a sign and a decimal point but no exponent or group separator; the whitespace
    // facet of XML Schema is collapse, so surrounding whitespace is allowed.
    private const NumberStyles XmlDecimalStyles =
        NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // A JADE Integer is 32-bit (xsd:int on SOAP), so its magnitude has at most ten decimal digits.
    private const int MaxIntegerDigits = 10;

    // A JSON exponent is read saturated at this magnitude: a number token is far shorter than it, so
    // beyond it only the exponent's sign decides whether the value is a whole number in range.
    private const long ExponentSaturation = 1_000_000_000_000_000;

    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Strict, so a raw body holding an invalid byte sequence is undecodable instead of being read with
    // U+FFFD in place of its bytes, which would let replacement text count as a valid string value.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The seed-token assertions of AAP 0.3.2.3: Pauline Wild's website is the seed literal `null`
    // (Erewhon/DataFiles/Clients.txt:1), which loads as the JADE null string, written "" in JSON; Brian
    // Olsen's email `bo@wol.com ` (Clients.txt:2) loses its trailing blank to the tokenizer.
    private static readonly SeedAssertion[] SeedAssertions =
    [
        new("pauline-wild-website", "Pauline Wild", "website", ""),
        new("brian-olsen-email", "Brian Olsen", "email", "bo@wol.com"),
    ];

    private readonly OperationCatalogue _catalogue;
    private readonly IReadOnlyList<InventoryEntry> _inventory;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _selfAndAncestors;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<DtoProperty>> _soapProperties;
    private readonly IReadOnlySet<string> _resultDtos;
    private readonly IReadOnlyDictionary<string, WireClass> _itemWires;

    // SOAP evidence is selected by expanded name: the envelope and its body in the envelope namespace,
    // and every message, result, property and array item element, and every xsi:type, in the target
    // namespace, both taken from the catalogue's SOAP address.
    private readonly XNamespace _targetNamespace;
    private readonly XName _soapEnvelope;
    private readonly XName _soapBody;
    private readonly XName _resultStatusCode;

    /// <summary>Builds the conformance inventory from <paramref name="catalogue"/>.</summary>
    /// <param name="catalogue">The operation catalogue, normally <see cref="OperationCatalogue.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// A catalogue fact the check relies on is inconsistent: a blank SOAP envelope or target namespace, a
    /// primitive property or array item type whose JADE type has no wire mapping or a mapping other than
    /// its type's, a SOAP property whose XML type disagrees with the mapping, a result DTO without an
    /// Integer <c>statusCode</c>, or a surface whose operations do not share one declaring class.
    /// </exception>
    public ConformanceCheck(OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        _catalogue = catalogue;
        _itemWires = catalogue.Arrays.ToDictionary(array => array.Name, ItemWireOf, StringComparer.Ordinal);

        var envelopeNamespace = SoapNamespace(catalogue.Addresses.Soap.EnvelopeNamespace, "envelopeNamespace");
        _targetNamespace = SoapNamespace(catalogue.Addresses.Soap.TargetNamespace, "targetNamespace");
        _soapEnvelope = envelopeNamespace + SoapEnvelopeElement;
        _soapBody = envelopeNamespace + SoapBodyElement;
        _resultStatusCode = _targetNamespace + ResultStatusCodeProperty;

        _selfAndAncestors = catalogue.Dtos.ToDictionary(
            dto => dto.Name,
            dto => (IReadOnlyList<string>)catalogue.Dtos
                .Where(candidate => catalogue.IsKindOf(dto.Name, candidate.Name))
                .Select(candidate => candidate.Name)
                .ToArray(),
            StringComparer.Ordinal);

        _soapProperties = catalogue.Dtos.ToDictionary(
            dto => dto.Name,
            dto => (IReadOnlyList<DtoProperty>)dto.Properties.Where(property => InSoapInventory(dto, property)).ToArray(),
            StringComparer.Ordinal);

        _resultDtos = ResolveResultDtos(catalogue);
        ValidateWireTypes();
        _inventory = BuildInventory();
    }

    /// <summary>
    /// Runs the check over captured records: walks every body that parses and decodes by its declared
    /// type, records each member observation, and decides every member, constant and seed-token assertion.
    /// A raw body that is not valid UTF-8 supplies no body observation, so every member only it would have
    /// evidenced stays a gap; a REST record still counts its status, which is not part of the body.
    /// </summary>
    /// <param name="records">The captured records, in replay order.</param>
    /// <param name="bodyBytes">
    /// Returns a record's raw body bytes; called only for a record whose <c>bodyText</c> is null, whose
    /// body is kept in its <c>.body.bin</c> file: a <c>getPhoto</c> record, or any record whose body was not
    /// reproducible as text.
    /// </param>
    /// <returns>The conformance result, members in inventory order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> holds a null entry.</exception>
    /// <exception cref="InvalidDataException">A record names an operation the catalogue does not define.</exception>
    public ConformanceResult Run(IEnumerable<BaselineRecord> records, Func<BaselineRecord, byte[]> bodyBytes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(bodyBytes);

        var observations = new Observations(_inventory);
        BaselineRecord? seedRecord = null;
        string? seedBody = null;

        foreach (var record in records)
        {
            if (record is null)
            {
                throw new ArgumentException("The record list contains a null entry.", nameof(records));
            }

            if (!_catalogue.TryGetById(record.Operation, out var operation))
            {
                throw new InvalidDataException(
                    $"Baseline record {record.CaseId} names operation '{record.Operation}', which the operation catalogue does not define.");
            }

            // Null when the raw body cannot be decoded; the seed-token assertions then observe it as absent.
            var body = BodyText(record, bodyBytes);
            if (operation.Surface == Surface.Rest)
            {
                observations.RestStatuses.Add(record.Response.Status);
                if (body is not null)
                {
                    ObserveRest(operation, record.Response.Status, body, observations);
                }

                if (seedRecord is null && string.Equals(record.CaseId, SeedAssertionCaseId, StringComparison.Ordinal))
                {
                    seedRecord = record;
                    seedBody = body;
                }
            }
            else if (body is not null)
            {
                ObserveSoap(operation, record.Response.Status, body, observations);
            }
        }

        var members = new List<ConformanceMember>(_inventory.Count);
        foreach (var entry in _inventory)
        {
            members.Add(Decide(entry, observations));
        }

        return new ConformanceResult(members, RunSeedAssertions(seedRecord is null ? null : seedBody));
    }

    /// <summary>
    /// The response body of <paramref name="record"/> as text with a leading byte-order mark removed, or
    /// null when its raw bytes are not valid UTF-8. A stored <c>bodyText</c> is used as it is. Raw bytes
    /// from <paramref name="bodyBytes"/> are decoded strictly after a UTF-8 byte-order mark is stripped, so
    /// a stray, truncated or overlong sequence or an encoded surrogate leaves the body undecodable instead
    /// of turning into U+FFFD, which would parse and could count as a valid string or DTO observation.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="bodyBytes"/> returned null.</exception>
    private static string? BodyText(BaselineRecord record, Func<BaselineRecord, byte[]> bodyBytes)
    {
        string text;
        if (record.Response.BodyText is { } stored)
        {
            text = stored;
        }
        else
        {
            var bytes = bodyBytes(record)
                ?? throw new InvalidOperationException($"The body delegate returned null for baseline record {record.CaseId}.");
            ReadOnlySpan<byte> raw = bytes;
            ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
            try
            {
                text = StrictUtf8.GetString(raw.StartsWith(utf8Bom) ? raw[utf8Bom.Length..] : raw);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        // A stored body keeps a byte-order mark as U+FEFF, which no JSON or XML parser accepts as content.
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    // Inventory.

    private bool InSoapInventory(DtoDefinition dto, DtoProperty property)
    {
        // DTOResult is absent from the WebServiceHTTP exposure list but written over SOAP
        // (KL-SOAP-DTORESULT); the catalogue marks that assumed exposure by a null exposure line.
        var assumedExposure = dto.SoapExposed && dto.SoapExposureLine is null;
        return (property.SoapExposed || assumedExposure)
            && !string.Equals(property.Reachability?.For(Surface.Soap), ReachabilityValues.NotExposed, StringComparison.Ordinal);
    }

    private List<InventoryEntry> BuildInventory()
    {
        var inventory = new List<InventoryEntry>();
        foreach (var surface in new[] { Surface.Rest, Surface.Soap })
        {
            foreach (var dto in _catalogue.Dtos)
            {
                var properties = surface == Surface.Rest ? dto.Properties : _soapProperties[dto.Name];
                foreach (var property in properties)
                {
                    inventory.Add(new InventoryEntry(
                        surface,
                        dto.Name,
                        property.Name,
                        null,
                        FailureItemIds.Conformance(surface, dto.Name, property.Name),
                        property.Reachability?.For(surface) ?? ReachabilityValues.Reachable));
                }
            }

            var serviceClass = ServiceClassOf(surface);
            foreach (var constant in _catalogue.GetConstants(surface))
            {
                inventory.Add(new InventoryEntry(
                    surface,
                    serviceClass,
                    null,
                    constant,
                    FailureItemIds.ConformanceStatus(surface, constant.Name),
                    constant.Reachability));
            }
        }

        return inventory;
    }

    private string ServiceClassOf(Surface surface)
    {
        var classes = _catalogue.GetBySurface(surface).Select(operation => operation.JadeClass).Distinct(StringComparer.Ordinal).ToArray();
        return classes.Length == 1
            ? classes[0]
            : throw new InvalidDataException(
                $"operation-catalogue.json: the {SurfaceToken(surface)} operations must share one declaring class, found {classes.Length}.");
    }

    private static HashSet<string> ResolveResultDtos(OperationCatalogue catalogue)
    {
        var names = catalogue.GetBySurface(Surface.Soap)
            .SelectMany(operation => operation.Statuses)
            .Where(status => status.ResultStatusCode is not null)
            .Select(status => status.BodyType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (!catalogue.TryGetDto(name, out var dto)
                || !dto.TryGetProperty(ResultStatusCodeProperty, out var statusCode)
                || !string.Equals(statusCode.JadeType, "Integer", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"operation-catalogue.json: SOAP result type '{name}' carries a result status code but is not a DTO with an Integer '{ResultStatusCodeProperty}'.");
            }
        }

        return names;
    }

    private void ValidateWireTypes()
    {
        foreach (var dto in _catalogue.Dtos)
        {
            foreach (var property in dto.Properties)
            {
                var wire = WireOf(property);
                if (wire is WireClass.Array or WireClass.Dto)
                {
                    continue;
                }

                var expectedJson = ExpectedJsonToken(wire);
                if (!_catalogue.WireTypes.TryGetValue(property.JadeType, out var mapping)
                    || !string.Equals(mapping.Json, expectedJson, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"operation-catalogue.json: {dto.Name}.{property.Name} of type {property.JadeType} needs the JSON wire form '{expectedJson}'.");
                }

                if (_soapProperties[dto.Name].Contains(property)
                    && !string.Equals(property.XmlType, mapping.Xml, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"operation-catalogue.json: {dto.Name}.{property.Name} has XML type '{property.XmlType}', but {property.JadeType} maps to '{mapping.Xml}'.");
                }
            }
        }

        foreach (var array in _catalogue.Arrays)
        {
            var wire = _itemWires[array.Name];
            if (wire is WireClass.Array or WireClass.Dto)
            {
                continue;
            }

            var expectedJson = ExpectedJsonToken(wire);
            if (!_catalogue.WireTypes.TryGetValue(array.ItemType, out var mapping)
                || !string.Equals(mapping.Json, expectedJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"operation-catalogue.json: the items of array '{array.Name}', of type {array.ItemType}, need the JSON wire form '{expectedJson}'.");
            }
        }
    }

    private WireClass WireOf(DtoProperty property)
    {
        if (property.Kind == PropertyKind.Collection || _catalogue.TryGetArray(property.JadeType, out _))
        {
            return WireClass.Array;
        }

        if (property.Kind == PropertyKind.Reference || _catalogue.TryGetDto(property.JadeType, out _))
        {
            return WireClass.Dto;
        }

        return PrimitiveWireOf(property.JadeType)
            ?? throw new InvalidDataException(
                $"operation-catalogue.json: property '{property.Name}' declared in {property.DeclaredIn} has JADE type '{property.JadeType}', which is no primitive wire type.");
    }

    /// <summary>
    /// The wire class every item of <paramref name="array"/> must have, mapped from its item type as
    /// <see cref="WireOf"/> maps a property's JADE type.
    /// </summary>
    private WireClass ItemWireOf(ArrayDefinition array)
    {
        if (_catalogue.TryGetArray(array.ItemType, out _))
        {
            return WireClass.Array;
        }

        if (_catalogue.TryGetDto(array.ItemType, out _))
        {
            return WireClass.Dto;
        }

        return PrimitiveWireOf(array.ItemType)
            ?? throw new InvalidDataException(
                $"operation-catalogue.json: array '{array.Name}' has item type '{array.ItemType}', which is no primitive wire type.");
    }

    private static WireClass? PrimitiveWireOf(string jadeType) => jadeType switch
    {
        "String" => WireClass.String,
        "Integer" => WireClass.Integer,
        "Decimal" => WireClass.Decimal,
        "Date" => WireClass.Date,
        "TimeStamp" => WireClass.TimeStamp,
        "Binary" => WireClass.Binary,
        _ => null,
    };

    private static string ExpectedJsonToken(WireClass wire) => wire switch
    {
        WireClass.String => StringJsonToken,
        WireClass.Integer => IntegerJsonToken,
        WireClass.Decimal => DecimalJsonToken,
        WireClass.Date or WireClass.TimeStamp => MicrosoftDateJsonToken,
        WireClass.Binary => ByteArrayJsonToken,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, "Not a primitive wire class."),
    };

    /// <summary>
    /// The type a body is walked as: the body type of the operation's first status with the record's HTTP
    /// status (the SOAP nil outcome skipped, since an <c>xsi:nil</c> result is detected from the body), null
    /// for the REST literal-null body, and the declared return type when no status matches.
    /// </summary>
    private static string? DeclaredRootType(OperationDescriptor operation, int status)
    {
        foreach (var candidate in operation.Statuses)
        {
            if (candidate.HttpStatus == status && !candidate.IsNilBody)
            {
                return candidate.IsNullBody ? null : candidate.BodyType;
            }
        }

        return operation.ReturnType;
    }

    // REST: JSON bodies.

    private void ObserveRest(OperationDescriptor operation, int status, string body, Observations observations)
    {
        var rootType = DeclaredRootType(operation, status);
        if (rootType is null)
        {
            return;
        }

        using var document = TryParseJson(body);
        if (document is not null && document.RootElement.ValueKind != JsonValueKind.Null)
        {
            WalkJson(document.RootElement, rootType, observations);
        }
    }

    /// <summary>
    /// The parsed body, or null when it is no JSON text or holds text that cannot be decoded. A raw
    /// unpaired surrogate fails transcoding with an <see cref="ArgumentException"/> rather than a
    /// <see cref="JsonException"/>, and an escaped one such as <c>"\uD800"</c> parses but throws
    /// <see cref="InvalidOperationException"/> once the name or string holding it is read. Both are
    /// treated exactly as an unparseable body, decided before anything of the body is read, so such a
    /// body contributes no observation and the check never aborts part-way.
    /// </summary>
    private static JsonDocument? TryParseJson(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (IsDecodable(document.RootElement))
        {
            return document;
        }

        document.Dispose();
        return null;
    }

    /// <summary>True when every property name and string value at or below <paramref name="element"/> decodes.</summary>
    private static bool IsDecodable(JsonElement element)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Decodes(element);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!IsDecodable(item))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (!Decodes(member) || !IsDecodable(member.Value))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static bool Decodes(JsonElement text)
    {
        try
        {
            _ = text.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Decodes(JsonProperty member)
    {
        try
        {
            _ = member.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void WalkJson(JsonElement element, string type, Observations observations)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        if (_catalogue.TryGetArray(type, out var array))
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Null)
                    {
                        WalkJson(item, array.ItemType, observations);
                    }
                }
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object || !_catalogue.TryGetDto(type, out var declared))
        {
            return;
        }

        var runtime = RuntimeDtoJson(element, declared);
        var members = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
        {
            if (!members.TryGetValue(member.Name, out var values))
            {
                values = [];
                members.Add(member.Name, values);
            }

            values.Add(member.Value);
        }

        foreach (var property in runtime.Properties)
        {
            if (!members.TryGetValue(property.Name, out var values))
            {
                continue;
            }

            var wire = WireOf(property);
            foreach (var value in values)
            {
                if (value.ValueKind == JsonValueKind.Null)
                {
                    Record(observations, Surface.Rest, runtime.Name, property.Name, Outcome.Null, null);
                    continue;
                }

                if (!IsJsonWire(value, wire))
                {
                    Record(observations, Surface.Rest, runtime.Name, property.Name, Outcome.Wrong, JsonToken(value));
                    continue;
                }

                // Invalid contents make the collection itself wrong, and none of its items is walked.
                if (wire == WireClass.Array
                    && _catalogue.TryGetArray(property.JadeType, out var collection)
                    && JsonItemsWrongToken(value, collection) is { } itemsWrong)
                {
                    Record(observations, Surface.Rest, runtime.Name, property.Name, Outcome.Wrong, itemsWrong);
                    continue;
                }

                Record(observations, Surface.Rest, runtime.Name, property.Name, Outcome.Valid, null);
                if (wire is WireClass.Dto or WireClass.Array)
                {
                    WalkJson(value, property.JadeType, observations);
                }
            }
        }
    }

    /// <summary>
    /// The class an object is walked as: the class its first <c>__type</c> member names (text before a
    /// <c>:</c>, as in the <c>Name:#Namespace</c> form) when that is a catalogue DTO of the declared kind,
    /// otherwise the declared class. Members unknown to the class, <c>__type</c> included, are ignored.
    /// </summary>
    private DtoDefinition RuntimeDtoJson(JsonElement element, DtoDefinition declared)
    {
        foreach (var member in element.EnumerateObject())
        {
            if (!string.Equals(member.Name, JadeWireFormats.TypeMemberName, StringComparison.Ordinal))
            {
                continue;
            }

            if (member.Value.ValueKind != JsonValueKind.String)
            {
                return declared;
            }

            var name = member.Value.GetString()!;
            var colon = name.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                name = name[..colon];
            }

            return _catalogue.TryGetDto(name, out var runtime) && _catalogue.IsKindOf(runtime.Name, declared.Name)
                ? runtime
                : declared;
        }

        return declared;
    }

    private static bool IsJsonWire(JsonElement value, WireClass wire) => wire switch
    {
        WireClass.String => value.ValueKind == JsonValueKind.String,
        WireClass.Integer => value.ValueKind == JsonValueKind.Number && IsJsonInteger(value.GetRawText()),
        WireClass.Decimal => value.ValueKind == JsonValueKind.Number,
        WireClass.Date or WireClass.TimeStamp => JadeWireFormats.IsMicrosoftJsonDate(value),
        WireClass.Binary => JadeWireFormats.TryReadByteArray(value, out _),
        WireClass.Dto => value.ValueKind == JsonValueKind.Object,
        WireClass.Array => value.ValueKind == JsonValueKind.Array,
        _ => false,
    };

    /// <summary>
    /// The wrong wire-type token of a JSON array's contents: <c>item:</c> and the token of an item that
    /// lacks the array's item wire type (an object for a DTO item type, the primitive's own form for a
    /// primitive one, and the same rule inside an array item), the ordinal-smallest when several do; null
    /// when every non-null item has it.
    /// </summary>
    private string? JsonItemsWrongToken(JsonElement collection, ArrayDefinition array)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        var itemWire = _itemWires[array.Name];
        string? wrong = null;
        foreach (var item in collection.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            string? token = null;
            if (!IsJsonWire(item, itemWire))
            {
                token = JsonToken(item);
            }
            else if (itemWire == WireClass.Array && _catalogue.TryGetArray(array.ItemType, out var nested))
            {
                token = JsonItemsWrongToken(item, nested);
            }

            if (token is not null)
            {
                wrong = OrdinalSmallest(wrong, CollectionItemPrefix + token);
            }
        }

        return wrong;
    }

    /// <summary>
    /// True when a JSON number token is exactly a whole number within the 32-bit JADE Integer range. The
    /// value is decided from the token's own digits and exponent, never through a decimal or double
    /// conversion that could round a fraction away: <c>1.0</c>, <c>1e2</c>, <c>10e-1</c> and
    /// <c>2.147483647e9</c> qualify, while <c>2147483648</c>, <c>1.5</c>, <c>1E-1</c> and
    /// <c>200.00000000000000000000000000001</c> do not. Exponents of any length are compared without
    /// overflow.
    /// </summary>
    private static bool IsJsonInteger(ReadOnlySpan<char> token)
    {
        var index = 0;
        var negative = index < token.Length && token[index] == '-';
        if (negative)
        {
            index++;
        }

        var integerStart = index;
        while (index < token.Length && char.IsAsciiDigit(token[index]))
        {
            index++;
        }

        var integerDigits = token[integerStart..index];
        var fractionDigits = ReadOnlySpan<char>.Empty;
        if (index < token.Length && token[index] == '.')
        {
            var fractionStart = ++index;
            while (index < token.Length && char.IsAsciiDigit(token[index]))
            {
                index++;
            }

            fractionDigits = token[fractionStart..index];
            if (fractionDigits.IsEmpty)
            {
                return false;
            }
        }

        long exponent = 0;
        if (index < token.Length && token[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = false;
            if (index < token.Length && token[index] is '+' or '-')
            {
                exponentNegative = token[index] == '-';
                index++;
            }

            var exponentStart = index;
            while (index < token.Length && char.IsAsciiDigit(token[index]))
            {
                exponent = Math.Min(exponent * 10 + (token[index] - '0'), ExponentSaturation);
                index++;
            }

            if (index == exponentStart)
            {
                return false;
            }

            exponent = exponentNegative ? -exponent : exponent;
        }

        if (integerDigits.IsEmpty || index != token.Length)
        {
            return false;
        }

        // The value is digits x 10^(exponent - fraction length), digits being the integer and fraction
        // digits together. Without its leading zeros and with its trailing zeros moved into the exponent,
        // the significand ends in a non-zero digit, so the value is whole exactly when that exponent is
        // not negative.
        var digits = string.Concat(integerDigits, fractionDigits).AsSpan().TrimStart('0');
        if (digits.IsEmpty)
        {
            // Zero, whatever its sign, fraction or exponent.
            return true;
        }

        var significand = digits.TrimEnd('0');
        var scale = exponent - fractionDigits.Length + (digits.Length - significand.Length);
        if (scale < 0 || significand.Length + scale > MaxIntegerDigits)
        {
            return false;
        }

        long magnitude = 0;
        foreach (var digit in significand)
        {
            magnitude = magnitude * 10 + (digit - '0');
        }

        for (var power = 0L; power < scale; power++)
        {
            magnitude *= 10;
        }

        return magnitude <= (negative ? -(long)int.MinValue : int.MaxValue);
    }

    private static string JsonToken(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => JsonObjectToken,
        JsonValueKind.Array => JsonArrayToken,
        JsonValueKind.String => JsonStringToken,
        JsonValueKind.Number => JsonNumberToken,
        JsonValueKind.True or JsonValueKind.False => JsonBooleanToken,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.ValueKind, "A null or undefined value has no wire-type token."),
    };

    // SOAP: XML envelopes.

    /// <summary>A namespace of the catalogue's SOAP address, which must not be blank.</summary>
    private static XNamespace SoapNamespace(string uri, string key) =>
        string.IsNullOrWhiteSpace(uri)
            ? throw new InvalidDataException(
                $"operation-catalogue.json: addresses.soap.{key} must name the namespace SOAP evidence is selected by.")
            : XNamespace.Get(uri);

    /// <summary>
    /// Observes one SOAP record. Elements are selected by expanded name, so prefix choice is irrelevant
    /// and an element in any other namespace is not evidence: the root must be <c>Envelope</c> and its
    /// child <c>Body</c> in the envelope namespace, and the <c>&lt;op&gt;Response</c> wrapper, its
    /// <c>&lt;op&gt;Result</c> and the result's <c>statusCode</c> must be in the target namespace. A body
    /// that is no such envelope, a nil result, and a DTO result whose <c>xsi:type</c> is invalid (see
    /// <see cref="TryRuntimeDtoXml"/>) give no observation and no status code.
    /// </summary>
    private void ObserveSoap(OperationDescriptor operation, int status, string body, Observations observations)
    {
        var rootType = DeclaredRootType(operation, status);
        if (rootType is null || operation.Soap is not { } binding || TryLoadXml(body)?.Root is not { } envelope)
        {
            return;
        }

        if (envelope.Name != _soapEnvelope
            || envelope.Element(_soapBody) is not { } soapBody
            || soapBody.Element(_targetNamespace + binding.ResponseElement) is not { } wrapper
            || wrapper.Element(_targetNamespace + binding.ResultElement) is not { } result
            || IsNil(result))
        {
            return;
        }

        WalkXml(result, rootType, observations);

        if (_catalogue.TryGetDto(rootType, out var declared)
            && TryRuntimeDtoXml(result, declared, out var runtime, out _)
            && _resultDtos.Any(resultDto => _catalogue.IsKindOf(runtime.Name, resultDto)))
        {
            foreach (var statusCode in result.Elements(_resultStatusCode))
            {
                if (!IsNil(statusCode)
                    && !statusCode.HasElements
                    && TryParseXmlInteger(statusCode.Value, out var code))
                {
                    observations.SoapStatusCodes.Add(code);
                }
            }
        }
    }

    private void WalkXml(XElement element, string type, Observations observations)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        if (_catalogue.TryGetArray(type, out var array))
        {
            // Only elements the array names as its items, in the target namespace, are items; any other
            // child element, and an item not of the item wire type, gives no observation.
            var itemWire = _itemWires[array.Name];
            foreach (var item in element.Elements())
            {
                if (IsSoapItem(item, array) && !IsNil(item) && IsXmlWire(item, itemWire))
                {
                    WalkXml(item, array.ItemType, observations);
                }
            }

            return;
        }

        // A DTO element whose xsi:type is invalid gives no observation of any of its members.
        if (!_catalogue.TryGetDto(type, out var declared) || !TryRuntimeDtoXml(element, declared, out var runtime, out _))
        {
            return;
        }

        foreach (var property in _soapProperties[runtime.Name])
        {
            var wire = WireOf(property);

            // A property element in any namespace but the target namespace is not this property.
            foreach (var value in element.Elements(_targetNamespace + property.Name))
            {
                if (IsNil(value))
                {
                    Record(observations, Surface.Soap, runtime.Name, property.Name, Outcome.Null, null);
                    continue;
                }

                if (!IsXmlWire(value, wire))
                {
                    Record(observations, Surface.Soap, runtime.Name, property.Name, Outcome.Wrong, value.HasElements ? XmlElementToken : XmlTextToken);
                    continue;
                }

                // A nested DTO whose xsi:type is invalid is no value of the property's type.
                if (wire == WireClass.Dto
                    && _catalogue.TryGetDto(property.JadeType, out var valueType)
                    && !TryRuntimeDtoXml(value, valueType, out _, out var typeWrong))
                {
                    Record(observations, Surface.Soap, runtime.Name, property.Name, Outcome.Wrong, typeWrong);
                    continue;
                }

                // Invalid contents make the collection itself wrong, and none of its items is walked.
                if (wire == WireClass.Array
                    && _catalogue.TryGetArray(property.JadeType, out var collection)
                    && XmlItemsWrongToken(value, collection) is { } itemsWrong)
                {
                    Record(observations, Surface.Soap, runtime.Name, property.Name, Outcome.Wrong, itemsWrong);
                    continue;
                }

                Record(observations, Surface.Soap, runtime.Name, property.Name, Outcome.Valid, null);
                if (wire is WireClass.Dto or WireClass.Array)
                {
                    WalkXml(value, property.JadeType, observations);
                }
            }
        }
    }

    /// <summary>
    /// The class a DTO element is walked as. Without <c>xsi:type</c> it is the declared class. An
    /// <c>xsi:type</c> is an XML Schema QName, read with surrounding XML whitespace removed: a prefixed name
    /// resolves through the prefix bound in scope at the element, and an unprefixed name through the
    /// in-scope default namespace, or to no namespace when none is declared. It switches the class only
    /// when its expanded name is in the target namespace and its local name is a catalogue DTO of the
    /// declared kind, the declared class itself included. Any other <c>xsi:type</c> (no QName, an unbound
    /// prefix, another namespace, an unknown class, or a class not of the declared kind) makes the element
    /// an invalid SOAP structure, which is never walked as the declared class and gives no pass.
    /// </summary>
    /// <param name="element">The DTO element.</param>
    /// <param name="declared">The class its position declares.</param>
    /// <param name="runtime">The class to walk the element as, when the method returns true.</param>
    /// <param name="wrongToken">
    /// When the method returns false, the element's wrong wire-type token: <c>xsi-type:</c> and the Clark
    /// name <c>{namespace}local</c> the QName resolves to, or <c>xsi-type:unresolved</c> when it does not
    /// resolve.
    /// </param>
    /// <returns>True when the element has no <c>xsi:type</c> or a valid one.</returns>
    private bool TryRuntimeDtoXml(
        XElement element,
        DtoDefinition declared,
        [NotNullWhen(true)] out DtoDefinition? runtime,
        [NotNullWhen(false)] out string? wrongToken)
    {
        runtime = null;
        wrongToken = null;
        if (element.Attribute(XsiType) is not { } typeAttribute)
        {
            runtime = declared;
            return true;
        }

        var text = typeAttribute.Value.Trim(XmlWhitespace);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var local = colon < 0 ? text : text[(colon + 1)..];
        XNamespace? typeNamespace;
        if (colon < 0)
        {
            typeNamespace = element.GetDefaultNamespace();
        }
        else
        {
            var prefix = text[..colon];
            typeNamespace = IsNcName(prefix) ? element.GetNamespaceOfPrefix(prefix) : null;
        }

        if (typeNamespace is null || !IsNcName(local))
        {
            wrongToken = UnresolvedXsiTypeToken;
            return false;
        }

        if (typeNamespace == _targetNamespace
            && _catalogue.TryGetDto(local, out var named)
            && _catalogue.IsKindOf(named.Name, declared.Name))
        {
            runtime = named;
            return true;
        }

        wrongToken = XsiTypeTokenPrefix + ClarkName(typeNamespace + local);
        return false;
    }

    private static bool IsXmlWire(XElement value, WireClass wire)
    {
        if (wire is WireClass.Dto or WireClass.Array)
        {
            return !value.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));
        }

        if (value.HasElements)
        {
            return false;
        }

        var text = value.Value;
        return wire switch
        {
            WireClass.String => true,
            WireClass.Integer => TryParseXmlInteger(text, out _),
            WireClass.Decimal => decimal.TryParse(text, XmlDecimalStyles, CultureInfo.InvariantCulture, out _),
            WireClass.Date => JadeWireFormats.IsXsdDate(text),
            WireClass.TimeStamp => JadeWireFormats.IsXsdDateTime(text),
            WireClass.Binary => JadeWireFormats.TryParseBase64Binary(text, out _),
            _ => false,
        };
    }

    /// <summary>
    /// Parses XML text as a JADE Integer, whose SOAP form is <c>xsd:int</c>: an optionally signed decimal
    /// integer within the 32-bit range, surrounding whitespace allowed.
    /// </summary>
    private static bool TryParseXmlInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// The wrong wire-type token of an XML collection's contents, the ordinal-smallest when several items
    /// offend; null when every child element is the array's SOAP item element in the target namespace and
    /// is <c>xsi:nil</c> or of the item wire type. An item named otherwise, every item when the array has
    /// no SOAP item element, gives <c>item-name:&lt;local name&gt;</c> when it is in the target namespace
    /// and <c>item-name:{namespace}local</c> (<c>{}local</c> for no namespace) when it is not, so an item
    /// whose local name matches in another namespace is never a valid item; a primitive item holding child
    /// elements gives <c>item:element</c>; a primitive item whose text is not of the item type, or a DTO or
    /// array item holding non-whitespace text, gives <c>item:text</c>; a DTO item whose <c>xsi:type</c> is
    /// invalid gives <c>item:</c> and its <c>xsi-type:</c> token; an array item whose own contents offend
    /// gives <c>item:</c> and their token.
    /// </summary>
    private string? XmlItemsWrongToken(XElement collection, ArrayDefinition array)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        var itemWire = _itemWires[array.Name];
        string? wrong = null;
        foreach (var item in collection.Elements())
        {
            string? token = null;
            if (!IsSoapItem(item, array))
            {
                token = CollectionItemNamePrefix
                    + (item.Name.Namespace == _targetNamespace ? item.Name.LocalName : ClarkName(item.Name));
            }
            else if (IsNil(item))
            {
                continue;
            }
            else if (!IsXmlWire(item, itemWire))
            {
                token = CollectionItemPrefix
                    + (itemWire is not (WireClass.Dto or WireClass.Array) && item.HasElements ? XmlElementToken : XmlTextToken);
            }
            else if (itemWire == WireClass.Dto
                && _catalogue.TryGetDto(array.ItemType, out var itemType)
                && !TryRuntimeDtoXml(item, itemType, out _, out var typeWrong))
            {
                token = CollectionItemPrefix + typeWrong;
            }
            else if (itemWire == WireClass.Array
                && _catalogue.TryGetArray(array.ItemType, out var nested)
                && XmlItemsWrongToken(item, nested) is { } nestedWrong)
            {
                token = CollectionItemPrefix + nestedWrong;
            }

            if (token is not null)
            {
                wrong = OrdinalSmallest(wrong, token);
            }
        }

        return wrong;
    }

    /// <summary>True when <paramref name="element"/> is the array's SOAP item element in the target namespace.</summary>
    private bool IsSoapItem(XElement element, ArrayDefinition array) =>
        array.SoapItemElement is { } itemElement && element.Name == _targetNamespace + itemElement;

    private static bool IsNil(XElement element) =>
        element.Attribute(XsiNil)?.Value.Trim() is "true" or "1";

    /// <summary>An expanded name in Clark notation, <c>{namespace}local</c>, with <c>{}</c> for no namespace.</summary>
    private static string ClarkName(XName name) => "{" + name.NamespaceName + "}" + name.LocalName;

    private static bool IsNcName(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(XmlConvert.VerifyNCName(text), text, StringComparison.Ordinal);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Loads XML with DTDs prohibited, no resolver and whitespace kept; null when it does not load.</summary>
    private static XDocument? TryLoadXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // Observations and results.

    // The token a collection reports among several offending items, chosen as a Tally chooses among
    // several wrong values, so it does not depend on item order.
    private static string OrdinalSmallest(string? current, string candidate) =>
        current is null || string.CompareOrdinal(candidate, current) < 0 ? candidate : current;

    /// <summary>
    /// Records an observation of <paramref name="property"/> on runtime class <paramref name="runtimeClass"/>
    /// for that class and for every catalogue ancestor whose inventory on the surface holds the property,
    /// so inherited members such as <c>DTOObject.guid</c> or <c>DTOItem.price</c> are exercised through
    /// their subclasses.
    /// </summary>
    private void Record(Observations observations, Surface surface, string runtimeClass, string property, Outcome outcome, string? token)
    {
        foreach (var owner in _selfAndAncestors[runtimeClass])
        {
            if (observations.Tallies.TryGetValue(new MemberKey(surface, owner, property), out var tally))
            {
                tally.Add(outcome, token);
            }
        }
    }

    private static ConformanceMember Decide(InventoryEntry entry, Observations observations)
    {
        string result;
        string? observedType = null;
        if (string.Equals(entry.Reachability, ReachabilityValues.UnreachableInSource, StringComparison.Ordinal))
        {
            // Never closed by an invented response path, whatever the records hold (AAP 0.7.8).
            result = FailureItemIds.UnreachableInSource;
        }
        else if (entry.Constant is { } constant)
        {
            var returned = constant.Surface == Surface.Rest
                ? observations.RestStatuses.Contains(constant.Value)
                : observations.SoapStatusCodes.Contains(constant.Value);
            result = returned ? ConformanceResult.PassResult : FailureItemIds.Unobserved;
        }
        else
        {
            var tally = observations.Tallies[new MemberKey(entry.Surface, entry.Class, entry.Property!)];
            if (tally.WrongToken is { } wrong)
            {
                observedType = wrong;
                result = FailureItemIds.WrongType(wrong);
            }
            else if (tally.SawValid)
            {
                result = ConformanceResult.PassResult;
            }
            else
            {
                result = tally.SawNull ? FailureItemIds.NullOnly : FailureItemIds.Unobserved;
            }
        }

        return new ConformanceMember(
            entry.ItemId,
            SurfaceToken(entry.Surface),
            entry.Class,
            entry.Property,
            entry.Constant?.Name,
            result,
            observedType);
    }

    private static List<SeedAssertionResult> RunSeedAssertions(string? body)
    {
        var document = body is null ? null : TryParseJson(body);
        using (document)
        {
            var results = new List<SeedAssertionResult>(SeedAssertions.Length);
            foreach (var assertion in SeedAssertions)
            {
                var expected = CompactJson(assertion.ExpectedValue);
                var observed = document is null ? AbsentObservation : ObserveSeed(document.RootElement, assertion);
                var result = string.Equals(observed, expected, StringComparison.Ordinal)
                    ? ConformanceResult.PassResult
                    : FailureItemIds.Differs(observed);
                results.Add(new SeedAssertionResult(assertion.Id, FailureItemIds.Seed(assertion.Id), expected, observed, result));
            }

            return results;
        }
    }

    /// <summary>
    /// The compact JSON of the asserted property of the first client object whose first <c>name</c> member
    /// is exactly the asserted client name; <c>absent</c> when the body is no array or no such client or
    /// property exists.
    /// </summary>
    private static string ObserveSeed(JsonElement root, SeedAssertion assertion)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return AbsentObservation;
        }

        foreach (var client in root.EnumerateArray())
        {
            if (client.ValueKind != JsonValueKind.Object
                || FirstMember(client, "name") is not { ValueKind: JsonValueKind.String } name
                || !string.Equals(name.GetString(), assertion.ClientName, StringComparison.Ordinal))
            {
                continue;
            }

            return FirstMember(client, assertion.Property) is { } value ? CompactJson(value) : AbsentObservation;
        }

        return AbsentObservation;
    }

    private static JsonElement? FirstMember(JsonElement element, string name)
    {
        foreach (var member in element.EnumerateObject())
        {
            if (string.Equals(member.Name, name, StringComparison.Ordinal))
            {
                return member.Value;
            }
        }

        return null;
    }

    private static string CompactJson(JsonElement value) =>
        WriteCompact(writer =>
        {
            // A string is re-encoded from its decoded value, so the text does not depend on how the body
            // escaped it.
            if (value.ValueKind == JsonValueKind.String)
            {
                writer.WriteStringValue(value.GetString());
            }
            else
            {
                value.WriteTo(writer);
            }
        });

    private static string CompactJson(string value) => WriteCompact(writer => writer.WriteStringValue(value));

    private static string WriteCompact(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CompactWriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // An explicit switch rather than Surface.ToString(), matching FailureItemIds, so renaming an enum
    // member cannot change a surface value baseline/conformance.json already holds.
    private static string SurfaceToken(Surface surface) => surface switch
    {
        Surface.Rest => "Rest",
        Surface.Soap => "Soap",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Not a defined surface."),
    };

    private enum WireClass
    {
        String,
        Integer,
        Decimal,
        Date,
        TimeStamp,
        Binary,
        Dto,
        Array,
    }

    private enum Outcome
    {
        Valid,
        Null,
        Wrong,
    }

    private readonly record struct MemberKey(Surface Surface, string Class, string Property);

    private sealed record InventoryEntry(
        Surface Surface,
        string Class,
        string? Property,
        StatusConstant? Constant,
        string ItemId,
        string Reachability);

    private sealed record SeedAssertion(string Id, string ClientName, string Property, string ExpectedValue);

    private sealed class Tally
    {
        public bool SawValid { get; private set; }

        public bool SawNull { get; private set; }

        /// <summary>The ordinal-smallest wrong wire-type token seen; null when every non-null value was right.</summary>
        public string? WrongToken { get; private set; }

        public void Add(Outcome outcome, string? token)
        {
            switch (outcome)
            {
                case Outcome.Valid:
                    SawValid = true;
                    break;
                case Outcome.Null:
                    SawNull = true;
                    break;
                default:
                    ArgumentNullException.ThrowIfNull(token);
                    if (WrongToken is null || string.CompareOrdinal(token, WrongToken) < 0)
                    {
                        WrongToken = token;
                    }

                    break;
            }
        }
    }

    private sealed class Observations
    {
        public Observations(IEnumerable<InventoryEntry> inventory)
        {
            foreach (var entry in inventory)
            {
                if (entry.Property is { } property)
                {
                    Tallies.Add(new MemberKey(entry.Surface, entry.Class, property), new Tally());
                }
            }
        }

        public Dictionary<MemberKey, Tally> Tallies { get; } = [];

        public HashSet<int> RestStatuses { get; } = [];

        public HashSet<int> SoapStatusCodes { get; } = [];
    }
}

/// <summary>
/// The result of the stand-in conformance check: one entry per inventoried member and per seed-token
/// assertion, with the counts and failure items they give. <c>capture</c> writes
/// <see cref="ToJsonNode"/> to <c>baseline/conformance.json</c> through <c>BaselineStore</c>, and
/// <c>replay</c> reads it back with <see cref="Parse"/>.
/// </summary>
/// <remarks>Instances are immutable and safe for concurrent reads.</remarks>
public sealed class ConformanceResult
{
    /// <summary>The result of a member that passed and of a seed-token assertion that holds.</summary>
    public const string PassResult = "pass";

    private const string FileLabel = "baseline/conformance.json";
    private const string WrongTypeNature = "wrong-type";
    private const string WrongTypePrefix = WrongTypeNature + ":";
    private const string RestSurface = "Rest";
    private const string SoapSurface = "Soap";

    private const string SourceKey = "source";
    private const string CountsKey = "counts";
    private const string MembersKey = "members";
    private const string SeedAssertionsKey = "seedAssertions";
    private const string MembersCheckedKey = "membersChecked";
    private const string PassedKey = "passed";
    private const string GapsByNatureKey = "gapsByNature";
    private const string ItemIdKey = "itemId";
    private const string SurfaceKey = "surface";
    private const string ClassKey = "class";
    private const string PropertyKey = "property";
    private const string ConstantKey = "constant";
    private const string ResultKey = "result";
    private const string ObservedTypeKey = "observedType";
    private const string AssertionIdKey = "assertionId";
    private const string ExpectedKey = "expected";
    private const string ObservedKey = "observed";

    /// <summary>The gap natures <see cref="ConformanceCounts.GapsByNature"/> always holds, in this order.</summary>
    private static readonly string[] GapNatures =
    [
        FailureItemIds.UnreachableInSource,
        FailureItemIds.Unobserved,
        FailureItemIds.NullOnly,
        WrongTypeNature,
    ];

    internal ConformanceResult(IEnumerable<ConformanceMember> members, IEnumerable<SeedAssertionResult> seedAssertions)
    {
        Members = members.ToArray().AsReadOnly();
        SeedAssertions = seedAssertions.ToArray().AsReadOnly();
        Counts = CountMembers(Members);

        var failures = new List<FailureItem>();
        foreach (var member in Members)
        {
            if (!IsPass(member.Result))
            {
                failures.Add(new FailureItem(member.ItemId, member.Result));
            }
        }

        foreach (var assertion in SeedAssertions)
        {
            if (!IsPass(assertion.Result))
            {
                failures.Add(new FailureItem(assertion.ItemId, assertion.Result, Detail: $"expected {assertion.Expected}"));
            }
        }

        Failures = failures.AsReadOnly();
    }

    /// <summary>Every inventoried member, in inventory order: REST properties, REST constants, SOAP properties, SOAP constants.</summary>
    public IReadOnlyList<ConformanceMember> Members { get; }

    /// <summary>The seed-token assertions, in their fixed order.</summary>
    public IReadOnlyList<SeedAssertionResult> SeedAssertions { get; }

    /// <summary>The member counts.</summary>
    public ConformanceCounts Counts { get; }

    /// <summary>
    /// One failure item per non-passing member (nature: its result) and per failed seed-token assertion
    /// (nature: <c>differs:&lt;observed&gt;</c>), in inventory order, members first; none names an operation.
    /// </summary>
    public IReadOnlyList<FailureItem> Failures { get; }

    /// <summary>The counts the results report states.</summary>
    public ConformanceSummary Summary => new(Counts.MembersChecked, Counts.Passed, Counts.GapsByNature);

    /// <summary>
    /// The <see cref="CheckNames.Conformance"/> check: every member and seed-token assertion is a case, and
    /// the cases passed are the passing members and the seed-token assertions that hold. The outcome is
    /// <see cref="CheckOutcomes.Pass"/> when <paramref name="register"/> documents every item of
    /// <see cref="Failures"/>, and so when nothing failed, because a gap or failed assertion that is
    /// registered is the accepted result for it: an unreachable member is always registered, and a
    /// reachable member or a seed-token assertion is registered once its correction attempts are spent.
    /// One undocumented item makes it <see cref="CheckOutcomes.Fail"/>. The detail states how many failing
    /// items the register documents, or leaves undocumented, and how many seed-token assertions hold.
    /// </summary>
    /// <param name="register">
    /// The known-limitations register, matched by exact (<c>itemId</c>, <c>nature</c>) pair as
    /// <see cref="KnownLimitationsRegister.IsDocumented"/> decides.
    /// </param>
    /// <returns>The check result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="register"/> is null.</exception>
    public CheckResult ToCheckResult(KnownLimitationsRegister register)
    {
        ArgumentNullException.ThrowIfNull(register);

        var passedAssertions = SeedAssertions.Count(assertion => IsPass(assertion.Result));
        var assertionText = string.Create(
            CultureInfo.InvariantCulture,
            $"{passedAssertions} of {SeedAssertions.Count} seed-token assertions hold");
        var undocumented = Failures.Count(item => !register.IsDocumented(item));

        var detail = Failures.Count == 0
            ? assertionText
            : undocumented == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{Failures.Count} of {Failures.Count} failing items documented by the register; {assertionText}")
                : string.Create(CultureInfo.InvariantCulture, $"{undocumented} of {Failures.Count} failing items not documented by the register; {assertionText}");

        return new CheckResult(
            CheckNames.Conformance,
            undocumented == 0 ? CheckOutcomes.Pass : CheckOutcomes.Fail,
            Members.Count + SeedAssertions.Count,
            Counts.Passed + passedAssertions,
            detail);
    }

    /// <summary>
    /// The document <c>baseline/conformance.json</c> holds: <c>source</c>, <c>counts</c>, <c>members</c>
    /// and <c>seedAssertions</c>, keys in a fixed order, so the same result always serialises identically.
    /// </summary>
    public JsonNode ToJsonNode()
    {
        var gaps = new JsonObject();
        foreach (var nature in GapNatures)
        {
            gaps[nature] = Counts.GapsByNature[nature];
        }

        var members = new JsonArray();
        foreach (var member in Members)
        {
            var entry = new JsonObject
            {
                [ItemIdKey] = member.ItemId,
                [SurfaceKey] = member.Surface,
                [ClassKey] = member.Class,
            };

            if (member.Property is not null)
            {
                entry[PropertyKey] = member.Property;
            }
            else
            {
                entry[ConstantKey] = member.Constant;
            }

            entry[ResultKey] = member.Result;
            if (member.ObservedType is not null)
            {
                entry[ObservedTypeKey] = member.ObservedType;
            }

            members.Add(entry);
        }

        var seedAssertions = new JsonArray();
        foreach (var assertion in SeedAssertions)
        {
            seedAssertions.Add(new JsonObject
            {
                [AssertionIdKey] = assertion.AssertionId,
                [ItemIdKey] = assertion.ItemId,
                [ExpectedKey] = assertion.Expected,
                [ObservedKey] = assertion.Observed,
                [ResultKey] = assertion.Result,
            });
        }

        return new JsonObject
        {
            [SourceKey] = SourceLabels.StandIn,
            [CountsKey] = new JsonObject
            {
                [MembersCheckedKey] = Counts.MembersChecked,
                [PassedKey] = Counts.Passed,
                [GapsByNatureKey] = gaps,
            },
            [MembersKey] = members,
            [SeedAssertionsKey] = seedAssertions,
        };
    }

    /// <summary>
    /// Reads a document written by <see cref="ToJsonNode"/>, so that <c>Parse(r.ToJsonNode())</c> gives a
    /// result that serialises identically. Every key, type and identity is checked: no key may be missing
    /// or unknown, every item id must be the one its surface and names spell, every result must be a
    /// known nature consistent with its observed type or observed value, and the stored counts must equal
    /// the counts of the members.
    /// </summary>
    /// <param name="node">The parsed <c>baseline/conformance.json</c>.</param>
    /// <returns>The conformance result.</returns>
    /// <exception cref="InvalidDataException">The document is not a valid conformance result; the message names the offending path.</exception>
    public static ConformanceResult Parse(JsonNode? node)
    {
        var root = RequireObject(node, "$");
        RequireExactKeys(root, "$", [SourceKey, CountsKey, MembersKey, SeedAssertionsKey], []);

        var source = RequireString(root, SourceKey, "$");
        if (!string.Equals(source, SourceLabels.StandIn, StringComparison.Ordinal))
        {
            throw Invalid($"$.{SourceKey}", $"must be '{SourceLabels.StandIn}', found '{source}'.");
        }

        var members = new List<ConformanceMember>();
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var memberArray = RequireArray(root, MembersKey, "$");
        for (var index = 0; index < memberArray.Count; index++)
        {
            var path = $"$.{MembersKey}[{index}]";
            var member = ParseMember(RequireObject(memberArray[index], path), path);
            if (!memberIds.Add(member.ItemId))
            {
                throw Invalid(path, $"repeats member '{member.ItemId}'.");
            }

            members.Add(member);
        }

        var assertions = new List<SeedAssertionResult>();
        var assertionIds = new HashSet<string>(StringComparer.Ordinal);
        var assertionArray = RequireArray(root, SeedAssertionsKey, "$");
        for (var index = 0; index < assertionArray.Count; index++)
        {
            var path = $"$.{SeedAssertionsKey}[{index}]";
            var assertion = ParseSeedAssertion(RequireObject(assertionArray[index], path), path);
            if (!assertionIds.Add(assertion.AssertionId))
            {
                throw Invalid(path, $"repeats seed-token assertion '{assertion.AssertionId}'.");
            }

            assertions.Add(assertion);
        }

        var result = new ConformanceResult(members, assertions);
        ValidateCounts(RequireObject(root[CountsKey], $"$.{CountsKey}"), result.Counts);
        return result;
    }

    private static ConformanceMember ParseMember(JsonObject entry, string path)
    {
        var isConstant = entry.ContainsKey(ConstantKey);
        RequireExactKeys(
            entry,
            path,
            [ItemIdKey, SurfaceKey, ClassKey, isConstant ? ConstantKey : PropertyKey, ResultKey],
            [ObservedTypeKey]);

        var itemId = RequireString(entry, ItemIdKey, path);
        var surfaceText = RequireString(entry, SurfaceKey, path);
        var surface = surfaceText switch
        {
            RestSurface => Surface.Rest,
            SoapSurface => Surface.Soap,
            _ => throw Invalid($"{path}.{SurfaceKey}", $"must be '{RestSurface}' or '{SoapSurface}', found '{surfaceText}'."),
        };

        var cls = RequireNonBlank(entry, ClassKey, path);
        string? property = null;
        string? constant = null;
        string expectedId;
        if (isConstant)
        {
            constant = RequireNonBlank(entry, ConstantKey, path);
            expectedId = FailureItemIds.ConformanceStatus(surface, constant);
        }
        else
        {
            property = RequireNonBlank(entry, PropertyKey, path);
            expectedId = FailureItemIds.Conformance(surface, cls, property);
        }

        if (!string.Equals(itemId, expectedId, StringComparison.Ordinal))
        {
            throw Invalid($"{path}.{ItemIdKey}", $"must be '{expectedId}', found '{itemId}'.");
        }

        var result = RequireString(entry, ResultKey, path);
        string? observedType = entry.ContainsKey(ObservedTypeKey) ? RequireNonBlank(entry, ObservedTypeKey, path) : null;
        if (result.StartsWith(WrongTypePrefix, StringComparison.Ordinal))
        {
            var token = result[WrongTypePrefix.Length..];
            if (string.IsNullOrWhiteSpace(token) || !string.Equals(observedType, token, StringComparison.Ordinal))
            {
                throw Invalid(path, $"result '{result}' needs a non-blank observed type and an '{ObservedTypeKey}' equal to it.");
            }
        }
        else if (result is not (PassResult or FailureItemIds.UnreachableInSource or FailureItemIds.Unobserved or FailureItemIds.NullOnly))
        {
            throw Invalid($"{path}.{ResultKey}", $"'{result}' is not a conformance result.");
        }
        else if (observedType is not null)
        {
            throw Invalid($"{path}.{ObservedTypeKey}", $"is allowed only with a '{WrongTypePrefix}' result.");
        }

        return new ConformanceMember(itemId, surfaceText, cls, property, constant, result, observedType);
    }

    private static SeedAssertionResult ParseSeedAssertion(JsonObject entry, string path)
    {
        RequireExactKeys(entry, path, [AssertionIdKey, ItemIdKey, ExpectedKey, ObservedKey, ResultKey], []);

        var assertionId = RequireNonBlank(entry, AssertionIdKey, path);
        var itemId = RequireString(entry, ItemIdKey, path);
        var expectedId = FailureItemIds.Seed(assertionId);
        if (!string.Equals(itemId, expectedId, StringComparison.Ordinal))
        {
            throw Invalid($"{path}.{ItemIdKey}", $"must be '{expectedId}', found '{itemId}'.");
        }

        var expected = RequireString(entry, ExpectedKey, path);
        var observed = RequireString(entry, ObservedKey, path);
        var result = RequireString(entry, ResultKey, path);
        var expectedResult = string.Equals(observed, expected, StringComparison.Ordinal) ? PassResult : FailureItemIds.Differs(observed);
        if (!string.Equals(result, expectedResult, StringComparison.Ordinal))
        {
            throw Invalid($"{path}.{ResultKey}", $"must be '{expectedResult}' for the observed value, found '{result}'.");
        }

        return new SeedAssertionResult(assertionId, itemId, expected, observed, result);
    }

    private static void ValidateCounts(JsonObject counts, ConformanceCounts actual)
    {
        const string path = "$." + CountsKey;
        RequireExactKeys(counts, path, [MembersCheckedKey, PassedKey, GapsByNatureKey], []);
        var gaps = RequireObject(counts[GapsByNatureKey], $"{path}.{GapsByNatureKey}");
        RequireExactKeys(gaps, $"{path}.{GapsByNatureKey}", GapNatures, []);

        CheckCount(RequireCount(counts, MembersCheckedKey, path), actual.MembersChecked, $"{path}.{MembersCheckedKey}");
        CheckCount(RequireCount(counts, PassedKey, path), actual.Passed, $"{path}.{PassedKey}");
        foreach (var nature in GapNatures)
        {
            CheckCount(RequireCount(gaps, nature, $"{path}.{GapsByNatureKey}"), actual.GapsByNature[nature], $"{path}.{GapsByNatureKey}.{nature}");
        }

        static void CheckCount(int stored, int computed, string countPath)
        {
            if (stored != computed)
            {
                throw Invalid(countPath, $"is {stored}, but the members give {computed}.");
            }
        }
    }

    private static ConformanceCounts CountMembers(IReadOnlyList<ConformanceMember> members)
    {
        var gaps = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nature in GapNatures)
        {
            gaps[nature] = 0;
        }

        var passed = 0;
        foreach (var member in members)
        {
            if (IsPass(member.Result))
            {
                passed++;
                continue;
            }

            var nature = member.Result.StartsWith(WrongTypePrefix, StringComparison.Ordinal) ? WrongTypeNature : member.Result;
            if (gaps.TryGetValue(nature, out var count))
            {
                gaps[nature] = count + 1;
            }
        }

        return new ConformanceCounts(members.Count, passed, new ReadOnlyDictionary<string, int>(gaps));
    }

    private static bool IsPass(string result) => string.Equals(result, PassResult, StringComparison.Ordinal);

    // Strict JSON access for Parse.

    private static void RequireExactKeys(JsonObject entry, string path, IReadOnlyCollection<string> required, IReadOnlyCollection<string> optional)
    {
        foreach (var key in required)
        {
            if (!entry.ContainsKey(key))
            {
                throw Invalid(path, $"is missing '{key}'.");
            }
        }

        foreach (var (key, _) in entry)
        {
            if (!required.Contains(key, StringComparer.Ordinal) && !optional.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid(path, $"has unknown key '{key}'.");
            }
        }
    }

    private static JsonObject RequireObject(JsonNode? node, string path) =>
        node as JsonObject ?? throw Invalid(path, "must be a JSON object.");

    private static JsonArray RequireArray(JsonObject parent, string key, string path) =>
        parent[key] as JsonArray ?? throw Invalid($"{path}.{key}", "must be a JSON array.");

    private static string RequireString(JsonObject parent, string key, string path) =>
        parent[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue<string>(out var text)
            ? text
            : throw Invalid($"{path}.{key}", "must be a JSON string.");

    private static string RequireNonBlank(JsonObject parent, string key, string path)
    {
        var text = RequireString(parent, key, path);
        return string.IsNullOrWhiteSpace(text) ? throw Invalid($"{path}.{key}", "must not be blank.") : text;
    }

    private static int RequireCount(JsonObject parent, string key, string path) =>
        parent[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var count) && count >= 0
            ? count
            : throw Invalid($"{path}.{key}", "must be a non-negative integer.");

    private static InvalidDataException Invalid(string path, string message) => new($"{FileLabel}: {path} {message}");
}
