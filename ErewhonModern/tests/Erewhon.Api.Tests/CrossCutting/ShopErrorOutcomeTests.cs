using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises, over HTTP against the running Erewhon API, the error outcomes of the shop search and cart endpoints
/// for a price bound or an offer beyond <c>Decimal[12,2]</c> (JADE error 4033) and for a second cart line for one item
/// (JADE error 1310), and the session's shop search criteria and cart after each. Each test opens a shop session for
/// the fixture client through <c>POST /api/app/sessions</c> and sends its id in the
/// <see cref="SessionMiddleware.HeaderName"/> header; it starts with the fixture company bound and ends with no
/// company and without the sessions it opened.
/// </summary>
public sealed class ShopErrorOutcomeTests : ErewhonTestBase
{
    private const string SessionsPath = "api/app/sessions";
    private const string ShopReferencePath = "api/app/shop/reference";
    private const string ShopSearchPath = "api/app/shop/search";
    private const string CartPath = "api/app/shop/cart";
    private const string CartItemsPath = "api/app/shop/cart/items";

    private const string ClientName = "Shop Client";
    private const decimal RetailItemPrice = 250m;
    private const decimal TenderItemReserve = 300m;
    private const decimal SavedPriceLow = 100m;
    private const decimal SavedPriceHigh = 600m;
    private const decimal BeyondDecimal12_2 = 99_999_999_999m;

    private const string OverflowCode = "4033";
    private const string OverflowText = "Result of expression overflows Decimal precision";
    private const string KeyUsedCode = "1310";
    private const string KeyUsedText = "Key already used in this dictionary";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<string> _sessionIds = new();

    private ItemCategory? _otherCategory;

    /// <summary>
    /// The search cases after the saved criteria low 100, high 600: the low and high price of the second search, and
    /// the low and high price the session's criteria hold after it.
    /// </summary>
    public static TheoryData<decimal, decimal, decimal, decimal> OverflowingSearchBounds => new()
    {
        { 200m, BeyondDecimal12_2, SavedPriceLow, SavedPriceHigh },
        { BeyondDecimal12_2, 500m, SavedPriceLow, 500m },
    };

    private Country FixtureCountry => Fixture(Builder.MyCountry, nameof(TestCompanyBuilder.MyCountry));

    private Region FixtureRegion => Fixture(Builder.MyRegion, nameof(TestCompanyBuilder.MyRegion));

    private ItemCategory FixtureCategory => Fixture(Builder.MyItemCategory, nameof(TestCompanyBuilder.MyItemCategory));

    private ItemCategory OtherCategory => Fixture(_otherCategory, nameof(_otherCategory));

    private RetailItem FixtureRetailItem => Fixture(Builder.MyRetailItem, nameof(TestCompanyBuilder.MyRetailItem));

    private TenderItem FixtureTenderItem => Fixture(Builder.MyTenderItem, nameof(TestCompanyBuilder.MyTenderItem));

    /// <summary>
    /// The criteria <c>POST /api/app/shop/search</c> receives for an empty JSON object, and the criteria of a new
    /// session: no country, region or category, all item kinds, no price bounds and closed tenders excluded.
    /// </summary>
    private static ShopSearchRequest DefaultCriteria => new(null, null, null, ShopItemKind.All, 0m, 0m, false);

