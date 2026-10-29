using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.Facade.Forwarding;
using Microsoft.Extensions.Options;

namespace Erewhon.Facade.Soap;

/// <summary>
/// The SOAP surface of the facade on the SOAP listener (21001, the address of the JADE
/// <c>WebServiceHTTP</c> application): the nine <c>ErewhonWebService</c> operations at
/// <c>/jade/jadehttp.dll</c>, the WSDL, and a catch-all for every other request on that socket.
/// </summary>
/// <remarks>
/// <para>
/// No SOAP payload is parsed or re-serialised here. A <c>POST</c> is resolved to its catalogue operation
/// by <see cref="SoapOperationResolver"/> (the <c>SOAPAction</c> header first, then at most 64 KiB of the
/// body, which it rewinds) and handed to <see cref="BackendForwarder"/> with its full original body. A
/// request that names no catalogue operation, any other verb or path, and a WSDL request while no WSDL
/// is published all go unchanged to <c>Routing:DefaultTarget</c>, so whatever JADE answers at this
/// address today keeps reaching it. Nothing here answers 400, 404, 405 or 413.
/// </para>
/// <para>
/// The handlers are <see cref="RequestDelegate"/>s, so minimal-API parameter binding never runs and the
/// framework never reads, rejects or replaces the body. Each endpoint is dispatched only on the SOAP
/// socket through <see cref="ListenerRoutingExtensions.RequireListener{TBuilder}(TBuilder, ListenerKind)"/>,
/// never by the <c>Host</c> header, and none carries a name or an OpenAPI description: names are the REST
/// operations' <c>operationId</c>s.
/// </para>
/// </remarks>
public static partial class SoapEndpoints
{
    // A static class cannot be a type argument of ILogger<T>, so the category is spelt out.
    private const string LoggerCategory = "Erewhon.Facade.Soap.SoapEndpoints";

    // The same pattern as the REST listener's catch-all; ListenerPortMatcherPolicy keeps only the one whose
    // listener owns the socket. MapFallback is not used: its nonfile constraint skips paths with a dot.
    private const string CatchAllPattern = "/{**path}";

    /// <summary>
    /// Maps the SOAP endpoint (<c>POST</c> and <c>GET</c> on the catalogue's SOAP path) and the SOAP
    /// listener's catch-all.
    /// </summary>
    /// <remarks>
    /// <see cref="WsdlDocument"/>, <see cref="SoapOperationResolver"/> and <see cref="BackendForwarder"/>
    /// are singletons resolved here once; resolving <see cref="WsdlDocument"/> loads the WSDL and logs the
    /// result at start-up. The routing table is still read per request by the forwarder.
    /// </remarks>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns><paramref name="endpoints"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A required service is not registered.</exception>
    public static IEndpointRouteBuilder MapSoapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var wsdl = services.GetRequiredService<WsdlDocument>();
        var resolver = services.GetRequiredService<SoapOperationResolver>();
        var forwarder = services.GetRequiredService<BackendForwarder>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        // Routing ignores the query string, so every query form of the endpoint matches; the forwarder
        // sends the raw path and query on unchanged.
        var endpointPath = OperationCatalogue.Default.Addresses.Soap.Path;

        // Typed as RequestDelegate so that the RequestDelegate overloads are bound, never the Delegate
        // overloads that infer parameters and could read the body.
        RequestDelegate post = context => PostAsync(context, resolver, forwarder, logger);
        RequestDelegate get = context => GetAsync(context, wsdl, forwarder, logger);
        RequestDelegate catchAll = context => forwarder.ForwardAsync(context, Surface.Soap, operation: null);

        endpoints.MapPost(endpointPath, post)
            .RequireListener(ListenerKind.Soap)
            .ExcludeFromDescription();

        endpoints.MapGet(endpointPath, get)
            .RequireListener(ListenerKind.Soap)
            .ExcludeFromDescription();

