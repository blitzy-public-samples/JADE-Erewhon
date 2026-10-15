namespace Erewhon.Application.Abstractions;

/// <summary>
/// Reads the current date and time from the database server's clock.
/// </summary>
public interface IServerClock
{
    /// <summary>
    /// Returns the current date on the database server: the date part of the value
    /// <see cref="GetServerTimeStampAsync(CancellationToken)"/> returns.
    /// </summary>
    /// <param name="ct">Cancels the clock read.</param>
    /// <returns>The database server's current date.</returns>
    Task<DateOnly> GetServerDateAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current timestamp on the database server, in the server's local time,
    /// at millisecond precision and with <see cref="DateTimeKind.Unspecified"/>, as PostgreSQL
    /// <c>SELECT clock_timestamp()::timestamp(3)</c> returns it.
    /// </summary>
    /// <param name="ct">Cancels the clock read.</param>
    /// <returns>The database server's current timestamp.</returns>
    Task<DateTime> GetServerTimeStampAsync(CancellationToken ct = default);
}
