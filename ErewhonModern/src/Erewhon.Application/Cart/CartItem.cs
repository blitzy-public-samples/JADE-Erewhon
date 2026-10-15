namespace Erewhon.Application.Cart;

/// <summary>
/// One line of a shopping cart: a retail purchase or a tender bid on one item, with the request values captured when the line was added.
/// </summary>
public sealed class CartItem
{
    /// <summary>The internal id of the line's item.</summary>
    public required long ItemId { get; init; }

    /// <summary>True for a tender item line; false for a retail item line.</summary>
    public required bool IsTender { get; init; }

    /// <summary>The item code (category prefix followed by code number) read when the line was added; at most 11 characters.</summary>
    public required string Code { get; init; }

    /// <summary>The transaction type text: the tender item type text ("Tender") or the retail item type text ("Retail").</summary>
    public required string Transaction { get; init; }

    /// <summary>The offer passed when the line was added, rounded to 2 decimal places; normally 0 on a retail line.</summary>
    public required decimal Offer { get; init; }

    /// <summary>Retail lines: the item price copied when the line was added, unchanged by any later repricing of the item. Tender lines: 0.</summary>
    public decimal SalePrice { get; init; }

    /// <summary>The id of the session client when the line was added; 0 when the session had no client.</summary>
    public long ClientId { get; init; }

    /// <summary>Tender lines: the server timestamp taken when the line was added. Retail lines: null.</summary>
    public DateTime? DateTendered { get; init; }

    /// <summary>
    /// Retail lines: null until the first checkout pre-validation of the line assigns the server timestamp, which then stays on the line for every later checkout attempt. Tender lines: null.
    /// </summary>
    public DateTime? DateSold { get; set; }
}
