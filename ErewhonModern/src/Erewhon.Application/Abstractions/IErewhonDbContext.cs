using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erewhon.Application.Abstractions;

/// <summary>
/// Reads and writes the Erewhon database: one entity set per table, saving of tracked changes,
/// and access to the change tracker of the current unit of work.
/// </summary>
public interface IErewhonDbContext
{
    /// <summary>The companies, stored in the <c>companies</c> table.</summary>
    DbSet<Company> Companies { get; }

    /// <summary>The agents of every company, stored in the <c>agents</c> table.</summary>
    DbSet<Agent> Agents { get; }

    /// <summary>The clients of every company, stored in the <c>clients</c> table.</summary>
    DbSet<Client> Clients { get; }

    /// <summary>The countries of every company, stored in the <c>countries</c> table.</summary>
    DbSet<Country> Countries { get; }

    /// <summary>The regions of every country, stored in the <c>regions</c> table.</summary>
    DbSet<Region> Regions { get; }

    /// <summary>The item categories of every company, stored in the <c>item_categories</c> table.</summary>
    DbSet<ItemCategory> ItemCategories { get; }

    /// <summary>The commission rates of every item category, stored in the <c>commission_rates</c> table.</summary>
    DbSet<CommissionRate> CommissionRates { get; }

    /// <summary>
    /// The links between agents and the commission rates they hold, stored in the
    /// <c>agent_commission_rates</c> table.
    /// </summary>
    DbSet<AgentCommissionRate> AgentCommissionRates { get; }

    /// <summary>
    /// The items of every company, stored in the <c>items</c> table. The set holds both
    /// <see cref="RetailItem"/> rows (<c>item_type</c> <c>R</c>) and <see cref="TenderItem"/> rows
    /// (<c>item_type</c> <c>T</c>); <c>Items.OfType&lt;RetailItem&gt;()</c> and
    /// <c>Items.OfType&lt;TenderItem&gt;()</c> select one kind.
    /// </summary>
    DbSet<Item> Items { get; }

    /// <summary>
    /// The sales of every company, stored in the <c>sales</c> table. The set holds both
    /// <see cref="RetailSale"/> rows (<c>sale_type</c> <c>R</c>) and <see cref="TenderSale"/> rows
    /// (<c>sale_type</c> <c>T</c>); <c>Sales.OfType&lt;RetailSale&gt;()</c> and
    /// <c>Sales.OfType&lt;TenderSale&gt;()</c> select one kind.
    /// </summary>
    DbSet<Sale> Sales { get; }

    /// <summary>The tenders placed on the tender items of every company, stored in the <c>tenders</c> table.</summary>
    DbSet<Tender> Tenders { get; }

    /// <summary>
    /// Writes every added, modified and deleted tracked entity to the database.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the save.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the change-tracking entry for <paramref name="entity"/>, through which its state is read
    /// or set, for example to <see cref="EntityState.Detached"/>.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="entity">The entity whose entry is returned.</param>
    /// <returns>The entry for <paramref name="entity"/>.</returns>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>
    /// The change tracker of this context, which lists the tracked entities and their states;
    /// callers use it to detach the entities of an aborted operation.
    /// </summary>
    ChangeTracker ChangeTracker { get; }
}
