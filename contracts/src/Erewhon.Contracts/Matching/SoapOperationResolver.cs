using System.Collections.Frozen;
using System.Text;
using System.Xml;
using Erewhon.Contracts.Catalogue;
using Microsoft.AspNetCore.Http;

namespace Erewhon.Contracts.Matching;

/// <summary>Where a <see cref="SoapResolution"/> found its operation.</summary>
public enum SoapResolutionSource
{
    /// <summary>The <c>SOAPAction</c> header named a catalogue operation, and the body was not read.</summary>
    Header,

    /// <summary>
    /// The first child element of the SOAP 1.1 <c>Body</c>, read within the first
    /// <see cref="SoapOperationResolver.MaxInspectionBytes"/> bytes, is a catalogue request element.
    /// </summary>
    Body,

    /// <summary>Neither the header nor the body identified a catalogue operation.</summary>
    None,
}

/// <summary>
/// The outcome of resolving a SOAP request: the catalogue operation it calls and how that was found,
/// or no operation. An unresolved request is still a valid request, and its caller forwards it unchanged
/// to the default target.
/// </summary>
/// <param name="Operation">The <see cref="Surface.Soap"/> operation the request calls; null when unresolved.</param>
/// <param name="Source">
/// How the operation was found: <see cref="SoapResolutionSource.None"/> exactly when
/// <paramref name="Operation"/> is null.
/// </param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="Source"/> is not a defined <see cref="SoapResolutionSource"/>.</exception>
/// <exception cref="ArgumentException">
/// <paramref name="Source"/> is <see cref="SoapResolutionSource.None"/> with an operation, or another source without one.
/// </exception>
public sealed record SoapResolution(OperationDescriptor? Operation, SoapResolutionSource Source)
{
    /// <summary>The result for a request that names no catalogue operation.</summary>
    public static SoapResolution Unresolved { get; } = new(null, SoapResolutionSource.None);

    /// <summary>The <see cref="Surface.Soap"/> operation the request calls; null when unresolved.</summary>
    public OperationDescriptor? Operation { get; } = Operation;

    /// <summary>How <see cref="Operation"/> was found; <see cref="SoapResolutionSource.None"/> exactly when it is null.</summary>
    public SoapResolutionSource Source { get; } = CheckSource(Operation, Source);

    private static SoapResolutionSource CheckSource(OperationDescriptor? operation, SoapResolutionSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown SOAP resolution source.");
        }

        if ((operation is null) != (source == SoapResolutionSource.None))
        {
            throw new ArgumentException(
                $"The source must be {SoapResolutionSource.None} exactly when the operation is null, but the " +
                $"operation is {operation?.Id ?? "null"} and the source is {source}.",
                nameof(source));
        }

        return source;
    }
}

/// <summary>
/// Resolves a request to the SOAP endpoint (<see cref="SoapAddress.EndpointPathAndQuery"/>) to the
/// <see cref="Surface.Soap"/> catalogue operation it calls, and recognises WSDL requests. The facade
/// routes each SOAP request with it, and the JADE stand-in dispatches with it.
/// </summary>
/// <remarks>
/// <para>
/// Resolution only chooses a route. It never rejects a request, never writes a response, never changes a
/// header and never alters, truncates or consumes a body byte: when it reads the body, the stream is left
/// at position 0 with every original byte available, so the forwarder sends the full request unchanged.
/// Malformed XML, a document that is not a SOAP 1.1 envelope, a <c>Body</c> whose first child element is
/// not reached within <see cref="MaxInspectionBytes"/>, and an unknown element name all resolve to no
/// operation, which the caller forwards unchanged to its default target.
/// </para>
/// <para>
/// Every SOAP action and request element name comes from the catalogue's <see cref="SoapBinding"/>
/// entries. The constructor precomputes immutable lookups, so one instance is safe for concurrent use
/// and can be registered as a singleton.
/// </para>
/// </remarks>
public sealed class SoapOperationResolver
{
    /// <summary>
    /// The most body bytes read while looking for the first child element of the SOAP <c>Body</c>,
    /// 64 KiB. Bytes beyond the bound are never read by the resolver and stay in the stream for the
    /// forwarder.
    /// </summary>
    public const int MaxInspectionBytes = 64 * 1024;

