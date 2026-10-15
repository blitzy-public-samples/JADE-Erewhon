using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Search;

/// <summary>
/// The filters of a tender search: the tender statuses to include and, optionally, the agent of the tender item,
/// the client who placed the tender and the tender item. A new instance includes no status and filters on no agent,
/// client or item.
/// </summary>
public sealed class TenderSearchCriteria
{
    /// <summary>Whether tenders with status <see cref="TenderStatus.Closed"/> are included.</summary>
    public bool IncludeClosedTenders { get; set; }

    /// <summary>Whether tenders with status <see cref="TenderStatus.Open"/> are included.</summary>
    public bool IncludeOpenTenders { get; set; }

    /// <summary>Whether tenders with status <see cref="TenderStatus.Successful"/> are included.</summary>
    public bool IncludeSuccessfulTenders { get; set; }

    /// <summary>
    /// The row id of the agent whose tender items' tenders are included; <see langword="null"/> includes every agent.
    /// </summary>
    public long? AgentId { get; set; }

    /// <summary>
    /// The row id of the client whose tenders are included; <see langword="null"/> includes every client.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// The row id of the tender item whose tenders are included; <see langword="null"/> includes every item.
    /// </summary>
    public long? ItemId { get; set; }
}

/// <summary>
/// Searches the tenders of the bound company with a <see cref="TenderSearchCriteria"/>. It only reads.
/// </summary>
/// <param name="db">The database the tenders are read from.</param>
/// <param name="companyContext">The bound company whose tenders are searched.</param>
/// <param name="serverClock">The database server clock whose date decides each tender's status.</param>
public sealed class TenderSearch(IErewhonDbContext db, ICompanyContext companyContext, IServerClock serverClock)
{
    private readonly IErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly ICompanyContext _companyContext =
        companyContext ?? throw new ArgumentNullException(nameof(companyContext));

