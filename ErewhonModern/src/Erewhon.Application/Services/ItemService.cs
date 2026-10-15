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
/// The values one item operation persists: the code number, name, description, listed date, photo and price of a
/// retail or tender item, and the ids of its agent, category and region. Ports the attributes of JADE <c>ItemTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public abstract class ItemCommand : TransactionCommand<Item>
{
    private decimal _price;

    /// <summary>
    /// The code number of the item; 0 (the JADE null value) has the next code number of the bound company allocated
    /// during pre-validation.
    /// </summary>
    public int CodeNumber { get; set; }

    /// <summary>The item name, at most 31 characters as given; trimmed of white space during pre-validation.</summary>
    public string? Name { get; set; }

    /// <summary>The item description, unbounded; trimmed of white space during pre-validation.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The date the item is listed; <see langword="null"/> or the default date is replaced by the server date during
    /// pre-validation.
    /// </summary>
    public DateOnly? ListedDate { get; set; }

    /// <summary>
    /// The picture of the item; <see langword="null"/> or an empty array for no picture. A picture must be a bitmap,
    /// icon, metafile, cursor, TIFF, JPEG or PNG.
    /// </summary>
    public byte[]? Photo { get; set; }

    /// <summary>
    /// The price of the item, or the reserve price of a tender item; stored rounded to 2 decimal places with ties away
    /// from zero. 0 (the JADE null value) and negative prices are rejected.
    /// </summary>
    public decimal Price
    {
        get => _price;
        set => _price = DecimalRules.Round2(value);
    }

    /// <summary>The row id of the agent selling the item; required except on delete.</summary>
    public long? AgentId { get; set; }

    /// <summary>The row id of the item's category; required except on delete.</summary>
    public long? ItemCategoryId { get; set; }

    /// <summary>The row id of the region the item is listed in; required except on delete.</summary>
    public long? RegionId { get; set; }

    /// <summary>
    /// Resets the common attributes, then sets <see cref="CodeNumber"/> and <see cref="Price"/> to 0 and every other
    /// item attribute to <see langword="null"/>. Ports <c>ItemTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        CodeNumber = 0;
        Description = null;
        ListedDate = null;
        AgentId = null;
        RegionId = null;
        ItemCategoryId = null;
        Photo = null;
        Price = 0m;
        Name = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then copies its code number, listed date, description,
    /// agent, region, category, photo, price and name. Ports <c>ItemTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The item the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Item modelObject)
    {
        base.PopulateFromObject(modelObject);

        CodeNumber = modelObject.CodeNumber;
        ListedDate = modelObject.ListedDate;
        Description = modelObject.Description;
        AgentId = modelObject.AgentId;
        RegionId = modelObject.RegionId;
        ItemCategoryId = modelObject.ItemCategoryId;
        Photo = modelObject.Photo;
        Price = modelObject.Price;
        Name = modelObject.Name;
    }
}

/// <summary>
/// The values one retail item operation persists; a create through it adds a <see cref="RetailItem"/>.
/// Ports JADE <c>RetailItemTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class RetailItemCommand : ItemCommand
{
}

/// <summary>
/// The values one tender item operation persists: the item values and the closure date; a create through it adds a
/// <see cref="TenderItem"/>. Ports JADE <c>TenderItemTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class TenderItemCommand : ItemCommand
{
    /// <summary>
    /// The date tendering on the item closes; <see langword="null"/> or the default date is rejected except on delete.
    /// </summary>
    public DateOnly? ClosureDate { get; set; }

    /// <summary>
    /// Resets the item attributes, then sets <see cref="ClosureDate"/> to <see langword="null"/>.
    /// Ports <c>TenderItemTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        ClosureDate = null;
    }

    /// <summary>
    /// Copies the item values of <paramref name="modelObject"/> and makes it the model object, then copies its closure
    /// date. Ports <c>TenderItemTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The tender item the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelObject"/> is not a <see cref="TenderItem"/>.</exception>
    public override void PopulateFromObject(Item modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        if (modelObject is not TenderItem tenderItem)
        {
            throw new ArgumentException("A tender item command is populated from a tender item only.", nameof(modelObject));
        }

        base.PopulateFromObject(tenderItem);
        ClosureDate = tenderItem.ClosureDate;
    }
}

