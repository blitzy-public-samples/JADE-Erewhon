using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOItem</c>, <c>DTORetailItem</c> and <c>DTOTenderItem</c> populated by their
/// <c>populateFromObject</c> methods: the item's row id, source GUID, code, name, description, category, listed date,
/// photo, price, region and country, agent and sale details, with the closure date and tender presence of a tender item.
/// </summary>
/// <param name="Id">The row id of the item.</param>
/// <param name="Guid">The source GUID text of the item.</param>
/// <param name="IsTender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
/// <param name="CodeNumber">The code number of the item.</param>
/// <param name="CategoryPrefix">The prefix of the item's category; <see langword="null"/> when the item has no category.</param>
/// <param name="Code">
/// The item code, category prefix followed by code number; <see langword="null"/> when the item has no category.
/// </param>
/// <param name="Name">The item name.</param>
/// <param name="Description">The item description.</param>
/// <param name="ItemCategoryId">The row id of the item's category.</param>
/// <param name="ItemCategoryName">The name of the item's category; <see langword="null"/> when the item has no category.</param>
/// <param name="ListedDate">The date the item was listed.</param>
/// <param name="Photo">The picture of the item, the stored array itself; <see langword="null"/> when the item has none.</param>
/// <param name="Price">The price of the item; for a tender item, its reserve price.</param>
/// <param name="RegionId">The row id of the region the item is listed in; <see langword="null"/> when it has no region.</param>
/// <param name="RegionName">The name of the region the item is listed in; <see langword="null"/> when it has no region.</param>
/// <param name="CountryId">
/// The row id of the country of the item's region; <see langword="null"/> when the item has no region.
/// </param>
/// <param name="CountryName">
/// The name of the country of the item's region; <see langword="null"/> when the item has no region or the region has no
/// country.
/// </param>
/// <param name="AgentId">The row id of the agent selling the item; <see langword="null"/> when it has no agent.</param>
/// <param name="Agent">The agent selling the item; <see langword="null"/> when it has no agent.</param>
/// <param name="ClosureDate">The date tendering closes on a tender item; <see langword="null"/> for a retail item.</param>
/// <param name="IsSold"><see langword="true"/> when the item has a sale; otherwise <see langword="false"/>.</param>
/// <param name="Sale">The details of the item's sale; <see langword="null"/> while the item is unsold.</param>
/// <param name="HasTenders">
/// <see langword="true"/> for a tender item with at least one tender; otherwise <see langword="false"/>.
/// </param>
public sealed record ItemProjection(
    long Id,
    string Guid,
    bool IsTender,
    int CodeNumber,
    string? CategoryPrefix,
    string? Code,
    string Name,
    string Description,
    long ItemCategoryId,
    string? ItemCategoryName,
    DateOnly ListedDate,
    byte[]? Photo,
    decimal Price,
    long? RegionId,
    string? RegionName,
    long? CountryId,
    string? CountryName,
    long? AgentId,
    AgentProjection? Agent,
    DateOnly? ClosureDate,
    bool IsSold,
    SaleDetailProjection? Sale,
    bool HasTenders)
{
    /// <summary>
    /// Builds the projection from a retail or tender item, copying each value as stored.
    /// Reads <see cref="Item.ItemCategory"/>, <see cref="Item.Region"/> with its <see cref="Region.Country"/>,
    /// <see cref="Item.Agent"/>, <see cref="Item.Sale"/> with the sale's <see cref="Domain.Sale.Client"/> and, for a
    /// tender item, <see cref="TenderItem.Tenders"/>; a category, region, country, agent or sale that is
    /// <see langword="null"/> projects as <see langword="null"/>.
    /// </summary>
    /// <param name="item">The item to project.</param>
    /// <returns>The projection of <paramref name="item"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The item has a sale whose <see cref="Domain.Sale.Client"/> is not loaded.
    /// </exception>
    /// <exception cref="ArgumentException">The item's sale is neither a <see cref="RetailSale"/> nor a <see cref="TenderSale"/>.</exception>
    public static ItemProjection From(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var tenderItem = item as TenderItem;
        var category = item.ItemCategory;
        var region = item.Region;

        return new ItemProjection(
            item.Id,
            item.Guid,
            tenderItem is not null,
            item.CodeNumber,
            category?.Prefix,
            item.GetCode(),
            item.Name,
            item.Description,
            item.ItemCategoryId,
            category?.Name,
            item.ListedDate,
            item.Photo,
            item.Price,
            item.RegionId,
            region?.Name,
            region?.CountryId,
            region?.Country?.Name,
            item.AgentId,
            item.Agent is null ? null : AgentProjection.From(item.Agent),
            tenderItem?.ClosureDate,
            item.IsSold,
            item.Sale is null ? null : SaleDetailProjection.From(item.Sale),
            tenderItem is not null && tenderItem.Tenders.Count > 0);
    }
}
