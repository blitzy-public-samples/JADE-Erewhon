using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using System.Xml.Linq;

namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// The in-repo files the catalogue was derived from, as repository-relative paths in their on-disk
/// spelling, which writes the reference schema and application definitions as <c>Erehwhon</c>.
/// </summary>
/// <param name="ReferenceSchema">
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm</c>: the operations, DTOs, status constants,
/// <c>WebServiceHTTP</c> exposure list and method bodies.
/// </param>
/// <param name="ConsumerSchema">
/// <c>Erewhon/ErewhonWebServiceConsumerSchema.scm</c>: the in-repo SOAP consumer
/// <c>HTTP_ErewhonWebService</c>, which records the SOAP actions, endpoint and element and array names.
/// </param>
/// <param name="ApplicationDefinitions">
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.ddx</c>: the <c>ErewhonREST</c> and
/// <c>WebServiceHTTP</c> application definitions and their ports.
/// </param>
public sealed record CatalogueSources(string ReferenceSchema, string ConsumerSchema, string ApplicationDefinitions);

/// <summary>The caller-facing addresses of the two JADE services, which the facade keeps unchanged.</summary>
/// <param name="Rest">The REST address form of <c>ErewhonRestService</c>.</param>
/// <param name="Soap">The SOAP endpoint and message conventions of <c>ErewhonWebService</c>.</param>
public sealed record CatalogueAddresses(RestAddress Rest, SoapAddress Soap);

/// <summary>
/// The REST address form <c>http://&lt;host&gt;:65000/&lt;resource&gt;[/&lt;p1&gt;...]</c> served by the
/// <c>ErewhonREST</c> application.
/// </summary>
/// <param name="Application">The JADE application name, <c>ErewhonREST</c>.</param>
/// <param name="Port">The listening port, 65000 (pipe <c>0.0.0.0:65000</c>).</param>
/// <param name="PortSource">The evidence for <paramref name="Port"/>, <c>Erewhon/ErehwhonExternalInterfacesSchema.ddx:82</c>.</param>
/// <param name="Form">
/// The address form, <c>direct</c>: no relay segment <c>/jade/jadehttp.dll</c>, no application query
/// <c>?ErewhonREST</c> and no query string.
/// </param>
/// <param name="FormatSuffixes">
/// The output-format suffixes JADE accepts on the last path segment, <c>.json</c>, <c>.xml</c> and
/// <c>.jsonn</c>; a suffix takes precedence over the <c>Accept</c> header.
/// </param>
/// <param name="DefaultFormat">The output format used when neither a suffix nor a header names one, <c>json</c>.</param>
/// <param name="ContentType">The JSON response content type, <c>application/json; charset=utf-8</c>.</param>
public sealed record RestAddress(
    string Application,
    int Port,
    string PortSource,
    string Form,
    IReadOnlyList<string> FormatSuffixes,
    string DefaultFormat,
    string ContentType);

/// <summary>
/// The SOAP 1.1 document/literal wrapped endpoint of <c>ErewhonWebService</c>, exposed through the
/// <c>WebServiceHTTP</c> list and served by the <c>WebServiceHTTP</c> application.
/// </summary>
/// <param name="Application">The JADE application name, <c>WebServiceHTTP</c>.</param>
/// <param name="Port">The listening port, 21001 (pipe <c>0.0.0.0:21001</c>).</param>
/// <param name="PortSource">The evidence for <paramref name="Port"/>, <c>Erewhon/ErehwhonExternalInterfacesSchema.ddx:158</c>.</param>
/// <param name="Path">The endpoint path, <c>/jade/jadehttp.dll</c>.</param>
/// <param name="Query">
/// The endpoint query without its leading <c>?</c>,
/// <c>WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>.
/// </param>
/// <param name="WsdlQueryKey">The query key appended to the endpoint query to request the WSDL, <c>wsdl</c>.</param>
/// <param name="EndpointSource">
/// The evidence for <paramref name="Path"/> and <paramref name="Query"/>, the consumer's
/// <c>endPointURL</c> at <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:1724</c>.
/// </param>
/// <param name="ServiceName">The web service class, <c>ErewhonWebService</c>.</param>
/// <param name="ListName">The exposure list, <c>WebServiceHTTP</c>.</param>
/// <param name="TargetNamespace">The message namespace, <c>urn:JadeWebServices/WebServiceHTTP/</c>.</param>
/// <param name="SoapActionPrefix">
/// The prefix of every <c>SOAPAction</c>, <c>urn:JadeWebServices/WebServiceHTTP/</c>, followed by the
/// operation name (KL-SOAP-ACTIONS).
/// </param>
/// <param name="EnvelopeNamespace">The SOAP 1.1 envelope namespace, <c>http://schemas.xmlsoap.org/soap/envelope/</c>.</param>
/// <param name="SoapVersion">The SOAP version, <c>1.1</c>.</param>
/// <param name="Style">The binding style, <c>document</c>.</param>
/// <param name="Use">The body use, <c>literal</c>.</param>
/// <param name="Wrapped">
/// True for wrapped messages: the request element is named after the operation and the response is
/// <c>&lt;op&gt;Response</c> holding <c>&lt;op&gt;Result</c>.
/// </param>
/// <param name="ContentType">The request and response content type, <c>text/xml; charset=utf-8</c>.</param>
/// <param name="WsdlNamespaces">
/// The WSDL namespace prefixes the consumer records, prefix to namespace URI, in catalogue order (for
/// example <c>soap</c> to <c>http://schemas.xmlsoap.org/wsdl/soap/</c> and <c>tns</c> to
/// <c>urn:JadeWebServices/WebServiceHTTP/</c>).
/// </param>
/// <param name="WsdlNamespacesSource">
/// The evidence for <paramref name="WsdlNamespaces"/>, the consumer's <c>additionalInfo</c> at
/// <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:1713-1721</c>.
/// </param>
public sealed record SoapAddress(
    string Application,
    int Port,
    string PortSource,
    string Path,
    string Query,
    string WsdlQueryKey,
    string EndpointSource,
    string ServiceName,
    string ListName,
    string TargetNamespace,
    string SoapActionPrefix,
    string EnvelopeNamespace,
    string SoapVersion,
    string Style,
    string Use,
    bool Wrapped,
    string ContentType,
    IReadOnlyDictionary<string, string> WsdlNamespaces,
    string WsdlNamespacesSource)
{
    /// <summary>
    /// The endpoint path and query every SOAP request is posted to,
    /// <c>/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>.
    /// </summary>
    [JsonIgnore]
    public string EndpointPathAndQuery => $"{Path}?{Query}";

    /// <summary>The path and query of the WSDL request, <see cref="EndpointPathAndQuery"/> followed by <c>&amp;wsdl</c>.</summary>
    [JsonIgnore]
    public string WsdlPathAndQuery => $"{EndpointPathAndQuery}&{WsdlQueryKey}";
}

