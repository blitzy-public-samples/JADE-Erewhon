using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the session endpoints, the back end of the shop and admin logon screens and of the admin File › Logout and
/// File › Exit menu items: the logon options of an area, the logon that opens a session for the selected user, and the
/// logout that ends the caller's session. The user is validated by <see cref="LogonService"/> and the session is held
/// by <see cref="SessionRegistry"/>.
/// </summary>
public static class SessionEndpoints
{
    private const string Tag = "App: Session";

    /// <summary>
    /// Maps these routes on <paramref name="api"/>:
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET /logon-options?area=shop|admin</c>, served without a session: 200 with the
    /// <see cref="LogonOptionsResponse"/> of the area, built from <see cref="LogonService.GetLogonOptionsAsync"/> and
    /// <see cref="AboutEndpoints.VersionString"/>. <c>area</c> must name <see cref="LogonArea.Shop"/> or
    /// <see cref="LogonArea.Admin"/> exactly, compared case-insensitively; a missing value, numeric text, a value with
    /// surrounding white space and any other text answer 400 with an empty body.
    /// </description></item>
    /// <item><description>
    /// <c>POST /sessions</c> with a <see cref="CreateSessionRequest"/> body, served without a session and whatever
    /// <see cref="SessionMiddleware.HeaderName"/> header the request carries: the selected user is validated by
    /// <see cref="LogonService.LogonAsync"/>, which also closes the due tenders after a successful admin-area logon.
    /// A valid user gets a new session from <see cref="SessionRegistry.Create"/> and 200 with a succeeded
    /// <see cref="CreateSessionResponse"/> carrying the session id, area, role, agent id, client id and user code. A
    /// rejected user gets no session and 200 with a failed <see cref="CreateSessionResponse"/> carrying role
    /// <see cref="SessionRole.None"/> and the message title and text. A body whose area is not a defined
    /// <see cref="LogonArea"/> answers 400 with an empty body, as does a missing or malformed body. An empty user
    /// selection raises <see cref="ErewhonException"/> <see cref="ErewhonException.UserNotProvided"/>, which ends the
    /// request with the 500 <see cref="ErrorOutcome"/> written by <see cref="ErrorOutcomeMiddleware"/>.
    /// </description></item>
    /// <item><description>
    /// <c>DELETE /sessions/current</c>, which needs a registered session: the caller's session is removed through
    /// <see cref="SessionRegistry.Remove"/>, which also discards its closure schedule, and the route answers 204. The
    /// session's cart is discarded when the last <see cref="SessionLease"/> on the session is disposed: the lease this
    /// request holds through <see cref="SessionMiddleware"/>, or the lease of a request of the session still running
    /// after it. A request without a registered session answers 401 with an empty body.
    /// </description></item>
    /// </list>
    /// Every route also declares the 500 <see cref="ErrorOutcome"/> of an exception that escapes it.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/logon-options", GetLogonOptionsAsync)
            .WithName("AppGetLogonOptions")
            .WithTags(Tag)
            .WithSummary(
                "Returns what the logon screen of the shop or admin area offers: whether data is loaded, the user "
                + "option caption, the Administrator option state, the users to choose from and the version.")
            .Produces<LogonOptionsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        api.MapPost("/sessions", CreateSessionAsync)
            .WithName("AppCreateSession")
            .WithTags(Tag)
            .WithSummary(
                "Validates the user selected on the logon screen of an area and, for a valid user, opens a session and "
                + "returns its id; a rejected user gets the message to show and no session.")
            .Produces<CreateSessionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        api.MapDelete("/sessions/current", DeleteCurrentSession)
            .WithName("AppDeleteCurrentSession")
            .WithTags(Tag)
            .WithSummary("Ends the caller's session and discards its cart and closure schedule.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns 200 with the logon options of the area named by <paramref name="area"/>, or 400 when
    /// <paramref name="area"/> does not name a logon area.
    /// </summary>
    /// <param name="area">The <c>area</c> query value: <c>shop</c> or <c>admin</c>, in any letter case.</param>
    /// <param name="logonService">The logon service of the request scope.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The 200 or 400 result.</returns>
    private static async Task<Results<Ok<LogonOptionsResponse>, BadRequest>> GetLogonOptionsAsync(
        [FromQuery] string? area,
        [FromServices] LogonService logonService,
        CancellationToken ct)
    {
        if (!TryParseArea(area, out LogonArea logonArea))
        {
            return TypedResults.BadRequest();
        }

        LogonOptions options = await logonService.GetLogonOptionsAsync(logonArea, ct);

        return TypedResults.Ok(new LogonOptionsResponse(
            DataLoaded: options.DataLoaded,
            Area: logonArea,
            UserOptionCaption: options.UserOptionCaption,
            AdministratorEnabled: options.AdministratorEnabled,
            AdministratorSelected: options.AdministratorSelected,
            Users: options.Users,
            Version: AboutEndpoints.VersionString));
    }

    /// <summary>
    /// Validates the selected user of <paramref name="request"/> and returns 200 with the outcome: a new session for a
    /// valid user, or the message for a rejected one. Returns 400 when the request's area is not a defined
    /// <see cref="LogonArea"/>.
    /// </summary>
    /// <param name="request">The area, the Administrator option and the selected user name, as entered.</param>
    /// <param name="logonService">The logon service of the request scope.</param>
    /// <param name="sessionRegistry">The registry the new session is stored in.</param>
    /// <param name="ct">Cancels the reads and the admin-area tender closure.</param>
    /// <returns>The 200 or 400 result.</returns>
    /// <exception cref="InvalidOperationException">A successful logon result carries no user code.</exception>
    private static async Task<Results<Ok<CreateSessionResponse>, BadRequest>> CreateSessionAsync(
        [FromBody] CreateSessionRequest request,
        [FromServices] LogonService logonService,
        [FromServices] SessionRegistry sessionRegistry,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Area))
        {
            return TypedResults.BadRequest();
        }

