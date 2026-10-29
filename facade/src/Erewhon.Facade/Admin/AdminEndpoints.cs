using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Hosting;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Microsoft.AspNetCore.Mvc;

namespace Erewhon.Facade.Admin;

/// <summary>
/// The facade's internal admin listener (<c>Facade:AdminPort</c>, 9090 by default): readiness, the live
/// OpenAPI document, the effective routing table and shutdown, which the harness uses on every run.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint here, the catch-all included, is admin-listener-only: it carries
/// <see cref="ListenerKind.Admin"/>, so <see cref="ListenerPortMatcherPolicy"/> dispatches it only for
/// requests that arrive on the admin socket, whatever <c>Host</c> header they carry. A request on the REST
/// or SOAP socket naming an admin path, <c>Host: facade:9090</c> included, reaches that listener's
/// catch-all and is forwarded to the backend, so the caller-visible surface gains no endpoint.
/// </para>
/// <para>
/// Identity and trust (AAP 0.4.1): the endpoints are unauthenticated and no handler reads a request
/// header. They are contained by network placement, because Compose never publishes the admin port and
/// the local-process runner binds it to loopback, and by listener isolation. Nothing here forwards to a
/// backend.
/// </para>
/// <para>The class holds no state; every response is built from the current services.</para>
/// </remarks>
public static partial class AdminEndpoints
{
    /// <summary>The <c>component</c> member of the <see cref="AdminRoutes.Info"/> body.</summary>
    internal const string ComponentName = "erewhon-facade";

    // A static class cannot be a type argument of ILogger<T>, so the category is spelt out.
    private const string LoggerCategory = "Erewhon.Facade.Admin.AdminEndpoints";

    private const string PlainTextContentType = "text/plain; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";

    // The same pattern as the REST and SOAP catch-alls; ListenerPortMatcherPolicy keeps only the one whose
    // listener owns the socket. MapFallback is not used: its nonfile constraint skips paths with a dot.
    private const string CatchAllPattern = "/{**path}";

    // The /admin/info member names. frameworkDescription is read by the harness (AdminInfo.Parse) and
    // reported as the facade's .NET version, so it must not be renamed.
    private const string ComponentMember = "component";
    private const string FrameworkDescriptionMember = "frameworkDescription";
    private const string WsdlMember = "wsdl";
    private const string BackendTimeoutSecondsMember = "backendTimeoutSeconds";
    private const string RoutingMember = "routing";
    private const string DefaultTargetMember = "defaultTarget";
    private const string TargetsMember = "targets";
    private const string RestBaseUrlMember = "restBaseUrl";
    private const string SoapBaseUrlMember = "soapBaseUrl";
    private const string TimeoutSecondsMember = "timeoutSeconds";
    private const string OperationsMember = "operations";
    private const string SurfaceMember = "surface";
    private const string OperationMember = "operation";
    private const string TargetMember = "target";

    // The health body, matching the stand-in's /health.
    private static readonly ReadOnlyMemory<byte> HealthyBody = "ok"u8.ToArray();

    /// <summary>
    /// Maps the admin endpoints, each admin-listener-only (AAP 0.4.1): <c>GET</c>
    /// <see cref="AdminRoutes.Health"/>, <c>GET</c> <see cref="AdminRoutes.OpenApiDocument"/>, <c>GET</c>
    /// <see cref="AdminRoutes.Info"/>, <c>POST</c> <see cref="AdminRoutes.Shutdown"/>, and a catch-all that
    /// answers every other method and path on the admin socket with an empty 404.
    /// </summary>
    /// <remarks>
    /// None of the endpoints is described in the OpenAPI document. The OpenAPI services and transformers
    /// are registered by <c>Program.cs</c> as document <c>v1</c>; this method only maps the document's route.
    /// <see cref="AdminRoutes.Reset"/> exists only on the stand-in, so on the facade it reaches the catch-all.
    /// </remarks>
    /// <param name="endpoints">The application's endpoint route builder.</param>
    /// <returns><paramref name="endpoints"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A service resolved at mapping time, such as <see cref="ILoggerFactory"/>, is not registered.</exception>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

        // Readiness and liveness: no dependency and no I/O, so it answers as soon as Kestrel listens. The
        // harness polls it for 200 before a run and until the connection is refused after shutdown.
        RequestDelegate health = static context => WriteAsync(
            context.Response, StatusCodes.Status200OK, PlainTextContentType, HealthyBody, context.RequestAborted);

        endpoints.MapGet(AdminRoutes.Health, health)
            .RequireListener(ListenerKind.Admin)
            .ExcludeFromDescription();

        // The pattern has no {documentName} placeholder, so the handler serves its default document name,
        // v1, the one Program.cs registers; it then reads documentName from the query string instead, and
        // any other name answers 404 because no other document exists. The document is generated from the
        // live endpoints and served unchanged; the harness writes and compares its bytes.
        endpoints.MapOpenApi(AdminRoutes.OpenApiDocument)
            .RequireListener(ListenerKind.Admin)
            .ExcludeFromDescription();

        // Everything but the WSDL status comes from the table in service, so a rejected reload reports
        // the table and Backend:TimeoutSeconds that requests are still routed with.
        endpoints.MapGet(
                AdminRoutes.Info,
                (HttpContext context, [FromServices] OperationRouter router, [FromServices] WsdlDocument wsdl) =>
                    WriteInfoAsync(context.Response, router.Describe(), wsdl.Status, context.RequestAborted))
            .RequireListener(ListenerKind.Admin)
            .ExcludeFromDescription();

