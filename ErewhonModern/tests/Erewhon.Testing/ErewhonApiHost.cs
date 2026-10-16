using Erewhon.Api;
using Erewhon.SoapClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

namespace Erewhon.Testing;

/// <summary>
/// The settings of one <see cref="ErewhonApiHost"/>: the database connection, the listen address, configuration
/// overrides, an optional log sink, and callbacks that change the service registrations and map further endpoints.
/// </summary>
public sealed record ErewhonApiHostOptions
{
    /// <summary>The value given to <c>ConnectionStrings:Erewhon</c>.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The address Kestrel listens on; <see cref="ErewhonApiHost.DefaultUrl"/> unless set.</summary>
    public string Url { get; init; } = ErewhonApiHost.DefaultUrl;

    /// <summary>
    /// Configuration values that replace the host's defaults or add further keys, for example
    /// <c>Erewhon:Closure:TickMs</c> or <c>Erewhon:Locks:TimeoutMs</c>. Keys compare case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>A sink that receives every event of the host's Serilog logger, or <see langword="null"/> for none.</summary>
    public CollectingLogSink? LogSink { get; init; }

    /// <summary>
    /// Receives the service collection after every Erewhon registration and before the service provider is built;
    /// it may add, remove or replace registrations.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Receives the built application before it starts, to map further endpoints.</summary>
    public Action<WebApplication>? MapEndpoints { get; init; }
}

/// <summary>
/// The Erewhon API built by <see cref="ErewhonHost.Build"/> and running on Kestrel in this process, with factories for
/// HTTP, SOAP and SignalR clients addressed to it. Several instances can run in one process at the same time.
/// </summary>
public sealed class ErewhonApiHost : IAsyncDisposable
{
    /// <summary>The default listen address: the IPv4 loopback interface on a port the operating system assigns.</summary>
    public const string DefaultUrl = "http://127.0.0.1:0";

    /// <summary>
    /// The <see cref="HttpClient.Timeout"/> of every client <see cref="CreateHttpClient"/> returns: 100 seconds.
    /// </summary>
    public static readonly TimeSpan DefaultHttpClientTimeout = TimeSpan.FromSeconds(100);

    private const string ConnectionStringKey = "ConnectionStrings:Erewhon";
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";
    private const string AllocationTimeoutKey = "Erewhon:Locks:AllocationTimeoutMs";
    private const string ClosureTickKey = "Erewhon:Closure:TickMs";
    private const string SeedDataDirectoryKey = "Erewhon:Seed:DataDirectory";
    private const string DefaultLockWaitMs = "10000";
    private const string DefaultClosureTickMs = "1000";
    private const string SolutionFileName = "ErewhonModern.sln";

    // The values of ErewhonHub.Path and SessionMiddleware.HubSessionQueryKey, written here as literals (D-130).
    private const string HubPath = "hubs/erewhon";
    private const string HubSessionQueryKey = "session";

    private int _stopRequested;
    private int _disposed;

