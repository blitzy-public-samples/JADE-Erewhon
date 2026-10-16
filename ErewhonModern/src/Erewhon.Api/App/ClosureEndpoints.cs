using Erewhon.Api.App.Contracts;
using Erewhon.Api.Closure;
using Erewhon.Api.Errors;
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
/// Maps the tender-closure endpoints, the back end of the close-tenders dialog: the dialog's initial state, closing
/// tenders at once at an entered date, and starting and stopping the repeating closure schedule of the caller's
/// session. The schedule's countdown and its scheduled closures run in the closure background service, which pushes
/// <c>ClosureTick</c> and <c>ClosureCompleted</c> to the session; these routes only change and read the session's
/// entry in <see cref="ClosureScheduleRegistry"/> and run the immediate closure.
/// </summary>
public static class ClosureEndpoints
{
    private const string Tag = "App: Admin closure";

    /// <summary>
    /// Maps these routes on the sub-group <c>/admin/tenders</c> of <paramref name="api"/>, each acting for the session
    /// bound by <see cref="ISessionContext"/>:
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET /admin/tenders/closure-schedule</c>: 200 with a <see cref="ClosureScheduleStateResponse"/> holding the
    /// database server date and, when the session has a running schedule, its date, minutes and seconds to go; the
    /// three are <see langword="null"/> and <see cref="ClosureScheduleStateResponse.Running"/> is
    /// <see langword="false"/> otherwise.
    /// </description></item>
    /// <item><description>
    /// <c>POST /admin/tenders/close-now</c> with a <see cref="CloseNowRequest"/>: stops the session's schedule, closes
    /// the bound company's tenders due at the request date through
    /// <see cref="TenderClosureService.CloseTendersAtDateAsync"/>, and answers 200 with a
    /// <see cref="CloseNowResponse"/> holding the number of tender items awarded.
    /// </description></item>
    /// <item><description>
    /// <c>POST /admin/tenders/closure-schedule/start</c> with a <see cref="StartClosureScheduleRequest"/>: replaces any
    /// schedule of the session with one at the request date and minutes, and answers 200 with a
    /// <see cref="StartClosureScheduleResponse"/>. When the minutes × <see cref="ClosureScheduleRegistry.SecondsPerMinute"/>
    /// are 0 or less, the session is left without a schedule, <see cref="StartClosureScheduleResponse.Started"/> is
    /// <see langword="false"/> and <see cref="StartClosureScheduleResponse.SecondsToGo"/> is 0.
    /// </description></item>
    /// <item><description>
    /// <c>POST /admin/tenders/closure-schedule/stop</c>: removes the session's schedule and answers 200 with a
    /// <see cref="StopClosureScheduleResponse"/> telling whether one was running.
    /// </description></item>
    /// </list>
    /// Every route answers 401 with an empty body when no session is bound and declares the 500
    /// <see cref="ErrorOutcome"/>, which reports, among others, the lock outcome of a closure row lock that was not
    /// obtained; the awards committed before it stay. The two routes that read a JSON body also declare the 400 with an
    /// empty body of a request whose body cannot be read. They also answer 400 with an empty body when the request has
    /// no <c>date</c> or its <c>date</c> is the null date 0001-01-01, before the session's schedule is stopped or
    /// replaced and before any tender is closed.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapClosureEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder tenders = api.MapGroup("/admin/tenders");

