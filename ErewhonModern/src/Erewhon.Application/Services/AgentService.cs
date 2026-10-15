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
/// The values an <see cref="AgentService"/> operation persists for an agent: its name, its address and the commission
/// rate a rate modify adds or removes. Ports the attributes of JADE <c>AgentTA</c> and its subordinate
/// <c>AddressTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class AgentCommand : TransactionCommand<Agent>
{
    /// <summary>Agent name; at most 81 characters.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The address values of the agent, or <see langword="null"/> when the operation carries no address.
    /// A create needs an address; an update or modify without one leaves the stored address unchanged.
    /// </summary>
    public AddressCommand? Address { get; set; }

    /// <summary>
    /// The internal id (<c>commission_rates.id</c>) of the commission rate that
    /// <see cref="TransactionType.ModifyAddCommissionRateForAgent"/> (1004) adds to the agent or
    /// <see cref="TransactionType.ModifyRemoveCommissionRateForAgent"/> (1005) removes from it; read only when
    /// <see cref="CommissionRate"/> is <see langword="null"/>. Ports <c>AgentTA.myCommissionRate</c>.
    /// </summary>
    public long? CommissionRateId { get; set; }

    /// <summary>
    /// The commission rate entity of a 1004 or 1005 modify, used instead of <see cref="CommissionRateId"/> when set;
    /// a rate with <see cref="PersistentModel.Id"/> 0 is one the caller has added to the change tracker and not yet
    /// saved. Ports <c>AgentTA.myCommissionRate</c>.
    /// </summary>
    public CommissionRate? CommissionRate { get; set; }

    /// <summary>
    /// The stored links of a set of agents for one item category, read together before their 1004 or 1005 modifies.
    /// When it covers the agent and the item category of the modify's commission rate, the modify takes the agent's
    /// stored link from it instead of reading the link; otherwise, or when <see langword="null"/>, the modify reads it.
    /// </summary>
    internal StoredCategoryLinks? StoredCategoryLinks { get; set; }

    /// <summary>
    /// Resets the common attributes and sets <see cref="Name"/>, <see cref="Address"/>, <see cref="CommissionRateId"/>,
    /// <see cref="CommissionRate"/> and <see cref="StoredCategoryLinks"/> to <see langword="null"/>.
    /// Ports <c>AgentTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = null;
        Address = null;
        CommissionRateId = null;
        CommissionRate = null;
        StoredCategoryLinks = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, sets <see cref="Address"/> to a copy of its address and
    /// <see cref="Name"/> to its name. Ports <c>AgentTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The agent the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Agent modelObject)
    {
        base.PopulateFromObject(modelObject);
        Name = modelObject.Name;
    }

    /// <summary>
    /// Sets <see cref="Address"/> to a new address command holding the seven fields and the GUID of the agent's
    /// address. Ports <c>AgentTA.populateSubordinateObjects</c>.
    /// </summary>
    /// <param name="modelObject">The agent being populated from.</param>
    protected override void PopulateSubordinateObjects(Agent modelObject)
    {
        base.PopulateSubordinateObjects(modelObject);
        Address = AddressCommand.FromAddress(modelObject.Address);
    }
}

