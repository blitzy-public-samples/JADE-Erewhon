using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.Realtime;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises when <see cref="SignalRNotificationPublisher"/> delivers hub messages relative to the HTTP response of
/// the request whose work caused them. The real API runs on Kestrel over the assembly database, with one test-only
/// endpoint under <c>/api/app</c> that does committed work through the application services of its request scope and
/// then waits on a barrier before it answers. A SignalR client of an Administrator session keeps every
/// <see cref="ErewhonHub.EntityChanged"/>, <see cref="ErewhonHub.CartItemDeleted"/> and
/// <see cref="ErewhonHub.SaveOutstandingChanges"/> message in arrival order. A company created and deleted in a scope of
/// the root service provider, which has no HTTP context, is the sentinel. Every interleaving is ordered by barriers and
/// by arrival order on the one connection, never by elapsed time.
/// </summary>
public sealed class NotificationDeliveryTimingTests
{
    private const string HeldPath = "/api/app/test-delivery/held";

    private const string CategoryName = "Abstractions";

    private const string CategoryPrefix = "ABST";

    private const string CategoryDescription = "Items that do not model real world observables";

    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HubRoundTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The committed work the held request does before it waits.</summary>
    public enum HeldWork
    {
        /// <summary>Creates a company and then deletes it, each in its own transaction.</summary>
        CreateAndDeleteCompany,

        /// <summary>Deletes the fixture retail item, which the listener session's cart holds.</summary>
        DeleteCartedItem,

        /// <summary>
        /// Soft-locks the fixture agent for the listener session, requests the save of the agent, and unlocks it.
        /// </summary>
        LockRequestSaveAndUnlockAgent,
    }

    /// <summary>Each work of the held request.</summary>
    public static TheoryData<HeldWork> HeldWorks => new()
    {
        HeldWork.CreateAndDeleteCompany,
        HeldWork.DeleteCartedItem,
        HeldWork.LockRequestSaveAndUnlockAgent,
    };

    /// <summary>
    /// Over HTTP: a request to an <c>/api/app</c> endpoint does <paramref name="work"/>, whose transactions commit, and
    /// waits. While it waits, the sentinel company created and deleted with no HTTP context reaches the listener, and
    /// nothing of the held request's work has reached it; a deleted carted item is already out of the listener
    /// session's cart. Once the request answers 204, the messages of its work reach the listener in the order the work
    /// published them, after the sentinel.
    /// </summary>
    /// <param name="work">The work the held request does.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(HeldWorks))]
    public async Task RequestMessagesArriveAfterItsResponseInCallOrder(HeldWork work)
    {
        var held = new HeldRequest(work);
        await using ErewhonApiHost host = await StartHostAsync(held);
        Fixture fixture = await CreateFixtureAsync(host);
        held.Fixture = fixture;
        var carts = host.Services.GetRequiredService<ICartStore>();
        HubListener? listener = null;

        try
        {
            listener = await HubListener.ConnectAsync(host);
            int joined = await JoinAllGroupAsync(host, listener);
            if (work == HeldWork.DeleteCartedItem)
            {
                carts.GetCart(listener.SessionId).Add(CartLine(fixture.RetailItemId));
            }

            using HttpClient client = host.CreateHttpClient();
            Task<HttpResponseMessage> running = client.SendAsync(SessionRequest(HeldPath, listener.SessionId));
            try
            {
                long writtenId = await held.Written.Task.WaitAsync(BarrierTimeout);
                object[] expected = ExpectedMessages(work, fixture, writtenId);

                (long sentinelId, int sentinelEnd) = await CompanyRoundTripAsync(host, listener);
                Assert.Equal(
                    new object[] { CompanyChange(sentinelId, ChangeKind.Added), CompanyChange(sentinelId, ChangeKind.Deleted) },
                    listener.Snapshot()[joined..sentinelEnd]);
                Assert.False(running.IsCompleted);
                if (work == HeldWork.DeleteCartedItem)
                {
                    Assert.False(carts.GetCart(listener.SessionId).Contains(fixture.RetailItemId));
                }

                held.Resume.SetResult();
                using HttpResponseMessage response = await running.WaitAsync(BarrierTimeout);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

                int end = sentinelEnd + expected.Length;
                Assert.True(
                    await listener.WaitForCountAsync(end, BarrierTimeout),
                    $"The SignalR session received {listener.Snapshot().Length - sentinelEnd} of the {expected.Length} "
                    + $"messages of the held request within {BarrierTimeout}.");
                Assert.Equal(expected, listener.Snapshot()[sentinelEnd..end]);
            }
            finally
            {
                held.Resume.TrySetResult();
            }
        }
        finally
        {
            if (listener is not null)
            {
                carts.RemoveCart(listener.SessionId);
                await listener.DisposeAsync();
            }

            await DeleteCompaniesAsync(host, held.CreatedCompanyId, fixture.CompanyId);
        }
    }