/// <summary>
/// The wire forms of one JADE primitive type, keyed in <see cref="OperationCatalogue.WireTypes"/> by the
/// JADE type name (<c>String</c>, <c>Integer</c>, <c>Decimal</c>, <c>Date</c>, <c>TimeStamp</c>,
/// <c>Binary</c>).
/// </summary>
/// <param name="Json">
/// The REST JSON form: <c>string</c>, <c>integer</c>, <c>decimal</c> (a number with two decimals),
/// <c>msDate</c> (<c>\/Date(ms)\/</c>) or <c>byteArray</c> (an array of byte values).
/// </param>
/// <param name="Xml">
/// The XML Schema type of the SOAP form in Clark notation (<c>{uri}local</c>), for example
/// <c>{http://www.w3.org/2001/XMLSchema}dateTime</c> or <c>{urn:JadeWebServices/WebServiceHTTP/}decimal</c>.
/// </param>
/// <param name="Source">The consumer line that evidences <paramref name="Xml"/>, for example <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:346</c>.</param>
public sealed record WireTypeMapping(string Json, string Xml, string Source);

/// <summary>
/// The single loader of <c>contracts/operation-catalogue.json</c>, embedded in this assembly as
/// <see cref="ResourceName"/>. The facade, the stand-in and the harness take every operation, DTO,
/// status and address fact from this class and keep no copy of their own.
/// </summary>
/// <remarks>
/// Loading is strict: an unknown or repeated key, a missing key, a <c>null</c> in a non-nullable member,
/// a number written as a string or an integer enum value fails, and so does any break of the catalogue's
/// own rules, such as an operation count other than the 22 carried-over operations (13 REST, 9 SOAP) or
/// an unresolved type name. Every load failure is an <see cref="InvalidDataException"/> whose message
/// starts <c>operation-catalogue.json:</c> and names the offending entry or JSON path. A loaded catalogue
/// is immutable, collections included, and safe for concurrent reads; lookups are ordinal and
/// case-sensitive, because JADE names are.
/// </remarks>
public sealed class OperationCatalogue
{
    /// <summary>The manifest resource name of the embedded catalogue, the <c>LogicalName</c> set in <c>Erewhon.Contracts.csproj</c>.</summary>
    public const string ResourceName = "Erewhon.Contracts.operation-catalogue.json";

    private const string FileLabel = "operation-catalogue.json";
    private const int ExpectedRestOperations = 13;
    private const int ExpectedSoapOperations = 9;
    private const int ExpectedRestConstants = 8;
    private const int ExpectedSoapConstants = 8;
    private const string RestConstantPrefix = "HTTP_";
    private const int ExpectedRestPort = 65000;
    private const int ExpectedSoapPort = 21001;

    // The consumer's endPointURL without scheme and host (Erewhon/ErewhonWebServiceConsumerSchema.scm:1724).
    private const string ConsumerEndpointPathAndQuery =
        "/jade/jadehttp.dll?WebServiceHTTP&serviceName=ErewhonWebService&listName=WebServiceHTTP";

    // JADE built-in roots that the catalogue names as superclasses but does not define.
    private const string DtoRootClass = "Object";
    private static readonly string[] ArrayRootClasses = ["ObjectArray", "Array"];

    private static readonly Lazy<OperationCatalogue> _default =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FrozenDictionary<string, OperationDescriptor> _operationsById;
    private readonly FrozenDictionary<(Surface Surface, string JadeMethod), OperationDescriptor> _operationsByMethod;
    private readonly FrozenDictionary<Surface, IReadOnlyList<OperationDescriptor>> _operationsBySurface;
    private readonly FrozenDictionary<string, DtoDefinition> _dtosByName;
    private readonly FrozenDictionary<string, ArrayDefinition> _arraysByName;
    private readonly FrozenDictionary<string, FrozenSet<string>> _lineage;
    private readonly FrozenDictionary<Surface, IReadOnlyList<StatusConstant>> _constantsBySurface;

