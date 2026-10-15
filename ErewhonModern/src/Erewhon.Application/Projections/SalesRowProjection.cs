using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Data of one <c>SalesListForm</c> row, as built by <c>buildSalesTable</c> and <c>Sale.makeRow</c>: the client,
/// the item's code, name and price, the sale date, the retail sale price or the winning tender offer, and the
/// agent commission. The client name with the sale price or offer is the data of <c>Sale.display</c>.
/// </summary>
/// <param name="SaleId">The row id of the sale.</param>
/// <param name="IsTender"><see langword="true"/> for a tender sale; <see langword="false"/> for a retail sale.</param>
/// <param name="ClientId">The row id of the client who bought the item.</param>
/// <param name="ClientName">The name of the client who bought the item.</param>
/// <param name="ItemId">The row id of the sold item.</param>
/// <param name="ItemCode">The item code, category prefix followed by code number; <see langword="null"/> when the item has no category.</param>
/// <param name="ItemName">The name of the sold item.</param>
/// <param name="ItemPrice">The price of the sold item; for a tender item, its reserve price.</param>
/// <param name="SaleDate">The sale date: the date sold of a retail sale, or the date the winning tender was placed.</param>
/// <param name="SalePrice">The retail sale price; <see langword="null"/> for a tender sale.</param>
/// <param name="Offer">The offer of the winning tender; <see langword="null"/> for a retail sale.</param>
/// <param name="AgentCommission">The commission earned by the item's agent on the sale.</param>
/// <param name="AgentId">The row id of the item's agent; <see langword="null"/> when the item has no agent.</param>
public sealed record SalesRowProjection(
    long SaleId,
    bool IsTender,
    long ClientId,
    string ClientName,
    long ItemId,
    string? ItemCode,
    string ItemName,
    decimal ItemPrice,
    DateOnly? SaleDate,
    decimal? SalePrice,
    decimal? Offer,
    decimal AgentCommission,
    long? AgentId)
{
    /// <summary>
    /// Builds the row of a retail or tender sale, copying each value as stored.
    /// Requires <see cref="Sale.Client"/>, <see cref="Sale.Item"/> with its <see cref="Item.ItemCategory"/> for the
    /// item code, and, for a tender sale, <see cref="TenderSale.Tender"/> to be loaded.
    /// </summary>
    /// <param name="sale">The sale to project.</param>
    /// <returns>The row of <paramref name="sale"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Sale.Client"/> or <see cref="Sale.Item"/> is not loaded, or <paramref name="sale"/> is a tender sale
    /// whose <see cref="TenderSale.Tender"/> is not loaded.
    /// </exception>
    public static SalesRowProjection From(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var client = sale.Client ?? throw new InvalidOperationException("Sale.Client must be loaded.");
        var item = sale.Item ?? throw new InvalidOperationException("Sale.Item must be loaded.");

        return new SalesRowProjection(
            sale.Id,
            sale is TenderSale,
            sale.ClientId,
            client.Name,
            sale.ItemId,
            item.GetCode(),
            item.Name,
            item.Price,
            sale.GetDate(),
            (sale as RetailSale)?.SalePrice,
            (sale as TenderSale)?.Tender?.Offer,
            sale.AgentCommission,
            item.AgentId);
    }
}

/// <summary>
/// Retail, tender and commission totals of the sales list, as accumulated by <c>buildSalesTable</c> and
/// <c>Sale.makeRow</c> over the rows it lists.
/// </summary>
/// <param name="RetailTotal">The sum of the sale prices of the retail rows.</param>
/// <param name="TenderTotal">The sum of the winning offers of the tender rows.</param>
/// <param name="CommissionTotal">The sum of the agent commissions of all rows.</param>
public sealed record SalesTotalsProjection(decimal RetailTotal, decimal TenderTotal, decimal CommissionTotal)
{
    /// <summary>
    /// Totals <paramref name="rows"/> in order: each retail row adds its sale price to <see cref="RetailTotal"/>, each
    /// tender row adds its offer to <see cref="TenderTotal"/>, and every row adds its agent commission to
    /// <see cref="CommissionTotal"/>. A missing sale price or offer adds 0. Each total is 0 when there are no rows.
    /// </summary>
    /// <param name="rows">The rows the sales list shows.</param>
    /// <returns>The totals of <paramref name="rows"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is <see langword="null"/>.</exception>
    public static SalesTotalsProjection Of(IEnumerable<SalesRowProjection> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var retailTotal = 0m;
        var tenderTotal = 0m;
        var commissionTotal = 0m;

        foreach (var row in rows)
        {
            if (row.IsTender)
            {
                tenderTotal += row.Offer ?? 0m;
            }
            else
            {
                retailTotal += row.SalePrice ?? 0m;
            }

            commissionTotal += row.AgentCommission;
        }

        return new SalesTotalsProjection(retailTotal, tenderTotal, commissionTotal);
    }
}
