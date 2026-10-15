using System.Globalization;
using Erewhon.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Persistence;

/// <summary>
/// The lock waits applied by <see cref="PostgresEntityLocks"/>, in milliseconds.
/// </summary>
/// <param name="LockTimeoutMs">
/// The longest wait for a transaction-level advisory lock or an item row lock, from <c>Erewhon:Locks:TimeoutMs</c>.
/// </param>
/// <param name="AllocationTimeoutMs">
/// The longest wait for the session-level item-code allocation lock, from <c>Erewhon:Locks:AllocationTimeoutMs</c>.
/// </param>
public sealed record EntityLockSettings(int LockTimeoutMs = 10000, int AllocationTimeoutMs = 10000);

/// <summary>
/// Takes the named advisory locks and the item row locks of <see cref="IEntityLocks"/> on the connection of an
/// <see cref="ErewhonDbContext"/>, inside the transaction that context currently has open.
/// Each lock is keyed in the database as <c>hashtextextended(name, 0)</c> of the full lock name. Before each wait the
/// transaction's <c>lock_timeout</c> is set, as <c>SET LOCAL</c> does, to the matching value of
/// <see cref="EntityLockSettings"/>; the setting lasts until the transaction ends or is rolled back to a savepoint
/// created before it was set.
/// A wait that expires reports SQLSTATE <c>55P03</c> and a detected deadlock reports <c>40P01</c>; both leave the
/// transaction aborted for the caller to roll back.
/// One instance serves one dependency-injection scope, together with the context of that scope.
/// </summary>
/// <param name="db">The context whose connection and current transaction hold the locks.</param>
/// <param name="settings">The lock waits applied to each lock.</param>
/// <param name="mapper">The mapper that turns lock-timeout and deadlock failures into their outcome exceptions.</param>
public sealed class PostgresEntityLocks(ErewhonDbContext db, EntityLockSettings settings, PostgresErrorMapper mapper)
    : IEntityLocks
{
    private const string NoTransactionMessage = "An advisory lock requires an active transaction.";

    private readonly ErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly EntityLockSettings _settings = ValidateSettings(settings);

    private readonly PostgresErrorMapper _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));

    /// <summary>
    /// Sets the transaction's <c>lock_timeout</c> to <see cref="EntityLockSettings.LockTimeoutMs"/> and takes the
    /// exclusive transaction-level advisory lock <c>pg_advisory_xact_lock(hashtextextended(lockName, 0))</c>.
    /// </summary>
    /// <param name="lockName">The full lock name, for example <c>Company.allAgents:7</c>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock is held; <see langword="false"/> when the wait expired (SQLSTATE
    /// <c>55P03</c>), which leaves the transaction aborted.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="lockName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The context has no active transaction.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the lock.
    /// </exception>
    public async Task<bool> TryLockAsync(string lockName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockName);
        EnsureTransaction();

        await SetLockTimeoutAsync(_settings.LockTimeoutMs, ct);
        return await TryTakeAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", ct);
    }

    /// <summary>
    /// Sets the transaction's <c>lock_timeout</c> to <see cref="EntityLockSettings.AllocationTimeoutMs"/> and takes the
    /// exclusive session-level advisory lock <c>pg_advisory_lock(hashtextextended(lockName, 0))</c>, which the
    /// connection holds until <see cref="ReleaseAllocationLockAsync"/> releases it.
    /// </summary>
    /// <param name="lockName">The full lock name, for example <c>Company.allItems:7</c>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the lock is held; <see langword="false"/> when the wait expired (SQLSTATE
    /// <c>55P03</c>), which takes no lock and leaves the transaction aborted.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="lockName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The context has no active transaction.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the lock.
    /// </exception>
    public async Task<bool> TryAcquireAllocationLockAsync(string lockName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockName);
        EnsureTransaction();

        await SetLockTimeoutAsync(_settings.AllocationTimeoutMs, ct);
        return await TryTakeAsync($"SELECT pg_advisory_lock(hashtextextended({lockName}, 0))", ct);
    }

    /// <summary>
    /// Releases the session-level advisory lock <c>pg_advisory_unlock(hashtextextended(lockName, 0))</c> taken by
    /// <see cref="TryAcquireAllocationLockAsync"/> on the same connection. Releasing a lock the connection does not
    /// hold has no effect.
    /// </summary>
    /// <param name="lockName">The name passed to <see cref="TryAcquireAllocationLockAsync"/>.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the lock is released.</returns>
    /// <exception cref="ArgumentException"><paramref name="lockName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The context has no active transaction.</exception>
    public async Task ReleaseAllocationLockAsync(string lockName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockName);
        EnsureTransaction();

        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({lockName}, 0))", ct);
    }

    /// <summary>
    /// Sets the transaction's <c>lock_timeout</c> to <see cref="EntityLockSettings.LockTimeoutMs"/> and locks the
    /// <c>items</c> row with the given id with <c>SELECT id FROM items WHERE id = @id FOR UPDATE</c>. The row lock is
    /// held until the transaction ends. When no row has the id, nothing is locked.
    /// </summary>
    /// <param name="itemId">Row id of the item to lock.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the row lock is held.</returns>
    /// <exception cref="InvalidOperationException">The context has no active transaction.</exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonLockException">
    /// The row lock was not obtained within the timeout (SQLSTATE <c>55P03</c>).
    /// </exception>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The database reported a deadlock (SQLSTATE <c>40P01</c>) while waiting for the row lock.
    /// </exception>
    public async Task LockItemRowAsync(long itemId, CancellationToken ct = default)
    {
        EnsureTransaction();

        await SetLockTimeoutAsync(_settings.LockTimeoutMs, ct);
        try
        {
            await _db.Database.ExecuteSqlAsync($"SELECT id FROM items WHERE id = {itemId} FOR UPDATE", ct);
        }
        catch (Exception ex) when (PostgresErrorMapper.IsLockNotAvailable(ex) || PostgresErrorMapper.IsDeadlock(ex))
        {
            throw _mapper.Map(ex);
        }
    }

    /// <summary>
    /// Returns <paramref name="settings"/> when both of its waits are positive.
    /// </summary>
    /// <param name="settings">The lock waits to check.</param>
    /// <returns><paramref name="settings"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A wait is zero or negative.</exception>
    private static EntityLockSettings ValidateSettings(EntityLockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.LockTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.AllocationTimeoutMs);
        return settings;
    }

    /// <summary>
    /// Throws when the context has no active transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context has no active transaction.</exception>
    private void EnsureTransaction()
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(NoTransactionMessage);
        }
    }

    /// <summary>
    /// Sets <c>lock_timeout</c> to <paramref name="milliseconds"/> for the rest of the active transaction with
    /// <c>set_config('lock_timeout', '&lt;milliseconds&gt;ms', true)</c>.
    /// </summary>
    /// <param name="milliseconds">The longest lock wait, in milliseconds.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>A task that completes when the setting is applied.</returns>
    private async Task SetLockTimeoutAsync(int milliseconds, CancellationToken ct)
    {
        var value = milliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
        await _db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {value}, true)", ct);
    }

    /// <summary>
    /// Runs one advisory-lock statement and reports whether the lock was obtained.
    /// </summary>
    /// <param name="sql">The lock statement, with the lock name as a parameter.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the statement completed; <see langword="false"/> when it reported SQLSTATE
    /// <c>55P03</c>.
    /// </returns>
    /// <exception cref="Erewhon.Application.Common.ErewhonDeadlockException">
    /// The statement reported SQLSTATE <c>40P01</c>.
    /// </exception>
    private async Task<bool> TryTakeAsync(FormattableString sql, CancellationToken ct)
    {
        try
        {
            await _db.Database.ExecuteSqlAsync(sql, ct);
            return true;
        }
        catch (Exception ex) when (PostgresErrorMapper.IsLockNotAvailable(ex))
        {
            return false;
        }
        catch (Exception ex) when (PostgresErrorMapper.IsDeadlock(ex))
        {
            throw _mapper.Map(ex);
        }
    }
}
