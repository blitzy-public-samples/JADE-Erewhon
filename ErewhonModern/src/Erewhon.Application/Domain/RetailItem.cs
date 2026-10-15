using Erewhon.Application.Common;
using Erewhon.Application.Messages;

namespace Erewhon.Application.Domain;

/// <summary>
/// An item sold at its listed price.
/// </summary>
public class RetailItem : Item
{
    /// <summary>The class number of <c>RetailItem</c>, <see cref="ObjectIdentity.RetailItem"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.RetailItem;

    /// <summary>Returns <see cref="Item.Price"/>.</summary>
    /// <returns>The price.</returns>
    public override decimal GetPrice() => Price;

    /// <summary>Returns the <see cref="MessageKeys.RetailItemType"/> text, <c>Retail</c>.</summary>
    /// <returns>The type text.</returns>
    public override string GetTypeString() => MessageCatalog.Get(MessageKeys.RetailItemType);

    /// <summary>Returns <see langword="true"/> for every offer.</summary>
    /// <param name="offer">The amount offered for the item.</param>
    /// <returns><see langword="true"/>.</returns>
    public override bool CheckOffer(decimal offer) => true;

    /// <summary>
    /// Returns <c>&lt;oid&gt; RetailItem: &lt;code&gt;, &lt;name&gt;, $&lt;price&gt;</c>,
    /// e.g. <c>5786.9 RetailItem: CATI2, Proxy Retail, $5.50</c>.
    /// </summary>
    /// <returns>The debug string.</returns>
    public override string GetDebugString() => BuildItemDebugString("RetailItem");
}
