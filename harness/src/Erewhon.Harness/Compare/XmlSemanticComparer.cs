using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;

namespace Erewhon.Harness.Compare;

/// <summary>
/// What an XML body is, which decides the structural role of each of its elements: which elements are
/// element-only containers, whose whitespace-only text is ignored, and which carry a catalogue type.
/// </summary>
/// <remarks>
/// The hierarchy is closed: only the four nested records derive from it. Each record is immutable, and
/// its properties have no setters, so a <c>with</c> expression cannot bypass the validation of the
/// operation-bearing contexts.
/// </remarks>
public abstract record XmlComparisonContext
{
    private XmlComparisonContext()
    {
    }

    /// <summary>A body with no catalogue typing: only the SOAP envelope elements are element-only containers.</summary>
    public static XmlComparisonContext Untyped { get; } = new UntypedContext();

    /// <summary>Returns the context of a SOAP 1.1 response envelope of <paramref name="operation"/>.</summary>
    /// <param name="operation">A SOAP catalogue operation with a <see cref="OperationDescriptor.Soap"/> binding.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is not a SOAP operation or has no SOAP binding.</exception>
    public static XmlComparisonContext SoapResponse(OperationDescriptor operation) => new SoapResponseContext(operation);

    /// <summary>Returns the context of a SOAP 1.1 request envelope of <paramref name="operation"/>.</summary>
    /// <param name="operation">A SOAP catalogue operation with a <see cref="OperationDescriptor.Soap"/> binding.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is not a SOAP operation or has no SOAP binding.</exception>
    public static XmlComparisonContext SoapRequest(OperationDescriptor operation) => new SoapRequestContext(operation);

    /// <summary>Returns the context of a WSDL 1.1 document.</summary>
    /// <param name="excludeSoapAddressLocation">
    /// True to leave the value of <c>soap:address/@location</c> uncompared; the attribute must still be
    /// present on both sides.
    /// </param>
    public static XmlComparisonContext Wsdl(bool excludeSoapAddressLocation) => new WsdlContext(excludeSoapAddressLocation);

    private static OperationDescriptor RequireSoap(OperationDescriptor operation, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(operation, parameterName);
        if (operation.Surface != Surface.Soap || operation.Soap is null)
        {
            throw new ArgumentException(
                $"Operation '{operation.Id}' is a {operation.Surface} operation without a SOAP binding; "
                + "a SOAP comparison context needs a SOAP operation.",
                parameterName);
        }

        return operation;
    }

    /// <summary>
    /// A SOAP response: the <c>Body</c> child named <see cref="SoapBinding.ResponseElement"/> is an
    /// element-only wrapper, and its child named <see cref="SoapBinding.ResultElement"/> is typed with the
    /// operation's <see cref="OperationDescriptor.ReturnType"/>.
    /// </summary>
    /// <param name="Operation">The SOAP operation whose response is compared.</param>
    public sealed record SoapResponseContext(OperationDescriptor Operation) : XmlComparisonContext
    {
        /// <summary>The SOAP operation whose response is compared; its <see cref="OperationDescriptor.Soap"/> is never null.</summary>
        public OperationDescriptor Operation { get; } = RequireSoap(Operation, nameof(Operation));
    }

    /// <summary>
    /// A SOAP request: the <c>Body</c> child named <see cref="SoapBinding.RequestElement"/> is an
    /// element-only wrapper, and each of its children named after an element parameter is typed with
    /// that parameter's JADE type.
    /// </summary>
    /// <param name="Operation">The SOAP operation whose request is compared.</param>
    public sealed record SoapRequestContext(OperationDescriptor Operation) : XmlComparisonContext
    {
        /// <summary>The SOAP operation whose request is compared; its <see cref="OperationDescriptor.Soap"/> is never null.</summary>
        public OperationDescriptor Operation { get; } = RequireSoap(Operation, nameof(Operation));
    }