        endpoints.Map(CatchAllPattern, catchAll)
            .WithOrder(int.MaxValue)
            .RequireListener(ListenerKind.Soap)
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>Resolves a SOAP call and forwards it, whole, to its operation's target or the default target.</summary>
    private static async Task PostAsync(
        HttpContext context,
        SoapOperationResolver resolver,
        BackendForwarder forwarder,
        ILogger logger)
    {
        // Installed before the resolver can buffer the body, so the forwarder's later reads of the server's
        // body, through the buffer, can be cancelled without leaving the server's body reader invalid.
        CancellableRequestBody.Install(context);

        SoapResolution resolution;
        try
        {
            resolution = await resolver.ResolveAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // A cancelled or broken body read after the caller disconnected: nobody is left to answer.
            LogCallerGone(logger);
            return;
        }
        catch (Exception exception)
        {
            // Resolution only chooses a route, so its failure must not lose the call: the rewound body
            // goes to the default target like any unresolved request. A body that cannot be rewound
            // could no longer be forwarded whole, so that failure propagates instead.
            LogResolutionFailed(logger, exception.GetType().FullName, context.Request.Body.CanSeek);
            if (!context.Request.Body.CanSeek)
            {
                throw;
            }

            context.Request.Body.Position = 0;
            resolution = SoapResolution.Unresolved;
        }

        LogResolved(logger, resolution.Operation?.JadeMethod ?? BackendForwarder.UnmatchedOperation, resolution.Source);
        await forwarder.ForwardAsync(context, Surface.Soap, resolution.Operation).ConfigureAwait(false);
    }

    /// <summary>Serves the published WSDL, or forwards any other GET (and a WSDL GET while none is published).</summary>
    private static async Task GetAsync(
        HttpContext context,
        WsdlDocument wsdl,
        BackendForwarder forwarder,
        ILogger logger)
    {
        if (!wsdl.IsPresent || !SoapOperationResolver.IsWsdlRequest(context.Request))
        {
            await forwarder.ForwardAsync(context, Surface.Soap, operation: null).ConfigureAwait(false);
            return;
        }

        try
        {
            await wsdl.WriteAsync(context.Response, context.Request, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            LogCallerGone(logger);
        }
    }

    [LoggerMessage(
        EventId = 1211,
        EventName = "SoapResolved",
        Level = LogLevel.Debug,
        Message = "SOAP request resolved to {Operation} from {Source}")]
    private static partial void LogResolved(ILogger logger, string operation, SoapResolutionSource source);

    [LoggerMessage(
        EventId = 1212,
        EventName = "SoapResolutionFailed",
        Level = LogLevel.Warning,
        Message = "SOAP operation resolution failed with {ExceptionType} (body rewindable: {Rewindable}); a rewindable request is forwarded unchanged to the default target")]
    private static partial void LogResolutionFailed(ILogger logger, string? exceptionType, bool rewindable);

    [LoggerMessage(
        EventId = 1213,
        EventName = "SoapCallerGone",
        Level = LogLevel.Debug,
        Message = "SOAP request abandoned: the caller disconnected")]
    private static partial void LogCallerGone(ILogger logger);
}

/// <summary>
/// The WSDL the facade publishes: <c>contracts/wsdl/ErewhonWebService.wsdl</c>, loaded once at start-up
/// and served with its single <c>soap:address/@location</c> set to the address the caller used.
/// </summary>
/// <remarks>
/// <para>
/// The facade never generates a WSDL of its own. The file is split once, at load, into the bytes before
/// and after the value of <c>soap:address/@location</c>, so every response is those exact file bytes with
/// only that attribute value replaced: the XML declaration, the stand-in label comment
/// (<see cref="Erewhon.Contracts.SourceLabels.WsdlHeaderComment"/>), whitespace, line breaks and any
/// byte-order mark are untouched. When the split cannot be verified, the document is re-serialised once
/// through <see cref="XDocument"/> with whitespace and comments preserved and split there instead; the
/// WSDL contract check compares semantically, so that form is equivalent.
/// </para>
/// <para>
/// A missing, unreadable, non-UTF-8 or malformed file, or one without exactly one <c>soap:address</c>
/// carrying a <c>location</c>, leaves the document absent: the facade still starts, <c>/admin/info</c>
/// reports <see cref="AbsentStatus"/>, and WSDL requests are forwarded to the default target unchanged.
/// </para>
/// <para>The instance is immutable after load and safe for concurrent use as a singleton.</para>
/// </remarks>
public sealed partial class WsdlDocument
{
    /// <summary>The <see cref="Status"/> of a loaded document.</summary>
    public const string PresentStatus = "present";

