using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one item-category operation persists: the category name, its code prefix and its description.
/// Ports the attributes of JADE <c>ItemCategoryTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class ItemCategoryCommand : TransactionCommand<ItemCategory>
{
    /// <summary>The category name; at most 41 characters as given, trimmed of blanks by pre-validation.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The code prefix of the category's items; at most 5 characters as given, trimmed of blanks and upper-cased by
    /// pre-validation.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>The category description; unbounded, trimmed of blanks by pre-validation.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Resets the common attributes, then sets <see cref="Prefix"/>, <see cref="Description"/> and <see cref="Name"/>
    /// to <see langword="null"/>. Ports <c>ItemCategoryTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Prefix = null;
        Description = null;
        Name = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then copies its prefix, description and name.
    /// Ports <c>ItemCategoryTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The category the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(ItemCategory modelObject)
    {
        base.PopulateFromObject(modelObject);
        Prefix = modelObject.Prefix;
        Description = modelObject.Description;
        Name = modelObject.Name;
    }
}

/// <summary>
/// Creates, updates and deletes the item categories of the bound company through the transaction-agent steps, and
/// reads them. Ports JADE <c>ItemCategoryTA</c> with <c>ItemCategory.onCreate</c> and
/// <c>ItemCategory.setCommonProperties</c>, and the <c>allItemCategoriesByName</c> and
/// <c>allItemCategoriesByPrefix</c> lookups of <c>Company</c>.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </remarks>
public sealed class ItemCategoryService : TransactionAgent<ItemCategory>
{
    private const int MinimumPrefixLength = 4;

