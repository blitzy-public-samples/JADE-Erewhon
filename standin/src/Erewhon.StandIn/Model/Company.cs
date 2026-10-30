namespace Erewhon.StandIn.Model;

/// <summary>
/// The stand-in's replacement for the JADE <c>app</c> services the model reads: the current
/// company (<c>app.myCompany</c>), the server clock (<c>Utilities@getServerTimeStamp</c> and
/// <c>Utilities@getServerDate</c>) and GUID generation (<c>Utilities@generateGUID</c>).
/// </summary>
/// <remarks>
/// The model never reads a real clock or generates a GUID itself, so that every seed load and
/// request is reproducible byte for byte; the caller supplies both for each operation. The clock
/// is UTC because the JADE applications run with <c>_webDefaultTimeZone Etc/UTC</c>.
/// </remarks>
public sealed class ModelContext
{
    /// <summary>Creates the context for one seed load or one request.</summary>
    /// <param name="company">The current company, or null before one is created.</param>
    /// <param name="newGuid">Generates the guid of an entity created without a supplied one.</param>
    /// <param name="utcNow">The server timestamp for this operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="newGuid"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="utcNow"/> is not a UTC time.</exception>
    public ModelContext(Company? company, Func<string> newGuid, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(newGuid);
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"The model clock must be a UTC time; the value supplied has kind {utcNow.Kind}.",
                nameof(utcNow));
        }

        Company = company;
        NewGuid = newGuid;
        UtcNow = utcNow;
    }

    /// <summary>
    /// JADE <c>app.myCompany</c>. The loader sets it once <c>CompanyTA</c> has created the company
    /// (JADE <c>app.setCompany</c>); it is null in the <c>empty</c> reset state.
    /// </summary>
    public Company? Company { get; set; }

    /// <summary>
    /// JADE <c>Utilities@generateGUID</c>; called only when an entity is actually created without
    /// a supplied guid.
    /// </summary>
    public Func<string> NewGuid { get; }

    /// <summary>JADE server timestamp (<c>app.actualTimeServer()</c>), in UTC.</summary>
    public DateTime UtcNow { get; }

    /// <summary>JADE server date (<c>app.actualTimeServer().date()</c>).</summary>
    public DateOnly Today => DateOnly.FromDateTime(UtcNow);

    /// <summary>Returns the current company.</summary>
    /// <exception cref="InvalidOperationException">No company has been set.</exception>
    public Company RequireCompany() => Company ?? throw new InvalidOperationException(Messages.CompanyNotSet);
}

/// <summary>
/// JADE <c>Company</c>: the singleton root of the Erewhon object graph, holding the root
/// dictionaries every lookup starts from.
/// </summary>
/// <remarks>
/// <para>
/// Dictionary membership is maintained by the transaction agents in <c>Validation.cs</c>, the
/// counterpart of the JADE automatic inverses; the company only exposes the dictionaries.
/// </para>
/// <para>
/// JADE locks (<c>exclusiveLock</c>, <c>tryLock</c>) are not ported: the stand-in serialises every
/// request under one lock, so they would always succeed. Not thread-safe.
/// </para>
/// </remarks>
public sealed class Company : PersistentModel
{
    private long _lastOid;

    /// <summary>Creates an empty company; it takes object number 0.</summary>
    internal Company()
        : base(0)
    {
    }

    /// <summary>JADE <c>name: String[81]</c>; the length limit is not enforced.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>JADE <c>myAddress</c>, set when <c>CompanyTA</c> creates the company's address.</summary>
    public Address? MyAddress { get; internal set; }

    /// <summary>JADE <c>allAgents</c>, keyed by name.</summary>
    public AgentsByNameDict AllAgents { get; } = new();

    /// <summary>JADE <c>allClients</c>, keyed by name.</summary>
    public ClientsByNameDict AllClients { get; } = new();

    /// <summary>JADE <c>allCountries</c>, keyed by name.</summary>
    public CountriesByNameDict AllCountries { get; } = new();

    /// <summary>JADE <c>allItemCategoriesByName</c>.</summary>
    public ItemCategoriesByNameDict AllItemCategoriesByName { get; } = new();

    /// <summary>JADE <c>allItemCategoriesByPrefix</c>, keyed by prefix ignoring case.</summary>
    public ItemCategoriesByPrefixDict AllItemCategoriesByPrefix { get; } = new();

    /// <summary>JADE <c>allItems</c>: retail and tender items, keyed by code number.</summary>
    public ItemsByNumberDict AllItems { get; } = new();

    /// <summary>JADE <c>allRetailItems</c>, keyed by guid.</summary>
    public RetailItemsByGUIDDict AllRetailItems { get; } = new();

    /// <summary>JADE <c>allTenderItems</c>, keyed by guid.</summary>
    public TenderItemsByGUIDDict AllTenderItems { get; } = new();

    /// <summary>
    /// JADE <c>allTenderItemsBySaleDate</c>: sale descending, closure date descending, prefix,
    /// code number.
    /// </summary>
    public TenderItemsBySaleDateCodeDict AllTenderItemsBySaleDate { get; } = new();

    /// <summary>JADE <c>allSalesByItem</c>, keyed by the sold item's prefix and code number.</summary>
    public SalesByItemDict AllSalesByItem { get; } = new();

    /// <summary>JADE <c>allTenders</c>: tender time, then offer descending.</summary>
    public TendersByTimeOfferDict AllTenders { get; } = new();

