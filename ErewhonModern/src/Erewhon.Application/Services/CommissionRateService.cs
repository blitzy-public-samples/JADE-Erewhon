using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The values one commission-rate operation persists: the item category of the rate, its percentage and the agents
/// that are to hold it. Ports the attributes of JADE <c>CommissionRateTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class CommissionRateCommand : TransactionCommand<CommissionRate>
{
    private decimal _percentage;
    private List<long> _agentIds = [];

    /// <summary>
    /// The row id of the rate's item category; required except on delete, and 0 counts as no category.
    /// Ports <c>CommissionRateTA.myItemCategory</c>.
    /// </summary>
    public long? ItemCategoryId { get; set; }

    /// <summary>
    /// The commission percentage, rounded to 2 decimal places with ties away from zero when it is assigned, so 12.345
    /// becomes 12.35; it must be greater than 0 and at most 100. Ports <c>CommissionRateTA.percentage</c>.
    /// </summary>
    public decimal Percentage
    {
        get => _percentage;
        set => _percentage = DecimalRules.Round2(value);
    }

    /// <summary>
    /// The row ids of the agents that are to hold the rate once a create or update completes: the rate is added to each
    /// listed agent that does not hold it and removed from each agent that holds it and is not listed.
    /// Ports <c>CommissionRateTA.allAgents</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value assigned is <see langword="null"/>.</exception>
    public List<long> AgentIds
    {
        get => _agentIds;
        set => _agentIds = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Resets the common attributes, empties <see cref="AgentIds"/>, sets <see cref="ItemCategoryId"/> to
    /// <see langword="null"/> and <see cref="Percentage"/> to 0. Ports <c>CommissionRateTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        AgentIds.Clear();
        ItemCategoryId = null;
        Percentage = 0m;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, copies its percentage and item category, and replaces
    /// <see cref="AgentIds"/> with the agent ids of its loaded <see cref="CommissionRate.AgentCommissionRates"/> links.
    /// Ports <c>CommissionRateTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The rate the next operation works on, with its agent links loaded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(CommissionRate modelObject)
    {
        base.PopulateFromObject(modelObject);
        Percentage = modelObject.Percentage;
        ItemCategoryId = modelObject.ItemCategoryId;
        AgentIds.Clear();
        AgentIds.AddRange(modelObject.AgentCommissionRates.Select(link => link.AgentId));
    }
}

/// <summary>
/// Creates, updates and deletes the commission rates of item categories, adds and removes the agents that hold a rate,
/// and reads the rates of the bound company. Ports JADE <c>CommissionRateTA</c>, <c>CommissionRate.setCommonProperties</c>
/// and <c>CommissionRate.addOrRemoveAgents</c>, and the <c>ItemCategory.allCommissionRates</c> lookups
/// (<c>CommissionRatesByPercentDict</c>).
/// </summary>
/// <remarks>
/// Create and delete take the <see cref="LockNames.ItemCategoryAllCommissionRates"/> lock of the command's item
/// category; an update takes it only when the category or the percentage changes; a modify takes no lock. Every
/// operation except a delete is validated. A create or update adds the rate to, and removes it from, agents through
/// nested <see cref="AgentService"/> modifies in the same transaction. One instance serves one dependency-injection
/// scope; its members are not safe to call from several threads at once.
/// </remarks>
public sealed class CommissionRateService : TransactionAgent<CommissionRate>
{
    private const decimal MaximumPercentage = 100m;

    private readonly ICompanyContext _companyContext;
    private readonly AgentService _agentService;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the bound company and the agent service that adds a rate to an agent or removes it.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken before a create, update or delete.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="agentService">The agent service that runs the commission-rate modifies of agents.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CommissionRateService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICompanyContext companyContext,
        AgentService agentService)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(agentService);

        _companyContext = companyContext;
        _agentService = agentService;
    }

    /// <summary>
    /// Deletes the commission rate with internal id <paramref name="id"/> in its own transaction, as
    /// <see cref="DeleteByIdAsync(long, TransactionMode, CancellationToken)"/> does with <see cref="TransactionMode.Own"/>.
    /// </summary>
    /// <param name="id">The internal id (<c>commission_rates.id</c>) of the rate.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete; succeeded with no work when no rate has that id.</returns>
    public Task<OperationResult> DeleteByIdAsync(long id, CancellationToken ct) =>
        DeleteByIdAsync(id, TransactionMode.Own, ct);

    /// <summary>
    /// Deletes the commission rate with internal id <paramref name="id"/>: reads it with its item category and agent
    /// links, populates a <see cref="CommissionRateCommand"/> from it and runs <see cref="TransactionType.Delete"/>;
    /// succeeds with no work when no such rate exists. The database deletes the rate's agent links with it.
    /// </summary>
    /// <param name="id">The internal id (<c>commission_rates.id</c>) of the rate.</param>
    /// <param name="mode">Whether the operation runs its own transaction or the caller's.</param>
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
        CommissionRate? rate = await FindByIdAsync(id, ct);

        var command = new CommissionRateCommand();
        if (rate is not null)
        {
            command.PopulateFromObject(rate);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Makes <paramref name="agentIds"/> the agents holding the commission rate with internal id
    /// <paramref name="commissionRateId"/>: reads the rate, populates a <see cref="CommissionRateCommand"/> from it,
    /// sets <see cref="CommissionRateCommand.AgentIds"/> to the distinct ids of <paramref name="agentIds"/> and runs
    /// <see cref="TransactionType.Persist"/> in its own transaction, an update that adds the rate to each listed agent
    /// not holding it and removes it from each holder not listed. Ports the save of <c>AgentCommissionRatesForm</c>.
    /// </summary>
    /// <param name="commissionRateId">The internal id (<c>commission_rates.id</c>) of the rate.</param>
    /// <param name="agentIds">The internal ids (<c>agents.id</c>) of every agent that is to hold the rate.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the update.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agentIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No commission rate has the id <paramref name="commissionRateId"/>.</exception>
    public async Task<OperationResult> ReplaceAgentsAsync(
        long commissionRateId,
        IReadOnlyCollection<long> agentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        CommissionRate rate = await FindByIdAsync(commissionRateId, ct)
            ?? throw new InvalidOperationException($"No commission rate has id {commissionRateId}.");

        var command = new CommissionRateCommand();
        command.PopulateFromObject(rate);
        command.AgentIds = agentIds.Distinct().ToList();

        return await ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own, ct);
    }

    /// <summary>
    /// Returns the commission rates of the bound company's item categories, or of the one category
    /// <paramref name="itemCategoryId"/> of the bound company when it is given, ordered by category name and then by
    /// percentage, each with its item category and its agent links, the links in agent name order with their agents.
    /// Ports <c>ItemCategory.allCommissionRates</c> (<c>CommissionRatesByPercentDict</c>) and
    /// <c>CommissionRate.allAgents</c> (<c>AgentsByNameDict</c>).
    /// </summary>
    /// <param name="itemCategoryId">
    /// The internal id (<c>item_categories.id</c>) of the one category to read, or <see langword="null"/> for every
    /// category of the bound company.
    /// </param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked rates; empty when there are none.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<CommissionRate>> GetCommissionRatesAsync(
        long? itemCategoryId,
        CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        IQueryable<CommissionRate> rates = Db.CommissionRates
            .Where(rate => rate.ItemCategory!.CompanyId == companyId);

        if (itemCategoryId is long categoryId)
        {
            rates = rates.Where(rate => rate.ItemCategoryId == categoryId);
        }

        return await rates
            .Include(rate => rate.ItemCategory)
            .Include(rate => rate.AgentCommissionRates.OrderBy(link => link.Agent!.Name).ThenBy(link => link.AgentId))
                .ThenInclude(link => link.Agent)
            .OrderBy(rate => rate.ItemCategory!.Name)
            .ThenBy(rate => rate.Percentage)
            .ThenBy(rate => rate.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the commission rate with internal id <paramref name="id"/>, with its item category and its agent links,
    /// the links in agent name order with their agents; or <see langword="null"/> when no such rate exists.
    /// </summary>
    /// <param name="id">The internal id (<c>commission_rates.id</c>) of the rate.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked rate, or <see langword="null"/>.</returns>
    public Task<CommissionRate?> FindByIdAsync(long id, CancellationToken ct = default) =>
        Db.CommissionRates
            .Include(rate => rate.ItemCategory)
            .Include(rate => rate.AgentCommissionRates.OrderBy(link => link.Agent!.Name).ThenBy(link => link.AgentId))
                .ThenInclude(link => link.Agent)
            .FirstOrDefaultAsync(rate => rate.Id == id, ct);

    /// <summary>
    /// Brings the agents holding <paramref name="rate"/> in line with <see cref="CommissionRateCommand.AgentIds"/>:
    /// first, in agent name order, runs a <see cref="TransactionType.ModifyAddCommissionRateForAgent"/> (1004) modify
    /// for each listed agent that does not hold the rate; then, in agent name order, a
    /// <see cref="TransactionType.ModifyRemoveCommissionRateForAgent"/> (1005) modify for each agent holding the rate
    /// that is not listed. Each modify runs through <see cref="AgentService"/> in <see cref="TransactionMode.Ambient"/>
    /// mode with the rate entity itself, and its result is not read; listed ids that match no agent are skipped.
    /// The holders are the rate's agent links as the change tracker sees them once the stored links are loaded.
    /// Before the 1004 modifies, the stored links of the joining agents for the rate's item category are read in one
    /// query, and each 1004 modify takes its agent's stored link from that read.
    /// Ports <c>CommissionRate.addOrRemoveAgents</c>.
    /// </summary>
    /// <param name="rate">The rate whose agents change; tracked, and unsaved with id 0 while it is being created.</param>
    /// <param name="command">The values of the operation, whose <see cref="CommissionRateCommand.AgentIds"/> are the agents to hold the rate.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when every modify has run.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rate"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotInTransactionStateException">A modify runs with no active transaction.</exception>
    public async Task AddOrRemoveAgents(CommissionRate rate, CommissionRateCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(command);

        var listedIds = new HashSet<long>(command.AgentIds);
        HashSet<long> holderIds = await LoadHolderIdsAsync(rate, ct);

        List<Agent> joiningAgents = (await AgentsInNameOrderAsync(listedIds, ct))
            .Where(agent => !holderIds.Contains(agent.Id))
            .ToList();
        StoredCategoryLinks? joiningLinks = await _agentService.ReadStoredCategoryLinksAsync(
            rate,
            joiningAgents.Select(agent => agent.Id).ToList(),
            ct);

        foreach (Agent agent in joiningAgents)
        {
            await _agentService.ExecuteAsync(
                RateModifyCommand(agent, rate, joiningLinks),
                TransactionType.ModifyAddCommissionRateForAgent,
                TransactionMode.Ambient,
                ct);
        }

        HashSet<long> leavingIds = await LoadHolderIdsAsync(rate, ct);
        leavingIds.ExceptWith(listedIds);

        foreach (Agent agent in await AgentsInNameOrderAsync(leavingIds, ct))
        {
            await _agentService.ExecuteAsync(
                RateModifyCommand(agent, rate, null),
                TransactionType.ModifyRemoveCommissionRateForAgent,
                TransactionMode.Ambient,
                ct);
        }
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds, in this order:
    /// <see cref="MessageKeys.ItemCategoryRequired"/> (focus <see cref="FocusFields.ItemCategory"/>) when the item
    /// category id is <see langword="null"/> or 0; <see cref="MessageKeys.CommissionRatePercentageInvalid"/> (focus
    /// <see cref="FocusFields.Percentage"/>) when the percentage is 0 or less, or more than 100; and, when a category is
    /// given, <see cref="MessageKeys.ItemCategoryHasCommissionRate"/> (focus <see cref="FocusFields.Percentage"/>) when
    /// that category holds a stored rate with the percentage other than the model object.
    /// Ports <c>CommissionRateTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate, a <see cref="CommissionRateCommand"/>.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CommissionRateCommand"/>.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<CommissionRate> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        CommissionRateCommand rateCommand = AsCommissionRateCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        if (JadeValues.IsNull(rateCommand.ItemCategoryId))
        {
            result.AddError(MessageKeys.ItemCategoryRequired, FocusFields.ItemCategory);
        }

        if (rateCommand.Percentage <= 0m || rateCommand.Percentage > MaximumPercentage)
        {
            result.AddError(MessageKeys.CommissionRatePercentageInvalid, FocusFields.Percentage);
        }

        if (!JadeValues.IsNull(rateCommand.ItemCategoryId))
        {
            long categoryId = rateCommand.ItemCategoryId!.Value;
            decimal percentage = DecimalRules.Round2(rateCommand.Percentage);
            long modelObjectId = rateCommand.ModelObject?.Id ?? 0;

            bool percentageHeld = await Db.CommissionRates.AnyAsync(
                rate => rate.ItemCategoryId == categoryId
                    && rate.Percentage == percentage
                    && rate.Id != modelObjectId,
                ct);

            if (percentageHeld)
            {
                result.AddError(MessageKeys.ItemCategoryHasCommissionRate, FocusFields.Percentage);
            }
        }
    }

    /// <summary>
    /// Takes the <see cref="LockNames.ItemCategoryAllCommissionRates"/> lock of the command's item category for
    /// <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>, and for
    /// <see cref="ValidationType.Update"/> only when the command's category or percentage differs from the model
    /// object's; takes none for <see cref="ValidationType.Modify"/>. Ports <c>CommissionRateTA.lockForCreate</c>,
    /// <c>lockForDelete</c>, <c>lockForUpdate</c> and <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The values being persisted, a <see cref="CommissionRateCommand"/>.</param>
    /// <param name="validationType">The operation the lock is taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when the lock is held or none is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CommissionRateCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update has no model object.</exception>
    /// <exception cref="NoCompanyException">
    /// The lock is taken and the command's item category id is <see langword="null"/> or 0: the 1090
    /// null-object-reference error.
    /// </exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<CommissionRate> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        CommissionRateCommand rateCommand = AsCommissionRateCommand(command);

        bool lockNeeded = validationType switch
        {
            ValidationType.Create or ValidationType.Delete => true,
            ValidationType.Update => IsCategoryOrPercentageChanged(rateCommand, RequireModelObject(rateCommand)),
            _ => false,
        };

        if (!lockNeeded)
        {
            return true;
        }

        long categoryId = RequireItemCategoryId(rateCommand);
        return await Locks.TryLockAsync(LockNames.ItemCategoryAllCommissionRates(categoryId), ct);
    }

    /// <summary>Returns a new, untracked commission rate. Ports <c>CommissionRateTA.getModelObjectClass</c>.</summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new rate.</returns>
    protected override CommissionRate CreateModelObject(TransactionCommand<CommissionRate> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock as the base does, writes the percentage, rounded to 2 decimal
    /// places, and the item category, then adds the rate to the listed agents with <see cref="AddOrRemoveAgents"/>.
    /// Ports <c>PersistentModel.onCreate</c> and <c>CommissionRate.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The rate being created, already tracked as added.</param>
    /// <param name="command">The values being created, a <see cref="CommissionRateCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set and the agents hold the rate.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CommissionRateCommand"/>.</exception>
    /// <exception cref="NoCompanyException">The command's item category id is <see langword="null"/> or 0.</exception>
    protected override async Task OnCreateAsync(
        CommissionRate entity,
        TransactionCommand<CommissionRate> command,
        CancellationToken ct)
    {
        CommissionRateCommand rateCommand = AsCommissionRateCommand(command);
        await base.OnCreateAsync(entity, command, ct);
        await SetCommonPropertiesAsync(entity, rateCommand, ct);
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, writes the percentage, rounded to 2 decimal places, and the item
    /// category, moving the rate's agent links to the new category when it changes, then adds the rate to the listed
    /// agents and removes it from the others with <see cref="AddOrRemoveAgents"/>.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>CommissionRate.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked rate being updated.</param>
    /// <param name="command">The values being written, a <see cref="CommissionRateCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set and the agents hold the rate.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CommissionRateCommand"/>.</exception>
    /// <exception cref="NoCompanyException">The command's item category id is <see langword="null"/> or 0.</exception>
    protected override async Task OnUpdateAsync(
        CommissionRate entity,
        TransactionCommand<CommissionRate> command,
        CancellationToken ct)
    {
        CommissionRateCommand rateCommand = AsCommissionRateCommand(command);
        await base.OnUpdateAsync(entity, command, ct);
        await SetCommonPropertiesAsync(entity, rateCommand, ct);
    }

    /// <summary>Returns <paramref name="command"/> as a <see cref="CommissionRateCommand"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="CommissionRateCommand"/>.</exception>
    private static CommissionRateCommand AsCommissionRateCommand(TransactionCommand<CommissionRate> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command as CommissionRateCommand
            ?? throw new ArgumentException(
                $"The command must be a {nameof(CommissionRateCommand)}.",
                nameof(command));
    }

    /// <summary>Returns the command's model object.</summary>
    /// <exception cref="InvalidOperationException">The command has no model object.</exception>
    private static CommissionRate RequireModelObject(CommissionRateCommand command) =>
        command.ModelObject
            ?? throw new InvalidOperationException("The commission-rate command has no model object.");

    /// <summary>Returns the command's item category id.</summary>
    /// <exception cref="NoCompanyException">The id is <see langword="null"/> or 0.</exception>
    private static long RequireItemCategoryId(CommissionRateCommand command) =>
        JadeValues.IsNull(command.ItemCategoryId) ? throw new NoCompanyException() : command.ItemCategoryId!.Value;

    /// <summary>Returns whether the command's item category or percentage differs from the rate's.</summary>
    private static bool IsCategoryOrPercentageChanged(CommissionRateCommand command, CommissionRate rate) =>
        command.ItemCategoryId != rate.ItemCategoryId || command.Percentage != rate.Percentage;

    /// <summary>
    /// Returns a new agent command whose model object is <paramref name="agent"/>, whose commission rate is
    /// <paramref name="rate"/>, with its id once the rate has one, and whose
    /// <see cref="AgentCommand.StoredCategoryLinks"/> are <paramref name="storedLinks"/>; the name and address are left
    /// unset.
    /// </summary>
    private static AgentCommand RateModifyCommand(Agent agent, CommissionRate rate, StoredCategoryLinks? storedLinks) =>
        new()
        {
            ModelObject = agent,
            CommissionRate = rate,
            CommissionRateId = JadeValues.IsNull(rate.Id) ? null : rate.Id,
            StoredCategoryLinks = storedLinks,
        };

    /// <summary>Returns the internal id of the bound company.</summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    private async Task<long> CurrentCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;

    /// <summary>
    /// Writes the rounded percentage and the item category to the rate, moves its agent links to the category when
    /// the category of a saved rate changes, then runs <see cref="AddOrRemoveAgents"/>.
    /// </summary>
    /// <exception cref="NoCompanyException">The command's item category id is <see langword="null"/> or 0.</exception>
    private async Task SetCommonPropertiesAsync(CommissionRate entity, CommissionRateCommand command, CancellationToken ct)
    {
        long categoryId = RequireItemCategoryId(command);

        entity.Percentage = DecimalRules.Round2(command.Percentage);

        if (entity.ItemCategoryId != categoryId)
        {
            entity.ItemCategoryId = categoryId;
            if (!JadeValues.IsNull(entity.Id))
            {
                foreach (AgentCommissionRate link in await LoadLinksAsync(entity, ct))
                {
                    link.ItemCategoryId = categoryId;
                }
            }
        }

        await AddOrRemoveAgents(entity, command, ct);
    }

    /// <summary>
    /// Loads the stored agent links of <paramref name="rate"/> into the change tracker and returns every tracked link
    /// of the rate that is not marked as deleted, including links added and not yet saved.
    /// </summary>
    private async Task<List<AgentCommissionRate>> LoadLinksAsync(CommissionRate rate, CancellationToken ct)
    {
        long rateId = rate.Id;
        if (!JadeValues.IsNull(rateId))
        {
            await Db.AgentCommissionRates
                .Where(link => link.CommissionRateId == rateId)
                .LoadAsync(ct);
        }

        return Db.AgentCommissionRates.Local
            .Where(link => ReferenceEquals(link.CommissionRate, rate)
                || (!JadeValues.IsNull(rateId) && link.CommissionRateId == rateId))
            .ToList();
    }

    /// <summary>Returns the agent ids of the links <see cref="LoadLinksAsync"/> returns for <paramref name="rate"/>.</summary>
    private async Task<HashSet<long>> LoadHolderIdsAsync(CommissionRate rate, CancellationToken ct) =>
        (await LoadLinksAsync(rate, ct)).Select(link => link.AgentId).ToHashSet();

    /// <summary>
    /// Returns the tracked agents whose internal ids are in <paramref name="agentIds"/>, ordered by name and then by id;
    /// empty when <paramref name="agentIds"/> is empty.
    /// </summary>
    private async Task<List<Agent>> AgentsInNameOrderAsync(IReadOnlyCollection<long> agentIds, CancellationToken ct)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }

        long[] ids = [.. agentIds];

        return await Db.Agents
            .Where(agent => ids.Contains(agent.Id))
            .OrderBy(agent => agent.Name)
            .ThenBy(agent => agent.Id)
            .ToListAsync(ct);
    }
}