    private readonly IServerClock _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));

    /// <summary>
    /// Returns the bound company's tenders that match <paramref name="criteria"/>, ordered by the time placed ascending
    /// and, within one time, by offer descending. The status of every tender is decided against one reading of the
    /// server date. Three queries read the result, each returning its entities untracked:
    /// <list type="number">
    /// <item><description>the bound company's tenders that pass the agent, client, item and status checks of
    /// <see cref="TenderMatchesSearch"/> at the server date, with each tender's client and tender sale, in result
    /// order;</description></item>
    /// <item><description>the distinct tender items of those tenders, one row each, with the item's category, its
    /// region with the region's country, its agent and its sale with the sale's client;</description></item>
    /// <item><description>every tender on those tender items, with each tender's client.</description></item>
    /// </list>
    /// Each tender then holds the one instance of its tender item, whose <see cref="TenderItem.Tenders"/> is a new list
    /// of every tender on the item ordered by time placed ascending and offer descending: the result's own instance
    /// for a tender in the result, otherwise the third query's instance, with its client. When the item's sale is a
    /// <see cref="TenderSale"/> whose <see cref="TenderSale.TenderId"/> is one of those tenders, that tender's
    /// <see cref="Tender.TenderSale"/> is the item's sale instance and the sale's <see cref="TenderSale.Tender"/> is
    /// that tender; a tender read with the item's sale that the sale no longer names holds no tender sale.
    /// <see cref="TenderMatchesSearch"/> then tests each tender of the first query in order, and only the tenders it
    /// matches are returned; a tender whose tender item is not read is left out.
    /// </summary>
    /// <param name="criteria">The filters to apply.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The matching tenders in order; empty when none match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="criteria"/> is <see langword="null"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    public async Task<IReadOnlyList<Tender>> DoSearchAsync(TenderSearchCriteria criteria, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var company = await _companyContext.GetCurrentAsync(ct);
        var serverDate = await _serverClock.GetServerDateAsync(ct);

        var tenders = await FilterTenders(criteria, company.Id, serverDate)
            .Include(t => t.Client)
            .Include(t => t.TenderSale)
            .OrderBy(t => t.DateTendered).ThenByDescending(t => t.Offer)
            .ToListAsync(ct);

        if (tenders.Count == 0)
        {
            return tenders;
        }

        var tenderItemIds = tenders.Select(t => t.TenderItemId).Distinct().ToList();

        var tenderItems = await _db.Items
            .OfType<TenderItem>()
            .AsNoTrackingWithIdentityResolution()
            .Where(ti => tenderItemIds.Contains(ti.Id))
            .Include(ti => ti.ItemCategory)
            .Include(ti => ti.Region).ThenInclude(r => r!.Country)
            .Include(ti => ti.Agent)
            .Include(ti => ti.Sale).ThenInclude(s => s!.Client)
            .ToListAsync(ct);

        var itemTenders = await _db.Tenders
            .AsNoTracking()
            .Where(t => tenderItemIds.Contains(t.TenderItemId))
            .Include(t => t.Client)
            .ToListAsync(ct);

        AttachTenderItems(tenders, tenderItems, itemTenders);

        return tenders.Where(t => t.TenderItem is not null && TenderMatchesSearch(criteria, t, serverDate)).ToList();
    }

    /// <summary>
    /// Returns the untracked, identity-resolving query of the tenders of the company with row id
    /// <paramref name="companyId"/> that pass the agent, client, item and status checks of
    /// <see cref="TenderMatchesSearch"/> at <paramref name="serverDate"/>, the status being
    /// <see cref="TenderStatus.Successful"/> for a tender with a tender sale, otherwise
    /// <see cref="TenderStatus.Closed"/> for a tender whose tender item's closure date is before
    /// <paramref name="serverDate"/>, otherwise <see cref="TenderStatus.Open"/>. The query selects no column of the
    /// tender item.
    /// </summary>
    /// <param name="criteria">The filters to apply.</param>
    /// <param name="companyId">The row id of the bound company.</param>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns>The filtered query, unordered and without navigations.</returns>
    private IQueryable<Tender> FilterTenders(TenderSearchCriteria criteria, long companyId, DateOnly serverDate)
    {
        var query = _db.Tenders
            .AsNoTrackingWithIdentityResolution()
            .Where(t => t.CompanyId == companyId);

        if (criteria.AgentId is long agentId)
        {
            query = query.Where(t => t.TenderItem!.AgentId == agentId);
        }

        if (criteria.ClientId is long clientId)
        {
            query = query.Where(t => t.ClientId == clientId);
        }

        if (criteria.ItemId is long itemId)
        {
            query = query.Where(t => t.TenderItemId == itemId);
        }

        var includeOpen = criteria.IncludeOpenTenders;
        var includeClosed = criteria.IncludeClosedTenders;
        var includeSuccessful = criteria.IncludeSuccessfulTenders;

        return query.Where(t => t.TenderSale != null
            ? includeSuccessful
            : t.TenderItem!.ClosureDate < serverDate
                ? includeClosed
                : includeOpen);
    }

    /// <summary>
    /// Sets <see cref="Tender.TenderItem"/> of each tender in <paramref name="tenders"/> to its instance in
    /// <paramref name="tenderItems"/>, and sets <see cref="TenderItem.Tenders"/> of each tender item to a new list of
    /// its tenders ordered by time placed ascending and offer descending: each tender of <paramref name="tenders"/> on
    /// the item, and each tender of <paramref name="itemTenders"/> on the item whose id is not in
    /// <paramref name="tenders"/>; every tender in that list holds the tender item. For a tender item whose sale is a
    /// <see cref="TenderSale"/>, the listed tender whose id is the sale's <see cref="TenderSale.TenderId"/> holds the
    /// sale as its <see cref="Tender.TenderSale"/> and the sale holds it as its <see cref="TenderSale.Tender"/>; a
    /// listed tender whose loaded tender sale has the id of the item's sale but is not named by it holds none. A
    /// tender whose tender item is not in <paramref name="tenderItems"/> is left unchanged.
    /// </summary>
    /// <param name="tenders">The tenders of the result, read with their tender sales.</param>
    /// <param name="tenderItems">The tender items of those tenders, read with their sales.</param>
    /// <param name="itemTenders">Every tender on those tender items, read with their clients.</param>
    private static void AttachTenderItems(
        IReadOnlyList<Tender> tenders,
        IReadOnlyList<TenderItem> tenderItems,
        IReadOnlyList<Tender> itemTenders)
    {
        var tendersByItemId = new Dictionary<long, List<Tender>>(tenderItems.Count);
        foreach (var tenderItem in tenderItems)
        {
            tendersByItemId[tenderItem.Id] = new List<Tender>();
        }

        var resultTenderIds = new HashSet<long>(tenders.Count);
        foreach (var tender in tenders)
        {
            resultTenderIds.Add(tender.Id);
            if (tendersByItemId.TryGetValue(tender.TenderItemId, out var itemList))
            {
                itemList.Add(tender);
            }
        }

        foreach (var tender in itemTenders)
        {
            if (!resultTenderIds.Contains(tender.Id)
                && tendersByItemId.TryGetValue(tender.TenderItemId, out var itemList))
            {
                itemList.Add(tender);
            }
        }

        foreach (var tenderItem in tenderItems)
        {
            var ordered = tendersByItemId[tenderItem.Id]
                .OrderBy(t => t.DateTendered)
                .ThenByDescending(t => t.Offer)
                .ToList();

            var itemSale = tenderItem.Sale;
            foreach (var tender in ordered)
            {
                tender.TenderItem = tenderItem;

                if (itemSale is TenderSale itemTenderSale && itemTenderSale.TenderId == tender.Id)
                {
                    tender.TenderSale = itemTenderSale;
                    itemTenderSale.Tender = tender;
                }
                else if (itemSale is not null && tender.TenderSale is not null && tender.TenderSale.Id == itemSale.Id)
                {
                    tender.TenderSale = null;
                }
            }

            tenderItem.Tenders = ordered;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="tender"/> matches <paramref name="criteria"/>. Checked in order, the first
    /// failing check returning <see langword="false"/>:
    /// <list type="number">
    /// <item><description>with <see cref="TenderSearchCriteria.AgentId"/> set, the tender item's agent is that agent
    /// (an item without an agent does not match);</description></item>
    /// <item><description>with <see cref="TenderSearchCriteria.ClientId"/> set, the tender was placed by that
    /// client;</description></item>
    /// <item><description>with <see cref="TenderSearchCriteria.ItemId"/> set, the tender is on that tender
    /// item;</description></item>
    /// <item><description>the status from <see cref="Tender.GetStatus(DateOnly)"/> at <paramref name="serverDate"/> is
    /// <see cref="TenderStatus.Open"/> only with <see cref="TenderSearchCriteria.IncludeOpenTenders"/>,
    /// <see cref="TenderStatus.Closed"/> only with <see cref="TenderSearchCriteria.IncludeClosedTenders"/> and
    /// <see cref="TenderStatus.Successful"/> only with
    /// <see cref="TenderSearchCriteria.IncludeSuccessfulTenders"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="criteria">The filters to apply.</param>
    /// <param name="tender">
    /// The tender to test; its <see cref="Tender.TenderItem"/> must be loaded when an agent filter is set or the tender
    /// has no <see cref="Tender.TenderSale"/>.
    /// </param>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns><see langword="true"/> when the tender matches; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="criteria"/> or <paramref name="tender"/> is <see langword="null"/>.
    /// </exception>
    public static bool TenderMatchesSearch(TenderSearchCriteria criteria, Tender tender, DateOnly serverDate)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(tender);

        if (criteria.AgentId is not null && tender.TenderItem!.AgentId != criteria.AgentId)
        {
            return false;
        }

        if (criteria.ClientId is not null && tender.ClientId != criteria.ClientId)
        {
            return false;
        }

        if (criteria.ItemId is not null && tender.TenderItemId != criteria.ItemId)
        {
            return false;
        }

        var status = tender.GetStatus(serverDate);

        if (!criteria.IncludeOpenTenders && status == TenderStatus.Open)
        {
            return false;
        }

        if (!criteria.IncludeClosedTenders && status == TenderStatus.Closed)
        {
            return false;
        }

        if (!criteria.IncludeSuccessfulTenders && status == TenderStatus.Successful)
        {
            return false;
        }

        return true;
    }
}
