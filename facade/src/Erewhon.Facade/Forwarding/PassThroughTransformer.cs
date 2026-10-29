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
    // The raw request target is re-parsed without canonicalisation, so escapes such as %2F, %20
    // and '+' and bare query keys such as "?WebServiceHTTP&...&wsdl" reach the backend exactly as
    // the caller sent them. Request.Path and Request.QueryString are decoded forms and cannot be
    // used to rebuild the target without changing it.
    private static readonly UriCreationOptions RawTargetOptions = new()
    {
        DangerousDisablePathAndQueryCanonicalization = true,
    };

    /// <summary>
    /// Copies every end-to-end request header unchanged, replaces the caller's <c>Host</c> with the
    /// target authority and points the request at <paramref name="destinationPrefix"/> followed by
    /// the caller's raw request target.
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

        // Only an origin-form target ("/path?query") is appended. For asterisk-form, absolute-form
        // or an unparsable result, RequestUri stays null and YARP builds the default address from
        // the prefix, Request.Path and Request.QueryString.
        var rawTarget = httpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget)
            && rawTarget[0] == '/'
            && Uri.TryCreate(destinationPrefix.TrimEnd('/') + rawTarget, in RawTargetOptions, out var uri))
        {
            proxyRequest.RequestUri = uri;
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
