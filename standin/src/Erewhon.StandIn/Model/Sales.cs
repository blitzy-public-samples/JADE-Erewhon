namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Sale</c> (abstract): the sale of one <see cref="Item"/> to a <see cref="Client"/>,
/// either a <see cref="RetailSale"/> at the item's price or a <see cref="TenderSale"/> to the
/// winning tender (<c>Erewhon/ErewhonModelSchema.scm</c> class definition 3501-3515, methods
/// 7525-7609).
/// </summary>
/// <remarks>
/// <para>
/// A sale is never updated once created. <c>SaleTA</c> and its subclasses in <c>Validation.cs</c>
/// create it in JADE <c>onCreate</c> order: the guid, the common properties
/// (<see cref="AgentCommission"/> from the transaction agent, then the subclass attributes), then
/// <see cref="MyCompany"/>, <see cref="MyClient"/> and <see cref="MyItem"/>, then (tender sales)
/// <see cref="TenderSale.MyTender"/>, and last <see cref="CalculateAgentCommission"/>, which
/// replaces the agent's commission value with the computed one.
/// </para>
/// <para>
/// <see cref="MyItem"/> keys <see cref="Company.AllSalesByItem"/>, <see cref="Client.AllRetailSales"/>
/// and <see cref="Client.AllTenderSales"/>, and the sale itself keys
/// <see cref="Company.AllTenderItemsBySaleDate"/> through <see cref="Item.MySale"/>. The transaction
/// agents set the references, the inverse <see cref="Item.MySale"/> and every dictionary membership,
/// the counterpart of the JADE automatic inverses, so the setters here have no side effects.
/// </para>
/// <para>
/// JADE <c>getDebugString</c> and <c>getTAClass</c> serve the JADE IDE only and are not ported.
/// Not thread-safe.
/// </para>
/// </remarks>
public abstract class Sale : PersistentModel
{
    /// <summary>Creates a sale with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    protected Sale(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>agentCommission: Decimal[12,2]</c>: the commission the item's agent earns on the
    /// sale, as computed by <see cref="CalculateAgentCommission"/>.
    /// </summary>
    public decimal AgentCommission { get; internal set; }

    /// <summary>JADE <c>myClient</c>: the buyer, or for a tender sale the winning bidder.</summary>
    public Client? MyClient { get; internal set; }

    /// <summary>JADE <c>myCompany</c>, set when the sale is created (JADE <c>app.myCompany</c>).</summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>JADE <c>myItem</c>: the item sold, whose <see cref="Item.MySale"/> is this sale.</summary>
    public Item? MyItem { get; internal set; }

    /// <summary>JADE <c>getDate</c>: the date of the sale, or null when it cannot be determined.</summary>
    public abstract DateOnly? GetDate();

    /// <summary>
    /// JADE <c>calculateAgentCommission</c> (7527-7557): sets <see cref="AgentCommission"/> to
    /// <paramref name="salePrice"/> times the percentage of the rate the item's agent earns on the
    /// item's category, divided by 100, or to 0 when the item has no agent or no rate applies.
    /// </summary>
    /// <remarks>
    /// The rate is <see cref="Agent.GetCommissionRateForCategory"/> of the item's agent (not of
    /// the client), so it falls back to the category's lowest rate when the agent has none of its
    /// own. Called once, when the sale is created, with <see cref="RetailSale.SalePrice"/> or the
    /// winning <see cref="Tender.Offer"/>.
    /// </remarks>
    /// <param name="salePrice">The amount the commission is earned on.</param>
    /// <exception cref="InvalidOperationException">The sale has no item.</exception>
    internal void CalculateAgentCommission(decimal salePrice)
    {
        // JADE raises a null-reference exception reading myItem.myItemCategory; the sale agents
        // always set the item first, so this is reachable only for a sale still being built.
        Item item = MyItem
            ?? throw new InvalidOperationException(
                $"{GetType().Name} (oid {Oid}) has no item, so its agent commission cannot be calculated.");

        AgentCommission = 0m;
        ItemCategory? category = item.MyItemCategory;

        Agent? agent = item.MyAgent;
        if (agent != null)
        {
            // ItemTA never creates an item without a category; the null string stands in for the
            // name JADE could not read, and matches no rate.
            CommissionRate? rate = agent.GetCommissionRateForCategory(category?.Name ?? "");
            if (rate != null)
            {
                // Assignment to Decimal[12,2] rounds to two places, assumed half away from zero
                // (register entry KL-STANDIN-ROUNDING); the product itself is exact in decimal.
                AgentCommission = Math.Round(
                    salePrice * (rate.Percentage / 100m), 2, MidpointRounding.AwayFromZero);
            }
        }
    }
}

