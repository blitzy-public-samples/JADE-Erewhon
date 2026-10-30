using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// that rebuilds no payload and adds no header (AAP 0.3.2.1, 0.4.1).
/// </summary>
/// <remarks>
/// <para>
/// Towards the backend, the method, the raw path and query bytes, the body and every end-to-end
/// request header pass unchanged, <c>SOAPAction</c> quotes, <c>Authorization</c>, <c>Cookie</c>,
/// <c>traceparent</c>, the caller's own <c>Expect</c> and any caller-supplied <c>X-Forwarded-*</c>
/// included. None of them is interpreted. The one header not forwarded is the caller's <c>Host</c>:
/// the outgoing <c>Host</c> is the target authority, which the HTTP stack derives from the
/// destination URI. No <c>X-Forwarded-*</c>, <c>Forwarded</c>, <c>Via</c>, credential or token header
/// is added, because no layer of the facade authenticates or derives an identity and the backend is
/// plain HTTP without authentication. A body-less write carries the <c>Content-Length: 0</c> the HTTP
/// stack would write for it anyway, as an empty content, so the stack sends it to the backend once.
/// </para>
/// <para>
/// An upload reaches the backend with an <c>Expect</c> header only when the caller sent one, whatever
/// the size or framing of its body. A backend that answers an upload before it has read all of it, for
/// example with 413 for its own body-size limit, whether it then keeps the connection open or closes
/// it, still has its answer reach the caller unchanged, also while the caller pauses its upload: the
/// backend connection, a <see cref="BackendConnectionStream"/>, watches the backend while the upload is
/// in progress and takes its answer, close or reset as the end of the upload, which is never resent.
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
/// The instance holds no state, so one singleton serves every request concurrently; the one fact it
/// reports about a call, that the backend's whole response has been relayed, goes to that call's own
/// <see cref="BackendCallState"/>, and the body that counts a declared length on its way to the caller
/// is installed on that call's own response.
/// </para>
/// </remarks>
public sealed class PassThroughTransformer : HttpTransformer
{
    /// <summary>The asterisk-form request target, which only <c>OPTIONS</c> carries (RFC 9112 3.2.4).</summary>
    internal const string AsteriskFormTarget = "*";

    // The authority of an absolute-form target ends at the first of these (RFC 3986 3.2).
    private static readonly char[] AuthorityTerminators = ['/', '?', '#'];

    // The complete raw request target is re-parsed without the canonicalisation System.Uri applies
    // by default, which decodes escapes of unreserved characters such as %41 and %7E and removes
    // dot segments. Escapes such as %2F and %20, '+', dot segments, and bare query keys such as
    // "?WebServiceHTTP&...&wsdl" therefore reach the backend exactly as the caller sent them.
    // Request.Path cannot rebuild the target without changing it: Kestrel decodes the escapes in
    // it, %20 and %41 for example though not %2F, and removes its dot segments.
    // Request.QueryString keeps the query as sent, escapes included, and the raw target supplies
    // it along with the path.
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

