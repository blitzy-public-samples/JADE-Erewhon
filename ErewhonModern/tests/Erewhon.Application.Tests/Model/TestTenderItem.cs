using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE unit tests of <c>TestTenderItem</c>: a create that allocates the next code number, the debug
/// string, the price and type text of the fixture tender item, and updates of the fixture tender item inside and
/// outside a transaction. Each test starts with the <see cref="TestItemFixture"/> company, whose tender item "Proxy
/// Tender" has code number 2 in the category "Abstractions" ("ABST"), the reserve price 5.50 and the server date as
/// its closure date.
/// </summary>
public sealed class TestTenderItem : TestItemFixture
{
    private const int UpdateOutsideTransaction = 1048;

    private const int UpdatedCodeNumber = 1180;

    private const string UpdatedName = "Marantz 6300";

    private const string UpdatedDescription =
        "Automatic and manual turntable featuring a servo-controlled direct drive platter and a precision tonearm mounted on gimbal bearings";

    private const decimal UpdatedPrice = 900.00m;

    private static readonly DateOnly UpdatedClosureDate = new(1976, 12, 7);

    private ItemService Items => GetService<ItemService>();

    private IErewhonDbContext Db => GetService<IErewhonDbContext>();

    /// <summary>
    /// Creating the tender item "Tender Item" with code number 0 in the fixture category persists it with the next
    /// code number of the company, read before the create, and keeps the prefix of the fixture tender item's category.
    /// Ports <c>TestTenderItem.testCreateUsingNextCodeNumber</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateUsingNextCodeNumber()
    {
        TenderItem fixtureTenderItem = await LoadStoredTenderItemAsync(MyTenderItem.Id);
        Assert.NotNull(fixtureTenderItem.ItemCategory);
        string codePrefix = fixtureTenderItem.ItemCategory.Prefix;
        int nextCodeNumber = await GetService<ItemCodeAllocator>().AllocateNextCodeNumberAsync(MyCompany.Id);
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        var tenderItemCommand = new TenderItemCommand
        {
            Name = "Tender Item",
            Description = "This represents an item that can be tendered",
            ClosureDate = date,
            Photo = null,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = 99.99m,
        };

        OperationResult result =
            await Items.ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        TenderItem createdTenderItem = Assert.IsType<TenderItem>(tenderItemCommand.ModelObject);
        TenderItem tenderItem = await LoadStoredTenderItemAsync(createdTenderItem.Id);
        Assert.Equal(nextCodeNumber, tenderItem.CodeNumber);
        Assert.NotNull(tenderItem.ItemCategory);
        Assert.Equal(codePrefix, tenderItem.ItemCategory.Prefix);
    }

    /// <summary>
    /// The debug string of the fixture tender item is its OID text, " TenderItem: ", its code, its name, "$" and its
    /// price as <c>Decimal.String</c>, and its closure date as <c>Date.String</c>, separated by ", ".
    /// Ports <c>TestTenderItem.testGetDebugString</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        TenderItem tenderItem = await LoadStoredTenderItemAsync(MyTenderItem.Id);
        Assert.NotNull(tenderItem.ItemCategory);

        string oid = ObjectIdentity.Of(tenderItem);
        string? code = tenderItem.GetCode();
        string name = tenderItem.Name;
        decimal price = tenderItem.Price;
        DateOnly closureDate = tenderItem.ClosureDate;
        string expected = oid + " TenderItem: " + code + ", " + name + ", $" + JadeFormat.DecimalString(price) + ", "
            + JadeFormat.DateString(closureDate);

        string actual = tenderItem.GetDebugString();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// <see cref="Item.GetPrice"/> of the fixture tender item is its price. Ports <c>TestTenderItem.testGetPrice</c>.
    /// </summary>
    [Fact]
    public void testGetPrice()
    {
        decimal price = MyTenderItem.Price;

        Assert.Equal(price, MyTenderItem.GetPrice());
    }

    /// <summary>
    /// <see cref="Item.GetTypeString"/> of the fixture tender item is the <see cref="MessageKeys.TenderItemType"/>
    /// text. Ports <c>TestTenderItem.testGetTypeString</c>.
    /// </summary>
    [Fact]
    public void testGetTypeString()
    {
        Assert.Equal(MessageCatalog.Get(MessageKeys.TenderItemType), MyTenderItem.GetTypeString());
    }