    /// <summary>The <see cref="Status"/> of a document that is not published.</summary>
    public const string AbsentStatus = "absent";

    // The WSDL 1.1 SOAP binding namespace, the consumer's soap= prefix (Erewhon/ErewhonWebServiceConsumerSchema.scm:1714).
    private const string SoapBindingNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
    private const string AddressElementName = "address";
    private const string LocationAttributeName = "location";

    private const string NotFoundReason = "file not found";
    private const string NotReadableReason = "file not readable";
    private const string NotUtf8Reason = "not UTF-8";
    private const string MalformedReason = "not well-formed XML";
    private const string NoLocationReason = "soap:address has no location attribute";
    private const string NotRewritableReason = "soap:address/@location could not be located for rewriting";

    private const string SpliceMode = "in-place splice of the file bytes";
    private const string ReserialisedMode = "splice of the re-serialised document";

    // Every character the escaping rewrites, so the load-time check proves the split with all of them.
    private const string ProbeLocation = "http://probe.invalid:1/p?a&b<c>d\"e'f\tg\rh\ni";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // A hostile or broken file can neither expand a DTD nor reach the network. Comments and whitespace
    // are kept, because the re-serialised form must keep them.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private static readonly XName AddressName = XName.Get(AddressElementName, SoapBindingNamespace);

    private readonly byte[] _prefix;
    private readonly byte[] _suffix;

    /// <summary>Loads the WSDL named by <c>Facade:WsdlPath</c>; <c>Program.cs</c> registers this as a singleton.</summary>
    /// <param name="options">The <c>Facade</c> section, whose <see cref="FacadeOptions.ResolveWsdlPath"/> names the file.</param>
    /// <param name="logger">Receives the load result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    public WsdlDocument(IOptions<FacadeOptions> options, ILogger<WsdlDocument> logger)
        : this(Read((options ?? throw new ArgumentNullException(nameof(options))).Value.ResolveWsdlPath(), logger))
    {
    }

    private WsdlDocument(LoadResult result)
    {
        Path = result.Path;
        AbsentReason = result.AbsentReason;
        _prefix = result.Prefix;
        _suffix = result.Suffix;
    }

    /// <summary>The absolute path the WSDL was loaded from, or looked for.</summary>
    public string Path { get; }

    /// <summary>True when the WSDL is published; false when WSDL requests are forwarded instead.</summary>
    public bool IsPresent => AbsentReason is null;

    /// <summary><see cref="PresentStatus"/> or <see cref="AbsentStatus"/>, as <c>/admin/info</c> reports it.</summary>
    public string Status => IsPresent ? PresentStatus : AbsentStatus;

    /// <summary>
    /// Why the WSDL is not published, a short fixed text such as <c>file not found</c>, <c>not UTF-8</c>,
    /// <c>not well-formed XML</c> or <c>expected exactly one soap:address, found 2</c>; null when present.
    /// </summary>
    public string? AbsentReason { get; }

    /// <summary>Loads the WSDL at <paramref name="path"/>, logging the result; it never throws for the file's content.</summary>
    /// <param name="path">The WSDL file path.</param>
    /// <param name="logger">Receives the load result.</param>
    /// <returns>The document, present or absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public static WsdlDocument Load(string path, ILogger logger) => new(Read(path, logger));

    /// <summary>
    /// Returns the address the WSDL publishes for <paramref name="request"/>:
    /// <c>{scheme}://{Host}/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>,
    /// unescaped.
    /// </summary>
    /// <remarks>
    /// The authority is the caller's <c>Host</c> header as sent. Without one (HTTP/1.0), it is the local
    /// address and port of the connection, as the stand-in's WSDL generator forms it, so the address
    /// still names the socket the caller reached; with neither, it is empty.
    /// </remarks>
    /// <param name="request">The WSDL request.</param>
    /// <returns>The endpoint URL.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string BuildLocation(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authority = request.Host.Value is { Length: > 0 } host
            ? host
            : LocalAuthority(request.HttpContext.Connection);
        return string.Concat(
            request.Scheme,
            "://",
            authority,
            OperationCatalogue.Default.Addresses.Soap.EndpointPathAndQuery);
    }

