using System.Globalization;
using System.Net.Http.Headers;
using Erewhon.Contracts.Catalogue;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using CatalogueParameterLocation = Erewhon.Contracts.Catalogue.ParameterLocation;
using OpenApiParameterLocation = Microsoft.OpenApi.ParameterLocation;

namespace Erewhon.Facade.OpenApi;

/// <summary>
/// Registers the transformers that shape the facade's runtime OpenAPI document (served at
/// <c>/openapi/v1.json</c> on the admin listener) from the operation catalogue: the 13
/// <c>ErewhonRestService</c> operations with their JADE-typed path parameters, request bodies and catalogue
/// status codes, and one component schema per catalogue DTO and array.
/// </summary>
/// <remarks>
/// The document is checked in as <c>contracts/openapi/erewhon-rest.openapi.json</c> and compared with the
/// live one on every replay, where array order is significant. Everything here is therefore emitted in a
/// fixed order and holds no host, port, time or machine value.
/// </remarks>
public static class CatalogueOpenApiTransformers
{
    /// <summary>The OpenAPI document name, which gives the path <c>/openapi/v1.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>The document's <c>info.title</c>.</summary>
    public const string Title = "Erewhon REST facade";

    /// <summary>The document's <c>info.version</c>.</summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Pins the document to OpenAPI 3.1 and adds <see cref="CatalogueOperationTransformer"/> and
    /// <see cref="CatalogueDocumentTransformer"/>. Both are activated from dependency injection, so the
    /// application must register <see cref="OperationCatalogue"/> as a service.
    /// </summary>
    /// <param name="options">The options of the document being configured.</param>
    /// <returns><paramref name="options"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static OpenApiOptions AddCatalogueTransformers(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
        options.AddOperationTransformer<CatalogueOperationTransformer>();
        options.AddDocumentTransformer<CatalogueDocumentTransformer>();
        return options;
    }
}

