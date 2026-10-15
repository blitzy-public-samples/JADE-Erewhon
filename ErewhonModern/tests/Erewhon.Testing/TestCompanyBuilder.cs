using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Testing;

/// <summary>
/// Builds test companies and their fixture objects through the production application services of one
/// dependency-injection scope, persisting every object with <see cref="TransactionType.Persist"/> in
/// <see cref="TransactionMode.Own"/> mode. Ports the JADE <c>JadeTestCase</c> helpers and the
/// <c>TestErewhonExternalInterfaces</c> helpers, including <c>createTestData</c>.
/// </summary>
/// <remarks>
/// Every helper except <see cref="BuildCompanyAsync"/> throws <see cref="InvalidOperationException"/>, with the
/// operation's full error text, when its object is not persisted. Objects other than companies belong to the company
/// bound with <see cref="Bind"/>. Instances are not safe to call from several threads at once.
/// </remarks>
public sealed class TestCompanyBuilder
{
    private readonly IServiceProvider _services;

    /// <summary>Creates a builder over the services of one dependency-injection scope.</summary>
    /// <param name="services">The service provider of the scope whose services build the fixtures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public TestCompanyBuilder(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The fixture company. Ports <c>myCompany</c>.</summary>
    public Company? MyCompany { get; set; }

    /// <summary>The fixture agent. Ports <c>myAgent</c>.</summary>
    public Agent? MyAgent { get; set; }

    /// <summary>The fixture client. Ports <c>myClient</c>.</summary>
    public Client? MyClient { get; set; }

    /// <summary>The fixture commission rate. Ports <c>myCommissionRate</c>.</summary>
    public CommissionRate? MyCommissionRate { get; set; }

    /// <summary>The fixture country. Ports <c>myCountry</c>.</summary>
    public Country? MyCountry { get; set; }

    /// <summary>The fixture region. Ports <c>myRegion</c>.</summary>
    public Region? MyRegion { get; set; }

    /// <summary>The fixture item category. Ports <c>myItemCategory</c>.</summary>
    public ItemCategory? MyItemCategory { get; set; }

    /// <summary>The fixture retail item. Ports <c>myRetailItem</c>.</summary>
    public RetailItem? MyRetailItem { get; set; }

    /// <summary>The fixture tender item. Ports <c>myTenderItem</c>.</summary>
    public TenderItem? MyTenderItem { get; set; }

    /// <summary>The fixture tender. Ports <c>myTender</c>.</summary>
    public Tender? MyTender { get; set; }

    /// <summary>The fixture retail sale. Ports <c>myRetailSale</c>.</summary>
    public RetailSale? MyRetailSale { get; set; }

    /// <summary>The fixture tender sale. Ports <c>myTenderSale</c>.</summary>
    public TenderSale? MyTenderSale { get; set; }

    /// <summary>
    /// Binds the company with internal id <paramref name="companyId"/> to the scope's <see cref="ICompanyContext"/>.
    /// Ports <c>app.initializeUnitTest</c>.
    /// </summary>
    /// <param name="companyId">The internal id (<c>companies.id</c>) of the company to bind.</param>
    public void Bind(long companyId) => Service<ICompanyContext>().Bind(companyId);

    /// <summary>
    /// Deletes the company with internal id <paramref name="companyId"/>, with everything it owns, through
    /// <see cref="CompanyService.DeleteByIdAsync"/>; does nothing when no company has that id.
    /// Ports the <c>delete myCompany</c> of <c>tearDown</c> and <c>afterTest</c>.
    /// </summary>
    /// <param name="companyId">The internal id (<c>companies.id</c>) of the company to delete.</param>
    /// <param name="ct">Token that cancels the delete.</param>
    /// <returns>A task that completes when the company is deleted.</returns>
    /// <exception cref="InvalidOperationException">The delete failed.</exception>
    public async Task DeleteCompanyAsync(long companyId, CancellationToken ct = default)
    {
        OperationResult result = await Service<CompanyService>().DeleteByIdAsync(companyId, TransactionMode.Own, ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Company {companyId} could not be deleted: {result.FullErrorText}");
        }
    }

    /// <summary>
    /// Returns a new address command for 314 Nohwere Lane, Christchurch, New Zealand; phone "555 1234567", fax "FAX",
    /// e-mail "admin@Erewhon.nz", web site "www.jadeworld.com". The address of <c>createAgent</c>.
    /// </summary>
    /// <returns>The new address command.</returns>
    public static AddressCommand NohwereLaneAddress() => new()
    {
        Street = "314 Nohwere Lane",
        City = "Christchurch",
        Country = "New Zealand",
        Phone = "555 1234567",
        Fax = "FAX",
        Email = "admin@Erewhon.nz",
        WebSite = "www.jadeworld.com",
    };

