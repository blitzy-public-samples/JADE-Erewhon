using System.Text.Json;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Ports the JADE unit-test class <c>RestTestGetSale</c>: the REST operation <c>getSale</c>, called over HTTP as
/// <c>GET /api/rest/sales/by-code</c> against the running API, over the data of <c>createTestData</c>.
/// </summary>
public sealed class RestTestGetSale : ErewhonTestBase
{
    /// <summary>
    /// Creates and binds the <c>createTestData</c> company with its retail sale of <c>CAT1</c> 1 and its tender sale of
    /// <c>CAT1</c> 2. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    [Fact]
    public async Task testGetRetailSale()
    {
        using HttpClient http = Api.CreateHttpClient();

        using HttpResponseMessage response = await GetSaleAsync(http, "CAT1", 1);
        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument dtoSale = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, dtoSale.RootElement.ValueKind);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retailSale", dtoSale.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task testGetTenderSale()
    {
        using HttpClient http = Api.CreateHttpClient();

        using HttpResponseMessage response = await GetSaleAsync(http, "CAT1", 2);
        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument dtoSale = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, dtoSale.RootElement.ValueKind);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("tenderSale", dtoSale.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task testInvalidCodeNumber()
    {
        using HttpClient http = Api.CreateHttpClient();

        // The JADE null Integer.
        using HttpResponseMessage response = await GetSaleAsync(http, "PRFX", 0);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task testInvalidPrefix()
    {
        using HttpClient http = Api.CreateHttpClient();

        using HttpResponseMessage response = await GetSaleAsync(http, "INVALID", 999);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task testSaleNotFound()
    {
        using HttpClient http = Api.CreateHttpClient();

        using HttpResponseMessage response = await GetSaleAsync(http, "CAT1", 999);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(404, (int)response.StatusCode);
    }

    /// <summary>
    /// Sends <c>GET /api/rest/sales/by-code</c> with <paramref name="prefix"/> as <c>codePrefix</c> and
    /// <paramref name="number"/> as <c>codeNumber</c>. Ports <c>erewhonRestService.getSale(prefix, number)</c>.
    /// </summary>
    /// <param name="http">The client addressed to the running API.</param>
    /// <param name="prefix">The category prefix of the sold item.</param>
    /// <param name="number">The code number of the sold item.</param>
    /// <returns>The response.</returns>
    private static Task<HttpResponseMessage> GetSaleAsync(HttpClient http, string prefix, int number) =>
        http.GetAsync($"/api/rest/sales/by-code?codePrefix={Uri.EscapeDataString(prefix)}&codeNumber={number}");
}
