using Erewhon.Application.Common;
using Npgsql;

namespace Erewhon.Persistence;

/// <summary>
/// A database-level failure other than a lock timeout or a deadlock, identified by its SQLSTATE and the database's message.
/// </summary>
public sealed class DatabaseFailureException : Exception
{
    /// <summary>
    /// Creates the exception for a SQLSTATE and database message, carrying the exception that reported the failure.
    /// </summary>
    /// <param name="sqlState">The SQLSTATE of the failure, e.g. <c>23505</c>.</param>
    /// <param name="databaseMessage">The database's message for the failure; it becomes this exception's message.</param>
    /// <param name="innerException">The exception that reported the failure, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="sqlState"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="databaseMessage"/> is null.</exception>
    public DatabaseFailureException(string sqlState, string databaseMessage, Exception? innerException)
        : base(databaseMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(sqlState);
        ArgumentNullException.ThrowIfNull(databaseMessage);
        SqlState = sqlState;
        DatabaseMessage = databaseMessage;
    }

    /// <summary>The SQLSTATE of the failure.</summary>
    public string SqlState { get; }

    /// <summary>
    /// The PostgreSQL primary message text, or the provider exception's message when the connection failed.
    /// </summary>
    public string DatabaseMessage { get; }
}

/// <summary>
/// Classifies PostgreSQL provider exceptions by SQLSTATE into <see cref="ErewhonLockException"/>,
/// <see cref="ErewhonDeadlockException"/> and <see cref="DatabaseFailureException"/>.
/// </summary>
public sealed class PostgresErrorMapper
{
    /// <summary>The SQLSTATE for "lock not available".</summary>
    public const string LockNotAvailable = ErewhonLockException.LockNotAvailableSqlState;

    /// <summary>The SQLSTATE for "deadlock detected".</summary>
    public const string DeadlockDetected = ErewhonDeadlockException.DeadlockDetectedSqlState;

    /// <summary>The SQLSTATE for "unique violation".</summary>
    public const string UniqueViolation = "23505";

    /// <summary>The SQLSTATE for "foreign key violation".</summary>
    public const string ForeignKeyViolation = "23503";

    /// <summary>The SQLSTATE for "connection failure", reported for a provider failure that carries no server SQLSTATE.</summary>
    public const string ConnectionFailure = "08006";

    /// <summary>
    /// Returns the first <see cref="PostgresException"/> found in <paramref name="exception"/>, its inner-exception chain
    /// and the inner exceptions of any <see cref="AggregateException"/>, searched depth first.
    /// </summary>
    /// <param name="exception">The exception to search, or <see langword="null"/>.</param>
    /// <returns>The first PostgreSQL server error found, or <see langword="null"/> when there is none.</returns>
    public static PostgresException? FindPostgresException(Exception? exception) =>
        Chain(exception).OfType<PostgresException>().FirstOrDefault();

    /// <summary>
    /// Returns the SQLSTATE of the first <see cref="PostgresException"/> in the exception chain; otherwise
    /// <see cref="ConnectionFailure"/> when the chain holds any other <see cref="NpgsqlException"/>; otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="exception">The exception to classify, or <see langword="null"/>.</param>
    /// <returns>The SQLSTATE, or <see langword="null"/> when the exception does not come from the database provider.</returns>
    public static string? GetSqlState(Exception? exception)
    {
        if (FindPostgresException(exception) is { } postgres)
        {
            return postgres.SqlState;
        }

        return FindConnectionFailure(exception) is null ? null : ConnectionFailure;
    }

    /// <summary>Returns whether the exception chain reports SQLSTATE <see cref="LockNotAvailable"/>.</summary>
    /// <param name="exception">The exception to classify, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a lock timeout; otherwise <see langword="false"/>.</returns>
    public static bool IsLockNotAvailable(Exception? exception) => GetSqlState(exception) == LockNotAvailable;

    /// <summary>Returns whether the exception chain reports SQLSTATE <see cref="DeadlockDetected"/>.</summary>
    /// <param name="exception">The exception to classify, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a deadlock; otherwise <see langword="false"/>.</returns>
    public static bool IsDeadlock(Exception? exception) => GetSqlState(exception) == DeadlockDetected;

    /// <summary>
    /// Returns whether the exception chain reports a SQLSTATE other than <see cref="LockNotAvailable"/> and
    /// <see cref="DeadlockDetected"/>, including <see cref="ConnectionFailure"/>.
    /// </summary>
    /// <param name="exception">The exception to classify, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for any other database-level failure; otherwise <see langword="false"/>.</returns>
    public static bool IsDatabaseFailure(Exception? exception) =>
        GetSqlState(exception) is { } sqlState && sqlState != LockNotAvailable && sqlState != DeadlockDetected;

    /// <summary>
    /// Maps a provider exception to the exception that reports its outcome.
    /// </summary>
    /// <param name="exception">The exception to map.</param>
    /// <returns>
    /// <paramref name="exception"/> itself when it already is an <see cref="ErewhonLockException"/>,
    /// <see cref="ErewhonDeadlockException"/>, <see cref="DatabaseFailureException"/> or <see cref="IJadeException"/>;
    /// an <see cref="ErewhonLockException"/> for <see cref="LockNotAvailable"/> and an
    /// <see cref="ErewhonDeadlockException"/> for <see cref="DeadlockDetected"/>, each with the
    /// <see cref="PostgresException"/> as inner exception; a <see cref="DatabaseFailureException"/> with the SQLSTATE,
    /// the PostgreSQL message text and <paramref name="exception"/> as inner exception for any other SQLSTATE;
    /// a <see cref="DatabaseFailureException"/> with <see cref="ConnectionFailure"/> and the provider message for a
    /// provider failure without a server SQLSTATE; otherwise <paramref name="exception"/> unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public Exception Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ErewhonLockException or ErewhonDeadlockException or DatabaseFailureException or IJadeException)
        {
            return exception;
        }

        if (FindPostgresException(exception) is { } postgres)
        {
            return postgres.SqlState switch
            {
                LockNotAvailable => new ErewhonLockException(LockNotAvailable, postgres),
                DeadlockDetected => new ErewhonDeadlockException(DeadlockDetected, postgres),
                _ => new DatabaseFailureException(
                    postgres.SqlState,
                    string.IsNullOrEmpty(postgres.MessageText) ? postgres.Message : postgres.MessageText,
                    exception),
            };
        }

        return FindConnectionFailure(exception) is { } connectionFailure
            ? new DatabaseFailureException(ConnectionFailure, connectionFailure.Message, exception)
            : exception;
    }

    private static NpgsqlException? FindConnectionFailure(Exception? exception) =>
        Chain(exception).OfType<NpgsqlException>().FirstOrDefault(e => e is not PostgresException);

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        if (exception is null)
        {
            yield break;
        }

        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                for (var i = aggregate.InnerExceptions.Count - 1; i >= 0; i--)
                {
                    pending.Push(aggregate.InnerExceptions[i]);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
