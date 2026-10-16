using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Messages;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// The REST behaviour-catalogue tests: rules of <c>ErewhonRestService</c> and of the <c>DTO*.populateFromObject</c>
/// methods that the <c>RestTest*</c> ports do not pin, called over HTTP against the running Erewhon API. Each test
/// starts with the <c>createTestData</c> company bound and ends with no company.
/// </summary>
public sealed class RestCatalogueTests : ErewhonTestBase
{
    private const string AgentsPath = "/api/rest/agents";
    private const string ClientsPath = "/api/rest/clients";
    private const string CompanyPath = "/api/rest/company";
    private const string ItemsPath = "/api/rest/items";
    private const string SalesPath = "/api/rest/sales";
    private const string RetailSalesPath = "/api/rest/retail-sales";
    private const string TendersPath = "/api/rest/tenders";

    private const string FixtureAgentName = "Test Agent";
    private const string FixtureClientName = "Test Client";
    private const string FixtureCategoryName = "Test Category 1";
    private const string FixtureCategoryPrefix = "CAT1";
    private const string FixtureRegionName = "Region Name";
    private const string RetailItemName = "Retail Item";
    private const string RetailItemDescription = "A retail item for testing";
    private const string TenderItemName = "Tender Item";
    private const string TenderItemDescription = "A tender item for testing";
    private const int RetailItemNumber = 1;
    private const int TenderItemNumber = 2;
    private const decimal RetailItemPrice = 999.99m;
    private const decimal TenderItemReserve = 888.88m;
    private const decimal FixtureTenderOffer = 889.88m;

    private const int Ok = 200;
    private const int Created = 201;
    private const int BadRequest = 400;
    private const int NotFound = 404;
    private const int ServerError = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private long CompanyId =>
        Builder.MyCompany?.Id
        ?? throw new InvalidOperationException($"The fixture company is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Creates the <c>createTestData</c> company and its fixtures through
    /// <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds it: the agent "Test Agent", the client
    /// "Test Client", the category "Test Category 1" (<c>CAT1</c>), the region "Region Name" of "Country Name", the
    /// retail item 1 at 999.99 and the tender item 2 with reserve 888.88 closing seven days after the server date, both
    /// with <see cref="TestPhoto"/>, a tender of 889.88 on item 2, a retail sale of item 1 and a tender sale of item 2.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// <c>getAgent</c> looks the name up as given: "Test Agent" answers 200 and " Test Agent " answers 404 with an
    /// empty body. <c>getClient</c> trims blanks: "  Test Client  " answers 200 with the client "Test Client".
    /// <c>getSale</c> checks the trimmed prefix and looks up the untrimmed one: <c>CAT1</c>/1 answers 200 with the
    /// retail sale and " CAT1 "/1 answers 404 with an empty body. <c>getItemByCode</c> looks the item up by number
    /// only: " CAT1 "/1 and <c>ZZZZ</c>/1 both answer 200 with retail item 1.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B52_RestLookupTrimming()
    {
        using HttpClient http = Api.CreateHttpClient();

        // getAgent: exact name found, padded name not found.
        JsonElement agent = await GetJsonAsync(http, AgentByName(FixtureAgentName));
        Assert.Equal(FixtureAgentName, agent.GetProperty("name").GetString());

        await GetEmptyAsync(http, AgentByName(" " + FixtureAgentName + " "), NotFound);

        // getClient: padded name found as the trimmed name.
        JsonElement client = await GetJsonAsync(http, ClientByName("  " + FixtureClientName + "  "));
        Assert.Equal(FixtureClientName, client.GetProperty("name").GetString());

        // getSale: exact prefix found, padded prefix not found.
        JsonElement sale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber));
        Assert.Equal("retailSale", sale.GetProperty("type").GetString());

        await GetEmptyAsync(http, SaleByCode(" " + FixtureCategoryPrefix + " ", RetailItemNumber), NotFound);

        // getItemByCode: padded and unrelated prefixes both find item 1.
        JsonElement paddedPrefixItem = await GetJsonAsync(
            http,
            ItemByCode(" " + FixtureCategoryPrefix + " ", RetailItemNumber));
        Assert.Equal("retailItem", paddedPrefixItem.GetProperty("type").GetString());
        Assert.Equal(RetailItemNumber, paddedPrefixItem.GetProperty("number").GetInt32());

