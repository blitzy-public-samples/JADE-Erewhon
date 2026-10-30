namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Client</c>: a buyer of retail items and bidder on tender items.
/// </summary>
/// <remarks>
/// <para>
/// Dictionary membership is maintained by <c>ClientTA</c> and the sale and tender transaction
/// agents in <c>Validation.cs</c>, the counterpart of the JADE automatic inverses; the client only
/// exposes the dictionaries. As in JADE <c>onCreate</c>, the agent sets <see cref="Name"/> before
/// adding the client to <see cref="Company.AllClients"/>, and re-keys that dictionary when the
/// name changes.
/// </para>
/// <para>
/// JADE <c>getAllSales</c> (the <c>ISearchable</c> GUI search), <c>getDebugString</c> and
/// <c>getTAClass</c> are not ported: no REST, SOAP or seed path reads them. Not thread-safe.
/// </para>
/// </remarks>
public sealed class Client : PersistentModel
{
    /// <summary>Creates a client with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Client(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>name: String[81]</c>, already trimmed by <c>ClientTA</c>; the key of
    /// <see cref="Company.AllClients"/>. The length limit is not enforced.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>JADE <c>myAddress</c>, set when <c>ClientTA</c> creates the client's address.</summary>
    public Address? MyAddress { get; internal set; }

    /// <summary>JADE <c>myCompany</c>, set when <c>ClientTA</c> creates the client.</summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>
    /// JADE <c>allRetailSales</c>: the client's retail sales keyed by <c>dateSold</c>, then the sold
    /// item's category prefix and code number. SOAP <c>getPhoto</c> walks it forward and keeps the
    /// photo of the last sale whose item has one.
    /// </summary>
    public RetailSalesByTimeItemDict AllRetailSales { get; } = new();

    /// <summary>
    /// JADE <c>allTenderSales</c>: the client's winning tenders as sales, keyed by
    /// <c>myTender.dateTendered</c>, then the sold item's category prefix and code number.
    /// </summary>
    public TenderSalesByTimeItemDict AllTenderSales { get; } = new();

    /// <summary>
    /// JADE <c>allTenders</c>: every tender the client placed, keyed by the tender item's category
    /// prefix and code number, then <c>dateTendered</c>, then <c>offer</c> descending.
    /// </summary>
    public TendersByItemTimeOfferDict AllTenders { get; } = new();

    /// <summary>
    /// JADE <c>getNameAndAddress</c>: the name, a comma and a space, then the formatted address.
    /// </summary>
    /// <remarks>
    /// JADE raises a null-reference exception for a client without an address; every client
    /// <c>ClientTA</c> creates has one, so the empty address text applies only to a client still
    /// being built.
    /// </remarks>
    public string GetNameAndAddress() => Name + ", " + (MyAddress?.GetFormattedAddress() ?? "");
}
