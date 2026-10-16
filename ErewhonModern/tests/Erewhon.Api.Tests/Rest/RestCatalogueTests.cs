using System.Globalization;
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

    /// <summary>Code prefixes made only of white space other than spaces, or of spaces around such white space.</summary>
    private static readonly string[] WhiteSpaceOnlyPrefixes = ["\t", "\n", "\u00A0", " \t "];

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
    /// only: " CAT1 "/1 and <c>ZZZZ</c>/1 both answer 200 with retail item 1. Each of the prefixes "\t", "\n",
    /// "\u00A0" and " \t " with number 1 answers 200 with retail item 1 from <c>getItemByCode</c> and 404 with an empty
    /// body from <c>getSale</c>. An agent named "\t" and a client named "\u00A0", each created with 201, are each
    /// found by that name with 200.
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

        // getItemByCode and getSale: white-space-only prefixes pass the prefix check.
        foreach (string prefix in WhiteSpaceOnlyPrefixes)
        {
            JsonElement whiteSpacePrefixItem = await GetJsonAsync(http, ItemByCode(prefix, RetailItemNumber));
            Assert.Equal("retailItem", whiteSpacePrefixItem.GetProperty("type").GetString());
            Assert.Equal(RetailItemNumber, whiteSpacePrefixItem.GetProperty("number").GetInt32());

            await GetEmptyAsync(http, SaleByCode(prefix, RetailItemNumber), NotFound);
        }

        // getAgent: a tab name found as given.
        const string TabAgentName = "\t";
        await PostResultAsync(http, AgentsPath, NewAgentDto(TabAgentName), Created);

        JsonElement tabAgent = await GetJsonAsync(http, AgentByName(TabAgentName));
        Assert.Equal(TabAgentName, tabAgent.GetProperty("name").GetString());

        // getClient: a no-break space name found as given.
        const string NoBreakSpaceClientName = "\u00A0";
        await PostResultAsync(http, ClientsPath, NewClientDto(NoBreakSpaceClientName), Created);

        JsonElement noBreakSpaceClient = await GetJsonAsync(http, ClientByName(NoBreakSpaceClientName));
        Assert.Equal(NoBreakSpaceClientName, noBreakSpaceClient.GetProperty("name").GetString());
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
    /// With no company, each of these requests answers 500 with an empty body: <c>getAgent</c> with the name
    /// "Test Agent", no name, an empty name, " Test Agent ", "Nobody" and a 200-character name; <c>getClient</c> with
    /// the name "Test Client", no name, an empty name, three spaces, "Nobody" and a 200-character name;
    /// <c>getCompany</c>; <c>postCreateAgent</c> and <c>postCreateClient</c> with a complete party named "New Agent" or
    /// "New Client", the same party with no name, with the e-mail "not-an-email" and with a street of three spaces, and
    /// a party with every field unset; <c>postCreateRetailSale</c> with client "Test Client" and code numbers 1, 0, 999
    /// and 2, with no client, client "Nobody" and client " Test Client " and code number 1, and with every field unset;
    /// <c>postCreateTender</c> with client "Test Client", code number 2 and offers 898.88, 1, 0 and -1, with client
    /// "Test Client", offer 898.88 and code numbers 0, 999 and 1, with no client and with client "Nobody", code
    /// number 2 and offer 898.88, and with every field unset. Afterwards no company, agent, client, item, sale or
    /// tender row exists.
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

        // getAgent with no, an empty, a padded, an unknown and a 200-character name; getClient with no, an empty, a
        // blank, an unknown and a 200-character name.
        string longName = new string('N', 200);
        string[] lookupPaths =
        [
            $"{AgentsPath}/by-name",
            AgentByName(string.Empty),
            AgentByName(" " + FixtureAgentName + " "),
            AgentByName("Nobody"),
            AgentByName(longName),
            $"{ClientsPath}/by-name",
            ClientByName(string.Empty),
            ClientByName("   "),
            ClientByName("Nobody"),
            ClientByName(longName),
        ];
        foreach (string path in lookupPaths)
        {
            await GetEmptyAsync(http, path, ServerError);
        }

        // postCreateAgent with no name, with every field unset, with an invalid e-mail and with a blank street.
        AgentDto unnamedAgent = NewAgentDto("New Agent");
        unnamedAgent.Name = null;
        AgentDto invalidEmailAgent = NewAgentDto("New Agent");
        invalidEmailAgent.Email = "not-an-email";
        AgentDto blankStreetAgent = NewAgentDto("New Agent");
        blankStreetAgent.Street = "   ";
        AgentDto[] agents = [unnamedAgent, new AgentDto(), invalidEmailAgent, blankStreetAgent];
        foreach (AgentDto agent in agents)
        {
            await PostEmptyAsync(http, AgentsPath, agent, ServerError);
        }

        // postCreateClient with no name, with every field unset, with an invalid e-mail and with a blank street.
        ClientDto unnamedClient = NewClientDto("New Client");
        unnamedClient.Name = null;
        ClientDto invalidEmailClient = NewClientDto("New Client");
        invalidEmailClient.Email = "not-an-email";
        ClientDto blankStreetClient = NewClientDto("New Client");
        blankStreetClient.Street = "   ";
        ClientDto[] clients = [unnamedClient, new ClientDto(), invalidEmailClient, blankStreetClient];
        foreach (ClientDto client in clients)
        {
            await PostEmptyAsync(http, ClientsPath, client, ServerError);
        }

        // postCreateRetailSale with no, an unknown and a padded client, with code numbers 0 and 999, with the tender
        // item's number and with every field unset.
        static RetailSaleRequest NewRetailSaleRequest(string? client, int codeNumber) =>
            new() { Client = client, CodePrefix = FixtureCategoryPrefix, CodeNumber = codeNumber };

        RetailSaleRequest[] retailSales =
        [
            NewRetailSaleRequest(null, RetailItemNumber),
            NewRetailSaleRequest("Nobody", RetailItemNumber),
            NewRetailSaleRequest(" " + FixtureClientName + " ", RetailItemNumber),
            NewRetailSaleRequest(FixtureClientName, 0),
            NewRetailSaleRequest(FixtureClientName, 999),
            NewRetailSaleRequest(FixtureClientName, TenderItemNumber),
            new RetailSaleRequest(),
        ];
        foreach (RetailSaleRequest retailSale in retailSales)
        {
            await PostEmptyAsync(http, RetailSalesPath, retailSale, ServerError);
        }

        // postCreateTender with no and an unknown client, with code numbers 0 and 999, with the retail item's number,
        // with an offer below the reserve, of 0 and negative, and with every field unset.
        static TenderRequest NewTenderRequest(string? client, int codeNumber, decimal offer) =>
            new() { Client = client, CodePrefix = FixtureCategoryPrefix, CodeNumber = codeNumber, Offer = offer };

        TenderRequest[] tenders =
        [
            NewTenderRequest(null, TenderItemNumber, 898.88m),
            NewTenderRequest("Nobody", TenderItemNumber, 898.88m),
            NewTenderRequest(FixtureClientName, 0, 898.88m),
            NewTenderRequest(FixtureClientName, 999, 898.88m),
            NewTenderRequest(FixtureClientName, RetailItemNumber, 898.88m),
            NewTenderRequest(FixtureClientName, TenderItemNumber, 1m),
            NewTenderRequest(FixtureClientName, TenderItemNumber, 0m),
            NewTenderRequest(FixtureClientName, TenderItemNumber, -1m),
            new TenderRequest(),
        ];
        foreach (TenderRequest tender in tenders)
        {
            await PostEmptyAsync(http, TendersPath, tender, ServerError);
        }

        // No row was written by the requests.
        Assert.False(await ReadDatabaseAsync(db => db.Companies.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Agents.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Clients.AsNoTracking().AnyAsync()));
        Assert.False(await ReadDatabaseAsync(db => db.Items.AsNoTracking().AnyAsync()));
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
    /// agent, and with the GUID, listed date as <c>yyyy-MM-dd</c> and agent GUID of the item row read back untracked:
    /// by <c>getItemByCode</c> for item 1 (<c>retailItem</c>) and item 2 (<c>tenderItem</c>, closing on the closure
    /// date of <see cref="TestCompanyBuilder.MyTenderItem"/>, which is also the stored closure date), by
    /// <c>getItemsByPrice(800, 1000)</c> for both in that order, and inside the <c>item</c> of both <c>getSale</c>
    /// responses and the <c>tender.tenderItem</c> of the tender sale, which also holds the stored closure date. In the
    /// <c>getItemsByPrice</c> response, item 1's <c>sale</c> is a <c>retailSaleDetail</c> with the GUID, date sold as
    /// <c>soldDate</c> to the millisecond, sale price, agent commission and client GUID of the retail sale row read
    /// back untracked, and item 2's <c>sale</c> is the base sale detail, holding only <c>guid</c>, <c>client</c> and
    /// <c>agentCommission</c>, with the GUID, client GUID and agent commission of the tender sale row read back
    /// untracked.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task Q32_ItemCategoryPrefixAndPhotoNotPopulated()
    {
        using HttpClient http = Api.CreateHttpClient();
        long companyId = CompanyId;
        DateOnly expectedClosureDate = Fixture(Builder.MyTenderItem, nameof(Builder.MyTenderItem)).ClosureDate;

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

        // Stored GUIDs, listed dates, closure date and agent GUIDs.
        StoredItem storedRetailItem = await ReadStoredItemAsync(RetailItemNumber);
        StoredItem storedTenderItem = await ReadStoredItemAsync(TenderItemNumber);
        Assert.Null(storedRetailItem.ClosureDate);
        Assert.Equal(expectedClosureDate, storedTenderItem.ClosureDate);

        // getItemByCode.
        AssertRetailItemFixture(
            await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, RetailItemNumber)),
            storedRetailItem);
        AssertTenderItemFixture(
            await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, TenderItemNumber)),
            expectedClosureDate,
            storedTenderItem);

        // getItemsByPrice, with the sale of each item.
        StoredRetailSale storedRetailSale = await ReadStoredRetailSaleAsync(RetailItemNumber);
        StoredTenderSale storedTenderSale = await ReadStoredTenderSaleAsync(TenderItemNumber);
        JsonElement items = await GetJsonAsync(http, ItemsByPrice(800, 1000));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Collection(
            items.EnumerateArray(),
            item =>
            {
                AssertRetailItemFixture(item, storedRetailItem);
                AssertRetailSaleDetail(item.GetProperty("sale"), storedRetailSale);
            },
            item =>
            {
                AssertTenderItemFixture(item, expectedClosureDate, storedTenderItem);
                AssertTenderItemSaleDetail(item.GetProperty("sale"), storedTenderSale);
            });

        // getSale: the sold item and the tender's item.
        JsonElement retailSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber));
        AssertCategoryPrefixAndPhotoUnpopulated(retailSale.GetProperty("item"));
        AssertItemValues(retailSale.GetProperty("item"), storedRetailItem);

        JsonElement tenderSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, TenderItemNumber));
        AssertCategoryPrefixAndPhotoUnpopulated(tenderSale.GetProperty("item"));
        AssertItemValues(tenderSale.GetProperty("item"), storedTenderItem);

        JsonElement tenderedItem = tenderSale.GetProperty("tender").GetProperty("tenderItem");
        AssertCategoryPrefixAndPhotoUnpopulated(tenderedItem);
        AssertItemValues(tenderedItem, storedTenderItem);
        AssertDate(storedTenderItem.ClosureDate, tenderedItem.GetProperty("closureDate"));
    }

    /// <summary>
    /// The sale shapes and values of the item and sale responses, each value compared with the row read back
    /// untracked: retail item 1's <c>sale</c> is a <c>retailSaleDetail</c> with <c>price</c> 999.99 and the client, and
    /// with the stored sale's GUID, date sold as <c>soldDate</c> to the millisecond, sale price, agent commission and
    /// client GUID; tender item 2's <c>sale</c> is the base sale detail, holding only <c>guid</c>, <c>client</c> and
    /// <c>agentCommission</c> with the stored tender sale's GUID, client GUID and agent commission, and no
    /// <c>offer</c> or <c>tenderDate</c>; the <c>item</c> of each <c>getSale</c> response is the base item, without
    /// <c>type</c>, <c>closureDate</c> or <c>sale</c>, with the stored item's GUID, listed date and agent GUID; the
    /// <c>retailSale</c> of item 1 has the stored GUID, agent commission, client GUID and date sold as
    /// <c>timestamp</c>; the <c>tenderSale</c> of item 2 has the stored GUID, agent commission and client GUID and no
    /// <c>offer</c> of its own, and its <c>tender</c> holds the offer 889.88, the client and the tender item, with the
    /// stored tender's GUID, offer, date tendered as <c>timestamp</c> and client GUID, and a <c>tenderItem</c> with
    /// item 2's stored GUID, listed date, agent GUID and closure date and the base sale detail as its <c>sale</c>.
    /// With a commission rate of 12.50 on the category held by the agent, a retail item at 123.45 sold to the client
    /// at 2024-02-03 04:05:06.789 and a tender item sold to the client through its tender of 345.67 placed at
    /// 2024-03-04 05:06:07.891 pass the same value checks through <c>getItemByCode</c> and <c>getSale</c>, with
    /// <c>soldDate</c> and <c>timestamp</c> "2024-02-03T04:05:06.789", <c>tender.timestamp</c>
    /// "2024-03-04T05:06:07.891", and stored agent commissions 15.43 and 43.21. Their code numbers follow item 2 in
    /// creation order. <c>getItemsByPrice(100, 300)</c> then answers those two items alone, in code-number order: the
    /// <c>retailItem</c> at 123.45 and the <c>tenderItem</c> at 234.56 with its stored closure date, each with its
    /// number and the stored GUID, listed date and agent GUID, and as <c>sale</c> the <c>retailSaleDetail</c> with
    /// <c>soldDate</c> "2024-02-03T04:05:06.789" and agent commission 15.43 and the base sale detail with agent
    /// commission 43.21, each with the values of its stored sale. <c>getAllSales</c> answers the four sales in the
    /// code-number order of their items, item 1, item 2, the retail item and the tender item, each with the values
    /// checked in its <c>getSale</c> response, including its base <c>item</c>, with <c>timestamp</c>
    /// "2024-02-03T04:05:06.789" for the third and <c>tender.timestamp</c> "2024-03-04T05:06:07.891" for the fourth.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task Q33_TenderSaleDetailShapes()
    {
        using HttpClient http = Api.CreateHttpClient();
        StoredItem storedRetailItem = await ReadStoredItemAsync(RetailItemNumber);
        StoredItem storedTenderItem = await ReadStoredItemAsync(TenderItemNumber);
        StoredRetailSale storedRetailSale = await ReadStoredRetailSaleAsync(RetailItemNumber);
        StoredTenderSale storedTenderSale = await ReadStoredTenderSaleAsync(TenderItemNumber);

        // Retail item sale detail.
        JsonElement retailItem = await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, RetailItemNumber));
        JsonElement retailSaleDetail = retailItem.GetProperty("sale");
        Assert.Equal("retailSaleDetail", retailSaleDetail.GetProperty("type").GetString());
        Assert.Equal(RetailItemPrice, retailSaleDetail.GetProperty("price").GetDecimal());
        Assert.False(IsUnpopulated(retailSaleDetail, "soldDate"));
        Assert.Equal(FixtureClientName, retailSaleDetail.GetProperty("client").GetProperty("name").GetString());
        AssertRetailSaleDetail(retailSaleDetail, storedRetailSale);

        // Tender item sale detail.
        JsonElement tenderItem = await GetJsonAsync(http, ItemByCode(FixtureCategoryPrefix, TenderItemNumber));
        AssertTenderItemSaleDetail(tenderItem.GetProperty("sale"), storedTenderSale);

        // Retail sale item.
        JsonElement retailSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, RetailItemNumber));
        Assert.Equal("retailSale", retailSale.GetProperty("type").GetString());
        AssertBaseItem(retailSale.GetProperty("item"), RetailItemNumber, storedRetailItem);
        AssertRetailSale(retailSale, storedRetailSale);

        // Tender sale item, offer and tender.
        JsonElement tenderSale = await GetJsonAsync(http, SaleByCode(FixtureCategoryPrefix, TenderItemNumber));
        Assert.Equal("tenderSale", tenderSale.GetProperty("type").GetString());
        AssertBaseItem(tenderSale.GetProperty("item"), TenderItemNumber, storedTenderItem);
        Assert.True(IsUnpopulated(tenderSale, "offer"));

        JsonElement tender = tenderSale.GetProperty("tender");
        Assert.Equal(FixtureTenderOffer, tender.GetProperty("offer").GetDecimal());
        Assert.Equal(FixtureClientName, tender.GetProperty("client").GetProperty("name").GetString());
        JsonElement tenderedItem = tender.GetProperty("tenderItem");
        Assert.Equal(JsonValueKind.Object, tenderedItem.ValueKind);
        Assert.Equal(TenderItemNumber, tenderedItem.GetProperty("number").GetInt32());
        AssertTenderSale(tenderSale, storedTenderSale, storedTenderItem);

        // A commission rate of 12.50 on the category held by the agent; a retail item at 123.45 sold to the client at
        // 2024-02-03 04:05:06.789; a tender item with reserve 234.56 closing seven days after item 2, sold to the
        // client through its tender of 345.67 placed at 2024-03-04 05:06:07.891.
        Agent agent = Fixture(Builder.MyAgent, nameof(Builder.MyAgent));
        Client client = Fixture(Builder.MyClient, nameof(Builder.MyClient));
        ItemCategory category = Fixture(Builder.MyItemCategory, nameof(Builder.MyItemCategory));
        Region region = Fixture(Builder.MyRegion, nameof(Builder.MyRegion));
        TenderItem fixtureTenderItem = Fixture(Builder.MyTenderItem, nameof(Builder.MyTenderItem));
        var dateSold = new DateTime(2024, 2, 3, 4, 5, 6, 789, DateTimeKind.Unspecified);
        var dateTendered = new DateTime(2024, 3, 4, 5, 6, 7, 891, DateTimeKind.Unspecified);

        await Builder.CreateCommissionRateAsync(category, 12.50m, agent);
        RetailItem timedRetailItem = await Builder.CreateTestRetailItemAsync(
            "Timed Retail Item",
            "A retail item sold at a fixed time",
            null,
            region,
            category,
            agent,
            123.45m);
        await Builder.CreateTestRetailSaleAsync(timedRetailItem, client, dateSold);
        TenderItem timedTenderItem = await Builder.CreateTestTenderItemAsync(
            "Timed Tender Item",
            "A tender item tendered at a fixed time",
            null,
            region,
            category,
            agent,
            234.56m,
            fixtureTenderItem.ClosureDate.AddDays(7));
        Tender timedTender = await Builder.CreateTestTenderAsync(345.67m, dateTendered, client, timedTenderItem);
        await Builder.CreateTestTenderSaleAsync(timedTenderItem, timedTender, client);

        StoredItem storedTimedRetailItem = await ReadStoredItemAsync(timedRetailItem.CodeNumber);
        StoredItem storedTimedTenderItem = await ReadStoredItemAsync(timedTenderItem.CodeNumber);
        StoredRetailSale storedTimedRetailSale = await ReadStoredRetailSaleAsync(timedRetailItem.CodeNumber);
        StoredTenderSale storedTimedTenderSale = await ReadStoredTenderSaleAsync(timedTenderItem.CodeNumber);
        Assert.Equal(dateSold, storedTimedRetailSale.DateSold);
        Assert.Equal(123.45m, storedTimedRetailSale.SalePrice);
        Assert.Equal(15.43m, storedTimedRetailSale.AgentCommission);
        Assert.Equal(dateTendered, storedTimedTenderSale.DateTendered);
        Assert.Equal(345.67m, storedTimedTenderSale.Offer);
        Assert.Equal(43.21m, storedTimedTenderSale.AgentCommission);

        // getItemByCode and getSale for the retail item sold at a fixed time.
        JsonElement timedRetailItemDto = await GetJsonAsync(
            http,
            ItemByCode(FixtureCategoryPrefix, timedRetailItem.CodeNumber));
        Assert.Equal("retailItem", timedRetailItemDto.GetProperty("type").GetString());
        AssertItemValues(timedRetailItemDto, storedTimedRetailItem);
        JsonElement timedRetailSaleDetail = timedRetailItemDto.GetProperty("sale");
        Assert.Equal("2024-02-03T04:05:06.789", timedRetailSaleDetail.GetProperty("soldDate").GetString());
        AssertRetailSaleDetail(timedRetailSaleDetail, storedTimedRetailSale);

        JsonElement timedRetailSaleDto = await GetJsonAsync(
            http,
            SaleByCode(FixtureCategoryPrefix, timedRetailItem.CodeNumber));
        Assert.Equal("2024-02-03T04:05:06.789", timedRetailSaleDto.GetProperty("timestamp").GetString());
        AssertRetailSale(timedRetailSaleDto, storedTimedRetailSale);
        AssertBaseItem(timedRetailSaleDto.GetProperty("item"), timedRetailItem.CodeNumber, storedTimedRetailItem);

        // getItemByCode and getSale for the tender item tendered at a fixed time.
        JsonElement timedTenderItemDto = await GetJsonAsync(
            http,
            ItemByCode(FixtureCategoryPrefix, timedTenderItem.CodeNumber));
        Assert.Equal("tenderItem", timedTenderItemDto.GetProperty("type").GetString());
        AssertItemValues(timedTenderItemDto, storedTimedTenderItem);
        AssertDate(storedTimedTenderItem.ClosureDate, timedTenderItemDto.GetProperty("closureDate"));
        AssertTenderItemSaleDetail(timedTenderItemDto.GetProperty("sale"), storedTimedTenderSale);

        JsonElement timedTenderSaleDto = await GetJsonAsync(
            http,
            SaleByCode(FixtureCategoryPrefix, timedTenderItem.CodeNumber));
        Assert.Equal(
            "2024-03-04T05:06:07.891",
            timedTenderSaleDto.GetProperty("tender").GetProperty("timestamp").GetString());
        AssertTenderSale(timedTenderSaleDto, storedTimedTenderSale, storedTimedTenderItem);
        AssertBaseItem(timedTenderSaleDto.GetProperty("item"), timedTenderItem.CodeNumber, storedTimedTenderItem);

        // Code numbers ascend in creation order: item 1, item 2, the retail item, the tender item.
        Assert.True(TenderItemNumber < timedRetailItem.CodeNumber);
        Assert.True(timedRetailItem.CodeNumber < timedTenderItem.CodeNumber);

        // getItemsByPrice over the band of the two items sold at a fixed time, with the sale of each item.
        JsonElement timedItems = await GetJsonAsync(http, ItemsByPrice(100, 300));
        Assert.Equal(JsonValueKind.Array, timedItems.ValueKind);
        Assert.Collection(
            timedItems.EnumerateArray(),
            item =>
            {
                Assert.Equal("retailItem", item.GetProperty("type").GetString());
                Assert.Equal(timedRetailItem.CodeNumber, item.GetProperty("number").GetInt32());
                Assert.Equal(123.45m, item.GetProperty("price").GetDecimal());
                AssertItemValues(item, storedTimedRetailItem);
                JsonElement sale = item.GetProperty("sale");
                Assert.Equal("2024-02-03T04:05:06.789", sale.GetProperty("soldDate").GetString());
                Assert.Equal(15.43m, sale.GetProperty("agentCommission").GetDecimal());
                AssertRetailSaleDetail(sale, storedTimedRetailSale);
            },
            item =>
            {
                Assert.Equal("tenderItem", item.GetProperty("type").GetString());
                Assert.Equal(timedTenderItem.CodeNumber, item.GetProperty("number").GetInt32());
                Assert.Equal(234.56m, item.GetProperty("price").GetDecimal());
                AssertItemValues(item, storedTimedTenderItem);
                AssertDate(storedTimedTenderItem.ClosureDate, item.GetProperty("closureDate"));
                JsonElement sale = item.GetProperty("sale");
                Assert.Equal(43.21m, sale.GetProperty("agentCommission").GetDecimal());
                AssertTenderItemSaleDetail(sale, storedTimedTenderSale);
            });

        // getAllSales: the four sales in the code-number order of their items, each with its item.
        JsonElement allSales = await GetJsonAsync(http, SalesPath);
        Assert.Equal(JsonValueKind.Array, allSales.ValueKind);
        Assert.Collection(
            allSales.EnumerateArray(),
            sale =>
            {
                AssertRetailSale(sale, storedRetailSale);
                AssertBaseItem(sale.GetProperty("item"), RetailItemNumber, storedRetailItem);
            },
            sale =>
            {
                AssertTenderSale(sale, storedTenderSale, storedTenderItem);
                AssertBaseItem(sale.GetProperty("item"), TenderItemNumber, storedTenderItem);
            },
            sale =>
            {
                Assert.Equal("2024-02-03T04:05:06.789", sale.GetProperty("timestamp").GetString());
                AssertRetailSale(sale, storedTimedRetailSale);
                AssertBaseItem(sale.GetProperty("item"), timedRetailItem.CodeNumber, storedTimedRetailItem);
            },
            sale =>
            {
                Assert.Equal(
                    "2024-03-04T05:06:07.891",
                    sale.GetProperty("tender").GetProperty("timestamp").GetString());
                AssertTenderSale(sale, storedTimedTenderSale, storedTimedTenderItem);
                AssertBaseItem(sale.GetProperty("item"), timedTenderItem.CodeNumber, storedTimedTenderItem);
            });
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
    /// Checks that <paramref name="item"/> is the <c>retailItem</c> 1 "Retail Item", "A retail item for testing", at
    /// 999.99, with the fixture region, category and agent, the GUID, listed date and agent GUID of
    /// <paramref name="stored"/>, and without <c>categoryPrefix</c> and <c>photo</c>.
    /// </summary>
    private static void AssertRetailItemFixture(JsonElement item, StoredItem stored)
    {
        AssertItemFixture(
            item,
            "retailItem",
            RetailItemNumber,
            RetailItemName,
            RetailItemDescription,
            RetailItemPrice,
            stored);
    }

    /// <summary>
    /// Checks that <paramref name="item"/> is the <c>tenderItem</c> 2 "Tender Item", "A tender item for testing", at
    /// 888.88, closing on <paramref name="expectedClosureDate"/>, with the fixture region, category and agent, the
    /// GUID, listed date, agent GUID and closure date of <paramref name="stored"/>, and without
    /// <c>categoryPrefix</c> and <c>photo</c>.
    /// </summary>
    private static void AssertTenderItemFixture(JsonElement item, DateOnly expectedClosureDate, StoredItem stored)
    {
        AssertItemFixture(
            item,
            "tenderItem",
            TenderItemNumber,
            TenderItemName,
            TenderItemDescription,
            TenderItemReserve,
            stored);
        Assert.Equal(expectedClosureDate, item.GetProperty("closureDate").Deserialize<DateOnly>(Json));
        AssertDate(stored.ClosureDate, item.GetProperty("closureDate"));
    }

    /// <summary>
    /// Checks the <c>type</c>, number, name, description and price of <paramref name="item"/>, the region
    /// "Region Name", the category "Test Category 1" and the agent "Test Agent", that <c>categoryPrefix</c> and
    /// <c>photo</c> are not populated, and that its <c>guid</c>, <c>listedDate</c> and <c>agent.guid</c> are those of
    /// <paramref name="stored"/>.
    /// </summary>
    private static void AssertItemFixture(
        JsonElement item,
        string expectedType,
        int expectedNumber,
        string expectedName,
        string expectedDescription,
        decimal expectedPrice,
        StoredItem stored)
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
        AssertItemValues(item, stored);
    }

    /// <summary>
    /// Checks that the <c>guid</c> of <paramref name="item"/> is the GUID of <paramref name="stored"/>, its
    /// <c>listedDate</c> the stored listed date as <c>yyyy-MM-dd</c>, and its <c>agent.guid</c> the GUID of the stored
    /// item's agent.
    /// </summary>
    private static void AssertItemValues(JsonElement item, StoredItem stored)
    {
        AssertGuid(stored.Guid, item.GetProperty("guid"));
        AssertDate(stored.ListedDate, item.GetProperty("listedDate"));
        AssertGuid(stored.AgentGuid, item.GetProperty("agent").GetProperty("guid"));
    }

    /// <summary>Asserts that <c>categoryPrefix</c> and <c>photo</c> of <paramref name="item"/> are not populated.</summary>
    private static void AssertCategoryPrefixAndPhotoUnpopulated(JsonElement item)
    {
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        Assert.True(IsUnpopulated(item, "categoryPrefix"));
        Assert.True(IsUnpopulated(item, "photo"));
    }

    /// <summary>
    /// Checks that <paramref name="item"/> is a base item with number <paramref name="expectedNumber"/>: no
    /// <c>type</c>, and <c>closureDate</c> and <c>sale</c> not populated; and that its <c>guid</c>,
    /// <c>listedDate</c> and <c>agent.guid</c> are those of <paramref name="stored"/>.
    /// </summary>
    private static void AssertBaseItem(JsonElement item, int expectedNumber, StoredItem stored)
    {
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        Assert.False(item.TryGetProperty("type", out _));
        Assert.Equal(expectedNumber, item.GetProperty("number").GetInt32());
        Assert.True(IsUnpopulated(item, "closureDate"));
        Assert.True(IsUnpopulated(item, "sale"));
        AssertItemValues(item, stored);
    }

    /// <summary>
    /// Checks that <paramref name="sale"/> is a <c>retailSaleDetail</c> whose <c>guid</c>, <c>soldDate</c>,
    /// <c>price</c>, <c>agentCommission</c> and <c>client.guid</c> are the GUID, date sold, sale price, agent
    /// commission and client GUID of <paramref name="stored"/>.
    /// </summary>
    private static void AssertRetailSaleDetail(JsonElement sale, StoredRetailSale stored)
    {
        Assert.Equal("retailSaleDetail", sale.GetProperty("type").GetString());
        AssertGuid(stored.Guid, sale.GetProperty("guid"));
        AssertTimestamp(stored.DateSold, sale.GetProperty("soldDate"));
        Assert.Equal(stored.SalePrice, sale.GetProperty("price").GetDecimal());
        Assert.Equal(stored.AgentCommission, sale.GetProperty("agentCommission").GetDecimal());
        AssertGuid(stored.ClientGuid, sale.GetProperty("client").GetProperty("guid"));
    }

    /// <summary>
    /// Checks that <paramref name="sale"/> is the base sale detail of a tender item: an object holding only
    /// <c>agentCommission</c>, <c>client</c> and <c>guid</c>, with no <c>type</c>, with the GUID of
    /// <paramref name="stored"/>, the client "Test Client" with the stored client GUID and the numeric stored agent
    /// commission, and with no <c>offer</c> or <c>tenderDate</c>.
    /// </summary>
    private static void AssertTenderItemSaleDetail(JsonElement sale, StoredTenderSale stored)
    {
        Assert.Equal(JsonValueKind.Object, sale.ValueKind);
        Assert.Equal(
            new[] { "agentCommission", "client", "guid" },
            sale.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(sale.TryGetProperty("type", out _));
        AssertGuid(stored.Guid, sale.GetProperty("guid"));
        Assert.Equal(FixtureClientName, sale.GetProperty("client").GetProperty("name").GetString());
        AssertGuid(stored.ClientGuid, sale.GetProperty("client").GetProperty("guid"));
        JsonElement agentCommission = sale.GetProperty("agentCommission");
        Assert.Equal(JsonValueKind.Number, agentCommission.ValueKind);
        Assert.Equal(stored.AgentCommission, agentCommission.GetDecimal());
        Assert.True(IsUnpopulated(sale, "offer"));
        Assert.True(IsUnpopulated(sale, "tenderDate"));
    }

    /// <summary>
    /// Checks that <paramref name="sale"/> is a <c>retailSale</c> whose <c>guid</c>, <c>agentCommission</c>,
    /// <c>client.guid</c> and <c>timestamp</c> are the GUID, agent commission, client GUID and date sold of
    /// <paramref name="stored"/>.
    /// </summary>
    private static void AssertRetailSale(JsonElement sale, StoredRetailSale stored)
    {
        Assert.Equal("retailSale", sale.GetProperty("type").GetString());
        AssertGuid(stored.Guid, sale.GetProperty("guid"));
        Assert.Equal(stored.AgentCommission, sale.GetProperty("agentCommission").GetDecimal());
        AssertGuid(stored.ClientGuid, sale.GetProperty("client").GetProperty("guid"));
        AssertTimestamp(stored.DateSold, sale.GetProperty("timestamp"));
    }

    /// <summary>
    /// Checks that <paramref name="sale"/> is a <c>tenderSale</c> whose <c>guid</c>, <c>agentCommission</c> and
    /// <c>client.guid</c> are those of <paramref name="stored"/> and whose own <c>offer</c> is not populated; that the
    /// <c>guid</c>, <c>offer</c>,
    /// <c>timestamp</c> and <c>client.guid</c> of its <c>tender</c> are the GUID, offer, date tendered and client GUID
    /// of the stored tender; and that its <c>tender.tenderItem</c> has the GUID, listed date, agent GUID and closure
    /// date of <paramref name="storedItem"/> and, as its <c>sale</c>, the base sale detail of
    /// <paramref name="stored"/>.
    /// </summary>
    private static void AssertTenderSale(JsonElement sale, StoredTenderSale stored, StoredItem storedItem)
    {
        Assert.Equal("tenderSale", sale.GetProperty("type").GetString());
        AssertGuid(stored.Guid, sale.GetProperty("guid"));
        Assert.Equal(stored.AgentCommission, sale.GetProperty("agentCommission").GetDecimal());
        AssertGuid(stored.ClientGuid, sale.GetProperty("client").GetProperty("guid"));
        Assert.True(IsUnpopulated(sale, "offer"));

        JsonElement tender = sale.GetProperty("tender");
        AssertGuid(stored.TenderGuid, tender.GetProperty("guid"));
        Assert.Equal(stored.Offer, tender.GetProperty("offer").GetDecimal());
        AssertTimestamp(stored.DateTendered, tender.GetProperty("timestamp"));
        AssertGuid(stored.TenderClientGuid, tender.GetProperty("client").GetProperty("guid"));

        JsonElement tenderItem = tender.GetProperty("tenderItem");
        AssertItemValues(tenderItem, storedItem);
        AssertDate(storedItem.ClosureDate, tenderItem.GetProperty("closureDate"));
        AssertTenderItemSaleDetail(tenderItem.GetProperty("sale"), stored);
    }

    /// <summary>
    /// Checks that <paramref name="expected"/> is neither <see langword="null"/> nor empty and that
    /// <paramref name="value"/> is that string.
    /// </summary>
    private static void AssertGuid(string? expected, JsonElement value)
    {
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(expected, value.GetString());
    }

    /// <summary>
    /// Checks that <paramref name="expected"/> has a value and that <paramref name="value"/> is that date as the string
    /// <c>yyyy-MM-dd</c>.
    /// </summary>
    private static void AssertDate(DateOnly? expected, JsonElement value)
    {
        DateOnly date = Assert.NotNull(expected);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value.GetString());
    }

    /// <summary>
    /// Checks that <paramref name="value"/> is <paramref name="expected"/> as the string
    /// <c>yyyy-MM-ddTHH:mm:ss</c> followed by the milliseconds without trailing zeros, or by nothing when they are
    /// zero, and that it reads back as <paramref name="expected"/> with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    private static void AssertTimestamp(DateTime expected, JsonElement value)
    {
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(
            expected.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF", CultureInfo.InvariantCulture),
            value.GetString());
        DateTime read = value.Deserialize<DateTime>(Json);
        Assert.Equal(DateTimeKind.Unspecified, read.Kind);
        Assert.Equal(expected, read);
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
    /// Returns <paramref name="value"/>, the fixture object of the <see cref="TestCompanyBuilder"/> property
    /// <paramref name="name"/>, or throws <see cref="InvalidOperationException"/> when it is <see langword="null"/>.
    /// </summary>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"The fixture {name} is created by {nameof(SetUpAsync)}.");

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

    /// <summary>
    /// Reads, untracked through <see cref="ReadDatabaseAsync"/>, the GUID, listed date, closure date
    /// (<see langword="null"/> for a retail item) and agent GUID of the item with code number
    /// <paramref name="codeNumber"/> in the fixture company.
    /// </summary>
    private Task<StoredItem> ReadStoredItemAsync(int codeNumber)
    {
        long companyId = CompanyId;
        return ReadDatabaseAsync(db => db.Items
            .AsNoTracking()
            .Where(item => item.CompanyId == companyId && item.CodeNumber == codeNumber)
            .Select(item => new StoredItem(
                item.Guid,
                item.ListedDate,
                item is TenderItem ? (DateOnly?)((TenderItem)item).ClosureDate : null,
                item.Agent!.Guid))
            .SingleAsync());
    }

    /// <summary>
    /// Reads, untracked through <see cref="ReadDatabaseAsync"/>, the GUID, date sold, sale price, agent commission and
    /// client GUID of the retail sale of the item with code number <paramref name="codeNumber"/> in the fixture
    /// company.
    /// </summary>
    private Task<StoredRetailSale> ReadStoredRetailSaleAsync(int codeNumber)
    {
        long companyId = CompanyId;
        return ReadDatabaseAsync(db => db.Sales
            .AsNoTracking()
            .OfType<RetailSale>()
            .Where(sale => sale.CompanyId == companyId && sale.Item!.CodeNumber == codeNumber)
            .Select(sale => new StoredRetailSale(
                sale.Guid,
                sale.DateSold,
                sale.SalePrice,
                sale.AgentCommission,
                sale.Client!.Guid))
            .SingleAsync());
    }

    /// <summary>
    /// Reads, untracked through <see cref="ReadDatabaseAsync"/>, the GUID, agent commission and client GUID of the
    /// tender sale of the item with code number <paramref name="codeNumber"/> in the fixture company, and the GUID,
    /// offer, date tendered and client GUID of its tender.
    /// </summary>
    private Task<StoredTenderSale> ReadStoredTenderSaleAsync(int codeNumber)
    {
        long companyId = CompanyId;
        return ReadDatabaseAsync(db => db.Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .Where(sale => sale.CompanyId == companyId && sale.Item!.CodeNumber == codeNumber)
            .Select(sale => new StoredTenderSale(
                sale.Guid,
                sale.AgentCommission,
                sale.Client!.Guid,
                sale.Tender!.Guid,
                sale.Tender.Offer,
                sale.Tender.DateTendered,
                sale.Tender.Client!.Guid))
            .SingleAsync());
    }

    /// <summary>The GUID, listed date, closure date and agent GUID of a stored item.</summary>
    private sealed record StoredItem(string Guid, DateOnly ListedDate, DateOnly? ClosureDate, string AgentGuid);

    /// <summary>The GUID, date sold, sale price, agent commission and client GUID of a stored retail sale.</summary>
    private sealed record StoredRetailSale(
        string Guid,
        DateTime DateSold,
        decimal SalePrice,
        decimal AgentCommission,
        string ClientGuid);

    /// <summary>
    /// The GUID, agent commission and client GUID of a stored tender sale, and the GUID, offer, date tendered and
    /// client GUID of its tender.
    /// </summary>
    private sealed record StoredTenderSale(
        string Guid,
        decimal AgentCommission,
        string ClientGuid,
        string TenderGuid,
        decimal Offer,
        DateTime DateTendered,
        string TenderClientGuid);
}