        JsonElement otherPrefixItem = await GetJsonAsync(http, ItemByCode("ZZZZ", RetailItemNumber));
        Assert.Equal("retailItem", otherPrefixItem.GetProperty("type").GetString());
        Assert.Equal(RetailItemNumber, otherPrefixItem.GetProperty("number").GetInt32());
    }

    /// <summary>
    /// <c>postCreateAgent</c> with the name of the existing agent answers 400, not 409, with <c>statusCode</c> 400,
    /// <c>$AgentAlreadyExists</c> as the first error and no GUID, and creates no agent; <c>postCreateClient</c> with
    /// the name of the existing client answers the same with <c>$ClientAlreadyExists</c> and creates no client.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B60_RestDuplicateAgentIs400()
    {
        using HttpClient http = Api.CreateHttpClient();
        long companyId = CompanyId;

        // Duplicate agent.
        ResultDto agentResult = await PostResultAsync(http, AgentsPath, NewAgentDto(FixtureAgentName), BadRequest);

        Assert.Equal(BadRequest, agentResult.StatusCode);
        Assert.NotEmpty(agentResult.AllErrors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentAlreadyExists), agentResult.AllErrors[0]);
        Assert.Null(agentResult.Guid);

        int agentCount = await ReadDatabaseAsync(
            db => db.Agents.AsNoTracking().CountAsync(row => row.CompanyId == companyId));
        Assert.Equal(1, agentCount);

        // Duplicate client.
        ResultDto clientResult = await PostResultAsync(http, ClientsPath, NewClientDto(FixtureClientName), BadRequest);

        Assert.Equal(BadRequest, clientResult.StatusCode);
        Assert.NotEmpty(clientResult.AllErrors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientAlreadyExists), clientResult.AllErrors[0]);
        Assert.Null(clientResult.Guid);

        int clientCount = await ReadDatabaseAsync(
            db => db.Clients.AsNoTracking().CountAsync(row => row.CompanyId == companyId));
        Assert.Equal(1, clientCount);
    }

    /// <summary>
    /// With no company, <c>getAllAgents</c>, <c>getAllClients</c> and <c>getAllSales</c> each answer 500 with an empty
    /// body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B69_ListOperationsWithoutCompanyAre500()
    {
        await DeleteFixtureCompanyAsync();
        using HttpClient http = Api.CreateHttpClient();

        await GetEmptyAsync(http, AgentsPath, ServerError);
        await GetEmptyAsync(http, ClientsPath, ServerError);
        await GetEmptyAsync(http, SalesPath, ServerError);
    }

    /// <summary>
    /// With no company, <c>getAgent</c>, <c>getClient</c>, <c>getCompany</c>, <c>postCreateAgent</c>,
    /// <c>postCreateClient</c>, <c>postCreateRetailSale</c> and <c>postCreateTender</c> each answer 500 with an empty
    /// body for inputs that would otherwise succeed, and nothing is persisted.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B69_SingleLookupsWithoutCompanyAre500()
    {
        await DeleteFixtureCompanyAsync();
        using HttpClient http = Api.CreateHttpClient();

        await GetEmptyAsync(http, AgentByName(FixtureAgentName), ServerError);
        await GetEmptyAsync(http, ClientByName(FixtureClientName), ServerError);
        await GetEmptyAsync(http, CompanyPath, ServerError);

        await PostEmptyAsync(http, AgentsPath, NewAgentDto("New Agent"), ServerError);
        await PostEmptyAsync(http, ClientsPath, NewClientDto("New Client"), ServerError);
        await PostEmptyAsync(
            http,
            RetailSalesPath,
            new RetailSaleRequest
            {
                Client = FixtureClientName,
                CodePrefix = FixtureCategoryPrefix,
                CodeNumber = RetailItemNumber,
            },
            ServerError);
        await PostEmptyAsync(
            http,
            TendersPath,
            new TenderRequest
            {
                Client = FixtureClientName,
                CodePrefix = FixtureCategoryPrefix,
                CodeNumber = TenderItemNumber,
                Offer = 898.88m,
            },
            ServerError);

        // No row was written by the requests.
        Assert.False(await ReadDatabaseAsync(db => db.Companies.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Agents.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Clients.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Sales.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Tenders.AsNoTracking().AnyAsync()));
    }

    /// <summary>
    /// With no company, <c>getItemByCode("", 1)</c>, <c>getItemsByPrice(-1, 5)</c> and <c>getSale("TOOLONG", 1)</c>
    /// each answer 400 with an empty body, and the same three operations with valid inputs each answer 500 with an
    /// empty body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B69_InputChecksPrecedeCompanyAccess()
    {
        await DeleteFixtureCompanyAsync();
        using HttpClient http = Api.CreateHttpClient();

        // Invalid inputs.
        await GetEmptyAsync(http, ItemByCode(string.Empty, RetailItemNumber), BadRequest);
        await GetEmptyAsync(http, ItemsByPrice(-1, 5), BadRequest);
        await GetEmptyAsync(http, SaleByCode("TOOLONG", RetailItemNumber), BadRequest);

        // Valid inputs.
        await GetEmptyAsync(http, ItemByCode(FixtureCategoryPrefix, RetailItemNumber), ServerError);
        await GetEmptyAsync(http, ItemsByPrice(0, 5), ServerError);
        await GetEmptyAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber), ServerError);
    }

    /// <summary>
    /// <c>postCreateAgent</c> with an 83-character name, 81 characters between a leading and a trailing blank, answers
    /// 500 with an empty body and creates no agent; an 81-character name answers 201 with <c>statusCode</c> 201, no
    /// errors and the new agent's GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B22_PaddedRestNameRejectedBeforeTrim()
    {
        using HttpClient http = Api.CreateHttpClient();
        long companyId = CompanyId;

        // Padded name over the declared length.
        string paddedName = " " + new string('A', 81) + " ";
        await PostEmptyAsync(http, AgentsPath, NewAgentDto(paddedName), ServerError);

        int agentCountAfterPadded = await ReadDatabaseAsync(
            db => db.Agents.AsNoTracking().CountAsync(row => row.CompanyId == companyId));
        Assert.Equal(1, agentCountAfterPadded);

        // Name at the declared length.
        ResultDto result = await PostResultAsync(http, AgentsPath, NewAgentDto(new string('B', 81)), Created);

        Assert.Equal(Created, result.StatusCode);
        Assert.Empty(result.AllErrors);
        Assert.False(string.IsNullOrEmpty(result.Guid));

        int agentCountAfterValid = await ReadDatabaseAsync(
            db => db.Agents.AsNoTracking().CountAsync(row => row.CompanyId == companyId));
        Assert.Equal(2, agentCountAfterValid);
    }

    /// <summary>
    /// <c>getAgent</c> and <c>getClient</c> with a 200-character name each answer 404 with an empty body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task B22_LongLookupNameIs404()
    {
        using HttpClient http = Api.CreateHttpClient();

        await GetEmptyAsync(http, AgentByName(new string('A', 200)), NotFound);
        await GetEmptyAsync(http, ClientByName(new string('C', 200)), NotFound);
    }

    /// <summary>
    /// The items stored with category <c>CAT1</c> and <see cref="TestPhoto"/> are returned without
    /// <c>categoryPrefix</c> and <c>photo</c>, with their number, name, description, price, region, category name and
    /// agent: by <c>getItemByCode</c> for item 1 (<c>retailItem</c>) and item 2 (<c>tenderItem</c>, closing seven days
    /// after the server date), by <c>getItemsByPrice(800, 1000)</c> for both, and inside the <c>item</c> of both
    /// <c>getSale</c> responses and the <c>tender.tenderItem</c> of the tender sale.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task Q32_ItemCategoryPrefixAndPhotoNotPopulated()
    {
        using HttpClient http = Api.CreateHttpClient();
        long companyId = CompanyId;
        DateOnly expectedClosureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(7);

        // Stored items hold the category prefix and the photo.
        foreach (int codeNumber in new[] { RetailItemNumber, TenderItemNumber })
        {
            var stored = await ReadDatabaseAsync(db => db.Items
                .AsNoTracking()
                .Where(item => item.CompanyId == companyId && item.CodeNumber == codeNumber)
                .Select(item => new { item.ItemCategory!.Prefix, item.Photo })
                .SingleAsync());
            Assert.Equal(FixtureCategoryPrefix, stored.Prefix);
            Assert.Equal(TestPhoto.ToArray(), stored.Photo);
        }

        // getItemByCode.
        AssertRetailItemFixture(await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, RetailItemNumber)));
        AssertTenderItemFixture(
            await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, TenderItemNumber)),
            expectedClosureDate);

        // getItemsByPrice.
        JsonElement items = await GetJsonAsync(http, ItemsByPrice(800, 1000));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Collection(
            items.EnumerateArray(),
            AssertRetailItemFixture,
            item => AssertTenderItemFixture(item, expectedClosureDate));

        // getSale: the sold item and the tender's item.
        JsonElement retailSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber));
        AssertCategoryPrefixAndPhotoUnpopulated(retailSale.GetProperty("item"));

        JsonElement tenderSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, TenderItemNumber));
        AssertCategoryPrefixAndPhotoUnpopulated(tenderSale.GetProperty("item"));
        AssertCategoryPrefixAndPhotoUnpopulated(tenderSale.GetProperty("tender").GetProperty("tenderItem"));
    }

    /// <summary>
    /// The sale shapes of the item and sale responses: retail item 1's <c>sale</c> is a <c>retailSaleDetail</c> with
    /// <c>price</c> 999.99, a <c>soldDate</c> and the client; tender item 2's <c>sale</c> is the base sale detail,
    /// holding only <c>guid</c>, <c>client</c> and <c>agentCommission</c>, with no <c>offer</c> or <c>tenderDate</c>;
    /// the <c>item</c> of each <c>getSale</c> response is the base item, without <c>type</c>, <c>closureDate</c> or
    /// <c>sale</c>; the <c>tenderSale</c> of item 2 has no <c>offer</c> of its own, and its <c>tender</c> holds the
    /// offer 889.88, the client and the tender item.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task Q33_TenderSaleDetailShapes()
    {
        using HttpClient http = Api.CreateHttpClient();
        long companyId = CompanyId;
        var storedTenderSale = await ReadDatabaseAsync(db => db.Sales
            .AsNoTracking()
            .Where(sale => sale.CompanyId == companyId && sale.Item!.CodeNumber == TenderItemNumber)
            .Select(sale => new { sale.Guid, sale.AgentCommission })
            .SingleAsync());

        // Retail item sale detail.
        JsonElement retailItem = await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, RetailItemNumber));
        JsonElement retailSaleDetail = retailItem.GetProperty("sale");
        Assert.Equal("retailSaleDetail", retailSaleDetail.GetProperty("type").GetString());
        Assert.Equal(RetailItemPrice, retailSaleDetail.GetProperty("price").GetDecimal());
        Assert.False(IsUnpopulated(retailSaleDetail, "soldDate"));
        Assert.Equal(FixtureClientName, retailSaleDetail.GetProperty("client").GetProperty("name").GetString());

        // Tender item sale detail.
        JsonElement tenderItem = await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, TenderItemNumber));
        JsonElement tenderSaleDetail = tenderItem.GetProperty("sale");
        Assert.Equal(JsonValueKind.Object, tenderSaleDetail.ValueKind);
        Assert.Equal(
            new[] { "agentCommission", "client", "guid" },
            tenderSaleDetail.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(tenderSaleDetail.TryGetProperty("type", out _));
        Assert.Equal(storedTenderSale.Guid, tenderSaleDetail.GetProperty("guid").GetString());
        Assert.Equal(FixtureClientName, tenderSaleDetail.GetProperty("client").GetProperty("name").GetString());
        JsonElement agentCommission = tenderSaleDetail.GetProperty("agentCommission");
        Assert.Equal(JsonValueKind.Number, agentCommission.ValueKind);
        Assert.Equal(storedTenderSale.AgentCommission, agentCommission.GetDecimal());
        Assert.True(IsUnpopulated(tenderSaleDetail, "offer"));
        Assert.True(IsUnpopulated(tenderSaleDetail, "tenderDate"));

        // Retail sale item.
        JsonElement retailSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber));
        Assert.Equal("retailSale", retailSale.GetProperty("type").GetString());
        AssertBaseItem(retailSale.GetProperty("item"), RetailItemNumber);

        // Tender sale item, offer and tender.
        JsonElement tenderSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, TenderItemNumber));
        Assert.Equal("tenderSale", tenderSale.GetProperty("type").GetString());
        AssertBaseItem(tenderSale.GetProperty("item"), TenderItemNumber);
        Assert.True(IsUnpopulated(tenderSale, "offer"));

        JsonElement tender = tenderSale.GetProperty("tender");
        Assert.Equal(FixtureTenderOffer, tender.GetProperty("offer").GetDecimal());
        Assert.Equal(FixtureClientName, tender.GetProperty("client").GetProperty("name").GetString());
        JsonElement tenderedItem = tender.GetProperty("tenderItem");
        Assert.Equal(JsonValueKind.Object, tenderedItem.ValueKind);
        Assert.Equal(TenderItemNumber, tenderedItem.GetProperty("number").GetInt32());
    }

    /// <summary>Returns the <c>getAgent</c> path for <paramref name="name"/>.</summary>
    private static string AgentByName(string name) => $"{AgentsPath}/by-name?name={Uri.EscapeDataString(name)}";

    /// <summary>Returns the <c>getClient</c> path for <paramref name="name"/>.</summary>
    private static string ClientByName(string name) => $"{ClientsPath}/by-name?name={Uri.EscapeDataString(name)}";

    /// <summary>Returns the <c>getItemByCode</c> path for <paramref name="codePrefix"/> and <paramref name="codeNumber"/>.</summary>
    private static string ItemByCode(string codePrefix, int codeNumber) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ItemsPath}/by-code?codePrefix={Uri.EscapeDataString(codePrefix)}&codeNumber={codeNumber}");

    /// <summary>Returns the <c>getSale</c> path for <paramref name="codePrefix"/> and <paramref name="codeNumber"/>.</summary>
    private static string SaleByCode(string codePrefix, int codeNumber) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{SalesPath}/by-code?codePrefix={Uri.EscapeDataString(codePrefix)}&codeNumber={codeNumber}");

    /// <summary>Returns the <c>getItemsByPrice</c> path for <paramref name="minPrice"/> and <paramref name="maxPrice"/>.</summary>
    private static string ItemsByPrice(int minPrice, int maxPrice) =>
        string.Create(CultureInfo.InvariantCulture, $"{ItemsPath}?minPrice={minPrice}&maxPrice={maxPrice}");

    /// <summary>
    /// Returns an agent named <paramref name="name"/> at "Street", "City", "Country", phone "Phone", fax "Fax", e-mail
    /// "agent@email.com" and web site "www.website.com".
    /// </summary>
    private static AgentDto NewAgentDto(string name) => new()
    {
        Name = name,
        Street = "Street",
        City = "City",
        Country = "Country",
        Phone = "Phone",
        Fax = "Fax",
        Email = "agent@email.com",
        Website = "www.website.com",
    };

    /// <summary>
    /// Returns a client named <paramref name="name"/> at "Street", "City", "Country", phone "Phone", fax "Fax", e-mail
    /// "client@email.com" and web site "www.website.com".
    /// </summary>
    private static ClientDto NewClientDto(string name) => new()
    {
        Name = name,
        Street = "Street",
        City = "City",
        Country = "Country",
        Phone = "Phone",
        Fax = "Fax",
        Email = "client@email.com",
        Website = "www.website.com",
    };

    /// <summary>
    /// Sends GET <paramref name="path"/>, asserts status 200 and returns the parsed JSON body.
    /// </summary>
    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string path)
    {
        using HttpResponseMessage response = await http.GetAsync(path);

        Assert.Equal(Ok, (int)response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Sends GET <paramref name="path"/> and asserts status <paramref name="expectedStatus"/> with an empty body.
    /// </summary>
    private static async Task GetEmptyAsync(HttpClient http, string path, int expectedStatus)
    {
        using HttpResponseMessage response = await http.GetAsync(path);

        await AssertEmptyResponseAsync(response, expectedStatus);
    }

    /// <summary>
    /// Sends POST <paramref name="path"/> with <paramref name="body"/> as JSON and asserts status
    /// <paramref name="expectedStatus"/> with an empty body.
    /// </summary>
    private static async Task PostEmptyAsync<TBody>(HttpClient http, string path, TBody body, int expectedStatus)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(path, body, Json);

        await AssertEmptyResponseAsync(response, expectedStatus);
    }

    /// <summary>
    /// Sends POST <paramref name="path"/> with <paramref name="body"/> as JSON, asserts status
    /// <paramref name="expectedStatus"/> and returns the <see cref="ResultDto"/> body.
    /// </summary>
    private static async Task<ResultDto> PostResultAsync<TBody>(
        HttpClient http,
        string path,
        TBody body,
        int expectedStatus)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(path, body, Json);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        ResultDto? result = await response.Content.ReadFromJsonAsync<ResultDto>(Json);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>Asserts that <paramref name="response"/> has status <paramref name="expectedStatus"/> and an empty body.</summary>
    private static async Task AssertEmptyResponseAsync(HttpResponseMessage response, int expectedStatus)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Asserts that <paramref name="item"/> is the <c>retailItem</c> 1 "Retail Item", "A retail item for testing", at
    /// 999.99, with the fixture region, category and agent, and without <c>categoryPrefix</c> and <c>photo</c>.
    /// </summary>
    private static void AssertRetailItemFixture(JsonElement item)
    {
        AssertItemFixture(
            item,
            "retailItem",
            RetailItemNumber,
            RetailItemName,
            RetailItemDescription,
            RetailItemPrice);
    }

    /// <summary>
    /// Asserts that <paramref name="item"/> is the <c>tenderItem</c> 2 "Tender Item", "A tender item for testing", at
    /// 888.88, closing on <paramref name="expectedClosureDate"/>, with the fixture region, category and agent, and
    /// without <c>categoryPrefix</c> and <c>photo</c>.
    /// </summary>
    private static void AssertTenderItemFixture(JsonElement item, DateOnly expectedClosureDate)
    {
        AssertItemFixture(
            item,
            "tenderItem",
            TenderItemNumber,
            TenderItemName,
            TenderItemDescription,
            TenderItemReserve);
        Assert.Equal(expectedClosureDate, item.GetProperty("closureDate").Deserialize<DateOnly>(Json));
    }

    /// <summary>
    /// Asserts the <c>type</c>, number, name, description and price of <paramref name="item"/>, the region
    /// "Region Name", the category "Test Category 1" and the agent "Test Agent", and that <c>categoryPrefix</c> and
    /// <c>photo</c> are not populated.
    /// </summary>
    private static void AssertItemFixture(
        JsonElement item,
        string expectedType,
        int expectedNumber,
        string expectedName,
        string expectedDescription,
        decimal expectedPrice)
    {
        Assert.Equal(expectedType, item.GetProperty("type").GetString());
        AssertCategoryPrefixAndPhotoUnpopulated(item);
        Assert.Equal(expectedNumber, item.GetProperty("number").GetInt32());
        Assert.Equal(expectedName, item.GetProperty("name").GetString());
        Assert.Equal(expectedDescription, item.GetProperty("description").GetString());
        Assert.Equal(expectedPrice, item.GetProperty("price").GetDecimal());
        Assert.Equal(FixtureRegionName, item.GetProperty("region").GetString());
        Assert.Equal(FixtureCategoryName, item.GetProperty("itemCategory").GetString());
        Assert.Equal(FixtureAgentName, item.GetProperty("agent").GetProperty("name").GetString());
    }

    /// <summary>Asserts that <c>categoryPrefix</c> and <c>photo</c> of <paramref name="item"/> are not populated.</summary>
    private static void AssertCategoryPrefixAndPhotoUnpopulated(JsonElement item)
    {
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        Assert.True(IsUnpopulated(item, "categoryPrefix"));
        Assert.True(IsUnpopulated(item, "photo"));
    }

    /// <summary>
    /// Asserts that <paramref name="item"/> is a base item with number <paramref name="expectedNumber"/>: no
    /// <c>type</c>, and <c>closureDate</c> and <c>sale</c> not populated.
    /// </summary>
    private static void AssertBaseItem(JsonElement item, int expectedNumber)
    {
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        Assert.False(item.TryGetProperty("type", out _));
        Assert.Equal(expectedNumber, item.GetProperty("number").GetInt32());
        Assert.True(IsUnpopulated(item, "closureDate"));
        Assert.True(IsUnpopulated(item, "sale"));
    }

    /// <summary>
    /// Returns whether the property <paramref name="name"/> of <paramref name="obj"/> is absent, JSON <c>null</c>, or the
    /// number 0.
    /// </summary>
    private static bool IsUnpopulated(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value))
        {
            return true;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Number => value.GetDecimal() == 0m,
            _ => false,
        };
    }

    /// <summary>
    /// Deletes the fixture company with everything it owns through <see cref="TestCompanyBuilder.DeleteCompanyAsync"/>
    /// and asserts that no company remains.
    /// </summary>
    private async Task DeleteFixtureCompanyAsync()
    {
        await Builder.DeleteCompanyAsync(CompanyId);

        Assert.False(await ReadDatabaseAsync(db => db.Companies.AsNoTracking().AnyAsync()));
    }

    /// <summary>
    /// Runs <paramref name="read"/> against the <see cref="IErewhonDbContext"/> of a new service scope of the API and
    /// returns its result.
    /// </summary>
    private async Task<T> ReadDatabaseAsync<T>(Func<IErewhonDbContext, Task<T>> read)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();
        return await read(db);
    }
}
