using System.Text.Json;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getItemByCode</c>, called over HTTP as
/// <c>GET /api/rest/items/by-code?codePrefix=&amp;codeNumber=</c>. Ports the JADE class <c>RestTestGetItemByCode</c>.
/// </summary>
public sealed class RestTestGetItemByCode : ErewhonTestBase
{
    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/> and binds its company: the retail
    /// item "Retail Item" with code number 1 and the tender item "Tender Item" with code number 2, both in the category
    /// <c>CAT1</c>. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>Requesting <c>CAT1</c> 1 answers 200 with a retail item.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetRetailItem()
    {
        using HttpClient http = Api.CreateHttpClient();

        // Arrange/Act
        using HttpResponseMessage response = await GetItemByCodeAsync(http, "CAT1", 1);

        using JsonDocument dtoItem = await ReadItemAsync(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retailItem", dtoItem.RootElement.GetProperty("type").GetString());
    }

    /// <summary>Requesting <c>CAT1</c> 2 answers 200 with a tender item.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetTenderItem()
    {
        using HttpClient http = Api.CreateHttpClient();

        // Arrange/Act
        using HttpResponseMessage response = await GetItemByCodeAsync(http, "CAT1", 2);

        using JsonDocument dtoItem = await ReadItemAsync(response);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("tenderItem", dtoItem.RootElement.GetProperty("type").GetString());
    }

    /// <summary>Requesting <c>CAT1</c> with code number 0 answers 400 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidCodeNumber()
    {
        using HttpClient http = Api.CreateHttpClient();

        // Arrange/Act
        using HttpResponseMessage response = await GetItemByCodeAsync(http, "CAT1", 0);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    /// <summary>Requesting <c>INVALID</c> 999 answers 400 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidPrefix()
    {
        using HttpClient http = Api.CreateHttpClient();

        // Arrange/Act
        using HttpResponseMessage response = await GetItemByCodeAsync(http, "INVALID", 999);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(400, (int)response.StatusCode);
    }

    /// <summary>Requesting <c>CAT1</c> 3, a code number no item has, answers 404 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testItemNotFound()
    {
        using HttpClient http = Api.CreateHttpClient();

        // Arrange/Act
        using HttpResponseMessage response = await GetItemByCodeAsync(http, "CAT1", 3);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(404, (int)response.StatusCode);
    }

    /// <summary>
    /// Sends <c>GET /api/rest/items/by-code</c> with <paramref name="prefix"/> as <c>codePrefix</c> and
    /// <paramref name="number"/> as <c>codeNumber</c>.
    /// </summary>
    private static Task<HttpResponseMessage> GetItemByCodeAsync(HttpClient http, string prefix, int number) =>
        http.GetAsync($"/api/rest/items/by-code?codePrefix={Uri.EscapeDataString(prefix)}&codeNumber={number}");

    /// <summary>
    /// Checks that the body of <paramref name="response"/> is not empty and is a JSON object, and returns it parsed.
    /// </summary>
    private static async Task<JsonDocument> ReadItemAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual(string.Empty, body);

        JsonDocument document = JsonDocument.Parse(body);
        JsonValueKind kind = document.RootElement.ValueKind;
        if (kind != JsonValueKind.Object)
        {
            document.Dispose();
            Assert.Fail($"The response body is a JSON {kind}, not an object: {body}");
        }

        return document;
    }
}
