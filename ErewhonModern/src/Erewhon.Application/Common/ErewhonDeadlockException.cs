namespace Erewhon.Application.Common;

/// <summary>
/// A database statement was chosen as the victim of a detected deadlock and its transaction was aborted.
/// </summary>
public sealed class ErewhonDeadlockException : Exception
{
    /// <summary>The SQLSTATE PostgreSQL reports for a detected deadlock.</summary>
    public const string DeadlockDetectedSqlState = "40P01";

    private const string DefaultText = "Deadlock detected";

    /// <summary>
    /// Creates the exception for the given SQLSTATE, carrying the provider exception that reported the deadlock.
    /// </summary>
    /// <param name="sqlState">The SQLSTATE reported by the database.</param>
    /// <param name="innerException">The provider exception, whose message becomes this exception's message; or <see langword="null"/>.</param>
    public ErewhonDeadlockException(string sqlState, Exception? innerException)
        : base(innerException?.Message ?? DefaultText, innerException)
    {
        SqlState = sqlState;
    }

    /// <summary>
    /// Creates the exception for <see cref="DeadlockDetectedSqlState"/>, carrying the provider exception that reported the deadlock.
    /// </summary>
    /// <param name="innerException">The provider exception, whose message becomes this exception's message; or <see langword="null"/>.</param>
    public ErewhonDeadlockException(Exception? innerException)
        : this(DeadlockDetectedSqlState, innerException)
    {
    }

    /// <summary>The SQLSTATE reported by the database for the deadlock.</summary>
    public string SqlState { get; }
}
