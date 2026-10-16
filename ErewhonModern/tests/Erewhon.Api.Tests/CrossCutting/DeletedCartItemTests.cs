using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Realtime;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises, over HTTP and SignalR against the running Erewhon API, the removal of a deleted item from the carts
/// that hold it. Shop sessions of the fixture clients and an Administrator session of the admin area are opened through
/// <c>POST /api/app/sessions</c>; each shop session listens on the hub <c>/hubs/erewhon</c> through a
/// <see cref="HubProbe"/>. The administrator deletes an item through <c>DELETE /api/app/admin/items/{id}</c> or its
/// category through <c>DELETE /api/app/admin/item-categories/{id}</c>; the cart of the session that held the item is
/// then read through <c>GET /api/app/shop/cart</c> and the hub messages each session received are inspected. Each test
/// starts with the fixture company bound and ends with no company and without the sessions it opened.
/// </summary>
public sealed class DeletedCartItemTests : ErewhonTestBase
{
    private const string SessionsPath = "api/app/sessions";
    private const string CartPath = "api/app/shop/cart";
    private const string CartItemsPath = "api/app/shop/cart/items";
    private const string AdminItemsPath = "api/app/admin/items/";
    private const string AdminItemCategoriesPath = "api/app/admin/item-categories/";

    private const string CartHolderName = "Clive Entworth";
    private const string OtherClientName = "Clissa Entworth";
    private const string CategoryName = "Abstractions";
    private const string CategoryPrefix = "ABST";
    private const string CategoryDescription = "Items that do not model real world observables";
    private const string TenderItemName = "Proxy Tender";
    private const string TenderItemDescription = "A Tender item that will be OrderProxy'd";
    private const decimal TenderItemReserve = 5.50m;
    private const decimal TenderOffer = 25.00m;
    private const int TenderClosureDaysAhead = 7;

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan HubReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HubReadyRoundTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<string> _sessionIds = new();

    private Client? _otherClient;

    private Client CartHolder => Fixture(Builder.MyClient, nameof(TestCompanyBuilder.MyClient));

    private Client OtherClient => Fixture(_otherClient, nameof(_otherClient));

    private ItemCategory FixtureCategory => Fixture(Builder.MyItemCategory, nameof(TestCompanyBuilder.MyItemCategory));

    private RetailItem FixtureRetailItem => Fixture(Builder.MyRetailItem, nameof(TestCompanyBuilder.MyRetailItem));

    private TenderItem FixtureTenderItem => Fixture(Builder.MyTenderItem, nameof(TestCompanyBuilder.MyTenderItem));

