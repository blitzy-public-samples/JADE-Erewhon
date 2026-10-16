using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises <see cref="UnitOfWork.CommitAsync"/> on PostgreSQL: a transaction the database has already aborted
/// after a failed statement is not committed and the commit throws <see cref="DatabaseFailureException"/> with SQLSTATE
/// <c>25P02</c>, leaving no active transaction and a context that opens and commits the next transaction; a transaction
/// rolled back to the <c>ta_locks</c> savepoint after a failed statement commits the writes made before the savepoint.
/// Each case writes countries of a company it creates and deletes that company at the end.
/// </summary>
public sealed class UnitOfWorkCommitTests : IAsyncLifetime
{
    private const string InFailedSqlTransaction = "25P02";

    private const int LockWaitMs = 200;

    private PostgresDatabase? _database;

    /// <summary>The failure that leaves the transaction aborted before the commit.</summary>
    public enum AbortCause
    {
        /// <summary>
        /// <see cref="PostgresEntityLocks.TryLockAsync"/> waits for <c>Company.allCountries:&lt;id&gt;</c>, which a
        /// second connection holds, until the wait expires (SQLSTATE <c>55P03</c>).
        /// </summary>
        AdvisoryLockWaitExpired,

        /// <summary><c>SELECT 1/0</c> fails with SQLSTATE <c>22012</c>.</summary>
        FailedStatement,
    }

    /// <summary>Each failure that leaves the transaction aborted.</summary>
    public static TheoryData<AbortCause> AbortCauses => new()
    {
        AbortCause.AdvisoryLockWaitExpired,
        AbortCause.FailedStatement,
    };

    private PostgresDatabase Database =>
        _database ?? throw new InvalidOperationException("The database is prepared by InitializeAsync.");

    /// <summary>Creates and migrates the test database of the assembly.</summary>
    /// <returns>A task that completes when the database is ready.</returns>
    public async Task InitializeAsync()
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(UnitOfWorkCommitTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();
        _database = database;
    }

    /// <summary>Completes without work; each case deletes the company it creates.</summary>
    /// <returns>A completed task.</returns>
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A country saved in a transaction that <paramref name="cause"/> then leaves aborted is not committed:
    /// <see cref="UnitOfWork.CommitAsync"/> throws <see cref="DatabaseFailureException"/> with SQLSTATE <c>25P02</c>
    /// and the PostgreSQL message text, no transaction is active afterwards, the country does not exist, and the same
    /// context then begins and commits a transaction that persists another country.
    /// </summary>
    /// <param name="cause">The failure that leaves the transaction aborted.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(AbortCauses))]
    public async Task CommitOfAbortedTransactionThrows25P02AndCommitsNothing(AbortCause cause)
    {
        long companyId = await CreateCompanyAsync();
        try
        {
            string lockName = LockNames.CompanyAllCountries(companyId);
            await using NpgsqlConnection? holder = cause == AbortCause.AdvisoryLockWaitExpired
                ? await HoldAdvisoryLockAsync(lockName)
                : null;

            await using ErewhonDbContext db = CreateContext();
            var uow = new UnitOfWork(db);
            var locks = new PostgresEntityLocks(
                db,
                new EntityLockSettings(LockWaitMs, LockWaitMs),
                new PostgresErrorMapper());

            await uow.BeginAsync();
            db.Countries.Add(NewCountry(companyId, "Aborted Country"));
            await db.SaveChangesAsync();

            if (cause == AbortCause.AdvisoryLockWaitExpired)
            {
                Assert.False(await locks.TryLockAsync(lockName));
            }
            else
            {
                PostgresException failed = await Assert.ThrowsAsync<PostgresException>(
                    () => db.Database.ExecuteSqlRawAsync("SELECT 1/0"));
                Assert.Equal(PostgresErrorCodes.DivisionByZero, failed.SqlState);
            }

            DatabaseFailureException failure = await Assert.ThrowsAsync<DatabaseFailureException>(
                () => uow.CommitAsync());

            Assert.Equal(InFailedSqlTransaction, failure.SqlState);
            PostgresException inner = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(InFailedSqlTransaction, inner.SqlState);
            Assert.Equal(inner.MessageText, failure.DatabaseMessage);
            Assert.Equal(failure.DatabaseMessage, failure.Message);
            Assert.False(uow.HasActiveTransaction);
            Assert.Empty(await CountryNamesAsync(companyId));

            await uow.BeginAsync();
            db.Countries.Add(NewCountry(companyId, "Committed Country"));
            await db.SaveChangesAsync();
            await uow.CommitAsync();
            Assert.False(uow.HasActiveTransaction);

            Assert.Equal(new[] { "Committed Country" }, await CountryNamesAsync(companyId));
        }
        finally
        {
            await DeleteCompanyAsync(companyId);
        }
    }

