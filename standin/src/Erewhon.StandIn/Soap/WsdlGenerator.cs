using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Microsoft.AspNetCore.Http;

namespace Erewhon.StandIn.Soap;

/// <summary>
/// Builds the WSDL 1.1 document the stand-in serves on port 21001 at
/// <c>GET /jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP&amp;wsdl</c>:
/// the SOAP 1.1 document/literal wrapped description of the 9 <c>ErewhonWebService</c> operations.
/// </summary>
/// <remarks>
/// <para>
/// The repository holds no provider WSDL: the provider's <c>wsdl</c> property is empty
/// (Erewhon/ErehwhonExternalInterfacesSchema.scm:1135) and the consumer's WSDL path points outside the
/// repository (Erewhon/ErewhonWebServiceConsumerSchema.scm:1723). The document is therefore modelled on
/// JADE 2022 default conventions (KL-SOAP-WSDL): the consumer's namespace prefixes, wrapper, result and
/// array names, and the reference schema's DTO hierarchy and exposure list. Every operation, element,
/// type and action comes from the <see cref="OperationCatalogue"/>.
/// </para>
/// <para>
/// The bytes become a permanent artefact: harness capture stores them in the baseline and copies them,
/// labelled, to <c>contracts/wsdl/ErewhonWebService.wsdl</c>, which the facade publishes with only
/// <c>soap:address/@location</c> rewritten. The output therefore starts with the XML declaration, holds
/// no comment and exactly one <c>soap:address</c>, and is byte-identical for equal inputs on every call,
/// reset and restart: it follows catalogue order, uses the invariant culture and reads no clock or
/// business state.
/// </para>
/// <para>
/// The address is the only input that varies between requests, so each catalogue's document is built,
/// validated and serialized once, with an empty <c>soap:address/@location</c>, and kept as an immutable
/// template: the bytes before that value and the bytes after it. Each call writes only the address,
/// through an <see cref="XmlWriter"/> with the same settings, between the two, which yields the bytes a
/// single pass over the whole document would write. The templates live in a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed by catalogue instance, so the cache holds one
/// entry per live catalogue, releases it with the catalogue, and holds nothing derived from a request's
/// <c>Host</c>, which callers control. Each entry has its own lock: concurrent first calls for one
/// catalogue wait while one of them builds, so each catalogue's template is built once even under
/// concurrent first use, and later calls read it without locking. A catalogue that cannot be described
/// is not cached: every call builds it again and throws. Templates are never modified after they are
/// built, so the class is safe for concurrent use.
/// </para>
/// </remarks>
public static class WsdlGenerator
{
    private const string WsdlPrefix = "wsdl";
    private const string SoapBindingPrefix = "soap";
    private const string XsdPrefix = "xsd";
    private const string TnsPrefix = "tns";
    private const string JadeTypesPrefix = "s1";
    private const string XmlnsPrefix = "xmlns";

    private const string SoapHttpTransport = "http://schemas.xmlsoap.org/soap/http";
    private const string SupportedSoapVersion = "1.1";

    // The portType, binding and port are named <service>Soap, and the messages <op>SoapIn and
    // <op>SoapOut, each with the single document/literal wrapped part "parameters".
    private const string PortSuffix = "Soap";
    private const string InputMessageSuffix = "SoapIn";
    private const string OutputMessageSuffix = "SoapOut";
    private const string MessagePartName = "parameters";

    private const string Unbounded = "unbounded";
    private const string One = "1";

    // SoapEnvelopeWriter writes JADE's null binary as xsi:nil (getPhoto's photo for a client with no
    // photo sale), so a Binary attribute is the one nillable attribute.
    private const string BinaryJadeType = "Binary";

    // The tns simple type the consumer declares for Decimal[12,2] (Erewhon/ErewhonWebServiceConsumerSchema.scm:331):
    // a restriction of xsd:decimal whose facets come from the properties' precision and scale.
    private const string DecimalJadeType = "Decimal";
    private const string DecimalSchemaType = "decimal";

