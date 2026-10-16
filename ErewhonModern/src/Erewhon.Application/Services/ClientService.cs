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
/// The values a <see cref="ClientService"/> operation persists for a client: its name and its address.
/// Ports the attributes of JADE <c>ClientTA</c> and its subordinate <c>AddressTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class ClientCommand : TransactionCommand<Client>
{
    /// <summary>Client name; at most 81 characters.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The address values of the client, or <see langword="null"/> when the operation carries no address.
    /// A create needs an address; an update or modify without one leaves the stored address unchanged.
    /// </summary>
    public AddressCommand? Address { get; set; }

    /// <summary>
    /// Resets the common attributes and sets <see cref="Name"/> and <see cref="Address"/> to <see langword="null"/>.
    /// Ports <c>ClientTA.initialize</c>.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        Name = null;
        Address = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, sets <see cref="Address"/> to a copy of its address and
    /// <see cref="Name"/> to its name. Ports <c>ClientTA.populateFromObject</c>.
    /// </summary>
    /// <param name="modelObject">The client the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public override void PopulateFromObject(Client modelObject)
    {
        base.PopulateFromObject(modelObject);
        Name = modelObject.Name;
    }

    /// <summary>
    /// Sets <see cref="Address"/> to a new address command holding the seven fields and the GUID of the client's
    /// address. Ports <c>ClientTA.populateSubordinateObjects</c>.
    /// </summary>
    /// <param name="modelObject">The client being populated from.</param>
    protected override void PopulateSubordinateObjects(Client modelObject)
    {
        base.PopulateSubordinateObjects(modelObject);
        Address = AddressCommand.FromAddress(modelObject.Address);
    }
}

/// <summary>
/// Creates, updates, modifies and deletes clients with their addresses, and reads the clients of the bound company
/// by name, by id, in name order and with their retail sales, the buying clients of item sales with their tenders on
/// those items, and the last photo of a client's purchased retail items. Ports JADE <c>ClientTA</c>,
/// <c>Client.onCreate</c>, <c>Client.setCommonProperties</c> and the reads of <c>Company.allClients</c> and
/// <c>Client.allRetailSales</c>.
/// </summary>
/// <remarks>
/// A client name is unique within its company, compared exactly, with case. Deleting a client deletes its retail
/// sales, tender sales and tenders with it. An instance serves one dependency-injection scope and is not safe to call
/// from several threads at once.
/// </remarks>
public sealed class ClientService : TransactionAgent<Client>
{
    private readonly ICompanyContext _companyContext;
    private readonly CompanyService _companyService;
    private readonly NotificationDeriver _deriver;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock, notification queue, bound company,
    /// company lookups and notification deriver of its scope.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks of the scope.</param>
    /// <param name="clock">The database server clock.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="companyService">The client-name lookups of the bound company.</param>
    /// <param name="deriver">Records the change notifications of the rows a client delete removes with it.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ClientService(
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
    /// Deletes the client with row id <paramref name="id"/> in its own transaction, as
    /// <see cref="DeleteByIdAsync(long, TransactionMode, CancellationToken)"/> does with <see cref="TransactionMode.Own"/>.
    /// </summary>
    /// <param name="id">The row id of the client.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete; succeeded with no work when no client has that id.</returns>
    /// <exception cref="NoCompanyException">A client was found and no company is bound to the scope.</exception>
    public Task<OperationResult> DeleteByIdAsync(long id, CancellationToken ct) =>
        DeleteByIdAsync(id, TransactionMode.Own, ct);

    /// <summary>
    /// Deletes the client with row id <paramref name="id"/>: reads it with change tracking, populates a
    /// <see cref="ClientCommand"/> from it and runs <see cref="TransactionType.Delete"/>. The database deletes the
    /// client's retail sales, tender sales and tenders with it. When no client has that id, the command has no model
    /// object and the delete succeeds with no work.
    /// </summary>
    /// <param name="id">The row id of the client.</param>
    /// <param name="mode">Whether the delete runs in its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NoCompanyException">A client was found and no company is bound to the scope.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached the delete with no active transaction.
    /// </exception>
    public async Task<OperationResult> DeleteByIdAsync(
        long id,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken ct = default)
    {
        Client? client = await Db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        var command = new ClientCommand();
        if (client is not null)
        {
            command.PopulateFromObject(client);
        }

        return await ExecuteAsync(command, TransactionType.Delete, mode, ct);
    }

