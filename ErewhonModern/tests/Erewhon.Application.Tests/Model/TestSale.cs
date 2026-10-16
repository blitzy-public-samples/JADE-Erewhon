using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// The fixture of the JADE unit-test class <c>TestSale</c>, shared by its subclasses <c>TestRetailSale</c> and
/// <c>TestTenderSale</c>: the company "Jade Software Corporation", bound to the test's scope, with the client
/// "Clive Entworth", the agent "John Smith", the country "France" and its region "Paris", the item category
/// "Abstractions" (<c>ABST</c>), the retail item "Proxy Retail" sold to the client at 555, the tender item
/// "Proxy Tender" sold to the client through the client's tender of 20, and a commission rate of 25.00 on the
/// category held by the agent. Declares no tests.
/// </summary>
public abstract class TestSaleFixture : ErewhonTestBase
{
    /// <summary>The fixture company "Jade Software Corporation". Ports <c>myCompany</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Company MyCompany => Builder.MyCompany ?? throw NotBuilt(nameof(MyCompany));

    /// <summary>The fixture client "Clive Entworth". Ports <c>myClient</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Client MyClient => Builder.MyClient ?? throw NotBuilt(nameof(MyClient));

    /// <summary>The fixture agent "John Smith". Ports <c>myAgent</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Agent MyAgent => Builder.MyAgent ?? throw NotBuilt(nameof(MyAgent));

    /// <summary>The fixture country "France". Ports <c>myCountry</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Country MyCountry => Builder.MyCountry ?? throw NotBuilt(nameof(MyCountry));

    /// <summary>The fixture region "Paris" of <see cref="MyCountry"/>. Ports <c>myRegion</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Region MyRegion => Builder.MyRegion ?? throw NotBuilt(nameof(MyRegion));

    /// <summary>The fixture item category "Abstractions" with the prefix <c>ABST</c>. Ports <c>myItemCategory</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected ItemCategory MyItemCategory => Builder.MyItemCategory ?? throw NotBuilt(nameof(MyItemCategory));

    /// <summary>The fixture retail item "Proxy Retail", priced 5.50 and sold by <see cref="MyRetailSale"/>. Ports <c>myRetailItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected RetailItem MyRetailItem => Builder.MyRetailItem ?? throw NotBuilt(nameof(MyRetailItem));

    /// <summary>
    /// The fixture tender item "Proxy Tender", with the reserve price 5.50, closing on the server date and sold by
    /// <see cref="MyTenderSale"/>. Ports <c>myTenderItem</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected TenderItem MyTenderItem => Builder.MyTenderItem ?? throw NotBuilt(nameof(MyTenderItem));

    /// <summary>
    /// The fixture tender of 20 by <see cref="MyClient"/> on <see cref="MyTenderItem"/>, placed at the server timestamp.
    /// Ports <c>myTender</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected Tender MyTender => Builder.MyTender ?? throw NotBuilt(nameof(MyTender));

    /// <summary>
    /// The fixture retail sale of <see cref="MyRetailItem"/> to <see cref="MyClient"/> at 555, sold at the server
    /// timestamp. Ports <c>myRetailSale</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected RetailSale MyRetailSale => Builder.MyRetailSale ?? throw NotBuilt(nameof(MyRetailSale));

    /// <summary>
    /// The fixture tender sale of <see cref="MyTenderItem"/> to <see cref="MyClient"/> through <see cref="MyTender"/>.
    /// Ports <c>myTenderSale</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected TenderSale MyTenderSale => Builder.MyTenderSale ?? throw NotBuilt(nameof(MyTenderSale));

    /// <summary>
    /// The fixture commission rate of 25.00 on <see cref="MyItemCategory"/>, held by <see cref="MyAgent"/> and created
    /// after both fixture sales. Ports <c>myCommissionRate</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created it.</exception>
    protected CommissionRate MyCommissionRate => Builder.MyCommissionRate ?? throw NotBuilt(nameof(MyCommissionRate));

    /// <summary>
    /// The day before the server date, at 00:00:00.000, with <see cref="DateTimeKind.Unspecified"/>; set at the end of
    /// <see cref="SetUpAsync"/>. Ports the <c>timestamp</c> attribute.
    /// </summary>
    protected DateTime Timestamp { get; private set; }

    /// <summary>
    /// Builds the fixtures in the order of <c>TestSale.setUp</c>, each through <see cref="TestCompanyBuilder"/> and the
    /// production services: the company, bound to the test's scope; the client, the agent, the country and its region;
    /// the item category "Abstractions" with the prefix given as <c>Abst</c>; the retail item and the tender item; the
    /// tender, the retail sale and the tender sale; the commission rate; and then <see cref="Timestamp"/>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    /// <exception cref="InvalidOperationException">A fixture was not persisted.</exception>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyClient = await Builder.CreateClientAsync();
        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyCountry = await Builder.CreateCountryAsync();
        Builder.MyRegion = await Builder.CreateRegionAsync(MyCountry);
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Abstractions",
            "Abst",
            "Items that do not model real world observables");
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(MyRegion, MyItemCategory, MyAgent);
        Builder.MyTenderItem = await Builder.CreateTenderItemAsync(MyRegion, MyItemCategory, MyAgent);
        Builder.MyTender = await Builder.CreateTenderAsync(MyClient, MyTenderItem, 20m);
        Builder.MyRetailSale = await Builder.CreateRetailSaleAsync(MyRetailItem, MyClient);
        Builder.MyTenderSale = await Builder.CreateTenderSaleAsync(MyTenderItem, MyTender, MyClient);
        Builder.MyCommissionRate = await Builder.CreateCommissionRateAsync(MyItemCategory, 25.00m, MyAgent);

        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();
        Timestamp = date.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
    }

    /// <summary>Returns the exception for the fixture <paramref name="fixture"/> read before it was created.</summary>
    private static InvalidOperationException NotBuilt(string fixture) =>
        new($"{fixture} is available once {nameof(SetUpAsync)} has created it.");
}

/// <summary>Ports the JADE unit-test class <c>TestSale</c> over the fixture of <see cref="TestSaleFixture"/>.</summary>
public sealed class TestSale : TestSaleFixture
{
    /// <summary>
    /// The company's sales, in the order of its sales by item, are the fixture's two sales, and the first and the last
    /// are both dated with the server date. Ports <c>TestSale.testGetDate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDate()
    {
        DateOnly date = await GetService<IServerClock>().GetServerDateAsync();

        // Arrange / act
        List<Sale> sales = await LoadSalesByItemAsync();

        // Assert
        Assert.Equal(2, sales.Count);
        Assert.Equal(date, sales[0].GetDate());
        Assert.Equal(date, sales[^1].GetDate());
    }

    /// <summary>
    /// Reads the sales of <see cref="TestSaleFixture.MyCompany"/> from the database without change tracking, each with
    /// its item and the item's category and, for a tender sale, its tender, ordered by the prefix of the item's category
    /// (ordinal comparison) and then by the item's code number.
    /// </summary>
    /// <returns>The company's sales in that order.</returns>
    private async Task<List<Sale>> LoadSalesByItemAsync()
    {
        long companyId = MyCompany.Id;

        List<Sale> sales = await GetService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .Include(sale => sale.Item).ThenInclude(item => item!.ItemCategory)
            .Include(sale => ((TenderSale)sale).Tender)
            .Where(sale => sale.CompanyId == companyId)
            .ToListAsync();

        return sales
            .OrderBy(sale => sale.Item?.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(sale => sale.Item?.CodeNumber)
            .ToList();
    }
}
