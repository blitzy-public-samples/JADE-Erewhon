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
/// The values one tender operation persists: the offer, the time the tender is placed, the client, the tender item and
/// the tender sale the tender won. Ports the attributes, <c>initialize</c> and <c>populateFromObject</c> of JADE
/// <c>TenderTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class TenderCommand : TransactionCommand<Tender>
{
    private decimal _offer;

    /// <summary>
    /// The amount offered, held rounded to 2 decimal places with ties away from zero; 0 for none.
    /// Ports <c>TenderTA.offer</c>.
    /// </summary>
    public decimal Offer
    {
        get => _offer;
        set => _offer = DecimalRules.Round2(value);
    }

    /// <summary>
    /// The time the tender is placed; <see langword="null"/> or the default timestamp is replaced by the database
    /// server's current timestamp during pre-validation. Ports <c>TenderTA.dateTendered</c>.
    /// </summary>
    public DateTime? DateTendered { get; set; }

    /// <summary>The row id of the client placing the tender; <see langword="null"/> or 0 for none. Ports <c>TenderTA.myClient</c>.</summary>
    public long? ClientId { get; set; }

    /// <summary>The row id of the tender item the tender is placed on; <see langword="null"/> or 0 for none. Ports <c>TenderTA.myTenderItem</c>.</summary>
    public long? TenderItemId { get; set; }

    /// <summary>
    /// The row id of the tender sale that awarded the item to the tender; <see langword="null"/> for none.
    /// Ports <c>TenderTA.myTenderSale</c>.
    /// </summary>
    public long? TenderSaleId { get; set; }

    /// <summary>
    /// Resets the common attributes, then sets <see cref="ClientId"/>, <see cref="TenderSaleId"/>,
    /// <see cref="TenderItemId"/> and <see cref="DateTendered"/> to <see langword="null"/> and <see cref="Offer"/> to 0.
    /// Ports <c>TenderTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        ClientId = null;
        TenderSaleId = null;
        TenderItemId = null;
        Offer = 0m;
        DateTendered = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then copies its client, tender sale, tender item, offer
    /// and time tendered. The tender sale id is that of <see cref="Tender.TenderSale"/>, <see langword="null"/> when the
    /// navigation is not loaded. Ports <c>TenderTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The tender the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Tender modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        base.PopulateFromObject(modelObject);

        ClientId = modelObject.ClientId;
        TenderSaleId = modelObject.TenderSale?.Id;
        TenderItemId = modelObject.TenderItemId;
        Offer = modelObject.Offer;
        DateTendered = modelObject.DateTendered;
    }
}

