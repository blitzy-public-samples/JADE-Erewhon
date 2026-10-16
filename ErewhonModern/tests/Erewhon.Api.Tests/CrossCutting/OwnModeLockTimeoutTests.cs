using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Events;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the lock-wait bound of app writes that run a transaction agent in its own transaction and take no
/// advisory lock. Each case starts a further API over the assembly database with <c>Erewhon:Locks:TimeoutMs</c> =
/// 500 and its own log sink, holds the written row with <c>SELECT id FROM ... WHERE id = @id FOR UPDATE</c> in an
/// open transaction on a second connection, sends the write, releases the row by rolling that transaction back, and
/// sends the write again.
/// </summary>
public sealed class OwnModeLockTimeoutTests : ErewhonTestBase
{
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";

    private const string ShortLockWaitMs = "500";

    private const string JsonMediaType = "application/json";

    private const string RequestLoggingSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private const string SourceContextProperty = "SourceContext";

    private const string EventIdProperty = "EventId";

    private const string SessionUserName = "Own Mode Lock Timeout";

    private const string UpdatedStreet = "1 Lock Wait Lane";

    private const string LockItemRowSql = "SELECT id FROM items WHERE id = @id FOR UPDATE";

    private const string LockAgentRowSql = "SELECT id FROM agents WHERE id = @id FOR UPDATE";

    private const string ItemRowTextSql = "SELECT t::text FROM items AS t WHERE t.id = @id";

    private const string AgentRowTextSql = "SELECT t::text FROM agents AS t WHERE t.id = @id";

    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(5);

    /// <summary>The app write sent while its row is held.</summary>
    public enum OwnModeWrite
    {
        /// <summary>
        /// <c>POST /api/app/edit-locks/TenderItem/{id}</c> on the unlocked tender item, the soft-lock modify 1001; the
        /// item's <c>items</c> row is held.
        /// </summary>
        SetSoftLock,

        /// <summary>
        /// <c>DELETE /api/app/edit-locks/RetailItem/{id}</c> by the session that locked the retail item before its row
        /// was held, the soft-lock modify 1002; the item's <c>items</c> row is held.
        /// </summary>
        ClearOwnSoftLock,

        /// <summary>
        /// <c>PUT /api/app/admin/agents/{id}</c> with the agent's own name and a new street, an update that takes no
        /// advisory lock; the agent's <c>agents</c> row is held.
        /// </summary>
        UpdateAgentKeepingName,
    }

    /// <summary>Each app write sent while its row is held.</summary>
    public static TheoryData<OwnModeWrite> Writes => new()
    {
        OwnModeWrite.SetSoftLock,
        OwnModeWrite.ClearOwnSoftLock,
        OwnModeWrite.UpdateAgentKeepingName,
    };