    /// <summary>
    /// Tells whether the request carries a body for the backend, decided as YARP decides whether to send
    /// one: the server's answer when it gives one, otherwise a chunked or non-empty declared body.
    /// </summary>
    /// <param name="context">The incoming request.</param>
    /// <returns>True for a request with a body, whatever its length.</returns>
    internal static bool CarriesBody(HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>() is { } detection
            ? detection.CanHaveBody
            : context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0;

    /// <summary>
    /// Tells whether the request carries a body framed by its declared <c>Content-Length</c>, which the
    /// HTTP stack sends to the backend framed the same way; a chunked body, or none, is not.
    /// </summary>
    /// <param name="context">The incoming request.</param>
    /// <returns>True for a body of declared, non-zero length that is not chunked.</returns>
    internal static bool CarriesLengthDelimitedBody(HttpContext context) =>
        CarriesBody(context)
        && context.Request.ContentLength > 0
        && context.Request.Headers.TransferEncoding.Count == 0;

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
    /// authority, and points the request at <paramref name="destinationPrefix"/>
    /// followed by the origin form of the caller's raw request target. A body-less request whose method the HTTP stack
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
        // (same method and raw target), and the fields either copy nominates are removed. The capture
        // follows every request framing Kestrel accepts, chunk extensions of any length included, and
        // stops only at framing Kestrel itself rejects or at a reader call it cannot account for.
        // Without a capture, or once it no longer knows the request in progress, Kestrel's copy is all
        // there is.
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
        // headers reaches this point with no content. SocketsHttpHandler resends such a request on another
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
    /// <remarks>
    /// Once the head has been sent, the call's <see cref="BackendCallState"/> learns when the backend's
    /// whole response has been relayed, as soon as the end of the response can be seen: at once when the
    /// head is the whole response, because it answers a <c>HEAD</c> request, has status 204 or 304 or
    /// declares <c>Content-Length: 0</c>, and, for a declared non-zero length, once the forwarder has
    /// written that many body bytes to the caller's response, counted by a pass-through body installed
    /// on this call's response alone. A chunked or unknown-length response is relayed only once its body
    /// has been copied to the end, which <see cref="TransformResponseTrailersAsync"/> records. A head the
    /// flush did not finish sending, and a 101 response, record nothing here.
    /// </remarks>
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
            var headSent = false;
            try
            {
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                headSent = true;
            }
            catch (OperationCanceledException) when (httpContext.Response.HasStarted)
            {
                // The head is the caller's now and cannot be replaced: an exception here would make
                // YARP clear the started response, which throws. YARP's body copy observes the same
                // cancellation and reports it, and BackendForwarder then aborts the response.
            }

            if (headSent && BackendCallState.Current is { } callState)
            {
                TrackRelayedResponse(httpContext, callState);
            }
        }

