using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Common;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the soft edit lock endpoints, the back end of the edit dialogs' edit lock: the lock state of a row with the
/// prompt shown when it is already being edited, setting the lock on the first change, clearing it on close or save,
/// and sending the save request to the session holding the lock. Each lock is owned by the caller's session.
/// </summary>
public static class EditLockEndpoints
{
    private const string Tag = "App: Edit locks";

    /// <summary>
    /// Maps these routes on the sub-group <c>/edit-locks</c> of <paramref name="api"/>, all served by
    /// <see cref="SoftLockService"/>, where <c>type</c> is passed to the service as received and names
    /// <c>Agent</c>, <c>Client</c>, <c>Company</c>, <c>RetailItem</c>, <c>TenderItem</c>, <c>ItemCategory</c>,
    /// <c>CommissionRate</c>, <c>Country</c> or <c>Region</c>, compared case-insensitively, and <c>id</c> is the row id:
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET /edit-locks/{type}/{id}</c>: 200 with the row's <see cref="SoftLockStatus"/>, unchanged.
    /// <see cref="SoftLockStatus.IsLocked"/> is <see langword="true"/> whichever session holds the lock, the caller's
    /// own included, and the status carries the prompt and its title.
    /// </description></item>
    /// <item><description>
    /// <c>POST /edit-locks/{type}/{id}</c>: sets the row's lock to the caller's session and answers 200 with the
    /// service's <see cref="OperationResult"/>, unchanged.
    /// </description></item>
    /// <item><description>
    /// <c>DELETE /edit-locks/{type}/{id}</c>: clears the row's lock when the caller's session holds it, keeps a lock
    /// another session holds, and answers 200 with the service's <see cref="OperationResult"/>, unchanged.
    /// </description></item>
    /// <item><description>
    /// <c>POST /edit-locks/{type}/{id}/save-request</c>: sends the <c>SaveOutstandingChanges</c> event to the session
    /// holding the row's lock and answers 200 with a <see cref="SaveRequestResponse"/> whose
    /// <see cref="SaveRequestResponse.Sent"/> is <see langword="true"/>; when the row is unlocked nothing is sent and
    /// <see cref="SaveRequestResponse.Sent"/> is <see langword="false"/>.
    /// </description></item>
    /// </list>
    /// Every route answers 404 with an empty body when the service finds no row for <c>type</c> and <c>id</c>, an unknown
    /// <c>type</c> included. Every route also declares the 401 of a request without a registered session and the 500
    /// <see cref="ErrorOutcome"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapEditLockEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder editLocks = api.MapGroup("/edit-locks");

        editLocks.MapGet("/{type}/{id:long}", GetStatusAsync)
            .WithName("AppEditLockGetStatus")
            .WithTags(Tag)
            .WithSummary("Returns the soft edit lock state of a row and the prompt shown when it is already being edited.")
            .Produces<SoftLockStatus>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        editLocks.MapPost("/{type}/{id:long}", LockAsync)
            .WithName("AppEditLockLock")
            .WithTags(Tag)
            .WithSummary("Sets the soft edit lock of a row to the caller's session and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        editLocks.MapDelete("/{type}/{id:long}", UnlockAsync)
            .WithName("AppEditLockUnlock")
            .WithTags(Tag)
            .WithSummary("Clears the soft edit lock of a row when the caller's session holds it and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        editLocks.MapPost("/{type}/{id:long}/save-request", RequestSaveAsync)
            .WithName("AppEditLockRequestSave")
            .WithTags(Tag)
            .WithSummary("Sends the save request to the session holding the soft edit lock of a row.")
            .Produces<SaveRequestResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns 200 with the lock state of the row of <paramref name="type"/> with row id <paramref name="id"/>, or 404
    /// when the service finds no such row.
    /// </summary>
    private static async Task<Results<Ok<SoftLockStatus>, NotFound>> GetStatusAsync(
        [FromRoute] string type,
        [FromRoute] long id,
        [FromServices] SoftLockService softLocks,
        CancellationToken ct)
    {
        SoftLockStatus? status = await softLocks.GetStatusAsync(type, id, ct);
        return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
    }

    /// <summary>
    /// Sets the lock of the row of <paramref name="type"/> with row id <paramref name="id"/> to the caller's session and
    /// returns 200 with the operation result, or 404 when the service finds no such row.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> LockAsync(
        [FromRoute] string type,
        [FromRoute] long id,
        [FromServices] SoftLockService softLocks,
        CancellationToken ct)
    {
        OperationResult? result = await softLocks.LockAsync(type, id, ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    /// <summary>
    /// Clears the lock of the row of <paramref name="type"/> with row id <paramref name="id"/> when the caller's session
    /// holds it and returns 200 with the operation result, or 404 when the service finds no such row.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UnlockAsync(
        [FromRoute] string type,
        [FromRoute] long id,
        [FromServices] SoftLockService softLocks,
        CancellationToken ct)
    {
        OperationResult? result = await softLocks.UnlockAsync(type, id, ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    /// <summary>
    /// Sends the save request for the row of <paramref name="type"/> with row id <paramref name="id"/> to the session
    /// holding its lock and returns 200 with whether it was sent, or 404 when the service finds no such row.
    /// </summary>
    private static async Task<Results<Ok<SaveRequestResponse>, NotFound>> RequestSaveAsync(
        [FromRoute] string type,
        [FromRoute] long id,
        [FromServices] SoftLockService softLocks,
        CancellationToken ct)
    {
        bool? sent = await softLocks.RequestSaveAsync(type, id, ct);
        return sent is { } wasSent ? TypedResults.Ok(new SaveRequestResponse(wasSent)) : TypedResults.NotFound();
    }
}