    private readonly ICompanyContext _companyContext;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the bound company and the notification deriver.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken before a create, update or delete.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="deriver">The deriver that records the notifications of the rows a category delete cascades to.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ItemCategoryService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext,
        NotificationDeriver deriver)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(deriver);

        _companyContext = companyContext;
        _deriver = deriver;
    }

    /// <summary>
    /// Deletes the category with internal id <paramref name="id"/> as a <see cref="TransactionType.Delete"/>
    /// operation; succeeds with no work when no such category exists. The database deletes the category's commission
    /// rates and items, and the items' sales and tenders, with it.
    /// </summary>
    /// <param name="id">The internal id (<c>item_categories.id</c>) of the category.</param>
    /// <param name="mode">Whether the operation runs its own transaction or the caller's.</param>
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
        ItemCategory? category = await FindByIdAsync(id, ct);

        var command = new ItemCategoryCommand();
        if (category is not null)
        {
            command.PopulateFromObject(category);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the categories of the bound company in name order, each with its commission rates loaded in
    /// percentage order. Ports <c>Company.allItemCategoriesByName</c> (<c>ItemCategoriesByNameDict</c>) and
    /// <c>ItemCategory.allCommissionRates</c> (<c>CommissionRatesByPercentDict</c>).
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked categories.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<ItemCategory>> GetItemCategoriesAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.ItemCategories
            .Where(category => category.CompanyId == companyId)
            .Include(category => category.CommissionRates.OrderBy(rate => rate.Percentage))
            .OrderBy(category => category.Name)
            .ToListAsync(ct);
    }

    /// <summary>Returns the category with internal id <paramref name="id"/>, or <see langword="null"/> when none exists.</summary>
    /// <param name="id">The internal id (<c>item_categories.id</c>) of the category.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked category, or <see langword="null"/>.</returns>
    public Task<ItemCategory?> FindByIdAsync(long id, CancellationToken ct = default) =>
        Db.ItemCategories.FirstOrDefaultAsync(category => category.Id == id, ct);

    /// <summary>
    /// Returns whether a category with internal id <paramref name="id"/> exists, with one scalar query by id alone and
    /// no bound-company filter.
    /// </summary>
    /// <param name="id">The internal id (<c>item_categories.id</c>) of the category.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the category exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.ItemCategories.AnyAsync(category => category.Id == id, ct);

    /// <summary>
    /// Returns the category of the bound company whose name equals <paramref name="name"/> exactly, or
    /// <see langword="null"/> when there is none or <paramref name="name"/> is <see langword="null"/> or empty.
    /// Ports <c>Company.allItemCategoriesByName[name]</c>.
    /// </summary>
    /// <param name="name">The category name to look up, used as given.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked category, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<ItemCategory?> FindByNameAsync(string? name, CancellationToken ct = default)
    {
        if (JadeValues.IsNull(name))
        {
            return null;
        }

        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.ItemCategories
            .FirstOrDefaultAsync(category => category.CompanyId == companyId && category.Name == name, ct);
    }

    /// <summary>
    /// Returns the category of the bound company whose prefix equals <paramref name="prefix"/> compared
    /// case-insensitively, or <see langword="null"/> when there is none or <paramref name="prefix"/> is
    /// <see langword="null"/> or empty. Ports <c>Company.allItemCategoriesByPrefix[prefix]</c>.
    /// </summary>
    /// <param name="prefix">The prefix to look up, used untrimmed.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked category, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<ItemCategory?> FindByPrefixAsync(string? prefix, CancellationToken ct = default)
    {
        if (JadeValues.IsNull(prefix))
        {
            return null;
        }

        long companyId = await CurrentCompanyIdAsync(ct);
        string upperPrefix = prefix!.ToUpperInvariant();

        return await Db.ItemCategories
            .FirstOrDefaultAsync(
                category => category.CompanyId == companyId && category.Prefix.ToUpper() == upperPrefix,
                ct);
    }

    /// <summary>
    /// Checks the command's <see cref="ItemCategoryCommand.Name"/> (41) and <see cref="ItemCategoryCommand.Prefix"/>
    /// (5), as given and before any trimming, then the common attributes.
    /// </summary>
    /// <param name="command">The values to check, an <see cref="ItemCategoryCommand"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<ItemCategory> command)
    {
        ItemCategoryCommand categoryCommand = AsItemCategoryCommand(command);

        FieldLengths.Check(categoryCommand.Name, FieldLengths.ItemCategoryName, nameof(ItemCategoryCommand.Name));
        FieldLengths.Check(categoryCommand.Prefix, FieldLengths.ItemCategoryPrefix, nameof(ItemCategoryCommand.Prefix));

        base.CheckFieldLengths(command);
    }

    /// <summary>
    /// Trims the blanks from the name, the description and the prefix, in that order, then upper-cases the prefix.
    /// Ports <c>ItemCategoryTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The values to prepare, an <see cref="ItemCategoryCommand"/>.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<ItemCategory> command, CancellationToken ct)
    {
        ItemCategoryCommand categoryCommand = AsItemCategoryCommand(command);

        categoryCommand.Name = JadeValues.TrimBlanks(categoryCommand.Name);
        categoryCommand.Description = JadeValues.TrimBlanks(categoryCommand.Description);
        categoryCommand.Prefix = JadeValues.TrimBlanks(categoryCommand.Prefix);
        categoryCommand.Prefix = categoryCommand.Prefix.ToUpperInvariant();

        return base.PreValidateCoreAsync(command, ct);
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds, in this order:
    /// <list type="number">
    /// <item><description><see cref="MessageKeys.ItemCategoryPrefixRequired"/> (focus
    /// <see cref="FocusFields.CodePrefix"/>) when the prefix is empty; else
    /// <see cref="MessageKeys.ItemCategoryPrefixInvalid"/> (focus <see cref="FocusFields.CodePrefix"/>) when it is
    /// shorter than 4 characters; else <see cref="MessageKeys.ItemCategoryPrefixExists"/> (focus
    /// <see cref="FocusFields.Name"/>) when another category of the bound company has the same prefix compared
    /// case-insensitively;</description></item>
    /// <item><description><see cref="MessageKeys.ItemCategoryNameRequired"/> (focus <see cref="FocusFields.Name"/>)
    /// when the name is empty; else <see cref="MessageKeys.ItemCategoryNameExists"/> (focus
    /// <see cref="FocusFields.Name"/>) when another category of the bound company has exactly that name;</description></item>
    /// <item><description><see cref="MessageKeys.ItemCategoryDescriptionRequired"/> (focus
    /// <see cref="FocusFields.Description"/>) when the description is empty.</description></item>
    /// </list>
    /// The bound company is read only by the two lookups. Ports <c>ItemCategoryTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate, an <see cref="ItemCategoryCommand"/>.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    /// <exception cref="NoCompanyException">A lookup ran and the bound company does not exist.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<ItemCategory> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        if (validationType == ValidationType.Delete)
        {
            return;
        }

        ItemCategoryCommand categoryCommand = AsItemCategoryCommand(command);
        long modelObjectId = categoryCommand.ModelObject?.Id ?? 0;
        long? companyId = null;

        if (JadeValues.IsNull(categoryCommand.Prefix))
        {
            result.AddError(MessageKeys.ItemCategoryPrefixRequired, FocusFields.CodePrefix);
        }
        else if (categoryCommand.Prefix!.Length < MinimumPrefixLength)
        {
            result.AddError(MessageKeys.ItemCategoryPrefixInvalid, FocusFields.CodePrefix);
        }
        else
        {
            long ownerId = companyId ??= await CurrentCompanyIdAsync(ct);
            string upperPrefix = categoryCommand.Prefix.ToUpperInvariant();

            bool prefixInUse = await Db.ItemCategories.AnyAsync(
                category => category.CompanyId == ownerId
                    && category.Prefix.ToUpper() == upperPrefix
                    && category.Id != modelObjectId,
                ct);

            if (prefixInUse)
            {
                result.AddError(MessageKeys.ItemCategoryPrefixExists, FocusFields.Name);
            }
        }

        if (JadeValues.IsNull(categoryCommand.Name))
        {
            result.AddError(MessageKeys.ItemCategoryNameRequired, FocusFields.Name);
        }
        else
        {
            long ownerId = companyId ??= await CurrentCompanyIdAsync(ct);
            string name = categoryCommand.Name!;

            bool nameInUse = await Db.ItemCategories.AnyAsync(
                category => category.CompanyId == ownerId
                    && category.Name == name
                    && category.Id != modelObjectId,
                ct);

            if (nameInUse)
            {
                result.AddError(MessageKeys.ItemCategoryNameExists, FocusFields.Name);
            }
        }

        if (JadeValues.IsNull(categoryCommand.Description))
        {
            result.AddError(MessageKeys.ItemCategoryDescriptionRequired, FocusFields.Description);
        }
    }

    /// <summary>
    /// Takes, in order and stopping at the first lock not obtained, the locks of the bound company:
    /// for <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>
    /// <see cref="LockNames.CompanyAllItemCategoriesByName"/>, <see cref="LockNames.CompanyAllSalesByItem"/> and
    /// <see cref="LockNames.CompanyAllTenderItemsBySaleDate"/>; for <see cref="ValidationType.Update"/>
    /// <see cref="LockNames.CompanyAllItemCategoriesByName"/> when the name changes, then
    /// <see cref="LockNames.CompanyAllSalesByItem"/> and <see cref="LockNames.CompanyAllTenderItemsBySaleDate"/> when
    /// the prefix changes and the category holds at least one item; none for <see cref="ValidationType.Modify"/>.
    /// Ports <c>ItemCategoryTA.lockForCreate</c>, <c>lockForDelete</c>, <c>lockForUpdate</c> and
    /// <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The values being persisted, an <see cref="ItemCategoryCommand"/>.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update has no model object.</exception>
    /// <exception cref="NoCompanyException">Locks are taken and the bound company does not exist.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<ItemCategory> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        ItemCategoryCommand categoryCommand = AsItemCategoryCommand(command);

        if (validationType is ValidationType.Create or ValidationType.Delete)
        {
            long companyId = await CurrentCompanyIdAsync(ct);
            return await Locks.TryLockAsync(LockNames.CompanyAllItemCategoriesByName(companyId), ct)
                && await Locks.TryLockAsync(LockNames.CompanyAllSalesByItem(companyId), ct)
                && await Locks.TryLockAsync(LockNames.CompanyAllTenderItemsBySaleDate(companyId), ct);
        }

        if (validationType == ValidationType.Update)
        {
            return await LockForUpdateAsync(categoryCommand, ct);
        }

        return true;
    }

    /// <summary>
    /// Returns a new, untracked <see cref="ItemCategory"/>.
    /// </summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new category.</returns>
    protected override ItemCategory CreateModelObject(TransactionCommand<ItemCategory> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock of the new category, assigns it to the bound company and copies the
    /// command's name, description and prefix. Ports <c>ItemCategory.onCreate</c> and
    /// <c>ItemCategory.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The category being created, already tracked as added.</param>
    /// <param name="command">The values being created, an <see cref="ItemCategoryCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    protected override async Task OnCreateAsync(
        ItemCategory entity,
        TransactionCommand<ItemCategory> command,
        CancellationToken ct)
    {
        ItemCategoryCommand categoryCommand = AsItemCategoryCommand(command);
        long companyId = await CurrentCompanyIdAsync(ct);

        await base.OnCreateAsync(entity, command, ct);

        entity.CompanyId = companyId;
        SetCommonProperties(entity, categoryCommand);
    }

    /// <summary>
    /// Copies the command's name, description and prefix to the category and clears its soft edit lock.
    /// Ports <c>ItemCategory.setCommonProperties</c> and <c>PersistentModel.onUpdate</c>.
    /// </summary>
    /// <param name="entity">The tracked category being updated.</param>
    /// <param name="command">The values being written, an <see cref="ItemCategoryCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    protected override Task OnUpdateAsync(ItemCategory entity, TransactionCommand<ItemCategory> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        SetCommonProperties(entity, AsItemCategoryCommand(command));
        return base.OnUpdateAsync(entity, command, ct);
    }

    /// <summary>
    /// Records, as deleted, the rows the database deletes with the category: its commission rates in percentage
    /// order, its items in code-number order, the items' sales in item code-number order and the items' tenders in
    /// item code-number, time and descending offer order; each sale also records an update of its item and tender.
    /// </summary>
    /// <param name="entity">The tracked category about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(ItemCategory entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long categoryId = entity.Id;

        List<CommissionRate> rates = await Db.CommissionRates
            .AsNoTracking()
            .Where(rate => rate.ItemCategoryId == categoryId)
            .OrderBy(rate => rate.Percentage)
            .ToListAsync(ct);

        List<Item> items = await Db.Items
            .AsNoTracking()
            .Where(item => item.ItemCategoryId == categoryId)
            .OrderBy(item => item.CodeNumber)
            .ToListAsync(ct);

        List<Sale> sales = await Db.Sales
            .AsNoTracking()
            .Include(sale => sale.Item)
            .Where(sale => sale.Item!.ItemCategoryId == categoryId)
            .OrderBy(sale => sale.Item!.CodeNumber)
            .ToListAsync(ct);

        List<Tender> tenders = await Db.Tenders
            .AsNoTracking()
            .Where(tender => tender.TenderItem!.ItemCategoryId == categoryId)
            .OrderBy(tender => tender.TenderItem!.CodeNumber)
            .ThenBy(tender => tender.DateTendered)
            .ThenByDescending(tender => tender.Offer)
            .ToListAsync(ct);

        _deriver.RecordCascadeDeleted([.. rates, .. items, .. sales, .. tenders]);
    }

    /// <summary>
    /// Returns <paramref name="command"/> as an <see cref="ItemCategoryCommand"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="ItemCategoryCommand"/>.</exception>
    private static ItemCategoryCommand AsItemCategoryCommand(TransactionCommand<ItemCategory> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command as ItemCategoryCommand
            ?? throw new ArgumentException(
                $"The command must be an {nameof(ItemCategoryCommand)}.",
                nameof(command));
    }

    /// <summary>Copies the command's name, description and prefix to the category.</summary>
    private static void SetCommonProperties(ItemCategory entity, ItemCategoryCommand command)
    {
        entity.Name = command.Name ?? string.Empty;
        entity.Description = command.Description ?? string.Empty;
        entity.Prefix = command.Prefix ?? string.Empty;
    }

    /// <summary>Returns the internal id of the bound company.</summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    private async Task<long> CurrentCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;

    /// <summary>
    /// Takes the update locks of the bound company: the category-name lock when the name changes, then the sale and
    /// tender-item locks when the prefix changes and the category holds at least one item.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command has no model object.</exception>
    private async Task<bool> LockForUpdateAsync(ItemCategoryCommand command, CancellationToken ct)
    {
        ItemCategory category = command.ModelObject
            ?? throw new InvalidOperationException("The transaction agent has no model object to work on.");

        long companyId = await CurrentCompanyIdAsync(ct);

        if (!string.Equals(command.Name, category.Name, StringComparison.Ordinal)
            && !await Locks.TryLockAsync(LockNames.CompanyAllItemCategoriesByName(companyId), ct))
        {
            return false;
        }

        if (!string.Equals(command.Prefix, category.Prefix, StringComparison.Ordinal))
        {
            long categoryId = category.Id;
            bool hasItems = await Db.Items.AnyAsync(item => item.ItemCategoryId == categoryId, ct);

            if (hasItems)
            {
                return await Locks.TryLockAsync(LockNames.CompanyAllSalesByItem(companyId), ct)
                    && await Locks.TryLockAsync(LockNames.CompanyAllTenderItemsBySaleDate(companyId), ct);
            }
        }

        return true;
    }
}
