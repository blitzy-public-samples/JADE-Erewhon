using System.Diagnostics;
using System.Globalization;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Search;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erewhon.Application.Tests.Catalogue;

/// <summary>
/// The behaviour-catalogue entries that no JADE unit test pins, one test per entry named after its catalogue ID, each
/// run through the production services on the PostgreSQL test database against the fixture company of
/// <see cref="ErewhonTestBase"/>.
/// </summary>
public sealed class BehaviourCatalogueTests : ErewhonTestBase
{
    /// <summary>The name of the retail items of <see cref="RetailItemCommandFor"/> and <c>NewRetailItemAsync</c>.</summary>
    private const string CatalogueRetailName = "Catalogue Retail";

    /// <summary>The description of the retail items of <see cref="RetailItemCommandFor"/> and <c>NewRetailItemAsync</c>.</summary>
    private const string CatalogueRetailDescription = "A retail item of the catalogue tests";

    /// <summary>The name of the tender items of <see cref="TenderItemCommandFor"/> and <c>NewTenderItemAsync</c>.</summary>
    private const string CatalogueTenderName = "Catalogue Tender";

    /// <summary>The description of the tender items of <see cref="TenderItemCommandFor"/> and <c>NewTenderItemAsync</c>.</summary>
    private const string CatalogueTenderDescription = "A tender item of the catalogue tests";

    private static readonly TimeSpan PollLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// B-06 cases: the case name, the photo bytes and whether the photo is a valid picture. Every signature is padded
    /// with zeros to at least 16 bytes, except the JPEG cut after its first 4 bytes and the missing photo.
    /// </summary>
    public static TheoryData<string, byte[]?, bool> PictureCases => new()
    {
        { "BMP", Padded(0x42, 0x4D), true },
        { "ICO", Padded(0x00, 0x00, 0x01, 0x00), true },
        { "CUR", Padded(0x00, 0x00, 0x02, 0x00), true },
        { "WMF", Padded(0xD7, 0xCD, 0xC6, 0x9A), true },
        { "EMF", EnhancedMetafile(), true },
        { "TIFF II", Padded(0x49, 0x49, 0x2A, 0x00), true },
        { "TIFF MM", Padded(0x4D, 0x4D, 0x00, 0x2A), true },
        { "JPEG", Padded(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00), true },
        { "PNG", Padded(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), true },
        { "JPEG cut after 4 bytes", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true },
        { "GIF", Padded(0x47, 0x49, 0x46, 0x38, 0x39, 0x61), false },
        { "No signature", Padded(0x13, 0x37, 0xC0, 0xDE, 0x5A, 0xA5, 0x0F, 0xF0, 0x3C, 0xC3), false },
        { "No photo", null, true },
    };

    /// <summary>
    /// B-24 cases: whether the line is a tender line, and an offer whose value rounded to 2 decimal places has a
    /// magnitude greater than <see cref="DecimalRules.MaxMoney"/>.
    /// </summary>
    public static TheoryData<bool, decimal> OfferOverflowCases => new()
    {
        { false, 99_999_999_999m },
        { false, 9_999_999_999.995m },
        { true, 99_999_999_999m },
        { true, 9_999_999_999.995m },
    };

    /// <summary>
    /// <see cref="DecimalRules.RoundMoney"/> cases within <c>Decimal[12,2]</c>: the value and the value it returns.
    /// </summary>
    public static TheoryData<decimal, decimal> RoundMoneyCases => new()
    {
        { 9_999_999_999.99m, 9_999_999_999.99m },
        { -9_999_999_999.99m, -9_999_999_999.99m },
        { 9_999_999_999.994m, 9_999_999_999.99m },
        { -9_999_999_999.994m, -9_999_999_999.99m },
        { 175.555m, 175.56m },
        { -175.555m, -175.56m },
        { 0m, 0m },
    };

    /// <summary>
    /// <see cref="DecimalRules.RoundMoney"/> cases beyond <c>Decimal[12,2]</c>: values whose magnitude, rounded to 2
    /// decimal places, is greater than <see cref="DecimalRules.MaxMoney"/>.
    /// </summary>
    public static TheoryData<decimal> RoundMoneyOverflowCases => new()
    {
        9_999_999_999.995m,
        -9_999_999_999.995m,
        10_000_000_000m,
        -10_000_000_000m,
        99_999_999_999m,
        decimal.MaxValue,
        decimal.MinValue,
    };

    private long CompanyId => Builder.MyCompany?.Id
        ?? throw new InvalidOperationException("The fixture company has not been created.");

    private string SessionId => Session.SessionId
        ?? throw new InvalidOperationException("The test session has no session id.");

    private ShoppingCartService Cart => GetService<ShoppingCartService>();

