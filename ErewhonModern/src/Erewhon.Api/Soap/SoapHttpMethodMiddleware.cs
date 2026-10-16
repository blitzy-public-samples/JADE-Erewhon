using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Erewhon.Api.Soap;

/// <summary>
/// Answers 405 with an empty body and the header <c>Allow: GET, POST</c> to a request whose method is neither
/// <c>GET</c> nor <c>POST</c> and whose path is one of the SOAP service paths or lies below one, compared
/// case-insensitively segment by segment, without running the rest of the pipeline. Every other request passes
/// through unchanged.
/// </summary>
/// <param name="next">The next middleware in the pipeline.</param>
/// <param name="servicePaths">The paths of the SOAP services, such as <c>/soap/ErewhonWebService</c>.</param>
public sealed class SoapHttpMethodMiddleware(RequestDelegate next, IReadOnlyList<PathString> servicePaths)
{
    /// <summary>The value of the <c>Allow</c> header of a 405 response.</summary>
    public const string AllowedMethods = "GET, POST";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly PathString[] _servicePaths =
        (servicePaths ?? throw new ArgumentNullException(nameof(servicePaths))).ToArray();

    /// <summary>
    /// Answers 405 to a request to a SOAP service path whose method is neither <c>GET</c> nor <c>POST</c>; runs the
    /// rest of the pipeline for every other request.
    /// </summary>
    /// <param name="context">The HTTP context of the request.</param>
    /// <returns>A task that completes when the request has been answered or passed on.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string method = context.Request.Method;
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsPost(method)
            || !Array.Exists(
                _servicePaths,
                path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase)))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers[HeaderNames.Allow] = AllowedMethods;
        return Task.CompletedTask;
    }
}
