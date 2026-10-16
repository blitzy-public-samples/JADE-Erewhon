using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Closure;
using Erewhon.Api.Realtime;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the tender closures of the close-tenders dialog over HTTP and SignalR against the real API on Kestrel
/// and the assembly database: "Close now" stops the caller's schedule and reports the number of tender items it
/// awarded (B-65); a started schedule closes the tenders due at its date once its countdown of minutes × 60 timer
/// seconds runs out, then advances the date by one day and counts down again, until Stop removes it (B-66). Each test
/// opens an Administrator session of the admin area and then creates, in the test's company, tender items with closure
/// dates after the server date D, each with one tender. The schedule tests run on a dedicated host whose
/// <see cref="TenderClosureScheduleService.TickMsKey"/> is <see cref="DedicatedTickMs"/> milliseconds, and listen on
/// the hub with the session.
/// </summary>
public sealed class ClosureScheduleTests : ErewhonTestBase
{
    /// <summary>The route that opens an app session.</summary>
    private const string SessionsPath = "api/app/sessions";

    /// <summary>The route that ends the caller's app session.</summary>
    private const string CurrentSessionPath = "api/app/sessions/current";

    /// <summary>The route that returns the server date and the caller's closure schedule.</summary>
    private const string SchedulePath = "api/app/admin/tenders/closure-schedule";

    /// <summary>The route that stops the caller's schedule and closes the tenders due at a date.</summary>
    private const string CloseNowPath = "api/app/admin/tenders/close-now";

    /// <summary>The route that starts the caller's closure schedule.</summary>
    private const string StartPath = "api/app/admin/tenders/closure-schedule/start";

    /// <summary>The route that stops the caller's closure schedule.</summary>
    private const string StopPath = "api/app/admin/tenders/closure-schedule/stop";

    /// <summary>The minutes per cycle every schedule of these tests is started with.</summary>
    private const int ScheduleMinutes = 1;

    /// <summary>The timer period, in milliseconds, of the dedicated hosts.</summary>
    private const string DedicatedTickMs = "10";

    /// <summary>The name of the item category of the tender items.</summary>
    private const string CategoryName = "Closure Lots";

    /// <summary>The prefix of the item category of the tender items.</summary>
    private const string CategoryPrefix = "CLOS";

    /// <summary>The description of the item category of the tender items.</summary>
    private const string CategoryDescription = "Tender items closed by the closure schedule tests";

    /// <summary>The description of each tender item.</summary>
    private const string ItemDescription = "A tender item with one tender";

    /// <summary>The reserve price of each tender item.</summary>
    private const decimal ReservePrice = 5.50m;

    /// <summary>The longest wait for a hub message.</summary>
    private static readonly TimeSpan HubDeadline = TimeSpan.FromSeconds(20);

    /// <summary>The window after Stop in which no scheduled closure may complete.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);

    /// <summary>The JSON options of the app endpoints: web defaults with enums as camel-case strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The configuration of the dedicated hosts: the timer period <see cref="DedicatedTickMs"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string?> ShortTickSettings = new Dictionary<string, string?>
    {
        [TenderClosureScheduleService.TickMsKey] = DedicatedTickMs,
    };

