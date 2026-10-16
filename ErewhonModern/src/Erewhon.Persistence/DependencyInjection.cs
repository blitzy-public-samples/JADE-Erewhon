using System.Data.Common;
using System.Globalization;
using System.Security;
using Erewhon.Application.Abstractions;
using Erewhon.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.Persistence;

/// <summary>
/// Registers the persistence layer of the Erewhon store: the PostgreSQL <see cref="ErewhonDbContext"/> and the
/// adapters that implement <see cref="IErewhonDbContext"/>, <see cref="IUnitOfWork"/>, <see cref="IEntityLocks"/> and
/// <see cref="IServerClock"/> over it, the change-notification interceptor, the error mapper, the lock and seed
/// settings, and the seed <see cref="InitialDataLoader"/>.
/// </summary>
public static class DependencyInjection
{
    internal const string ConnectionStringName = "Erewhon";
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";
    private const string AllocationTimeoutKey = "Erewhon:Locks:AllocationTimeoutMs";
    private const string SeedDataDirectoryKey = "Erewhon:Seed:DataDirectory";
    private const string ContentRootKey = "contentRoot";
    private const string DefaultSeedDataDirectory = "../../../Erewhon/DataFiles";
    private const int DefaultLockWaitMs = 10000;

    private const string LockSettingRejectedTemplate =
        "{ConfigurationKey} is {RejectedValue}, which is not a positive integer number of milliseconds; "
        + "{LockWaitMs} ms is used instead";

    /// <summary>
    /// The event id, 600 <c>ErewhonLockSettingRejected</c>, of the <see cref="LogLevel.Warning"/> event written for an
    /// <c>Erewhon:Locks:TimeoutMs</c> or <c>Erewhon:Locks:AllocationTimeoutMs</c> value that is present and not empty
    /// but is not a positive integer.
    /// </summary>
    public static readonly EventId LockSettingRejectedEventId = new(600, "ErewhonLockSettingRejected");

    private static readonly EventId[] IgnoredEvents =
    [
        RelationalEventId.ConnectionOpening,
        RelationalEventId.ConnectionOpened,
        RelationalEventId.ConnectionClosing,
        RelationalEventId.ConnectionClosed,
        RelationalEventId.ConnectionDisposing,
        RelationalEventId.ConnectionDisposed,
        RelationalEventId.ConnectionError,
        RelationalEventId.DataReaderClosing,
        RelationalEventId.DataReaderDisposing,
        RelationalEventId.MigrateUsingConnection,
        RelationalEventId.CommandError,
        RelationalEventId.NonQueryOperationFailed,
        RelationalEventId.ExecuteDeleteFailed,
        RelationalEventId.ExecuteUpdateFailed,
        RelationalEventId.TransactionError,
        RelationalEventId.BatchExecutorFailedToRollbackToSavepoint,
        RelationalEventId.BatchExecutorFailedToReleaseSavepoint,
        CoreEventId.QueryIterationFailed,
        CoreEventId.SaveChangesFailed,
        CoreEventId.OptimisticConcurrencyException,
        CoreEventId.ExecutionStrategyRetrying,
    ];