    // Protocol facts rather than catalogue facts: the SOAP 1.1 envelope of the frozen interface, its two
    // structural element names, the header JADE's SOAP clients send and the WSDL query key. The
    // constructor checks the catalogue's envelope namespace and WSDL key against them.
    private const string Soap11EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string EnvelopeElementName = "Envelope";
    private const string BodyElementName = "Body";
    private const string SoapActionHeaderName = "SOAPAction";
    private const string WsdlQueryKey = "wsdl";

    // The reader resolves nothing external and expands no DTD, so inspecting a hostile body can neither
    // reach the network nor grow beyond the bounded input. Kestrel forbids synchronous reads of a request
    // body, so the reader runs in async mode and only its async methods are called.
    private static readonly XmlReaderSettings InspectionSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    private readonly FrozenDictionary<string, OperationDescriptor>.AlternateLookup<ReadOnlySpan<char>> _operationsByAction;
    private readonly FrozenDictionary<(string NamespaceUri, string LocalName), OperationDescriptor> _operationsByRequestElement;

    /// <summary>Builds the action and request-element lookups from the catalogue's SOAP operations.</summary>
    /// <param name="catalogue">The operation catalogue, normally <see cref="OperationCatalogue.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The catalogue's envelope namespace is not SOAP 1.1 or its WSDL query key is not <c>wsdl</c>; a SOAP
    /// operation has no action or request element, or an action that is not the message namespace followed
    /// by the request element; or two SOAP operations share an action or a request element. The message
    /// names the operation ids involved.
    /// </exception>
    public SoapOperationResolver(OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var address = catalogue.Addresses.Soap;
        RequireCatalogueValue("addresses.soap.envelopeNamespace", address.EnvelopeNamespace, Soap11EnvelopeNamespace);
        RequireCatalogueValue("addresses.soap.wsdlQueryKey", address.WsdlQueryKey, WsdlQueryKey);
        if (string.IsNullOrWhiteSpace(address.TargetNamespace))
        {
            throw new InvalidOperationException("The operation catalogue has no SOAP message namespace (addresses.soap.targetNamespace).");
        }

        var messageNamespace = address.TargetNamespace;
        var byAction = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);
        var byRequestElement = new Dictionary<(string NamespaceUri, string LocalName), OperationDescriptor>();
        foreach (var operation in catalogue.GetBySurface(Surface.Soap))
        {
            if (operation.Soap is not { } binding
                || string.IsNullOrWhiteSpace(binding.Action)
                || string.IsNullOrWhiteSpace(binding.RequestElement))
            {
                throw new InvalidOperationException(
                    $"SOAP operation '{operation.Id}' has no SOAP action or request element in the operation catalogue.");
            }

            // JADE derives each SOAPAction from the message namespace and the operation name, so the
            // namespace the request element is matched in is the one every action is built on.
            if (!string.Equals(binding.Action, messageNamespace + binding.RequestElement, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"SOAP operation '{operation.Id}' has action '{binding.Action}', which is not the message " +
                    $"namespace '{messageNamespace}' followed by its request element '{binding.RequestElement}'.");
            }

            AddUnique(byAction, binding.Action, operation, "SOAP action");
            AddUnique(byRequestElement, (messageNamespace, binding.RequestElement), operation, "request element");
        }

