using Erewhon.Api.Sessions;
using Erewhon.Application.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;

namespace Erewhon.Api.Realtime;

/// <summary>
/// SignalR hub at <see cref="Path"/> that the React app and the test clients connect to with the query value
/// <c>?session=&lt;sessionId&gt;</c>. A connection whose session is registered in <see cref="SessionRegistry"/> joins
/// the group <see cref="SessionGroup(string)"/> of that session and the group <see cref="AllGroup"/>, and is tracked in
/// <see cref="HubConnectionRegistry"/> until it disconnects; any other connection is aborted. When
/// <see cref="SessionRegistry.Remove(string)"/> removes the session, the registry aborts every connection of it, and
/// SignalR then takes the connection out of its groups. Clients only listen: the hub declares no method they can invoke.
/// <list type="bullet">
/// <item><description><see cref="EntityChanged"/> with <see cref="EntityChangedMessage"/> goes to <see cref="AllGroup"/>.</description></item>
/// <item><description><see cref="CartItemDeleted"/> with <see cref="CartItemDeletedMessage"/> goes to the session group of each cart that held the deleted item.</description></item>
/// <item><description><see cref="SaveOutstandingChanges"/> with <see cref="SaveOutstandingChangesMessage"/> goes to the session group of the editor holding the row's soft edit lock.</description></item>
/// <item><description><see cref="ClosureTick"/> with <see cref="ClosureTickMessage"/> and <see cref="ClosureCompleted"/> with <see cref="ClosureCompletedMessage"/> go to the session group of the session that started the closure schedule.</description></item>
/// </list>
/// </summary>
/// <param name="registry">The registry of logged-on sessions that a connection's <c>session</c> query value is looked up in.</param>
/// <param name="connections">The registry that tracks each accepted connection under its session.</param>
public sealed class ErewhonHub(SessionRegistry registry, HubConnectionRegistry connections) : Hub
{
    /// <summary>The route the hub is mapped to.</summary>
    public const string Path = "/hubs/erewhon";

    /// <summary>The group every accepted connection joins.</summary>
    public const string AllGroup = "all";

    /// <summary>
    /// The client method name of the message announcing a committed change to one row, sent to <see cref="AllGroup"/>
    /// with an <see cref="EntityChangedMessage"/>.
    /// </summary>
    public const string EntityChanged = "EntityChanged";

    /// <summary>
    /// The client method name of the message announcing that a deleted item was removed from the session's cart,
    /// sent to a session group with a <see cref="CartItemDeletedMessage"/>.
    /// </summary>
    public const string CartItemDeleted = "CartItemDeleted";

    /// <summary>
    /// The client method name of the request to save outstanding changes to a soft-locked row, sent to the editor's
    /// session group with a <see cref="SaveOutstandingChangesMessage"/>.
    /// </summary>
    public const string SaveOutstandingChanges = "SaveOutstandingChanges";

    /// <summary>
    /// The client method name of one countdown tick of a tender closure schedule, sent to the scheduling session's
    /// group with a <see cref="ClosureTickMessage"/>.
    /// </summary>
    public const string ClosureTick = "ClosureTick";

    /// <summary>
    /// The client method name of the result of one scheduled tender closure, sent to the scheduling session's group
    /// with a <see cref="ClosureCompletedMessage"/>.
    /// </summary>
    public const string ClosureCompleted = "ClosureCompleted";

    /// <summary>
    /// Returns the name of the group that the connections of one session join.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><c>session:&lt;sessionId&gt;</c>.</returns>
    public static string SessionGroup(string sessionId) => $"session:{sessionId}";

