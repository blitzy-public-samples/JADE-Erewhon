using System.Globalization;
using System.Text;
using System.Xml;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Wire;
using Erewhon.StandIn.Dto;

namespace Erewhon.StandIn.Soap;

/// <summary>
/// Writes every SOAP response body the stand-in serves on port 21001: the document/literal wrapped
/// responses of the 9 <c>ErewhonWebService</c> operations and the stand-in's own SOAP 1.1 Faults.
/// </summary>
/// <remarks>
/// <para>
/// Element names, exposure flags, array item names and value formats come from the operation catalogue
/// and <see cref="JadeWireFormats"/>; the values come from the <see cref="DtoBuilder"/> tree, which is
/// only read, never changed, because the REST writer serialises the same trees.
/// </para>
/// <para>
/// Output is deterministic: equal inputs give byte-identical bodies on every call, reset and restart.
/// Every write uses <see cref="Settings"/>, the prefixes <c>soap</c>, <c>xsi</c> and <c>xsd</c> are
/// declared once on the envelope in that order, members follow catalogue order, values use the
/// invariant culture, and nothing here reads a clock or keeps state between calls, so the class is safe
/// for concurrent use. A success envelope carries no stand-in label, comment or extra element; only
/// Fault strings name the stand-in. Status code and headers belong to the caller, which sets only
/// <c>Content-Type</c> to <see cref="ContentType"/>.
/// </para>
/// </remarks>
public static class SoapEnvelopeWriter
{
    /// <summary>The SOAP 1.1 envelope namespace, bound to the prefix <c>soap</c>.</summary>
    public const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>
    /// The message namespace of every element below the response wrapper, written as the default
    /// namespace and never prefixed (the consumer's <c>targetNamespace</c>,
    /// Erewhon/ErewhonWebServiceConsumerSchema.scm:1720).
    /// </summary>
    public const string ServiceNamespace = "urn:JadeWebServices/WebServiceHTTP/";

    /// <summary>The XML Schema instance namespace, bound to the prefix <c>xsi</c> for <c>xsi:nil</c>.</summary>
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The XML Schema namespace, bound to the prefix <c>xsd</c>.</summary>
    public const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <summary>The content type of every body written here.</summary>
    public const string ContentType = "text/xml; charset=utf-8";

    /// <summary>The SOAP 1.1 fault code for a request the stand-in rejects as the sender's error.</summary>
    public const string FaultCodeClient = "soap:Client";

    /// <summary>The SOAP 1.1 fault code for a request the stand-in cannot answer through its own failure.</summary>
    public const string FaultCodeServer = "soap:Server";

    /// <summary>The prefix every Fault string starts with, so a Fault is never mistaken for JADE output.</summary>
    public const string FaultStringPrefix = "jade-standin:";

    /// <summary>
    /// The writer settings of every body: UTF-8 without a byte-order mark, so the declaration reads
    /// <c>&lt;?xml version="1.0" encoding="utf-8"?&gt;</c> and the first byte is <c>0x3C</c>; no
    /// indentation; <c>\n</c> line breaks (the bodies written here carry a stored carriage return as the
    /// character reference <c>&amp;#xD;</c> instead, so that a parser reads it back); character checking
    /// on; the output stream left open.
    /// </summary>
    /// <remarks>
    /// The instance is read-only, so no caller can change the bytes another caller gets: assigning any
    /// property throws.
    /// </remarks>
    public static readonly XmlWriterSettings Settings = CreateSettings();

    private const string SoapPrefix = "soap";
    private const string XsiPrefix = "xsi";
    private const string XsdPrefix = "xsd";
    private const string XmlnsPrefix = "xmlns";
    private const string NilAttribute = "nil";

    // DTOResult is absent from the WebServiceHTTP exposure list (Erewhon/ErehwhonExternalInterfacesSchema.scm:1793-1932),
    // yet four SOAP operations return it, so its own members are written whatever their exposure
    // flags say and its inherited guid never is (KL-SOAP-DTORESULT).
    private const string ResultClass = "DTOResult";

