using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE <c>TestClient</c> unit tests: a tender placed by a client, the retail and tender sales of the
/// company's items to a client, the client's debug string, and updates of the client's name and address in its own
/// transaction and outside any transaction. Each test starts with the company "Jade Software Corporation", bound to the
/// test's scope, and its client "Clive Entworth" at 555 Fake St., Dunedin, New Zealand.
/// </summary>
public sealed class TestClient : ErewhonTestBase
{
    private const string UpdatedName = "Jane Doe";
    private const string UpdatedStreet = "123 Another Pl";
    private const string UpdatedCity = "Auckland";
    private const string UpdatedCountry = "Aotearoa";
    private const string UpdatedEmail = "support@erewhon.nz";
    private const string UpdatedPhone = "555 7654321";
    private const string UpdatedFax = "FAX2";
    private const string UpdatedWebSite = "https://www.jadeworld.com/contact-us/";

    /// <summary>The client created by <see cref="SetUpAsync"/>.</summary>
    private Client MyClient =>
        Builder.MyClient ?? throw new InvalidOperationException("The test client is created by SetUpAsync.");

    /// <summary>The company created and bound by <see cref="SetUpAsync"/>.</summary>
    private Company MyCompany =>
        Builder.MyCompany ?? throw new InvalidOperationException("The test company is created by SetUpAsync.");

    /// <summary>The Erewhon database of the test's scope.</summary>
    private IErewhonDbContext Db => GetService<IErewhonDbContext>();

    /// <summary>
    /// A tender of 100.50 by the client on a tender item, placed at the server timestamp, persists with that offer,
    /// tender item, client and timestamp.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateTender()
    {
        // Arrange
        Agent agent = await Builder.CreateAgentAsync();
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync("Furniture", "FURN", "Household Furniture");
        TenderItem tenderItem = await Builder.CreateTenderItemAsync(region, itemCategory, agent);

        decimal offer = 100.50m;
        DateTime timeStamp = await GetService<IServerClock>().GetServerTimeStampAsync();

        // Act
        var tenderCommand = new TenderCommand
        {
            Offer = offer,
            DateTendered = timeStamp,
            ClientId = MyClient.Id,
            TenderItemId = tenderItem.Id,
        };

        OperationResult result = await GetService<TenderService>()
            .ExecuteAsync(tenderCommand, TransactionType.Persist, TransactionMode.Own);

        Tender? tender = tenderCommand.ModelObject;

        // Assert
        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.NotNull(tender);

        Tender? storedTender = await Db.Tenders
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == tender.Id);

