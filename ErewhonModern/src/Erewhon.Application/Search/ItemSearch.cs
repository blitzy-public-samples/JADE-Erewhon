using System.Linq.Expressions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Search;

/// <summary>
/// The criteria of an item search and the ids of the items the last search found.
/// A new instance has every flag <see langword="false"/>, both prices 0, every filter id <see langword="null"/>
/// and no results. A price of 0 sets no bound and a <see langword="null"/> filter id sets no filter.
/// One instance holds the search state of one session; its members are not safe to call from several threads at once.
/// </summary>
public sealed class ItemSearchCriteria
{
    private decimal _priceLow;
    private decimal _priceHigh;

    /// <summary>Includes tender items whose closure date is before the server date.</summary>
    public bool IncludeClosedTenders { get; set; }

    /// <summary>Includes items that have a line in the searching session's cart.</summary>
    public bool IncludeItemsInCart { get; set; }

    /// <summary>Includes items that have a sale.</summary>
    public bool IncludeSoldItems { get; set; }

    /// <summary>Excludes tender items.</summary>
    public bool OnlyRetailItems { get; set; }

    /// <summary>Excludes retail items.</summary>
    public bool OnlyTenderItems { get; set; }

    /// <summary>
    /// The lowest price an item may have, inclusive; 0 sets no lower bound.
    /// The value is stored rounded to 2 decimal places, ties away from zero.
    /// </summary>
    public decimal PriceLow
    {
        get => _priceLow;
        set => _priceLow = DecimalRules.Round2(value);
    }

    /// <summary>
    /// The highest price an item may have, inclusive; 0 sets no upper bound.
    /// The value is stored rounded to 2 decimal places, ties away from zero.
    /// </summary>
    public decimal PriceHigh
    {
        get => _priceHigh;
        set => _priceHigh = DecimalRules.Round2(value);
    }

    /// <summary>The row id of the agent an item must have; <see langword="null"/> for any agent.</summary>
    public long? AgentId { get; set; }

    /// <summary>
    /// The row id of the client an item must have been sold to; <see langword="null"/> for sold and unsold items alike.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>The row id of the country of the region an item must be listed in; <see langword="null"/> for any country.</summary>
    public long? CountryId { get; set; }

    /// <summary>The row id of the region an item must be listed in; <see langword="null"/> for any region.</summary>
    public long? RegionId { get; set; }

    /// <summary>The row id of the category an item must belong to; <see langword="null"/> for any category.</summary>
    public long? ItemCategoryId { get; set; }

    /// <summary>
    /// The row ids of the items the last search found, in code-number order; empty before the first search and after
    /// <see cref="Clear"/> or <see cref="Initialize"/>. Each search or reset replaces the list rather than changing it.
    /// </summary>
    public IReadOnlyList<long> ResultItemIds { get; private set; } = Array.Empty<long>();

    /// <summary>
    /// Empties <see cref="ResultItemIds"/>, sets <see cref="CountryId"/>, <see cref="RegionId"/> and
    /// <see cref="ItemCategoryId"/> to <see langword="null"/>, sets <see cref="OnlyRetailItems"/> and
    /// <see cref="OnlyTenderItems"/> to <see langword="false"/>, sets <see cref="PriceLow"/> and <see cref="PriceHigh"/>
    /// to 0, and sets <see cref="IncludeClosedTenders"/> and <see cref="IncludeSoldItems"/> to <see langword="false"/>.
    /// <see cref="IncludeItemsInCart"/>, <see cref="AgentId"/> and <see cref="ClientId"/> keep their values.
    /// </summary>
    public void Initialize()
    {
        ResultItemIds = Array.Empty<long>();

        CountryId = null;
        RegionId = null;
        ItemCategoryId = null;
        OnlyRetailItems = false;
        OnlyTenderItems = false;
        PriceLow = 0m;
        PriceHigh = 0m;
        IncludeClosedTenders = false;
        IncludeSoldItems = false;
    }