    /// <summary>
    /// Writes the response envelope of one SOAP operation:
    /// <c>soap:Envelope/soap:Body/&lt;ResponseElement xmlns="urn:JadeWebServices/WebServiceHTTP/"&gt;/&lt;ResultElement&gt;</c>,
    /// with no <c>soap:Header</c>.
    /// </summary>
    /// <param name="operation">A SOAP operation of the catalogue, whose binding names the wrapper and result elements.</param>
    /// <param name="result">
    /// The method's return value: a <see cref="DtoNode"/> of the operation's return type, a
    /// <see cref="DtoArray"/> of that array type, or null, which writes <c>&lt;ResultElement xsi:nil="true" /&gt;</c>.
    /// </param>
    /// <returns>The UTF-8 body bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> has no SOAP binding, or <paramref name="result"/> is neither null, a
    /// node of the operation's return type nor an array of it. The <see cref="XmlWriter"/> also throws it,
    /// through <see cref="Settings"/>' character checking, when a value in the tree holds a character XML 1.0
    /// cannot represent, such as U+0001, U+000B or U+FFFF; no envelope can carry such a value, so it is
    /// neither replaced nor skipped.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="result"/> is null and the binding's result is not nillable.</exception>
    /// <exception cref="NotSupportedException">
    /// The tree holds a value with no SOAP wire form here: a JADE type outside String, Integer, Decimal,
    /// Date, TimeStamp, Binary, a DTO reference and StringArray; a class or array the SOAP surface does
    /// not expose; or an object whose class differs from its position's declared class, which would need
    /// the <c>xsi:type</c> this surface never writes.
    /// </exception>
    public static byte[] WriteResponse(OperationDescriptor operation, object? result)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var binding = operation.Soap ?? throw new ArgumentException(
            $"Operation '{operation.Id}' has no SOAP binding, so it has no SOAP response.", nameof(operation));

        var catalogue = OperationCatalogue.Default;
        switch (result)
        {
            case null when !binding.ResultNillable:
                throw new InvalidOperationException(
                    $"Operation '{operation.Id}' returned null, but its result element '{binding.ResultElement}' is not nillable.");
            case DtoNode node when !catalogue.IsKindOf(node.RuntimeClass, operation.ReturnType):
                throw new ArgumentException(
                    $"Operation '{operation.Id}' returns '{operation.ReturnType}'; a '{node.RuntimeClass}' node cannot be its result.",
                    nameof(result));
            case DtoArray array when !string.Equals(array.ArrayType, operation.ReturnType, StringComparison.Ordinal):
                throw new ArgumentException(
                    $"Operation '{operation.Id}' returns '{operation.ReturnType}'; a '{array.ArrayType}' array cannot be its result.",
                    nameof(result));
            case null or DtoNode or DtoArray:
                break;
            default:
                throw new ArgumentException(
                    $"Operation '{operation.Id}' returns '{operation.ReturnType}'; a {result.GetType().Name} cannot be its result.",
                    nameof(result));
        }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            WriteStartEnvelope(writer);
            writer.WriteStartElement(string.Empty, binding.ResponseElement, ServiceNamespace);
            switch (result)
            {
                case DtoNode node:
                    WriteNode(writer, binding.ResultElement, node, operation.ReturnType, catalogue);
                    break;
                case DtoArray array:
                    WriteArray(writer, binding.ResultElement, array, catalogue);
                    break;
                default:
                    WriteNil(writer, binding.ResultElement);
                    break;
            }

            writer.WriteEndElement();
            WriteEndEnvelope(writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Writes a SOAP 1.1 Fault, the stand-in's own response to a request it cannot model:
    /// <c>soap:Envelope/soap:Body/soap:Fault</c> holding the unqualified <c>faultcode</c> and
    /// <c>faultstring</c>. Business errors are never Faults; they travel as a <c>DTOResult</c> in an
    /// HTTP 200 response.
    /// </summary>
    /// <param name="faultCode"><see cref="FaultCodeClient"/> or <see cref="FaultCodeServer"/>.</param>
    /// <param name="faultString">
    /// The reason; <c>jade-standin: </c> is prepended unless it already starts with
    /// <see cref="FaultStringPrefix"/>.
    /// </param>
    /// <returns>The UTF-8 body bytes, which the caller sends with HTTP 500 and <see cref="ContentType"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="faultCode"/> or <paramref name="faultString"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="faultCode"/> is neither fault code, or <paramref name="faultString"/> is empty or
    /// whitespace.
    /// </exception>
    public static byte[] WriteFault(string faultCode, string faultString)
    {
        ArgumentNullException.ThrowIfNull(faultCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(faultString);
        if (faultCode is not (FaultCodeClient or FaultCodeServer))
        {
            throw new ArgumentException(
                $"Fault code '{faultCode}' is neither '{FaultCodeClient}' nor '{FaultCodeServer}'.", nameof(faultCode));
        }

        var text = faultString.StartsWith(FaultStringPrefix, StringComparison.Ordinal)
            ? faultString
            : $"{FaultStringPrefix} {faultString}";

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, Settings))
        {
            WriteStartEnvelope(writer);
            writer.WriteStartElement(SoapPrefix, "Fault", SoapEnvelopeNamespace);
            writer.WriteElementString("faultcode", string.Empty, faultCode);
            writer.WriteElementString("faultstring", string.Empty, text);
            writer.WriteEndElement();
            WriteEndEnvelope(writer);
        }

        return stream.ToArray();
    }