/// <summary>
/// Creates, updates and deletes the agents of the bound company with their addresses, adds a commission rate to an
/// agent or removes one from it, and answers the agent lookups by name and id and the agent lists in name order.
/// Ports JADE <c>AgentTA</c>, the <c>Agent.onCreate</c>, <c>onModify</c>, <c>onModify_AddCommissionRate</c>,
/// <c>onModify_RemoveCommissionRate</c> and <c>setCommonProperties</c> handlers, and the
/// <c>Company.allAgents</c> lookups (<c>AgentsByNameDict</c>).
/// </summary>
/// <remarks>
/// Create and delete take the <see cref="LockNames.CompanyAllAgents"/> lock of the bound company; an update takes it
/// only when the name changes; a modify takes no lock. Only create and update are validated. An agent holds at most
/// one commission rate per item category. Name lookups compare names exactly, with case and untrimmed.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </remarks>
public sealed class AgentService : TransactionAgent<Agent>
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
    /// <param name="locks">The advisory locks taken before a create, update or delete.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="companyService">The company lookups that check whether an agent name is in use.</param>
    /// <param name="deriver">Records the change notifications of the items whose agent an agent delete clears.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AgentService(
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
    /// Deletes the agent with internal id <paramref name="id"/>, populated with its name and address, as a
    /// <see cref="TransactionType.Delete"/> operation; succeeds with no work when no such agent exists. The database
    /// deletes the agent's commission-rate links with it and clears the agent of its items.
    /// </summary>
    /// <param name="id">The internal id (<c>agents.id</c>) of the agent.</param>
    /// <param name="mode">Whether the operation runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="NoCompanyException">The agent exists and the bound company does not.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        Agent? agent = await Db.Agents.FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        var command = new AgentCommand();
        if (agent is not null)
        {
            command.PopulateFromObject(agent);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the agent of the bound company whose name equals <paramref name="name"/> exactly, with case and
    /// untrimmed, or <see langword="null"/> when there is none or <paramref name="name"/> is <see langword="null"/>
    /// or empty. A name longer than 81 characters finds no agent. The bound company is read before the name is
    /// looked at. Ports <c>app.myCompany.allAgents[name]</c>.
    /// </summary>
    /// <param name="name">The agent name to look up, used as given.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked agent with its address, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<Agent?> FindByNameAsync(string? name, CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        if (JadeValues.IsNull(name))
        {
            return null;
        }

        return await Db.Agents
            .FirstOrDefaultAsync(agent => agent.CompanyId == companyId && agent.Name == name, ct);
    }

    /// <summary>
    /// Returns the agent with internal id <paramref name="id"/>, with its address, its commission-rate links each with
    /// its rate and the rate's item category, and its items each with its item category and sale; or
    /// <see langword="null"/> when no such agent exists. The agent, its commission-rate links and its items are read
    /// in three separate queries: the links in commission-rate id order and the items in item id order. Only the
    /// agent is read when no agent has that id.
    /// </summary>
    /// <param name="id">The internal id (<c>agents.id</c>) of the agent.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The tracked agent, or <see langword="null"/>.</returns>
    public async Task<Agent?> FindByIdAsync(long id, CancellationToken ct = default)
    {
        Agent? agent = await Db.Agents
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        if (agent is null)
        {
            return null;
        }

        await Db.Entry(agent)
            .Collection(candidate => candidate.AgentCommissionRates)
            .Query()
            .Include(link => link.CommissionRate)
                .ThenInclude(rate => rate!.ItemCategory)
            .OrderBy(link => link.CommissionRateId)
            .LoadAsync(ct);

        await Db.Entry(agent)
            .Collection(candidate => candidate.Items)
            .Query()
            .Include(item => item.ItemCategory)
            .Include(item => item.Sale)
            .OrderBy(item => item.Id)
            .LoadAsync(ct);

        return agent;
    }

    /// <summary>
    /// Returns whether an agent with internal id <paramref name="id"/> exists, with one scalar query by id alone and
    /// no bound-company filter.
    /// </summary>
    /// <param name="id">The internal id (<c>agents.id</c>) of the agent.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the agent exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.Agents.AnyAsync(agent => agent.Id == id, ct);

    /// <summary>
    /// Returns whether every distinct id of <paramref name="ids"/> is the internal id of an existing agent, with one
    /// scalar query that counts the agents among those ids, by id alone and with no bound-company filter. An empty
    /// <paramref name="ids"/> runs no query and yields <see langword="true"/>; an id of 0 or less matches no agent.
    /// </summary>
    /// <param name="ids">The internal ids (<c>agents.id</c>) to check; repeated ids count once.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>
    /// <see langword="true"/> when an agent exists for each distinct id; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    public async Task<bool> AllExistAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        long[] distinctIds = ids.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return true;
        }

        int found = await Db.Agents.CountAsync(agent => distinctIds.Contains(agent.Id), ct);
        return found == distinctIds.Length;
    }

    /// <summary>
    /// Returns the projection of each agent of the bound company, with its address, in name order.
    /// Ports the walk of <c>app.myCompany.allAgents</c> (<c>AgentsByNameDict</c>) that fills <c>DTOAgent</c> values.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The agent projections; empty when the company has no agents.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<AgentProjection>> GetAllAgentsAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        List<Agent> agents = await Db.Agents
            .AsNoTracking()
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Name)
            .ToListAsync(ct);

        return agents.Select(AgentProjection.From).ToList();
    }

    /// <summary>
    /// Returns the agents of the bound company, each with its address, in name order.
    /// Ports <c>app.myCompany.allAgents</c> (<c>AgentsByNameDict</c>).
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked agents; empty when the company has no agents.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<Agent>> GetAgentsAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Agents
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the id and name of each agent of the bound company, in name order.
    /// Ports <c>ErewhonWebService.getAgentNames</c>' walk of <c>app.myCompany.allAgents</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The name projections; empty when the company has no agents.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    public async Task<IReadOnlyList<NameProjection>> GetAgentNamesAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Agents
            .AsNoTracking()
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Name)
            .Select(agent => new NameProjection(agent.Id, agent.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Reads in one tracked query the stored links of the agents <paramref name="agentIds"/> for the item category of
    /// <paramref name="rate"/>: its <see cref="CommissionRate.ItemCategoryId"/>, or the id of its loaded
    /// <see cref="CommissionRate.ItemCategory"/> when that is 0. Reads nothing and returns <see langword="null"/> when
    /// <paramref name="agentIds"/> is empty or the rate has no item category.
    /// </summary>
    /// <param name="rate">The commission rate whose item category the links are read for.</param>
    /// <param name="agentIds">The internal ids (<c>agents.id</c>) of the agents whose links are read.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>
    /// The stored links of the agents for the category, covering every id in <paramref name="agentIds"/>; or
    /// <see langword="null"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rate"/> or <paramref name="agentIds"/> is <see langword="null"/>.
    /// </exception>
    internal async Task<StoredCategoryLinks?> ReadStoredCategoryLinksAsync(
        CommissionRate rate,
        IReadOnlyCollection<long> agentIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(agentIds);

        long itemCategoryId = ItemCategoryIdOf(rate);
        if (JadeValues.IsNull(itemCategoryId) || agentIds.Count == 0)
        {
            return null;
        }

        long[] ids = [.. agentIds];

        List<AgentCommissionRate> links = await Db.AgentCommissionRates
            .Where(link => ids.Contains(link.AgentId) && link.ItemCategoryId == itemCategoryId)
            .ToListAsync(ct);

        return new StoredCategoryLinks(itemCategoryId, ids, links);
    }

    /// <summary>
    /// Checks the common attributes, the name (81) and, when present, the address values, each as given and before
    /// any trimming.
    /// </summary>
    /// <param name="command">The agent values to check, an <see cref="AgentCommand"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Agent> command)
    {
        base.CheckFieldLengths(command);
        AgentCommand agent = AsAgentCommand(command);
        FieldLengths.Check(agent.Name, FieldLengths.AgentName, nameof(AgentCommand.Name));
        if (agent.Address is { } address)
        {
            AddressRules.CheckFieldLengths(address);
        }
    }

    /// <summary>
    /// Removes leading and trailing spaces from the name; the address is left as given.
    /// Ports <c>AgentTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The agent values to prepare, an <see cref="AgentCommand"/>.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<Agent> command, CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        agent.Name = JadeValues.TrimBlanks(agent.Name);
        return base.PreValidateCoreAsync(command, ct);
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/> and <see cref="ValidationType.Modify"/>. For
    /// <see cref="ValidationType.Create"/> and <see cref="ValidationType.Update"/> adds, in this order, each with focus
    /// <see cref="FocusFields.Name"/>: <see cref="MessageKeys.AgentNameRequired"/> when the name is
    /// <see langword="null"/> or empty; on create, <see cref="MessageKeys.AgentAlreadyExists"/> when the bound company
    /// has an agent with exactly that name; on update, <see cref="MessageKeys.AgentWithSameNameExists"/> when an agent
    /// of the bound company other than the model object has exactly that name; then, when the command has an address,
    /// the errors of <see cref="AddressRules.Validate"/> on the address as given. Ports <c>AgentTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The agent values to validate, an <see cref="AgentCommand"/>.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="NoCompanyException">A create or update is validated and the bound company does not exist.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Agent> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType is not (ValidationType.Create or ValidationType.Update))
        {
            return;
        }

        if (JadeValues.IsNull(agent.Name))
        {
            result.AddError(MessageKeys.AgentNameRequired, FocusFields.Name);
        }

        long companyId = await CurrentCompanyIdAsync(ct);

        if (validationType == ValidationType.Create
            && await _companyService.HasAgentWithThisName(companyId, agent.Name, ct))
        {
            result.AddError(MessageKeys.AgentAlreadyExists, FocusFields.Name);
        }

        if (validationType == ValidationType.Update
            && await _companyService.HasAnotherAgentWithThisName(companyId, agent.Name, agent.ModelObject?.Id, ct))
        {
            result.AddError(MessageKeys.AgentWithSameNameExists, FocusFields.Name);
        }

        if (agent.Address is { } address)
        {
            AddressRules.Validate(address, validationType, result);
        }
    }

    /// <summary>
    /// Takes the <see cref="LockNames.CompanyAllAgents"/> lock of the bound company for
    /// <see cref="ValidationType.Create"/> and <see cref="ValidationType.Delete"/>, and for
    /// <see cref="ValidationType.Update"/> only when the command's name differs, by ordinal comparison, from the model
    /// object's name; takes none for <see cref="ValidationType.Modify"/>. Ports <c>AgentTA.lockForCreate</c>,
    /// <c>lockForDelete</c>, <c>lockForUpdate</c> and <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The agent values being persisted, an <see cref="AgentCommand"/>.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when the lock is held or none is needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update has no model object.</exception>
    /// <exception cref="NoCompanyException">The lock is taken and the bound company does not exist.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Agent> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);

        bool lockNeeded = validationType switch
        {
            ValidationType.Create or ValidationType.Delete => true,
            ValidationType.Update => !string.Equals(agent.Name, RequireModelObject(agent).Name, StringComparison.Ordinal),
            _ => false,
        };

        if (!lockNeeded)
        {
            return true;
        }

        long companyId = await CurrentCompanyIdAsync(ct);
        return await Locks.TryLockAsync(LockNames.CompanyAllAgents(companyId), ct);
    }

    /// <summary>Returns a new, untracked agent. Ports <c>AgentTA.getModelObjectClass</c>.</summary>
    /// <param name="command">The agent values being created.</param>
    /// <returns>The new agent.</returns>
    protected override Agent CreateModelObject(TransactionCommand<Agent> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock as the base does, assigns the agent to the bound company and sets
    /// the name. Ports <c>Agent.onCreate</c> and <c>Agent.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The agent being created, already tracked as added.</param>
    /// <param name="command">The agent values being created, an <see cref="AgentCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    protected override async Task OnCreateAsync(Agent entity, TransactionCommand<Agent> command, CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        await base.OnCreateAsync(entity, command, ct);
        entity.CompanyId = await CurrentCompanyIdAsync(ct);
        entity.Name = agent.Name ?? string.Empty;
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, then writes the name.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>Agent.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked agent being updated.</param>
    /// <param name="command">The agent values being written, an <see cref="AgentCommand"/>.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Agent entity, TransactionCommand<Agent> command, CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        await base.OnUpdateAsync(entity, command, ct);
        entity.Name = agent.Name ?? string.Empty;
    }


    /// <summary>
    /// Applies a modify sub-code to the agent:
    /// <see cref="TransactionType.ModifyAddCommissionRateForAgent"/> (1004) makes the command's commission rate the
    /// agent's rate for that rate's item category, replacing any other rate the agent holds for the category and
    /// changing nothing when the agent already holds that rate;
    /// <see cref="TransactionType.ModifyRemoveCommissionRateForAgent"/> (1005) removes whatever rate the agent holds
    /// for that rate's item category, if any; every other sub-code is applied as the base applies it. The name and
    /// address are not written. Ports <c>Agent.onModify</c>, <c>onModify_AddCommissionRate</c> and
    /// <c>onModify_RemoveCommissionRate</c>.
    /// </summary>
    /// <param name="entity">The tracked agent being modified.</param>
    /// <param name="command">The agent values of the modify, an <see cref="AgentCommand"/>.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the sub-code is applied.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A 1004 or 1005 modify has no commission rate, names a rate that does not exist, or names a rate with no item
    /// category.
    /// </exception>
    protected override async Task OnModifyAsync(
        Agent entity,
        TransactionCommand<Agent> command,
        int modification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        AgentCommand agent = AsAgentCommand(command);

        if (modification == (int)TransactionType.ModifyAddCommissionRateForAgent)
        {
            await AddCommissionRateAsync(entity, agent, ct);
        }
        else if (modification == (int)TransactionType.ModifyRemoveCommissionRateForAgent)
        {
            await RemoveCommissionRateAsync(entity, agent, ct);
        }
        else
        {
            await base.OnModifyAsync(entity, command, modification, ct);
        }
    }

    /// <summary>
    /// Trims and validates the address as a create, copying its errors into <paramref name="result"/>, and on success
    /// writes it to the new agent's address. Ports <c>AgentTA.createSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The agent values being created, an <see cref="AgentCommand"/>.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command has no address.</exception>
    protected override Task<bool> CreateSubordinateObjectsAsync(
        TransactionCommand<Agent> command,
        OperationResult result,
        CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        AddressCommand address = agent.Address
            ?? throw new InvalidOperationException("An agent create needs an address.");

        if (AddressRules.PersistInTransState(address, ValidationType.Create, result))
        {
            AddressRules.Apply(address, RequireModelObject(agent).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as an update, copying its errors into
    /// <paramref name="result"/>, and on success writes it to the agent's address.
    /// Ports <c>AgentTA.updateSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The agent values being written, an <see cref="AgentCommand"/>.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    protected override Task<bool> UpdateSubordinateObjectsAsync(
        TransactionCommand<Agent> command,
        OperationResult result,
        CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (agent.Address is { } address && AddressRules.PersistInTransState(address, ValidationType.Update, result))
        {
            AddressRules.Apply(address, RequireModelObject(agent).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as a modify, copying its errors into
    /// <paramref name="result"/>; the address is not written. Ports <c>AgentTA.modifySubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The agent values of the modify, an <see cref="AgentCommand"/>.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not an <see cref="AgentCommand"/>.</exception>
    protected override Task<bool> ModifySubordinateObjectsAsync(
        TransactionCommand<Agent> command,
        int modification,
        OperationResult result,
        CancellationToken ct)
    {
        AgentCommand agent = AsAgentCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (agent.Address is { } address)
        {
            AddressRules.PersistInTransState(address, ValidationType.Modify, result);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// Records an updated notification for each item of the agent, in id order, read in the delete's transaction as
    /// <see cref="CascadeRow"/> values (type name and id); the database clears the agent of those items. The agent's
    /// commission-rate links are deleted with it and raise no notification.
    /// </summary>
    /// <param name="entity">The tracked agent about to be deleted.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(Agent entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long agentId = entity.Id;

        List<CascadeRow> items = await Db.Items
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.Id)
            .Select(CascadeRow.FromItem)
            .ToListAsync(ct);

        _deriver.RecordPeerCleared(items);
    }

    private static AgentCommand AsAgentCommand(TransactionCommand<Agent> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as AgentCommand
            ?? throw new ArgumentException($"The command must be an {nameof(AgentCommand)}.", nameof(command));
    }

    private static Agent RequireModelObject(AgentCommand command) =>
        command.ModelObject
            ?? throw new InvalidOperationException("The agent command has no model object.");

    /// <summary>Returns the internal id of the bound company.</summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    private async Task<long> CurrentCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;

    /// <summary>
    /// Makes the command's commission rate the agent's rate for that rate's item category: does nothing when the
    /// agent already holds that rate, otherwise removes the agent's link for the category, if any, and adds a link to
    /// the rate. The agent's link is found with the command's <see cref="AgentCommand.StoredCategoryLinks"/>.
    /// </summary>
    private async Task AddCommissionRateAsync(Agent agent, AgentCommand command, CancellationToken ct)
    {
        (CommissionRate rate, long itemCategoryId) = await ResolveCommissionRateAsync(command, ct);
        AgentCommissionRate? existing = await FindCategoryLinkAsync(
            agent.Id,
            itemCategoryId,
            command.StoredCategoryLinks,
            ct);

        if (existing is not null
            && (ReferenceEquals(existing.CommissionRate, rate) || (rate.Id != 0 && existing.CommissionRateId == rate.Id)))
        {
            return;
        }

        if (existing is not null)
        {
            Db.AgentCommissionRates.Remove(existing);
        }

        var link = new AgentCommissionRate
        {
            AgentId = agent.Id,
            ItemCategoryId = itemCategoryId,
        };

        if (rate.Id == 0)
        {
            link.CommissionRate = rate;
        }
        else
        {
            link.CommissionRateId = rate.Id;
        }

        Db.AgentCommissionRates.Add(link);
    }

    /// <summary>
    /// Removes the agent's link for the item category of the command's commission rate, if any, found with the
    /// command's <see cref="AgentCommand.StoredCategoryLinks"/>.
    /// </summary>
    private async Task RemoveCommissionRateAsync(Agent agent, AgentCommand command, CancellationToken ct)
    {
        (_, long itemCategoryId) = await ResolveCommissionRateAsync(command, ct);
        AgentCommissionRate? existing = await FindCategoryLinkAsync(
            agent.Id,
            itemCategoryId,
            command.StoredCategoryLinks,
            ct);

        if (existing is not null)
        {
            Db.AgentCommissionRates.Remove(existing);
        }
    }

    /// <summary>
    /// Returns the command's <see cref="AgentCommand.CommissionRate"/> when set, otherwise the tracked rate with id
    /// <see cref="AgentCommand.CommissionRateId"/>, together with the id of the rate's item category: its
    /// <see cref="CommissionRate.ItemCategoryId"/>, or the id of its loaded <see cref="CommissionRate.ItemCategory"/>
    /// when that is 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The command names no rate, the named rate does not exist, or the rate has no item category.
    /// </exception>
    private async Task<(CommissionRate Rate, long ItemCategoryId)> ResolveCommissionRateAsync(
        AgentCommand command,
        CancellationToken ct)
    {
        CommissionRate? rate = command.CommissionRate;
        if (rate is null && command.CommissionRateId is long rateId)
        {
            rate = await Db.CommissionRates.FirstOrDefaultAsync(candidate => candidate.Id == rateId, ct)
                ?? throw new InvalidOperationException($"No commission rate has id {rateId}.");
        }

        if (rate is null)
        {
            throw new InvalidOperationException("A commission-rate modify of an agent needs a commission rate.");
        }

        long itemCategoryId = ItemCategoryIdOf(rate);
        if (JadeValues.IsNull(itemCategoryId))
        {
            throw new InvalidOperationException("The commission rate has no item category.");
        }

        return (rate, itemCategoryId);
    }

    /// <summary>
    /// Returns the id of the item category of <paramref name="rate"/>: its <see cref="CommissionRate.ItemCategoryId"/>,
    /// or the id of its loaded <see cref="CommissionRate.ItemCategory"/> when that is 0; 0 when it has neither.
    /// </summary>
    private static long ItemCategoryIdOf(CommissionRate rate) =>
        JadeValues.IsNull(rate.ItemCategoryId) ? rate.ItemCategory?.Id ?? 0 : rate.ItemCategoryId;

    /// <summary>
    /// Returns the link of agent <paramref name="agentId"/> for item category <paramref name="itemCategoryId"/> as the
    /// change tracker sees it: a tracked link that is not deleted, otherwise the stored link unless it is tracked as
    /// deleted; <see langword="null"/> when there is none. The stored link is taken from
    /// <paramref name="storedLinks"/> when it covers the agent and the category, and is read otherwise.
    /// </summary>
    private async Task<AgentCommissionRate?> FindCategoryLinkAsync(
        long agentId,
        long itemCategoryId,
        StoredCategoryLinks? storedLinks,
        CancellationToken ct)
    {
        AgentCommissionRate? tracked = Db.AgentCommissionRates.Local
            .FirstOrDefault(link => link.AgentId == agentId && link.ItemCategoryId == itemCategoryId);
        if (tracked is not null)
        {
            return tracked;
        }

        AgentCommissionRate? stored = storedLinks is not null && storedLinks.Covers(agentId, itemCategoryId)
            ? storedLinks.StoredLinkOf(agentId)
            : await Db.AgentCommissionRates
                .FirstOrDefaultAsync(link => link.AgentId == agentId && link.ItemCategoryId == itemCategoryId, ct);

        return stored is not null && Db.Entry(stored).State != EntityState.Deleted ? stored : null;
    }
}

/// <summary>
/// The stored commission-rate links of a set of agents for one item category, as one read returned them; an agent of
/// the set with no link here has no stored link for that category.
/// </summary>
internal sealed class StoredCategoryLinks
{
    private readonly HashSet<long> _agentIds;
    private readonly Dictionary<long, AgentCommissionRate> _linksByAgentId = [];

    /// <summary>
    /// Creates the links of the agents <paramref name="agentIds"/> for item category
    /// <paramref name="itemCategoryId"/>; the first link of each agent in <paramref name="links"/> is kept.
    /// </summary>
    /// <param name="itemCategoryId">The internal id (<c>item_categories.id</c>) of the item category.</param>
    /// <param name="agentIds">The internal ids (<c>agents.id</c>) of the agents whose links were read.</param>
    /// <param name="links">The links the read returned.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="agentIds"/> or <paramref name="links"/> is <see langword="null"/>.
    /// </exception>
    internal StoredCategoryLinks(
        long itemCategoryId,
        IEnumerable<long> agentIds,
        IEnumerable<AgentCommissionRate> links)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(links);

        ItemCategoryId = itemCategoryId;
        _agentIds = [.. agentIds];
        foreach (AgentCommissionRate link in links)
        {
            _linksByAgentId.TryAdd(link.AgentId, link);
        }
    }

    /// <summary>The internal id (<c>item_categories.id</c>) of the item category the links were read for.</summary>
    internal long ItemCategoryId { get; }

    /// <summary>
    /// Returns whether the read covered agent <paramref name="agentId"/> for item category
    /// <paramref name="itemCategoryId"/>.
    /// </summary>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the agent.</param>
    /// <param name="itemCategoryId">The internal id (<c>item_categories.id</c>) of the item category.</param>
    /// <returns><see langword="true"/> when the agent was read for that category.</returns>
    internal bool Covers(long agentId, long itemCategoryId) =>
        itemCategoryId == ItemCategoryId && _agentIds.Contains(agentId);

    /// <summary>Returns the stored link of agent <paramref name="agentId"/>, or <see langword="null"/> when it has none.</summary>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of a covered agent.</param>
    /// <returns>The link the read returned for the agent, or <see langword="null"/>.</returns>
    internal AgentCommissionRate? StoredLinkOf(long agentId) => _linksByAgentId.GetValueOrDefault(agentId);
}
