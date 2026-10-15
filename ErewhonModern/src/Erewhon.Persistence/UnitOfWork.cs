using System.Data;
using Erewhon.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Erewhon.Persistence;

/// <summary>
/// Opens, commits and rolls back one PostgreSQL transaction on the connection of an <see cref="ErewhonDbContext"/>,
/// and creates, rolls back to and releases savepoints inside it. The connection stays open and bound to the
/// context from <see cref="BeginAsync"/> until the transaction is committed or rolled back.
/// Neither the change tracker nor any change notification is touched.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ErewhonDbContext _db;

    /// <summary>
    /// Creates a unit of work over the transaction of <paramref name="db"/>.
    /// </summary>
    /// <param name="db">The context whose connection carries the transaction.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is null.</exception>
    public UnitOfWork(ErewhonDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Gets a value indicating whether the context has a current transaction, whether it was begun through this
    /// unit of work or otherwise.
    /// </summary>
    public bool HasActiveTransaction => _db.Database.CurrentTransaction is not null;

    /// <summary>
    /// Begins a transaction at the Read Committed isolation level, opening the context's connection when it is
    /// closed.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the transaction is open.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already active on the context.</exception>
    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (HasActiveTransaction)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    }

    /// <summary>
    /// Commits the active transaction, then disposes it, so that no transaction is active afterwards.
    /// When the commit fails the transaction is still disposed, which rolls back any part of it the database has
    /// not ended, and the failure propagates.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the commit has returned and the transaction is disposed.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the context.</exception>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        var transaction = RequireActiveTransaction();

        try
        {
            await transaction.CommitAsync(ct);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Rolls back the active transaction, including one the database has left aborted after a failed statement,
    /// then disposes it, so that no transaction is active afterwards. Returns without doing anything when no
    /// transaction is active. When the rollback fails the transaction is still disposed and the failure propagates.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the rollback has returned and the transaction is disposed.</returns>
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        var transaction = _db.Database.CurrentTransaction;
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(ct);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a savepoint with the given name inside the active transaction.
    /// </summary>
    /// <param name="name">The savepoint name, for example <c>ta_locks</c> or <c>code_alloc</c>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the savepoint has been created.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">No transaction is active on the context.</exception>
    public async Task CreateSavepointAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await RequireActiveTransaction().CreateSavepointAsync(name, ct);
    }

    /// <summary>
    /// Rolls the active transaction back to the named savepoint, including when the database has left the
    /// transaction aborted after a failed statement. The writes, transaction-level locks and transaction-local
    /// settings made since the savepoint are undone, the savepoint itself remains, and the transaction stays open
    /// and usable.
    /// </summary>
    /// <param name="name">The name of a savepoint created by <see cref="CreateSavepointAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the rollback to the savepoint has returned.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">No transaction is active on the context.</exception>
    public async Task RollbackToSavepointAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await RequireActiveTransaction().RollbackToSavepointAsync(name, ct);
    }

    /// <summary>
    /// Releases the named savepoint, keeping the writes and locks made since it was created as part of the active
    /// transaction.
    /// </summary>
    /// <param name="name">The name of a savepoint created by <see cref="CreateSavepointAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the savepoint has been released.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">No transaction is active on the context.</exception>
    public async Task ReleaseSavepointAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await RequireActiveTransaction().ReleaseSavepointAsync(name, ct);
    }

    /// <summary>
    /// Returns the context's current transaction.
    /// </summary>
    /// <returns>The active transaction.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active on the context.</exception>
    private IDbContextTransaction RequireActiveTransaction()
    {
        return _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("No transaction is active.");
    }
}
