using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Erewhon.Facade.Forwarding;

/// <summary>
/// The request and response transform used for every forwarded call: a byte-faithful pass-through
/// that adds no header and rebuilds no payload (AAP 0.3.2.1, 0.4.1).
/// </summary>
/// <remarks>
/// <para>
/// Towards the backend, the method, the raw path and query bytes, the body and every end-to-end
/// request header pass unchanged, <c>SOAPAction</c> quotes, <c>Authorization</c>, <c>Cookie</c>,
/// <c>traceparent</c> and any caller-supplied <c>X-Forwarded-*</c> included. None of them is
/// interpreted. The one header not forwarded is the caller's <c>Host</c>: the outgoing
/// <c>Host</c> is the target authority, which the HTTP stack derives from the destination URI.
/// No <c>X-Forwarded-*</c>, <c>Forwarded</c>, <c>Via</c>, credential or token header is added,
/// because no layer of the facade authenticates or derives an identity and the backend is plain
/// HTTP without authentication. A body-less write carries the <c>Content-Length: 0</c> the HTTP
/// stack would write for it anyway, as an empty content, so the stack sends it to the backend once.
/// </para>
/// <para>
/// The request target is taken from the raw target the caller sent, never from the decoded
/// <c>Request.Path</c>, in each form <see cref="TryGetOriginForm"/> recognises: an origin-form target
/// (<c>/path?query</c>) is sent as it arrived, and an absolute-form target
/// (<c>http://authority/path?query</c>) is sent as the path and query after its authority, so the
/// backend receives the same escapes either way and the caller's authority never chooses the
/// destination. An asterisk-form target (<c>OPTIONS *</c>) is addressed to the target's base URL. Any
/// other form leaves the destination to YARP's default. The HTTP stack upper-cases a known method token
/// and cannot write <c>*</c>, so <see cref="BackendForwarder"/> restores the caller's exact method token
/// and the asterisk on the wire.
/// </para>
/// <para>
/// Towards the caller, the backend's status code, end-to-end response headers and body bytes pass
/// unchanged, 4xx and 5xx results included. The status line and headers are sent to the caller as
/// soon as the backend's have arrived, without waiting for the backend's first body byte.
/// </para>
/// <para>
/// Hop-by-hop fields are dropped in both directions. YARP drops its fixed list (<c>Connection</c>,
/// <c>Keep-Alive</c>, <c>TE</c>, <c>Transfer-Encoding</c>, <c>Upgrade</c>, the <c>Proxy-*</c> fields
/// and the like), and this transform drops every further field that the sender's
/// <c>Connection</c> header nominates (RFC 9110, section 7.6.1): the caller's on the way to the
/// backend, the backend's on the way to the caller. <c>Content-Length</c> is kept even when
/// nominated, because it frames the forwarded body. Kestrel reduces its copy of a caller's
/// <c>Connection</c> header to <c>close</c>, <c>keep-alive</c> or <c>upgrade</c> alone when that is
/// the only connection option the header holds, so the caller's <c>Connection</c> lines are also
/// taken as sent from the <see cref="RawConnectionHeaderCapture"/> of the listener the request arrived
/// on, and the fields either nominates are dropped.
/// </para>
/// <para>
/// The instance holds no state, so one singleton serves every request concurrently.
/// </para>
/// </remarks>
public sealed class PassThroughTransformer : HttpTransformer
{
    /// <summary>The asterisk-form request target, which only <c>OPTIONS</c> carries (RFC 9112 3.2.4).</summary>
    internal const string AsteriskFormTarget = "*";

    // The authority of an absolute-form target ends at the first of these (RFC 3986 3.2).
    private static readonly char[] AuthorityTerminators = ['/', '?', '#'];

    // The raw request target is re-parsed without canonicalisation, so escapes such as %2F, %20
    // and '+', dot segments, and bare query keys such as "?WebServiceHTTP&...&wsdl" reach the
    // backend exactly as the caller sent them. Request.Path and Request.QueryString are decoded
    // forms and cannot be used to rebuild the target without changing it.
    private static readonly UriCreationOptions RawTargetOptions = new()
    {
        DangerousDisablePathAndQueryCanonicalization = true,
    };

    // Optional white space (OWS) around the elements of a Connection list: SP and HTAB.
    private static readonly char[] OptionalWhiteSpace = [' ', '\t'];

    /// <summary>Returns the request target exactly as it arrived, or null when the server exposes none.</summary>
    /// <param name="context">The incoming request.</param>
    /// <returns>The raw request target, for example <c>/agent/Hank%20Williams</c>, <c>http://host/company</c> or <c>*</c>.</returns>
    internal static string? GetRawTarget(HttpContext context) =>
        context.Features.Get<IHttpRequestFeature>()?.RawTarget;

