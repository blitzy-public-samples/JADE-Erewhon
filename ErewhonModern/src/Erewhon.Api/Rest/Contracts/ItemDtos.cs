using System.Text.Json.Serialization;

namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// An item offered for sale or tender.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RetailItemDto), "retailItem")]
[JsonDerivedType(typeof(TenderItemDto), "tenderItem")]
public class ItemDto
{
    /// <summary>
    /// Source GUID of the item. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Code number of the item.
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Category prefix of the item code. At most 5 characters.
    /// </summary>
    public string? CategoryPrefix { get; set; }

    /// <summary>
    /// Full description of the item.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Date the item was listed.
    /// </summary>
    public DateOnly? ListedDate { get; set; }

    /// <summary>
    /// Agent selling the item.
    /// </summary>
    public AgentDto? Agent { get; set; }

    /// <summary>
    /// Name of the region the item is in. At most 81 characters.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Photo of the item as image bytes, written as base64 in JSON.
    /// </summary>
    public byte[]? Photo { get; set; }

    /// <summary>
    /// Price of the item, or the reserve price of a tender item, to 2 decimal places.
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Name of the item. At most 31 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Name of the item's category. At most 41 characters.
    /// </summary>
    public string? ItemCategory { get; set; }

    /// <summary>
    /// Details of the sale of the item, or <see langword="null"/> when the item is unsold.
    /// </summary>
    public SaleDetailDto? Sale { get; set; }
}

/// <summary>
/// A retail item.
/// </summary>
public sealed class RetailItemDto : ItemDto
{
}

/// <summary>
/// A tender item.
/// </summary>
public sealed class TenderItemDto : ItemDto
{
    /// <summary>
    /// Date the tender closes.
    /// </summary>
    public DateOnly? ClosureDate { get; set; }
}
