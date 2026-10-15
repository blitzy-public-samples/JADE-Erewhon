using System.Text.Json.Serialization;

namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// A sale of an item to a client.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RetailSaleDto), "retailSale")]
[JsonDerivedType(typeof(TenderSaleDto), "tenderSale")]
public class SaleDto
{
    /// <summary>
    /// Source GUID of the sale. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Commission earned by the item's agent on the sale, to 2 decimal places.
    /// </summary>
    public decimal AgentCommission { get; set; }

    /// <summary>
    /// Client who bought the item.
    /// </summary>
    public ClientDto? Client { get; set; }

    /// <summary>
    /// Item that was sold.
    /// </summary>
    public ItemDto? Item { get; set; }
}

/// <summary>
/// A sale of a retail item to a client.
/// </summary>
public sealed class RetailSaleDto : SaleDto
{
    /// <summary>
    /// Date and time the item was sold.
    /// </summary>
    public DateTime? Timestamp { get; set; }
}

/// <summary>
/// A sale of a tender item to the client whose tender won.
/// </summary>
public sealed class TenderSaleDto : SaleDto
{
    /// <summary>
    /// Winning offer, to 2 decimal places.
    /// </summary>
    public decimal Offer { get; set; }

    /// <summary>
    /// Winning tender.
    /// </summary>
    public TenderDto? Tender { get; set; }
}

/// <summary>
/// Details of the sale of an item.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RetailSaleDetailDto), "retailSaleDetail")]
[JsonDerivedType(typeof(TenderSaleDetailDto), "tenderSaleDetail")]
public class SaleDetailDto
{
    /// <summary>
    /// Source GUID of the sale. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Commission earned by the item's agent on the sale, to 2 decimal places.
    /// </summary>
    public decimal AgentCommission { get; set; }

    /// <summary>
    /// Client who bought the item.
    /// </summary>
    public ClientDto? Client { get; set; }
}

/// <summary>
/// Details of the sale of a retail item.
/// </summary>
public sealed class RetailSaleDetailDto : SaleDetailDto
{
    /// <summary>
    /// Date and time the item was sold.
    /// </summary>
    public DateTime? SoldDate { get; set; }

    /// <summary>
    /// Price the item was sold for, to 2 decimal places.
    /// </summary>
    public decimal Price { get; set; }
}

/// <summary>
/// Details of the sale of a tender item.
/// </summary>
public sealed class TenderSaleDetailDto : SaleDetailDto
{
    /// <summary>
    /// Winning offer, to 2 decimal places.
    /// </summary>
    public decimal Offer { get; set; }

    /// <summary>
    /// Date and time the winning tender was placed.
    /// </summary>
    public DateTime? TenderDate { get; set; }
}