/// <summary>
/// Creates, updates and deletes retail and tender items through the transaction-agent steps of JADE <c>ItemTA</c>,
/// <c>RetailItemTA</c> and <c>TenderItemTA</c>, and reads the bound company's items. Pre-validation allocates a code
/// number of 0 through <see cref="ItemCodeAllocator"/>, defaults the listed date to the server date and trims the name
/// and description; validation applies the required-field, picture, price and closure-date rules in the JADE order;
/// create, update and delete take the item collection locks in the JADE order.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope and keeps no state between operations. Instances are not safe
/// to call from several threads at once.
/// </remarks>
public sealed class ItemService : TransactionAgent<Item>
{
    private readonly ICompanyContext _companyContext;
    private readonly ItemCodeAllocator _allocator;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the bound company, the code allocator and the notification deriver.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken for create, update and delete.</param>
    /// <param name="clock">The database server clock that supplies the default listed date.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="allocator">The allocator of item code numbers.</param>
    /// <param name="deriver">The deriver that records the notifications of the rows a delete removes with the item.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ItemService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext,
        ItemCodeAllocator allocator,
        NotificationDeriver deriver)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(deriver);

        _companyContext = companyContext;
        _allocator = allocator;
        _deriver = deriver;
    }

    /// <summary>
    /// Checks the GUID (39) and soft-lock session (64) through the base, then the item name (31), as given and before
    /// trimming; the description is unbounded.
    /// </summary>
    /// <param name="command">The values to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Item> command)
    {
        base.CheckFieldLengths(command);
        ItemCommand item = AsItemCommand(command);
        FieldLengths.Check(item.Name, FieldLengths.ItemName, nameof(ItemCommand.Name));
    }

    /// <summary>
    /// For every operation: replaces a code number of 0 with the next code number of the bound company, replaces a
    /// null or default listed date with the server date, then trims white space from the description and the name.
    /// Ports <c>ItemTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The values to prepare.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    /// <exception cref="NoCompanyException">A code number is allocated and no company is bound.</exception>
    /// <exception cref="ErewhonException">
    /// The allocation lock was not obtained in three attempts (<see cref="ErewhonException.LockNotObtained"/>, 500001).
    /// </exception>
    protected override async Task PreValidateCoreAsync(TransactionCommand<Item> command, CancellationToken ct)
    {
        ItemCommand item = AsItemCommand(command);

        if (JadeValues.IsNull(item.CodeNumber))
        {
            item.CodeNumber = await _allocator.AllocateNextCodeNumberAsync(await GetCompanyIdAsync(ct), ct);
        }

        if (JadeValues.IsNull(item.ListedDate) || !JadeValues.IsValid(item.ListedDate))
        {
            item.ListedDate = await Clock.GetServerDateAsync(ct);
        }

        item.Description = JadeValues.TrimWhiteSpace(item.Description);
        item.Name = JadeValues.TrimWhiteSpace(item.Name);
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds, in this order: a code number of 0 →
    /// <see cref="MessageKeys.ItemNumberRequired"/>; no agent → <see cref="MessageKeys.AgentRequired"/>; no category →
    /// <see cref="MessageKeys.ItemCategoryRequired"/>; no region → <see cref="MessageKeys.RegionRequired"/>; an empty
    /// name → <see cref="MessageKeys.ItemNameRequired"/>; an empty description →
    /// <see cref="MessageKeys.ItemDescriptionRequired"/>; a photo that is not a valid picture →
    /// <see cref="MessageKeys.ImageNotValid"/>; a price of 0 or below → <see cref="MessageKeys.ItemPriceNotValid"/>;
    /// then, for a tender item, a null or default closure date → <see cref="MessageKeys.TenderItemClosureDateNotValid"/>.
    /// Ports <c>ItemTA.doValidate</c> and <c>TenderItemTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    protected override Task ValidateCoreAsync(
        TransactionCommand<Item> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        ItemCommand item = AsItemCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return Task.CompletedTask;
        }

        if (JadeValues.IsNull(item.CodeNumber))
        {
            result.AddError(MessageKeys.ItemNumberRequired, FocusFields.CodeNumber);
        }

        if (JadeValues.IsNull(item.AgentId))
        {
            result.AddError(MessageKeys.AgentRequired, FocusFields.Agent);
        }

        if (JadeValues.IsNull(item.ItemCategoryId))
        {
            result.AddError(MessageKeys.ItemCategoryRequired, FocusFields.ItemCategory);
        }

        if (JadeValues.IsNull(item.RegionId))
        {
            result.AddError(MessageKeys.RegionRequired, FocusFields.Region);
        }

        if (JadeValues.IsNull(item.Name))
        {
            result.AddError(MessageKeys.ItemNameRequired, FocusFields.ItemName);
        }

        if (JadeValues.IsNull(item.Description))
        {
            result.AddError(MessageKeys.ItemDescriptionRequired, FocusFields.ItemDescription);
        }

        if (!JadeValues.IsNull(item.Photo) && !PictureValidator.IsValidPicture(item.Photo))
        {
            result.AddError(MessageKeys.ImageNotValid, FocusFields.Photo);
        }

        if (JadeValues.IsNull(item.Price) || item.Price < 0m)
        {
            result.AddError(MessageKeys.ItemPriceNotValid, FocusFields.Price);
        }

        if (item is TenderItemCommand tender
            && (JadeValues.IsNull(tender.ClosureDate) || !JadeValues.IsValid(tender.ClosureDate)))
        {
            result.AddError(MessageKeys.TenderItemClosureDateNotValid, FocusFields.Date);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// For <see cref="ValidationType.Create"/>, <see cref="ValidationType.Update"/> and
    /// <see cref="ValidationType.Delete"/>, takes in this order the locks on the bound company's items, the agent's
    /// items, the agent's sold items, the region's items and the category's items, then, for a tender item, the bound
    /// company's tender items by sale date, returning <see langword="false"/> at the first lock not obtained; takes
    /// none for <see cref="ValidationType.Modify"/>. Ports <c>ItemTA.lockForCreate</c>, <c>lockForUpdate</c>,
    /// <c>lockForDelete</c> and <c>lockForModify</c>, and the <c>TenderItemTA</c> lock methods.
    /// </summary>
    /// <param name="command">The values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    /// <exception cref="NoCompanyException">
    /// No company is bound, or the agent, region or category of the command is <see langword="null"/> when its lock is
    /// reached: the 1090 null-object-reference error.
    /// </exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Item> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        ItemCommand item = AsItemCommand(command);

        if (validationType is not (ValidationType.Create or ValidationType.Update or ValidationType.Delete))
        {
            return true;
        }

        long companyId = await GetCompanyIdAsync(ct);
        if (!await Locks.TryLockAsync(LockNames.CompanyAllItems(companyId), ct))
        {
            return false;
        }

        long agentId = RequireReference(item.AgentId);
        if (!await Locks.TryLockAsync(LockNames.AgentAllItems(agentId), ct))
        {
            return false;
        }

        if (!await Locks.TryLockAsync(LockNames.AgentAllSoldItems(agentId), ct))
        {
            return false;
        }

        long regionId = RequireReference(item.RegionId);
        if (!await Locks.TryLockAsync(LockNames.RegionAllItems(regionId), ct))
        {
            return false;
        }

        long itemCategoryId = RequireReference(item.ItemCategoryId);
        if (!await Locks.TryLockAsync(LockNames.ItemCategoryAllItems(itemCategoryId), ct))
        {
            return false;
        }

        if (item is TenderItemCommand
            && !await Locks.TryLockAsync(LockNames.CompanyAllTenderItemsBySaleDate(companyId), ct))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a new <see cref="TenderItem"/> for a <see cref="TenderItemCommand"/> and a new <see cref="RetailItem"/>
    /// otherwise. Ports <c>RetailItemTA.getModelObjectClass</c> and <c>TenderItemTA.getModelObjectClass</c>.
    /// </summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new, untracked item.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    protected override Item CreateModelObject(TransactionCommand<Item> command) =>
        AsItemCommand(command) is TenderItemCommand ? new TenderItem() : new RetailItem();

    /// <summary>
    /// Sets the GUID (the command's GUID, or a new one when it is empty) and clears the soft edit lock through the base,
    /// makes the bound company the item's company, then writes the command's item values.
    /// Ports <c>Item.onCreate</c> with <c>Item.setCommonProperties</c> and <c>TenderItem.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The item being created, already tracked as added.</param>
    /// <param name="command">The values being created.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    protected override async Task OnCreateAsync(Item entity, TransactionCommand<Item> command, CancellationToken ct)
    {
        await base.OnCreateAsync(entity, command, ct);
        ItemCommand item = AsItemCommand(command);

        entity.CompanyId = await GetCompanyIdAsync(ct);
        SetCommonProperties(entity, item);
    }

    /// <summary>
    /// Clears the soft edit lock through the base, then writes the command's item values.
    /// Ports <c>PersistentModel.onUpdate</c> with <c>Item.setCommonProperties</c> and
    /// <c>TenderItem.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked item being updated.</param>
    /// <param name="command">The values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Item entity, TransactionCommand<Item> command, CancellationToken ct)
    {
        await base.OnUpdateAsync(entity, command, ct);
        SetCommonProperties(entity, AsItemCommand(command));
    }

    /// <summary>
    /// Records the deleted-row notifications of the rows the database deletes with the item: its sale, then, for a
    /// tender item, each of its tenders in id order. The rows are read without change tracking.
    /// </summary>
    /// <param name="entity">The tracked item about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    protected override async Task DeleteCascadeNotificationsAsync(Item entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long itemId = entity.Id;

        var removed = new List<PersistentModel>();
        removed.AddRange(await Db.Sales
            .AsNoTracking()
            .Include(sale => sale.Item)
            .Where(sale => sale.ItemId == itemId)
            .ToListAsync(ct));

        if (entity is TenderItem)
        {
            removed.AddRange(await Db.Tenders
                .AsNoTracking()
                .Where(tender => tender.TenderItemId == itemId)
                .OrderBy(tender => tender.Id)
                .ToListAsync(ct));
        }

        _deriver.RecordCascadeDeleted(removed);
    }

    /// <summary>
    /// Deletes the item with row id <paramref name="id"/> in the agent's own transaction, as
    /// <see cref="DeleteByIdAsync(long, TransactionMode, CancellationToken)"/> does with
    /// <see cref="TransactionMode.Own"/>.
    /// </summary>
    /// <param name="id">The row id of the item.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete; succeeded with no work when no item has that id.</returns>
    /// <exception cref="NoCompanyException">
    /// No company is bound, or the item has no agent or no region: the 1090 null-object-reference error.
    /// </exception>
    public Task<OperationResult> DeleteByIdAsync(long id, CancellationToken ct) =>
        DeleteByIdAsync(id, TransactionMode.Own, ct);

    /// <summary>
    /// Deletes the item with row id <paramref name="id"/>: reads it with change tracking, populates a
    /// <see cref="TenderItemCommand"/> for a tender item or a <see cref="RetailItemCommand"/> for a retail item from it,
    /// and runs <see cref="TransactionType.Delete"/>. The database deletes the item's sale and tenders with it. When no
    /// item has that id, the command has no model object and the delete succeeds with no work.
    /// </summary>
    /// <param name="id">The row id of the item.</param>
    /// <param name="mode">Whether the delete runs in its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NoCompanyException">
    /// No company is bound, or the item has no agent or no region: the 1090 null-object-reference error.
    /// </exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        Item? item = await Db.Items
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        ItemCommand command = item is TenderItem ? new TenderItemCommand() : new RetailItemCommand();
        if (item is not null)
        {
            command.PopulateFromObject(item);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the item with row id <paramref name="id"/>, tracked, with its category, its region and the region's
    /// country, its agent with the agent's address, its sale with the sale's client (and, for a tender sale, its
    /// winning tender) and, for a tender item, its tenders with their clients; <see langword="null"/> when no item has
    /// that id.
    /// </summary>
    /// <param name="id">The row id of the item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The item, or <see langword="null"/>.</returns>
    public Task<Item?> FindByIdAsync(long id, CancellationToken ct = default) =>
        IncludeDetails(Db.Items.AsTracking())
            .FirstOrDefaultAsync(item => item.Id == id, ct);

    /// <summary>
    /// Returns whether an item of either kind with row id <paramref name="id"/> exists, with one scalar query by id
    /// alone and no bound-company filter.
    /// </summary>
    /// <param name="id">The row id of the item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the item exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.Items.AnyAsync(item => item.Id == id, ct);

    /// <summary>
    /// Returns the projections of the bound company's items whose price lies between <paramref name="minPrice"/> and
    /// <paramref name="maxPrice"/>, both included, in code-number order; an empty list when there are none. The bounds
    /// are not checked here. Ports the item walk of the REST <c>getItemsByPrice</c> operation.
    /// </summary>
    /// <param name="minPrice">The lowest price included.</param>
    /// <param name="maxPrice">The highest price included.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The matching items, each projected by <see cref="ItemProjection.From"/>.</returns>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    public async Task<IReadOnlyList<ItemProjection>> GetItemsByPriceAsync(
        decimal minPrice,
        decimal maxPrice,
        CancellationToken ct = default)
    {
        long companyId = await GetCompanyIdAsync(ct);

        List<Item> items = await IncludeDetails(Db.Items.AsNoTrackingWithIdentityResolution())
            .Where(item => item.CompanyId == companyId && item.Price >= minPrice && item.Price <= maxPrice)
            .OrderBy(item => item.CodeNumber)
            .ToListAsync(ct);

        return items.ConvertAll(ItemProjection.From);
    }

    /// <summary>
    /// Returns the bound company's items, or only those of the category with row id <paramref name="itemCategoryId"/>
    /// when it is given, ordered by category name and then by code number, with the navigations
    /// <see cref="FindByIdAsync"/> loads, read without change tracking. Ports the item lists of
    /// <c>ItemsListByCategoryForm</c>.
    /// </summary>
    /// <param name="itemCategoryId">The row id of the category to list, or <see langword="null"/> for every category.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The items.</returns>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    public async Task<IReadOnlyList<Item>> GetItemsByCategoryAsync(long? itemCategoryId, CancellationToken ct = default)
    {
        long companyId = await GetCompanyIdAsync(ct);

        IQueryable<Item> items = Db.Items
            .AsNoTrackingWithIdentityResolution()
            .Where(item => item.CompanyId == companyId);

        if (itemCategoryId is long categoryId)
        {
            items = items.Where(item => item.ItemCategoryId == categoryId);
        }

        return await IncludeDetails(items)
            .OrderBy(item => item.ItemCategory!.Name)
            .ThenBy(item => item.CodeNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the photo of the item with row id <paramref name="id"/>; <see langword="null"/> when the item has no
    /// photo or no item has that id.
    /// </summary>
    /// <param name="id">The row id of the item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The photo bytes, or <see langword="null"/>.</returns>
    public Task<byte[]?> GetPhotoAsync(long id, CancellationToken ct = default) =>
        Db.Items
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Photo)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Returns the tenders the client with row id <paramref name="clientId"/> has placed on the tender item with row
    /// id <paramref name="itemId"/>, by time tendered and then offer descending, read without change tracking; an empty
    /// list when there are none.
    /// </summary>
    /// <param name="itemId">The row id of the tender item.</param>
    /// <param name="clientId">The row id of the client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The client's tenders on the item.</returns>
    public async Task<IReadOnlyList<Tender>> GetClientTendersOnItemAsync(
        long itemId,
        long clientId,
        CancellationToken ct = default) =>
        await Db.Tenders
            .AsNoTracking()
            .Where(tender => tender.TenderItemId == itemId && tender.ClientId == clientId)
            .OrderBy(tender => tender.DateTendered)
            .ThenByDescending(tender => tender.Offer)
            .ToListAsync(ct);

    /// <summary>
    /// Adds to <paramref name="items"/> the navigations an item read loads: the category, the region and its country,
    /// the agent, the sale and its client, and a tender item's tenders and their clients. A tender sale's winning
    /// tender is one of the item's tenders and is connected to the sale by the query.
    /// </summary>
    private static IQueryable<Item> IncludeDetails(IQueryable<Item> items) =>
        items
            .Include(item => item.ItemCategory)
            .Include(item => item.Region).ThenInclude(region => region!.Country)
            .Include(item => item.Agent)
            .Include(item => item.Sale).ThenInclude(sale => sale!.Client)
            .Include(item => ((TenderItem)item).Tenders).ThenInclude(tender => tender.Client);

    /// <summary>
    /// Writes the command's item values to the item: category, code number, description, region, photo (an empty
    /// array as no photo), price rounded to 2 decimal places, name, listed date and agent, then, for a tender item
    /// written from a tender item command, the closure date.
    /// Ports <c>Item.setCommonProperties</c> and <c>TenderItem.setCommonProperties</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The category or the listed date is not set.</exception>
    private static void SetCommonProperties(Item entity, ItemCommand command)
    {
        entity.ItemCategoryId = command.ItemCategoryId
            ?? throw new InvalidOperationException("The item command has no category.");
        entity.CodeNumber = command.CodeNumber;
        entity.Description = command.Description ?? string.Empty;
        entity.RegionId = command.RegionId;
        entity.Photo = JadeValues.IsNull(command.Photo) ? null : command.Photo;
        entity.Price = DecimalRules.Round2(command.Price);
        entity.Name = command.Name ?? string.Empty;
        entity.ListedDate = command.ListedDate
            ?? throw new InvalidOperationException("The item command has no listed date.");
        entity.AgentId = command.AgentId;

        if (entity is TenderItem tenderItem && command is TenderItemCommand tenderCommand)
        {
            tenderItem.ClosureDate = tenderCommand.ClosureDate
                ?? throw new InvalidOperationException("The tender item command has no closure date.");
        }
    }

    /// <summary>Returns the command as an <see cref="ItemCommand"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCommand"/>.</exception>
    private static ItemCommand AsItemCommand(TransactionCommand<Item> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as ItemCommand
            ?? throw new ArgumentException("The command is not an item command.", nameof(command));
    }

    /// <summary>
    /// Returns the referenced row id; raises the 1090 null-object-reference error when the reference is
    /// <see langword="null"/> or 0.
    /// </summary>
    /// <exception cref="NoCompanyException">The reference is <see langword="null"/> or 0.</exception>
    private static long RequireReference(long? id) =>
        JadeValues.IsNull(id) ? throw new NoCompanyException() : id!.Value;

    /// <summary>Returns the row id of the bound company.</summary>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    private async Task<long> GetCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;
}
