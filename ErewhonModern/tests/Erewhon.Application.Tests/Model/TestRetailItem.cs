using System.Globalization;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE unit tests of <c>TestRetailItem</c>: the code, name, price, sale, type and debug-string accessors of
/// the fixture retail item, the allocation of the next code number to a new retail item, and the update of a retail
/// item in its own transaction and in <see cref="TransactionMode.Ambient"/> mode with no active transaction. Each test
/// starts with the <see cref="TestItemFixture"/> fixture: the retail item "Proxy Retail" priced 5.50, code number 1 in
/// the category "Abstractions" ("ABST") and sold to the client, and the tender item with code number 2.
/// </summary>
public sealed class TestRetailItem : TestItemFixture
{
    /// <summary>The error code of an update attempted with no active transaction.</summary>
    private const int UpdateOutsideTransaction = 1048;

    /// <summary>
    /// The description of the category "Music" that <see cref="testUpdate"/> and
    /// <see cref="testUpdateOutsideTransStateException"/> move the fixture retail item to.
    /// </summary>
    private const string MusicDescription =
        "Vocal or instrumental sounds (or both) combined in such a way as to produce beauty of form, harmony, and expression of emotion.";

    /// <summary>
    /// Allocates the next code number of the fixture company, then creates the retail item "Retail Item" with code
    /// number 0 in the fixture region, category and agent at 777.77: the item is persisted with the allocated code
    /// number and the fixture item's category prefix.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateUsingNextCodeNumber()
    {
        RetailItem myRetailItem = await LoadRetailItemAsync(MyRetailItem.Id);
        string? codePrefix = myRetailItem.ItemCategory?.Prefix;
        int nextCodeNumber = await GetService<ItemCodeAllocator>().AllocateNextCodeNumberAsync(MyCompany.Id);

        var retailItemCommand = new RetailItemCommand
        {
            Name = "Retail Item",
            Description = "This is a description for a retail item",
            Photo = null,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = 777.77m,
        };

        OperationResult result = await PersistEntityAsync(retailItemCommand);

        Assert.True(result.Succeeded, result.FullErrorText);
        RetailItem retailItem = await LoadRetailItemAsync(Assert.IsType<RetailItem>(retailItemCommand.ModelObject).Id);
        Assert.Equal(nextCodeNumber, retailItem.CodeNumber);
        Assert.Equal(codePrefix, retailItem.ItemCategory?.Prefix);
    }

    /// <summary>The code of the fixture retail item is its category prefix followed by its code number.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetCode()
    {
        RetailItem myRetailItem = await LoadRetailItemAsync(MyRetailItem.Id);
        string expected = myRetailItem.ItemCategory?.Prefix
            + myRetailItem.CodeNumber.ToString(CultureInfo.InvariantCulture);

        string? actual = myRetailItem.GetCode();

        Assert.Equal(expected, actual);
    }

    /// <summary><see cref="Item.GetCodeNumber"/> of the fixture retail item is its code number.</summary>
    [Fact]
    public void testGetCodeNumber()
    {
        int expected = MyRetailItem.CodeNumber;
        int actual = MyRetailItem.GetCodeNumber();

        Assert.Equal(expected, actual);
    }

    /// <summary><see cref="Item.GetCodePrefix"/> of the fixture retail item is the prefix of its category.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetCodePrefix()
    {
        RetailItem myRetailItem = await LoadRetailItemAsync(MyRetailItem.Id);
        string? expected = myRetailItem.ItemCategory?.Prefix;
        string? actual = myRetailItem.GetCodePrefix();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The debug string of the fixture retail item is its object identity, " RetailItem: ", its code, ", ", its name,
    /// ", $" and its price to 2 decimal places.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        RetailItem myRetailItem = await LoadRetailItemAsync(MyRetailItem.Id);
        string oid = ObjectIdentity.Of(myRetailItem);
        string expected = oid + " RetailItem: " + myRetailItem.GetCode() + ", " + myRetailItem.Name + ", $"
            + JadeFormat.DecimalString(myRetailItem.Price);

        string actual = myRetailItem.GetDebugString();

        Assert.Equal(expected, actual);
    }

