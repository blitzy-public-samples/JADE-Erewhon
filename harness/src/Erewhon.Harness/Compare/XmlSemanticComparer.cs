using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
    /// True to leave the value of <c>soap:address/@location</c> uncompared while requiring the attribute on
    /// both sides. A location on one side only is a <c>missing</c> or <c>extra</c> attribute, and an address
    /// element on one side only a <c>missing</c> or <c>extra</c> element, as for any other difference. A paired
    /// <c>soap:address</c> that lacks the location on both sides gives one <c>missing</c> discrepancy at its
    /// <c>@location</c> path, and documents with the same root and no <c>soap:address</c> anywhere give one
    /// <c>missing</c> discrepancy at <c>/&lt;root&gt;/service/port/address</c>, both values absent in each
    /// case. False to compare the location like any other attribute: the presence requirement belongs to the
    /// exclusion, so two documents that both lack the location or the address then compare equal.
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
    /// <c>element</c>, <c>message</c>, <c>binding</c> and <c>base</c> attributes are compared as QNames. With
    /// the location excluded, its presence on both sides is required as <see cref="Wsdl"/> describes.
    /// </summary>
    /// <param name="ExcludeSoapAddressLocation">
    /// True to leave the value of <c>soap:address/@location</c> uncompared and require the attribute on both sides.
    /// </param>
    public sealed record WsdlContext(bool ExcludeSoapAddressLocation) : XmlComparisonContext
    {
        /// <summary>
        /// True to leave the value of <c>soap:address/@location</c> uncompared and require the attribute on both
        /// sides: absent on one side it is reported missing or extra, absent on both sides of a paired address
        /// it is reported missing with both values absent, and with no <c>soap:address</c> in either of two
        /// documents of the same root the address is reported missing at
        /// <c>/&lt;root&gt;/service/port/address</c>. False compares the location as any other attribute, with
        /// no presence requirement.
        /// </summary>
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
/// Both bodies are loaded with whitespace preserved, DTDs prohibited and no resolver; a body held as bytes
/// is decoded losslessly in the encoding XML detection reads from its byte-order mark, leading bytes or XML
/// declaration, UTF-8 when they name none. The XML declaration, comments and processing instructions are
/// ignored everywhere; CDATA sections are text.
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
/// <see cref="Normaliser"/> decides equality, and an <c>xsi:nil</c> element that holds no text is its null.
/// Content there that is neither a valid value nor a plain nil, element children or an <c>xsi:nil</c> element
/// that holds text, is compared exactly.
/// </para>
/// <para>
/// In a WSDL compared with <c>soap:address/@location</c> excluded, the location's value is never compared
/// but its presence on both sides is: a location or address on one side only is reported missing or extra
/// as usual; a paired address that lacks the location on both sides is reported <c>missing</c> at its
/// <c>@location</c>, and two documents with the same root and no <c>soap:address</c> at all are reported
/// <c>missing</c> at <c>/&lt;root&gt;/service/port/address</c>, with both values absent.
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

    // The decoders of the encodings a body's first bytes can name. Each throws on bytes that are invalid or
    // incomplete in its encoding instead of replacing or dropping them.
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

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
    /// <param name="baselineRoot">
    /// The baseline root element as <see cref="TryLoadRoot(string)"/> or <see cref="TryLoadRoot(ReadOnlyMemory{byte})"/>
    /// returned it; it is not modified.
    /// </param>
    /// <param name="observedRoot">
    /// The observed root element as <see cref="TryLoadRoot(string)"/> or <see cref="TryLoadRoot(ReadOnlyMemory{byte})"/>
    /// returned it; it is not modified.
    /// </param>
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
        walk.CompareRoots(baselineRoot, observedRoot);
        return walk.Result();
    }

    /// <summary>
    /// Loads <paramref name="text"/> with whitespace preserved, DTDs prohibited and no resolver, and returns
    /// its root element; null when it does not load. Whitespace must be preserved: the default load drops
    /// whitespace-only text even inside leaf elements, which would make <c>&lt;a&gt; &lt;/a&gt;</c> equal
    /// <c>&lt;a/&gt;</c>. The text is already decoded, so an encoding its XML declaration names is not applied;
    /// a body held as bytes loads with <see cref="TryLoadRoot(ReadOnlyMemory{byte})"/>, which applies it. Both
    /// loads use the same reader settings, so every XML body comparison and well-formedness check loads alike.
    /// </summary>
    internal static XElement? TryLoadRoot(string text)
    {
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, LoadSettings());
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a body from its bytes exactly as received, byte-order mark included, as
    /// <see cref="TryLoadRoot(string)"/> loads text, and returns its root element; null when it does not load.
    /// The XML reader detects the encoding: a UTF-8, UTF-16 or UTF-32 byte-order mark names it, else the UTF-16
    /// or UTF-32 form of a leading <c>&lt;</c>, else the XML declaration, else it is UTF-8.
    /// </summary>
    /// <remarks>
    /// The body loads only when its bytes decode losslessly in that one encoding. The reader alone would drop
    /// an incomplete sequence at the end of the body and decode a declared encoding such as <c>us-ascii</c>
    /// with replacement characters, so different bodies could load as the same tree. A body therefore does
    /// not load when it is empty, when any of its bytes is invalid or incomplete in the encoding, when its
    /// declaration names an encoding the runtime does not support, or when the declaration names an encoding
    /// other than the one a byte-order mark or the leading bytes name; the two UTF-16 byte orders count as one
    /// encoding there, as do the two UTF-32 ones. It never throws for a malformed body.
    /// </remarks>
    /// <param name="body">The raw body bytes.</param>
    internal static XElement? TryLoadRoot(ReadOnlyMemory<byte> body)
    {
        XDocument document;
        try
        {
            using var input = ReadOnlyStream(body);
            using var reader = XmlReader.Create(input, LoadSettings());
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // The reader reports every malformed body this way, bytes invalid in their encoding and an
            // encoding it does not support included.
            return null;
        }

        return DecodesLosslessly(body.Span, document.Declaration?.Encoding) ? document.Root : null;
    }

    /// <summary>
    /// The reader settings of every load: DTDs prohibited and no resolver, so a body can neither declare
    /// entities nor make the reader fetch anything. Each load gets its own instance, because the settings are
    /// mutable.
    /// </summary>
    private static XmlReaderSettings LoadSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    /// <summary>A read-only stream over <paramref name="bytes"/>, sharing their array when they have one.</summary>
    private static MemoryStream ReadOnlyStream(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    /// <summary>
    /// True when <paramref name="body"/>, which has loaded, decodes losslessly in the one encoding it was read
    /// in: the encoding its first bytes name, which <paramref name="declaredEncoding"/> must agree with; else
    /// the declared encoding; else UTF-8. The bytes after the byte-order mark are decoded strictly, so one that
    /// is invalid or incomplete, or that a replacement character would stand for, makes it false.
    /// </summary>
    /// <param name="body">The raw body bytes, byte-order mark included.</param>
    /// <param name="declaredEncoding">The encoding the XML declaration names; null or empty when it names none.</param>
    private static bool DecodesLosslessly(ReadOnlySpan<byte> body, string? declaredEncoding)
    {
        if (ReadSignature(body) is not { } signature)
        {
            return false;
        }

        var encoding = signature.Encoding ?? StrictUtf8;
        if (!string.IsNullOrEmpty(declaredEncoding))
        {
            Encoding declared;
            try
            {
                declared = Encoding.GetEncoding(declaredEncoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // A name the runtime does not support, such as ucs-4, which the reader accepts without decoding by it.
                return false;
            }

            if (signature.Encoding is not null && EncodingForm(declared) != EncodingForm(signature.Encoding))
            {
                return false;
            }

            encoding = signature.Encoding ?? declared;
        }

        try
        {
            encoding.GetCharCount(body[signature.MarkLength..]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the encoding the first bytes of <paramref name="body"/> name, as XML encoding detection does:
    /// a UTF-32, UTF-8 or UTF-16 byte-order mark, or, without one, the UTF-32 or UTF-16 form of a leading
    /// <c>&lt;</c>.
    /// </summary>
    /// <returns>
    /// The named encoding's strict decoder and its mark length, 0 without a mark; an encoding of null when the
    /// bytes name none. Null when they name UCS-4 in the 2143 or 3412 byte order, which no runtime encoding decodes.
    /// </returns>
    private static ByteSignature? ReadSignature(ReadOnlySpan<byte> body) => body switch
    {
        [0x00, 0x00, 0xFE, 0xFF, ..] => new(StrictUtf32BigEndian, 4),
        [0x00, 0x00, 0x00, 0x3C, ..] => new(StrictUtf32BigEndian, 0),
        [0x00, 0x00, 0xFF, 0xFE, ..] or [0x00, 0x00, 0x3C, 0x00, ..] => null,
        [0xFF, 0xFE, 0x00, 0x00, ..] => new(StrictUtf32LittleEndian, 4),
        [0x3C, 0x00, 0x00, 0x00, ..] => new(StrictUtf32LittleEndian, 0),
        [0xFE, 0xFF, 0x00, 0x00, ..] or [0x00, 0x3C, 0x00, 0x00, ..] => null,
        [0xEF, 0xBB, 0xBF, ..] => new(StrictUtf8, 3),
        [0xFE, 0xFF, ..] => new(StrictUtf16BigEndian, 2),
        [0xFF, 0xFE, ..] => new(StrictUtf16LittleEndian, 2),
        [0x00, 0x3C, ..] => new(StrictUtf16BigEndian, 0),
        [0x3C, 0x00, ..] => new(StrictUtf16LittleEndian, 0),
        _ => new(null, 0),
    };

    /// <summary>
    /// The code page of <paramref name="encoding"/>, with UTF-16 big-endian (1201) as UTF-16 (1200) and UTF-32
    /// big-endian (12001) as UTF-32 (12000): the byte-order mark or the leading bytes decide the byte order, and
    /// a declared <c>utf-16</c> or <c>utf-32</c> stands for either.
    /// </summary>
    private static int EncodingForm(Encoding encoding) => encoding.CodePage switch
    {
        1201 => 1200,
        12001 => 12000,
        var codePage => codePage,
    };

    /// <summary>The encoding a body's first bytes name, null when they name none, and the length of its byte-order mark.</summary>
    private readonly record struct ByteSignature(Encoding? Encoding, int MarkLength);

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

    /// <summary>
    /// The namespace declarations in scope at an element, which resolve prefixes exactly as
    /// <see cref="XElement.GetNamespaceOfPrefix"/> and <see cref="XElement.GetDefaultNamespace"/> do, without
    /// their walk through the element's ancestors.
    /// </summary>
    /// <remarks>
    /// A binding is keyed by its declaration attribute's local name: the prefix of <c>xmlns:p</c>, and
    /// <c>xmlns</c> for a default namespace declaration. A scope is immutable and is shared with its parent's
    /// until an element declares a namespace, so entering an element costs a scan of its own attributes, and a
    /// lookup is logarithmic in the number of bindings in scope rather than linear in the element's depth.
    /// </remarks>
    private sealed class NamespaceScope
    {
        // The local name of a default namespace declaration, xmlns="...", and so the key of its binding.
        private const string DefaultKey = "xmlns";

        private readonly ImmutableDictionary<string, string> _bindings;

        private NamespaceScope(ImmutableDictionary<string, string> bindings) => _bindings = bindings;

        /// <summary>The scope above a root element, where nothing is declared.</summary>
        public static NamespaceScope Empty { get; } = new(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));

        /// <summary>The default namespace: none when no default is declared or when <c>xmlns=""</c> undeclares it.</summary>
        public XNamespace DefaultNamespace =>
            _bindings.TryGetValue(DefaultKey, out var namespaceName) ? XNamespace.Get(namespaceName) : XNamespace.None;

        /// <summary>
        /// The scope inside <paramref name="element"/>: this scope with the element's own declarations added,
        /// which apply to the element's attributes too; this instance when the element declares nothing new.
        /// </summary>
        public NamespaceScope Enter(XElement element)
        {
            var bindings = _bindings;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    bindings = bindings.SetItem(attribute.Name.LocalName, attribute.Value);
                }
            }

            return ReferenceEquals(bindings, _bindings) ? this : new NamespaceScope(bindings);
        }

        /// <summary>
        /// The namespace bound to the non-empty <paramref name="prefix"/>: always the reserved namespace for
        /// <c>xmlns</c>, whose key a default namespace declaration shares but never binds; otherwise the nearest
        /// declaration, then the reserved namespace for <c>xml</c>; null when the prefix is unbound.
        /// </summary>
        public XNamespace? NamespaceOfPrefix(string prefix)
        {
            if (string.Equals(prefix, DefaultKey, StringComparison.Ordinal))
            {
                return XNamespace.Xmlns;
            }

            if (_bindings.TryGetValue(prefix, out var namespaceName))
            {
                return XNamespace.Get(namespaceName);
            }

            return string.Equals(prefix, "xml", StringComparison.Ordinal) ? XNamespace.Xml : null;
        }
    }

    /// <summary>
    /// The state of one comparison: the context's facts, the path of the element being compared and the
    /// discrepancies found so far.
    /// </summary>
    /// <remarks>
    /// The walk is depth-first. The path is one builder that each element extends by its segment on the way
    /// down and cuts back on the way up, and it is rendered only when a discrepancy is added; each side's
    /// namespace scope travels down with its element. Apart from rendering the path of a discrepancy, the
    /// work done at an element therefore does not grow with its depth.
    /// </remarks>
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

        // Where WSDL 1.1 publishes a SOAP endpoint below the root: wsdl:service/wsdl:port/soap:address.
        private const string SoapAddressPosition = "/service/port/address";

        private readonly OperationCatalogue _catalogue;
        private readonly Normaliser _normaliser;
        private readonly OperationDescriptor? _response;
        private readonly OperationDescriptor? _request;
        private readonly bool _wsdl;
        private readonly bool _excludeSoapAddressLocation;
        private readonly List<Discrepancy> _discrepancies = [];

        // The path of the element being compared; see the remarks.
        private readonly StringBuilder _path = new();

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

        /// <summary>
        /// Compares two root elements of the same expanded name, whose path is <c>/</c> followed by the root's
        /// local name; nothing is in scope above them. When the location is excluded and neither document has a
        /// <c>soap:address</c>, the address is reported missing on both sides at its WSDL 1.1 position.
        /// </summary>
        public void CompareRoots(XElement baseline, XElement observed)
        {
            _path.Append(RootPath).Append(baseline.Name.LocalName);
            var rootLength = _path.Length;
            CompareElements(
                baseline,
                NamespaceScope.Empty.Enter(baseline),
                observed,
                NamespaceScope.Empty.Enter(observed),
                RootRole(baseline));

            // The location rule in CompareAttributes sees only addresses the two documents pair; with no
            // address on either side there is no pair, and nothing else would report the absence.
            if (_excludeSoapAddressLocation && !HasSoapAddress(baseline) && !HasSoapAddress(observed))
            {
                _path.Length = rootLength;
                _path.Append(SoapAddressPosition);
                Add(DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent);
                _path.Length = rootLength;
            }
        }

        private static bool HasSoapAddress(XElement root) => root.DescendantsAndSelf(SoapAddress).Any();

        /// <summary>The role of a root element, which only its name decides.</summary>
        private Role RootRole(XElement root) => EnvelopeRole(root.Name) ?? GenericRole(root.Name);

        /// <summary>
        /// Compares two elements of the same expanded name at the current path, each with its own namespace
        /// scope: attributes, text, then children.
        /// </summary>
        private void CompareElements(XElement baseline, NamespaceScope baselineScope, XElement observed, NamespaceScope observedScope, Role role)
        {
            // Turns a pathologically deep document into an exception the caller can report, instead of a
            // stack overflow that would end the process.
            RuntimeHelpers.EnsureSufficientExecutionStack();

            CompareAttributes(baseline, baselineScope, observed, observedScope);
            CompareText(baseline, observed, role.IsElementOnly);
            CompareChildren(baseline, baselineScope, observed, observedScope, role);
        }

        /// <summary>Adds a discrepancy at the current path, the only place a path is rendered.</summary>
        private void Add(string kind, DiscrepancyValue baseline, DiscrepancyValue observed) =>
            _discrepancies.Add(new Discrepancy(_path.ToString(), kind, baseline, observed));

        /// <summary>Appends <paramref name="name"/> as <see cref="XName.ToString"/> writes it: <c>{uri}local</c>, or the local name alone in no namespace.</summary>
        private void AppendExpandedName(XName name)
        {
            if (name.Namespace != XNamespace.None)
            {
                _path.Append('{').Append(name.NamespaceName).Append('}');
            }

            _path.Append(name.LocalName);
        }

        /// <summary>Appends the 1-based position <c>[n]</c> of the item at <paramref name="index"/> when its group is indexed.</summary>
        private void AppendItemIndex(int index, bool indexed)
        {
            if (indexed)
            {
                _path.Append(CultureInfo.InvariantCulture, $"[{index + 1}]");
            }
        }

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

        /// <summary>
        /// The role of <paramref name="child"/>, the BASELINE element of a pair, under a parent of role
        /// <paramref name="parent"/>; <paramref name="childScope"/> is the child's own namespace scope.
        /// </summary>
        private Role ChildRole(Role parent, XElement child, NamespaceScope childScope)
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
                    return Typed(operation.ReturnType, child, childScope);

                case RoleKind.RequestWrapper when ElementParameter(local) is { } parameter:
                    return Typed(parameter.JadeType, child, childScope);

                case RoleKind.Dto:
                    return parent.Dto!.TryGetProperty(local, out var property)
                        && (property.Kind is PropertyKind.Reference or PropertyKind.Collection)
                            ? Typed(property.JadeType, child, childScope)
                            : Role.Leaf;

                case RoleKind.Array:
                    return Typed(parent.Array!.ItemType, child, childScope);

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
        /// <paramref name="baselineScope"/> is the baseline element's own namespace scope.
        /// </summary>
        private Role Typed(string type, XElement baselineElement, NamespaceScope baselineScope)
        {
            if (_catalogue.TryGetDto(type, out var declared))
            {
                return Role.ForDto(RuntimeDto(baselineElement, baselineScope) ?? declared);
            }

            return _catalogue.TryGetArray(type, out var array) ? Role.ForArray(array) : Role.Leaf;
        }

        private DtoDefinition? RuntimeDto(XElement element, NamespaceScope scope) =>
            element.Attribute(XsiType) is { } typeAttribute
            && TryResolveQName(typeAttribute.Value, scope, out _, out var local)
            && _catalogue.TryGetDto(local, out var runtime)
                ? runtime
                : null;

        // Attributes.

        /// <summary>
        /// Compares the attributes of two elements as sets by expanded name, each side's QName values resolved
        /// in that side's scope.
        /// </summary>
        private void CompareAttributes(XElement baseline, NamespaceScope baselineScope, XElement observed, NamespaceScope observedScope)
        {
            var baselineAttributes = AttributesOf(baseline);
            var observedAttributes = AttributesOf(observed);

            foreach (var (name, baselineAttribute) in baselineAttributes)
            {
                if (!observedAttributes.TryGetValue(name, out var observedAttribute))
                {
                    AddAttribute(name, DiscrepancyKinds.Missing, DiscrepancyValue.Literal(ValueOf(baselineAttribute, baselineScope).Text), DiscrepancyValue.Absent);
                    continue;
                }

                if (IsExcludedLocation(baseline.Name, name))
                {
                    continue;
                }

                var baselineValue = ValueOf(baselineAttribute, baselineScope);
                var observedValue = ValueOf(observedAttribute, observedScope);
                if (baselineValue != observedValue)
                {
                    AddAttribute(name, DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineValue.Text), DiscrepancyValue.Literal(observedValue.Text));
                }
            }

            foreach (var (name, observedAttribute) in observedAttributes)
            {
                if (!baselineAttributes.ContainsKey(name))
                {
                    AddAttribute(name, DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal(ValueOf(observedAttribute, observedScope).Text));
                }
            }

            // An excluded location must still be present on both sides. Neither loop above sees an attribute
            // that both sides lack, so that case is reported here.
            if (_excludeSoapAddressLocation
                && baseline.Name == SoapAddress
                && !baselineAttributes.ContainsKey(LocationAttribute)
                && !observedAttributes.ContainsKey(LocationAttribute))
            {
                AddAttribute(LocationAttribute, DiscrepancyKinds.Missing, DiscrepancyValue.Absent, DiscrepancyValue.Absent);
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

        /// <summary>
        /// Adds a discrepancy at the current element's attribute <paramref name="name"/>, whose path is
        /// <c>/@local</c> or <c>/@{uri}local</c> below the element's.
        /// </summary>
        private void AddAttribute(XName name, string kind, DiscrepancyValue baseline, DiscrepancyValue observed)
        {
            var elementLength = _path.Length;
            _path.Append("/@");
            AppendExpandedName(name);
            Add(kind, baseline, observed);
            _path.Length = elementLength;
        }

        private bool IsExcludedLocation(XName element, XName attribute) =>
            _excludeSoapAddressLocation && element == SoapAddress && attribute == LocationAttribute;

        private bool IsQNameAttribute(XName name) =>
            name == XsiType
            || (_wsdl && name.Namespace == XNamespace.None && Array.IndexOf(WsdlQNameAttributes, name.LocalName) >= 0);

        /// <summary>The value of <paramref name="attribute"/> as compared, a QName resolved in <paramref name="scope"/>, its element's scope.</summary>
        private AttributeValue ValueOf(XAttribute attribute, NamespaceScope scope) =>
            IsQNameAttribute(attribute.Name) && TryResolveQName(attribute.Value, scope, out var ns, out var local)
                ? new AttributeValue(IsResolvedQName: true, ExpandedName(ns, local))
                : new AttributeValue(IsResolvedQName: false, attribute.Value);

        /// <summary>
        /// Resolves the QName <paramref name="text"/> of an attribute in <paramref name="scope"/>, the scope of
        /// the attribute's element: a prefix through its in-scope declarations, no prefix through the default
        /// namespace. False when the text is not a QName or its prefix is unbound.
        /// </summary>
        private static bool TryResolveQName(string text, NamespaceScope scope, out XNamespace ns, out string local)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var prefix = colon < 0 ? string.Empty : text[..colon];
            local = colon < 0 ? text : text[(colon + 1)..];
            ns = XNamespace.None;

            if (!IsNcName(local) || (colon >= 0 && !IsNcName(prefix)))
            {
                return false;
            }

            var resolved = colon < 0 ? scope.DefaultNamespace : scope.NamespaceOfPrefix(prefix);
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
        /// children k-1 and k, ordinally and with no trimming or collapsing. A difference is reported at
        /// <c>/text()</c> below the element's path, or at <c>/text()[k]</c> when either element has children.
        /// </summary>
        private void CompareText(XElement baseline, XElement observed, bool elementOnly)
        {
            var baselineRuns = TextRuns(baseline, elementOnly);
            var observedRuns = TextRuns(observed, elementOnly);
            var indexed = baseline.HasElements || observed.HasElements;
            var count = Math.Max(baselineRuns.Count, observedRuns.Count);
            var elementLength = _path.Length;

            for (var index = 0; index < count; index++)
            {
                var baselineText = index < baselineRuns.Count ? baselineRuns[index] : null;
                var observedText = index < observedRuns.Count ? observedRuns[index] : null;
                if (!string.Equals(baselineText, observedText, StringComparison.Ordinal))
                {
                    _path.Append("/text()");
                    AppendItemIndex(index, indexed);
                    Add(DiscrepancyKinds.Value, DiscrepancyValue.Literal(baselineText), DiscrepancyValue.Literal(observedText));
                    _path.Length = elementLength;
                }
            }
        }

        /// <summary>
        /// The text runs of <paramref name="element"/>, one more than its element children; null for a run
        /// with no text. Comments and processing instructions are skipped, so the text on either side of one
        /// joins into one run. An empty run is no text, as <c>&lt;a&gt;&lt;/a&gt;</c> holds no text node;
        /// in an element-only container a whitespace-only run is no text too.
        /// </summary>
        /// <remarks>
        /// A run's first fragment is kept as it is; only when a second fragment joins the run is it copied
        /// into a builder, which takes every further fragment, and the run's text is materialised once when
        /// the run closes. A run of many fragments therefore costs time linear in its length.
        /// </remarks>
        private static List<string?> TextRuns(XElement element, bool elementOnly)
        {
            var runs = new List<string?>();
            string? first = null;
            StringBuilder? joined = null;
            var isJoined = false;

            string? CloseRun()
            {
                var run = isJoined ? joined!.ToString() : first;
                first = null;
                isJoined = false;
                return FinishRun(run, elementOnly);
            }

            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XElement:
                        runs.Add(CloseRun());
                        break;

                    // XCData derives from XText, so CDATA sections are text like any other.
                    case XText text when first is null && !isJoined:
                        first = text.Value;
                        break;

                    case XText text:
                        if (!isJoined)
                        {
                            joined = (joined ?? new StringBuilder()).Clear().Append(first);
                            first = null;
                            isJoined = true;
                        }

                        joined!.Append(text.Value);
                        break;
                }
            }

            runs.Add(CloseRun());
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
        /// one group pairwise in document order. A group's path is the parent's with <c>/local</c>, or with
        /// <c>/{uri}local</c> when another group shares the local name, and an item's adds <c>[n]</c> when
        /// either side's group holds more than one element. Each child pair is compared in its own scopes.
        /// </summary>
        private void CompareChildren(XElement baseline, NamespaceScope baselineScope, XElement observed, NamespaceScope observedScope, Role role)
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
            var parentLength = _path.Length;
            foreach (var name in names)
            {
                // Two groups that share a local name in different namespaces need the namespace in their
                // path, or their discrepancies would be indistinguishable.
                _path.Length = parentLength;
                _path.Append('/');
                if (ambiguousLocalNames.Contains(name.LocalName))
                {
                    AppendExpandedName(name);
                }
                else
                {
                    _path.Append(name.LocalName);
                }

                var groupLength = _path.Length;
                var baselineElements = baselineGroups.GetValueOrDefault(name) ?? [];
                var observedElements = observedGroups.GetValueOrDefault(name) ?? [];
                var indexed = baselineElements.Count > 1 || observedElements.Count > 1;
                var rule = role.Kind == RoleKind.Dto ? TargetRule(role.Dto!, name.LocalName) : null;

                if (observedElements.Count == 0)
                {
                    for (var index = 0; index < baselineElements.Count; index++)
                    {
                        AppendItemIndex(index, indexed);
                        Add(DiscrepancyKinds.Missing, OneSidedValue(baselineElements[index], rule), DiscrepancyValue.Absent);
                        _path.Length = groupLength;
                    }

                    continue;
                }

                if (baselineElements.Count == 0)
                {
                    for (var index = 0; index < observedElements.Count; index++)
                    {
                        AppendItemIndex(index, indexed);
                        Add(DiscrepancyKinds.Extra, DiscrepancyValue.Absent, OneSidedValue(observedElements[index], rule));
                        _path.Length = groupLength;
                    }

                    continue;
                }

                if (baselineElements.Count != observedElements.Count)
                {
                    Add(
                        DiscrepancyKinds.Length,
                        DiscrepancyValue.Literal(baselineElements.Count.ToString(CultureInfo.InvariantCulture)),
                        DiscrepancyValue.Literal(observedElements.Count.ToString(CultureInfo.InvariantCulture)));
                }

                var common = Math.Min(baselineElements.Count, observedElements.Count);
                for (var index = 0; index < common; index++)
                {
                    AppendItemIndex(index, indexed);
                    var baselineChild = baselineElements[index];
                    var observedChild = observedElements[index];
                    var baselineChildScope = baselineScope.Enter(baselineChild);
                    var observedChildScope = observedScope.Enter(observedChild);
                    if (rule is not null && !baselineChild.HasElements && !observedChild.HasElements)
                    {
                        CompareTargeted(baselineChild, baselineChildScope, observedChild, observedChildScope, rule);
                    }
                    else
                    {
                        CompareElements(
                            baselineChild,
                            baselineChildScope,
                            observedChild,
                            observedChildScope,
                            ChildRole(role, baselineChild, baselineChildScope));
                    }

                    _path.Length = groupLength;
                }
            }

            _path.Length = parentLength;
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
        /// values, reported once at the element's path, the current one. Two slots follow the pairwise rule.
        /// An element that is nil and holds text has no slot: its content is never a valid value and never a
        /// plain nil, so it equals only another such element with the same exact text, and a difference reports
        /// it as its markup while the other side keeps its normalised value. The kind is <c>type</c> when
        /// exactly one side is nil and <c>value</c> otherwise. A targeted element with element children is
        /// compared exactly instead, because such content is never a valid value. The scopes are the two
        /// elements' own, which their attributes resolve in.
        /// </summary>
        private void CompareTargeted(XElement baseline, NamespaceScope baselineScope, XElement observed, NamespaceScope observedScope, string rule)
        {
            CompareAttributes(baseline, baselineScope, observed, observedScope);

            var baselineSlot = Slot(baseline);
            var observedSlot = Slot(observed);
            if (baselineSlot is not null && observedSlot is not null)
            {
                if (_normaliser.PairEquals(rule, baselineSlot, observedSlot, WireSyntax.Xml))
                {
                    return;
                }
            }
            else if (baselineSlot is null
                && observedSlot is null
                && string.Equals(baseline.Value, observed.Value, StringComparison.Ordinal))
            {
                return;
            }

            Add(
                IsNil(baseline) != IsNil(observed) ? DiscrepancyKinds.Type : DiscrepancyKinds.Value,
                TargetedValue(baseline, baselineSlot, rule),
                TargetedValue(observed, observedSlot, rule));
        }

        /// <summary>
        /// The value of an element present on one side only: its normalised value when it is targeted and has
        /// a slot, else its markup, as for element children or a nil element that holds text.
        /// </summary>
        private DiscrepancyValue OneSidedValue(XElement element, string? rule) =>
            rule is not null && !element.HasElements
                ? TargetedValue(element, Slot(element), rule)
                : Markup(element);

        /// <summary>
        /// The value of a targeted element with simple content whose slot is <paramref name="slot"/>: the
        /// slot's normalised value, or the element's markup when it has no slot.
        /// </summary>
        private DiscrepancyValue TargetedValue(XElement element, NormalisedSlot? slot, string rule) =>
            slot is null ? Markup(element) : _normaliser.ToValue(rule, slot, WireSyntax.Xml);

        /// <summary>The markup of <paramref name="element"/>, unformatted, as a literal value.</summary>
        private static DiscrepancyValue Markup(XElement element) =>
            DiscrepancyValue.Literal(element.ToString(SaveOptions.DisableFormatting));

        /// <summary>
        /// The slot of a targeted element with simple content: <see cref="NormalisedSlot.Null"/> when it is nil
        /// and holds no text, its exact text when it is not nil, comments and processing instructions excluded;
        /// null when it is nil and holds text, which no slot represents, because a null slot holds no text. As
        /// for any text run, empty text, an empty CDATA section included, is no text.
        /// </summary>
        private static NormalisedSlot? Slot(XElement element)
        {
            var text = element.Value;
            if (!IsNil(element))
            {
                return NormalisedSlot.Of(text);
            }

            return text.Length == 0 ? NormalisedSlot.Null : null;
        }

        /// <summary>True when <paramref name="element"/> carries <c>xsi:nil</c> <c>true</c> or <c>1</c>.</summary>
        private static bool IsNil(XElement element) => element.Attribute(XsiNil)?.Value is "true" or "1";
    }
}
