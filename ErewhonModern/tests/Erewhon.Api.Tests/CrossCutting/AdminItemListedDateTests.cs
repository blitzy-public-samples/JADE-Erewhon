using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the listed date of an admin item update: <c>PUT /api/app/admin/retail-items/{id}</c> and
/// <c>PUT /api/app/admin/tender-items/{id}</c> with every stored value and a <see langword="null"/>
/// <c>listedDate</c> keep the item's stored listed date, and with a <c>listedDate</c> store that date. The code number
/// and a tender item's closure date are unchanged either way.
/// </summary>
public sealed class AdminItemListedDateTests : ErewhonTestBase
{
    /// <summary>The route that opens an app session.</summary>
    private const string SessionsPath = "/api/app/sessions";

    /// <summary>The route that creates a retail item and, with the item id appended, updates one.</summary>
    private const string RetailItemsPath = "/api/app/admin/retail-items";

    /// <summary>The route that creates a tender item and, with the item id appended, updates one.</summary>
    private const string TenderItemsPath = "/api/app/admin/tender-items";

    /// <summary>The name of the item created and updated.</summary>
    private const string ItemName = "Listed Date Item";

    /// <summary>The description of the item created and updated.</summary>
    private const string ItemDescription = "An item whose listed date is checked";

    /// <summary>The price, or reserve price, of the item created and updated.</summary>
    private const decimal ItemPrice = 120.50m;

    /// <summary>The HTTP status 200.</summary>
    private const int Ok = 200;

    /// <summary>The listed date the item is created with.</summary>
    private static readonly DateOnly CreatedListedDate = new(2020, 3, 14);

    /// <summary>The listed date an update that sends one stores.</summary>
    private static readonly DateOnly UpdatedListedDate = new(2021, 5, 20);

    /// <summary>The closure date of the tender item.</summary>
    private static readonly DateOnly TenderClosureDate = new(2030, 6, 30);

    /// <summary>The JSON options of the app endpoints: web defaults with enums as camel-case strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The agent selling the item, created by <see cref="SetUpAsync"/>.</summary>
    private Agent? _agent;

    /// <summary>The category of the item, created by <see cref="SetUpAsync"/>.</summary>
    private ItemCategory? _itemCategory;

    /// <summary>The region the item is listed in, created by <see cref="SetUpAsync"/>.</summary>
    private Region? _region;

    /// <summary>The kind of item created and updated.</summary>
    public enum ItemKind
    {
        /// <summary>A retail item, through <c>/api/app/admin/retail-items</c>.</summary>
        Retail,

        /// <summary>A tender item, through <c>/api/app/admin/tender-items</c>.</summary>
        Tender,
    }