        return true;
    }

    /// <summary>
    /// Copies the backend's response trailers unchanged, if the caller's response supports them, and
    /// records on the call's <see cref="BackendCallState"/> that the backend's whole response has been
    /// relayed. This is where a chunked or unknown-length response is first known to be relayed; a
    /// response without a body or with a declared length was recorded as soon as it had been sent
    /// (<see cref="TransformResponseAsync"/>).
    /// </summary>
    /// <param name="httpContext">The incoming request, whose response is being written.</param>
    /// <param name="proxyResponse">The backend's response, whose body has been copied in full.</param>
    /// <param name="cancellationToken">Cancelled when the call is abandoned or its deadline expires.</param>
    /// <returns>A task that completes when the trailers have been copied.</returns>
    public override async ValueTask TransformResponseTrailersAsync(
        HttpContext httpContext,
        HttpResponseMessage proxyResponse,
        CancellationToken cancellationToken)
    {
        await base.TransformResponseTrailersAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);

        // The forwarder asks for the trailers only after it has copied the backend's whole response
        // body, and checks the upload only after this: BackendForwarder then keeps the backend's answer
        // even when the upload did not complete.
        BackendCallState.Current?.MarkResponseRelayed();
    }

    // Called once the response head has been sent. Kestrel sends no body after the head of a response
    // to HEAD, of a 204 or 304, or of one that declares Content-Length: 0, so such a head is the whole
    // response. A declared non-zero length is reached by the forwarder's body writes, which a pass-through
    // body counts; the forwarder takes the caller's body stream only after this transform. A chunked or
    // unknown-length body ends with framing Kestrel writes after the forwarder returns, so its end is
    // left to the trailers step.
    private static void TrackRelayedResponse(HttpContext httpContext, BackendCallState callState)
    {
        var response = httpContext.Response;
        if (HttpMethods.IsHead(httpContext.Request.Method)
            || response.StatusCode is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified
            || response.ContentLength == 0)
        {
            callState.MarkResponseRelayed();
            return;
        }

        if (response.ContentLength is { } declaredLength
            && declaredLength > 0
            && httpContext.Features.Get<IHttpResponseBodyFeature>() is { } body)
        {
            httpContext.Features.Set<IHttpResponseBodyFeature>(new DeclaredLengthResponseBody(body, declaredLength, callState));
        }
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
    // handler may resend such a request on another connection, up to three more times, when the backend
    // closes the connection before answering, so the backend can receive it more than once. RFC 9110
    // defines GET, HEAD, OPTIONS and DELETE as idempotent, which means only that the intended effect of
    // repeating one is the same as sending it once (RFC 9110 9.2.2); each delivery still reaches the
    // backend and can change state or have side effects of its own. CONNECT is not defined as idempotent.
    // The methods are compared with HttpMethod equality (ordinal, case-insensitive), as the handler
    // compares them. A request the forwarder tunnels as an upgrade is left to its own upgrade handling.
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

    /// <summary>
    /// The caller's response body for one call whose response declares a non-zero <c>Content-Length</c>:
    /// every member passes to the server's own body feature unchanged, and the body stream counts the
    /// bytes written through it, so the call is recorded as relayed once the declared length has been
    /// written.
    /// </summary>
    /// <remarks>
    /// Bytes are neither altered, buffered nor reordered: each write and flush goes straight to the
    /// server's stream, which keeps enforcing the declared length and the server's synchronous-IO policy.
    /// A write is counted only once it has completed, so a write that fails or is cancelled adds nothing.
    /// Writes through <see cref="Writer"/> or <see cref="SendFileAsync"/>, which the forwarder does not
    /// use, go to the server's feature uncounted.
    /// </remarks>
    private sealed class DeclaredLengthResponseBody : IHttpResponseBodyFeature
    {
        private readonly IHttpResponseBodyFeature _inner;
        private readonly long _declaredLength;
        private readonly BackendCallState _callState;
        private long _written;

        /// <summary>Wraps the server's body feature for a call whose response declares a length.</summary>
        /// <param name="inner">The server's body feature of the call's response.</param>
        /// <param name="declaredLength">The response's declared <c>Content-Length</c>, greater than 0.</param>
        /// <param name="callState">The call recorded as relayed once that many bytes have been written.</param>
        public DeclaredLengthResponseBody(IHttpResponseBodyFeature inner, long declaredLength, BackendCallState callState)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(declaredLength);
            ArgumentNullException.ThrowIfNull(callState);

            _inner = inner;
            _declaredLength = declaredLength;
            _callState = callState;
            Stream = new CountingStream(inner.Stream, this);
        }

        public Stream Stream { get; }

        public PipeWriter Writer => _inner.Writer;

        public void DisableBuffering() => _inner.DisableBuffering();

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            _inner.SendFileAsync(path, offset, count, cancellationToken);

        public Task CompleteAsync() => _inner.CompleteAsync();

        private void Count(long bytes)
        {
            if (bytes > 0 && Interlocked.Add(ref _written, bytes) >= _declaredLength)
            {
                _callState.MarkResponseRelayed();
            }
        }

        /// <summary>Passes every member to the server's body stream and counts each completed write.</summary>
        private sealed class CountingStream(Stream inner, DeclaredLengthResponseBody owner) : Stream
        {
            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => inner.CanWrite;

            public override bool CanTimeout => inner.CanTimeout;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override int ReadTimeout
            {
                get => inner.ReadTimeout;
                set => inner.ReadTimeout = value;
            }

            public override int WriteTimeout
            {
                get => inner.WriteTimeout;
                set => inner.WriteTimeout = value;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                inner.Write(buffer, offset, count);
                owner.Count(count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                inner.Write(buffer);
                owner.Count(buffer.Length);
            }

            public override void WriteByte(byte value)
            {
                inner.WriteByte(value);
                owner.Count(1);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                owner.Count(count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                owner.Count(buffer.Length);
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
                TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);

            public override void EndWrite(IAsyncResult asyncResult) => TaskToAsyncResult.End(asyncResult);

            public override void Flush() => inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

            public override int Read(Span<byte> buffer) => inner.Read(buffer);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => inner.SetLength(value);

            public override ValueTask DisposeAsync() => inner.DisposeAsync();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
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
/// values, as Kestrel reads them, and each body, framed by <c>Content-Length</c> or chunked, is skipped
/// by count, never copied. Chunk extensions of any length are skipped as they arrive, never collected:
/// a chunk-size line ends at the first CRLF after its hex size, where Kestrel ends every chunk-size
/// line it accepts, and a CR or LF before that CRLF is skipped with the extensions, as Kestrel reads it
/// with its <c>EnableInsecureChunkedRequestParsing</c> switch set and rejects it otherwise. A
/// head's values become current when Kestrel consumes the head, which it does before it dispatches the
/// request; Kestrel dispatches the requests of one HTTP/1.1 connection one at a time, so the head
/// consumed last is that of the request in progress. At most 32 framed heads wait for Kestrel: a
/// pipelining caller's further heads are framed on later reads, as Kestrel catches up.
/// </para>
/// <para>
/// Framing stops only at framing Kestrel itself rejects: a head or trailer section larger than the
/// listener's Kestrel limits admit, a request line without a method or a target, a header line without
/// a field name, a <c>Content-Length</c> that is not a number or that a second one contradicts, a final
/// transfer coding other than chunked, a chunk size without hex digits or followed by anything but
/// <c>;</c> or CR, and chunk data not followed by CRLF. Kestrel dispatches no request after such
/// framing, and the heads framed before it still become current as Kestrel consumes them. A reader call
/// the capture cannot account for, or Kestrel consuming past the bytes the capture has framed, stops the
/// capture for the rest of the connection: <see cref="TryGetConnectionValues"/> then answers false, and
/// the request headers Kestrel parsed are all there is. Observation never throws into Kestrel and never
/// changes what Kestrel reads. The state belongs to one connection, so it only ever describes requests
/// of the caller that sent them.
/// </para>
/// <para>
/// Kestrel's reader calls are serialised by the pipe; the lookup comes from the request's own flow, so
/// every access to the shared state takes the instance's lock.
/// </para>
/// </remarks>
public sealed class RawConnectionHeaderCapture
{
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

    // Set at framing Kestrel rejects, and by a stop: no further byte is framed.
    private bool _framingStopped;

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

    // The size of the chunk-size line being read and whether it has a hex digit yet; both are reset
    // when the line ends.
    private long _chunkSize;
    private bool _chunkSizeHasDigits;

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

        // The hex digits of a chunk size, up to the ';' that starts its chunk extensions or a CR.
        ChunkSize,

        // Chunk extensions, skipped up to the next CR.
        ChunkExtension,

        // The byte after a CR in a chunk-size line: an LF ends the line.
        ChunkSizeLineFeed,
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

    // A read returned buffer: it is kept for Kestrel's advance, and the bytes past those already framed
    // are framed now, unless framing has stopped.
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
            if (_framingStopped)
            {
                return false;
            }

            var used = _framing switch
            {
                Framing.BeforeRequestLine => SkipLineBreaks(bytes),
                Framing.RequestLine or Framing.HeaderLine => TakeLine(bytes, _maxHeadBytes),
                Framing.ContentLengthBody or Framing.ChunkData => SkipCounted(bytes),
                Framing.ChunkSize => TakeChunkSize(bytes),
                Framing.ChunkExtension => SkipChunkExtension(bytes),
                Framing.ChunkSizeLineFeed => EndChunkSizeLine(bytes[0]),
                Framing.ChunkDataCarriageReturn => Expect(bytes[0], CarriageReturn, Framing.ChunkDataLineFeed),
                Framing.ChunkDataLineFeed => Expect(bytes[0], LineFeed, Framing.ChunkSize),
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

        return !_framingStopped;
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

    // The CR and the LF that end a chunk's data, which Kestrel requires.
    private int Expect(byte actual, byte expected, Framing next)
    {
        _observed++;
        if (actual == expected)
        {
            _framing = next;
        }
        else
        {
            StopFraming();
        }

        return 1;
    }

    // "<hex size>" at the start of a chunk-size line, up to the ';' that starts its chunk extensions or
    // the CR of its line end. Kestrel requires at least one hex digit followed directly by one of the
    // two, and a size that fits an int; a long holds every size it accepts.
    private int TakeChunkSize(ReadOnlySpan<byte> bytes)
    {
        var used = 0;
        while (used < bytes.Length)
        {
            var digit = HexValue(bytes[used]);
            if (digit < 0)
            {
                break;
            }

            if (_chunkSize > (long.MaxValue >> 4))
            {
                StopFraming();
                return bytes.Length;
            }

            _chunkSize = (_chunkSize << 4) | (long)digit;
            _chunkSizeHasDigits = true;
            used++;
        }

        _observed += used;
        if (used == bytes.Length)
        {
            return used;
        }

        var end = bytes[used];
        if (!_chunkSizeHasDigits || end is not ((byte)';' or CarriageReturn))
        {
            StopFraming();
            return bytes.Length;
        }

        _observed++;
        _framing = end == CarriageReturn ? Framing.ChunkSizeLineFeed : Framing.ChunkExtension;
        return used + 1;
    }

    // Chunk extensions are skipped up to the next CR, never collected, so an extension of any length,
    // spread over any number of reads, is followed without holding any of it.
    private int SkipChunkExtension(ReadOnlySpan<byte> bytes)
    {
        var carriageReturn = bytes.IndexOf(CarriageReturn);
        var used = carriageReturn < 0 ? bytes.Length : carriageReturn + 1;
        if (carriageReturn >= 0)
        {
            _framing = Framing.ChunkSizeLineFeed;
        }

        _observed += used;
        return used;
    }

    // The byte after a CR in a chunk-size line. An LF ends the line, the first CRLF after the size, as it
    // ends every chunk-size line Kestrel accepts: the last chunk (size 0) is followed by the trailer
    // section, any other chunk by its data. Another CR may be the one before the LF; any other byte is
    // skipped as chunk extension text, as Kestrel's insecure chunked parsing reads a lone CR.
    private int EndChunkSizeLine(byte next)
    {
        _observed++;
        if (next == LineFeed)
        {
            if (_chunkSize == 0)
            {
                _sectionLength = 0;
                _lineLength = 0;
                _framing = Framing.TrailerLine;
            }
            else
            {
                _bytesToSkip = _chunkSize;
                _framing = Framing.ChunkData;
            }

            _chunkSize = 0;
            _chunkSizeHasDigits = false;
        }
        else if (next != CarriageReturn)
        {
            _framing = Framing.ChunkExtension;
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
            StopFraming();
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
        if (!line.IsEmpty && line[^1] == CarriageReturn)
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
            case Framing.TrailerLine when line.IsEmpty:
                _framing = Framing.BeforeRequestLine;
                break;
            default:
                // A trailer field: only the empty line that ends the trailer section matters.
                break;
        }

        return used;
    }

    // "<method> SP <request-target> SP <version>": the method and the raw target are kept with the line,
    // bounded as Kestrel bounds them. The method ends at the first SP. The target starts right after it,
    // with a byte other than SP, and ends at the next SP; when no SP follows its first byte, Kestrel
    // takes that byte alone as the target and the byte after it as the SP, so "GET /xHTTP/1.1" is a
    // GET of "/". A line without a method or a target is one Kestrel rejects.
    private void OnRequestLine(ReadOnlySpan<byte> line)
    {
        var methodLength = line.IndexOf((byte)' ');
        var targetStart = methodLength + 1;
        if (methodLength <= 0 || targetStart >= line.Length || line[targetStart] == (byte)' ')
        {
            StopFraming();
            return;
        }

        var spaceAfterFirstByte = line[(targetStart + 1)..].IndexOf((byte)' ');
        var targetLength = spaceAfterFirstByte < 0 ? 1 : spaceAfterFirstByte + 1;

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
            StopFraming();
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
                StopFraming();
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
                StopFraming();
                return;
            }

            next = Framing.ChunkSize;
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

    // Framing Kestrel rejects: no byte from here on is framed, and Kestrel dispatches no request after
    // it. The heads framed before it stay and still become current as Kestrel consumes them.
    private void StopFraming()
    {
        _framingStopped = true;
        _connectionValues.Clear();
        _line = [];
        _requestLine = [];
    }

    // A reader call the capture cannot account for, or Kestrel consuming past the framed bytes: which
    // head is in progress is no longer known, so nothing is reported for the connection afterwards.
    private void Stop()
    {
        StopFraming();
        _stopped = true;
        _pending.Clear();
        _current = null;
        _readBuffer = default;
    }

    // Kestrel reads a Content-Length value as a long that must not be negative: through long.TryParse
    // with NumberStyles.Integer (an optional sign, surrounding white space and trailing NULs) when its
    // request header encoding selector gives Content-Length an encoding, and as an optionally signed
    // digit string otherwise. This parse accepts every value either reading accepts, as the same length.
    private static bool TryParseContentLength(ReadOnlySpan<byte> value, out long length) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out length) && length >= 0;

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
