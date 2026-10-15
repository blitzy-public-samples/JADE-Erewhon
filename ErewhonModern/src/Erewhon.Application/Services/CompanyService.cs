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
/// The values a <see cref="CompanyService"/> operation persists for a company: its name and its address.
/// Ports the attributes of JADE <c>CompanyTA</c> and its subordinate <c>AddressTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class CompanyCommand : TransactionCommand<Company>
{
    /// <summary>Company name; at most 81 characters.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The address values of the company, or <see langword="null"/> when the operation carries no address.
    /// A create needs an address; an update or modify without one leaves the stored address unchanged.
    /// </summary>
    public AddressCommand? Address { get; set; }

    /// <summary>
    /// Resets the common attributes and sets <see cref="Name"/> and <see cref="Address"/> to <see langword="null"/>.
    /// Ports <c>CompanyTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = null;
        Address = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, sets <see cref="Address"/> to a copy of its address and
    /// <see cref="Name"/> to its name. Ports <c>CompanyTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The company the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Company modelObject)
    {
        base.PopulateFromObject(modelObject);
        Name = modelObject.Name;
    }

    /// <summary>
    /// Sets <see cref="Address"/> to a new address command holding the seven fields and the GUID of the company's
    /// address. Ports <c>CompanyTA.populateSubordinateObjects</c>.
    /// </summary>
    /// <param name="modelObject">The company being populated from.</param>
    protected override void PopulateSubordinateObjects(Company modelObject)
    {
        base.PopulateSubordinateObjects(modelObject);
        Address = AddressCommand.FromAddress(modelObject.Address);
    }
}

