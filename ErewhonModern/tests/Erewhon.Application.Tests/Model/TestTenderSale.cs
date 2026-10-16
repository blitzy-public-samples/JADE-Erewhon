using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Tests of tender sale creation and validation through <see cref="TenderSaleService"/>, and of the
/// <see cref="TenderSale"/> accessors, over the fixture of <see cref="TestSaleFixture"/>.
/// Ports the JADE class <c>TestTenderSale</c>.
/// </summary>
public sealed class TestTenderSale : TestSaleFixture
{
    /// <summary>
    /// Creates the tender item 2222 "Tender Item" of the fixture region, category and agent, then creates a tender sale
    /// of it to the fixture client through the fixture tender in <see cref="TransactionMode.Ambient"/> mode with no
    /// active transaction: <see cref="NotInTransactionStateException"/> with error code 1048 is raised and the new item
    /// has no sale. Ports <c>TestTenderSale.testCreateOutsideTransStateException</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateOutsideTransStateException()
    {
        const int UpdateOutsideTransaction = 1048;

        // Arrange
        TenderItem tenderItem = await CreateTenderItemClosingInSevenDaysAsync();

        var tenderSaleCommand = new TenderSaleCommand
        {
            ItemId = tenderItem.Id,
            ClientId = MyClient.Id,
            TenderId = MyTender.Id,
        };

        // Act
        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistEntityInTransStateAsync(tenderSaleCommand));

