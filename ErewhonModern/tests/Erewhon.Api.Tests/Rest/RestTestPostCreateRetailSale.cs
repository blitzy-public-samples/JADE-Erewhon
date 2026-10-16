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
/// Ports the JADE class <c>RestTestPostCreateRetailSale</c>.
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
    /// leaves the company's two sales unchanged.
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

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNotFound), result.AllErrors[0]);
    }

    /// <summary>
    /// Posting a retail sale of a new unsold retail item to the fixture client answers 201 with no errors and adds a
    /// third sale to the company.
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

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await PostRetailSaleAsync(
            http,
            Builder.MyClient!.Name,
            retailItem.ItemCategory!.Prefix,
            retailItem.CodeNumber);
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(3, await SaleCountAsync());
    }

    /// <summary>
    /// Posting a retail sale of item number 999, which the company does not hold, answers 404 with
    /// <see cref="MessageKeys.ItemNotFound"/> first and leaves the company's two sales unchanged.
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

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(404, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotFound), result.AllErrors[0]);
    }

    /// <summary>
    /// Posting a retail sale of the fixture tender item answers 400 with <see cref="MessageKeys.ItemNotRetailItem"/>
    /// first and leaves the company's two sales unchanged.
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

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(2, await SaleCountAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemNotRetailItem), result.AllErrors[0]);
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
}
