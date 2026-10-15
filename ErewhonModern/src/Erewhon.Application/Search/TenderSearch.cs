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
    /// and, within one time, by offer descending. The tenders are read in one query and returned untracked with their
    /// client, their tender sale and their tender item; the tender item carries its category, its region with the
    /// region's country, its agent, its sale with the sale's client, and its tenders. The status of every tender is
    /// decided against one reading of the server date.
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

        var tenders = await _db.Tenders
            .AsNoTrackingWithIdentityResolution()
            .Where(t => t.CompanyId == company.Id)
            .Include(t => t.Client)
            .Include(t => t.TenderSale)
            .Include(t => t.TenderItem).ThenInclude(ti => ti!.ItemCategory)
            .Include(t => t.TenderItem).ThenInclude(ti => ti!.Region).ThenInclude(r => r!.Country)
            .Include(t => t.TenderItem).ThenInclude(ti => ti!.Agent)
            .Include(t => t.TenderItem).ThenInclude(ti => ti!.Sale).ThenInclude(s => s!.Client)
            .Include(t => t.TenderItem).ThenInclude(ti => ti!.Tenders)
            .OrderBy(t => t.DateTendered).ThenByDescending(t => t.Offer)
            .ToListAsync(ct);

        return tenders.Where(t => TenderMatchesSearch(criteria, t, serverDate)).ToList();
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
