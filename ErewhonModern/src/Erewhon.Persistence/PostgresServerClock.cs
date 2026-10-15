using Erewhon.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Persistence;

/// <summary>
/// Reads the current date and time from the PostgreSQL server's clock through the connection of an
/// <see cref="ErewhonDbContext"/>. Every call queries the server; when the context has a current transaction the
/// query runs in it.
/// </summary>
/// <param name="db">The context whose connection the clock is read through.</param>
public sealed class PostgresServerClock(ErewhonDbContext db) : IServerClock
{
    private const string ServerTimeStampSql = "SELECT clock_timestamp()::timestamp(3) AS \"Value\"";

    private readonly ErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Returns the date part of the timestamp <see cref="GetServerTimeStampAsync(CancellationToken)"/> returns.
    /// </summary>
    /// <param name="ct">Cancels the clock read.</param>
    /// <returns>The database server's current date.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<DateOnly> GetServerDateAsync(CancellationToken ct = default)
    {
        var timeStamp = await GetServerTimeStampAsync(ct);
        return DateOnly.FromDateTime(timeStamp);
    }

    /// <summary>
    /// Returns the result of <c>SELECT clock_timestamp()::timestamp(3)</c> on the database server: the server's
    /// current local time at the moment of the query, at millisecond precision, with
    /// <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    /// <param name="ct">Cancels the clock read.</param>
    /// <returns>The database server's current timestamp.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<DateTime> GetServerTimeStampAsync(CancellationToken ct = default)
    {
        var timeStamp = await _db.Database
            .SqlQueryRaw<DateTime>(ServerTimeStampSql)
            .SingleAsync(ct);
        return DateTime.SpecifyKind(timeStamp, DateTimeKind.Unspecified);
    }
}
