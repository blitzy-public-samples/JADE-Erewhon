using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using CoreWCF;
using CoreWCF.Channels;
using CoreWCF.Description;
using CoreWCF.Dispatcher;

namespace Erewhon.Api.Soap;

/// <summary>
/// Checks the SOAP body of each request to one service endpoint against the document/literal wrapped request
/// message its action names in the endpoint's contract, and rejects a body that does not match with a
/// <see cref="FaultException"/> whose fault code is <c>Sender</c> (SOAP 1.1 <c>Client</c>) and whose reason names the
/// expected and the received element. A request whose action is not a request action of the contract passes
/// unchanged to CoreWCF.
/// <list type="bullet">
/// <item><description>
/// Rejected: a body the binding's XML reader cannot read (XML that is not well-formed, a processing instruction, or a
/// reader quota exceeded), with the reason followed by the reader's message; an empty body; a first body element
/// other than the wrapper element of the action's request message; an element after the wrapper element; a child of
/// the wrapper element that is not one of the message's parts; a part that occurs more than once; inside a part whose
/// type carries <see cref="XmlTypeAttribute"/>, a child that is not one of the type's fields or a field that occurs
/// more than once; a part or field element that holds an element. Every one of these elements is compared by local
/// name and namespace, so an element in any other namespace is rejected.
/// </description></item>
/// <item><description>
/// Accepted: absent parts and fields, parts and fields in any order, attributes such as <c>xsi:nil</c>, and text,
/// white space, CDATA and comments.
/// </description></item>
/// </list>
/// </summary>
public sealed class SoapRequestContractInspector : IDispatchMessageInspector
{
    private const string WildcardAction = "*";

    private readonly IReadOnlyDictionary<string, RequestShape> _requests;

    /// <summary>
    /// Creates the inspector of an endpoint with <paramref name="contract"/>: for each operation, the input message's
    /// action, wrapper element and parts. A part whose type carries <see cref="XmlTypeAttribute"/> holds one field
    /// element per public instance property with a public getter and setter and no <see cref="XmlIgnoreAttribute"/>,
    /// named by the property's <see cref="XmlElementAttribute.ElementName"/> or else the property name, in the
    /// property's <see cref="XmlElementAttribute.Namespace"/>, else the type's <see cref="XmlTypeAttribute.Namespace"/>,
    /// else the part's namespace. Every other part, and every field, holds text only. An operation whose input message
    /// has no action, the action <c>*</c>, or no wrapper element is not checked.
    /// </summary>
    /// <param name="contract">The contract of the endpoint whose requests are checked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public SoapRequestContractInspector(ContractDescription contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var requests = new Dictionary<string, RequestShape>(StringComparer.Ordinal);
        foreach (OperationDescription operation in contract.Operations)
        {
            MessageDescription? input = operation.Messages.FirstOrDefault(
                message => message.Direction == MessageDirection.Input);
            if (input is null
                || string.IsNullOrEmpty(input.Action)
                || input.Action == WildcardAction
                || string.IsNullOrEmpty(input.Body.WrapperName))
            {
                continue;
            }

            var parts = new Dictionary<XmlQualifiedName, ElementShape>();
            foreach (MessagePartDescription part in input.Body.Parts)
            {
                parts[new XmlQualifiedName(part.Name, part.Namespace)] = PartShape(part);
            }

            var wrapper = new XmlQualifiedName(input.Body.WrapperName, input.Body.WrapperNamespace ?? string.Empty);
            requests[input.Action] = new RequestShape(input.Action, wrapper, new ElementShape(wrapper, parts));
        }

