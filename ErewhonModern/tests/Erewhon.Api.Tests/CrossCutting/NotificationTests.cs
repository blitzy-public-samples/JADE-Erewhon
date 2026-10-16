using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Realtime;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises change notification over the real Erewhon API on Kestrel, the PostgreSQL test database and SignalR:
/// a second session connected to <c>/hubs/erewhon</c> receives <see cref="ErewhonHub.EntityChanged"/> only after the
/// change commits, nothing for rolled-back work and one message for repeated identical changes in one transaction
/// (B-64); the editor of a soft-locked row receives <see cref="ErewhonHub.SaveOutstandingChanges"/> and nothing else
/// changes (B-40); and a sale, a closure award, a client delete and a country delete announce the inverse sides,
/// database cascades and cleared references they change, with no update for a row they delete (B-76). Each test opens
/// its sessions through <c>POST /api/app/sessions</c>, connects its hub probes, then creates its fixtures in the
/// test's company and waits until the probes have received the fixtures' last messages; every assertion is filtered by
/// the ids the test created. Each test ends without the sessions it opened and with no company.
/// </summary>
public sealed class NotificationTests : ErewhonTestBase
{
    private const string SessionsPath = "api/app/sessions";
    private const string AgentsPath = "api/app/admin/agents/";
    private const string ClientsPath = "api/app/admin/clients/";
    private const string CountriesPath = "api/app/admin/countries/";
    private const string CloseNowPath = "api/app/admin/tenders/close-now";
    private const string CartItemsPath = "api/app/shop/cart/items";
    private const string CheckoutPath = "api/app/shop/checkout";
    private const string EditLocksPath = "api/app/edit-locks/";

    private const string CategoryName = "Abstractions";
    private const string CategoryPrefix = "ABST";
    private const string CategoryDescription = "Items that do not model real world observables";

    private const decimal Reserve = 100m;
    private const decimal Offer = 150m;

    private const string AdvisoryLockSql = "SELECT pg_advisory_lock(hashtextextended(@name, 0))";
    private const string AdvisoryUnlockSql = "SELECT pg_advisory_unlock(hashtextextended(@name, 0))";

