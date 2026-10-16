using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Projections;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one sale operation persists: the agent commission, the client and the sold item.
/// Ports the attributes, <c>initialize</c> and <c>populateFromObject</c> of JADE <c>SaleTA</c>.
/// Every sale command is a <see cref="TransactionCommand{TEntity}"/> of <see cref="Sale"/>; <typeparamref name="TSale"/>
/// is the kind of sale the command populates from.
/// </summary>
/// <typeparam name="TSale">The kind of sale: <see cref="RetailSale"/> or <see cref="TenderSale"/>.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public abstract class SaleCommand<TSale> : TransactionCommand<Sale>
    where TSale : Sale
{
    private decimal _agentCommission;

    /// <summary>
    /// The commission earned by the item's agent, held rounded to 2 decimal places with ties away from zero.
    /// Ports <c>SaleTA.agentCommission</c>.
    /// </summary>
    public decimal AgentCommission
    {
        get => _agentCommission;
        set => _agentCommission = DecimalRules.Round2(value);
    }

    /// <summary>The row id of the client who buys the item; <see langword="null"/> for none. Ports <c>SaleTA.myClient</c>.</summary>
    public long? ClientId { get; set; }

    /// <summary>The row id of the sold item; <see langword="null"/> for none. Ports <c>SaleTA.myItem</c>.</summary>
    public long? ItemId { get; set; }

    /// <summary>
    /// The stored state of a checkout's lines read for the validation in progress; the create-time sold check reads
    /// the item's stored sales from it when it covers the item, and from the database otherwise.
    /// <see langword="null"/> outside a checkout's validation.
    /// </summary>
    public CheckoutValidationSnapshot? ValidationSnapshot { get; set; }

    /// <summary>
    /// The commission data a checkout loaded for its retail lines' items after taking its locks; the create step reads
    /// the item's commission data from it without a query when it covers the item, and from the database otherwise.
    /// <see langword="null"/> outside a checkout's persistence of its lines.
    /// </summary>
    public CommissionPreload? CommissionPreload { get; set; }

    /// <summary>
    /// Resets the common attributes, then sets <see cref="AgentCommission"/> to 0 and <see cref="ClientId"/> and
    /// <see cref="ItemId"/> to <see langword="null"/>. Ports <c>SaleTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        AgentCommission = 0;
        ClientId = null;
        ItemId = null;
    }

    /// <summary>
    /// Populates the command from <paramref name="modelObject"/> through <see cref="PopulateFromObject(TSale)"/>.
    /// </summary>
    /// <param name="modelObject">The sale the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelObject"/> is not a <typeparamref name="TSale"/>.</exception>
    public override void PopulateFromObject(Sale modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        if (modelObject is not TSale sale)
        {
            throw new ArgumentException(
                $"The sale is a {modelObject.GetType().Name}, not a {typeof(TSale).Name}.",
                nameof(modelObject));
        }

        PopulateFromObject(sale);
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then copies its agent commission, client and item.
    /// Ports <c>SaleTA.populateFromObject</c>; an override calls this method first, then copies its own attributes.
    /// </summary>
    /// <param name="modelObject">The sale the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public virtual void PopulateFromObject(TSale modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        base.PopulateFromObject(modelObject);
        AgentCommission = modelObject.AgentCommission;
        ClientId = modelObject.ClientId;
        ItemId = modelObject.ItemId;
    }
}

