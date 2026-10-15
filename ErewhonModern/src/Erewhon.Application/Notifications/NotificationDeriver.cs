using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erewhon.Application.Notifications;

/// <summary>
/// Derives the change events of a transaction and adds them to the <see cref="NotificationQueue"/>: one event per
/// persistent row a save adds, modifies or deletes, an <see cref="ChangeKind.Updated"/> event for the owner of each
/// changed address, <see cref="ChangeKind.Updated"/> events for the item and the tender of each added or deleted sale,
/// and the events of the rows the database deletes or changes itself, which callers report.
/// It only enqueues; it never flushes, clears or publishes the queue.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </summary>
/// <param name="queue">The queue the derived events are added to.</param>
public sealed class NotificationDeriver(NotificationQueue queue)
{
    private const string DomainNamespace = "Erewhon.Application.Domain";

    private readonly NotificationQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    private List<CapturedRow>? _pendingCapture;

    /// <summary>
    /// Captures the rows of a save that is about to run and makes them the pending capture, replacing any capture
    /// still pending. Nothing is enqueued until <see cref="CompleteSavedChanges"/> is called. Captured are:
    /// <list type="bullet">
    /// <item><description>every <see cref="PersistentModel"/> entry in state <see cref="EntityState.Added"/>,
    /// <see cref="EntityState.Modified"/> or <see cref="EntityState.Deleted"/>, with, for an added or deleted
    /// <see cref="Sale"/>, the type name of its item and, for a deleted sale, its item id and tender id;</description></item>
    /// <item><description>an <see cref="ChangeKind.Updated"/> event for every <see cref="PersistentModel"/> entry in state
    /// <see cref="EntityState.Unchanged"/> or <see cref="EntityState.Modified"/> that references an owned entry
    /// (its <see cref="Address"/>) in state <see cref="EntityState.Added"/>, <see cref="EntityState.Modified"/> or
    /// <see cref="EntityState.Deleted"/>.</description></item>
    /// </list>
    /// Every other entry is ignored, including <see cref="AgentCommissionRate"/> rows and the owned entries themselves.
    /// </summary>
    /// <param name="entries">
    /// The tracked entries of the context being saved. They must reflect detected changes: pass
    /// <c>ChangeTracker.Entries()</c> after <c>ChangeTracker.DetectChanges()</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    public void RecordSavingChanges(IEnumerable<EntityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _pendingCapture = null;
        var snapshot = entries.ToList();
        var capture = new List<CapturedRow>(snapshot.Count);

        foreach (var entry in snapshot)
        {
            if (entry.Entity is not PersistentModel entity)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    capture.Add(CaptureRow(entity, ChangeKind.Added));
                    break;
                case EntityState.Modified:
                    capture.Add(CaptureRow(entity, ChangeKind.Updated));
                    break;
                case EntityState.Deleted:
                    capture.Add(CaptureRow(entity, ChangeKind.Deleted));
                    break;
            }