    /// <summary>
    /// Adds the persistence services to <paramref name="services"/>. The lock and seed settings are read from
    /// <paramref name="configuration"/> once, at the time of the call; the connection string is read from it each time
    /// the options of a context are built:
    /// <list type="bullet">
    /// <item><description>
    /// a singleton <see cref="EntityLockSettings"/> with <c>Erewhon:Locks:TimeoutMs</c> and
    /// <c>Erewhon:Locks:AllocationTimeoutMs</c>, each read as a positive integer number of milliseconds and 10000 when
    /// it is absent, empty, not an integer, zero or negative; when the singleton is first resolved, the provider's
    /// <see cref="ILoggerFactory"/>, if it has one, receives one <see cref="LogLevel.Warning"/> event with
    /// <see cref="LockSettingRejectedEventId"/> for each of the two keys whose value is present and not empty but gave
    /// 10000, naming the key, the value and the 10000 milliseconds used;
    /// </description></item>
    /// <item><description>
    /// a singleton <see cref="SeedSettings"/> with the full path of <c>Erewhon:Seed:DataDirectory</c>
    /// (<c>../../../Erewhon/DataFiles</c> when it is absent, empty or white space); a relative path is taken from the
    /// host content root (<c>contentRoot</c>), or from the current directory when no content root is configured, and
    /// a path that cannot be normalised is passed on as combined;
    /// </description></item>
    /// <item><description>a singleton <see cref="PostgresErrorMapper"/>;</description></item>
    /// <item><description>a scoped <see cref="ChangeNotificationInterceptor"/>;</description></item>
    /// <item><description>
    /// a scoped <see cref="ErewhonDbContext"/> on the Npgsql provider with the connection string
    /// <c>ConnectionStrings:Erewhon</c>, or with no connection string when that value is absent, empty or white space,
    /// in which case every database access fails with a <see cref="MissingConnectionStringException"/>, whose message
    /// names the setting without a value, when the context creates or opens its connection; the context is created
    /// with the scope's <see cref="ChangeNotificationInterceptor"/> attached to its saves;
    /// </description></item>
    /// <item><description>
    /// on that context, these EF Core events are ignored, so none of them is logged at any level while interceptors
    /// still receive them: the connection events <c>ConnectionOpening</c>, <c>ConnectionOpened</c>,
    /// <c>ConnectionClosing</c>, <c>ConnectionClosed</c>, <c>ConnectionDisposing</c>, <c>ConnectionDisposed</c> and
    /// <c>ConnectionError</c>; the data-reader events <c>DataReaderClosing</c> and <c>DataReaderDisposing</c>;
    /// <c>MigrateUsingConnection</c>; the failure events <c>CommandError</c>,
    /// <c>NonQueryOperationFailed</c>, <c>ExecuteDeleteFailed</c>, <c>ExecuteUpdateFailed</c>,
    /// <c>TransactionError</c>, <c>BatchExecutorFailedToRollbackToSavepoint</c>,
    /// <c>BatchExecutorFailedToReleaseSavepoint</c>, <c>QueryIterationFailed</c>, <c>SaveChangesFailed</c> and
    /// <c>OptimisticConcurrencyException</c>; and <c>ExecutionStrategyRetrying</c>;
    /// </description></item>
    /// <item><description>
    /// scoped <see cref="IErewhonDbContext"/> (the scope's <see cref="ErewhonDbContext"/> itself),
    /// <see cref="IUnitOfWork"/> (<see cref="UnitOfWork"/>), <see cref="IEntityLocks"/>
    /// (<see cref="PostgresEntityLocks"/>) and <see cref="IServerClock"/> (<see cref="PostgresServerClock"/>), all over
    /// that one context per scope;
    /// </description></item>
    /// <item><description>a scoped <see cref="InitialDataLoader"/>.</description></item>
    /// </list>
    /// The application services, the notification queue and deriver, the company and session contexts, the
    /// notification publisher and the health checks are not registered here. No migration is applied.
    /// </summary>
    /// <param name="services">The service collection to add the persistence services to.</param>
    /// <param name="configuration">The configuration the connection string and settings are read from.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddErewhonPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var rejectedLockSettings = new List<KeyValuePair<string, string>>();
        var lockSettings = new EntityLockSettings(
            LockTimeoutMs: ReadPositiveMilliseconds(configuration, LockTimeoutKey, rejectedLockSettings),
            AllocationTimeoutMs: ReadPositiveMilliseconds(configuration, AllocationTimeoutKey, rejectedLockSettings));
        services.AddSingleton(provider =>
        {
            LogRejectedLockSettings(provider.GetService<ILoggerFactory>(), rejectedLockSettings);
            return lockSettings;
        });
        services.AddSingleton(new SeedSettings(ResolveSeedDataDirectory(configuration)));
        services.AddSingleton<PostgresErrorMapper>();
        services.AddScoped<ChangeNotificationInterceptor>();

