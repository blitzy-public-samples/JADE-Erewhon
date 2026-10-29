using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

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
/// HTTP without authentication.
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
/// unchanged, 4xx and 5xx results included. Hop-by-hop headers are dropped by YARP in both
/// directions.
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
    /// Copies every end-to-end request header unchanged, replaces the caller's <c>Host</c> with the
    /// target authority and points the request at <paramref name="destinationPrefix"/> followed by
    /// the origin form of the caller's raw request target.
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

        // With no Host header set, the handler writes the destination URI's authority.
        proxyRequest.Headers.Host = null;

        // The origin form of an origin-form or absolute-form target is appended to the prefix. An
        // asterisk-form request is addressed to the target's base URL, and BackendForwarder writes the
        // '*' on the wire, because the HTTP stack cannot. For any other form, or a result that does
        // not parse, RequestUri stays null and YARP builds its default address from the prefix,
        // Request.Path and Request.QueryString.
        var rawTarget = GetRawTarget(httpContext);
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
    }

    /// <summary>
    /// Copies the backend's end-to-end response headers unchanged. The forwarder has already
    /// copied the status code and streams the body afterwards.
    /// </summary>
    /// <param name="httpContext">The incoming request, whose response is being written.</param>
    /// <param name="proxyResponse">The backend's response, or null when the backend did not respond.</param>
    /// <param name="cancellationToken">Cancelled when the call is abandoned or its deadline expires.</param>
    /// <returns>Whether the response is forwarded; false when there is no backend response.</returns>
    public override ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        // Overridden only to make the contract explicit: nothing is set, appended or removed here,
        // so the backend's Server, Date, Content-Type and Content-Length values are the ones the
        // caller sees, and failure responses are left to BackendForwarder.
        return base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
    }
}
