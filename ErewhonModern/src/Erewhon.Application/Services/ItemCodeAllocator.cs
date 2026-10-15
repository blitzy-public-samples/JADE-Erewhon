using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// Allocates the next item code number of a company: the highest code number among the company's stored items plus
/// <see cref="Increment"/>, or <see cref="StartingNumber"/> when the company has no stored items. Ports
/// <c>ItemTA.getNextCodeNumber</c>.
/// Each attempt creates the <see cref="LockSavepointName"/> savepoint, takes the session-level allocation lock
/// <c>Company.allItems:&lt;companyId&gt;</c> through <see cref="IEntityLocks.TryAcquireAllocationLockAsync"/>, reads
/// the highest stored code number and releases the lock before the attempt ends. When the read or the release fails,
/// the attempt rolls back to that savepoint and releases the lock, up to <see cref="ReleaseTries"/> times, before the
/// read's or the release's exception propagates. Nothing is written, and the lock is not held once the method returns
/// a number.
/// With no active transaction an attempt runs in its own transaction, committed after the read; inside a caller's
/// transaction it runs under the <see cref="SavepointName"/> savepoint, rolled back and released after the read; the
/// caller's transaction stays open and unchanged.
/// When the lock is not obtained, the attempt is rolled back and the next attempt starts after
/// <see cref="RetryDelayMilliseconds"/> milliseconds; when the <see cref="MaxAttempts"/>th attempt fails,
/// <see cref="ErewhonException"/> 500001 (<see cref="ErewhonException.LockNotObtained"/>) is thrown.
/// One instance serves one dependency-injection scope, together with the context and unit of work of that scope.
/// </summary>
/// <param name="db">The context whose items are read.</param>
/// <param name="uow">The unit of work that opens and closes each attempt's transaction or savepoint.</param>
/// <param name="locks">The lock adapter that takes and releases the allocation lock.</param>
public sealed class ItemCodeAllocator(IErewhonDbContext db, IUnitOfWork uow, IEntityLocks locks)
{
    /// <summary>The number of lock attempts made before <see cref="ErewhonException.LockNotObtained"/> is raised.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The wait, in milliseconds, after each failed lock attempt that is not the last.</summary>
    public const int RetryDelayMilliseconds = 100;

    /// <summary>The code number allocated when the company has no stored items.</summary>
    public const int StartingNumber = 1;

    /// <summary>The amount added to the company's highest stored code number.</summary>
    public const int Increment = 1;

    /// <summary>The savepoint an attempt runs under inside a caller's transaction.</summary>
    public const string SavepointName = "code_alloc";

    /// <summary>
    /// The savepoint created in the attempt's transaction or its <see cref="SavepointName"/> savepoint before the
    /// allocation lock is requested. A failure after the lock is obtained is rolled back to it before the lock is
    /// released; it is discarded when the attempt scope closes.
    /// </summary>
    public const string LockSavepointName = "code_alloc_lock";

    /// <summary>
    /// The number of times a failed attempt rolls back to <see cref="LockSavepointName"/> and releases the allocation
    /// lock before it gives up.
    /// </summary>
    private const int ReleaseTries = 2;

    private readonly IErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly IUnitOfWork _uow = uow ?? throw new ArgumentNullException(nameof(uow));

    private readonly IEntityLocks _locks = locks ?? throw new ArgumentNullException(nameof(locks));

    /// <summary>
    /// Returns the next code number of the company: its highest stored code number plus <see cref="Increment"/>, or
    /// <see cref="StartingNumber"/> when it has no stored items. Items added to the context but not yet saved are not
    /// counted. Any other exception raised by an attempt, such as a deadlock reported while waiting for the lock or a
    /// failure of the read after the lock is obtained, propagates after an obtained lock is released, in up to
    /// <see cref="ReleaseTries"/> tries, and the attempt's transaction or savepoint is rolled back; an exception raised
    /// by that release or that rollback is dropped.
    /// </summary>
    /// <param name="companyId">Row id of the company whose items are numbered.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The allocated code number.</returns>
    /// <exception cref="ErewhonException">
    /// The allocation lock was not obtained in <see cref="MaxAttempts"/> attempts; the error code is
    /// <see cref="ErewhonException.LockNotObtained"/> (500001).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<int> AllocateNextCodeNumberAsync(long companyId, CancellationToken ct = default)
    {
        var lockName = LockNames.CompanyAllItems(companyId);

        for (var attempt = 1; ; attempt++)
        {
            var ownsTransaction = await OpenAttemptScopeAsync(ct);

            int? allocated;
            try
            {
                allocated = await TryReadNextCodeNumberAsync(companyId, lockName, ct);
                if (allocated is not null)
                {
                    await CompleteAttemptScopeAsync(ownsTransaction);
                }
            }
            catch
            {
                await RunWhileHandlingFailureAsync(() => AbortAttemptScopeAsync(ownsTransaction));
                throw;
            }

            if (allocated is int number)
            {
                return number;
            }

            await AbortAttemptScopeAsync(ownsTransaction);

            if (attempt >= MaxAttempts)
            {
                throw new ErewhonException(ErewhonException.LockNotObtained);
            }

            await Task.Delay(RetryDelayMilliseconds, ct);
        }
    }

