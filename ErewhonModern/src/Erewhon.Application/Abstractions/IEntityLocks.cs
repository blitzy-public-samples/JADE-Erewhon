namespace Erewhon.Application.Abstractions;

/// <summary>
/// Takes the named advisory locks and the item row locks that transaction agents, item-code allocation and
/// tender closure hold on the shared Erewhon database connection.
/// Every member requires an active transaction on that connection (<see cref="IUnitOfWork.HasActiveTransaction"/>)
/// and throws <see cref="InvalidOperationException"/> when none is open.
/// Lock names have the form <c>&lt;Owner&gt;.&lt;collection&gt;:&lt;ownerId&gt;</c>, as built by
/// <see cref="Erewhon.Application.Common.LockNames"/> (for example <c>Company.allItems:7</c>), and are keyed in the
/// database as <c>hashtextextended(name, 0)</c>.
/// </summary>
public interface IEntityLocks
{
    /// <summary>
    /// Tries to take the exclusive transaction-level advisory lock with the given name, waiting at most
    /// <c>Erewhon:Locks:TimeoutMs</c> milliseconds (default 10000) for it. An obtained lock is held until the
    /// active transaction commits or rolls back, or until a rollback to a savepoint created before it was taken.
    /// When the wait expires (SQLSTATE <c>55P03</c>) no lock is taken and the transaction is left aborted: the
    /// caller rolls it back with <see cref="IUnitOfWork.RollbackAsync"/>, or rolls back to the <c>ta_locks</c>
    /// savepoint with <see cref="IUnitOfWork.RollbackToSavepointAsync"/>.
    /// </summary>
    /// <param name="lockName">Lock name from <see cref="Erewhon.Application.Common.LockNames"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock is held; <see langword="false"/> when it was not obtained within the
    /// timeout.
    /// </returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the shared connection.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the lock.
    /// </exception>
    Task<bool> TryLockAsync(string lockName, CancellationToken ct = default);

    /// <summary>
    /// Tries to take the exclusive session-level advisory lock with the given name for one item-code allocation
    /// attempt, waiting at most <c>Erewhon:Locks:AllocationTimeoutMs</c> milliseconds (default 10000) for it.
    /// An obtained lock is held by the connection until <see cref="ReleaseAllocationLockAsync"/> releases it;
    /// committing or rolling back the transaction does not release it.
    /// When the wait expires (SQLSTATE <c>55P03</c>) no lock is taken and the attempt's transaction, or its
    /// <c>code_alloc</c> savepoint, is left aborted for the caller to roll back.
    /// </summary>
    /// <param name="lockName">Lock name from <see cref="Erewhon.Application.Common.LockNames"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock is held; <see langword="false"/> when it was not obtained within the
    /// timeout.
    /// </returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the shared connection.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the lock.
    /// </exception>
    Task<bool> TryAcquireAllocationLockAsync(string lockName, CancellationToken ct = default);

    /// <summary>
    /// Releases the session-level advisory lock with the given name that
    /// <see cref="TryAcquireAllocationLockAsync"/> obtained on the same connection. The caller releases it after
    /// reading the next code number and before its transaction or savepoint ends.
    /// Releasing a lock that is not held has no effect.
    /// </summary>
    /// <param name="lockName">The name passed to <see cref="TryAcquireAllocationLockAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the lock is released.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the shared connection.</exception>
    Task ReleaseAllocationLockAsync(string lockName, CancellationToken ct = default);

    /// <summary>
    /// Takes an exclusive row lock on the <c>items</c> row with the given id (<c>SELECT ... FOR UPDATE</c>),
    /// waiting at most <c>Erewhon:Locks:TimeoutMs</c> milliseconds (default 10000) for it. The row lock is held
    /// until the active transaction commits or rolls back.
    /// </summary>
    /// <param name="itemId">Row id of the item to lock.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the row lock is held.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the shared connection.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonLockException">
    /// The row lock was not obtained within the timeout (SQLSTATE <c>55P03</c>).
    /// </exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the row lock.
    /// </exception>
    Task LockItemRowAsync(long itemId, CancellationToken ct = default);
}