/// <summary>
/// Finishes the document after every operation has been transformed: fixed title and version, no
/// <c>servers</c>, exactly the catalogue REST operations in ordinal path order, and one component schema per
/// catalogue DTO and array in ordinal name order.
/// </summary>
/// <remarks>Stateless and safe for concurrent document generation.</remarks>
public sealed class CatalogueDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly OperationCatalogue _catalogue;

    /// <summary>Creates the transformer over the catalogue the facade serves.</summary>
    /// <param name="catalogue">The operation catalogue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    public CatalogueDocumentTransformer(OperationCatalogue catalogue)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A catalogue REST operation is not in the document at its verb and path template, a catalogue type
    /// has no schema mapping, or a schema reference has no component.
    /// </exception>
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        document.Info = new OpenApiInfo
        {
            Title = CatalogueOpenApiTransformers.Title,
            Version = CatalogueOpenApiTransformers.Version,
        };

        // ASP.NET Core fills servers from the request's scheme and Host; null omits the key entirely.
        document.Servers = null;

        document.Paths = KeepCatalogueOperations(document.Paths);

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas = BuildComponentSchemas(document, document.Components.Schemas);
        return Task.CompletedTask;
    }

    // Keeps each operation whose operationId, path and verb are those of a catalogue REST operation, drops
    // everything else (anything not excluded from the description elsewhere), and orders paths ordinally.
    private OpenApiPaths KeepCatalogueOperations(OpenApiPaths? paths)
    {
        var expected = _catalogue.GetBySurface(Surface.Rest)
            .ToDictionary(operation => operation.JadeMethod, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var kept = new OpenApiPaths();

        IEnumerable<KeyValuePair<string, IOpenApiPathItem>> entries =
            paths ?? Enumerable.Empty<KeyValuePair<string, IOpenApiPathItem>>();
        foreach (var (path, item) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (item.Operations is not { } operations)
            {
                continue;
            }

            foreach (var method in operations.Keys.ToArray())
            {
                var operationId = operations[method].OperationId;
                if (operationId is not null
                    && expected.TryGetValue(operationId, out var descriptor)
                    && IsBoundTo(descriptor, path, method)
                    && found.Add(operationId))
                {
                    continue;
                }

                operations.Remove(method);
            }

            if (operations.Count > 0)
            {
                kept.Add(path, item);
            }
        }

        var missing = expected.Values
            .Where(operation => !found.Contains(operation.JadeMethod))
            .Select(operation =>
                $"{operation.Id} ({operation.Rest?.Verb} {operation.Rest?.PathTemplate}, operationId {operation.JadeMethod})")
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The OpenAPI document lacks catalogue REST operation(s) " + string.Join(", ", missing) +
                ". Each must be mapped at its catalogue verb and path template with WithName(<jadeMethod>) " +
                "and kept in the API description.");
        }

        return kept;
    }

    private static bool IsBoundTo(OperationDescriptor operation, string path, HttpMethod method) =>
        operation.Rest is { } rest
        && string.Equals(path, rest.PathTemplate, StringComparison.Ordinal)
        && string.Equals(method.Method, rest.Verb, StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, IOpenApiSchema> BuildComponentSchemas(
        OpenApiDocument document,
        IDictionary<string, IOpenApiSchema>? generated)
    {
        var schemas = new SortedDictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        foreach (var dto in _catalogue.Dtos)
        {
            // Properties are already flattened with the inherited guid, so no allOf chain is built. No
            // required list, maxLength or additionalProperties: the same component describes request
            // bodies (a name-only DTOAgent stays valid) and responses, and JADE's "__type" member must
            // stay permitted.
            var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            foreach (var property in dto.Properties.OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                properties.Add(
                    property.Name,
                    CatalogueSchemas.SchemaForJadeType(
                        _catalogue,
                        property.JadeType,
                        property.Kind,
                        document,
                        $"DTO '{dto.Name}' property '{property.Name}'"));
            }

            schemas.Add(dto.Name, new OpenApiSchema { Type = JsonSchemaType.Object, Properties = properties });
        }

        foreach (var array in _catalogue.Arrays)
        {
            schemas.Add(
                array.Name,
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = CatalogueSchemas.SchemaForJadeType(
                        _catalogue,
                        array.ItemType,
                        kind: null,
                        document,
                        $"array '{array.Name}' item type"),
                });
        }

        RetainReferencedGeneratedSchemas(document, schemas, generated);

        var ordered = new Dictionary<string, IOpenApiSchema>(schemas.Count, StringComparer.Ordinal);
        foreach (var (name, schema) in schemas)
        {
            ordered.Add(name, schema);
        }

        return ordered;
    }

    // A schema ASP.NET Core generated is kept only while the finished document still references it, and
    // every reference must end at a component, so the published contract never holds a dangling $ref.
    private static void RetainReferencedGeneratedSchemas(
        OpenApiDocument document,
        SortedDictionary<string, IOpenApiSchema> schemas,
        IDictionary<string, IOpenApiSchema>? generated)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        CatalogueSchemas.CollectReferences(document.Paths, referenced);
        foreach (var schema in schemas.Values)
        {
            CatalogueSchemas.CollectReferences(schema, referenced);
        }

        var pending = new Queue<string>(referenced.Order(StringComparer.Ordinal));
        while (pending.TryDequeue(out var name))
        {
            if (schemas.ContainsKey(name))
            {
                continue;
            }

            if (generated is null || !generated.TryGetValue(name, out var kept))
            {
                throw new InvalidOperationException(
                    $"The OpenAPI document references '#/components/schemas/{name}', which is neither a " +
                    "catalogue DTO or array nor a schema ASP.NET Core generated.");
            }

            schemas.Add(name, kept);
            var further = new HashSet<string>(StringComparer.Ordinal);
            CatalogueSchemas.CollectReferences(kept, further);
            foreach (var next in further.Order(StringComparer.Ordinal))
            {
                if (referenced.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }
    }
}

/// <summary>
/// Describes one catalogue REST operation from the catalogue alone: summary
/// <c>&lt;JadeClass&gt;.&lt;JadeMethod&gt;</c>, path parameters typed by their JADE types in signature order,
/// the DTO request body of <c>postCreateAgent</c> and <c>postCreateClient</c>, and one response per catalogue
/// HTTP status. The facade's endpoints bind nothing (route values stay untyped strings and the body is
/// streamed to the backend), so none of this can come from the endpoints themselves.
/// </summary>
/// <remarks>
/// An operation that is not a catalogue REST operation is left unchanged, and
/// <see cref="CatalogueDocumentTransformer"/> removes it. Stateless and safe for concurrent document
/// generation.
/// </remarks>
public sealed class CatalogueOperationTransformer : IOpenApiOperationTransformer
{
    private const string ResponseDescriptionSeparator = "; ";

    private readonly OperationCatalogue _catalogue;

