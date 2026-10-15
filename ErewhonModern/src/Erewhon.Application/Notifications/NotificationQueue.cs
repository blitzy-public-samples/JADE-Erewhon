using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;

namespace Erewhon.Application.Notifications;

/// <summary>
/// Queues the change events raised during a transaction and publishes them after the transaction commits.
/// Events are queued with <see cref="Enqueue(ChangeEvent)"/> while the transaction runs, and
/// <see cref="FlushAsync(CancellationToken)"/> is called only after the commit returns. On rollback the caller
/// calls <see cref="Clear"/> (Own-mode rollback of the whole transaction) or <see cref="DiscardSince(int)"/> with
/// the value <see cref="Mark"/> returned before the savepoint work (rollback to an Ambient-mode savepoint).
/// Nothing is published before the commit, and nothing queued in rolled-back work is published.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </summary>
/// <param name="cartStore">The session carts from which deleted items are removed.</param>
/// <param name="publisher">The publisher that sends the notifications to the connected clients.</param>
public sealed class NotificationQueue(ICartStore cartStore, INotificationPublisher publisher)
{
    private readonly ICartStore _cartStore = cartStore ?? throw new ArgumentNullException(nameof(cartStore));

    private readonly INotificationPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    private readonly List<ChangeEvent> _pending = new();

    private readonly HashSet<ChangeEvent> _keys = new();

    /// <summary>
    /// The queued events, in the order they were queued. The view is read-only and reflects later changes to the queue.
    /// </summary>
    public IReadOnlyList<ChangeEvent> Pending => _pending.AsReadOnly();

    /// <summary>
    /// Appends the event to the queue. An event equal to one already queued, by entity type, id, kind and
    /// info, is dropped without error.
    /// </summary>
    /// <param name="change">The change to queue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    public void Enqueue(ChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (_keys.Add(change))
        {
            _pending.Add(change);
        }
    }

    /// <summary>Returns the number of queued events, for a later <see cref="DiscardSince(int)"/>.</summary>
    /// <returns>The current number of queued events.</returns>
    public int Mark() => _pending.Count;

    /// <summary>
    /// Removes every event queued after the mark was taken, keeping the events queued before it. A removed event
    /// can be queued again. Does nothing when the queue holds no more events than <paramref name="mark"/>.
    /// </summary>
    /// <param name="mark">A value returned by <see cref="Mark"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mark"/> is negative.</exception>
    public void DiscardSince(int mark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mark);

        if (mark >= _pending.Count)
        {
            return;
        }

        for (var index = mark; index < _pending.Count; index++)
        {
            _keys.Remove(_pending[index]);
        }

        _pending.RemoveRange(mark, _pending.Count - mark);
    }

    /// <summary>Removes every queued event.</summary>
    public void Clear()
    {
        _pending.Clear();
        _keys.Clear();
    }

    /// <summary>
    /// Publishes the queued events and empties the queue. Returns without publishing when the queue is empty.
    /// Otherwise the queue is emptied before anything is published, whether or not publishing then succeeds, and:
    /// <list type="number">
    /// <item><description>Every <see cref="ChangeKind.Updated"/> event whose entity type and id also have a
    /// <see cref="ChangeKind.Deleted"/> event in the batch is dropped.</description></item>
    /// <item><description>For each <see cref="ChangeKind.Deleted"/> event of a <see cref="RetailItem"/> or
    /// <see cref="TenderItem"/>, in queue order, the item is removed from every session cart, then one
    /// <c>CartItemDeleted</c> notification is sent to each session whose cart held it.</description></item>
    /// <item><description>The remaining events are published in queue order as one batch.</description></item>
    /// </list>
    /// Exceptions from the cart store or the publisher propagate to the caller.
    /// </summary>
    /// <param name="ct">Cancels the publishing.</param>
    /// <returns>A task that completes when every notification has been handed to the publisher.</returns>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var batch = new List<ChangeEvent>(_pending);
        Clear();

        var deletedTargets = new HashSet<(string EntityType, long Id)>();
        foreach (var change in batch)
        {
            if (change.Kind == ChangeKind.Deleted)
            {
                deletedTargets.Add((change.EntityType, change.Id));
            }
        }

        var remaining = new List<ChangeEvent>(batch.Count);
        foreach (var change in batch)
        {
            if (change.Kind != ChangeKind.Updated || !deletedTargets.Contains((change.EntityType, change.Id)))
            {
                remaining.Add(change);
            }
        }

        foreach (var change in remaining)
        {
            if (change.Kind != ChangeKind.Deleted || !IsItemType(change.EntityType))
            {
                continue;
            }

            var sessionIds = _cartStore.RemoveItemFromAllCarts(change.Id);
            foreach (var sessionId in sessionIds)
            {
                await _publisher.PublishCartItemDeletedAsync(sessionId, change.Id, ct);
            }
        }

        if (remaining.Count > 0)
        {
            await _publisher.PublishEntityChangesAsync(remaining, ct);
        }
    }

    private static bool IsItemType(string entityType) =>
        string.Equals(entityType, nameof(RetailItem), StringComparison.Ordinal)
        || string.Equals(entityType, nameof(TenderItem), StringComparison.Ordinal);
}
