using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps <c>GET /api/app/admin/sales</c>, the back end of the sales list: the sales of one agent's items, of one client
/// or of the bound company, restricted to the session agent's own items for a session that is not the company user,
/// with the retail, tender and commission totals of the listed rows.
/// </summary>
public static class AdminSalesEndpoints
{
    private const string Tag = "App: Admin sales";

    /// <summary>
    /// Maps <c>GET /admin/sales?showFor={agent|client|company}&amp;id={id}</c> on <paramref name="api"/>, which answers:
    /// <list type="bullet">
    /// <item><description>
    /// 400 with an empty body when <c>showFor</c> is missing or is not exactly <c>agent</c>, <c>client</c> or
    /// <c>company</c>, compared case-insensitively.
    /// </description></item>
    /// <item><description>
    /// 200 with a <see cref="SalesListResponse"/> holding no rows and zero totals when <c>showFor</c> is <c>agent</c> or
    /// <c>client</c> and <c>id</c> is missing.
    /// </description></item>
    /// <item><description>
    /// 404 with an empty body when <c>showFor</c> is <c>agent</c> or <c>client</c> and no agent or client has the row
    /// id <c>id</c>.
    /// </description></item>
    /// <item><description>
    /// Otherwise 200 with a <see cref="SalesListResponse"/>: the <see cref="SalesRowProjection"/> rows of the agent's
    /// item sales, the client's sales, or, for <c>company</c>, which ignores <c>id</c>, every sale of the bound company
    /// (none when no company is bound), in the order <see cref="RetailSaleService.GetSalesRowsAsync"/> returns them.
    /// When the session user is not the company user, only rows whose item agent is the session agent are kept. The
    /// totals are those of <see cref="SalesTotalsProjection.Of"/> over the kept rows.
    /// </description></item>
    /// </list>
    /// The route also declares the 401 of a request without a registered session and the 500
    /// <see cref="ErrorOutcome"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapAdminSalesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder admin = api.MapGroup("/admin");

        admin.MapGet("/sales", GetSalesAsync)
            .WithName("AppAdminGetSales")
            .WithTags(Tag)
            .WithSummary("Lists the sales of an agent's items, of a client or of the company, with retail, tender and commission totals.")
            .Produces<SalesListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns the sales list of the <paramref name="showFor"/> option and the agent or client with row id
    /// <paramref name="id"/>, filtered to the session agent's own items unless the session user is the company user.
    /// </summary>
    private static async Task<Results<Ok<SalesListResponse>, BadRequest, NotFound>> GetSalesAsync(
        [FromQuery(Name = "showFor")] string? showFor,
        [FromQuery(Name = "id")] long? id,
        [FromServices] RetailSaleService retailSaleService,
        [FromServices] AgentService agentService,
        [FromServices] ClientService clientService,
        [FromServices] ISessionContext session,
        CancellationToken ct)
    {
        if (!TryParseShowFor(showFor, out SalesShowFor option))
        {
            return TypedResults.BadRequest();
        }

        if (option is SalesShowFor.Agent or SalesShowFor.Client)
        {
            if (id is not long entityId)
            {
                return TypedResults.Ok(new SalesListResponse([], SalesTotalsProjection.Of([])));
            }

            bool exists = option == SalesShowFor.Agent
                ? await agentService.ExistsAsync(entityId, ct)
                : await clientService.ExistsAsync(entityId, ct);

            if (!exists)
            {
                return TypedResults.NotFound();
            }
        }

        IReadOnlyList<SalesRowProjection> rows = await retailSaleService.GetSalesRowsAsync(option, id, ct);

        bool isCompanyUser = session.IsCompanyUser;
        long? sessionAgentId = session.AgentId;
        List<SalesRowProjection> visible = rows
            .Where(row => isCompanyUser || row.AgentId == sessionAgentId)
            .ToList();

        return TypedResults.Ok(new SalesListResponse(visible, SalesTotalsProjection.Of(visible)));
    }

    /// <summary>
    /// Parses <paramref name="value"/> as the name of a <see cref="SalesShowFor"/> member, compared case-insensitively;
    /// numeric text, surrounding white space and comma-separated lists are not names and are rejected.
    /// </summary>
    /// <param name="value">The <c>showFor</c> query value.</param>
    /// <param name="showFor">The parsed option, or <see cref="SalesShowFor.Agent"/> when parsing fails.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> names a member; otherwise <see langword="false"/>.</returns>
    private static bool TryParseShowFor(string? value, out SalesShowFor showFor)
    {
        if (!string.IsNullOrEmpty(value)
            && Enum.TryParse(value, ignoreCase: true, out SalesShowFor parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value, StringComparison.OrdinalIgnoreCase))
        {
            showFor = parsed;
            return true;
        }

        showFor = default;
        return false;
    }
}