    /// <summary>Creates the transformer over the catalogue the facade serves.</summary>
    /// <param name="catalogue">The operation catalogue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    public CatalogueOperationTransformer(OperationCatalogue catalogue)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The catalogue operation declares no status, more than one body parameter, or a type with no schema
    /// mapping.
    /// </exception>
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var jadeMethod = operation.OperationId ?? EndpointName(context);
        if (jadeMethod is null
            || !_catalogue.TryFind(Surface.Rest, jadeMethod, out var descriptor)
            || descriptor.Rest is not { } rest)
        {
            return Task.CompletedTask;
        }

        var host = context.Document;
        operation.OperationId = descriptor.JadeMethod;
        operation.Summary = $"{descriptor.JadeClass}.{descriptor.JadeMethod}";
        operation.Description = null;
        operation.Parameters = PathParameters(descriptor, host);
        operation.RequestBody = RequestBody(descriptor, rest, host);
        operation.Responses = Responses(descriptor, host);
        return Task.CompletedTask;
    }

    private static string? EndpointName(OpenApiOperationTransformerContext context) =>
        context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IEndpointNameMetadata>()
            .LastOrDefault()
            ?.EndpointName;

    // Replaces the untyped string parameters ApiExplorer infers from the route pattern; the catalogue types
    // integer and decimal segments (pCodeNumber, pMinPrice, pOffer, ...).
    private List<IOpenApiParameter>? PathParameters(OperationDescriptor descriptor, OpenApiDocument? host)
    {
        var parameters = descriptor.Parameters
            .Where(parameter => parameter.Location == CatalogueParameterLocation.Path)
            .OrderBy(parameter => parameter.Position)
            .Select(parameter => (IOpenApiParameter)new OpenApiParameter
            {
                Name = parameter.Name,
                In = OpenApiParameterLocation.Path,
                Required = true,
                Schema = CatalogueSchemas.SchemaForJadeType(
                    _catalogue,
                    parameter.JadeType,
                    kind: null,
                    host,
                    $"operation {descriptor.Id} parameter '{parameter.Name}'"),
            })
            .ToList();
        return parameters.Count == 0 ? null : parameters;
    }

    private OpenApiRequestBody? RequestBody(OperationDescriptor descriptor, RestBinding rest, OpenApiDocument? host)
    {
        var bodyParameters = descriptor.Parameters
            .Where(parameter => parameter.Location == CatalogueParameterLocation.Body)
            .ToArray();
        switch (bodyParameters.Length)
        {
            case 0:
                return null;
            case > 1:
                throw new InvalidOperationException(
                    $"Catalogue operation {descriptor.Id} declares {bodyParameters.Length} body parameters; " +
                    "a REST request carries at most one object parameter as its JSON body.");
        }

        var body = bodyParameters[0];
        var mediaType = rest.RequestContentType ?? CatalogueSchemas.JsonMediaType(_catalogue);
        return new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [mediaType] = new()
                {
                    Schema = CatalogueSchemas.SchemaForJadeType(
                        _catalogue,
                        body.JadeType,
                        kind: null,
                        host,
                        $"operation {descriptor.Id} body parameter '{body.Name}'"),
                },
            },
        };
    }

    private OpenApiResponses Responses(OperationDescriptor descriptor, OpenApiDocument? host)
    {
        if (descriptor.Statuses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Catalogue operation {descriptor.Id} declares no status, so it has no OpenAPI response.");
        }

        var mediaType = CatalogueSchemas.JsonMediaType(_catalogue);
        var responses = new OpenApiResponses();

        // GroupBy keeps catalogue order inside each group; OrderBy is stable.
        foreach (var group in descriptor.Statuses.GroupBy(status => status.HttpStatus).OrderBy(group => group.Key))
        {
            var description = string.Join(
                ResponseDescriptionSeparator,
                group.Select(status => status.Condition).Distinct(StringComparer.Ordinal));

            responses.Add(
                group.Key.ToString(CultureInfo.InvariantCulture),
                new OpenApiResponse
                {
                    Description = description,

                    // A null body is still sent as the literal JSON null with the JSON content type.
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        [mediaType] = new() { Schema = BodySchema(descriptor, group.Key, group, host) },
                    },
                });
        }

        return responses;
    }

    // One schema per distinct body of the status group, ordered by component name with the null body last;
    // several become anyOf.
    private IOpenApiSchema BodySchema(
        OperationDescriptor descriptor,
        int httpStatus,
        IEnumerable<OperationStatus> statuses,
        OpenApiDocument? host)
    {
        var owner = $"operation {descriptor.Id} status {httpStatus.ToString(CultureInfo.InvariantCulture)}";
        var bodies = new SortedDictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        var nullBody = false;

        foreach (var status in statuses)
        {
            if (CatalogueSchemas.IsNullBody(status.BodyType))
            {
                nullBody = true;
            }
            else if (status.RuntimeTypes is { Count: > 0 } runtimeTypes)
            {
                if (_catalogue.TryGetArray(status.BodyType, out _))
                {
                    // For an array body (getAllSales, getItemsByPrice) the runtime types are those of its
                    // items. Offering them as alternatives to the body itself would describe the array as a
                    // single object, so the body is an array whose items are any of the runtime types.
                    var itemTypes = runtimeTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    bodies.TryAdd(
                        $"{status.BodyType}<{string.Join(",", itemTypes)}>",
                        new OpenApiSchema
                        {
                            Type = JsonSchemaType.Array,
                            Items = OneOrAnyOf(itemTypes.Select(itemType => CatalogueSchemas.SchemaForJadeType(
                                _catalogue, itemType, kind: null, host, $"{owner} runtime type")).ToList()),
                        });
                }
                else
                {
                    foreach (var runtimeType in runtimeTypes)
                    {
                        bodies.TryAdd(
                            runtimeType,
                            CatalogueSchemas.SchemaForJadeType(_catalogue, runtimeType, kind: null, host, $"{owner} runtime type"));
                    }
                }
            }
            else
            {
                bodies.TryAdd(
                    status.BodyType,
                    CatalogueSchemas.SchemaForJadeType(_catalogue, status.BodyType, kind: null, host, $"{owner} body type"));
            }
        }

        var schemas = bodies.Values.ToList();
        if (nullBody)
        {
            schemas.Add(CatalogueSchemas.NullSchema());
        }

        return OneOrAnyOf(schemas);
    }

    private static IOpenApiSchema OneOrAnyOf(List<IOpenApiSchema> schemas) =>
        schemas.Count == 1 ? schemas[0] : new OpenApiSchema { AnyOf = schemas };
}

