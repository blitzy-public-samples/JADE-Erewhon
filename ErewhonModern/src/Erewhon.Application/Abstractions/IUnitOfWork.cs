namespace Erewhon.Application.Abstractions;

/// <summary>
/// Opens, commits and rolls back one database transaction on the shared Erewhon database connection,
/// and creates, rolls back to and releases savepoints inside that transaction.
/// Committing or rolling back neither publishes nor discards change notifications; the caller does that
/// after <see cref="CommitAsync"/> returns or after a rollback.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Gets a value indicating whether a database transaction is currently open on the shared connection,
    /// whether it was begun through this unit of work or otherwise.
    /// </summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Begins a database transaction at the Read Committed isolation level.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the transaction is open.</returns>
    Task BeginAsync(CancellationToken ct = default);

    /// <summary>
    /// Commits the active transaction, which releases every transaction-level lock taken in it.
    /// Pending change notifications are not published.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the commit has returned.</returns>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back the active transaction, which discards its writes and releases every transaction-level
    /// advisory lock taken in it. Pending change notifications are not discarded.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the rollback has returned.</returns>
    Task RollbackAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a savepoint with the given name inside the active transaction. Requires an active transaction.
    /// The names passed are <c>ta_locks</c>, for the Ambient-mode transaction-agent lock and write step,
    /// <c>code_alloc</c>, for an item-code allocation attempt inside a caller's transaction, and
    /// <c>code_alloc_lock</c>, for the section of an allocation attempt from the lock request to the lock release.
    /// </summary>
    /// <param name="name">Savepoint name: <c>ta_locks</c>, <c>code_alloc</c> or <c>code_alloc_lock</c>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the savepoint exists.</returns>
    Task CreateSavepointAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Rolls the active transaction back to the named savepoint, discarding the writes and the
    /// transaction-level locks taken since it was created; the transaction stays open and usable.
    /// Requires an active transaction.
    /// </summary>
    /// <param name="name">Name of a savepoint created by <see cref="CreateSavepointAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the rollback to the savepoint has returned.</returns>
    Task RollbackToSavepointAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Releases the named savepoint, keeping the writes and locks taken since it was created as part of the
    /// active transaction. Requires an active transaction.
    /// </summary>
    /// <param name="name">Name of a savepoint created by <see cref="CreateSavepointAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the savepoint is released.</returns>
    Task ReleaseSavepointAsync(string name, CancellationToken ct = default);
}