    private const string AdvisoryLockWaiterSql = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND NOT granted
              AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND pid <> pg_backend_pid()
              AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@name, 0))
        """;

    private static readonly TimeSpan HubTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<string> _sessionIds = [];

    /// <summary>The T-01 address: 314 Nohwere Lane, Christchurch, New Zealand.</summary>
    private static AddressFields NohwereLane => new(
        "314 Nohwere Lane",
        "Christchurch",
        "New Zealand",
        "555 1234567",
        "FAX",
        "admin@Erewhon.nz",
        "www.jadeworld.com");

    /// <summary>
    /// With an advisory lock held by a test connection on <see cref="LockNames.CompanyAllAgents"/> of the company,
    /// <c>PUT /api/app/admin/agents/{id}</c> renaming "John Smith" to "Jane Doe" waits on that lock: for the quiet
    /// window the listener session receives nothing for the agent, the row still reads "John Smith" and the request has
    /// not answered. Once the lock is released the request answers 200 with a succeeded result, the listener receives
    /// <c>EntityChanged(Agent, id, updated)</c> after the release, and the row reads "Jane Doe".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeliveredAfterCommit()
    {
        string editor = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Agent agent = await Builder.CreateAgentAsync();
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Added));
        Assert.Equal("John Smith", await ReadAgentNameAsync(agent.Id));

        using HttpClient http = CreateSessionClient(editor);
        string lockName = LockNames.CompanyAllAgents(agent.CompanyId);
        await using NpgsqlConnection holder = await OpenConnectionAsync();
        await ExecuteWithNameAsync(holder, AdvisoryLockSql, lockName);

        Task<HttpResponseMessage> update;
        long releasedAfter;
        try
        {
            update = http.PutAsJsonAsync(AgentsPath + agent.Id, new AgentRequest("Jane Doe", NohwereLane), Json);
            await WaitForLockWaiterAsync(holder, lockName, update);

            // While the update waits on the lock, nothing of the agent reaches the listener and the row is unchanged.
            await probe.AssertNoneAsync(
                message => message.Sequence > created.Sequence && IsAnyChangeOf(message, nameof(Agent), agent.Id),
                QuietWindow);
            Assert.Equal("John Smith", await ReadAgentNameAsync(agent.Id));
            Assert.False(update.IsCompleted, "The update answered while the advisory lock was held.");
        }
        finally
        {
            releasedAfter = probe.LastSequence;
            await ExecuteWithNameAsync(holder, AdvisoryUnlockSql, lockName);
        }

        using HttpResponseMessage response = await update;
        OperationResult result = await ReadOkAsync<OperationResult>(response);
        Assert.True(result.Succeeded, result.FullErrorText);

        // The update reaches the listener after the release, and the row holds the new name.
        ReceivedMessage updated = await probe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Updated));
        Assert.True(
            updated.Sequence > releasedAfter,
            $"The agent update was received as message {updated.Sequence}, before the lock was released after message {releasedAfter}.");
        Assert.Equal("Jane Doe", await ReadAgentNameAsync(agent.Id));
    }

    /// <summary>
    /// With tender 1 of 150.00 by "Clive Entworth" on the tender item "First Tender" at the server timestamp, a tender
    /// of the same offer at the same timestamp on "Second Tender", run through <see cref="TenderService"/> in
    /// <see cref="TransactionMode.Own"/> in a new scope of the API, throws with SQLSTATE
    /// <see cref="PostgresErrorMapper.UniqueViolation"/> and leaves that scope's <see cref="NotificationQueue"/> empty.
    /// For the quiet window the listener session receives no message for a tender other than tender 1 and none for
    /// "Second Tender", and no tender on "Second Tender" is stored. An agent update over HTTP afterwards reaches the
    /// listener.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DiscardedOnRollback()
    {
        string editor = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Client client = await Builder.CreateClientAsync();
        Listing listing = await CreateListingAsync();
        DateOnly closureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(7);
        TenderItem first = await CreateTenderItemAsync(listing, "First Tender", closureDate);
        TenderItem second = await CreateTenderItemAsync(listing, "Second Tender", closureDate);
        DateTime tendered = await GetService<IServerClock>().GetServerTimeStampAsync();
        Tender firstTender = await Builder.CreateTestTenderAsync(Offer, tendered, client, first);
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(Tender), firstTender.Id, ChangeKind.Added));

        // The second tender fails on the database's unique key, and its scope queues nothing.
        Exception failure;
        await using (AsyncServiceScope scope = Api.Services.CreateAsyncScope())
        {
            TenderService tenders = scope.ServiceProvider.GetRequiredService<TenderService>();
            var command = new TenderCommand
            {
                TenderItemId = second.Id,
                ClientId = client.Id,
                Offer = Offer,
                DateTendered = tendered,
            };

            failure = await Assert.ThrowsAnyAsync<Exception>(
                () => tenders.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own));
            Assert.Empty(scope.ServiceProvider.GetRequiredService<NotificationQueue>().Pending);
        }

        Assert.Equal(PostgresErrorMapper.UniqueViolation, PostgresErrorMapper.GetSqlState(failure));

        // Nothing of the rolled-back tender reaches the listener, and no tender on the second item is stored.
        await probe.AssertNoneAsync(
            message => message.Sequence > created.Sequence
                && message.Payload is EntityChangedMessage change
                && ((change.EntityType == nameof(Tender) && change.Id != firstTender.Id)
                    || (change.EntityType == nameof(TenderItem) && change.Id == second.Id)),
            QuietWindow);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM tenders WHERE tender_item_id = @id", second.Id));

        // A committed agent update afterwards reaches the listener.
        using HttpClient http = CreateSessionClient(editor);
        OperationResult control = await SendOkAsync<OperationResult>(
            http,
            HttpMethod.Put,
            AgentsPath + listing.Agent.Id,
            new AgentRequest(listing.Agent.Name, NohwereLane with { Phone = "555 7654321" }));
        Assert.True(control.Succeeded, control.FullErrorText);
        await probe.WaitForAsync(Change(nameof(Agent), listing.Agent.Id, ChangeKind.Updated), created.Sequence);
    }

    /// <summary>
    /// In one transaction of a new scope of the API, two <see cref="TransactionMode.Ambient"/> updates of the agent
    /// "John Smith", the first changing the phone and the second the fax, leave exactly one
    /// <c>ChangeEvent(Agent, id, Updated)</c> in the scope's <see cref="NotificationQueue"/>. After the commit and the
    /// flush the listener session receives exactly one <c>EntityChanged(Agent, id, updated)</c>, and no second one
    /// within the quiet window.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DuplicatesCollapse()
    {
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Agent agent = await Builder.CreateAgentAsync();
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Added));

        await using (AsyncServiceScope scope = Api.Services.CreateAsyncScope())
        {
            IServiceProvider services = scope.ServiceProvider;
            IUnitOfWork unitOfWork = services.GetRequiredService<IUnitOfWork>();
            AgentService agents = services.GetRequiredService<AgentService>();
            NotificationQueue queue = services.GetRequiredService<NotificationQueue>();

            await unitOfWork.BeginAsync();
            Agent? stored = await agents.FindByIdAsync(agent.Id);
            Assert.NotNull(stored);

            OperationResult phoneChanged = await agents.ExecuteAsync(
                AgentUpdate(stored, address => address.Phone = "555 7654321"),
                TransactionType.Persist,
                TransactionMode.Ambient);
            Assert.True(phoneChanged.Succeeded, phoneChanged.FullErrorText);

            OperationResult faxChanged = await agents.ExecuteAsync(
                AgentUpdate(stored, address => address.Fax = "FAX2"),
                TransactionType.Persist,
                TransactionMode.Ambient);
            Assert.True(faxChanged.Succeeded, faxChanged.FullErrorText);

            // Both saves queue the same change, which the queue holds once.
            Assert.Equal(new[] { new ChangeEvent(nameof(Agent), agent.Id, ChangeKind.Updated) }, queue.Pending);

            await unitOfWork.CommitAsync();
            await queue.FlushAsync();
        }

        // Exactly one update of the agent reaches the listener.
        await probe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Updated), created.Sequence);
        IReadOnlyList<ReceivedMessage> updates = await probe.SettleAsync(
            QuietWindow,
            message => message.Sequence > created.Sequence
                && message.Payload.Equals(Change(nameof(Agent), agent.Id, ChangeKind.Updated)));
        Assert.Single(updates);
    }


    /// <summary>
    /// The editor session soft-locks the agent "John Smith" through <c>POST /api/app/edit-locks/Agent/{id}</c>, and
    /// both sessions receive the agent's update. The requester session's <c>POST .../save-request</c> answers 200 with
    /// <see cref="SaveRequestResponse.Sent"/> set; the editor receives <see cref="SaveOutstandingChangesMessage"/> for
    /// the agent, and the requester receives none. For the quiet window neither session receives a further change of
    /// the agent, the agent's name, lock owner and lock time are unchanged, and the editor's
    /// <c>GET /api/app/edit-locks/Agent/{id}</c> still reports the lock as its own.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SaveRequestReachesEditorWithoutEffect()
    {
        string editor = await OpenAdminSessionAsync();
        string requester = await OpenAdminSessionAsync();
        await using HubProbe editorProbe = await ConnectProbeAsync(editor);
        await using HubProbe requesterProbe = await ConnectProbeAsync(requester);

        Agent agent = await Builder.CreateAgentAsync();
        ReceivedMessage editorCreated = await editorProbe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Added));
        ReceivedMessage requesterCreated = await requesterProbe.WaitForAsync(Change(nameof(Agent), agent.Id, ChangeKind.Added));

        using HttpClient editorHttp = CreateSessionClient(editor);
        using HttpClient requesterHttp = CreateSessionClient(requester);
        string lockPath = EditLocksPath + nameof(Agent) + "/" + agent.Id;

        // The editor's soft lock is stored and announced to both sessions.
        OperationResult locked = await SendOkAsync<OperationResult>(editorHttp, HttpMethod.Post, lockPath, null);
        Assert.True(locked.Succeeded, locked.FullErrorText);
        AgentLockRow before = await ReadAgentLockAsync(agent.Id);
        Assert.Equal(editor, before.LockedBySession);
        Assert.NotNull(before.LockedTimestamp);
        long editorLocked = (await editorProbe.WaitForAsync(
            Change(nameof(Agent), agent.Id, ChangeKind.Updated), editorCreated.Sequence)).Sequence;
        long requesterLocked = (await requesterProbe.WaitForAsync(
            Change(nameof(Agent), agent.Id, ChangeKind.Updated), requesterCreated.Sequence)).Sequence;

        // The save request reaches the editor only.
        SaveRequestResponse sent = await SendOkAsync<SaveRequestResponse>(
            requesterHttp, HttpMethod.Post, lockPath + "/save-request", null);
        Assert.True(sent.Sent);
        ReceivedMessage request = await editorProbe.WaitForAsync(
            message => message.Sequence > editorLocked
                && message.Payload is SaveOutstandingChangesMessage save
                && save.Id == agent.Id
                && string.Equals(save.EntityType, nameof(Agent), StringComparison.OrdinalIgnoreCase),
            $"SaveOutstandingChanges for agent {agent.Id}");
        await requesterProbe.AssertNoneAsync(
            message => message.Payload is SaveOutstandingChangesMessage save && save.Id == agent.Id,
            QuietWindow);

        // The request changes nothing: no further change of the agent, the same stored lock, still the editor's.
        await editorProbe.AssertNoneAsync(
            message => message.Sequence > request.Sequence && IsAnyChangeOf(message, nameof(Agent), agent.Id),
            TimeSpan.Zero);
        await requesterProbe.AssertNoneAsync(
            message => message.Sequence > requesterLocked && IsAnyChangeOf(message, nameof(Agent), agent.Id),
            TimeSpan.Zero);
        Assert.Equal(before, await ReadAgentLockAsync(agent.Id));
        SoftLockStatus status = await SendOkAsync<SoftLockStatus>(editorHttp, HttpMethod.Get, lockPath, null);
        Assert.True(status.IsLocked);
        Assert.True(status.IsLockedByCaller);

        OperationResult unlocked = await SendOkAsync<OperationResult>(editorHttp, HttpMethod.Delete, lockPath, null);
        Assert.True(unlocked.Succeeded, unlocked.FullErrorText);
    }

    /// <summary>
    /// The shop session of "Clive Entworth" adds the unsold retail item "Proxy Retail" to its cart and checks out, each
    /// answering a succeeded result. The listener session receives <c>EntityChanged(RetailItem, item, updated)</c> and
    /// <c>EntityChanged(RetailSale, sale, added)</c> for the stored sale of the item.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SaleRaisesItemUpdatedForSecondSession()
    {
        Client client = await Builder.CreateClientAsync();
        string shopper = await OpenShopSessionAsync(client.Name);
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Listing listing = await CreateListingAsync();
        RetailItem item = await Builder.CreateRetailItemAsync(listing.Region, listing.Category, listing.Agent);
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(RetailItem), item.Id, ChangeKind.Added));

        using HttpClient http = CreateSessionClient(shopper);
        CartOperationResponse added = await SendOkAsync<CartOperationResponse>(
            http, HttpMethod.Post, CartItemsPath, new AddCartItemRequest(item.Id, 0m));
        Assert.True(added.Result.Succeeded, added.Result.FullErrorText);
        CheckoutResponse checkout = await SendOkAsync<CheckoutResponse>(http, HttpMethod.Post, CheckoutPath, null);
        Assert.True(checkout.Result.Succeeded, checkout.Result.FullErrorText);

        long saleId = await ReadSaleIdAsync(item.Id);
        await probe.WaitForAllAsync(
            created.Sequence,
            Change(nameof(RetailItem), item.Id, ChangeKind.Updated),
            Change(nameof(RetailSale), saleId, ChangeKind.Added));
    }

    /// <summary>
    /// With the tender item "Closing Tender" closing on the day after the server date and one tender of 150.00 on it,
    /// <c>POST /api/app/admin/tenders/close-now</c> at that day answers one award. The listener session receives
    /// <c>EntityChanged(TenderItem, item, updated)</c>, <c>EntityChanged(Tender, tender, updated)</c> and
    /// <c>EntityChanged(TenderSale, sale, added)</c> for the stored sale of the item.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClosureRaisesItemAndTenderUpdated()
    {
        string closer = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Client client = await Builder.CreateClientAsync();
        Listing listing = await CreateListingAsync();
        DateOnly closureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(1);
        TenderItem item = await CreateTenderItemAsync(listing, "Closing Tender", closureDate);
        Tender tender = await Builder.CreateTenderAsync(client, item, Offer);
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(Tender), tender.Id, ChangeKind.Added));

        using HttpClient http = CreateSessionClient(closer);
        CloseNowResponse closed = await SendOkAsync<CloseNowResponse>(
            http, HttpMethod.Post, CloseNowPath, new CloseNowRequest(closureDate));
        Assert.Equal(1, closed.NumClosed);

        long saleId = await ReadSaleIdAsync(item.Id);
        await probe.WaitForAllAsync(
            created.Sequence,
            Change(nameof(TenderItem), item.Id, ChangeKind.Updated),
            Change(nameof(Tender), tender.Id, ChangeKind.Updated),
            Change(nameof(TenderSale), saleId, ChangeKind.Added));
    }


    /// <summary>
    /// "Clive Entworth" holds a tender of 150.00 on the unsold tender item "First Tender" and one of 160.00 on
    /// "Second Tender". <c>DELETE /api/app/admin/clients/{id}</c> answers a succeeded result, and the listener session
    /// receives <c>EntityChanged(Tender, ·, deleted)</c> for both tenders and <c>EntityChanged(Client, client,
    /// deleted)</c>; for the quiet window it receives no update of either tender or of the client.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClientDeleteRaisesTenderDeleted()
    {
        string editor = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Client client = await Builder.CreateClientAsync();
        Listing listing = await CreateListingAsync();
        DateOnly closureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(7);
        TenderItem first = await CreateTenderItemAsync(listing, "First Tender", closureDate);
        TenderItem second = await CreateTenderItemAsync(listing, "Second Tender", closureDate);
        Tender firstTender = await Builder.CreateTenderAsync(client, first, Offer);
        Tender secondTender = await Builder.CreateTenderAsync(client, second, Offer + 10m);
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(Tender), secondTender.Id, ChangeKind.Added));

        using HttpClient http = CreateSessionClient(editor);
        OperationResult deleted = await SendOkAsync<OperationResult>(http, HttpMethod.Delete, ClientsPath + client.Id, null);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        await probe.WaitForAllAsync(
            created.Sequence,
            Change(nameof(Tender), firstTender.Id, ChangeKind.Deleted),
            Change(nameof(Tender), secondTender.Id, ChangeKind.Deleted),
            Change(nameof(Client), client.Id, ChangeKind.Deleted));
        await probe.AssertNoneAsync(
            message => message.Sequence > created.Sequence
                && IsChange(
                    message,
                    ChangeKind.Updated,
                    (nameof(Tender), firstTender.Id),
                    (nameof(Tender), secondTender.Id),
                    (nameof(Client), client.Id)),
            QuietWindow);
    }

    /// <summary>
    /// "Clive Entworth" bought the retail item "Proxy Retail" and won the tender item "Proxy Tender" through a tender
    /// sale. <c>DELETE /api/app/admin/clients/{id}</c> answers a succeeded result, and the listener session receives
    /// <c>EntityChanged(RetailItem, item, updated)</c>, <c>EntityChanged(TenderItem, item, updated)</c> and
    /// <c>EntityChanged(·, ·, deleted)</c> for the retail sale, the tender sale, the tender and the client. Both items
    /// are still stored with no sale; for the quiet window the listener receives no update of the client, the sales
    /// or the tender.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClientDeleteRefreshesBoughtItemsForSecondSession()
    {
        string editor = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Client client = await Builder.CreateClientAsync();
        Listing listing = await CreateListingAsync();
        RetailItem retailItem = await Builder.CreateRetailItemAsync(listing.Region, listing.Category, listing.Agent);
        RetailSale retailSale = await Builder.CreateRetailSaleAsync(retailItem, client);
        DateOnly closureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(7);
        TenderItem tenderItem = await CreateTenderItemAsync(listing, "Proxy Tender", closureDate);
        Tender tender = await Builder.CreateTenderAsync(client, tenderItem, Offer);
        TenderSale tenderSale = await Builder.CreateTenderSaleAsync(tenderItem, tender, client);
        await probe.WaitForAllAsync(
            0,
            Change(nameof(TenderSale), tenderSale.Id, ChangeKind.Added),
            Change(nameof(TenderItem), tenderItem.Id, ChangeKind.Updated),
            Change(nameof(Tender), tender.Id, ChangeKind.Updated));
        long created = probe.LastSequence;

        using HttpClient http = CreateSessionClient(editor);
        OperationResult deleted = await SendOkAsync<OperationResult>(http, HttpMethod.Delete, ClientsPath + client.Id, null);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        await probe.WaitForAllAsync(
            created,
            Change(nameof(RetailItem), retailItem.Id, ChangeKind.Updated),
            Change(nameof(TenderItem), tenderItem.Id, ChangeKind.Updated),
            Change(nameof(RetailSale), retailSale.Id, ChangeKind.Deleted),
            Change(nameof(TenderSale), tenderSale.Id, ChangeKind.Deleted),
            Change(nameof(Tender), tender.Id, ChangeKind.Deleted),
            Change(nameof(Client), client.Id, ChangeKind.Deleted));

        long[] itemIds = [retailItem.Id, tenderItem.Id];
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM items WHERE id = ANY(@id)", itemIds));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM sales WHERE item_id = ANY(@id)", itemIds));
        await probe.AssertNoneAsync(
            message => message.Sequence > created
                && IsChange(
                    message,
                    ChangeKind.Updated,
                    (nameof(Client), client.Id),
                    (nameof(RetailSale), retailSale.Id),
                    (nameof(TenderSale), tenderSale.Id),
                    (nameof(Tender), tender.Id)),
            QuietWindow);
    }

    /// <summary>
    /// The country "France" holds the regions "Paris" and "Lyon", with the retail item "Paris Retail" in the first and
    /// the tender item "Lyon Tender" in the second. <c>DELETE /api/app/admin/countries/{id}</c> answers a succeeded
    /// result, and the listener session receives <c>EntityChanged(Region, ·, deleted)</c> for both regions,
    /// <c>EntityChanged(Country, country, deleted)</c>, <c>EntityChanged(RetailItem, item, updated)</c> and
    /// <c>EntityChanged(TenderItem, item, updated)</c>. Both items are stored with no region; for the quiet window the
    /// listener receives no update of the country or either region.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CountryDeleteRefreshesItemsForSecondSession()
    {
        string editor = await OpenAdminSessionAsync();
        string listener = await OpenAdminSessionAsync();
        await using HubProbe probe = await ConnectProbeAsync(listener);

        Listing listing = await CreateListingAsync();
        Region lyon = await Builder.CreateTestRegionAsync("Lyon", listing.Country);
        RetailItem parisItem = await Builder.CreateTestRetailItemAsync(
            "Paris Retail", "A retail item listed in Paris", null, listing.Region, listing.Category, listing.Agent, Reserve);
        DateOnly closureDate = (await GetService<IServerClock>().GetServerDateAsync()).AddDays(7);
        TenderItem lyonItem = await Builder.CreateTestTenderItemAsync(
            "Lyon Tender", "A tender item listed in Lyon", null, lyon, listing.Category, listing.Agent, Reserve, closureDate);
        ReceivedMessage created = await probe.WaitForAsync(Change(nameof(TenderItem), lyonItem.Id, ChangeKind.Added));

        using HttpClient http = CreateSessionClient(editor);
        OperationResult deleted = await SendOkAsync<OperationResult>(
            http, HttpMethod.Delete, CountriesPath + listing.Country.Id, null);
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        await probe.WaitForAllAsync(
            created.Sequence,
            Change(nameof(Region), listing.Region.Id, ChangeKind.Deleted),
            Change(nameof(Region), lyon.Id, ChangeKind.Deleted),
            Change(nameof(Country), listing.Country.Id, ChangeKind.Deleted),
            Change(nameof(RetailItem), parisItem.Id, ChangeKind.Updated),
            Change(nameof(TenderItem), lyonItem.Id, ChangeKind.Updated));

        Assert.Equal(
            2L,
            await CountAsync("SELECT count(*) FROM items WHERE id = ANY(@id) AND region_id IS NULL", new[] { parisItem.Id, lyonItem.Id }));
        await probe.AssertNoneAsync(
            message => message.Sequence > created.Sequence
                && IsChange(
                    message,
                    ChangeKind.Updated,
                    (nameof(Country), listing.Country.Id),
                    (nameof(Region), listing.Region.Id),
                    (nameof(Region), lyon.Id)),
            QuietWindow);
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

    /// <summary>Returns the <see cref="EntityChangedMessage"/> of one change of one row.</summary>
    private static EntityChangedMessage Change(string entityType, long id, ChangeKind kind) => new(entityType, id, kind);

    /// <summary>Returns whether <paramref name="message"/> is any <see cref="EntityChangedMessage"/> of the row.</summary>
    private static bool IsAnyChangeOf(ReceivedMessage message, string entityType, long id) =>
        message.Payload is EntityChangedMessage change && change.EntityType == entityType && change.Id == id;

    /// <summary>
    /// Returns whether <paramref name="message"/> is an <see cref="EntityChangedMessage"/> of <paramref name="kind"/>
    /// for one of <paramref name="rows"/>.
    /// </summary>
    private static bool IsChange(ReceivedMessage message, ChangeKind kind, params (string EntityType, long Id)[] rows) =>
        message.Payload is EntityChangedMessage change
        && change.Kind == kind
        && rows.Contains((change.EntityType, change.Id));

    /// <summary>
    /// Returns a new agent command populated from <paramref name="agent"/> with <paramref name="change"/> applied to
    /// its address.
    /// </summary>
    private static AgentCommand AgentUpdate(Agent agent, Action<AddressCommand> change)
    {
        var command = new AgentCommand();
        command.PopulateFromObject(agent);
        Assert.NotNull(command.Address);
        change(command.Address);
        return command;
    }

    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="path"/>, with <paramref name="body"/> as JSON when it is not
    /// <see langword="null"/>, and returns the 200 body read as <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> SendOkAsync<T>(HttpClient http, HttpMethod method, string path, object? body)
        where T : class
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        return await ReadOkAsync<T>(response);
    }

    /// <summary>
    /// Asserts that <paramref name="response"/> has status 200, failing with its status and body otherwise, and returns
    /// its body read as <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> ReadOkAsync<T>(HttpResponseMessage response)
        where T : class
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            string content = await response.Content.ReadAsStringAsync();
            Assert.Fail(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered "
                + $"{(int)response.StatusCode}: {content}");
        }

        T? body = await response.Content.ReadFromJsonAsync<T>(Json);
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> on <paramref name="connection"/> with <paramref name="lockName"/> as the parameter
    /// <c>@name</c>.
    /// </summary>
    private static async Task ExecuteWithNameAsync(NpgsqlConnection connection, string sql, string lockName)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", lockName);
        await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Polls <c>pg_locks</c> every <see cref="LockPollInterval"/> until a session of the current database other than
    /// <paramref name="holder"/>'s waits on the advisory lock <paramref name="lockName"/>; fails when
    /// <paramref name="pending"/> completes first or when no session waits within <see cref="HubTimeout"/>.
    /// </summary>
    private static async Task WaitForLockWaiterAsync(NpgsqlConnection holder, string lockName, Task pending)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            await using (var command = new NpgsqlCommand(AdvisoryLockWaiterSql, holder))
            {
                command.Parameters.AddWithValue("name", lockName);
                if (await command.ExecuteScalarAsync() is true)
                {
                    return;
                }
            }

            if (pending.IsCompleted)
            {
                Assert.Fail($"The request completed before it waited on the advisory lock {lockName}.");
            }

            if (elapsed.Elapsed >= HubTimeout)
            {
                Assert.Fail($"No other session waited on the advisory lock {lockName} within {HubTimeout}.");
            }

            await Task.Delay(LockPollInterval);
        }
    }

    /// <summary>
    /// Opens an admin-area session for the Administrator option and returns its id, recorded for
    /// <see cref="TearDownAsync"/>.
    /// </summary>
    private Task<string> OpenAdminSessionAsync() =>
        OpenSessionAsync(new CreateSessionRequest(LogonArea.Admin, true, null));

    /// <summary>
    /// Opens a shop-area session for the client <paramref name="clientName"/> and returns its id, recorded for
    /// <see cref="TearDownAsync"/>.
    /// </summary>
    private Task<string> OpenShopSessionAsync(string clientName) =>
        OpenSessionAsync(new CreateSessionRequest(LogonArea.Shop, false, clientName));

    /// <summary>
    /// Sends <c>POST /api/app/sessions</c> with <paramref name="request"/>, asserts a succeeded logon with a session id
    /// and returns the id, recorded for <see cref="TearDownAsync"/>.
    /// </summary>
    private async Task<string> OpenSessionAsync(CreateSessionRequest request)
    {
        using HttpClient http = Api.CreateHttpClient();
        CreateSessionResponse session = await SendOkAsync<CreateSessionResponse>(http, HttpMethod.Post, SessionsPath, request);
        Assert.True(session.Succeeded, session.Message);
        Assert.NotNull(session.SessionId);

        _sessionIds.Add(session.SessionId);
        return session.SessionId;
    }

    /// <summary>
    /// Returns a client of the API whose requests carry <paramref name="sessionId"/> in the
    /// <see cref="SessionMiddleware.HeaderName"/> header. The caller disposes it.
    /// </summary>
    private HttpClient CreateSessionClient(string sessionId)
    {
        HttpClient http = Api.CreateHttpClient();
        http.DefaultRequestHeaders.Add(SessionMiddleware.HeaderName, sessionId);
        return http;
    }

    /// <summary>Connects a <see cref="HubProbe"/> to the hub of the API for <paramref name="sessionId"/>.</summary>
    private Task<HubProbe> ConnectProbeAsync(string sessionId) => HubProbe.ConnectAsync(Api.HubUrl(sessionId));

    /// <summary>
    /// Creates, in the test's company, the agent "John Smith", the country "France" with its region "Paris" and the
    /// item category "Abstractions".
    /// </summary>
    private async Task<Listing> CreateListingAsync()
    {
        Agent agent = await Builder.CreateAgentAsync();
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory category = await Builder.CreateItemCategoryAsync(CategoryName, CategoryPrefix, CategoryDescription);
        return new Listing(agent, country, region, category);
    }

    /// <summary>
    /// Creates the unsold tender item <paramref name="name"/> of <paramref name="listing"/> with the reserve
    /// <see cref="Reserve"/>, no photo and the closure date <paramref name="closureDate"/>.
    /// </summary>
    private Task<TenderItem> CreateTenderItemAsync(Listing listing, string name, DateOnly closureDate) =>
        Builder.CreateTestTenderItemAsync(
            name,
            "A tender item for notification tests",
            null,
            listing.Region,
            listing.Category,
            listing.Agent,
            Reserve,
            closureDate);

    /// <summary>Opens a new connection to the test database. The caller disposes it.</summary>
    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(Database.ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the count <paramref name="sql"/> reads with <paramref name="id"/> as the parameter <c>@id</c>.</summary>
    private async Task<long> CountAsync(string sql, object id)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        object? count = await command.ExecuteScalarAsync();
        return Assert.IsType<long>(count);
    }

    /// <summary>Returns the stored name of the agent with internal id <paramref name="agentId"/>.</summary>
    private async Task<string> ReadAgentNameAsync(long agentId) => (await ReadAgentLockAsync(agentId)).Name;

    /// <summary>Returns the stored name, lock owner and lock time of the agent with internal id <paramref name="agentId"/>.</summary>
    private async Task<AgentLockRow> ReadAgentLockAsync(long agentId)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT name, locked_by_session, locked_timestamp FROM agents WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", agentId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No agent row has id {agentId}.");
        return new AgentLockRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    /// <summary>Returns the internal id of the one stored sale of the item with internal id <paramref name="itemId"/>.</summary>
    private async Task<long> ReadSaleIdAsync(long itemId)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM sales WHERE item_id = @id", connection);
        command.Parameters.AddWithValue("id", itemId);
        var saleIds = new List<long>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                saleIds.Add(reader.GetInt64(0));
            }
        }

        return Assert.Single(saleIds);
    }

    /// <summary>The agent, country, region and item category the test's items are listed with.</summary>
    /// <param name="Agent">The agent "John Smith".</param>
    /// <param name="Country">The country "France".</param>
    /// <param name="Region">The region "Paris" of <paramref name="Country"/>.</param>
    /// <param name="Category">The item category "Abstractions".</param>
    private sealed record Listing(Agent Agent, Country Country, Region Region, ItemCategory Category);

    /// <summary>The stored name and soft edit lock of one agent row.</summary>
    /// <param name="Name">The agent name.</param>
    /// <param name="LockedBySession">The session id holding the soft edit lock, or <see langword="null"/>.</param>
    /// <param name="LockedTimestamp">The time the soft edit lock was set, or <see langword="null"/>.</param>
    private sealed record AgentLockRow(string Name, string? LockedBySession, DateTime? LockedTimestamp);

    /// <summary>One hub message a <see cref="HubProbe"/> received.</summary>
    /// <param name="Sequence">The message's position in arrival order, from 1.</param>
    /// <param name="Payload">The <see cref="EntityChangedMessage"/> or <see cref="SaveOutstandingChangesMessage"/>.</param>
    private sealed record ReceivedMessage(long Sequence, object Payload);


    /// <summary>
    /// A SignalR client of the API's hub for one session that keeps every <see cref="ErewhonHub.EntityChanged"/> and
    /// <see cref="ErewhonHub.SaveOutstandingChanges"/> message it receives, numbered in arrival order. Disposing it
    /// stops and disposes the connection. Safe to read while messages arrive.
    /// </summary>
    private sealed class HubProbe : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<ReceivedMessage> _received = [];
        private readonly HubConnection _connection;
        private TaskCompletionSource _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HubProbe(HubConnection connection)
        {
            _connection = connection;
            _connection.On<EntityChangedMessage>(ErewhonHub.EntityChanged, Record);
            _connection.On<SaveOutstandingChangesMessage>(ErewhonHub.SaveOutstandingChanges, Record);
        }

        /// <summary>The sequence number of the last message received, or 0 when none has been.</summary>
        public long LastSequence
        {
            get
            {
                lock (_gate)
                {
                    return _received.Count;
                }
            }
        }

        /// <summary>
        /// Connects to <paramref name="hubUrl"/> with the JSON protocol reading camel-case property names and enum
        /// values, with both handlers registered before the connection starts, and returns the probe once the
        /// connection has started. A connection that fails to start is disposed.
        /// </summary>
        /// <param name="hubUrl">The hub address carrying the session id.</param>
        /// <returns>The started probe.</returns>
        public static async Task<HubProbe> ConnectAsync(Uri hubUrl)
        {
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
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

        /// <summary>
        /// Returns the first message received after sequence number <paramref name="after"/> whose payload equals
        /// <paramref name="payload"/>, waiting for it at most <see cref="HubTimeout"/>.
        /// </summary>
        /// <param name="payload">The expected payload.</param>
        /// <param name="after">The sequence number the message must follow; 0 for any message.</param>
        /// <returns>The received message.</returns>
        public Task<ReceivedMessage> WaitForAsync(object payload, long after = 0) =>
            WaitForAsync(message => message.Sequence > after && message.Payload.Equals(payload), $"{payload}");

        /// <summary>
        /// Waits for each of <paramref name="payloads"/> in turn as <see cref="WaitForAsync(object, long)"/> does, each
        /// after sequence number <paramref name="after"/>.
        /// </summary>
        /// <param name="after">The sequence number every message must follow; 0 for any message.</param>
        /// <param name="payloads">The expected payloads.</param>
        /// <returns>A task that completes when every payload has been received.</returns>
        public async Task WaitForAllAsync(long after, params object[] payloads)
        {
            foreach (object payload in payloads)
            {
                await WaitForAsync(payload, after);
            }
        }

        /// <summary>
        /// Returns the first received message that matches <paramref name="predicate"/>, those received before the call
        /// included, waiting for it at most <see cref="HubTimeout"/>; fails the test with <paramref name="expected"/>
        /// and every message received when none arrives in time.
        /// </summary>
        /// <param name="predicate">The test a message must pass.</param>
        /// <param name="expected">The description of the awaited message for the failure text.</param>
        /// <returns>The received message.</returns>
        public async Task<ReceivedMessage> WaitForAsync(Func<ReceivedMessage, bool> predicate, string expected)
        {
            using var timeout = new CancellationTokenSource(HubTimeout);
            while (true)
            {
                Task arrival;
                lock (_gate)
                {
                    if (_received.FirstOrDefault(predicate) is { } match)
                    {
                        return match;
                    }

                    if (timeout.IsCancellationRequested)
                    {
                        Assert.Fail(
                            $"The SignalR session received no {expected} within {HubTimeout}. Received:"
                            + Environment.NewLine
                            + string.Join(Environment.NewLine, _received));
                    }

                    arrival = _arrival.Task;
                }

                try
                {
                    await arrival.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }
            }
        }

        /// <summary>
        /// Waits <paramref name="window"/>, then returns the received messages that match <paramref name="predicate"/>,
        /// in arrival order.
        /// </summary>
        /// <param name="window">The time to wait before the messages are read.</param>
        /// <param name="predicate">The test a message must pass.</param>
        /// <returns>The matching messages.</returns>
        public async Task<IReadOnlyList<ReceivedMessage>> SettleAsync(TimeSpan window, Func<ReceivedMessage, bool> predicate)
        {
            await Task.Delay(window);
            lock (_gate)
            {
                return _received.Where(predicate).ToList();
            }
        }

        /// <summary>
        /// Waits <paramref name="window"/>, then asserts that no received message matches <paramref name="predicate"/>.
        /// </summary>
        /// <param name="predicate">The test no message may pass.</param>
        /// <param name="window">The time to wait before the messages are read.</param>
        /// <returns>A task that completes when the assertion has run.</returns>
        public async Task AssertNoneAsync(Func<ReceivedMessage, bool> predicate, TimeSpan window) =>
            Assert.Empty(await SettleAsync(window, predicate));

        /// <summary>Stops and disposes the connection.</summary>
        /// <returns>A task that completes when the connection is disposed.</returns>
        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        /// <summary>Keeps <paramref name="payload"/> with the next sequence number and signals its arrival.</summary>
        private void Record(object payload)
        {
            TaskCompletionSource arrival;
            lock (_gate)
            {
                _received.Add(new ReceivedMessage(_received.Count + 1, payload));
                arrival = _arrival;
                _arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            arrival.SetResult();
        }
    }
}

