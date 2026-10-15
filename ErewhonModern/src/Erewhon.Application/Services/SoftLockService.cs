using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The soft edit lock state of one row and the texts of the prompt an editor shows when it opens a locked row.
/// Ports the state <c>ErewhonBaseForm.displayObject</c> reads and the <c>$ThingCurrentlyEdited</c> message box it shows.
/// </summary>
/// <param name="EntityType">
/// The domain type name of the row: <c>Agent</c>, <c>Client</c>, <c>Company</c>, <c>RetailItem</c>,
/// <c>TenderItem</c>, <c>ItemCategory</c>, <c>CommissionRate</c>, <c>Country</c> or <c>Region</c>.
/// </param>
/// <param name="Id">The internal row id.</param>
/// <param name="IsLocked">
/// <see langword="true"/> when any session holds the row's soft edit lock, the caller's own session included.
/// </param>
/// <param name="IsLockedByCaller"><see langword="true"/> when the caller's session holds the row's soft edit lock.</param>
/// <param name="LockedBySession">The id of the session holding the lock; <see langword="null"/> when the row is unlocked.</param>
/// <param name="Thing">The name of the thing being edited, the <c>$Thing</c> text "thing".</param>
/// <param name="Prompt">The <c>$ThingCurrentlyEdited</c> text for <paramref name="Thing"/>, asked with Yes and No.</param>
/// <param name="Title">The <c>$ThingAlreadyBeingEdited</c> text for <paramref name="Thing"/>, the title of the prompt.</param>
public sealed record SoftLockStatus(
    string EntityType,
    long Id,
    bool IsLocked,
    bool IsLockedByCaller,
    string? LockedBySession,
    string Thing,
    string Prompt,
    string Title);

/// <summary>
/// Reads, sets and clears the soft edit lock of the rows the admin editors open, and sends the save request to the
/// session editing a locked row. A lock or unlock runs the row's own transaction-agent modify path, with sub-code
/// <see cref="TransactionType.ModifyLockEntity"/> (1001) or <see cref="TransactionType.ModifyUnlockEntity"/> (1002),
/// in its own transaction. Ports the soft lock of JADE <c>ErewhonBaseForm</c> (<c>displayObject</c>,
/// <c>lockEntity</c>, <c>formUnload</c>, <c>thing</c>) and <c>PersistentModel.isSoftLockedByMe</c>.
/// </summary>
/// <remarks>
/// The entity type is matched case-insensitively against <c>Agent</c>, <c>Client</c>, <c>Company</c>,
/// <c>RetailItem</c>, <c>TenderItem</c>, <c>ItemCategory</c>, <c>CommissionRate</c>, <c>Country</c> and
/// <c>Region</c>; <c>RetailItem</c> and <c>TenderItem</c> find only an item of that kind. An unknown entity type, or an
/// id that names no row of the type, makes every member return <see langword="null"/>. The lock owner is the session id.
/// No member checks the session's role. One instance serves one dependency-injection scope; its members are not safe
/// to call from several threads at once.
/// </remarks>
public sealed class SoftLockService
{
    private static readonly string[] EntityTypes =
    [
        nameof(Agent),
        nameof(Client),
        nameof(Company),
        nameof(RetailItem),
        nameof(TenderItem),
        nameof(ItemCategory),
        nameof(CommissionRate),
        nameof(Country),
        nameof(Region),
    ];

    private readonly ISessionContext _session;
    private readonly IServerClock _clock;
    private readonly INotificationPublisher _publisher;
    private readonly IErewhonDbContext _db;
    private readonly AgentService _agents;
    private readonly ClientService _clients;
    private readonly CompanyService _companies;
    private readonly ItemService _items;
    private readonly ItemCategoryService _categories;
    private readonly CommissionRateService _rates;
    private readonly CountryService _countries;
    private readonly RegionService _regions;

    /// <summary>
    /// Creates the service over the session, server clock, notification publisher and database of its scope and the
    /// transaction agents of the entity types it locks.
    /// </summary>
    /// <param name="session">The session bound to the scope, whose id is the lock owner.</param>
    /// <param name="clock">The database server clock that stamps a lock.</param>
    /// <param name="publisher">Sends the save request to the editing session.</param>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="agents">Runs the modify of an agent.</param>
    /// <param name="clients">Runs the modify of a client.</param>
    /// <param name="companies">Runs the modify of a company.</param>
    /// <param name="items">Runs the modify of a retail or tender item.</param>
    /// <param name="categories">Runs the modify of an item category.</param>
    /// <param name="rates">Runs the modify of a commission rate.</param>
    /// <param name="countries">Runs the modify of a country.</param>
    /// <param name="regions">Runs the modify of a region.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SoftLockService(
        ISessionContext session,
        IServerClock clock,
        INotificationPublisher publisher,
        IErewhonDbContext db,
        AgentService agents,
        ClientService clients,
        CompanyService companies,
        ItemService items,
        ItemCategoryService categories,
        CommissionRateService rates,
        CountryService countries,
        RegionService regions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(countries);
        ArgumentNullException.ThrowIfNull(regions);