    /// <summary><see cref="Item.GetName"/> of the fixture retail item is its name.</summary>
    [Fact]
    public void testGetName()
    {
        string description = MyRetailItem.Name;

        Assert.Equal(description, MyRetailItem.GetName());
    }

    /// <summary><see cref="RetailItem.GetPrice"/> of the fixture retail item is its price.</summary>
    [Fact]
    public void testGetPrice()
    {
        decimal expected = MyRetailItem.Price;

        decimal actual = MyRetailItem.GetPrice();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The fixture company holds 2 items. A new retail item "Dog" with code number 4567 at 15005.00 has no sale; once
    /// it is sold to the fixture client at its price, its sale is that retail sale, compared by row id.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetSale()
    {
        Assert.Equal(2, await CountCompanyItemsAsync());

        var retailItemCommand = new RetailItemCommand
        {
            CodeNumber = 4567,
            Name = "Dog",
            Description = "A Labrador Puppy",
            Photo = null,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = 15005.00m,
        };

        await PersistEntityAsync(retailItemCommand);

        Item? retailItem = retailItemCommand.ModelObject;
        Assert.NotNull(retailItem);
        Assert.Null(retailItem.GetSale());

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = retailItemCommand.Price,
            DateSold = await GetService<IServerClock>().GetServerTimeStampAsync(),
        };

        await GetService<RetailSaleService>()
            .ExecuteAsync(retailSaleCommand, TransactionType.Persist, TransactionMode.Own);

        Sale? retailSale = retailSaleCommand.ModelObject;
        Sale? sale = (await LoadRetailItemAsync(retailItem.Id)).GetSale();
        Assert.NotNull(sale);
        Assert.Equal(retailSale?.Id, sale.Id);
    }

    /// <summary>
    /// <see cref="RetailItem.GetTypeString"/> of the fixture retail item is the <see cref="MessageKeys.RetailItemType"/>
    /// text.
    /// </summary>
    [Fact]
    public void testGetTypeString()
    {
        Assert.Equal(MessageCatalog.Get(MessageKeys.RetailItemType), MyRetailItem.GetTypeString());
    }

    /// <summary>
    /// Updates the sold fixture retail item to code number 123, name "Easy as", description "Do Re Me", no photo, the
    /// region "Paris" of a new country "USA", a new category "Music" ("MUSI") and the price 19.70: the update succeeds
    /// and the stored item holds every new value, its region and category compared by row id.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        Assert.NotNull(Builder.MyRetailItem);

        const int codeNumber = 123;
        const string name = "Easy as";
        const string description = "Do Re Me";
        const decimal price = 19.70m;
        byte[]? photo = null;
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync("Music", "MUSI", MusicDescription);
        Region region = await CreateRegionInUsaAsync();

        RetailItemCommand retailItemCommand = CommandFromMyRetailItem(
            codeNumber, name, description, photo, region, itemCategory, price);

        OperationResult result = await PersistEntityAsync(retailItemCommand);