    /// <summary>Returns the published bytes with <paramref name="location"/> as the <c>soap:address/@location</c> value.</summary>
    /// <param name="location">The unescaped address; it is XML-escaped into the attribute.</param>
    /// <returns>A new array holding the whole document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="location"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document is absent.</exception>
    public byte[] Render(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        EnsurePresent();
        return Splice(_prefix, EncodeLocation(location), _suffix);
    }

    /// <summary>
    /// Writes the WSDL for <paramref name="request"/>: status 200, <see cref="SoapFaultWriter.ContentType"/>,
    /// <c>Content-Length</c> and the document with the request's address. No other header is set, and no
    /// per-host output is cached.
    /// </summary>
    /// <param name="response">The response to write.</param>
    /// <param name="request">The WSDL request, whose scheme and <c>Host</c> form the address.</param>
    /// <param name="cancellationToken">Cancels the body write, normally the request's abort token.</param>
    /// <returns>A task that completes when the body is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document is absent; such a request is forwarded instead.</exception>
    public async Task WriteAsync(HttpResponse response, HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);
        EnsurePresent();

        var location = EncodeLocation(BuildLocation(request));

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = SoapFaultWriter.ContentType;
        response.ContentLength = _prefix.Length + location.Length + _suffix.Length;

        await response.Body.WriteAsync(_prefix, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(location, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(_suffix, cancellationToken).ConfigureAwait(false);
    }

    private void EnsurePresent()
    {
        if (!IsPresent)
        {
            throw new InvalidOperationException(
                $"The WSDL at {Path} is not published ({AbsentReason}); WSDL requests are forwarded to the default target.");
        }
    }


    private static LoadResult Read(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(path))
        {
            LogNotFound(logger, path);
            return LoadResult.Absent(path, NotFoundReason);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unpublished(logger, path, NotReadableReason);
        }

        byte[] bom = bytes.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom : [];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, bom.Length, bytes.Length - bom.Length);
        }
        catch (DecoderFallbackException)
        {
            return Unpublished(logger, path, NotUtf8Reason);
        }

        AddressScan scan;
        try
        {
            scan = ScanAddresses(text);
        }
        catch (XmlException)
        {
            return Unpublished(logger, path, MalformedReason);
        }

        if (scan.Count != 1)
        {
            return Unpublished(
                logger,
                path,
                string.Create(CultureInfo.InvariantCulture, $"expected exactly one soap:address, found {scan.Count}"));
        }

        if (scan.Location is null)
        {
            return Unpublished(logger, path, NoLocationReason);
        }

        if (TrySpliceInPlace(text, bom, bytes, scan, out var prefix, out var suffix))
        {
            LogLoaded(logger, path, bytes.Length, SpliceMode);
            return LoadResult.Present(path, prefix, suffix);
        }

        if (TrySpliceReserialised(text, bom, out prefix, out suffix))
        {
            LogLoaded(logger, path, bytes.Length, ReserialisedMode);
            return LoadResult.Present(path, prefix, suffix);
        }

        return Unpublished(logger, path, NotRewritableReason);
    }

    private static LoadResult Unpublished(ILogger logger, string path, string reason)
    {
        LogUnpublished(logger, path, reason);
        return LoadResult.Absent(path, reason);
    }

    /// <summary>
    /// Reads the whole document, which proves it well-formed, and counts its <c>soap:address</c> elements,
    /// recording the first one's <c>location</c> value and that attribute's line and position.
    /// </summary>
    /// <exception cref="XmlException">The text is not well-formed XML or holds a DTD.</exception>
    private static AddressScan ScanAddresses(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), ReaderSettings);
        var lineInfo = reader as IXmlLineInfo;

        var count = 0;
        string? location = null;
        int line = 0, position = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, AddressElementName, StringComparison.Ordinal)
                || !string.Equals(reader.NamespaceURI, SoapBindingNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            if (count == 1 && reader.MoveToAttribute(LocationAttributeName))
            {
                location = reader.Value;
                if (lineInfo is not null && lineInfo.HasLineInfo())
                {
                    line = lineInfo.LineNumber;
                    position = lineInfo.LinePosition;
                }

                reader.MoveToElement();
            }
        }

        return new AddressScan(count, location, line, position);
    }

    /// <summary>
    /// Splits the file bytes around the location value found by its line and position, and accepts the
    /// split only when it is lossless and its slot is exactly that one attribute value.
    /// </summary>
    private static bool TrySpliceInPlace(
        string text,
        byte[] bom,
        byte[] original,
        AddressScan scan,
        out byte[] prefix,
        out byte[] suffix)
    {
        prefix = [];
        suffix = [];
        if (!TryLocateValue(text, scan.Line, scan.Position, out var valueStart, out var valueEnd))
        {
            return false;
        }

        prefix = [.. bom, .. StrictUtf8.GetBytes(text, 0, valueStart)];
        suffix = StrictUtf8.GetBytes(text, valueEnd, text.Length - valueEnd);
        var value = StrictUtf8.GetBytes(text, valueStart, valueEnd - valueStart);

        return Splice(prefix, value, suffix).AsSpan().SequenceEqual(original) && ProbeRoundTrips(prefix, suffix);
    }

    /// <summary>
    /// Re-serialises the document once, whitespace and comments preserved, with a marker as the location
    /// value, and splits the written bytes around the marker.
    /// </summary>
    private static bool TrySpliceReserialised(string text, byte[] bom, out byte[] prefix, out byte[] suffix)
    {
        prefix = [];
        suffix = [];
        try
        {
            XDocument document;
            using (var reader = XmlReader.Create(new StringReader(text), ReaderSettings))
            {
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }

            // A value that occurs nowhere else in the document marks where every address goes.
            var marker = "urn:erewhon-facade:wsdl-location:" + Guid.NewGuid().ToString("N");
            document.Descendants(AddressName).Single().SetAttributeValue(LocationAttributeName, marker);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = document.Declaration is null,
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = "\n",
            };

            using var buffer = new MemoryStream();
            using (var writer = XmlWriter.Create(buffer, settings))
            {
                document.Save(writer);
            }

            var written = StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var at = written.IndexOf(marker, StringComparison.Ordinal);
            var end = at + marker.Length;
            if (at < 0 || written.IndexOf(marker, end, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            prefix = [.. bom, .. StrictUtf8.GetBytes(written, 0, at)];
            suffix = StrictUtf8.GetBytes(written, end, written.Length - end);
            return ProbeRoundTrips(prefix, suffix);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders <see cref="ProbeLocation"/> into the split and checks that the result parses with exactly one
    /// <c>soap:address</c> whose <c>location</c> is the probe, every escaped character included.
    /// </summary>
    private static bool ProbeRoundTrips(byte[] prefix, byte[] suffix)
    {
        var rendered = Splice(prefix, EncodeLocation(ProbeLocation), suffix);
        try
        {
            using var stream = new MemoryStream(rendered, writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            var addresses = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Descendants(AddressName).ToList();
            return addresses.Count == 1
                && string.Equals(addresses[0].Attribute(LocationAttributeName)?.Value, ProbeLocation, StringComparison.Ordinal);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the value of the <c>location</c> attribute whose name starts at the 1-based
    /// <paramref name="line"/> and <paramref name="position"/>: the characters between the quotes after
    /// <c>location</c>, optional whitespace, <c>=</c> and optional whitespace.
    /// </summary>
    private static bool TryLocateValue(string text, int line, int position, out int valueStart, out int valueEnd)
    {
        valueStart = -1;
        valueEnd = -1;

        var index = OffsetOf(text, line, position);
        if (index < 0 || !text.AsSpan(index).StartsWith(LocationAttributeName, StringComparison.Ordinal))
        {
            return false;
        }

        index = SkipXmlWhitespace(text, index + LocationAttributeName.Length);
        if (index >= text.Length || text[index] != '=')
        {
            return false;
        }

        index = SkipXmlWhitespace(text, index + 1);
        if (index >= text.Length || text[index] is not ('"' or '\''))
        {
            return false;
        }

        // An attribute value never holds its own quote character literally.
        var end = text.IndexOf(text[index], index + 1);
        if (end < 0)
        {
            return false;
        }

        valueStart = index + 1;
        valueEnd = end;
        return true;
    }

    // XmlReader counts lines after normalising line breaks, so the original text is walked with \r\n, \n
    // and a lone \r each ending one line.
    private static int OffsetOf(string text, int line, int position)
    {
        if (line < 1 || position < 1)
        {
            return -1;
        }

        var index = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.AsSpan(index).IndexOfAny('\r', '\n');
            if (next < 0)
            {
                return -1;
            }

            index += next;
            index += text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
        }

        var offset = index + position - 1;
        return offset < text.Length ? offset : -1;
    }

    private static int SkipXmlWhitespace(string text, int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t' or '\r' or '\n')
        {
            index++;
        }

        return index;
    }

    private static byte[] Splice(byte[] prefix, byte[] value, byte[] suffix) => [.. prefix, .. value, .. suffix];

    private static byte[] EncodeLocation(string location) => Encoding.UTF8.GetBytes(EscapeAttributeValue(location));

    /// <summary>
    /// Escapes text for an attribute value in either quote character. The <c>Host</c> header is chosen by
    /// the caller and the endpoint query holds <c>&amp;</c>, so every markup character is escaped; tab,
    /// CR and LF become character references, which attribute-value normalisation would otherwise turn
    /// into spaces. A character XML 1.0 cannot carry (a control character, a lone surrogate, U+FFFE or
    /// U+FFFF) becomes U+FFFD, so the document stays well-formed whatever the caller sends.
    /// </summary>
    private static string EscapeAttributeValue(string value)
    {
        StringBuilder? escaped = null;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            string? replacement = c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                '\t' => "&#x9;",
                '\r' => "&#xD;",
                '\n' => "&#xA;",
                _ => null,
            };

            if (replacement is null)
            {
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
                {
                    escaped?.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }

                if (XmlConvert.IsXmlChar(c))
                {
                    escaped?.Append(c);
                    continue;
                }

                replacement = "\uFFFD";
            }

            escaped ??= new StringBuilder(value.Length + 32).Append(value, 0, i);
            escaped.Append(replacement);
        }

        return escaped?.ToString() ?? value;
    }

    // The stand-in's form (WsdlGenerator.LocalAuthority): an IPv4-mapped address as IPv4, an IPv6 address
    // bracketed with its zone index escaped as %25 (RFC 6874), then the local port.
    private static string LocalAuthority(ConnectionInfo connection)
    {
        if (connection.LocalIpAddress is not { } address)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address.ToString().Replace("%", "%25", StringComparison.Ordinal)}]"
            : address.ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{host}:{connection.LocalPort}");
    }

    [LoggerMessage(
        EventId = 1201,
        EventName = "WsdlNotFound",
        Level = LogLevel.Warning,
        Message = "WSDL not found at {Path}; WSDL requests will be forwarded to the default target")]
    private static partial void LogNotFound(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1202,
        EventName = "WsdlUnpublished",
        Level = LogLevel.Warning,
        Message = "WSDL at {Path} is not published ({Reason}); WSDL requests will be forwarded to the default target")]
    private static partial void LogUnpublished(ILogger logger, string path, string reason);

    [LoggerMessage(
        EventId = 1203,
        EventName = "WsdlLoaded",
        Level = LogLevel.Information,
        Message = "WSDL loaded from {Path} ({Length} bytes); soap:address/@location is rewritten per request by {Mode}")]
    private static partial void LogLoaded(ILogger logger, string path, int length, string mode);

    /// <summary>The outcome of a load: the path, why it is absent (null when present) and the split bytes.</summary>
    private sealed record LoadResult(string Path, string? AbsentReason, byte[] Prefix, byte[] Suffix)
    {
        public static LoadResult Absent(string path, string reason) => new(path, reason, [], []);

        public static LoadResult Present(string path, byte[] prefix, byte[] suffix) => new(path, null, prefix, suffix);
    }

    /// <summary>The <c>soap:address</c> count and the first one's <c>location</c> value, line and position (0 when unknown).</summary>
    private readonly record struct AddressScan(int Count, string? Location, int Line, int Position);
}

