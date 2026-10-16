using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the 20 tests of the JADE class <c>TestCompany</c>. Each test starts with the company
/// "Jade Software Corporation", bound to the test's scope, holding the agent "John Smith", the client
/// "Clive Entworth", the country "France" with its region "Paris", the item category "Test Category" ("TEST"), a
/// tender item closing on the server date with a tender of its reserve plus 10, and a retail item; no commission rate
/// exists. Every object is persisted with <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Own"/>
/// mode, and every persisted object is read back from the database without change tracking before it is asserted.
/// </summary>
public sealed class TestCompany : ErewhonTestBase
{
    private Company MyCompany => Builder.MyCompany ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyCompany));

    private Agent MyAgent => Builder.MyAgent ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyAgent));

    private Client MyClient => Builder.MyClient ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyClient));

    private Region MyRegion => Builder.MyRegion ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyRegion));

    private ItemCategory MyItemCategory =>
        Builder.MyItemCategory ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyItemCategory));

    private RetailItem MyRetailItem =>
        Builder.MyRetailItem ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyRetailItem));

    private TenderItem MyTenderItem =>
        Builder.MyTenderItem ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyTenderItem));

    private Tender MyTender => Builder.MyTender ?? throw FixtureMissing(nameof(TestCompanyBuilder.MyTender));

    private IErewhonDbContext Db => GetService<IErewhonDbContext>();

    /// <summary>
    /// Creates and binds the company, then creates, in this order, the agent, the client, the country, the region,
    /// the item category "Test Category" ("TEST", "Test Category Description"), the tender item, a tender by the
    /// client on the tender item offering its price plus 10, and the retail item. Ports <c>TestCompany.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyClient = await Builder.CreateClientAsync();
        Country myCountry = await Builder.CreateCountryAsync();
        Builder.MyCountry = myCountry;
        Builder.MyRegion = await Builder.CreateRegionAsync(myCountry);
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Test Category", "TEST", "Test Category Description");
        Builder.MyTenderItem = await Builder.CreateTenderItemAsync(MyRegion, MyItemCategory, MyAgent);
        Builder.MyTender = await Builder.CreateTenderAsync(MyClient, MyTenderItem, MyTenderItem.Price + 10);
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(MyRegion, MyItemCategory, MyAgent);
    }

    /// <summary>
    /// <c>agentExists</c> finds "John Smith" and finds neither "Not Jane Doe", "Clissa Entworth", <see langword="null"/>
    /// nor the empty string.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAgentAlreadyExists()
    {
        CompanyService companyService = GetService<CompanyService>();

        Assert.True(await companyService.AgentExists(MyCompany.Id, "John Smith"));
        Assert.False(await companyService.AgentExists(MyCompany.Id, "Not Jane Doe"));
        Assert.False(await companyService.AgentExists(MyCompany.Id, "Clissa Entworth"));
        Assert.False(await companyService.AgentExists(MyCompany.Id, null));
        Assert.False(await companyService.AgentExists(MyCompany.Id, ""));
    }

    /// <summary>
    /// <c>hasClientWithThisName</c> finds "Clive Entworth" and finds neither "Not Clissa Entworth", "Jane Doe",
    /// <see langword="null"/> nor the empty string.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testClientAlreadyExists()
    {
        CompanyService companyService = GetService<CompanyService>();

        Assert.True(await companyService.HasClientWithThisName(MyCompany.Id, "Clive Entworth"));
        Assert.False(await companyService.HasClientWithThisName(MyCompany.Id, "Not Clissa Entworth"));
        Assert.False(await companyService.HasClientWithThisName(MyCompany.Id, "Jane Doe"));
        Assert.False(await companyService.HasClientWithThisName(MyCompany.Id, null));
        Assert.False(await companyService.HasClientWithThisName(MyCompany.Id, ""));
    }

    /// <summary>
    /// The company holds no tender sale; closing tenders at the server date then awards the tender item, which closes
    /// on that date, and reports 1 tender closed. The company then holds exactly one tender sale, of the fixture tender
    /// item to the fixture client through the fixture tender, and the fixture tender item is sold by that sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCloseTenders()
    {
        int tenderSaleCount = await Db.Sales
            .AsNoTracking()
            .Where(sale => sale.CompanyId == MyCompany.Id)
            .OfType<TenderSale>()
            .CountAsync();

        Assert.Equal(0, tenderSaleCount);

        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();
        int tendersClosedCount = await GetService<TenderClosureService>().CloseTendersAtDateAsync(date);

        Assert.Equal(1, tendersClosedCount);

        List<TenderSale> tenderSales = await Db.Sales
            .AsNoTracking()
            .Where(sale => sale.CompanyId == MyCompany.Id)
            .OfType<TenderSale>()
            .ToListAsync();
        TenderSale tenderSale = Assert.Single(tenderSales);

        Assert.Equal(MyTenderItem.Id, tenderSale.ItemId);
        Assert.Equal(MyTender.Id, tenderSale.TenderId);
        Assert.Equal(MyClient.Id, tenderSale.ClientId);

        long tenderItemId = MyTenderItem.Id;
        TenderItem tenderItem = await Db.Items
            .AsNoTracking()
            .OfType<TenderItem>()
            .Include(item => item.Sale)
            .SingleAsync(stored => stored.Id == tenderItemId);

        Assert.True(tenderItem.IsSold);
        Assert.NotNull(tenderItem.Sale);
        Assert.Equal(tenderSale.Id, tenderItem.Sale.Id);
    }

    /// <summary><c>hasClientWithThisName</c> finds the name of the fixture client.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCompanyHasClient()
    {
        Assert.True(await GetService<CompanyService>().HasClientWithThisName(MyCompany.Id, MyClient.Name));
    }

    /// <summary>
    /// The address steps of an owner's create, run on their own: the field-length check, the pre-validation and the
    /// create validation of "314 Nowhere Lane", "Christchurch", "New Zealand", "admin@erewhon.nz", "555 1234567",
    /// "FAX" and "www.jadeworld.com" add no error, and applying the values to a new, unstored
    /// <see cref="Address"/> gives an address holding all seven.
    /// </summary>
    [Fact]
    public void testCreateAddress()
    {
        const string street = "314 Nowhere Lane";
        const string city = "Christchurch";
        const string country = "New Zealand";
        const string email = "admin@erewhon.nz";
        const string phone = "555 1234567";
        const string fax = "FAX";
        const string website = "www.jadeworld.com";

        var addressCommand = new AddressCommand
        {
            Street = street,
            City = city,
            Country = country,
            Email = email,
            Phone = phone,
            Fax = fax,
            WebSite = website,
        };

        AddressRules.CheckFieldLengths(addressCommand);
        AddressRules.PreValidate(addressCommand);
        var result = new OperationResult();
        bool valid = AddressRules.Validate(addressCommand, ValidationType.Create, result);
        Assert.True(valid, result.FullErrorText);

        var address = new Address();
        AddressRules.Apply(addressCommand, address);

        Assert.NotNull(address);
        Assert.Equal(street, address.Street);
        Assert.Equal(city, address.City);
        Assert.Equal(country, address.Country);
        Assert.Equal(email, address.Email);
        Assert.Equal(fax, address.Fax);
        Assert.Equal(phone, address.Phone);
        Assert.Equal(website, address.WebSite);
    }

    /// <summary>
    /// Creating the agent "Namer Namington" at "314 Nowhere Lane", "Christchurch", "New Zealand", e-mail
    /// "admin@erewhon.nz", phone "555 1234567", fax "FAX", web site "www.jadeworld.com" stores the name, the seven
    /// address fields and the fixture company.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateAgent()
    {
        const string name = "Namer Namington";
        const string street = "314 Nowhere Lane";
        const string city = "Christchurch";
        const string country = "New Zealand";
        const string email = "admin@erewhon.nz";
        const string phone = "555 1234567";
        const string fax = "FAX";
        const string website = "www.jadeworld.com";

        var agentCommand = new AgentCommand
        {
            Name = name,
            Address = new AddressCommand
            {
                Street = street,
                City = city,
                Country = country,
                Email = email,
                Phone = phone,
                Fax = fax,
                WebSite = website,
            },
        };

        OperationResult result = await GetService<AgentService>()
            .ExecuteAsync(agentCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(agentCommand.ModelObject);
        long agentId = agentCommand.ModelObject.Id;
        Agent agent = await Db.Agents.AsNoTracking().SingleAsync(stored => stored.Id == agentId);

        Assert.Equal(name, agent.Name);
        Assert.Equal(street, agent.Address.Street);
        Assert.Equal(city, agent.Address.City);
        Assert.Equal(country, agent.Address.Country);
        Assert.Equal(email, agent.Address.Email);
        Assert.Equal(fax, agent.Address.Fax);
        Assert.Equal(phone, agent.Address.Phone);
        Assert.Equal(website, agent.Address.WebSite);
        Assert.Equal(MyCompany.Id, agent.CompanyId);
    }

    /// <summary>
    /// Creating the item category "Pets" ("PETS", "Cats, Dogs and other creatures") stores its name and description.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateCategory()
    {
        const string name = "Pets";
        const string prefix = "PETS";
        const string description = "Cats, Dogs and other creatures";

        var itemCategoryCommand = new ItemCategoryCommand { Name = name, Prefix = prefix, Description = description };
        OperationResult result = await GetService<ItemCategoryService>()
            .ExecuteAsync(itemCategoryCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(itemCategoryCommand.ModelObject);
        long itemCategoryId = itemCategoryCommand.ModelObject.Id;
        ItemCategory itemCategory = await Db.ItemCategories
            .AsNoTracking()
            .SingleAsync(stored => stored.Id == itemCategoryId);

        Assert.Equal(name, itemCategory.Name);
        Assert.Equal(description, itemCategory.Description);
    }

    /// <summary>
    /// Creating the client "Client Clienton" at "555 Road Street", "Citieville", "Republic of Countryton", phone
    /// "999 888 777", fax "555 666 777", e-mail "client@somedomain.co", web site "www.clienton.com" stores the name,
    /// the seven address fields and the fixture company.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateClient()
    {
        const string name = "Client Clienton";
        const string street = "555 Road Street";
        const string city = "Citieville";
        const string country = "Republic of Countryton";
        const string phone = "999 888 777";
        const string fax = "555 666 777";
        const string email = "client@somedomain.co";
        const string website = "www.clienton.com";

        var clientCommand = new ClientCommand
        {
            Name = name,
            Address = new AddressCommand
            {
                Street = street,
                City = city,
                Country = country,
                Phone = phone,
                Fax = fax,
                Email = email,
                WebSite = website,
            },
        };

        OperationResult result = await GetService<ClientService>()
            .ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(clientCommand.ModelObject);
        long clientId = clientCommand.ModelObject.Id;
        Client client = await Db.Clients.AsNoTracking().SingleAsync(stored => stored.Id == clientId);

        Assert.Equal(name, client.Name);
        Assert.Equal(street, client.Address.Street);
        Assert.Equal(city, client.Address.City);
        Assert.Equal(country, client.Address.Country);
        Assert.Equal(email, client.Address.Email);
        Assert.Equal(fax, client.Address.Fax);
        Assert.Equal(phone, client.Address.Phone);
        Assert.Equal(website, client.Address.WebSite);
        Assert.Equal(MyCompany.Id, client.CompanyId);
    }

    /// <summary>
    /// Creating the company "Test Company" at "5 Sir Gill Simpson Drive", "Christchurch", "New Zealand", phone
    /// "+64 3 365 2500", fax "+64 3 358 7276", e-mail "info@jadeworld.com", web site "www.jadeworld.com" through
    /// <see cref="CompanyService.CreateAsync"/>, beside the fixture company, raises the number of companies by one.
    /// The new company is deleted afterwards and the fixture company stays bound.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateCompany()
    {
        int companyCount = await Db.Companies.AsNoTracking().CountAsync();

        var companyCommand = new CompanyCommand
        {
            Name = "Test Company",
            Address = TestCompanyBuilder.GillSimpsonDriveAddress(),
        };

        try
        {
            await GetService<CompanyService>().CreateAsync(companyCommand, TransactionMode.Own);

            Assert.Equal(companyCount + 1, await Db.Companies.AsNoTracking().CountAsync());
        }
        finally
        {
            if (companyCommand.ModelObject is { } company)
            {
                await Builder.DeleteCompanyAsync(company.Id);
            }
        }
    }

    /// <summary>Creating the country "England" stores its name and the fixture company.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateCountry()
    {
        const string name = "England";

        var countryCommand = new CountryCommand { Name = name };
        OperationResult result = await GetService<CountryService>()
            .ExecuteAsync(countryCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(countryCommand.ModelObject);
        long countryId = countryCommand.ModelObject.Id;
        Country country = await Db.Countries.AsNoTracking().SingleAsync(stored => stored.Id == countryId);

        Assert.Equal(name, country.Name);
        Assert.Equal(MyCompany.Id, country.CompanyId);
    }

    /// <summary>
    /// Creating the retail item 3141 "Retail Item" ("This item represents a retail item for purchase"), priced
    /// 1234.56 with no photo, in the fixture region and category and sold by the fixture agent, stores the code number,
    /// price, name, description, agent, company, region and category.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateRetailItem()
    {
        const int codeNumber = 3141;
        const string name = "Retail Item";
        const string description = "This item represents a retail item for purchase";
        const decimal price = 1234.56m;

        var retailItemCommand = new RetailItemCommand
        {
            CodeNumber = codeNumber,
            Name = name,
            Description = description,
            Photo = null,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = price,
        };

        OperationResult result = await GetService<ItemService>()
            .ExecuteAsync(retailItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(retailItemCommand.ModelObject);
        long retailItemId = retailItemCommand.ModelObject.Id;
        RetailItem retailItem = await Db.Items
            .AsNoTracking()
            .OfType<RetailItem>()
            .SingleAsync(stored => stored.Id == retailItemId);

        Assert.Equal(codeNumber, retailItem.CodeNumber);
        Assert.Equal(price, retailItem.Price);
        Assert.Equal(name, retailItem.Name);
        Assert.Equal(description, retailItem.Description);
        Assert.Equal(MyAgent.Id, retailItem.AgentId);
        Assert.Equal(MyCompany.Id, retailItem.CompanyId);
        Assert.Equal(MyRegion.Id, retailItem.RegionId);
        Assert.Equal(MyItemCategory.Id, retailItem.ItemCategoryId);
    }

    /// <summary>
    /// Creating a retail sale of the fixture retail item to the fixture client at 1000000.00, sold at the server
    /// timestamp, stores that sale price and that time.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateRetailSale()
    {
        const decimal price = 1000000.00m;
        DateTime timestamp = await GetService<IServerClock>().GetServerTimeStampAsync();

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = MyRetailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = price,
            DateSold = timestamp,
        };

        OperationResult result = await GetService<RetailSaleService>()
            .ExecuteAsync(retailSaleCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(retailSaleCommand.ModelObject);
        long retailSaleId = retailSaleCommand.ModelObject.Id;
        RetailSale retailSale = await Db.Sales
            .AsNoTracking()
            .OfType<RetailSale>()
            .SingleAsync(stored => stored.Id == retailSaleId);

        Assert.Equal(price, retailSale.SalePrice);
        Assert.Equal(timestamp, retailSale.DateSold);
    }

    /// <summary>
    /// Creating the tender item 3141 "Tender Item" ("This is an item that can be tendered on"), reserve 99.01, closing
    /// on the server date, with no photo, in the fixture region and category and sold by the fixture agent, stores the
    /// code number, closure date, price, name, description, agent, company, region and category.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateTenderItem()
    {
        const int codeNumber = 3141;
        const string name = "Tender Item";
        const string description = "This is an item that can be tendered on";
        const decimal price = 99.01m;
        DateOnly closureDate = await GetService<IServerClock>().GetServerDateAsync();

        var tenderItemCommand = new TenderItemCommand
        {
            CodeNumber = codeNumber,
            Name = name,
            Description = description,
            ClosureDate = closureDate,
            Photo = null,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = price,
        };

        OperationResult result = await GetService<ItemService>()
            .ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(tenderItemCommand.ModelObject);
        long tenderItemId = tenderItemCommand.ModelObject.Id;
        TenderItem tenderItem = await Db.Items
            .AsNoTracking()
            .OfType<TenderItem>()
            .SingleAsync(stored => stored.Id == tenderItemId);

        Assert.Equal(codeNumber, tenderItem.CodeNumber);
        Assert.Equal(closureDate, tenderItem.ClosureDate);
        Assert.Equal(price, tenderItem.Price);
        Assert.Equal(name, tenderItem.Name);
        Assert.Equal(description, tenderItem.Description);
        Assert.Equal(MyAgent.Id, tenderItem.AgentId);
        Assert.Equal(MyCompany.Id, tenderItem.CompanyId);
        Assert.Equal(MyRegion.Id, tenderItem.RegionId);
        Assert.Equal(MyItemCategory.Id, tenderItem.ItemCategoryId);
    }

    /// <summary>
    /// Creating a tender sale of the fixture tender item to the fixture client through the fixture tender stores the
    /// tender, an agent commission of 0.00, the client, the company and the item.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateTenderSale()
    {
        var tenderSaleCommand = new TenderSaleCommand
        {
            ItemId = MyTenderItem.Id,
            TenderId = MyTender.Id,
            ClientId = MyClient.Id,
        };

        OperationResult result = await GetService<TenderSaleService>()
            .ExecuteAsync(tenderSaleCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(tenderSaleCommand.ModelObject);
        long tenderSaleId = tenderSaleCommand.ModelObject.Id;
        TenderSale tenderSale = await Db.Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .SingleAsync(stored => stored.Id == tenderSaleId);

        Assert.Equal(MyTender.Id, tenderSale.TenderId);
        Assert.Equal(0.00m, tenderSale.AgentCommission);
        Assert.Equal(MyClient.Id, tenderSale.ClientId);
        Assert.Equal(MyCompany.Id, tenderSale.CompanyId);
        Assert.Equal(MyTenderItem.Id, tenderSale.ItemId);
    }

    /// <summary>
    /// After a retail sale of the fixture retail item and a tender sale of the fixture tender item, the company holds
    /// exactly those 2 sales.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllSales()
    {
        RetailSale retailSale = await Builder.CreateRetailSaleAsync(MyRetailItem, MyClient);
        TenderSale tenderSale = await Builder.CreateTenderSaleAsync(MyTenderItem, MyTender, MyClient);

        Assert.NotNull(retailSale);
        Assert.NotNull(tenderSale);

        long[] companySaleIds = await Db.Sales
            .AsNoTracking()
            .Where(sale => sale.CompanyId == MyCompany.Id)
            .Select(sale => sale.Id)
            .ToArrayAsync();

        Assert.Equal(2, companySaleIds.Length);
        Assert.Contains(retailSale.Id, companySaleIds);
        Assert.Contains(tenderSale.Id, companySaleIds);
    }

    /// <summary>The company's debug string is its object identity followed by " Company".</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        Company company = await ReadMyCompanyAsync();

        Assert.Equal(ObjectIdentity.Of(company) + " Company", company.GetDebugString());
    }

    /// <summary>
    /// With an extra retail item created, the next code number of the company is that item's number plus 1, and a
    /// retail item "Retail Item 2" ("Another Retail item for sale"), priced 5.50 with no photo and no code number, is
    /// created with that number.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetNextCode()
    {
        RetailItem existingRetailItem = await Builder.CreateRetailItemAsync(MyRegion, MyItemCategory, MyAgent);
        int code = existingRetailItem.CodeNumber;

        int newCode = await GetService<ItemCodeAllocator>().AllocateNextCodeNumberAsync(MyCompany.Id);

        var retailItemCommand = new RetailItemCommand
        {
            ItemCategoryId = MyItemCategory.Id,
            Name = "Retail Item 2",
            Description = "Another Retail item for sale",
            Photo = null,
            RegionId = MyRegion.Id,
            AgentId = MyAgent.Id,
            Price = 5.50m,
        };

        OperationResult result = await GetService<ItemService>()
            .ExecuteAsync(retailItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(retailItemCommand.ModelObject);
        long retailItemId = retailItemCommand.ModelObject.Id;
        RetailItem retailItem = await Db.Items
            .AsNoTracking()
            .OfType<RetailItem>()
            .SingleAsync(stored => stored.Id == retailItemId);

        Assert.Equal(code + 1, newCode);
        Assert.Equal(code + 1, retailItem.CodeNumber);
    }

    /// <summary>The company, with one country holding one region, has 2 locations.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetNumOfLocations()
    {
        int count = await GetService<CompanyService>().GetNumberOfLocations(MyCompany.Id);

        Assert.Equal(2, count);
    }

    /// <summary>
    /// <c>getRegion("France", "Paris")</c> returns the fixture region, and
    /// <c>getRegion("Not a real place", "Shouldn't be found")</c> returns <see langword="null"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetRegion()
    {
        CompanyService companyService = GetService<CompanyService>();

        Region? region1 = await companyService.GetRegion(MyCompany.Id, "France", "Paris");
        Region? region2 = await companyService.GetRegion(MyCompany.Id, "Not a real place", "Shouldn't be found");

        Assert.NotNull(region1);
        Assert.Equal(MyRegion.Id, region1.Id);
        Assert.Null(region2);
    }

    /// <summary>The fixture company is named "Jade Software Corporation".</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInitialProperties()
    {
        Company company = await ReadMyCompanyAsync();

        Assert.Equal("Jade Software Corporation", company.Name);
    }

    /// <summary>Returns the fixture company as stored, read without change tracking.</summary>
    private async Task<Company> ReadMyCompanyAsync()
    {
        long companyId = MyCompany.Id;
        return await Db.Companies.AsNoTracking().SingleAsync(stored => stored.Id == companyId);
    }

    /// <summary>Returns the exception for a fixture read before <see cref="SetUpAsync"/> created it.</summary>
    private static InvalidOperationException FixtureMissing(string fixture) =>
        new($"The fixture {fixture} is created by {nameof(SetUpAsync)}.");
}
