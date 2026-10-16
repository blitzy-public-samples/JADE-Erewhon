using System.Globalization;
using System.Security;
using Erewhon.Application.Abstractions;
using Erewhon.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Persistence;

/// <summary>
/// Registers the persistence layer of the Erewhon store: the PostgreSQL <see cref="ErewhonDbContext"/> and the
/// adapters that implement <see cref="IErewhonDbContext"/>, <see cref="IUnitOfWork"/>, <see cref="IEntityLocks"/> and
/// <see cref="IServerClock"/> over it, the change-notification interceptor, the error mapper, the lock and seed
/// settings, and the seed <see cref="InitialDataLoader"/>.
/// </summary>
public static class DependencyInjection
{
    private const string ConnectionStringName = "Erewhon";
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";
    private const string AllocationTimeoutKey = "Erewhon:Locks:AllocationTimeoutMs";
    private const string SeedDataDirectoryKey = "Erewhon:Seed:DataDirectory";
    private const string ContentRootKey = "contentRoot";
    private const string DefaultSeedDataDirectory = "../../../Erewhon/DataFiles";
    private const int DefaultLockWaitMs = 10000;

    /// <summary>
    /// Adds the persistence services to <paramref name="services"/>. The lock and seed settings are read from
    /// <paramref name="configuration"/> once, at the time of the call; the connection string is read from it each time
    /// the options of a context are built:
    /// <list type="bullet">
    /// <item><description>
    /// a singleton <see cref="EntityLockSettings"/> with <c>Erewhon:Locks:TimeoutMs</c> and
    /// <c>Erewhon:Locks:AllocationTimeoutMs</c>, each read as a positive integer number of milliseconds and 10000 when
    /// it is absent, not an integer, zero or negative;
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
    /// in which case every database access fails with the provider's error; the context is created with the scope's
    /// <see cref="ChangeNotificationInterceptor"/> attached to its saves;
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

        services.AddSingleton(new EntityLockSettings(
            LockTimeoutMs: ReadPositiveMilliseconds(configuration, LockTimeoutKey),
            AllocationTimeoutMs: ReadPositiveMilliseconds(configuration, AllocationTimeoutKey)));
        services.AddSingleton(new SeedSettings(ResolveSeedDataDirectory(configuration)));
        services.AddSingleton<PostgresErrorMapper>();
        services.AddScoped<ChangeNotificationInterceptor>();

        services.AddDbContext<ErewhonDbContext>((_, options) =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseNpgsql();
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
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
    /// absent, is not an integer in the invariant culture, or is zero or negative.
    /// </summary>
    /// <param name="configuration">The configuration to read.</param>
    /// <param name="key">The configuration key of the wait.</param>
    /// <returns>The configured wait, or 10000.</returns>
    private static int ReadPositiveMilliseconds(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? milliseconds
            : DefaultLockWaitMs;
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
}