    /// <summary>
    /// A WSDL document: every element in the WSDL, WSDL SOAP binding or XML Schema namespace other than
    /// <c>documentation</c> is an element-only container, and the unqualified <c>type</c>,
    /// <c>element</c>, <c>message</c>, <c>binding</c> and <c>base</c> attributes are compared as QNames.
    /// </summary>
    /// <param name="ExcludeSoapAddressLocation">True to leave the value of <c>soap:address/@location</c> uncompared.</param>
    public sealed record WsdlContext(bool ExcludeSoapAddressLocation) : XmlComparisonContext
    {
        /// <summary>True to leave the value of <c>soap:address/@location</c> uncompared; its presence is always compared.</summary>
        public bool ExcludeSoapAddressLocation { get; } = ExcludeSoapAddressLocation;
    }

    /// <summary>A body with no catalogue typing.</summary>
    public sealed record UntypedContext : XmlComparisonContext;
}

/// <summary>
/// Compares two XML bodies semantically: SOAP 1.1 envelopes typed by the operation's catalogue
/// descriptor, WSDL documents, or untyped XML. Neither body is rewritten.
/// </summary>
/// <remarks>
/// <para>
/// Both bodies are loaded with whitespace preserved, DTDs prohibited and no resolver. The XML
/// declaration, comments and processing instructions are ignored everywhere; CDATA sections are text.
/// </para>
/// <para>
/// Each element has a role known from the context and its position, never from its content. In an
/// element-only container (the SOAP <c>Envelope</c>, <c>Header</c>, <c>Body</c> and <c>Fault</c>; the
/// operation's request or response wrapper; every element whose catalogue type is a DTO or an array; and,
/// in a WSDL, every WSDL, WSDL SOAP binding and XML Schema element except <c>documentation</c>)
/// whitespace-only text is ignored. Every other element keeps all of its text exactly, so
/// <c>&lt;v&gt;a&lt;b/&gt; &lt;/v&gt;</c> differs from <c>&lt;v&gt;a&lt;b/&gt;&lt;/v&gt;</c>, while
/// <c>&lt;a&gt;&lt;/a&gt;</c> equals <c>&lt;a/&gt;</c>.
/// </para>
/// <para>
/// Elements and attributes are compared by expanded name, so prefix choice is irrelevant; attributes form a
/// set excluding namespace declarations; QName-valued attributes compare by resolved expanded name;
/// differently named siblings form an unordered set of name groups, and same-named siblings pair in
/// document order. At a SOAP-exposed DTO property that a normalisation rule targets, the pairwise rule of
/// <see cref="Normaliser"/> decides equality.
/// </para>
/// <para>
/// Paths start at <c>/</c> with the root's local name, for example
/// <c>/Envelope/Body/getAgentResponse/getAgentResult/name/text()</c>,
/// <c>/Envelope/Body/getAgentNamesResponse/getAgentNamesResult/DTOName[2]/name/text()</c> or
/// <c>/definitions/service/port/address/@location</c>. The returned list is in
/// <see cref="Discrepancy.CanonicalOrder"/>, so attribute and sibling order never change it. The instance
/// holds only immutable state and performs no I/O, so concurrent <see cref="Compare"/> calls are safe.
/// </para>
/// </remarks>
/// <param name="catalogue">The operation catalogue that types SOAP bodies.</param>
/// <param name="normaliser">The normaliser applied at targeted DTO properties.</param>
public sealed class XmlSemanticComparer(OperationCatalogue catalogue, Normaliser normaliser)
{
    private const string RootPath = "/";

    private readonly OperationCatalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly Normaliser _normaliser = normaliser ?? throw new ArgumentNullException(nameof(normaliser));

    /// <summary>
    /// Compares <paramref name="observed"/> with <paramref name="baseline"/> in <paramref name="context"/>.
    /// </summary>
    /// <param name="baseline">The baseline XML text.</param>
    /// <param name="observed">The observed XML text.</param>
    /// <param name="context">What the bodies are, which types their elements.</param>
    /// <returns>
    /// Every discrepancy in <see cref="Discrepancy.CanonicalOrder"/>; empty when the bodies are equal.
    /// When either body does not load as XML, exactly one <c>malformed</c> discrepancy at <c>/</c> whose
    /// value is the raw text on each side that failed and absent on a side that loaded.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public IReadOnlyList<Discrepancy> Compare(string baseline, string observed, XmlComparisonContext context)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(context);