        _requests = requests;
    }

    /// <summary>
    /// Checks the body of <paramref name="request"/> when its action is a request action of the contract, replacing
    /// <paramref name="request"/> with an unread copy of it; leaves any other request untouched.
    /// </summary>
    /// <param name="request">The received request; replaced by an unread copy when it is checked.</param>
    /// <param name="channel">The channel the request arrived on. Not used.</param>
    /// <param name="instanceContext">The instance context of the service. Not used.</param>
    /// <returns><see langword="null"/>, the correlation state passed to <see cref="BeforeSendReply"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="FaultException">
    /// The binding's XML reader cannot read the body, or the body does not match the request message of its action;
    /// the fault code is <c>Sender</c>.
    /// </exception>
    public object? AfterReceiveRequest(ref Message request, IClientChannel channel, InstanceContext instanceContext)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? action = request.Headers.Action;
        if (action is null || !_requests.TryGetValue(action, out RequestShape? shape))
        {
            return null;
        }

        string? violation;
        try
        {
            using MessageBuffer buffer = request.CreateBufferedCopy(int.MaxValue);
            request = buffer.CreateMessage();
            using Message copy = buffer.CreateMessage();
            violation = FindViolation(copy, shape);
        }
        catch (XmlException exception)
        {
            violation = $"The SOAP body cannot be read by the XML reader of the binding; action {shape.Action} "
                + $"takes the element {Format(shape.Wrapper)}. {exception.Message}";
        }

        if (violation is not null)
        {
            throw new FaultException(new FaultReason(violation), FaultCode.CreateSenderFaultCode(null));
        }

        return null;
    }

    /// <summary>Does nothing.</summary>
    /// <param name="reply">The reply. Not used.</param>
    /// <param name="correlationState">The value <see cref="AfterReceiveRequest"/> returned. Not used.</param>
    public void BeforeSendReply(ref Message reply, object? correlationState)
    {
    }

    /// <summary>
    /// Returns the shape of <paramref name="part"/>: its fields when its type carries
    /// <see cref="XmlTypeAttribute"/>, otherwise text only.
    /// </summary>
    private static ElementShape PartShape(MessagePartDescription part)
    {
        var name = new XmlQualifiedName(part.Name, part.Namespace ?? string.Empty);
        XmlTypeAttribute? xmlType = part.Type?.GetCustomAttribute<XmlTypeAttribute>(inherit: false);
        if (part.Type is null || xmlType is null)
        {
            return new ElementShape(name, null);
        }

        string fieldNamespace = xmlType.Namespace ?? name.Namespace;
        var fields = new Dictionary<XmlQualifiedName, ElementShape>();
        foreach (PropertyInfo property in part.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetGetMethod() is null
                || property.GetSetMethod() is null
                || property.GetIndexParameters().Length > 0
                || property.IsDefined(typeof(XmlIgnoreAttribute), inherit: true))
            {
                continue;
            }

            XmlElementAttribute? element = property.GetCustomAttribute<XmlElementAttribute>(inherit: true);
            var fieldName = new XmlQualifiedName(
                string.IsNullOrEmpty(element?.ElementName) ? property.Name : element.ElementName,
                element?.Namespace ?? fieldNamespace);
            fields[fieldName] = new ElementShape(fieldName, null);
        }

        return new ElementShape(name, fields);
    }

    /// <summary>
    /// Returns the description of the first way the body of <paramref name="message"/> departs from
    /// <paramref name="shape"/>, or <see langword="null"/> when it matches.
    /// </summary>
    /// <exception cref="XmlException">The XML reader of the message cannot read the body.</exception>
    private static string? FindViolation(Message message, RequestShape shape)
    {
        string emptyBody = $"The SOAP body is empty; action {shape.Action} takes the element {Format(shape.Wrapper)}.";
        if (message.IsEmpty)
        {
            return emptyBody;
        }

        using XmlDictionaryReader reader = message.GetReaderAtBodyContents();
        if (reader.MoveToContent() != XmlNodeType.Element)
        {
            return emptyBody;
        }

        XmlQualifiedName received = NameOf(reader);
        if (received != shape.Wrapper)
        {
            return $"The SOAP body element is {Format(received)}; action {shape.Action} takes the element "
                + $"{Format(shape.Wrapper)}.";
        }

        string? violation = FindChildViolation(reader, shape.Body);
        if (violation is not null)
        {
            return violation;
        }

        reader.Read();
        if (reader.MoveToContent() == XmlNodeType.Element)
        {
            return $"The SOAP body holds the element {Format(NameOf(reader))} after the element "
                + $"{Format(shape.Wrapper)}; action {shape.Action} takes that element only.";
        }

        return null;
    }

    /// <summary>
    /// Reads the content of the element <paramref name="reader"/> is positioned on, which has
    /// <paramref name="shape"/>, up to and including its end, and returns the description of the first child element
    /// that departs from <paramref name="shape"/>, or <see langword="null"/> when none does. The reader is left on the
    /// element's end, or on the element itself when it is empty; after a departure it is left where it was found.
    /// </summary>
    private static string? FindChildViolation(XmlDictionaryReader reader, ElementShape shape)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        int depth = reader.Depth;
        var seen = new HashSet<XmlQualifiedName>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return null;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            XmlQualifiedName received = NameOf(reader);
            if (shape.Children is null)
            {
                return $"The element {Format(shape.Name)} holds the element {Format(received)}; it takes text only.";
            }

            if (!shape.Children.TryGetValue(received, out ElementShape? child))
            {
                return $"The element {Format(shape.Name)} holds the element {Format(received)}; it takes only the "
                    + $"elements {string.Join(", ", shape.Children.Keys.Select(Format))}.";
            }

            if (!seen.Add(received))
            {
                return $"The element {Format(shape.Name)} holds the element {Format(received)} more than once.";
            }

            string? violation = FindChildViolation(reader, child);
            if (violation is not null)
            {
                return violation;
            }
        }

        return null;
    }

    /// <summary>Returns the local name and namespace of the element <paramref name="reader"/> is positioned on.</summary>
    private static XmlQualifiedName NameOf(XmlReader reader) => new(reader.LocalName, reader.NamespaceURI);

    /// <summary>Returns <paramref name="name"/> as <c>{namespace}local-name</c>, with <c>{}</c> for no namespace.</summary>
    private static string Format(XmlQualifiedName name) => $"{{{name.Namespace}}}{name.Name}";

    /// <summary>
    /// An element of a request body: its name and the elements it may hold, or <see langword="null"/> when it holds
    /// text only.
    /// </summary>
    /// <param name="Name">The element's local name and namespace.</param>
    /// <param name="Children">The elements it may hold, each at most once, keyed by local name and namespace.</param>
    private sealed record ElementShape(
        XmlQualifiedName Name,
        IReadOnlyDictionary<XmlQualifiedName, ElementShape>? Children);

    /// <summary>The request message of one action: the action, its wrapper element and the wrapper's shape.</summary>
    /// <param name="Action">The action that selects the operation.</param>
    /// <param name="Wrapper">The local name and namespace of the wrapper element.</param>
    /// <param name="Body">The wrapper element with its parts.</param>
    private sealed record RequestShape(string Action, XmlQualifiedName Wrapper, ElementShape Body);
}