    /// <summary>
    /// Creates and binds the company "Jade Software Corporation" through the base set-up, then in it the clients
    /// "Clive Entworth" and "Clissa Entworth", the agent "John Smith", the country "France" with its region "Paris",
    /// the item category "Abstractions" (<c>ABST</c>), the retail item "Proxy Retail" priced 5.50 and the tender item
    /// "Proxy Tender" with reserve 5.50 closing seven days after the server date, both in that category and region,
    /// sold by the agent, with no photo and unsold.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyClient = await Builder.CreateClientAsync();
        Address otherAddress = Builder.CreateTestAddress(
            "42 Elsewhere Road", "Wellington", "New Zealand", "555 7654321", "FAX", "clissa@erewhon.nz", "www.erewhon.nz");
        _otherClient = await Builder.CreateTestClientAsync(OtherClientName, otherAddress);
        Agent agent = await Builder.CreateAgentAsync();
        Builder.MyAgent = agent;
        Country country = await Builder.CreateCountryAsync();
        Builder.MyCountry = country;
        Region region = await Builder.CreateRegionAsync(country);
        Builder.MyRegion = region;
        ItemCategory category = await Builder.CreateItemCategoryAsync(CategoryName, CategoryPrefix, CategoryDescription);
        Builder.MyItemCategory = category;
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(region, category, agent);
        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        Builder.MyTenderItem = await Builder.CreateTestTenderItemAsync(
            TenderItemName,
            TenderItemDescription,
            null,
            region,
            category,
            agent,
            TenderItemReserve,
            serverDate.AddDays(TenderClosureDaysAhead));
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
    /// The shop session of "Clive Entworth" adds the retail item to its cart with the offer 0, and the shop session of
    /// "Clissa Entworth" adds nothing. The Administrator deletes the retail item: the delete succeeds; the cart of
    /// "Clive Entworth" then holds no line for the item; that session receives one <see cref="ErewhonHub.CartItemDeleted"/>
    /// message, for the item, and the <see cref="ErewhonHub.EntityChanged"/> message of the item's deletion; the session
    /// of "Clissa Entworth" receives the same <see cref="ErewhonHub.EntityChanged"/> message and no
    /// <see cref="ErewhonHub.CartItemDeleted"/> message within the quiet window.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ItemDeleteRemovesCartLineAndNotifiesSession()
    {
        long retailItemId = FixtureRetailItem.Id;
        var itemDeleted = new EntityChangedMessage(nameof(RetailItem), retailItemId, ChangeKind.Deleted);

        using AppSession holder = await OpenShopSessionAsync(CartHolder);
        using AppSession other = await OpenShopSessionAsync(OtherClient);
        using AppSession admin = await OpenAdminSessionAsync();
        await using HubProbe holderProbe = await HubProbe.ConnectAsync(Api, holder.SessionId);
        await using HubProbe otherProbe = await HubProbe.ConnectAsync(Api, other.SessionId);
        await AwaitHubGroupsAsync(holderProbe, otherProbe);

        // The retail item is added to the cart of "Clive Entworth".
        CartOperationResponse added = await PostOkAsync<CartOperationResponse>(
            holder.Http, CartItemsPath, new AddCartItemRequest(retailItemId, 0m));
        Assert.True(added.Result.Succeeded, added.Result.FullErrorText);
        Assert.Equal(retailItemId, Assert.Single(added.Cart.Lines).ItemId);

        // The Administrator deletes the retail item.
        OperationResult deleted = await DeleteOkAsync(admin.Http, AdminItemsPath + retailItemId);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        // The cart of "Clive Entworth" no longer holds the item.
        CartView cart = await GetOkAsync<CartView>(holder.Http, CartPath);
        Assert.DoesNotContain(cart.Lines, line => line.ItemId == retailItemId);
        Assert.True(cart.IsEmpty);

        // "Clive Entworth" receives the cart message for the item and the item's deletion.
        Assert.True(
            await holderProbe.WaitForAsync(message => message.Equals(new CartItemDeletedMessage(retailItemId)), MessageTimeout),
            $"The cart holder's session received no {ErewhonHub.CartItemDeleted} for item {retailItemId} within {MessageTimeout}.");
        Assert.True(
            await holderProbe.WaitForAsync(message => message.Equals(itemDeleted), MessageTimeout),
            $"The cart holder's session received no {itemDeleted} within {MessageTimeout}.");
        Assert.Equal(
            retailItemId,
            Assert.Single(holderProbe.Snapshot().OfType<CartItemDeletedMessage>()).ItemId);

        // "Clissa Entworth" receives the item's deletion and no cart message.
        Assert.True(
            await otherProbe.WaitForAsync(message => message.Equals(itemDeleted), MessageTimeout),
            $"The other client's session received no {itemDeleted} within {MessageTimeout}.");
        await otherProbe.AssertNoneAsync(message => message is CartItemDeletedMessage, QuietWindow);
    }