    /// <summary>Tells whether a raw request target is the asterisk form, <c>*</c>.</summary>
    /// <param name="rawTarget">The raw request target, or null.</param>
    /// <returns>True for exactly <c>*</c>.</returns>
    internal static bool IsAsteriskForm(string? rawTarget) =>
        string.Equals(rawTarget, AsteriskFormTarget, StringComparison.Ordinal);

    /// <summary>
    /// Returns the origin-form target, path and query still percent-encoded, that a raw request target
    /// addresses: an origin-form target as sent, or the path and query after the authority of an
    /// absolute-form target.
    /// </summary>
    /// <remarks>
    /// An absolute-form target is recognised by a valid scheme name followed by <c>://</c>, and its
    /// authority ends at the first <c>/</c>, <c>?</c> or <c>#</c>, as the stand-in reads it. Everything
    /// after the authority is kept unchanged, and an empty path becomes <c>/</c> (RFC 9112 3.2.1), so
    /// <c>http://host?x</c> addresses <c>/?x</c>. The authority itself is dropped: it never selects the
    /// destination. The asterisk form, the authority form and an absolute URI without <c>//</c> or with
    /// a fragment right after its authority have no origin form.
    /// </remarks>
    /// <param name="rawTarget">The raw request target, or null.</param>
    /// <param name="originForm">The origin-form target, starting with <c>/</c>, when the method returns true.</param>
    /// <returns>True for an origin-form or absolute-form target.</returns>
    internal static bool TryGetOriginForm(string? rawTarget, [NotNullWhen(true)] out string? originForm)
    {
        originForm = null;
        if (string.IsNullOrEmpty(rawTarget))
        {
            return false;
        }

        if (rawTarget[0] == '/')
        {
            originForm = rawTarget;
            return true;
        }

        var schemeEnd = rawTarget.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !Uri.CheckSchemeName(rawTarget[..schemeEnd]))
        {
            return false;
        }

        var authorityEnd = rawTarget.IndexOfAny(AuthorityTerminators, schemeEnd + 3);
        if (authorityEnd < 0)
        {
            originForm = "/";
            return true;
        }

        switch (rawTarget[authorityEnd])
        {
            case '/':
                originForm = rawTarget[authorityEnd..];
                return true;
            case '?':
                originForm = "/" + rawTarget[authorityEnd..];
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Copies every end-to-end request header unchanged, drops the fields the caller's
    /// <c>Connection</c> header nominates, replaces the caller's <c>Host</c> with the target
    /// authority and points the request at <paramref name="destinationPrefix"/> followed by the
    /// origin form of the caller's raw request target. A body-less request whose method the HTTP stack
    /// sends with <c>Content-Length: 0</c> gets an empty content carrying that same header, so the
    /// handler sends it to the backend once.
    /// </summary>
    /// <param name="httpContext">The incoming request.</param>
    /// <param name="proxyRequest">The outgoing request to the backend.</param>
    /// <param name="destinationPrefix">
    /// The target base URL, for example <c>http://standin:65000</c>, optionally with a path base.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the call is abandoned or its deadline expires.</param>
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        // The base copies every request header except YARP's hop-by-hop skip list, and it copies
        // the caller's Host, which is not on that list.
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);

        // A field the caller's Connection header nominates belongs to the caller's hop alone. The
        // base filed each copied field under the request headers or, for a content field such as
        // Content-Type of a request with a body, under the content headers, so a nominated field is
        // removed from both. YARP re-adds Connection: Upgrade and Upgrade for an upgrade request
        // after this transform, so upgrades are unaffected. Kestrel reduces its copy of a request
        // Connection header to close, keep-alive or upgrade alone, every line of it, when that is the
        // only connection option the header holds; the lines as the caller sent them come from the
        // REST and SOAP listeners' RawConnectionHeaderCapture, for the head of this very request
        // (same method and raw target), and the fields either copy nominates are removed. Without a
        // capture, or once it has stopped for the connection, Kestrel's copy is all there is.
        var rawTarget = GetRawTarget(httpContext);
        RemoveNominatedRequestFields(proxyRequest, httpContext.Request.Headers.Connection);
        if (rawTarget is not null
            && httpContext.Features.Get<RawConnectionHeaderCapture>() is { } capture
            && capture.TryGetConnectionValues(httpContext.Request.Method, rawTarget, out var sentConnectionValues))
        {
            RemoveNominatedRequestFields(proxyRequest, sentConnectionValues);
        }

        // With no Host header set, the handler writes the destination URI's authority.
        proxyRequest.Headers.Host = null;

