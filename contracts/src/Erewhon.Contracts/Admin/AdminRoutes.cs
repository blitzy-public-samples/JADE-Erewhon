namespace Erewhon.Contracts.Admin;

/// <summary>
/// The internal admin listener's port and paths, shared by the facade, the stand-in and the harness,
/// which take them from here and restate none of them.
/// </summary>
/// <remarks>
/// The admin listener is never published by Compose, and <c>ListenerPortMatcherPolicy</c> dispatches
/// admin endpoints only for requests that arrive on its own socket, whatever the <c>Host</c> header
/// says. Its endpoints are unauthenticated because that placement is what contains them. Hosts and base
/// URLs are configuration, never constants.
/// </remarks>
public static class AdminRoutes
{
    /// <summary>
    /// Admin listener port of the facade and the stand-in; distinct from the JADE ports 65000, 21001
    /// and 8081.
    /// </summary>
    public const int DefaultPort = 9090;

    /// <summary><c>GET</c> readiness and liveness probe.</summary>
    public const string Health = "/health";

    /// <summary>
    /// <c>GET</c> component information: for the stand-in its source label, seed SHA-256, reference
    /// date, reset generation and mutation count; for the facade its component name, framework
    /// description and effective routing table.
    /// </summary>
    public const string Info = "/admin/info";

    /// <summary>
    /// <c>POST</c> stand-in state reset, selected by the <see cref="ResetStateQueryKey"/> query value.
    /// </summary>
    public const string Reset = "/admin/reset";

    /// <summary><c>POST</c> graceful stop through <c>IHostApplicationLifetime.StopApplication</c>.</summary>
    public const string Shutdown = "/admin/shutdown";

    /// <summary>
    /// The facade's live OpenAPI document; equal to ASP.NET Core's default <c>MapOpenApi</c> route for
    /// document <c>v1</c>.
    /// </summary>
    public const string OpenApiDocument = "/openapi/v1.json";

    /// <summary>Query key on <see cref="Reset"/> that names the state to rebuild.</summary>
    public const string ResetStateQueryKey = "state";

    /// <summary>Reset state rebuilt from the seed files.</summary>
    public const string ResetStateSeed = "seed";

    /// <summary>Reset state with no company, used for the <c>getCompany</c> 500 case.</summary>
    public const string ResetStateEmpty = "empty";
}
