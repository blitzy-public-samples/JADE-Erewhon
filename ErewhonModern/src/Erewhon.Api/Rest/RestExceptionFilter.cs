using Erewhon.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Erewhon.Api.Rest;

/// <summary>
/// Maps <see cref="NoCompanyException"/> and <see cref="StringTooLongException"/> raised by a REST controller action
/// to status 500 with an empty body. Every other exception is left unhandled.
/// </summary>
public sealed class RestExceptionFilter : IExceptionFilter
{
    /// <summary>
    /// Sets the result to status 500 with an empty body and marks the exception handled when the exception is a
    /// <see cref="NoCompanyException"/> or a <see cref="StringTooLongException"/>. Any other exception is left unhandled.
    /// </summary>
    /// <param name="context">The context of the action that raised the exception.</param>
    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Exception is NoCompanyException or StringTooLongException)
        {
            context.Result = EmptyResponse(StatusCodes.Status500InternalServerError);
            context.ExceptionHandled = true;
        }
    }

    /// <summary>
    /// Creates a result with the given status code and an empty body.
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <returns>A result that writes <paramref name="statusCode"/> with no body and no content type.</returns>
    public static ObjectResult EmptyResponse(int statusCode) => new(null) { StatusCode = statusCode };
}
