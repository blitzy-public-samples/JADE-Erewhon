using System.Linq.Expressions;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erewhon.Application.Notifications;

/// <summary>
/// Derives the change events of a transaction and adds them to the <see cref="NotificationQueue"/>: one event per
/// persistent row a save adds, modifies or deletes, an <see cref="ChangeKind.Updated"/> event for the owner of each
/// changed address, <see cref="ChangeKind.Updated"/> events for the item and the tender of each added or deleted sale,
/// and the events of the rows the database deletes or changes itself, which callers report as
/// <see cref="CascadeRow"/> values.
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
    /// Enqueues, at once and in the given order, a <see cref="ChangeKind.Deleted"/> event with the row's
    /// <see cref="CascadeRow.EntityType"/> and <see cref="CascadeRow.Id"/> for each row the database deletes with its
    /// parent; then, for each of those rows that has a <see cref="CascadeRow.SaleItemType"/>, an
    /// <see cref="ChangeKind.Updated"/> event for its item (<see cref="CascadeRow.SaleItemType"/>,
    /// <see cref="CascadeRow.SaleItemId"/>) and, when <see cref="CascadeRow.SaleTenderId"/> is neither null nor 0, an
    /// <see cref="ChangeKind.Updated"/> event for that <see cref="Tender"/>.
    /// </summary>
    /// <param name="rows">
    /// The descendants the delete removes, read in the same transaction before the delete is saved; a sale's row
    /// carries its item's type name and id and, for a tender sale, its tender id.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> contains a null element, a row whose <see cref="CascadeRow.EntityType"/> is null or
    /// white space, or a row whose <see cref="CascadeRow.SaleItemType"/> is empty or white space; nothing is enqueued.
    /// </exception>
    public void RecordCascadeDeleted(IEnumerable<CascadeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var deleted = Materialise(rows, nameof(rows));

        foreach (var row in deleted)
        {
            _queue.Enqueue(new ChangeEvent(row.EntityType, row.Id, ChangeKind.Deleted));
        }

        foreach (var row in deleted)
        {
            if (row.SaleItemType is { } saleItemType)
            {
                EnqueueRelationshipSides(saleItemType, row.SaleItemId, row.SaleTenderId);
            }
        }
    }

    /// <summary>
    /// Enqueues, at once and in the given order, an <see cref="ChangeKind.Updated"/> event with the row's
    /// <see cref="CascadeRow.EntityType"/> and <see cref="CascadeRow.Id"/> for each row whose reference the database
    /// clears when the referenced row is deleted: an item's agent or region, a tender sale's tender.
    /// </summary>
    /// <param name="items">
    /// The rows whose reference the delete clears: items whose agent or region it deletes and tender sales whose tender
    /// it deletes.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> contains a null element, a row whose <see cref="CascadeRow.EntityType"/> is null or
    /// white space, or a row whose <see cref="CascadeRow.SaleItemType"/> is empty or white space; nothing is enqueued.
    /// </exception>
    public void RecordPeerCleared(IEnumerable<CascadeRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in Materialise(items, nameof(items)))
        {
            _queue.Enqueue(new ChangeEvent(item.EntityType, item.Id, ChangeKind.Updated));
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

    private static List<CascadeRow> Materialise(IEnumerable<CascadeRow> source, string paramName)
    {
        var list = source.ToList();
        if (list.Exists(element => element is null))
        {
            throw new ArgumentException("The sequence contains a null element.", paramName);
        }

        if (list.Exists(element => string.IsNullOrWhiteSpace(element.EntityType)))
        {
            throw new ArgumentException("The sequence contains a row without an entity type name.", paramName);
        }

        if (list.Exists(element => element.SaleItemType is { } saleItemType && string.IsNullOrWhiteSpace(saleItemType)))
        {
            throw new ArgumentException("The sequence contains a row with an empty sale item type name.", paramName);
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

/// <summary>
/// A row the database deletes or changes itself, reported to <see cref="NotificationDeriver.RecordCascadeDeleted"/>
/// or <see cref="NotificationDeriver.RecordPeerCleared"/> by its domain type name and id and, for a deleted
/// <see cref="Sale"/>, its item's type name and id and its tender id.
/// </summary>
/// <param name="EntityType">
/// The domain type name of the row, as carried by <see cref="ChangeEvent.EntityType"/>: for example <c>Agent</c>,
/// <c>RetailItem</c>, <c>TenderItem</c>, <c>RetailSale</c>, <c>TenderSale</c> or <c>Tender</c>.
/// </param>
/// <param name="Id">The row id.</param>
/// <param name="SaleItemType">
/// For a deleted sale, the domain type name of its item (<c>RetailItem</c> or <c>TenderItem</c>);
/// <see langword="null"/> for every other row.
/// </param>
/// <param name="SaleItemId">
/// For a deleted sale, the row id of its item (<see cref="Sale.ItemId"/>); 0 for every other row.
/// </param>
/// <param name="SaleTenderId">
/// For a deleted <see cref="TenderSale"/>, its <see cref="TenderSale.TenderId"/>; <see langword="null"/> for every other
/// row.
/// </param>
public sealed record CascadeRow(string EntityType, long Id, string? SaleItemType, long SaleItemId, long? SaleTenderId)
{
    /// <summary>Creates a row without sale details: no sale item type, sale item id 0 and no sale tender id.</summary>
    /// <param name="entityType">The domain type name of the row.</param>
    /// <param name="id">The row id.</param>
    public CascadeRow(string entityType, long id)
        : this(entityType, id, null, 0, null)
    {
    }

    /// <summary>
    /// Projects an item to its row: type name <c>TenderItem</c> for a <see cref="TenderItem"/> and <c>RetailItem</c>
    /// otherwise, and the item's id. EF Core translates the projection to SQL over the item type discriminator.
    /// </summary>
    public static Expression<Func<Item, CascadeRow>> FromItem { get; } =
        item => new CascadeRow(item is TenderItem ? nameof(TenderItem) : nameof(RetailItem), item.Id);

    /// <summary>
    /// Projects a sale to its row: type name <c>TenderSale</c> for a <see cref="TenderSale"/> and <c>RetailSale</c>
    /// otherwise; the sale's id; sale item type <c>TenderItem</c> when <see cref="Sale.Item"/> is a
    /// <see cref="TenderItem"/> and <c>RetailItem</c> otherwise; <see cref="Sale.ItemId"/>; and, for a tender sale,
    /// <see cref="TenderSale.TenderId"/>. EF Core translates the projection to SQL over the sale and item type
    /// discriminators, joining each sale to its item.
    /// </summary>
    public static Expression<Func<Sale, CascadeRow>> FromSale { get; } =
        sale => new CascadeRow(
            sale is TenderSale ? nameof(TenderSale) : nameof(RetailSale),
            sale.Id,
            sale.Item is TenderItem ? nameof(TenderItem) : nameof(RetailItem),
            sale.ItemId,
            sale is TenderSale ? ((TenderSale)sale).TenderId : null);
}