        // Assert
        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Assert.False(await ItemHasSaleAsync(tenderItem.Id));
    }

    /// <summary>
    /// The date of the fixture tender sale is the server date. Ports <c>TestTenderSale.testGetDate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDate()
    {
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();
        TenderSale tenderSale = await LoadMyTenderSaleAsync();

        Assert.Equal(date, tenderSale.GetDate());
    }

    /// <summary>
    /// The debug string of the fixture tender sale is its object identity, " TenderSale: $", the winning offer to 2
    /// decimal places, the time tendered as <c>dd MMMM yyyy, HH:mm:ss</c>, and the item's code followed by the item's
    /// name in parentheses, then the fixture client's name, separated by ", ".
    /// Ports <c>TestTenderSale.testGetDebugString</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        // Arrange
        TenderSale tenderSale = await LoadMyTenderSaleAsync();
        Assert.NotNull(tenderSale.Tender);
        Assert.NotNull(tenderSale.Item);

        string oid = ObjectIdentity.Of(tenderSale);
        decimal price = tenderSale.Tender.Offer;
        DateTime timeStamp = tenderSale.Tender.DateTendered;
        Item item = tenderSale.Item;

        // Act
        string expected = oid + " TenderSale: $" + JadeFormat.DecimalString(price) + ", "
            + JadeFormat.DateString(DateOnly.FromDateTime(timeStamp)) + ", "
            + JadeFormat.TimeString(TimeOnly.FromDateTime(timeStamp)) + ", "
            + item.GetCode() + " (" + item.Name + "), " + MyClient.Name;
        string actual = tenderSale.GetDebugString();

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The time of the fixture tender sale is the time-of-day part of the fixture tender's time tendered.
    /// Ports <c>TestTenderSale.testGetTime</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetTime()
    {
        // Arrange
        TimeOnly time = TimeOnly.FromDateTime(MyTender.DateTendered);
        TenderSale tenderSale = await LoadMyTenderSaleAsync();

        // Assert
        Assert.Equal(time, tenderSale.GetTime());
    }

    /// <summary>
    /// Creates a tender sale with no item, the fixture tender and the fixture client: no tender sale is created and the
    /// only error is <see cref="MessageKeys.ItemRequired"/>. Ports <c>TestTenderSale.testInvalidItem</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidItem()
    {
        // Arrange
        var tenderSaleCommand = new TenderSaleCommand
        {
            ItemId = null,
            TenderId = MyTender.Id,
            ClientId = MyClient.Id,
        };

        // Act
        OperationResult result = await PersistEntityAsync(tenderSaleCommand);

        // Assert
        Assert.Null(tenderSaleCommand.ModelObject);
        Assert.False(result.Succeeded);
        AssertSingleError(result, MessageKeys.ItemRequired);
    }

    /// <summary>
    /// Creates the tender item 2222 "Tender Item" of the fixture region, category and agent, then creates a tender sale
    /// of it to the fixture client with no tender: no tender sale is created and the only error is
    /// <see cref="MessageKeys.TenderRequired"/>. Ports <c>TestTenderSale.testInvalidTender</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidTender()
    {
        // Arrange
        TenderItem tenderItem = await CreateTenderItemClosingInSevenDaysAsync();

        var tenderSaleCommand = new TenderSaleCommand
        {
            ItemId = tenderItem.Id,
            TenderId = null,
            ClientId = MyClient.Id,
        };

        // Act
        OperationResult result = await PersistEntityAsync(tenderSaleCommand);

        // Assert
        Assert.Null(tenderSaleCommand.ModelObject);
        Assert.False(result.Succeeded);
        AssertSingleError(result, MessageKeys.TenderRequired);
    }

    /// <summary>
    /// Asserts that <paramref name="result"/> holds exactly one error, the text of <paramref name="messageKey"/>.
    /// </summary>
    private static void AssertSingleError(OperationResult result, string messageKey) =>
        Assert.Equal(MessageCatalog.Get(messageKey), Assert.Single(result.Errors));

    /// <summary>
    /// Creates, through <see cref="ItemService"/> with <see cref="TransactionType.Persist"/> in
    /// <see cref="TransactionMode.Own"/> mode, the tender item 2222 "Tender Item", "A full description of the Tender
    /// Item", with no photo, in the fixture region, category and agent, with the reserve price 55.50 and a closure date
    /// seven days after the server date, and returns it.
    /// </summary>
    private async Task<TenderItem> CreateTenderItemClosingInSevenDaysAsync()
    {
        byte[]? photo = null;
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        var tenderItemCommand = new TenderItemCommand
        {
            CodeNumber = 2222,
            Name = "Tender Item",
            Description = "A full description of the Tender Item",
            Photo = photo,
            RegionId = MyRegion.Id,
            ItemCategoryId = MyItemCategory.Id,
            AgentId = MyAgent.Id,
            Price = 55.50m,
            ClosureDate = date.AddDays(7),
        };

        OperationResult result = await GetService<ItemService>()
            .ExecuteAsync(tenderItemCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        return Assert.IsType<TenderItem>(tenderItemCommand.ModelObject);
    }

    /// <summary>
    /// Returns <see cref="TestSaleFixture.MyTenderSale"/> as <see cref="TenderSaleService.FindByIdAsync"/> reads it,
    /// with its winning tender, client, and item with the item's category.
    /// </summary>
    private async Task<TenderSale> LoadMyTenderSaleAsync()
    {
        TenderSale? tenderSale = await GetService<TenderSaleService>().FindByIdAsync(MyTenderSale.Id);
        Assert.NotNull(tenderSale);
        return tenderSale;
    }

    /// <summary>
    /// Runs <paramref name="tenderSaleCommand"/> through <see cref="TenderSaleService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Own"/> mode. Ports
    /// <c>persistEntity(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityAsync(TenderSaleCommand tenderSaleCommand) =>
        GetService<TenderSaleService>().ExecuteAsync(tenderSaleCommand, TransactionType.Persist, TransactionMode.Own);

    /// <summary>
    /// Runs <paramref name="tenderSaleCommand"/> through <see cref="TenderSaleService"/> with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Ambient"/> mode. Ports
    /// <c>persistEntityInTransState(TransactionType_Persist)</c>.
    /// </summary>
    private Task<OperationResult> PersistEntityInTransStateAsync(TenderSaleCommand tenderSaleCommand) =>
        GetService<TenderSaleService>().ExecuteAsync(tenderSaleCommand, TransactionType.Persist, TransactionMode.Ambient);

    /// <summary>Returns whether the database holds a sale of the item with row id <paramref name="itemId"/>.</summary>
    private Task<bool> ItemHasSaleAsync(long itemId) =>
        GetService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .AnyAsync(sale => sale.ItemId == itemId);
}