        LogonResult result = await logonService.LogonAsync(
            new LogonRequest(request.Area, request.Administrator, request.UserName),
            ct);

        if (!result.Succeeded)
        {
            return TypedResults.Ok(new CreateSessionResponse(
                Succeeded: false,
                SessionId: null,
                Area: request.Area,
                Role: SessionRole.None,
                AgentId: null,
                ClientId: null,
                UserName: null,
                MessageTitle: result.ErrorTitle,
                Message: result.ErrorText));
        }

        string userCode = result.UserCode
            ?? throw new InvalidOperationException("A successful logon result carries no user code.");

        SessionEntry entry = sessionRegistry.Create(request.Area, result.Role, result.AgentId, result.ClientId, userCode);

        return TypedResults.Ok(new CreateSessionResponse(
            Succeeded: true,
            SessionId: entry.SessionId,
            Area: entry.Area,
            Role: entry.Role,
            AgentId: entry.AgentId,
            ClientId: entry.ClientId,
            UserName: entry.UserName,
            MessageTitle: null,
            Message: null));
    }

    /// <summary>
    /// Removes the session the request was resolved to and returns 204, or returns 401 when the request carries no
    /// registered session.
    /// </summary>
    /// <param name="httpContext">The HTTP context holding the session resolved by <see cref="SessionMiddleware"/>.</param>
    /// <param name="sessionRegistry">The registry the session is removed from.</param>
    /// <returns>The 204 or 401 result.</returns>
    private static Results<NoContent, UnauthorizedHttpResult> DeleteCurrentSession(
        HttpContext httpContext,
        [FromServices] SessionRegistry sessionRegistry)
    {
        SessionEntry? session = SessionMiddleware.GetSession(httpContext);
        if (session is null)
        {
            return TypedResults.Unauthorized();
        }

        sessionRegistry.Remove(session.SessionId);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Parses <paramref name="value"/> as the exact name of a <see cref="LogonArea"/>, compared case-insensitively.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="area">The parsed area when the text names one; otherwise <see cref="LogonArea.Shop"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> is the name of a defined <see cref="LogonArea"/>;
    /// <see langword="false"/> for null, empty, numeric, padded or any other text.
    /// </returns>
    private static bool TryParseArea(string? value, out LogonArea area)
    {
        if (!string.IsNullOrEmpty(value)
            && Enum.TryParse(value, ignoreCase: true, out LogonArea parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.OrdinalIgnoreCase))
        {
            area = parsed;
            return true;
        }

        area = LogonArea.Shop;
        return false;
    }
}
