using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the rules of the app endpoints against the real API on Kestrel and the assembly database: the admin
/// start-up closure (B-67), the session gate of the app endpoints and the hub (B-79), the soft edit lock seen by its
/// own session and by another session (B-40, Q-31), and the refused interactive data load (B-80). Each test runs on
/// its own company "Jade Software Corporation", bound to the API as the lowest-id company, with the agent "John Smith",
/// the client "Clive Entworth", the country "France", its region "Paris" and the item category "Abstractions"
/// (<c>ABST</c>); the base class deletes the company after the test.
/// </summary>
public sealed class AppEndpointRuleTests : ErewhonTestBase
{
    /// <summary>The route that lists the logon options of an area.</summary>
    private const string LogonOptionsPath = "api/app/logon-options";

    /// <summary>The route that opens an app session.</summary>
    private const string SessionsPath = "api/app/sessions";

    /// <summary>The route of the interactive data load.</summary>
    private const string LoadDataPath = "api/app/setup/load-data";

    /// <summary>The route of the About dialog.</summary>
    private const string AboutPath = "api/app/about";

    /// <summary>The route of one tender in the admin area, without its id.</summary>
    private const string AdminTendersPath = "api/app/admin/tenders";

    /// <summary>The route of the soft edit locks, without the entity type and id.</summary>
    private const string EditLocksPath = "api/app/edit-locks";

    /// <summary>The hub path, relative to the host's base address, with no query.</summary>
    private const string HubPath = "hubs/erewhon";

    /// <summary>The token in a protected route that stands for the fixture agent's row id.</summary>
    private const string AgentIdToken = "{agentId}";

    /// <summary>The domain type name of the agent row the edit-lock tests lock.</summary>
    private const string AgentEntityType = nameof(Agent);

    /// <summary>The name of the fixture client.</summary>
    private const string ClientName = "Clive Entworth";

    /// <summary>The statement that selects every <c>companies</c> row id in ascending order.</summary>
    private const string CompanyIdsSql = "SELECT id FROM companies ORDER BY id";

    /// <summary>The statement that selects every <c>agents</c> row id in ascending order.</summary>
    private const string AgentIdsSql = "SELECT id FROM agents ORDER BY id";

    /// <summary>The <c>sale_type</c> of a tender sale.</summary>
    private const string TenderSaleType = "T";

    /// <summary>The offer of the first tender placed on the due tender item.</summary>
    private const decimal LowerOffer = 10.00m;

    /// <summary>The offer of the second tender placed on the due tender item, the highest one.</summary>
    private const decimal HigherOffer = 25.00m;

    /// <summary>The longest wait for a refused hub connection to close after it started.</summary>
    private static readonly TimeSpan HubRefusalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long an accepted hub connection is watched.</summary>
    private static readonly TimeSpan HubControlPeriod = TimeSpan.FromSeconds(1);

    /// <summary>The JSON options of the app endpoints: web defaults with enums as camel-case strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The protected app endpoints sent without a registered session: the HTTP method and the route, in which
    /// <see cref="AgentIdToken"/> stands for the fixture agent's row id.
    /// </summary>
    private static readonly (string Method, string Path)[] ProtectedEndpoints =
    [
        ("GET", "api/app/about"),
        ("GET", "api/app/shop/reference"),
        ("GET", "api/app/shop/cart"),
        ("GET", "api/app/admin/agents"),
        ("GET", "api/app/admin/documentation/candidates"),
        ("GET", $"api/app/edit-locks/Agent/{AgentIdToken}"),
        ("POST", "api/app/admin/tenders/close-now"),
        ("DELETE", "api/app/sessions/current"),
    ];

    /// <summary>The <c>X-Erewhon-Session</c> header a request to a protected endpoint carries.</summary>
    public enum SessionHeader
    {
        /// <summary>No header.</summary>
        Absent,

        /// <summary>A well-formed session id of 32 lower-case hexadecimal characters that no session holds.</summary>
        Unregistered,
    }

    /// <summary>Each protected endpoint with each session header that registers no session.</summary>
    public static TheoryData<string, string, SessionHeader> ProtectedRequests
    {
        get
        {
            var data = new TheoryData<string, string, SessionHeader>();
            foreach ((string method, string path) in ProtectedEndpoints)
            {
                foreach (SessionHeader header in Enum.GetValues<SessionHeader>())
                {
                    data.Add(method, path, header);
                }
            }

            return data;
        }
    }

