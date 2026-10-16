using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Search;
using Erewhon.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erewhon.Application;

/// <summary>
/// Registers the Application layer's services, cart store and message catalogue.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the Application layer's services, cart store and message catalogue:
    /// <see cref="MessageCatalog.Default"/> and <see cref="InMemoryCartStore"/> as <see cref="ICartStore"/> as
    /// singletons shared by every scope; <see cref="CompanyContext"/> as <see cref="ICompanyContext"/>, the
    /// notification queue and deriver, the item-code allocator, the transaction-agent services, the closure,
    /// soft-lock, self-documentation and logon services, the shopping-cart service and both searches as scoped
    /// services, one instance per scope. Each registration is skipped when its service type is already registered,
    /// so a second call changes nothing.
    /// </summary>
    /// <remarks>
    /// The ports <see cref="IErewhonDbContext"/>, <see cref="IUnitOfWork"/>, <see cref="IEntityLocks"/>,
    /// <see cref="IServerClock"/>, <see cref="INotificationPublisher"/> and <see cref="ISessionContext"/> are not
    /// registered here; the persistence layer and the host register them.
    /// </remarks>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddErewhonApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(MessageCatalog.Default);
        services.TryAddSingleton<ICartStore, InMemoryCartStore>();

        services.TryAddScoped<ICompanyContext, CompanyContext>();
        services.TryAddScoped<NotificationQueue>();
        services.TryAddScoped<NotificationDeriver>();
        services.TryAddScoped<ItemCodeAllocator>();

        services.TryAddScoped<AgentService>();
        services.TryAddScoped<ClientService>();
        services.TryAddScoped<CompanyService>();
        services.TryAddScoped<CommissionRateService>();
        services.TryAddScoped<ItemCategoryService>();
        services.TryAddScoped<ItemService>();
        services.TryAddScoped<CountryService>();
        services.TryAddScoped<RegionService>();
        services.TryAddScoped<RetailSaleService>();
        services.TryAddScoped<TenderSaleService>();
        services.TryAddScoped<TenderService>();

        services.TryAddScoped<TenderClosureService>();
        services.TryAddScoped<SoftLockService>();
        services.TryAddScoped<SelfDocumentationService>();
        services.TryAddScoped<LogonService>();

        services.TryAddScoped<ShoppingCartService>();
        services.TryAddScoped<ItemSearch>();
        services.TryAddScoped<TenderSearch>();

        return services;
    }
}