    /// <summary>
    /// While another transaction holds the written row, the write answers status 500 with the <c>application/json</c>
    /// <see cref="ErrorOutcome"/> that <see cref="ErrorOutcomeMiddleware.Create"/> builds for an
    /// <see cref="ErewhonLockException"/>: kind <see cref="ErrorOutcome.KindLock"/>, heading
    /// <c>$BaseLockMessageHeading</c> and body <c>$BaseLockMessageBodyWeb</c>; it answers after at least 400 ms and
    /// within 5 s; no error or fatal event other than the HTTP request event has event id
    /// <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/> or an exception, and no event of any level has
    /// that event id; the row is unchanged. Once the row is released, the same write answers 200 with a succeeded
    /// <see cref="OperationResult"/> and no errors.
    /// </summary>
    /// <param name="write">The app write sent while its row is held.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task RowLockWaitEndsWithLockOutcomeWithinTimeout(OwnModeWrite write)
    {
        var sink = new CollectingLogSink();
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [LockTimeoutKey] = ShortLockWaitMs },
            sink);
        using HttpClient client = host.CreateHttpClient();
        string sessionId = host.Services.GetRequiredService<SessionRegistry>()
            .Create(LogonArea.Admin, SessionRole.Administrator, agentId: null, clientId: null, SessionUserName)
            .SessionId;

        HeldRow row = HeldRowOf(write);
        using (HttpResponseMessage status = await SendAsync(client, sessionId, HttpMethod.Get, row.EditLockPath))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        if (write == OwnModeWrite.ClearOwnSoftLock)
        {
            using HttpResponseMessage locked = await SendAsync(client, sessionId, HttpMethod.Post, row.EditLockPath);
            await AssertSucceededAsync(locked);
        }

        string before = await ReadRowTextAsync(row);
        sink.Clear();

        HeldWriteOutcome held = await SendWhileRowHeldAsync(row, () => SendWriteAsync(client, sessionId, write));

        Assert.Equal(HttpStatusCode.InternalServerError, held.StatusCode);
        Assert.Equal(JsonMediaType, held.MediaType);
        ErrorOutcome? outcome = JsonSerializer.Deserialize<ErrorOutcome>(held.Body);
        Assert.NotNull(outcome);
        Assert.Equal(ErrorOutcome.KindLock, outcome.Kind);
        Assert.Equal(MessageCatalog.Get(MessageKeys.BaseLockMessageHeading), outcome.Heading);
        Assert.Equal(ErrorOutcomeMiddleware.Create(new ErewhonLockException(innerException: null)), outcome);
        Assert.InRange(held.Elapsed, MinimumWait, MaximumWait);

        Assert.DoesNotContain(sink.Events, IsUnhandledExceptionEvent);
        Assert.DoesNotContain(
            sink.ErrorEvents,
            logEvent => !IsRequestLoggingEvent(logEvent)
                && (IsUnhandledExceptionEvent(logEvent) || logEvent.Exception is not null));

        Assert.Equal(before, await ReadRowTextAsync(row));

        using HttpResponseMessage retried = await SendWriteAsync(client, sessionId, write);
        await AssertSucceededAsync(retried);
    }

    /// <summary>
    /// Builds the fixtures after the company of <see cref="ErewhonTestBase.SetUpAsync"/>: the agent "John Smith", the
    /// country "France" with its region "Paris", the item category "Abstractions" and, in them, the retail item
    /// "Proxy Retail" and the tender item "Proxy Tender", each sold by the agent and held in its <c>My*</c> property.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Agent agent = await Builder.CreateAgentAsync();
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory category = await Builder.CreateItemCategoryAsync(
            "Abstractions",
            "ABST",
            "Items that do not model real world observables");

        Builder.MyAgent = agent;
        Builder.MyCountry = country;
        Builder.MyRegion = region;
        Builder.MyItemCategory = category;
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(region, category, agent);
        Builder.MyTenderItem = await Builder.CreateTenderItemAsync(region, category, agent);
    }

    /// <summary>Sends a request with no body and the session header to <paramref name="path"/>.</summary>
    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string sessionId,
        HttpMethod method,
        string path) =>
        SendAsync(client, sessionId, new HttpRequestMessage(method, path));

    /// <summary>Adds the session header to <paramref name="request"/>, sends it and disposes it.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string sessionId,
        HttpRequestMessage request)
    {
        using (request)
        {
            request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
            return await client.SendAsync(request);
        }
    }

    /// <summary>Asserts status 200 with an <see cref="OperationResult"/> body that succeeded with no errors.</summary>
    private static async Task AssertSucceededAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("errors").GetArrayLength());
    }

    /// <summary>Returns whether the event's <c>EventId</c> is <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.</summary>
    private static bool IsUnhandledExceptionEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(EventIdProperty, out LogEventPropertyValue? eventId)
        && eventId is StructureValue structure
        && structure.Properties.Any(property =>
            property.Name == "Id"
            && Equals(property.Value, new ScalarValue(ErrorOutcomeMiddleware.UnhandledExceptionEventId.Id)));

    /// <summary>Returns whether the event is the HTTP request event of the Serilog request logging.</summary>
    private static bool IsRequestLoggingEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(SourceContextProperty, out LogEventPropertyValue? sourceContext)
        && Equals(sourceContext, new ScalarValue(RequestLoggingSourceContext));

    /// <summary>Sends <paramref name="write"/> for its fixture row with the session header.</summary>
    private Task<HttpResponseMessage> SendWriteAsync(HttpClient client, string sessionId, OwnModeWrite write)
    {
        HeldRow row = HeldRowOf(write);
        return write switch
        {
            OwnModeWrite.SetSoftLock => SendAsync(client, sessionId, HttpMethod.Post, row.EditLockPath),
            OwnModeWrite.ClearOwnSoftLock => SendAsync(client, sessionId, HttpMethod.Delete, row.EditLockPath),
            OwnModeWrite.UpdateAgentKeepingName => SendAsync(client, sessionId, AgentUpdateRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null),
        };
    }

    /// <summary>
    /// Returns <c>PUT /api/app/admin/agents/{id}</c> for the fixture agent with its own name and its
    /// <see cref="TestCompanyBuilder.NohwereLaneAddress"/> address with the street <see cref="UpdatedStreet"/>.
    /// </summary>
    private HttpRequestMessage AgentUpdateRequest()
    {
        Agent agent = Fixture(Builder.MyAgent, nameof(Builder.MyAgent));
        AddressCommand address = TestCompanyBuilder.NohwereLaneAddress();
        var body = new AgentRequest(
            agent.Name,
            new AddressFields(
                UpdatedStreet,
                address.City,
                address.Country,
                address.Phone,
                address.Fax,
                address.Email,
                address.WebSite));

        return new HttpRequestMessage(HttpMethod.Put, $"api/app/admin/agents/{agent.Id}")
        {
            Content = JsonContent.Create(body),
        };
    }

    /// <summary>Returns the row <paramref name="write"/> writes, with its edit-lock path and its two statements.</summary>
    private HeldRow HeldRowOf(OwnModeWrite write) =>
        write switch
        {
            OwnModeWrite.SetSoftLock => ItemRow(
                nameof(TenderItem),
                Fixture(Builder.MyTenderItem, nameof(Builder.MyTenderItem)).Id),
            OwnModeWrite.ClearOwnSoftLock => ItemRow(
                nameof(RetailItem),
                Fixture(Builder.MyRetailItem, nameof(Builder.MyRetailItem)).Id),
            OwnModeWrite.UpdateAgentKeepingName => new HeldRow(
                Fixture(Builder.MyAgent, nameof(Builder.MyAgent)).Id,
                EditLockPath(nameof(Agent), Fixture(Builder.MyAgent, nameof(Builder.MyAgent)).Id),
                LockAgentRowSql,
                AgentRowTextSql),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null),
        };

    /// <summary>Returns the <c>items</c> row with id <paramref name="id"/>, edited as <paramref name="entityType"/>.</summary>
    private static HeldRow ItemRow(string entityType, long id) =>
        new(id, EditLockPath(entityType, id), LockItemRowSql, ItemRowTextSql);

    /// <summary>Returns the path <c>api/app/edit-locks/{entityType}/{id}</c>.</summary>
    private static string EditLockPath(string entityType, long id) => $"api/app/edit-locks/{entityType}/{id}";

    /// <summary>Returns <paramref name="value"/>, or throws when <see cref="SetUpAsync"/> has not set it.</summary>
    private static T Fixture<T>(T? value, string name)
        where T : class =>
        value ?? throw new InvalidOperationException($"{name} is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Holds <paramref name="row"/> with its <c>FOR UPDATE</c> statement in an open transaction on an unpooled
    /// connection, runs <paramref name="send"/> and reads its response, then rolls the transaction back, also when a
    /// step fails.
    /// </summary>
    private async Task<HeldWriteOutcome> SendWhileRowHeldAsync(HeldRow row, Func<Task<HttpResponseMessage>> send)
    {
        await using var holder = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Pooling = false }.ConnectionString);
        await holder.OpenAsync();
        await using NpgsqlTransaction hold = await holder.BeginTransactionAsync();
        try
        {
            await using (var lockRow = new NpgsqlCommand(row.LockSql, holder, hold))
            {
                lockRow.Parameters.AddWithValue("id", row.Id);
                Assert.Equal(row.Id, await lockRow.ExecuteScalarAsync());
            }

            var stopwatch = Stopwatch.StartNew();
            using HttpResponseMessage response = await send();
            string body = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();

            return new HeldWriteOutcome(
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                body,
                stopwatch.Elapsed);
        }
        finally
        {
            await hold.RollbackAsync();
        }
    }

    /// <summary>Returns the committed text form of <paramref name="row"/>, read on a new connection.</summary>
    private async Task<string> ReadRowTextAsync(HeldRow row)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand(row.TextSql, connection);
        read.Parameters.AddWithValue("id", row.Id);
        return Assert.IsType<string>(await read.ExecuteScalarAsync());
    }

    /// <summary>
    /// A row a write changes: its id, the edit-lock path of its entity, the statement that holds it and the statement
    /// that reads its text form.
    /// </summary>
    private sealed record HeldRow(long Id, string EditLockPath, string LockSql, string TextSql);

    /// <summary>The status, media type, body and elapsed time of the write sent while its row was held.</summary>
    private sealed record HeldWriteOutcome(HttpStatusCode StatusCode, string? MediaType, string Body, TimeSpan Elapsed);
}
