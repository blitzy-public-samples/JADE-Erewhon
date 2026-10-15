using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one tender sale operation persists: the sale attributes of <see cref="SaleCommand{TSale}"/> and the
/// winning tender. Ports the attributes, <c>initialize</c> and <c>populateFromObject</c> of JADE <c>TenderSaleTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class TenderSaleCommand : SaleCommand<TenderSale>
{
    /// <summary>
    /// The row id of the winning tender; <see langword="null"/> for none, as is 0. Ports <c>TenderSaleTA.myTender</c>.
    /// </summary>
    public long? TenderId { get; set; }

    /// <summary>
    /// Resets the sale attributes, then sets <see cref="TenderId"/> to <see langword="null"/>.
    /// Ports <c>TenderSaleTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        TenderId = null;
    }

    /// <summary>
    /// Copies the sale attributes of <paramref name="modelObject"/>, then its winning tender.
    /// Ports <c>TenderSaleTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The tender sale the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(TenderSale modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        base.PopulateFromObject(modelObject);
        TenderId = modelObject.TenderId;
    }
}

/// <summary>
/// Creates, updates and deletes the tender sales of the bound company through the transaction-agent steps; a tender
/// sale awards a tender item to the client of its winning tender. Ports JADE <c>TenderSaleTA</c> with the
/// <c>SaleTA</c> steps it inherits, and the <c>Sale.onCreate</c>, <c>Sale.setCommonProperties</c> and
/// <c>TenderSale.onCreate</c> methods.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope. Instances are not safe to call from several threads at once.
/// </remarks>
public sealed class TenderSaleService : TransactionAgent<Sale>
{
    private readonly ICompanyContext _companyContext;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock, notification queue and bound company
    /// of its scope.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken for each operation.</param>
    /// <param name="clock">The database server clock of the scope.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TenderSaleService(
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
    /// Deletes the tender sale with row id <paramref name="id"/>; succeeds with no work when no tender sale has that id.
    /// </summary>
    /// <param name="id">The row id of the tender sale.</param>
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
        var command = new TenderSaleCommand();
        if (await FindByIdAsync(id, ct) is { } sale)
        {
            command.PopulateFromObject(sale);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the tender sale with row id <paramref name="id"/>, tracked, with its winning tender, its client, and its
    /// item with the item's category; <see langword="null"/> when no tender sale has that id.
    /// </summary>
    /// <param name="id">The row id of the tender sale.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tender sale, or <see langword="null"/>.</returns>
    public async Task<TenderSale?> FindByIdAsync(long id, CancellationToken ct = default) =>
        await Db.Sales
            .OfType<TenderSale>()
            .Include(s => s.Tender)
            .Include(s => s.Client)
            .Include(s => s.Item).ThenInclude(i => i!.ItemCategory)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>
    /// Adds the tender sale validation errors to <paramref name="result"/>; a delete adds none. In order: the sale
    /// rules of <see cref="SaleRules.ValidateAsync"/>; then no winning tender → <see cref="MessageKeys.TenderRequired"/>
    /// (<see cref="FocusFields.Tender"/>), a tender id of 0 counting as none.
    /// Ports <c>TenderSaleTA.doValidate</c> and <c>SaleTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The tender sale values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderSaleCommand"/>.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Sale> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        TenderSaleCommand cmd = AsTenderSaleCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        await SaleRules.ValidateAsync(Db, cmd, validationType, result, ct);

        if (JadeValues.IsNull(cmd.TenderId))
        {
            result.AddError(MessageKeys.TenderRequired, FocusFields.Tender);
        }
    }