    /// <summary>
    /// Returns a new address command for 555 Fake St., Dunedin, New Zealand; phone "555 5555555", fax "555 4444444",
    /// e-mail "CliEnt@E.mail", web site "www.website.com". The address of <c>createClient</c>.
    /// </summary>
    /// <returns>The new address command.</returns>
    public static AddressCommand FakeStreetAddress() => new()
    {
        Street = "555 Fake St.",
        City = "Dunedin",
        Country = "New Zealand",
        Phone = "555 5555555",
        Fax = "555 4444444",
        Email = "CliEnt@E.mail",
        WebSite = "www.website.com",
    };

    /// <summary>
    /// Returns a new address command for 5 Sir Gill Simpson Drive, Christchurch, New Zealand; phone "+64 3 365 2500",
    /// fax "+64 3 358 7276", e-mail "info@jadeworld.com", web site "www.jadeworld.com". The address of
    /// <c>createCompany</c> and <c>createTestData</c>.
    /// </summary>
    /// <returns>The new address command.</returns>
    public static AddressCommand GillSimpsonDriveAddress() => new()
    {
        Street = "5 Sir Gill Simpson Drive",
        City = "Christchurch",
        Country = "New Zealand",
        Phone = "+64 3 365 2500",
        Fax = "+64 3 358 7276",
        Email = "info@jadeworld.com",
        WebSite = "www.jadeworld.com",
    };

    /// <summary>
    /// Creates the company "Jade Software Corporation" at <see cref="GillSimpsonDriveAddress"/>, without binding it.
    /// Ports <c>JadeTestCase.createCompany</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new company.</returns>
    /// <exception cref="InvalidOperationException">The company was not persisted.</exception>
    public Task<Company> CreateCompanyAsync(CancellationToken ct = default) =>
        PersistCompanyAsync("Jade Software Corporation", GillSimpsonDriveAddress(), ct);

    /// <summary>
    /// Creates the agent "John Smith" at <see cref="NohwereLaneAddress"/> in the bound company.
    /// Ports <c>JadeTestCase.createAgent</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new agent.</returns>
    /// <exception cref="InvalidOperationException">The agent was not persisted.</exception>
    public Task<Agent> CreateAgentAsync(CancellationToken ct = default) =>
        PersistAgentAsync("John Smith", NohwereLaneAddress(), ct);

    /// <summary>
    /// Creates the client "Clive Entworth" at <see cref="FakeStreetAddress"/> in the bound company.
    /// Ports <c>JadeTestCase.createClient</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new client.</returns>
    /// <exception cref="InvalidOperationException">The client was not persisted.</exception>
    public Task<Client> CreateClientAsync(CancellationToken ct = default) =>
        PersistClientAsync("Clive Entworth", FakeStreetAddress(), ct);

    /// <summary>
    /// Creates a commission rate of <paramref name="rate"/> percent on <paramref name="itemCategory"/>, held by
    /// <paramref name="agent"/>. Ports <c>JadeTestCase.createCommissionRate</c>.
    /// </summary>
    /// <param name="itemCategory">The item category of the rate.</param>
    /// <param name="rate">The percentage of the rate.</param>
    /// <param name="agent">The agent that holds the rate.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new commission rate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemCategory"/> or <paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The commission rate was not persisted.</exception>
    public Task<CommissionRate> CreateCommissionRateAsync(
        ItemCategory itemCategory,
        decimal rate,
        Agent agent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemCategory);
        ArgumentNullException.ThrowIfNull(agent);

        var command = new CommissionRateCommand
        {
            ItemCategoryId = itemCategory.Id,
            Percentage = rate,
            AgentIds = [agent.Id],
        };

