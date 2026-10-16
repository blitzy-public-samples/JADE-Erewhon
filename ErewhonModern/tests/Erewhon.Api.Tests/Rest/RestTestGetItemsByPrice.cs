using System.Globalization;
using System.Text.Json;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Ports the JADE test class <c>RestTestGetItemsByPrice</c>: the REST operation <c>getItemsByPrice</c>, called over
/// HTTP as <c>GET /api/rest/items?minPrice=&amp;maxPrice=</c> against the running API. Each test runs over the company
/// and objects of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which is deleted after the test.
/// </summary>
public sealed class RestTestGetItemsByPrice : ErewhonTestBase
{
    /// <summary>
    /// <c>getItemsByPrice(100, -10)</c> returns status 400 with an empty body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidMaxPrice()
    {
        // Arrange/Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, 100, -10);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    /// <summary>
    /// <c>getItemsByPrice(-100, 100)</c> returns status 400 with an empty body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidMinPrice()
    {
        // Arrange/Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, -100, 100);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    /// <summary>
    /// <c>getItemsByPrice(1000, 100)</c>, whose minimum price is above its maximum price, returns status 400 with an
    /// empty body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testMaxPriceHigherThanMinPrice()
    {
        // Arrange/Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, 1000, 100);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    /// <summary>
    /// After every item of the company is removed, <c>getItemsByPrice(100, 1000)</c> returns status 200 with an empty
    /// array.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testNoItems()
    {
        // Arrange
        await PurgeCompanyItemsAsync();

        // Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, 100, 1000);

        using JsonDocument items = await ReadItemsArrayAsync(response);
        Assert.Equal(0, items.RootElement.GetArrayLength());
        Assert.Equal(200, (int)response.StatusCode);
    }

    /// <summary>
    /// <c>getItemsByPrice(800, 900)</c> returns status 200 with one item, the tender item of reserve 888.88, typed
    /// <c>tenderItem</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testPriceBetween800And900()
    {
        // Arrange/Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, 800, 900);

        using JsonDocument items = await ReadItemsArrayAsync(response);
        Assert.Equal(1, items.RootElement.GetArrayLength());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("tenderItem", items.RootElement[0].GetProperty("type").GetString());
    }

    /// <summary>
    /// <c>getItemsByPrice(900, 1000)</c> returns status 200 with one item, the retail item of price 999.99, typed
    /// <c>retailItem</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testPriceBetween900And1000()
    {
        // Arrange/Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, 900, 1000);

        using JsonDocument items = await ReadItemsArrayAsync(response);
        Assert.Equal(1, items.RootElement.GetArrayLength());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retailItem", items.RootElement[0].GetProperty("type").GetString());
    }

    /// <summary>
    /// After the retail item 3 at 900.00, the tender item 4 of reserve 800.00, the retail item 5 at 799.99 and the
    /// retail item 6 at 900.01 are created in that order, <c>getItemsByPrice(minPrice, maxPrice)</c> returns status
    /// 200 with the items whose price lies between <paramref name="minPrice"/> and <paramref name="maxPrice"/>, both
    /// included, in code-number order: 800 to 900 → 2, 3 and 4; 900 to 900 → 3; 800 to 800 → 4; 0 to 0 → none. Item
    /// 2 is the fixture tender item of reserve 888.88.
    /// </summary>
    /// <param name="minPrice">The lowest price requested.</param>
    /// <param name="maxPrice">The highest price requested.</param>
    /// <param name="expectedNumbers">The code numbers of the items returned, in response order.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(800, 900, new[] { 2, 3, 4 })]
    [InlineData(900, 900, new[] { 3 })]
    [InlineData(800, 800, new[] { 4 })]
    [InlineData(0, 0, new int[0])]
    public async Task PriceBandBoundsAreInclusive(int minPrice, int maxPrice, int[] expectedNumbers)
    {
        // Arrange
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        await Builder.CreateTestRetailItemAsync(
            "Upper Bound Item",
            "Description",
            null,
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            900.00m);
        await Builder.CreateTestTenderItemAsync(
            "Lower Bound Item",
            "Description",
            null,
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            800.00m,
            serverDate.AddDays(7));
        await Builder.CreateTestRetailItemAsync(
            "Below Lower Bound Item",
            "Description",
            null,
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            799.99m);
        await Builder.CreateTestRetailItemAsync(
            "Above Upper Bound Item",
            "Description",
            null,
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            900.01m);

        // Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await GetItemsByPriceAsync(http, minPrice, maxPrice);

        using JsonDocument items = await ReadItemsArrayAsync(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(
            expectedNumbers.Select(ExpectedBandItem),
            items.RootElement.EnumerateArray().Select(item => (
                item.GetProperty("number").GetInt32(),
                item.GetProperty("type").GetString(),
                item.GetProperty("price").GetDecimal())));
    }

    /// <summary>
    /// Creates and binds the test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>: among its objects,
    /// the retail item of price 999.99 and the tender item of reserve 888.88. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// Sends <c>GET /api/rest/items?minPrice=&lt;minPrice&gt;&amp;maxPrice=&lt;maxPrice&gt;</c> through
    /// <paramref name="http"/>, with both integers in invariant-culture format.
    /// </summary>
    /// <param name="http">The client addressed to <see cref="ErewhonTestBase.Api"/>.</param>
    /// <param name="minPrice">The lowest price included.</param>
    /// <param name="maxPrice">The highest price included.</param>
    /// <returns>The response.</returns>
    private static Task<HttpResponseMessage> GetItemsByPriceAsync(HttpClient http, int minPrice, int maxPrice) =>
        http.GetAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"/api/rest/items?minPrice={minPrice}&maxPrice={maxPrice}"));

    /// <summary>
    /// Returns the code number, <c>type</c> discriminator and price that <c>getItemsByPrice</c> answers for the item
    /// numbered <paramref name="number"/> in <see cref="PriceBandBoundsAreInclusive"/>: 2 is the tender item of reserve
    /// 888.88, 3 the retail item at 900.00 and 4 the tender item of reserve 800.00.
    /// </summary>
    /// <param name="number">The code number of the item, 2, 3 or 4.</param>
    /// <returns>The code number, type discriminator and price of the item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is not 2, 3 or 4.</exception>
    private static (int Number, string? Type, decimal Price) ExpectedBandItem(int number) => number switch
    {
        2 => (2, "tenderItem", 888.88m),
        3 => (3, "retailItem", 900.00m),
        4 => (4, "tenderItem", 800.00m),
        _ => throw new ArgumentOutOfRangeException(
            nameof(number), number, "Only the items 2, 3 and 4 are expected in a price band."),
    };

    /// <summary>
    /// Reads the body of <paramref name="response"/>, asserts that it is not empty and that it is a JSON array, and
    /// returns it parsed. The caller disposes the document.
    /// </summary>
    /// <param name="response">The response of <c>getItemsByPrice</c>.</param>
    /// <returns>The parsed body, whose root element is an array.</returns>
    private static async Task<JsonDocument> ReadItemsArrayAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(string.Empty, body);

        JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            Assert.Fail($"The response body is not a JSON array: {body}");
        }

        return document;
    }

    /// <summary>
    /// Removes every item of <see cref="TestCompanyBuilder.MyCompany"/> in one transaction of a new scope of
    /// <see cref="ErewhonTestBase.Api"/>, through that scope's <see cref="IUnitOfWork"/> and
    /// <see cref="IErewhonDbContext"/>; the database removes the items' sales and tenders with them.
    /// Ports <c>beginTransaction; app.myCompany.allItems.purge(); commitTransaction</c>.
    /// </summary>
    /// <returns>A task that completes when the removal is committed.</returns>
    /// <exception cref="InvalidOperationException">Called before the test company exists.</exception>
    private async Task PurgeCompanyItemsAsync()
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException($"The test company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();

        await unitOfWork.BeginAsync();
        List<Item> items = await db.Items.Where(item => item.CompanyId == companyId).ToListAsync();
        db.Items.RemoveRange(items);
        await db.SaveChangesAsync();
        await unitOfWork.CommitAsync();
    }
}
