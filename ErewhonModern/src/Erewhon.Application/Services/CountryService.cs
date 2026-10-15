using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one location operation persists: the location name. The base of <see cref="CountryCommand"/> and the
/// region command. Ports the attributes of JADE <c>LocationTA</c>.
/// </summary>
/// <typeparam name="TLocation">The location type the command persists.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public abstract class LocationCommand<TLocation> : TransactionCommand<TLocation>
    where TLocation : Location
{
    /// <summary>The location name; at most 81 characters as given, trimmed of blanks by pre-validation.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Resets the common attributes, then sets <see cref="Name"/> to <see langword="null"/>.
    /// Ports <c>LocationTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then copies its name.
    /// Ports <c>LocationTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The location the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(TLocation modelObject)
    {
        base.PopulateFromObject(modelObject);
        Name = modelObject.Name;
    }
}

/// <summary>
/// The rules every location operation applies to its name, shared by <see cref="CountryService"/> and the region
/// service. Ports the field length of <c>Location.name</c> and <c>LocationTA.doPreValidate</c>.
/// </summary>
public static class LocationRules
{
    /// <summary>
    /// Checks the location name, as given and before any trimming, against its declared length of 81 characters.
    /// </summary>
    /// <param name="name">The location name; <see langword="null"/> passes.</param>
    /// <exception cref="StringTooLongException"><paramref name="name"/> is longer than 81 characters.</exception>
    public static void CheckFieldLengths(string? name) =>
        FieldLengths.Check(name, FieldLengths.LocationName, nameof(LocationCommand<Location>.Name));

    /// <summary>
    /// Removes the leading and trailing spaces from the command's name; a <see langword="null"/> name becomes empty.
    /// Ports <c>LocationTA.doPreValidate</c>.
    /// </summary>
    /// <typeparam name="TLocation">The location type the command persists.</typeparam>
    /// <param name="command">The location values to prepare.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public static void PreValidate<TLocation>(LocationCommand<TLocation> command)
        where TLocation : Location
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Name = JadeValues.TrimBlanks(command.Name);
    }
}

/// <summary>
/// The values one country operation persists: the country name. Ports the attributes of JADE <c>CountryTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class CountryCommand : LocationCommand<Country>
{
}

