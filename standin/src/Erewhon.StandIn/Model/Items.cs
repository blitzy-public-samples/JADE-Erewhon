using System.Globalization;

namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Item</c> (abstract): an item an agent lists for sale, either a
/// <see cref="RetailItem"/> sold at its price or a <see cref="TenderItem"/> sold to the highest
/// tender once it closes.
/// </summary>
/// <remarks>
/// <para>
/// Every attribute holds the value <c>ItemTA</c> assigned (JADE <c>onCreate</c> and
/// <c>setCommonProperties</c>), already trimmed and validated there; this class neither trims nor
/// validates. The empty string is the JADE null string. <see cref="PersistentModel.Guid"/> comes
/// from the seed file, so item guids are never generated.
/// </para>
/// <para>
/// <see cref="CodeNumber"/> and the category prefix key <see cref="Company.AllItems"/> and every
/// item dictionary; both are set once, when the item is created. <see cref="MySale"/> is the
/// inverse of <c>Sale.MyItem</c> and keys <see cref="Company.AllTenderItemsBySaleDate"/>; the sale
/// transaction agents in <c>Validation.cs</c> set it, re-key that dictionary and add the item to
/// <see cref="Agent.AllSoldItems"/>, so its setter has no side effects. Dictionary membership is
/// likewise maintained by <c>ItemTA</c>, the counterpart of the JADE automatic inverses.
/// </para>
/// <para>
/// The JADE getters <c>getCodeNumber</c>, <c>getName</c> and <c>getSale</c> are the properties
/// <see cref="CodeNumber"/>, <see cref="Name"/> and <see cref="MySale"/>. <c>getDebugString</c>
/// and <c>getTAClass</c> serve the JADE IDE only and are not ported. Not thread-safe.
/// </para>
/// </remarks>
public abstract class Item : PersistentModel
{
    private byte[]? _photo;

    /// <summary>Creates an item with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    protected Item(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>codeNumber: Integer</c>, from <c>Company.GetNextCodeNumber</c>: unique across retail
    /// and tender items, and the only key an item is looked up by.
    /// </summary>
    public int CodeNumber { get; internal set; }

    /// <summary>JADE <c>description: String</c>.</summary>
    public string Description { get; internal set; } = "";

    /// <summary>JADE <c>listedDate: Date</c>, the seed date shifted by the loader's day offset.</summary>
    public DateOnly ListedDate { get; internal set; }

    /// <summary>JADE <c>name: String[31]</c>, not necessarily unique; the length is not enforced.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// JADE <c>photo: Binary</c>: the raw bytes of the item's image file, or null for the JADE null
    /// binary.
    /// </summary>
    /// <remarks>
    /// A zero-length value is stored as null, because JADE has no distinct empty binary and the
    /// writers emit null and empty differently.
    /// </remarks>
    public byte[]? Photo
    {
        get => _photo;
        internal set => _photo = value is { Length: 0 } ? null : value;
    }

    /// <summary>JADE <c>price: Decimal[12,2]</c>, assigned already rounded to two decimals.</summary>
    public decimal Price { get; internal set; }

    /// <summary>JADE <c>myAgent</c>: the agent who lists the item.</summary>
    public Agent? MyAgent { get; internal set; }

    /// <summary>JADE <c>myCompany</c>, set when the item is created (JADE <c>app.myCompany</c>).</summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>JADE <c>myItemCategory</c>, whose prefix forms the item's code.</summary>
    public ItemCategory? MyItemCategory { get; internal set; }

    /// <summary>JADE <c>myRegion</c>: the region the item is located in.</summary>
    public Region? MyRegion { get; internal set; }

    /// <summary>
    /// JADE <c>mySale</c> (<c>mySale of Item automatic parentOf myItem of Sale</c>): the item's sale,
    /// or null while it is unsold.
    /// </summary>
    public Sale? MySale { get; internal set; }

    /// <summary>JADE <c>isSold</c>: true once the item has a sale.</summary>
    public bool IsSold => MySale != null;