        _session = session;
        _clock = clock;
        _publisher = publisher;
        _db = db;
        _agents = agents;
        _clients = clients;
        _companies = companies;
        _items = items;
        _categories = categories;
        _rates = rates;
        _countries = countries;
        _regions = regions;
    }

    /// <summary>
    /// Returns the soft edit lock state of the row and the prompt texts. <see cref="SoftLockStatus.IsLocked"/> is
    /// <see langword="true"/> whichever session holds the lock, the caller's own included; the prompt and title name
    /// the thing "thing" for every entity type. Ports the test and the message box of
    /// <c>ErewhonBaseForm.displayObject</c>.
    /// </summary>
    /// <param name="entityType">The domain type name of the row, compared case-insensitively.</param>
    /// <param name="id">The internal row id.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>
    /// The lock state, its <see cref="SoftLockStatus.EntityType"/> spelled as the domain type name; or
    /// <see langword="null"/> when the entity type is unknown or no such row exists.
    /// </returns>
    public async Task<SoftLockStatus?> GetStatusAsync(string entityType, long id, CancellationToken ct = default)
    {
        SoftLockTarget? target = await ResolveAsync(entityType, id, ct);
        if (target is null)
        {
            return null;
        }

        PersistentModel entity = target.Entity;
        string thing = MessageCatalog.Get(MessageKeys.Thing);

        return new SoftLockStatus(
            target.EntityType,
            entity.Id,
            entity.IsSoftLocked,
            IsLockedByCaller(entity),
            entity.LockedBySession,
            thing,
            MessageCatalog.Get(MessageKeys.ThingCurrentlyEdited, thing),
            MessageCatalog.Get(MessageKeys.ThingAlreadyBeingEdited, thing));
    }

    /// <summary>
    /// Sets the row's soft edit lock to the caller's session at the database server's current timestamp, whichever
    /// session held it before: populates the row's command from the row, sets its lock owner and lock time, and runs
    /// the row's transaction agent with <see cref="TransactionType.ModifyLockEntity"/> (1001) in
    /// <see cref="TransactionMode.Own"/> mode. That modify pre-validates and validates the populated values as the row's
    /// agent does for <see cref="ValidationType.Modify"/>, so a failed validation leaves the lock unchanged and its
    /// errors in the result. Ports <c>ErewhonBaseForm.lockEntity</c>.
    /// </summary>
    /// <param name="entityType">The domain type name of the row, compared case-insensitively.</param>
    /// <param name="id">The internal row id.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// The result of the modify, unchanged; or <see langword="null"/> when the entity type is unknown or no such row
    /// exists.
    /// </returns>
    /// <exception cref="InvalidOperationException">No session is bound to the scope.</exception>
    public async Task<OperationResult?> LockAsync(string entityType, long id, CancellationToken ct = default)
    {
        string sessionId = _session.HasSession && _session.SessionId is { } boundSessionId
            ? boundSessionId
            : throw new InvalidOperationException("A soft edit lock is owned by a session, and no session is bound to the scope.");

        SoftLockTarget? target = await ResolveAsync(entityType, id, ct);
        if (target is null)
        {
            return null;
        }

        DateTime lockedTimestamp = await _clock.GetServerTimeStampAsync(ct);
        return await target.ModifyAsync(sessionId, lockedTimestamp, TransactionType.ModifyLockEntity, ct);
    }

    /// <summary>
    /// Clears the row's soft edit lock when the caller's session holds it: populates the row's command from the row and
    /// runs the row's transaction agent with <see cref="TransactionType.ModifyUnlockEntity"/> (1002) in
    /// <see cref="TransactionMode.Own"/> mode. When the row is unlocked, or another session holds the lock, nothing
    /// changes and the result succeeds with no errors. Ports <c>ErewhonBaseForm.formUnload</c>.
    /// </summary>
    /// <param name="entityType">The domain type name of the row, compared case-insensitively.</param>
    /// <param name="id">The internal row id.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// The result of the modify, unchanged, or a succeeded result when no modify runs; <see langword="null"/> when the
    /// entity type is unknown or no such row exists.
    /// </returns>
    public async Task<OperationResult?> UnlockAsync(string entityType, long id, CancellationToken ct = default)
    {
        SoftLockTarget? target = await ResolveAsync(entityType, id, ct);
        if (target is null)
        {
            return null;
        }

        if (!IsLockedByCaller(target.Entity))
        {
            return new OperationResult { Succeeded = true };
        }

        return await target.ModifyAsync(null, null, TransactionType.ModifyUnlockEntity, ct);
    }

    /// <summary>
    /// Sends the <c>SaveOutstandingChanges</c> message, with the row's domain type name and id, to the session holding
    /// the row's soft edit lock, the caller's own session included; sends nothing when the row is unlocked. The message
    /// changes no data, and no editor acts on it. Ports the Yes answer of <c>ErewhonBaseForm.displayObject</c>, which
    /// causes <c>Erewhon_Event_SaveOutstandingChanges</c> on the row.
    /// </summary>
    /// <param name="entityType">The domain type name of the row, compared case-insensitively.</param>
    /// <param name="id">The internal row id.</param>
    /// <param name="ct">Token that cancels the read and the sending.</param>
    /// <returns>
    /// <see langword="true"/> when the message was sent; <see langword="false"/> when the row is unlocked;
    /// <see langword="null"/> when the entity type is unknown or no such row exists.
    /// </returns>
    public async Task<bool?> RequestSaveAsync(string entityType, long id, CancellationToken ct = default)
    {
        SoftLockTarget? target = await ResolveAsync(entityType, id, ct);
        if (target is null)
        {
            return null;
        }

        PersistentModel entity = target.Entity;
        if (!entity.IsSoftLocked)
        {
            return false;
        }

        await _publisher.PublishSaveOutstandingChangesAsync(entity.LockedBySession!, target.EntityType, entity.Id, ct);
        return true;
    }

    /// <summary>
    /// Returns whether a session is bound to the scope and holds the soft edit lock of <paramref name="entity"/>.
    /// Ports <c>PersistentModel.isSoftLockedByMe</c>.
    /// </summary>
    private bool IsLockedByCaller(PersistentModel entity) =>
        _session.HasSession
        && _session.SessionId is { } sessionId
        && entity.IsSoftLockedBy(sessionId);

    /// <summary>
    /// Reads the row of the matched entity type with change tracking, together with the navigations its command's
    /// <c>PopulateFromObject</c> reads, and pairs it with the modify of its transaction agent; returns
    /// <see langword="null"/> for an unknown entity type or a missing row.
    /// </summary>
    private async Task<SoftLockTarget?> ResolveAsync(string entityType, long id, CancellationToken ct)
    {
        string? name = Array.Find(
            EntityTypes,
            candidate => string.Equals(candidate, entityType, StringComparison.OrdinalIgnoreCase));

        return name switch
        {
            nameof(Agent) => CreateTarget<Agent, AgentCommand>(
                name,
                await _db.Agents.FirstOrDefaultAsync(agent => agent.Id == id, ct),
                _agents),
            nameof(Client) => CreateTarget<Client, ClientCommand>(
                name,
                await _db.Clients.FirstOrDefaultAsync(client => client.Id == id, ct),
                _clients),
            nameof(Company) => CreateTarget<Company, CompanyCommand>(
                name,
                await _db.Companies.FirstOrDefaultAsync(company => company.Id == id, ct),
                _companies),
            nameof(RetailItem) => CreateTarget<Item, RetailItemCommand>(
                name,
                await _db.Items.OfType<RetailItem>().FirstOrDefaultAsync(item => item.Id == id, ct),
                _items),
            nameof(TenderItem) => CreateTarget<Item, TenderItemCommand>(
                name,
                await _db.Items.OfType<TenderItem>().FirstOrDefaultAsync(item => item.Id == id, ct),
                _items),
            nameof(ItemCategory) => CreateTarget<ItemCategory, ItemCategoryCommand>(
                name,
                await _db.ItemCategories.FirstOrDefaultAsync(category => category.Id == id, ct),
                _categories),
            nameof(CommissionRate) => CreateTarget<CommissionRate, CommissionRateCommand>(
                name,
                await _db.CommissionRates
                    .Include(rate => rate.AgentCommissionRates)
                    .FirstOrDefaultAsync(rate => rate.Id == id, ct),
                _rates),
            nameof(Country) => CreateTarget<Country, CountryCommand>(
                name,
                await _db.Countries.FirstOrDefaultAsync(country => country.Id == id, ct),
                _countries),
            nameof(Region) => CreateTarget<Region, RegionCommand>(
                name,
                await _db.Regions.FirstOrDefaultAsync(region => region.Id == id, ct),
                _regions),
            _ => null,
        };
    }

    /// <summary>
    /// Pairs <paramref name="entity"/> with a modify that populates a new <typeparamref name="TCommand"/> from it, sets
    /// the command's lock owner and lock time, and runs <paramref name="service"/> in <see cref="TransactionMode.Own"/>
    /// mode; returns <see langword="null"/> when <paramref name="entity"/> is <see langword="null"/>.
    /// </summary>
    private static SoftLockTarget? CreateTarget<TEntity, TCommand>(
        string entityType,
        TEntity? entity,
        TransactionAgent<TEntity> service)
        where TEntity : PersistentModel
        where TCommand : TransactionCommand<TEntity>, new()
    {
        if (entity is null)
        {
            return null;
        }

        return new SoftLockTarget(
            entityType,
            entity,
            (lockedBySession, lockedTimestamp, transactionType, ct) =>
            {
                var command = new TCommand();
                command.PopulateFromObject(entity);
                command.LockedBySession = lockedBySession;
                command.LockedTimestamp = lockedTimestamp;
                return service.ExecuteAsync(command, transactionType, TransactionMode.Own, ct);
            });
    }

    /// <summary>
    /// A resolved row: its domain type name, the tracked entity, and the modify that runs a lock-owner, lock-time and
    /// sub-code through the entity's transaction agent.
    /// </summary>
    private sealed record SoftLockTarget(
        string EntityType,
        PersistentModel Entity,
        Func<string?, DateTime?, TransactionType, CancellationToken, Task<OperationResult>> ModifyAsync);
}