    /// <summary>
    /// Takes the locks of the operation in order: the company sale lock of <see cref="SaleRules.AcquireLocksAsync"/>,
    /// then <see cref="LockNames.ClientAllTenderSales"/> of the command's client on create and delete, and on update
    /// only when the command's item differs from the model object's item. A modify takes no lock and reads nothing.
    /// Ports <c>TenderSaleTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and the inherited
    /// <c>SaleTA.lockForModify</c>.
    /// </summary>
    /// <param name="command">The tender sale values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held; <see langword="false"/> at the first lock not obtained.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderSaleCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">A client lock is due and the command has no client.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Sale> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        TenderSaleCommand cmd = AsTenderSaleCommand(command);
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
            || await Locks.TryLockAsync(LockNames.ClientAllTenderSales(RequireClientId(cmd)), ct);
    }

    /// <summary>Returns a new, untracked <see cref="TenderSale"/>.</summary>
    /// <param name="command">The tender sale values being created.</param>
    /// <returns>The new tender sale.</returns>
    protected override Sale CreateModelObject(TransactionCommand<Sale> command) => new TenderSale();

    /// <summary>
    /// Sets the attributes of the new tender sale: the GUID and cleared soft edit lock of the base; the command's
    /// agent commission; the bound company, the command's client and item; the command's winning tender, read with
    /// change tracking; then the agent commission computed by <see cref="Sale.CalculateAgentCommission"/> on the
    /// tender's offer, after loading the commission data with <see cref="SaleRules.LoadForCommissionAsync"/> and the
    /// command's <see cref="SaleCommand{TSale}.CommissionPreload"/>.
    /// Ports <c>Sale.onCreate</c>, <c>Sale.setCommonProperties</c> and <c>TenderSale.onCreate</c>.
    /// </summary>
    /// <param name="entity">The tender sale being created, already tracked as added.</param>
    /// <param name="command">The tender sale values being created.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is not a <see cref="TenderSale"/> or <paramref name="command"/> is not a
    /// <see cref="TenderSaleCommand"/>.
    /// </exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">
    /// The command has no client, no item or no tender, or the item or the tender does not exist.
    /// </exception>
    protected override async Task OnCreateAsync(Sale entity, TransactionCommand<Sale> command, CancellationToken ct)
    {
        TenderSale sale = AsTenderSale(entity);
        TenderSaleCommand cmd = AsTenderSaleCommand(command);

        await base.OnCreateAsync(sale, cmd, ct);
        SetCommonProperties(sale, cmd);

        Company company = await _companyContext.GetCurrentAsync(ct);
        sale.CompanyId = company.Id;
        sale.ClientId = RequireClientId(cmd);
        sale.ItemId = cmd.ItemId ?? throw new InvalidOperationException("The tender sale has no item.");

        long tenderId = cmd.TenderId ?? throw new InvalidOperationException("The tender sale has no tender.");
        Tender tender = await Db.Tenders.FirstOrDefaultAsync(t => t.Id == tenderId, ct)
            ?? throw new InvalidOperationException($"No tender has the id {tenderId}.");
        sale.TenderId = tender.Id;
        sale.Tender = tender;

        await SaleRules.LoadForCommissionAsync(Db, sale, cmd.CommissionPreload, ct);
        sale.CalculateAgentCommission(tender.Offer);
    }

    /// <summary>
    /// Writes the command's agent commission to the tender sale and clears its soft edit lock; the company, client,
    /// item and winning tender are left unchanged. Ports <c>Sale.setCommonProperties</c> as <c>onUpdate</c> calls it.
    /// </summary>
    /// <param name="entity">The tracked tender sale being updated.</param>
    /// <param name="command">The tender sale values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is not a <see cref="TenderSale"/> or <paramref name="command"/> is not a
    /// <see cref="TenderSaleCommand"/>.
    /// </exception>
    protected override async Task OnUpdateAsync(Sale entity, TransactionCommand<Sale> command, CancellationToken ct)
    {
        TenderSale sale = AsTenderSale(entity);
        TenderSaleCommand cmd = AsTenderSaleCommand(command);

        SetCommonProperties(sale, cmd);
        await base.OnUpdateAsync(sale, cmd, ct);
    }

    /// <summary>Writes the agent commission, rounded to 2 decimal places with ties away from zero.</summary>
    private static void SetCommonProperties(TenderSale sale, TenderSaleCommand cmd) =>
        sale.AgentCommission = DecimalRules.Round2(cmd.AgentCommission);

    /// <summary>Returns the command's client id, or throws when it has none.</summary>
    private static long RequireClientId(TenderSaleCommand cmd) =>
        cmd.ClientId ?? throw new InvalidOperationException("The tender sale has no client.");

    /// <summary>Returns <paramref name="command"/> as a <see cref="TenderSaleCommand"/>.</summary>
    private static TenderSaleCommand AsTenderSaleCommand(TransactionCommand<Sale> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as TenderSaleCommand
            ?? throw new ArgumentException(
                $"The command is a {command.GetType().Name}, not a {nameof(TenderSaleCommand)}.",
                nameof(command));
    }

    /// <summary>Returns <paramref name="entity"/> as a <see cref="TenderSale"/>.</summary>
    private static TenderSale AsTenderSale(Sale entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity as TenderSale
            ?? throw new ArgumentException(
                $"The sale is a {entity.GetType().Name}, not a {nameof(TenderSale)}.",
                nameof(entity));
    }
}