    private static XmlWriterSettings CreateSettings()
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
            CheckCharacters = true,
            CloseOutput = false,
        };

        // XmlWriter.Settings returns a read-only copy of the settings the writer was created with; the
        // shared public instance is that copy, so a caller cannot change what every other caller writes.
        using var probe = XmlWriter.Create(Stream.Null, settings);
        return probe.Settings ?? throw new InvalidOperationException("XmlWriter.Create returned a writer without settings.");
    }

    // The declaration, then soap:Envelope declaring soap, xsi and xsd in that fixed order, then
    // soap:Body. The explicit xmlns:soap pins the order XmlWriter would otherwise choose itself.
    private static void WriteStartEnvelope(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement(SoapPrefix, "Envelope", SoapEnvelopeNamespace);
        writer.WriteAttributeString(XmlnsPrefix, SoapPrefix, null, SoapEnvelopeNamespace);
        writer.WriteAttributeString(XmlnsPrefix, XsiPrefix, null, XsiNamespace);
        writer.WriteAttributeString(XmlnsPrefix, XsdPrefix, null, XsdNamespace);
        writer.WriteStartElement(SoapPrefix, "Body", SoapEnvelopeNamespace);
    }

    private static void WriteEndEnvelope(XmlWriter writer)
    {
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    // One DTO object as <elementName> holding its written members. No xsi:type is ever written: for
    // the 9 SOAP operations every object's class equals its position's declared class (DTOAgent,
    // DTOClient, DTOPhoto, DTOResult, and DTOName items), so a subclass in a declared position is a
    // tree this surface cannot write faithfully and is refused rather than written as its declared class.
    private static void WriteNode(
        XmlWriter writer, string elementName, DtoNode node, string declaredClass, OperationCatalogue catalogue)
    {
        if (!string.Equals(node.RuntimeClass, declaredClass, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"A '{node.RuntimeClass}' in a position declared '{declaredClass}' needs xsi:type, which the stand-in's " +
                "SOAP surface never writes.");
        }

        var definition = node.Definition;
        if (!definition.SoapExposed)
        {
            throw new NotSupportedException(
                $"DTO '{definition.Name}' is not exposed by the WebServiceHTTP list, so the SOAP surface cannot write it.");
        }

        var isResult = catalogue.IsKindOf(definition.Name, ResultClass);
        writer.WriteStartElement(string.Empty, elementName, ServiceNamespace);
        foreach (var member in node.Members)
        {
            var written = isResult
                ? catalogue.IsKindOf(member.Property.DeclaredIn, ResultClass)
                : member.Property.SoapExposed;
            if (written)
            {
                WriteMember(writer, definition.Name, member, catalogue);
            }
        }

        writer.WriteEndElement();
    }

    // A DTO array as <elementName> holding one catalogue-named item element per entry, in the order the
    // handler built it; an empty array is an empty element.
    private static void WriteArray(XmlWriter writer, string elementName, DtoArray array, OperationCatalogue catalogue)
    {
        var itemElement = SoapItemElementOf(array.Definition);
        writer.WriteStartElement(string.Empty, elementName, ServiceNamespace);
        foreach (var item in array.Items)
        {
            if (item is null)
            {
                WriteNil(writer, itemElement);
            }
            else
            {
                WriteNode(writer, itemElement, item, array.ItemDeclaredClass, catalogue);
            }
        }

        writer.WriteEndElement();
    }

    // One member as <propertyName>, formatted by the value kind the tree derived from its JadeType. The
    // JADE null string is held as "" and written as an empty element, never as xsi:nil
    // (KL-SOAP-SHAPES). A null or empty Binary, both JADE's null binary, is xsi:nil, as getPhoto's
    // photo is for a client with no photo. A null reference, a null collection and a null Date or
    // TimeStamp, which have no lexical form, are xsi:nil too.
    private static void WriteMember(XmlWriter writer, string owner, DtoMember member, OperationCatalogue catalogue)
    {
        var property = member.Property;
        var name = property.Name;
        switch (member.Kind, member.Value)
        {
            case (DtoValueKind.String, string text):
                WriteText(writer, name, text);
                return;
            case (DtoValueKind.Integer, int number):
                WriteText(writer, name, number.ToString(CultureInfo.InvariantCulture));
                return;
            case (DtoValueKind.Decimal, decimal amount):
                WriteText(writer, name, JadeWireFormats.FormatDecimal2(amount));
                return;
            case (DtoValueKind.Date, DateOnly date):
                WriteText(writer, name, JadeWireFormats.FormatXsdDate(date));
                return;
            case (DtoValueKind.TimeStamp, DateTime timestamp):
                WriteText(writer, name, JadeWireFormats.FormatXsdDateTime(timestamp));
                return;
            case (DtoValueKind.Binary, byte[] { Length: > 0 } bytes):
                WriteText(writer, name, JadeWireFormats.FormatBase64Binary(bytes));
                return;
            case (DtoValueKind.StringArray, IReadOnlyList<string> entries):
                WriteStringArray(writer, property, entries, catalogue);
                return;
            case (DtoValueKind.Reference, DtoNode nested):
                WriteNode(writer, name, nested, property.JadeType, catalogue);
                return;
            case (DtoValueKind.Binary, null or byte[]):
            case (DtoValueKind.Date or DtoValueKind.TimeStamp or DtoValueKind.StringArray or DtoValueKind.Reference, null):
                WriteNil(writer, name);
                return;
            case (DtoValueKind.String or DtoValueKind.Integer or DtoValueKind.Decimal or DtoValueKind.Date
                or DtoValueKind.TimeStamp or DtoValueKind.Binary or DtoValueKind.StringArray or DtoValueKind.Reference, _):
                throw new InvalidOperationException(
                    $"DTO '{owner}' property '{name}' is of kind {member.Kind} (JADE type '{property.JadeType}') but holds " +
                    $"{(member.Value is null ? "null" : $"a value of type {member.Value.GetType().Name}")}.");
            default:
                throw new NotSupportedException(
                    $"DTO '{owner}' property '{name}' has JADE type '{property.JadeType}', which has no SOAP wire form " +
                    "in the stand-in.");
        }
    }

    // A StringArray member as <propertyName> holding one item element per entry, in stored order, the
    // item name taken from the catalogue array the member's JadeType names (String, consumer
    // ArrayOfString, Erewhon/ErewhonWebServiceConsumerSchema.scm:148). An empty collection is an empty
    // element, because a JADE exclusive collection always exists.
    private static void WriteStringArray(
        XmlWriter writer, DtoProperty property, IReadOnlyList<string> entries, OperationCatalogue catalogue)
    {
        if (!catalogue.TryGetArray(property.JadeType, out var array))
        {
            throw new NotSupportedException(
                $"Property '{property.Name}' has JADE type '{property.JadeType}', which the operation catalogue defines as no array.");
        }

        var itemElement = SoapItemElementOf(array);
        writer.WriteStartElement(string.Empty, property.Name, ServiceNamespace);
        foreach (var entry in entries)
        {
            WriteText(writer, itemElement, entry);
        }

        writer.WriteEndElement();
    }

    private static string SoapItemElementOf(ArrayDefinition array) =>
        array is { SoapExposed: true, SoapItemElement: { } itemElement }
            ? itemElement
            : throw new NotSupportedException(
                $"Array '{array.Name}' is not exposed by the WebServiceHTTP list, so the SOAP surface cannot write it.");

    // XmlWriter escapes the text. An empty value, such as the JADE null string, is written as an
    // empty-element tag: WriteString("") would close the start tag and give <street></street> instead.
    // Each carriage return is written as the character reference &#xD;, because Settings' line-break
    // handling turns a literal CR, alone or in CR LF, into LF, and a parser would read a literal CR as
    // LF anyway; the reference reads back as the stored CR. LF and tab are written as they are.
    private static void WriteText(XmlWriter writer, string elementName, string text)
    {
        writer.WriteStartElement(string.Empty, elementName, ServiceNamespace);
        var start = 0;
        for (var index = text.IndexOf('\r'); index >= 0; index = text.IndexOf('\r', start))
        {
            if (index > start)
            {
                writer.WriteString(text[start..index]);
            }

            writer.WriteCharEntity('\r');
            start = index + 1;
        }

        if (start < text.Length)
        {
            writer.WriteString(text[start..]);
        }

        writer.WriteEndElement();
    }

    private static void WriteNil(XmlWriter writer, string elementName)
    {
        writer.WriteStartElement(string.Empty, elementName, ServiceNamespace);
        writer.WriteAttributeString(XsiPrefix, NilAttribute, XsiNamespace, "true");
        writer.WriteEndElement();
    }
}
