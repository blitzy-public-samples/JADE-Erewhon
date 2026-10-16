using System.Text.Json.Serialization;
using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOSaleDetail</c>, <c>DTORetailSaleDetail</c> and <c>DTOTenderSaleDetail</c> populated by
/// their <c>populateFromObject</c> methods: the sale's row id, source GUID, agent commission and client, with the date
/// sold and sale price of a retail sale or the offer and time of the winning tender of a tender sale.
/// </summary>
/// <param name="Id">The row id of the sale.</param>
/// <param name="Guid">The source GUID text of the sale; not written to JSON.</param>
/// <param name="IsTender"><see langword="true"/> for a tender sale; <see langword="false"/> for a retail sale.</param>
/// <param name="AgentCommission">The commission earned by the item's agent on the sale.</param>
/// <param name="Client">The client who bought the item.</param>
/// <param name="SoldDate">The time a retail sale was made; <see langword="null"/> for a tender sale.</param>
/// <param name="Price">The sale price of a retail sale; <see langword="null"/> for a tender sale.</param>
/// <param name="Offer">
/// The offer of the winning tender of a tender sale; <see langword="null"/> for a retail sale or when the tender sale
/// has no tender.
/// </param>
/// <param name="TenderDate">
/// The time the winning tender of a tender sale was placed; <see langword="null"/> for a retail sale or when the tender
/// sale has no tender.
/// </param>
public sealed record SaleDetailProjection(
    long Id,
    [property: JsonIgnore] string Guid,
    bool IsTender,
    decimal AgentCommission,
    ClientProjection Client,
    DateTime? SoldDate,
    decimal? Price,
    decimal? Offer,
    DateTime? TenderDate)
{
    /// <summary>
    /// Builds the projection from a retail or tender sale, copying each value as stored.
    /// Requires <see cref="Sale.Client"/> to be loaded and, for the offer and tender time of a tender sale,
    /// <see cref="TenderSale.Tender"/>.
    /// </summary>
    /// <param name="sale">The sale to project.</param>
    /// <returns>The projection of <paramref name="sale"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Sale.Client"/> is not loaded.</exception>
    /// <exception cref="ArgumentException"><paramref name="sale"/> is neither a <see cref="RetailSale"/> nor a <see cref="TenderSale"/>.</exception>
    public static SaleDetailProjection From(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var client = ClientProjection.From(
            sale.Client ?? throw new InvalidOperationException("Sale.Client must be loaded."));

        return sale switch
        {
            RetailSale r => new SaleDetailProjection(
                r.Id, r.Guid, false, r.AgentCommission, client, r.DateSold, r.SalePrice, null, null),
            TenderSale t => new SaleDetailProjection(
                t.Id, t.Guid, true, t.AgentCommission, client, null, null, t.Tender?.Offer, t.Tender?.DateTendered),
            _ => throw new ArgumentException("Unsupported sale type.", nameof(sale)),
        };
    }
}

/// <summary>
/// Read model of the JADE <c>DTOSale</c>, <c>DTORetailSale</c> and <c>DTOTenderSale</c> populated by their
/// <c>populateFromObject</c> methods: the sale's row id, source GUID, agent commission, client and item, with the
/// time and price of a retail sale or the offer and winning tender of a tender sale.
/// </summary>
/// <param name="Id">The row id of the sale.</param>
/// <param name="Guid">The source GUID text of the sale; not written to JSON.</param>
/// <param name="IsTender"><see langword="true"/> for a tender sale; <see langword="false"/> for a retail sale.</param>
/// <param name="AgentCommission">The commission earned by the item's agent on the sale.</param>
/// <param name="Client">The client who bought the item.</param>
/// <param name="Item">The sold item.</param>
/// <param name="Timestamp">The time a retail sale was made; <see langword="null"/> for a tender sale.</param>
/// <param name="SalePrice">The sale price of a retail sale; <see langword="null"/> for a tender sale.</param>
/// <param name="Offer">
/// The offer of the winning tender of a tender sale; <see langword="null"/> for a retail sale or when the tender sale
/// has no tender.
/// </param>
/// <param name="Tender">
/// The winning tender of a tender sale; <see langword="null"/> for a retail sale or when the tender sale has no tender.
/// </param>
public sealed record SaleProjection(
    long Id,
    [property: JsonIgnore] string Guid,
    bool IsTender,
    decimal AgentCommission,
    ClientProjection Client,
    ItemProjection Item,
    DateTime? Timestamp,
    decimal? SalePrice,
    decimal? Offer,
    TenderProjection? Tender)
{
    /// <summary>
    /// Builds the projection from a retail or tender sale, copying each value as stored.
    /// Requires <see cref="Sale.Client"/> and <see cref="Sale.Item"/> to be loaded, with everything
    /// <see cref="ItemProjection.From"/> reads from the item: its <see cref="Domain.Item.ItemCategory"/>, its
    /// <see cref="Domain.Item.Region"/> with the region's country, its <see cref="Domain.Item.Agent"/> and its
    /// <see cref="Domain.Item.Sale"/>.
    /// For a tender sale, also requires <see cref="TenderSale.Tender"/> with its <see cref="Domain.Tender.Client"/> and
    /// <see cref="Domain.Tender.TenderItem"/>.
    /// </summary>
    /// <param name="sale">The sale to project.</param>
    /// <returns>The projection of <paramref name="sale"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Sale.Client"/> or <see cref="Sale.Item"/> is not loaded, or a navigation read by
    /// <see cref="ItemProjection.From"/> or <see cref="TenderProjection.From"/> is not loaded.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="sale"/> is neither a <see cref="RetailSale"/> nor a <see cref="TenderSale"/>.</exception>
    public static SaleProjection From(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var client = ClientProjection.From(
            sale.Client ?? throw new InvalidOperationException("Sale.Client must be loaded."));
        var item = ItemProjection.From(
            sale.Item ?? throw new InvalidOperationException("Sale.Item must be loaded."));

        return sale switch
        {
            RetailSale r => new SaleProjection(
                r.Id, r.Guid, false, r.AgentCommission, client, item, r.DateSold, r.SalePrice, null, null),
            TenderSale t => new SaleProjection(
                t.Id,
                t.Guid,
                true,
                t.AgentCommission,
                client,
                item,
                null,
                null,
                t.Tender?.Offer,
                t.Tender is null ? null : TenderProjection.From(t.Tender)),
            _ => throw new ArgumentException("Unsupported sale type.", nameof(sale)),
        };
    }
}
