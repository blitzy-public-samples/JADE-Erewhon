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
    /// when the change tracker holds an added sale of it. A client or item id of 0 counts as none.
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
            && await ItemHasSaleAsync(db, itemId, ct))
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
    /// Loads, with change tracking, the sale's item with its category and that category's commission rates, and the
    /// item's agent with its company and its commission-rate links, each link with its rate and the rate's category;
    /// then sets <see cref="Sale.Item"/> to the loaded item. After the load, <see cref="Sale.CalculateAgentCommission"/>
    /// reads the agent's rate for the item's category, or that category's lowest rate.
    /// </summary>
    /// <param name="db">The database the item is read from.</param>
    /// <param name="sale">The sale whose <see cref="Sale.ItemId"/> names the item.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the item and its commission data are loaded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> or <paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No item has the sale's <see cref="Sale.ItemId"/>.</exception>
    public static async Task LoadForCommissionAsync(IErewhonDbContext db, Sale sale, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sale);

        long itemId = sale.ItemId;
        Item item = await db.Items
            .Include(i => i.ItemCategory)
            .Include(i => i.Agent)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new InvalidOperationException($"No item has the id {itemId}.");

        if (item.ItemCategory is { } category)
        {
            await db.Entry(category).Collection(c => c.CommissionRates).LoadAsync(ct);
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
        }

        sale.Item = item;
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
    /// deleted in the change tracker.
    /// </summary>
    private static async Task<bool> ItemHasSaleAsync(IErewhonDbContext db, long itemId, CancellationToken ct)
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

        List<long> storedSaleIds = await db.Sales
            .AsNoTracking()
            .Where(s => s.ItemId == itemId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        return storedSaleIds.Any(id => !deletedSaleIds.Contains(id));
    }
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
    /// that method returns. Rows are returned for every sale; no access filter is applied.
    /// Empty when <paramref name="showFor"/> is <see cref="SalesShowFor.Agent"/> or <see cref="SalesShowFor.Client"/>
    /// and <paramref name="id"/> is <see langword="null"/> or names no agent or client, and when
    /// <paramref name="showFor"/> is <see cref="SalesShowFor.Company"/> and no company is bound;
    /// <paramref name="id"/> is ignored for <see cref="SalesShowFor.Company"/>.
    /// Ports the sale sets read by <c>SalesListForm.buildSalesTable</c>.
    /// </summary>
    /// <param name="showFor">Whose sales are listed.</param>
    /// <param name="id">The row id of the agent or client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="showFor"/> is not a defined value.</exception>
    public async Task<IReadOnlyList<SalesRowProjection>> GetSalesRowsAsync(
        SalesShowFor showFor,
        long? id,
        CancellationToken ct = default)
    {
        IReadOnlyList<Sale> sales = showFor switch
        {
            SalesShowFor.Agent => await AgentSalesAsync(id, ct),
            SalesShowFor.Client => await ClientSalesAsync(id, ct),
            SalesShowFor.Company => await CompanySalesAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(showFor), showFor, "The sales-list option is not a defined value."),
        };

        return sales.Select(SalesRowProjection.From).ToList();
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
    /// the commission data with <see cref="SaleRules.LoadForCommissionAsync"/>.
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

        await SaleRules.LoadForCommissionAsync(Db, sale, ct);
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
    /// Returns <see cref="Agent.GetAllSales"/> of the agent with row id <paramref name="agentId"/>, read without change
    /// tracking with its items, each item's category and sale, and each sale's client and winning tender; empty when
    /// <paramref name="agentId"/> is <see langword="null"/> or names no agent.
    /// </summary>
    private async Task<IReadOnlyList<Sale>> AgentSalesAsync(long? agentId, CancellationToken ct)
    {
        if (agentId is not long id)
        {
            return [];
        }

        Agent? agent = await Db.Agents
            .AsNoTrackingWithIdentityResolution()
            .Include(a => a.Items).ThenInclude(i => i.ItemCategory)
            .Include(a => a.Items).ThenInclude(i => i.Sale).ThenInclude(s => s!.Client)
            .Include(a => a.Items).ThenInclude(i => i.Sale).ThenInclude(s => ((TenderSale)s!).Tender)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        return agent?.GetAllSales() ?? [];
    }

    /// <summary>
    /// Returns <see cref="Client.GetAllSales"/> of the client with row id <paramref name="clientId"/>, read without
    /// change tracking with its sales, each sale's item with its category and each tender sale's winning tender; empty
    /// when <paramref name="clientId"/> is <see langword="null"/> or names no client.
    /// </summary>
    private async Task<IReadOnlyList<Sale>> ClientSalesAsync(long? clientId, CancellationToken ct)
    {
        if (clientId is not long id)
        {
            return [];
        }

        Client? client = await Db.Clients
            .AsNoTrackingWithIdentityResolution()
            .Include(c => c.Sales).ThenInclude(s => s.Item).ThenInclude(i => i!.ItemCategory)
            .Include(c => c.Sales).ThenInclude(s => ((TenderSale)s).Tender)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return client?.GetAllSales() ?? [];
    }

    /// <summary>
    /// Returns <see cref="Company.GetAllSales"/> of the bound company, read without change tracking with its sales,
    /// each sale's client, item with its category and each tender sale's winning tender; empty when no company is
    /// bound.
    /// </summary>
    private async Task<IReadOnlyList<Sale>> CompanySalesAsync(CancellationToken ct)
    {
        Company? bound = await _companyContext.FindCurrentAsync(ct);
        if (bound is null)
        {
            return [];
        }

        long companyId = bound.Id;
        Company? company = await Db.Companies
            .AsNoTrackingWithIdentityResolution()
            .Include(c => c.Sales).ThenInclude(s => s.Client)
            .Include(c => c.Sales).ThenInclude(s => s.Item).ThenInclude(i => i!.ItemCategory)
            .Include(c => c.Sales).ThenInclude(s => ((TenderSale)s).Tender)
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);

        return company?.GetAllSales() ?? [];
    }
}