    /// <summary>
    /// On the shared host, an Administrator session starts a schedule at D + 1 for <see cref="ScheduleMinutes"/> minute;
    /// <c>POST close-now</c> at D + 1 then answers 200 with <c>numClosed</c> 2 for two tender items closing at D + 1
    /// and one closing at D + 3, each with one tender. Each D + 1 item holds one tender sale through its tender, the
    /// D + 3 item none, and the session has no running schedule afterwards.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CloseNowReportsCount()
    {
        DateOnly serverDate = await ReadServerDateAsync();
        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);
        try
        {
            IReadOnlyList<TenderedItem> items = await CreateTenderedItemsAsync(Builder, serverDate, 1, 1, 3);

            // A running schedule that "Close now" stops (D-174).
            StartClosureScheduleResponse started = await SendAsync<StartClosureScheduleResponse>(
                http,
                HttpMethod.Post,
                StartPath,
                sessionId,
                new StartClosureScheduleRequest(serverDate.AddDays(1), ScheduleMinutes));
            Assert.True(started.Started);

            // Act
            CloseNowResponse closed = await SendAsync<CloseNowResponse>(
                http,
                HttpMethod.Post,
                CloseNowPath,
                sessionId,
                new CloseNowRequest(serverDate.AddDays(1)));

            // Assert
            Assert.Equal(2, closed.NumClosed);
            Assert.Equal(items[0].TenderId, Assert.Single(await ReadTenderSaleTenderIdsAsync(items[0].ItemId)));
            Assert.Equal(items[1].TenderId, Assert.Single(await ReadTenderSaleTenderIdsAsync(items[1].ItemId)));
            Assert.Empty(await ReadTenderSaleTenderIdsAsync(items[2].ItemId));
            ClosureScheduleStateResponse state = await SendAsync<ClosureScheduleStateResponse>(
                http,
                HttpMethod.Get,
                SchedulePath,
                sessionId,
                body: null);
            Assert.False(state.Running);
        }
        finally
        {
            await LogOutAsync(http, sessionId);
        }
    }

    /// <summary>
    /// On a dedicated host, an Administrator session starts a schedule at D + 1 for <see cref="ScheduleMinutes"/>
    /// minute, which answers <c>started</c> true and <c>secondsToGo</c> 60, with tender item X closing at D + 1 and
    /// tender item Y closing at D + 2, each with one tender. Within <see cref="HubDeadline"/> the session receives two
    /// <c>ClosureCompleted</c> messages, first (D + 1, 1) and then (D + 2, 1); the <c>ClosureTick</c> values before the
    /// first and between the two each lie in 1 to 60 and decrease strictly, and at least one between the two lies in 1
    /// to 59. Stop then answers <c>stopped</c> true, and X and Y each hold one tender sale through their tender.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ScheduleRepeatsAndAdvancesDate()
    {
        DateOnly serverDate = await ReadServerDateAsync();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(ShortTickSettings);
        using HttpClient http = host.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);
        await using HubProbe probe = await HubProbe.ConnectAsync(host, sessionId);
        IReadOnlyList<TenderedItem> items = await CreateTenderedItemsOnHostAsync(host, serverDate, 1, 2);

        // The schedule starts once the probe holds the fixture tender's Added change (D-174).
        await probe.WaitForAsync(
            messages => messages.Contains(new EntityChangedMessage(nameof(Tender), items[^1].TenderId, ChangeKind.Added)),
            HubDeadline,
            "the Added change of the last fixture tender");

        // Act
        StartClosureScheduleResponse started = await SendAsync<StartClosureScheduleResponse>(
            http,
            HttpMethod.Post,
            StartPath,
            sessionId,
            new StartClosureScheduleRequest(serverDate.AddDays(1), ScheduleMinutes));
        Assert.True(started.Started);
        Assert.Equal(ScheduleMinutes * ClosureScheduleRegistry.SecondsPerMinute, started.SecondsToGo);
        object[] messages = await probe.WaitForAsync(
            received => received.OfType<ClosureCompletedMessage>().Count() >= 2,
            HubDeadline,
            "two ClosureCompleted messages");
        StopClosureScheduleResponse stopped = await SendAsync<StopClosureScheduleResponse>(
            http,
            HttpMethod.Post,
            StopPath,
            sessionId,
            body: null);

        // Assert
        Assert.True(stopped.Stopped);

        int[] completions = IndicesOf<ClosureCompletedMessage>(messages);
        Assert.Equal(new ClosureCompletedMessage(serverDate.AddDays(1), 1), messages[completions[0]]);
        Assert.Equal(new ClosureCompletedMessage(serverDate.AddDays(2), 1), messages[completions[1]]);

        int[] firstCycle = TickValues(messages[..completions[0]]);
        int[] secondCycle = TickValues(messages[(completions[0] + 1)..completions[1]]);
        AssertCountdown(firstCycle);
        AssertCountdown(secondCycle);
        Assert.Contains(secondCycle, secondsToGo => secondsToGo < ScheduleMinutes * ClosureScheduleRegistry.SecondsPerMinute);

        Assert.Equal(items[0].TenderId, Assert.Single(await ReadTenderSaleTenderIdsAsync(items[0].ItemId)));
        Assert.Equal(items[1].TenderId, Assert.Single(await ReadTenderSaleTenderIdsAsync(items[1].ItemId)));
    }

    /// <summary>
    /// On a dedicated host, an Administrator session starts a schedule at D + 1 for <see cref="ScheduleMinutes"/>
    /// minute with tender item X closing at D + 1 and holding one tender, and stops it once the first
    /// <c>ClosureTick</c> has arrived. Stop answers <c>stopped</c> true; the schedule state then answers
    /// <c>running</c> false with no date, minutes or seconds to go; no <c>ClosureCompleted</c> arrives within
    /// <see cref="QuietWindow"/>; and X holds no tender sale.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task StopHaltsSchedule()
    {
        DateOnly serverDate = await ReadServerDateAsync();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(ShortTickSettings);
        using HttpClient http = host.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);
        await using HubProbe probe = await HubProbe.ConnectAsync(host, sessionId);
        IReadOnlyList<TenderedItem> items = await CreateTenderedItemsOnHostAsync(host, serverDate, 1);

        // The schedule starts once the probe holds the fixture tender's Added change (D-174).
        await probe.WaitForAsync(
            messages => messages.Contains(new EntityChangedMessage(nameof(Tender), items[0].TenderId, ChangeKind.Added)),
            HubDeadline,
            "the Added change of the fixture tender");
        StartClosureScheduleResponse started = await SendAsync<StartClosureScheduleResponse>(
            http,
            HttpMethod.Post,
            StartPath,
            sessionId,
            new StartClosureScheduleRequest(serverDate.AddDays(1), ScheduleMinutes));
        Assert.True(started.Started);
        await probe.WaitForAsync(
            messages => messages.OfType<ClosureTickMessage>().Any(),
            HubDeadline,
            "the first ClosureTick message");

        // Act
        StopClosureScheduleResponse stopped = await SendAsync<StopClosureScheduleResponse>(
            http,
            HttpMethod.Post,
            StopPath,
            sessionId,
            body: null);

        // Assert
        Assert.True(stopped.Stopped);
        ClosureScheduleStateResponse state = await SendAsync<ClosureScheduleStateResponse>(
            http,
            HttpMethod.Get,
            SchedulePath,
            sessionId,
            body: null);
        Assert.False(state.Running);
        Assert.Null(state.Date);
        Assert.Null(state.Minutes);
        Assert.Null(state.SecondsToGo);
        await probe.AssertNoneWithinAsync<ClosureCompletedMessage>(QuietWindow);
        Assert.Empty(await ReadTenderSaleTenderIdsAsync(items[0].ItemId));
    }

    /// <summary>Returns the server date read once through the <see cref="IServerClock"/> of the test's scope.</summary>
    private Task<DateOnly> ReadServerDateAsync() => GetService<IServerClock>().GetServerDateAsync();

    /// <summary>
    /// Opens an Administrator session of the admin area with <c>POST /api/app/sessions</c> through
    /// <paramref name="http"/>, asserts that it answers 200 with a succeeded logon of the Administrator role, and returns
    /// the session id.
    /// </summary>
    private static async Task<string> OpenAdminSessionAsync(HttpClient http)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            SessionsPath,
            new CreateSessionRequest(LogonArea.Admin, Administrator: true, UserName: null),
            Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateSessionResponse? session = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(Json);
        Assert.NotNull(session);
        Assert.True(session.Succeeded, session.Message);
        Assert.Equal(SessionRole.Administrator, session.Role);
        return session.SessionId ?? throw new InvalidOperationException("The opened admin session has no id.");
    }

    /// <summary>
    /// Ends the session <paramref name="sessionId"/> with <c>DELETE /api/app/sessions/current</c> through
    /// <paramref name="http"/>; the response is not checked.
    /// </summary>
    private static async Task LogOutAsync(HttpClient http, string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, CurrentSessionPath);
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        using HttpResponseMessage response = await http.SendAsync(request);
    }

    /// <summary>
    /// Sends <paramref name="method"/> to <paramref name="path"/> in the session <paramref name="sessionId"/>, with
    /// <paramref name="body"/> as JSON when it is not <see langword="null"/>, asserts that it answers 200 with a JSON
    /// body, and returns the body as a <typeparamref name="TResponse"/>.
    /// </summary>
    private static async Task<TResponse> SendAsync<TResponse>(
        HttpClient http,
        HttpMethod method,
        string path,
        string sessionId,
        object? body)
        where TResponse : class
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        TResponse? result = await response.Content.ReadFromJsonAsync<TResponse>(Json);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Creates, through a <see cref="TestCompanyBuilder"/> over a new scope of <paramref name="host"/> bound to the
    /// test's company, the fixtures <see cref="CreateTenderedItemsAsync"/> describes, and returns their ids.
    /// </summary>
    private async Task<IReadOnlyList<TenderedItem>> CreateTenderedItemsOnHostAsync(
        ErewhonApiHost host,
        DateOnly serverDate,
        params int[] closureDays)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException($"The test company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(scope.ServiceProvider);
        builder.Bind(companyId);
        return await CreateTenderedItemsAsync(builder, serverDate, closureDays);
    }

    /// <summary>
    /// Creates, through <paramref name="builder"/> in its bound company, the agent "John Smith", the client "Clive
    /// Entworth", the country "France", its region "Paris" and the item category "Closure Lots" (<c>CLOS</c>), then one
    /// tender item per entry of <paramref name="closureDays"/>, in order, closing that many days after
    /// <paramref name="serverDate"/> with the reserve price <see cref="ReservePrice"/>, each with one tender by the
    /// client of the reserve price plus the item's one-based position. Returns the ids of the items and their tenders
    /// in the same order.
    /// </summary>
    private static async Task<IReadOnlyList<TenderedItem>> CreateTenderedItemsAsync(
        TestCompanyBuilder builder,
        DateOnly serverDate,
        params int[] closureDays)
    {
        Agent agent = await builder.CreateAgentAsync();
        Client client = await builder.CreateClientAsync();
        Country country = await builder.CreateCountryAsync();
        Region region = await builder.CreateRegionAsync(country);
        ItemCategory category = await builder.CreateItemCategoryAsync(CategoryName, CategoryPrefix, CategoryDescription);

        var items = new List<TenderedItem>(closureDays.Length);
        for (var index = 0; index < closureDays.Length; index++)
        {
            int position = index + 1;
            TenderItem item = await builder.CreateTestTenderItemAsync(
                $"Closure Item {position}",
                ItemDescription,
                null,
                region,
                category,
                agent,
                ReservePrice,
                serverDate.AddDays(closureDays[index]));
            Tender tender = await builder.CreateTenderAsync(client, item, ReservePrice + position);
            items.Add(new TenderedItem(item.Id, tender.Id));
        }

        return items;
    }

    /// <summary>
    /// Returns, read untracked in a new scope of <see cref="ErewhonTestBase.Api"/>, the tender id of every tender sale
    /// of the item with row id <paramref name="itemId"/>.
    /// </summary>
    private async Task<long?[]> ReadTenderSaleTenderIdsAsync(long itemId)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        IErewhonDbContext db = scope.ServiceProvider.GetRequiredService<IErewhonDbContext>();
        return await db.Sales
            .OfType<TenderSale>()
            .AsNoTracking()
            .Where(sale => sale.ItemId == itemId)
            .Select(sale => sale.TenderId)
            .ToArrayAsync();
    }

    /// <summary>Returns the positions of the <typeparamref name="TMessage"/> values in <paramref name="messages"/>.</summary>
    private static int[] IndicesOf<TMessage>(object[] messages) =>
        messages
            .Select((message, index) => (message, index))
            .Where(entry => entry.message is TMessage)
            .Select(entry => entry.index)
            .ToArray();

    /// <summary>Returns the seconds to go of the <see cref="ClosureTickMessage"/> values in <paramref name="messages"/>, in order.</summary>
    private static int[] TickValues(object[] messages) =>
        messages.OfType<ClosureTickMessage>().Select(tick => tick.SecondsToGo).ToArray();

    /// <summary>
    /// Asserts that <paramref name="secondsToGo"/> is not empty, that each value lies in 1 to
    /// <see cref="ScheduleMinutes"/> × <see cref="ClosureScheduleRegistry.SecondsPerMinute"/>, and that each value is
    /// less than the one before it.
    /// </summary>
    private static void AssertCountdown(int[] secondsToGo)
    {
        Assert.NotEmpty(secondsToGo);
        Assert.All(
            secondsToGo,
            value => Assert.InRange(value, 1, ScheduleMinutes * ClosureScheduleRegistry.SecondsPerMinute));
        for (var index = 1; index < secondsToGo.Length; index++)
        {
            Assert.True(
                secondsToGo[index] < secondsToGo[index - 1],
                $"ClosureTick {secondsToGo[index]} followed {secondsToGo[index - 1]} within one cycle: [{string.Join(", ", secondsToGo)}].");
        }
    }

    /// <summary>The row ids of one fixture tender item and its one tender.</summary>
    /// <param name="ItemId">The internal id of the tender item.</param>
    /// <param name="TenderId">The internal id of the tender on it.</param>
    private sealed record TenderedItem(long ItemId, long TenderId);

    /// <summary>
    /// A SignalR client of the hub of one host, connected with one session, that keeps every
    /// <see cref="ErewhonHub.ClosureTick"/>, <see cref="ErewhonHub.ClosureCompleted"/> and
    /// <see cref="ErewhonHub.EntityChanged"/> message it receives, as <see cref="ClosureTickMessage"/>,
    /// <see cref="ClosureCompletedMessage"/> and <see cref="EntityChangedMessage"/> values in arrival order. Disposing it
    /// stops and disposes the connection.
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
            _connection.On<ClosureTickMessage>(ErewhonHub.ClosureTick, Receive);
            _connection.On<ClosureCompletedMessage>(ErewhonHub.ClosureCompleted, Receive);
            _connection.On<EntityChangedMessage>(ErewhonHub.EntityChanged, Receive);
        }

        /// <summary>
        /// Connects to <see cref="ErewhonApiHost.HubUrl"/> of <paramref name="host"/> for <paramref name="sessionId"/>
        /// with the JSON protocol reading enum values as camel-case strings, the handlers registered before the
        /// connection starts, and returns the probe once the connection has started. A connection that fails to start
        /// is disposed. The caller disposes the probe.
        /// </summary>
        /// <param name="host">The host whose hub is connected to.</param>
        /// <param name="sessionId">The registered session the connection presents.</param>
        /// <returns>The started probe.</returns>
        public static async Task<HubProbe> ConnectAsync(ErewhonApiHost host, string sessionId)
        {
            HubConnection connection = new HubConnectionBuilder()
                .WithUrl(host.HubUrl(sessionId))
                .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
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
        /// Waits at most <paramref name="timeout"/> until <paramref name="condition"/> holds for the messages received
        /// so far, evaluated under the lock after each arrival, and returns a copy of them in arrival order; fails the
        /// test naming <paramref name="awaited"/> when it does not hold by then.
        /// </summary>
        /// <param name="condition">The condition over the received messages.</param>
        /// <param name="timeout">The longest wait.</param>
        /// <param name="awaited">What the condition waits for, for the failure message.</param>
        /// <returns>The messages received when the condition held.</returns>
        public async Task<object[]> WaitForAsync(
            Func<IReadOnlyList<object>, bool> condition,
            TimeSpan timeout,
            string awaited)
        {
            if (!await WaitUntilAsync(condition, timeout))
            {
                Assert.Fail($"The hub session did not receive {awaited} within {timeout}; it received [{Describe()}].");
            }

            lock (_gate)
            {
                return _messages.ToArray();
            }
        }

        /// <summary>
        /// Waits <paramref name="window"/>, ending early when a <typeparamref name="TMessage"/> arrives, and fails the
        /// test when any <typeparamref name="TMessage"/> has been received.
        /// </summary>
        /// <typeparam name="TMessage">The message type that must not be received.</typeparam>
        /// <param name="window">The length of the wait.</param>
        /// <returns>A task that completes when the window has passed without such a message.</returns>
        public async Task AssertNoneWithinAsync<TMessage>(TimeSpan window)
        {
            if (await WaitUntilAsync(messages => messages.OfType<TMessage>().Any(), window))
            {
                Assert.Fail($"The hub session received a {typeof(TMessage).Name}: [{Describe()}].");
            }
        }

        /// <summary>Stops and disposes the connection.</summary>
        /// <returns>A task that completes when the connection is disposed.</returns>
        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        /// <summary>
        /// Evaluates <paramref name="condition"/> over the received messages, under the lock, after each arrival until it
        /// holds or <paramref name="timeout"/> has passed, and returns its last value.
        /// </summary>
        private async Task<bool> WaitUntilAsync(Func<IReadOnlyList<object>, bool> condition, TimeSpan timeout)
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

        /// <summary>Returns the received messages, in arrival order, as one comma-separated text.</summary>
        private string Describe()
        {
            lock (_gate)
            {
                return string.Join(", ", _messages);
            }
        }

        /// <summary>Keeps <paramref name="message"/> and completes the pending arrival signal.</summary>
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