            if (entry.State is EntityState.Unchanged or EntityState.Modified && HasChangedOwnedReference(entry))
            {
                capture.Add(new CapturedRow(entity, ChangeKind.Updated, IsOwnerUpdate: true, SaleItemType: null, CapturedItemId: 0, CapturedTenderId: null));
            }
        }

        _pendingCapture = capture;
    }

    /// <summary>
    /// Enqueues the events of the pending capture and drops it; does nothing when no capture is pending.
    /// First, in capture order, one event per captured row: <see cref="ChangeKind.Added"/>,
    /// <see cref="ChangeKind.Updated"/> or <see cref="ChangeKind.Deleted"/> with the row's domain type name and its
    /// current id, so an added row carries its database-assigned id; owner updates are enqueued here as
    /// <see cref="ChangeKind.Updated"/>. Then, for each added or deleted <see cref="Sale"/>, an
    /// <see cref="ChangeKind.Updated"/> event for its item and, for a <see cref="TenderSale"/> with a tender, an
    /// <see cref="ChangeKind.Updated"/> event for that <see cref="Tender"/>. An added sale's item and tender ids are read
    /// now; a deleted sale's are the captured ones.
    /// </summary>
    public void CompleteSavedChanges()
    {
        var capture = _pendingCapture;
        if (capture is null)
        {
            return;
        }

        _pendingCapture = null;

        foreach (var row in capture)
        {
            _queue.Enqueue(new ChangeEvent(EntityTypeName(row.Entity), row.Entity.Id, row.Kind));
        }

        foreach (var row in capture)
        {
            if (row.IsOwnerUpdate || row.Entity is not Sale sale)
            {
                continue;
            }

            switch (row.Kind)
            {
                case ChangeKind.Added:
                    EnqueueRelationshipSides(row.SaleItemType ?? SaleItemTypeName(sale), sale.ItemId, (sale as TenderSale)?.TenderId);
                    break;
                case ChangeKind.Deleted:
                    EnqueueRelationshipSides(row.SaleItemType ?? SaleItemTypeName(sale), row.CapturedItemId, row.CapturedTenderId);
                    break;
            }
        }
    }

    /// <summary>Drops the pending capture without enqueuing anything; the queue is left unchanged.</summary>
    public void DiscardPendingCapture() => _pendingCapture = null;

    /// <summary>
    /// Enqueues, at once and in the given order, a <see cref="ChangeKind.Deleted"/> event for each row the database
    /// deletes with its parent; then, for each of those rows that is a <see cref="Sale"/>, an
    /// <see cref="ChangeKind.Updated"/> event for its item (<see cref="Sale.ItemId"/>) and, for a
    /// <see cref="TenderSale"/> with a tender, an <see cref="ChangeKind.Updated"/> event for that
    /// <see cref="Tender"/> (<see cref="TenderSale.TenderId"/>).
    /// </summary>
    /// <param name="rows">
    /// The descendants the delete removes, read in the same transaction before the delete is saved; a sale's
    /// <see cref="Sale.Item"/> is loaded.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="rows"/> contains a null element; nothing is enqueued.</exception>
    public void RecordCascadeDeleted(IEnumerable<PersistentModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var deleted = Materialise(rows, nameof(rows));

        foreach (var row in deleted)
        {
            _queue.Enqueue(new ChangeEvent(EntityTypeName(row), row.Id, ChangeKind.Deleted));
        }

        foreach (var row in deleted)
        {
            if (row is Sale sale)
            {
                EnqueueRelationshipSides(SaleItemTypeName(sale), sale.ItemId, (sale as TenderSale)?.TenderId);
            }
        }
    }

    /// <summary>
    /// Enqueues, at once and in the given order, an <see cref="ChangeKind.Updated"/> event for each item whose agent or
    /// region reference the database clears when that agent or region is deleted.
    /// </summary>
    /// <param name="items">The items whose agent or region reference the delete clears.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> contains a null element; nothing is enqueued.</exception>
    public void RecordPeerCleared(IEnumerable<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in Materialise(items, nameof(items)))
        {
            _queue.Enqueue(new ChangeEvent(EntityTypeName(item), item.Id, ChangeKind.Updated));
        }
    }

    private void EnqueueRelationshipSides(string itemTypeName, long itemId, long? tenderId)
    {
        _queue.Enqueue(new ChangeEvent(itemTypeName, itemId, ChangeKind.Updated));

        if (tenderId is long id && id != 0)
        {
            _queue.Enqueue(new ChangeEvent(nameof(Tender), id, ChangeKind.Updated));
        }
    }

    private static CapturedRow CaptureRow(PersistentModel entity, ChangeKind kind)
    {
        if (entity is Sale sale && kind is ChangeKind.Added or ChangeKind.Deleted)
        {
            var isDeleted = kind == ChangeKind.Deleted;
            return new CapturedRow(
                entity,
                kind,
                IsOwnerUpdate: false,
                SaleItemType: SaleItemTypeName(sale),
                CapturedItemId: isDeleted ? sale.ItemId : 0,
                CapturedTenderId: isDeleted ? (sale as TenderSale)?.TenderId : null);
        }

        return new CapturedRow(entity, kind, IsOwnerUpdate: false, SaleItemType: null, CapturedItemId: 0, CapturedTenderId: null);
    }

    private static bool HasChangedOwnedReference(EntityEntry entry)
    {
        foreach (var reference in entry.References)
        {
            var target = reference.TargetEntry;
            if (target is not null
                && target.Metadata.IsOwned()
                && target.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                return true;
            }
        }

        return false;
    }

    private static List<T> Materialise<T>(IEnumerable<T> source, string paramName)
        where T : class
    {
        var list = source.ToList();
        if (list.Exists(element => element is null))
        {
            throw new ArgumentException("The sequence contains a null element.", paramName);
        }

        return list;
    }

    private static string EntityTypeName(object entity)
    {
        for (var type = entity.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.Namespace, DomainNamespace, StringComparison.Ordinal))
            {
                return type.Name;
            }
        }

        throw new ArgumentException($"{entity.GetType().FullName} is not a type of {DomainNamespace}.", nameof(entity));
    }

    private static string SaleItemTypeName(Sale sale) =>
        sale.Item is { } item
            ? EntityTypeName(item)
            : sale is TenderSale ? nameof(TenderItem) : nameof(RetailItem);

    private readonly record struct CapturedRow(
        PersistentModel Entity,
        ChangeKind Kind,
        bool IsOwnerUpdate,
        string? SaleItemType,
        long CapturedItemId,
        long? CapturedTenderId);
}
