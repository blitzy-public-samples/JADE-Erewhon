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
/// <c>createTestData</c> company bound and ends with no company.
/// </summary>
public sealed class RestTestPostCreateTender : ErewhonTestBase
{
    private const string TendersRoute = "/api/rest/tenders";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>postCreateTender</c> with no client name answers 404 with the error <c>$ClientNotFound</c> and creates no
    /// sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testClientNotValid()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            null,
            Builder.MyItemCategory!.Prefix,
            Builder.MyTenderItem!.CodeNumber,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNotFound), dtoResults.AllErrors[0]);
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on a further tender item, offering its reserve plus 10, answers
    /// 201 with no errors.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateTender()
    {
        // Arrange
        TenderItem tenderItem = await CreateAnotherTenderItemAsync();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.Empty(dtoResults.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on the code number 999, which no item holds, answers 404 with the
    /// error <c>$ItemNotFound</c> and creates no sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotFound()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            999,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotFound), dtoResults.AllErrors[0]);
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on the code of the fixture retail item answers 400 with the error
    /// <c>$ItemNotTenderItem</c> and creates no sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotTenderItem()
    {
        // Arrange / Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            Builder.MyRetailItem!.CodeNumber,
            Builder.MyTenderItem!.Price + 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotTenderItem), dtoResults.AllErrors[0]);
    }

    /// <summary>
    /// <c>postCreateTender</c> by the fixture client on a further tender item, offering its reserve less 10, answers
    /// 400 with the first error <c>$TenderOfferLessThanReserve</c> and creates no sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testOfferLowerThanReserve()
    {
        // Arrange
        TenderItem tenderItem = await CreateAnotherTenderItemAsync();

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostTenderAsync(
            http,
            Builder.MyClient!.Name,
            Builder.MyItemCategory!.Prefix,
            tenderItem.CodeNumber,
            tenderItem.Price - 10m);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.TenderOfferLessThanReserve), dtoResults.AllErrors[0]);
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