        var baselineRoot = TryLoadRoot(baseline);
        var observedRoot = TryLoadRoot(observed);
        if (baselineRoot is null || observedRoot is null)
        {
            return
            [
                new Discrepancy(
                    RootPath,
                    DiscrepancyKinds.Malformed,
                    baselineRoot is null ? DiscrepancyValue.Literal(baseline) : DiscrepancyValue.Absent,
                    observedRoot is null ? DiscrepancyValue.Literal(observed) : DiscrepancyValue.Absent),
            ];
        }

        return Compare(baselineRoot, observedRoot, context);
    }

    /// <summary>
    /// Compares two bodies that have already been loaded, so a caller that loaded them to judge their
    /// well-formedness does not load them again. The result is the one
    /// <see cref="Compare(string, string, XmlComparisonContext)"/> returns for the same two texts when both load.
    /// </summary>
    /// <param name="baselineRoot">The baseline root element as <see cref="TryLoadRoot"/> returned it; it is not modified.</param>
    /// <param name="observedRoot">The observed root element as <see cref="TryLoadRoot"/> returned it; it is not modified.</param>
    /// <param name="context">What the bodies are, which types their elements.</param>
    /// <returns>Every discrepancy in <see cref="Discrepancy.CanonicalOrder"/>; empty when the bodies are equal.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal IReadOnlyList<Discrepancy> Compare(XElement baselineRoot, XElement observedRoot, XmlComparisonContext context)
    {
        ArgumentNullException.ThrowIfNull(baselineRoot);
        ArgumentNullException.ThrowIfNull(observedRoot);
        ArgumentNullException.ThrowIfNull(context);

        if (baselineRoot.Name != observedRoot.Name)
        {
            return
            [
                new Discrepancy(
                    RootPath,
                    DiscrepancyKinds.Value,
                    DiscrepancyValue.Literal(baselineRoot.Name.ToString()),
                    DiscrepancyValue.Literal(observedRoot.Name.ToString())),
            ];
        }

        var walk = new Walk(_catalogue, _normaliser, context);
        walk.CompareElements(RootPath + baselineRoot.Name.LocalName, baselineRoot, observedRoot, walk.RootRole(baselineRoot));
        return walk.Result();
    }

    /// <summary>
    /// Loads <paramref name="text"/> with whitespace preserved, DTDs prohibited and no resolver, and returns
    /// its root element; null when it does not load. Whitespace must be preserved: the default load drops
    /// whitespace-only text even inside leaf elements, which would make <c>&lt;a&gt; &lt;/a&gt;</c> equal
    /// <c>&lt;a/&gt;</c>. The one load every XML body comparison uses, well-formedness checks included.
    /// </summary>
    internal static XElement? TryLoadRoot(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>What an element is, which decides how its text and children are compared.</summary>
    private enum RoleKind
    {
        /// <summary>Keeps every text node; its children are typed by the context only.</summary>
        Leaf,

        /// <summary>An element-only container with no catalogue type: a SOAP envelope element or a WSDL element.</summary>
        Container,

        /// <summary>The SOAP <c>Body</c>, whose request or response wrapper child the context names.</summary>
        Body,

        /// <summary>The <c>&lt;op&gt;Response</c> wrapper, whose result child is typed with the return type.</summary>
        ResponseWrapper,

        /// <summary>The <c>&lt;op&gt;</c> request wrapper, whose parameter children are typed with their JADE types.</summary>
        RequestWrapper,

        /// <summary>A DTO-typed element, whose children are typed by the runtime class's properties.</summary>
        Dto,

        /// <summary>An array-typed element, whose children are typed with the item type.</summary>
        Array,
    }

    /// <summary>An element's role, with the runtime DTO class or the array type it carries.</summary>
    private readonly record struct Role(RoleKind Kind, DtoDefinition? Dto, ArrayDefinition? Array)
    {
        public static Role Leaf => new(RoleKind.Leaf, null, null);

        public static Role Container => new(RoleKind.Container, null, null);

        public static Role Body => new(RoleKind.Body, null, null);

        public static Role ResponseWrapper => new(RoleKind.ResponseWrapper, null, null);

        public static Role RequestWrapper => new(RoleKind.RequestWrapper, null, null);

        /// <summary>True for every element-only container, whose whitespace-only text is ignored.</summary>
        public bool IsElementOnly => Kind != RoleKind.Leaf;

        public static Role ForDto(DtoDefinition dto) => new(RoleKind.Dto, dto, null);

        public static Role ForArray(ArrayDefinition array) => new(RoleKind.Array, null, array);
    }

    /// <summary>
    /// An attribute value as compared: the resolved expanded name of a QName-valued attribute in
    /// <c>{uri}local</c> form, or the exact text otherwise. A QName that does not resolve is kept as raw text
    /// and never equals a resolved one, even when the texts coincide.
    /// </summary>
    private readonly record struct AttributeValue(bool IsResolvedQName, string Text);

    /// <summary>The state of one comparison: the context's facts and the discrepancies found so far.</summary>
    private sealed class Walk
    {
        private static readonly XNamespace SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace WsdlNamespace = "http://schemas.xmlsoap.org/wsdl/";
        private static readonly XNamespace WsdlSoapNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
        private static readonly XNamespace XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";
        private static readonly XNamespace XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

        private static readonly XName XsiType = XsiNamespace + "type";
        private static readonly XName XsiNil = XsiNamespace + "nil";
        private static readonly XName SoapAddress = WsdlSoapNamespace + "address";
        private static readonly XName LocationAttribute = XName.Get("location");

        // The WSDL 1.1 and XML Schema attributes whose values are QNames; prefix choice must not matter.
        private static readonly string[] WsdlQNameAttributes = ["type", "element", "message", "binding", "base"];

        // The one element of the WSDL and XML Schema vocabularies that holds prose rather than structure.
        private const string DocumentationElement = "documentation";

        private readonly OperationCatalogue _catalogue;
        private readonly Normaliser _normaliser;
        private readonly OperationDescriptor? _response;
        private readonly OperationDescriptor? _request;
        private readonly bool _wsdl;
        private readonly bool _excludeSoapAddressLocation;
        private readonly List<Discrepancy> _discrepancies = [];

        public Walk(OperationCatalogue catalogue, Normaliser normaliser, XmlComparisonContext context)
        {
            _catalogue = catalogue;
            _normaliser = normaliser;
            switch (context)
            {
                case XmlComparisonContext.SoapResponseContext response:
                    _response = response.Operation;
                    break;
                case XmlComparisonContext.SoapRequestContext request:
                    _request = request.Operation;
                    break;
                case XmlComparisonContext.WsdlContext wsdl:
                    _wsdl = true;
                    _excludeSoapAddressLocation = wsdl.ExcludeSoapAddressLocation;
                    break;
                case XmlComparisonContext.UntypedContext:
                    break;
                default:
                    throw new ArgumentException(
                        $"Comparison context '{context.GetType().Name}' is not one of the XmlComparisonContext factories' contexts.",
                        nameof(context));
            }
        }

        /// <summary>Returns the discrepancies found, in canonical order.</summary>
        public ReadOnlyCollection<Discrepancy> Result()
        {
            _discrepancies.Sort(Discrepancy.CanonicalOrder);
            return _discrepancies.AsReadOnly();
        }

        /// <summary>The role of a root element, which only its name decides.</summary>
        public Role RootRole(XElement root) => EnvelopeRole(root.Name) ?? GenericRole(root.Name);

        /// <summary>Compares two elements of the same expanded name at <paramref name="path"/>: attributes, text, then children.</summary>
        public void CompareElements(string path, XElement baseline, XElement observed, Role role)
        {
            // Turns a pathologically deep document into an exception the caller can report, instead of a
            // stack overflow that would end the process.
            RuntimeHelpers.EnsureSufficientExecutionStack();

            CompareAttributes(path, baseline, observed);
            CompareText(path, baseline, observed, role.IsElementOnly);
            CompareChildren(path, baseline, observed, role);
        }

        private void Add(string path, string kind, DiscrepancyValue baseline, DiscrepancyValue observed) =>
            _discrepancies.Add(new Discrepancy(path, kind, baseline, observed));

        // Roles. A role comes from the context, the parent's role and the element's own name, never from
        // the element's content: a DTO element stays element-only even when it holds stray text.

        private static Role? EnvelopeRole(XName name)
        {
            if (name.Namespace != SoapEnvelopeNamespace)
            {
                return null;
            }

            return name.LocalName switch
            {
                "Body" => Role.Body,
                "Envelope" or "Header" or "Fault" => Role.Container,
                _ => null,
            };
        }

        private Role GenericRole(XName name) =>
            _wsdl
            && (name.Namespace == WsdlNamespace || name.Namespace == WsdlSoapNamespace || name.Namespace == XmlSchemaNamespace)
            && !string.Equals(name.LocalName, DocumentationElement, StringComparison.Ordinal)
                ? Role.Container
                : Role.Leaf;

        /// <summary>The role of <paramref name="child"/>, the BASELINE element of a pair, under a parent of role <paramref name="parent"/>.</summary>
        private Role ChildRole(Role parent, XElement child)
        {
            if (EnvelopeRole(child.Name) is { } envelope)
            {
                return envelope;
            }

            var local = child.Name.LocalName;
            switch (parent.Kind)
            {
                case RoleKind.Body when _response?.Soap is { } binding
                    && string.Equals(local, binding.ResponseElement, StringComparison.Ordinal):
                    return Role.ResponseWrapper;

                case RoleKind.Body when _request?.Soap is { } binding
                    && string.Equals(local, binding.RequestElement, StringComparison.Ordinal):
                    return Role.RequestWrapper;

                case RoleKind.ResponseWrapper when _response is { Soap: { } binding } operation
                    && string.Equals(local, binding.ResultElement, StringComparison.Ordinal):
                    return Typed(operation.ReturnType, child);

                case RoleKind.RequestWrapper when ElementParameter(local) is { } parameter:
                    return Typed(parameter.JadeType, child);

                case RoleKind.Dto:
                    return parent.Dto!.TryGetProperty(local, out var property)
                        && (property.Kind is PropertyKind.Reference or PropertyKind.Collection)
                            ? Typed(property.JadeType, child)
                            : Role.Leaf;

                case RoleKind.Array:
                    return Typed(parent.Array!.ItemType, child);

                default:
                    return GenericRole(child.Name);
            }
        }

        private ParameterDescriptor? ElementParameter(string local)
        {
            if (_request is null)
            {
                return null;
            }

            foreach (var parameter in _request.Parameters)
            {
                if (parameter.Location == ParameterLocation.Element
                    && string.Equals(parameter.Name, local, StringComparison.Ordinal))
                {
                    return parameter;
                }
            }

            return null;
        }

        /// <summary>
        /// The role of an element declared with catalogue type <paramref name="type"/>. A DTO's runtime class
        /// is the BASELINE element's <c>xsi:type</c> when it resolves to a catalogue DTO, otherwise the
        /// declared class; an observed <c>xsi:type</c> that differs is reported as an attribute difference.
        /// </summary>
        private Role Typed(string type, XElement baselineElement)
        {
            if (_catalogue.TryGetDto(type, out var declared))
            {
                return Role.ForDto(RuntimeDto(baselineElement) ?? declared);
            }

            return _catalogue.TryGetArray(type, out var array) ? Role.ForArray(array) : Role.Leaf;
        }

        private DtoDefinition? RuntimeDto(XElement element) =>
            element.Attribute(XsiType) is { } typeAttribute
            && TryResolveQName(typeAttribute, out _, out var local)
            && _catalogue.TryGetDto(local, out var runtime)
                ? runtime
                : null;

        // Attributes.

        private void CompareAttributes(string path, XElement baseline, XElement observed)
        {
            var baselineAttributes = AttributesOf(baseline);
            var observedAttributes = AttributesOf(observed);

            foreach (var (name, baselineAttribute) in baselineAttributes)
            {
                var attributePath = AttributePath(path, name);
                if (!observedAttributes.TryGetValue(name, out var observedAttribute))
                {
                    Add(attributePath, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(ValueOf(baselineAttribute).Text), DiscrepancyValue.Absent);
                    continue;
                }

                if (IsExcludedLocation(baseline.Name, name))
                {
                    continue;
                }

                var baselineValue = ValueOf(baselineAttribute);
                var observedValue = ValueOf(observedAttribute);
                if (baselineValue != observedValue)
                {
                    Add(attributePath, DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineValue.Text), DiscrepancyValue.Literal(observedValue.Text));
                }
            }

            foreach (var (name, observedAttribute) in observedAttributes)
            {
                if (!baselineAttributes.ContainsKey(name))
                {
                    Add(AttributePath(path, name), DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(ValueOf(observedAttribute).Text));
                }
            }
        }

        /// <summary>The attributes of <paramref name="element"/> by expanded name, namespace declarations excluded.</summary>
        private static Dictionary<XName, XAttribute> AttributesOf(XElement element)
        {
            var attributes = new Dictionary<XName, XAttribute>();
            foreach (var attribute in element.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration)
                {
                    attributes.TryAdd(attribute.Name, attribute);
                }
            }

            return attributes;
        }

        private static string AttributePath(string path, XName name) =>
            name.Namespace == XNamespace.None
                ? $"{path}/@{name.LocalName}"
                : $"{path}/@{{{name.NamespaceName}}}{name.LocalName}";

        private bool IsExcludedLocation(XName element, XName attribute) =>
            _excludeSoapAddressLocation && element == SoapAddress && attribute == LocationAttribute;

        private bool IsQNameAttribute(XName name) =>
            name == XsiType
            || (_wsdl && name.Namespace == XNamespace.None && Array.IndexOf(WsdlQNameAttributes, name.LocalName) >= 0);

        private AttributeValue ValueOf(XAttribute attribute) =>
            IsQNameAttribute(attribute.Name) && TryResolveQName(attribute, out var ns, out var local)
                ? new AttributeValue(IsResolvedQName: true, ExpandedName(ns, local))
                : new AttributeValue(IsResolvedQName: false, attribute.Value);

        /// <summary>
        /// Resolves a QName-valued attribute: a prefix through the in-scope declarations of its element, no
        /// prefix through the default namespace. False when the text is not a QName or its prefix is unbound.
        /// </summary>
        private static bool TryResolveQName(XAttribute attribute, out XNamespace ns, out string local)
        {
            var text = attribute.Value;
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var prefix = colon < 0 ? string.Empty : text[..colon];
            local = colon < 0 ? text : text[(colon + 1)..];
            ns = XNamespace.None;

            if (!IsNcName(local) || (colon >= 0 && !IsNcName(prefix)) || attribute.Parent is not { } element)
            {
                return false;
            }

            var resolved = colon < 0 ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(prefix);
            if (resolved is null)
            {
                return false;
            }

            ns = resolved;
            return true;
        }

        private static bool IsNcName(string text)
        {
            if (text.Length == 0 || !XmlConvert.IsStartNCNameChar(text[0]))
            {
                return false;
            }

            for (var index = 1; index < text.Length; index++)
            {
                if (!XmlConvert.IsNCNameChar(text[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ExpandedName(XNamespace ns, string local) =>
            ns == XNamespace.None ? local : $"{{{ns.NamespaceName}}}{local}";

        // Text.

        /// <summary>
        /// Compares the direct text of two elements run by run, where run k is the text between element
        /// children k-1 and k, ordinally and with no trimming or collapsing.
        /// </summary>
        private void CompareText(string path, XElement baseline, XElement observed, bool elementOnly)
        {
            var baselineRuns = TextRuns(baseline, elementOnly);
            var observedRuns = TextRuns(observed, elementOnly);
            var indexed = baseline.HasElements || observed.HasElements;
            var count = Math.Max(baselineRuns.Count, observedRuns.Count);

            for (var index = 0; index < count; index++)
            {
                var baselineText = index < baselineRuns.Count ? baselineRuns[index] : null;
                var observedText = index < observedRuns.Count ? observedRuns[index] : null;
                if (!string.Equals(baselineText, observedText, StringComparison.Ordinal))
                {
                    Add(
                        indexed ? $"{path}/text()[{(index + 1).ToString(CultureInfo.InvariantCulture)}]" : $"{path}/text()",
                        DiscrepancyKinds.Value,
                        DiscrepancyValue.Literal(baselineText),
                        DiscrepancyValue.Literal(observedText));
                }
            }
        }

        /// <summary>
        /// The text runs of <paramref name="element"/>, one more than its element children; null for a run
        /// with no text. Comments and processing instructions are skipped, so the text on either side of one
        /// joins into one run. An empty run is no text, as <c>&lt;a&gt;&lt;/a&gt;</c> holds no text node;
        /// in an element-only container a whitespace-only run is no text too.
        /// </summary>
        private static List<string?> TextRuns(XElement element, bool elementOnly)
        {
            var runs = new List<string?>();
            string? run = null;
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XElement:
                        runs.Add(FinishRun(run, elementOnly));
                        run = null;
                        break;

                    // XCData derives from XText, so CDATA sections are text like any other.
                    case XText text:
                        run = run is null ? text.Value : run + text.Value;
                        break;
                }
            }

            runs.Add(FinishRun(run, elementOnly));
            return runs;
        }

        private static string? FinishRun(string? run, bool elementOnly) =>
            string.IsNullOrEmpty(run) || (elementOnly && IsXmlWhitespace(run)) ? null : run;

        // XML white space is exactly space, tab, carriage return and line feed (XML 1.0 production S);
        // char.IsWhiteSpace would also swallow significant characters such as a no-break space.
        private static bool IsXmlWhitespace(string text)
        {
            foreach (var character in text)
            {
                if (character is not (' ' or '\t' or '\r' or '\n'))
                {
                    return false;
                }
            }

            return true;
        }

        // Child elements.

        /// <summary>
        /// Compares the child elements of two elements: name groups as an unordered set, and the elements of
        /// one group pairwise in document order.
        /// </summary>
        private void CompareChildren(string path, XElement baseline, XElement observed, Role role)
        {
            var baselineGroups = GroupByName(baseline);
            var observedGroups = GroupByName(observed);

            var names = new List<XName>(baselineGroups.Keys);
            foreach (var name in observedGroups.Keys)
            {
                if (!baselineGroups.ContainsKey(name))
                {
                    names.Add(name);
                }
            }

            var ambiguousLocalNames = AmbiguousLocalNames(names);
            foreach (var name in names)
            {
                // Two groups that share a local name in different namespaces need the namespace in their
                // path, or their discrepancies would be indistinguishable.
                var groupPath = $"{path}/{(ambiguousLocalNames.Contains(name.LocalName) ? name.ToString() : name.LocalName)}";
                var baselineElements = baselineGroups.GetValueOrDefault(name) ?? [];
                var observedElements = observedGroups.GetValueOrDefault(name) ?? [];
                var indexed = baselineElements.Count > 1 || observedElements.Count > 1;
                var rule = role.Kind == RoleKind.Dto ? TargetRule(role.Dto!, name.LocalName) : null;

                if (observedElements.Count == 0)
                {
                    for (var index = 0; index < baselineElements.Count; index++)
                    {
                        Add(ItemPath(groupPath, index, indexed), DiscrepancyKinds.Missing, OneSidedValue(baselineElements[index], rule), DiscrepancyValue.Absent);
                    }

                    continue;
                }

                if (baselineElements.Count == 0)
                {
                    for (var index = 0; index < observedElements.Count; index++)
                    {
                        Add(ItemPath(groupPath, index, indexed), DiscrepancyKinds.Extra, DiscrepancyValue.Absent, OneSidedValue(observedElements[index], rule));
                    }

                    continue;
                }

                if (baselineElements.Count != observedElements.Count)
                {
                    Add(
                        groupPath,
                        DiscrepancyKinds.Length,
                        DiscrepancyValue.Literal(baselineElements.Count.ToString(CultureInfo.InvariantCulture)),
                        DiscrepancyValue.Literal(observedElements.Count.ToString(CultureInfo.InvariantCulture)));
                }

                var common = Math.Min(baselineElements.Count, observedElements.Count);
                for (var index = 0; index < common; index++)
                {
                    var childPath = ItemPath(groupPath, index, indexed);
                    var baselineChild = baselineElements[index];
                    var observedChild = observedElements[index];
                    if (rule is not null && !baselineChild.HasElements && !observedChild.HasElements)
                    {
                        CompareTargeted(childPath, baselineChild, observedChild, rule);
                    }
                    else
                    {
                        CompareElements(childPath, baselineChild, observedChild, ChildRole(role, baselineChild));
                    }
                }
            }
        }

        private static Dictionary<XName, List<XElement>> GroupByName(XElement parent)
        {
            var groups = new Dictionary<XName, List<XElement>>();
            foreach (var child in parent.Elements())
            {
                if (!groups.TryGetValue(child.Name, out var group))
                {
                    group = [];
                    groups.Add(child.Name, group);
                }

                group.Add(child);
            }

            return groups;
        }

        private static HashSet<string> AmbiguousLocalNames(List<XName> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (!seen.Add(name.LocalName))
                {
                    ambiguous.Add(name.LocalName);
                }
            }

            return ambiguous;
        }

        private static string ItemPath(string groupPath, int index, bool indexed) =>
            indexed ? $"{groupPath}[{(index + 1).ToString(CultureInfo.InvariantCulture)}]" : groupPath;

        // Normalisation.

        /// <summary>
        /// The normalisation rule a child of a DTO element of runtime class <paramref name="dto"/> follows,
        /// or null when it is compared exactly.
        /// </summary>
        /// <remarks>
        /// Only properties the SOAP surface exposes are targeted. <c>guid</c> carries N1 in the catalogue but
        /// is never SOAP-exposed, so a <c>guid</c> element in a SOAP body is not a generated identifier the
        /// surface emits and is compared exactly; the N2 timestamp properties are all exposed.
        /// </remarks>
        private string? TargetRule(DtoDefinition dto, string local) =>
            dto.TryGetProperty(local, out var property) && property.SoapExposed
                ? _normaliser.RuleFor(dto.Name, local)
                : null;

        /// <summary>
        /// Compares a pair of targeted elements with simple content: their attributes as usual, and their
        /// values under the pairwise rule, reported once at the element's path. A targeted element with
        /// element children is compared exactly instead, because such content is never a valid value.
        /// </summary>
        private void CompareTargeted(string path, XElement baseline, XElement observed, string rule)
        {
            CompareAttributes(path, baseline, observed);

            var baselineSlot = Slot(baseline);
            var observedSlot = Slot(observed);
            if (_normaliser.PairEquals(rule, baselineSlot, observedSlot, WireSyntax.Xml))
            {
                return;
            }

            Add(
                path,
                baselineSlot.IsNull != observedSlot.IsNull ? DiscrepancyKinds.Type : DiscrepancyKinds.Value,
                _normaliser.ToValue(rule, baselineSlot, WireSyntax.Xml),
                _normaliser.ToValue(rule, observedSlot, WireSyntax.Xml));
        }

        /// <summary>The value of an element present on one side only: its normalised value when targeted, else its markup.</summary>
        private DiscrepancyValue OneSidedValue(XElement element, string? rule) =>
            rule is not null && !element.HasElements
                ? _normaliser.ToValue(rule, Slot(element), WireSyntax.Xml)
                : DiscrepancyValue.Literal(element.ToString(SaveOptions.DisableFormatting));

        /// <summary>
        /// The slot of a targeted element with simple content: null for <c>xsi:nil</c> <c>true</c> or <c>1</c>,
        /// otherwise its exact text, comments and processing instructions excluded.
        /// </summary>
        private static NormalisedSlot Slot(XElement element) =>
            element.Attribute(XsiNil)?.Value is "true" or "1"
                ? NormalisedSlot.Null
                : NormalisedSlot.Of(element.Value);
    }
}