    /// <summary>
    /// Allocates the next object number in this graph. Every entity created under this company,
    /// its own address included, takes its <see cref="PersistentModel.Oid"/> from here, so object
    /// numbers record creation order as JADE object identifiers do.
    /// </summary>
    internal long AllocateOid() => ++_lastOid;

    /// <summary>JADE <c>agentExists</c>: true when an agent has exactly this name.</summary>
    /// <remarks>
    /// JADE reads <c>app.myCompany.allAgents</c>; the company is a singleton, so this is the
    /// receiver's own dictionary.
    /// </remarks>
    public bool AgentExists(string agentName) => AllAgents.GetAtKey(agentName) != null;

    /// <summary>JADE <c>hasAgentWithThisName</c>: true when an agent has exactly this name.</summary>
    public bool HasAgentWithThisName(string name) => AllAgents.GetAtKey(name) != null;

    /// <summary>
    /// JADE <c>hasAnotherAgentWithThisName</c>: true when an agent other than
    /// <paramref name="agent"/> has exactly this name.
    /// </summary>
    public bool HasAnotherAgentWithThisName(string name, Agent? agent)
    {
        Agent? found = AllAgents.GetAtKey(name);
        return found != null && !ReferenceEquals(found, agent);
    }

    /// <summary>JADE <c>hasClientWithThisName</c>: true when a client has exactly this name.</summary>
    public bool HasClientWithThisName(string name) => AllClients.GetAtKey(name) != null;

    /// <summary>
    /// JADE <c>hasAnotherClientWithThisName</c>: true when a client other than
    /// <paramref name="client"/> has exactly this name.
    /// </summary>
    public bool HasAnotherClientWithThisName(string name, Client? client)
    {
        Client? found = AllClients.GetAtKey(name);
        return found != null && !ReferenceEquals(found, client);
    }

    /// <summary>JADE <c>hasCountryWithThisName</c>: true when a country has exactly this name.</summary>
    public bool HasCountryWithThisName(string name) => AllCountries.GetAtKey(name) != null;

    /// <summary>
    /// JADE <c>hasAnotherCountryWithThisName</c>: true when a country other than
    /// <paramref name="country"/> has exactly this name.
    /// </summary>
    public bool HasAnotherCountryWithThisName(string name, Country? country)
    {
        Country? found = AllCountries.GetAtKey(name);
        return found != null && !ReferenceEquals(found, country);
    }

    /// <summary>
    /// JADE <c>getItem</c>: the item with this code number, or null. The category prefix is never
    /// part of an item lookup.
    /// </summary>
    public Item? GetItem(int codeNumber) => AllItems.GetAtKey(codeNumber);

    /// <summary>
    /// JADE <c>getRegion</c>: the region with exactly this name in the country with exactly this
    /// name, or null when either does not exist.
    /// </summary>
    public Region? GetRegion(string countryName, string regionName) =>
        AllCountries.GetAtKey(countryName)?.AllRegions.GetAtKey(regionName);

    /// <summary>
    /// JADE <c>ItemTA.getNextCodeNumber</c>: 1 when the company has no items, otherwise the highest
    /// code number plus 1. Retail and tender items share the sequence, so the loader gives retail
    /// line n code n and, after the 58 retail items, tender line n code 58 + n.
    /// </summary>
    public int GetNextCodeNumber()
    {
        const int StartingNumber = 1;
        const int Increment = 1;
        return AllItems.IsEmpty ? StartingNumber : AllItems.Last()!.CodeNumber + Increment;
    }

    /// <summary>
    /// JADE <c>getNumberOfLocations</c>: each country counts as one location, plus one per region.
    /// </summary>
    public int GetNumberOfLocations()
    {
        int total = 0;
        foreach (Country country in AllCountries)
        {
            total += 1 + country.AllRegions.Count;
        }

        return total;
    }

    /// <summary>
    /// JADE <c>closeTendersAtDate</c>: accepts the highest tender of every unsold tender item that
    /// closes on or before <paramref name="date"/>.
    /// </summary>
    /// <remarks>
    /// The reversed dictionary lists unsold items first, by ascending closure date, so the walk stops
    /// at the first item that is sold or not yet due. Walking a snapshot is equivalent to JADE's live
    /// reversed iteration: an item sold inside the loop has already been visited, and the loop stops
    /// at the first sold item anyway. An item with no tenders is not closed and not counted.
    /// </remarks>
    /// <param name="date">The closure date; items closing on this date are closed.</param>
    /// <param name="context">The operation's context, whose company must be this company.</param>
    /// <returns>JADE's <c>numClosed</c> output: the number of tender items whose highest tender
    /// was accepted.</returns>
    /// <exception cref="InvalidOperationException">The context's company is not this company.</exception>
    public int CloseTendersAtDate(DateOnly date, ModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The tender-sale agents index the sales they create under context.Company (JADE
        // app.myCompany), while this walk reads the receiver; JADE only ever calls it on
        // app.myCompany, and a mismatch here would split one closure across two graphs.
        if (!ReferenceEquals(context.Company, this))
        {
            throw new InvalidOperationException(
                "Tenders can be closed only through a context whose company is this company.");
        }

        int numClosed = 0;
        foreach (TenderItem tenderItem in AllTenderItemsBySaleDate.Reversed())
        {
            if (tenderItem.MySale != null || tenderItem.ClosureDate > date)
            {
                return numClosed;
            }

            if (tenderItem.AcceptHighestTender(context) != null)
            {
                numClosed++;
            }
        }

        return numClosed;
    }
}