    /// <summary>
    /// Returns the client of the bound company whose name equals <paramref name="name"/> exactly, with case and
    /// untrimmed, with its address; <see langword="null"/> when there is none or <paramref name="name"/> is
    /// <see langword="null"/> or empty. A name that contains the NUL character (U+0000) finds no client and is not
    /// sent to the database. The bound company is read before the name is looked at, for every name. Ports
    /// <c>Company.allClients[name]</c>.
    /// </summary>
    /// <param name="name">The client name to look up, used as given.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked client, or <see langword="null"/>.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public async Task<Client?> FindByNameAsync(string? name, CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        if (JadeValues.IsNull(name) || name.AsSpan().Contains('\0'))
        {
            return null;
        }

        return await Db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(client => client.CompanyId == companyId && client.Name == name, ct);
    }

    /// <summary>
    /// Returns the client with row id <paramref name="id"/>, tracked, with its address; its retail and tender sales,
    /// each with its item and the item's category, and for a tender sale its winning tender; and its tenders, each
    /// with its tender item and the item's category. Each sale and tender also refers back to the client.
    /// </summary>
    /// <param name="id">The row id of the client.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The tracked client, or <see langword="null"/> when no client has that id.</returns>
    public async Task<Client?> FindByIdAsync(long id, CancellationToken ct = default)
    {
        Client? client = await Db.Clients
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);

        if (client is null)
        {
            return null;
        }

        await Db.Entry(client)
            .Collection(candidate => candidate.Sales)
            .Query()
            .AsTracking()
            .Include(sale => sale.Item)
                .ThenInclude(item => item!.ItemCategory)
            .Include(sale => ((TenderSale)sale).Tender)
            .LoadAsync(ct);

        await Db.Entry(client)
            .Collection(candidate => candidate.Tenders)
            .Query()
            .AsTracking()
            .Include(tender => tender.TenderItem)
                .ThenInclude(item => item!.ItemCategory)
            .LoadAsync(ct);

        return client;
    }

    /// <summary>
    /// Returns whether a client with row id <paramref name="id"/> exists, with one scalar query by id alone and no
    /// bound-company filter.
    /// </summary>
    /// <param name="id">The row id of the client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns><see langword="true"/> when the client exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) =>
        Db.Clients.AnyAsync(candidate => candidate.Id == id, ct);

    /// <summary>
    /// Reads with change tracking, in at most two queries, the client of each sale of <paramref name="items"/> whose
    /// <see cref="Sale.Client"/> is not loaded, with its address; the winning tender of each sale of
    /// <paramref name="items"/> that is a <see cref="TenderSale"/>; and the tenders those clients placed on the tender
    /// items of <paramref name="items"/>. The tracked sales, tender sales and tender items of the scope then refer to
    /// the clients and tenders read. Reads nothing when every sale of <paramref name="items"/> already has its client,
    /// or no item has a loaded sale.
    /// </summary>
    /// <param name="items">
    /// The items whose sales receive their clients; an item without a loaded sale is skipped.
    /// </param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the clients and tenders are read.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> is <see langword="null"/> or holds a <see langword="null"/> item.
    /// </exception>
    public async Task LoadSaleClientsAsync(IEnumerable<Item> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var buyerIds = new HashSet<long>();
        var winningTenderIds = new HashSet<long>();
        var tenderItemIds = new HashSet<long>();
        foreach (Item item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));

            if (item is TenderItem tenderItem)
            {
                tenderItemIds.Add(tenderItem.Id);
            }

            if (item.Sale is TenderSale { TenderId: long tenderId })
            {
                winningTenderIds.Add(tenderId);
            }

            if (item.Sale is { Client: null } sale)
            {
                buyerIds.Add(sale.ClientId);
            }
        }

        if (buyerIds.Count == 0)
        {
            return;
        }

        long[] buyers = [.. buyerIds];
        await Db.Clients
            .AsTracking()
            .Where(client => buyers.Contains(client.Id))
            .LoadAsync(ct);

        if (winningTenderIds.Count == 0 && tenderItemIds.Count == 0)
        {
            return;
        }

        long[] winners = [.. winningTenderIds];
        long[] buyerTenderItems = [.. tenderItemIds];
        await Db.Tenders
            .AsTracking()
            .Where(tender => winners.Contains(tender.Id)
                || (buyers.Contains(tender.ClientId) && buyerTenderItems.Contains(tender.TenderItemId)))
            .LoadAsync(ct);
    }

    /// <summary>
    /// Returns the projections of the clients of the bound company in name order. Ports <c>Company.allClients</c>
    /// (<c>ClientsByNameDict</c>) as <c>DTOClient.populateFromObject</c> reads it.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The client projections; empty when the company has no clients.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public async Task<IReadOnlyList<ClientProjection>> GetAllClientsAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        List<Client> clients = await Db.Clients
            .AsNoTracking()
            .Where(client => client.CompanyId == companyId)
            .OrderBy(client => client.Name)
            .ToListAsync(ct);

        return clients.Select(ClientProjection.From).ToList();
    }

    /// <summary>
    /// Returns the clients of the bound company in name order, each with its address. Ports <c>Company.allClients</c>
    /// (<c>ClientsByNameDict</c>).
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The tracked clients; empty when the company has no clients.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public async Task<IReadOnlyList<Client>> GetClientsAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Clients
            .AsTracking()
            .Where(client => client.CompanyId == companyId)
            .OrderBy(client => client.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the row id and name of each client of the bound company in name order. Ports the
    /// <c>Company.allClients</c> walk of <c>getClientNames</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The name projections; empty when the company has no clients.</returns>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    public async Task<IReadOnlyList<NameProjection>> GetClientNamesAsync(CancellationToken ct = default)
    {
        long companyId = await CurrentCompanyIdAsync(ct);

        return await Db.Clients
            .AsNoTracking()
            .Where(client => client.CompanyId == companyId)
            .OrderBy(client => client.Name)
            .Select(client => new NameProjection(client.Id, client.Name))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the retail sales of the client with row id <paramref name="clientId"/>, each with its item, the item's
    /// photo and the item's category, ordered by time sold, then the item's category prefix (ordinal), then the item's
    /// code number. Ports <c>Client.allRetailSales</c> (<c>RetailSalesByTimeItemDict</c>).
    /// </summary>
    /// <param name="clientId">The row id of the client.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The untracked retail sales; empty when the client has none or does not exist.</returns>
    public async Task<IReadOnlyList<RetailSale>> GetRetailSalesAsync(long clientId, CancellationToken ct = default)
    {
        List<RetailSale> sales = await Db.Sales
            .OfType<RetailSale>()
            .AsNoTracking()
            .Include(sale => sale.Item)
                .ThenInclude(item => item!.ItemCategory)
            .Where(sale => sale.ClientId == clientId)
            .ToListAsync(ct);

        return sales
            .OrderBy(sale => sale.DateSold)
            .ThenBy(sale => sale.Item?.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(sale => sale.Item?.CodeNumber)
            .ToList();
    }

    /// <summary>
    /// Returns the photo of the item of the last retail sale of the client with row id <paramref name="clientId"/>
    /// whose item has a photo that is neither <see langword="null"/> nor empty, in the order of
    /// <see cref="GetRetailSalesAsync"/>: time sold, then the item's category prefix (ordinal), then the item's code
    /// number. Reads, without change tracking, the item id and those three keys of each such sale, then the photo of
    /// the last item only; when that photo is <see langword="null"/> or empty by the time it is read, the photo of the
    /// item before it, and so on. Ports the <c>Client.allRetailSales</c> walk of <c>getPhoto</c>.
    /// </summary>
    /// <param name="clientId">The row id of the client.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>
    /// The photo bytes; <see langword="null"/> when no retail sale of the client has an item with a photo, or no client
    /// has that id.
    /// </returns>
    public async Task<byte[]?> GetLastRetailSalePhotoAsync(long clientId, CancellationToken ct = default)
    {
        var keys = await Db.Sales
            .OfType<RetailSale>()
            .AsNoTracking()
            .Where(sale => sale.ClientId == clientId && sale.Item!.Photo != null && sale.Item.Photo.Length > 0)
            .Select(sale => new
            {
                sale.ItemId,
                sale.DateSold,
                sale.Item!.ItemCategory!.Prefix,
                sale.Item.CodeNumber,
            })
            .ToListAsync(ct);

        IEnumerable<long> itemIdsLastFirst = keys
            .OrderBy(key => key.DateSold)
            .ThenBy(key => key.Prefix, StringComparer.Ordinal)
            .ThenBy(key => key.CodeNumber)
            .Select(key => key.ItemId)
            .Reverse();

        foreach (long itemId in itemIdsLastFirst)
        {
            byte[]? photo = await Db.Items
                .AsNoTracking()
                .Where(item => item.Id == itemId)
                .Select(item => item.Photo)
                .FirstOrDefaultAsync(ct);

            if (!JadeValues.IsNull(photo))
            {
                return photo;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the common attributes, the name (81) and, when present, the address values, each as given and before
    /// any trimming.
    /// </summary>
    /// <param name="command">The client values to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected override void CheckFieldLengths(TransactionCommand<Client> command)
    {
        base.CheckFieldLengths(command);
        ClientCommand client = AsClientCommand(command);
        FieldLengths.Check(client.Name, FieldLengths.ClientName, nameof(ClientCommand.Name));
        if (client.Address is { } address)
        {
            AddressRules.CheckFieldLengths(address);
        }
    }

    /// <summary>
    /// Removes leading and trailing carriage returns, line feeds, tabs, spaces and NUL characters from the name; a
    /// <see langword="null"/> name becomes the empty string. The address is left as given.
    /// Ports <c>ClientTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The client values to prepare.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    protected override Task PreValidateCoreAsync(TransactionCommand<Client> command, CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);
        client.Name = JadeValues.TrimWhiteSpace(client.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. For every other operation, modify included, adds in this
    /// order, each with focus <see cref="FocusFields.Name"/>: <see cref="MessageKeys.ClientNameRequired"/> when the name
    /// is <see langword="null"/> or empty; on create, <see cref="MessageKeys.ClientAlreadyExists"/> when the bound
    /// company has a client with the name; on update, <see cref="MessageKeys.ClientWithSameNameExists"/> when a client
    /// of the bound company other than the model object has the name; then, when the command has an address, the
    /// errors of <see cref="AddressRules.Validate"/> on the address as given. Ports <c>ClientTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The client values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update command has no model object.</exception>
    /// <exception cref="NoCompanyException">A create or update is validated and no company is bound to the scope.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<Client> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        if (JadeValues.IsNull(client.Name))
        {
            result.AddError(MessageKeys.ClientNameRequired, FocusFields.Name);
        }

        if (validationType == ValidationType.Create
            && await _companyService.HasClientWithThisName(await CurrentCompanyIdAsync(ct), client.Name, ct))
        {
            result.AddError(MessageKeys.ClientAlreadyExists, FocusFields.Name);
        }

        if (validationType == ValidationType.Update
            && await _companyService.HasAnotherClientWithThisName(
                await CurrentCompanyIdAsync(ct),
                client.Name,
                RequireModelObject(client).Id,
                ct))
        {
            result.AddError(MessageKeys.ClientWithSameNameExists, FocusFields.Name);
        }

        if (client.Address is { } address)
        {
            AddressRules.Validate(address, validationType, result);
        }
    }

    /// <summary>
    /// Takes the <see cref="LockNames.CompanyAllClients"/> lock of the bound company for a create and a delete, and
    /// for an update only when the name differs from the model object's name (ordinal comparison); takes no lock for a
    /// modify. Ports <c>ClientTA.lockForCreate</c>, <c>lockForDelete</c>, <c>lockForUpdate</c> and
    /// <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The client values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock wait.</param>
    /// <returns><see langword="true"/> when every lock of the operation is held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">An update command has no model object.</exception>
    /// <exception cref="NoCompanyException">A lock is taken and no company is bound to the scope.</exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<Client> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);

        switch (validationType)
        {
            case ValidationType.Create:
            case ValidationType.Delete:
                return await Locks.TryLockAsync(LockNames.CompanyAllClients(await CurrentCompanyIdAsync(ct)), ct);

            case ValidationType.Update:
                if (!string.Equals(client.Name, RequireModelObject(client).Name, StringComparison.Ordinal))
                {
                    return await Locks.TryLockAsync(LockNames.CompanyAllClients(await CurrentCompanyIdAsync(ct)), ct);
                }

                return true;

            default:
                return true;
        }
    }

    /// <summary>Returns a new, untracked client. Ports <c>ClientTA.getModelObjectClass</c>.</summary>
    /// <param name="command">The client values being created.</param>
    /// <returns>The new client.</returns>
    protected override Client CreateModelObject(TransactionCommand<Client> command) => new();

    /// <summary>
    /// Sets the GUID and clears the soft edit lock as the base does, then sets the name and makes the bound company
    /// the client's company. Ports <c>Client.onCreate</c> and <c>Client.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The client being created, already tracked as added.</param>
    /// <param name="command">The client values being created.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    protected override async Task OnCreateAsync(Client entity, TransactionCommand<Client> command, CancellationToken ct)
    {
        await base.OnCreateAsync(entity, command, ct);
        entity.Name = AsClientCommand(command).Name ?? string.Empty;
        entity.CompanyId = await CurrentCompanyIdAsync(ct);
    }

    /// <summary>
    /// Clears the soft edit lock as the base does, then writes the name.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>Client.setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked client being updated.</param>
    /// <param name="command">The client values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entity"/> or <paramref name="command"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    protected override async Task OnUpdateAsync(Client entity, TransactionCommand<Client> command, CancellationToken ct)
    {
        await base.OnUpdateAsync(entity, command, ct);
        entity.Name = AsClientCommand(command).Name ?? string.Empty;
    }

    /// <summary>
    /// Trims and validates the address as a create, copying its errors into <paramref name="result"/>, and on success
    /// writes it to the new client's address. Ports <c>ClientTA.createSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The client values being created.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    /// <exception cref="InvalidOperationException">The command has no address.</exception>
    protected override Task<bool> CreateSubordinateObjectsAsync(
        TransactionCommand<Client> command,
        OperationResult result,
        CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        AddressCommand address = client.Address
            ?? throw new InvalidOperationException("A client create needs an address.");

        if (AddressRules.PersistInTransState(address, ValidationType.Create, result))
        {
            AddressRules.Apply(address, RequireModelObject(client).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as an update, copying its errors into
    /// <paramref name="result"/>, and on success writes it to the client's address.
    /// Ports <c>ClientTA.updateSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The client values being written.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    protected override Task<bool> UpdateSubordinateObjectsAsync(
        TransactionCommand<Client> command,
        OperationResult result,
        CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (client.Address is { } address && AddressRules.PersistInTransState(address, ValidationType.Update, result))
        {
            AddressRules.Apply(address, RequireModelObject(client).Address);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// When the command has an address, trims and validates it as a modify, copying its errors into
    /// <paramref name="result"/>; the address is not written. Ports <c>ClientTA.modifySubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The client values of the modify.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="result">The result that receives the address errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    protected override Task<bool> ModifySubordinateObjectsAsync(
        TransactionCommand<Client> command,
        int modification,
        OperationResult result,
        CancellationToken ct)
    {
        ClientCommand client = AsClientCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (client.Address is { } address)
        {
            AddressRules.PersistInTransState(address, ValidationType.Modify, result);
        }

        return Task.FromResult(result.HasNoErrors);
    }

    /// <summary>
    /// Records a deleted notification for each row the database deletes with the client, read in the delete's
    /// transaction as <see cref="CascadeRow"/> values (type name and id; for a sale, also its item's type name and id
    /// and its tender id): its retail and tender sales in id order, then its tenders in id order. Each sale also
    /// records an update of its item and, for a tender sale, of its winning tender. Then records an updated notification
    /// for each tender sale of another client whose tender is one of those tenders, in id order.
    /// </summary>
    /// <param name="entity">The tracked client about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <see langword="null"/>.</exception>
    protected override async Task DeleteCascadeNotificationsAsync(Client entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        long clientId = entity.Id;

        List<CascadeRow> sales = await Db.Sales
            .Where(sale => sale.ClientId == clientId)
            .OrderBy(sale => sale.Id)
            .Select(CascadeRow.FromSale)
            .ToListAsync(ct);

        List<CascadeRow> tenders = await Db.Tenders
            .Where(tender => tender.ClientId == clientId)
            .OrderBy(tender => tender.Id)
            .Select(tender => new CascadeRow(nameof(Tender), tender.Id))
            .ToListAsync(ct);

        List<CascadeRow> peerTenderSales = await Db.Sales
            .OfType<TenderSale>()
            .Where(sale => sale.ClientId != clientId && sale.Tender!.ClientId == clientId)
            .OrderBy(sale => sale.Id)
            .Select(sale => new CascadeRow(nameof(TenderSale), sale.Id))
            .ToListAsync(ct);

        _deriver.RecordCascadeDeleted([.. sales, .. tenders]);
        _deriver.RecordPeerCleared(peerTenderSales);
    }

    /// <summary>Returns <paramref name="command"/> as a <see cref="ClientCommand"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ClientCommand"/>.</exception>
    private static ClientCommand AsClientCommand(TransactionCommand<Client> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as ClientCommand
            ?? throw new ArgumentException($"The command must be a {nameof(ClientCommand)}.", nameof(command));
    }

    /// <summary>Returns the model object of <paramref name="command"/>.</summary>
    /// <exception cref="InvalidOperationException">The command has no model object.</exception>
    private static Client RequireModelObject(ClientCommand command) =>
        command.ModelObject
            ?? throw new InvalidOperationException("The client command has no model object.");

    /// <summary>Returns the row id of the bound company.</summary>
    /// <exception cref="NoCompanyException">No company is bound to the scope.</exception>
    private async Task<long> CurrentCompanyIdAsync(CancellationToken ct) =>
        (await _companyContext.GetCurrentAsync(ct)).Id;
}
