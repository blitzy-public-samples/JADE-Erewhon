using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Tests of retail sale creation and validation through <see cref="RetailSaleService"/>, of the agent commission a
/// retail sale earns, and of the <see cref="RetailSale"/> accessors, over the fixture of <see cref="TestSaleFixture"/>.
/// Ports the JADE class <c>TestRetailSale</c>.
/// </summary>
public sealed class TestRetailSale : TestSaleFixture
{
    private const int UpdateOutsideTransaction = 1048;

    /// <summary>
    /// Creates the retail item 1851 "Moby-Dick" of the fixture region, category and agent, priced 23 with no photo, then
    /// sells it to the fixture client at the price of the fixture retail item (5.50) at the server timestamp: the
    /// sale's agent commission is its sale price times the fixture commission rate's percentage (25.00) divided by 100,
    /// rounded to 2 decimal places with ties away from zero (1.38). Ports <c>TestRetailSale.testAgentCommission</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAgentCommission()
    {
        decimal percentage = MyCommissionRate.Percentage;
        DateTime timeStamp = await GetService<IServerClock>().GetServerTimeStampAsync();

        RetailItem retailItem = await CreateRetailItemAsync(
            1851,
            "Moby-Dick",
            "Moby-Dick; or, The Whale is an 1851 novel by American writer Herman Melville.",
            23m);

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = MyRetailItem.Price,
            DateSold = timeStamp,
        };

        await PersistEntityAsync(retailSaleCommand);
        RetailSale retailSale = Assert.IsType<RetailSale>(retailSaleCommand.ModelObject);

        decimal salePrice = retailSale.SalePrice;

        // Round2 is the assignment to the JADE Decimal[12,2] local.
        decimal commission = DecimalRules.Round2(salePrice * (percentage / 100m));