    // ---------------------------------------------------------------------------------------------------------------
    // Shared arrangement and verification helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Returns the bytes given followed by zeros up to 16 bytes.</summary>
    private static byte[] Padded(params byte[] signature)
    {
        var bytes = new byte[Math.Max(16, signature.Length)];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>Returns an EMF header: record type <c>01 00 00 00</c>, zeros up to offset 40, then " EMF".</summary>
    private static byte[] EnhancedMetafile()
    {
        var bytes = new byte[48];
        new byte[] { 0x01, 0x00, 0x00, 0x00 }.CopyTo(bytes, 0);
        new byte[] { 0x20, 0x45, 0x4D, 0x46 }.CopyTo(bytes, 40);
        return bytes;
    }

    /// <summary>Returns the locale-5129 text of <paramref name="key"/> with <paramref name="args"/>.</summary>
    private static string Text(string key, params object?[] args) => MessageCatalog.Get(key, args);

    /// <summary>Asserts that <paramref name="result"/> succeeded and returns the row id it reports.</summary>
    private static long Succeeded(OperationResult result)
    {
        Assert.True(result.Succeeded, result.FullErrorText);
        return Assert.IsType<long>(result.Id);
    }

    /// <summary>Runs <paramref name="call"/>, asserts that it throws nothing and returns its result.</summary>
    private static async Task<OperationResult> WithoutExceptionAsync(Func<Task<OperationResult>> call)
    {
        OperationResult? result = null;
        Exception? failure = await Record.ExceptionAsync(async () => result = await call());
        Assert.Null(failure);
        return Assert.IsType<OperationResult>(result);
    }

    /// <summary>Returns the labels of <paramref name="ids"/>, in order, joined by spaces.</summary>
    private static string Labels(IEnumerable<long> ids, IReadOnlyDictionary<long, string> labels) =>
        string.Join(" ", ids.Select(id => labels.TryGetValue(id, out string? label) ? label : "#" + id));

    /// <summary>Opens a new scope of the API with the fixture company bound.</summary>
    private AsyncServiceScope NewBoundScope()
    {
        AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(CompanyId);
        return scope;
    }

    /// <summary>Runs <paramref name="action"/> in a new scope with the fixture company bound.</summary>
    private async Task<T> InNewScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using AsyncServiceScope scope = NewBoundScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="read"/> against the database context of a new scope.</summary>
    private async Task<T> ReadAsync<T>(Func<IErewhonDbContext, Task<T>> read)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<IErewhonDbContext>());
    }

    private Task<DateOnly> ServerDateAsync() => GetService<IServerClock>().GetServerDateAsync();

    private Task<DateTime> ServerTimeStampAsync() => GetService<IServerClock>().GetServerTimeStampAsync();

    /// <summary>Reads the server timestamp until it is later than <paramref name="instant"/>, for at most five seconds.</summary>
    private async Task<DateTime> WaitForServerTimeAfterAsync(DateTime instant)
    {
        var stopwatch = Stopwatch.StartNew();
        DateTime now = await ServerTimeStampAsync();
        while (now <= instant)
        {
            Assert.True(stopwatch.Elapsed < PollLimit, $"The server timestamp did not pass {instant:O} within {PollLimit}.");
            await Task.Delay(PollInterval);
            now = await ServerTimeStampAsync();
        }

        return now;
    }

    /// <summary>
    /// Counts the advisory locks of the test database that sessions are waiting for, on a connection of its own, until
    /// <paramref name="expected"/> are waiting or five seconds have passed.
    /// </summary>
    /// <returns><see langword="true"/> when <paramref name="expected"/> waiters were seen.</returns>
    private async Task<bool> WaitForAdvisoryLockWaitersAsync(long expected)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted "
            + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())",
            connection);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollLimit)
        {
            if (await command.ExecuteScalarAsync() is long waiting && waiting >= expected)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    /// <summary>
    /// Creates, through <see cref="Builder"/>, the country "France", its region "Paris", the item category "Lamps"
    /// ("LAMP"), the agent "John Smith" and the client "Clive Entworth".
    /// </summary>
    private async Task<CatalogueFixture> ArrangeCatalogueAsync()
    {
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory category = await Builder.CreateItemCategoryAsync("Lamps", "LAMP", "Lamps and lighting");
        Agent agent = await Builder.CreateAgentAsync();
        Client client = await Builder.CreateClientAsync();
        return new CatalogueFixture(country, region, category, agent, client);
    }

    /// <summary>
    /// Returns, through <see cref="TestCompanyBuilder.CreateTestAddress"/>, a new address with the seven field values of
    /// <paramref name="fields"/>, a field with no value given as the empty string.
    /// </summary>
    private Address AddressFrom(AddressCommand fields) =>
        Builder.CreateTestAddress(
            fields.Street ?? string.Empty,
            fields.City ?? string.Empty,
            fields.Country ?? string.Empty,
            fields.Phone ?? string.Empty,
            fields.Fax ?? string.Empty,
            fields.Email ?? string.Empty,
            fields.WebSite ?? string.Empty);

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateTestAgentAsync"/> of <paramref name="builder"/>, an agent
    /// named <paramref name="name"/> in the company bound to the builder's scope, with the field values of
    /// <see cref="TestCompanyBuilder.NohwereLaneAddress"/> as its address.
    /// </summary>
    private Task<Agent> NewAgentAsync(TestCompanyBuilder builder, string name) =>
        builder.CreateTestAgentAsync(name, AddressFrom(TestCompanyBuilder.NohwereLaneAddress()));

    /// <summary>
    /// Creates, through <see cref="Builder"/>, an agent named <paramref name="name"/> in the fixture company, with the
    /// field values of <see cref="TestCompanyBuilder.NohwereLaneAddress"/> as its address.
    /// </summary>
    private Task<Agent> NewAgentAsync(string name) => NewAgentAsync(Builder, name);

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateTestClientAsync"/> of <see cref="Builder"/>, a client named
    /// <paramref name="name"/> in the fixture company, with the field values of
    /// <see cref="TestCompanyBuilder.FakeStreetAddress"/> as its address.
    /// </summary>
    private Task<Client> NewClientAsync(string name) =>
        Builder.CreateTestClientAsync(name, AddressFrom(TestCompanyBuilder.FakeStreetAddress()));

    /// <summary>
    /// Returns a retail item command named <see cref="CatalogueRetailName"/> with code number 0, the agent, category and
    /// region of <paramref name="fixture"/> and <paramref name="price"/>.
    /// </summary>
    private static RetailItemCommand RetailItemCommandFor(CatalogueFixture fixture, decimal price) => new()
    {
        CodeNumber = 0,
        Name = CatalogueRetailName,
        Description = CatalogueRetailDescription,
        Price = price,
        AgentId = fixture.Agent.Id,
        ItemCategoryId = fixture.Category.Id,
        RegionId = fixture.Region.Id,
    };

    /// <summary>
    /// Returns a tender item command named <see cref="CatalogueTenderName"/> with code number 0, the agent, category and
    /// region of <paramref name="fixture"/>, <paramref name="reserve"/> and <paramref name="closureDate"/>.
    /// </summary>
    private static TenderItemCommand TenderItemCommandFor(CatalogueFixture fixture, decimal reserve, DateOnly closureDate) => new()
    {
        CodeNumber = 0,
        Name = CatalogueTenderName,
        Description = CatalogueTenderDescription,
        Price = reserve,
        ClosureDate = closureDate,
        AgentId = fixture.Agent.Id,
        ItemCategoryId = fixture.Category.Id,
        RegionId = fixture.Region.Id,
    };

    /// <summary>Persists <paramref name="command"/> through <see cref="ItemService"/>, asserts success and returns the id.</summary>
    private async Task<long> PersistItemAsync(ItemCommand command) =>
        Succeeded(await GetService<ItemService>().ExecuteAsync(command, TransactionType.Persist));

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateTestRetailItemAsync"/> of <see cref="Builder"/>, the retail
    /// item <see cref="CatalogueRetailName"/> with no photo, the next code number of the fixture company,
    /// <paramref name="agent"/>, <paramref name="category"/>, <paramref name="region"/> and <paramref name="price"/>.
    /// </summary>
    private Task<RetailItem> NewRetailItemAsync(Agent agent, ItemCategory category, Region region, decimal price) =>
        Builder.CreateTestRetailItemAsync(
            CatalogueRetailName, CatalogueRetailDescription, null, region, category, agent, price);

    /// <summary>
    /// Creates, through <see cref="Builder"/>, the retail item <see cref="CatalogueRetailName"/> with the agent,
    /// category and region of <paramref name="fixture"/> and <paramref name="price"/>.
    /// </summary>
    private Task<RetailItem> NewRetailItemAsync(CatalogueFixture fixture, decimal price) =>
        NewRetailItemAsync(fixture.Agent, fixture.Category, fixture.Region, price);

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateTestTenderItemAsync"/> of <see cref="Builder"/>, the tender
    /// item <see cref="CatalogueTenderName"/> with no photo, the next code number of the fixture company,
    /// <paramref name="agent"/>, <paramref name="category"/>, <paramref name="region"/>, <paramref name="reserve"/> and
    /// <paramref name="closureDate"/>.
    /// </summary>
    private Task<TenderItem> NewTenderItemAsync(
        Agent agent,
        ItemCategory category,
        Region region,
        decimal reserve,
        DateOnly closureDate) =>
        Builder.CreateTestTenderItemAsync(
            CatalogueTenderName, CatalogueTenderDescription, null, region, category, agent, reserve, closureDate);

    /// <summary>
    /// Creates, through <see cref="Builder"/>, the tender item <see cref="CatalogueTenderName"/> with the agent, category
    /// and region of <paramref name="fixture"/>, <paramref name="reserve"/> and <paramref name="closureDate"/>.
    /// </summary>
    private Task<TenderItem> NewTenderItemAsync(CatalogueFixture fixture, decimal reserve, DateOnly closureDate) =>
        NewTenderItemAsync(fixture.Agent, fixture.Category, fixture.Region, reserve, closureDate);

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateTestRetailSaleAsync"/> of <see cref="Builder"/>, a retail
    /// sale of <paramref name="item"/> to <paramref name="client"/> at the item's price, sold at the server timestamp.
    /// </summary>
    private async Task<RetailSale> NewRetailSaleAsync(RetailItem item, Client client) =>
        await Builder.CreateTestRetailSaleAsync(item, client, await ServerTimeStampAsync());

    /// <summary>
    /// Creates, through <see cref="TestCompanyBuilder.CreateCompanyAsync"/> of a builder over a new scope of the API, the
    /// company "Jade Software Corporation", unbound, and returns it.
    /// </summary>
    private async Task<Company> NewCompanyAsync()
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await new TestCompanyBuilder(scope.ServiceProvider).CreateCompanyAsync();
    }

    /// <summary>Runs a retail sale create of the item to the client through <see cref="RetailSaleService"/>.</summary>
    private Task<OperationResult> SellAsync(RetailItem item, Client client, decimal salePrice, decimal agentCommission = 0m) =>
        GetService<RetailSaleService>().ExecuteAsync(
            new RetailSaleCommand
            {
                ItemId = item.Id,
                ClientId = client.Id,
                SalePrice = salePrice,
                AgentCommission = agentCommission,
            },
            TransactionType.Persist);

    /// <summary>Runs a tender create by the client on the tender item through <see cref="TenderService"/>.</summary>
    private Task<OperationResult> TenderAsync(Client client, TenderItem tenderItem, decimal offer, DateTime? dateTendered) =>
        GetService<TenderService>().ExecuteAsync(
            new TenderCommand
            {
                ClientId = client.Id,
                TenderItemId = tenderItem.Id,
                Offer = offer,
                DateTendered = dateTendered,
            },
            TransactionType.Persist);

    /// <summary>
    /// Reads the item with <paramref name="itemId"/> in a new scope, applies <paramref name="change"/> to a command
    /// populated from it and persists the update; asserts that the update succeeded.
    /// </summary>
    private Task<OperationResult> UpdateItemAsync(long itemId, Action<ItemCommand> change) =>
        InNewScopeAsync(async services =>
        {
            ItemService items = services.GetRequiredService<ItemService>();
            Item item = await items.FindByIdAsync(itemId)
                ?? throw new InvalidOperationException($"Item {itemId} does not exist.");
            ItemCommand command = item is TenderItem ? new TenderItemCommand() : new RetailItemCommand();
            command.PopulateFromObject(item);
            change(command);
            OperationResult result = await items.ExecuteAsync(command, TransactionType.Persist);
            Assert.True(result.Succeeded, result.FullErrorText);
            return result;
        });

    /// <summary>Runs <see cref="TenderClosureService.CloseTendersAtDateAsync"/> in a new scope.</summary>
    private Task<int> CloseTendersAsync(DateOnly date) =>
        InNewScopeAsync(services => services.GetRequiredService<TenderClosureService>().CloseTendersAtDateAsync(date));

    /// <summary>Reads, in a new scope, the item with its category, region, agent and sale.</summary>
    private Task<Item> ReadItemAsync(long itemId) =>
        InNewScopeAsync(async services =>
            await services.GetRequiredService<ItemService>().FindByIdAsync(itemId)
            ?? throw new InvalidOperationException($"Item {itemId} does not exist."));

    /// <summary>Reads, untracked, the sales of the item.</summary>
    private Task<List<Sale>> SalesOfItemAsync(long itemId) =>
        ReadAsync(db => db.Sales.AsNoTracking().Where(sale => sale.ItemId == itemId).OrderBy(sale => sale.Id).ToListAsync());

    /// <summary>Reads, untracked, the tenders on the tender item in time order.</summary>
    private Task<List<Tender>> TendersOnItemAsync(long tenderItemId) =>
        ReadAsync(db => db.Tenders
            .AsNoTracking()
            .Where(tender => tender.TenderItemId == tenderItemId)
            .OrderBy(tender => tender.DateTendered)
            .ToListAsync());

    /// <summary>Reads, untracked, the ids of the items of the fixture company.</summary>
    private Task<List<long>> CompanyItemIdsAsync()
    {
        long companyId = CompanyId;
        return ReadAsync(db => db.Items.AsNoTracking().Where(item => item.CompanyId == companyId).Select(item => item.Id).ToListAsync());
    }

    /// <summary>Reads, untracked, the ids of the agents of the fixture company.</summary>
    private Task<List<long>> CompanyAgentIdsAsync()
    {
        long companyId = CompanyId;
        return ReadAsync(db => db.Agents.AsNoTracking().Where(agent => agent.CompanyId == companyId).Select(agent => agent.Id).ToListAsync());
    }

    /// <summary>Creates a company named <paramref name="name"/> at 5 Sir Gill Simpson Drive in a new scope and returns its id.</summary>
    private async Task<long> CreateExtraCompanyAsync(string name)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        var command = new CompanyCommand { Name = name, Address = TestCompanyBuilder.GillSimpsonDriveAddress() };
        return Succeeded(await scope.ServiceProvider.GetRequiredService<CompanyService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own));
    }

    /// <summary>Deletes the company with <paramref name="companyId"/> in a new scope and asserts that the delete succeeded.</summary>
    private async Task DeleteExtraCompanyAsync(long companyId)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        OperationResult result = await scope.ServiceProvider.GetRequiredService<CompanyService>()
            .DeleteByIdAsync(companyId, TransactionMode.Own);
        Assert.True(result.Succeeded, result.FullErrorText);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Item model and catalogue
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-04: a retail item create validated without pre-validation, with code number 0, no agent, category or region,
    /// an empty name and description, price 10.00 and no photo → the six required-field errors in the JADE order, the
    /// first one setting the focus field.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B04_ItemRequiredFieldsInOrder()
    {
        Assert.Equal("Item number must be provided", Text(MessageKeys.ItemNumberRequired));
        Assert.Equal("Agent is required", Text(MessageKeys.AgentRequired));
        Assert.Equal("Item Category is required", Text(MessageKeys.ItemCategoryRequired));
        Assert.Equal("Region is required", Text(MessageKeys.RegionRequired));
        Assert.Equal("A Name for the item is required", Text(MessageKeys.ItemNameRequired));
        Assert.Equal("A description of the item is required", Text(MessageKeys.ItemDescriptionRequired));

        var command = new RetailItemCommand
        {
            CodeNumber = 0,
            Name = string.Empty,
            Description = string.Empty,
            Price = 10.00m,
            Photo = null,
        };
        var result = new OperationResult();

        bool valid = await GetService<ItemService>().ValidateAsync(command, ValidationType.Create, result);

        Assert.False(valid);
        Assert.Equal(
            new[]
            {
                Text(MessageKeys.ItemNumberRequired),
                Text(MessageKeys.AgentRequired),
                Text(MessageKeys.ItemCategoryRequired),
                Text(MessageKeys.RegionRequired),
                Text(MessageKeys.ItemNameRequired),
                Text(MessageKeys.ItemDescriptionRequired),
            },
            result.Errors);
        Assert.Equal(FocusFields.CodeNumber, result.FocusField);
        Assert.Equal(0, command.CodeNumber);
    }

    /// <summary>
    /// B-05: a retail item created with no listed date, the name <c>"  Lamp "</c> and the description
    /// <c>" A lamp\t"</c> → stored with the server date as its listed date, the name "Lamp" and the description "A lamp".
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B05_ItemDefaultsAndTrims()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        RetailItemCommand command = RetailItemCommandFor(fixture, 25.00m);
        command.ListedDate = null;
        command.Name = "  Lamp ";
        command.Description = " A lamp\t";

        DateOnly before = await ServerDateAsync();
        long itemId = await PersistItemAsync(command);
        DateOnly after = await ServerDateAsync();

        Item item = await ReadItemAsync(itemId);
        Assert.InRange(item.ListedDate, before, after);
        Assert.Equal("Lamp", item.Name);
        Assert.Equal("A lamp", item.Description);
    }

    /// <summary>
    /// B-06 (Q-22, EE-16): a retail item created with each photo of <see cref="PictureCases"/> → created when the photo
    /// is a valid picture; otherwise the single error <c>$ImageNotValid</c> and nothing stored.
    /// <see cref="PictureValidator.IsValidPicture"/> gives the same classification.
    /// </summary>
    /// <param name="name">The case name, also part of the item name.</param>
    /// <param name="photo">The photo bytes, or <see langword="null"/> for no photo.</param>
    /// <param name="expectedValid">Whether the photo is a valid picture.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(PictureCases))]
    public async Task B06_PictureValidity(string name, byte[]? photo, bool expectedValid)
    {
        Assert.Equal("The image provided is not a valid picture file", Text(MessageKeys.ImageNotValid));
        Assert.Equal(expectedValid, PictureValidator.IsValidPicture(photo));

        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        RetailItemCommand command = RetailItemCommandFor(fixture, 25.00m);
        command.Name = "Picture " + name;
        command.Photo = photo;

        OperationResult result = await GetService<ItemService>().ExecuteAsync(command, TransactionType.Persist);

        if (expectedValid)
        {
            long itemId = Succeeded(result);
            Assert.DoesNotContain(Text(MessageKeys.ImageNotValid), result.Errors);
            Assert.Equal(photo, (await ReadItemAsync(itemId)).Photo);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal(Text(MessageKeys.ImageNotValid), Assert.Single(result.Errors));
            Assert.Empty(await CompanyItemIdsAsync());
        }
    }

    /// <summary>
    /// B-07 (C-03, Q-02): item prices 0 and −1 → the single error <c>$ItemPriceNotValid</c> and nothing stored;
    /// price 0.01 → stored at 0.01.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B07_ItemPriceZeroIsRejected()
    {
        Assert.Equal("Item price must be greater than 0", Text(MessageKeys.ItemPriceNotValid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        ItemService items = GetService<ItemService>();

        foreach (decimal rejected in new[] { 0m, -1m })
        {
            OperationResult result = await items.ExecuteAsync(RetailItemCommandFor(fixture, rejected), TransactionType.Persist);

            Assert.False(result.Succeeded);
            Assert.Equal(Text(MessageKeys.ItemPriceNotValid), Assert.Single(result.Errors));
        }

        Assert.Empty(await CompanyItemIdsAsync());

        long itemId = await PersistItemAsync(RetailItemCommandFor(fixture, 0.01m));
        Assert.Equal(0.01m, (await ReadItemAsync(itemId)).Price);
    }

    /// <summary>
    /// B-08: a tender item create with no closure date → the single error <c>$TenderItemClosureDateNotValid</c> and
    /// nothing stored.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B08_TenderClosureDateRequired()
    {
        Assert.Equal("The tender closure date is not a valid date", Text(MessageKeys.TenderItemClosureDateNotValid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        var command = new TenderItemCommand
        {
            Name = "Undated Tender",
            Description = "A tender item without a closure date",
            Price = 100.00m,
            ClosureDate = null,
            AgentId = fixture.Agent.Id,
            ItemCategoryId = fixture.Category.Id,
            RegionId = fixture.Region.Id,
        };

        OperationResult result = await GetService<ItemService>().ExecuteAsync(command, TransactionType.Persist);

        Assert.False(result.Succeeded);
        Assert.Equal(Text(MessageKeys.TenderItemClosureDateNotValid), Assert.Single(result.Errors));
        Assert.Empty(await CompanyItemIdsAsync());
    }

    /// <summary>
    /// B-09 (Q-01): a category created with the 5-character prefix "ABCDE" → stored with that prefix; the first item of
    /// the company in it has code number 1 and the code "ABCDE1".
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B09_FiveCharacterPrefix()
    {
        Assert.Equal("Category Prefix must be exactly 4 characters", Text(MessageKeys.ItemCategoryPrefixInvalid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        var categoryCommand = new ItemCategoryCommand
        {
            Name = "Five Letter Prefix",
            Prefix = "ABCDE",
            Description = "A category with a five-character prefix",
        };

        long categoryId = Succeeded(
            await GetService<ItemCategoryService>().ExecuteAsync(categoryCommand, TransactionType.Persist));

        string storedPrefix = await ReadAsync(db => db.ItemCategories
            .AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => category.Prefix)
            .SingleAsync());
        Assert.Equal("ABCDE", storedPrefix);

        RetailItemCommand itemCommand = RetailItemCommandFor(fixture, 10.00m);
        itemCommand.ItemCategoryId = categoryId;
        long itemId = await PersistItemAsync(itemCommand);
        Item item = await ReadItemAsync(itemId);
        Assert.Equal(1, item.CodeNumber);
        Assert.Equal("ABCDE1", item.GetCode());
    }

    /// <summary>
    /// B-13 (C-08, Q-23): a second company created through <see cref="CompanyService"/> with the fixture company's
    /// name → created, two companies carry the name, and the fixture company stays bound.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B13_CompanyNameNotCheckedForUniqueness()
    {
        Assert.Equal("Company name is required", Text(MessageKeys.CompanyNameRequired));
        string name = Builder.MyCompany!.Name;
        long secondCompanyId = await CreateExtraCompanyAsync(name);
        try
        {
            List<long> namedCompanies = await ReadAsync(db => db.Companies
                .AsNoTracking()
                .Where(company => company.Name == name)
                .OrderBy(company => company.Id)
                .Select(company => company.Id)
                .ToListAsync());

            Assert.Equal(new[] { CompanyId, secondCompanyId }, namedCompanies);
            Assert.Equal(CompanyId, GetService<ICompanyContext>().CurrentId);
        }
        finally
        {
            await DeleteExtraCompanyAsync(secondCompanyId);
        }
    }

    /// <summary>B-14 (Q-04): a commission rate created with the percentage 12.345 → stored as 12.35.</summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B14_PercentageStoredWithTwoDecimals()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        var command = new CommissionRateCommand { ItemCategoryId = fixture.Category.Id, Percentage = 12.345m };

        long rateId = Succeeded(await GetService<CommissionRateService>().ExecuteAsync(command, TransactionType.Persist));

        decimal stored = await ReadAsync(db => db.CommissionRates
            .AsNoTracking()
            .Where(rate => rate.Id == rateId)
            .Select(rate => rate.Percentage)
            .SingleAsync());
        Assert.Equal(12.35m, command.Percentage);
        Assert.Equal(12.35m, stored);
    }

    /// <summary>
    /// B-16: with the category prefix "ABCD" stored, a category with another name and description and the prefix
    /// "abcd" → the single error <c>$ItemCategoryPrefixExists</c>, and one category of the company has the upper-case
    /// prefix "ABCD".
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B16_PrefixCaseInsensitiveDuplicate()
    {
        Assert.Equal("Category Prefix already exists", Text(MessageKeys.ItemCategoryPrefixExists));
        await Builder.CreateItemCategoryAsync("Upper Case", "ABCD", "The category holding ABCD");
        var command = new ItemCategoryCommand
        {
            Name = "Lower Case",
            Prefix = "abcd",
            Description = "A category asking for abcd",
        };

        OperationResult result = await GetService<ItemCategoryService>().ExecuteAsync(command, TransactionType.Persist);

        Assert.False(result.Succeeded);
        Assert.Equal(Text(MessageKeys.ItemCategoryPrefixExists), Assert.Single(result.Errors));

        long companyId = CompanyId;
        List<long> abcdCategories = await ReadAsync(db => db.ItemCategories
            .AsNoTracking()
            .Where(category => category.CompanyId == companyId && category.Prefix.ToUpper() == "ABCD")
            .Select(category => category.Id)
            .ToListAsync());
        Assert.Single(abcdCategories);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Validation outcomes, string length
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-12: a lock-entity modify (1001) of a client with an empty name → the single error <c>$ClientNameRequired</c>
    /// and the client row unchanged and unlocked; the same modify of an agent with an empty name → no error, the soft
    /// lock written and the name unchanged.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B12_ClientNameCheckedOnModify()
    {
        Assert.Equal("Client name is required", Text(MessageKeys.ClientNameRequired));
        Client client = await Builder.CreateClientAsync();
        Agent agent = await Builder.CreateAgentAsync();
        DateTime lockedAt = await ServerTimeStampAsync();

        var clientCommand = new ClientCommand();
        clientCommand.PopulateFromObject(client);
        clientCommand.Name = string.Empty;
        clientCommand.LockedBySession = SessionId;
        clientCommand.LockedTimestamp = lockedAt;

        OperationResult clientResult = await GetService<ClientService>()
            .ExecuteAsync(clientCommand, TransactionType.ModifyLockEntity);

        Assert.False(clientResult.Succeeded);
        Assert.Equal(Text(MessageKeys.ClientNameRequired), Assert.Single(clientResult.Errors));
        Client storedClient = await ReadAsync(db => db.Clients.AsNoTracking().SingleAsync(c => c.Id == client.Id));
        Assert.Equal("Clive Entworth", storedClient.Name);
        Assert.Null(storedClient.LockedBySession);

        var agentCommand = new AgentCommand();
        agentCommand.PopulateFromObject(agent);
        agentCommand.Name = string.Empty;
        agentCommand.LockedBySession = SessionId;
        agentCommand.LockedTimestamp = lockedAt;

        OperationResult agentResult = await GetService<AgentService>()
            .ExecuteAsync(agentCommand, TransactionType.ModifyLockEntity);

        Assert.True(agentResult.Succeeded, agentResult.FullErrorText);
        Assert.Empty(agentResult.Errors);
        Agent storedAgent = await ReadAsync(db => db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id));
        Assert.Equal("John Smith", storedAgent.Name);
        Assert.Equal(SessionId, storedAgent.LockedBySession);
    }

    /// <summary>
    /// B-22 (D-41): an agent create with an 82-character name, and with an 81-character name followed by one space →
    /// <see cref="StringTooLongException"/> 1035 "String too long" for the name (maximum 81, actual 82), whose error
    /// outcome is headed "Error 1035" with the web exception body; no agent stored.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B22_StringTooLong()
    {
        Assert.Equal(1035, StringTooLongException.Code);
        Assert.Equal(81, FieldLengths.AgentName);
        AgentService agents = GetService<AgentService>();

        foreach (string name in new[] { new string('A', 82), new string('B', 81) + " " })
        {
            var command = new AgentCommand { Name = name, Address = TestCompanyBuilder.NohwereLaneAddress() };

            StringTooLongException failure = await Assert.ThrowsAsync<StringTooLongException>(
                () => agents.ExecuteAsync(command, TransactionType.Persist));

            Assert.Equal(StringTooLongException.Code, failure.ErrorCode);
            Assert.Equal("String too long", failure.Message);
            Assert.Equal(nameof(AgentCommand.Name), failure.FieldName);
            Assert.Equal(FieldLengths.AgentName, failure.MaxLength);
            Assert.Equal(82, failure.ActualLength);

            ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
            Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
            Assert.Equal("Error 1035", outcome.Heading);
            Assert.Equal(
                Text(MessageKeys.BaseExceptionBodyWeb, "1035", "String too long", nameof(AgentCommand.Name), string.Empty),
                outcome.Body);
            Assert.Contains("Error: String too long (1035)", outcome.Body);
        }

        Assert.Empty(await CompanyAgentIdsAsync());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sales
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-19 (Q-05): retail sales of two unsold items → the one with agent commission 0 is created; the one with agent
    /// commission −0.01 gets the single error <c>$SaleAgentCommissionInvalid</c> and stores no sale.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B19_ZeroCommissionAccepted()
    {
        Assert.Equal("Agent commission must be greater than 0", Text(MessageKeys.SaleAgentCommissionInvalid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        RetailItem acceptedItem = await NewRetailItemAsync(fixture, 50.00m);
        RetailItem rejectedItem = await NewRetailItemAsync(fixture, 60.00m);

        Succeeded(await SellAsync(acceptedItem, fixture.Client, 50.00m, agentCommission: 0m));
        OperationResult rejected = await SellAsync(rejectedItem, fixture.Client, 60.00m, agentCommission: -0.01m);

        Assert.False(rejected.Succeeded);
        Assert.Equal(Text(MessageKeys.SaleAgentCommissionInvalid), Assert.Single(rejected.Errors));
        Assert.Single(await SalesOfItemAsync(acceptedItem.Id));
        Assert.Empty(await SalesOfItemAsync(rejectedItem.Id));
    }

    /// <summary>
    /// B-21 (C-04): a retail sale at the price −5 → the single error <c>$RetailPriceInvalid</c>, not
    /// <c>$RetailSalePriceRequired</c>, and no sale stored.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B21_NegativeRetailPrice()
    {
        Assert.Equal("The retail item price must be greater than 0", Text(MessageKeys.RetailPriceInvalid));
        Assert.Equal("A price for the retail item is required", Text(MessageKeys.RetailSalePriceRequired));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        RetailItem item = await NewRetailItemAsync(fixture, 50.00m);

        OperationResult result = await SellAsync(item, fixture.Client, -5m);

        Assert.False(result.Succeeded);
        Assert.Equal(Text(MessageKeys.RetailPriceInvalid), Assert.Single(result.Errors));
        Assert.DoesNotContain(Text(MessageKeys.RetailSalePriceRequired), result.Errors);
        Assert.Empty(await SalesOfItemAsync(item.Id));
    }

    /// <summary>
    /// B-21 (D-58): a retail sale with no sold date validated without pre-validation → the single error
    /// <c>$SoldDateNotValid</c>; the same sale through the service → created with no error and the server timestamp as
    /// its sold date.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B21_SoldDateDefaultAndValidation()
    {
        Assert.Equal("Sold Date is Not Valid", Text(MessageKeys.SoldDateNotValid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        RetailItem item = await NewRetailItemAsync(fixture, 50.00m);
        RetailSaleService sales = GetService<RetailSaleService>();

        var unvalidated = new RetailSaleCommand { ItemId = item.Id, ClientId = fixture.Client.Id, SalePrice = 50.00m };
        var validation = new OperationResult();
        bool valid = await sales.ValidateAsync(unvalidated, ValidationType.Create, validation);

        Assert.False(valid);
        Assert.Equal(Text(MessageKeys.SoldDateNotValid), Assert.Single(validation.Errors));
        Assert.Null(unvalidated.DateSold);

        DateTime before = await ServerTimeStampAsync();
        OperationResult created = await SellAsync(item, fixture.Client, 50.00m);
        DateTime after = await ServerTimeStampAsync();

        Succeeded(created);
        Assert.Empty(created.Errors);
        RetailSale sale = Assert.IsType<RetailSale>(Assert.Single(await SalesOfItemAsync(item.Id)));
        Assert.InRange(sale.DateSold, before, after);
    }

    /// <summary>
    /// B-29 (C-06, Q-07): category rates 2.00 and 3.50, agent A1 holding 3.50 and agent A2 holding none → a sale at
    /// 23.00 of A1's item earns 0.81 (0.805 rounded away from zero); a sale at 100.00 of A2's item earns 2.00; a sale at
    /// 100.00 of A2's item in a category with no rates earns 0.00.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B29_CommissionFallbackAndRounding()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        Agent firstAgent = fixture.Agent;
        Agent secondAgent = await NewAgentAsync("Agent Without Rate");
        await Builder.CreateTestCommissionRateAsync(fixture.Category, 2.00m);
        await Builder.CreateCommissionRateAsync(fixture.Category, 3.50m, firstAgent);
        ItemCategory unratedCategory = await Builder.CreateItemCategoryAsync("Unrated", "UNRT", "A category with no rates");

        RetailItem roundedItem = await NewRetailItemAsync(firstAgent, fixture.Category, fixture.Region, 23.00m);
        RetailItem fallbackItem = await NewRetailItemAsync(secondAgent, fixture.Category, fixture.Region, 100.00m);
        RetailItem unratedItem = await NewRetailItemAsync(secondAgent, unratedCategory, fixture.Region, 100.00m);

        Succeeded(await SellAsync(roundedItem, fixture.Client, 23.00m));
        Succeeded(await SellAsync(fallbackItem, fixture.Client, 100.00m));
        Succeeded(await SellAsync(unratedItem, fixture.Client, 100.00m));

        Assert.Equal(0.81m, Assert.Single(await SalesOfItemAsync(roundedItem.Id)).AgentCommission);
        Assert.Equal(2.00m, Assert.Single(await SalesOfItemAsync(fallbackItem.Id)).AgentCommission);
        Assert.Equal(0.00m, Assert.Single(await SalesOfItemAsync(unratedItem.Id)).AgentCommission);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Purchasing
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>B-23: adding no item to the cart → the single error <c>$ItemRequired</c> and the cart stays empty.</summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B23_AddNullItem()
    {
        Assert.Equal("An Item must be provided", Text(MessageKeys.ItemRequired));
        Client client = await Builder.CreateClientAsync();
        Session.SetClient(client.Id);

        OperationResult result = await Cart.AddItemAsync(SessionId, null, 0m);

        Assert.False(result.Succeeded);
        Assert.Equal(Text(MessageKeys.ItemRequired), Assert.Single(result.Errors));
        Assert.True(GetService<ICartStore>().GetCart(SessionId).IsEmpty);
    }

    /// <summary>
    /// B-24 (Q-16): a tender line added at server time t0 holds a time tendered tl ≥ t0; checked out once the server
    /// clock has passed tl → the stored tender has the time tendered tl.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B24_TenderLineTimestampAtAdd()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        Session.SetClient(fixture.Client.Id);

        DateTime t0 = await ServerTimeStampAsync();
        Assert.True((await Cart.AddItemAsync(SessionId, tenderItem.Id, 150.00m)).Succeeded);
        DateTime addedBy = await ServerTimeStampAsync();

        CartItem line = Assert.Single(GetService<ICartStore>().GetCart(SessionId).Items);
        DateTime tl = Assert.NotNull(line.DateTendered);
        Assert.InRange(tl, t0, addedBy);

        await WaitForServerTimeAfterAsync(tl);
        (OperationResult checkout, _) = await Cart.CheckoutAsync(SessionId);

        Assert.True(checkout.Succeeded, checkout.FullErrorText);
        Tender tender = Assert.Single(await TendersOnItemAsync(tenderItem.Id));
        Assert.Equal(tl, tender.DateTendered);
    }

    /// <summary>
    /// B-24 (Q-29): with the agent holding a 5.00 rate on the category, a retail item at 100.00 is added to the cart and
    /// then repriced to 120.00 → the cart total is 120.00; checkout stores a sale at 100.00 with commission 5.00.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B24_PriceChangeBetweenAddAndCheckout()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        await Builder.CreateCommissionRateAsync(fixture.Category, 5.00m, fixture.Agent);
        RetailItem item = await NewRetailItemAsync(fixture, 100.00m);
        Session.SetClient(fixture.Client.Id);
        Assert.True((await Cart.AddItemAsync(SessionId, item.Id, 0m)).Succeeded);

        await UpdateItemAsync(item.Id, command => command.Price = 120.00m);

        Assert.Equal(120.00m, await Cart.GetCartTotalAsync(SessionId));
        (OperationResult checkout, _) = await Cart.CheckoutAsync(SessionId);

        Assert.True(checkout.Succeeded, checkout.FullErrorText);
        RetailSale sale = Assert.IsType<RetailSale>(Assert.Single(await SalesOfItemAsync(item.Id)));
        Assert.Equal(100.00m, sale.SalePrice);
        Assert.Equal(5.00m, sale.AgentCommission);
        Assert.Equal(fixture.Client.Id, sale.ClientId);
    }

    /// <summary>
    /// B-24 (D-16, D-169): an offer of 99999999999 or 9999999999.995 on a retail item at 100.00 or a tender item with reserve
    /// 100.00 → <see cref="DecimalOverflowException"/> 4033 "Result of expression overflows Decimal precision", whose
    /// error outcome is headed "Error 4033" with the web exception body; the cart stays empty.
    /// </summary>
    /// <param name="tender"><see langword="true"/> for a tender item; <see langword="false"/> for a retail item.</param>
    /// <param name="offer">The offer added with the item.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(OfferOverflowCases))]
    public async Task B24_OfferBeyondDecimal12_2Overflows(bool tender, decimal offer)
    {
        Assert.Equal(4033, DecimalOverflowException.Code);
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        long itemId = tender
            ? (await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5))).Id
            : (await NewRetailItemAsync(fixture, 100.00m)).Id;
        Session.SetClient(fixture.Client.Id);

        DecimalOverflowException failure = await Assert.ThrowsAsync<DecimalOverflowException>(
            () => Cart.AddItemAsync(SessionId, itemId, offer));

        Assert.Equal(DecimalOverflowException.Code, failure.ErrorCode);
        Assert.Equal("Result of expression overflows Decimal precision", failure.Message);
        ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
        Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
        Assert.Equal("Error 4033", outcome.Heading);
        Assert.Equal(
            Text(
                MessageKeys.BaseExceptionBodyWeb,
                "4033",
                "Result of expression overflows Decimal precision",
                Text(MessageKeys.BaseNotAvailable),
                string.Empty),
            outcome.Body);
        Assert.Contains("Error: Result of expression overflows Decimal precision (4033)", outcome.Body);
        Assert.True(GetService<ICartStore>().GetCart(SessionId).IsEmpty);
    }

    /// <summary>
    /// B-24 (D-16, D-169): an offer of 9999999999.99 on a tender item with reserve 100.00 → added, the line holding the offer
    /// 9999999999.99.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B24_OfferAtDecimal12_2LimitIsAdded()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        Session.SetClient(fixture.Client.Id);

        OperationResult result = await Cart.AddItemAsync(SessionId, tenderItem.Id, 9_999_999_999.99m);

        Assert.True(result.Succeeded, result.FullErrorText);
        CartItem line = Assert.Single(GetService<ICartStore>().GetCart(SessionId).Items);
        Assert.Equal(tenderItem.Id, line.ItemId);
        Assert.True(line.IsTender);
        Assert.Equal(9_999_999_999.99m, line.Offer);
    }

    /// <summary>
    /// B-24 (D-16, D-169): <see cref="DecimalRules.MaxMoney"/> is <see cref="DecimalRules.Precision"/> nines with
    /// <see cref="DecimalRules.Scale"/> of them after the decimal point; <see cref="DecimalRules.RoundMoney"/> of a
    /// value whose rounded magnitude is at most that → the value rounded to 2 decimal places, ties away from zero.
    /// </summary>
    /// <param name="value">The value rounded.</param>
    /// <param name="expected">The value <see cref="DecimalRules.RoundMoney"/> returns.</param>
    [Theory]
    [MemberData(nameof(RoundMoneyCases))]
    public void B24_DecimalRulesRoundMoneyKeepsValuesWithinDecimal12_2(decimal value, decimal expected)
    {
        Assert.Equal(
            new string('9', DecimalRules.Precision - DecimalRules.Scale) + "." + new string('9', DecimalRules.Scale),
            DecimalRules.MaxMoney.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(expected, DecimalRules.RoundMoney(value));
    }

    /// <summary>
    /// B-24 (D-16, D-169): <see cref="DecimalRules.RoundMoney"/> of a value whose magnitude, rounded to 2 decimal places, is
    /// greater than <see cref="DecimalRules.MaxMoney"/> → <see cref="DecimalOverflowException"/> 4033.
    /// </summary>
    /// <param name="value">The value rounded.</param>
    [Theory]
    [MemberData(nameof(RoundMoneyOverflowCases))]
    public void B24_DecimalRulesRoundMoneyRejectsValuesBeyondDecimal12_2(decimal value)
    {
        DecimalOverflowException failure = Assert.Throws<DecimalOverflowException>(() => DecimalRules.RoundMoney(value));

        Assert.Equal(DecimalOverflowException.Code, failure.ErrorCode);
        Assert.Equal(DecimalOverflowException.Text, failure.Message);
    }

    /// <summary>B-25: removing no item from the cart → the single error <c>$NoItemSelected</c>.</summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B25_RemoveNothingSelected()
    {
        Assert.Equal("No Item selected", Text(MessageKeys.NoItemSelected));
        Client client = await Builder.CreateClientAsync();
        Session.SetClient(client.Id);

        OperationResult result = Cart.RemoveItem(SessionId, null);

        Assert.False(result.Succeeded);
        Assert.Equal(Text(MessageKeys.NoItemSelected), Assert.Single(result.Errors));
    }

    /// <summary>
    /// B-26: a cart holding a retail item at 100.00 and an offer of 250.50 on a tender item with reserve 200.00 → the
    /// cart total is 350.50.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B26_CartTotal()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        RetailItem retailItem = await NewRetailItemAsync(fixture, 100.00m);
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 200.00m, today.AddDays(5));
        Session.SetClient(fixture.Client.Id);

        Assert.True((await Cart.AddItemAsync(SessionId, retailItem.Id, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, tenderItem.Id, 250.50m)).Succeeded);

        Assert.Equal(350.50m, await Cart.GetCartTotalAsync(SessionId));
        Assert.Equal(350.50m, (await Cart.GetCartAsync(SessionId)).Total);
    }

    /// <summary>
    /// B-27: checkout of an empty cart → the single error <c>$ShoppingCartHasNoItems</c> and no receipt.
    /// Checkout of a cart whose lines, added in the order T2, R2, T1, R1, are held in item-id order: retail item R1;
    /// retail item R2, sold to another client after it was added; tender item T1, whose closure date was moved to
    /// yesterday after it was added; and tender item T2, on which the client already holds a tender of the line's offer
    /// 45.00 and whose reserve was raised from 40.00 to 50.00 after it was added → fails with no receipt and exactly
    /// the errors <c>$ItemSold</c>, <c>$TenderItemAlreadyClosed</c>, <c>$TenderOfferLessThanReserve</c> and
    /// <c>$TenderAlreadyPlaced</c> in that order, the full error text joining them with CR LF, the focus field of R2's
    /// error, no sale of R1, only the other client's sale of R2, no tender on T1, only the client's earlier tender on
    /// T2, and the four lines kept in item-id order.
    /// With the cart emptied, checkout of retail item R3 and tender item T3, after the other client tendered on tender
    /// item T4 at the time tendered and offer of T3's line → throws with the unique-violation SQLSTATE 23505 and leaves
    /// no sale of R3, R3 unsold, no tender on T3, the other client's tender as the only tender on T4, and both lines in
    /// the cart with T3's time tendered unchanged.
    /// With the cart emptied, checkout of retail item R4 and tender item T5 → succeeds with a receipt of both lines, an
    /// empty cart, one sale of R4 to the client whose id is two above the id of R2's sale, and one tender of 55.00 by
    /// the client on T5.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B27_CheckoutAllOrNothing()
    {
        Assert.Equal("There are no items in the shopping cart", Text(MessageKeys.ShoppingCartHasNoItems));
        Assert.Equal("The Item has already been sold", Text(MessageKeys.ItemSold));
        Assert.Equal("The Tender Item has already closed", Text(MessageKeys.TenderItemAlreadyClosed));
        Assert.Equal("The offer price cannot be less than the reserve price", Text(MessageKeys.TenderOfferLessThanReserve));
        Assert.Equal("A tender has already been placed on this item", Text(MessageKeys.TenderAlreadyPlaced));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        long clientId = fixture.Client.Id;
        Client otherClient = await NewClientAsync("Other Client");
        Session.SetClient(clientId);
        ShoppingCart cart = GetService<ICartStore>().GetCart(SessionId);

        (OperationResult empty, CheckoutReceipt? emptyReceipt) = await Cart.CheckoutAsync(SessionId);
        Assert.False(empty.Succeeded);
        Assert.Equal(Text(MessageKeys.ShoppingCartHasNoItems), Assert.Single(empty.Errors));
        Assert.Null(emptyReceipt);

        RetailItem r1 = await NewRetailItemAsync(fixture, 10.00m);
        RetailItem r2 = await NewRetailItemAsync(fixture, 20.00m);
        TenderItem t1 = await NewTenderItemAsync(fixture, 30.00m, today.AddDays(5));
        TenderItem t2 = await NewTenderItemAsync(fixture, 40.00m, today.AddDays(5));
        Tender earlierTender = await Builder.CreateTestTenderAsync(
            45.00m, (await ServerTimeStampAsync()).AddHours(-1), fixture.Client, t2);
        Assert.True((await Cart.AddItemAsync(SessionId, t2.Id, 45.00m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, r2.Id, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, t1.Id, 35.00m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, r1.Id, 0m)).Succeeded);
        RetailSale r2Sale = await NewRetailSaleAsync(r2, otherClient);
        await UpdateItemAsync(t1.Id, command => ((TenderItemCommand)command).ClosureDate = today.AddDays(-1));
        await UpdateItemAsync(t2.Id, command => command.Price = 50.00m);

        (OperationResult invalid, CheckoutReceipt? invalidReceipt) = await Cart.CheckoutAsync(SessionId);

        Assert.False(invalid.Succeeded);
        Assert.Null(invalidReceipt);
        Assert.Equal(
            new[]
            {
                Text(MessageKeys.ItemSold),
                Text(MessageKeys.TenderItemAlreadyClosed),
                Text(MessageKeys.TenderOfferLessThanReserve),
                Text(MessageKeys.TenderAlreadyPlaced),
            },
            invalid.Errors);
        Assert.Equal(
            "The Item has already been sold\r\n"
            + "The Tender Item has already closed\r\n"
            + "The offer price cannot be less than the reserve price\r\n"
            + "A tender has already been placed on this item",
            invalid.FullErrorText);
        Assert.Equal(FocusFields.Item, invalid.FocusField);
        Assert.Empty(await SalesOfItemAsync(r1.Id));
        Sale r2StoredSale = Assert.Single(await SalesOfItemAsync(r2.Id));
        Assert.Equal(r2Sale.Id, r2StoredSale.Id);
        Assert.Equal(otherClient.Id, r2StoredSale.ClientId);
        Assert.Empty(await TendersOnItemAsync(t1.Id));
        Assert.Equal(earlierTender.Id, Assert.Single(await TendersOnItemAsync(t2.Id)).Id);
        Assert.Equal(new[] { r1.Id, r2.Id, t1.Id, t2.Id }, cart.Items.Select(line => line.ItemId));

        Cart.Empty(SessionId);
        RetailItem r3 = await NewRetailItemAsync(fixture, 60.00m);
        TenderItem t3 = await NewTenderItemAsync(fixture, 70.00m, today.AddDays(5));
        TenderItem t4 = await NewTenderItemAsync(fixture, 70.00m, today.AddDays(5));
        Assert.True((await Cart.AddItemAsync(SessionId, r3.Id, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, t3.Id, 75.00m)).Succeeded);
        DateTime t3Tendered = Assert.NotNull(Assert.Single(cart.Items, line => line.ItemId == t3.Id).DateTendered);
        Tender collidingTender = await Builder.CreateTestTenderAsync(75.00m, t3Tendered, otherClient, t4);

        Exception collision = await Assert.ThrowsAnyAsync<Exception>(() => Cart.CheckoutAsync(SessionId));

        Assert.Empty(await SalesOfItemAsync(r3.Id));
        Assert.False((await ReadItemAsync(r3.Id)).IsSold);
        Assert.Empty(await TendersOnItemAsync(t3.Id));
        Tender t4Tender = Assert.Single(await TendersOnItemAsync(t4.Id));
        Assert.Equal(collidingTender.Id, t4Tender.Id);
        Assert.Equal(otherClient.Id, t4Tender.ClientId);
        Assert.Equal(75.00m, t4Tender.Offer);
        Assert.Equal(t3Tendered, t4Tender.DateTendered);
        Assert.Equal(new[] { r3.Id, t3.Id }, cart.Items.Select(line => line.ItemId));
        Assert.Equal(t3Tendered, Assert.Single(cart.Items, line => line.ItemId == t3.Id).DateTendered);
        Assert.Equal("23505", PostgresErrorMapper.UniqueViolation);
        Assert.Equal(PostgresErrorMapper.UniqueViolation, PostgresErrorMapper.GetSqlState(collision));

        Cart.Empty(SessionId);
        RetailItem r4 = await NewRetailItemAsync(fixture, 40.00m);
        TenderItem t5 = await NewTenderItemAsync(fixture, 50.00m, today.AddDays(5));
        Assert.True((await Cart.AddItemAsync(SessionId, r4.Id, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, t5.Id, 55.00m)).Succeeded);
        string r4Code = (await ReadItemAsync(r4.Id)).GetCode()!;
        string t5Code = (await ReadItemAsync(t5.Id)).GetCode()!;

        (OperationResult succeeded, CheckoutReceipt? succeededReceipt) = await Cart.CheckoutAsync(SessionId);

        Assert.True(succeeded.Succeeded, succeeded.FullErrorText);
        CheckoutReceipt receipt = Assert.IsType<CheckoutReceipt>(succeededReceipt);
        Assert.Equal(
            new[]
            {
                new CheckoutReceiptLine(r4.Id, Text(MessageKeys.RetailItemType), r4Code, "Catalogue Retail", 40.00m, 0m, false),
                new CheckoutReceiptLine(t5.Id, Text(MessageKeys.TenderItemType), t5Code, "Catalogue Tender", 50.00m, 55.00m, true),
            },
            receipt.Lines);
        Assert.True(cart.IsEmpty);
        Sale r4Sale = Assert.Single(await SalesOfItemAsync(r4.Id));
        Assert.Equal(clientId, r4Sale.ClientId);
        Assert.Equal(r2Sale.Id + 2, r4Sale.Id);
        Tender tender = Assert.Single(await TendersOnItemAsync(t5.Id));
        Assert.Equal(clientId, tender.ClientId);
        Assert.Equal(55.00m, tender.Offer);
    }

    /// <summary>
    /// B-27: checkout of a cart holding one tender line with the offer 150.00 → one tender of 150.00 by the client
    /// stored, no tender sale or other sale of the item, and the item unsold.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B27_CheckoutPersistsTendersNotTenderSales()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        Session.SetClient(fixture.Client.Id);
        Assert.True((await Cart.AddItemAsync(SessionId, tenderItem.Id, 150.00m)).Succeeded);

        (OperationResult checkout, _) = await Cart.CheckoutAsync(SessionId);

        Assert.True(checkout.Succeeded, checkout.FullErrorText);
        Tender tender = Assert.Single(await TendersOnItemAsync(tenderItem.Id));
        Assert.Equal(150.00m, tender.Offer);
        Assert.Equal(fixture.Client.Id, tender.ClientId);
        Assert.Empty(await SalesOfItemAsync(tenderItem.Id));
        Assert.False((await ReadItemAsync(tenderItem.Id)).IsSold);
    }

    /// <summary>
    /// B-27 (Q-34): a checkout of retail items R1 and R2 that fails at T1 with R2 sold to another client, then,
    /// with R2 removed from the cart, succeeds once the server clock has passed T1 → the sale of R1 has the sold date T1.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B27_RetryKeepsFirstSoldTimestamp()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        Client otherClient = await NewClientAsync("Other Client");
        RetailItem r1 = await NewRetailItemAsync(fixture, 10.00m);
        RetailItem r2 = await NewRetailItemAsync(fixture, 20.00m);
        Session.SetClient(fixture.Client.Id);
        Assert.True((await Cart.AddItemAsync(SessionId, r1.Id, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, r2.Id, 0m)).Succeeded);
        await NewRetailSaleAsync(r2, otherClient);

        DateTime beforeFirstAttempt = await ServerTimeStampAsync();
        (OperationResult failed, _) = await Cart.CheckoutAsync(SessionId);
        DateTime afterFirstAttempt = await ServerTimeStampAsync();

        Assert.False(failed.Succeeded);
        Assert.Contains(Text(MessageKeys.ItemSold), failed.FullErrorText);
        CartItem r1Line = Assert.Single(GetService<ICartStore>().GetCart(SessionId).Items, line => line.ItemId == r1.Id);
        DateTime firstAttempt = Assert.NotNull(r1Line.DateSold);
        Assert.InRange(firstAttempt, beforeFirstAttempt, afterFirstAttempt);

        Assert.True(Cart.RemoveItem(SessionId, r2.Id).Succeeded);
        DateTime secondAttempt = await WaitForServerTimeAfterAsync(firstAttempt);
        (OperationResult succeeded, _) = await Cart.CheckoutAsync(SessionId);

        Assert.True(succeeded.Succeeded, succeeded.FullErrorText);
        RetailSale sale = Assert.IsType<RetailSale>(Assert.Single(await SalesOfItemAsync(r1.Id)));
        Assert.Equal(firstAttempt, sale.DateSold);
        Assert.True(sale.DateSold < secondAttempt, $"The sale of R1 is dated {sale.DateSold:O}, not before {secondAttempt:O}.");
    }

    /// <summary>
    /// B-77: a retail item and a tender item created with the same GUID → both store it and the cart holds two lines;
    /// removing the retail line leaves the tender line alone; with the retail line added again, deleting the retail item
    /// leaves the tender line alone.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B77_SameGuidItemsAreDistinctCartLines()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        string guid = PersistentModel.GenerateGuid();

        RetailItemCommand retailCommand = RetailItemCommandFor(fixture, 10.00m);
        retailCommand.Guid = guid;
        long retailItemId = await PersistItemAsync(retailCommand);
        TenderItemCommand tenderCommand = TenderItemCommandFor(fixture, 20.00m, today.AddDays(5));
        tenderCommand.Guid = guid;
        long tenderItemId = await PersistItemAsync(tenderCommand);

        Assert.Equal(guid, (await ReadItemAsync(retailItemId)).Guid);
        Assert.Equal(guid, (await ReadItemAsync(tenderItemId)).Guid);

        Session.SetClient(fixture.Client.Id);
        ShoppingCart cart = GetService<ICartStore>().GetCart(SessionId);
        Assert.True((await Cart.AddItemAsync(SessionId, retailItemId, 0m)).Succeeded);
        Assert.True((await Cart.AddItemAsync(SessionId, tenderItemId, 25.00m)).Succeeded);
        Assert.Equal(new[] { retailItemId, tenderItemId }, cart.Items.Select(line => line.ItemId));

        Assert.True(Cart.RemoveItem(SessionId, retailItemId).Succeeded);
        Assert.Equal(tenderItemId, Assert.Single(cart.Items).ItemId);

        Assert.True((await Cart.AddItemAsync(SessionId, retailItemId, 0m)).Succeeded);
        OperationResult deleted = await InNewScopeAsync(
            services => services.GetRequiredService<ItemService>().DeleteByIdAsync(retailItemId, TransactionMode.Own));
        Assert.True(deleted.Succeeded, deleted.FullErrorText);
        Assert.Equal(tenderItemId, Assert.Single(cart.Items).ItemId);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Tendering and closure
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-30 (Q-14): two tenders of 500 by client Y on one tender item, both validated while a held lock on Y's tenders
    /// blocks their writes, are both stored once the lock is released → a third tender of 500 by Y on the item gets
    /// <c>$TenderAlreadyPlaced</c> twice, once per matching tender, and the item keeps two tenders.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B30_DuplicateMessagesPerMatch()
    {
        Assert.Equal("A tender has already been placed on this item", Text(MessageKeys.TenderAlreadyPlaced));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        DateTime start = (await ServerTimeStampAsync()).AddHours(-1);
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        Client client = await NewClientAsync("Client Y");

        await using AsyncServiceScope blocker = NewBoundScope();
        await using AsyncServiceScope firstScope = NewBoundScope();
        await using AsyncServiceScope secondScope = NewBoundScope();
        IUnitOfWork blockerTransaction = blocker.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await blockerTransaction.BeginAsync();
        try
        {
            Assert.True(await blocker.ServiceProvider.GetRequiredService<IEntityLocks>()
                .TryLockAsync(LockNames.ClientAllTenders(client.Id)));

            Task<Tender> first = new TestCompanyBuilder(firstScope.ServiceProvider)
                .CreateTestTenderAsync(500m, start.AddMinutes(1), client, tenderItem);
            Task<Tender> second = new TestCompanyBuilder(secondScope.ServiceProvider)
                .CreateTestTenderAsync(500m, start.AddMinutes(2), client, tenderItem);

            bool bothWaiting = await WaitForAdvisoryLockWaitersAsync(2);
            await blockerTransaction.CommitAsync();
            Tender[] stored = await Task.WhenAll(first, second);

            Assert.True(bothWaiting, "The two tender creates were not both waiting for the client's tender lock.");
            Assert.Equal(2, stored.Select(tender => tender.Id).Distinct().Count());
        }
        finally
        {
            if (blockerTransaction.HasActiveTransaction)
            {
                await blockerTransaction.RollbackAsync();
            }
        }

        Assert.Equal(2, (await TendersOnItemAsync(tenderItem.Id)).Count);

        OperationResult third = await TenderAsync(client, tenderItem, 500m, start.AddMinutes(3));

        Assert.False(third.Succeeded);
        Assert.Equal(new[] { Text(MessageKeys.TenderAlreadyPlaced), Text(MessageKeys.TenderAlreadyPlaced) }, third.Errors);
        Assert.Equal(2, (await TendersOnItemAsync(tenderItem.Id)).Count);
    }

    /// <summary>
    /// B-30 (Q-17, EE-01): a tender of 500 at time t by client X on one tender item, then a tender of 500 at the same
    /// time t by client Y on another → the second fails at the database with the unique-violation SQLSTATE 23505 and
    /// stores no tender.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B30_TenderTimeOfferCollision()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        DateTime at = (await ServerTimeStampAsync()).AddHours(-1);
        TenderItem firstItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        TenderItem secondItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        Client otherClient = await NewClientAsync("Client Y");

        await Builder.CreateTestTenderAsync(500m, at, fixture.Client, firstItem);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => InNewScopeAsync(
            services => services.GetRequiredService<TenderService>().ExecuteAsync(
                new TenderCommand { ClientId = otherClient.Id, TenderItemId = secondItem.Id, Offer = 500m, DateTendered = at },
                TransactionType.Persist)));

        Assert.Equal(PostgresErrorMapper.UniqueViolation, PostgresErrorMapper.GetSqlState(failure));
        Assert.Equal("23505", PostgresErrorMapper.UniqueViolation);
        Assert.Empty(await TendersOnItemAsync(secondItem.Id));
        Assert.Single(await TendersOnItemAsync(firstItem.Id));
    }

    /// <summary>
    /// B-30 (D-58): a tender with no time tendered validated without pre-validation → the single error
    /// <c>$TimeStampInvalid</c>; the same tender through the service → stored with no error and the server timestamp as
    /// its time tendered.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B30_TimestampDefaultAndValidation()
    {
        Assert.Equal("Timestamp must be a valid date/time", Text(MessageKeys.TimeStampInvalid));
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        TenderService tenders = GetService<TenderService>();

        var unvalidated = new TenderCommand { ClientId = fixture.Client.Id, TenderItemId = tenderItem.Id, Offer = 150.00m };
        var validation = new OperationResult();
        bool valid = await tenders.ValidateAsync(unvalidated, ValidationType.Create, validation);

        Assert.False(valid);
        Assert.Equal(Text(MessageKeys.TimeStampInvalid), Assert.Single(validation.Errors));
        Assert.Null(unvalidated.DateTendered);

        DateTime before = await ServerTimeStampAsync();
        OperationResult created = await TenderAsync(fixture.Client, tenderItem, 150.00m, null);
        DateTime after = await ServerTimeStampAsync();

        Succeeded(created);
        Assert.Empty(created.Errors);
        Assert.InRange(Assert.Single(await TendersOnItemAsync(tenderItem.Id)).DateTendered, before, after);
    }

    /// <summary>
    /// B-33 (Q-27): a tender on an item closing at the server date → Open, and the item is not closed; with the closure
    /// date moved to the day before → Closed; after closing tenders at the server date, which awards one item → Successful.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B33_TenderStatus()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        TenderItem tenderItem = await NewTenderItemAsync(fixture, 100.00m, today);
        Tender tender = await Builder.CreateTenderAsync(fixture.Client, tenderItem, 150.00m);

        Tender open = await ReadTenderForStatusAsync(tender.Id);
        Assert.Equal(TenderStatus.Open, open.GetStatus(today));
        Assert.False(open.TenderItem!.IsClosed(today));

        await UpdateItemAsync(tenderItem.Id, command => ((TenderItemCommand)command).ClosureDate = today.AddDays(-1));

        Tender closed = await ReadTenderForStatusAsync(tender.Id);
        Assert.Equal(TenderStatus.Closed, closed.GetStatus(today));
        Assert.True(closed.TenderItem!.IsClosed(today));

        Assert.Equal(1, await CloseTendersAsync(today));

        Assert.Equal(TenderStatus.Successful, (await ReadTenderForStatusAsync(tender.Id)).GetStatus(today));
    }

    /// <summary>
    /// B-34 (Q-03, Q-27, EE-04): tender items with a reserve of 100, day 0 being the server date, created in the order
    /// A, C, I, F, E, H, B, G, their row ids and code numbers ascending in that order: A (closing on day 0, category
    /// LAMP; X 500, then Y 700), C (day 2, LAMP; X 600), I (day 1, ALFA; Y 310), F (day 1, ZETA; X 320), E (day 0, ALFA;
    /// Y 330), H (day 1, LAMP; X 340), B (day 0, LAMP; no tenders) and G (day 1, ZETA; Y 350). Closing tenders at day 1
    /// while another transaction holds the row lock of H → while the closure waits for that lock, the committed tender
    /// sales are those of A, E, G and F, in that order; after that transaction rolls back → 6 awards, the tender sales in
    /// the order A, E, G, F, H, I; A sold to Y through the 700 tender; H not closed at day 1; B unsold; C unsold and
    /// keeping its one tender. Then D (day 0, LAMP; X 800, then Y 800) and J (day 1, ZETA; X 360) are created and
    /// tenders are closed at day 2 → 3 awards, the tender sales continuing D, J, C; D sold to X through the earlier 800
    /// tender; C not closed at day 2; B unsold.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B34_ClosureOrderStopAndTieBreak()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        ItemCategory lamp = fixture.Category;
        ItemCategory zeta = await Builder.CreateItemCategoryAsync("Zeta Goods", "ZETA", "Goods of the Z range");
        ItemCategory alfa = await Builder.CreateItemCategoryAsync("Alfa Goods", "ALFA", "Goods of the A range");
        DateOnly day0 = await ServerDateAsync();
        DateOnly day1 = day0.AddDays(1);
        DateOnly day2 = day0.AddDays(2);
        DateTime start = (await ServerTimeStampAsync()).AddHours(-1);
        Client x = fixture.Client;
        Client y = await NewClientAsync("Client Y");

        TenderItem itemA = await NewTenderItemAsync(fixture.Agent, lamp, fixture.Region, 100.00m, day0);
        TenderItem itemC = await NewTenderItemAsync(fixture.Agent, lamp, fixture.Region, 100.00m, day2);
        TenderItem itemI = await NewTenderItemAsync(fixture.Agent, alfa, fixture.Region, 100.00m, day1);
        TenderItem itemF = await NewTenderItemAsync(fixture.Agent, zeta, fixture.Region, 100.00m, day1);
        TenderItem itemE = await NewTenderItemAsync(fixture.Agent, alfa, fixture.Region, 100.00m, day0);
        TenderItem itemH = await NewTenderItemAsync(fixture.Agent, lamp, fixture.Region, 100.00m, day1);
        TenderItem itemB = await NewTenderItemAsync(fixture.Agent, lamp, fixture.Region, 100.00m, day0);
        TenderItem itemG = await NewTenderItemAsync(fixture.Agent, zeta, fixture.Region, 100.00m, day1);
        await Builder.CreateTestTenderAsync(500m, start.AddMinutes(1), x, itemA);
        Tender y700 = await Builder.CreateTestTenderAsync(700m, start.AddMinutes(2), y, itemA);
        await Builder.CreateTestTenderAsync(600m, start.AddMinutes(3), x, itemC);
        await Builder.CreateTestTenderAsync(310m, start.AddMinutes(4), y, itemI);
        await Builder.CreateTestTenderAsync(320m, start.AddMinutes(5), x, itemF);
        await Builder.CreateTestTenderAsync(330m, start.AddMinutes(6), y, itemE);
        await Builder.CreateTestTenderAsync(340m, start.AddMinutes(7), x, itemH);
        await Builder.CreateTestTenderAsync(350m, start.AddMinutes(8), y, itemG);
        var labels = new Dictionary<long, string>
        {
            [itemA.Id] = "A",
            [itemB.Id] = "B",
            [itemC.Id] = "C",
            [itemE.Id] = "E",
            [itemF.Id] = "F",
            [itemG.Id] = "G",
            [itemH.Id] = "H",
            [itemI.Id] = "I",
        };

        await using AsyncServiceScope blocker = NewBoundScope();
        IUnitOfWork blockerTransaction = blocker.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await blockerTransaction.BeginAsync();
        try
        {
            await blocker.ServiceProvider.GetRequiredService<IEntityLocks>().LockItemRowAsync(itemH.Id);

            Task<int> closing = CloseTendersAsync(day1);
            bool waiting = await WaitForRowLockWaiterAsync();
            string soldWhileWaiting = await TenderSaleItemLabelsAsync(labels);
            await blockerTransaction.RollbackAsync();
            int awarded = await closing;

            Assert.True(waiting, "The closure was not waiting for the row lock of item H.");
            Assert.Equal("A E G F", soldWhileWaiting);
            Assert.Equal(6, awarded);
        }
        finally
        {
            if (blockerTransaction.HasActiveTransaction)
            {
                await blockerTransaction.RollbackAsync();
            }
        }

        Assert.Equal("A E G F H I", await TenderSaleItemLabelsAsync(labels));
        TenderSale saleOfA = Assert.IsType<TenderSale>(Assert.Single(await SalesOfItemAsync(itemA.Id)));
        Assert.Equal(y700.Id, saleOfA.TenderId);
        Assert.Equal(y.Id, saleOfA.ClientId);
        Assert.False(Assert.IsType<TenderItem>(await ReadItemAsync(itemH.Id)).IsClosed(day1));
        Assert.Empty(await SalesOfItemAsync(itemB.Id));
        Assert.Empty(await SalesOfItemAsync(itemC.Id));
        Assert.Single(await TendersOnItemAsync(itemC.Id));

        TenderItem itemD = await NewTenderItemAsync(fixture.Agent, lamp, fixture.Region, 100.00m, day0);
        TenderItem itemJ = await NewTenderItemAsync(fixture.Agent, zeta, fixture.Region, 100.00m, day1);
        Tender x800 = await Builder.CreateTestTenderAsync(800m, start.AddMinutes(9), x, itemD);
        await Builder.CreateTestTenderAsync(800m, start.AddMinutes(10), y, itemD);
        await Builder.CreateTestTenderAsync(360m, start.AddMinutes(11), x, itemJ);
        labels[itemD.Id] = "D";
        labels[itemJ.Id] = "J";

        Assert.Equal(3, await CloseTendersAsync(day2));

        Assert.Equal("A E G F H I D J C", await TenderSaleItemLabelsAsync(labels));
        TenderSale saleOfD = Assert.IsType<TenderSale>(Assert.Single(await SalesOfItemAsync(itemD.Id)));
        Assert.Equal(x800.Id, saleOfD.TenderId);
        Assert.Equal(x.Id, saleOfD.ClientId);
        Assert.False(Assert.IsType<TenderItem>(await ReadItemAsync(itemC.Id)).IsClosed(day2));
        Assert.Empty(await SalesOfItemAsync(itemB.Id));
    }

    /// <summary>
    /// Reads, untracked, the tender sales of the items in <paramref name="labels"/> in tender-sale id order, and returns
    /// the labels of their items joined by spaces.
    /// </summary>
    private async Task<string> TenderSaleItemLabelsAsync(IReadOnlyDictionary<long, string> labels)
    {
        long[] itemIds = labels.Keys.ToArray();
        List<long> soldItemIds = await ReadAsync(db => db.Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .Where(sale => itemIds.Contains(sale.ItemId))
            .OrderBy(sale => sale.Id)
            .Select(sale => sale.ItemId)
            .ToListAsync());
        return Labels(soldItemIds, labels);
    }

    /// <summary>
    /// Counts the row-lock waits (lock types <c>transactionid</c> and <c>tuple</c>, not granted) of the sessions on the
    /// test database, on a connection of its own, until one session is waiting or five seconds have passed.
    /// </summary>
    /// <returns><see langword="true"/> when a waiting session was seen.</returns>
    private async Task<bool> WaitForRowLockWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks JOIN pg_stat_activity ON pg_stat_activity.pid = pg_locks.pid "
            + "WHERE pg_locks.locktype IN ('transactionid', 'tuple') AND NOT pg_locks.granted "
            + "AND pg_stat_activity.datname = current_database()",
            connection);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollLimit)
        {
            if (await command.ExecuteScalarAsync() is long waiting && waiting >= 1)
            {
                return true;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    /// <summary>Reads, in a new scope, the tender with its tender item and tender sale for its status.</summary>
    private Task<Tender> ReadTenderForStatusAsync(long tenderId) =>
        InNewScopeAsync(async services =>
            await services.GetRequiredService<TenderService>().FindForStatusAsync(tenderId)
            ?? throw new InvalidOperationException($"Tender {tenderId} does not exist."));

    // ---------------------------------------------------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-35 (Q-25, D-37): items I1 to I6 of <see cref="ArrangeSearchAsync"/>, created from I6 down to I1 and then given
    /// the code numbers 10, 20, 30, 40, 50 and 60 in label order (I1 retail K1/R1/G1 100; I2 retail K2/R2/G2 200; I3
    /// tender K1/R2/G1 300 open; I4 tender K2/R1/G2 400 closed; I5 retail K1/R1/G2 500 sold to Z; I6 retail K2/R2/G1 600
    /// in the session cart; R1 in N1, R2 in N2) searched with each criteria set → the items listed per set, in
    /// code-number order: the returned code numbers are those of the listed items, strictly ascending, and the result
    /// ids of the criteria are the returned item ids in the same order; a price bound of 0 sets no bound.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B35_ItemSearchFilters()
    {
        SearchFixture fixture = await ArrangeSearchAsync();
        var cases = new (string Name, Action<ItemSearchCriteria> Configure, string Expected)[]
        {
            ("defaults", _ => { }, "I1 I2 I3"),
            ("include closed tenders", c => c.IncludeClosedTenders = true, "I1 I2 I3 I4"),
            ("include items in cart", c => c.IncludeItemsInCart = true, "I1 I2 I3 I6"),
            ("include sold items", c => c.IncludeSoldItems = true, "I1 I2 I3 I5"),
            ("agent G1", c => c.AgentId = fixture.G1.Id, "I1 I3"),
            ("client Z with sold items", c => { c.ClientId = fixture.Z.Id; c.IncludeSoldItems = true; }, "I5"),
            ("client X with sold items", c => { c.ClientId = fixture.X.Id; c.IncludeSoldItems = true; }, string.Empty),
            ("country N1", c => c.CountryId = fixture.N1.Id, "I1"),
            ("region R2", c => c.RegionId = fixture.R2.Id, "I2 I3"),
            ("category K1", c => c.ItemCategoryId = fixture.K1.Id, "I1 I3"),
            ("only retail items", c => c.OnlyRetailItems = true, "I1 I2"),
            ("only tender items", c => c.OnlyTenderItems = true, "I3"),
            ("price 200 to 300", c => { c.PriceLow = 200m; c.PriceHigh = 300m; }, "I2 I3"),
            ("price 0 to 0", c => { c.PriceLow = 0m; c.PriceHigh = 0m; }, "I1 I2 I3"),
            ("price 0 to 150", c => { c.PriceLow = 0m; c.PriceHigh = 150m; }, "I1"),
            ("price 250 to 0", c => { c.PriceLow = 250m; c.PriceHigh = 0m; }, "I3"),
        };

        var expected = new Dictionary<string, string>();
        var actual = new Dictionary<string, string>();
        var expectedCodeNumbers = new Dictionary<string, string>();
        var actualCodeNumbers = new Dictionary<string, string>();
        foreach ((string name, Action<ItemSearchCriteria> configure, string expectedItems) in cases)
        {
            var criteria = new ItemSearchCriteria();
            configure(criteria);
            IReadOnlyList<Item> items = await GetService<ItemSearch>().DoSearchAsync(criteria, SessionId);
            int[] codeNumbers = items.Select(item => item.CodeNumber).ToArray();

            expected[name] = expectedItems;
            actual[name] = Labels(items.Select(item => item.Id), fixture.Labels);
            expectedCodeNumbers[name] = string.Join(
                " ",
                expectedItems.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(label => fixture.CodeNumbers[label]));
            actualCodeNumbers[name] = string.Join(" ", codeNumbers);
            Assert.Equal(items.Select(item => item.Id), criteria.ResultItemIds);
            Assert.True(
                codeNumbers.Zip(codeNumbers.Skip(1)).All(pair => pair.First < pair.Second),
                $"The case \"{name}\" returned the code numbers \"{actualCodeNumbers[name]}\", not in strictly ascending order.");
        }

        Assert.Equal(expected, actual);
        Assert.Equal(expectedCodeNumbers, actualCodeNumbers);
    }

    /// <summary>
    /// B-35: a search with the price bounds 900 and 800 → <see cref="ErewhonException"/> 500002, whose error outcome is
    /// <c>$MinPriceGreaterThanMaxError</c> titled <c>$Error</c>; the bounds 0 and 800 → no exception.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B35_PriceBandRejectsLowAboveHigh()
    {
        Assert.Equal("The maximum price must be greater than the minimum price", Text(MessageKeys.MinPriceGreaterThanMaxError));
        Assert.Equal("Error", Text(MessageKeys.Error));
        Assert.Equal(500002, ErewhonException.MinHigherThanMax);
        ItemSearch search = GetService<ItemSearch>();

        ErewhonException failure = await Assert.ThrowsAsync<ErewhonException>(
            () => search.DoSearchAsync(new ItemSearchCriteria { PriceLow = 900m, PriceHigh = 800m }, SessionId));

        Assert.Equal(ErewhonException.MinHigherThanMax, failure.ErrorCode);
        ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
        Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
        Assert.Equal(Text(MessageKeys.Error), outcome.Heading);
        Assert.Equal(Text(MessageKeys.MinPriceGreaterThanMaxError), outcome.Body);

        Exception? noBound = await Record.ExceptionAsync(
            () => search.DoSearchAsync(new ItemSearchCriteria { PriceLow = 0m, PriceHigh = 800m }, SessionId));
        Assert.Null(noBound);
    }

    /// <summary>
    /// B-35 (D-16, D-169): with the bounds 100 and 600 set, setting <see cref="ItemSearchCriteria.PriceLow"/> or
    /// <see cref="ItemSearchCriteria.PriceHigh"/> to 99999999999 → <see cref="DecimalOverflowException"/> 4033, whose
    /// error outcome is headed "Error 4033", and both bounds keep 100 and 600; setting that bound to 9999999999.99 →
    /// stored.
    /// </summary>
    /// <param name="upper">
    /// <see langword="true"/> to set <see cref="ItemSearchCriteria.PriceHigh"/>; <see langword="false"/> to set
    /// <see cref="ItemSearchCriteria.PriceLow"/>.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B35_PriceBoundBeyondDecimal12_2Overflows(bool upper)
    {
        var criteria = new ItemSearchCriteria { PriceLow = 100m, PriceHigh = 600m };
        void SetBound(decimal value)
        {
            if (upper)
            {
                criteria.PriceHigh = value;
            }
            else
            {
                criteria.PriceLow = value;
            }
        }

        DecimalOverflowException failure = Assert.Throws<DecimalOverflowException>(() => SetBound(99_999_999_999m));

        Assert.Equal(DecimalOverflowException.Code, failure.ErrorCode);
        Assert.Equal("Error 4033", ErrorOutcomeMiddleware.Create(failure).Heading);
        Assert.Equal(100m, criteria.PriceLow);
        Assert.Equal(600m, criteria.PriceHigh);

        SetBound(9_999_999_999.99m);

        Assert.Equal(9_999_999_999.99m, upper ? criteria.PriceHigh : criteria.PriceLow);
        Assert.Equal(upper ? 100m : 600m, upper ? criteria.PriceLow : criteria.PriceHigh);
    }

    /// <summary>
    /// B-35 (D-17): over the items I1 to I6 of <see cref="ArrangeSearchAsync"/>, the unsold retail item I7 (K1/G1 700,
    /// code number 70) listed in region R3 of N1, then R3 deleted through <see cref="RegionService"/>, which leaves I7
    /// with no region. A search with country N1 or N2 → <see cref="NoCompanyException"/> 1090 "Null object reference",
    /// whose error outcome is the web exception body titled "Error 1090"; the same search with no criteria → I1, I2, I3
    /// and I7; with region R1 → I1.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B35_CountryFilterOnItemWithoutRegionRaises1090()
    {
        SearchFixture fixture = await ArrangeSearchAsync();
        Region r3 = await Builder.CreateTestRegionAsync("Region R3", fixture.N1);
        RetailItem i7 = await NewRetailItemAsync(fixture.G1, fixture.K1, r3, 700m);
        await UpdateItemAsync(i7.Id, command => command.CodeNumber = 70);
        OperationResult regionDeleted = await InNewScopeAsync(
            services => services.GetRequiredService<RegionService>().DeleteByIdAsync(r3.Id));
        Assert.True(regionDeleted.Succeeded, regionDeleted.FullErrorText);
        Assert.Null((await ReadItemAsync(i7.Id)).RegionId);
        var labels = new Dictionary<long, string>(fixture.Labels) { [i7.Id] = "I7" };
        ItemSearch search = GetService<ItemSearch>();

        foreach (Country country in new[] { fixture.N1, fixture.N2 })
        {
            NoCompanyException failure = await Assert.ThrowsAsync<NoCompanyException>(
                () => search.DoSearchAsync(new ItemSearchCriteria { CountryId = country.Id }, SessionId));

            Assert.Equal(1090, failure.ErrorCode);
            Assert.Equal("Null object reference", failure.Message);
            ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
            Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
            Assert.Equal("Error 1090", outcome.Heading);
            Assert.Equal(Text(MessageKeys.BaseExceptionHeading, "1090"), outcome.Heading);
            Assert.Equal(
                Text(
                    MessageKeys.BaseExceptionBodyWeb,
                    "1090",
                    "Null object reference",
                    Text(MessageKeys.BaseNotAvailable),
                    string.Empty),
                outcome.Body);
            Assert.Contains("Error: Null object reference (1090)", outcome.Body);
        }

        var unfiltered = new ItemSearchCriteria();
        IReadOnlyList<Item> all = await search.DoSearchAsync(unfiltered, SessionId);
        Assert.Equal("I1 I2 I3 I7", Labels(all.Select(item => item.Id), labels));
        Assert.Equal(all.Select(item => item.Id), unfiltered.ResultItemIds);

        var byRegion = new ItemSearchCriteria { RegionId = fixture.R1.Id };
        IReadOnlyList<Item> inRegion = await search.DoSearchAsync(byRegion, SessionId);
        Assert.Equal("I1", Labels(inRegion.Select(item => item.Id), labels));
        Assert.Equal(inRegion.Select(item => item.Id), byRegion.ResultItemIds);
    }

    /// <summary>
    /// B-36 (Q-21): over the items I1 to I6 of <see cref="ArrangeSearchAsync"/>, created from I6 down to I1 and then
    /// given the code numbers 10 to 60 in label order, with every criterion set, agent G1, client Z and
    /// include-items-in-cart among them, and results held, <see cref="ItemSearchCriteria.Initialize"/> keeps the agent,
    /// the client and the cart flag and clears every other criterion and the results; with the client then cleared, a
    /// search → I1, I3 and I6.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B36_InitializeKeepsAgentClientAndCartFlag()
    {
        SearchFixture fixture = await ArrangeSearchAsync();
        ItemSearch search = GetService<ItemSearch>();
        var criteria = new ItemSearchCriteria();
        await search.DoSearchAsync(criteria, SessionId);
        Assert.NotEmpty(criteria.ResultItemIds);

        criteria.AgentId = fixture.G1.Id;
        criteria.ClientId = fixture.Z.Id;
        criteria.IncludeItemsInCart = true;
        criteria.CountryId = fixture.N2.Id;
        criteria.RegionId = fixture.R2.Id;
        criteria.ItemCategoryId = fixture.K2.Id;
        criteria.OnlyRetailItems = true;
        criteria.OnlyTenderItems = true;
        criteria.IncludeClosedTenders = true;
        criteria.IncludeSoldItems = true;
        criteria.PriceLow = 100m;
        criteria.PriceHigh = 600m;

        criteria.Initialize();

        Assert.Equal(fixture.G1.Id, criteria.AgentId);
        Assert.Equal(fixture.Z.Id, criteria.ClientId);
        Assert.True(criteria.IncludeItemsInCart);
        Assert.Null(criteria.CountryId);
        Assert.Null(criteria.RegionId);
        Assert.Null(criteria.ItemCategoryId);
        Assert.False(criteria.OnlyRetailItems);
        Assert.False(criteria.OnlyTenderItems);
        Assert.False(criteria.IncludeClosedTenders);
        Assert.False(criteria.IncludeSoldItems);
        Assert.True(JadeValues.IsNull(criteria.PriceLow));
        Assert.True(JadeValues.IsNull(criteria.PriceHigh));
        Assert.Empty(criteria.ResultItemIds);

        criteria.ClientId = null;
        IReadOnlyList<Item> items = await search.DoSearchAsync(criteria, SessionId);

        Assert.Equal("I1 I3 I6", Labels(items.Select(item => item.Id), fixture.Labels));
    }

    /// <summary>
    /// B-37: tenders labelled by client and offer, each item's tenders created in the order listed, with times placed
    /// t1 to t7: X300 (t1), Y310 (t4), X320 and Y330 (both t6), X340 and X350 (both t7) on an open item; X500 (t3) and
    /// Y450 (t5) on an item awarded to X500 at the server date; X400 and Y410 (both t2) on an item of agent G2 whose
    /// closure date is then moved to the day before. The tender ids rise in creation order, the lower offer of each
    /// shared time first → by status, Successful gives X500, Closed Y410 X400, Open X300 Y310 Y450 Y330 X320 X350 X340,
    /// and all three X300 Y410 X400 X500 Y310 Y450 Y330 X320 X350 X340, in time order and, within one time, by offer
    /// descending; from all three statuses, the client X gives X300 X400 X500 X320 X350 X340, the open item X300 Y310
    /// Y330 X320 X350 X340 and the agent G2 Y410 X400.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B37_TenderSearch()
    {
        CatalogueFixture fixture = await ArrangeCatalogueAsync();
        DateOnly today = await ServerDateAsync();
        DateTime start = (await ServerTimeStampAsync()).AddHours(-1);
        Client x = fixture.Client;
        Client y = await NewClientAsync("Client Y");
        Agent secondAgent = await NewAgentAsync("Agent G2");

        TenderItem openItem = await NewTenderItemAsync(fixture, 100.00m, today.AddDays(5));
        TenderItem wonItem = await NewTenderItemAsync(fixture, 100.00m, today);
        var labels = new Dictionary<long, string>
        {
            [(await Builder.CreateTestTenderAsync(300m, start.AddMinutes(1), x, openItem)).Id] = "X300",
            [(await Builder.CreateTestTenderAsync(310m, start.AddMinutes(4), y, openItem)).Id] = "Y310",
            [(await Builder.CreateTestTenderAsync(320m, start.AddMinutes(6), x, openItem)).Id] = "X320",
            [(await Builder.CreateTestTenderAsync(330m, start.AddMinutes(6), y, openItem)).Id] = "Y330",
            [(await Builder.CreateTestTenderAsync(340m, start.AddMinutes(7), x, openItem)).Id] = "X340",
            [(await Builder.CreateTestTenderAsync(350m, start.AddMinutes(7), x, openItem)).Id] = "X350",
            [(await Builder.CreateTestTenderAsync(500m, start.AddMinutes(3), x, wonItem)).Id] = "X500",
            [(await Builder.CreateTestTenderAsync(450m, start.AddMinutes(5), y, wonItem)).Id] = "Y450",
        };
        Assert.Equal(1, await CloseTendersAsync(today));

        TenderItem closedItem = await NewTenderItemAsync(secondAgent, fixture.Category, fixture.Region, 100.00m, today);
        labels[(await Builder.CreateTestTenderAsync(400m, start.AddMinutes(2), x, closedItem)).Id] = "X400";
        labels[(await Builder.CreateTestTenderAsync(410m, start.AddMinutes(2), y, closedItem)).Id] = "Y410";
        await UpdateItemAsync(closedItem.Id, command => ((TenderItemCommand)command).ClosureDate = today.AddDays(-1));
        Assert.Equal("X300 Y310 X320 Y330 X340 X350 X500 Y450 X400 Y410", Labels(labels.Keys.Order(), labels));

        static void AllStatuses(TenderSearchCriteria criteria)
        {
            criteria.IncludeOpenTenders = true;
            criteria.IncludeClosedTenders = true;
            criteria.IncludeSuccessfulTenders = true;
        }

        var cases = new (string Name, Action<TenderSearchCriteria> Configure, string Expected)[]
        {
            ("successful", c => c.IncludeSuccessfulTenders = true, "X500"),
            ("closed", c => c.IncludeClosedTenders = true, "Y410 X400"),
            ("open", c => c.IncludeOpenTenders = true, "X300 Y310 Y450 Y330 X320 X350 X340"),
            ("all statuses", AllStatuses, "X300 Y410 X400 X500 Y310 Y450 Y330 X320 X350 X340"),
            ("all statuses, client X", c => { AllStatuses(c); c.ClientId = x.Id; }, "X300 X400 X500 X320 X350 X340"),
            ("all statuses, open item", c => { AllStatuses(c); c.ItemId = openItem.Id; }, "X300 Y310 Y330 X320 X350 X340"),
            ("all statuses, agent of the closed item", c => { AllStatuses(c); c.AgentId = secondAgent.Id; }, "Y410 X400"),
        };

        var expected = new Dictionary<string, string>();
        var actual = new Dictionary<string, string>();
        foreach ((string name, Action<TenderSearchCriteria> configure, string expectedTenders) in cases)
        {
            var criteria = new TenderSearchCriteria();
            configure(criteria);
            IReadOnlyList<Tender> tenders = await GetService<TenderSearch>().DoSearchAsync(criteria);

            expected[name] = expectedTenders;
            actual[name] = Labels(tenders.Select(tender => tender.Id), labels);
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Creates, through <see cref="Builder"/>, countries N1 and N2, regions R1 (in N1) and R2 (in N2), categories K1 and
    /// K2, agents G1 and G2, clients X and Z, and then the B-35 items from I6 down to I1, and asserts that the item ids
    /// fall from I1 to I6. Sets the code numbers of I1 to I6 to 10, 20, 30, 40, 50 and 60 through
    /// <see cref="UpdateItemAsync"/>, then sells I5 to Z at its price and adds I6 to the cart of the test session, whose
    /// client is X.
    /// </summary>
    private async Task<SearchFixture> ArrangeSearchAsync()
    {
        DateOnly today = await ServerDateAsync();
        Country n1 = await Builder.CreateTestCountryAsync("Country N1");
        Country n2 = await Builder.CreateTestCountryAsync("Country N2");
        Region r1 = await Builder.CreateTestRegionAsync("Region R1", n1);
        Region r2 = await Builder.CreateTestRegionAsync("Region R2", n2);
        ItemCategory k1 = await Builder.CreateItemCategoryAsync("Category K1", "KONE", "First search category");
        ItemCategory k2 = await Builder.CreateItemCategoryAsync("Category K2", "KTWO", "Second search category");
        Agent g1 = await NewAgentAsync("Agent G1");
        Agent g2 = await NewAgentAsync("Agent G2");
        Client x = await NewClientAsync("Client X");
        Client z = await NewClientAsync("Client Z");

        RetailItem i6 = await NewRetailItemAsync(g1, k2, r2, 600m);
        RetailItem i5 = await NewRetailItemAsync(g2, k1, r1, 500m);
        TenderItem i4 = await NewTenderItemAsync(g2, k2, r1, 400m, today.AddDays(-1));
        TenderItem i3 = await NewTenderItemAsync(g1, k1, r2, 300m, today.AddDays(5));
        RetailItem i2 = await NewRetailItemAsync(g2, k2, r2, 200m);
        RetailItem i1 = await NewRetailItemAsync(g1, k1, r1, 100m);

        var searchItems = new (string Label, Item Item, int CodeNumber)[]
        {
            ("I1", i1, 10),
            ("I2", i2, 20),
            ("I3", i3, 30),
            ("I4", i4, 40),
            ("I5", i5, 50),
            ("I6", i6, 60),
        };
        long[] idsInLabelOrder = searchItems.Select(entry => entry.Item.Id).ToArray();
        Assert.Equal(idsInLabelOrder.OrderDescending(), idsInLabelOrder);

        foreach ((_, Item item, int codeNumber) in searchItems)
        {
            await UpdateItemAsync(item.Id, command => command.CodeNumber = codeNumber);
        }

        await NewRetailSaleAsync(i5, z);
        Session.SetClient(x.Id);
        Assert.True((await Cart.AddItemAsync(SessionId, i6.Id, 0m)).Succeeded);

        return new SearchFixture(
            searchItems.ToDictionary(entry => entry.Item.Id, entry => entry.Label),
            searchItems.ToDictionary(entry => entry.Label, entry => entry.CodeNumber),
            g1,
            g2,
            x,
            z,
            n1,
            n2,
            r1,
            r2,
            k1,
            k2);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Transactions and company
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// B-39 (C-07, Q-08): one agent read in two scopes before any write, then updated from each read with the cities
    /// "First City" and "Second City" in turn → both updates succeed and the stored city is "Second City".
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B39_StaleEditLastWriterWins()
    {
        Agent agent = await Builder.CreateAgentAsync();
        await using AsyncServiceScope firstScope = NewBoundScope();
        await using AsyncServiceScope secondScope = NewBoundScope();
        AgentService firstAgents = firstScope.ServiceProvider.GetRequiredService<AgentService>();
        AgentService secondAgents = secondScope.ServiceProvider.GetRequiredService<AgentService>();

        Agent firstRead = await firstAgents.FindByIdAsync(agent.Id)
            ?? throw new InvalidOperationException($"Agent {agent.Id} does not exist.");
        Agent secondRead = await secondAgents.FindByIdAsync(agent.Id)
            ?? throw new InvalidOperationException($"Agent {agent.Id} does not exist.");

        var firstCommand = new AgentCommand();
        firstCommand.PopulateFromObject(firstRead);
        firstCommand.Address!.City = "First City";
        var secondCommand = new AgentCommand();
        secondCommand.PopulateFromObject(secondRead);
        secondCommand.Address!.City = "Second City";

        OperationResult firstUpdate = await firstAgents.ExecuteAsync(firstCommand, TransactionType.Persist);
        OperationResult secondUpdate = await secondAgents.ExecuteAsync(secondCommand, TransactionType.Persist);

        Assert.True(firstUpdate.Succeeded, firstUpdate.FullErrorText);
        Assert.True(secondUpdate.Succeeded, secondUpdate.FullErrorText);
        string storedCity = await ReadAsync(db => db.Agents
            .AsNoTracking()
            .Where(stored => stored.Id == agent.Id)
            .Select(stored => stored.Address.City)
            .SingleAsync());
        Assert.Equal("Second City", storedCity);
    }

    /// <summary>
    /// B-70: agent operations in Own mode → Delete with no model object succeeds and writes nothing; Persist with no
    /// model object creates the agent; Persist with the model object updates it; the add-commission-rate modify (1004)
    /// gives the agent the rate; the transaction type −2 fails with no error and writes nothing.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B70_PersistEntityDispatch()
    {
        AgentService agents = GetService<AgentService>();

        OperationResult deleted = await agents.ExecuteAsync(new AgentCommand { Name = "Nobody" }, TransactionType.Delete);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);
        Assert.Empty(deleted.Errors);
        Assert.Empty(await CompanyAgentIdsAsync());

        var command = new AgentCommand { Name = "Dispatch Agent", Address = TestCompanyBuilder.NohwereLaneAddress() };
        long agentId = Succeeded(await agents.ExecuteAsync(command, TransactionType.Persist));
        Assert.Equal(agentId, Assert.Single(await CompanyAgentIdsAsync()));
        Assert.NotNull(command.ModelObject);

        command.Address!.City = "Dispatch City";
        OperationResult updated = await agents.ExecuteAsync(command, TransactionType.Persist);
        Assert.True(updated.Succeeded, updated.FullErrorText);
        Assert.Equal(agentId, Assert.Single(await CompanyAgentIdsAsync()));
        Agent stored = await ReadAsync(db => db.Agents.AsNoTracking().SingleAsync(agent => agent.Id == agentId));
        Assert.Equal("Dispatch City", stored.Address.City);

        ItemCategory category = await Builder.CreateItemCategoryAsync("Dispatch", "DSPT", "The category of the dispatch rate");
        long rateId = (await Builder.CreateTestCommissionRateAsync(category, 10.00m)).Id;
        command.CommissionRateId = rateId;
        OperationResult modified = await agents.ExecuteAsync(command, TransactionType.ModifyAddCommissionRateForAgent);
        Assert.True(modified.Succeeded, modified.FullErrorText);
        List<long> heldRates = await ReadAsync(db => db.AgentCommissionRates
            .AsNoTracking()
            .Where(link => link.AgentId == agentId)
            .Select(link => link.CommissionRateId)
            .ToListAsync());
        Assert.Equal(rateId, Assert.Single(heldRates));

        var unknownType = new AgentCommand { Name = "Never Written", Address = TestCompanyBuilder.NohwereLaneAddress() };
        OperationResult rejected = await agents.ExecuteAsync(unknownType, (TransactionType)(-2));
        Assert.False(rejected.Succeeded);
        Assert.Empty(rejected.Errors);
        Assert.Null(unknownType.ModelObject);
        Assert.Equal(agentId, Assert.Single(await CompanyAgentIdsAsync()));
    }

    /// <summary>
    /// B-70: agent operations in Ambient mode with no active transaction → Delete with no model object returns
    /// success, the transaction type −2 returns failure, and an empty-name Persist returns failure with
    /// <c>$AgentNameRequired</c> first, each without an exception and without a row; a valid Persist raises
    /// <see cref="NotInTransactionStateException"/> 1048 and writes no row.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B70_InTransStateEarlyReturnsOutsideTransaction()
    {
        Assert.Equal("Agent name is required", Text(MessageKeys.AgentNameRequired));
        Assert.Equal(1048, NotInTransactionStateException.Code);
        AgentService agents = GetService<AgentService>();
        IUnitOfWork unitOfWork = GetService<IUnitOfWork>();
        Assert.False(unitOfWork.HasActiveTransaction);

        OperationResult deleted = await WithoutExceptionAsync(() => agents.ExecuteAsync(
            new AgentCommand { Name = "Nobody" }, TransactionType.Delete, TransactionMode.Ambient));
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        OperationResult unknownType = await WithoutExceptionAsync(() => agents.ExecuteAsync(
            new AgentCommand { Name = "Never Written", Address = TestCompanyBuilder.NohwereLaneAddress() },
            (TransactionType)(-2),
            TransactionMode.Ambient));
        Assert.False(unknownType.Succeeded);

        OperationResult invalid = await WithoutExceptionAsync(() => agents.ExecuteAsync(
            new AgentCommand { Name = string.Empty, Address = TestCompanyBuilder.NohwereLaneAddress() },
            TransactionType.Persist,
            TransactionMode.Ambient));
        Assert.False(invalid.Succeeded);
        Assert.Equal(Text(MessageKeys.AgentNameRequired), invalid.Errors[0]);

        Assert.Empty(await CompanyAgentIdsAsync());

        NotInTransactionStateException outside = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => agents.ExecuteAsync(
                new AgentCommand { Name = "Outside Transaction", Address = TestCompanyBuilder.NohwereLaneAddress() },
                TransactionType.Persist,
                TransactionMode.Ambient));
        Assert.Equal(NotInTransactionStateException.Code, outside.ErrorCode);
        Assert.Empty(await CompanyAgentIdsAsync());
        Assert.False(unitOfWork.HasActiveTransaction);
    }

    /// <summary>
    /// B-73 (Q-30): agent "Only In Second" created in a second company and agent "Only In First" in the bound fixture
    /// company → asked on the second company, <see cref="CompanyService.AgentExists"/> answers for the bound company:
    /// "Only In First" true, "Only In Second" false, <see langword="null"/> and "" false.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B73_AgentExistsReadsBoundCompany()
    {
        long secondCompanyId = (await NewCompanyAsync()).Id;
        try
        {
            await using (AsyncServiceScope secondScope = Api.Services.CreateAsyncScope())
            {
                var secondBuilder = new TestCompanyBuilder(secondScope.ServiceProvider);
                secondBuilder.Bind(secondCompanyId);
                await NewAgentAsync(secondBuilder, "Only In Second");
            }

            await NewAgentAsync("Only In First");
            CompanyService companies = GetService<CompanyService>();

            Assert.True(await companies.AgentExists(secondCompanyId, "Only In First"));
            Assert.False(await companies.AgentExists(secondCompanyId, "Only In Second"));
            Assert.False(await companies.AgentExists(secondCompanyId, null));
            Assert.False(await companies.AgentExists(secondCompanyId, string.Empty));
        }
        finally
        {
            await DeleteExtraCompanyAsync(secondCompanyId);
        }
    }

    /// <summary>
    /// B-80 (C-16): <see cref="CompanyService.CreateCompanyAsync"/> with the fixture company present →
    /// <see cref="ErewhonException"/> 500003 "Company already exists"; one company remains and it stays bound.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B80_CreateCompanyRefusesSurvivingCompany()
    {
        Assert.Equal(500003, ErewhonException.CompanyAlreadyExists);
        await using (AsyncServiceScope scope = Api.Services.CreateAsyncScope())
        {
            var command = new CompanyCommand
            {
                Name = "Erewhon Investments Inc",
                Address = TestCompanyBuilder.GillSimpsonDriveAddress(),
            };

            ErewhonException failure = await Assert.ThrowsAsync<ErewhonException>(
                () => scope.ServiceProvider.GetRequiredService<CompanyService>().CreateCompanyAsync(command));

            Assert.Equal(ErewhonException.CompanyAlreadyExists, failure.ErrorCode);
            Assert.Equal("Company already exists", failure.Message);
        }

        List<long> companyIds = await ReadAsync(db => db.Companies.AsNoTracking().Select(company => company.Id).ToListAsync());
        Assert.Equal(CompanyId, Assert.Single(companyIds));
        Assert.Equal(CompanyId, GetService<ICompanyContext>().CurrentId);
    }

    /// <summary>
    /// B-80 (D-43): with a second company created through <see cref="TestCompanyBuilder.CreateCompanyAsync"/> in a new
    /// scope, the company context of a new scope with no company bound resolves the lowest company id, the fixture
    /// company's.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task B80_FirstCompanyIsBound()
    {
        long secondCompanyId = (await NewCompanyAsync()).Id;
        try
        {
            long lowestId = await ReadAsync(db => db.Companies.AsNoTracking().MinAsync(company => company.Id));

            await using AsyncServiceScope unbound = Api.Services.CreateAsyncScope();
            long currentId = unbound.ServiceProvider.GetRequiredService<ICompanyContext>().CurrentId;

            Assert.Equal(lowestId, currentId);
            Assert.Equal(CompanyId, currentId);
            Assert.NotEqual(secondCompanyId, currentId);
        }
        finally
        {
            await DeleteExtraCompanyAsync(secondCompanyId);
        }
    }

    /// <summary>The fixtures of <see cref="ArrangeCatalogueAsync"/>.</summary>
    private sealed record CatalogueFixture(Country Country, Region Region, ItemCategory Category, Agent Agent, Client Client);

    /// <summary>
    /// The fixtures of <see cref="ArrangeSearchAsync"/>: the item labels by item id, the code numbers set by item label
    /// and the other rows.
    /// </summary>
    private sealed record SearchFixture(
        IReadOnlyDictionary<long, string> Labels,
        IReadOnlyDictionary<string, int> CodeNumbers,
        Agent G1,
        Agent G2,
        Client X,
        Client Z,
        Country N1,
        Country N2,
        Region R1,
        Region R2,
        ItemCategory K1,
        ItemCategory K2);
}
