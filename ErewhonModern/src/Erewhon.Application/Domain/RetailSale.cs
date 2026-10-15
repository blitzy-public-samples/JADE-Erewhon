using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// The sale of a retail item to a client at a recorded price and time.
/// </summary>
public class RetailSale : Sale
{
    /// <summary>
    /// The price the item was sold at, stored with precision 12 and 2 decimal places; for a sale from a shopping cart,
    /// the item price copied when its line was added.
    /// </summary>
    public decimal SalePrice { get; set; }

    /// <summary>
    /// The time the item was sold, to the millisecond, in database server local time with
    /// <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    public DateTime DateSold { get; set; }

    /// <summary>The class number of <c>RetailSale</c>, <see cref="ObjectIdentity.RetailSale"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.RetailSale;

    /// <summary>Returns the date part of <see cref="DateSold"/>.</summary>
    /// <returns>The date the item was sold.</returns>
    public override DateOnly? GetDate() => DateOnly.FromDateTime(DateSold);

    /// <summary>
    /// Returns <c>&lt;oid&gt; RetailSale: $&lt;sale price&gt;, &lt;date sold&gt;, &lt;item code&gt; (&lt;item name&gt;), &lt;client name&gt;</c>
    /// with the sale price to 2 decimal places and the date sold as <c>dd MMMM yyyy, HH:mm:ss</c>,
    /// e.g. <c>5783.6 RetailSale: $5.50, 10 May 2024, 14:03:07, CATI2 (Proxy Retail), Clive Entworth</c>
    /// for the retail sale with id 6. The item code is empty when the item has no category.
    /// </summary>
    /// <returns>The debug string.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Sale.Item"/> or <see cref="Sale.Client"/> is not loaded.</exception>
    public override string GetDebugString()
    {
        var item = RequireItem();
        var client = RequireClient();
        return ObjectIdentity.Of(this) + " RetailSale: $" + JadeFormat.DecimalString(SalePrice) + ", "
            + JadeFormat.TimeStampString(DateSold) + ", " + item.GetCode() + " (" + item.Name + "), " + client.Name;
    }

    private Item RequireItem() =>
        Item ?? throw new InvalidOperationException("RetailSale.Item is not loaded.");

    private Client RequireClient() =>
        Client ?? throw new InvalidOperationException("RetailSale.Client is not loaded.");
}