    /// <summary>
    /// Looks up the connection's first <see cref="SessionMiddleware.HubSessionQueryKey"/> query-string value in the
    /// session registry. A missing, empty or unknown session id aborts the connection without tracking it or adding it
    /// to any group. A registered session tracks the connection under the session in
    /// <see cref="HubConnectionRegistry"/>, adds it to the session's <see cref="SessionGroup(string)"/> group and to
    /// <see cref="AllGroup"/>, in that order, and then looks the session up again: when the session has been removed in
    /// the meantime, the connection is untracked and aborted. When adding the connection to a group throws, the
    /// connection is untracked and the exception is rethrown.
    /// </summary>
    /// <returns>A task that completes when the connection has been accepted into its groups or aborted.</returns>
    public override async Task OnConnectedAsync()
    {
        var values = Context.GetHttpContext()?.Request.Query[SessionMiddleware.HubSessionQueryKey]
            ?? StringValues.Empty;
        var sessionId = values.Count > 0 ? values[0] : null;
        if (!registry.TryGet(sessionId, out var entry))
        {
            Context.Abort();
            return;
        }

        connections.Track(entry.SessionId, Context);
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(entry.SessionId), Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, AllGroup, Context.ConnectionAborted);
        }
        catch
        {
            connections.Untrack(Context.ConnectionId);
            throw;
        }

        if (!registry.TryGet(entry.SessionId, out _))
        {
            connections.Untrack(Context.ConnectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Untracks the connection in <see cref="HubConnectionRegistry"/>; a connection that was never tracked, or has
    /// already been untracked, changes nothing. SignalR removes the connection from its groups.
    /// </summary>
    /// <param name="exception">The error that closed the connection, or <see langword="null"/> when it closed normally.</param>
    /// <returns>A task that completes when the connection has been untracked.</returns>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Untrack(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Tracks the accepted connections of <see cref="ErewhonHub"/> in process memory, keyed by the ordinal session id and,
/// within a session, by the ordinal connection id. When <see cref="SessionRegistry.SessionRemoved"/> is raised for a
/// session, every connection tracked under it is untracked and aborted: the hub sends an aborted connection no further
/// event, and SignalR then closes it, removes it from its groups and runs <see cref="ErewhonHub.OnDisconnectedAsync"/>.
/// Every member is safe to call from several threads at once.
/// </summary>
public sealed class HubConnectionRegistry : IDisposable
{
    private readonly object _gate = new();

    private readonly Dictionary<string, Dictionary<string, HubCallerContext>> _bySession = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _sessionByConnection = new(StringComparer.Ordinal);

    private readonly SessionRegistry _sessions;

    /// <summary>
    /// Creates an empty registry that aborts the tracked connections of each session removed from
    /// <paramref name="sessions"/>.
    /// </summary>
    /// <param name="sessions">The registry whose <see cref="SessionRegistry.SessionRemoved"/> event aborts connections.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public HubConnectionRegistry(SessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _sessions.SessionRemoved += OnSessionRemoved;
    }

    /// <summary>
    /// Tracks <paramref name="connection"/> under <paramref name="sessionId"/>. A connection already tracked, under
    /// this or another session, is moved to <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">The id of the session the connection was accepted for.</param>
    /// <param name="connection">The caller context of the accepted connection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> or <paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public void Track(string sessionId, HubCallerContext connection)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            RemoveTracked(connection.ConnectionId);

            if (!_bySession.TryGetValue(sessionId, out var tracked))
            {
                tracked = new Dictionary<string, HubCallerContext>(StringComparer.Ordinal);
                _bySession[sessionId] = tracked;
            }

            tracked[connection.ConnectionId] = connection;
            _sessionByConnection[connection.ConnectionId] = sessionId;
        }
    }

    /// <summary>
    /// Stops tracking the connection with id <paramref name="connectionId"/>.
    /// </summary>
    /// <param name="connectionId">The connection id; null, empty or untracked removes nothing.</param>
    /// <returns><see langword="true"/> when the connection was tracked and has been untracked; otherwise <see langword="false"/>.</returns>
    public bool Untrack(string? connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return false;
        }

        lock (_gate)
        {
            return RemoveTracked(connectionId);
        }
    }

    /// <summary>
    /// Stops reacting to <see cref="SessionRegistry.SessionRemoved"/>. Calling it more than once has no further effect.
    /// </summary>
    public void Dispose() => _sessions.SessionRemoved -= OnSessionRemoved;

    /// <summary>
    /// Removes the connection with id <paramref name="connectionId"/> from both maps, dropping its session's entry when
    /// the session has no tracked connection left. The caller holds the lock.
    /// </summary>
    private bool RemoveTracked(string connectionId)
    {
        if (!_sessionByConnection.Remove(connectionId, out var sessionId))
        {
            return false;
        }

        if (_bySession.TryGetValue(sessionId, out var tracked)
            && tracked.Remove(connectionId)
            && tracked.Count == 0)
        {
            _bySession.Remove(sessionId);
        }

        return true;
    }

    /// <summary>
    /// Untracks every connection tracked under <paramref name="sessionId"/> and then, outside the lock, aborts each one.
    /// </summary>
    private void OnSessionRemoved(string sessionId)
    {
        HubCallerContext[] removed;
        lock (_gate)
        {
            if (!_bySession.Remove(sessionId, out var tracked))
            {
                return;
            }

            removed = tracked.Values.ToArray();
            foreach (var connectionId in tracked.Keys)
            {
                _sessionByConnection.Remove(connectionId);
            }
        }

        foreach (var connection in removed)
        {
            connection.Abort();
        }
    }
}