    private ErewhonApiHost(WebApplication app, Uri baseAddress)
    {
        App = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The absolute <c>http</c> address the host listens on, ending in <c>/</c>, with the bound port.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The running web application.</summary>
    public WebApplication App { get; }

    /// <summary>The root service provider of the running application.</summary>
    public IServiceProvider Services => App.Services;

    /// <summary>
    /// The full path of <c>Erewhon/DataFiles</c>, the directory beside the folder returned by
    /// <see cref="FindErewhonModernRoot"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">No folder above the application base directory holds
    /// <c>ErewhonModern.sln</c>.</exception>
    public static string DefaultSeedDataDirectory =>
        Path.GetFullPath(Path.Combine(FindErewhonModernRoot(), "..", "Erewhon", "DataFiles"));

    /// <summary>
    /// Builds the Erewhon API through <see cref="ErewhonHost.Build"/>, maps the endpoints of
    /// <see cref="ErewhonApiHostOptions.MapEndpoints"/>, starts it on Kestrel at <see cref="ErewhonApiHostOptions.Url"/>
    /// and returns once it listens.
    /// <list type="bullet">
    /// <item><description>
    /// Configuration, overriding every <c>appsettings</c> file and environment variable: <c>ConnectionStrings:Erewhon</c>
    /// = <see cref="ErewhonApiHostOptions.ConnectionString"/>; <c>Erewhon:Locks:TimeoutMs</c> and
    /// <c>Erewhon:Locks:AllocationTimeoutMs</c> = 10000; <c>Erewhon:Closure:TickMs</c> = 1000;
    /// <c>Erewhon:Seed:DataDirectory</c> = <see cref="DefaultSeedDataDirectory"/>; then every entry of
    /// <see cref="ErewhonApiHostOptions.Settings"/>, which replaces a default with the same key.
    /// </description></item>
    /// <item><description>
    /// Services, after every Erewhon registration: <see cref="ErewhonApiHostOptions.LogSink"/> as a Serilog
    /// <see cref="ILogEventSink"/> when set, then <see cref="ErewhonApiHostOptions.ConfigureServices"/>. The service
    /// provider validates scopes and registrations on build in the Development environment only.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="options">The connection, address, settings and callbacks of the host.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running host.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, its <see cref="ErewhonApiHostOptions.ConnectionString"/> or its
    /// <see cref="ErewhonApiHostOptions.Settings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><see cref="ErewhonApiHostOptions.Url"/> is null, empty or white space.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <see cref="ErewhonApiHostOptions.Settings"/> has no <c>Erewhon:Seed:DataDirectory</c> entry and no folder above
    /// the application base directory holds <c>ErewhonModern.sln</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The started server reports no <c>http</c> address.</exception>
    /// <exception cref="AggregateException">
    /// The start failed and stopping or disposing the application failed too; holds both failures.
    /// </exception>
    /// <remarks>
    /// When mapping, starting or reading the address fails, the application is stopped and disposed and the failure
    /// is rethrown.
    /// </remarks>
    public static async Task<ErewhonApiHost> StartAsync(ErewhonApiHostOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Url);
        ArgumentNullException.ThrowIfNull(options.Settings);

        var settings = BuildSettings(options);

        var app = ErewhonHost.Build(Array.Empty<string>(), builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseUrls(options.Url);
            builder.Host.UseServiceProviderFactory(new PostConfiguringServiceProviderFactory(
                services => ApplyServiceOptions(services, options),
                validate: builder.Environment.IsDevelopment()));
        });

        var startInvoked = false;
        try
        {
            options.MapEndpoints?.Invoke(app);

            startInvoked = true;
            await app.StartAsync(ct);

            return new ErewhonApiHost(app, ReadBaseAddress(app));
        }
        catch (Exception startFailure)
        {
            try
            {
                await ShutDownAfterFailedStartAsync(app, startInvoked);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "The Erewhon API failed to start, and stopping or disposing it failed too.",
                    startFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> and returns the full path of the first folder that holds
    /// <c>ErewhonModern.sln</c>.
    /// </summary>
    /// <returns>The full path of the <c>ErewhonModern</c> folder.</returns>
    /// <exception cref="DirectoryNotFoundException">No folder on the way up holds <c>ErewhonModern.sln</c>.</exception>
    public static string FindErewhonModernRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No folder at or above '{AppContext.BaseDirectory}' holds {SolutionFileName}.");
    }

    /// <summary>
    /// Creates an HTTP client whose base address is <see cref="BaseAddress"/> and whose timeout is
    /// <see cref="DefaultHttpClientTimeout"/>; a request that runs longer fails with a
    /// <see cref="TaskCanceledException"/>. The caller disposes it.
    /// </summary>
    /// <returns>A new <see cref="HttpClient"/>.</returns>
    public HttpClient CreateHttpClient() => new() { BaseAddress = BaseAddress, Timeout = DefaultHttpClientTimeout };

    /// <summary>
    /// Creates a SOAP client factory for <c>/soap/ErewhonWebService</c> and <c>/soap/ErewhonWebServiceAdmin</c> under
    /// <see cref="BaseAddress"/>.
    /// </summary>
    /// <returns>A new <see cref="ErewhonSoapClientFactory"/>.</returns>
    public ErewhonSoapClientFactory CreateSoapClientFactory() => new(BaseAddress);

    /// <summary>
    /// Returns the address of the SignalR hub <c>/hubs/erewhon</c> under <see cref="BaseAddress"/>, with the query value
    /// <c>session</c> set to <paramref name="sessionId"/>, escaped.
    /// </summary>
    /// <param name="sessionId">The session id the connection presents.</param>
    /// <returns>The absolute hub address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    public Uri HubUrl(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return new Uri(BaseAddress, $"{HubPath}?{HubSessionQueryKey}={Uri.EscapeDataString(sessionId)}");
    }

