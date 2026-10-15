using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Persistence;

/// <summary>
/// The Entity Framework Core context over the Erewhon PostgreSQL database: one entity set per table
/// (<c>companies</c>, <c>agents</c>, <c>clients</c>, <c>countries</c>, <c>regions</c>, <c>item_categories</c>,
/// <c>commission_rates</c>, <c>agent_commission_rates</c>, <c>items</c>, <c>sales</c> and <c>tenders</c>), with
/// the table, column, key, index, discriminator, owned-address and delete mappings applied from the
/// <c>Erewhon.Persistence.Configurations</c> classes of this assembly. When created with a
/// <see cref="ChangeNotificationInterceptor"/>, every save of the context passes through that interceptor.
/// </summary>
public sealed class ErewhonDbContext : DbContext, IErewhonDbContext
{
    private readonly ChangeNotificationInterceptor? _changeNotificationInterceptor;

    /// <summary>
    /// Creates a context with the given options and no change-notification interceptor.
    /// </summary>
    /// <param name="options">The provider and connection options of the context.</param>
    public ErewhonDbContext(DbContextOptions<ErewhonDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Creates a context with the given options whose saves pass through
    /// <paramref name="changeNotificationInterceptor"/>.
    /// </summary>
    /// <param name="options">The provider and connection options of the context.</param>
    /// <param name="changeNotificationInterceptor">The interceptor attached to every save of this context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changeNotificationInterceptor"/> is null.</exception>
    public ErewhonDbContext(
        DbContextOptions<ErewhonDbContext> options,
        ChangeNotificationInterceptor changeNotificationInterceptor)
        : base(options)
    {
        _changeNotificationInterceptor = changeNotificationInterceptor
            ?? throw new ArgumentNullException(nameof(changeNotificationInterceptor));
    }

    /// <summary>The companies, stored in the <c>companies</c> table.</summary>
    public DbSet<Company> Companies => Set<Company>();

    /// <summary>The agents of every company, stored in the <c>agents</c> table.</summary>
    public DbSet<Agent> Agents => Set<Agent>();

    /// <summary>The clients of every company, stored in the <c>clients</c> table.</summary>
    public DbSet<Client> Clients => Set<Client>();

    /// <summary>The countries of every company, stored in the <c>countries</c> table.</summary>
    public DbSet<Country> Countries => Set<Country>();

    /// <summary>The regions of every country, stored in the <c>regions</c> table.</summary>
    public DbSet<Region> Regions => Set<Region>();

    /// <summary>The item categories of every company, stored in the <c>item_categories</c> table.</summary>
    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();

    /// <summary>The commission rates of every item category, stored in the <c>commission_rates</c> table.</summary>
    public DbSet<CommissionRate> CommissionRates => Set<CommissionRate>();

    /// <summary>
    /// The links between agents and the commission rates they hold, stored in the <c>agent_commission_rates</c> table.
    /// </summary>
    public DbSet<AgentCommissionRate> AgentCommissionRates => Set<AgentCommissionRate>();

    /// <summary>
    /// The retail and tender items of every company, stored in the <c>items</c> table and told apart by its
    /// <c>item_type</c> column.
    /// </summary>
    public DbSet<Item> Items => Set<Item>();

    /// <summary>
    /// The retail and tender sales of every company, stored in the <c>sales</c> table and told apart by its
    /// <c>sale_type</c> column.
    /// </summary>
    public DbSet<Sale> Sales => Set<Sale>();

    /// <summary>The tenders placed on the tender items of every company, stored in the <c>tenders</c> table.</summary>
    public DbSet<Tender> Tenders => Set<Tender>();

    /// <summary>
    /// Attaches the change-notification interceptor this context was created with, if any, to its options.
    /// </summary>
    /// <param name="optionsBuilder">The builder of this context's options.</param>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (_changeNotificationInterceptor is not null)
        {
            optionsBuilder.AddInterceptors(_changeNotificationInterceptor);
        }
    }

    /// <summary>
    /// Builds the model from every <see cref="IEntityTypeConfiguration{TEntity}"/> class in this assembly.
    /// </summary>
    /// <param name="modelBuilder">The builder of this context's model.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ErewhonDbContext).Assembly);
    }
}