/// <summary>
/// Payload of <see cref="ErewhonHub.EntityChanged"/>: one committed create, update or delete of a persisted row.
/// Sent as <c>{entityType, id, kind}</c> with <c>kind</c> one of <c>added</c>, <c>updated</c> or <c>deleted</c>.
/// </summary>
/// <param name="EntityType">The domain class name of the changed row, such as <c>RetailItem</c>, <c>TenderItem</c>, <c>Tender</c>, <c>ItemCategory</c> or <c>Agent</c>.</param>
/// <param name="Id">The internal row id of the changed row.</param>
/// <param name="Kind">The kind of change.</param>
public sealed record EntityChangedMessage(string EntityType, long Id, ChangeKind Kind);

/// <summary>
/// Payload of <see cref="ErewhonHub.CartItemDeleted"/>: an item deleted by another user that has been removed from
/// the receiving session's cart. Sent as <c>{itemId}</c>.
/// </summary>
/// <param name="ItemId">The internal row id (<c>items.id</c>) of the deleted item.</param>
public sealed record CartItemDeletedMessage(long ItemId);

/// <summary>
/// Payload of <see cref="ErewhonHub.SaveOutstandingChanges"/>: the row whose editor is asked to save outstanding
/// changes. Sent as <c>{entityType, id}</c>.
/// </summary>
/// <param name="EntityType">The domain class name of the row being edited, such as <c>Agent</c>.</param>
/// <param name="Id">The internal row id of the row being edited.</param>
public sealed record SaveOutstandingChangesMessage(string EntityType, long Id);

/// <summary>
/// Payload of <see cref="ErewhonHub.ClosureTick"/>: the countdown of a running tender closure schedule.
/// Sent as <c>{secondsToGo}</c>.
/// </summary>
/// <param name="SecondsToGo">The number of seconds left before the next scheduled closure.</param>
public sealed record ClosureTickMessage(int SecondsToGo);

/// <summary>
/// Payload of <see cref="ErewhonHub.ClosureCompleted"/>: the result of one scheduled tender closure.
/// Sent as <c>{date, numClosed}</c> with <c>date</c> in <c>yyyy-MM-dd</c> form.
/// </summary>
/// <param name="Date">The date the tenders were closed at.</param>
/// <param name="NumClosed">The number of tender items awarded by the closure.</param>
public sealed record ClosureCompletedMessage(DateOnly Date, int NumClosed);