    /// <summary>
    /// Updating the fixture tender item in its own transaction to code number 1180, "Marantz 6300", a new description,
    /// the closure date 7 December 1976, no photo, the region "Paris" of the new country "USA", the new category
    /// "Audio Equipment" ("AUDQ") and the price 900.00 keeps the fixture tender item as the model object and stores
    /// the new price, code number, name, description and closure date. Ports <c>TestTenderItem.testUpdate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        Assert.NotNull(Builder.MyTenderItem);

        TenderItemCommand tenderItemCommand = await CreateMarantzUpdateCommandAsync();

        OperationResult result =
            await Items.ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.Same(MyTenderItem, tenderItemCommand.ModelObject);
        Assert.Equal(MyTenderItem.Id, result.Id);

        TenderItem tenderItem = await LoadStoredTenderItemAsync(MyTenderItem.Id);
        Assert.Equal(UpdatedPrice, tenderItem.Price);
        Assert.Equal(UpdatedCodeNumber, tenderItem.CodeNumber);
        Assert.Equal(UpdatedName, tenderItem.Name);
        Assert.Equal(UpdatedDescription, tenderItem.Description);
        Assert.Equal(UpdatedClosureDate, tenderItem.ClosureDate);
    }

    /// <summary>
    /// The update of <see cref="testUpdate"/> in the caller's transaction when none is active raises
    /// <see cref="NotInTransactionStateException"/> with error code 1048 and leaves the stored fixture tender item
    /// "Proxy Tender" at 5.50 with its code number, description, closure date, region and category unchanged.
    /// Ports <c>TestTenderItem.testUpdateOutsideTransStateException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        Assert.NotNull(Builder.MyTenderItem);

        TenderItem original = await LoadStoredTenderItemAsync(MyTenderItem.Id);
        TenderItemCommand tenderItemCommand = await CreateMarantzUpdateCommandAsync();

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => Items.ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Ambient));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);

        TenderItem tenderItem = await LoadStoredTenderItemAsync(MyTenderItem.Id);
        Assert.Equal("Proxy Tender", tenderItem.Name);
        Assert.Equal(5.50m, tenderItem.Price);
        Assert.Equal(original.CodeNumber, tenderItem.CodeNumber);
        Assert.Equal(original.Description, tenderItem.Description);
        Assert.Equal(original.ClosureDate, tenderItem.ClosureDate);
        Assert.Equal(original.RegionId, tenderItem.RegionId);
        Assert.Equal(original.ItemCategoryId, tenderItem.ItemCategoryId);
    }

    /// <summary>
    /// Creates the item category "Audio Equipment" ("AUDQ"), the country "USA" in its own transaction and the region
    /// "Paris" in it, then returns a tender item command populated from the fixture tender item and set to code number
    /// 1180, "Marantz 6300", the turntable description, the closure date 7 December 1976, no photo, the new region,
    /// the new category and the price 900.00.
    /// </summary>
    private async Task<TenderItemCommand> CreateMarantzUpdateCommandAsync()
    {
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Audio Equipment",
            "AUDQ",
            "Devices that reproduce, record, or process sound. ");

        var countryCommand = new CountryCommand { Name = "USA" };
        await GetService<CountryService>().ExecuteAsync(countryCommand, TransactionType.Persist, TransactionMode.Own);
        Assert.NotNull(countryCommand.ModelObject);

        Region region = await Builder.CreateRegionAsync(countryCommand.ModelObject);

        var tenderItemCommand = new TenderItemCommand();
        tenderItemCommand.PopulateFromObject(MyTenderItem);
        tenderItemCommand.CodeNumber = UpdatedCodeNumber;
        tenderItemCommand.Name = UpdatedName;
        tenderItemCommand.Description = UpdatedDescription;
        tenderItemCommand.ClosureDate = UpdatedClosureDate;
        tenderItemCommand.Photo = null;
        tenderItemCommand.RegionId = region.Id;
        tenderItemCommand.ItemCategoryId = itemCategory.Id;
        tenderItemCommand.Price = UpdatedPrice;

        return tenderItemCommand;
    }

    /// <summary>Reads the stored tender item <paramref name="id"/>, untracked, with its category.</summary>
    private Task<TenderItem> LoadStoredTenderItemAsync(long id) =>
        Db.Items
            .AsNoTracking()
            .OfType<TenderItem>()
            .Include(item => item.ItemCategory)
            .SingleAsync(item => item.Id == id);
}