/// <summary>
/// The validation, locking and commission-loading steps shared by the retail and tender sale services.
/// Ports <c>SaleTA.doValidate</c>, <c>lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and
/// <c>lockForModify</c>, and the reads behind <c>Sale.calculateAgentCommission</c>.
/// </summary>
public static class SaleRules
{
    /// <summary>
    /// Adds the sale validation errors to <paramref name="result"/>, in this order; a delete adds none:
    /// no client → <see cref="MessageKeys.ClientRequired"/> (<see cref="FocusFields.Client"/>);
    /// no item → <see cref="MessageKeys.ItemRequired"/> (<see cref="FocusFields.Item"/>);
    /// an agent commission below 0 → <see cref="MessageKeys.SaleAgentCommissionInvalid"/>
    /// (<see cref="FocusFields.AgentCommission"/>), a commission of 0 being accepted;
    /// on create, an item that already has a sale → <see cref="MessageKeys.ItemSold"/> (<see cref="FocusFields.Item"/>).
    /// An item has a sale when the database holds one for it that is not marked as deleted in the change tracker, or
    /// when the change tracker holds an added sale of it; the stored sales are those of
    /// <see cref="SaleCommand{TSale}.ValidationSnapshot"/> when it covers the item, and are read from the database
    /// otherwise. A client or item id of 0 counts as none.
    /// Ports <c>SaleTA.doValidate</c>.
    /// </summary>
    /// <typeparam name="TSale">The kind of sale the command populates from.</typeparam>
    /// <param name="db">The database the item's sale is read from.</param>
    /// <param name="c">The values to validate.</param>
    /// <param name="type">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="db"/>, <paramref name="c"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    public static async Task ValidateAsync<TSale>(
        IErewhonDbContext db,
        SaleCommand<TSale> c,
        ValidationType type,
        OperationResult result,
        CancellationToken ct)
        where TSale : Sale
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(result);

        if (type == ValidationType.Delete)
        {
            return;
        }

        if (JadeValues.IsNull(c.ClientId))
        {
            result.AddError(MessageKeys.ClientRequired, FocusFields.Client);
        }

        if (JadeValues.IsNull(c.ItemId))
        {
            result.AddError(MessageKeys.ItemRequired, FocusFields.Item);
        }

        if (c.AgentCommission < 0)
        {
            result.AddError(MessageKeys.SaleAgentCommissionInvalid, FocusFields.AgentCommission);
        }

        if (type == ValidationType.Create
            && c.ItemId is long itemId
            && !JadeValues.IsNull(itemId)
            && await ItemHasSaleAsync(db, c.ValidationSnapshot, itemId, ct))
        {
            result.AddError(MessageKeys.ItemSold, FocusFields.Item);
        }
    }

    /// <summary>
    /// Takes the sale lock <see cref="LockNames.CompanyAllSalesByItem"/> of the company: on create and delete always,
    /// on update only when the command's item differs from the model object's item, and on modify never.
    /// Ports <c>SaleTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and <c>lockForModify</c>.
    /// </summary>
    /// <typeparam name="TSale">The kind of sale the command populates from.</typeparam>
    /// <param name="locks">The advisory locks of the operation's transaction.</param>
    /// <param name="companyId">The row id of the bound company.</param>
    /// <param name="c">The values being persisted.</param>
    /// <param name="modelObject">The sale being updated or deleted; <see langword="null"/> on create.</param>
    /// <param name="type">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when every lock the operation needs is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="locks"/> or <paramref name="c"/> is <see langword="null"/>.</exception>
    public static async Task<bool> AcquireLocksAsync<TSale>(
        IEntityLocks locks,
        long companyId,
        SaleCommand<TSale> c,
        Sale? modelObject,
        ValidationType type,
        CancellationToken ct)
        where TSale : Sale
    {
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(c);

        bool takesLock = type switch
        {
            ValidationType.Create or ValidationType.Delete => true,
            ValidationType.Update => ItemChanges(c, modelObject),
            _ => false,
        };

        return !takesLock || await locks.TryLockAsync(LockNames.CompanyAllSalesByItem(companyId), ct);
    }

    /// <summary>
    /// Makes the sale's item tracked with the commission data <see cref="Sale.CalculateAgentCommission"/> reads, then
    /// sets <see cref="Sale.Item"/> to the tracked item. The commission data are the item's category with its
    /// commission rates and the item's agent with its company and its commission-rate links, each link with its rate
    /// and the rate's category; when the item's category belongs to another company than the agent, also the item
    /// category of the agent's company with the same name (ordinal comparison), with its commission rates. After the
    /// load, <see cref="Sale.CalculateAgentCommission"/> reads the agent's rate for a category of the item category's
    /// name, or the lowest rate of the agent's company's category of that name.
    /// When <paramref name="preload"/> covers the item, nothing is read: the item is taken from the change tracker,
    /// provided it is still tracked with its category and, when it has an agent, with that agent and the agent's
    /// company, the preload searched that company for the category's name, and a category the search found is still in
    /// the company's categories. Otherwise every call runs these reads, with change tracking: the item with its category
    /// and agent; the category's commission rates; the agent's company, through EF Core's navigation load, which reads
    /// nothing once that navigation is marked as loaded; the agent's commission-rate links with their rates and the
    /// rates' categories; and, for an agent of another company than the item's category, the category of the agent's
    /// company with the name, with its commission rates. The rows these reads return join the tracked collections; a
    /// row already tracked keeps its tracked values, and a tracked row a read no longer finds stays in its collection.
    /// </summary>
    /// <param name="db">The database the item is read from.</param>
    /// <param name="sale">The sale whose <see cref="Sale.ItemId"/> names the item.</param>
    /// <param name="preload">
    /// The commission data a checkout loaded with <see cref="PreloadForCommissionAsync"/>; <see langword="null"/> for none.
    /// </param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the item and its commission data are loaded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> or <paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The item is not taken from <paramref name="preload"/> and no item has the sale's <see cref="Sale.ItemId"/>.
    /// </exception>
    public static async Task LoadForCommissionAsync(
        IErewhonDbContext db,
        Sale sale,
        CommissionPreload? preload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sale);

        long itemId = sale.ItemId;
        if (FindPreloadedItem(db, preload, itemId) is { } preloadedItem)
        {
            sale.Item = preloadedItem;
            return;
        }

        Item item = await db.Items
            .Include(i => i.ItemCategory)
            .Include(i => i.Agent)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new InvalidOperationException($"No item has the id {itemId}.");

        if (item.ItemCategory is { } category)
        {
            await db.Entry(category).Collection(c => c.CommissionRates).Query().LoadAsync(ct);
        }

        if (item.Agent is { } agent)
        {
            await db.Entry(agent).Reference(a => a.Company).LoadAsync(ct);
            await db.Entry(agent)
                .Collection(a => a.AgentCommissionRates)
                .Query()
                .Include(link => link.CommissionRate)
                .ThenInclude(rate => rate!.ItemCategory)
                .LoadAsync(ct);

            if (item.ItemCategory is { } itemCategory && itemCategory.CompanyId != agent.CompanyId)
            {
                long agentCompanyId = agent.CompanyId;
                string categoryName = itemCategory.Name;
                await db.ItemCategories
                    .Include(c => c.CommissionRates)
                    .Where(c => c.CompanyId == agentCompanyId && c.Name == categoryName)
                    .LoadAsync(ct);
            }
        }

        sale.Item = item;
    }

    /// <summary>
    /// Loads, with change tracking and in at most four queries whatever the number of items, the commission data that
    /// <see cref="LoadForCommissionAsync"/> loads for each item in <paramref name="itemIds"/>: the items with their
    /// categories and agents; those categories, and the categories of the agents' companies whose names equal theirs,
    /// each with its commission rates; the agents' companies not yet tracked; and the agents' commission-rate links,
    /// each with its rate and the rate's category. Returns a <see cref="CommissionPreload"/> that covers each item read
    /// and records, for each item with an agent, the category of the agent's company with the item category's name
    /// that the search found, or that it found none. No loaded flag is set on the agents' links. An id that names no
    /// item is not covered.
    /// </summary>
    /// <param name="db">The database the commission data is read from.</param>
    /// <param name="itemIds">The row ids of the items.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The preload; it covers no item when <paramref name="itemIds"/> names none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> or <paramref name="itemIds"/> is <see langword="null"/>.</exception>
    public static async Task<CommissionPreload> PreloadForCommissionAsync(
        IErewhonDbContext db,
        IEnumerable<long> itemIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(itemIds);

        long[] ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new CommissionPreload([], []);
        }

        List<Item> items = await db.Items
            .Include(i => i.ItemCategory)
            .Include(i => i.Agent)
            .Where(i => ids.Contains(i.Id))
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return new CommissionPreload([], []);
        }

        List<Agent> agents = items
            .Select(i => i.Agent)
            .OfType<Agent>()
            .Distinct<Agent>(ReferenceEqualityComparer.Instance)
            .ToList();
        long[] categoryIds = items.Select(i => i.ItemCategoryId).Distinct().ToArray();
        long[] agentCompanyIds = agents.Select(a => a.CompanyId).Distinct().ToArray();
        string[] agentCategoryNames = items
            .Where(i => i.Agent is not null && i.ItemCategory is not null)
            .Select(i => i.ItemCategory!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        List<ItemCategory> categories = await db.ItemCategories
            .Include(c => c.CommissionRates)
            .Where(c => categoryIds.Contains(c.Id)
                || (agentCompanyIds.Contains(c.CompanyId) && agentCategoryNames.Contains(c.Name)))
            .ToListAsync(ct);

        long[] untrackedCompanyIds = agents
            .Where(a => a.Company is null)
            .Select(a => a.CompanyId)
            .Distinct()
            .ToArray();
        if (untrackedCompanyIds.Length > 0)
        {
            await db.Companies.Where(c => untrackedCompanyIds.Contains(c.Id)).LoadAsync(ct);
        }

        if (agents.Count > 0)
        {
            long[] agentIds = agents.Select(a => a.Id).ToArray();
            await db.AgentCommissionRates
                .Include(link => link.CommissionRate)
                .ThenInclude(rate => rate!.ItemCategory)
                .Where(link => agentIds.Contains(link.AgentId))
                .LoadAsync(ct);
        }

        return new CommissionPreload(items, categories);
    }

    /// <summary>
    /// Returns the tracked item with row id <paramref name="itemId"/> when <paramref name="preload"/> covers it and the
    /// change tracker still holds its commission data: the item with its category and, when it has an agent, that
    /// agent with its company, whose search by the preload for the category's name is recorded, a category it found
    /// being among the company's categories. Returns <see langword="null"/> otherwise.
    /// </summary>
    private static Item? FindPreloadedItem(IErewhonDbContext db, CommissionPreload? preload, long itemId)
    {
        if (preload is null
            || !preload.CoversItem(itemId)
            || db.Items.Local.FindEntry(itemId)?.Entity is not { ItemCategory: { } category } item)
        {
            return null;
        }

        if (item.AgentId is null)
        {
            return item;
        }

        if (item.Agent is not { Company: { } agentCompany } agent
            || !preload.TryGetAgentCompanyCategory(agent.CompanyId, category.Name, out ItemCategory? agentCompanyCategory)
            || (agentCompanyCategory is not null && !agentCompany.ItemCategories.Contains(agentCompanyCategory)))
        {
            return null;
        }

        return item;
    }

    /// <summary>
    /// Returns whether the command's item differs from the item of <paramref name="modelObject"/>; a missing model
    /// object counts as a change.
    /// </summary>
    private static bool ItemChanges<TSale>(SaleCommand<TSale> c, Sale? modelObject)
        where TSale : Sale =>
        modelObject is null || c.ItemId != modelObject.ItemId;

    /// <summary>
    /// Returns whether the item has a sale: an added sale in the change tracker, or a stored sale not marked as
    /// deleted in the change tracker. The stored sales are taken from <paramref name="snapshot"/> when it covers the
    /// item, and are read from the database otherwise.
    /// </summary>
    private static async Task<bool> ItemHasSaleAsync(
        IErewhonDbContext db,
        CheckoutValidationSnapshot? snapshot,
        long itemId,
        CancellationToken ct)
    {
        var trackedSales = db.ChangeTracker.Entries<Sale>().ToList();
        if (trackedSales.Any(entry => entry.State == EntityState.Added && entry.Entity.ItemId == itemId))
        {
            return true;
        }

        var deletedSaleIds = trackedSales
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        IReadOnlyList<long> storedSaleIds;
        if (snapshot is null || !snapshot.TryGetStoredSaleIds(itemId, out storedSaleIds))
        {
            storedSaleIds = await db.Sales
                .AsNoTracking()
                .Where(s => s.ItemId == itemId)
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        return storedSaleIds.Any(id => !deletedSaleIds.Contains(id));
    }
}