        // The origin form of an origin-form or absolute-form target is appended to the prefix. An
        // asterisk-form request is addressed to the target's base URL, and BackendForwarder writes the
        // '*' on the wire, because the HTTP stack cannot. For any other form, or a result that does
        // not parse, RequestUri stays null and YARP builds its default address from the prefix,
        // Request.Path and Request.QueryString.
        if (TryGetOriginForm(rawTarget, out var originForm))
        {
            if (Uri.TryCreate(destinationPrefix.TrimEnd('/') + originForm, in RawTargetOptions, out var uri))
            {
                proxyRequest.RequestUri = uri;
            }
        }
        else if (IsAsteriskForm(rawTarget) && Uri.TryCreate(destinationPrefix, in RawTargetOptions, out var root))
        {
            proxyRequest.RequestUri = root;
        }

        // A request that arrived without a body (no Content-Length, not chunked) and without content
        // headers reaches this point with no content. SocketsHttpHandler resends such a request on a new
        // connection, up to three more times, when the backend closes the connection before answering,
        // because no body has started to go out, while the forwarder still sees and logs one call. For a
        // path-only write such as POST /createTender/... each resend is another backend transaction. A
        // request with content is sent at most once by the invoker (SingleDeliveryHandler), so a write
        // gets content here. YARP rejects any content a transform sets except its own empty content, which
        // it creates when a content header is added through RequestTransform.AddHeader. That header is
        // "Content-Length: 0", the value the handler writes at the same place for a content-less request
        // of these methods, so the bytes on the wire do not change. A caller's "Expect: 100-continue" is
        // still forwarded unchanged, and such a write, with an empty or a real body, is sent once as well.
        if (proxyRequest.Content is null && SendsContentLengthWhenBodyless(httpContext, proxyRequest.Method))
        {
            var context = new RequestTransformContext
            {
                HttpContext = httpContext,
                ProxyRequest = proxyRequest,
                DestinationPrefix = destinationPrefix,
                CancellationToken = cancellationToken,
            };
            RequestTransform.AddHeader(context, HeaderNames.ContentLength, "0");
        }
    }

    /// <summary>
    /// Copies the backend's end-to-end response headers unchanged, drops the fields the backend's
    /// <c>Connection</c> header nominates and sends the response head to the caller. The forwarder
    /// has already copied the status code and streams the body afterwards.
    /// </summary>
    /// <param name="httpContext">The incoming request, whose response is being written.</param>
    /// <param name="proxyResponse">The backend's response, or null when the backend did not respond.</param>
    /// <param name="cancellationToken">Cancelled when the call is abandoned or its deadline expires.</param>
    /// <returns>Whether the response is forwarded; false when there is no backend response.</returns>
    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        // Nothing is set or appended here, so the backend's Server, Date, Content-Type and
        // Content-Length values are the ones the caller sees, and failure responses (no backend
        // response, for which the base returns false) are left to BackendForwarder.
        var forwarded = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken)
            .ConfigureAwait(false);
        if (!forwarded || proxyResponse is null)
        {
            return forwarded;
        }

        // A field the backend's Connection header nominates belongs to the backend's hop alone. The
        // base copied the response and content headers into one collection, so each nominated field
        // is removed from it; the collection matches names case-insensitively.
        if (proxyResponse.Headers.NonValidated.TryGetValues(HeaderNames.Connection, out var connection))
        {
            var responseHeaders = httpContext.Response.Headers;
            foreach (var name in NominatedFieldNames(connection))
            {
                responseHeaders.Remove(name);
            }
        }

        // YARP starts the caller's response only with its first body write, so a backend that sends
        // its head and then pauses would hold the caller's status line and headers until its first
        // body byte. The head is sent now instead. YARP defers the start for gRPC over HTTP/2, which
        // no facade listener speaks; on HTTP/1.1 a body-less response that declares no length is then
        // sent chunked with an empty body instead of with Content-Length: 0, the same empty body under
        // hop-by-hop framing. A 101 response is left unstarted, because YARP performs the upgrade
        // after this transform.
        if (proxyResponse.StatusCode != HttpStatusCode.SwitchingProtocols)
        {
            // StartAsync fails on an already cancelled token before anything is sent, so such a call
            // still ends before the response started: with the facade's 504 or Fault, or with
            // nothing for a caller that left. On HTTP/1.1 StartAsync only commits the head to
            // Kestrel's output pipe; the flush puts it on the wire.
            await httpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (httpContext.Response.HasStarted)
            {
                // The head is the caller's now and cannot be replaced: an exception here would make
                // YARP clear the started response, which throws. YARP's body copy observes the same
                // cancellation and reports it, and BackendForwarder then aborts the response.
            }
        }

        return true;
    }

    // The field names a sender's Connection header values nominate: every element of each
    // comma-separated list, trimmed of optional white space, with empty elements skipped. Names
    // are yielded as sent; each removal matches them case-insensitively.
    private static IEnumerable<string> NominatedFieldNames(IEnumerable<string?> connectionValues)
    {
        foreach (var value in connectionValues)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var element in value.Split(','))
            {
                var name = element.Trim(OptionalWhiteSpace);

                // Content-Length frames the body being forwarded: removing it would re-frame the
                // request or response as chunked, so it is kept even when nominated. A nominated
                // Host needs no exception, because the outgoing Host is the target authority anyway.
                if (name.Length > 0 && !string.Equals(name, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
                {
                    yield return name;
                }
            }
        }
    }

    // Removes every field the Connection values nominate from the copied request headers and, for a
    // request with a body, from its content headers.
    private static void RemoveNominatedRequestFields(HttpRequestMessage proxyRequest, IEnumerable<string?> connectionValues)
    {
        foreach (var name in NominatedFieldNames(connectionValues))
        {
            RemoveCopiedField(proxyRequest.Headers, name);
            if (proxyRequest.Content is not null)
            {
                RemoveCopiedField(proxyRequest.Content.Headers, name);
            }
        }
    }

    // HttpHeaders.Remove throws for a name that is not a valid field name (FormatException) or that
    // the collection may not hold, such as a Content-* field on request headers
    // (InvalidOperationException). The non-validated lookup is false for both, so a malformed or
    // misplaced Connection token never fails the call.
    private static void RemoveCopiedField(HttpHeaders headers, string name)
    {
        if (headers.NonValidated.Contains(name))
        {
            headers.Remove(name);
        }
    }

    // GET, HEAD, OPTIONS, DELETE and CONNECT are the methods SocketsHttpHandler sends without any framing
    // header when they carry no content. Any content would make the handler write Content-Length, or
    // Transfer-Encoding: chunked when the content has no length, a header the caller never sent, so these
    // requests keep no content and their headers pass unchanged. The cost is that, without content, the
    // handler may still resend such a request when the backend closes the connection unanswered; these
    // methods are idempotent, so a resend repeats no write. The methods are compared with HttpMethod
    // equality (ordinal, case-insensitive), as the handler compares them. A request the forwarder tunnels
    // as an upgrade is left to its own upgrade handling.
    private static bool SendsContentLengthWhenBodyless(HttpContext httpContext, HttpMethod method) =>
        method != HttpMethod.Get
        && method != HttpMethod.Head
        && method != HttpMethod.Options
        && method != HttpMethod.Delete
        && method != HttpMethod.Connect
        && !IsTunnelledUpgrade(httpContext);

    // YARP tunnels an upgradable request whose Upgrade value is "websocket" or starts with "SPDY/",
    // compared ordinal case-insensitively. It drops any other Upgrade value as a hop-by-hop header and
    // forwards an ordinary request, which then needs the same single delivery as any other.
    private static bool IsTunnelledUpgrade(HttpContext httpContext)
    {
        if (httpContext.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest != true)
        {
            return false;
        }

        var upgrade = httpContext.Request.Headers.Upgrade.ToString();
        return string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            || upgrade.StartsWith("SPDY/", StringComparison.OrdinalIgnoreCase);
    }
}