    /// <summary>Returns the messages <paramref name="work"/> publishes, in publication order.</summary>
    private static object[] ExpectedMessages(HeldWork work, Fixture fixture, long writtenId) => work switch
    {
        HeldWork.CreateAndDeleteCompany =>
        [
            CompanyChange(writtenId, ChangeKind.Added),
            CompanyChange(writtenId, ChangeKind.Deleted),
        ],
        HeldWork.DeleteCartedItem =>
        [
            new CartItemDeletedMessage(fixture.RetailItemId),
            new EntityChangedMessage(nameof(RetailItem), fixture.RetailItemId, ChangeKind.Deleted),
        ],
        HeldWork.LockRequestSaveAndUnlockAgent =>
        [
            new EntityChangedMessage(nameof(Agent), fixture.AgentId, ChangeKind.Updated),
            new SaveOutstandingChangesMessage(nameof(Agent), fixture.AgentId),
            new EntityChangedMessage(nameof(Agent), fixture.AgentId, ChangeKind.Updated),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(work), work, "The held work is not defined."),
    };

    /// <summary>
    /// Starts the real API over the assembly database with <see cref="HeldPath"/> mapped to <see cref="HoldAsync"/> for
    /// <paramref name="held"/>.
    /// </summary>
    private static async Task<ErewhonApiHost> StartHostAsync(HeldRequest held)
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(NotificationDeliveryTimingTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();

        return await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = database.ConnectionString,
            MapEndpoints = app => app.MapPost(
                HeldPath,
                (Func<HttpContext, Task<IResult>>)(context => HoldAsync(context, held))),
        });
    }

    /// <summary>
    /// Creates, in a scope of the root service provider, a company and, bound to it, the agent "John Smith", the
    /// country "France", its region "Paris", the item category "Abstractions" and the retail item "Proxy Retail". A
    /// failure after the company was created deletes the company and is rethrown.
    /// </summary>
    private static async Task<Fixture> CreateFixtureAsync(ErewhonApiHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(scope.ServiceProvider);
        Company company = await builder.CreateCompanyAsync();
        try
        {
            builder.Bind(company.Id);
            Agent agent = await builder.CreateAgentAsync();
            Country country = await builder.CreateCountryAsync();
            Region region = await builder.CreateRegionAsync(country);
            ItemCategory category = await builder.CreateItemCategoryAsync(CategoryName, CategoryPrefix, CategoryDescription);
            RetailItem item = await builder.CreateRetailItemAsync(region, category, agent);
            return new Fixture(company.Id, agent.Id, item.Id);
        }
        catch
        {
            await builder.DeleteCompanyAsync(company.Id);
            throw;
        }
    }

    /// <summary>
    /// The held endpoint: binds the fixture company, does the work of <paramref name="held"/> with the request's
    /// services, reports the id it wrote, waits until <see cref="HeldRequest.Resume"/> completes and answers 204. A
    /// failed work is reported and rethrown.
    /// </summary>
    private static async Task<IResult> HoldAsync(HttpContext context, HeldRequest held)
    {
        try
        {
            Fixture fixture = held.Fixture
                ?? throw new InvalidOperationException("The held request reached its endpoint before the fixture existed.");
            IServiceProvider services = context.RequestServices;
            services.GetRequiredService<ICompanyContext>().Bind(fixture.CompanyId);
            held.Written.TrySetResult(await DoWorkAsync(services, held, fixture, context.RequestAborted));
        }
        catch (Exception failure)
        {
            held.Written.TrySetException(failure);
            throw;
        }

        await held.Resume.Task;
        return Results.NoContent();
    }

    /// <summary>
    /// Does the work of <paramref name="held"/> with <paramref name="services"/> and returns the id it wrote: the
    /// created company, the deleted item or the locked agent.
    /// </summary>
    private static async Task<long> DoWorkAsync(
        IServiceProvider services,
        HeldRequest held,
        Fixture fixture,
        CancellationToken ct)
    {
        switch (held.Work)
        {
            case HeldWork.CreateAndDeleteCompany:
                var builder = new TestCompanyBuilder(services);
                Company company = await builder.CreateCompanyAsync(ct);
                held.CreatedCompanyId = company.Id;
                await builder.DeleteCompanyAsync(company.Id, ct);
                return company.Id;

            case HeldWork.DeleteCartedItem:
                EnsureSucceeded(
                    await services.GetRequiredService<ItemService>().DeleteByIdAsync(fixture.RetailItemId, ct),
                    "delete of the fixture retail item");
                return fixture.RetailItemId;

            case HeldWork.LockRequestSaveAndUnlockAgent:
                var softLocks = services.GetRequiredService<SoftLockService>();
                EnsureSucceeded(await softLocks.LockAsync(nameof(Agent), fixture.AgentId, ct), "soft lock of the fixture agent");
                if (await softLocks.RequestSaveAsync(nameof(Agent), fixture.AgentId, ct) != true)
                {
                    throw new InvalidOperationException("The save request of the fixture agent was not sent.");
                }

                EnsureSucceeded(await softLocks.UnlockAsync(nameof(Agent), fixture.AgentId, ct), "soft unlock of the fixture agent");
                return fixture.AgentId;

            default:
                throw new ArgumentOutOfRangeException(nameof(held), held.Work, "The held work is not defined.");
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> naming <paramref name="operation"/> unless it succeeded.</summary>
    private static void EnsureSucceeded(OperationResult? result, string operation)
    {
        if (result is not { Succeeded: true })
        {
            throw new InvalidOperationException($"The {operation} failed: {result?.FullErrorText ?? "no such row"}");
        }
    }

    /// <summary>
    /// Repeats <see cref="CompanyRoundTripAsync"/>'s create and delete, waiting at most <see cref="HubRoundTimeout"/>
    /// after each round for its <c>Company</c> Deleted, until <paramref name="listener"/> has received <c>Company</c>
    /// Added and then <c>Company</c> Deleted for one round's company; fails when no round has done so within
    /// <see cref="BarrierTimeout"/>. Returns the number of messages received up to and including that Deleted.
    /// </summary>
    private static async Task<int> JoinAllGroupAsync(ErewhonApiHost host, HubListener listener)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            long companyId = await CreateAndDeleteCompanyAsync(host);
            TimeSpan wait = TimeSpan.FromTicks(
                Math.Clamp((BarrierTimeout - elapsed.Elapsed).Ticks, 0, HubRoundTimeout.Ticks));
            int deleted = await listener.WaitForAsync(CompanyChange(companyId, ChangeKind.Deleted), wait);
            if (deleted >= 0 && listener.Snapshot()[..deleted].Contains(CompanyChange(companyId, ChangeKind.Added)))
            {
                return deleted + 1;
            }

            if (elapsed.Elapsed >= BarrierTimeout)
            {
                Assert.Fail($"The SignalR session received no Company Added and Deleted pair within {BarrierTimeout}.");
            }
        }
    }

    /// <summary>
    /// Creates and deletes a company in a scope of the root service provider of <paramref name="host"/> and waits at
    /// most <see cref="BarrierTimeout"/> until <paramref name="listener"/> has received its <c>Company</c> Deleted,
    /// failing when it has not. Returns the company id and the number of messages received up to and including that
    /// Deleted.
    /// </summary>
    private static async Task<(long CompanyId, int End)> CompanyRoundTripAsync(ErewhonApiHost host, HubListener listener)
    {
        long companyId = await CreateAndDeleteCompanyAsync(host);
        int deleted = await listener.WaitForAsync(CompanyChange(companyId, ChangeKind.Deleted), BarrierTimeout);
        Assert.True(
            deleted >= 0,
            $"The SignalR session received no Company Deleted for company {companyId} within {BarrierTimeout}.");
        return (companyId, deleted + 1);
    }

    /// <summary>
    /// Creates a company and deletes it with <see cref="TestCompanyBuilder"/> in a new scope of the root service
    /// provider of <paramref name="host"/>, and returns its id.
    /// </summary>
    private static async Task<long> CreateAndDeleteCompanyAsync(ErewhonApiHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(scope.ServiceProvider);
        Company company = await builder.CreateCompanyAsync();
        await builder.DeleteCompanyAsync(company.Id);
        return company.Id;
    }

    /// <summary>
    /// Deletes each company of <paramref name="companyIds"/> that is not null, with everything it owns, in a new scope
    /// of the root service provider of <paramref name="host"/>; an id with no company is passed over.
    /// </summary>
    private static async Task DeleteCompaniesAsync(ErewhonApiHost host, params long?[] companyIds)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(scope.ServiceProvider);
        foreach (long? companyId in companyIds)
        {
            if (companyId is { } id)
            {
                await builder.DeleteCompanyAsync(id);
            }
        }
    }

    /// <summary>Returns the <see cref="EntityChangedMessage"/> of a <c>Company</c> change.</summary>
    private static EntityChangedMessage CompanyChange(long companyId, ChangeKind kind) => new(nameof(Company), companyId, kind);

    /// <summary>Returns a retail cart line for <paramref name="itemId"/>.</summary>
    private static CartItem CartLine(long itemId) => new()
    {
        ItemId = itemId,
        IsTender = false,
        Code = CategoryPrefix + "1",
        Transaction = "Retail",
        Offer = 0m,
        SalePrice = 5.50m,
        ClientId = 1,
    };

    /// <summary>Returns a POST to <paramref name="path"/> carrying <paramref name="sessionId"/> as the session header.</summary>
    private static HttpRequestMessage SessionRequest(string path, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'));
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        return request;
    }

    /// <summary>The rows the held request works on.</summary>
    /// <param name="CompanyId">The internal id of the fixture company.</param>
    /// <param name="AgentId">The internal id of the fixture agent.</param>
    /// <param name="RetailItemId">The internal id of the fixture retail item.</param>
    private sealed record Fixture(long CompanyId, long AgentId, long RetailItemId);

    /// <summary>The barriers and state shared by one held request and its test.</summary>
    /// <param name="work">The work the request does before it waits.</param>
    private sealed class HeldRequest(HeldWork work)
    {
        /// <summary>The work the request does before it waits.</summary>
        public HeldWork Work { get; } = work;

        /// <summary>The fixture rows; set by the test before the request is sent.</summary>
        public Fixture? Fixture { get; set; }

        /// <summary>The company the work created, or <see langword="null"/>.</summary>
        public long? CreatedCompanyId { get; set; }

        /// <summary>Completed with the id the work wrote once the work has committed.</summary>
        public TaskCompletionSource<long> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test to let the request answer.</summary>
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A SignalR client of the hub of one host that keeps every <see cref="ErewhonHub.EntityChanged"/>,
    /// <see cref="ErewhonHub.CartItemDeleted"/> and <see cref="ErewhonHub.SaveOutstandingChanges"/> message it receives,
    /// as <see cref="EntityChangedMessage"/>, <see cref="CartItemDeletedMessage"/> and
    /// <see cref="SaveOutstandingChangesMessage"/> values in arrival order. It connects with an Administrator session of
    /// the admin area registered in the host's <see cref="SessionRegistry"/>; disposing it stops and disposes the
    /// connection.
    /// </summary>
    private sealed class HubListener : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<object> _messages = [];
        private readonly HubConnection _connection;
        private TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private HubListener(HubConnection connection, string sessionId)
        {
            _connection = connection;
            SessionId = sessionId;
            _connection.On<EntityChangedMessage>(ErewhonHub.EntityChanged, Receive);
            _connection.On<CartItemDeletedMessage>(ErewhonHub.CartItemDeleted, Receive);
            _connection.On<SaveOutstandingChangesMessage>(ErewhonHub.SaveOutstandingChanges, Receive);
        }

        /// <summary>The id of the session the connection presents.</summary>
        public string SessionId { get; }

        /// <summary>
        /// Registers an Administrator session of the admin area in the <see cref="SessionRegistry"/> of
        /// <paramref name="host"/>, connects to <see cref="ErewhonApiHost.HubUrl"/> of that session with the JSON
        /// protocol reading enum values as camel-case strings, and returns the listener once the connection has started.
        /// A connection that fails to start is disposed. The caller disposes the listener.
        /// </summary>
        /// <param name="host">The host whose hub is connected to.</param>
        /// <returns>The started listener.</returns>
        public static async Task<HubListener> ConnectAsync(ErewhonApiHost host)
        {
            SessionEntry session = host.Services.GetRequiredService<SessionRegistry>()
                .Create(LogonArea.Admin, SessionRole.Administrator, agentId: null, clientId: null, "Delivery Administrator");
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(host.HubUrl(session.SessionId))
                .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
                .Build();
            var listener = new HubListener(connection, session.SessionId);
            try
            {
                await connection.StartAsync();
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            return listener;
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
        /// Returns the index of the first received message equal to <paramref name="message"/>, waiting for it at most
        /// <paramref name="timeout"/>; returns -1 when it has not been received by then.
        /// </summary>
        /// <param name="message">The message to wait for.</param>
        /// <param name="timeout">The longest wait.</param>
        /// <returns>The index of the message in arrival order, or -1.</returns>
        public async Task<int> WaitForAsync(object message, TimeSpan timeout)
        {
            int index = -1;
            await WaitUntilAsync(messages => (index = messages.IndexOf(message)) >= 0, timeout);
            return index;
        }

        /// <summary>
        /// Returns whether at least <paramref name="count"/> messages have been received, waiting for them at most
        /// <paramref name="timeout"/>.
        /// </summary>
        /// <param name="count">The number of messages to wait for.</param>
        /// <param name="timeout">The longest wait.</param>
        /// <returns><see langword="true"/> when that many messages have been received.</returns>
        public Task<bool> WaitForCountAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(messages => messages.Count >= count, timeout);

        /// <summary>Stops and disposes the connection.</summary>
        /// <returns>A task that completes when the connection is disposed.</returns>
        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        /// <summary>
        /// Evaluates <paramref name="condition"/> over the received messages, under the lock, after each arrival until it
        /// holds or <paramref name="timeout"/> has passed, and returns its last value.
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