    private OperationCatalogue(CatalogueDocument document)
    {
        CatalogueVersion = document.CatalogueVersion;
        Description = document.Description;
        Sources = document.Sources;
        Addresses = new CatalogueAddresses(
            document.Addresses.Rest with { FormatSuffixes = AsReadOnlyList(document.Addresses.Rest.FormatSuffixes) },
            document.Addresses.Soap with { WsdlNamespaces = AsReadOnlyMap(document.Addresses.Soap.WsdlNamespaces) });
        WireTypes = AsReadOnlyMap(document.WireTypes);
        Operations = AsReadOnlyList(document.Operations.Select(FreezeOperation));
        Dtos = AsReadOnlyList(document.Dtos.Select(dto => dto with { Properties = AsReadOnlyList(dto.Properties) }));
        Arrays = AsReadOnlyList(document.Arrays);
        StatusConstants = AsReadOnlyList(document.StatusConstants);

        _operationsById = Operations.ToFrozenDictionary(operation => operation.Id, StringComparer.Ordinal);
        _operationsByMethod = Operations.ToFrozenDictionary(operation => (operation.Surface, operation.JadeMethod));
        _operationsBySurface = Enum.GetValues<Surface>().ToFrozenDictionary(
            surface => surface,
            surface => (IReadOnlyList<OperationDescriptor>)AsReadOnlyList(Operations.Where(operation => operation.Surface == surface)));
        _dtosByName = Dtos.ToFrozenDictionary(dto => dto.Name, StringComparer.Ordinal);
        _arraysByName = Arrays.ToFrozenDictionary(array => array.Name, StringComparer.Ordinal);
        _constantsBySurface = Enum.GetValues<Surface>().ToFrozenDictionary(
            surface => surface,
            surface => (IReadOnlyList<StatusConstant>)AsReadOnlyList(StatusConstants.Where(constant => constant.Surface == surface)));

        var superclassOf = SuperclassMap(Dtos, Arrays);
        _lineage = superclassOf.Keys.ToFrozenDictionary(
            name => name,
            name => SelfAndAncestors(name, superclassOf).ToFrozenSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The strict, read-only serializer options the catalogue is read with: web defaults (camelCase,
    /// case-insensitive names), no unknown, repeated or missing keys, no <c>null</c> in a non-nullable
    /// member, no numbers written as strings, no trailing commas or comments, and enums read from their
    /// names case-insensitively, never from integers. Consumers may reuse the instance to read or write
    /// catalogue records; it is frozen, so any attempt to change it throws.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// The catalogue embedded in this assembly, loaded and validated once on first use and shared by
    /// every caller. A load failure is rethrown on every access.
    /// </summary>
    /// <exception cref="InvalidDataException">The embedded catalogue is missing or invalid.</exception>
    public static OperationCatalogue Default => _default.Value;

    /// <summary>The catalogue's version, for example <c>1.0.0</c>.</summary>
    public string CatalogueVersion { get; }

    /// <summary>A one-paragraph description of what the catalogue holds.</summary>
    public string Description { get; }

    /// <summary>The in-repo files the catalogue was derived from.</summary>
    public CatalogueSources Sources { get; }

    /// <summary>The REST and SOAP caller addresses.</summary>
    public CatalogueAddresses Addresses { get; }

    /// <summary>
    /// The wire forms of each JADE primitive type, keyed by JADE type name with an ordinal comparer and
    /// enumerated in catalogue order.
    /// </summary>
    public IReadOnlyDictionary<string, WireTypeMapping> WireTypes { get; }

    /// <summary>
    /// The 22 carried-over operations in catalogue order, the 13 REST operations first: the basis of the
    /// fixed replay order.
    /// </summary>
    public IReadOnlyList<OperationDescriptor> Operations { get; }

    /// <summary>Every DTO class, in catalogue order, each with its flattened property list.</summary>
    public IReadOnlyList<DtoDefinition> Dtos { get; }

    /// <summary>Every array type the DTOs and operations use, in catalogue order.</summary>
    public IReadOnlyList<ArrayDefinition> Arrays { get; }

    /// <summary>The 8 REST <c>HTTP_*</c> and 8 SOAP status constants, in catalogue order.</summary>
    public IReadOnlyList<StatusConstant> StatusConstants { get; }

    /// <summary>Loads and validates the catalogue embedded in this assembly as <see cref="ResourceName"/>.</summary>
    /// <returns>A new catalogue instance; use <see cref="Default"/> for the shared one.</returns>
    /// <exception cref="InvalidDataException">
    /// The resource is missing (the message lists the resources present) or the catalogue is invalid.
    /// </exception>
    public static OperationCatalogue Load()
    {
        Assembly assembly = typeof(OperationCatalogue).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            var present = assembly.GetManifestResourceNames();
            throw Invalid(
                $"embedded resource '{ResourceName}' not found in assembly '{assembly.GetName().Name}'; " +
                $"resources present: {(present.Length == 0 ? "none" : string.Join(", ", present))}.");
        }

        return Parse(stream);
    }

    /// <summary>Parses and validates a catalogue from JSON text.</summary>
    /// <param name="json">The catalogue document.</param>
    /// <returns>The validated catalogue.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="InvalidDataException">The text is not a valid catalogue.</exception>
    public static OperationCatalogue Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        CatalogueDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogueDocument>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception);
        }

