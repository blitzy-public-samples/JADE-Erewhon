using Erewhon.Api.Sessions;
using Erewhon.Application.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;

namespace Erewhon.Api.Realtime;

/// <summary>
/// SignalR hub at <see cref="Path"/> that the React app and the test clients connect to with the query value
/// <c>?session=&lt;sessionId&gt;</c>. A connection whose session is registered in <see cref="SessionRegistry"/> joins
/// the group <see cref="SessionGroup(string)"/> of that session and the group <see cref="AllGroup"/>; any other
/// connection is aborted. Clients only listen: the hub declares no method they can invoke.
/// <list type="bullet">
/// <item><description><see cref="EntityChanged"/> with <see cref="EntityChangedMessage"/> goes to <see cref="AllGroup"/>.</description></item>
/// <item><description><see cref="CartItemDeleted"/> with <see cref="CartItemDeletedMessage"/> goes to the session group of each cart that held the deleted item.</description></item>
/// <item><description><see cref="SaveOutstandingChanges"/> with <see cref="SaveOutstandingChangesMessage"/> goes to the session group of the editor holding the row's soft edit lock.</description></item>
/// <item><description><see cref="ClosureTick"/> with <see cref="ClosureTickMessage"/> and <see cref="ClosureCompleted"/> with <see cref="ClosureCompletedMessage"/> go to the session group of the session that started the closure schedule.</description></item>
/// </list>
/// </summary>
/// <param name="registry">The registry of logged-on sessions that a connection's <c>session</c> query value is looked up in.</param>
public sealed class ErewhonHub(SessionRegistry registry) : Hub
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
    /// session registry. A registered session adds the connection to its <see cref="SessionGroup(string)"/> group and
    /// to <see cref="AllGroup"/>, in that order; a missing, empty or unknown session id aborts the connection without
    /// adding it to any group.
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

        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(entry.SessionId), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, AllGroup, Context.ConnectionAborted);
        await base.OnConnectedAsync();
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