    /// <summary>
    /// Creates and binds the company "Jade Software Corporation" through the base set-up, then in it the client
    /// "Shop Client", the agent "Shop Agent", the country "Shop Country" with the region "Shop Region", the categories
    /// "Shop Category A" (<c>SHPA</c>) and "Shop Category B" (<c>SHPB</c>), the retail item "Shop Retail" in category
    /// A at 250.00, and the tender item "Shop Tender" in category B with reserve 300.00 closing seven days after the
    /// server date, both in "Shop Region", sold by the agent, with no photo and unsold.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Address address = Builder.CreateTestAddress(
            "Street", "City", "Country", "Phone", "Fax", "shop@email.com", "www.website.com");
        Builder.MyClient = await Builder.CreateTestClientAsync(ClientName, address);
        Agent agent = await Builder.CreateTestAgentAsync("Shop Agent", address);
        Builder.MyAgent = agent;
        Country country = await Builder.CreateTestCountryAsync("Shop Country");
        Builder.MyCountry = country;
        Region region = await Builder.CreateTestRegionAsync("Shop Region", country);
        Builder.MyRegion = region;
        ItemCategory category = await Builder.CreateTestItemCategoryAsync(
            "Shop Category A", "SHPA", "The category of the retail item");
        Builder.MyItemCategory = category;
        ItemCategory otherCategory = await Builder.CreateTestItemCategoryAsync(
            "Shop Category B", "SHPB", "The category of the tender item");
        _otherCategory = otherCategory;
        Builder.MyRetailItem = await Builder.CreateTestRetailItemAsync(
            "Shop Retail",
            "A retail item for the shop",
            null,
            region,
            category,
            agent,
            RetailItemPrice);
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        Builder.MyTenderItem = await Builder.CreateTestTenderItemAsync(
            "Shop Tender",
            "A tender item for the shop",
            null,
            region,
            otherCategory,
            agent,
            TenderItemReserve,
            serverDate.AddDays(7));
    }

    /// <summary>
    /// Removes every session the test opened from the <see cref="SessionRegistry"/> of the API, with its cart, then
    /// deletes the test's companies through the base tear-down.
    /// </summary>
    /// <returns>A task that completes when the sessions and companies are gone.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            SessionRegistry registry = Api.Services.GetRequiredService<SessionRegistry>();
            foreach (string sessionId in _sessionIds)
            {
                registry.Remove(sessionId);
            }
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    /// <summary>
    /// After a search with country, region, category A, retail only, low 100, high 600 and closed tenders included
    /// (200, the retail item only), a search with no country or region, category B, tender only, closed tenders
    /// excluded and the low and high price of the case answers 500 with the exception <see cref="ErrorOutcome"/>
    /// headed "Error 4033". The session's criteria then hold no country or region, category B and tender only, the low
    /// and high price of the case, and closed tenders included. A search with an empty JSON object then answers 200
    /// with the retail and the tender item, and the criteria hold the defaults.
    /// </summary>
    /// <param name="priceLow">The low price of the second search.</param>
    /// <param name="priceHigh">The high price of the second search.</param>
    /// <param name="expectedLow">The low price the criteria hold after the second search.</param>
    /// <param name="expectedHigh">The high price the criteria hold after the second search.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [MemberData(nameof(OverflowingSearchBounds))]
    public async Task SearchBoundOverflowKeepsLaterCriteria(
        decimal priceLow,
        decimal priceHigh,
        decimal expectedLow,
        decimal expectedHigh)
    {
        using HttpClient http = await CreateShopClientAsync();
        var saved = new ShopSearchRequest(
            FixtureCountry.Id,
            FixtureRegion.Id,
            FixtureCategory.Id,
            ShopItemKind.Retail,
            SavedPriceLow,
            SavedPriceHigh,
            true);

        // The saved search finds the retail item only, and the criteria hold its values.
        ShopSearchResponse savedResults = await PostOkAsync<ShopSearchResponse>(http, ShopSearchPath, saved);
        Assert.Equal(FixtureRetailItem.Id, Assert.Single(savedResults.Items).Id);
        Assert.Equal(saved, await GetCurrentSearchAsync(http));

        // The search with a bound beyond Decimal[12,2] answers the 4033 outcome.
        var overflowing = new ShopSearchRequest(
            null,
            null,
            OtherCategory.Id,
            ShopItemKind.Tender,
            priceLow,
            priceHigh,
            false);
        await PostErrorAsync(http, ShopSearchPath, overflowing, OverflowCode, OverflowText);

        // The criteria hold the request's country, region, category and item kind, the case's bounds and the saved
        // closed-tender flag.
        Assert.Equal(
            new ShopSearchRequest(null, null, OtherCategory.Id, ShopItemKind.Tender, expectedLow, expectedHigh, true),
            await GetCurrentSearchAsync(http));

        // A search with an empty JSON object finds both items, and the criteria hold the defaults.
        ShopSearchResponse defaultResults = await PostOkAsync<ShopSearchResponse>(http, ShopSearchPath, new { });
        Assert.Equal(
            new[] { FixtureRetailItem.Id, FixtureTenderItem.Id },
            defaultResults.Items.Select(row => row.Id));
        Assert.Equal(DefaultCriteria, await GetCurrentSearchAsync(http));
    }

    /// <summary>
    /// Adding the retail item to the session cart answers 200 with a succeeded result and one line; adding it again
    /// answers 500 with the exception <see cref="ErrorOutcome"/> headed "Error 1310", and the cart still holds that
    /// one line.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DuplicateCartItemIsError1310()
    {
        using HttpClient http = await CreateShopClientAsync();
        var request = new AddCartItemRequest(FixtureRetailItem.Id, 0m);

        // The first add succeeds with one line for the retail item.
        CartOperationResponse added = await PostOkAsync<CartOperationResponse>(http, CartItemsPath, request);
        Assert.True(added.Result.Succeeded, added.Result.FullErrorText);
        CartLineView line = Assert.Single(added.Cart.Lines);
        Assert.Equal(FixtureRetailItem.Id, line.ItemId);

        // The second add answers the 1310 outcome.
        await PostErrorAsync(http, CartItemsPath, request, KeyUsedCode, KeyUsedText);

        // The cart holds the first line only.
        CartView cart = await GetOkAsync<CartView>(http, CartPath);
        Assert.Equal(line, Assert.Single(cart.Lines));
    }

    /// <summary>
    /// With a line for the other item in the session cart (the tender item at its reserve 300.00, or the retail item
    /// at 0), adding the retail or the tender item with the offer 99999999999 answers 500 with the exception
    /// <see cref="ErrorOutcome"/> headed "Error 4033", and the cart holds the same lines, total and size as before.
    /// </summary>
    /// <param name="tender">
    /// <see langword="true"/> to add the tender item with the offer; <see langword="false"/> to add the retail item.
    /// </param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CartOfferOverflowIsError4033(bool tender)
    {
        using HttpClient http = await CreateShopClientAsync();
        long itemId = tender ? FixtureTenderItem.Id : FixtureRetailItem.Id;
        AddCartItemRequest otherLine = tender
            ? new AddCartItemRequest(FixtureRetailItem.Id, 0m)
            : new AddCartItemRequest(FixtureTenderItem.Id, TenderItemReserve);

        // The line for the other item is added.
        CartOperationResponse before = await PostOkAsync<CartOperationResponse>(http, CartItemsPath, otherLine);
        Assert.True(before.Result.Succeeded, before.Result.FullErrorText);
        Assert.Equal(otherLine.ItemId, Assert.Single(before.Cart.Lines).ItemId);

        // The offer beyond Decimal[12,2] answers the 4033 outcome.
        await PostErrorAsync(
            http,
            CartItemsPath,
            new AddCartItemRequest(itemId, BeyondDecimal12_2),
            OverflowCode,
            OverflowText);

        // The cart holds the same lines, total and size.
        CartView after = await GetOkAsync<CartView>(http, CartPath);
        Assert.Equal(before.Cart.Lines, after.Lines);
        Assert.Equal(before.Cart.Total, after.Total);
        Assert.Equal(before.Cart.Size, after.Size);
    }

    /// <summary>
    /// Returns <paramref name="value"/>, the fixture object named <paramref name="name"/>, or throws
    /// <see cref="InvalidOperationException"/> when it is <see langword="null"/>.
    /// </summary>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"The fixture {name} is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Sends GET <paramref name="path"/>, asserts status 200 and returns the body read as <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetOkAsync<T>(HttpClient http, string path)
        where T : class
    {
        using HttpResponseMessage response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? body = await response.Content.ReadFromJsonAsync<T>(Json);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// Sends POST <paramref name="path"/> with <paramref name="body"/> as JSON, asserts status 200 and returns the
    /// body read as <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> PostOkAsync<T>(HttpClient http, string path, object body)
        where T : class
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(path, body, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? result = await response.Content.ReadFromJsonAsync<T>(Json);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Sends POST <paramref name="path"/> with <paramref name="body"/> as JSON and asserts status 500 with the
    /// exception <see cref="ErrorOutcome"/> headed "Error " and <paramref name="code"/>, whose body is the web
    /// exception body with <paramref name="code"/>, <paramref name="text"/> and the error item <c>$BaseNotAvailable</c>.
    /// </summary>
    private static async Task PostErrorAsync(HttpClient http, string path, object body, string code, string text)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(path, body, Json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        ErrorOutcome? outcome = await response.Content.ReadFromJsonAsync<ErrorOutcome>(Json);
        Assert.NotNull(outcome);
        Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
        Assert.Equal("Error " + code, outcome.Heading);
        Assert.Equal(
            MessageCatalog.Get(
                MessageKeys.BaseExceptionBodyWeb,
                code,
                text,
                MessageCatalog.Get(MessageKeys.BaseNotAvailable),
                string.Empty),
            outcome.Body);
    }

    /// <summary>Returns the session's current shop search criteria from <c>GET /api/app/shop/reference</c>.</summary>
    private static async Task<ShopSearchRequest> GetCurrentSearchAsync(HttpClient http)
    {
        ShopReferenceResponse reference = await GetOkAsync<ShopReferenceResponse>(http, ShopReferencePath);
        return reference.CurrentSearch;
    }

    /// <summary>
    /// Returns a client of the API whose requests carry the <see cref="SessionMiddleware.HeaderName"/> header of a new
    /// shop session for "Shop Client", opened through <c>POST /api/app/sessions</c> and recorded for
    /// <see cref="TearDownAsync"/>. The caller disposes the client.
    /// </summary>
    private async Task<HttpClient> CreateShopClientAsync()
    {
        HttpClient http = Api.CreateHttpClient();
        try
        {
            CreateSessionResponse session = await PostOkAsync<CreateSessionResponse>(
                http,
                SessionsPath,
                new CreateSessionRequest(LogonArea.Shop, false, ClientName));
            Assert.True(session.Succeeded, session.Message);
            Assert.Equal(SessionRole.Client, session.Role);
            Assert.Equal(Fixture(Builder.MyClient, nameof(TestCompanyBuilder.MyClient)).Id, session.ClientId);
            Assert.NotNull(session.SessionId);

            _sessionIds.Add(session.SessionId);
            http.DefaultRequestHeaders.Add(SessionMiddleware.HeaderName, session.SessionId);
            return http;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }
}