        return FromDocument(document);
    }

    /// <summary>Parses and validates a catalogue from a UTF-8 JSON stream, which is read to its end and not disposed.</summary>
    /// <param name="utf8Json">The catalogue document as UTF-8 bytes.</param>
    /// <returns>The validated catalogue.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is null.</exception>
    /// <exception cref="InvalidDataException">The stream does not hold a valid catalogue.</exception>
    public static OperationCatalogue Parse(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        CatalogueDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogueDocument>(utf8Json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidJson(exception);
        }

        return FromDocument(document);
    }

    /// <summary>Returns the operation with the given id, for example <c>Rest.getAgent</c> or <c>Soap.getAgent</c>.</summary>
    /// <param name="id">The operation id <c>&lt;Surface&gt;.&lt;JadeMethod&gt;</c>.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">No operation has that id.</exception>
    public OperationDescriptor GetById(string id) =>
        TryGetById(id, out var operation)
            ? operation
            : throw new KeyNotFoundException($"The operation catalogue has no operation with id '{id}'.");

    /// <summary>Finds the operation with the given id.</summary>
    /// <param name="id">The operation id <c>&lt;Surface&gt;.&lt;JadeMethod&gt;</c>.</param>
    /// <param name="operation">The operation when found; otherwise null.</param>
    /// <returns>True when an operation has that id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public bool TryGetById(string id, [NotNullWhen(true)] out OperationDescriptor? operation)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _operationsById.TryGetValue(id, out operation);
    }

    /// <summary>Returns the operations of one surface in catalogue order: 13 for REST, 9 for SOAP.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>The surface's operations.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined <see cref="Surface"/>.</exception>
    public IReadOnlyList<OperationDescriptor> GetBySurface(Surface surface) =>
        _operationsBySurface.TryGetValue(surface, out var operations)
            ? operations
            : throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface.");

    /// <summary>Returns the operation a surface declares for a JADE method.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="jadeMethod">The JADE method name as declared, for example <c>getPhoto</c>.</param>
    /// <returns>The operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jadeMethod"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The surface declares no such operation.</exception>
    public OperationDescriptor Find(Surface surface, string jadeMethod) =>
        TryFind(surface, jadeMethod, out var operation)
            ? operation
            : throw new KeyNotFoundException($"The operation catalogue has no {surface} operation '{jadeMethod}'.");

    /// <summary>Finds the operation a surface declares for a JADE method.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="jadeMethod">The JADE method name as declared, for example <c>getPhoto</c>.</param>
    /// <param name="operation">The operation when found; otherwise null.</param>
    /// <returns>True when the surface declares the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jadeMethod"/> is null.</exception>
    public bool TryFind(Surface surface, string jadeMethod, [NotNullWhen(true)] out OperationDescriptor? operation)
    {
        ArgumentNullException.ThrowIfNull(jadeMethod);
        return _operationsByMethod.TryGetValue((surface, jadeMethod), out operation);
    }

    /// <summary>Returns the DTO class with the given name, for example <c>DTORetailItem</c>.</summary>
    /// <param name="name">The JADE class name.</param>
    /// <returns>The DTO definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The catalogue defines no DTO of that name.</exception>
    public DtoDefinition GetDto(string name) =>
        TryGetDto(name, out var dto)
            ? dto
            : throw new KeyNotFoundException($"The operation catalogue has no DTO '{name}'.");

    /// <summary>Finds the DTO class with the given name.</summary>
    /// <param name="name">The JADE class name.</param>
    /// <param name="dto">The DTO definition when found; otherwise null.</param>
    /// <returns>True when the catalogue defines a DTO of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetDto(string name, [NotNullWhen(true)] out DtoDefinition? dto)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _dtosByName.TryGetValue(name, out dto);
    }

    /// <summary>Finds the array type with the given name, for example <c>DTONameArray</c> or <c>StringArray</c>.</summary>
    /// <param name="name">The JADE class name.</param>
    /// <param name="array">The array definition when found; otherwise null.</param>
    /// <returns>True when the catalogue defines an array of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetArray(string name, [NotNullWhen(true)] out ArrayDefinition? array)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _arraysByName.TryGetValue(name, out array);
    }

    /// <summary>
    /// Tells whether a catalogue class is <paramref name="ancestor"/> or inherits from it, following DTO
    /// superclasses for a DTO name and array superclasses for an array name: <c>DTORetailItem</c> is a
    /// <c>DTOItem</c>, and <c>DTOAgentArray</c> is a <c>DTOObjectArray</c>.
    /// </summary>
    /// <remarks>
    /// Only catalogue classes take part, so the result is false for an unknown name, and the JADE
    /// built-in roots the catalogue does not define (<c>Object</c>, <c>ObjectArray</c>) are never an
    /// ancestor.
    /// </remarks>
    /// <param name="className">The class to test, for example a <c>__type</c> or <c>xsi:type</c> value.</param>
    /// <param name="ancestor">The class it may be a kind of, for example the declared type at a position.</param>
    /// <returns>True when the names are equal or <paramref name="ancestor"/> is on the superclass chain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="className"/> or <paramref name="ancestor"/> is null.</exception>
    public bool IsKindOf(string className, string ancestor)
    {
        ArgumentNullException.ThrowIfNull(className);
        ArgumentNullException.ThrowIfNull(ancestor);
        return _lineage.TryGetValue(className, out var lineage) && lineage.Contains(ancestor);
    }

    /// <summary>Returns the status constants one surface declares, in catalogue order: 8 for each surface.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>The surface's constants.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined <see cref="Surface"/>.</exception>
    public IReadOnlyList<StatusConstant> GetConstants(Surface surface) =>
        _constantsBySurface.TryGetValue(surface, out var constants)
            ? constants
            : throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface.");


    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,

            // A repeated key would otherwise keep its last value silently.
            AllowDuplicateProperties = false,

            // Under UnmappedMemberHandling.Disallow, System.Text.Json still counts the name of a
            // [JsonIgnore] member (RoutingKey, IsNullBody, EndpointPathAndQuery, ...) as mapped and
            // skips a key of that name silently. Removing those contract entries makes such a key an
            // unmapped member, rejected with its JSON path.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveIgnoredMembers } },
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void RemoveIgnoredMembers(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            var property = typeInfo.Properties[index];
            if (property.Get is null && property.Set is null && property.AssociatedParameter is null)
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }

    private static InvalidDataException InvalidJson(JsonException exception) =>
        new($"{FileLabel}: invalid at {exception.Path ?? "$"}: {exception.Message}", exception);

    private static InvalidDataException Invalid(string message) => new($"{FileLabel}: {message}");

    private static OperationCatalogue FromDocument(CatalogueDocument? document)
    {
        if (document is null)
        {
            throw Invalid("the document is the JSON literal null, not a catalogue object.");
        }

        Validate(document);
        return new OperationCatalogue(document);
    }

    private static ReadOnlyCollection<T> AsReadOnlyList<T>(IEnumerable<T> items) => Array.AsReadOnly(items.ToArray());

    // ReadOnlyDictionary over a copy keeps the catalogue's key order, which a FrozenDictionary would not.
    private static ReadOnlyDictionary<string, T> AsReadOnlyMap<T>(IReadOnlyDictionary<string, T> items) =>
        new(new Dictionary<string, T>(items, StringComparer.Ordinal));

    private static OperationDescriptor FreezeOperation(OperationDescriptor operation) => operation with
    {
        Parameters = AsReadOnlyList(operation.Parameters),
        Statuses = AsReadOnlyList(operation.Statuses.Select(status => status with
        {
            RuntimeTypes = status.RuntimeTypes is null ? null : AsReadOnlyList(status.RuntimeTypes),
        })),
    };

    private static Dictionary<string, string?> SuperclassMap(
        IReadOnlyList<DtoDefinition> dtos,
        IReadOnlyList<ArrayDefinition> arrays)
    {
        var superclassOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var dto in dtos)
        {
            superclassOf[dto.Name] = dto.Superclass;
        }

        foreach (var array in arrays)
        {
            superclassOf[array.Name] = array.Superclass;
        }

        return superclassOf;
    }

    // Walks the superclass chain through catalogue classes only; callers guarantee it is acyclic.
    private static IEnumerable<string> SelfAndAncestors(string name, IReadOnlyDictionary<string, string?> superclassOf)
    {
        var current = name;
        while (current is not null && superclassOf.TryGetValue(current, out var superclass))
        {
            yield return current;
            current = superclass;
        }
    }

    private static void Validate(CatalogueDocument document)
    {
        ValidateStructure(document);
        ValidateOperationCounts(document.Operations);
        ValidateOperationIds(document.Operations);
        ValidateBindings(document.Operations);
        ValidateSoapMessageNames(document.Operations, document.Addresses.Soap);
        ValidateParametersAndStatuses(document.Operations);
        ValidateTypeReferences(document);
        ValidateStatusConstantReferences(document.Operations, document.StatusConstants);
        ValidateClassHierarchy(document);
        ValidatePropertyValues(document);
        ValidateStatusConstants(document.StatusConstants);
        ValidateAddresses(document.Addresses);
    }

    // RespectNullableAnnotations does not reach collection elements or dictionary values, so a null
    // there is rejected here before any rule dereferences it.
    private static void ValidateStructure(CatalogueDocument document)
    {
        RequireNoNullElements(document.Addresses.Rest.FormatSuffixes, "addresses.rest.formatSuffixes");
        RequireNoNullValues(document.Addresses.Soap.WsdlNamespaces, "addresses.soap.wsdlNamespaces");
        RequireNoNullValues(document.WireTypes, "wireTypes");
        RequireNoNullElements(document.Operations, "operations");
        for (var index = 0; index < document.Operations.Count; index++)
        {
            var operation = document.Operations[index];
            RequireNoNullElements(operation.Parameters, $"operations[{index}].parameters");
            RequireNoNullElements(operation.Statuses, $"operations[{index}].statuses");
            for (var statusIndex = 0; statusIndex < operation.Statuses.Count; statusIndex++)
            {
                if (operation.Statuses[statusIndex].RuntimeTypes is { } runtimeTypes)
                {
                    RequireNoNullElements(runtimeTypes, $"operations[{index}].statuses[{statusIndex}].runtimeTypes");
                }
            }
        }

        RequireNoNullElements(document.Dtos, "dtos");
        for (var index = 0; index < document.Dtos.Count; index++)
        {
            RequireNoNullElements(document.Dtos[index].Properties, $"dtos[{index}].properties");
        }

        RequireNoNullElements(document.Arrays, "arrays");
        RequireNoNullElements(document.StatusConstants, "statusConstants");
    }

    private static void RequireNoNullElements<T>(IReadOnlyList<T> items, string path)
        where T : class
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is null)
            {
                throw Invalid($"{path}[{index}] is null.");
            }
        }
    }

    private static void RequireNoNullValues<T>(IReadOnlyDictionary<string, T> items, string path)
        where T : class
    {
        foreach (var (key, value) in items)
        {
            if (value is null)
            {
                throw Invalid($"{path}.{key} is null.");
            }
        }
    }

    private static void ValidateOperationCounts(IReadOnlyList<OperationDescriptor> operations)
    {
        var rest = operations.Count(operation => operation.Surface == Surface.Rest);
        var soap = operations.Count(operation => operation.Surface == Surface.Soap);
        if (operations.Count != ExpectedRestOperations + ExpectedSoapOperations
            || rest != ExpectedRestOperations
            || soap != ExpectedSoapOperations)
        {
            throw Invalid(
                $"expected exactly {ExpectedRestOperations + ExpectedSoapOperations} operations " +
                $"({ExpectedRestOperations} Rest, {ExpectedSoapOperations} Soap), the methods carried over from " +
                $"ErewhonRestService and ErewhonWebService, but found {operations.Count} ({rest} Rest, {soap} Soap).");
        }
    }

    private static void ValidateOperationIds(IReadOnlyList<OperationDescriptor> operations)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (!ids.Add(operation.Id))
            {
                throw Invalid($"operation id '{operation.Id}' is listed more than once.");
            }
        }

        // With every id unique and equal to <surface>.<jadeMethod>, each JADE method is unique per surface.
        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.JadeMethod))
            {
                throw Invalid($"operation '{operation.Id}': jadeMethod is empty.");
            }

            var expected = $"{operation.Surface}.{operation.JadeMethod}";
            if (!string.Equals(operation.Id, expected, StringComparison.Ordinal))
            {
                throw Invalid($"operation '{operation.Id}': id must be '{expected}', the form <surface>.<jadeMethod>.");
            }
        }
    }


    private static void ValidateBindings(IReadOnlyList<OperationDescriptor> operations)
    {
        foreach (var operation in operations)
        {
            if (operation.Surface == Surface.Rest)
            {
                ValidateRestBinding(operation);
            }
            else
            {
                ValidateSoapBinding(operation);
            }
        }
    }

    private static void ValidateRestBinding(OperationDescriptor operation)
    {
        if (operation.Rest is not { } rest)
        {
            throw Invalid($"operation '{operation.Id}': a Rest operation needs a rest binding, but rest is null.");
        }

        if (operation.Soap is not null)
        {
            throw Invalid($"operation '{operation.Id}': a Rest operation must have soap null.");
        }

        if (rest.Verb is not ("GET" or "POST"))
        {
            throw Invalid($"operation '{operation.Id}': rest.verb '{rest.Verb}' must be GET or POST.");
        }

        var bodyParameters = 0;
        foreach (var parameter in operation.Parameters)
        {
            switch (parameter.Location)
            {
                case ParameterLocation.Path:
                    break;
                case ParameterLocation.Body:
                    bodyParameters++;
                    break;
                default:
                    throw Invalid(
                        $"operation '{operation.Id}': parameter '{parameter.Name}' has location {parameter.Location}; " +
                        "a Rest parameter travels in the path or the body.");
            }
        }

        if (bodyParameters > 1)
        {
            throw Invalid($"operation '{operation.Id}': {bodyParameters} body parameters, but at most one travels as the JSON body.");
        }

        var template = rest.PathTemplate;
        if (!template.StartsWith('/'))
        {
            throw Invalid($"operation '{operation.Id}': rest.pathTemplate '{template}' must start with '/'.");
        }

        // Each placeholder is one whole segment, so the matcher can bind segments by position.
        var placeholders = new List<string>();
        foreach (var segment in template[1..].Split('/'))
        {
            if (segment.Length == 0)
            {
                throw Invalid($"operation '{operation.Id}': rest.pathTemplate '{template}' has an empty segment.");
            }

            if (segment.AsSpan().IndexOfAny('{', '}') < 0)
            {
                continue;
            }

            if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}'
                || segment.AsSpan(1, segment.Length - 2).IndexOfAny('{', '}') >= 0)
            {
                throw Invalid(
                    $"operation '{operation.Id}': rest.pathTemplate segment '{segment}' must be a literal " +
                    "or exactly one {name} placeholder.");
            }

            placeholders.Add(segment[1..^1]);
        }

        var pathParameters = operation.Parameters
            .Where(parameter => parameter.Location == ParameterLocation.Path)
            .Select(parameter => parameter.Name)
            .ToList();
        if (!placeholders.SequenceEqual(pathParameters, StringComparer.Ordinal))
        {
            throw Invalid(
                $"operation '{operation.Id}': rest.pathTemplate '{template}' has placeholders " +
                $"[{string.Join(", ", placeholders)}], but each path parameter must appear exactly once, in " +
                $"signature order, with no other placeholder: [{string.Join(", ", pathParameters)}].");
        }
    }

    private static void ValidateSoapBinding(OperationDescriptor operation)
    {
        if (operation.Soap is null)
        {
            throw Invalid($"operation '{operation.Id}': a Soap operation needs a soap binding, but soap is null.");
        }

        if (operation.Rest is not null)
        {
            throw Invalid($"operation '{operation.Id}': a Soap operation must have rest null.");
        }

        foreach (var parameter in operation.Parameters)
        {
            if (parameter.Location != ParameterLocation.Element)
            {
                throw Invalid(
                    $"operation '{operation.Id}': parameter '{parameter.Name}' has location {parameter.Location}; " +
                    "a Soap parameter is a child element of the request wrapper.");
            }
        }
    }

    private static void ValidateSoapMessageNames(IReadOnlyList<OperationDescriptor> operations, SoapAddress soapAddress)
    {
        foreach (var operation in operations)
        {
            if (operation.Soap is not { } soap)
            {
                continue;
            }

            RequireName(operation, "soap.action", soap.Action, soapAddress.SoapActionPrefix + operation.JadeMethod);
            RequireName(operation, "soap.requestElement", soap.RequestElement, operation.JadeMethod);
            RequireName(operation, "soap.responseElement", soap.ResponseElement, operation.JadeMethod + "Response");
            RequireName(operation, "soap.resultElement", soap.ResultElement, operation.JadeMethod + "Result");
        }
    }

    private static void RequireName(OperationDescriptor operation, string member, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw Invalid($"operation '{operation.Id}': {member} is '{actual}' but must be '{expected}'.");
        }
    }

    private static void ValidateParametersAndStatuses(IReadOnlyList<OperationDescriptor> operations)
    {
        foreach (var operation in operations)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < operation.Parameters.Count; index++)
            {
                var parameter = operation.Parameters[index];

                // Position equal to the list index makes positions unique, ascending and zero-based.
                if (parameter.Position != index)
                {
                    throw Invalid(
                        $"operation '{operation.Id}': parameter '{parameter.Name}' has position {parameter.Position} " +
                        $"but is listed at index {index}; positions run 0, 1, 2, ... in signature order.");
                }

                if (!names.Add(parameter.Name))
                {
                    throw Invalid($"operation '{operation.Id}': parameter '{parameter.Name}' is listed more than once.");
                }
            }

            if (operation.Statuses.Count == 0)
            {
                throw Invalid($"operation '{operation.Id}': statuses is empty.");
            }

            foreach (var status in operation.Statuses)
            {
                if (status.HttpStatus is < 100 or > 599)
                {
                    throw Invalid($"operation '{operation.Id}': httpStatus {status.HttpStatus} is outside 100-599.");
                }
            }
        }
    }


    private static void ValidateTypeReferences(CatalogueDocument document)
    {
        var types = new TypeIndex(document);
        foreach (var operation in document.Operations)
        {
            var subject = $"operation '{operation.Id}'";
            if (types.Categorise(operation.ReturnType) == TypeCategory.Unresolved)
            {
                throw Unresolved($"{subject}: returnType", operation.ReturnType);
            }

            foreach (var parameter in operation.Parameters)
            {
                var category = types.Categorise(parameter.JadeType);
                var (allowed, expectation) = parameter.Location switch
                {
                    ParameterLocation.Path => (category == TypeCategory.WireType, "wire type, because a path segment is a primitive"),
                    ParameterLocation.Body => (category == TypeCategory.Dto, "DTO, because the JSON body is one object"),
                    _ => (category is TypeCategory.WireType or TypeCategory.Dto, "wire type or DTO"),
                };
                if (!allowed)
                {
                    throw Invalid(
                        $"{subject}: parameter '{parameter.Name}' has jadeType '{parameter.JadeType}', " +
                        $"which is not a catalogue {expectation}.");
                }
            }

            foreach (var status in operation.Statuses)
            {
                switch (status.BodyType)
                {
                    case OperationStatus.NullBody when operation.Surface == Surface.Rest:
                    case OperationStatus.NilBody when operation.Surface == Surface.Soap:
                        break;
                    case OperationStatus.NullBody or OperationStatus.NilBody:
                        throw Invalid(
                            $"{subject}: status {status.HttpStatus} has bodyType '{status.BodyType}', which the " +
                            $"{operation.Surface} surface never writes (REST writes '{OperationStatus.NullBody}', " +
                            $"SOAP '{OperationStatus.NilBody}').");
                    default:
                        if (types.Categorise(status.BodyType) == TypeCategory.Unresolved)
                        {
                            throw Unresolved($"{subject}: status {status.HttpStatus} bodyType", status.BodyType);
                        }

                        break;
                }

                foreach (var runtimeType in status.RuntimeTypes ?? [])
                {
                    if (types.Categorise(runtimeType) == TypeCategory.Unresolved)
                    {
                        throw Unresolved($"{subject}: status {status.HttpStatus} runtimeTypes entry", runtimeType);
                    }
                }
            }
        }

        foreach (var dto in document.Dtos)
        {
            foreach (var property in dto.Properties)
            {
                var (required, expectation) = property.Kind switch
                {
                    PropertyKind.Attribute => (TypeCategory.WireType, "wire type"),
                    PropertyKind.Reference => (TypeCategory.Dto, "DTO"),
                    _ => (TypeCategory.Array, "array"),
                };
                if (types.Categorise(property.JadeType) != required)
                {
                    throw Invalid(
                        $"DTO '{dto.Name}': property '{property.Name}' of kind {property.Kind} has jadeType " +
                        $"'{property.JadeType}', which is not a catalogue {expectation}.");
                }
            }
        }

        foreach (var array in document.Arrays)
        {
            if (types.Categorise(array.ItemType) == TypeCategory.Unresolved)
            {
                throw Unresolved($"array '{array.Name}': itemType", array.ItemType);
            }
        }
    }

    private static InvalidDataException Unresolved(string subject, string typeName) =>
        Invalid($"{subject} '{typeName}' does not name a catalogue DTO, array or wire type.");

    private static void ValidateStatusConstantReferences(
        IReadOnlyList<OperationDescriptor> operations,
        IReadOnlyList<StatusConstant> constants)
    {
        var declared = constants.Select(constant => (constant.Surface, constant.Name)).ToHashSet();
        foreach (var operation in operations)
        {
            foreach (var status in operation.Statuses)
            {
                if (status.Constant is { } name && !declared.Contains((operation.Surface, name)))
                {
                    throw Invalid(
                        $"operation '{operation.Id}': status {status.HttpStatus} names constant '{name}', " +
                        $"which is not a {operation.Surface} status constant.");
                }
            }
        }
    }

    private static void ValidateClassHierarchy(CatalogueDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in document.Dtos.Select(dto => dto.Name).Concat(document.Arrays.Select(array => array.Name)))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Invalid("a DTO or array has an empty name.");
            }

            if (document.WireTypes.ContainsKey(name) || !names.Add(name))
            {
                throw Invalid($"class name '{name}' is defined more than once across dtos, arrays and wireTypes.");
            }
        }

        var dtosByName = document.Dtos.ToDictionary(dto => dto.Name, StringComparer.Ordinal);
        var arrayNames = document.Arrays.Select(array => array.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var dto in document.Dtos)
        {
            if (dto.Superclass != DtoRootClass && !dtosByName.ContainsKey(dto.Superclass))
            {
                throw Invalid(
                    $"DTO '{dto.Name}': superclass '{dto.Superclass}' is neither a catalogue DTO nor the JADE root " +
                    $"class '{DtoRootClass}'.");
            }
        }

        foreach (var array in document.Arrays)
        {
            if (array.Superclass is { } superclass && !arrayNames.Contains(superclass) && !ArrayRootClasses.Contains(superclass))
            {
                throw Invalid(
                    $"array '{array.Name}': superclass '{superclass}' is neither a catalogue array nor a JADE " +
                    $"built-in array class ({string.Join(", ", ArrayRootClasses)}).");
            }
        }

        var superclassOf = SuperclassMap(document.Dtos, document.Arrays);
        foreach (var name in superclassOf.Keys)
        {
            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = name; current is not null && superclassOf.ContainsKey(current); current = superclassOf[current])
            {
                chain.Add(current);
                if (!seen.Add(current))
                {
                    var kind = dtosByName.ContainsKey(name) ? "DTO" : "array";
                    throw Invalid($"{kind} '{name}': superclass chain {string.Join(" -> ", chain)} is cyclic.");
                }
            }
        }

        foreach (var dto in document.Dtos)
        {
            ValidateFlattenedProperties(dto, dtosByName, superclassOf);
        }
    }

    private static void ValidateFlattenedProperties(
        DtoDefinition dto,
        Dictionary<string, DtoDefinition> dtosByName,
        IReadOnlyDictionary<string, string?> superclassOf)
    {
        var selfAndAncestors = SelfAndAncestors(dto.Name, superclassOf).ToHashSet(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in dto.Properties)
        {
            if (!propertyNames.Add(property.Name))
            {
                throw Invalid($"DTO '{dto.Name}': property '{property.Name}' is listed more than once.");
            }

            if (!selfAndAncestors.Contains(property.DeclaredIn))
            {
                throw Invalid(
                    $"DTO '{dto.Name}': property '{property.Name}' has declaredIn '{property.DeclaredIn}', which is " +
                    "neither the DTO nor one of its ancestors.");
            }
        }

        if (!dtosByName.TryGetValue(dto.Superclass, out var parent))
        {
            return;
        }

        // Inherited entries must be exactly the superclass's own flattened list, so checking against the
        // direct superclass covers the whole chain.
        foreach (var inherited in parent.Properties)
        {
            if (!dto.TryGetProperty(inherited.Name, out var copy)
                || copy.DeclaredIn != inherited.DeclaredIn
                || copy.JadeType != inherited.JadeType)
            {
                throw Invalid(
                    $"DTO '{dto.Name}': inherited property '{inherited.Name}' of superclass '{parent.Name}' " +
                    $"(declaredIn '{inherited.DeclaredIn}', jadeType '{inherited.JadeType}') is missing or differs; " +
                    "the property list must be flattened.");
            }
        }

        foreach (var property in dto.Properties)
        {
            if (property.DeclaredIn != dto.Name && !parent.TryGetProperty(property.Name, out _))
            {
                throw Invalid(
                    $"DTO '{dto.Name}': property '{property.Name}' has declaredIn '{property.DeclaredIn}', but " +
                    $"superclass '{parent.Name}' does not have it.");
            }
        }
    }


    private static void ValidatePropertyValues(CatalogueDocument document)
    {
        foreach (var dto in document.Dtos)
        {
            foreach (var property in dto.Properties)
            {
                var subject = $"DTO '{dto.Name}': property '{property.Name}'";
                if (property.Normalisation is { } normalisation && !NormalisationClasses.All.Contains(normalisation))
                {
                    throw Invalid(
                        $"{subject}: normalisation '{normalisation}' is not one of " +
                        $"{NormalisationClasses.N1}, {NormalisationClasses.N2}.");
                }

                // Conformance inventories every member of a class some body creates; only an abstract
                // class, which no body creates, has no reachability.
                if ((property.Reachability is null) != dto.Abstract)
                {
                    throw Invalid(
                        $"{subject}: reachability must be null exactly on an abstract DTO, " +
                        $"but the DTO is {(dto.Abstract ? "abstract" : "not abstract")}.");
                }

                if ((property.Evidence is null) != (property.Reachability is null))
                {
                    throw Invalid($"{subject}: evidence must be null exactly when reachability is null.");
                }

                if (property.Reachability is { } reachability)
                {
                    RequireReachability($"{subject}: Rest reachability", reachability.Rest);
                    RequireReachability($"{subject}: Soap reachability", reachability.Soap);
                }

                if (!IsClarkName(property.XmlType))
                {
                    throw Invalid($"{subject}: xmlType '{property.XmlType}' is not Clark notation {{uri}}local.");
                }
            }
        }

        foreach (var constant in document.StatusConstants)
        {
            RequireReachability($"status constant {constant.Surface} '{constant.Name}': reachability", constant.Reachability);
        }

        foreach (var array in document.Arrays)
        {
            if (array.SoapExposed
                && (string.IsNullOrWhiteSpace(array.SoapTypeName) || string.IsNullOrWhiteSpace(array.SoapItemElement)))
            {
                throw Invalid($"array '{array.Name}': a SOAP-exposed array needs a soapTypeName and a soapItemElement.");
            }
        }

        foreach (var (name, mapping) in document.WireTypes)
        {
            if (!IsClarkName(mapping.Xml))
            {
                throw Invalid($"wire type '{name}': xml '{mapping.Xml}' is not Clark notation {{uri}}local.");
            }
        }
    }

    private static void RequireReachability(string subject, string value)
    {
        if (!ReachabilityValues.All.Contains(value))
        {
            throw Invalid(
                $"{subject} '{value}' is not one of {ReachabilityValues.Reachable}, " +
                $"{ReachabilityValues.UnreachableInSource}, {ReachabilityValues.NotExposed}.");
        }
    }

    private static bool IsClarkName(string value)
    {
        if (!value.StartsWith('{'))
        {
            return false;
        }

        try
        {
            return XName.Get(value).NamespaceName.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or XmlException)
        {
            return false;
        }
    }

    private static void ValidateStatusConstants(IReadOnlyList<StatusConstant> constants)
    {
        var seen = new HashSet<(Surface Surface, string Name)>();
        foreach (var constant in constants)
        {
            if (!seen.Add((constant.Surface, constant.Name)))
            {
                throw Invalid($"status constant {constant.Surface} '{constant.Name}' is listed more than once.");
            }
        }

        var rest = constants.Count(constant => constant.Surface == Surface.Rest);
        var restHttp = constants.Count(constant =>
            constant.Surface == Surface.Rest && constant.Name.StartsWith(RestConstantPrefix, StringComparison.Ordinal));
        if (rest != ExpectedRestConstants || restHttp != ExpectedRestConstants)
        {
            throw Invalid(
                $"expected {ExpectedRestConstants} Rest {RestConstantPrefix}* status constants " +
                $"(Erewhon/ErehwhonExternalInterfacesSchema.scm:293-300) but found {rest} Rest constants, " +
                $"{restHttp} of them named {RestConstantPrefix}*.");
        }

        var soap = constants.Count(constant => constant.Surface == Surface.Soap);
        if (soap != ExpectedSoapConstants)
        {
            throw Invalid(
                $"expected {ExpectedSoapConstants} Soap status constants " +
                $"(Erewhon/ErehwhonExternalInterfacesSchema.scm:1139-1146) but found {soap}.");
        }
    }

    private static void ValidateAddresses(CatalogueAddresses addresses)
    {
        if (addresses.Rest.Port != ExpectedRestPort)
        {
            throw Invalid(
                $"addresses.rest.port is {addresses.Rest.Port}, but the ErewhonREST application listens on " +
                $"{ExpectedRestPort} (Erewhon/ErehwhonExternalInterfacesSchema.ddx:82).");
        }

        if (addresses.Soap.Port != ExpectedSoapPort)
        {
            throw Invalid(
                $"addresses.soap.port is {addresses.Soap.Port}, but the WebServiceHTTP application listens on " +
                $"{ExpectedSoapPort} (Erewhon/ErehwhonExternalInterfacesSchema.ddx:158).");
        }

        if (!string.Equals(addresses.Soap.EndpointPathAndQuery, ConsumerEndpointPathAndQuery, StringComparison.Ordinal))
        {
            throw Invalid(
                $"addresses.soap path and query '{addresses.Soap.EndpointPathAndQuery}' must equal the consumer " +
                $"endpoint '{ConsumerEndpointPathAndQuery}' (Erewhon/ErewhonWebServiceConsumerSchema.scm:1724).");
        }
    }

    private enum TypeCategory
    {
        Unresolved,
        WireType,
        Dto,
        Array,
    }

    /// <summary>Classifies a type name as a catalogue DTO, array or wire type.</summary>
    private sealed class TypeIndex(CatalogueDocument document)
    {
        private readonly HashSet<string> _dtoNames = document.Dtos.Select(dto => dto.Name).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _arrayNames = document.Arrays.Select(array => array.Name).ToHashSet(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, WireTypeMapping> _wireTypes = document.WireTypes;

        public TypeCategory Categorise(string typeName) =>
            _dtoNames.Contains(typeName) ? TypeCategory.Dto
            : _arrayNames.Contains(typeName) ? TypeCategory.Array
            : _wireTypes.ContainsKey(typeName) ? TypeCategory.WireType
            : TypeCategory.Unresolved;
    }

    /// <summary>The catalogue's top-level JSON object, one positional parameter per key.</summary>
    private sealed record CatalogueDocument(
        string CatalogueVersion,
        string Description,
        CatalogueSources Sources,
        CatalogueAddresses Addresses,
        IReadOnlyDictionary<string, WireTypeMapping> WireTypes,
        IReadOnlyList<OperationDescriptor> Operations,
        IReadOnlyList<DtoDefinition> Dtos,
        IReadOnlyList<ArrayDefinition> Arrays,
        IReadOnlyList<StatusConstant> StatusConstants);
}