// The connection-level capture of each request's Connection lines, and the listener extension that
// installs it, live in this file because PassThroughTransformer is the capture's only consumer.
/// <summary>
/// Installs <see cref="RawConnectionHeaderCapture"/> on a Kestrel listener.
/// </summary>
public static class RawConnectionHeaderCaptureExtensions
{
    /// <summary>
    /// Adds connection middleware that gives every HTTP/1.1 connection of the listener its own
    /// <see cref="RawConnectionHeaderCapture"/>, set as a connection feature, and lets it observe the
    /// bytes Kestrel reads from the connection. What Kestrel reads and writes is unchanged.
    /// </summary>
    /// <param name="listen">The listener to install the capture on.</param>
    /// <returns><paramref name="listen"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listen"/> is null.</exception>
    public static ListenOptions UseRawConnectionHeaderCapture(this ListenOptions listen)
    {
        ArgumentNullException.ThrowIfNull(listen);

        // Kestrel adds its HTTP connection handling after every middleware configured on the listener,
        // so the HTTP/1.1 parser reads through the observed transport, and the request's feature
        // collection falls back to the connection features, where the capture is found.
        listen.Use(next => async connection =>
        {
            // Kestrel's limits are final once connections arrive. It admits a request line of up to
            // MaxRequestLineSize bytes with its CRLF and a header block of up to
            // MaxRequestHeadersTotalSize bytes plus its closing CRLF, and counts a chunked body's
            // trailer section against what the header block left of that allowance.
            var limits = listen.KestrelServerOptions.Limits;
            var headerBlockBytes = (long)limits.MaxRequestHeadersTotalSize + 2;
            var transport = connection.Transport;
            var capture = new RawConnectionHeaderCapture(
                (int)Math.Min(limits.MaxRequestLineSize + headerBlockBytes, int.MaxValue),
                (int)Math.Min(headerBlockBytes, int.MaxValue));
            connection.Transport = capture.Observe(transport);
            connection.Features.Set(capture);
            try
            {
                await next(connection).ConfigureAwait(false);
            }
            finally
            {
                connection.Transport = transport;
            }
        });
        return listen;
    }
}

