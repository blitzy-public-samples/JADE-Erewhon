using Erewhon.Application.Domain;
using Erewhon.Testing;

namespace Erewhon.Application.Tests.View;

/// <summary>
/// The sale-item fixture shared by the view sale-item test classes: a company bound for the test, the client
/// "Clive Entworth", the agent "John Smith", the region "Paris" in "France", the item category "Category Item"
/// ("CATI"), the tender item "Proxy Tender" with a tender of 500 by the client, and the retail item "Proxy Retail".
/// Ports the JADE fixture class <c>TestViewSaleItem</c>.
/// </summary>
public abstract class TestViewSaleItem : ErewhonTestBase
{
    /// <summary>The fixture company. Ports <c>myCompany</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Company MyCompany => Fixture(Builder.MyCompany, nameof(MyCompany));

    /// <summary>The fixture client "Clive Entworth". Ports <c>myClient</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Client MyClient => Fixture(Builder.MyClient, nameof(MyClient));

    /// <summary>The fixture agent "John Smith". Ports <c>myAgent</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Agent MyAgent => Fixture(Builder.MyAgent, nameof(MyAgent));

    /// <summary>The fixture region "Paris" in "France". Ports <c>myRegion</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Region MyRegion => Fixture(Builder.MyRegion, nameof(MyRegion));

    /// <summary>The fixture item category "Category Item" with the prefix "CATI". Ports <c>myItemCategory</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected ItemCategory MyItemCategory => Fixture(Builder.MyItemCategory, nameof(MyItemCategory));

    /// <summary>The fixture tender item "Proxy Tender", reserve 5.50, closing today. Ports <c>myTenderItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected TenderItem MyTenderItem => Fixture(Builder.MyTenderItem, nameof(MyTenderItem));

    /// <summary>The fixture tender of 500 by <see cref="MyClient"/> on <see cref="MyTenderItem"/>. Ports <c>myTender</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected Tender MyTender => Fixture(Builder.MyTender, nameof(MyTender));

    /// <summary>The fixture retail item "Proxy Retail", priced 5.50. Ports <c>myRetailItem</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> set it.</exception>
    protected RetailItem MyRetailItem => Fixture(Builder.MyRetailItem, nameof(MyRetailItem));

    /// <summary>
    /// Creates and binds the company, then creates, in this order, the client, the agent, the country "France" and its
    /// region "Paris", the item category "Category Item" ("CATI", "Some Category Description"), the tender item, a
    /// tender of 500 by the client on the tender item, and the retail item, each through
    /// <see cref="TestCompanyBuilder"/>, and sets the matching <c>My*</c> property of <see cref="ErewhonTestBase.Builder"/>.
    /// Ports <c>TestViewSaleItem.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when every fixture object exists.</returns>
    /// <exception cref="InvalidOperationException">A fixture object was not persisted.</exception>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Client client = await Builder.CreateClientAsync();
        Builder.MyClient = client;

        Agent agent = await Builder.CreateAgentAsync();
        Builder.MyAgent = agent;

        Country country = await Builder.CreateCountryAsync();
        Builder.MyCountry = country;
        Region region = await Builder.CreateRegionAsync(country);
        Builder.MyRegion = region;

        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Category Item", "CATI", "Some Category Description");
        Builder.MyItemCategory = itemCategory;

        TenderItem tenderItem = await Builder.CreateTenderItemAsync(region, itemCategory, agent);
        Builder.MyTenderItem = tenderItem;

        Builder.MyTender = await Builder.CreateTenderAsync(client, tenderItem, 500m);

        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(region, itemCategory, agent);
    }

    /// <summary>Returns <paramref name="value"/>, or throws when it is not set.</summary>
    /// <typeparam name="T">The type of the fixture object.</typeparam>
    /// <param name="value">The fixture object held by <see cref="ErewhonTestBase.Builder"/>.</param>
    /// <param name="name">The name of the accessor that reads it.</param>
    /// <returns><paramref name="value"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is <see langword="null"/>.</exception>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"{name} is available once {nameof(SetUpAsync)} has created it.");
}

/// <summary>
/// Tests of the retail item's offer check on the <see cref="TestViewSaleItem"/> fixture.
/// Ports the JADE class <c>TestViewRetailSaleItem</c>.
/// </summary>
public sealed class TestViewRetailSaleItem : TestViewSaleItem
{
    /// <summary>The retail item accepts the offers null (0), -1, 0 and 1.</summary>
    [Fact]
    public void testCheckOffer()
    {
        // Assert
        Assert.True(MyRetailItem.CheckOffer(0m));
        Assert.True(MyRetailItem.CheckOffer(-1m));
        Assert.True(MyRetailItem.CheckOffer(0m));
        Assert.True(MyRetailItem.CheckOffer(1m));
    }
}