    /// <summary>The row id of the fixture agent.</summary>
    private long AgentId => Fixture(Builder.MyAgent, nameof(Builder.MyAgent)).Id;

    /// <summary>The row id of the fixture client.</summary>
    private long ClientId => Fixture(Builder.MyClient, nameof(Builder.MyClient)).Id;

    /// <summary>The edit-lock route of the fixture agent.</summary>
    private string AgentEditLockPath => $"{EditLocksPath}/{AgentEntityType}/{AgentId}";

    /// <summary>
    /// Creates and binds the company, then creates in it the agent "John Smith", the client "Clive Entworth", the
    /// country "France", its region "Paris" and the item category "Abstractions" (<c>ABST</c>), each stored in the
    /// matching <c>My*</c> property of <see cref="ErewhonTestBase.Builder"/>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyClient = await Builder.CreateClientAsync();
        Builder.MyCountry = await Builder.CreateCountryAsync();
        Builder.MyRegion = await Builder.CreateRegionAsync(Builder.MyCountry);
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Abstractions",
            "ABST",
            "Items that do not model real world observables");
    }

    /// <summary>
    /// A tender item closing on the server date D holds two tenders by the client, 10.00 and then 25.00, and no sale.
    /// An administrator logon of the admin area succeeds as <see cref="SessionRole.Administrator"/>, after which the
    /// item has exactly one sale: a tender sale to the client through the 25.00 tender. The session's
    /// <c>GET /api/app/admin/tenders/{id}</c> reports the item sold to "Clive Entworth" for 25.00, the 25.00 tender as
    /// the winning one and the 10.00 tender as not winning.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task AdminLogonClosesDueTenders()
    {
        DateOnly serverDate = await ServerDateAsync();
        DueTenderItem due = await CreateDueTenderItemAsync(serverDate);
        Assert.Empty(await ReadSalesAsync(due.ItemId));

        using HttpClient http = Api.CreateHttpClient();

        // Act
        string sessionId = await OpenAdministratorSessionAsync(http);

        // Assert
        StoredSale sale = Assert.Single(await ReadSalesAsync(due.ItemId));
        Assert.Equal(new StoredSale(TenderSaleType, ClientId, due.HigherTenderId), sale);

        TenderDetails winning = await GetTenderDetailsAsync(http, sessionId, due.HigherTenderId);
        Assert.True(winning.ItemSold);
        Assert.True(winning.IsWinning);
        Assert.Equal(ClientName, winning.WinnerClientName);
        Assert.Equal(HigherOffer, winning.WinningOffer);

        TenderDetails losing = await GetTenderDetailsAsync(http, sessionId, due.LowerTenderId);
        Assert.True(losing.ItemSold);
        Assert.False(losing.IsWinning);
        Assert.Equal(ClientName, losing.WinnerClientName);
        Assert.Equal(HigherOffer, losing.WinningOffer);
    }

    /// <summary>
    /// Without an <c>X-Erewhon-Session</c> header, <c>GET /api/app/logon-options?area=shop</c> answers 200 with data
    /// loaded and the client "Clive Entworth" listed, <c>GET /api/app/logon-options?area=admin</c> answers 200 with data
    /// loaded and the Administrator option enabled, <c>POST /api/app/sessions</c> for the shop area and "Clive Entworth"
    /// answers 200 with a succeeded <see cref="SessionRole.Client"/> session for the client, and
    /// <c>POST /api/app/setup/load-data</c> answers 409, not 401. <c>GET /api/app/about</c> with the new session's id
    /// then answers 200.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task BootstrapEndpointsNeedNoSession()
    {
        using HttpClient http = Api.CreateHttpClient();

        LogonOptionsResponse shopOptions = await GetLogonOptionsAsync(http, "shop");
        Assert.True(shopOptions.DataLoaded);
        Assert.Equal(LogonArea.Shop, shopOptions.Area);
        Assert.Contains(shopOptions.Users, user => user.Id == ClientId && user.Name == ClientName);

        LogonOptionsResponse adminOptions = await GetLogonOptionsAsync(http, "admin");
        Assert.True(adminOptions.DataLoaded);
        Assert.Equal(LogonArea.Admin, adminOptions.Area);
        Assert.True(adminOptions.AdministratorEnabled);

        CreateSessionResponse session = await OpenSessionAsync(
            http,
            new CreateSessionRequest(LogonArea.Shop, Administrator: false, UserName: ClientName));
        Assert.Equal(SessionRole.Client, session.Role);
        Assert.Equal(ClientId, session.ClientId);
        string sessionId = SessionIdOf(session);

        using (HttpResponseMessage loadData = await SendAsync(http, HttpMethod.Post, LoadDataPath, sessionId: null))
        {
            Assert.Equal(HttpStatusCode.Conflict, loadData.StatusCode);
        }

        using HttpResponseMessage about = await SendAsync(http, HttpMethod.Get, AboutPath, sessionId);
        Assert.Equal(HttpStatusCode.OK, about.StatusCode);
    }

    /// <summary>
    /// A request to the protected endpoint <paramref name="method"/> <paramref name="path"/> with the session header
    /// <paramref name="header"/> answers 401 with an empty body and no content type; <c>close-now</c> carries the body
    /// <c>{date: D}</c>, D being the server date. Afterwards a tender item closing on D that holds two tenders is still
    /// unsold and the agent holds no soft edit lock.
    /// </summary>
    /// <param name="method">The HTTP method of the endpoint.</param>
    /// <param name="path">The route of the endpoint, with <c>{agentId}</c> standing for the fixture agent's row id.</param>
    /// <param name="header">The session header the request carries.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedEndpointsReturn401WithoutSession(string method, string path, SessionHeader header)
    {
        DateOnly serverDate = await ServerDateAsync();
        DueTenderItem due = await CreateDueTenderItemAsync(serverDate);
        string route = path.Replace(AgentIdToken, AgentId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var httpMethod = new HttpMethod(method);
        object? body = httpMethod == HttpMethod.Post ? new { date = serverDate } : null;
        string? sessionId = header == SessionHeader.Unregistered ? Guid.NewGuid().ToString("N") : null;

        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await SendAsync(http, httpMethod, route, sessionId, body);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentType);

        Assert.Empty(await ReadSalesAsync(due.ItemId));
        Assert.Equal(new StoredSoftLock(null, null), await ReadAgentSoftLockAsync());
    }

    /// <summary>
    /// A hub connection whose <c>session</c> query value is a well-formed id that no session holds, and one with no
    /// <c>session</c> query value, each fail to start or close within five seconds of starting, ending
    /// <see cref="HubConnectionState.Disconnected"/>. A connection with the id of a registered shop session starts and
    /// is still <see cref="HubConnectionState.Connected"/>, and not closed, one second later.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task HubRefusesUnknownSession()
    {
        await AssertHubRefusedAsync(Api.HubUrl(Guid.NewGuid().ToString("N")));
        await AssertHubRefusedAsync(new Uri(Api.BaseAddress, HubPath));

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = SessionIdOf(await OpenSessionAsync(
            http,
            new CreateSessionRequest(LogonArea.Shop, Administrator: false, UserName: ClientName)));

        await using HubConnection control = new HubConnectionBuilder().WithUrl(Api.HubUrl(sessionId)).Build();
        Task<Exception?> closed = ObserveClosed(control);
        await control.StartAsync();
        await Task.Delay(HubControlPeriod);

        Assert.Equal(HubConnectionState.Connected, control.State);
        Assert.False(closed.IsCompleted);
    }

    /// <summary>
    /// Administrator session A sets the agent's soft edit lock with <c>POST /api/app/edit-locks/Agent/{id}</c>, which
    /// answers 200 with a succeeded result and no errors, and the agent row then holds A as <c>locked_by_session</c> and
    /// a <c>locked_timestamp</c>. A's <c>GET</c> of the same route answers 200 with the agent's entity type and id,
    /// locked, locked by the caller, the thing "thing", the <c>$ThingCurrentlyEdited</c> prompt and the
    /// <c>$ThingAlreadyBeingEdited</c> title for that thing. A's <c>DELETE</c> then answers 200 with a succeeded result
    /// and leaves both lock columns null.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task OpeningOwnLockedRowReportsLock()
    {
        using HttpClient http = Api.CreateHttpClient();
        string sessionA = await OpenAdministratorSessionAsync(http);

        OperationResult locked = await SendForOperationResultAsync(http, HttpMethod.Post, AgentEditLockPath, sessionA);
        Assert.True(locked.Succeeded, locked.FullErrorText);
        Assert.Empty(locked.Errors);
        StoredSoftLock stored = await ReadAgentSoftLockAsync();
        Assert.Equal(sessionA, stored.LockedBySession);
        Assert.NotNull(stored.LockedTimestamp);

        // Act
        SoftLockStatus status = await GetSoftLockStatusAsync(http, sessionA);

        // Assert
        Assert.Equal(AgentEntityType, status.EntityType);
        Assert.Equal(AgentId, status.Id);
        Assert.True(status.IsLocked);
        Assert.True(status.IsLockedByCaller);
        Assert.Equal(MessageCatalog.Get(MessageKeys.Thing), status.Thing);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ThingCurrentlyEdited, status.Thing), status.Prompt);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ThingAlreadyBeingEdited, status.Thing), status.Title);
        Assert.Equal(stored, await ReadAgentSoftLockAsync());

        OperationResult unlocked = await SendForOperationResultAsync(http, HttpMethod.Delete, AgentEditLockPath, sessionA);
        Assert.True(unlocked.Succeeded, unlocked.FullErrorText);
        Assert.Equal(new StoredSoftLock(null, null), await ReadAgentSoftLockAsync());
    }

    /// <summary>
    /// Administrator session A sets the agent's soft edit lock. Administrator session B's
    /// <c>DELETE /api/app/edit-locks/Agent/{id}</c> leaves the agent row's <c>locked_by_session</c> (A) and
    /// <c>locked_timestamp</c> unchanged, and B's <c>GET</c> of the same route reports the row locked but not by the
    /// caller. A's <c>DELETE</c> then leaves both lock columns null.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task NonOwnerCannotUnlock()
    {
        using HttpClient http = Api.CreateHttpClient();
        string sessionA = await OpenAdministratorSessionAsync(http);
        string sessionB = await OpenAdministratorSessionAsync(http);
        Assert.NotEqual(sessionA, sessionB);

        OperationResult locked = await SendForOperationResultAsync(http, HttpMethod.Post, AgentEditLockPath, sessionA);
        Assert.True(locked.Succeeded, locked.FullErrorText);
        StoredSoftLock lockedByA = await ReadAgentSoftLockAsync();
        Assert.Equal(sessionA, lockedByA.LockedBySession);
        Assert.NotNull(lockedByA.LockedTimestamp);

        // Act
        (await SendAsync(http, HttpMethod.Delete, AgentEditLockPath, sessionB)).Dispose();

        // Assert
        Assert.Equal(lockedByA, await ReadAgentSoftLockAsync());

        SoftLockStatus seenByB = await GetSoftLockStatusAsync(http, sessionB);
        Assert.True(seenByB.IsLocked);
        Assert.False(seenByB.IsLockedByCaller);

        OperationResult unlocked = await SendForOperationResultAsync(http, HttpMethod.Delete, AgentEditLockPath, sessionA);
        Assert.True(unlocked.Succeeded, unlocked.FullErrorText);
        Assert.Equal(new StoredSoftLock(null, null), await ReadAgentSoftLockAsync());
    }

    /// <summary>
    /// With the test's company present, <c>POST /api/app/setup/load-data</c> without a session header answers 409 with
    /// a load result that is not loaded, built no tender sales and holds no output lines. The row ids of
    /// <c>companies</c> and of <c>agents</c> are the same afterwards as before, the test's company among them.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task LoadDataRefusedWhenCompanyExists()
    {
        long companyId = Fixture(Builder.MyCompany, nameof(Builder.MyCompany)).Id;
        IReadOnlyList<long> companiesBefore = await ReadIdsAsync(CompanyIdsSql);
        IReadOnlyList<long> agentsBefore = await ReadIdsAsync(AgentIdsSql);
        Assert.Contains(companyId, companiesBefore);
        Assert.Contains(AgentId, agentsBefore);

        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await SendAsync(http, HttpMethod.Post, LoadDataPath, sessionId: null);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        LoadDataResponse? refused = await response.Content.ReadFromJsonAsync<LoadDataResponse>(Json);
        Assert.NotNull(refused);
        Assert.False(refused.Loaded);
        Assert.Equal(0, refused.TenderSalesBuilt);
        Assert.Empty(refused.OutputLines);

        Assert.Equal(companiesBefore, await ReadIdsAsync(CompanyIdsSql));
        Assert.Equal(agentsBefore, await ReadIdsAsync(AgentIdsSql));
    }

    /// <summary>Returns <paramref name="value"/>, or throws when <see cref="SetUpAsync"/> has not set it.</summary>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"{name} is created by {nameof(SetUpAsync)}.");

    /// <summary>Adds the <c>X-Erewhon-Session</c> header with <paramref name="sessionId"/> to <paramref name="request"/>.</summary>
    private static HttpRequestMessage WithSession(HttpRequestMessage request, string sessionId)
    {
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        return request;
    }

    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="path"/>, with the session header when <paramref name="sessionId"/>
    /// is set and with <paramref name="body"/> as JSON when it is set, and returns the response for the caller to
    /// dispose.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpMethod method,
        string path,
        string? sessionId,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (sessionId is not null)
        {
            WithSession(request, sessionId);
        }

        return await http.SendAsync(request);
    }

    /// <summary>
    /// Sends <c>POST /api/app/sessions</c> with <paramref name="request"/> and no session header, asserts 200 with a
    /// succeeded response that carries a session id, and returns the response.
    /// </summary>
    private static async Task<CreateSessionResponse> OpenSessionAsync(HttpClient http, CreateSessionRequest request)
    {
        using HttpResponseMessage response = await SendAsync(http, HttpMethod.Post, SessionsPath, sessionId: null, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateSessionResponse? session = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(Json);
        Assert.NotNull(session);
        Assert.True(session.Succeeded, session.Message);
        Assert.NotNull(session.SessionId);
        return session;
    }

    /// <summary>
    /// Opens an administrator session of the admin area, asserts its role is <see cref="SessionRole.Administrator"/>
    /// and returns its id.
    /// </summary>
    private static async Task<string> OpenAdministratorSessionAsync(HttpClient http)
    {
        CreateSessionResponse session = await OpenSessionAsync(
            http,
            new CreateSessionRequest(LogonArea.Admin, Administrator: true, UserName: null));
        Assert.Equal(SessionRole.Administrator, session.Role);
        return SessionIdOf(session);
    }

    /// <summary>Returns the session id of a succeeded logon response.</summary>
    private static string SessionIdOf(CreateSessionResponse session) =>
        session.SessionId ?? throw new InvalidOperationException("The opened session has no id.");

    /// <summary>
    /// Sends <c>GET /api/app/logon-options?area=<paramref name="area"/></c> with no session header, asserts 200 and
    /// returns the logon options.
    /// </summary>
    private static async Task<LogonOptionsResponse> GetLogonOptionsAsync(HttpClient http, string area)
    {
        using HttpResponseMessage response = await SendAsync(
            http,
            HttpMethod.Get,
            $"{LogonOptionsPath}?area={area}",
            sessionId: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LogonOptionsResponse? options = await response.Content.ReadFromJsonAsync<LogonOptionsResponse>(Json);
        Assert.NotNull(options);
        return options;
    }

    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="path"/> in the session <paramref name="sessionId"/>, asserts 200
    /// and returns the operation result of the body.
    /// </summary>
    private static async Task<OperationResult> SendForOperationResultAsync(
        HttpClient http,
        HttpMethod method,
        string path,
        string sessionId)
    {
        using HttpResponseMessage response = await SendAsync(http, method, path, sessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        OperationResult? result = await response.Content.ReadFromJsonAsync<OperationResult>(Json);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    /// Sends <c>GET /api/app/edit-locks/Agent/{id}</c> for the fixture agent in the session <paramref name="sessionId"/>,
    /// asserts 200 and returns the soft edit lock status of the body.
    /// </summary>
    private async Task<SoftLockStatus> GetSoftLockStatusAsync(HttpClient http, string sessionId)
    {
        using HttpResponseMessage response = await SendAsync(http, HttpMethod.Get, AgentEditLockPath, sessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SoftLockStatus? status = await response.Content.ReadFromJsonAsync<SoftLockStatus>(Json);
        Assert.NotNull(status);
        return status;
    }

    /// <summary>
    /// Sends <c>GET /api/app/admin/tenders/{tenderId}</c> in the session <paramref name="sessionId"/>, asserts 200 and
    /// returns whether the tender won its item, whether the item is sold, the buyer's name and the winning offer.
    /// </summary>
    private static async Task<TenderDetails> GetTenderDetailsAsync(HttpClient http, string sessionId, long tenderId)
    {
        using HttpResponseMessage response = await SendAsync(
            http,
            HttpMethod.Get,
            $"{AdminTendersPath}/{tenderId}",
            sessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        JsonElement winningOffer = root.GetProperty("winningOffer");
        return new TenderDetails(
            root.GetProperty("isWinning").GetBoolean(),
            root.GetProperty("itemSold").GetBoolean(),
            root.GetProperty("winnerClientName").GetString(),
            winningOffer.ValueKind == JsonValueKind.Null ? null : winningOffer.GetDecimal());
    }

    /// <summary>
    /// Starts a hub connection to <paramref name="hubUrl"/> and asserts that the start fails, or that the connection
    /// closes within <see cref="HubRefusalTimeout"/> of starting, and that it ends
    /// <see cref="HubConnectionState.Disconnected"/>. The connection is disposed afterwards.
    /// </summary>
    private static async Task AssertHubRefusedAsync(Uri hubUrl)
    {
        await using HubConnection connection = new HubConnectionBuilder().WithUrl(hubUrl).Build();
        Task<Exception?> closed = ObserveClosed(connection);

        Exception? startFailure = await Record.ExceptionAsync(() => connection.StartAsync());
        if (startFailure is null)
        {
            await closed.WaitAsync(HubRefusalTimeout);
        }

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    /// <summary>Returns a task that completes with the error, if any, when <paramref name="connection"/> closes.</summary>
    private static Task<Exception?> ObserveClosed(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    /// <summary>Returns the server date, read in a new scope of <see cref="ErewhonTestBase.Api"/>.</summary>
    private async Task<DateOnly> ServerDateAsync()
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync();
    }

    /// <summary>
    /// Creates the tender item "Proxy Tender" closing on <paramref name="serverDate"/> in the fixture region and
    /// category, sold by the fixture agent, then a tender of <see cref="LowerOffer"/> and a tender of
    /// <see cref="HigherOffer"/> by the fixture client on it, in that order.
    /// </summary>
    private async Task<DueTenderItem> CreateDueTenderItemAsync(DateOnly serverDate)
    {
        TenderItem item = await Builder.CreateTenderItemAsync(
            Fixture(Builder.MyRegion, nameof(Builder.MyRegion)),
            Fixture(Builder.MyItemCategory, nameof(Builder.MyItemCategory)),
            Fixture(Builder.MyAgent, nameof(Builder.MyAgent)));
        Assert.Equal(serverDate, item.ClosureDate);

        Client client = Fixture(Builder.MyClient, nameof(Builder.MyClient));
        Tender lower = await Builder.CreateTenderAsync(client, item, LowerOffer);
        Tender higher = await Builder.CreateTenderAsync(client, item, HigherOffer);

        return new DueTenderItem(item.Id, lower.Id, higher.Id);
    }

    /// <summary>
    /// Returns the <c>sale_type</c>, <c>client_id</c> and <c>tender_id</c> of each committed <c>sales</c> row of the
    /// item <paramref name="itemId"/>, in row id order.
    /// </summary>
    private async Task<IReadOnlyList<StoredSale>> ReadSalesAsync(long itemId)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sale_type, client_id, tender_id FROM sales WHERE item_id = @itemId ORDER BY id",
            connection);
        command.Parameters.AddWithValue("itemId", itemId);

        var sales = new List<StoredSale>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sales.Add(new StoredSale(
                reader.GetString(0),
                reader.GetInt64(1),
                await reader.IsDBNullAsync(2) ? null : reader.GetInt64(2)));
        }

        return sales;
    }

    /// <summary>
    /// Returns the committed <c>locked_by_session</c> and <c>locked_timestamp</c> of the fixture agent's row: the lock
    /// owner and lock time, which <see cref="SoftLockStatus"/> does not carry (D-175).
    /// </summary>
    private async Task<StoredSoftLock> ReadAgentSoftLockAsync()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT locked_by_session, locked_timestamp FROM agents WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", AgentId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredSoftLock(
            await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetDateTime(1));
    }

    /// <summary>Returns the committed row ids that <paramref name="sql"/> selects, in the order it returns them.</summary>
    /// <param name="sql">A statement selecting one <c>bigint</c> id column.</param>
    private async Task<IReadOnlyList<long>> ReadIdsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var ids = new List<long>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>The row ids of a tender item closing on the server date and of its lower and higher tender.</summary>
    private sealed record DueTenderItem(long ItemId, long LowerTenderId, long HigherTenderId);

    /// <summary>The <c>sale_type</c>, <c>client_id</c> and <c>tender_id</c> of a committed <c>sales</c> row.</summary>
    private sealed record StoredSale(string SaleType, long ClientId, long? TenderId);

    /// <summary>The committed <c>locked_by_session</c> and <c>locked_timestamp</c> of a row.</summary>
    private sealed record StoredSoftLock(string? LockedBySession, DateTime? LockedTimestamp);

    /// <summary>
    /// The values of <c>GET /api/app/admin/tenders/{id}</c> the admin start-up closure test reads: whether the tender
    /// won its item, whether the item is sold, the buyer's name and the winning offer.
    /// </summary>
    private sealed record TenderDetails(bool IsWinning, bool ItemSold, string? WinnerClientName, decimal? WinningOffer);
}