/// <summary>
/// The <c>Connection</c> lines, as the caller sent them, of the request Kestrel is dispatching on one
/// HTTP/1.1 connection.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel replaces a request's <c>Connection</c> header, every line of it, with <c>close</c>,
/// <c>keep-alive</c> or <c>Upgrade</c> alone when that is the only connection option the header holds,
/// before any application code runs, so the other elements of such a header, which nominate
/// connection-specific fields, are not in the request headers the application sees.
/// </para>
/// <para>
/// The capture follows the connection's HTTP/1.1 request framing forward over the bytes Kestrel reads:
/// each request head (bounded by the listener's Kestrel request-line and header limits, so every head
/// Kestrel admits is framed) is parsed once for its method, raw target and <c>Connection</c> line
/// values, and each body, framed by <c>Content-Length</c> or chunked, is skipped
/// by count, never copied. A head's values become current when Kestrel consumes the head, which it does
/// before it dispatches the request; Kestrel dispatches the requests of one HTTP/1.1 connection one at
/// a time, so the head consumed last is that of the request in progress. At most 32 framed heads wait
/// for Kestrel: a pipelining caller's further heads are framed on later reads, as Kestrel catches up.
/// </para>
/// <para>
/// Framing the capture cannot follow (a head or trailer section over its bound, an invalid or
/// conflicting <c>Content-Length</c>, a final transfer coding other than chunked, a malformed chunk)
/// or a reader call it cannot account for stops the capture for the rest of the connection:
/// <see cref="TryGetConnectionValues"/> then answers false, and the request headers Kestrel parsed
/// are all there is. Observation never throws into Kestrel and never changes what Kestrel reads. The
/// state belongs to one connection, so it only ever describes requests of the caller that sent them.
/// </para>
/// <para>
/// Kestrel's reader calls are serialised by the pipe; the lookup comes from the request's own flow, so
/// every access to the shared state takes the instance's lock.
/// </para>
/// </remarks>
public sealed class RawConnectionHeaderCapture
{
    // A chunk-size line with its chunk extensions.
    private const int MaxChunkLineBytes = 8 * 1024;

    // Framed request heads waiting for Kestrel's dispatch, as a pipelining caller sends them.
    private const int MaxPendingHeads = 32;

    private const int InitialLineCapacity = 256;

    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';

    // The request head and trailer section bounds follow the listener's Kestrel request-line and header
    // limits, so they frame every head and trailer section Kestrel admits.
    private readonly int _maxHeadBytes;
    private readonly int _maxTrailerBytes;

    private readonly Lock _gate = new();
    private readonly Queue<CapturedHead> _pending = new();
    private readonly List<string> _connectionValues = [];
    private CapturedHead? _current;
    private bool _stopped;

    // Offsets count bytes from the start of the connection. Kestrel has consumed _consumed bytes and the
    // capture has framed _observed bytes; the buffer of the read Kestrel has not yet advanced past
    // starts at _consumed.
    private ReadOnlySequence<byte> _readBuffer;
    private bool _readOutstanding;
    private long _consumed;
    private long _observed;

    private Framing _framing = Framing.BeforeRequestLine;
    private long _bytesToSkip;
    private int _sectionLength;
    private byte[] _line = new byte[InitialLineCapacity];
    private int _lineLength;
    private byte[] _requestLine = new byte[InitialLineCapacity];
    private int _methodLength;
    private int _targetLength;
    private long _contentLength;
    private bool _hasTransferEncoding;
    private bool _chunked;

