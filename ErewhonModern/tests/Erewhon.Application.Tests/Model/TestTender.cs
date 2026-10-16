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
/// Tests of tender creation, validation and update through <see cref="TenderService"/>, and of the <see cref="Tender"/>
/// accessors, on the API of the test assembly. Each test starts with the company "Jade Software Corporation", the
/// client "Clive Entworth", the agent "John Smith", the item category "Drinks" ("DRNK"), the region "Paris" of
/// "France", the tender item "Proxy Tender" of that category, region and agent with the reserve price 5.50 closing on
/// the server date, and a tender of 5.50 by the client on that item placed at the server timestamp.
/// Ports the JADE class <c>TestTender</c>.
/// </summary>
public sealed class TestTender : ErewhonTestBase
{
    private DateTime? _timestamp;

    /// <summary>The server date less one day, at 00:00:00.000. Ports <c>TestTender.timestamp</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private DateTime Timestamp => _timestamp ?? throw NotSetUp(nameof(Timestamp));

    /// <summary>The fixture client "Clive Entworth". Ports <c>myClient</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private Client MyClient => Builder.MyClient ?? throw NotSetUp(nameof(MyClient));

    /// <summary>The fixture agent "John Smith". Ports <c>myAgent</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private Agent MyAgent => Builder.MyAgent ?? throw NotSetUp(nameof(MyAgent));

    /// <summary>The fixture item category "Drinks". Ports <c>myItemCategory</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private ItemCategory MyItemCategory => Builder.MyItemCategory ?? throw NotSetUp(nameof(MyItemCategory));

    /// <summary>The fixture tender item, reserve price 5.50. Ports <c>myTenderItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private TenderItem MyTenderItem => Builder.MyTenderItem ?? throw NotSetUp(nameof(MyTenderItem));

    /// <summary>The fixture tender of 5.50 on <see cref="MyTenderItem"/>. Ports <c>myTender</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    private Tender MyTender => Builder.MyTender ?? throw NotSetUp(nameof(MyTender));

    /// <summary>
    /// Creates a tender with the offer, time tendered, client and tender item of the fixture tender: it is not
    /// persisted, the item still holds one tender, and the only error is <see cref="MessageKeys.TenderAlreadyPlaced"/>.
    /// Ports <c>TestTender.testDuplicateTender</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testDuplicateTender()
    {
        Assert.NotNull(Builder.MyTender);
        Assert.Equal(1, await CountTendersOnAsync(MyTenderItem.Id));

        decimal offer = MyTender.Offer;
        DateTime timeStamp = MyTender.DateTendered;
        long client = MyTender.ClientId;
        long tenderItem = MyTender.TenderItemId;

        var tenderCommand = new TenderCommand
        {
            Offer = offer,
            DateTendered = timeStamp,
            ClientId = client,
            TenderItemId = tenderItem,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await CountTendersOnAsync(MyTenderItem.Id));
        AssertSingleError(result, MessageKeys.TenderAlreadyPlaced);
    }

    /// <summary>
    /// The date of the fixture tender is the server date. Ports <c>TestTender.testGetDate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDate()
    {
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        Assert.Equal(date, MyTender.GetDate());
    }

    /// <summary>
    /// The debug string of the fixture tender is its object identity, " Tender: $", its offer to 2 decimal places, its
    /// time tendered as <c>dd MMMM yyyy, HH:mm:ss</c> and its client's name, separated by ", ".
    /// Ports <c>TestTender.testGetDebugString</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        Tender? tender = await GetService<TenderService>().FindByIdAsync(MyTender.Id);
        Assert.NotNull(tender);
        Assert.NotNull(tender.Client);

        decimal offer = tender.Offer;
        string clientName = tender.Client.Name;
        DateTime timeStamp = tender.DateTendered;
        string oid = ObjectIdentity.Of(tender);
        string expected = oid + " Tender: $" + JadeFormat.DecimalString(offer) + ", "
            + JadeFormat.TimeStampString(timeStamp) + ", " + clientName;

        string actual = tender.GetDebugString();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The time of the fixture tender is the time-of-day part of its time tendered. Ports <c>TestTender.testGetTime</c>.
    /// </summary>
    [Fact]
    public void testGetTime()
    {
        Assert.Equal(TimeOnly.FromDateTime(MyTender.DateTendered), MyTender.GetTime());
    }

