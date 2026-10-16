using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Erewhon.Api.Sessions;

/// <summary>
/// Resolves the session of a request to the app endpoints or the SignalR hub against <see cref="SessionRegistry"/>,
/// stores the resolved <see cref="SessionEntry"/> in <see cref="HttpContext.Items"/> under
/// <see cref="HttpContextItemKey"/>, and answers 401 with an empty body to a request that needs a session and has none.
/// An app endpoint request holds a <see cref="SessionLease"/> on its session until the rest of the pipeline ends.
/// Requests outside <c>/api/app</c> and <c>/hubs/erewhon</c> pass through unchanged.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
/// <param name="registry">The registry of logged-on sessions.</param>
public sealed class SessionMiddleware(RequestDelegate next, SessionRegistry registry)
{
    /// <summary>The request header that carries the session id on <c>/api/app/**</c> requests.</summary>
    public const string HeaderName = "X-Erewhon-Session";

    /// <summary>The query-string key that carries the session id on <c>/hubs/erewhon/**</c> requests.</summary>
    public const string HubSessionQueryKey = "session";

    /// <summary>The <see cref="HttpContext.Items"/> key under which the resolved <see cref="SessionEntry"/> is stored.</summary>
    public const string HttpContextItemKey = "Erewhon.Session";

    private static readonly PathString AppPath = new("/api/app");

    private static readonly PathString HubPath = new("/hubs/erewhon");

    private static readonly (string Method, PathString Path)[] BootstrapEndpoints =
    [
        (HttpMethods.Get, new PathString("/api/app/logon-options")),
        (HttpMethods.Post, new PathString("/api/app/sessions")),
        (HttpMethods.Post, new PathString("/api/app/setup/load-data")),
    ];

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly SessionRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Returns the session that <see cref="InvokeAsync"/> resolved for the request.
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <returns>
    /// The <see cref="SessionEntry"/> stored under <see cref="HttpContextItemKey"/>, or <see langword="null"/> when the
    /// request carried no registered session or did not pass through <see cref="InvokeAsync"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static SessionEntry? GetSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(HttpContextItemKey, out var value) ? value as SessionEntry : null;
    }

    /// <summary>
    /// Applies the session gate to the request and then, unless it answers 401, runs the rest of the pipeline.
    /// <list type="bullet">
    /// <item><description>
    /// A path under <c>/api/app</c>, compared case-insensitively: a <see cref="SessionLease"/> is taken with
    /// <see cref="SessionRegistry.TryAcquire"/> on the first <see cref="HeaderName"/> header value. When it is taken,
    /// the leased <see cref="SessionLease.Entry"/> is stored under <see cref="HttpContextItemKey"/>, the rest of the
    /// pipeline runs, and the lease is disposed once the rest of the pipeline has completed or thrown.
    /// <c>GET /api/app/logon-options</c>, <c>POST /api/app/sessions</c> and <c>POST /api/app/setup/load-data</c>,
    /// matched on method and on the path case-insensitively with one trailing <c>/</c> allowed, continue whether or
    /// not a session was resolved. Every other request answers 401 with an empty body and no content type when no lease
    /// was taken, without running the rest of the pipeline.
    /// </description></item>
    /// <item><description>
    /// A path under <c>/hubs/erewhon</c>, compared case-insensitively: the first <see cref="HubSessionQueryKey"/>
    /// query-string value is looked up with <see cref="SessionRegistry.TryGet"/>, without a lease; a registered session
    /// is stored under <see cref="HttpContextItemKey"/> and the request continues, otherwise it answers 401 with an
    /// empty body without running the rest of the pipeline. The <see cref="HeaderName"/> header is not read.
    /// </description></item>
    /// <item><description>
    /// Any other path continues unchanged; nothing is read or stored.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <returns>A task that completes when the response has been produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;

        if (request.Path.StartsWithSegments(AppPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_registry.TryAcquire(FirstValue(request.Headers[HeaderName]), out var lease))
            {
                try
                {
                    context.Items[HttpContextItemKey] = lease.Entry;
                    await _next(context);
                }
                finally
                {
                    lease.Dispose();
                }

                return;
            }

            if (!IsBootstrapEndpoint(request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        else if (request.Path.StartsWithSegments(HubPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryStoreSession(context, request.Query[HubSessionQueryKey]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await _next(context);
    }

    private bool TryStoreSession(HttpContext context, StringValues values)
    {
        if (!_registry.TryGet(FirstValue(values), out var entry))
        {
            return false;
        }

        context.Items[HttpContextItemKey] = entry;
        return true;
    }

    private static string? FirstValue(StringValues values) => values.Count > 0 ? values[0] : null;

    private static bool IsBootstrapEndpoint(HttpRequest request)
    {
        var path = request.Path;
        var value = path.Value;
        if (value is { Length: > 1 } && value[^1] == '/')
        {
            path = new PathString(value[..^1]);
        }

        foreach (var (method, bootstrapPath) in BootstrapEndpoints)
        {
            if (HttpMethods.Equals(request.Method, method)
                && path.Equals(bootstrapPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
