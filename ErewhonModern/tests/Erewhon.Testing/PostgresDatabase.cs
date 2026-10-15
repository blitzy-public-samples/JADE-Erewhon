using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Erewhon.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erewhon.Testing;

/// <summary>
/// A PostgreSQL test database reached through the connection string held in the
/// <c>ConnectionStrings__ErewhonTests</c> environment variable, with that string's database replaced by
/// <see cref="DatabaseName"/>. Creates the database and applies the <c>Erewhon.Persistence</c> migrations to it.
/// </summary>
public sealed class PostgresDatabase
{
    /// <summary>The environment variable that holds the base connection string of every test database.</summary>
    public const string EnvironmentVariable = "ConnectionStrings__ErewhonTests";

    /// <summary>The prefix of the database name derived for a test assembly.</summary>
    public const string DatabasePrefix = "erewhon_test_";

    private const string DefaultMaintenanceDatabase = "postgres";

    private static readonly Regex DatabaseNamePattern = new(
        @"^[a-z0-9_]{1,63}\z",
        RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, Lazy<Task>> Preparations = new(StringComparer.Ordinal);

    private readonly string _baseConnectionString;

    private PostgresDatabase(string baseConnectionString, string databaseName)
    {
        _baseConnectionString = baseConnectionString;
        DatabaseName = databaseName;
        ConnectionString = WithDatabase(databaseName);
    }

    /// <summary>The name of this database: lower-case letters, digits and underscores, 1 to 63 characters.</summary>
    public string DatabaseName { get; }

    /// <summary>
    /// The connection string of this database: every setting of the base connection string, with
    /// <c>Database</c> set to <see cref="DatabaseName"/>.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Returns the database of a test assembly, named <c>erewhon_test_</c> followed by the assembly name in lower
    /// case with each <c>.</c> replaced by <c>_</c> (for example <c>erewhon_test_erewhon_application_tests</c>).
    /// </summary>
    /// <param name="assembly">The test assembly.</param>
    /// <returns>The database of <paramref name="assembly"/>; nothing is created or opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The assembly has no name, or the derived name is not 1 to 63 lower-case letters, digits and underscores.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__ErewhonTests</c> is unset, empty, whitespace or not a valid PostgreSQL connection string.
    /// </exception>
    public static PostgresDatabase ForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var assemblyName = assembly.GetName().Name;
        if (string.IsNullOrEmpty(assemblyName))
        {
            throw new ArgumentException("The assembly has no name to derive a database name from.", nameof(assembly));
        }

        var databaseName = DatabasePrefix + assemblyName.ToLowerInvariant().Replace('.', '_');
        ValidateDatabaseName(databaseName, nameof(assembly));
        return new PostgresDatabase(ReadBaseConnectionString(), databaseName);
    }

    /// <summary>Returns the database with the given name, such as <c>erewhon_test_vitest</c>.</summary>
    /// <param name="databaseName">The database name: 1 to 63 lower-case letters, digits and underscores.</param>
    /// <returns>The named database; nothing is created or opened.</returns>
    /// <exception cref="ArgumentException"><paramref name="databaseName"/> is null or not a valid name.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__ErewhonTests</c> is unset, empty, whitespace or not a valid PostgreSQL connection string.
    /// </exception>
    public static PostgresDatabase Named(string databaseName)
    {
        ValidateDatabaseName(databaseName, nameof(databaseName));
        return new PostgresDatabase(ReadBaseConnectionString(), databaseName);
    }

    /// <summary>
    /// Returns the base connection string with <c>Database</c> set to <paramref name="databaseName"/> and every
    /// other setting unchanged.
    /// </summary>
    /// <param name="databaseName">The database name: 1 to 63 lower-case letters, digits and underscores.</param>
    /// <returns>The connection string of <paramref name="databaseName"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="databaseName"/> is null or not a valid name.</exception>
    public string WithDatabase(string databaseName)
    {
        ValidateDatabaseName(databaseName, nameof(databaseName));
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString) { Database = databaseName };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Returns <see cref="ConnectionString"/> with <c>Port</c> set to <paramref name="port"/> and every other
    /// setting unchanged.
    /// </summary>
    /// <param name="port">The server port, from 1 to 65535.</param>
    /// <returns>The connection string of this database on <paramref name="port"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is below 1 or above 65535.</exception>
    public string ForPort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Port = port };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Drops this database, ending every session connected to it, and creates it again empty. Every idle pooled
    /// connection of this process is closed first.
    /// </summary>
    /// <param name="ct">Cancels the drop and create commands.</param>
    /// <returns>A task that completes when the empty database exists.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>ConnectionStrings__ErewhonTests</c> names this database.
    /// </exception>
    /// <exception cref="NpgsqlException">The server cannot be reached or refuses a command.</exception>
    public async Task RecreateAsync(CancellationToken ct = default)
    {
        var maintenanceConnectionString = MaintenanceConnectionString;

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", ct);
        await ExecuteAsync(connection, $"CREATE DATABASE \"{DatabaseName}\"", ct);
    }

    /// <summary>Applies every pending <c>Erewhon.Persistence</c> migration to this database.</summary>
    /// <param name="ct">Cancels the migration.</param>
    /// <returns>A task that completes when the database holds every migration.</returns>
    /// <exception cref="NpgsqlException">The database cannot be reached or a migration fails.</exception>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<ErewhonDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var context = new ErewhonDbContext(options);
        await context.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Runs <see cref="RecreateAsync"/> and then <see cref="MigrateAsync"/> once in this process for
    /// <see cref="DatabaseName"/>. Every call for the same name, concurrent or later, completes when that single
    /// run completes, and fails with its exception when it failed.
    /// </summary>
    /// <param name="ct">Stops this call's wait; the shared run continues.</param>
    /// <returns>A task that completes when the database has been recreated and migrated in this process.</returns>
    public Task EnsureCreatedAndMigratedAsync(CancellationToken ct = default)
    {
        var preparation = Preparations.GetOrAdd(
            DatabaseName,
            _ => new Lazy<Task>(RecreateAndMigrateAsync, LazyThreadSafetyMode.ExecutionAndPublication));

        return preparation.Value.WaitAsync(ct);
    }

    private string MaintenanceConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString);
            var maintenanceDatabase = string.IsNullOrWhiteSpace(builder.Database)
                ? DefaultMaintenanceDatabase
                : builder.Database;

            if (string.Equals(maintenanceDatabase, DatabaseName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariable} names the database {DatabaseName}, which is dropped and recreated for "
                    + $"the tests. It must name another database, or none to use {DefaultMaintenanceDatabase}.");
            }

            builder.Database = maintenanceDatabase;
            return builder.ConnectionString;
        }
    }

    private async Task RecreateAndMigrateAsync()
    {
        await RecreateAsync();
        await MigrateAsync();
    }

    private static string ReadBaseConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The environment variable {EnvironmentVariable} is not set. It must hold a PostgreSQL 16 connection "
                + "string for a role allowed to create databases.");
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"The environment variable {EnvironmentVariable} does not hold a valid PostgreSQL connection string. "
                + "It must hold a PostgreSQL 16 connection string for a role allowed to create databases.",
                exception);
        }

        return connectionString;
    }

    private static void ValidateDatabaseName(string databaseName, string parameterName)
    {
        if (databaseName is null || !DatabaseNamePattern.IsMatch(databaseName))
        {
            throw new ArgumentException(
                $"'{databaseName}' is not a database name of 1 to 63 lower-case letters, digits and underscores.",
                parameterName);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
