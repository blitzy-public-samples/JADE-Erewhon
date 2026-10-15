using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Notifications;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erewhon.Application.Services;

/// <summary>
/// Closes the tenders of the bound company that are due at a date: each due tender item is awarded to its highest
/// tender through a tender sale. Ports JADE <c>Company.closeTendersAtDate</c> and
/// <c>TenderItem.acceptHighestTender</c>.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope. Instances are not safe to call from several threads at once.
/// </remarks>
/// <param name="companyContext">The company bound to the scope.</param>
/// <param name="db">The Erewhon database of the scope.</param>
/// <param name="uow">The unit of work that begins, commits and rolls back the transaction of each tender item.</param>
/// <param name="locks">The locks that take the row lock of each tender item.</param>
/// <param name="queue">The queue of change notifications raised by the awards.</param>
/// <param name="tenderSaleService">The service that persists the tender sale of each award.</param>
public sealed class TenderClosureService(
    ICompanyContext companyContext,
    IErewhonDbContext db,
    IUnitOfWork uow,
    IEntityLocks locks,
    NotificationQueue queue,
    TenderSaleService tenderSaleService)
{
    private readonly ICompanyContext _companyContext =
        companyContext ?? throw new ArgumentNullException(nameof(companyContext));

    private readonly IErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly IUnitOfWork _uow = uow ?? throw new ArgumentNullException(nameof(uow));

    private readonly IEntityLocks _locks = locks ?? throw new ArgumentNullException(nameof(locks));

    private readonly NotificationQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    private readonly TenderSaleService _tenderSaleService =
        tenderSaleService ?? throw new ArgumentNullException(nameof(tenderSaleService));

    /// <summary>
    /// Awards every unsold tender item of the bound company whose closure date is on or before
    /// <paramref name="date"/> to its highest tender, and returns the number of items that had a winning tender.
    /// The company's tender items are visited unsold first, then by closure date ascending, category prefix
    /// descending and code number descending. Each next item is the first one after the key the previous item had
    /// when it was reached; an item awarded during the run moves among the sold items and is not visited again.
    /// Each item is handled in its own transaction: the item row is locked, its sale and closure date are read again,
    /// and the award is committed before the queued notifications are published. The visit stops at the first item
    /// that is sold or whose closure date is after <paramref name="date"/>; an item whose closure date equals
    /// <paramref name="date"/> is awarded. An item without tenders stays unsold and is not counted. An item counts
    /// when it has a winning tender, whether or not its tender sale persisted. Ports <c>Company.closeTendersAtDate</c>.
    /// </summary>
    /// <param name="date">The date at or before which tender items close.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The number of tender items that had a winning tender.</returns>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">A transaction is already active when an item's transaction begins.</exception>
    /// <exception cref="ErewhonLockException">
    /// An item row lock was not obtained within <c>Erewhon:Locks:TimeoutMs</c>. The item's transaction is rolled back
    /// and its queued notifications discarded; the awards committed before it stay.
    /// </exception>
    /// <exception cref="ErewhonDeadlockException">
    /// The database reported a deadlock. The item's transaction is rolled back and its queued notifications
    /// discarded; the awards committed before it stay.
    /// </exception>
    public async Task<int> CloseTendersAtDateAsync(DateOnly date, CancellationToken ct = default)
    {
        Company company = await _companyContext.GetCurrentAsync(ct);

        int numClosed = 0;
        ClosurePosition? position = null;
        while (await ReadNextTenderItemAsync(company.Id, position, ct) is { } next)
        {
            ItemOutcome outcome = await CloseTenderItemAsync(next.ItemId, date, ct);
            if (outcome == ItemOutcome.Stopped)
            {
                break;
            }

            if (outcome == ItemOutcome.Awarded)
            {
                numClosed++;
            }

            position = next;
        }

        return numClosed;
    }

    /// <summary>
    /// Persists a tender sale that awards <paramref name="item"/> to its highest tender, and returns that tender.
    /// The highest tender is the one with the highest offer, the earliest placed among equal offers. The tender sale
    /// is persisted inside the caller's transaction, under a savepoint, with the winning tender, the item and the
    /// tender's client; its result is not reported, and a tender sale that fails leaves the caller's transaction
    /// usable. Ports <c>TenderItem.acceptHighestTender</c>.
    /// </summary>
    /// <param name="item">The tender item to award; called inside an active transaction.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The winning tender, tracked; <see langword="null"/> when the item has no tenders.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// No transaction is active and the tender sale reached its write.
    /// </exception>
    public async Task<Tender?> AcceptHighestTenderAsync(TenderItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        Tender? highestTender = await _db.Tenders
            .Where(t => t.TenderItemId == item.Id)
            .OrderByDescending(t => t.Offer)
            .ThenBy(t => t.DateTendered)
            .FirstOrDefaultAsync(ct);

        if (highestTender is null)
        {
            return null;
        }

        var command = new TenderSaleCommand
        {
            TenderId = highestTender.Id,
            ItemId = item.Id,
            ClientId = highestTender.ClientId,
        };

        await _tenderSaleService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient, ct);

        return highestTender;
    }

    /// <summary>
    /// Returns the key of the company's tender item that follows <paramref name="after"/> in the closure order
    /// (unsold first, then closure date ascending, category prefix descending and code number descending), as the
    /// items stand now; the first item when <paramref name="after"/> is <see langword="null"/>, and
    /// <see langword="null"/> when no item follows.
    /// </summary>
    private async Task<ClosurePosition?> ReadNextTenderItemAsync(
        long companyId,
        ClosurePosition? after,
        CancellationToken ct)
    {
        if (after is { IsSold: true })
        {
            return await ReadFirstInGroupAsync(companyId, sold: true, after, ct);
        }

        return await ReadFirstInGroupAsync(companyId, sold: false, after, ct)
            ?? await ReadFirstInGroupAsync(companyId, sold: true, null, ct);
    }

    /// <summary>
    /// Returns the key of the first unsold or sold tender item of the company by closure date ascending, category
    /// prefix descending and code number descending, after <paramref name="after"/> when one is given;
    /// <see langword="null"/> when there is none.
    /// </summary>
    private Task<ClosurePosition?> ReadFirstInGroupAsync(
        long companyId,
        bool sold,
        ClosurePosition? after,
        CancellationToken ct)
    {
        IQueryable<TenderItem> items = _db.Items
            .OfType<TenderItem>()
            .Where(i => i.CompanyId == companyId);

        items = sold ? items.Where(i => i.Sale != null) : items.Where(i => i.Sale == null);

        if (after is not null)
        {
            items = items.Where(i =>
                i.ClosureDate > after.ClosureDate
                || (i.ClosureDate == after.ClosureDate
                    && (string.Compare(i.ItemCategory!.Prefix, after.Prefix) < 0
                        || (i.ItemCategory!.Prefix == after.Prefix && i.CodeNumber < after.CodeNumber))));
        }

        return items
            .OrderBy(i => i.ClosureDate)
            .ThenByDescending(i => i.ItemCategory!.Prefix)
            .ThenByDescending(i => i.CodeNumber)
            .Select(i => new ClosurePosition(i.Id, sold, i.ClosureDate, i.ItemCategory!.Prefix, i.CodeNumber))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Handles one tender item in its own transaction: locks its row, reads its sale and closure date again, then
    /// stops on a sold item or a closure date after <paramref name="date"/>, and otherwise awards the item, commits
    /// and publishes the queued notifications. An exception rolls the transaction back, discards the queued
    /// notifications, detaches the entities first tracked while handling the item, and propagates.
    /// </summary>
    private async Task<ItemOutcome> CloseTenderItemAsync(long itemId, DateOnly date, CancellationToken ct)
    {
        HashSet<object> trackedBefore = TrackedEntities();
        ItemOutcome outcome;

        await _uow.BeginAsync(ct);
        try
        {
            await _locks.LockItemRowAsync(itemId, ct);

            var current = await _db.Items
                .OfType<TenderItem>()
                .Where(i => i.Id == itemId)
                .Select(i => new { i.ClosureDate, IsSold = i.Sale != null })
                .FirstOrDefaultAsync(ct);

            // A tender item deleted after its key was read is no longer one of the company's tender items and is passed over.
            if (current is null)
            {
                await _uow.RollbackAsync(CancellationToken.None);
                return ItemOutcome.PassedOver;
            }

            if (current.IsSold || current.ClosureDate > date)
            {
                await _uow.RollbackAsync(CancellationToken.None);
                return ItemOutcome.Stopped;
            }

            TenderItem item = await _db.Items
                .OfType<TenderItem>()
                .FirstAsync(i => i.Id == itemId, ct);

            outcome = await AcceptHighestTenderAsync(item, ct) is null ? ItemOutcome.NotAwarded : ItemOutcome.Awarded;

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await RollBackWhileHandlingFailureAsync();
            _queue.Clear();
            DetachEntitiesTrackedSince(trackedBefore);
            throw;
        }

        await _queue.FlushAsync(CancellationToken.None);
        return outcome;
    }

    /// <summary>Returns every entity the change tracker holds.</summary>
    private HashSet<object> TrackedEntities()
    {
        var tracked = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (EntityEntry entry in _db.ChangeTracker.Entries())
        {
            tracked.Add(entry.Entity);
        }

        return tracked;
    }

    /// <summary>Detaches every tracked entity that is not in <paramref name="trackedBefore"/>.</summary>
    private void DetachEntitiesTrackedSince(HashSet<object> trackedBefore)
    {
        foreach (EntityEntry entry in _db.ChangeTracker.Entries().ToList())
        {
            if (!trackedBefore.Contains(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Rolls back the active transaction while an exception is being handled; an exception raised by the rollback is
    /// dropped and the exception being handled propagates.
    /// </summary>
    private async Task RollBackWhileHandlingFailureAsync()
    {
        try
        {
            await _uow.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return;
        }
    }

    /// <summary>The closure-order key of one tender item, as read when the item was reached.</summary>
    /// <param name="ItemId">The row id of the tender item.</param>
    /// <param name="IsSold">Whether the item had a sale.</param>
    /// <param name="ClosureDate">The item's closure date.</param>
    /// <param name="Prefix">The prefix of the item's category.</param>
    /// <param name="CodeNumber">The item's code number.</param>
    private sealed record ClosurePosition(long ItemId, bool IsSold, DateOnly ClosureDate, string Prefix, int CodeNumber);

    /// <summary>The result of handling one tender item.</summary>
    private enum ItemOutcome
    {
        /// <summary>The item had a winning tender.</summary>
        Awarded,

        /// <summary>The item had no tenders and stays unsold.</summary>
        NotAwarded,

        /// <summary>The item no longer exists.</summary>
        PassedOver,

        /// <summary>The item is sold or closes after the date, which ends the closure.</summary>
        Stopped,
    }
}
