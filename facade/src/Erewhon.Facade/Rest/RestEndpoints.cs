using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Contracts.Matching;
using Erewhon.Facade.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Erewhon.Facade.Rest;

/// <summary>
/// Maps the facade's REST surface, the address of the JADE <c>ErewhonREST</c> application
/// (Erewhon/ErehwhonExternalInterfacesSchema.ddx:82): one endpoint per catalogue operation of
/// <c>ErewhonRestService</c> and one catch-all for everything else on the REST listener.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint only hands the request to <see cref="BackendForwarder"/>. None of them builds a
/// response, reads the body or looks at a header, so the backend's answer reaches the caller
/// unchanged, and the facade itself never answers 400, 404, 405 or 415 for a REST request.
/// </para>
/// <para>
/// Every endpoint carries <see cref="ListenerKind.Rest"/>, because <see cref="ListenerPortMatcherPolicy"/>
/// dispatches by the socket a request arrived on and drops endpoints without listener metadata.
/// </para>
/// <para>
/// Which operation a request is, and therefore which target serves it, is decided only by the shared
/// <see cref="RestOperationMatcher"/> over the verb and the raw path, exactly as the stand-in and the
/// harness decide it, whichever endpoint ASP.NET Core routing selected. Routing matches templates
/// decoded, ignoring case and tolerating a trailing slash, so <c>/COMPANY</c>, <c>/%63ompany</c> and
/// <c>/company/</c> select the <c>getCompany</c> endpoint but are not that operation for the matcher;
/// they go to <c>Routing:DefaultTarget</c> like <c>/COMPANY.json</c>.
/// </para>
/// </remarks>
public static class RestEndpoints
{
    /// <summary>
    /// The REST listener's catch-all route. It is mapped with <c>Map</c> rather than <c>MapFallback</c>,
    /// whose <c>nonfile</c> constraint skips paths containing a dot such as <c>/company.json</c>.
    /// </summary>
    internal const string CatchAllPattern = "/{**path}";

    /// <summary>
    /// Maps the REST surface with the <see cref="OperationCatalogue"/> registered in the application's
    /// services.
    /// </summary>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns><paramref name="endpoints"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="OperationCatalogue"/> is registered, or a catalogue REST operation has no REST binding.
    /// </exception>
    public static IEndpointRouteBuilder MapRestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapRestEndpoints(endpoints.ServiceProvider.GetRequiredService<OperationCatalogue>());
    }

    /// <summary>
    /// Maps one endpoint per REST operation of <paramref name="catalogue"/>, in catalogue order, at its
    /// verb and path template, and the REST catch-all, all on the REST listener.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each operation endpoint is named after its JADE method, which becomes its OpenAPI
    /// <c>operationId</c>. Its route parameters stay unconstrained and unbound, so a segment the backend
    /// would reject, such as <c>/itemByCode/DEST/abc</c>, is still forwarded for the backend to answer.
    /// </para>
    /// <para>
    /// The catch-all accepts every method and has the highest order, so the operation endpoints always
    /// win on the REST socket. It receives the relay form <c>/&lt;alias&gt;/jadehttp.dll/&lt;path&gt;</c>,
    /// unknown paths, wrong verbs, <c>HEAD</c> and <c>OPTIONS</c>, and suffixed forms such as
    /// <c>/company.json</c>.
    /// </para>
    /// <para>
    /// Every endpoint, operation and catch-all alike, resolves the operation through
    /// <see cref="RestOperationMatcher"/> from the verb and the raw path. A match, a suffixed form
    /// included, goes to that operation's target; anything else goes to <c>Routing:DefaultTarget</c>,
    /// also when routing selected an operation endpoint for a case variant, an escaped literal, a dot
    /// segment or a trailing slash of its template.
    /// </para>
    /// <para>
    /// The services <see cref="BackendForwarder"/> and <see cref="RestOperationMatcher"/> are resolved per
    /// request and must be registered by the application.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <param name="catalogue">The catalogue whose REST operations are mapped.</param>
    /// <returns><paramref name="endpoints"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A catalogue REST operation has no REST binding.</exception>
    public static IEndpointRouteBuilder MapRestEndpoints(this IEndpointRouteBuilder endpoints, OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalogue);

        foreach (var operation in catalogue.GetBySurface(Surface.Rest))
        {
            var rest = operation.Rest
                ?? throw new InvalidOperationException($"Catalogue REST operation {operation.Id} has no REST binding.");

            // The endpoint supplies the route, name and listener that OpenAPI describes; the operation
            // forwarded is the matcher's, as on the catch-all. [FromServices] keeps the request delegate
            // factory from ever inferring a service as a JSON body on the POST operations; the body
            // stream must stay unread for the forwarder.
            endpoints
                .MapMethods(
                    rest.PathTemplate,
                    [rest.Verb],
                    ([FromServices] RestOperationMatcher matcher, [FromServices] BackendForwarder forwarder, HttpContext context) =>
                        ForwardMatchedAsync(context, matcher, forwarder))
                .WithName(operation.JadeMethod)
                .RequireListener(ListenerKind.Rest);
        }

        // The catch-all does not bind the decoded "path" route value; matching uses the raw target.
        endpoints
            .Map(
                CatchAllPattern,
                ([FromServices] RestOperationMatcher matcher, [FromServices] BackendForwarder forwarder, HttpContext context) =>
                    ForwardMatchedAsync(context, matcher, forwarder))
            .WithOrder(int.MaxValue)
            .ExcludeFromDescription()
            .RequireListener(ListenerKind.Rest);

        return endpoints;
    }

    /// <summary>
    /// Returns the request path exactly as the caller sent it, still percent-encoded and without the
    /// query, or null when the request target has no path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path comes from the origin form of the raw request target
    /// (<see cref="PassThroughTransformer.TryGetOriginForm"/>), the same target the forwarder sends: an
    /// origin-form target as sent, so <c>/agent/Hank%20Williams</c> is never decoded or re-encoded, or
    /// the path after the authority of an absolute-form target, the empty path read as <c>/</c>. An
    /// asterisk-form or any other target has no path and is no catalogue operation.
    /// </para>
    /// <para>
    /// When the server exposes no raw request target, the escaped <c>PathBase + Path</c> is returned
    /// instead. That reconstructed path is matched like a raw one, so <c>/company</c> is still
    /// <c>getCompany</c>; only a path that matches no operation goes to <c>Routing:DefaultTarget</c>.
    /// </para>
    /// </remarks>
    /// <param name="context">The incoming request.</param>
    /// <returns>The raw path, starting with <c>/</c>; null for a target without a path.</returns>
    internal static string? GetRawPath(HttpContext context)
    {
        var rawTarget = PassThroughTransformer.GetRawTarget(context);
        if (string.IsNullOrEmpty(rawTarget))
        {
            return (context.Request.PathBase + context.Request.Path).ToUriComponent();
        }

        if (!PassThroughTransformer.TryGetOriginForm(rawTarget, out var originForm))
        {
            return null;
        }

        var queryStart = originForm.IndexOf('?', StringComparison.Ordinal);
        return queryStart < 0 ? originForm : originForm[..queryStart];
    }

    private static Task ForwardMatchedAsync(HttpContext context, RestOperationMatcher matcher, BackendForwarder forwarder)
    {
        var rawPath = GetRawPath(context);
        var operation = rawPath is not null && matcher.TryMatch(context.Request.Method, rawPath, out var match)
            ? match.Operation
            : null;
        return forwarder.ForwardAsync(context, Surface.Rest, operation);
    }
}
