namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Tender</c>: one client's offer for a <see cref="TenderItem"/>
/// (<c>Erewhon/ErewhonModelSchema.scm</c> class definition 3549-3572, methods 7771-7888).
/// </summary>
/// <remarks>
/// <para>
/// Every attribute holds the value <c>TenderTA</c> assigned (JADE <c>setCommonProperties</c>, then
/// <c>onCreate</c> for <see cref="MyClient"/>, <see cref="MyTenderItem"/> and
/// <see cref="MyCompany"/>), already validated there; this class neither rounds nor validates
/// business rules. <see cref="PersistentModel.Guid"/> is generated when the tender is created
/// (<c>ModelContext.NewGuid</c>), never taken from the seed, whose fifth field names the tender
/// item instead.
/// </para>
/// <para>
/// <see cref="Offer"/>, <see cref="DateTendered"/> and <see cref="MyTenderItem"/> key
/// <see cref="TenderItem.AllTendersByOfferTime"/>, <see cref="TenderItem.AllTendersByTimeOffer"/>,
/// <see cref="Client.AllTenders"/> and <see cref="Company.AllTenders"/>, the parent sides of the JADE
/// automatic inverses on <c>myTenderItem</c>, <c>myClient</c> and <c>myCompany</c>. <c>TenderTA</c>
/// sets them once, before adding the tender to those dictionaries, and maintains the membership;
/// the setters have no side effects. <see cref="MyTenderSale"/> is the automatic peer of
/// <c>TenderSale.MyTender</c>, set by <c>TenderSaleTA</c> when this tender wins.
/// </para>
/// <para>
/// JADE <c>getDebugString</c> and <c>getTAClass</c> serve the JADE IDE only and are not ported. Not
/// thread-safe.
/// </para>
/// </remarks>
public sealed class Tender : PersistentModel
{
    private DateTime _dateTendered;

    /// <summary>Creates a tender with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Tender(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>offer: Decimal[12,2]</c>, assigned already rounded to two decimals by
    /// <c>TenderTA</c>.
    /// </summary>
    public decimal Offer { get; internal set; }

    /// <summary>
    /// JADE <c>dateTendered: TimeStamp</c> (millisecond precision): the seed date shifted by the
    /// loader's day offset plus the seed time, or the server timestamp of the request that placed
    /// the tender.
    /// </summary>
    /// <remarks>
    /// The value must be UTC, as every model timestamp is (the JADE applications run with
    /// <c>_webDefaultTimeZone Etc/UTC</c>). A local or unspecified time is rejected rather than
    /// stored, because the writers would otherwise convert it through the host time zone and the
    /// output would depend on the machine.
    /// </remarks>
    /// <exception cref="ArgumentException">The value assigned is not a UTC time.</exception>
    public DateTime DateTendered
    {
        get => _dateTendered;
        internal set
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    $"A tender timestamp must be a UTC time; the value supplied has kind {value.Kind}.",
                    nameof(value));
            }

            _dateTendered = value;
        }
    }

    /// <summary>JADE <c>myClient</c>: the client who placed the tender.</summary>
    public Client? MyClient { get; internal set; }

    /// <summary>JADE <c>myCompany</c>, set when the tender is created (JADE <c>app.myCompany</c>).</summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>JADE <c>myTenderItem</c>: the item the offer is for.</summary>
    public TenderItem? MyTenderItem { get; internal set; }

    /// <summary>
    /// JADE <c>myTenderSale</c> (<c>myTender of TenderSale manual peerOf myTenderSale of Tender
    /// automatic</c>): the sale this tender won, or null while it has not won.
    /// </summary>
    public TenderSale? MyTenderSale { get; internal set; }

    /// <summary>JADE <c>getDate</c> (7773-7784): the date part of <see cref="DateTendered"/>.</summary>
    public DateOnly GetDate() => DateOnly.FromDateTime(DateTendered);

    /// <summary>JADE <c>getTime</c> (7841-7852): the time part of <see cref="DateTendered"/>.</summary>
    public TimeOnly GetTime() => TimeOnly.FromDateTime(DateTendered);

    /// <summary>
    /// JADE <c>getStatus</c> (7804-7832): <see cref="ErewhonStatuses.Successful"/> when this tender
    /// won its item, otherwise <see cref="ErewhonStatuses.Closed"/> when the item's closure date is
    /// before <paramref name="today"/>, otherwise <see cref="ErewhonStatuses.Open"/>. An item
    /// closing today is still open.
    /// </summary>
    /// <param name="today">
    /// The server date (JADE <c>Utilities@getServerDate</c>), normally
    /// <see cref="ModelContext.Today"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The tender has not won and has no tender item.
    /// </exception>
    public string GetStatus(DateOnly today)
    {
        if (MyTenderSale != null)
        {
            return ErewhonStatuses.Successful;
        }

        // JADE raises a null-reference exception reading myTenderItem.closureDate; TenderTA never
        // creates a tender without an item, so this is reachable only for a tender still being built.
        TenderItem tenderItem = MyTenderItem
            ?? throw new InvalidOperationException(
                $"Tender (oid {Oid}) has no tender item, so its status cannot be determined.");

        return tenderItem.ClosureDate < today ? ErewhonStatuses.Closed : ErewhonStatuses.Open;
    }
}
