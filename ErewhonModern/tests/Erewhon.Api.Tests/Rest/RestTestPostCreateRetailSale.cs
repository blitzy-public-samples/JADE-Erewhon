using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>postCreateRetailSale</c> at <c>POST /api/rest/retail-sales</c>, called over HTTP.
/// Each test also checks the raw JSON result body: its four properties, status code, complete error list, empty
/// warnings and GUID. Ports the JADE class <c>RestTestPostCreateRetailSale</c>, and adds tests of the stored agent
/// commission and of the rejection of an item that is already sold.
/// </summary>
public sealed class RestTestPostCreateRetailSale : ErewhonTestBase
{
    private const string RetailSalesRoute = "/api/rest/retail-sales";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/> and binds its company. Ports
    /// <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// Posting a retail sale with no client name answers 404 with <see cref="MessageKeys.ClientNotFound"/> first and
    /// leaves the company's two sales unchanged. The raw body holds status code 404,
    /// <see cref="MessageKeys.ClientNotFound"/> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testClientNotValid()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            null,
            Builder.MyItemCategory!.Prefix,
            Builder.MyRetailItem!.CodeNumber);
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNotFound), result.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ClientNotFound)], expectedGuid: null);
    }

    /// <summary>
    /// Posting a retail sale of a new unsold retail item to the fixture client answers 201 with no errors and adds a
    /// third sale to the company: a retail sale of that item to the client in the company at the item's price 555,
    /// with no agent commission, sold between the server timestamps read just before and just after the request. The
    /// raw body holds status code 201, no errors, no warnings and the GUID of that sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateRetailSale()
    {
        // Arrange
        RetailItem retailItem = await Builder.CreateTestRetailItemAsync(
            "Another Retail Item",
            "Description",
            TestPhoto.ToArray(),
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            555m);
        IServerClock clock = GetService<IServerClock>();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        DateTime before = await clock.GetServerTimeStampAsync();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            retailItem.ItemCategory!.Prefix,
            retailItem.CodeNumber);
        DateTime after = await clock.GetServerTimeStampAsync();
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(result);
        Assert.Empty(result.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(3, await SaleCountAsync());
        RetailSale retailSale = await RetailSaleOfItemAsync(retailItem.Id);
        CheckRetailSaleRow(retailSale, retailItem, expectedPrice: 555m, expectedCommission: 0m, before, after);
        await CheckResultBodyAsync(response, [], retailSale.Guid);
    }

    /// <summary>
    /// With a 3.5% commission rate on the fixture category held by the fixture agent, posting a retail sale of a new
    /// unsold retail item priced 555 to the fixture client answers 201 and adds one retail sale of that item to the
    /// client in the company at 555, with the agent commission 19.43, sold between the server timestamps read just
    /// before and just after the request. The raw body holds status code 201, no errors, no warnings and the GUID of
    /// that sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CreateRetailSaleRecordsAgentCommission()
    {
        // Arrange
        CommissionRate rate = await Builder.CreateTestCommissionRateAsync(Builder.MyItemCategory!, 3.5m);
        await Builder.AddTestAgentToCommissionRateAsync(Builder.MyAgent!, rate);
        RetailItem retailItem = await Builder.CreateTestRetailItemAsync(
            "Another Retail Item",
            "Description",
            TestPhoto.ToArray(),
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            555m);
        IServerClock clock = GetService<IServerClock>();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        DateTime before = await clock.GetServerTimeStampAsync();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            retailItem.ItemCategory!.Prefix,
            retailItem.CodeNumber);
        DateTime after = await clock.GetServerTimeStampAsync();

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(3, await SaleCountAsync());
        RetailSale retailSale = await RetailSaleOfItemAsync(retailItem.Id);
        CheckRetailSaleRow(retailSale, retailItem, expectedPrice: 555m, expectedCommission: 19.43m, before, after);
        await CheckResultBodyAsync(response, [], retailSale.Guid);
    }

    /// <summary>
    /// Posting a retail sale of item number 999, which the company does not hold, answers 404 with
    /// <see cref="MessageKeys.ItemNotFound"/> first and leaves the company's two sales unchanged. The raw body holds
    /// status code 404, <see cref="MessageKeys.ItemNotFound"/> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotFound()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            999);
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotFound), result.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ItemNotFound)], expectedGuid: null);
    }

    /// <summary>
    /// Posting a retail sale of the fixture tender item answers 400 with <see cref="MessageKeys.ItemNotRetailItem"/>
    /// first and leaves the company's two sales unchanged. The raw body holds status code 400,
    /// <see cref="MessageKeys.ItemNotRetailItem"/> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotRetailSale()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            Builder.MyTenderItem!.CodeNumber);
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotRetailItem), result.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ItemNotRetailItem)], expectedGuid: null);
    }

    /// <summary>
    /// Posting a retail sale of the fixture retail item, which the fixture retail sale has already sold, to the fixture
    /// client answers 400. The raw body holds status code 400, <see cref="MessageKeys.ItemSold"/> as its only error, no
    /// warnings and a null GUID. The company keeps its two sales, and the fixture retail sale keeps its id, client,
    /// price, agent commission, sold timestamp and GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SoldRetailItemIs400WithItemSold()
    {
        // Arrange
        RetailSale saleBefore = await RetailSaleOfItemAsync(Builder.MyRetailItem!.Id);

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            Builder.MyRetailItem!.CodeNumber);

        Assert.Equal(400, (int)response.StatusCode);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ItemSold)], expectedGuid: null);
        Assert.Equal(2, await SaleCountAsync());
        RetailSale saleAfter = await RetailSaleOfItemAsync(Builder.MyRetailItem!.Id);
        Assert.Equal(Builder.MyRetailSale!.Id, saleBefore.Id);
        Assert.Equal(saleBefore.Id, saleAfter.Id);
        Assert.Equal(saleBefore.ClientId, saleAfter.ClientId);
        Assert.Equal(saleBefore.SalePrice, saleAfter.SalePrice);
        Assert.Equal(saleBefore.AgentCommission, saleAfter.AgentCommission);
        Assert.Equal(saleBefore.DateSold, saleAfter.DateSold);
        Assert.Equal(saleBefore.Guid, saleAfter.Guid);
    }

    /// <summary>
    /// Posts a <see cref="RetailSaleRequest"/> with <paramref name="client"/>, <paramref name="codePrefix"/> and
    /// <paramref name="codeNumber"/> to <c>POST /api/rest/retail-sales</c>.
    /// </summary>
    private static Task<HttpResponseMessage> PostRetailSaleAsync(
        HttpClient http,
        string? client,
        string codePrefix,
        int codeNumber) =>
        http.PostAsJsonAsync(
            RetailSalesRoute,
            new RetailSaleRequest { Client = client, CodePrefix = codePrefix, CodeNumber = codeNumber },
            Json);

    /// <summary>Returns the number of sales of <see cref="TestCompanyBuilder.MyCompany"/>, read in a new scope of the API.</summary>
    private async Task<int> SaleCountAsync()
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Sales
            .AsNoTracking()
            .CountAsync(sale => sale.CompanyId == companyId);
    }

    /// <summary>
    /// Returns the retail sale of the item with id <paramref name="itemId"/>, read untracked in a new scope of the API;
    /// throws <see cref="InvalidOperationException"/> when the item has no retail sale or more than one.
    /// </summary>
    private async Task<RetailSale> RetailSaleOfItemAsync(long itemId)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Sales
            .OfType<RetailSale>()
            .AsNoTracking()
            .SingleAsync(sale => sale.ItemId == itemId);
    }

    /// <summary>
    /// Checks that <paramref name="retailSale"/> sells <paramref name="retailItem"/> to
    /// <see cref="TestCompanyBuilder.MyClient"/> in <see cref="TestCompanyBuilder.MyCompany"/> at the item's price,
    /// which equals <paramref name="expectedPrice"/>, with the agent commission <paramref name="expectedCommission"/>,
    /// and that its sold timestamp lies between <paramref name="before"/> and <paramref name="after"/> inclusive.
    /// </summary>
    private void CheckRetailSaleRow(
        RetailSale retailSale,
        RetailItem retailItem,
        decimal expectedPrice,
        decimal expectedCommission,
        DateTime before,
        DateTime after)
    {
        Assert.Equal(retailItem.Id, retailSale.ItemId);
        Assert.Equal(Builder.MyClient!.Id, retailSale.ClientId);
        Assert.Equal(Builder.MyCompany!.Id, retailSale.CompanyId);
        Assert.Equal(expectedPrice, retailItem.Price);
        Assert.Equal(expectedPrice, retailSale.SalePrice);
        Assert.Equal(expectedCommission, retailSale.AgentCommission);
        Assert.InRange(retailSale.DateSold, before, after);
    }

    /// <summary>
    /// Checks the raw JSON body of <paramref name="response"/>: an object with exactly the properties
    /// <c>statusCode</c>, <c>allErrors</c>, <c>allWarnings</c> and <c>guid</c>, where <c>statusCode</c> is the number
    /// of the HTTP status of <paramref name="response"/>, <c>allErrors</c> holds the strings of
    /// <paramref name="expectedErrors"/> in order and nothing else, <c>allWarnings</c> is an empty array, and
    /// <c>guid</c> is null when <paramref name="expectedGuid"/> is <see langword="null"/> and otherwise the non-empty
    /// string <paramref name="expectedGuid"/>.
    /// </summary>
    private static async Task CheckResultBodyAsync(
        HttpResponseMessage response,
        IReadOnlyList<string> expectedErrors,
        string? expectedGuid)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            new[] { "allErrors", "allWarnings", "guid", "statusCode" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        JsonElement statusCode = root.GetProperty("statusCode");
        Assert.Equal(JsonValueKind.Number, statusCode.ValueKind);
        Assert.Equal((int)response.StatusCode, statusCode.GetInt32());

        JsonElement allErrors = root.GetProperty("allErrors");
        Assert.Equal(JsonValueKind.Array, allErrors.ValueKind);
        Assert.All(allErrors.EnumerateArray(), error => Assert.Equal(JsonValueKind.String, error.ValueKind));
        Assert.Equal(expectedErrors, allErrors.EnumerateArray().Select(error => error.GetString()!));

        JsonElement allWarnings = root.GetProperty("allWarnings");
        Assert.Equal(JsonValueKind.Array, allWarnings.ValueKind);
        Assert.Empty(allWarnings.EnumerateArray());

        JsonElement guid = root.GetProperty("guid");
        if (expectedGuid is null)
        {
            Assert.Equal(JsonValueKind.Null, guid.ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.String, guid.ValueKind);
            string? actualGuid = guid.GetString();
            Assert.NotNull(actualGuid);
            Assert.NotEmpty(actualGuid);
            Assert.Equal(expectedGuid, actualGuid);
        }
    }
}
