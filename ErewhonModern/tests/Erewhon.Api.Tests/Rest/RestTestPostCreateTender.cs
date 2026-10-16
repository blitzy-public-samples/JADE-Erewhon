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
/// Ports the JADE test class <c>RestTestPostCreateTender</c>: the REST operation <c>postCreateTender</c>, called over
/// HTTP as <c>POST /api/rest/tenders</c> against the running Erewhon API. Each test starts with the
/// <c>createTestData</c> company bound and ends with no company. Each test also checks the raw JSON result body (its
/// four properties, status code, complete error list, empty warnings and GUID) and the company's stored tenders. Adds
/// tests of the rejections of a sold item, a closed item and a repeated own offer.
/// </summary>
public sealed class RestTestPostCreateTender : ErewhonTestBase
{
    private const string TendersRoute = "/api/rest/tenders";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>postCreateTender</c> with no client name answers 404 with the error <c>$ClientNotFound</c> and creates no
    /// sale and no tender. The raw body holds status code 404, <c>$ClientNotFound</c> as its only error, no warnings
    /// and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testClientNotValid()
    {
        // Arrange / Act
        List<long> tenderIdsBefore = await TenderIdsAsync();
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            null,
            Builder.MyItemCategory!.Prefix,
            Builder.MyTenderItem!.CodeNumber,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNotFound), dtoResults.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ClientNotFound)], expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on a further tender item, offering its reserve plus 10, answers
    /// 201 with no errors. The company gains exactly one tender, on that item by the fixture client for 121, placed
    /// between the server timestamps read just before and just after the request; its other tenders and its sales stay
    /// unchanged. The raw body holds status code 201, no errors, no warnings and the GUID of the new tender.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateTender()
    {
        // Arrange
        TenderItem tenderItem = await CreateAnotherTenderItemAsync();
        List<long> tenderIdsBefore = await TenderIdsAsync();
        List<long> saleIdsBefore = await SaleIdsAsync();
        IServerClock clock = GetService<IServerClock>();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        DateTime before = await clock.GetServerTimeStampAsync();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price + 10m);
        DateTime after = await clock.GetServerTimeStampAsync();
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.Empty(dtoResults.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
        List<Tender> tendersAfter = await CompanyTendersAsync();
        Tender newTender = Assert.Single(tendersAfter, tender => !tenderIdsBefore.Contains(tender.Id));
        Assert.Equal(
            tenderIdsBefore,
            tendersAfter.Where(tender => tender.Id != newTender.Id).Select(tender => tender.Id));
        Assert.Equal(tenderItem.Id, newTender.TenderItemId);
        Assert.Equal(Builder.MyClient!.Id, newTender.ClientId);
        Assert.Equal(Builder.MyCompany!.Id, newTender.CompanyId);
        Assert.Equal(121m, newTender.Offer);
        Assert.InRange(newTender.DateTendered, before, after);
        Assert.Equal(saleIdsBefore, await SaleIdsAsync());
        await CheckResultBodyAsync(response, [], newTender.Guid);
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on the code number 999, which no item holds, answers 404 with the
    /// error <c>$ItemNotFound</c> and creates no sale and no tender. The raw body holds status code 404,
    /// <c>$ItemNotFound</c> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotFound()
    {
        // Arrange / Act
        List<long> tenderIdsBefore = await TenderIdsAsync();
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            999,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotFound), dtoResults.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ItemNotFound)], expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on the code of the fixture retail item answers 400 with the error
    /// <c>$ItemNotTenderItem</c> and creates no sale and no tender. The raw body holds status code 400,
    /// <c>$ItemNotTenderItem</c> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotTenderItem()
    {
        // Arrange / Act
        List<long> tenderIdsBefore = await TenderIdsAsync();
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            Builder.MyRetailItem!.CodeNumber,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotTenderItem), dtoResults.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.ItemNotTenderItem)], expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on a further tender item, offering its reserve less 10, answers
    /// 400 with the first error <c>$TenderOfferLessThanReserve</c> and creates no sale and no tender. The raw body
    /// holds status code 400, <c>$TenderOfferLessThanReserve</c> as its only error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testOfferLowerThanReserve()
    {
        // Arrange
        TenderItem tenderItem = await CreateAnotherTenderItemAsync();
        List<long> tenderIdsBefore = await TenderIdsAsync();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price - 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.TenderOfferLessThanReserve), dtoResults.AllErrors[0]);
        await CheckResultBodyAsync(
            response,
            [MessageCatalog.Get(MessageKeys.TenderOfferLessThanReserve)],
            expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
    }

    /// <summary>
    /// A tender by the fixture client on the fixture tender item, which the fixture tender sale has already sold,
    /// offering its reserve plus 10, answers 400. The raw body holds status code 400,
    /// <c>$TenderItemAlreadySold</c> as its only error, no warnings and a null GUID. The company keeps its tenders and
    /// its two sales.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SoldTenderItemIs400WithTenderItemAlreadySold()
    {
        // Arrange
        List<long> tenderIdsBefore = await TenderIdsAsync();
        List<long> saleIdsBefore = await SaleIdsAsync();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            Builder.MyTenderItem!.CodeNumber,
            Builder.MyTenderItem!.Price + 10m);

        Assert.Equal(400, (int)response.StatusCode);
        await CheckResultBodyAsync(
            response,
            [MessageCatalog.Get(MessageKeys.TenderItemAlreadySold)],
            expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(saleIdsBefore, await SaleIdsAsync());
    }

    /// <summary>
    /// A tender by the fixture client on a new tender item with reserve 111 whose closure date is the day before the
    /// server date, offering its reserve plus 10, answers 400. The raw body holds status code 400,
    /// <c>$TenderItemAlreadyClosed</c> as its only error, no warnings and a null GUID. The company keeps its tenders
    /// and its two sales.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClosedTenderItemIs400WithTenderItemAlreadyClosed()
    {
        // Arrange
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        TenderItem tenderItem = await Builder.CreateTestTenderItemAsync(
            "Closed Tender Item",
            "Description",
            TestPhoto.ToArray(),
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            111m,
            serverDate.AddDays(-1));
        List<long> tenderIdsBefore = await TenderIdsAsync();
        List<long> saleIdsBefore = await SaleIdsAsync();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price + 10m);

        Assert.Equal(400, (int)response.StatusCode);
        await CheckResultBodyAsync(
            response,
            [MessageCatalog.Get(MessageKeys.TenderItemAlreadyClosed)],
            expectedGuid: null);
        Assert.Equal(tenderIdsBefore, await TenderIdsAsync());
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(saleIdsBefore, await SaleIdsAsync());
    }

    /// <summary>
    /// The fixture client tenders 121 on a further tender item with reserve 111, which answers 201 and adds one tender,
    /// then tenders the same offer on the same item again, which answers 400. The raw body of the second response
    /// holds status code 400, <c>$TenderAlreadyPlaced</c> once as its only error, no warnings and a null GUID. The
    /// second request leaves the company's tenders as the first left them and its two sales unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RepeatedOwnOfferIs400WithTenderAlreadyPlaced()
    {
        // Arrange
        TenderItem tenderItem = await CreateAnotherTenderItemAsync();
        List<long> tenderIdsBefore = await TenderIdsAsync();
        List<long> saleIdsBefore = await SaleIdsAsync();
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage firstResponse = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price + 10m);
        Assert.Equal(201, (int)firstResponse.StatusCode);
        Tender firstTender = Assert.Single(
            await CompanyTendersAsync(),
            tender => !tenderIdsBefore.Contains(tender.Id));
        await CheckResultBodyAsync(firstResponse, [], firstTender.Guid);
        List<long> tenderIdsAfterFirst = await TenderIdsAsync();

        // Act
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price + 10m);

        Assert.Equal(400, (int)response.StatusCode);
        await CheckResultBodyAsync(
            response,
            [MessageCatalog.Get(MessageKeys.TenderAlreadyPlaced)],
            expectedGuid: null);
        Assert.Equal(tenderIdsAfterFirst, await TenderIdsAsync());
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(saleIdsBefore, await SaleIdsAsync());
    }

    /// <summary>
    /// Creates the <c>createTestData</c> company "Jade Software Test Company" and its fixtures through
    /// <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds it. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// Posts a <see cref="TenderRequest"/> with the given client name, item code and offer to
    /// <c>POST /api/rest/tenders</c>. Ports <c>erewhonRestService.postCreateTender</c>.
    /// </summary>
    /// <param name="http">The client addressed to <see cref="ErewhonTestBase.Api"/>.</param>
    /// <param name="client">The client name, or <see langword="null"/> for none.</param>
    /// <param name="prefix">The category prefix of the item code.</param>
    /// <param name="number">The code number of the item.</param>
    /// <param name="offer">The amount offered.</param>
    /// <returns>The HTTP response; the caller disposes it.</returns>
    private static Task<HttpResponseMessage> PostTenderAsync(
        HttpClient http,
        string? client,
        string prefix,
        int number,
        decimal offer) =>
        http.PostAsJsonAsync(
            TendersRoute,
            new TenderRequest { Client = client, CodePrefix = prefix, CodeNumber = number, Offer = offer },
            Json);

    /// <summary>
    /// Returns the number of sales of <see cref="TestCompanyBuilder.MyCompany"/>, read untracked in a new scope of
    /// <see cref="ErewhonTestBase.Api"/>. Ports <c>app.myCompany.allSalesByItem.size()</c>.
    /// </summary>
    /// <returns>The number of retail and tender sales of the company.</returns>
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
    /// Returns the tenders of <see cref="TestCompanyBuilder.MyCompany"/> in id order, read untracked in a new scope of
    /// <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>The tenders of the company.</returns>
    private async Task<List<Tender>> CompanyTendersAsync()
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Tenders
            .AsNoTracking()
            .Where(tender => tender.CompanyId == companyId)
            .OrderBy(tender => tender.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the ids of the tenders of <see cref="TestCompanyBuilder.MyCompany"/> in ascending order, read as
    /// <see cref="CompanyTendersAsync"/> reads them.
    /// </summary>
    /// <returns>The tender ids of the company.</returns>
    private async Task<List<long>> TenderIdsAsync() =>
        (await CompanyTendersAsync()).Select(tender => tender.Id).ToList();

    /// <summary>
    /// Returns the ids of the retail and tender sales of <see cref="TestCompanyBuilder.MyCompany"/> in ascending order,
    /// read untracked in a new scope of <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>The sale ids of the company.</returns>
    private async Task<List<long>> SaleIdsAsync()
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Sales
            .AsNoTracking()
            .Where(sale => sale.CompanyId == companyId)
            .OrderBy(sale => sale.Id)
            .Select(sale => sale.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Checks the raw JSON body of <paramref name="response"/>: an object with exactly the properties
    /// <c>statusCode</c>, <c>allErrors</c>, <c>allWarnings</c> and <c>guid</c>, where <c>statusCode</c> is the number
    /// of the HTTP status of <paramref name="response"/>, <c>allErrors</c> holds the strings of
    /// <paramref name="expectedErrors"/> in order and nothing else, <c>allWarnings</c> is an empty array, and
    /// <c>guid</c> is null when <paramref name="expectedGuid"/> is <see langword="null"/> and otherwise the non-empty
    /// string <paramref name="expectedGuid"/>.
    /// </summary>
    /// <param name="response">The response of <c>POST /api/rest/tenders</c>.</param>
    /// <param name="expectedErrors">The complete list of error messages, in order.</param>
    /// <param name="expectedGuid">The GUID of the created tender, or <see langword="null"/> for none.</param>
    /// <returns>A task that completes when the body is checked.</returns>
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

    /// <summary>
    /// Creates the tender item "Another Retail Item" ("Description", <see cref="TestPhoto"/>, reserve 111) in the
    /// fixture region and category, sold by the fixture agent and closing seven days after the server date. Ports
    /// <c>createTestTenderItem("Another Retail Item", ..., 111, Utilities@getServerDate() + 7)</c>.
    /// </summary>
    /// <returns>The new tender item.</returns>
    private async Task<TenderItem> CreateAnotherTenderItemAsync()
    {
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();

        return await Builder.CreateTestTenderItemAsync(
            "Another Retail Item",
            "Description",
            TestPhoto.ToArray(),
            Builder.MyRegion!,
            Builder.MyItemCategory!,
            Builder.MyAgent!,
            111m,
            serverDate.AddDays(7));
    }
}
