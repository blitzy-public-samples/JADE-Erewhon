using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Projections;
using Erewhon.Application.Search;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the <c>/api/app/admin</c> tender endpoints, the back end of the tenders list and the view-tender dialog: the
/// tender search, the details of one tender and the delete of a tender. Tender details carry no photo bytes.
/// </summary>
public static class AdminTenderEndpoints
{
    private const string Tag = "App: Admin tenders";

    /// <summary>
    /// Maps, under <c>/admin</c> on <paramref name="api"/>:
    /// <list type="bullet">
    /// <item><description><c>POST /admin/tenders/search</c>: the bound company's tenders matching an
    /// <see cref="AdminTenderSearchRequest"/>, as <see cref="TenderListRow"/> values ordered by the time placed and,
    /// within one time, by offer descending.</description></item>
    /// <item><description><c>GET /admin/tenders/{id}</c>: the tender with that row id as a
    /// <see cref="TenderDetailsResponse"/>; 404 when no tender has the id.</description></item>
    /// <item><description><c>DELETE /admin/tenders/{id}</c>: delete the tender with that row id and return the
    /// <see cref="OperationResult"/>; 404 when no tender has the id.</description></item>
    /// </list>
    /// The id routes accept only an integer id.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapAdminTenderEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var admin = api.MapGroup("/admin");

        admin.MapPost("/tenders/search", SearchTendersAsync)
            .WithName("AppAdminTenderSearch")
            .WithTags(Tag)
            .WithSummary(
                "Returns the tenders matching the tenders list filters, ordered by the time placed and then by offer descending.")
            .Produces<IReadOnlyList<TenderListRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/tenders/{id:long}", GetTenderAsync)
            .WithName("AppAdminTenderGet")
            .WithTags(Tag)
            .WithSummary("Returns one tender with its client and tender item, its status and the item's sale details.")
            .Produces<TenderDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/tenders/{id:long}", DeleteTenderAsync)
            .WithName("AppAdminTenderDelete")
            .WithTags(Tag)
            .WithSummary("Deletes a tender and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Runs a tender search with the request's agent, client and item filters and status flags, and returns the
    /// matching tenders as rows in search order, each status judged at one reading of the server date.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<TenderListRow>>> SearchTendersAsync(
        [FromBody] AdminTenderSearchRequest request,
        [FromServices] TenderSearch tenderSearch,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        var criteria = new TenderSearchCriteria
        {
            AgentId = request.AgentId,
            ClientId = request.ClientId,
            ItemId = request.ItemId,
            IncludeOpenTenders = request.IncludeOpenTenders,
            IncludeClosedTenders = request.IncludeClosedTenders,
            IncludeSuccessfulTenders = request.IncludeSuccessfulTenders,
        };

        IReadOnlyList<Tender> tenders = await tenderSearch.DoSearchAsync(criteria, ct);
        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        var rows = new List<TenderListRow>(tenders.Count);
        foreach (Tender tender in tenders)
        {
            rows.Add(ToRow(tender, serverDate));
        }

        return TypedResults.Ok<IReadOnlyList<TenderListRow>>(rows.AsReadOnly());
    }

    /// <summary>
    /// Returns the tender with row id <paramref name="id"/>: its projection without photo bytes, its status at the
    /// server date, whether it won its item, whether its item is sold and, for a sold item, the buyer's name and the
    /// winning offer; 404 when no tender has that id. The status and whether the tender won come from
    /// <see cref="TenderService.FindForStatusAsync"/>, the projection from
    /// <see cref="TenderService.GetTenderProjectionAsync"/>.
    /// </summary>
    private static async Task<Results<Ok<TenderDetailsResponse>, NotFound>> GetTenderAsync(
        [FromRoute] long id,
        [FromServices] TenderService tenderService,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        Tender? tender = await tenderService.FindForStatusAsync(id, ct);
        if (tender is null)
        {
            return TypedResults.NotFound();
        }

        TenderProjection? projection = await tenderService.GetTenderProjectionAsync(id, ct);
        if (projection is null)
        {
            return TypedResults.NotFound();
        }

        DateOnly serverDate = await clock.GetServerDateAsync(ct);
        var sale = projection.TenderItem.Sale;

        return TypedResults.Ok(
            new TenderDetailsResponse(
                projection,
                tender.GetStatus(serverDate),
                tender.TenderSale is not null,
                sale is not null,
                sale?.Client.Name,
                sale?.Offer));
    }

    /// <summary>
    /// Deletes the tender with row id <paramref name="id"/> in its own transaction and returns the operation result, or
    /// 404 when no tender has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteTenderAsync(
        [FromRoute] long id,
        [FromServices] TenderService tenderService,
        CancellationToken ct)
    {
        if (!await tenderService.ExistsAsync(id, ct))
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await tenderService.DeleteByIdAsync(id, ct: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Maps a tender read with its client, tender sale and tender item (with the item's category and agent) to its
    /// list row, judging its status at <paramref name="serverDate"/>.
    /// </summary>
    private static TenderListRow ToRow(Tender tender, DateOnly serverDate)
    {
        TenderItem tenderItem = tender.TenderItem
            ?? throw new InvalidOperationException("Tender.TenderItem must be loaded.");
        var client = tender.Client
            ?? throw new InvalidOperationException("Tender.Client must be loaded.");

        return new TenderListRow(
            tender.Id,
            tender.GetStatus(serverDate),
            tender.GetDate(),
            tender.DateTendered,
            tenderItem.ClosureDate,
            tender.ClientId,
            client.Name,
            tender.TenderItemId,
            tenderItem.GetCode(),
            tenderItem.Name,
            tenderItem.Price,
            tender.Offer,
            tenderItem.Agent?.Name,
            tender.TenderSale is not null);
    }
}
