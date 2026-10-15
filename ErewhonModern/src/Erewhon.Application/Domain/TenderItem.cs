using Erewhon.Application.Common;
using Erewhon.Application.Messages;

namespace Erewhon.Application.Domain;

/// <summary>
/// An item sold by tender: the inherited item details, with <see cref="Item.Price"/> as the reserve price,
/// its closure date and the tenders placed on it.
/// </summary>
public class TenderItem : Item
{
    /// <summary>The date tendering on the item closes.</summary>
    public DateOnly ClosureDate { get; set; }

    /// <summary>The tenders placed on the item; deleting the item deletes them.</summary>
    public ICollection<Tender> Tenders { get; set; } = new List<Tender>();

    /// <summary>The class number of <c>TenderItem</c>, <see cref="ObjectIdentity.TenderItem"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.TenderItem;

    /// <summary>
    /// Returns whether <see cref="ClosureDate"/> is before <paramref name="serverDate"/>;
    /// an item whose closure date equals the server date is not closed.
    /// </summary>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns><see langword="true"/> when the item is closed; otherwise <see langword="false"/>.</returns>
    public bool IsClosed(DateOnly serverDate) => IsClosedAt(ClosureDate, serverDate);

    /// <summary>
    /// Returns whether a tender item with closure date <paramref name="closureDate"/> is closed at
    /// <paramref name="serverDate"/>: whether <paramref name="closureDate"/> is before <paramref name="serverDate"/>;
    /// a closure date equal to the server date is not closed.
    /// </summary>
    /// <param name="closureDate">The date tendering on the item closes.</param>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns><see langword="true"/> when the item is closed; otherwise <see langword="false"/>.</returns>
    public static bool IsClosedAt(DateOnly closureDate, DateOnly serverDate) => closureDate < serverDate;

    /// <summary>Returns <see cref="Item.Price"/>, the reserve price.</summary>
    /// <returns>The price.</returns>
    public override decimal GetPrice() => Price;

    /// <summary>Returns the <see cref="MessageKeys.TenderItemType"/> text, <c>Tender</c>.</summary>
    /// <returns>The type text.</returns>
    public override string GetTypeString() => MessageCatalog.Get(MessageKeys.TenderItemType);

    /// <summary>
    /// Returns whether <paramref name="offer"/> is at least <see cref="Item.Price"/>, the reserve price.
    /// </summary>
    /// <param name="offer">The amount offered for the item.</param>
    /// <returns><see langword="true"/> when <paramref name="offer"/> is greater than or equal to the price; otherwise <see langword="false"/>.</returns>
    public override bool CheckOffer(decimal offer) => offer >= Price;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; TenderItem: &lt;code&gt;, &lt;name&gt;, $&lt;price&gt;, &lt;closure date&gt;</c>
    /// with the closure date as <c>dd MMMM yyyy</c>,
    /// e.g. <c>5787.4 TenderItem: CATI1, Proxy Tender, $10.00, 10 May 2024</c> for the tender item with id 4.
    /// </summary>
    /// <returns>The debug string.</returns>
    public override string GetDebugString() =>
        BuildItemDebugString("TenderItem") + ", " + JadeFormat.DateString(ClosureDate);
}