/// <summary>
/// The commission data <see cref="SaleRules.PreloadForCommissionAsync"/> loaded, with change tracking, for a set of
/// items: the row ids of the items whose commission data it loaded and, for each pair of an agent's company and an item
/// category name it searched, the item category of that company with that name (ordinal comparison), or none.
/// <see cref="SaleRules.LoadForCommissionAsync"/> reads nothing for an item the preload covers.
/// </summary>
/// <remarks>
/// The preload's ids and search results do not change after it is created; the categories it holds are entities
/// tracked by the context it was loaded from.
/// </remarks>
public sealed class CommissionPreload
{
    private readonly HashSet<long> _itemIds;
    private readonly Dictionary<AgentCompanyCategoryKey, ItemCategory?> _agentCompanyCategories = new();

    /// <summary>
    /// Creates the preload of <paramref name="items"/>, each read with its category and agent, from
    /// <paramref name="categories"/>, the categories read for them: for each item with an agent, the category of the
    /// agent's company with the item category's name (ordinal comparison) among <paramref name="categories"/>, or none.
    /// </summary>
    /// <param name="items">The items whose commission data were loaded.</param>
    /// <param name="categories">The item categories read for the items and their agents' companies.</param>
    internal CommissionPreload(IReadOnlyCollection<Item> items, IReadOnlyCollection<ItemCategory> categories)
    {
        _itemIds = items.Select(item => item.Id).ToHashSet();
        foreach (Item item in items)
        {
            if (item.Agent is not { } agent || item.ItemCategory is not { } category)
            {
                continue;
            }

            var key = new AgentCompanyCategoryKey(agent.CompanyId, category.Name);
            if (!_agentCompanyCategories.ContainsKey(key))
            {
                _agentCompanyCategories[key] = categories.FirstOrDefault(candidate =>
                    candidate.CompanyId == key.CompanyId
                    && string.Equals(candidate.Name, key.CategoryName, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Returns whether the preload loaded the commission data of the item with row id <paramref name="itemId"/>.</summary>
    /// <param name="itemId">The row id of the item.</param>
    /// <returns><see langword="true"/> when the preload covers the item.</returns>
    public bool CoversItem(long itemId) => _itemIds.Contains(itemId);

    /// <summary>
    /// Returns whether the preload searched the company with row id <paramref name="companyId"/> for an item category
    /// named <paramref name="categoryName"/> (ordinal comparison); when it did, <paramref name="category"/> receives the
    /// category it found, loaded with its commission rates, or <see langword="null"/> when the company has none of that
    /// name.
    /// </summary>
    /// <param name="companyId">The row id of the agent's company.</param>
    /// <param name="categoryName">The item category name.</param>
    /// <param name="category">The category found, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the preload searched the company for the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="categoryName"/> is <see langword="null"/>.</exception>
    public bool TryGetAgentCompanyCategory(long companyId, string categoryName, out ItemCategory? category)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return _agentCompanyCategories.TryGetValue(new AgentCompanyCategoryKey(companyId, categoryName), out category);
    }

    /// <summary>An agent's company and an item category name searched for in it.</summary>
    private readonly record struct AgentCompanyCategoryKey(long CompanyId, string CategoryName);
}

/// <summary>Selects whose sales the sales list shows. Ports the Agent, Client and Company options of <c>SalesListForm</c>.</summary>
public enum SalesShowFor
{
    /// <summary>The sales of the items of one agent.</summary>
    Agent,

    /// <summary>The sales made to one client.</summary>
    Client,

    /// <summary>Every sale of the bound company.</summary>
    Company,
}

/// <summary>
/// The values one retail sale operation persists: the sale attributes of <see cref="SaleCommand{TSale}"/>, the sale
/// price and the time sold. Ports the attributes, <c>initialize</c> and <c>populateFromObject</c> of JADE
/// <c>RetailSaleTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class RetailSaleCommand : SaleCommand<RetailSale>
{
    private decimal _salePrice;

    /// <summary>
    /// The price the item is sold at, held rounded to 2 decimal places with ties away from zero; 0 for none.
    /// Ports <c>RetailSaleTA.salePrice</c>.
    /// </summary>
    public decimal SalePrice
    {
        get => _salePrice;
        set => _salePrice = DecimalRules.Round2(value);
    }

    /// <summary>
    /// The time the item is sold; <see langword="null"/> until pre-validation sets it to the database server's current
    /// timestamp. Ports <c>RetailSaleTA.dateSold</c>.
    /// </summary>
    public DateTime? DateSold { get; set; }

    /// <summary>
    /// Resets the sale attributes, then sets <see cref="SalePrice"/> to 0 and <see cref="DateSold"/> to
    /// <see langword="null"/>. Ports <c>RetailSaleTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        SalePrice = 0;
        DateSold = null;
    }

    /// <summary>
    /// Copies the sale attributes of <paramref name="modelObject"/>, then its sale price and time sold.
    /// Ports <c>RetailSaleTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The retail sale the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(RetailSale modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        base.PopulateFromObject(modelObject);
        SalePrice = modelObject.SalePrice;
        DateSold = modelObject.DateSold;
    }
}

/// <summary>
/// Creates, updates and deletes the retail sales of the bound company through the transaction-agent steps, and reads
/// the company's sales for the REST operations and the sales list. Ports JADE <c>RetailSaleTA</c> with the
/// <c>SaleTA</c> steps it inherits, the <c>Sale.onCreate</c>, <c>RetailSale.onCreate</c> and
/// <c>setCommonProperties</c> methods, the <c>allSalesByItem</c> reads of <c>ErewhonRestService.getAllSales</c> and
/// <c>getSale</c>, and the sale sets read by <c>SalesListForm.buildSalesTable</c>.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope. Instances are not safe to call from several threads at once.
/// </remarks>
public sealed class RetailSaleService : TransactionAgent<Sale>
{
    private readonly ICompanyContext _companyContext;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock, notification queue and bound company
    /// of its scope.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken for each operation.</param>
    /// <param name="clock">The database server clock that supplies the default time sold.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RetailSaleService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        _companyContext = companyContext;
    }

    /// <summary>
    /// Deletes the retail sale with row id <paramref name="id"/>; succeeds with no work when no retail sale has that id.
    /// </summary>
    /// <param name="id">The row id of the retail sale.</param>
    /// <param name="mode">Whether the service runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        var command = new RetailSaleCommand();
        if (await FindByIdAsync(id, ct) is RetailSale sale)
        {
            command.PopulateFromObject(sale);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the sale, retail or tender, with row id <paramref name="id"/>, tracked, with its client, its item and
    /// the item's category and, for a tender sale, its tender; <see langword="null"/> when no sale has that id.
    /// </summary>
    /// <param name="id">The row id of the sale.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The sale, or <see langword="null"/>.</returns>
    public async Task<Sale?> FindByIdAsync(long id, CancellationToken ct = default) =>
        await Db.Sales
            .Include(s => s.Client)
            .Include(s => s.Item).ThenInclude(i => i!.ItemCategory)
            .Include(s => ((TenderSale)s).Tender)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>
    /// Returns every sale of the bound company, retail and tender, ordered by the prefix of the item's category
    /// (ordinal comparison) and then the item's code number, as projections carrying the client, the item with its
    /// category, region and country, agent, sale and tenders, and the winning tender of a tender sale.
    /// Ports the <c>app.myCompany.allSalesByItem</c> iteration of <c>ErewhonRestService.getAllSales</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The sales; empty when the company has none.</returns>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    public async Task<IReadOnlyList<SaleProjection>> GetAllSalesByItemAsync(CancellationToken ct = default)
    {
        Company company = await _companyContext.GetCurrentAsync(ct);
        long companyId = company.Id;

        List<Sale> sales = await SalesWithProjectionData()
            .Where(s => s.CompanyId == companyId)
            .ToListAsync(ct);

        return sales
            .OrderBy(s => s.Item?.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(s => s.Item?.CodeNumber)
            .Select(SaleProjection.From)
            .ToList();
    }

    /// <summary>
    /// Returns the sale of the bound company whose item's category prefix equals <paramref name="codePrefix"/> by
    /// ordinal comparison, as given and untrimmed, and whose item's code number equals <paramref name="codeNumber"/>,
    /// as a projection with the data of <see cref="GetAllSalesByItemAsync"/>; <see langword="null"/> when there is none.
    /// Ports the <c>app.myCompany.allSalesByItem[pCodePrefix, pCodeNumber]</c> lookup of
    /// <c>ErewhonRestService.getSale</c>.
    /// </summary>
    /// <param name="codePrefix">The category prefix of the sold item.</param>
    /// <param name="codeNumber">The code number of the sold item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The sale, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    public async Task<SaleProjection?> FindSaleByItemCodeAsync(
        string? codePrefix,
        int codeNumber,
        CancellationToken ct = default)
    {
        Company company = await _companyContext.GetCurrentAsync(ct);
        long companyId = company.Id;

        List<Sale> candidates = await SalesWithProjectionData()
            .Where(s => s.CompanyId == companyId && s.Item!.CodeNumber == codeNumber)
            .ToListAsync(ct);

        Sale? sale = candidates.FirstOrDefault(
            s => string.Equals(s.Item?.ItemCategory?.Prefix, codePrefix, StringComparison.Ordinal));

        return sale is null ? null : SaleProjection.From(sale);
    }

    /// <summary>
    /// Returns the sales-list rows of the sales of one agent's items (<see cref="Agent.GetAllSales"/>), of one client
    /// (<see cref="Client.GetAllSales"/>) or of the bound company (<see cref="Company.GetAllSales"/>), in the order
    /// those methods return: by class number, so retail sales precede tender sales, and then by sale row id. Rows are
    /// returned for every sale; no access filter is applied.
    /// Empty when <paramref name="showFor"/> is <see cref="SalesShowFor.Agent"/> or <see cref="SalesShowFor.Client"/>
    /// and <paramref name="id"/> is <see langword="null"/> or names no agent or client, and when
    /// <paramref name="showFor"/> is <see cref="SalesShowFor.Company"/> and no company is bound;
    /// <paramref name="id"/> is ignored for <see cref="SalesShowFor.Company"/>.
    /// The sales are read in one query without change tracking, selecting only the values a row holds: the sales whose
    /// item has the agent, the sales of the client, or the sales of the bound company, the agent and client options with
    /// no company filter. Each row is built by <see cref="SalesRowProjection.From"/> from an untracked sale holding
    /// those values.
    /// Ports the sale sets read by <c>SalesListForm.buildSalesTable</c>.
    /// </summary>
    /// <param name="showFor">Whose sales are listed.</param>
    /// <param name="id">The row id of the agent or client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="showFor"/> is not a defined value.</exception>
    /// <exception cref="NoCompanyException">
    /// A listed tender sale has no winning tender: its <see cref="TenderSale.TenderId"/> is <see langword="null"/>.
    /// </exception>
    public async Task<IReadOnlyList<SalesRowProjection>> GetSalesRowsAsync(
        SalesShowFor showFor,
        long? id,
        CancellationToken ct = default)
    {
        IQueryable<Sale>? sales = showFor switch
        {
            SalesShowFor.Agent => AgentSales(id),
            SalesShowFor.Client => ClientSales(id),
            SalesShowFor.Company => await CompanySalesAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(showFor), showFor, "The sales-list option is not a defined value."),
        };

        if (sales is null)
        {
            return [];
        }

        List<SalesRowValues> values = await sales
            .AsNoTracking()
            .Select(s => new SalesRowValues(
                s.Id,
                s is TenderSale,
                s.ClientId,
                s.Client!.Name,
                s.ItemId,
                s.Item is TenderItem,
                s.Item!.CodeNumber,
                s.Item.Name,
                s.Item.Price,
                s.Item.AgentId,
                s.Item.ItemCategory!.Prefix,
                s.AgentCommission,
                (decimal?)((RetailSale)s).SalePrice,
                (DateTime?)((RetailSale)s).DateSold,
                (long?)((TenderSale)s).Tender!.Id,
                (DateTime?)((TenderSale)s).Tender!.DateTendered,
                (decimal?)((TenderSale)s).Tender!.Offer))
            .ToListAsync(ct);

        return values
            .Select(ToUntrackedSale)
            .OrderBy(sale => sale.JadeClassNumber)
            .ThenBy(sale => sale.Id)
            .Select(SalesRowProjection.From)
            .ToList();
    }


    /// <summary>
    /// Sets <see cref="RetailSaleCommand.DateSold"/> to the database server's current timestamp when it is null or
    /// invalid. Ports <c>RetailSaleTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The retail sale values to prepare.</param>
    /// <param name="ct">Token that cancels the clock read.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RetailSaleCommand"/>.</exception>
    protected override async Task PreValidateCoreAsync(TransactionCommand<Sale> command, CancellationToken ct)
    {
        RetailSaleCommand cmd = AsRetailSaleCommand(command);
        if (JadeValues.IsNull(cmd.DateSold) || !JadeValues.IsValid(cmd.DateSold))
        {
            cmd.DateSold = await Clock.GetServerTimeStampAsync(ct);
        }
    }

    /// <summary>
    /// Adds the retail sale validation errors to <paramref name="result"/>; a delete adds none. In order: the sale
    /// rules of <see cref="SaleRules.ValidateAsync"/>; a sale price of 0 → <see cref="MessageKeys.RetailSalePriceRequired"/>,
    /// otherwise a sale price below 0 → <see cref="MessageKeys.RetailPriceInvalid"/> (both <see cref="FocusFields.Price"/>);
    /// a null or invalid time sold → <see cref="MessageKeys.SoldDateNotValid"/> (<see cref="FocusFields.Timestamp"/>).
    /// Ports <c>RetailSaleTA.doValidate</c> and <c>SaleTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The retail sale values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RetailSaleCommand"/>.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Sale> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        RetailSaleCommand cmd = AsRetailSaleCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        await SaleRules.ValidateAsync(Db, cmd, validationType, result, ct);

        if (JadeValues.IsNull(cmd.SalePrice))
        {
            result.AddError(MessageKeys.RetailSalePriceRequired, FocusFields.Price);
        }
        else if (cmd.SalePrice < 0)
        {
            result.AddError(MessageKeys.RetailPriceInvalid, FocusFields.Price);
        }

        if (JadeValues.IsNull(cmd.DateSold) || !JadeValues.IsValid(cmd.DateSold))
        {
            result.AddError(MessageKeys.SoldDateNotValid, FocusFields.Timestamp);
        }
    }

