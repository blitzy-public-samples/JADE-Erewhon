namespace Erewhon.Application.Common;

/// <summary>
/// A lock could not be obtained within the configured lock timeout outside a transaction agent's lock step.
/// </summary>
public sealed class ErewhonLockException : Exception
{
    /// <summary>The PostgreSQL SQLSTATE for "lock not available".</summary>
    public const string LockNotAvailableSqlState = "55P03";

    private const string DefaultText = "Lock not obtained";

    /// <summary>
    /// Creates the exception for the given SQLSTATE, taking its message from <paramref name="innerException"/>
    /// when one is supplied.
    /// </summary>
    /// <param name="sqlState">The SQLSTATE reported by the database.</param>
    /// <param name="innerException">The provider exception that reported the lock failure, if any.</param>
    public ErewhonLockException(string sqlState, Exception? innerException)
        : base(innerException?.Message ?? DefaultText, innerException)
    {
        SqlState = sqlState;
    }

    /// <summary>
    /// Creates the exception with the SQLSTATE <see cref="LockNotAvailableSqlState"/>.
    /// </summary>
    /// <param name="innerException">The provider exception that reported the lock failure, if any.</param>
    public ErewhonLockException(Exception? innerException)
        : this(LockNotAvailableSqlState, innerException)
    {
    }

    /// <summary>The SQLSTATE reported by the database.</summary>
    public string SqlState { get; }
}