    // maxHeadBytes bounds a request line, its header fields and the blank line that ends them;
    // maxTrailerBytes bounds the trailer fields and blank line after a chunked body's last chunk.
    internal RawConnectionHeaderCapture(int maxHeadBytes, int maxTrailerBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTrailerBytes);
        _maxHeadBytes = maxHeadBytes;
        _maxTrailerBytes = maxTrailerBytes;
    }

    private enum Framing
    {
        // CR and LF bytes before a request line, which Kestrel skips.
        BeforeRequestLine,
        RequestLine,
        HeaderLine,
        ContentLengthBody,
        ChunkSizeLine,
        ChunkData,
        ChunkDataCarriageReturn,
        ChunkDataLineFeed,
        TrailerLine,
    }

    /// <summary>
    /// Returns the value of every <c>Connection</c> line of the request in progress, in received order
    /// and as sent, when that request's head is known and has <paramref name="method"/> and
    /// <paramref name="rawTarget"/>.
    /// </summary>
    /// <param name="method">The request method, as Kestrel reports it.</param>
    /// <param name="rawTarget">The request target as on the wire, from <c>IHttpRequestFeature.RawTarget</c>.</param>
    /// <param name="values">The line values, OWS-trimmed; empty when the method returns false.</param>
    /// <returns>
    /// True when the head matches ordinally and holds at least one <c>Connection</c> line; false when it
    /// holds none, when it does not match, or when the capture has stopped for this connection.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> or <paramref name="rawTarget"/> is null.</exception>
    public bool TryGetConnectionValues(string method, string rawTarget, out IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(rawTarget);

        lock (_gate)
        {
            if (!_stopped
                && _current is { ConnectionValues: { } captured } current
                && string.Equals(current.Method, method, StringComparison.Ordinal)
                && string.Equals(current.RawTarget, rawTarget, StringComparison.Ordinal))
            {
                values = captured;
                return true;
            }
        }

        values = [];
        return false;
    }

    // The transport Kestrel reads through: the original output, and an input that reports every read
    // and advance to this capture before passing it on.
    internal IDuplexPipe Observe(IDuplexPipe transport) =>
        new ObservedTransport(new ObservingPipeReader(transport.Input, this), transport.Output);

    // A read returned buffer: the bytes past those already framed are framed now.
    private void OnRead(in ReadOnlySequence<byte> buffer)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            // Observation must never fail Kestrel's read, so any exception, whatever its source, only
            // stops the capture.
            try
            {
                var framedInBuffer = _observed - _consumed;
                if (_readOutstanding || framedInBuffer < 0 || framedInBuffer > buffer.Length)
                {
                    Stop();
                    return;
                }

                _readBuffer = buffer;
                _readOutstanding = true;
                foreach (var segment in buffer.Slice(framedInBuffer))
                {
                    if (!Frame(segment.Span))
                    {
                        return;
                    }
                }
            }
            catch (Exception)
            {
                Stop();
            }
        }
    }

    // Kestrel consumed bytes up to the position: the head that ends there, or last before it, is now
    // the one in progress. Runs before the advance is passed on, while the buffer's segments are live.
    private void OnAdvance(SequencePosition consumed)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            try
            {
                if (!_readOutstanding)
                {
                    Stop();
                    return;
                }

                var consumedLength = _readBuffer.Slice(_readBuffer.Start, consumed).Length;
                _readBuffer = default;
                _readOutstanding = false;
                _consumed += consumedLength;
                if (_consumed > _observed)
                {
                    Stop();
                    return;
                }

                while (_pending.TryPeek(out var head) && head.End <= _consumed)
                {
                    _current = _pending.Dequeue();
                }
            }
            catch (Exception)
            {
                Stop();
            }
        }
    }

    // Runs the framing state machine over bytes not framed before, which follow the last framed byte.
    // Returns false when framing stopped, or paused at the start of a head, before the end of the bytes.
    private bool Frame(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_stopped)
            {
                return false;
            }

            var used = _framing switch
            {
                Framing.BeforeRequestLine => SkipLineBreaks(bytes),
                Framing.RequestLine or Framing.HeaderLine => TakeLine(bytes, _maxHeadBytes),
                Framing.ContentLengthBody or Framing.ChunkData => SkipCounted(bytes),
                Framing.ChunkSizeLine => TakeLine(bytes, MaxChunkLineBytes),
                Framing.ChunkDataCarriageReturn => Expect(bytes[0], CarriageReturn, Framing.ChunkDataLineFeed),
                Framing.ChunkDataLineFeed => Expect(bytes[0], LineFeed, Framing.ChunkSizeLine),
                Framing.TrailerLine => TakeLine(bytes, _maxTrailerBytes),
                _ => throw new UnreachableException(),
            };

            // Only SkipLineBreaks uses no byte: at the first byte of a head, which it either starts
            // (the next step reads the request line from it) or pauses at.
            if (used == 0 && _framing == Framing.BeforeRequestLine)
            {
                return false;
            }

            bytes = bytes[used..];
        }

        return !_stopped;
    }

    // Skips the CR and LF bytes before a request line. A head starts only while fewer than the bound
    // wait for Kestrel; otherwise framing pauses at its first byte, and a later read, which Kestrel makes
    // before it consumes past the heads already waiting, resumes it there.
    private int SkipLineBreaks(ReadOnlySpan<byte> bytes)
    {
        var used = bytes.IndexOfAnyExcept(CarriageReturn, LineFeed);
        if (used < 0)
        {
            used = bytes.Length;
        }
        else if (_pending.Count < MaxPendingHeads)
        {
            _framing = Framing.RequestLine;
            _sectionLength = 0;
            _lineLength = 0;
        }

        _observed += used;
        return used;
    }

    // Body bytes are counted past, never copied or inspected.
    private int SkipCounted(ReadOnlySpan<byte> bytes)
    {
        var used = (int)Math.Min(_bytesToSkip, bytes.Length);
        _bytesToSkip -= used;
        _observed += used;
        if (_bytesToSkip == 0)
        {
            _framing = _framing == Framing.ContentLengthBody ? Framing.BeforeRequestLine : Framing.ChunkDataCarriageReturn;
        }

        return used;
    }

    // The CR and the LF that end a chunk's data.
    private int Expect(byte actual, byte expected, Framing next)
    {
        _observed++;
        if (actual == expected)
        {
            _framing = next;
        }
        else
        {
            Stop();
        }

        return 1;
    }

    // Collects a line up to its LF, within the byte limit of its section, and handles it once complete.
    // A line ends at LF with an optional CR before it, as Kestrel reads request and header lines. The
    // line buffer grows only as far as the section limit, the most one line of the section can hold.
    private int TakeLine(ReadOnlySpan<byte> bytes, int sectionLimit)
    {
        var lineFeed = bytes.IndexOf(LineFeed);
        var used = lineFeed < 0 ? bytes.Length : lineFeed + 1;
        if (used > sectionLimit - _sectionLength)
        {
            Stop();
            return bytes.Length;
        }

        _sectionLength += used;
        var content = bytes[..(lineFeed < 0 ? used : lineFeed)];
        if (_lineLength + content.Length > _line.Length)
        {
            Array.Resize(ref _line, Math.Min(Math.Max(_line.Length * 2, _lineLength + content.Length), sectionLimit));
        }

        content.CopyTo(_line.AsSpan(_lineLength));
        _lineLength += content.Length;
        _observed += used;
        if (lineFeed < 0)
        {
            return used;
        }

        var line = _line.AsSpan(0, _lineLength);
        var endsWithCarriageReturn = !line.IsEmpty && line[^1] == CarriageReturn;
        if (endsWithCarriageReturn)
        {
            line = line[..^1];
        }

        _lineLength = 0;
        switch (_framing)
        {
            case Framing.RequestLine:
                OnRequestLine(line);
                break;
            case Framing.HeaderLine when line.IsEmpty:
                OnHeadEnd();
                break;
            case Framing.HeaderLine:
                OnHeaderField(line);
                break;
            case Framing.ChunkSizeLine:
                OnChunkSizeLine(line, endsWithCarriageReturn);
                break;
            case Framing.TrailerLine when line.IsEmpty:
                _framing = Framing.BeforeRequestLine;
                break;
            default:
                // A trailer field: only the empty line that ends the trailer section matters.
                break;
        }

        return used;
    }

    // "<method> SP <request-target> SP <version>": the method and the raw target are kept with the line.
    private void OnRequestLine(ReadOnlySpan<byte> line)
    {
        var methodLength = line.IndexOf((byte)' ');
        var targetLength = methodLength <= 0 ? -1 : line[(methodLength + 1)..].IndexOf((byte)' ');
        if (targetLength <= 0)
        {
            Stop();
            return;
        }

        // The line is the start of _line, which becomes the request line buffer; the old request line
        // buffer collects the header lines.
        (_line, _requestLine) = (_requestLine, _line);
        _methodLength = methodLength;
        _targetLength = targetLength;
        _connectionValues.Clear();
        _contentLength = -1;
        _hasTransferEncoding = false;
        _chunked = false;
        _framing = Framing.HeaderLine;
    }

    // "<field-name> ':' OWS <field-value> OWS". A line that starts with white space (obsolete line
    // folding) or has no field name is one Kestrel rejects.
    private void OnHeaderField(ReadOnlySpan<byte> line)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0 || line[0] is (byte)' ' or (byte)'\t')
        {
            Stop();
            return;
        }

        var name = line[..colon];
        var value = TrimOptionalWhiteSpace(line[(colon + 1)..]);
        if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
        {
            _connectionValues.Add(Encoding.Latin1.GetString(value));
        }
        else if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
        {
            if (!TryParseContentLength(value, out var length)
                || (_contentLength >= 0 && _contentLength != length))
            {
                Stop();
                return;
            }

            _contentLength = length;
        }
        else if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
        {
            // As Kestrel decides, the last coding of the last line is the final one.
            _hasTransferEncoding = true;
            foreach (var range in value.Split((byte)','))
            {
                var coding = TrimOptionalWhiteSpace(value[range]);
                if (!coding.IsEmpty)
                {
                    _chunked = Ascii.EqualsIgnoreCase(coding, "chunked"u8);
                }
            }
        }
    }

    // The blank line: the head is complete, and its body framing decides where the next head starts.
    // Transfer-Encoding overrides Content-Length; a head with neither, an upgrade request included, has
    // no body.
    private void OnHeadEnd()
    {
        Framing next;
        if (_hasTransferEncoding)
        {
            if (!_chunked)
            {
                Stop();
                return;
            }

            next = Framing.ChunkSizeLine;
        }
        else if (_contentLength > 0)
        {
            _bytesToSkip = _contentLength;
            next = Framing.ContentLengthBody;
        }
        else
        {
            next = Framing.BeforeRequestLine;
        }

        // The method and target are only needed to confirm a head that has Connection lines.
        _pending.Enqueue(_connectionValues.Count == 0
            ? new CapturedHead(_observed, null, null, null)
            : new CapturedHead(
                _observed,
                Encoding.Latin1.GetString(_requestLine.AsSpan(0, _methodLength)),
                Encoding.Latin1.GetString(_requestLine.AsSpan(_methodLength + 1, _targetLength)),
                _connectionValues.ToArray()));
        _connectionValues.Clear();
        _sectionLength = 0;
        _framing = next;
    }

    // "<hex size> [';' extensions] CRLF". A chunk line must end with CRLF, as Kestrel requires.
    private void OnChunkSizeLine(ReadOnlySpan<byte> line, bool endsWithCarriageReturn)
    {
        var digits = 0;
        long size = 0;
        while (digits < line.Length && HexValue(line[digits]) >= 0)
        {
            if (size > (long.MaxValue >> 4))
            {
                Stop();
                return;
            }

            size = (size << 4) | (long)HexValue(line[digits]);
            digits++;
        }

        if (!endsWithCarriageReturn || digits == 0 || (digits < line.Length && line[digits] != (byte)';'))
        {
            Stop();
            return;
        }

        _sectionLength = 0;
        if (size == 0)
        {
            // The last chunk: trailer fields follow, up to an empty line.
            _framing = Framing.TrailerLine;
        }
        else
        {
            _bytesToSkip = size;
            _framing = Framing.ChunkData;
        }
    }

    // Any inconsistency ends the capture for the connection: nothing is reported for it afterwards.
    private void Stop()
    {
        _stopped = true;
        _pending.Clear();
        _current = null;
        _connectionValues.Clear();
        _readBuffer = default;
        _line = [];
        _requestLine = [];
    }

    private static bool TryParseContentLength(ReadOnlySpan<byte> value, out long length)
    {
        length = 0;
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var b in value)
        {
            if (b is < (byte)'0' or > (byte)'9' || length > (long.MaxValue - 9) / 10)
            {
                return false;
            }

            length = (length * 10) + (b - '0');
        }

        return true;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // Optional white space (OWS): SP and HTAB.
    private static ReadOnlySpan<byte> TrimOptionalWhiteSpace(ReadOnlySpan<byte> value) => value.Trim(" \t"u8);

    // One request head: where it ends on the connection and, when it has Connection lines, its method,
    // raw target and line values.
    private readonly record struct CapturedHead(long End, string? Method, string? RawTarget, string[]? ConnectionValues);

    private sealed class ObservedTransport(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    // Passes every call to the transport's reader unchanged, and reports each returned buffer and each
    // advance to the capture first.
    private sealed class ObservingPipeReader(PipeReader inner, RawConnectionHeaderCapture capture) : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var pending = inner.ReadAsync(cancellationToken);
            if (!pending.IsCompletedSuccessfully)
            {
                return ObserveAsync(pending);
            }

            var result = pending.Result;
            capture.OnRead(result.Buffer);
            return new ValueTask<ReadResult>(result);
        }

        public override bool TryRead(out ReadResult result)
        {
            if (!inner.TryRead(out result))
            {
                return false;
            }

            capture.OnRead(result.Buffer);
            return true;
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            capture.OnAdvance(consumed);
            inner.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            capture.OnAdvance(consumed);
            inner.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<ReadResult> ObserveAsync(ValueTask<ReadResult> pending)
        {
            var result = await pending.ConfigureAwait(false);
            capture.OnRead(result.Buffer);
            return result;
        }
    }
}