/// <summary>
/// The JADE-type to JSON Schema mapping and the reference helpers both transformers share. The only fixed
/// table is the JADE primitive switch; DTO and array names, wire-format descriptions and the JSON media type
/// come from the catalogue.
/// </summary>
file static class CatalogueSchemas
{
    // JSON text "\/Date(ms)\/" decodes to /Date(ms)/, which is the value the pattern applies to.
    private const string MsDatePattern = @"^/Date\(-?\d+([+-]\d{4})?\)/$";
    private const string MsDateDescription = "Microsoft JSON date \"\\/Date(<ms since epoch, UTC>)\\/\"";
    private const string Int32Format = "int32";
    private const string ByteMinimum = "0";
    private const string ByteMaximum = "255";

    /// <summary>
    /// Maps a JADE type to its schema: a component reference for a catalogue DTO or array (nullable for a
    /// <see cref="PropertyKind.Reference"/> property, because JADE writes null references), or the JSON form
    /// of a JADE primitive (nullable for a Date, TimeStamp or Binary <see cref="PropertyKind.Attribute"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The type is none of these; <paramref name="owner"/> names where it occurs.</exception>
    public static IOpenApiSchema SchemaForJadeType(
        OperationCatalogue catalogue,
        string jadeType,
        PropertyKind? kind,
        OpenApiDocument? host,
        string owner)
    {
        if (catalogue.TryGetDto(jadeType, out _) || catalogue.TryGetArray(jadeType, out _))
        {
            var reference = Ref(jadeType, host);
            return kind == PropertyKind.Reference
                ? new OpenApiSchema { AnyOf = [reference, NullSchema()] }
                : reference;
        }

        // A DTO attribute is written even when null, as JADE's null value for its type: "", 0, 0.00 and
        // false for String, Integer, Decimal and Boolean, but JSON null for Date, TimeStamp and Binary (as in
        // the baseline's "photo":null). Those three therefore admit null as DTO attributes, although the rule
        // that primitive properties are not nullable would exclude it; a path segment is never null.
        var attribute = kind == PropertyKind.Attribute;
        return jadeType switch
        {
            "String" => new OpenApiSchema { Type = JsonSchemaType.String },
            "Integer" => new OpenApiSchema { Type = JsonSchemaType.Integer, Format = Int32Format },
            "Decimal" => new OpenApiSchema { Type = JsonSchemaType.Number, Description = WireDescription(catalogue, jadeType) },
            "Boolean" => new OpenApiSchema { Type = JsonSchemaType.Boolean },
            "Binary" => new OpenApiSchema
            {
                Type = OrNull(JsonSchemaType.Array, attribute),
                Items = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = ByteMinimum, Maximum = ByteMaximum },
                Description = WireDescription(catalogue, jadeType),
            },
            "Date" or "TimeStamp" => new OpenApiSchema
            {
                Type = OrNull(JsonSchemaType.String, attribute),
                Pattern = MsDatePattern,
                Description = WireDescription(catalogue, jadeType) ?? MsDateDescription,
            },
            _ => throw new InvalidOperationException(
                $"JADE type '{jadeType}' of {owner} has no OpenAPI schema: it is neither a catalogue DTO or " +
                "array nor a JADE primitive (String, Integer, Decimal, Boolean, Binary, Date, TimeStamp)."),
        };
    }

    private static JsonSchemaType OrNull(JsonSchemaType type, bool nullable) =>
        nullable ? type | JsonSchemaType.Null : type;

    /// <summary>
    /// A reference to <c>#/components/schemas/&lt;componentName&gt;</c>, resolved through
    /// <paramref name="host"/> when given.
    /// </summary>
    public static OpenApiSchemaReference Ref(string componentName, OpenApiDocument? host) => new(componentName, host);

    /// <summary>The schema of a JSON <c>null</c>: <c>{ "type": "null" }</c>.</summary>
    public static OpenApiSchema NullSchema() => new() { Type = JsonSchemaType.Null };

    /// <summary>True when a status body type denotes the literal JSON <c>null</c> body: <c>null</c> (any case), null or empty.</summary>
    public static bool IsNullBody(string? bodyType) =>
        string.IsNullOrEmpty(bodyType) || string.Equals(bodyType, OperationStatus.NullBody, StringComparison.OrdinalIgnoreCase);

    /// <summary>The media type of the catalogue's REST JSON content type, without parameters (<c>application/json</c>).</summary>
    /// <exception cref="InvalidOperationException">The catalogue's REST content type has no media type.</exception>
    public static string JsonMediaType(OperationCatalogue catalogue)
    {
        var contentType = catalogue.Addresses.Rest.ContentType;
        return MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.MediaType is { Length: > 0 } mediaType
            ? mediaType
            : throw new InvalidOperationException($"The catalogue's REST content type '{contentType}' is not a media type.");
    }

    /// <summary>Adds the component name of every schema reference in the operations of <paramref name="paths"/>.</summary>
    public static void CollectReferences(OpenApiPaths? paths, ISet<string> into)
    {
        foreach (var item in paths?.Values ?? Enumerable.Empty<IOpenApiPathItem>())
        {
            CollectReferences(item.Parameters, into);
            foreach (var operation in item.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
            {
                CollectReferences(operation.Parameters, into);
                CollectReferences(operation.RequestBody?.Content, into);
                foreach (var response in operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>())
                {
                    CollectReferences(response.Content, into);
                }
            }
        }
    }

    /// <summary>Adds the component name of every schema reference within <paramref name="schema"/>, without following references.</summary>
    public static void CollectReferences(IOpenApiSchema? schema, ISet<string> into)
    {
        switch (schema)
        {
            case OpenApiSchemaReference reference:
                if (reference.Reference.Id is { } id)
                {
                    into.Add(id);
                }

                return;
            case OpenApiSchema inline:
                CollectReferences(inline.Items, into);
                CollectReferences(inline.Not, into);
                CollectReferences(inline.AdditionalProperties, into);
                CollectEach(inline.Properties?.Values, into);
                CollectEach(inline.AnyOf, into);
                CollectEach(inline.AllOf, into);
                CollectEach(inline.OneOf, into);
                return;
        }
    }

    private static void CollectReferences(IEnumerable<IOpenApiParameter>? parameters, ISet<string> into)
    {
        foreach (var parameter in parameters ?? Enumerable.Empty<IOpenApiParameter>())
        {
            CollectReferences(parameter.Schema, into);
            CollectReferences(parameter.Content, into);
        }
    }

    private static void CollectReferences(IDictionary<string, OpenApiMediaType>? content, ISet<string> into)
    {
        foreach (var mediaType in content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
        {
            CollectReferences(mediaType.Schema, into);
        }
    }

    private static void CollectEach(IEnumerable<IOpenApiSchema>? schemas, ISet<string> into)
    {
        foreach (var schema in schemas ?? Enumerable.Empty<IOpenApiSchema>())
        {
            CollectReferences(schema, into);
        }
    }

    private static string? WireDescription(OperationCatalogue catalogue, string jadeType) =>
        catalogue.WireTypes.TryGetValue(jadeType, out var wire) ? wire.Json : null;
}