        _operationsByAction = byAction
            .ToFrozenDictionary(StringComparer.Ordinal)
            .GetAlternateLookup<ReadOnlySpan<char>>();
        _operationsByRequestElement = byRequestElement.ToFrozenDictionary();
    }

    /// <summary>
    /// Resolves a <c>SOAPAction</c> header value to its operation. Surrounding whitespace is trimmed, then
    /// one pair of surrounding double quotes is removed when both are present, and the rest must equal a
    /// catalogue <see cref="SoapBinding.Action"/> exactly (ordinal, case-sensitive).
    /// </summary>
    /// <param name="soapAction">The header value, for example <c>"urn:JadeWebServices/WebServiceHTTP/getAgent"</c>, quoted or not.</param>
    /// <returns>
    /// The operation; null for a null, empty or <c>""</c> value, an unknown action, or a value with only
    /// one of its two quotes.
    /// </returns>
    public OperationDescriptor? ResolveFromSoapAction(string? soapAction)
    {
        if (soapAction is null)
        {
            return null;
        }

        var value = soapAction.AsSpan().Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return !value.IsEmpty && _operationsByAction.TryGetValue(value, out var operation) ? operation : null;
    }

    /// <summary>
    /// Resolves a SOAP request to its operation: from the <c>SOAPAction</c> header first and, only when the
    /// header does not name an operation, from the first child element of the envelope's <c>Body</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The header is used only when the request carries exactly one <c>SOAPAction</c> value; none, several,
    /// an empty value or an unknown action count as unmatched. A header match returns without touching
    /// <see cref="HttpRequest.Body"/>: the body is neither buffered nor read.
    /// </para>
    /// <para>
    /// Otherwise the body is made rewindable with <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/>,
    /// which sets no buffer limit and replaces <see cref="HttpRequest.Body"/> only when it is not already
    /// seekable. At most <see cref="MaxInspectionBytes"/> bytes are read from position 0, and the body is
    /// rewound to position 0 on every exit, exceptions included, with its full content intact. Content that
    /// does not identify an operation gives <see cref="SoapResolution.Unresolved"/> and never an exception.
    /// </para>
    /// <para>The HTTP method, path and <c>Content-Type</c> are not checked; endpoint mapping does that.</para>
    /// </remarks>
    /// <param name="request">The request; its headers are only read.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The operation and its <see cref="SoapResolutionSource"/>, or <see cref="SoapResolution.Unresolved"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while the body was read.</exception>
    /// <exception cref="IOException">
    /// Reading the body failed in transport, for example when the client aborted the upload
    /// (<see cref="BadHttpRequestException"/>). The body is rewound before the exception propagates.
    /// </exception>
    public ValueTask<SoapResolution> ResolveAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var soapAction = request.Headers[SoapActionHeaderName];
        if (soapAction.Count == 1 && ResolveFromSoapAction(soapAction[0]) is { } fromHeader)
        {
            return ValueTask.FromResult(new SoapResolution(fromHeader, SoapResolutionSource.Header));
        }

        return ResolveFromRequestBodyAsync(request, cancellationToken);
    }

    /// <summary>
    /// Resolves an operation from the first child element of the SOAP 1.1 <c>Body</c> in a stream the
    /// caller already holds rewindable, such as a buffered request body or a test fixture.
    /// </summary>
    /// <remarks>
    /// At most <see cref="MaxInspectionBytes"/> bytes are read from position 0. The stream is only read,
    /// never written or truncated, and is rewound to position 0 on every exit, exceptions included.
    /// Content that does not identify an operation gives null and never an exception.
    /// </remarks>
    /// <param name="body">A readable, seekable stream holding the whole request body.</param>
    /// <param name="cancellationToken">Cancels reading the stream.</param>
    /// <returns>The operation; null when the body does not identify one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="body"/> is not readable or not seekable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while the stream was read.</exception>
    /// <exception cref="IOException">Reading the stream failed. The stream is rewound before the exception propagates.</exception>
    public ValueTask<OperationDescriptor?> ResolveFromBodyAsync(Stream body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.CanRead || !body.CanSeek)
        {
            throw new ArgumentException(
                "The body stream must be readable and seekable so that it can be rewound after inspection; " +
                $"for a request body use {nameof(ResolveAsync)}, which enables buffering.",
                nameof(body));
        }

        return InspectFromStartAsync(body, cancellationToken);
    }

    /// <summary>Tells whether a request asks for the WSDL; see <see cref="IsWsdlRequest(string, string?)"/>.</summary>
    /// <param name="request">The request; only its method and raw query string are read.</param>
    /// <returns>True for a <c>GET</c> whose query has a key that percent-decodes to <c>wsdl</c>, in any ASCII case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static bool IsWsdlRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsWsdlRequest(request.Method, request.QueryString.Value);
    }

    /// <summary>
    /// Tells whether a method and raw query string ask for the WSDL: the method is <c>GET</c> and some
    /// <c>&amp;</c>-separated part of the query has the key <c>wsdl</c>. A part's key is the text before
    /// its first <c>=</c>, or the whole part when it has none; both separators are found in the raw text,
    /// so an escaped <c>&amp;</c> or <c>=</c> stays part of the key. The key is percent-decoded once and
    /// then compared with <c>wsdl</c> case-insensitively in ASCII letters only. The path is not inspected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP&amp;wsdl</c>,
    /// <c>?wsdl</c>, <c>?WSDL=1</c>, <c>?%77sdl</c> and <c>?%57SDL=1</c> ask for it; <c>?x=wsdl</c>,
    /// <c>?x=%77sdl</c>, <c>?wsdlx</c>, <c>?%2577sdl</c> (decoded once, to <c>%77sdl</c>), <c>?wsdl%3D1</c>,
    /// <c>?w%C5%BFdl</c> (U+017F is not an ASCII letter), an empty query and any <c>POST</c> do not.
    /// </para>
    /// <para>
    /// The query itself is only read: the caller forwards or serves the request with its query unchanged.
    /// A <c>+</c> is not translated to a space, because neither can form the key.
    /// </para>
    /// </remarks>
    /// <param name="method">The HTTP method.</param>
    /// <param name="rawQuery">The raw query string, with or without its leading <c>?</c>; null or empty for none.</param>
    /// <returns>True for a WSDL request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
    public static bool IsWsdlRequest(string method, string? rawQuery)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!HttpMethods.IsGet(method) || string.IsNullOrEmpty(rawQuery))
        {
            return false;
        }

        var query = rawQuery.AsSpan();
        if (query[0] == '?')
        {
            query = query[1..];
        }

        foreach (var range in query.Split('&'))
        {
            var part = query[range];
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (IsWsdlQueryKey(key))
            {
                return true;
            }
        }

        return false;
    }

    // The key arrives split on the raw '&' and '=', so an escaped separator is still part of it. It is
    // percent-decoded once, as ASP.NET Core decodes query keys: an invalid escape stays literal, and a '%'
    // produced by decoding is not decoded again. The comparison folds ASCII letters only, so a decoded
    // non-ASCII character such as U+017F (long s) never stands in for an 's'.
    private static bool IsWsdlQueryKey(ReadOnlySpan<char> key)
    {
        if (!key.Contains('%'))
        {
            return Ascii.EqualsIgnoreCase(key, WsdlQueryKey);
        }

        // Each letter of the key is spelled by at most one three-character escape, so a longer key never
        // decodes to it.
        if (key.Length > WsdlQueryKey.Length * 3)
        {
            return false;
        }

        // Unescaping never lengthens its input, so a buffer of the key's own length always suffices.
        Span<char> decoded = stackalloc char[key.Length];
        return Uri.TryUnescapeDataString(key, decoded, out var written)
            && Ascii.EqualsIgnoreCase(decoded[..written], WsdlQueryKey);
    }

    private static void RequireCatalogueValue(string member, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The operation catalogue's {member} is '{actual}', but SOAP resolution requires '{expected}'.");
        }
    }

    private static void AddUnique<TKey>(
        Dictionary<TKey, OperationDescriptor> lookup,
        TKey key,
        OperationDescriptor operation,
        string what)
        where TKey : notnull
    {
        if (!lookup.TryAdd(key, operation))
        {
            throw new InvalidOperationException(
                $"SOAP operations '{lookup[key].Id}' and '{operation.Id}' share the {what} '{key}'.");
        }
    }

    private async ValueTask<SoapResolution> ResolveFromRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No arguments: the buffer limit stays unlimited, so inspection never imposes a body-size limit of its own.
        request.EnableBuffering();
        var operation = await InspectFromStartAsync(request.Body, cancellationToken).ConfigureAwait(false);
        return operation is null ? SoapResolution.Unresolved : new SoapResolution(operation, SoapResolutionSource.Body);
    }

    private async ValueTask<OperationDescriptor?> InspectFromStartAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            body.Position = 0;
            return await InspectAsync(body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            body.Position = 0;
        }
    }

    private async ValueTask<OperationDescriptor?> InspectAsync(Stream body, CancellationToken cancellationToken)
    {
        using var bounded = new BoundedReadStream(body, MaxInspectionBytes, cancellationToken);
        try
        {
            using var reader = XmlReader.Create(bounded, InspectionSettings);
            return await FindRequestOperationAsync(reader).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            // Malformed XML, a DTD, the end of input the bound produces, and an unknown or undecodable
            // encoding (DecoderFallbackException is an ArgumentException) are content, never a reason to
            // reject the request: it stays unresolved and is forwarded unchanged. Cancellation and transport
            // failures are not content and propagate.
            return null;
        }
    }

    private async Task<OperationDescriptor?> FindRequestOperationAsync(XmlReader reader)
    {
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element
            || !IsEnvelopeElement(reader, EnvelopeElementName)
            || reader.IsEmptyElement)
        {
            return null;
        }

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        // Walk the Envelope's direct children, skipping each whole subtree before Body (the optional Header).
        while (true)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.EndElement:
                    // Every child subtree is skipped whole, so this is the Envelope's end tag: there is no Body.
                    return null;

                case XmlNodeType.Element when IsEnvelopeElement(reader, BodyElementName):
                    return reader.IsEmptyElement ? null : await FindFirstBodyChildAsync(reader).ConfigureAwait(false);

                case XmlNodeType.Element:
                    // Leaves the reader on the next sibling or on the Envelope's end tag, so no Read follows.
                    await reader.SkipAsync().ConfigureAwait(false);
                    break;

                default:
                    if (!await reader.ReadAsync().ConfigureAwait(false))
                    {
                        return null;
                    }

                    break;
            }
        }
    }

    private async Task<OperationDescriptor?> FindFirstBodyChildAsync(XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    // Only the first element child decides; later siblings are never read.
                    return _operationsByRequestElement.TryGetValue((reader.NamespaceURI, reader.LocalName), out var operation)
                        ? operation
                        : null;

                case XmlNodeType.EndElement:
                    // No element child has started, so this is Body's own end tag.
                    return null;
            }
        }

        return null;
    }

    private static bool IsEnvelopeElement(XmlReader reader, string localName) =>
        string.Equals(reader.LocalName, localName, StringComparison.Ordinal)
        && string.Equals(reader.NamespaceURI, Soap11EnvelopeNamespace, StringComparison.Ordinal);

    /// <summary>
    /// A read-only, forward-only view of at most a fixed number of bytes of another stream, which reports
    /// the end of input once the allowance is used. It does not own the inner stream: disposing the view
    /// (the base <see cref="Stream"/> disposal, synchronous or asynchronous) never disposes the inner stream.
    /// </summary>
    /// <remarks>
    /// It exposes no limit-reached flag: running out of allowance reaches the reader as the end of input,
    /// which it reports as an <see cref="XmlException"/>, and resolution treats that like any other
    /// unreadable content, so no caller needs to tell the two apart.
    /// </remarks>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _inspectionToken;
        private int _remaining;

        public BoundedReadStream(Stream inner, int allowance, CancellationToken inspectionToken)
        {
            _inner = inner;
            _remaining = allowance;
            _inspectionToken = inspectionToken;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer[..Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        // Overridden so that the reader's Task-based reads stay asynchronous: the base implementation
        // would call the synchronous Read, which Kestrel rejects for a request body.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }

            var window = buffer[..Math.Min(buffer.Length, _remaining)];
            int read;

            // XmlReader passes no token of its own, so the inspection's token is what cancels its reads.
            if (cancellationToken.CanBeCanceled && _inspectionToken.CanBeCanceled)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _inspectionToken);
                read = await _inner.ReadAsync(window, linked.Token).ConfigureAwait(false);
            }
            else
            {
                var token = cancellationToken.CanBeCanceled ? cancellationToken : _inspectionToken;
                read = await _inner.ReadAsync(window, token).ConfigureAwait(false);
            }

            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
            // Nothing is ever written, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