    // The soap:address attribute that carries the endpoint URL, the one value substituted per request.
    private const string AddressLocationAttribute = "location";

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        OmitXmlDeclaration = false,
        CheckCharacters = true,
        CloseOutput = false,
    };

    // The empty location attribute as the template's writer emits it. XmlWriter escapes '"' inside
    // attribute values and the document has no text or comment, so a raw '"' byte is only ever an
    // attribute delimiter, and every attribute name is a constant of this class: the sequence occurs
    // once, at soap:address.
    private static readonly byte[] EmptyAddressLocation = Encoding.UTF8.GetBytes($" {AddressLocationAttribute}=\"\"");

    private static readonly ConditionalWeakTable<OperationCatalogue, TemplateSlot> Templates = new();

    /// <summary>
    /// Returns the value of the WSDL's <c>soap:address/@location</c> for a request:
    /// <c>{scheme}://{Host}/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>,
    /// the consumer's <c>endPointURL</c> path and query (Erewhon/ErewhonWebServiceConsumerSchema.scm:1724)
    /// on the authority the caller addressed.
    /// </summary>
    /// <remarks>
    /// The <c>Host</c> header is used as sent. A request without one (HTTP/1.0) gets the connection's
    /// local address and port instead, an IPv6 address bracketed. The string is returned unescaped:
    /// <see cref="Generate"/> writes it through <see cref="XmlWriter"/>, which escapes <c>&amp;</c> and any
    /// other markup character, so no <c>Host</c> value can alter the document's structure.
    /// </remarks>
    /// <param name="request">The WSDL request.</param>
    /// <param name="catalogue">The catalogue whose SOAP address supplies the endpoint path and query.</param>
    /// <returns>The absolute endpoint URL.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The request has no <c>Host</c> and its connection no local address.</exception>
    public static string BuildAddressLocation(HttpRequest request, OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalogue);

        var authority = request.Host.Value is { Length: > 0 } host
            ? host
            : LocalAuthority(request.HttpContext.Connection);
        return string.Concat(request.Scheme, "://", authority, catalogue.Addresses.Soap.EndpointPathAndQuery);
    }

    /// <summary>
    /// Builds the WSDL for the catalogue's SOAP operations, with <paramref name="addressLocation"/> as the
    /// value of its single <c>soap:address/@location</c>.
    /// </summary>
    /// <remarks>
    /// The catalogue's document is built and validated once per catalogue instance, by the first call for
    /// it, while concurrent first calls wait for that build; later calls reuse it and write only
    /// <paramref name="addressLocation"/>. Every call returns the bytes of the whole document written in
    /// one pass with that address.
    /// </remarks>
    /// <param name="catalogue">The operation catalogue, normally <see cref="OperationCatalogue.Default"/>.</param>
    /// <param name="addressLocation">The endpoint URL, usually from <see cref="BuildAddressLocation"/>; written XML-escaped.</param>
    /// <returns>
    /// A new array of the UTF-8 document bytes, without a byte-order mark, starting
    /// <c>&lt;?xml version="1.0" encoding="utf-8"?&gt;</c> and a line break, then <c>&lt;wsdl:definitions</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> or <paramref name="addressLocation"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="addressLocation"/> is empty or whitespace, or holds a character or unpaired surrogate
    /// that XML cannot represent.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The catalogue cannot be described consistently: its SOAP address is not SOAP 1.1 wrapped in
    /// <see cref="SoapEnvelopeWriter.ServiceNamespace"/>, it lacks the <c>wsdl</c> or <c>soap</c>
    /// namespace, its decimal properties disagree on precision or scale, a type it names is undefined or
    /// lies in an undeclared namespace, or two schema components share a name; or the written document
    /// has no unique <c>soap:address/@location</c> to hold the address.
    /// </exception>
    public static byte[] Generate(OperationCatalogue catalogue, string addressLocation)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(addressLocation);

        // The table may run the factory more than once when first calls race, but it keeps one slot per
        // catalogue and hands that slot to every caller; the slot, not the table, builds the template.
        var template = Templates.GetValue(catalogue, static _ => new TemplateSlot()).GetOrBuild(catalogue);

        // The value is written by an XmlWriter with the document's own settings, so it gets the same
        // escaping of markup characters, the same entities for tab, carriage return and line feed, and
        // the same rejection of characters XML cannot hold as when the whole document is written in one pass.
        using var stream = new MemoryStream();
        using var writer = XmlWriter.Create(stream, Settings);
        writer.WriteStartElement("address");
        writer.WriteStartAttribute(AddressLocationAttribute);
        writer.Flush();
        var valueStart = (int)stream.Length;
        writer.WriteString(addressLocation);
        writer.Flush();
        var value = stream.GetBuffer().AsSpan(valueStart, (int)stream.Length - valueStart);

        var document = new byte[template.Prefix.Length + value.Length + template.Suffix.Length];
        template.Prefix.CopyTo(document, 0);
        value.CopyTo(document.AsSpan(template.Prefix.Length));
        template.Suffix.CopyTo(document, template.Prefix.Length + value.Length);
        return document;
    }

    private static string LocalAuthority(ConnectionInfo connection)
    {
        var address = connection.LocalIpAddress ?? throw new InvalidOperationException(
            "The WSDL request has no Host header and its connection no local address, so soap:address cannot be formed.");
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // An IPv6 zone index is written %25<zone> inside the brackets of a URI (RFC 6874).
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address.ToString().Replace("%", "%25", StringComparison.Ordinal)}]"
            : address.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{host}:{connection.LocalPort}");
    }

    // The catalogue's whole document written with an empty soap:address/@location, split between the
    // value's two quotes, so that template + written address equals the document written in one pass.
    private static WsdlTemplate BuildTemplate(OperationCatalogue catalogue)
    {
        var model = BuildModel(catalogue);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            WriteDocument(writer, model, string.Empty);
        }

        ReadOnlySpan<byte> document = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        var occurrences = document.Count(EmptyAddressLocation);
        if (occurrences != 1)
        {
            throw new InvalidOperationException(
                $"The WSDL written with an empty soap:address/@{AddressLocationAttribute} holds {occurrences} empty " +
                $"'{AddressLocationAttribute}' attributes instead of one, so the endpoint address has no unique place in it.");
        }

        // The prefix ends with the value's opening quote and the suffix starts with its closing quote.
        var valueAt = document.IndexOf(EmptyAddressLocation) + EmptyAddressLocation.Length - 1;
        return new WsdlTemplate(document[..valueAt].ToArray(), document[valueAt..].ToArray());
    }

    private static WsdlModel BuildModel(OperationCatalogue catalogue)
    {
        var soap = catalogue.Addresses.Soap;
        RequireSupportedAddress(soap);

        var dtos = catalogue.Dtos.Where(dto => dto.SoapExposed).ToArray();
        var complexTypes = new List<ComplexTypeSpec>();
        complexTypes.AddRange(dtos.Select(dto => DtoType(dto, catalogue)));

        // Every array the SOAP surface exposes, the consumer's ArrayOfDTOObject (scm:150) included,
        // because SoapEnvelopeWriter can write each of them.
        complexTypes.AddRange(catalogue.Arrays
            .Where(array => array is { SoapExposed: true, SoapTypeName: not null, SoapItemElement: not null })
            .Select(array => ArrayType(array, catalogue)));

        var operations = catalogue.GetBySurface(Surface.Soap);
        var wrappers = new List<WrapperSpec>();
        var operationSpecs = new List<OperationSpec>();
        foreach (var operation in operations)
        {
            var binding = operation.Soap ?? throw new InvalidOperationException(
                $"SOAP operation '{operation.Id}' has no SOAP binding, so it has no WSDL messages.");
            wrappers.Add(RequestWrapper(operation, binding, catalogue));
            wrappers.Add(ResponseWrapper(operation, binding, catalogue));
            operationSpecs.Add(new OperationSpec(operation.JadeMethod, binding.Action, binding.RequestElement, binding.ResponseElement));
        }

        var simpleTypes = DecimalSimpleTypes(dtos);
        RequireUniqueNames(simpleTypes, complexTypes, wrappers);

        var wsdlNamespace = RequireCatalogueNamespace(soap, WsdlPrefix);
        var soapBindingNamespace = RequireCatalogueNamespace(soap, SoapBindingPrefix);
        var namespaces = DeclaredNamespaces(soap, wsdlNamespace, soapBindingNamespace, simpleTypes, complexTypes, wrappers);
        return new WsdlModel(
            soap.ServiceName,
            soap.Style,
            soap.Use,
            wsdlNamespace,
            soapBindingNamespace,
            namespaces,
            simpleTypes,
            complexTypes,
            wrappers,
            operationSpecs);
    }

    private static void RequireSupportedAddress(SoapAddress soap)
    {
        if (!string.Equals(soap.TargetNamespace, SoapEnvelopeWriter.ServiceNamespace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The catalogue's SOAP target namespace '{soap.TargetNamespace}' differs from the namespace the stand-in " +
                $"writes, '{SoapEnvelopeWriter.ServiceNamespace}'.");
        }

        if (!string.Equals(soap.SoapVersion, SupportedSoapVersion, StringComparison.Ordinal) || !soap.Wrapped)
        {
            throw new InvalidOperationException(
                $"The catalogue describes SOAP {soap.SoapVersion} {(soap.Wrapped ? "wrapped" : "bare")} messages; the stand-in " +
                $"models only SOAP {SupportedSoapVersion} wrapped messages.");
        }
    }

    // complexType X: a root class holds its own written properties directly; every other class extends
    // its superclass and adds only the properties it declares, so the content model is the flattened
    // catalogue order SoapEnvelopeWriter writes (inherited members first).
    private static ComplexTypeSpec DtoType(DtoDefinition dto, OperationCatalogue catalogue)
    {
        var own = dto.Properties
            .Where(property => property.SoapExposed && string.Equals(property.DeclaredIn, dto.Name, StringComparison.Ordinal))
            .Select(property => PropertyElement(dto, property))
            .ToArray();
        XName? baseType = catalogue.TryGetDto(dto.Superclass, out var superclass)
            ? ServiceName(superclass.Name)
            : null;
        return new ComplexTypeSpec(dto.Name, dto.Abstract, baseType, own);
    }

    // Element forms per the consumer: an attribute is optional (scm:236-243); a reference or a
    // StringArray collection is required and nillable (scm:295, 333, 426).
    private static ElementSpec PropertyElement(DtoDefinition dto, DtoProperty property)
    {
        var type = XName.Get(property.XmlType);
        return property.Kind switch
        {
            PropertyKind.Attribute => new ElementSpec(
                property.Name, type, MinOccurs: 0, One,
                Nillable: string.Equals(property.JadeType, BinaryJadeType, StringComparison.Ordinal)),
            PropertyKind.Reference or PropertyKind.Collection => new ElementSpec(property.Name, type, MinOccurs: 1, One, Nillable: true),
            _ => throw new InvalidOperationException(
                $"DTO '{dto.Name}' property '{property.Name}' is of kind {property.Kind}, which has no WSDL element form."),
        };
    }

    private static ComplexTypeSpec ArrayType(ArrayDefinition array, OperationCatalogue catalogue)
    {
        var item = new ElementSpec(
            array.SoapItemElement!,
            TypeOf(array.ItemType, catalogue, $"array '{array.Name}' item"),
            MinOccurs: 0,
            Unbounded,
            Nillable: IsObjectType(array.ItemType, catalogue));
        return new ComplexTypeSpec(array.SoapTypeName!, Abstract: false, BaseType: null, [item]);
    }

    // Each parameter is required, as the consumer declares it (scm:477, 593-600); an object parameter is
    // also nillable (scm:624, 679).
    private static WrapperSpec RequestWrapper(OperationDescriptor operation, SoapBinding binding, OperationCatalogue catalogue)
    {
        var parameters = operation.Parameters
            .Where(parameter => parameter.Location == ParameterLocation.Element)
            .OrderBy(parameter => parameter.Position)
            .Select(parameter => new ElementSpec(
                parameter.Name,
                TypeOf(parameter.JadeType, catalogue, $"operation '{operation.Id}' parameter '{parameter.Name}'"),
                MinOccurs: 1,
                One,
                Nillable: IsObjectType(parameter.JadeType, catalogue)))
            .ToArray();
        return new WrapperSpec(binding.RequestElement, parameters);
    }

    // The result is typed by the reference schema's return type, not the consumer's HTTP_DTOObject.
    private static WrapperSpec ResponseWrapper(OperationDescriptor operation, SoapBinding binding, OperationCatalogue catalogue)
    {
        var result = new ElementSpec(
            binding.ResultElement,
            TypeOf(operation.ReturnType, catalogue, $"operation '{operation.Id}' result"),
            MinOccurs: 1,
            One,
            binding.ResultNillable);
        return new WrapperSpec(binding.ResponseElement, [result]);
    }

    // A DTO is the complex type of its own name, an array its SOAP type, both in the service namespace;
    // a primitive is its catalogue wire type.
    private static XName TypeOf(string typeName, OperationCatalogue catalogue, string subject)
    {
        if (catalogue.TryGetDto(typeName, out var dto))
        {
            return ServiceName(dto.Name);
        }

        if (catalogue.TryGetArray(typeName, out var array))
        {
            return array is { SoapExposed: true, SoapTypeName: { } soapTypeName }
                ? ServiceName(soapTypeName)
                : throw new InvalidOperationException(
                    $"The {subject} is of array type '{typeName}', which the WebServiceHTTP list does not expose.");
        }

        return catalogue.WireTypes.TryGetValue(typeName, out var wireType)
            ? XName.Get(wireType.Xml)
            : throw new InvalidOperationException($"The {subject} is of type '{typeName}', which the operation catalogue does not define.");
    }

    private static bool IsObjectType(string typeName, OperationCatalogue catalogue) =>
        catalogue.TryGetDto(typeName, out _) || catalogue.TryGetArray(typeName, out _);

    private static XName ServiceName(string localName) => XName.Get(localName, SoapEnvelopeWriter.ServiceNamespace);

    // One restriction of xsd:decimal per distinct service-namespace attribute type, in ordinal name
    // order, with totalDigits and fractionDigits from the declared Decimal[precision,scale].
    private static SimpleTypeSpec[] DecimalSimpleTypes(IEnumerable<DtoDefinition> dtos)
    {
        var groups = dtos
            .SelectMany(dto => dto.Properties
                .Where(property => property.SoapExposed && property.Kind == PropertyKind.Attribute)
                .Select(property => (Dto: dto, Property: property, Type: XName.Get(property.XmlType))))
            .Where(entry => string.Equals(entry.Type.NamespaceName, SoapEnvelopeWriter.ServiceNamespace, StringComparison.Ordinal))
            .GroupBy(entry => entry.Type.LocalName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        var simpleTypes = new List<SimpleTypeSpec>();
        foreach (var group in groups)
        {
            foreach (var (dto, property, _) in group)
            {
                if (!string.Equals(group.Key, DecimalSchemaType, StringComparison.Ordinal)
                    || !string.Equals(property.JadeType, DecimalJadeType, StringComparison.Ordinal)
                    || property.Precision is null
                    || property.Scale is null)
                {
                    throw new InvalidOperationException(
                        $"DTO '{dto.Name}' property '{property.Name}' (JADE type '{property.JadeType}') has the service-namespace " +
                        $"type '{group.Key}'; only '{DecimalSchemaType}' for {DecimalJadeType}[precision,scale] has a modelled restriction.");
                }
            }

            var facets = group.Select(entry => (Precision: entry.Property.Precision!.Value, Scale: entry.Property.Scale!.Value)).Distinct().ToArray();
            if (facets.Length != 1)
            {
                throw new InvalidOperationException(
                    $"The catalogue's '{group.Key}' properties disagree on precision and scale " +
                    $"({string.Join(", ", facets.Select(facet => $"[{facet.Precision},{facet.Scale}]"))}), so one simple type cannot describe them.");
            }

            simpleTypes.Add(new SimpleTypeSpec(
                group.Key, XName.Get(DecimalSchemaType, SoapEnvelopeWriter.XsdNamespace), facets[0].Precision, facets[0].Scale));
        }

        return [.. simpleTypes];
    }

    private static void RequireUniqueNames(
        IEnumerable<SimpleTypeSpec> simpleTypes, IEnumerable<ComplexTypeSpec> complexTypes, IEnumerable<WrapperSpec> wrappers)
    {
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in simpleTypes.Select(type => type.Name).Concat(complexTypes.Select(type => type.Name)))
        {
            if (!typeNames.Add(name))
            {
                throw new InvalidOperationException($"Two schema types are named '{name}'.");
            }
        }

        var elementNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wrapper in wrappers)
        {
            if (!elementNames.Add(wrapper.Name))
            {
                throw new InvalidOperationException($"Two wrapper elements are named '{wrapper.Name}'.");
            }
        }
    }

    // The WSDL and SOAP-binding namespaces come from the consumer's additionalInfo, which the catalogue
    // records (Erewhon/ErewhonWebServiceConsumerSchema.scm:1713-1721).
    private static string RequireCatalogueNamespace(SoapAddress soap, string prefix) =>
        soap.WsdlNamespaces.TryGetValue(prefix, out var uri) && uri.Length > 0
            ? uri
            : throw new InvalidOperationException(
                $"The catalogue's SOAP address records no '{prefix}' namespace ({soap.WsdlNamespacesSource}).");

    // The root declares wsdl, soap, xsd and tns in that order, then s1 only when a type lies in it.
    // Every referenced service-namespace type must be defined in the schema, and no type may lie in a
    // namespace without a declared prefix, so every QName attribute value resolves.
    private static (string Prefix, string Namespace)[] DeclaredNamespaces(
        SoapAddress soap,
        string wsdlNamespace,
        string soapBindingNamespace,
        IReadOnlyList<SimpleTypeSpec> simpleTypes,
        IReadOnlyList<ComplexTypeSpec> complexTypes,
        IReadOnlyList<WrapperSpec> wrappers)
    {
        RequireCatalogueAgrees(soap, XsdPrefix, SoapEnvelopeWriter.XsdNamespace);
        RequireCatalogueAgrees(soap, TnsPrefix, SoapEnvelopeWriter.ServiceNamespace);

        var defined = simpleTypes.Select(type => type.Name)
            .Concat(complexTypes.Select(type => type.Name))
            .ToHashSet(StringComparer.Ordinal);
        var referenced = simpleTypes.Select(type => type.BaseType)
            .Concat(complexTypes.Select(type => type.BaseType).OfType<XName>())
            .Concat(complexTypes.SelectMany(type => type.Sequence).Select(element => element.Type))
            .Concat(wrappers.SelectMany(wrapper => wrapper.Sequence).Select(element => element.Type));

        soap.WsdlNamespaces.TryGetValue(JadeTypesPrefix, out var jadeTypesNamespace);
        var usesJadeTypes = false;
        foreach (var type in referenced)
        {
            var uri = type.NamespaceName;
            if (string.Equals(uri, SoapEnvelopeWriter.ServiceNamespace, StringComparison.Ordinal))
            {
                if (!defined.Contains(type.LocalName))
                {
                    throw new InvalidOperationException(
                        $"The schema references type '{type.LocalName}' in '{uri}', which no exposed DTO, array or simple type defines.");
                }
            }
            else if (!string.IsNullOrEmpty(jadeTypesNamespace) && string.Equals(uri, jadeTypesNamespace, StringComparison.Ordinal))
            {
                usesJadeTypes = true;
            }
            else if (!string.Equals(uri, SoapEnvelopeWriter.XsdNamespace, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The schema references type '{type.LocalName}' in '{uri}', a namespace the WSDL declares no prefix for.");
            }
        }

        List<(string Prefix, string Namespace)> namespaces =
        [
            (WsdlPrefix, wsdlNamespace),
            (SoapBindingPrefix, soapBindingNamespace),
            (XsdPrefix, SoapEnvelopeWriter.XsdNamespace),
            (TnsPrefix, SoapEnvelopeWriter.ServiceNamespace),
        ];
        if (usesJadeTypes)
        {
            namespaces.Add((JadeTypesPrefix, jadeTypesNamespace!));
        }

        var distinct = namespaces.Select(entry => entry.Namespace).Distinct(StringComparer.Ordinal).Count();
        if (distinct != namespaces.Count)
        {
            throw new InvalidOperationException(
                $"The WSDL prefixes {string.Join(", ", namespaces.Select(entry => $"{entry.Prefix}={entry.Namespace}"))} " +
                "do not name distinct namespaces.");
        }

        return [.. namespaces];
    }

    private static void RequireCatalogueAgrees(SoapAddress soap, string prefix, string expected)
    {
        if (soap.WsdlNamespaces.TryGetValue(prefix, out var uri) && !string.Equals(uri, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The catalogue binds '{prefix}' to '{uri}', but the stand-in writes '{expected}' ({soap.WsdlNamespacesSource}).");
        }
    }

    // The explicit xmlns attributes pin the declaration order XmlWriter would otherwise choose itself.
    private static void WriteDocument(XmlWriter writer, WsdlModel model, string addressLocation)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement(WsdlPrefix, "definitions", model.WsdlNamespace);
        foreach (var (prefix, uri) in model.Namespaces)
        {
            writer.WriteAttributeString(XmlnsPrefix, prefix, null, uri);
        }

        writer.WriteAttributeString("targetNamespace", SoapEnvelopeWriter.ServiceNamespace);
        writer.WriteAttributeString("name", model.ServiceName);

        WriteTypes(writer, model);
        WriteMessages(writer, model);
        WritePortType(writer, model);
        WriteBinding(writer, model);
        WriteService(writer, model, addressLocation);

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteTypes(XmlWriter writer, WsdlModel model)
    {
        writer.WriteStartElement(WsdlPrefix, "types", model.WsdlNamespace);
        StartXsd(writer, "schema");
        writer.WriteAttributeString("targetNamespace", SoapEnvelopeWriter.ServiceNamespace);
        writer.WriteAttributeString("elementFormDefault", "qualified");

        foreach (var simpleType in model.SimpleTypes)
        {
            StartXsd(writer, "simpleType");
            writer.WriteAttributeString("name", simpleType.Name);
            StartXsd(writer, "restriction");
            writer.WriteAttributeString("base", model.Qualify(simpleType.BaseType));
            WriteFacet(writer, "totalDigits", simpleType.TotalDigits);
            WriteFacet(writer, "fractionDigits", simpleType.FractionDigits);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        foreach (var complexType in model.ComplexTypes)
        {
            StartXsd(writer, "complexType");
            writer.WriteAttributeString("name", complexType.Name);
            if (complexType.Abstract)
            {
                writer.WriteAttributeString("abstract", "true");
            }

            if (complexType.BaseType is null)
            {
                WriteSequence(writer, model, complexType.Sequence);
            }
            else
            {
                StartXsd(writer, "complexContent");
                StartXsd(writer, "extension");
                writer.WriteAttributeString("base", model.Qualify(complexType.BaseType));
                WriteSequence(writer, model, complexType.Sequence);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        foreach (var wrapper in model.Wrappers)
        {
            StartXsd(writer, "element");
            writer.WriteAttributeString("name", wrapper.Name);
            StartXsd(writer, "complexType");
            WriteSequence(writer, model, wrapper.Sequence);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteFacet(XmlWriter writer, string facet, int value)
    {
        StartXsd(writer, facet);
        writer.WriteAttributeString("value", value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    // An element-free sequence is written as the empty element <xsd:sequence />.
    private static void WriteSequence(XmlWriter writer, WsdlModel model, IReadOnlyList<ElementSpec> elements)
    {
        StartXsd(writer, "sequence");
        foreach (var element in elements)
        {
            StartXsd(writer, "element");
            writer.WriteAttributeString("minOccurs", element.MinOccurs.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("maxOccurs", element.MaxOccurs);
            writer.WriteAttributeString("name", element.Name);
            if (element.Nillable)
            {
                writer.WriteAttributeString("nillable", "true");
            }

            writer.WriteAttributeString("type", model.Qualify(element.Type));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteMessages(XmlWriter writer, WsdlModel model)
    {
        foreach (var operation in model.Operations)
        {
            WriteMessage(writer, model, operation.Name + InputMessageSuffix, operation.RequestElement);
            WriteMessage(writer, model, operation.Name + OutputMessageSuffix, operation.ResponseElement);
        }
    }

    private static void WriteMessage(XmlWriter writer, WsdlModel model, string messageName, string elementName)
    {
        writer.WriteStartElement(WsdlPrefix, "message", model.WsdlNamespace);
        writer.WriteAttributeString("name", messageName);
        writer.WriteStartElement(WsdlPrefix, "part", model.WsdlNamespace);
        writer.WriteAttributeString("name", MessagePartName);
        writer.WriteAttributeString("element", model.Qualify(ServiceName(elementName)));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WritePortType(XmlWriter writer, WsdlModel model)
    {
        writer.WriteStartElement(WsdlPrefix, "portType", model.WsdlNamespace);
        writer.WriteAttributeString("name", model.PortName);
        foreach (var operation in model.Operations)
        {
            writer.WriteStartElement(WsdlPrefix, "operation", model.WsdlNamespace);
            writer.WriteAttributeString("name", operation.Name);
            writer.WriteStartElement(WsdlPrefix, "input", model.WsdlNamespace);
            writer.WriteAttributeString("message", model.Qualify(ServiceName(operation.Name + InputMessageSuffix)));
            writer.WriteEndElement();
            writer.WriteStartElement(WsdlPrefix, "output", model.WsdlNamespace);
            writer.WriteAttributeString("message", model.Qualify(ServiceName(operation.Name + OutputMessageSuffix)));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    // SOAP 1.1 only: the consumer sets _useSOAP12 = false (Erewhon/ErewhonWebServiceConsumerSchema.scm:1726).
    private static void WriteBinding(XmlWriter writer, WsdlModel model)
    {
        writer.WriteStartElement(WsdlPrefix, "binding", model.WsdlNamespace);
        writer.WriteAttributeString("name", model.PortName);
        writer.WriteAttributeString("type", model.Qualify(ServiceName(model.PortName)));
        writer.WriteStartElement(SoapBindingPrefix, "binding", model.SoapBindingNamespace);
        writer.WriteAttributeString("transport", SoapHttpTransport);
        writer.WriteAttributeString("style", model.Style);
        writer.WriteEndElement();

        foreach (var operation in model.Operations)
        {
            writer.WriteStartElement(WsdlPrefix, "operation", model.WsdlNamespace);
            writer.WriteAttributeString("name", operation.Name);
            writer.WriteStartElement(SoapBindingPrefix, "operation", model.SoapBindingNamespace);
            writer.WriteAttributeString("soapAction", operation.Action);
            writer.WriteAttributeString("style", model.Style);
            writer.WriteEndElement();
            WriteLiteralBody(writer, model, "input");
            WriteLiteralBody(writer, model, "output");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteLiteralBody(XmlWriter writer, WsdlModel model, string direction)
    {
        writer.WriteStartElement(WsdlPrefix, direction, model.WsdlNamespace);
        writer.WriteStartElement(SoapBindingPrefix, "body", model.SoapBindingNamespace);
        writer.WriteAttributeString("use", model.Use);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    // The document's only soap:address, whose location the facade rewrites per request.
    private static void WriteService(XmlWriter writer, WsdlModel model, string addressLocation)
    {
        writer.WriteStartElement(WsdlPrefix, "service", model.WsdlNamespace);
        writer.WriteAttributeString("name", model.ServiceName);
        writer.WriteStartElement(WsdlPrefix, "port", model.WsdlNamespace);
        writer.WriteAttributeString("name", model.PortName);
        writer.WriteAttributeString("binding", model.Qualify(ServiceName(model.PortName)));
        writer.WriteStartElement(SoapBindingPrefix, "address", model.SoapBindingNamespace);
        writer.WriteAttributeString(AddressLocationAttribute, addressLocation);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void StartXsd(XmlWriter writer, string localName) =>
        writer.WriteStartElement(XsdPrefix, localName, SoapEnvelopeWriter.XsdNamespace);

    /// <summary>An <c>xsd:element</c> inside a sequence; <c>MaxOccurs</c> is <c>1</c> or <c>unbounded</c>.</summary>
    private sealed record ElementSpec(string Name, XName Type, int MinOccurs, string MaxOccurs, bool Nillable);

    /// <summary>A restriction of an XML Schema built-in type with digit facets.</summary>
    private sealed record SimpleTypeSpec(string Name, XName BaseType, int TotalDigits, int FractionDigits);

    /// <summary>A named complex type: a root sequence when <c>BaseType</c> is null, otherwise an extension of it.</summary>
    private sealed record ComplexTypeSpec(string Name, bool Abstract, XName? BaseType, IReadOnlyList<ElementSpec> Sequence);

    /// <summary>A global wrapper element with an anonymous complex type holding <c>Sequence</c>.</summary>
    private sealed record WrapperSpec(string Name, IReadOnlyList<ElementSpec> Sequence);

    /// <summary>One WSDL operation: its name, SOAP action and wrapper elements.</summary>
    private sealed record OperationSpec(string Name, string Action, string RequestElement, string ResponseElement);

    /// <summary>Everything the document holds, resolved and checked before the first byte is written.</summary>
    private sealed record WsdlModel(
        string ServiceName,
        string Style,
        string Use,
        string WsdlNamespace,
        string SoapBindingNamespace,
        IReadOnlyList<(string Prefix, string Namespace)> Namespaces,
        IReadOnlyList<SimpleTypeSpec> SimpleTypes,
        IReadOnlyList<ComplexTypeSpec> ComplexTypes,
        IReadOnlyList<WrapperSpec> Wrappers,
        IReadOnlyList<OperationSpec> Operations)
    {
        public string PortName => ServiceName + PortSuffix;

        public string Qualify(XName name)
        {
            foreach (var (prefix, uri) in Namespaces)
            {
                if (string.Equals(uri, name.NamespaceName, StringComparison.Ordinal))
                {
                    return $"{prefix}:{name.LocalName}";
                }
            }

            throw new InvalidOperationException($"No prefix is declared for the namespace of '{name}'.");
        }
    }

    /// <summary>
    /// A catalogue's serialized document around its <c>soap:address/@location</c> value: <c>Prefix</c> ends
    /// with the value's opening quote and <c>Suffix</c> starts with its closing quote. Neither array is
    /// modified or handed out after it is built.
    /// </summary>
    private sealed record WsdlTemplate(byte[] Prefix, byte[] Suffix);

    /// <summary>
    /// One catalogue's entry in <see cref="Templates"/>: empty until that catalogue's template is built,
    /// then holding it for as long as the catalogue lives. It holds no reference to the catalogue, which
    /// each call passes in, so the entry never keeps its own key reachable.
    /// </summary>
    private sealed class TemplateSlot
    {
        private readonly Lock _gate = new();
        private volatile WsdlTemplate? _template;

        /// <summary>
        /// Returns the catalogue's template, building it if no call has built it yet. A built template is
        /// read without locking. Calls that find none take the slot's gate in turn, and each looks again
        /// before building, so one build serves every concurrent first call. A build that throws publishes
        /// nothing: its exception reaches its caller and the next call builds again.
        /// </summary>
        public WsdlTemplate GetOrBuild(OperationCatalogue catalogue)
        {
            var template = _template;
            if (template is not null)
            {
                return template;
            }

            lock (_gate)
            {
                return _template ??= BuildTemplate(catalogue);
            }
        }
    }
}