        tenders.MapGet("/closure-schedule", GetScheduleStateAsync)
            .WithName("AppAdminClosureGetSchedule")
            .WithTags(Tag)
            .WithSummary(
                "Returns the server date and the caller's running tender-closure schedule, if any, for the close-tenders dialog.")
            .Produces<ClosureScheduleStateResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        tenders.MapPost("/close-now", CloseNowAsync)
            .WithName("AppAdminClosureCloseNow")
            .WithTags(Tag)
            .WithSummary(
                "Stops the caller's closure schedule, closes the tenders due at the given date and returns the number of items awarded.")
            .Produces<CloseNowResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        tenders.MapPost("/closure-schedule/start", StartSchedule)
            .WithName("AppAdminClosureStartSchedule")
            .WithTags(Tag)
            .WithSummary(
                "Starts the caller's repeating tender-closure schedule at the given date and minutes per cycle, replacing any running one.")
            .Produces<StartClosureScheduleResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        tenders.MapPost("/closure-schedule/stop", StopSchedule)
            .WithName("AppAdminClosureStopSchedule")
            .WithTags(Tag)
            .WithSummary("Stops the caller's tender-closure schedule and returns whether one was running.")
            .Produces<StopClosureScheduleResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns 200 with the database server date and the session's running schedule, if any, or 401 when no session id
    /// is bound.
    /// </summary>
    private static async Task<Results<Ok<ClosureScheduleStateResponse>, UnauthorizedHttpResult>> GetScheduleStateAsync(
        [FromServices] ISessionContext session,
        [FromServices] ClosureScheduleRegistry closureRegistry,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        if (session.SessionId is not { Length: > 0 } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        ClosureScheduleStateResponse state = closureRegistry.TryGet(sessionId, out ClosureSchedule? schedule)
            ? new ClosureScheduleStateResponse(serverDate, true, schedule.Date, schedule.Minutes, schedule.SecondsToGo)
            : new ClosureScheduleStateResponse(serverDate, false, null, null, null);

        return TypedResults.Ok(state);
    }

    /// <summary>
    /// Stops the session's schedule, closes the bound company's tenders due at the request date and returns 200 with
    /// the number of tender items awarded; 401 when no session id is bound, then 400 when the request date is the null
    /// date, each before the schedule is stopped or any tender is closed.
    /// </summary>
    private static async Task<Results<Ok<CloseNowResponse>, UnauthorizedHttpResult, BadRequest>> CloseNowAsync(
        [FromBody] CloseNowRequest request,
        [FromServices] ISessionContext session,
        [FromServices] ClosureScheduleRegistry closureRegistry,
        [FromServices] TenderClosureService tenderClosureService,
        CancellationToken ct)
    {
        if (session.SessionId is not { Length: > 0 } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        if (!JadeValues.IsValid(request.Date))
        {
            return TypedResults.BadRequest();
        }

        closureRegistry.Stop(sessionId);

        int numClosed = await tenderClosureService.CloseTendersAtDateAsync(request.Date, ct);

        return TypedResults.Ok(new CloseNowResponse(numClosed));
    }

    /// <summary>
    /// Replaces the session's schedule with one at the request date and minutes and returns 200 with whether it started
    /// and its initial seconds to go; 401 when no session id is bound, then 400 when the request date is the null date,
    /// each before the session's schedule is replaced.
    /// </summary>
    private static Results<Ok<StartClosureScheduleResponse>, UnauthorizedHttpResult, BadRequest> StartSchedule(
        [FromBody] StartClosureScheduleRequest request,
        [FromServices] ISessionContext session,
        [FromServices] ClosureScheduleRegistry closureRegistry)
    {
        if (session.SessionId is not { Length: > 0 } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        if (!JadeValues.IsValid(request.Date))
        {
            return TypedResults.BadRequest();
        }

        bool started = closureRegistry.Start(sessionId, request.Date, request.Minutes);
        int secondsToGo = started ? request.Minutes * ClosureScheduleRegistry.SecondsPerMinute : 0;

        return TypedResults.Ok(new StartClosureScheduleResponse(started, secondsToGo));
    }

    /// <summary>
    /// Removes the session's schedule and returns 200 with whether one was running, or 401 when no session id is bound.
    /// </summary>
    private static Results<Ok<StopClosureScheduleResponse>, UnauthorizedHttpResult> StopSchedule(
        [FromServices] ISessionContext session,
        [FromServices] ClosureScheduleRegistry closureRegistry)
    {
        if (session.SessionId is not { Length: > 0 } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        bool stopped = closureRegistry.Stop(sessionId);

        return TypedResults.Ok(new StopClosureScheduleResponse(stopped));
    }
}