/// <summary>
/// Creates, updates and deletes the tenders of the bound company through the transaction-agent steps, and reads one
/// tender for the admin screens. Ports JADE <c>TenderTA</c> with the <c>Tender.onCreate</c> and
/// <c>Tender.setCommonProperties</c> methods it drives.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope and keeps no state between operations. Instances are not safe
/// to call from several threads at once.
/// </remarks>
public sealed class TenderService : TransactionAgent<Tender>
{
    private readonly ICompanyContext _companyContext;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock, notification queue and bound company
    /// of its scope.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken for create, update and delete.</param>
    /// <param name="clock">The database server clock that supplies the default time tendered and the closure date check.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TenderService(
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
    /// Deletes the tender with row id <paramref name="id"/>: reads it with <see cref="FindByIdAsync"/>, populates a
    /// <see cref="TenderCommand"/> from it and runs <see cref="TransactionType.Delete"/>. When no tender has that id, the
    /// command has no model object and the delete succeeds with no work. A deleted winning tender leaves its tender sale
    /// in place with no tender.
    /// </summary>
    /// <param name="id">The row id of the tender.</param>
    /// <param name="mode">Whether the delete runs in its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        var command = new TenderCommand();
        if (await FindByIdAsync(id, ct) is { } tender)
        {
            command.PopulateFromObject(tender);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the tender with row id <paramref name="id"/>, tracked, with its client, its tender item with the item's
    /// category and sale, and the tender sale it won; <see langword="null"/> when no tender has that id. The tender is
    /// found by id alone, with no bound-company filter.
    /// </summary>
    /// <param name="id">The row id of the tender.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tender, or <see langword="null"/>.</returns>
    public Task<Tender?> FindByIdAsync(long id, CancellationToken ct = default) =>
        Db.Tenders
            .AsTracking()
            .Include(t => t.Client)
            .Include(t => t.TenderItem).ThenInclude(i => i!.ItemCategory)
            .Include(t => t.TenderItem).ThenInclude(i => i!.Sale)
            .Include(t => t.TenderSale)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// Returns whether a tender with row id <paramref name="id"/> exists, with one scalar query by id alone and no
    /// bound-company filter.
    /// </summary>
    /// <param name="id">The row id of the tender.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the tender exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.Tenders.AnyAsync(t => t.Id == id, ct);

    /// <summary>
    /// Returns the tender with row id <paramref name="id"/>, read without change tracking, holding the tender's own
    /// values, the tender sale it won and a <see cref="Tender.TenderItem"/> that holds every value of the item's own
    /// row except its photo; <see langword="null"/> when no tender has that id. Nothing else is loaded: not the
    /// tender's client, nor any navigation of the tender sale or of the tender item, whose
    /// <see cref="TenderItem.Tenders"/> is empty and whose <see cref="Item.Photo"/> is <see langword="null"/>. The
    /// tender is found by id alone, with no bound-company filter.
    /// </summary>
    /// <param name="id">The row id of the tender.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tender, or <see langword="null"/>.</returns>
    public Task<Tender?> FindForStatusAsync(long id, CancellationToken ct = default) =>
        Db.Tenders
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new Tender
            {
                Id = t.Id,
                Guid = t.Guid,
                LockedBySession = t.LockedBySession,
                LockedTimestamp = t.LockedTimestamp,
                CompanyId = t.CompanyId,
                ClientId = t.ClientId,
                TenderItemId = t.TenderItemId,
                Offer = t.Offer,
                DateTendered = t.DateTendered,
                TenderSale = t.TenderSale,
                TenderItem = new TenderItem
                {
                    Id = t.TenderItem!.Id,
                    Guid = t.TenderItem.Guid,
                    LockedBySession = t.TenderItem.LockedBySession,
                    LockedTimestamp = t.TenderItem.LockedTimestamp,
                    CompanyId = t.TenderItem.CompanyId,
                    CodeNumber = t.TenderItem.CodeNumber,
                    Name = t.TenderItem.Name,
                    Description = t.TenderItem.Description,
                    ListedDate = t.TenderItem.ListedDate,
                    Price = t.TenderItem.Price,
                    AgentId = t.TenderItem.AgentId,
                    ItemCategoryId = t.TenderItem.ItemCategoryId,
                    RegionId = t.TenderItem.RegionId,
                    ClosureDate = t.TenderItem.ClosureDate,
                },
            })
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Returns the projection of the tender with row id <paramref name="id"/>, the values
    /// <see cref="TenderProjection.From"/> gives for the tender read with every navigation it reads, with the tender
    /// item's <see cref="ItemProjection.Photo"/> <see langword="null"/>. Two reads without change tracking: the
    /// tender's own values with its client and the row id of the tender sale it won, then its tender item through
    /// <see cref="ItemProjection.Metadata"/>, which reads neither the item's photo nor its tenders.
    /// <see langword="null"/> when no tender has that id or no tender item has the tender's item id. The tender is
    /// found by id alone, with no bound-company filter.
    /// Ports <c>DTOTender.populateFromObject</c> and the tender read of <c>ViewTenderForm</c>.
    /// </summary>
    /// <param name="id">The row id of the tender.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The projection, or <see langword="null"/>.</returns>
    public async Task<TenderProjection?> GetTenderProjectionAsync(long id, CancellationToken ct = default)
    {
        var tender = await Db.Tenders
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.Guid,
                t.Offer,
                t.DateTendered,
                Client = t.Client!,
                t.TenderItemId,
                TenderSaleId = t.TenderSale == null ? null : (long?)t.TenderSale.Id,
            })
            .FirstOrDefaultAsync(ct);

        if (tender is null)
        {
            return null;
        }

        ItemProjection? tenderItem = await Db.Items
            .AsNoTracking()
            .Where(item => item.Id == tender.TenderItemId && item is TenderItem)
            .Select(ItemProjection.Metadata)
            .FirstOrDefaultAsync(ct);

        return tenderItem is null
            ? null
            : new TenderProjection(
                tender.Id,
                tender.Guid,
                tender.Offer,
                tender.DateTendered,
                ClientProjection.From(tender.Client),
                tenderItem,
                tender.TenderSaleId);
    }

    /// <summary>
    /// Sets <see cref="TenderCommand.DateTendered"/> to the database server's current timestamp when it is
    /// <see langword="null"/> or invalid. Ports <c>TenderTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The tender values to prepare.</param>
    /// <param name="ct">Token that cancels the clock read.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderCommand"/>.</exception>
    protected override async Task PreValidateCoreAsync(TransactionCommand<Tender> command, CancellationToken ct)
    {
        TenderCommand cmd = AsTenderCommand(command);
        if (JadeValues.IsNull(cmd.DateTendered) || !JadeValues.IsValid(cmd.DateTendered))
        {
            cmd.DateTendered = await Clock.GetServerTimeStampAsync(ct);
        }
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds, in this order:
    /// <list type="number">
    /// <item><description>no tender item → <see cref="MessageKeys.TenderItemRequired"/>
    /// (<see cref="FocusFields.TenderItem"/>);</description></item>
    /// <item><description>with a tender item: a sold item → <see cref="MessageKeys.TenderItemAlreadySold"/>
    /// (<see cref="FocusFields.TenderItem"/>), and no further rule is checked; a closed item
    /// (<see cref="TenderItem.IsClosed"/> at the server date) → <see cref="MessageKeys.TenderItemAlreadyClosed"/>
    /// (<see cref="FocusFields.TenderItem"/>), and no further rule is checked; an offer of 0 or below →
    /// <see cref="MessageKeys.TenderOfferLessThanZero"/>, otherwise an offer below the reserve price →
    /// <see cref="MessageKeys.TenderOfferLessThanReserve"/> (both <see cref="FocusFields.TenderItem"/>), an offer equal
    /// to the reserve being accepted; then, when the command has a client and a non-zero offer, one
    /// <see cref="MessageKeys.TenderAlreadyPlaced"/> (<see cref="FocusFields.Offer"/>) for each tender of the item by
    /// the same client with the same offer, the tender being updated included;</description></item>
    /// <item><description>no client → <see cref="MessageKeys.ClientRequired"/> (<see cref="FocusFields.Client"/>);</description></item>
    /// <item><description>a <see langword="null"/> or invalid time tendered → <see cref="MessageKeys.TimeStampInvalid"/>
    /// (<see cref="FocusFields.Timestamp"/>).</description></item>
    /// </list>
    /// A client or tender item id of 0 counts as none. The item, its sale and its tenders are read as stored, with the
    /// sales and tenders the change tracker holds as added counted and those it holds as deleted left out. A tender
    /// whose time and offer equal those of a tender on another item of the company is not refused here; its save fails
    /// on the database's unique key. Ports <c>TenderTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The tender values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command's tender item id names no tender item.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Tender> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        TenderCommand cmd = AsTenderCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        if (JadeValues.IsNull(cmd.TenderItemId))
        {
            result.AddError(MessageKeys.TenderItemRequired, FocusFields.TenderItem);
        }
        else
        {
            long tenderItemId = cmd.TenderItemId!.Value;
            TenderItem tenderItem = await ReadTenderItemAsync(tenderItemId, ct);

            if (await TenderItemHasSaleAsync(tenderItemId, ct))
            {
                result.AddError(MessageKeys.TenderItemAlreadySold, FocusFields.TenderItem);
                return;
            }

            if (tenderItem.IsClosed(await Clock.GetServerDateAsync(ct)))
            {
                result.AddError(MessageKeys.TenderItemAlreadyClosed, FocusFields.TenderItem);
                return;
            }

            if (cmd.Offer <= 0m)
            {
                result.AddError(MessageKeys.TenderOfferLessThanZero, FocusFields.TenderItem);
            }
            else if (cmd.Offer < tenderItem.Price)
            {
                result.AddError(MessageKeys.TenderOfferLessThanReserve, FocusFields.TenderItem);
            }

            if (!JadeValues.IsNull(cmd.ClientId) && !JadeValues.IsNull(cmd.Offer))
            {
                int matches = await CountSameOfferTendersAsync(tenderItemId, cmd.ClientId!.Value, cmd.Offer, ct);
                for (int match = 0; match < matches; match++)
                {
                    result.AddError(MessageKeys.TenderAlreadyPlaced, FocusFields.Offer);
                }
            }
        }

        if (JadeValues.IsNull(cmd.ClientId))
        {
            result.AddError(MessageKeys.ClientRequired, FocusFields.Client);
        }

        if (JadeValues.IsNull(cmd.DateTendered) || !JadeValues.IsValid(cmd.DateTendered))
        {
            result.AddError(MessageKeys.TimeStampInvalid, FocusFields.Timestamp);
        }
    }

    /// <summary>
    /// Takes the locks of the operation in order, returning <see langword="false"/> at the first lock not obtained:
    /// for <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>,
    /// <see cref="LockNames.ClientAllTenders"/> of the command's client, then
    /// <see cref="LockNames.TenderItemAllTendersByOfferTime"/> and <see cref="LockNames.TenderItemAllTendersByTimeOffer"/>
    /// of the command's tender item; for <see cref="ValidationType.Update"/>, the client lock, then the two tender-item
    /// locks only when the command's tender sale or tender item differs from the model object's (a missing model object
    /// counting as a difference); for <see cref="ValidationType.Modify"/>, none.
    /// Ports <c>TenderTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The tender values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderCommand"/>.</exception>
    /// <exception cref="NoCompanyException">
    /// The command's client or tender item is <see langword="null"/> or 0 when its lock is reached: the 1090
    /// null-object-reference error.
    /// </exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Tender> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        TenderCommand cmd = AsTenderCommand(command);

        switch (validationType)
        {
            case ValidationType.Create:
            case ValidationType.Delete:
                return await Locks.TryLockAsync(LockNames.ClientAllTenders(RequireReference(cmd.ClientId)), ct)
                    && await TryLockTenderItemTendersAsync(cmd, ct);

            case ValidationType.Update:
                if (!await Locks.TryLockAsync(LockNames.ClientAllTenders(RequireReference(cmd.ClientId)), ct))
                {
                    return false;
                }

                return !TenderItemKeyChanges(cmd) || await TryLockTenderItemTendersAsync(cmd, ct);

            default:
                return true;
        }
    }

    /// <summary>Returns a new, untracked <see cref="Tender"/>. Ports <c>TenderTA.getModelObjectClass</c>.</summary>
    /// <param name="command">The tender values being created.</param>
    /// <returns>The new tender.</returns>
    protected override Tender CreateModelObject(TransactionCommand<Tender> command) => new();

    /// <summary>
    /// Sets the attributes of the new tender: the GUID (the command's GUID, or a new one when it is empty) and the
    /// cleared soft edit lock of the base; the offer, rounded to 2 decimal places with ties away from zero, and the
    /// time tendered of the command; the command's client and tender item; and the bound company.
    /// Ports <c>Tender.onCreate</c> and <c>Tender.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tender being created, already tracked as added.</param>
    /// <param name="command">The tender values being created.</param>
    /// <param name="ct">Token that cancels the company read.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">The command has no client or no tender item.</exception>
    protected override async Task OnCreateAsync(Tender entity, TransactionCommand<Tender> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        TenderCommand cmd = AsTenderCommand(command);

        await base.OnCreateAsync(entity, cmd, ct);
        SetCommonProperties(entity, cmd);

        entity.ClientId = cmd.ClientId ?? throw new InvalidOperationException("The tender has no client.");
        entity.TenderItemId = cmd.TenderItemId ?? throw new InvalidOperationException("The tender has no tender item.");
        entity.CompanyId = (await _companyContext.GetCurrentAsync(ct)).Id;
    }

    /// <summary>
    /// Writes the command's offer, rounded to 2 decimal places with ties away from zero, and time tendered to the tender
    /// and clears its soft edit lock; the company, client and tender item are left unchanged.
    /// Ports <c>Tender.setCommonProperties</c> as <c>onUpdate</c> calls it.
    /// </summary>
    /// <param name="entity">The tracked tender being updated.</param>
    /// <param name="command">The tender values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="TenderCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Tender entity, TransactionCommand<Tender> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        TenderCommand cmd = AsTenderCommand(command);

        SetCommonProperties(entity, cmd);
        await base.OnUpdateAsync(entity, cmd, ct);
    }

    /// <summary>
    /// Loads, with change tracking, the tender sale that awarded the item to the tender when it is not loaded; the save
    /// then clears the sale's tender reference as a tracked change and reports the sale as updated. The database
    /// deletes no other row with the tender.
    /// </summary>
    /// <param name="entity">The tracked tender about to be deleted.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>A task that completes when the tender sale is loaded.</returns>
    protected override async Task DeleteCascadeNotificationsAsync(Tender entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var tenderSale = Db.Entry(entity).Reference(t => t.TenderSale);
        if (!tenderSale.IsLoaded)
        {
            await tenderSale.LoadAsync(ct);
        }
    }

    /// <summary>
    /// Writes the offer, rounded to 2 decimal places with ties away from zero, and the time tendered with
    /// <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    private static void SetCommonProperties(Tender tender, TenderCommand cmd)
    {
        tender.Offer = DecimalRules.Round2(cmd.Offer);
        tender.DateTendered = DateTime.SpecifyKind(cmd.DateTendered.GetValueOrDefault(), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Returns whether the command's tender sale or tender item differs from the model object's; a missing model object
    /// counts as a difference.
    /// </summary>
    private static bool TenderItemKeyChanges(TenderCommand cmd) =>
        cmd.ModelObject is not { } model
        || cmd.TenderSaleId != model.TenderSale?.Id
        || cmd.TenderItemId != model.TenderItemId;

    /// <summary>Returns the id, or raises the 1090 null-object-reference error when it is <see langword="null"/> or 0.</summary>
    private static long RequireReference(long? id) =>
        JadeValues.IsNull(id) ? throw new NoCompanyException() : id!.Value;

    /// <summary>Returns <paramref name="command"/> as a <see cref="TenderCommand"/>.</summary>
    private static TenderCommand AsTenderCommand(TransactionCommand<Tender> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as TenderCommand
            ?? throw new ArgumentException(
                $"The command is a {command.GetType().Name}, not a {nameof(TenderCommand)}.",
                nameof(command));
    }

    /// <summary>
    /// Takes <see cref="LockNames.TenderItemAllTendersByOfferTime"/>, then
    /// <see cref="LockNames.TenderItemAllTendersByTimeOffer"/>, of the command's tender item.
    /// </summary>
    private async Task<bool> TryLockTenderItemTendersAsync(TenderCommand cmd, CancellationToken ct)
    {
        long tenderItemId = RequireReference(cmd.TenderItemId);
        return await Locks.TryLockAsync(LockNames.TenderItemAllTendersByOfferTime(tenderItemId), ct)
            && await Locks.TryLockAsync(LockNames.TenderItemAllTendersByTimeOffer(tenderItemId), ct);
    }

    /// <summary>
    /// Reads, without change tracking, the reserve price and closure date of the tender item with row id
    /// <paramref name="tenderItemId"/>, returned as an untracked <see cref="TenderItem"/> holding only its id, price and
    /// closure date. An id that names no tender item raises <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task<TenderItem> ReadTenderItemAsync(long tenderItemId, CancellationToken ct)
    {
        var stored = await Db.Items
            .OfType<TenderItem>()
            .AsNoTracking()
            .Where(item => item.Id == tenderItemId)
            .Select(item => new { item.Price, item.ClosureDate })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No tender item has the id {tenderItemId}.");

        return new TenderItem { Id = tenderItemId, Price = stored.Price, ClosureDate = stored.ClosureDate };
    }

    /// <summary>
    /// Returns whether the item has a sale: an added sale in the change tracker, or a stored sale not marked as deleted
    /// in the change tracker.
    /// </summary>
    private async Task<bool> TenderItemHasSaleAsync(long tenderItemId, CancellationToken ct)
    {
        var trackedSales = Db.ChangeTracker.Entries<Sale>().ToList();
        if (trackedSales.Exists(entry => entry.State == EntityState.Added && entry.Entity.ItemId == tenderItemId))
        {
            return true;
        }

        var deletedSaleIds = trackedSales
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        List<long> storedSaleIds = await Db.Sales
            .AsNoTracking()
            .Where(sale => sale.ItemId == tenderItemId)
            .Select(sale => sale.Id)
            .ToListAsync(ct);

        return storedSaleIds.Exists(id => !deletedSaleIds.Contains(id));
    }

    /// <summary>
    /// Returns the number of tenders on the tender item by the client whose offer equals <paramref name="offer"/>: the
    /// stored tenders not marked as deleted in the change tracker, plus the matching tenders the change tracker holds as
    /// added.
    /// </summary>
    private async Task<int> CountSameOfferTendersAsync(
        long tenderItemId,
        long clientId,
        decimal offer,
        CancellationToken ct)
    {
        var trackedTenders = Db.ChangeTracker.Entries<Tender>().ToList();
        int added = trackedTenders.Count(entry =>
            entry.State == EntityState.Added
            && entry.Entity.TenderItemId == tenderItemId
            && entry.Entity.ClientId == clientId
            && entry.Entity.Offer == offer);

        var deletedTenderIds = trackedTenders
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        List<long> storedTenderIds = await Db.Tenders
            .AsNoTracking()
            .Where(tender => tender.TenderItemId == tenderItemId && tender.ClientId == clientId && tender.Offer == offer)
            .Select(tender => tender.Id)
            .ToListAsync(ct);

        return storedTenderIds.Count(id => !deletedTenderIds.Contains(id)) + added;
    }
}

