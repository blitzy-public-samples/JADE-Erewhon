using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOTender</c> populated by <c>DTOTender.populateFromObject</c>: the tender's row id,
/// source GUID, offer, time placed, client and tender item, with the row id of the tender sale it won.
/// </summary>
/// <param name="Id">The row id of the tender.</param>
/// <param name="Guid">The source GUID text of the tender.</param>
/// <param name="Offer">The amount offered.</param>
/// <param name="Timestamp">The time the tender was placed, as stored.</param>
/// <param name="Client">The client who placed the tender.</param>
/// <param name="TenderItem">The tender item the tender is placed on.</param>
/// <param name="TenderSaleId">
/// The row id of the tender sale awarding the item to this tender; <see langword="null"/> unless the tender won the item.
/// </param>
public sealed record TenderProjection(
    long Id,
    string Guid,
    decimal Offer,
    DateTime Timestamp,
    ClientProjection Client,
    ItemProjection TenderItem,
    long? TenderSaleId)
{
    /// <summary>
    /// Builds the projection from a tender, copying each value as stored.
    /// Requires <see cref="Tender.Client"/> and <see cref="Tender.TenderItem"/> to be loaded, with everything
    /// <see cref="ItemProjection.From"/> reads from the tender item: its <see cref="Item.ItemCategory"/>, its
    /// <see cref="Item.Region"/> with the region's <see cref="Region.Country"/>, its <see cref="Item.Agent"/> and its
    /// <see cref="Item.Sale"/> with the sale's <see cref="Sale.Client"/>; <see cref="Tender.TenderSale"/> is read when loaded.
    /// <see cref="TenderSaleId"/> is the id of <see cref="Tender.TenderSale"/> when it is loaded; otherwise the id of the
    /// tender item's sale when that sale is a <see cref="TenderSale"/> whose <see cref="Domain.TenderSale.TenderId"/> is
    /// the tender's id; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="tender">The tender to project.</param>
    /// <returns>The projection of <paramref name="tender"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tender"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Tender.Client"/> or <see cref="Tender.TenderItem"/> is not loaded, or a navigation read by
    /// <see cref="ItemProjection.From"/> is not loaded.
    /// </exception>
    /// <exception cref="ArgumentException">The tender item's sale is neither a <see cref="RetailSale"/> nor a <see cref="TenderSale"/>.</exception>
    public static TenderProjection From(Tender tender)
    {
        ArgumentNullException.ThrowIfNull(tender);

        var client = tender.Client ?? throw new InvalidOperationException("Tender.Client must be loaded.");
        var tenderItem = tender.TenderItem ?? throw new InvalidOperationException("Tender.TenderItem must be loaded.");

        var tenderSaleId = tender.TenderSale?.Id
            ?? (tenderItem.Sale is TenderSale ts && ts.TenderId == tender.Id ? ts.Id : null);

        return new TenderProjection(
            tender.Id,
            tender.Guid,
            tender.Offer,
            tender.DateTendered,
            ClientProjection.From(client),
            ItemProjection.From(tenderItem),
            tenderSaleId);
    }
}