    /// <summary>
    /// A country saved before the <c>ta_locks</c> savepoint persists when a statement fails after the savepoint and
    /// the transaction is rolled back to it: <see cref="UnitOfWork.CommitAsync"/> returns normally, no transaction is
    /// active afterwards, and the country saved after the savepoint does not exist.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CommitAfterRollbackToSavepointPersistsEarlierWrites()
    {
        long companyId = await CreateCompanyAsync();
        try
        {
            await using ErewhonDbContext db = CreateContext();
            var uow = new UnitOfWork(db);

            await uow.BeginAsync();
            db.Countries.Add(NewCountry(companyId, "Before Savepoint"));
            await db.SaveChangesAsync();

            await uow.CreateSavepointAsync("ta_locks");
            db.Countries.Add(NewCountry(companyId, "After Savepoint"));
            await db.SaveChangesAsync();
            PostgresException failed = await Assert.ThrowsAsync<PostgresException>(
                () => db.Database.ExecuteSqlRawAsync("SELECT 1/0"));
            Assert.Equal(PostgresErrorCodes.DivisionByZero, failed.SqlState);
            await uow.RollbackToSavepointAsync("ta_locks");

            await uow.CommitAsync();

            Assert.False(uow.HasActiveTransaction);
            Assert.Equal(new[] { "Before Savepoint" }, await CountryNamesAsync(companyId));
        }
        finally
        {
            await DeleteCompanyAsync(companyId);
        }
    }

    /// <summary>
    /// Opens an unpooled connection to the test database that holds the session-level advisory lock
    /// <c>pg_advisory_lock(hashtextextended(lockName, 0))</c> until the connection is disposed.
    /// </summary>
    private async Task<NpgsqlConnection> HoldAdvisoryLockAsync(string lockName)
    {
        var holder = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await holder.OpenAsync();
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@name, 0))", holder);
            hold.Parameters.AddWithValue("name", lockName);
            await hold.ExecuteNonQueryAsync();
            return holder;
        }
        catch
        {
            await holder.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns a new context over the test database with no change-notification interceptor.</summary>
    private ErewhonDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ErewhonDbContext>().UseNpgsql(Database.ConnectionString).Options);

    /// <summary>Returns a new country with the given name in the company with id <paramref name="companyId"/>.</summary>
    private static Country NewCountry(long companyId, string name) => new()
    {
        CompanyId = companyId,
        Name = name,
        Guid = PersistentModel.GenerateGuid(),
    };

    /// <summary>Commits a new company with an empty address in its own context and returns its row id.</summary>
    private async Task<long> CreateCompanyAsync()
    {
        await using ErewhonDbContext db = CreateContext();
        var company = new Company
        {
            Name = "Unit Of Work Commit Company",
            Guid = PersistentModel.GenerateGuid(),
            Address = new Address { Guid = PersistentModel.GenerateGuid() },
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>Returns the names of the committed countries of the company, in name order, read in a new context.</summary>
    private async Task<List<string>> CountryNamesAsync(long companyId)
    {
        await using ErewhonDbContext db = CreateContext();
        return await db.Countries
            .Where(c => c.CompanyId == companyId)
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();
    }

    /// <summary>Deletes the company row, and with it every country of the company, in a new context.</summary>
    private async Task DeleteCompanyAsync(long companyId)
    {
        await using ErewhonDbContext db = CreateContext();
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync();
    }
}
