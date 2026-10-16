using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// The per-test fixture of the JADE <c>TestItem</c> class and of the test classes derived from it, built through the
/// production services before each test: the bound company "Jade Software Corporation", the client "Clive Entworth",
/// the agent "John Smith", the country "France" and its region "Paris", the item category "Abstractions" ("ABST"), the
/// retail item "Proxy Retail" and the tender item "Proxy Tender" (both priced 5.50, code numbers 1 and 2), a tender of
/// 20 by the client on the tender item, a retail sale of the retail item to the client and a tender sale of the tender
/// item to the client through the tender. Ports <c>TestItem.setUp</c>; the company and everything it owns are deleted
/// after each test, porting <c>TestItem.tearDown</c>. Declares no tests; <see cref="TestItem"/>,
/// <c>TestRetailItem</c> and <c>TestTenderItem</c> derive from it.
/// </summary>
public abstract class TestItemFixture : ErewhonTestBase
{
    /// <summary>The bound fixture company. Ports <c>myCompany</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Company MyCompany => Builder.MyCompany ?? throw NotSetUp(nameof(MyCompany));

    /// <summary>The fixture client "Clive Entworth". Ports <c>myClient</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Client MyClient => Builder.MyClient ?? throw NotSetUp(nameof(MyClient));

    /// <summary>The fixture agent "John Smith". Ports <c>myAgent</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Agent MyAgent => Builder.MyAgent ?? throw NotSetUp(nameof(MyAgent));

    /// <summary>The fixture country "France". Ports <c>myCountry</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Country MyCountry => Builder.MyCountry ?? throw NotSetUp(nameof(MyCountry));

    /// <summary>The fixture region "Paris" of <see cref="MyCountry"/>. Ports <c>myRegion</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Region MyRegion => Builder.MyRegion ?? throw NotSetUp(nameof(MyRegion));

    /// <summary>The fixture item category "Abstractions" with prefix "ABST". Ports <c>myItemCategory</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected ItemCategory MyItemCategory => Builder.MyItemCategory ?? throw NotSetUp(nameof(MyItemCategory));

    /// <summary>The fixture retail item "Proxy Retail", sold by <see cref="MyRetailSale"/>. Ports <c>myRetailItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected RetailItem MyRetailItem => Builder.MyRetailItem ?? throw NotSetUp(nameof(MyRetailItem));

    /// <summary>The fixture tender item "Proxy Tender", sold by <see cref="MyTenderSale"/>. Ports <c>myTenderItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected TenderItem MyTenderItem => Builder.MyTenderItem ?? throw NotSetUp(nameof(MyTenderItem));

    /// <summary>The fixture tender of 20 by <see cref="MyClient"/> on <see cref="MyTenderItem"/>. Ports <c>myTender</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Tender MyTender => Builder.MyTender ?? throw NotSetUp(nameof(MyTender));

    /// <summary>The fixture retail sale of <see cref="MyRetailItem"/> to <see cref="MyClient"/>. Ports <c>myRetailSale</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected RetailSale MyRetailSale => Builder.MyRetailSale ?? throw NotSetUp(nameof(MyRetailSale));

    /// <summary>
    /// The fixture tender sale of <see cref="MyTenderItem"/> to <see cref="MyClient"/> through <see cref="MyTender"/>.
    /// Ports <c>myTenderSale</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected TenderSale MyTenderSale => Builder.MyTenderSale ?? throw NotSetUp(nameof(MyTenderSale));

    /// <summary>
    /// Creates and binds the company, then creates, in this order, the client, the agent, the country, the region, the
    /// item category, the retail item, the tender item, the tender, the retail sale and the tender sale, setting each
    /// <c>My*</c> property of <see cref="ErewhonTestBase.Builder"/>. Ports <c>TestItem.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when every fixture object is persisted.</returns>
    /// <exception cref="InvalidOperationException">A fixture object was not persisted.</exception>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyClient = await Builder.CreateClientAsync();
        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyCountry = await Builder.CreateCountryAsync();
        Builder.MyRegion = await Builder.CreateRegionAsync(MyCountry);
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Abstractions",
            "ABST",
            "Items that do not model real world observables");
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(MyRegion, MyItemCategory, MyAgent);
        Builder.MyTenderItem = await Builder.CreateTenderItemAsync(MyRegion, MyItemCategory, MyAgent);
        Builder.MyTender = await Builder.CreateTenderAsync(MyClient, MyTenderItem, 20m);
        Builder.MyRetailSale = await Builder.CreateRetailSaleAsync(MyRetailItem, MyClient);
        Builder.MyTenderSale = await Builder.CreateTenderSaleAsync(MyTenderItem, MyTender, MyClient);
    }

    /// <summary>Returns the exception for the fixture <paramref name="member"/> read before it was set.</summary>
    private static InvalidOperationException NotSetUp(string member) =>
        new($"{member} is available once {nameof(SetUpAsync)} has set it.");
}

/// <summary>
/// Ports the JADE unit tests of <c>TestItem</c>: the price and the kind of each item of the fixture company, read in
/// code-number order.
/// </summary>
public sealed class TestItem : TestItemFixture
{
    /// <summary>The company holds 2 items, and each item's <see cref="Item.GetPrice"/> is its price.</summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testGetPrice()
    {
        List<Item> items = await LoadCompanyItemsAsync();

        Assert.Equal(2, items.Count);
        foreach (Item item in items)
        {
            Assert.Equal(item.Price, item.GetPrice());
        }
    }

    /// <summary>
    /// The company holds 2 items; the first in code-number order is a <see cref="RetailItem"/> and the last a
    /// <see cref="TenderItem"/>.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testGetTypeString()
    {
        List<Item> items = await LoadCompanyItemsAsync();
        Item? firstItem = items.FirstOrDefault();
        Item? lastItem = items.LastOrDefault();

        Assert.Equal(2, items.Count);
        Assert.IsType<RetailItem>(firstItem, exactMatch: false);
        Assert.IsType<TenderItem>(lastItem, exactMatch: false);
    }

    /// <summary>Reads the items of <see cref="TestItemFixture.MyCompany"/> from the database in code-number order.</summary>
    private async Task<List<Item>> LoadCompanyItemsAsync()
    {
        long companyId = MyCompany.Id;

        return await GetService<IErewhonDbContext>()
            .Items
            .AsNoTracking()
            .Where(item => item.CompanyId == companyId)
            .OrderBy(item => item.CodeNumber)
            .ToListAsync();
    }
}