    /// <summary>
    /// The shop session of "Clive Entworth" adds the retail item with the offer 0 and the tender item with the offer
    /// 25.00 to its cart, which then holds both lines. The Administrator deletes the item category of both items: the
    /// delete succeeds; the cart then holds no line; neither item is left in the database; and the session receives
    /// one <see cref="ErewhonHub.CartItemDeleted"/> message for each item and the <see cref="ErewhonHub.EntityChanged"/>
    /// messages of the deletion of the <c>RetailItem</c>, the <c>TenderItem</c> and the <c>ItemCategory</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CategoryDeleteCascadesToCarts()
    {
        long categoryId = FixtureCategory.Id;
        long retailItemId = FixtureRetailItem.Id;
        long tenderItemId = FixtureTenderItem.Id;
        long[] itemIds = [retailItemId, tenderItemId];
        EntityChangedMessage[] deletions =
        [
            new(nameof(RetailItem), retailItemId, ChangeKind.Deleted),
            new(nameof(TenderItem), tenderItemId, ChangeKind.Deleted),
            new(nameof(ItemCategory), categoryId, ChangeKind.Deleted),
        ];

        using AppSession holder = await OpenShopSessionAsync(CartHolder);
        using AppSession admin = await OpenAdminSessionAsync();
        await using HubProbe holderProbe = await HubProbe.ConnectAsync(Api, holder.SessionId);
        await AwaitHubGroupsAsync(holderProbe);

        // The retail and the tender item are added to the cart of "Clive Entworth".
        CartOperationResponse retailAdded = await PostOkAsync<CartOperationResponse>(
            holder.Http, CartItemsPath, new AddCartItemRequest(retailItemId, 0m));
        Assert.True(retailAdded.Result.Succeeded, retailAdded.Result.FullErrorText);
        CartOperationResponse tenderAdded = await PostOkAsync<CartOperationResponse>(
            holder.Http, CartItemsPath, new AddCartItemRequest(tenderItemId, TenderOffer));
        Assert.True(tenderAdded.Result.Succeeded, tenderAdded.Result.FullErrorText);
        Assert.Equal(itemIds.Order(), tenderAdded.Cart.Lines.Select(line => line.ItemId));

        // The Administrator deletes the item category of both items.
        OperationResult deleted = await DeleteOkAsync(admin.Http, AdminItemCategoriesPath + categoryId);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        // The cart of "Clive Entworth" holds no line, and neither item is left.
        CartView cart = await GetOkAsync<CartView>(holder.Http, CartPath);
        Assert.Empty(cart.Lines);
        Assert.True(cart.IsEmpty);
        Assert.False(await GetService<IErewhonDbContext>().Items
            .AsNoTracking()
            .AnyAsync(item => item.Id == retailItemId || item.Id == tenderItemId));

        // "Clive Entworth" receives the cart message for each item and the deletion of both items and the category.
        foreach (long itemId in itemIds)
        {
            Assert.True(
                await holderProbe.WaitForAsync(message => message.Equals(new CartItemDeletedMessage(itemId)), MessageTimeout),
                $"The cart holder's session received no {ErewhonHub.CartItemDeleted} for item {itemId} within {MessageTimeout}.");
        }

        foreach (EntityChangedMessage deletion in deletions)
        {
            Assert.True(
                await holderProbe.WaitForAsync(message => message.Equals(deletion), MessageTimeout),
                $"The cart holder's session received no {deletion} within {MessageTimeout}.");
        }

        Assert.Equal(
            itemIds.Order(),
            holderProbe.Snapshot().OfType<CartItemDeletedMessage>().Select(message => message.ItemId).Order());
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
    /// Sends DELETE <paramref name="path"/>, asserts status 200 and returns the body read as an
    /// <see cref="OperationResult"/>.
    /// </summary>
    private static async Task<OperationResult> DeleteOkAsync(HttpClient http, string path)
    {
        using HttpResponseMessage response = await http.DeleteAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OperationResult? result = await response.Content.ReadFromJsonAsync<OperationResult>(Json);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Opens a shop session for <paramref name="client"/> through <c>POST /api/app/sessions</c>, asserts that it is a
    /// <see cref="SessionRole.Client"/> session of that client, records it for <see cref="TearDownAsync"/> and returns
    /// it with a client of the API whose requests carry its <see cref="SessionMiddleware.HeaderName"/> header. The
    /// caller disposes the session.
    /// </summary>
    private Task<AppSession> OpenShopSessionAsync(Client client) =>
        OpenSessionAsync(
            new CreateSessionRequest(LogonArea.Shop, false, client.Name),
            session =>
            {
                Assert.Equal(SessionRole.Client, session.Role);
                Assert.Equal(client.Id, session.ClientId);
            });

    /// <summary>
    /// Opens an Administrator session of the admin area through <c>POST /api/app/sessions</c>, asserts that it is a
    /// <see cref="SessionRole.Administrator"/> session with no agent and no client, records it for
    /// <see cref="TearDownAsync"/> and returns it with a client of the API whose requests carry its
    /// <see cref="SessionMiddleware.HeaderName"/> header. The caller disposes the session.
    /// </summary>
    private Task<AppSession> OpenAdminSessionAsync() =>
        OpenSessionAsync(
            new CreateSessionRequest(LogonArea.Admin, true, null),
            session =>
            {
                Assert.Equal(SessionRole.Administrator, session.Role);
                Assert.Null(session.AgentId);
                Assert.Null(session.ClientId);
            });

    /// <summary>
    /// Sends <paramref name="request"/> to <c>POST /api/app/sessions</c>, asserts that the logon succeeded with a
    /// session id, runs <paramref name="assertSession"/> on the response, records the session for
    /// <see cref="TearDownAsync"/> and returns it with a client of the API whose requests carry its
    /// <see cref="SessionMiddleware.HeaderName"/> header. A failure disposes the client and is rethrown.
    /// </summary>
    private async Task<AppSession> OpenSessionAsync(
        CreateSessionRequest request,
        Action<CreateSessionResponse> assertSession)
    {
        HttpClient http = Api.CreateHttpClient();
        try
        {
            CreateSessionResponse session = await PostOkAsync<CreateSessionResponse>(http, SessionsPath, request);
            Assert.True(session.Succeeded, session.Message);
            Assert.NotNull(session.SessionId);
            _sessionIds.Add(session.SessionId);
            assertSession(session);

            http.DefaultRequestHeaders.Add(SessionMiddleware.HeaderName, session.SessionId);
            return new AppSession(session.SessionId, http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the countries "Hub Probe 1", "Hub Probe 2", … in the bound company, one per round, waiting at most
    /// <see cref="HubReadyRoundTimeout"/> per probe after each, until every probe of <paramref name="probes"/> has
    /// received the <see cref="ErewhonHub.EntityChanged"/> Added message of one of them; fails when that has not
    /// happened within <see cref="HubReadyTimeout"/>.
    /// </summary>
    private async Task AwaitHubGroupsAsync(params HubProbe[] probes)
    {
        var markerIds = new HashSet<long>();
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int round = 1; ; round++)
        {
            Country marker = await Builder.CreateTestCountryAsync($"Hub Probe {round}");
            markerIds.Add(marker.Id);

            bool ready = true;
            foreach (HubProbe probe in probes)
            {
                ready &= await probe.WaitForAsync(
                    message => message is EntityChangedMessage { EntityType: nameof(Country), Kind: ChangeKind.Added } added
                        && markerIds.Contains(added.Id),
                    HubReadyRoundTimeout);
            }

            if (ready)
            {
                return;
            }

            if (elapsed.Elapsed >= HubReadyTimeout)
            {
                Assert.Fail($"The hub probes received no marker country's Added message within {HubReadyTimeout}.");
            }
        }
    }

    /// <summary>A logged-on session of the API and a client whose requests carry its session header.</summary>
    /// <param name="SessionId">The id of the session.</param>
    /// <param name="Http">The client of the API; disposed with the session.</param>
    private sealed record AppSession(string SessionId, HttpClient Http) : IDisposable
    {
        /// <summary>Disposes <see cref="Http"/>.</summary>
        public void Dispose() => Http.Dispose();
    }

    /// <summary>
    /// A SignalR client of the hub of one host that keeps every <see cref="ErewhonHub.CartItemDeleted"/> and
    /// <see cref="ErewhonHub.EntityChanged"/> message it receives, as <see cref="CartItemDeletedMessage"/> and
    /// <see cref="EntityChangedMessage"/> values in arrival order. Disposing it stops and disposes the connection.
    /// Every member is safe to call from several threads at once.
    /// </summary>
    private sealed class HubProbe : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<object> _messages = [];
        private readonly HubConnection _connection;
        private TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HubProbe(HubConnection connection)
        {
            _connection = connection;
            _connection.On<CartItemDeletedMessage>(ErewhonHub.CartItemDeleted, Receive);
            _connection.On<EntityChangedMessage>(ErewhonHub.EntityChanged, Receive);
        }

        /// <summary>
        /// Connects to <see cref="ErewhonApiHost.HubUrl"/> of <paramref name="sessionId"/> on <paramref name="host"/>
        /// with the JSON protocol reading camel-case property names and enum values as camel-case strings, and returns
        /// the probe once the connection has started. A connection that fails to start is disposed. The caller
        /// disposes the probe.
        /// </summary>
        /// <param name="host">The host whose hub is connected to.</param>
        /// <param name="sessionId">The id of the registered session the connection presents.</param>
        /// <returns>The started probe.</returns>
        public static async Task<HubProbe> ConnectAsync(ErewhonApiHost host, string sessionId)
        {
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(host.HubUrl(sessionId))
                .AddJsonProtocol(options =>
                {
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                })
                .Build();
            var probe = new HubProbe(connection);
            try
            {
                await connection.StartAsync();
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            return probe;
        }

        /// <summary>Returns a copy of the messages received so far, in arrival order.</summary>
        /// <returns>The messages.</returns>
        public object[] Snapshot()
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }

        /// <summary>
        /// Returns whether a received message satisfies <paramref name="predicate"/>, waiting for one at most
        /// <paramref name="timeout"/>.
        /// </summary>
        /// <param name="predicate">The test each received message is put to, under the probe's lock.</param>
        /// <param name="timeout">The longest wait.</param>
        /// <returns><see langword="true"/> when such a message has been received.</returns>
        public Task<bool> WaitForAsync(Func<object, bool> predicate, TimeSpan timeout) =>
            WaitUntilAsync(messages => messages.Exists(message => predicate(message)), timeout);

        /// <summary>
        /// Asserts that no message satisfying <paramref name="predicate"/> has been received when
        /// <paramref name="window"/> has passed, failing as soon as one is received.
        /// </summary>
        /// <param name="predicate">The test each received message is put to, under the probe's lock.</param>
        /// <param name="window">The time during which no such message may arrive.</param>
        /// <returns>A task that completes when the window has passed.</returns>
        public async Task AssertNoneAsync(Func<object, bool> predicate, TimeSpan window)
        {
            bool received = await WaitForAsync(predicate, window);
            Assert.False(
                received,
                $"The session received {string.Join(", ", Snapshot().Where(predicate))} within {window}.");
        }

        /// <summary>Stops and disposes the connection.</summary>
        /// <returns>A task that completes when the connection is disposed.</returns>
        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        /// <summary>
        /// Evaluates <paramref name="condition"/> over the received messages, under the lock, after each arrival until
        /// it holds or <paramref name="timeout"/> has passed, and returns its last value.
        /// </summary>
        private async Task<bool> WaitUntilAsync(Func<List<object>, bool> condition, TimeSpan timeout)
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            while (true)
            {
                Task received;
                lock (_gate)
                {
                    if (condition(_messages))
                    {
                        return true;
                    }

                    received = _received.Task;
                }

                try
                {
                    await received.WaitAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        return condition(_messages);
                    }
                }
            }
        }

        /// <summary>Keeps <paramref name="message"/> and completes the pending wait signal.</summary>
        /// <param name="message">The received message.</param>
        private void Receive(object message)
        {
            TaskCompletionSource received;
            lock (_gate)
            {
                _messages.Add(message);
                received = _received;
                _received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            received.SetResult();
        }
    }
}