        Assert.Equal(commission, retailSale.AgentCommission);
    }

    /// <summary>
    /// Creates the retail item 1851 "Moby-Dick" as <see cref="testAgentCommission"/> does, then sells it to the fixture
    /// client at the price of the fixture retail item in <see cref="TransactionMode.Ambient"/> mode with no active
    /// transaction: <see cref="NotInTransactionStateException"/> with error code 1048 is raised and the item stays
    /// unsold. Ports <c>TestRetailSale.testAgentCommissionOutsideTransStateException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAgentCommissionOutsideTransStateException()
    {
        DateTime timeStamp = await GetService<IServerClock>().GetServerTimeStampAsync();

        RetailItem retailItem = await CreateRetailItemAsync(
            1851,
            "Moby-Dick",
            "Moby-Dick; or, The Whale is an 1851 novel by American writer Herman Melville.",
            23m);

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = MyRetailItem.Price,
            DateSold = timeStamp,
        };

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistEntityInTransStateAsync(retailSaleCommand));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Assert.False(await ItemHasStoredSaleAsync(retailItem.Id));
    }

    /// <summary>
    /// Sells the fixture retail item, already sold, to the fixture client at 555 at the server timestamp: the sale is
    /// not persisted and the only error is <see cref="MessageKeys.ItemSold"/>.
    /// Ports <c>TestRetailSale.testAlreadySoldException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAlreadySoldException()
    {
        DateTime timeStamp = await GetService<IServerClock>().GetServerTimeStampAsync();

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = MyRetailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = 555m,
            DateSold = timeStamp,
        };

        OperationResult result = await PersistEntityAsync(retailSaleCommand);

        Assert.False(result.Succeeded);
        AssertSingleError(result, MessageKeys.ItemSold);
    }

    /// <summary>
    /// Creates the retail item 1111 "Retail Item", "A full description of the Retail Item", of the fixture region,
    /// category and agent, priced 55.50 with no photo, then sells it to the fixture client at its price, sold at
    /// <see cref="TestSaleFixture.Timestamp"/>, in <see cref="TransactionMode.Ambient"/> mode with no active
    /// transaction: <see cref="NotInTransactionStateException"/> with error code 1048 is raised and the item stays
    /// unsold. Ports <c>TestRetailSale.testCreateOutsideTransStateException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateOutsideTransStateException()
    {
        RetailItem retailItem = await CreateRetailItemAsync(
            1111,
            "Retail Item",
            "A full description of the Retail Item",
            55.50m);

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = retailItem.Price,
            DateSold = Timestamp,
        };

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistEntityInTransStateAsync(retailSaleCommand));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Assert.False(await ItemHasStoredSaleAsync(retailItem.Id));
    }

    /// <summary>
    /// The date of the fixture retail sale is the server date. Ports <c>TestRetailSale.testGetDate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDate()
    {
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        Assert.Equal(date, MyRetailSale.GetDate());
    }

    /// <summary>
    /// The debug string of the fixture retail sale is its object identity, " RetailSale: $", its sale price to 2
    /// decimal places, ", ", its time sold as <c>dd MMMM yyyy, HH:mm:ss</c>, ", ", the fixture retail item's code,
    /// " (", the item's name, "), " and the fixture client's name. Ports <c>TestRetailSale.testGetDebugString</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        RetailSale retailSale = Assert.IsType<RetailSale>(
            await GetService<RetailSaleService>().FindByIdAsync(MyRetailSale.Id));
        Item? retailItem = await GetService<ItemService>().FindByIdAsync(MyRetailItem.Id);
        Assert.NotNull(retailItem);

        string oid = ObjectIdentity.Of(retailSale);
        decimal price = retailSale.SalePrice;
        DateTime timeStamp = retailSale.DateSold;
        string expected = oid + " RetailSale: $" + JadeFormat.DecimalString(price) + ", "
            + JadeFormat.TimeStampString(timeStamp) + ", " + retailItem.GetCode() + " (" + retailItem.Name + "), "
            + MyClient.Name;

        string actual = retailSale.GetDebugString();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Sells the fixture retail item, already sold, with no client at 555, sold at
    /// <see cref="TestSaleFixture.Timestamp"/>: no sale is created, the sale is not persisted, and the errors are
    /// exactly <see cref="MessageKeys.ClientRequired"/> then <see cref="MessageKeys.ItemSold"/>.
    /// Ports <c>TestRetailSale.testInvalidClient</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidClient()
    {
        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = MyRetailItem.Id,
            ClientId = null,
            SalePrice = 555m,
            DateSold = Timestamp,
        };

        OperationResult result = await PersistEntityAsync(retailSaleCommand);

        Assert.Null(retailSaleCommand.ModelObject);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientRequired), result.Errors[0]);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemSold), result.Errors[1]);
    }

    /// <summary>
    /// Sells no item to the fixture client at 555, sold at <see cref="TestSaleFixture.Timestamp"/>: no sale is created,
    /// the sale is not persisted, and the only error is <see cref="MessageKeys.ItemRequired"/>.
    /// Ports <c>TestRetailSale.testInvalidItem</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidItem()
    {
        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = null,
            ClientId = MyClient.Id,
            SalePrice = 555m,
            DateSold = Timestamp,
        };

        OperationResult result = await PersistEntityAsync(retailSaleCommand);

        Assert.Null(retailSaleCommand.ModelObject);
        Assert.False(result.Succeeded);
        AssertSingleError(result, MessageKeys.ItemRequired);
    }

    /// <summary>
    /// Creates the retail item 1111 "Retail Item" as <see cref="testCreateOutsideTransStateException"/> does, then sells
    /// it to the fixture client at the sale price 0, sold at <see cref="TestSaleFixture.Timestamp"/>: no sale is
    /// created, the sale is not persisted, and the only error is <see cref="MessageKeys.RetailSalePriceRequired"/>.
    /// Ports <c>TestRetailSale.testInvalidPrice</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidPrice()
    {
        RetailItem retailItem = await CreateRetailItemAsync(
            1111,
            "Retail Item",
            "A full description of the Retail Item",
            55.50m);

        var retailSaleCommand = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = MyClient.Id,
            SalePrice = 0m, // the JADE null sale price
            DateSold = Timestamp,
        };

        OperationResult result = await PersistEntityAsync(retailSaleCommand);

        Assert.Null(retailSaleCommand.ModelObject);
        Assert.False(result.Succeeded);
        AssertSingleError(result, MessageKeys.RetailSalePriceRequired);
    }

    /// <summary>
    /// Asserts that <paramref name="result"/> holds exactly one error, the text of <paramref name="messageKey"/>.
    /// </summary>
    private static void AssertSingleError(OperationResult result, string messageKey) =>
        Assert.Equal(MessageCatalog.Get(messageKey), Assert.Single(result.Errors));

    /// <summary>
    /// Creates a retail item with the given code number, name, description and price, no photo, and the fixture
    /// region, item category and agent, through <see cref="ItemService"/> with <see cref="TransactionType.Persist"/> in
    /// <see cref="TransactionMode.Own"/> mode, and returns it.
    /// </summary>
    private async Task<RetailItem> CreateRetailItemAsync(int codeNumber, string name, string description, decimal price)
    {
        byte[]? photo = null;

        var retailItemCommand = new RetailItemCommand
        {
            CodeNumber = codeNumber,
            Name = name,
            Description = description,
            Photo = photo,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = price,
        };

        OperationResult result = await GetService<ItemService>()
            .ExecuteAsync(retailItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        return Assert.IsType<RetailItem>(retailItemCommand.ModelObject);
    }

    /// <summary>
    /// Runs <paramref name="retailSaleCommand"/> through <see cref="RetailSaleService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Own"/> mode. Ports
    /// <c>persistEntity(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityAsync(RetailSaleCommand retailSaleCommand) =>
        GetService<RetailSaleService>().ExecuteAsync(retailSaleCommand, TransactionType.Persist, TransactionMode.Own);

    /// <summary>
    /// Runs <paramref name="retailSaleCommand"/> through <see cref="RetailSaleService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Ambient"/> mode. Ports
    /// <c>persistEntityInTransState(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityInTransStateAsync(RetailSaleCommand retailSaleCommand) =>
        GetService<RetailSaleService>().ExecuteAsync(retailSaleCommand, TransactionType.Persist, TransactionMode.Ambient);

    /// <summary>Returns whether the database holds a sale of the item with row id <paramref name="itemId"/>.</summary>
    private Task<bool> ItemHasStoredSaleAsync(long itemId) =>
        GetService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .AnyAsync(sale => sale.ItemId == itemId);
}
