using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Response of <c>GET /api/app/admin/sales</c>: the sales list of the Agent, Client or Company option, replacing the
/// <c>SalesListForm.tblSales</c> table and the <c>lblRetail</c>, <c>lblTender</c> and <c>lblCommission</c> total
/// labels that <c>SalesListForm.buildSalesTable</c> fills.
/// </summary>
/// <param name="Rows">
/// The listed sales, one per <c>tblSales</c> row in the order <c>buildSalesTable</c> adds them: client, item code and
/// name, item price, sale date, the retail sale price or winning tender offer of <c>Sale.makeRow</c>, and agent
/// commission. For a session that is not the company user, only sales of the session agent's own items.
/// </param>
/// <param name="Totals">
/// The retail, tender and commission totals over <paramref name="Rows"/>, replacing the <c>lblRetail</c>,
/// <c>lblTender</c> and <c>lblCommission</c> captions.
/// </param>
public sealed record SalesListResponse(IReadOnlyList<SalesRowProjection> Rows, SalesTotalsProjection Totals);