        Assert.True(result.Succeeded, result.FullErrorText);
        RetailItem stored = await ReadStoredRetailItemAsync(MyRetailItem.Id);
        Assert.Equal(price, stored.Price);
        Assert.Equal(codeNumber, stored.CodeNumber);
        Assert.Equal(name, stored.Name);
        Assert.Equal(description, stored.Description);
        Assert.Null(stored.Photo);
        Assert.Equal(region.Id, stored.RegionId);
        Assert.Equal(itemCategory.Id, stored.ItemCategoryId);
    }

    /// <summary>
    /// Updates the fixture retail item as <see cref="testUpdate"/> does, with the category "Music" ("MUSC"), in
    /// <see cref="TransactionMode.Ambient"/> mode with no active transaction:
    /// <see cref="NotInTransactionStateException"/> with error code 1048 is raised and the stored item keeps its name,
    /// price, code number, region and category.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        Assert.NotNull(Builder.MyRetailItem);

        RetailItem original = await ReadStoredRetailItemAsync(MyRetailItem.Id);
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync("Music", "MUSC", MusicDescription);
        Region region = await CreateRegionInUsaAsync();

        RetailItemCommand retailItemCommand = CommandFromMyRetailItem(
            123, "Easy as", "Do Re Me", null, region, itemCategory, 19.70m);

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistEntityInTransStateAsync(retailItemCommand));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        RetailItem stored = await ReadStoredRetailItemAsync(MyRetailItem.Id);
        Assert.Equal("Proxy Retail", stored.Name);
        Assert.Equal(5.50m, stored.Price);
        Assert.Equal(original.CodeNumber, stored.CodeNumber);
        Assert.Equal(MyRegion.Id, stored.RegionId);
        Assert.Equal(MyItemCategory.Id, stored.ItemCategoryId);
    }

    /// <summary>
    /// Returns a retail item command populated from <see cref="TestItemFixture.MyRetailItem"/>, with the given code
    /// number, name, description, photo, region, category and price.
    /// </summary>
    private RetailItemCommand CommandFromMyRetailItem(
        int codeNumber,
        string name,
        string description,
        byte[]? photo,
        Region region,
        ItemCategory itemCategory,
        decimal price)
    {
        var retailItemCommand = new RetailItemCommand();
        retailItemCommand.PopulateFromObject(MyRetailItem);
        retailItemCommand.CodeNumber = codeNumber;
        retailItemCommand.Name = name;
        retailItemCommand.Description = description;
        retailItemCommand.Photo = photo;
        retailItemCommand.RegionId = region.Id;
        retailItemCommand.ItemCategoryId = itemCategory.Id;
        retailItemCommand.Price = price;
        return retailItemCommand;
    }

    /// <summary>
    /// Creates the country "USA" through <see cref="CountryService"/> in its own transaction, then its region "Paris"
    /// through <see cref="Erewhon.Testing.TestCompanyBuilder.CreateRegionAsync"/>.
    /// </summary>
    private async Task<Region> CreateRegionInUsaAsync()
    {
        var countryCommand = new CountryCommand { Name = "USA" };
        await GetService<CountryService>().ExecuteAsync(countryCommand, TransactionType.Persist, TransactionMode.Own);

        Country? country = countryCommand.ModelObject;
        Assert.NotNull(country);
        return await Builder.CreateRegionAsync(country);
    }

    /// <summary>
    /// Returns the item with row id <paramref name="itemId"/>, tracked and read through
    /// <see cref="ItemService.FindByIdAsync"/> with its category, region and sale, asserting that it is a
    /// <see cref="RetailItem"/>.
    /// </summary>
    private async Task<RetailItem> LoadRetailItemAsync(long itemId) =>
        Assert.IsType<RetailItem>(await GetService<ItemService>().FindByIdAsync(itemId));

    /// <summary>Returns the stored retail item with row id <paramref name="itemId"/>, read without change tracking.</summary>
    private Task<RetailItem> ReadStoredRetailItemAsync(long itemId) =>
        GetService<IErewhonDbContext>().Items
            .AsNoTracking()
            .OfType<RetailItem>()
            .SingleAsync(item => item.Id == itemId);

    /// <summary>Returns the number of stored items of <see cref="TestItemFixture.MyCompany"/>.</summary>
    private Task<int> CountCompanyItemsAsync()
    {
        long companyId = MyCompany.Id;

        return GetService<IErewhonDbContext>().Items
            .AsNoTracking()
            .CountAsync(item => item.CompanyId == companyId);
    }

    /// <summary>
    /// Runs <paramref name="command"/> through <see cref="ItemService"/> with <see cref="TransactionType.Persist"/> in
    /// <see cref="TransactionMode.Own"/> mode.
    /// </summary>
    private Task<OperationResult> PersistEntityAsync(ItemCommand command) =>
        GetService<ItemService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

    /// <summary>
    /// Runs <paramref name="command"/> through <see cref="ItemService"/> with <see cref="TransactionType.Persist"/> in
    /// <see cref="TransactionMode.Ambient"/> mode.
    /// </summary>
    private Task<OperationResult> PersistEntityInTransStateAsync(ItemCommand command) =>
        GetService<ItemService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient);
}