    /// <summary>
    /// Creates a tender of the reserve price plus 10 with no client: no tender is created and the only error is
    /// <see cref="MessageKeys.ClientRequired"/>. Ports <c>TestTender.testInvalidClient</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidClient()
    {
        var tenderCommand = new TenderCommand
        {
            Offer = MyTenderItem.Price + 10,
            DateTendered = Timestamp,
            ClientId = null,
            TenderItemId = MyTenderItem.Id,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.Null(tenderCommand.ModelObject);
        AssertSingleError(result, MessageKeys.ClientRequired);
    }

    /// <summary>
    /// Creates a tender with an offer of 0: no tender is created and the only error is
    /// <see cref="MessageKeys.TenderOfferLessThanZero"/>. Ports <c>TestTender.testInvalidOffer</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidOffer()
    {
        var tenderCommand = new TenderCommand
        {
            Offer = 0m, // the JADE null offer
            DateTendered = Timestamp,
            ClientId = MyClient.Id,
            TenderItemId = MyTenderItem.Id,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.Null(tenderCommand.ModelObject);
        AssertSingleError(result, MessageKeys.TenderOfferLessThanZero);
    }

    /// <summary>
    /// Creates the country "Countroplois", its region "Regiontopia" and the tender item 2222 "Tender Item" of the
    /// fixture category and agent in that region, with no photo, the reserve price 55.50 and a closure date seven days
    /// after the server date; then creates a tender of that item's price plus 10 with no tender item: no tender is
    /// created and the only error is <see cref="MessageKeys.TenderItemRequired"/>.
    /// Ports <c>TestTender.testInvalidTenderItem</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidTenderItem()
    {
        byte[]? photo = null;
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        var countryCommand = new CountryCommand { Name = "Countroplois" };
        await GetService<CountryService>().ExecuteAsync(countryCommand, TransactionType.Persist, TransactionMode.Own);

        var regionCommand = new RegionCommand { Name = "Regiontopia", CountryId = countryCommand.ModelObject?.Id };
        await GetService<RegionService>().ExecuteAsync(regionCommand, TransactionType.Persist, TransactionMode.Own);

        var tenderItemCommand = new TenderItemCommand
        {
            CodeNumber = 2222,
            Name = "Tender Item",
            Description = "A full description of the Tender Item",
            Photo = photo,
            RegionId = regionCommand.ModelObject?.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = 55.50m,
            ClosureDate = date.AddDays(7),
        };
        await GetService<ItemService>().ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);

        TenderItem tenderItem = Assert.IsType<TenderItem>(tenderItemCommand.ModelObject);
        var tenderCommand = new TenderCommand
        {
            Offer = tenderItem.Price + 10,
            DateTendered = Timestamp,
            ClientId = MyClient.Id,
            TenderItemId = null,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.Null(tenderCommand.ModelObject);
        AssertSingleError(result, MessageKeys.TenderItemRequired);
    }

    /// <summary>
    /// Creates a tender of the reserve price plus 10 with no time tendered: the tender is created with no errors.
    /// Ports <c>TestTender.testInvalidTimestamp</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidTimestamp()
    {
        var tenderCommand = new TenderCommand
        {
            Offer = MyTenderItem.Price + 10,
            DateTendered = null,
            ClientId = MyClient.Id,
            TenderItemId = MyTenderItem.Id,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.NotNull(tenderCommand.ModelObject);
        Assert.Empty(result.Errors);
        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// Creates the client "Mr Fakington" at 123 Fake Street, Fakesville, Faketopia, then creates a tender by the
    /// fixture client of the reserve price less 2: no tender is created and the only error is
    /// <see cref="MessageKeys.TenderOfferLessThanReserve"/>. Ports <c>TestTender.testOfferTooLow</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testOfferTooLow()
    {
        var clientCommand = new ClientCommand
        {
            Name = "Mr Fakington",
            Address = new AddressCommand
            {
                Street = "123 Fake Street",
                City = "Fakesville",
                Country = "Faketopia",
                Phone = "555 5555555",
                Fax = "555 4444444",
                Email = "fake@fakedomain.com",
                WebSite = "www.fake.com",
            },
        };
        await GetService<ClientService>().ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Own);

        var tenderCommand = new TenderCommand
        {
            Offer = MyTenderItem.Price - 2,
            DateTendered = Timestamp,
            ClientId = MyClient.Id,
            TenderItemId = MyTenderItem.Id,
        };

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.Null(tenderCommand.ModelObject);
        AssertSingleError(result, MessageKeys.TenderOfferLessThanReserve);
    }

    /// <summary>
    /// Updates the fixture tender's offer to its offer plus 10: the update succeeds with no errors and the stored offer
    /// is the new one. Ports <c>TestTender.testUpdate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        decimal offer = MyTender.Offer + 10;

        var tenderCommand = new TenderCommand();
        tenderCommand.PopulateFromObject(MyTender);
        tenderCommand.Offer = offer;

        OperationResult result = await PersistEntityAsync(tenderCommand);

        Assert.True(result.Succeeded);
        Assert.NotNull(tenderCommand.ModelObject);
        Assert.Empty(result.Errors);
        Assert.Equal(offer, await ReadStoredOfferAsync(MyTender.Id));
    }

    /// <summary>
    /// Updates the fixture tender's offer to its offer plus 10 in <see cref="TransactionMode.Ambient"/> mode with no
    /// active transaction: <see cref="NotInTransactionStateException"/> with error code 1048 is raised and the stored
    /// offer is unchanged. Ports <c>TestTender.testUpdateOutsideTransStateException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        const int UpdateOutsideTransaction = 1048;
        decimal storedOffer = MyTender.Offer;
        decimal offer = MyTender.Offer + 10;

        var tenderCommand = new TenderCommand();
        tenderCommand.PopulateFromObject(MyTender);
        tenderCommand.Offer = offer;

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistEntityInTransStateAsync(tenderCommand));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Assert.Equal(storedOffer, await ReadStoredOfferAsync(MyTender.Id));
    }

