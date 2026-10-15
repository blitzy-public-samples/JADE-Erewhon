using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one region operation persists: the region name and its country. Ports the attributes of JADE
/// <c>RegionTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class RegionCommand : LocationCommand<Region>
{
    /// <summary>
    /// The internal id (<c>countries.id</c>) of the region's country; <see langword="null"/> or 0 means no country.
    /// Ports <c>RegionTA.myCountry</c>.
    /// </summary>
    public long? CountryId { get; set; }

    /// <summary>
    /// Resets the common attributes and the name, then sets <see cref="CountryId"/> to <see langword="null"/>.
    /// Ports <c>RegionTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        CountryId = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, copies its name, then copies its country.
    /// Ports <c>RegionTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The region the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Region modelObject)
    {
        base.PopulateFromObject(modelObject);
        CountryId = modelObject.CountryId;
    }
}

/// <summary>
/// Creates, updates and deletes the regions of a country through the transaction-agent steps, and reads the regions
/// of the bound company. Ports JADE <c>RegionTA</c> and <c>LocationTA</c>, <c>Region.onCreate</c>,
/// <c>Region.setCommonProperties</c> and the <c>Country.allRegions</c> collection (<c>RegionsByNameDict</c>).
/// </summary>
/// <remarks>
/// Deleting a region clears the region of every item listed in it. Region names are compared exactly, with case.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </remarks>
public sealed class RegionService : TransactionAgent<Region>
{
    private readonly ICompanyContext _companyContext;
    private readonly CountryService _countryService;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the bound company, the country region-name lookups and the notification deriver.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken before a create, a delete and a renaming update.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="countryService">The region-name lookups of a country.</param>
    /// <param name="deriver">Records the change notifications of the items a region delete changes with it.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RegionService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext,
        CountryService countryService,
        NotificationDeriver deriver)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(countryService);
        ArgumentNullException.ThrowIfNull(deriver);

        _companyContext = companyContext;
        _countryService = countryService;
        _deriver = deriver;
    }

    /// <summary>
    /// Deletes the region with internal id <paramref name="id"/> as a <see cref="TransactionType.Delete"/> operation;
    /// succeeds with no work when no such region exists. The database clears the region of the items listed in it.
    /// </summary>
    /// <param name="id">The internal id (<c>regions.id</c>) of the region.</param>
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
        Region? region = await Db.Regions.FirstOrDefaultAsync(r => r.Id == id, ct);

        var command = new RegionCommand();
        if (region is not null)
        {
            command.PopulateFromObject(region);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the regions of the bound company, or only those of country <paramref name="countryId"/> when it is
    /// given, ordered by country name and then region name, each with its <see cref="Region.Country"/> loaded.
    /// Ports <c>Company.allCountries</c> (<c>CountriesByNameDict</c>) walked with each <c>Country.allRegions</c>
    /// (<c>RegionsByNameDict</c>).
    /// </summary>
    /// <param name="countryId">
    /// The internal id (<c>countries.id</c>) of the country whose regions are read; <see langword="null"/> reads the
    /// regions of every country of the bound company.
    /// </param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked regions.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<Region>> GetRegionsAsync(long? countryId, CancellationToken ct = default)
    {
        long companyId = (await _companyContext.GetCurrentAsync(ct)).Id;

        IQueryable<Region> regions = Db.Regions
            .Include(region => region.Country)
            .Where(region => region.Country!.CompanyId == companyId);

        if (countryId is long id)
        {
            regions = regions.Where(region => region.CountryId == id);
        }

        return await regions
            .OrderBy(region => region.Country!.Name)
            .ThenBy(region => region.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the region with internal id <paramref name="id"/>, with its <see cref="Region.Country"/> loaded, or
    /// <see langword="null"/> when none exists.
    /// </summary>
    /// <param name="id">The internal id (<c>regions.id</c>) of the region.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked region, or <see langword="null"/>.</returns>
    public Task<Region?> FindByIdAsync(long id, CancellationToken ct = default) =>
        Db.Regions
            .Include(region => region.Country)
            .FirstOrDefaultAsync(region => region.Id == id, ct);

    /// <summary>
    /// Checks the command's <see cref="LocationCommand{TLocation}.Name"/> (81), as given and before any trimming, then
    /// the common attributes.
    /// </summary>
    /// <param name="command">The values to check, a <see cref="RegionCommand"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Region> command)
    {
        RegionCommand regionCommand = AsRegionCommand(command);
        LocationRules.CheckFieldLengths(regionCommand.Name);
        base.CheckFieldLengths(command);
    }

    /// <summary>
    /// Removes the leading and trailing spaces from the name. Ports <c>LocationTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The values to prepare, a <see cref="RegionCommand"/>.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<Region> command, CancellationToken ct)
    {
        LocationRules.PreValidate(AsRegionCommand(command));
        return base.PreValidateCoreAsync(command, ct);
    }

    /// <summary>
    /// Adds, in this order and for every validation type, <see cref="ValidationType.Delete"/> and
    /// <see cref="ValidationType.Modify"/> included:
    /// <list type="number">
    /// <item><description><see cref="MessageKeys.RegionNameRequired"/> (focus <see cref="FocusFields.Name"/>) when the
    /// name is <see langword="null"/> or empty;</description></item>
    /// <item><description><see cref="MessageKeys.CountryRequired"/> (focus <see cref="FocusFields.Country"/>) when the
    /// command has no country; otherwise, for <see cref="ValidationType.Create"/>,
    /// <see cref="MessageKeys.RegionExists"/> (focus <see cref="FocusFields.Name"/>) when the country has a region with
    /// exactly that name, and for <see cref="ValidationType.Update"/>, <see cref="MessageKeys.RegionExists"/> (focus
    /// <see cref="FocusFields.Name"/>) when a region of the country other than the model object has exactly that
    /// name.</description></item>
    /// </list>
    /// Ports <c>RegionTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate, a <see cref="RegionCommand"/>.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Region> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        RegionCommand regionCommand = AsRegionCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (JadeValues.IsNull(regionCommand.Name))
        {
            result.AddError(MessageKeys.RegionNameRequired, FocusFields.Name);
        }

        if (JadeValues.IsNull(regionCommand.CountryId))
        {
            result.AddError(MessageKeys.CountryRequired, FocusFields.Country);
            return;
        }

        long countryId = regionCommand.CountryId!.Value;

        if (validationType == ValidationType.Create
            && await _countryService.HasRegionWithThisName(countryId, regionCommand.Name, ct))
        {
            result.AddError(MessageKeys.RegionExists, FocusFields.Name);
        }

        if (validationType == ValidationType.Update
            && await _countryService.HasAnotherRegionWithThisName(
                countryId,
                regionCommand.Name,
                regionCommand.ModelObject?.Id,
                ct))
        {
            result.AddError(MessageKeys.RegionExists, FocusFields.Name);
        }
    }

    /// <summary>
    /// Takes the <see cref="LockNames.CountryAllRegions"/> lock of the command's country for
    /// <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>, and for
    /// <see cref="ValidationType.Update"/> only when the name differs, compared ordinally, from the model object's
    /// name; takes none for <see cref="ValidationType.Modify"/>. Ports <c>RegionTA.lockForCreate</c>,
    /// <c>lockForDelete</c> and <c>lockForUpdate</c>, and <c>LocationTA.lockForModify</c>.
    /// </summary>
    /// <param name="command">The values being persisted, a <see cref="RegionCommand"/>.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A lock is taken and the command has no country, or an update has no model object.
    /// </exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Region> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        RegionCommand regionCommand = AsRegionCommand(command);

        if (validationType is ValidationType.Create or ValidationType.Delete)
        {
            return await TryLockAllRegionsAsync(regionCommand, ct);
        }

        if (validationType == ValidationType.Update)
        {
            Region region = regionCommand.ModelObject
                ?? throw new InvalidOperationException("The transaction agent has no model object to work on.");

            if (!string.Equals(regionCommand.Name, region.Name, StringComparison.Ordinal))
            {
                return await TryLockAllRegionsAsync(regionCommand, ct);
            }
        }

        return true;
    }

    /// <summary>Returns a new, untracked <see cref="Region"/>.</summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new region.</returns>
    protected override Region CreateModelObject(TransactionCommand<Region> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock of the new region as the base does, then copies the command's name
    /// and country. Ports <c>PersistentModel.onCreate</c>, <c>Location.setCommonProperties</c>,
    /// <c>Region.onCreate</c> and <c>Region.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The region being created, already tracked as added.</param>
    /// <param name="command">The values being created, a <see cref="RegionCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command has no country.</exception>
    protected override async Task OnCreateAsync(Region entity, TransactionCommand<Region> command, CancellationToken ct)
    {
        RegionCommand regionCommand = AsRegionCommand(command);

        await base.OnCreateAsync(entity, command, ct);

        entity.Name = regionCommand.Name ?? string.Empty;
        entity.CountryId = RequireCountryId(regionCommand);
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, then writes the command's name and country.
    /// Ports <c>PersistentModel.onUpdate</c>, <c>Location.setCommonProperties</c> and
    /// <c>Region.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked region being updated.</param>
    /// <param name="command">The values being written, a <see cref="RegionCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command has no country.</exception>
    protected override async Task OnUpdateAsync(Region entity, TransactionCommand<Region> command, CancellationToken ct)
    {
        RegionCommand regionCommand = AsRegionCommand(command);

        await base.OnUpdateAsync(entity, command, ct);

        entity.Name = regionCommand.Name ?? string.Empty;
        entity.CountryId = RequireCountryId(regionCommand);
    }

    /// <summary>
    /// Records, read in the delete's transaction, an updated notification for each item whose region the database
    /// clears, in category prefix and code-number order. Ports <c>Region.allItems</c>
    /// (<c>ItemsByPrefixByNumberDict</c>) and the <c>myRegion</c> peer reference it clears.
    /// </summary>
    /// <param name="entity">The tracked region about to be deleted.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(Region entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long regionId = entity.Id;

        List<Item> items = await Db.Items
            .AsNoTracking()
            .Where(item => item.RegionId == regionId)
            .OrderBy(item => item.ItemCategory!.Prefix)
            .ThenBy(item => item.CodeNumber)
            .ToListAsync(ct);

        _deriver.RecordPeerCleared(items);
    }

    /// <summary>Returns <paramref name="command"/> as a <see cref="RegionCommand"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="RegionCommand"/>.</exception>
    private static RegionCommand AsRegionCommand(TransactionCommand<Region> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command as RegionCommand
            ?? throw new ArgumentException($"The command must be a {nameof(RegionCommand)}.", nameof(command));
    }

    /// <summary>Returns the command's country id.</summary>
    /// <exception cref="InvalidOperationException">The command has no country.</exception>
    private static long RequireCountryId(RegionCommand command) =>
        JadeValues.IsNull(command.CountryId)
            ? throw new InvalidOperationException("The region has no country.")
            : command.CountryId!.Value;

    /// <summary>Takes the <see cref="LockNames.CountryAllRegions"/> lock of the command's country.</summary>
    /// <exception cref="InvalidOperationException">The command has no country.</exception>
    private async Task<bool> TryLockAllRegionsAsync(RegionCommand command, CancellationToken ct) =>
        await Locks.TryLockAsync(LockNames.CountryAllRegions(RequireCountryId(command)), ct);
}