/// <summary>
/// Creates, updates and deletes the countries of the bound company through the transaction-agent steps, answers the
/// region-name lookups of a country, and reads the countries with their regions. Ports JADE <c>CountryTA</c> and
/// <c>LocationTA</c>, <c>Country.onCreate</c>, <c>Location.setCommonProperties</c>,
/// <c>Country.hasRegionWithThisName</c>, <c>Country.hasAnotherRegionWithThisName</c> and the
/// <c>Company.allCountries</c> collection (<c>CountriesByNameDict</c>).
/// </summary>
/// <remarks>
/// Deleting a country deletes its regions and clears the region of every item listed in them. Name lookups compare
/// names exactly, with case. One instance serves one dependency-injection scope; its members are not safe to call
/// from several threads at once.
/// </remarks>
public sealed class CountryService : TransactionAgent<Country>
{
    private readonly ICompanyContext _companyContext;
    private readonly CompanyService _companyService;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the bound company, the company lookups and the notification deriver.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken before a create, a delete and a renaming update.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="companyService">The company lookups of country names.</param>
    /// <param name="deriver">Records the change notifications of the rows a country delete removes or changes with it.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CountryService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext,
        CompanyService companyService,
        NotificationDeriver deriver)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(companyService);
        ArgumentNullException.ThrowIfNull(deriver);

        _companyContext = companyContext;
        _companyService = companyService;
        _deriver = deriver;
    }

    /// <summary>
    /// Deletes the country with internal id <paramref name="id"/> as a <see cref="TransactionType.Delete"/>
    /// operation; succeeds with no work when no such country exists. The database deletes the country's regions with
    /// it and clears the region of the items listed in them.
    /// </summary>
    /// <param name="id">The internal id (<c>countries.id</c>) of the country.</param>
    /// <param name="mode">Whether the operation runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="NoCompanyException">The country exists and the bound company does not.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        Country? country = await Db.Countries.FirstOrDefaultAsync(c => c.Id == id, ct);

        var command = new CountryCommand();
        if (country is not null)
        {
            command.PopulateFromObject(country);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the countries of the bound company in name order, each with its regions loaded in name order.
    /// Ports <c>Company.allCountries</c> (<c>CountriesByNameDict</c>) and <c>Country.allRegions</c>
    /// (<c>RegionsByNameDict</c>).
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked countries.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Countries
            .Where(country => country.CompanyId == companyId)
            .Include(country => country.Regions.OrderBy(region => region.Name))
            .OrderBy(country => country.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the country with internal id <paramref name="id"/>, with its regions loaded in name order, or
    /// <see langword="null"/> when none exists.
    /// </summary>
    /// <param name="id">The internal id (<c>countries.id</c>) of the country.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked country, or <see langword="null"/>.</returns>
    public Task<Country?> FindByIdAsync(long id, CancellationToken ct = default) =>
        Db.Countries
            .Include(country => country.Regions.OrderBy(region => region.Name))
            .FirstOrDefaultAsync(country => country.Id == id, ct);

    /// <summary>
    /// Returns whether a country with internal id <paramref name="id"/> exists, with one scalar query by id alone and
    /// no bound-company filter.
    /// </summary>
    /// <param name="id">The internal id (<c>countries.id</c>) of the country.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the country exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.Countries.AnyAsync(country => country.Id == id, ct);

    /// <summary>
    /// Returns the country of the bound company whose name equals <paramref name="name"/> exactly, with its regions
    /// loaded in name order, or <see langword="null"/> when there is none or <paramref name="name"/> is
    /// <see langword="null"/> or empty. Ports <c>Company.allCountries[name]</c>.
    /// </summary>
    /// <param name="name">The country name to look up, used as given.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked country, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">
    /// <paramref name="name"/> is not empty and the bound company does not exist.
    /// </exception>
    public async Task<Country?> FindByNameAsync(string? name, CancellationToken ct = default)
    {
        if (JadeValues.IsNull(name))
        {
            return null;
        }

        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Countries
            .Include(country => country.Regions.OrderBy(region => region.Name))
            .FirstOrDefaultAsync(country => country.CompanyId == companyId && country.Name == name, ct);
    }

    /// <summary>
    /// Returns whether country <paramref name="countryId"/> has a region named <paramref name="name"/>.
    /// Ports <c>Country.hasRegionWithThisName</c>.
    /// </summary>
    /// <param name="countryId">The internal id of the country.</param>
    /// <param name="name">The exact region name; <see langword="null"/> or empty matches no region.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the country has a region with that name.</returns>
    public async Task<bool> HasRegionWithThisName(long countryId, string? name, CancellationToken ct = default) =>
        await FindRegionId(countryId, name, ct) is not null;

    /// <summary>
    /// Returns whether country <paramref name="countryId"/> has a region named <paramref name="name"/> other than the
    /// region with id <paramref name="regionId"/>. Ports <c>Country.hasAnotherRegionWithThisName</c>.
    /// </summary>
    /// <param name="countryId">The internal id of the country.</param>
    /// <param name="name">The exact region name; <see langword="null"/> or empty matches no region.</param>
    /// <param name="regionId">The id of the region to disregard; <see langword="null"/> disregards none.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when another region of the country has that name.</returns>
    public async Task<bool> HasAnotherRegionWithThisName(
        long countryId,
        string? name,
        long? regionId,
        CancellationToken ct = default) =>
        await FindRegionId(countryId, name, ct) is long foundId && foundId != regionId;

    /// <summary>
    /// Checks the command's <see cref="LocationCommand{TLocation}.Name"/> (81), as given and before any trimming, then
    /// the common attributes.
    /// </summary>
    /// <param name="command">The values to check, a <see cref="CountryCommand"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Country> command)
    {
        CountryCommand countryCommand = AsCountryCommand(command);
        LocationRules.CheckFieldLengths(countryCommand.Name);
        base.CheckFieldLengths(command);
    }

    /// <summary>
    /// Removes the leading and trailing spaces from the name. Ports <c>LocationTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The values to prepare, a <see cref="CountryCommand"/>.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<Country> command, CancellationToken ct)
    {
        LocationRules.PreValidate(AsCountryCommand(command));
        return base.PreValidateCoreAsync(command, ct);
    }

    /// <summary>
    /// Adds, in this order and for every validation type, <see cref="ValidationType.Delete"/> and
    /// <see cref="ValidationType.Modify"/> included:
    /// <list type="number">
    /// <item><description><see cref="MessageKeys.CountryNameRequired"/> (focus <see cref="FocusFields.Name"/>) when the
    /// name is <see langword="null"/> or empty;</description></item>
    /// <item><description>for <see cref="ValidationType.Create"/>, <see cref="MessageKeys.CountryAlreadyExists"/> (focus
    /// <see cref="FocusFields.Name"/>) when the bound company has a country with exactly that name;</description></item>
    /// <item><description>for <see cref="ValidationType.Update"/>, <see cref="MessageKeys.CountryWithSameNameExists"/>
    /// (focus <see cref="FocusFields.Name"/>) when a country of the bound company other than the model object has
    /// exactly that name.</description></item>
    /// </list>
    /// The bound company is read only by the create and update lookups. Ports <c>CountryTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate, a <see cref="CountryCommand"/>.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    /// <exception cref="NoCompanyException">A lookup ran and the bound company does not exist.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Country> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        CountryCommand countryCommand = AsCountryCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (JadeValues.IsNull(countryCommand.Name))
        {
            result.AddError(MessageKeys.CountryNameRequired, FocusFields.Name);
        }

        if (validationType == ValidationType.Create
            && await _companyService.HasCountryWithThisName(
                await CurrentCompanyIdAsync(ct),
                countryCommand.Name,
                ct))
        {
            result.AddError(MessageKeys.CountryAlreadyExists, FocusFields.Name);
        }

        if (validationType == ValidationType.Update
            && await _companyService.HasAnotherCountryWithThisName(
                await CurrentCompanyIdAsync(ct),
                countryCommand.Name,
                countryCommand.ModelObject?.Id,
                ct))
        {
            result.AddError(MessageKeys.CountryWithSameNameExists, FocusFields.Name);
        }
    }

    /// <summary>
    /// Takes the <see cref="LockNames.CompanyAllCountries"/> lock of the bound company for
    /// <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>, and for
    /// <see cref="ValidationType.Update"/> only when the name differs, compared ordinally, from the model object's
    /// name; takes none for <see cref="ValidationType.Modify"/>. Ports <c>CountryTA.lockForCreate</c>,
    /// <c>lockForDelete</c> and <c>lockForUpdate</c>, and <c>LocationTA.lockForModify</c>.
    /// </summary>
    /// <param name="command">The values being persisted, a <see cref="CountryCommand"/>.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update has no model object.</exception>
    /// <exception cref="NoCompanyException">A lock is taken and the bound company does not exist.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Country> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        CountryCommand countryCommand = AsCountryCommand(command);

        if (validationType is ValidationType.Create or ValidationType.Delete)
        {
            return await TryLockAllCountriesAsync(ct);
        }

        if (validationType == ValidationType.Update)
        {
            Country country = countryCommand.ModelObject
                ?? throw new InvalidOperationException("The transaction agent has no model object to work on.");

            if (!string.Equals(countryCommand.Name, country.Name, StringComparison.Ordinal))
            {
                return await TryLockAllCountriesAsync(ct);
            }
        }

        return true;
    }

    /// <summary>Returns a new, untracked <see cref="Country"/>.</summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new country.</returns>
    protected override Country CreateModelObject(TransactionCommand<Country> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock of the new country as the base does, copies the command's name and
    /// assigns the country to the bound company. Ports <c>PersistentModel.onCreate</c>,
    /// <c>Location.setCommonProperties</c> and <c>Country.onCreate</c>.
    /// </summary>
    /// <param name="entity">The country being created, already tracked as added.</param>
    /// <param name="command">The values being created, a <see cref="CountryCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    protected override async Task OnCreateAsync(Country entity, TransactionCommand<Country> command, CancellationToken ct)
    {
        CountryCommand countryCommand = AsCountryCommand(command);

        await base.OnCreateAsync(entity, command, ct);

        entity.Name = countryCommand.Name ?? string.Empty;
        entity.CompanyId = await CurrentCompanyIdAsync(ct);
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, then writes the command's name.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>Location.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked country being updated.</param>
    /// <param name="command">The values being written, a <see cref="CountryCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Country entity, TransactionCommand<Country> command, CancellationToken ct)
    {
        CountryCommand countryCommand = AsCountryCommand(command);

        await base.OnUpdateAsync(entity, command, ct);

        entity.Name = countryCommand.Name ?? string.Empty;
    }

    /// <summary>
    /// Records, read in the delete's transaction, a deleted notification for each region the database deletes with
    /// the country, in name order, then an updated notification for each item whose region the database clears, in
    /// region name, category prefix and code-number order.
    /// </summary>
    /// <param name="entity">The tracked country about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(Country entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long countryId = entity.Id;

        List<Region> regions = await Db.Regions
            .AsNoTracking()
            .Where(region => region.CountryId == countryId)
            .OrderBy(region => region.Name)
            .ThenBy(region => region.Id)
            .ToListAsync(ct);

        List<Item> items = await Db.Items
            .AsNoTracking()
            .Where(item => item.Region!.CountryId == countryId)
            .OrderBy(item => item.Region!.Name)
            .ThenBy(item => item.ItemCategory!.Prefix)
            .ThenBy(item => item.CodeNumber)
            .ToListAsync(ct);

        _deriver.RecordCascadeDeleted(regions);
        _deriver.RecordPeerCleared(items);
    }

    /// <summary>Returns <paramref name="command"/> as a <see cref="CountryCommand"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CountryCommand"/>.</exception>
    private static CountryCommand AsCountryCommand(TransactionCommand<Country> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command as CountryCommand
            ?? throw new ArgumentException($"The command must be a {nameof(CountryCommand)}.", nameof(command));
    }

    /// <summary>Returns the internal id of the bound company.</summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    private async Task<long> CurrentCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;

    /// <summary>Takes the <see cref="LockNames.CompanyAllCountries"/> lock of the bound company.</summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    private async Task<bool> TryLockAllCountriesAsync(CancellationToken ct) =>
        await Locks.TryLockAsync(LockNames.CompanyAllCountries(await CurrentCompanyIdAsync(ct)), ct);

    /// <summary>
    /// Returns the id of the region of country <paramref name="countryId"/> named exactly <paramref name="name"/>, or
    /// <see langword="null"/> when there is none or <paramref name="name"/> is <see langword="null"/> or empty.
    /// </summary>
    private async Task<long?> FindRegionId(long countryId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await Db.Regions
                .Where(region => region.CountryId == countryId && region.Name == name)
                .Select(region => (long?)region.Id)
                .FirstOrDefaultAsync(ct);
}