    /// <summary>
    /// Takes the locks of the operation in order: the company sale lock of <see cref="SaleRules.AcquireLocksAsync"/>,
    /// then <see cref="LockNames.ClientAllRetailSales"/> of the command's client on create and delete, and on update
    /// only when the command's item differs from the model object's item. A modify takes no lock and reads nothing.
    /// Ports <c>RetailSaleTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and the inherited
    /// <c>SaleTA.lockForModify</c>.
    /// </summary>
    /// <param name="command">The retail sale values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held; <see langword="false"/> at the first lock not obtained.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RetailSaleCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">A client lock is due and the command has no client.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Sale> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        RetailSaleCommand cmd = AsRetailSaleCommand(command);
        if (validationType == ValidationType.Modify)
        {
            return true;
        }

        Company company = await _companyContext.GetCurrentAsync(ct);
        if (!await SaleRules.AcquireLocksAsync(Locks, company.Id, cmd, cmd.ModelObject, validationType, ct))
        {
            return false;
        }

        bool takesClientLock = validationType switch
        {
            ValidationType.Create or ValidationType.Delete => true,
            ValidationType.Update => cmd.ModelObject is null || cmd.ItemId != cmd.ModelObject.ItemId,
            _ => false,
        };

        return !takesClientLock
            || await Locks.TryLockAsync(LockNames.ClientAllRetailSales(RequireClientId(cmd)), ct);
    }

    /// <summary>Returns a new, untracked <see cref="RetailSale"/>.</summary>
    /// <param name="command">The retail sale values being created.</param>
    /// <returns>The new retail sale.</returns>
    protected override Sale CreateModelObject(TransactionCommand<Sale> command) => new RetailSale();

    /// <summary>
    /// Sets the attributes of the new retail sale: the GUID and cleared soft edit lock of the base; the agent
    /// commission, sale price and time sold of the command; the bound company, the command's client and item; then
    /// the agent commission computed by <see cref="Sale.CalculateAgentCommission"/> on the sale price, after loading
    /// the commission data with <see cref="SaleRules.LoadForCommissionAsync"/> and the command's
    /// <see cref="SaleCommand{TSale}.CommissionPreload"/>.
    /// Ports <c>Sale.onCreate</c>, <c>RetailSale.onCreate</c> and <c>RetailSale.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The retail sale being created, already tracked as added.</param>
    /// <param name="command">The retail sale values being created.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is not a <see cref="RetailSale"/> or <paramref name="command"/> is not a
    /// <see cref="RetailSaleCommand"/>.
    /// </exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">The command has no client or no item, or the item does not exist.</exception>
    protected override async Task OnCreateAsync(Sale entity, TransactionCommand<Sale> command, CancellationToken ct)
    {
        RetailSale sale = AsRetailSale(entity);
        RetailSaleCommand cmd = AsRetailSaleCommand(command);

        await base.OnCreateAsync(sale, cmd, ct);
        SetCommonProperties(sale, cmd);

        Company company = await _companyContext.GetCurrentAsync(ct);
        sale.CompanyId = company.Id;
        sale.ClientId = RequireClientId(cmd);
        sale.ItemId = cmd.ItemId ?? throw new InvalidOperationException("The retail sale has no item.");

        await SaleRules.LoadForCommissionAsync(Db, sale, cmd.CommissionPreload, ct);
        sale.CalculateAgentCommission(sale.SalePrice);
    }

    /// <summary>
    /// Writes the command's agent commission, sale price and time sold to the retail sale and clears its soft edit
    /// lock; the company, client and item are left unchanged. Ports <c>RetailSale.setCommonProperties</c> as
    /// <c>onUpdate</c> calls it.
    /// </summary>
    /// <param name="entity">The tracked retail sale being updated.</param>
    /// <param name="command">The retail sale values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is not a <see cref="RetailSale"/> or <paramref name="command"/> is not a
    /// <see cref="RetailSaleCommand"/>.
    /// </exception>
    protected override async Task OnUpdateAsync(Sale entity, TransactionCommand<Sale> command, CancellationToken ct)
    {
        RetailSale sale = AsRetailSale(entity);
        RetailSaleCommand cmd = AsRetailSaleCommand(command);

        SetCommonProperties(sale, cmd);
        await base.OnUpdateAsync(sale, cmd, ct);
    }

    /// <summary>
    /// Writes the agent commission and sale price, each rounded to 2 decimal places with ties away from zero, and the
    /// time sold with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    private static void SetCommonProperties(RetailSale sale, RetailSaleCommand cmd)
    {
        sale.AgentCommission = DecimalRules.Round2(cmd.AgentCommission);
        sale.SalePrice = DecimalRules.Round2(cmd.SalePrice);
        sale.DateSold = DateTime.SpecifyKind(cmd.DateSold.GetValueOrDefault(), DateTimeKind.Unspecified);
    }

    /// <summary>Returns the command's client id, or throws when it has none.</summary>
    private static long RequireClientId(RetailSaleCommand cmd) =>
        cmd.ClientId ?? throw new InvalidOperationException("The retail sale has no client.");

    /// <summary>Returns <paramref name="command"/> as a <see cref="RetailSaleCommand"/>.</summary>
    private static RetailSaleCommand AsRetailSaleCommand(TransactionCommand<Sale> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as RetailSaleCommand
            ?? throw new ArgumentException(
                $"The command is a {command.GetType().Name}, not a {nameof(RetailSaleCommand)}.",
                nameof(command));
    }

    /// <summary>Returns <paramref name="entity"/> as a <see cref="RetailSale"/>.</summary>
    private static RetailSale AsRetailSale(Sale entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity as RetailSale
            ?? throw new ArgumentException(
                $"The sale is a {entity.GetType().Name}, not a {nameof(RetailSale)}.",
                nameof(entity));
    }

    /// <summary>
    /// The sales with every navigation <see cref="SaleProjection.From"/> reads, without change tracking and with
    /// identity resolution: a tender sale's item and its winning tender's item are one instance.
    /// </summary>
    private IQueryable<Sale> SalesWithProjectionData() =>
        Db.Sales
            .AsNoTrackingWithIdentityResolution()
            .Include(s => s.Client)
            .Include(s => s.Item).ThenInclude(i => i!.ItemCategory)
            .Include(s => s.Item).ThenInclude(i => i!.Region).ThenInclude(r => r!.Country)
            .Include(s => s.Item).ThenInclude(i => i!.Agent)
            .Include(s => s.Item).ThenInclude(i => ((TenderItem)i!).Tenders)
            .Include(s => ((TenderSale)s).Tender).ThenInclude(t => t!.Client)
            .Include(s => ((TenderSale)s).Tender).ThenInclude(t => t!.TenderItem);

    /// <summary>
    /// The sales whose item has the agent with row id <paramref name="agentId"/>, of any company;
    /// <see langword="null"/> when <paramref name="agentId"/> is <see langword="null"/>.
    /// </summary>
    private IQueryable<Sale>? AgentSales(long? agentId) =>
        agentId is long id ? Db.Sales.Where(s => s.Item!.AgentId == id) : null;

    /// <summary>
    /// The sales of the client with row id <paramref name="clientId"/>, of any company; <see langword="null"/> when
    /// <paramref name="clientId"/> is <see langword="null"/>.
    /// </summary>
    private IQueryable<Sale>? ClientSales(long? clientId) =>
        clientId is long id ? Db.Sales.Where(s => s.ClientId == id) : null;

    /// <summary>
    /// The sales of the bound company, read with <see cref="ICompanyContext.FindCurrentAsync"/>; <see langword="null"/>
    /// when no company is bound.
    /// </summary>
    private async Task<IQueryable<Sale>?> CompanySalesAsync(CancellationToken ct)
    {
        Company? bound = await _companyContext.FindCurrentAsync(ct);
        if (bound is null)
        {
            return null;
        }

        long companyId = bound.Id;
        return Db.Sales.Where(s => s.CompanyId == companyId);
    }

    /// <summary>
    /// Returns a new sale, attached to no context, holding the values of <paramref name="values"/>: a
    /// <see cref="TenderSale"/> with its winning tender when one was read, otherwise a <see cref="RetailSale"/>; its
    /// client with the client name; and its item, a <see cref="TenderItem"/> or a <see cref="RetailItem"/>, with the
    /// item's category prefix.
    /// </summary>
    private static Sale ToUntrackedSale(SalesRowValues values)
    {
        Item item = values.IsTenderItem ? new TenderItem() : new RetailItem();
        item.Id = values.ItemId;
        item.CodeNumber = values.ItemCodeNumber;
        item.Name = values.ItemName;
        item.Price = values.ItemPrice;
        item.AgentId = values.ItemAgentId;
        item.ItemCategory = new ItemCategory { Prefix = values.ItemCategoryPrefix };

        Sale sale = values.IsTenderSale
            ? new TenderSale
            {
                TenderId = values.TenderId,
                Tender = values.TenderId is long tenderId
                    ? new Tender
                    {
                        Id = tenderId,
                        DateTendered = values.DateTendered.GetValueOrDefault(),
                        Offer = values.Offer.GetValueOrDefault(),
                    }
                    : null,
            }
            : new RetailSale
            {
                SalePrice = values.SalePrice.GetValueOrDefault(),
                DateSold = values.DateSold.GetValueOrDefault(),
            };

        sale.Id = values.SaleId;
        sale.ClientId = values.ClientId;
        sale.Client = new Client { Id = values.ClientId, Name = values.ClientName };
        sale.ItemId = values.ItemId;
        sale.Item = item;
        sale.AgentCommission = values.AgentCommission;
        return sale;
    }

    /// <summary>
    /// The values of one sale that <see cref="SalesRowProjection.From"/> reads, as one row of the query of
    /// <see cref="GetSalesRowsAsync"/>.
    /// </summary>
    /// <param name="SaleId">The row id of the sale.</param>
    /// <param name="IsTenderSale"><see langword="true"/> for a tender sale.</param>
    /// <param name="ClientId">The row id of the buying client.</param>
    /// <param name="ClientName">The name of the buying client.</param>
    /// <param name="ItemId">The row id of the sold item.</param>
    /// <param name="IsTenderItem"><see langword="true"/> when the sold item is a tender item.</param>
    /// <param name="ItemCodeNumber">The code number of the sold item.</param>
    /// <param name="ItemName">The name of the sold item.</param>
    /// <param name="ItemPrice">The price of the sold item.</param>
    /// <param name="ItemAgentId">The row id of the item's agent; <see langword="null"/> when it has none.</param>
    /// <param name="ItemCategoryPrefix">The prefix of the item's category.</param>
    /// <param name="AgentCommission">The agent commission of the sale.</param>
    /// <param name="SalePrice">The sale price of a retail sale; <see langword="null"/> for a tender sale.</param>
    /// <param name="DateSold">The time sold of a retail sale; <see langword="null"/> for a tender sale.</param>
    /// <param name="TenderId">
    /// The row id of the winning tender of a tender sale; <see langword="null"/> for a retail sale and for a tender sale
    /// with no winning tender.
    /// </param>
    /// <param name="DateTendered">The time the winning tender was placed; <see langword="null"/> when there is none.</param>
    /// <param name="Offer">The offer of the winning tender; <see langword="null"/> when there is none.</param>
    private sealed record SalesRowValues(
        long SaleId,
        bool IsTenderSale,
        long ClientId,
        string ClientName,
        long ItemId,
        bool IsTenderItem,
        int ItemCodeNumber,
        string ItemName,
        decimal ItemPrice,
        long? ItemAgentId,
        string ItemCategoryPrefix,
        decimal AgentCommission,
        decimal? SalePrice,
        DateTime? DateSold,
        long? TenderId,
        DateTime? DateTendered,
        decimal? Offer);
}

