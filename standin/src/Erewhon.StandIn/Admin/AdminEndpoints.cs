using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Hosting;
using Erewhon.StandIn.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Erewhon.StandIn.Admin;

/// <summary>
/// The stand-in's admin listener: readiness, state facts, state reset and shutdown, which the
/// harness uses before capture and replay and on every exit path.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint here, the catch-all included, is dispatched only for requests that arrive on the
/// admin socket (<see cref="ListenerKind.Admin"/>), whatever <c>Host</c> header they carry. The
/// listener has no authentication because nothing publishes it: Compose keeps it on the internal
/// network and the local-process runner binds it to loopback.
/// </para>
/// <para>
/// The class holds no state. <see cref="StandInState"/> serialises resets and request handlers
/// under its own locks, and every response is built from a fresh <see cref="StandInInfo"/>.
/// </para>
/// </remarks>
public static partial class AdminEndpoints
{
    private const string PlainTextContentType = "text/plain; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";

    // A plain Map catch-all: the framework's fallback mapping adds a nonfile constraint that skips
    // dotted paths such as /company.json.
    private const string CatchAllPattern = "/{**path}";

    private const string HealthyBody = "ok";

    private const string InvalidStateMessage =
        $"{AdminRoutes.ResetStateQueryKey} must be '{AdminRoutes.ResetStateSeed}' or '{AdminRoutes.ResetStateEmpty}'";

    /// <summary>
    /// Maps <see cref="AdminRoutes.Health"/>, <see cref="AdminRoutes.Info"/>,
    /// <see cref="AdminRoutes.Reset"/>, <see cref="AdminRoutes.Shutdown"/> and the admin
    /// catch-all, each restricted to <see cref="ListenerKind.Admin"/>.
    /// </summary>
    /// <param name="endpoints">The stand-in's endpoint route builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ILogger logger = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AdminEndpoints));

        // Readiness and liveness; independent of the state so it answers during a reset.
        endpoints.MapGet(
                AdminRoutes.Health,
                (HttpContext context) => WriteTextAsync(
                    context.Response, StatusCodes.Status200OK, HealthyBody, context.RequestAborted))
            .RequireListener(ListenerKind.Admin);

        endpoints.MapGet(
                AdminRoutes.Info,
                (HttpContext context, [FromServices] StandInState state) => WriteInfoAsync(
                    context.Response, state.GetInfo(), context.RequestAborted))
            .RequireListener(ListenerKind.Admin);

        endpoints.MapPost(
                AdminRoutes.Reset,
                (HttpContext context, [FromServices] StandInState state) => ResetAsync(context, state, logger))
            .RequireListener(ListenerKind.Admin);

        endpoints.MapPost(
                AdminRoutes.Shutdown,
                (HttpContext context, [FromServices] IHostApplicationLifetime lifetime) =>
                {
                    // Stopping only after the response completes lets the 202 reach the harness
                    // before Kestrel starts closing connections; app.Run() then returns and the
                    // process exits 0.
                    context.Response.OnCompleted(() =>
                    {
                        lifetime.StopApplication();
                        return Task.CompletedTask;
                    });

                    LogShutdownRequested(logger);
                    return WriteAsync(
                        context.Response, StatusCodes.Status202Accepted, contentType: null, ReadOnlyMemory<byte>.Empty, context.RequestAborted);
                })
            .RequireListener(ListenerKind.Admin);

        // Every other method and path on the admin socket, a wrong verb on a known path included.
        // The order keeps it below the specific endpoints whatever their route precedence.
        endpoints.Map(
                CatchAllPattern,
                (HttpContext context) => WriteAsync(
                    context.Response, StatusCodes.Status404NotFound, contentType: null, ReadOnlyMemory<byte>.Empty, context.RequestAborted))
            .WithOrder(int.MaxValue)
            .RequireListener(ListenerKind.Admin);
    }

    private static Task ResetAsync(HttpContext context, StandInState state, ILogger logger)
    {
        if (!TryGetResetKind(context.Request.Query[AdminRoutes.ResetStateQueryKey], out ResetKind kind))
        {
            return WriteTextAsync(context.Response, StatusCodes.Status400BadRequest, InvalidStateMessage, context.RequestAborted);
        }

        StandInInfo info;
        try
        {
            info = state.Reset(kind);
        }
        catch (Exception ex)
        {
            // StandInState swaps the state only after a successful build, so the previous state,
            // generation and mutation count still stand and the harness sees the failure as a
            // generation that did not advance.
            LogResetFailed(logger, ex, ResetKinds.ToQueryValue(kind));
            return WriteTextAsync(context.Response, StatusCodes.Status500InternalServerError, ex.Message, context.RequestAborted);
        }

        return WriteInfoAsync(context.Response, info, context.RequestAborted);
    }

    /// <summary>
    /// Resolves the <see cref="AdminRoutes.ResetStateQueryKey"/> query values: absent or a single
    /// empty value selects <see cref="ResetKind.Seed"/>; a single value must be exactly
    /// <see cref="AdminRoutes.ResetStateSeed"/> or <see cref="AdminRoutes.ResetStateEmpty"/>.
    /// </summary>
    private static bool TryGetResetKind(StringValues values, out ResetKind kind)
    {
        kind = ResetKind.Seed;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        // ResetKinds.TryParse ignores case; the admin contract is the exact AdminRoutes spelling,
        // so the parsed kind must also round-trip to the value as sent ('SEED' is rejected).
        return ResetKinds.TryParse(value, out kind)
            && string.Equals(ResetKinds.ToQueryValue(kind), value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the <see cref="AdminRoutes.Info"/> body: compact JSON whose members appear in the
    /// fixed order source, seedSha256, referenceDate, generation, mutations, framework, under the
    /// camelCase names the harness reads and copies into the baseline manifest.
    /// </summary>
    private static Task WriteInfoAsync(HttpResponse response, StandInInfo info, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("source", SourceLabels.StandIn);
            writer.WriteString("seedSha256", info.SeedSha256);
            writer.WriteString("referenceDate", info.ReferenceDate);
            writer.WriteNumber("generation", info.Generation);
            writer.WriteNumber("mutations", info.Mutations);
            writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
            writer.WriteEndObject();
        }

        return WriteAsync(response, StatusCodes.Status200OK, JsonContentType, buffer.WrittenMemory, cancellationToken);
    }

    private static Task WriteTextAsync(HttpResponse response, int statusCode, string text, CancellationToken cancellationToken) =>
        WriteAsync(response, statusCode, PlainTextContentType, Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>
    /// Sets the status, the exact <c>Content-Type</c> (none when <paramref name="contentType"/> is
    /// null) and <c>Content-Length</c>, then writes the body asynchronously; Kestrel disallows
    /// synchronous body writes.
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Shutdown requested on the admin listener; the host stops once the response completes")]
    private static partial void LogShutdownRequested(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Reset to state '{State}' failed; the previous state is kept")]
    private static partial void LogResetFailed(ILogger logger, Exception exception, string state);
}
