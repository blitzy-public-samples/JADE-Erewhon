using System.Globalization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps <c>GET /api/app/about</c>, the back end of the About dialog, and holds the application version numbers.
/// </summary>
public static class AboutEndpoints
{
    /// <summary>The major version number of the application.</summary>
    public const int MajorVersionNumber = 2;

    /// <summary>The minor version number of the application.</summary>
    public const int MinorVersionNumber = 0;

    /// <summary>
    /// The application version string: <see cref="MajorVersionNumber"/> and <see cref="MinorVersionNumber"/> joined by a
    /// full stop ("2.0").
    /// </summary>
    public static string VersionString { get; } =
        MajorVersionNumber.ToString(CultureInfo.InvariantCulture)
        + "."
        + MinorVersionNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps <c>GET /about</c> on <paramref name="api"/>, which returns 200 with an <see cref="AboutResponse"/> carrying
    /// <see cref="VersionString"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    public static RouteGroupBuilder MapAboutEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/about", () => TypedResults.Ok(new AboutResponse(VersionString)))
            .WithName("AppGetAbout")
            .WithTags("App: About")
            .WithSummary("Returns the application version shown by the About dialog.")
            .Produces<AboutResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }
}