/// <summary>
/// JADE <c>RetailSale subclassOf Sale</c>: the sale of a <see cref="RetailItem"/> at a fixed
/// price, timestamped when it was made (class definition 3516-3532, methods 7610-7690).
/// </summary>
/// <remarks>
/// <see cref="SalePrice"/> and <see cref="DateSold"/> hold the values <c>RetailSaleTA</c> assigned
/// (JADE <c>setCommonProperties</c>), already rounded and validated there.
/// <see cref="DateSold"/> keys <see cref="Client.AllRetailSales"/>, so it is set before the sale is
/// indexed and never changed.
/// </remarks>
public sealed class RetailSale : Sale
{
    private DateTime _dateSold;

    /// <summary>Creates a retail sale with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal RetailSale(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>salePrice: Decimal[12,2]</c>: the price paid, the item's price at sale.</summary>
    public decimal SalePrice { get; internal set; }

    /// <summary>
    /// JADE <c>dateSold: TimeStamp</c> (millisecond precision): the seed date shifted by the
    /// loader's day offset plus the seed time, or the server timestamp of the request that made
    /// the sale.
    /// </summary>
    /// <remarks>
    /// The value must be UTC, as every model timestamp is (the JADE applications run with
    /// <c>_webDefaultTimeZone Etc/UTC</c>). A local or unspecified time is rejected rather than
    /// stored, because the writers would otherwise convert it through the host time zone and the
    /// output would depend on the machine.
    /// </remarks>
    /// <exception cref="ArgumentException">The value assigned is not a UTC time.</exception>
    public DateTime DateSold
    {
        get => _dateSold;
        internal set
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    $"A retail sale timestamp must be a UTC time; the value supplied has kind {value.Kind}.",
                    nameof(value));
            }

            _dateSold = value;
        }
    }

    /// <summary>JADE <c>getDate</c> (7612-7626): the date part of <see cref="DateSold"/>.</summary>
    public override DateOnly? GetDate() => DateOnly.FromDateTime(DateSold);
}

/// <summary>
/// JADE <c>TenderSale subclassOf Sale</c>: the sale of a <see cref="TenderItem"/> to its winning
/// <see cref="Tender"/>, made when the item's tenders close (class definition 3533-3548, methods
/// 7691-7770).
/// </summary>
/// <remarks>
/// A tender sale has no price or timestamp of its own: the winning tender's
/// <see cref="Tender.Offer"/> is the amount the commission is earned on, and its
/// <see cref="Tender.DateTendered"/> dates the sale and keys <see cref="Client.AllTenderSales"/>.
/// </remarks>
public sealed class TenderSale : Sale
{
    /// <summary>Creates a tender sale with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal TenderSale(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>myTender</c> (<c>manual peerOf myTenderSale of Tender automatic</c>): the winning
    /// tender, whose <see cref="Tender.MyTenderSale"/> is this sale. <c>TenderSaleTA</c> sets both
    /// sides.
    /// </summary>
    public Tender? MyTender { get; internal set; }

    /// <summary>
    /// JADE <c>getDate</c> (7693-7705): the date of the winning tender, or null when the sale has
    /// no tender (JADE would raise a null-reference exception; <c>TenderSaleTA</c> never creates
    /// one without it).
    /// </summary>
    public override DateOnly? GetDate() => MyTender?.GetDate();

    /// <summary>
    /// JADE <c>getTime</c> (7735-7747): the time of the winning tender, or null when the sale has
    /// no tender.
    /// </summary>
    public TimeOnly? GetTime() => MyTender?.GetTime();
}