    /// <summary>
    /// Creates the company "Jade Software Corporation" and binds it, then the agent "John Smith", the country "France",
    /// its region "Paris" and the item category "Listed Dates" (<c>LIST</c>) in it.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        _agent = await Builder.CreateAgentAsync();
        Country country = await Builder.CreateCountryAsync();
        _region = await Builder.CreateRegionAsync(country);
        _itemCategory = await Builder.CreateItemCategoryAsync("Listed Dates", "LIST", "Items with a checked listed date");
    }

    /// <summary>
    /// An administrator session creates an item of <paramref name="kind"/> through <c>POST</c> with the listed date
    /// 2020-03-14, which is stored, then sends <c>PUT</c> for its id with every stored value and, as
    /// <paramref name="sendListedDate"/> says, the listed date 2021-05-20 or a <see langword="null"/> one. The update
    /// answers 200 with a succeeded result for the item's id, and the item then has the listed date 2021-05-20 when one
    /// was sent and 2020-03-14 otherwise, its code number unchanged and, for a tender item, its closure date
    /// 2030-06-30 unchanged.
    /// </summary>
    /// <param name="kind">The kind of item created and updated.</param>
    /// <param name="sendListedDate">Whether the update sends a listed date.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(ItemKind.Retail, false)]
    [InlineData(ItemKind.Retail, true)]
    [InlineData(ItemKind.Tender, false)]
    [InlineData(ItemKind.Tender, true)]
    public async Task ItemUpdateKeepsOrReplacesListedDate(ItemKind kind, bool sendListedDate)
    {
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        Assert.NotEqual(serverDate, CreatedListedDate);
        Assert.NotEqual(serverDate, UpdatedListedDate);

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await LogOnAdministratorAsync(http);
        string path = kind == ItemKind.Retail ? RetailItemsPath : TenderItemsPath;

        ItemResult created = await SendItemAsync(
            http,
            HttpMethod.Post,
            path,
            sessionId,
            NewRequest(kind, codeNumber: 0, CreatedListedDate));
        Assert.Equal(Ok, created.Status);
        Assert.True(created.Succeeded, created.FullErrorText);
        long id = created.Id ?? throw new InvalidOperationException("The created item has no id.");
        StoredItem before = await ReadStoredItemAsync(id);
        Assert.Equal(CreatedListedDate, before.ListedDate);

        DateOnly? listedDate = sendListedDate ? UpdatedListedDate : null;

        // Act
        ItemResult updated = await SendItemAsync(
            http,
            HttpMethod.Put,
            $"{path}/{id}",
            sessionId,
            NewRequest(kind, before.CodeNumber, listedDate));

        // Assert
        Assert.Equal(Ok, updated.Status);
        Assert.True(updated.Succeeded, updated.FullErrorText);
        Assert.Equal(id, updated.Id);
        StoredItem after = await ReadStoredItemAsync(id);
        Assert.Equal(sendListedDate ? UpdatedListedDate : CreatedListedDate, after.ListedDate);
        Assert.Equal(before.CodeNumber, after.CodeNumber);
        Assert.Equal(kind == ItemKind.Tender ? TenderClosureDate : (DateOnly?)null, after.ClosureDate);
    }

    /// <summary>
    /// Opens an administrator session of the admin area with <c>POST /api/app/sessions</c> for the bound company's name
    /// and returns its id.
    /// </summary>
    private async Task<string> LogOnAdministratorAsync(HttpClient http)
    {
        string companyName = Builder.MyCompany?.Name
            ?? throw new InvalidOperationException($"The fixture company is created by {nameof(SetUpAsync)}.");

        using HttpResponseMessage response = await http.PostAsJsonAsync(
            SessionsPath,
            new CreateSessionRequest(LogonArea.Admin, Administrator: true, companyName),
            Json);
        Assert.Equal(Ok, (int)response.StatusCode);
        CreateSessionResponse? session = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(Json);
        Assert.NotNull(session);
        Assert.True(session.Succeeded, session.Message);
        Assert.Equal(SessionRole.Administrator, session.Role);
        return session.SessionId ?? throw new InvalidOperationException("The opened session has no id.");
    }

    /// <summary>
    /// Returns the request for an item of <paramref name="kind"/> with the fixture name, description, price, agent,
    /// category and region, no photo, <paramref name="codeNumber"/> and <paramref name="listedDate"/>, and for a tender
    /// item the closure date 2030-06-30.
    /// </summary>
    private object NewRequest(ItemKind kind, int codeNumber, DateOnly? listedDate)
    {
        long agentId = _agent?.Id ?? throw NotSetUp();
        long itemCategoryId = _itemCategory?.Id ?? throw NotSetUp();
        long regionId = _region?.Id ?? throw NotSetUp();

        return kind == ItemKind.Retail
            ? new RetailItemRequest(
                codeNumber,
                ItemName,
                ItemDescription,
                listedDate,
                PhotoBase64: null,
                ItemPrice,
                agentId,
                itemCategoryId,
                regionId)
            : new TenderItemRequest(
                codeNumber,
                ItemName,
                ItemDescription,
                listedDate,
                PhotoBase64: null,
                ItemPrice,
                agentId,
                itemCategoryId,
                regionId,
                TenderClosureDate);
    }

    /// <summary>
    /// Sends <paramref name="request"/> as JSON with <paramref name="method"/> to <paramref name="path"/> in the session
    /// <paramref name="sessionId"/> and returns the status and the operation result's succeeded flag, full error text
    /// and id.
    /// </summary>
    private static async Task<ItemResult> SendItemAsync(
        HttpClient http,
        HttpMethod method,
        string path,
        string sessionId,
        object request)
    {
        using var message = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(request, request.GetType(), options: Json),
        };
        message.Headers.Add(SessionMiddleware.HeaderName, sessionId);

        using HttpResponseMessage response = await http.SendAsync(message);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement result = document.RootElement;
        return new ItemResult(
            (int)response.StatusCode,
            result.GetProperty("succeeded").GetBoolean(),
            result.GetProperty("fullErrorText").GetString(),
            result.GetProperty("id").ValueKind == JsonValueKind.Null ? null : result.GetProperty("id").GetInt64());
    }

    /// <summary>
    /// Reads, untracked in a new scope of <see cref="ErewhonTestBase.Api"/>, the code number, listed date and closure
    /// date (<see langword="null"/> for a retail item) of the item with row id <paramref name="id"/>.
    /// </summary>
    private async Task<StoredItem> ReadStoredItemAsync(long id)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();
        return await db.Items
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new StoredItem(
                item.CodeNumber,
                item.ListedDate,
                item is TenderItem ? (DateOnly?)((TenderItem)item).ClosureDate : null))
            .SingleAsync();
    }

    /// <summary>Returns the exception for a fixture read before <see cref="SetUpAsync"/> has run.</summary>
    private static InvalidOperationException NotSetUp() =>
        new($"The fixtures are created by {nameof(SetUpAsync)}.");

    /// <summary>The status of an item request and the operation result it answered with.</summary>
    /// <param name="Status">The HTTP status code.</param>
    /// <param name="Succeeded">The result's <c>succeeded</c> value.</param>
    /// <param name="FullErrorText">The result's <c>fullErrorText</c> value.</param>
    /// <param name="Id">The result's <c>id</c> value, or <see langword="null"/> when it is null.</param>
    private sealed record ItemResult(int Status, bool Succeeded, string? FullErrorText, long? Id);

    /// <summary>The stored values of an item that an update must leave or set.</summary>
    /// <param name="CodeNumber">The item's code number.</param>
    /// <param name="ListedDate">The item's listed date.</param>
    /// <param name="ClosureDate">The tender item's closure date, or <see langword="null"/> for a retail item.</param>
    private sealed record StoredItem(int CodeNumber, DateOnly ListedDate, DateOnly? ClosureDate);
}
