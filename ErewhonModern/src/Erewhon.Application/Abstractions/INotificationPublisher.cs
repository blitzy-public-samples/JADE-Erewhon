using Erewhon.Application.Notifications;

namespace Erewhon.Application.Abstractions;

/// <summary>
/// Pushes change notifications to the clients connected to the Erewhon hub. Each member sends one kind of
/// message to one hub group: <c>all</c>, which every connection joins, or <c>session:&lt;sessionId&gt;</c>,
/// which the connections of one session join. Callers invoke these members only after the database commit
/// of any change they report has returned, and never for a change whose transaction was rolled back.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Sends one <c>EntityChanged</c> message per event, in list order, to the hub group <c>all</c>.
    /// Each message carries the payload <c>{entityType, id, kind}</c> taken from
    /// <see cref="ChangeEvent.EntityType"/>, <see cref="ChangeEvent.Id"/> and <see cref="ChangeEvent.Kind"/>.
    /// An empty list sends nothing.
    /// </summary>
    /// <param name="events">The committed changes to announce, in the order they are sent.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when every message has been accepted for delivery.</returns>
    Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default);

    /// <summary>
    /// Sends one <c>CartItemDeleted</c> message with the payload <c>{itemId}</c> to the hub group
    /// <c>session:&lt;sessionId&gt;</c>. Callers send it after the deleted item has been removed from
    /// that session's cart.
    /// </summary>
    /// <param name="sessionId">Id of the session whose cart held the deleted item.</param>
    /// <param name="itemId">Internal row id of the deleted item.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when the message has been accepted for delivery.</returns>
    Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default);

    /// <summary>
    /// Sends one <c>SaveOutstandingChanges</c> message with the payload <c>{entityType, id}</c> to the hub
    /// group <c>session:&lt;sessionId&gt;</c> of the session that holds the row's soft edit lock.
    /// The message reports no database change.
    /// </summary>
    /// <param name="sessionId">Id of the session editing the row.</param>
    /// <param name="entityType">Domain type name of the row being edited, such as <c>Agent</c>.</param>
    /// <param name="id">Internal row id of the row being edited.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when the message has been accepted for delivery.</returns>
    Task PublishSaveOutstandingChangesAsync(string sessionId, string entityType, long id, CancellationToken ct = default);
}