/// <summary>
/// Creates, updates and deletes companies with their addresses, creates the seed loader's company, and answers the
/// company lookups by agent, client and country name, by item code number, and by country and region name.
/// Ports JADE <c>CompanyTA</c>, the query methods of <c>Company</c> and <c>InitialDataLoader.createCompany</c>.
/// </summary>
/// <remarks>
/// The operations take no locks, and the company name is not checked for uniqueness, so several companies may hold
/// the same name. Deleting a company deletes everything it owns. Name lookups compare names exactly, with case.
/// An instance serves one dependency-injection scope and is not safe to call from several threads at once.
/// </remarks>
public sealed class CompanyService : TransactionAgent<Company>
{
    private readonly ICompanyContext _companyContext;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock, notification queue, bound company
    /// and notification deriver of its scope.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks of the scope.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="deriver">Records the change notifications of the rows a company delete removes with it.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CompanyService(
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
    /// Creates a company with the command's name and address. A company is created whether or not other companies
    /// exist, including one with the same name.
    /// </summary>
    /// <param name="command">The name and address of the new company; it must have no model object.</param>
    /// <param name="mode">Whether the service runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// The result of the create: on success the id and GUID of the new company; otherwise the validation errors,
    /// <see cref="MessageKeys.CompanyNameRequired"/> first, then the address errors.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> has a model object.</exception>
    /// <exception cref="InvalidOperationException">The command passed validation and has no address.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    public Task<OperationResult> CreateAsync(
        CompanyCommand command,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ModelObject is not null)
        {
            throw new ArgumentException("A company create command must have no model object.", nameof(command));
        }

        return ExecuteAsync(command, TransactionType.Persist, mode, ct);
    }

    /// <summary>
    /// Creates the seed loader's company: raises <see cref="ErewhonException"/>
    /// <see cref="ErewhonException.CompanyAlreadyExists"/> (500003) when any company exists, and otherwise creates
    /// the company as <see cref="CreateAsync"/> does and, when the create succeeds, binds it to the scope's
    /// <see cref="ICompanyContext"/>. Ports <c>InitialDataLoader.createCompany</c>.
    /// </summary>
    /// <param name="command">The name and address of the new company; it must have no model object.</param>
    /// <param name="mode">Whether the service runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the create.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ErewhonException">A company exists (500003).</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> has a model object.</exception>
    /// <exception cref="InvalidOperationException">The command passed validation and has no address.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    public async Task<OperationResult> CreateCompanyAsync(
        CompanyCommand command,
        TransactionMode mode = TransactionMode.Ambient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await Db.Companies.AnyAsync(ct))
        {
            throw new ErewhonException(ErewhonException.CompanyAlreadyExists);
        }

        OperationResult result = await CreateAsync(command, mode, ct);
        if (result.Succeeded && result.Id is long companyId)
        {
            _companyContext.Bind(companyId);
        }

        return result;
    }

    /// <summary>
    /// Deletes the company with id <paramref name="companyId"/>, with everything it owns. Succeeds without any work
    /// when no company has that id.
    /// </summary>
    /// <param name="companyId">The internal id of the company to delete.</param>
    /// <param name="mode">Whether the service runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long companyId,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        var command = new CompanyCommand();
        Company? company = await Db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is not null)
        {
            command.PopulateFromObject(company);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns whether the bound company has an agent named <paramref name="agentName"/>. The lookup reads the agents
    /// of the company bound to the scope, whichever company <paramref name="companyId"/> names.
    /// Ports <c>Company.agentExists</c>.
    /// </summary>
    /// <param name="companyId">The id of the company the lookup is made on; not used by the lookup.</param>
    /// <param name="agentName">The exact agent name; <see langword="null"/> or empty matches no agent.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns><see langword="true"/> when the bound company has an agent with that name.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public async Task<bool> AgentExists(long companyId, string? agentName, CancellationToken ct = default)
    {
        Company boundCompany = await _companyContext.GetCurrentAsync(ct);
        return await HasAgentWithThisName(boundCompany.Id, agentName, ct);
    }

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has an agent named <paramref name="name"/>.
    /// Ports <c>Company.hasAgentWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact agent name; <see langword="null"/> or empty matches no agent.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the company has an agent with that name.</returns>
    public async Task<bool> HasAgentWithThisName(long companyId, string? name, CancellationToken ct = default) =>
        await FindAgentId(companyId, name, ct) is not null;

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has an agent named <paramref name="name"/> other than the
    /// agent with id <paramref name="agentId"/>. Ports <c>Company.hasAnotherAgentWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact agent name; <see langword="null"/> or empty matches no agent.</param>
    /// <param name="agentId">The id of the agent to disregard; <see langword="null"/> disregards none.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when another agent of the company has that name.</returns>
    public async Task<bool> HasAnotherAgentWithThisName(
        long companyId,
        string? name,
        long? agentId,
        CancellationToken ct = default) =>
        await FindAgentId(companyId, name, ct) is long foundId && foundId != agentId;

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has a client named <paramref name="name"/>.
    /// Ports <c>Company.hasClientWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact client name; <see langword="null"/> or empty matches no client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the company has a client with that name.</returns>
    public async Task<bool> HasClientWithThisName(long companyId, string? name, CancellationToken ct = default) =>
        await FindClientId(companyId, name, ct) is not null;

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has a client named <paramref name="name"/> other than the
    /// client with id <paramref name="clientId"/>. Ports <c>Company.hasAnotherClientWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact client name; <see langword="null"/> or empty matches no client.</param>
    /// <param name="clientId">The id of the client to disregard; <see langword="null"/> disregards none.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when another client of the company has that name.</returns>
    public async Task<bool> HasAnotherClientWithThisName(
        long companyId,
        string? name,
        long? clientId,
        CancellationToken ct = default) =>
        await FindClientId(companyId, name, ct) is long foundId && foundId != clientId;

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has a country named <paramref name="name"/>.
    /// Ports <c>Company.hasCountryWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact country name; <see langword="null"/> or empty matches no country.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the company has a country with that name.</returns>
    public async Task<bool> HasCountryWithThisName(long companyId, string? name, CancellationToken ct = default) =>
        await FindCountryId(companyId, name, ct) is not null;

    /// <summary>
    /// Returns whether company <paramref name="companyId"/> has a country named <paramref name="name"/> other than the
    /// country with id <paramref name="countryId"/>. Ports <c>Company.hasAnotherCountryWithThisName</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="name">The exact country name; <see langword="null"/> or empty matches no country.</param>
    /// <param name="countryId">The id of the country to disregard; <see langword="null"/> disregards none.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when another country of the company has that name.</returns>
    public async Task<bool> HasAnotherCountryWithThisName(
        long companyId,
        string? name,
        long? countryId,
        CancellationToken ct = default) =>
        await FindCountryId(companyId, name, ct) is long foundId && foundId != countryId;

    /// <summary>
    /// Returns the item of company <paramref name="companyId"/> with code number <paramref name="codeNumber"/>,
    /// whatever its category, with its category, its region and the region's country, its agent, its sale with the
    /// sale's client and, for a tender sale, its tender, and, for a tender item, its tenders. Ports
    /// <c>Company.getItem</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="codeNumber">The code number; 0 matches no item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked item, or <see langword="null"/> when the company has no item with that number.</returns>
    public async Task<Item?> GetItem(long companyId, int codeNumber, CancellationToken ct = default)
    {
        if (JadeValues.IsNull(codeNumber))
        {
            return null;
        }

        return await Db.Items
            .Include(item => item.ItemCategory)
            .Include(item => item.Region)
                .ThenInclude(region => region!.Country)
            .Include(item => item.Agent)
            .Include(item => item.Sale)
                .ThenInclude(sale => sale!.Client)
            .Include(item => item.Sale)
                .ThenInclude(sale => ((TenderSale)sale!).Tender)
            .Include(item => ((TenderItem)item).Tenders)
            .FirstOrDefaultAsync(item => item.CompanyId == companyId && item.CodeNumber == codeNumber, ct);
    }

    /// <summary>
    /// Returns the projection of the item of company <paramref name="companyId"/> with code number
    /// <paramref name="codeNumber"/>, whatever its category, read through <see cref="ItemProjection.Metadata"/> without
    /// change tracking, so its <see cref="ItemProjection.Photo"/> is <see langword="null"/>. Finds the item
    /// <see cref="GetItem"/> finds.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="codeNumber">The code number; 0 matches no item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The projection, or <see langword="null"/> when the company has no item with that number.</returns>
    public async Task<ItemProjection?> GetItemProjectionAsync(
        long companyId,
        int codeNumber,
        CancellationToken ct = default)
    {
        if (JadeValues.IsNull(codeNumber))
        {
            return null;
        }

        return await Db.Items
            .AsNoTracking()
            .Where(item => item.CompanyId == companyId && item.CodeNumber == codeNumber)
            .Select(ItemProjection.Metadata)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns the number of locations of company <paramref name="companyId"/>: each country counts once and each of
    /// its regions once more, so one country with one region gives 2. Ports <c>Company.getNumberOfLocations</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The number of countries plus the number of regions of the company; 0 when it has no countries.</returns>
    public async Task<int> GetNumberOfLocations(long companyId, CancellationToken ct = default)
    {
        int countries = await Db.Countries.CountAsync(country => country.CompanyId == companyId, ct);
        int regions = await Db.Regions.CountAsync(region => region.Country!.CompanyId == companyId, ct);
        return countries + regions;
    }

    /// <summary>
    /// Returns the region named <paramref name="regionName"/> of the country named <paramref name="countryName"/> of
    /// company <paramref name="companyId"/>, with its country. Ports <c>Company.getRegion</c>.
    /// </summary>
    /// <param name="companyId">The id of the company.</param>
    /// <param name="countryName">The exact country name; <see langword="null"/> or empty matches no country.</param>
    /// <param name="regionName">The exact region name; <see langword="null"/> or empty matches no region.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked region, or <see langword="null"/> when the country or the region is unknown.</returns>
    public async Task<Region?> GetRegion(
        long companyId,
        string? countryName,
        string? regionName,
        CancellationToken ct = default)
    {
        if (JadeValues.IsNull(countryName) || JadeValues.IsNull(regionName))
        {
            return null;
        }

        return await Db.Regions
            .Include(region => region.Country)
            .FirstOrDefaultAsync(
                region => region.Country!.CompanyId == companyId
                    && region.Country.Name == countryName
                    && region.Name == regionName,
                ct);
    }

    /// <summary>Returns the company bound to the scope, with its address.</summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked bound company.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public Task<Company> GetCompanyAsync(CancellationToken ct = default) => _companyContext.GetCurrentAsync(ct);

    /// <summary>Returns the projection of the company bound to the scope, or <see langword="null"/> when there is none.</summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The projection of the bound company, or <see langword="null"/>.</returns>
    public async Task<CompanyProjection?> FindCompanyProjectionAsync(CancellationToken ct = default)
    {
        Company? company = await _companyContext.FindCurrentAsync(ct);
        return company is null ? null : CompanyProjection.From(company);
    }

    /// <summary>
    /// Checks the common attributes, the name (81) and, when present, the address values, each as given and before
    /// any trimming.
    /// </summary>
    /// <param name="command">The company values to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Company> command)
    {
        base.CheckFieldLengths(command);
        CompanyCommand company = AsCompanyCommand(command);
        FieldLengths.Check(company.Name, FieldLengths.CompanyName, nameof(CompanyCommand.Name));
        if (company.Address is { } address)
        {
            AddressRules.CheckFieldLengths(address);
        }
    }

    /// <summary>
    /// Removes leading and trailing spaces from the name; the address is left as given.
    /// Ports <c>CompanyTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The company values to prepare.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<Company> command, CancellationToken ct)
    {
        CompanyCommand company = AsCompanyCommand(command);
        company.Name = JadeValues.TrimBlanks(company.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds
    /// <see cref="MessageKeys.CompanyNameRequired"/> with focus <see cref="FocusFields.Name"/> when the name is
    /// <see langword="null"/> or empty, then, when the command has an address, the errors of
    /// <see cref="AddressRules.Validate"/> on the address as given. The name is not checked for uniqueness.
    /// Ports <c>CompanyTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The company values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override Task ValidateCoreAsync(
        TransactionCommand<Company> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        CompanyCommand company = AsCompanyCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return Task.CompletedTask;
        }

        if (JadeValues.IsNull(company.Name))
        {
            result.AddError(MessageKeys.CompanyNameRequired, FocusFields.Name);
        }

        if (company.Address is { } address)
        {
            AddressRules.Validate(address, validationType, result);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes no locks for any operation and returns <see langword="true"/>. Ports <c>CompanyTA.lockForCreate</c>,
    /// <c>lockForUpdate</c>, <c>lockForDelete</c> and <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The company values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/>.</returns>
    protected override Task<bool> AcquireLocksAsync(
        TransactionCommand<Company> command,
        ValidationType validationType,
        CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>Returns a new, untracked company. Ports <c>CompanyTA.getModelObjectClass</c>.</summary>
    /// <param name="command">The company values being created.</param>
    /// <returns>The new company.</returns>
    protected override Company CreateModelObject(TransactionCommand<Company> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock as the base does, then sets the name.
    /// Ports <c>PersistentModel.onCreate</c> and <c>Company.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The company being created, already tracked as added.</param>
    /// <param name="command">The company values being created.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override async Task OnCreateAsync(Company entity, TransactionCommand<Company> command, CancellationToken ct)
    {
        await base.OnCreateAsync(entity, command, ct);
        entity.Name = AsCompanyCommand(command).Name ?? string.Empty;
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, then writes the name.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>Company.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked company being updated.</param>
    /// <param name="command">The company values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Company entity, TransactionCommand<Company> command, CancellationToken ct)
    {
        await base.OnUpdateAsync(entity, command, ct);
        entity.Name = AsCompanyCommand(command).Name ?? string.Empty;
    }

    /// <summary>
    /// Trims and validates the address as a create, copying its errors into <paramref name="result"/>, and on success
    /// writes it to the new company's address. Ports <c>CompanyTA.createSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The company values being created.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command has no address.</exception>
    protected override Task<bool> CreateSubordinateObjectsAsync(
        TransactionCommand<Company> command,
        OperationResult result,
        CancellationToken ct)
    {
        CompanyCommand company = AsCompanyCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        AddressCommand address = company.Address
            ?? throw new InvalidOperationException("A company create needs an address.");

        if (AddressRules.PersistInTransState(address, ValidationType.Create, result))
        {
            AddressRules.Apply(address, RequireModelObject(company).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as an update, copying its errors into
    /// <paramref name="result"/>, and on success writes it to the company's address.
    /// Ports <c>CompanyTA.updateSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The company values being written.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override Task<bool> UpdateSubordinateObjectsAsync(
        TransactionCommand<Company> command,
        OperationResult result,
        CancellationToken ct)
    {
        CompanyCommand company = AsCompanyCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (company.Address is { } address && AddressRules.PersistInTransState(address, ValidationType.Update, result))
        {
            AddressRules.Apply(address, RequireModelObject(company).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as a modify, copying its errors into
    /// <paramref name="result"/>; the address is not written. Ports <c>CompanyTA.modifySubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The company values of the modify.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CompanyCommand"/>.</exception>
    protected override Task<bool> ModifySubordinateObjectsAsync(
        TransactionCommand<Company> command,
        int modification,
        OperationResult result,
        CancellationToken ct)
    {
        CompanyCommand company = AsCompanyCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (company.Address is { } address)
        {
            AddressRules.PersistInTransState(address, ValidationType.Modify, result);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// Records a deleted notification for each row the database deletes with the company, read in the delete's
    /// transaction as <see cref="CascadeRow"/> values (type name and id; for a sale, also its item's type name and id
    /// and its tender id): its agents, clients, countries, the countries' regions, item categories, the categories'
    /// commission rates, items, sales and tenders, each group in id order.
    /// </summary>
    /// <param name="entity">The tracked company about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(Company entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long companyId = entity.Id;
        var rows = new List<CascadeRow>();

        rows.AddRange(await Db.Agents
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Id)
            .Select(agent => new CascadeRow(nameof(Agent), agent.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.Clients
            .Where(client => client.CompanyId == companyId)
            .OrderBy(client => client.Id)
            .Select(client => new CascadeRow(nameof(Client), client.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.Countries
            .Where(country => country.CompanyId == companyId)
            .OrderBy(country => country.Id)
            .Select(country => new CascadeRow(nameof(Country), country.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.Regions
            .Where(region => region.Country!.CompanyId == companyId)
            .OrderBy(region => region.Id)
            .Select(region => new CascadeRow(nameof(Region), region.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.ItemCategories
            .Where(category => category.CompanyId == companyId)
            .OrderBy(category => category.Id)
            .Select(category => new CascadeRow(nameof(ItemCategory), category.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.CommissionRates
            .Where(rate => rate.ItemCategory!.CompanyId == companyId)
            .OrderBy(rate => rate.Id)
            .Select(rate => new CascadeRow(nameof(CommissionRate), rate.Id))
            .ToListAsync(ct));
        rows.AddRange(await Db.Items
            .Where(item => item.CompanyId == companyId)
            .OrderBy(item => item.Id)
            .Select(CascadeRow.FromItem)
            .ToListAsync(ct));
        rows.AddRange(await Db.Sales
            .Where(sale => sale.CompanyId == companyId)
            .OrderBy(sale => sale.Id)
            .Select(CascadeRow.FromSale)
            .ToListAsync(ct));
        rows.AddRange(await Db.Tenders
            .Where(tender => tender.CompanyId == companyId)
            .OrderBy(tender => tender.Id)
            .Select(tender => new CascadeRow(nameof(Tender), tender.Id))
            .ToListAsync(ct));

        _deriver.RecordCascadeDeleted(rows);
    }

    private static CompanyCommand AsCompanyCommand(TransactionCommand<Company> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as CompanyCommand
            ?? throw new ArgumentException($"The command must be a {nameof(CompanyCommand)}.", nameof(command));
    }

    private static Company RequireModelObject(CompanyCommand command) =>
        command.ModelObject
            ?? throw new InvalidOperationException("The company command has no model object.");

    private async Task<long?> FindAgentId(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await Db.Agents
                .Where(agent => agent.CompanyId == companyId && agent.Name == name)
                .Select(agent => (long?)agent.Id)
                .FirstOrDefaultAsync(ct);

    private async Task<long?> FindClientId(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await Db.Clients
                .Where(client => client.CompanyId == companyId && client.Name == name)
                .Select(client => (long?)client.Id)
                .FirstOrDefaultAsync(ct);

    private async Task<long?> FindCountryId(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await Db.Countries
                .Where(country => country.CompanyId == companyId && country.Name == name)
                .Select(country => (long?)country.Id)
                .FirstOrDefaultAsync(ct);
}
