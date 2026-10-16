using Erewhon.Application.Abstractions;
using Erewhon.Application.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace Erewhon.Api.Realtime;

/// <summary>
/// Sends the notifications of <see cref="INotificationPublisher"/> to the clients connected to <see cref="ErewhonHub"/>.
/// Each member sends the messages it is given, as given, to one hub group and returns when the hub has accepted them.
/// </summary>
/// <param name="hub">The context of the hub the messages are sent through.</param>
public sealed class SignalRNotificationPublisher(IHubContext<ErewhonHub> hub) : INotificationPublisher
{
    /// <summary>
    /// Sends one <see cref="ErewhonHub.EntityChanged"/> message per event to the group <see cref="ErewhonHub.AllGroup"/>,
    /// in list order, each send awaited before the next begins. Each message carries an
    /// <see cref="EntityChangedMessage"/> built from <see cref="ChangeEvent.EntityType"/>, <see cref="ChangeEvent.Id"/>
    /// and <see cref="ChangeEvent.Kind"/>; <see cref="ChangeEvent.Info"/> is not sent. An empty list sends nothing.
    /// </summary>
    /// <param name="events">The committed changes to announce, in the order they are sent.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when every message has been handed to the hub.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    public async Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var all = hub.Clients.Group(ErewhonHub.AllGroup);
        foreach (var change in events)
        {
            await all.SendAsync(
                ErewhonHub.EntityChanged,
                new EntityChangedMessage(change.EntityType, change.Id, change.Kind),
                ct);
        }
    }

    /// <summary>
    /// Sends one <see cref="ErewhonHub.CartItemDeleted"/> message carrying a <see cref="CartItemDeletedMessage"/> with
    /// <paramref name="itemId"/> to the group <see cref="ErewhonHub.SessionGroup(string)"/> of
    /// <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">Id of the session whose cart held the deleted item.</param>
    /// <param name="itemId">Internal row id of the deleted item.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when the message has been handed to the hub.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        return hub.Clients.Group(ErewhonHub.SessionGroup(sessionId)).SendAsync(
            ErewhonHub.CartItemDeleted,
            new CartItemDeletedMessage(itemId),
            ct);
    }

    /// <summary>
    /// Sends one <see cref="ErewhonHub.SaveOutstandingChanges"/> message carrying a
    /// <see cref="SaveOutstandingChangesMessage"/> with <paramref name="entityType"/> and <paramref name="id"/> to the
    /// group <see cref="ErewhonHub.SessionGroup(string)"/> of <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">Id of the session editing the row.</param>
    /// <param name="entityType">Domain type name of the row being edited, such as <c>Agent</c>.</param>
    /// <param name="id">Internal row id of the row being edited.</param>
    /// <param name="ct">Cancels the sending.</param>
    /// <returns>A task that completes when the message has been handed to the hub.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> or <paramref name="entityType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="entityType"/> is empty.</exception>
    public Task PublishSaveOutstandingChangesAsync(
        string sessionId,
        string entityType,
        long id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(entityType);

        return hub.Clients.Group(ErewhonHub.SessionGroup(sessionId)).SendAsync(
            ErewhonHub.SaveOutstandingChanges,
            new SaveOutstandingChangesMessage(entityType, id),
            ct);
    }
}