    /// <summary>
    /// JADE <c>getCodePrefix</c>: the four-character prefix of the item's category, or null when the
    /// item has no category.
    /// </summary>
    public string? GetCodePrefix() => MyItemCategory?.Prefix;

    /// <summary>
    /// JADE <c>getCode</c>: the category prefix followed by the code number with no separator, for
    /// example <c>ANQU1</c>; the JADE null string when the item has no category.
    /// </summary>
    public string GetCode() =>
        MyItemCategory == null ? "" : MyItemCategory.Prefix + CodeNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>JADE <c>getPrice</c>, implemented identically by both subclasses.</summary>
    public decimal GetPrice() => Price;

    /// <summary>JADE <c>getTypeString</c>: the translatable name of the item's kind.</summary>
    public abstract string GetTypeString();
}

/// <summary>JADE <c>RetailItem subclassOf Item</c>: an item sold outright at its price.</summary>
public sealed class RetailItem : Item
{
    /// <summary>Creates a retail item with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal RetailItem(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>getTypeString</c>: <c>$RetailItemType</c>, "Retail".</summary>
    public override string GetTypeString() => Messages.RetailItemType;
}

/// <summary>
/// JADE <c>TenderItem subclassOf Item</c>: an item whose <see cref="Item.Price"/> is the reserve,
/// sold to the highest tender once its closure date has passed.
/// </summary>
/// <remarks>
/// <see cref="ClosureDate"/> keys <see cref="Company.AllTenderItemsBySaleDate"/> and is set once,
/// by <c>TenderItemTA</c>, before the item is indexed. The tender dictionaries are the parent side
/// of <c>Tender.MyTenderItem</c> and are maintained by <c>TenderTA</c> in <c>Validation.cs</c>.
/// </remarks>
public sealed class TenderItem : Item
{
    /// <summary>Creates a tender item with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal TenderItem(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>closureDate: Date</c>: the last day tenders are accepted.</summary>
    public DateOnly ClosureDate { get; internal set; }

    /// <summary>
    /// JADE <c>allTendersByOfferTime</c>: offer descending, then tender time, then creation order, so
    /// the first entry is the highest offer and the earliest among equal offers.
    /// </summary>
    public TendersByOfferTimeDict AllTendersByOfferTime { get; } = new();

    /// <summary>JADE <c>allTendersByTimeOffer</c>: tender time, then offer descending.</summary>
    public TendersByTimeOfferDict AllTendersByTimeOffer { get; } = new();

    /// <summary>
    /// JADE <c>isClosed</c>: true when the closure date is before <paramref name="today"/>. An item
    /// closing today is still open.
    /// </summary>
    /// <param name="today">
    /// The server date (JADE <c>app.actualTimeServer().date()</c>), normally
    /// <see cref="ModelContext.Today"/>.
    /// </param>
    public bool IsClosed(DateOnly today) => ClosureDate < today;

    /// <summary>JADE <c>getTypeString</c>: <c>$TenderItemType</c>, "Tender".</summary>
    public override string GetTypeString() => Messages.TenderItemType;

    /// <summary>
    /// JADE <c>acceptHighestTender</c>: creates a tender sale of this item for its highest tender.
    /// </summary>
    /// <remarks>
    /// As in JADE, the winning tender is returned whether or not <c>TenderSaleTA</c> persisted the
    /// sale (a failed validation creates nothing), and <see cref="Company.CloseTendersAtDate"/>
    /// counts every non-null result.
    /// </remarks>
    /// <param name="context">The operation's context, which supplies the sale's guid and company.</param>
    /// <returns>The winning tender, or null when the item has no tenders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Tender? AcceptHighestTender(ModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Tender? highestTender = AllTendersByOfferTime.First();
        if (highestTender != null)
        {
            var tenderSaleTA = new TenderSaleTA
            {
                MyTender = highestTender,
                MyItem = this,
                MyClient = highestTender.MyClient,
            };

            _ = tenderSaleTA.PersistEntity(TransactionType.Persist, context);
        }

        return highestTender;
    }
}