    /// <summary>
    /// Builds the fixtures in the JADE <c>setUp</c> order: sets <see cref="Timestamp"/> to the server date less one day
    /// at 00:00:00.000; creates and binds the company; then creates the client, the agent, the item category "Drinks"
    /// ("DRNK", "Refreshing Liquid Beverages"), the country "France" with its region "Paris", the tender item in that
    /// region, category and agent, and a tender of 5.50 by the client on it, setting the matching
    /// <see cref="TestCompanyBuilder"/> fixtures. Ports <c>TestTender.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        DateOnly date = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(-1);
        _timestamp = date.ToDateTime(TimeOnly.MinValue);

        await base.SetUpAsync();

        Client client = await Builder.CreateClientAsync();
        Builder.MyClient = client;

        Agent agent = await Builder.CreateAgentAsync();
        Builder.MyAgent = agent;

        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Drinks", "DRNK", "Refreshing Liquid Beverages");
        Builder.MyItemCategory = itemCategory;

        Region region = await Builder.CreateRegionAsync(await Builder.CreateCountryAsync());

        TenderItem tenderItem = await Builder.CreateTenderItemAsync(region, itemCategory, agent);
        Builder.MyTenderItem = tenderItem;

        Builder.MyTender = await Builder.CreateTenderAsync(client, tenderItem, 5.50m);
    }

    /// <summary>
    /// Asserts that <paramref name="result"/> holds exactly one error, the text of <paramref name="messageKey"/>.
    /// </summary>
    private static void AssertSingleError(OperationResult result, string messageKey) =>
        Assert.Equal(MessageCatalog.Get(messageKey), Assert.Single(result.Errors));

    /// <summary>Returns the exception for <paramref name="member"/> read before <see cref="SetUpAsync"/> set it.</summary>
    private static InvalidOperationException NotSetUp(string member) =>
        new($"{member} is available once {nameof(SetUpAsync)} has set it.");

    /// <summary>
    /// Runs <paramref name="tenderCommand"/> through <see cref="TenderService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Own"/> mode. Ports
    /// <c>persistEntity(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityAsync(TenderCommand tenderCommand) =>
        GetService<TenderService>().ExecuteAsync(tenderCommand, TransactionType.Persist, TransactionMode.Own);

    /// <summary>
    /// Runs <paramref name="tenderCommand"/> through <see cref="TenderService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Ambient"/> mode. Ports
    /// <c>persistEntityInTransState(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityInTransStateAsync(TenderCommand tenderCommand) =>
        GetService<TenderService>().ExecuteAsync(tenderCommand, TransactionType.Persist, TransactionMode.Ambient);

    /// <summary>
    /// Returns the number of stored tenders on the tender item with row id <paramref name="tenderItemId"/>.
    /// Ports <c>myTenderItem.allTendersByOfferTime.size()</c>.
    /// </summary>
    private Task<int> CountTendersOnAsync(long tenderItemId) =>
        GetService<IErewhonDbContext>().Tenders
            .AsNoTracking()
            .CountAsync(tender => tender.TenderItemId == tenderItemId);

    /// <summary>Returns the stored offer of the tender with row id <paramref name="tenderId"/>.</summary>
    private Task<decimal> ReadStoredOfferAsync(long tenderId) =>
        GetService<IErewhonDbContext>().Tenders
            .AsNoTracking()
            .Where(tender => tender.Id == tenderId)
            .Select(tender => tender.Offer)
            .SingleAsync();
}
