using System.Text.Json;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getAllSales</c>, <c>GET /api/rest/sales</c>, called over HTTP on the test host.
/// Ports the JADE class <c>RestTestGetAllSales</c>.
/// </summary>
public sealed class RestTestGetAllSales : ErewhonTestBase
{
    private const string SalesPath = "/api/rest/sales";

    /// <summary>
    /// Creates and binds the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>: one retail sale and one
    /// tender sale in the category <c>CAT1</c>. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// Calling <c>getAllSales</c> answers 200 with an array of the company's two sales: the retail sale first, typed
    /// <c>retailSale</c>, and the tender sale last, typed <c>tenderSale</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllSales()
    {
        // Arrange
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync(SalesPath);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement sales = body.RootElement;

        Assert.Equal(JsonValueKind.Array, sales.ValueKind);
        Assert.Equal(2, sales.GetArrayLength());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retailSale", sales[0].GetProperty("type").GetString());
        Assert.Equal("tenderSale", sales[sales.GetArrayLength() - 1].GetProperty("type").GetString());
    }

    /// <summary>
    /// After the categories "Alpha Category" (<c>ZED1</c>) and "Bravo Category" (<c>ABC1</c>) are created in that
    /// order, then the retail items 3 in <c>ZED1</c>, 4 in <c>ABC1</c>, 5 in <c>CAT1</c> and 6 in <c>ABC1</c>, and
    /// items 6, 3, 5 and 4 are sold to the fixture client in that order, calling <c>getAllSales</c> answers 200 with
    /// the six sales ordered by item prefix, then item code number: <c>ABC1</c> 4 and 6, <c>CAT1</c> 1 (the retail
    /// sale of "Retail Item"), 2 (the tender sale of "Tender Item") and 5, then <c>ZED1</c> 3.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SalesListedByItemPrefixThenCodeNumber()
    {
        // Arrange
        ItemCategory zedCategory = await Builder.CreateTestItemCategoryAsync(
            "Alpha Category", "ZED1", "The alpha test category");
        ItemCategory abcCategory = await Builder.CreateTestItemCategoryAsync(
            "Bravo Category", "ABC1", "The bravo test category");

        RetailItem zedItem = await CreateRetailItemAsync("ZED1 Item", zedCategory, 103m);
        RetailItem firstAbcItem = await CreateRetailItemAsync("First ABC1 Item", abcCategory, 104m);
        RetailItem catItem = await CreateRetailItemAsync("CAT1 Item", Builder.MyItemCategory!, 105m);
        RetailItem secondAbcItem = await CreateRetailItemAsync("Second ABC1 Item", abcCategory, 106m);

        IServerClock clock = GetService<IServerClock>();
        foreach (RetailItem item in new[] { secondAbcItem, zedItem, catItem, firstAbcItem })
        {
            await Builder.CreateTestRetailSaleAsync(item, Builder.MyClient!, await clock.GetServerTimeStampAsync());
        }

        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync(SalesPath);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement sales = body.RootElement;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(JsonValueKind.Array, sales.ValueKind);
        Assert.Equal(6, sales.GetArrayLength());

        (string? Type, int Number, string? Name)[] expected =
        {
            ("retailSale", 4, "First ABC1 Item"),
            ("retailSale", 6, "Second ABC1 Item"),
            ("retailSale", 1, "Retail Item"),
            ("tenderSale", 2, "Tender Item"),
            ("retailSale", 5, "CAT1 Item"),
            ("retailSale", 3, "ZED1 Item"),
        };
        Assert.Equal(
            expected,
            sales.EnumerateArray().Select(sale => (
                sale.GetProperty("type").GetString(),
                sale.GetProperty("item").GetProperty("number").GetInt32(),
                sale.GetProperty("item").GetProperty("name").GetString())));
    }

    /// <summary>
    /// Creates the retail item <paramref name="name"/> ("Description", no photo) at <paramref name="price"/> in
    /// <paramref name="itemCategory"/>, in the fixture region and sold by the fixture agent.
    /// </summary>
    /// <param name="name">The item name.</param>
    /// <param name="itemCategory">The category of the item.</param>
    /// <param name="price">The price of the item.</param>
    /// <returns>The new retail item, with the next code number of the company.</returns>
    private Task<RetailItem> CreateRetailItemAsync(string name, ItemCategory itemCategory, decimal price) =>
        Builder.CreateTestRetailItemAsync(
            name, "Description", null, Builder.MyRegion!, itemCategory, Builder.MyAgent!, price);
}
