using System.Text.Json;
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

        // Assert
        Assert.Equal(JsonValueKind.Array, sales.ValueKind);
        Assert.Equal(2, sales.GetArrayLength());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retailSale", sales[0].GetProperty("type").GetString());
        Assert.Equal("tenderSale", sales[sales.GetArrayLength() - 1].GetProperty("type").GetString());
    }
}