    /// <summary>
    /// Begins the attempt's own transaction when none is active, otherwise creates the <see cref="SavepointName"/>
    /// savepoint in the active transaction.
    /// </summary>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns><see langword="true"/> when the attempt began its own transaction; <see langword="false"/> for a savepoint.</returns>
    private async Task<bool> OpenAttemptScopeAsync(CancellationToken ct)
    {
        if (!_uow.HasActiveTransaction)
        {
            await _uow.BeginAsync(ct);
            return true;
        }

        await _uow.CreateSavepointAsync(SavepointName, ct);
        return false;
    }

    /// <summary>
    /// Creates the <see cref="LockSavepointName"/> savepoint, takes the allocation lock, reads the company's highest
    /// stored code number and releases the lock. When the read or the release fails, a <see langword="finally"/>
    /// block rolls back to that savepoint and releases the lock, up to <see cref="ReleaseTries"/> times; an exception
    /// raised there is dropped and the read's or the release's exception propagates. The savepoint creation and the
    /// lock wait observe <paramref name="ct"/>; the read, the rollbacks and the releases use
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    /// <param name="companyId">Row id of the company whose items are numbered.</param>
    /// <param name="lockName">The allocation lock name of the company.</param>
    /// <param name="ct">Token that cancels the savepoint creation and the wait for the lock.</param>
    /// <returns>The next code number, or <see langword="null"/> when the lock was not obtained.</returns>
    private async Task<int?> TryReadNextCodeNumberAsync(long companyId, string lockName, CancellationToken ct)
    {
        await _uow.CreateSavepointAsync(LockSavepointName, ct);

        if (!await _locks.TryAcquireAllocationLockAsync(lockName, ct))
        {
            return null;
        }

        int? highest;
        var released = false;
        try
        {
            highest = await _db.Items
                .Where(item => item.CompanyId == companyId)
                .MaxAsync(item => (int?)item.CodeNumber, CancellationToken.None);

            await _locks.ReleaseAllocationLockAsync(lockName, CancellationToken.None);
            released = true;
        }
        finally
        {
            if (!released)
            {
                await RollBackAndReleaseLockAsync(lockName);
            }
        }

        return highest is int last ? checked(last + Increment) : StartingNumber;
    }

    /// <summary>
    /// Rolls back to the <see cref="LockSavepointName"/> savepoint, then releases the allocation lock, both with
    /// <see cref="CancellationToken.None"/>. When either step raises an exception, both steps run again, up to
    /// <see cref="ReleaseTries"/> times in all. Returns after the first try whose two steps complete, or after the
    /// last failed try; no exception is raised.
    /// </summary>
    /// <param name="lockName">The allocation lock name of the company.</param>
    /// <returns>A task that completes when a try has completed or every try has failed.</returns>
    private async Task RollBackAndReleaseLockAsync(string lockName)
    {
        for (var tryNumber = 1; tryNumber <= ReleaseTries; tryNumber++)
        {
            try
            {
                await _uow.RollbackToSavepointAsync(LockSavepointName, CancellationToken.None);
                await _locks.ReleaseAllocationLockAsync(lockName, CancellationToken.None);
                return;
            }
            catch (Exception)
            {
                continue;
            }
        }
    }

    /// <summary>
    /// Ends a successful attempt: commits the attempt's own transaction, or rolls back to and releases the
    /// <see cref="SavepointName"/> savepoint, leaving the caller's transaction as it was before the attempt.
    /// Runs with <see cref="CancellationToken.None"/>.
    /// </summary>
    /// <param name="ownsTransaction"><see langword="true"/> when the attempt began its own transaction.</param>
    /// <returns>A task that completes when the attempt scope is closed.</returns>
    private async Task CompleteAttemptScopeAsync(bool ownsTransaction)
    {
        if (ownsTransaction)
        {
            await _uow.CommitAsync(CancellationToken.None);
            return;
        }

        await CloseSavepointAsync();
    }

    /// <summary>
    /// Ends a failed attempt: rolls back the attempt's own transaction, or rolls back to and releases the
    /// <see cref="SavepointName"/> savepoint, leaving the caller's transaction usable.
    /// Runs with <see cref="CancellationToken.None"/>.
    /// </summary>
    /// <param name="ownsTransaction"><see langword="true"/> when the attempt began its own transaction.</param>
    /// <returns>A task that completes when the attempt scope is closed.</returns>
    private async Task AbortAttemptScopeAsync(bool ownsTransaction)
    {
        if (ownsTransaction)
        {
            await _uow.RollbackAsync(CancellationToken.None);
            return;
        }

        await CloseSavepointAsync();
    }

    /// <summary>Rolls back to the <see cref="SavepointName"/> savepoint, then releases it.</summary>
    /// <returns>A task that completes when the savepoint is released.</returns>
    private async Task CloseSavepointAsync()
    {
        await _uow.RollbackToSavepointAsync(SavepointName, CancellationToken.None);
        await _uow.ReleaseSavepointAsync(SavepointName, CancellationToken.None);
    }

    /// <summary>
    /// Runs a step while an exception is being handled. An exception raised by the step itself is dropped; the
    /// exception being handled propagates.
    /// </summary>
    /// <param name="step">The step to run.</param>
    /// <returns>A task that completes when the step has completed or failed.</returns>
    private static async Task RunWhileHandlingFailureAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception)
        {
            return;
        }
    }
}