        services.AddDbContext<ErewhonDbContext>((_, options) =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseNpgsql();
                options.AddInterceptors(MissingConnectionStringGuard.Instance);
            }
            else
            {
                options.UseNpgsql(connectionString);
            }

            options.ConfigureWarnings(warnings => warnings.Ignore(IgnoredEvents));
        });

        services.AddScoped<IErewhonDbContext>(provider => provider.GetRequiredService<ErewhonDbContext>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IEntityLocks, PostgresEntityLocks>();
        services.AddScoped<IServerClock, PostgresServerClock>();
        services.AddScoped<InitialDataLoader>();

        return services;
    }

    /// <summary>
    /// Returns the value of <paramref name="key"/> as a positive number of milliseconds, or 10000 when the value is
    /// absent or empty, is not an integer in the invariant culture, or is zero or negative. A value that is present and
    /// not empty but gives 10000 is added to <paramref name="rejected"/> with its key.
    /// </summary>
    /// <param name="configuration">The configuration to read.</param>
    /// <param name="key">The configuration key of the wait.</param>
    /// <param name="rejected">Receives the key and value of a rejected value.</param>
    /// <returns>The configured wait, or 10000.</returns>
    private static int ReadPositiveMilliseconds(
        IConfiguration configuration,
        string key,
        ICollection<KeyValuePair<string, string>> rejected)
    {
        var value = configuration[key];
        if (string.IsNullOrEmpty(value))
        {
            return DefaultLockWaitMs;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
        {
            return milliseconds;
        }

        rejected.Add(new KeyValuePair<string, string>(key, value));
        return DefaultLockWaitMs;
    }

    /// <summary>
    /// Writes one <see cref="LogLevel.Warning"/> event with <see cref="LockSettingRejectedEventId"/> for each entry of
    /// <paramref name="rejected"/>, in order, through a logger of the <see cref="EntityLockSettings"/> category, with the
    /// properties <c>ConfigurationKey</c>, <c>RejectedValue</c> and <c>LockWaitMs</c> (10000). Writes nothing when
    /// <paramref name="loggerFactory"/> is <see langword="null"/> or <paramref name="rejected"/> is empty.
    /// </summary>
    /// <param name="loggerFactory">
    /// The logger factory of the service provider, or <see langword="null"/> when it has none.
    /// </param>
    /// <param name="rejected">The keys and values of the rejected lock settings.</param>
    private static void LogRejectedLockSettings(
        ILoggerFactory? loggerFactory,
        IReadOnlyCollection<KeyValuePair<string, string>> rejected)
    {
        if (loggerFactory is null || rejected.Count == 0)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger<EntityLockSettings>();
        foreach (var (key, value) in rejected)
        {
            logger.LogWarning(LockSettingRejectedEventId, LockSettingRejectedTemplate, key, value, DefaultLockWaitMs);
        }
    }

    /// <summary>
    /// Returns the full path of the configured seed data directory: <c>Erewhon:Seed:DataDirectory</c>, or
    /// <c>../../../Erewhon/DataFiles</c> when that value is absent, empty or white space; a relative path is combined
    /// with <c>contentRoot</c>, or with the current directory when <c>contentRoot</c> is absent, empty or white space.
    /// When the combined path cannot be normalised it is returned as combined. Whether the directory exists is not
    /// checked.
    /// </summary>
    /// <param name="configuration">The configuration to read.</param>
    /// <returns>The seed data directory path.</returns>
    private static string ResolveSeedDataDirectory(IConfiguration configuration)
    {
        var configured = configuration[SeedDataDirectoryKey];
        var directory = string.IsNullOrWhiteSpace(configured) ? DefaultSeedDataDirectory : configured;

        if (!Path.IsPathRooted(directory))
        {
            var contentRoot = configuration[ContentRootKey];
            var basePath = string.IsNullOrWhiteSpace(contentRoot) ? Directory.GetCurrentDirectory() : contentRoot;
            directory = Path.Combine(basePath, directory);
        }

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception invalidPath) when (
            invalidPath is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            // Passed on unnormalised; InitialDataLoader reports it as an invalid directory (D-118).
            return directory;
        }
    }

    /// <summary>
    /// The connection interceptor of a context configured without a connection string: it throws
    /// <see cref="MissingConnectionStringException"/> when the context creates or opens a connection that has no
    /// connection string, and otherwise lets the creation or opening proceed unchanged.
    /// </summary>
    private sealed class MissingConnectionStringGuard : DbConnectionInterceptor
    {
        /// <summary>The instance attached to every context configured without a connection string.</summary>
        public static readonly MissingConnectionStringGuard Instance = new();

        /// <summary>
        /// Throws <see cref="MissingConnectionStringException"/> when the connection is to be created with no
        /// connection string; otherwise returns <paramref name="result"/>.
        /// </summary>
        /// <param name="eventData">The context and connection string of the connection being created.</param>
        /// <param name="result">The interception result passed in.</param>
        /// <returns><paramref name="result"/>.</returns>
        /// <exception cref="MissingConnectionStringException">The connection string is absent, empty or white space.</exception>
        public override InterceptionResult<DbConnection> ConnectionCreating(
            ConnectionCreatingEventData eventData,
            InterceptionResult<DbConnection> result) =>
            string.IsNullOrWhiteSpace(eventData.ConnectionString)
                ? throw new MissingConnectionStringException()
                : result;

        /// <summary>
        /// Throws <see cref="MissingConnectionStringException"/> when <paramref name="connection"/> has no connection
        /// string; otherwise returns <paramref name="result"/>.
        /// </summary>
        /// <param name="connection">The connection being opened.</param>
        /// <param name="eventData">The context and connection of the event.</param>
        /// <param name="result">The interception result passed in.</param>
        /// <returns><paramref name="result"/>.</returns>
        /// <exception cref="MissingConnectionStringException">The connection string is empty or white space.</exception>
        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) =>
            string.IsNullOrWhiteSpace(connection.ConnectionString)
                ? throw new MissingConnectionStringException()
                : result;

        /// <summary>
        /// Returns a task faulted with <see cref="MissingConnectionStringException"/> when
        /// <paramref name="connection"/> has no connection string; otherwise a task completed with
        /// <paramref name="result"/>.
        /// </summary>
        /// <param name="connection">The connection being opened.</param>
        /// <param name="eventData">The context and connection of the event.</param>
        /// <param name="result">The interception result passed in.</param>
        /// <param name="cancellationToken">Not used.</param>
        /// <returns>The faulted or completed task.</returns>
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            string.IsNullOrWhiteSpace(connection.ConnectionString)
                ? ValueTask.FromException<InterceptionResult>(new MissingConnectionStringException())
                : ValueTask.FromResult(result);
    }
}

/// <summary>
/// Raised when a database context registered by <see cref="DependencyInjection.AddErewhonPersistence"/> creates or
/// opens its connection while <c>ConnectionStrings:Erewhon</c> is absent, empty or white space. Its message,
/// <see cref="Text"/>, names the setting and carries no connection value.
/// </summary>
public sealed class MissingConnectionStringException : InvalidOperationException
{
    /// <summary>The exception message, <c>ConnectionStrings:Erewhon is not set.</c></summary>
    public const string Text = "ConnectionStrings:" + DependencyInjection.ConnectionStringName + " is not set.";

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message.
    /// </summary>
    public MissingConnectionStringException()
        : base(Text)
    {
    }
}
