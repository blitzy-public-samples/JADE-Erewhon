using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The company bound to one dependency-injection scope. After <see cref="Bind(long)"/> it is the company with
/// the bound id; otherwise it is the company with the lowest id, whose id is kept for the rest of the scope while
/// that company exists. Every access reads <see cref="IErewhonDbContext.Companies"/> with change tracking and
/// returns the tracked <see cref="Company"/> entity. A company whose row no longer exists, or whose tracked entry
/// is <see cref="EntityState.Deleted"/>, counts as absent, and an absent result is never kept.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class CompanyContext : ICompanyContext
{
    private readonly IErewhonDbContext _db;
    private long? _boundId;
    private long? _resolvedId;

    /// <summary>
    /// Creates a context with no bound company id that reads companies through <paramref name="db"/>.
    /// </summary>
    /// <param name="db">The database context of the current scope.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    public CompanyContext(IErewhonDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// The bound company, or the company with the lowest id when no id is bound, read with synchronous queries.
    /// </summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public Company Current => Find() ?? throw new NoCompanyException();

    /// <summary>
    /// The internal id of <see cref="Current"/>.
    /// </summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public long CurrentId => Current.Id;

    /// <summary>
    /// Returns the bound company, or the company with the lowest id when no id is bound.
    /// </summary>
    /// <param name="ct">Cancels the company read.</param>
    /// <returns>The tracked company entity.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<Company> GetCurrentAsync(CancellationToken ct = default) =>
        await FindCurrentAsync(ct) ?? throw new NoCompanyException();

    /// <summary>
    /// Returns the bound company, or the company with the lowest id when no id is bound, or
    /// <see langword="null"/> when that company does not exist.
    /// </summary>
    /// <param name="ct">Cancels the company read.</param>
    /// <returns>The tracked company entity, or <see langword="null"/>.</returns>
    public async Task<Company?> FindCurrentAsync(CancellationToken ct = default)
    {
        if (_boundId is long boundId)
        {
            return Live(await ById(boundId).FirstOrDefaultAsync(ct));
        }

        if (_resolvedId is long resolvedId)
        {
            var resolved = Live(await ById(resolvedId).FirstOrDefaultAsync(ct));
            if (resolved is not null)
            {
                return resolved;
            }

            _resolvedId = null;
        }

        long? afterId = null;
        while (true)
        {
            var candidate = await LowestAfter(afterId).FirstOrDefaultAsync(ct);
            if (candidate is null)
            {
                return null;
            }

            if (Live(candidate) is not null)
            {
                _resolvedId = candidate.Id;
                return candidate;
            }

            afterId = candidate.Id;
        }
    }

    /// <summary>
    /// Records <paramref name="companyId"/> as the bound company id of this scope, without reading the database.
    /// Every later access returns the company with that id, and finds no company when no company has that id.
    /// </summary>
    /// <param name="companyId">The internal id (<c>companies.id</c>) of the company to bind.</param>
    public void Bind(long companyId)
    {
        _boundId = companyId;
        _resolvedId = null;
    }

    private Company? Find()
    {
        if (_boundId is long boundId)
        {
            return Live(ById(boundId).FirstOrDefault());
        }

        if (_resolvedId is long resolvedId)
        {
            var resolved = Live(ById(resolvedId).FirstOrDefault());
            if (resolved is not null)
            {
                return resolved;
            }

            _resolvedId = null;
        }

        long? afterId = null;
        while (true)
        {
            var candidate = LowestAfter(afterId).FirstOrDefault();
            if (candidate is null)
            {
                return null;
            }

            if (Live(candidate) is not null)
            {
                _resolvedId = candidate.Id;
                return candidate;
            }

            afterId = candidate.Id;
        }
    }

    private IQueryable<Company> ById(long companyId) =>
        _db.Companies.Where(company => company.Id == companyId);

    private IQueryable<Company> LowestAfter(long? afterId)
    {
        IQueryable<Company> companies = _db.Companies;
        if (afterId is long lowerBound)
        {
            companies = companies.Where(company => company.Id > lowerBound);
        }

        return companies.OrderBy(company => company.Id);
    }

    private Company? Live(Company? company) =>
        company is not null && _db.Entry(company).State != EntityState.Deleted ? company : null;
}