        endpoints.MapPost(
                AdminRoutes.Shutdown,
                (HttpContext context, [FromServices] IHostApplicationLifetime lifetime) =>
                {
                    // Stopping only once the response has completed lets the 202 reach the caller before
                    // Kestrel closes its listeners; app.Run() then returns and the process exits.
                    context.Response.OnCompleted(() =>
                    {
                        lifetime.StopApplication();
                        return Task.CompletedTask;
                    });

                    LogShutdownRequested(logger);
                    return WriteAsync(
                        context.Response, StatusCodes.Status202Accepted, contentType: null, ReadOnlyMemory<byte>.Empty, context.RequestAborted);
                })
            .RequireListener(ListenerKind.Admin)
            .ExcludeFromDescription();

        // Every other method and path on the admin socket, a wrong verb on a known path included. It answers
        // itself and forwards nothing; the order keeps it below the specific endpoints.
        RequestDelegate catchAll = static context => WriteAsync(
            context.Response, StatusCodes.Status404NotFound, contentType: null, ReadOnlyMemory<byte>.Empty, context.RequestAborted);

        endpoints.Map(CatchAllPattern, catchAll)
            .WithOrder(int.MaxValue)
            .RequireListener(ListenerKind.Admin)
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// The form of a configured target base URL that <see cref="AdminRoutes.Info"/> reports, which never
    /// holds user information, a query or a fragment: the endpoint is unauthenticated and the facade
    /// carries no credential, so none may appear in its body.
    /// </summary>
    /// <param name="configured">The configured base URL, as the routing snapshot holds it.</param>
    /// <returns>
    /// Null for null. The configured string unchanged when
    /// <see cref="RoutingOptionsValidator.TryParseBaseUrl"/> accepts it, which is every URL start-up
    /// accepted. For an absolute <c>http</c> or <c>https</c> URL carrying user information, only its
    /// scheme, host, port and path. Null for anything else, because no part of it can be shown safely.
    /// </returns>
    internal static string? DisplayBaseUrl(string? configured)
    {
        if (configured is null || RoutingOptionsValidator.TryParseBaseUrl(configured, out _))
        {
            return configured;
        }

        // A host holding a compatibility form of '@' still holds the user name before it, so such a URL
        // is not shown at all.
        return Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.UserInfo.Length > 0
            && uri.Host.Length > 0
            && !RoutingOptionsValidator.HoldsAtSign(uri.Host)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : null;
    }

    /// <summary>
    /// Writes the <see cref="AdminRoutes.Info"/> body: compact JSON whose members appear in the fixed order
    /// component, frameworkDescription, wsdl, backendTimeoutSeconds, routing. <c>backendTimeoutSeconds</c>
    /// is the snapshot's <see cref="RoutingSnapshot.BackendTimeoutSeconds"/>, and <c>routing</c> projects
    /// <paramref name="routing"/> as <see cref="OperationRouter.Resolve"/> uses it: the default target, the
    /// targets keyed by name in the snapshot's ordinal order, and one row per catalogue operation in the
    /// snapshot's order, REST first, then SOAP. Each target's <c>restBaseUrl</c> and <c>soapBaseUrl</c>
    /// pass through <see cref="DisplayBaseUrl"/>, so the body never shows user information; a URL start-up
    /// accepted appears exactly as configured.
    /// </summary>
    private static Task WriteInfoAsync(
        HttpResponse response,
        RoutingSnapshot routing,
        string wsdlStatus,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString(ComponentMember, ComponentName);
            writer.WriteString(FrameworkDescriptionMember, RuntimeInformation.FrameworkDescription);
            writer.WriteString(WsdlMember, wsdlStatus);
            writer.WriteNumber(BackendTimeoutSecondsMember, routing.BackendTimeoutSeconds);

            writer.WriteStartObject(RoutingMember);
            writer.WriteString(DefaultTargetMember, routing.DefaultTarget);

            writer.WriteStartObject(TargetsMember);
            foreach (var target in routing.Targets)
            {
                writer.WriteStartObject(target.Name);
                writer.WriteString(RestBaseUrlMember, DisplayBaseUrl(target.RestBaseUrl));
                writer.WriteString(SoapBaseUrlMember, DisplayBaseUrl(target.SoapBaseUrl));
                writer.WriteNumber(TimeoutSecondsMember, target.TimeoutSeconds);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartArray(OperationsMember);
            foreach (var operation in routing.Operations)
            {
                writer.WriteStartObject();
                writer.WriteString(SurfaceMember, operation.Surface);
                writer.WriteString(OperationMember, operation.Operation);
                writer.WriteString(TargetMember, operation.Target);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return WriteAsync(response, StatusCodes.Status200OK, JsonContentType, buffer.WrittenMemory, cancellationToken);
    }

    /// <summary>
    /// Sets the status, the exact <c>Content-Type</c> (none when <paramref name="contentType"/> is null) and
    /// <c>Content-Length</c>, then writes the body asynchronously; Kestrel disallows synchronous body writes.
    /// </summary>
    private static Task WriteAsync(
        HttpResponse response,
        int statusCode,
        string? contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        if (contentType is not null)
        {
            response.ContentType = contentType;
        }

        response.ContentLength = body.Length;
        return body.IsEmpty ? Task.CompletedTask : response.Body.WriteAsync(body, cancellationToken).AsTask();
    }

    [LoggerMessage(
        EventId = 1301,
        EventName = "AdminShutdownRequested",
        Level = LogLevel.Information,
        Message = "Shutdown requested on the admin listener; the host stops once the response completes")]
    private static partial void LogShutdownRequested(ILogger logger);
}