    /// <summary>Stops the web server and the hosted services, waiting for them to finish.</summary>
    /// <param name="ct">Ends the graceful shutdown early.</param>
    /// <returns>A task that completes when the application has stopped.</returns>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return StopCoreAsync(ct);
    }

    /// <summary>
    /// Stops the application unless <see cref="StopAsync"/> was already called, then disposes it. Later calls do nothing.
    /// </summary>
    /// <returns>A task that completes when the application is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _stopRequested) == 0)
            {
                await StopCoreAsync(CancellationToken.None);
            }
        }
        finally
        {
            await App.DisposeAsync();
        }
    }

    /// <summary>Marks the host as stopped and stops the application.</summary>
    /// <param name="ct">Ends the graceful shutdown early.</param>
    /// <returns>A task that completes when the application has stopped.</returns>
    private Task StopCoreAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        return App.StopAsync(ct);
    }

    /// <summary>
    /// Returns the default settings with the connection string of <paramref name="options"/>, overlaid with every entry
    /// of <see cref="ErewhonApiHostOptions.Settings"/>. The seed directory default is computed only when the settings
    /// carry no seed directory entry.
    /// </summary>
    /// <param name="options">The host options.</param>
    /// <returns>The effective configuration values, keyed case-insensitively.</returns>
    private static Dictionary<string, string?> BuildSettings(ErewhonApiHostOptions options)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConnectionStringKey] = options.ConnectionString,
            [LockTimeoutKey] = DefaultLockWaitMs,
            [AllocationTimeoutKey] = DefaultLockWaitMs,
            [ClosureTickKey] = DefaultClosureTickMs,
        };

        if (!options.Settings.Keys.Any(key => string.Equals(key, SeedDataDirectoryKey, StringComparison.OrdinalIgnoreCase)))
        {
            settings[SeedDataDirectoryKey] = DefaultSeedDataDirectory;
        }

        foreach (var (key, value) in options.Settings)
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>
    /// Adds <see cref="ErewhonApiHostOptions.LogSink"/> as a singleton <see cref="ILogEventSink"/> when it is set, then
    /// passes the collection to <see cref="ErewhonApiHostOptions.ConfigureServices"/>.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="options">The host options.</param>
    private static void ApplyServiceOptions(IServiceCollection services, ErewhonApiHostOptions options)
    {
        if (options.LogSink is not null)
        {
            services.AddSingleton<ILogEventSink>(options.LogSink);
        }

        options.ConfigureServices?.Invoke(services);
    }

    /// <summary>Returns the first <c>http</c> address the started server reports, ending in <c>/</c>.</summary>
    /// <param name="app">The started application.</param>
    /// <returns>The absolute base address.</returns>
    /// <exception cref="InvalidOperationException">The server reports no <c>http</c> address.</exception>
    private static Uri ReadBaseAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .GetRequiredFeature<IServerAddressesFeature>()
            .Addresses;

        var httpPrefix = Uri.UriSchemeHttp + Uri.SchemeDelimiter;
        var address = addresses.FirstOrDefault(candidate => candidate.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The started server reports no {Uri.UriSchemeHttp} address; it reports: [{string.Join(", ", addresses)}].");

        return new Uri(address.EndsWith('/') ? address : address + "/", UriKind.Absolute);
    }

    /// <summary>Stops the application when its start was invoked, then disposes it.</summary>
    /// <param name="app">The application whose start failed.</param>
    /// <param name="startInvoked">Whether <c>StartAsync</c> was called on the application.</param>
    /// <returns>A task that completes when the application is disposed.</returns>
    private static async Task ShutDownAfterFailedStartAsync(WebApplication app, bool startInvoked)
    {
        try
        {
            if (startInvoked)
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A service provider factory that passes the service collection to a callback when the host builds its services,
    /// then builds the default provider with scope and build-time validation on or off.
    /// </summary>
    /// <param name="configure">Receives the complete service collection.</param>
    /// <param name="validate">Whether the provider validates scopes and every registration on build.</param>
    private sealed class PostConfiguringServiceProviderFactory(Action<IServiceCollection> configure, bool validate)
        : IServiceProviderFactory<IServiceCollection>
    {
        /// <summary>Passes <paramref name="services"/> to the callback and returns it.</summary>
        /// <param name="services">The complete service collection.</param>
        /// <returns><paramref name="services"/>.</returns>
        public IServiceCollection CreateBuilder(IServiceCollection services)
        {
            configure(services);
            return services;
        }

        /// <summary>Builds the default service provider from <paramref name="containerBuilder"/>.</summary>
        /// <param name="containerBuilder">The service collection returned by <see cref="CreateBuilder"/>.</param>
        /// <returns>The service provider.</returns>
        public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) =>
            containerBuilder.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = validate,
                ValidateOnBuild = validate,
            });
    }
}