        return PersistAsync<CommissionRate, CommissionRate>(
            Service<CommissionRateService>(), command, $"commission rate {rate}% on \"{itemCategory.Name}\"", ct);
    }

    /// <summary>Creates the country "France" in the bound company. Ports <c>JadeTestCase.createCountry</c>.</summary>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new country.</returns>
    /// <exception cref="InvalidOperationException">The country was not persisted.</exception>
    public Task<Country> CreateCountryAsync(CancellationToken ct = default) => CreateTestCountryAsync("France", ct);

    /// <summary>
    /// Creates an item category with the given name, prefix and description in the bound company.
    /// Ports <c>JadeTestCase.createItemCategory</c>.
    /// </summary>
    /// <param name="name">The category name.</param>
    /// <param name="prefix">The category prefix.</param>
    /// <param name="description">The category description.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new item category.</returns>
    /// <exception cref="InvalidOperationException">The item category was not persisted.</exception>
    public Task<ItemCategory> CreateItemCategoryAsync(
        string name,
        string prefix,
        string description,
        CancellationToken ct = default) =>
        CreateTestItemCategoryAsync(name, prefix, description, ct);

    /// <summary>Creates the region "Paris" in <paramref name="country"/>. Ports <c>JadeTestCase.createRegion</c>.</summary>
    /// <param name="country">The country of the region.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new region.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="country"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The region was not persisted.</exception>
    public Task<Region> CreateRegionAsync(Country country, CancellationToken ct = default) =>
        CreateTestRegionAsync("Paris", country, ct);

    /// <summary>
    /// Creates the retail item "Proxy Retail", "A Retail item that will be OrderProxy'd", priced 5.50, with no photo,
    /// the next code number of the bound company and the server date as its listed date.
    /// Ports <c>JadeTestCase.createRetailItem</c>.
    /// </summary>
    /// <param name="region">The region the item is listed in.</param>
    /// <param name="itemCategory">The category of the item.</param>
    /// <param name="agent">The agent selling the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new retail item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/>, <paramref name="itemCategory"/> or <paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The retail item was not persisted.</exception>
    public Task<RetailItem> CreateRetailItemAsync(
        Region region,
        ItemCategory itemCategory,
        Agent agent,
        CancellationToken ct = default) =>
        CreateTestRetailItemAsync(
            "Proxy Retail",
            "A Retail item that will be OrderProxy'd",
            null,
            region,
            itemCategory,
            agent,
            5.50m,
            ct);

    /// <summary>
    /// Creates a retail sale of <paramref name="retailItem"/> to <paramref name="client"/> at the sale price 555, sold
    /// at the server timestamp. Ports <c>JadeTestCase.createRetailSale</c>.
    /// </summary>
    /// <param name="retailItem">The item sold.</param>
    /// <param name="client">The client who buys the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new retail sale.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="retailItem"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The retail sale was not persisted.</exception>
    public async Task<RetailSale> CreateRetailSaleAsync(
        RetailItem retailItem,
        Client client,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retailItem);
        ArgumentNullException.ThrowIfNull(client);

        DateTime dateSold = await Service<IServerClock>().GetServerTimeStampAsync(ct);
        return await PersistRetailSaleAsync(retailItem, client, 555m, dateSold, ct);
    }

    /// <summary>
    /// Creates a tender of <paramref name="offer"/> by <paramref name="client"/> on <paramref name="tenderItem"/>,
    /// placed at the server timestamp. Ports <c>JadeTestCase.createTender</c>.
    /// </summary>
    /// <param name="client">The client placing the tender.</param>
    /// <param name="tenderItem">The tender item the tender is placed on.</param>
    /// <param name="offer">The amount offered.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="tenderItem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender was not persisted.</exception>
    public async Task<Tender> CreateTenderAsync(
        Client client,
        TenderItem tenderItem,
        decimal offer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tenderItem);

        DateTime dateTendered = await Service<IServerClock>().GetServerTimeStampAsync(ct);
        return await CreateTestTenderAsync(offer, dateTendered, client, tenderItem, ct);
    }

    /// <summary>
    /// Creates the tender item "Proxy Tender", "A Tender item that will be OrderProxy'd", with the reserve price 5.50,
    /// no photo, the server date as its closure and listed date, and the next code number of the bound company.
    /// Ports <c>JadeTestCase.createTenderItem</c>.
    /// </summary>
    /// <param name="region">The region the item is listed in.</param>
    /// <param name="itemCategory">The category of the item.</param>
    /// <param name="agent">The agent selling the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/>, <paramref name="itemCategory"/> or <paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender item was not persisted.</exception>
    public async Task<TenderItem> CreateTenderItemAsync(
        Region region,
        ItemCategory itemCategory,
        Agent agent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(itemCategory);
        ArgumentNullException.ThrowIfNull(agent);

        DateOnly closureDate = await Service<IServerClock>().GetServerDateAsync(ct);
        return await CreateTestTenderItemAsync(
            "Proxy Tender",
            "A Tender item that will be OrderProxy'd",
            null,
            region,
            itemCategory,
            agent,
            5.50m,
            closureDate,
            ct);
    }

    /// <summary>
    /// Creates a tender sale that awards <paramref name="tenderItem"/> to <paramref name="client"/> through
    /// <paramref name="tender"/>. Ports <c>JadeTestCase.createTenderSale</c>.
    /// </summary>
    /// <param name="tenderItem">The item sold.</param>
    /// <param name="tender">The winning tender.</param>
    /// <param name="client">The client who buys the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender sale.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenderItem"/>, <paramref name="tender"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender sale was not persisted.</exception>
    public Task<TenderSale> CreateTenderSaleAsync(
        TenderItem tenderItem,
        Tender tender,
        Client client,
        CancellationToken ct = default) =>
        CreateTestTenderSaleAsync(tenderItem, tender, client, ct);

    /// <summary>
    /// Creates, in the bound company and in this order, the agent "John Smith" at <see cref="NohwereLaneAddress"/>,
    /// the client "Clive Entworth" at <see cref="FakeStreetAddress"/>, the country "France", its region "Paris", the
    /// item category "Abstractions" ("ABST", "Items that do not model real world observables"), the retail item 1900
    /// "Proxy Retail" and the tender item 2000 "Proxy Tender" (both priced 5.50 with no photo, the tender item closing
    /// yesterday), a tender of 20.00 by the client on the tender item, a commission rate of 25.00 on the category held
    /// by the agent, a retail sale of the retail item to the client at 555, and a tender sale of the tender item to the
    /// client through the tender. Yesterday is the server date less one day; the tender and the retail sale are dated
    /// yesterday at the server's current time of day. No result is checked and nothing is thrown when an object is not
    /// created; a later object that refers to it then refers to no object. No <c>My*</c> property is set.
    /// Ports <c>JadeTestCase.buildCompany</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the creates.</param>
    /// <returns>A task that completes when every create has run.</returns>
    public async Task BuildCompanyAsync(CancellationToken ct = default)
    {
        DateTime serverTimeStamp = await Service<IServerClock>().GetServerTimeStampAsync(ct);
        DateOnly date = DateOnly.FromDateTime(serverTimeStamp).AddDays(-1);
        DateTime timestamp = date.ToDateTime(TimeOnly.FromDateTime(serverTimeStamp));

        var agentCommand = new AgentCommand { Name = "John Smith", Address = NohwereLaneAddress() };
        await Service<AgentService>().ExecuteAsync(agentCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var clientCommand = new ClientCommand { Name = "Clive Entworth", Address = FakeStreetAddress() };
        await Service<ClientService>().ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var countryCommand = new CountryCommand { Name = "France" };
        await Service<CountryService>().ExecuteAsync(countryCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var regionCommand = new RegionCommand { Name = "Paris", CountryId = countryCommand.ModelObject?.Id };
        await Service<RegionService>().ExecuteAsync(regionCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var itemCategoryCommand = new ItemCategoryCommand
        {
            Prefix = "ABST",
            Name = "Abstractions",
            Description = "Items that do not model real world observables",
        };
        await Service<ItemCategoryService>()
            .ExecuteAsync(itemCategoryCommand, TransactionType.Persist, TransactionMode.Own, ct);

        ItemService itemService = Service<ItemService>();
        var retailItemCommand = new RetailItemCommand
        {
            CodeNumber = 1900,
            Name = "Proxy Retail",
            Description = "A Retail item that will be OrderProxy'd",
            RegionId = regionCommand.ModelObject?.Id,
            ItemCategoryId = itemCategoryCommand.ModelObject?.Id,
            AgentId = agentCommand.ModelObject?.Id,
            Price = 5.50m,
        };
        await itemService.ExecuteAsync(retailItemCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var tenderItemCommand = new TenderItemCommand
        {
            CodeNumber = 2000,
            Name = "Proxy Tender",
            Description = "A Tender item that will be OrderProxy'd",
            ClosureDate = date,
            RegionId = regionCommand.ModelObject?.Id,
            ItemCategoryId = itemCategoryCommand.ModelObject?.Id,
            AgentId = agentCommand.ModelObject?.Id,
            Price = 5.50m,
        };
        await itemService.ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var tenderCommand = new TenderCommand
        {
            Offer = 20.00m,
            DateTendered = timestamp,
            ClientId = clientCommand.ModelObject?.Id,
            TenderItemId = tenderItemCommand.ModelObject?.Id,
        };
        await Service<TenderService>().ExecuteAsync(tenderCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var commissionRateCommand = new CommissionRateCommand
        {
            ItemCategoryId = itemCategoryCommand.ModelObject?.Id,
            Percentage = 25.00m,
            AgentIds = agentCommand.ModelObject is { } agent ? [agent.Id] : [],
        };
        await Service<CommissionRateService>()
            .ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItemCommand.ModelObject?.Id,
            ClientId = clientCommand.ModelObject?.Id,
            SalePrice = 555m,
            DateSold = timestamp,
        };
        await Service<RetailSaleService>()
            .ExecuteAsync(retailSaleCommand, TransactionType.Persist, TransactionMode.Own, ct);

        var tenderSaleCommand = new TenderSaleCommand
        {
            ItemId = tenderItemCommand.ModelObject?.Id,
            TenderId = tenderCommand.ModelObject?.Id,
            ClientId = clientCommand.ModelObject?.Id,
        };
        await Service<TenderSaleService>()
            .ExecuteAsync(tenderSaleCommand, TransactionType.Persist, TransactionMode.Own, ct);
    }

    /// <summary>
    /// Returns a new address with the given seven fields, trimmed of white space and validated as a new address, and a
    /// new GUID. The address is not stored. Ports <c>TestErewhonExternalInterfaces.createTestAddress</c>.
    /// </summary>
    /// <param name="street">The street.</param>
    /// <param name="city">The city.</param>
    /// <param name="country">The country name.</param>
    /// <param name="phone">The phone number.</param>
    /// <param name="fax">The fax number.</param>
    /// <param name="email">The e-mail address.</param>
    /// <param name="webSite">The web site.</param>
    /// <returns>The new address.</returns>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    /// <exception cref="InvalidOperationException">The address failed validation.</exception>
    public Address CreateTestAddress(
        string street,
        string city,
        string country,
        string phone,
        string fax,
        string email,
        string webSite)
    {
        var command = new AddressCommand
        {
            Street = street,
            City = city,
            Country = country,
            Phone = phone,
            Fax = fax,
            Email = email,
            WebSite = webSite,
        };

        AddressRules.CheckFieldLengths(command);
        AddressRules.PreValidate(command);

        var result = new OperationResult();
        if (!AddressRules.Validate(command, ValidationType.Create, result))
        {
            throw NotPersisted($"address \"{street}\"", result);
        }

        var address = new Address();
        AddressRules.Apply(command, address);
        return address;
    }

    /// <summary>
    /// Creates an agent named <paramref name="name"/> in the bound company, with the seven fields of
    /// <paramref name="address"/> as its address. Ports <c>TestErewhonExternalInterfaces.createTestAgent</c>.
    /// </summary>
    /// <param name="name">The agent name.</param>
    /// <param name="address">The address whose street, city, country, phone, fax, e-mail and web site are copied.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new agent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The agent was not persisted.</exception>
    public Task<Agent> CreateTestAgentAsync(string name, Address address, CancellationToken ct = default) =>
        PersistAgentAsync(name, CopyAddressFields(address), ct);

    /// <summary>
    /// Creates a client named <paramref name="name"/> in the bound company, with the seven fields of
    /// <paramref name="address"/> as its address. Ports <c>TestErewhonExternalInterfaces.createTestClient</c>.
    /// </summary>
    /// <param name="name">The client name.</param>
    /// <param name="address">The address whose street, city, country, phone, fax, e-mail and web site are copied.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new client.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client was not persisted.</exception>
    public Task<Client> CreateTestClientAsync(string name, Address address, CancellationToken ct = default) =>
        PersistClientAsync(name, CopyAddressFields(address), ct);

    /// <summary>
    /// Creates a commission rate of <paramref name="rate"/> percent on <paramref name="itemCategory"/>, held by no
    /// agent. Ports <c>TestErewhonExternalInterfaces.createTestCommissionRate</c>.
    /// </summary>
    /// <param name="itemCategory">The item category of the rate.</param>
    /// <param name="rate">The percentage of the rate.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new commission rate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemCategory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The commission rate was not persisted.</exception>
    public Task<CommissionRate> CreateTestCommissionRateAsync(
        ItemCategory itemCategory,
        decimal rate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemCategory);

        var command = new CommissionRateCommand { ItemCategoryId = itemCategory.Id, Percentage = rate };
        return PersistAsync<CommissionRate, CommissionRate>(
            Service<CommissionRateService>(), command, $"commission rate {rate}% on \"{itemCategory.Name}\"", ct);
    }

    /// <summary>
    /// Adds <paramref name="agent"/> to the agents holding <paramref name="commissionRate"/>, when it does not hold the
    /// rate already, by updating the rate with the agents it holds plus <paramref name="agent"/>.
    /// Ports <c>TestErewhonExternalInterfaces.addTestAgentToCommissionRate</c>.
    /// </summary>
    /// <param name="agent">The agent that is to hold the rate.</param>
    /// <param name="commissionRate">The commission rate to update.</param>
    /// <param name="ct">Token that cancels the update.</param>
    /// <returns>A task that completes when the rate is updated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> or <paramref name="commissionRate"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The commission rate does not exist or was not persisted.</exception>
    public async Task AddTestAgentToCommissionRateAsync(
        Agent agent,
        CommissionRate commissionRate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(commissionRate);

        CommissionRateService service = Service<CommissionRateService>();
        CommissionRate storedRate = await service.FindByIdAsync(commissionRate.Id, ct)
            ?? throw new InvalidOperationException($"Commission rate {commissionRate.Id} does not exist.");

        var command = new CommissionRateCommand();
        command.PopulateFromObject(storedRate);
        if (!command.AgentIds.Contains(agent.Id))
        {
            command.AgentIds.Add(agent.Id);
        }

        await PersistAsync<CommissionRate, CommissionRate>(
            service, command, $"commission rate {storedRate.Percentage}% with agent \"{agent.Name}\"", ct);
    }

    /// <summary>
    /// Creates a country named <paramref name="name"/> in the bound company.
    /// Ports <c>TestErewhonExternalInterfaces.createTestCountry</c>.
    /// </summary>
    /// <param name="name">The country name.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new country.</returns>
    /// <exception cref="InvalidOperationException">The country was not persisted.</exception>
    public Task<Country> CreateTestCountryAsync(string name, CancellationToken ct = default) =>
        PersistAsync<Country, Country>(
            Service<CountryService>(), new CountryCommand { Name = name }, $"country \"{name}\"", ct);

    /// <summary>
    /// Creates an item category with the given name, prefix and description in the bound company.
    /// Ports <c>TestErewhonExternalInterfaces.createTestItemCategory</c>.
    /// </summary>
    /// <param name="name">The category name.</param>
    /// <param name="prefix">The category prefix.</param>
    /// <param name="description">The category description.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new item category.</returns>
    /// <exception cref="InvalidOperationException">The item category was not persisted.</exception>
    public Task<ItemCategory> CreateTestItemCategoryAsync(
        string name,
        string prefix,
        string description,
        CancellationToken ct = default)
    {
        var command = new ItemCategoryCommand { Name = name, Prefix = prefix, Description = description };
        return PersistAsync<ItemCategory, ItemCategory>(
            Service<ItemCategoryService>(), command, $"item category \"{name}\"", ct);
    }

    /// <summary>
    /// Creates a region named <paramref name="name"/> in <paramref name="country"/>.
    /// Ports <c>TestErewhonExternalInterfaces.createTestRegion</c>.
    /// </summary>
    /// <param name="name">The region name.</param>
    /// <param name="country">The country of the region.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new region.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="country"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The region was not persisted.</exception>
    public Task<Region> CreateTestRegionAsync(string name, Country country, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(country);

        var command = new RegionCommand { Name = name, CountryId = country.Id };
        return PersistAsync<Region, Region>(Service<RegionService>(), command, $"region \"{name}\"", ct);
    }

    /// <summary>
    /// Creates a retail item with the given name, description, photo and price, the next code number of the bound
    /// company and the server date as its listed date. Ports <c>TestErewhonExternalInterfaces.createTestRetailItem</c>.
    /// </summary>
    /// <param name="name">The item name.</param>
    /// <param name="description">The item description.</param>
    /// <param name="photo">The item picture, or <see langword="null"/> for none.</param>
    /// <param name="region">The region the item is listed in.</param>
    /// <param name="itemCategory">The category of the item.</param>
    /// <param name="agent">The agent selling the item.</param>
    /// <param name="price">The price of the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new retail item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/>, <paramref name="itemCategory"/> or <paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The retail item was not persisted.</exception>
    public Task<RetailItem> CreateTestRetailItemAsync(
        string name,
        string description,
        byte[]? photo,
        Region region,
        ItemCategory itemCategory,
        Agent agent,
        decimal price,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(itemCategory);
        ArgumentNullException.ThrowIfNull(agent);

        var command = new RetailItemCommand
        {
            Name = name,
            Description = description,
            Photo = photo,
            RegionId = region.Id,
            ItemCategoryId = itemCategory.Id,
            AgentId = agent.Id,
            Price = price,
        };

        return PersistAsync<Item, RetailItem>(Service<ItemService>(), command, $"retail item \"{name}\"", ct);
    }

    /// <summary>
    /// Creates a tender item with the given name, description, photo, reserve price and closure date, the next code
    /// number of the bound company and the server date as its listed date.
    /// Ports <c>TestErewhonExternalInterfaces.createTestTenderItem</c>.
    /// </summary>
    /// <param name="name">The item name.</param>
    /// <param name="description">The item description.</param>
    /// <param name="photo">The item picture, or <see langword="null"/> for none.</param>
    /// <param name="region">The region the item is listed in.</param>
    /// <param name="itemCategory">The category of the item.</param>
    /// <param name="agent">The agent selling the item.</param>
    /// <param name="price">The reserve price of the item.</param>
    /// <param name="closureDate">The date tendering on the item closes.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="region"/>, <paramref name="itemCategory"/> or <paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender item was not persisted.</exception>
    public Task<TenderItem> CreateTestTenderItemAsync(
        string name,
        string description,
        byte[]? photo,
        Region region,
        ItemCategory itemCategory,
        Agent agent,
        decimal price,
        DateOnly closureDate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(itemCategory);
        ArgumentNullException.ThrowIfNull(agent);

        var command = new TenderItemCommand
        {
            Name = name,
            Description = description,
            Photo = photo,
            RegionId = region.Id,
            ItemCategoryId = itemCategory.Id,
            AgentId = agent.Id,
            Price = price,
            ClosureDate = closureDate,
        };

        return PersistAsync<Item, TenderItem>(Service<ItemService>(), command, $"tender item \"{name}\"", ct);
    }

    /// <summary>
    /// Creates a retail sale of <paramref name="retailItem"/> to <paramref name="client"/> at the item's price, sold at
    /// <paramref name="dateSold"/>. Ports <c>TestErewhonExternalInterfaces.createTestRetailSale</c>.
    /// </summary>
    /// <param name="retailItem">The item sold.</param>
    /// <param name="client">The client who buys the item.</param>
    /// <param name="dateSold">The time the item is sold.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new retail sale.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="retailItem"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The retail sale was not persisted.</exception>
    public Task<RetailSale> CreateTestRetailSaleAsync(
        RetailItem retailItem,
        Client client,
        DateTime dateSold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retailItem);
        ArgumentNullException.ThrowIfNull(client);

        return PersistRetailSaleAsync(retailItem, client, retailItem.Price, dateSold, ct);
    }

    /// <summary>
    /// Creates a tender of <paramref name="offer"/> by <paramref name="client"/> on <paramref name="tenderItem"/>,
    /// placed at <paramref name="dateTendered"/>. Ports <c>TestErewhonExternalInterfaces.createTestTender</c>.
    /// </summary>
    /// <param name="offer">The amount offered.</param>
    /// <param name="dateTendered">The time the tender is placed.</param>
    /// <param name="client">The client placing the tender.</param>
    /// <param name="tenderItem">The tender item the tender is placed on.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="tenderItem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender was not persisted.</exception>
    public Task<Tender> CreateTestTenderAsync(
        decimal offer,
        DateTime dateTendered,
        Client client,
        TenderItem tenderItem,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tenderItem);

        var command = new TenderCommand
        {
            Offer = offer,
            DateTendered = dateTendered,
            ClientId = client.Id,
            TenderItemId = tenderItem.Id,
        };

        return PersistAsync<Tender, Tender>(
            Service<TenderService>(), command, $"tender {offer} on \"{tenderItem.Name}\"", ct);
    }

    /// <summary>
    /// Creates a tender sale that awards <paramref name="tenderItem"/> to <paramref name="client"/> through
    /// <paramref name="tender"/>. Ports <c>TestErewhonExternalInterfaces.createTestTenderSale</c>.
    /// </summary>
    /// <param name="tenderItem">The item sold.</param>
    /// <param name="tender">The winning tender.</param>
    /// <param name="client">The client who buys the item.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>The new tender sale.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tenderItem"/>, <paramref name="tender"/> or <paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The tender sale was not persisted.</exception>
    public Task<TenderSale> CreateTestTenderSaleAsync(
        TenderItem tenderItem,
        Tender tender,
        Client client,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenderItem);
        ArgumentNullException.ThrowIfNull(tender);
        ArgumentNullException.ThrowIfNull(client);

        var command = new TenderSaleCommand { ItemId = tenderItem.Id, TenderId = tender.Id, ClientId = client.Id };
        return PersistAsync<Sale, TenderSale>(
            Service<TenderSaleService>(), command, $"tender sale of \"{tenderItem.Name}\"", ct);
    }

    /// <summary>
    /// Creates the company "Jade Software Test Company" at <see cref="GillSimpsonDriveAddress"/> and binds it, then
    /// creates in it, in this order: the client "Test Client" and the agent "Test Agent", both at "Street", "City",
    /// "Country", phone "Phone", fax "Fax", web site "www.website.com", with the e-mail "client@email.com" and
    /// "agent@email.com"; the item category "Test Category 1" ("CAT1", "This is a test category"); the country
    /// "Country Name" and its region "Region Name"; the retail item "Retail Item" ("A retail item for testing", 999.99)
    /// and the tender item "Tender Item" ("A tender item for testing", reserve 888.88, closing seven days after the
    /// server date), both with <see cref="TestPhoto"/> and sold by the agent; a tender by the client on the tender item
    /// of its reserve plus 1, placed at the server timestamp; a retail sale of the retail item to the client at its
    /// price, sold at the server timestamp; and a tender sale of the tender item to the client through the tender.
    /// Sets <see cref="MyCompany"/>, <see cref="MyClient"/>, <see cref="MyAgent"/>, <see cref="MyItemCategory"/>,
    /// <see cref="MyCountry"/>, <see cref="MyRegion"/>, <see cref="MyRetailItem"/>, <see cref="MyTenderItem"/>,
    /// <see cref="MyTender"/>, <see cref="MyRetailSale"/> and <see cref="MyTenderSale"/> as each is created.
    /// Ports <c>TestErewhonExternalInterfaces.createTestData</c>.
    /// </summary>
    /// <param name="ct">Token that cancels the creates.</param>
    /// <returns>A task that completes when the test data is created.</returns>
    /// <exception cref="InvalidOperationException">An object was not persisted.</exception>
    public async Task CreateTestDataAsync(CancellationToken ct = default)
    {
        IServerClock clock = Service<IServerClock>();

        MyCompany = await PersistCompanyAsync("Jade Software Test Company", GillSimpsonDriveAddress(), ct);
        Bind(MyCompany.Id);

        Address clientAddress = CreateTestAddress(
            "Street", "City", "Country", "Phone", "Fax", "client@email.com", "www.website.com");
        Address agentAddress = CreateTestAddress(
            "Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.website.com");

        MyClient = await CreateTestClientAsync("Test Client", clientAddress, ct);
        MyAgent = await CreateTestAgentAsync("Test Agent", agentAddress, ct);
        MyItemCategory = await CreateTestItemCategoryAsync("Test Category 1", "CAT1", "This is a test category", ct);
        MyCountry = await CreateTestCountryAsync("Country Name", ct);
        MyRegion = await CreateTestRegionAsync("Region Name", MyCountry, ct);
        MyRetailItem = await CreateTestRetailItemAsync(
            "Retail Item",
            "A retail item for testing",
            TestPhoto.ToArray(),
            MyRegion,
            MyItemCategory,
            MyAgent,
            999.99m,
            ct);
        MyTenderItem = await CreateTestTenderItemAsync(
            "Tender Item",
            "A tender item for testing",
            TestPhoto.ToArray(),
            MyRegion,
            MyItemCategory,
            MyAgent,
            888.88m,
            (await clock.GetServerDateAsync(ct)).AddDays(7),
            ct);
        MyTender = await CreateTestTenderAsync(
            MyTenderItem.Price + 1,
            await clock.GetServerTimeStampAsync(ct),
            MyClient,
            MyTenderItem,
            ct);
        MyRetailSale = await CreateTestRetailSaleAsync(
            MyRetailItem,
            MyClient,
            await clock.GetServerTimeStampAsync(ct),
            ct);
        MyTenderSale = await CreateTestTenderSaleAsync(MyTenderItem, MyTender, MyClient, ct);
    }

    /// <summary>Creates a company with the given name and address, without binding it.</summary>
    private Task<Company> PersistCompanyAsync(string name, AddressCommand address, CancellationToken ct) =>
        PersistAsync<Company, Company>(
            Service<CompanyService>(), new CompanyCommand { Name = name, Address = address }, $"company \"{name}\"", ct);

    /// <summary>Creates an agent with the given name and address in the bound company.</summary>
    private Task<Agent> PersistAgentAsync(string name, AddressCommand address, CancellationToken ct) =>
        PersistAsync<Agent, Agent>(
            Service<AgentService>(), new AgentCommand { Name = name, Address = address }, $"agent \"{name}\"", ct);

    /// <summary>Creates a client with the given name and address in the bound company.</summary>
    private Task<Client> PersistClientAsync(string name, AddressCommand address, CancellationToken ct) =>
        PersistAsync<Client, Client>(
            Service<ClientService>(), new ClientCommand { Name = name, Address = address }, $"client \"{name}\"", ct);

    /// <summary>Creates a retail sale of the item to the client at the given price and time.</summary>
    private Task<RetailSale> PersistRetailSaleAsync(
        RetailItem retailItem,
        Client client,
        decimal salePrice,
        DateTime dateSold,
        CancellationToken ct)
    {
        var command = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = client.Id,
            SalePrice = salePrice,
            DateSold = dateSold,
        };

        return PersistAsync<Sale, RetailSale>(
            Service<RetailSaleService>(), command, $"retail sale of \"{retailItem.Name}\"", ct);
    }

    /// <summary>
    /// Returns a new address command holding the street, city, country, phone, fax, e-mail and web site of
    /// <paramref name="address"/>, with no GUID.
    /// </summary>
    private static AddressCommand CopyAddressFields(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);

        AddressCommand command = AddressCommand.FromAddress(address);
        command.Guid = null;
        return command;
    }

    /// <summary>
    /// Persists <paramref name="command"/> through <paramref name="agent"/> with <see cref="TransactionType.Persist"/>
    /// in <see cref="TransactionMode.Own"/> mode and returns its model object, as <see cref="Succeeded{TEntity, TResult}"/> does.
    /// </summary>
    private static async Task<TResult> PersistAsync<TEntity, TResult>(
        TransactionAgent<TEntity> agent,
        TransactionCommand<TEntity> command,
        string fixture,
        CancellationToken ct)
        where TEntity : class
        where TResult : class, TEntity
    {
        OperationResult result = await agent.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own, ct);
        return Succeeded<TEntity, TResult>(result, command, fixture);
    }

    /// <summary>
    /// Returns the model object of <paramref name="command"/> as a <typeparamref name="TResult"/> when
    /// <paramref name="result"/> succeeded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The operation failed, or left no model object of type <typeparamref name="TResult"/>.
    /// </exception>
    private static TResult Succeeded<TEntity, TResult>(
        OperationResult result,
        TransactionCommand<TEntity> command,
        string fixture)
        where TEntity : class
        where TResult : class, TEntity
    {
        if (!result.Succeeded)
        {
            throw NotPersisted(fixture, result);
        }

        return command.ModelObject as TResult
            ?? throw new InvalidOperationException(
                $"Fixture {fixture} was persisted as row {result.Id} without a {typeof(TResult).Name} model object.");
    }

    /// <summary>Returns the exception reporting that <paramref name="fixture"/> was not persisted.</summary>
    private static InvalidOperationException NotPersisted(string fixture, OperationResult result) =>
        new($"Fixture {fixture} could not be persisted: {result.FullErrorText}");

    /// <summary>Returns the service of type <typeparamref name="T"/> from the builder's scope.</summary>
    private T Service<T>()
        where T : notnull =>
        _services.GetRequiredService<T>();
}