    /// <summary>Empties <see cref="ResultItemIds"/>; every criterion keeps its value.</summary>
    public void Clear()
    {
        ResultItemIds = Array.Empty<long>();
    }

    /// <summary>Replaces <see cref="ResultItemIds"/> with a copy of <paramref name="itemIds"/>, in the same order.</summary>
    /// <param name="itemIds">The row ids of the items found, in code-number order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="itemIds"/> is <see langword="null"/>.</exception>
    internal void SetResults(IReadOnlyList<long> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ResultItemIds = itemIds.ToList().AsReadOnly();
    }
}

/// <summary>
/// Searches the items of the bound company with an <see cref="ItemSearchCriteria"/>.
/// It only reads: it writes no row and opens no transaction.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </summary>
/// <param name="db">The database the items are read from.</param>
/// <param name="companyContext">The bound company whose items are searched.</param>
/// <param name="cartStore">The carts whose lines are excluded unless <see cref="ItemSearchCriteria.IncludeItemsInCart"/> is set.</param>
/// <param name="serverClock">The clock that gives the server date against which tender closure is judged.</param>
public sealed class ItemSearch(
    IErewhonDbContext db,
    ICompanyContext companyContext,
    ICartStore cartStore,
    IServerClock serverClock)
{
    private readonly IErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ICompanyContext _companyContext = companyContext ?? throw new ArgumentNullException(nameof(companyContext));
    private readonly ICartStore _cartStore = cartStore ?? throw new ArgumentNullException(nameof(cartStore));
    private readonly IServerClock _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));

    /// <summary>
    /// Returns the bound company's items, in code-number order, that match <paramref name="criteria"/>, and stores their
    /// row ids in <see cref="ItemSearchCriteria.ResultItemIds"/>. Each returned item carries its category, its region
    /// with the region's country, its agent, its sale with the sale's client and, for a tender item, its tenders.
    /// When <see cref="ItemSearchCriteria.PriceLow"/> and <see cref="ItemSearchCriteria.PriceHigh"/> are both non-zero
    /// and the low price is above the high price, it throws before the previous results are cleared or any data is read.
    /// Otherwise it clears the previous results, then reads the bound company, the server date and the cart of
    /// <paramref name="sessionId"/> when <see cref="ItemSearchCriteria.IncludeItemsInCart"/> is
    /// <see langword="false"/>. One query then reads the company's items that pass every check of
    /// <see cref="ItemMatchesSearch"/> except the cart check and, when <see cref="ItemSearchCriteria.CountryId"/> is
    /// set, also those with no region that pass the closed-tender, sold, agent and client checks; each comes with its
    /// category, its region and the region's country, its agent, and its sale with the sale's client. It keeps each of
    /// those items, in code-number order, for which <see cref="ItemMatchesSearch"/> returns <see langword="true"/>.
    /// When it keeps a tender item, one further query reads the tenders of the kept tender items; each tender item
    /// then carries its tenders in date-tendered order, highest offer first at the same time, and a tender sale
    /// carries its winning tender.
    /// </summary>
    /// <param name="criteria">The criteria to apply; its <see cref="ItemSearchCriteria.ResultItemIds"/> is replaced.</param>
    /// <param name="sessionId">
    /// The id of the session whose cart lines are excluded; <see langword="null"/> when the search has no session cart.
    /// </param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The matching items, in code-number order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="criteria"/> is <see langword="null"/>.</exception>
    /// <exception cref="ErewhonException">
    /// Error <see cref="ErewhonException.MinHigherThanMax"/>: both prices are non-zero and the low price is above the high price.
    /// </exception>
    /// <exception cref="NoCompanyException">
    /// No company is bound, or <see cref="ItemSearchCriteria.CountryId"/> is set and an item reaching the country check
    /// has no region.
    /// </exception>
    public async Task<IReadOnlyList<Item>> DoSearchAsync(
        ItemSearchCriteria criteria,
        string? sessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (!JadeValues.IsNull(criteria.PriceLow)
            && !JadeValues.IsNull(criteria.PriceHigh)
            && criteria.PriceLow > criteria.PriceHigh)
        {
            throw new ErewhonException(ErewhonException.MinHigherThanMax);
        }

        criteria.Clear();

        var company = await _companyContext.GetCurrentAsync(ct);
        var serverDate = await _serverClock.GetServerDateAsync(ct);
        var cart = !criteria.IncludeItemsInCart && sessionId is not null
            ? _cartStore.GetCart(sessionId)
            : null;

        var candidates = WhereCriteriaCanMatch(
            _db.Items
                .AsNoTrackingWithIdentityResolution()
                .Where(i => i.CompanyId == company.Id),
            criteria,
            serverDate);

        var items = await candidates
            .Include(i => i.ItemCategory)
            .Include(i => i.Region).ThenInclude(r => r!.Country)
            .Include(i => i.Agent)
            .Include(i => i.Sale).ThenInclude(s => s!.Client)
            .OrderBy(i => i.CodeNumber)
            .ToListAsync(ct);

        var matches = new List<Item>(items.Count);
        foreach (var item in items)
        {
            if (ItemMatchesSearch(criteria, item, serverDate, cart))
            {
                matches.Add(item);
            }
        }

        await AttachTendersAsync(matches, ct);

        criteria.SetResults(matches.ConvertAll(i => i.Id));
        return matches.AsReadOnly();
    }

    /// <summary>
    /// Narrows <paramref name="items"/> to the items that can pass every check of <see cref="ItemMatchesSearch"/>
    /// except the cart check. The closed-tender, sold, agent and client checks apply to every item. With
    /// <see cref="ItemSearchCriteria.CountryId"/> set, an item with no region is kept whatever the country, region,
    /// category, item-kind and price checks give; every other item must pass them.
    /// </summary>
    /// <param name="items">The items to narrow.</param>
    /// <param name="criteria">The criteria to apply.</param>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns>The query over the items that can match.</returns>
    private static IQueryable<Item> WhereCriteriaCanMatch(
        IQueryable<Item> items,
        ItemSearchCriteria criteria,
        DateOnly serverDate)
    {
        if (!criteria.IncludeClosedTenders)
        {
            items = items.Where(i => !(i is TenderItem) || ((TenderItem)i).ClosureDate >= serverDate);
        }

        if (!criteria.IncludeSoldItems)
        {
            items = items.Where(i => i.Sale == null);
        }

        if (criteria.AgentId is long agentId)
        {
            items = items.Where(i => i.AgentId == agentId);
        }

        if (criteria.ClientId is long clientId)
        {
            items = items.Where(i => i.Sale != null && i.Sale.ClientId == clientId);
        }

        var keepItemsWithoutRegion = criteria.CountryId is not null;

        if (criteria.CountryId is long countryId)
        {
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => i.Region!.CountryId == countryId);
        }

        if (criteria.RegionId is long regionId)
        {
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => i.RegionId == regionId);
        }

        if (criteria.ItemCategoryId is long itemCategoryId)
        {
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => i.ItemCategoryId == itemCategoryId);
        }

        if (criteria.OnlyRetailItems)
        {
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => !(i is TenderItem));
        }

        if (criteria.OnlyTenderItems)
        {
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => !(i is RetailItem));
        }

        if (!JadeValues.IsNull(criteria.PriceLow))
        {
            var priceLow = criteria.PriceLow;
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => i.Price >= priceLow);
        }

        if (!JadeValues.IsNull(criteria.PriceHigh))
        {
            var priceHigh = criteria.PriceHigh;
            items = WhereOrWithoutRegion(items, keepItemsWithoutRegion, i => i.Price <= priceHigh);
        }

        return items;
    }

    /// <summary>
    /// Narrows <paramref name="items"/> to the items that satisfy <paramref name="predicate"/> or, when
    /// <paramref name="orWithoutRegion"/> is <see langword="true"/>, have no region.
    /// </summary>
    /// <param name="items">The items to narrow.</param>
    /// <param name="orWithoutRegion">Keeps the items with no region as well.</param>
    /// <param name="predicate">The condition an item must satisfy.</param>
    /// <returns>The narrowed query.</returns>
    private static IQueryable<Item> WhereOrWithoutRegion(
        IQueryable<Item> items,
        bool orWithoutRegion,
        Expression<Func<Item, bool>> predicate)
    {
        if (!orWithoutRegion)
        {
            return items.Where(predicate);
        }

        var item = predicate.Parameters[0];
        var withoutRegion = Expression.Equal(
            Expression.Property(item, nameof(Item.RegionId)),
            Expression.Constant(null, typeof(long?)));

        return items.Where(Expression.Lambda<Func<Item, bool>>(Expression.OrElse(withoutRegion, predicate.Body), item));
    }

    /// <summary>
    /// Reads, in one query, the tenders of the tender items in <paramref name="items"/> and attaches them: each tender
    /// item's <see cref="TenderItem.Tenders"/> becomes a new list of its tenders in date-tendered order, highest offer
    /// first at the same time, and each tender's <see cref="Tender.TenderItem"/> is set to its item. When a tender
    /// item's sale is a <see cref="TenderSale"/> whose <see cref="TenderSale.TenderId"/> is one of the tenders read,
    /// the sale's <see cref="TenderSale.Tender"/> and that tender's <see cref="Tender.TenderSale"/> are set to each
    /// other. Reads nothing when <paramref name="items"/> holds no tender item.
    /// </summary>
    /// <param name="items">The items whose tender items receive their tenders.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>A task that completes when the tenders are attached.</returns>
    private async Task AttachTendersAsync(List<Item> items, CancellationToken ct)
    {
        var tenderItems = new Dictionary<long, TenderItem>();
        foreach (var item in items)
        {
            if (item is TenderItem tenderItem)
            {
                tenderItems.Add(tenderItem.Id, tenderItem);
            }
        }

        if (tenderItems.Count == 0)
        {
            return;
        }

        var tenderItemIds = tenderItems.Keys.ToList();
        var tenders = await _db.Tenders
            .AsNoTracking()
            .Where(t => tenderItemIds.Contains(t.TenderItemId))
            .OrderBy(t => t.DateTendered).ThenByDescending(t => t.Offer)
            .ToListAsync(ct);

        foreach (var tenderItem in tenderItems.Values)
        {
            tenderItem.Tenders = new List<Tender>();
        }

        var tendersById = new Dictionary<long, Tender>(tenders.Count);
        foreach (var tender in tenders)
        {
            var tenderItem = tenderItems[tender.TenderItemId];
            tender.TenderItem = tenderItem;
            tenderItem.Tenders.Add(tender);
            tendersById.Add(tender.Id, tender);
        }

        foreach (var tenderItem in tenderItems.Values)
        {
            if (tenderItem.Sale is TenderSale { TenderId: long winningTenderId } tenderSale
                && tendersById.TryGetValue(winningTenderId, out var winningTender))
            {
                tenderSale.Tender = winningTender;
                winningTender.TenderSale = tenderSale;
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="item"/> matches <paramref name="criteria"/>. The checks run in this order and the
    /// first that fails returns <see langword="false"/>:
    /// <list type="number">
    /// <item><description>a tender item closed at <paramref name="serverDate"/> fails unless
    /// <see cref="ItemSearchCriteria.IncludeClosedTenders"/> is set;</description></item>
    /// <item><description>an item with a line in <paramref name="cart"/> fails unless
    /// <see cref="ItemSearchCriteria.IncludeItemsInCart"/> is set;</description></item>
    /// <item><description>a sold item fails unless <see cref="ItemSearchCriteria.IncludeSoldItems"/> is set;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.AgentId"/> set, an item with another agent or no agent fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.ClientId"/> set, an unsold item, or an item sold to another
    /// client, fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.CountryId"/> set, an item whose region is in another country
    /// fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.RegionId"/> set, an item in another region or no region fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.ItemCategoryId"/> set, an item in another category fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.OnlyRetailItems"/> set, a tender item fails;</description></item>
    /// <item><description>with <see cref="ItemSearchCriteria.OnlyTenderItems"/> set, a retail item fails;</description></item>
    /// <item><description>with a non-zero <see cref="ItemSearchCriteria.PriceLow"/>, an item priced below it fails;</description></item>
    /// <item><description>with a non-zero <see cref="ItemSearchCriteria.PriceHigh"/>, an item priced above it fails.</description></item>
    /// </list>
    /// An item that passes every check matches; a price equal to a bound passes that bound.
    /// </summary>
    /// <param name="criteria">The criteria to apply.</param>
    /// <param name="item">
    /// The item to test, with its <see cref="Item.Sale"/> loaded and, when <see cref="ItemSearchCriteria.CountryId"/> is
    /// set, its <see cref="Item.Region"/>.
    /// </param>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <param name="cart">The cart whose lines are excluded; <see langword="null"/> for no cart lines.</param>
    /// <returns><see langword="true"/> when <paramref name="item"/> matches; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="criteria"/> or <paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="NoCompanyException">
    /// <see cref="ItemSearchCriteria.CountryId"/> is set, <paramref name="item"/> passes the checks before the country
    /// check and has no region.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ItemSearchCriteria.CountryId"/> is set, <paramref name="item"/> passes the checks before the country
    /// check, and its <see cref="Item.RegionId"/> is set but its <see cref="Item.Region"/> is not loaded.
    /// </exception>
    public static bool ItemMatchesSearch(
        ItemSearchCriteria criteria,
        Item item,
        DateOnly serverDate,
        ShoppingCart? cart)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(item);

        if (item is TenderItem tenderItem
            && tenderItem.IsClosed(serverDate)
            && !criteria.IncludeClosedTenders)
        {
            return false;
        }

        if (!criteria.IncludeItemsInCart
            && cart is not null
            && cart.Contains(item.Id))
        {
            return false;
        }

        if (!criteria.IncludeSoldItems
            && item.Sale is not null)
        {
            return false;
        }

        if (criteria.AgentId is not null
            && item.AgentId != criteria.AgentId)
        {
            return false;
        }

        if (criteria.ClientId is not null)
        {
            if (item.Sale is null)
            {
                return false;
            }

            if (item.Sale.ClientId != criteria.ClientId)
            {
                return false;
            }
        }

        if (criteria.CountryId is not null
            && RequireRegion(item).CountryId != criteria.CountryId)
        {
            return false;
        }

        if (criteria.RegionId is not null
            && item.RegionId != criteria.RegionId)
        {
            return false;
        }

        if (criteria.ItemCategoryId is not null
            && item.ItemCategoryId != criteria.ItemCategoryId)
        {
            return false;
        }

        if (criteria.OnlyRetailItems
            && item is TenderItem)
        {
            return false;
        }

        if (criteria.OnlyTenderItems
            && item is RetailItem)
        {
            return false;
        }

        if (!JadeValues.IsNull(criteria.PriceLow)
            && item.Price < criteria.PriceLow)
        {
            return false;
        }

        if (!JadeValues.IsNull(criteria.PriceHigh)
            && item.Price > criteria.PriceHigh)
        {
            return false;
        }

        return true;
    }

    /// <summary>Returns the region of <paramref name="item"/>.</summary>
    /// <param name="item">The item whose region is read.</param>
    /// <returns>The loaded <see cref="Item.Region"/>.</returns>
    /// <exception cref="NoCompanyException">
    /// <paramref name="item"/> has no region: its <see cref="Item.RegionId"/> is <see langword="null"/> or 0.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Item.RegionId"/> is set and <see cref="Item.Region"/> is not loaded.
    /// </exception>
    private static Region RequireRegion(Item item)
    {
        if (item.Region is not null)
        {
            return item.Region;
        }

        if (JadeValues.IsNull(item.RegionId))
        {
            throw new NoCompanyException();
        }

        throw new InvalidOperationException("Item.Region is not loaded.");
    }
}