        Assert.NotNull(storedTender);
        Assert.Equal(offer, storedTender.Offer);
        Assert.Equal(tenderItem.Id, storedTender.TenderItemId);
        Assert.Equal(MyClient.Id, storedTender.ClientId);
        Assert.Equal(timeStamp, storedTender.DateTendered);
    }

    /// <summary>
    /// After the client buys the retail item 1000 through a retail sale and is awarded the tender item 2000 through a
    /// tender sale, the company holds 2 items in code-number order: the first sold by a <see cref="RetailSale"/> of the
    /// retail item and the last by a <see cref="TenderSale"/> of the tender item.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllSales()
    {
        // Arrange
        IServerClock clock = GetService<IServerClock>();
        DateTime timestamp = await clock.GetServerTimeStampAsync();
        DateOnly date = await clock.GetServerDateAsync();

        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Test Category", "TEST", "This is a test item category");
        Agent agent = await Builder.CreateAgentAsync();

        ItemService itemService = GetService<ItemService>();

        string retailSaleDescription = "A Retail Sale Description";
        var retailItemCommand = new RetailItemCommand
        {
            CodeNumber = 1000,
            Name = "Test Retail Sale",
            Description = retailSaleDescription,
            Photo = null,
            RegionId = region.Id,
            ItemCategoryId = itemCategory.Id,
            AgentId = agent.Id,
            Price = 15.50m,
        };

        await itemService.ExecuteAsync(retailItemCommand, TransactionType.Persist, TransactionMode.Own);
        RetailItem retailItem = Assert.IsType<RetailItem>(retailItemCommand.ModelObject);
        await Builder.CreateRetailSaleAsync(retailItem, MyClient);

        string tenderSaleDescription = "A Tender Sale Description";
        var tenderItemCommand = new TenderItemCommand
        {
            CodeNumber = 2000,
            Name = "Test Tender Sale",
            Description = tenderSaleDescription,
            ClosureDate = date,
            Photo = null,
            RegionId = region.Id,
            ItemCategoryId = itemCategory.Id,
            AgentId = agent.Id,
            Price = 105.50m,
        };

        await itemService.ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);
        TenderItem tenderItem = Assert.IsType<TenderItem>(tenderItemCommand.ModelObject);

        var tenderCommand = new TenderCommand
        {
            Offer = tenderItemCommand.Price + 10,
            DateTendered = timestamp,
            ClientId = MyClient.Id,
            TenderItemId = tenderItem.Id,
        };

        await GetService<TenderService>().ExecuteAsync(tenderCommand, TransactionType.Persist, TransactionMode.Own);
        Tender tender = Assert.IsType<Tender>(tenderCommand.ModelObject);
        await Builder.CreateTenderSaleAsync(tenderItem, tender, MyClient);

        // Act
        long companyId = MyCompany.Id;
        List<Item> items = await Db.Items
            .AsNoTrackingWithIdentityResolution()
            .Where(item => item.CompanyId == companyId)
            .Include(item => item.Sale)
            .OrderBy(item => item.CodeNumber)
            .ToListAsync();

        // Assert
        Assert.Equal(2, items.Count);

        Item firstSale = items[0];
        RetailSale retailSale = Assert.IsType<RetailSale>(firstSale.Sale);
        Assert.NotNull(retailSale.Item);
        Assert.Equal(retailSaleDescription, retailSale.Item.Description);

        Item lastSale = items[^1];
        TenderSale tenderSale = Assert.IsType<TenderSale>(lastSale.Sale);
        Assert.NotNull(tenderSale.Item);
        Assert.Equal(tenderSaleDescription, tenderSale.Item.Description);
    }

    /// <summary>The client's debug string is its object identity, a space and the <c>$Client</c> text.</summary>
    [Fact]
    public void testGetDebugString()
    {
        Assert.Equal(
            ObjectIdentity.Of(MyClient) + " " + MessageCatalog.Get(MessageKeys.Client),
            MyClient.GetDebugString());
    }

    /// <summary>
    /// An update of the client to the name "Jane Doe" and a new street, city, country, e-mail, phone, fax and web site,
    /// persisted in the agent's own transaction, stores all eight values.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        // Arrange
        ClientCommand clientCommand = CreateUpdatedClientCommand();

        // Act
        OperationResult result = await GetService<ClientService>()
            .ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Own);

        // Assert
        Assert.True(result.Succeeded, result.FullErrorText);

        Client storedClient = await ReadMyClientAsync();
        AssertClient(
            UpdatedName,
            new AddressCommand
            {
                Street = UpdatedStreet,
                City = UpdatedCity,
                Country = UpdatedCountry,
                Phone = UpdatedPhone,
                Fax = UpdatedFax,
                Email = UpdatedEmail,
                WebSite = UpdatedWebSite,
            },
            storedClient);
    }

    /// <summary>
    /// The same update persisted in the caller's transaction with no transaction active raises error 1048 and leaves
    /// the stored client with its original name and address.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateInTransStateException()
    {
        // Arrange
        const int UpdateOutsideTransaction = 1048;
        ClientCommand clientCommand = CreateUpdatedClientCommand();

        // Act
        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => GetService<ClientService>()
                .ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Ambient));

        // Assert
        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);

        Client storedClient = await ReadMyClientAsync();
        AssertClient("Clive Entworth", TestCompanyBuilder.FakeStreetAddress(), storedClient);
    }

    /// <summary>Creates the client of every test. Ports <c>setUp</c>: <c>self.myClient := self.createClient()</c>.</summary>
    /// <returns>A task that completes when the company and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        Builder.MyClient = await Builder.CreateClientAsync();
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> holds <paramref name="expectedName"/> and the street, city, country,
    /// e-mail, fax, phone and web site of <paramref name="expectedAddress"/>.
    /// </summary>
    private static void AssertClient(string expectedName, AddressCommand expectedAddress, Client actual)
    {
        Assert.Equal(expectedName, actual.Name);
        Assert.Equal(expectedAddress.Street, actual.Address.Street);
        Assert.Equal(expectedAddress.City, actual.Address.City);
        Assert.Equal(expectedAddress.Country, actual.Address.Country);
        Assert.Equal(expectedAddress.Email, actual.Address.Email);
        Assert.Equal(expectedAddress.Fax, actual.Address.Fax);
        Assert.Equal(expectedAddress.Phone, actual.Address.Phone);
        Assert.Equal(expectedAddress.WebSite, actual.Address.WebSite);
    }

    /// <summary>
    /// Returns a command populated from the client, with the name "Jane Doe" and the address 123 Another Pl, Auckland,
    /// Aotearoa, e-mail "support@erewhon.nz", phone "555 7654321", fax "FAX2" and web site
    /// "https://www.jadeworld.com/contact-us/".
    /// </summary>
    private ClientCommand CreateUpdatedClientCommand()
    {
        var clientCommand = new ClientCommand();
        clientCommand.PopulateFromObject(MyClient);
        Assert.NotNull(clientCommand.Address);

        clientCommand.Name = UpdatedName;
        clientCommand.Address.Street = UpdatedStreet;
        clientCommand.Address.City = UpdatedCity;
        clientCommand.Address.Country = UpdatedCountry;
        clientCommand.Address.Email = UpdatedEmail;
        clientCommand.Address.Phone = UpdatedPhone;
        clientCommand.Address.Fax = UpdatedFax;
        clientCommand.Address.WebSite = UpdatedWebSite;

        return clientCommand;
    }

    /// <summary>Reads the stored row of the client, with its address, from the database.</summary>
    private Task<Client> ReadMyClientAsync()
    {
        long clientId = MyClient.Id;
        return Db.Clients.AsNoTracking().SingleAsync(client => client.Id == clientId);
    }
}
