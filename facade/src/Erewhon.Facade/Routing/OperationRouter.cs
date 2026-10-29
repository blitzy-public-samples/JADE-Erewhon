using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using Erewhon.Contracts.Catalogue;
using Microsoft.Extensions.Options;

namespace Erewhon.Facade.Routing;

/// <summary>
/// The target one request is forwarded to: the routing target's name, the base URL of the request's
/// surface and the call's deadline.
/// </summary>
/// <remarks>
/// Equal configuration gives equal records, so <c>BackendForwarder</c> keys one
/// <c>HttpMessageInvoker</c> per distinct target on this record, and a changed URL or deadline becomes
/// a new key.
/// </remarks>
/// <param name="Name">The target's key under <c>Routing:Targets</c>, spelled as configured.</param>
/// <param name="BaseUrl">
/// The target's <c>RestBaseUrl</c> for a REST request or its <c>SoapBaseUrl</c> for a SOAP request.
/// </param>
/// <param name="Timeout">
/// The target's <c>TimeoutSeconds</c>, or <c>Backend:TimeoutSeconds</c> when the target sets none.
/// </param>
public sealed record ResolvedTarget(string Name, Uri BaseUrl, TimeSpan Timeout);

/// <summary>
/// The effective routing table, as <c>GET /admin/info</c> on the admin listener reports it.
/// </summary>
/// <param name="DefaultTarget"><c>Routing:DefaultTarget</c> as configured.</param>
/// <param name="BackendTimeoutSeconds">
/// <c>Backend:TimeoutSeconds</c> as the table in service was compiled with it, the deadline of every
/// target that sets none. While a reload is rejected it stays the value the table still applies.
/// </param>
/// <param name="Targets">Every configured target, referenced or not, in ordinal name order.</param>
/// <param name="Operations">
/// One row per catalogue operation: the REST operations in catalogue order, then the SOAP ones.
/// </param>
public sealed record RoutingSnapshot(
    string DefaultTarget,
    int BackendTimeoutSeconds,
    IReadOnlyList<RoutingTargetInfo> Targets,
    IReadOnlyList<RoutedOperation> Operations);

/// <summary>One configured routing target in a <see cref="RoutingSnapshot"/>.</summary>
/// <param name="Name">The target's key under <c>Routing:Targets</c>, spelled as configured.</param>
/// <param name="RestBaseUrl">The configured REST base URL; null when absent or blank.</param>
/// <param name="SoapBaseUrl">The configured SOAP base URL; null when absent or blank.</param>
/// <param name="TimeoutSeconds">
/// The effective deadline in seconds: the target's own <c>TimeoutSeconds</c>, or
/// <c>Backend:TimeoutSeconds</c> when the target sets none.
/// </param>
public sealed record RoutingTargetInfo(string Name, string? RestBaseUrl, string? SoapBaseUrl, int TimeoutSeconds);

/// <summary>One catalogue operation and the target it is routed to, in a <see cref="RoutingSnapshot"/>.</summary>
/// <param name="Id">The catalogue operation id, <c>&lt;Surface&gt;.&lt;JadeMethod&gt;</c>.</param>
/// <param name="Surface">
/// The operation's surface, <c>Rest</c> or <c>Soap</c>, written as a string so the admin JSON needs no
/// enum converter.
/// </param>
/// <param name="Operation">The operation's routing key, its JADE method name.</param>
/// <param name="Target">The name of the target <see cref="OperationRouter.Resolve"/> picks for it.</param>
public sealed record RoutedOperation(string Id, string Surface, string Operation, string Target);

/// <summary>
/// Resolves the target each request is forwarded to from its surface and matched catalogue operation,
/// so moving one operation means changing one <c>Routing:Operations:&lt;Rest|Soap&gt;:&lt;jadeMethod&gt;</c>
/// key while callers and every other operation are unaffected.
/// </summary>
/// <remarks>
/// <para>
/// A listed operation goes to the target its entry names; an unlisted operation and a request that
/// matched no catalogue operation go to <c>Routing:DefaultTarget</c>, so anything the backend answers
/// today at the caller addresses keeps reaching it. Resolution depends only on the surface and the
/// operation, never on caller headers or identity, and it records nothing.
/// </para>
/// <para>
/// Every call reads <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> of both options, so a
/// configuration reload takes effect on the next request. The URLs are parsed once per options
/// instance into an immutable table. A call whose options are the ones the published table was
/// compiled from reads that table without a lock. Rebuilding and publishing are serialised, and each
/// rebuild re-reads both options under the lock, so a table compiled from older options never replaces
/// a newer one; that makes the router safe for concurrent requests as a singleton.
/// </para>
/// <para>
/// <see cref="OperationRouterStartup"/> publishes the table of the start-up options, which
/// <c>ValidateOnStart</c> has just accepted, before the listeners accept a request. From then on a
/// reload that fails binding (a value that does not convert to its option's type) or validation keeps
/// the last valid table in service: it is logged once per distinct failure, and the next valid reload
/// replaces the table. Until a table is published such a failure propagates unchanged, so invalid
/// start-up configuration still stops the facade.
/// </para>
/// </remarks>
public sealed partial class OperationRouter
{
    private const string DefaultTargetPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.DefaultTarget);
    private const string TargetsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Targets);
    private const string OperationsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Operations);
    private const string DefaultTargetUsage = "is " + DefaultTargetPath;

    private static readonly SurfaceRoute RestRoute = new(
        Surface.Rest, nameof(RoutingOperations.Rest), nameof(RoutingTarget.RestBaseUrl), static target => target.RestBaseUrl);

    private static readonly SurfaceRoute SoapRoute = new(
        Surface.Soap, nameof(RoutingOperations.Soap), nameof(RoutingTarget.SoapBaseUrl), static target => target.SoapBaseUrl);

    private readonly IOptionsMonitor<RoutingOptions> _routing;
    private readonly IOptionsMonitor<BackendOptions> _backend;
    private readonly OperationCatalogue _catalogue;
    private readonly ILogger<OperationRouter> _logger;

    // Serialises rebuilding and publishing _table, and reporting rejected reloads.
    private readonly Lock _publication = new();

    // Read with Volatile.Read without the lock, and replaced whole with Volatile.Write only while
    // _publication is held; never mutated, and never null again once published.
    private CompiledTable? _table;

    // The rejected reload last logged, so a failure is logged once rather than per request; written
    // only while _publication is held.
    private string? _reportedFailure;

    /// <summary>Creates the router; <c>Program.cs</c> registers it as a singleton.</summary>
    /// <param name="routing">The routing table, validated by <see cref="RoutingOptionsValidator"/>.</param>
    /// <param name="backend">The <c>Backend</c> section, whose <c>TimeoutSeconds</c> is the default deadline.</param>
    /// <param name="catalogue">The operation catalogue, the only source of operations.</param>
    /// <param name="logger">Receives one error per distinct rejected reload and one note when a reload is valid again.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public OperationRouter(
        IOptionsMonitor<RoutingOptions> routing,
        IOptionsMonitor<BackendOptions> backend,
        OperationCatalogue catalogue,
        ILogger<OperationRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(logger);

        _routing = routing;
        _backend = backend;
        _catalogue = catalogue;
        _logger = logger;
    }

    /// <summary>
    /// Compiles and publishes the table of the current options, unless the published table was already
    /// compiled from them, so the start-up configuration stays in service if a reload is rejected
    /// before the first request.
    /// </summary>
    /// <remarks><see cref="OperationRouterStartup"/> calls it once start-up validation has passed.</remarks>
    /// <exception cref="OptionsValidationException">The configuration fails validation and no table has been published yet.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configuration cannot be bound, a value not converting to its option's type, and no table has
    /// been published yet.
    /// </exception>
    public void Initialize() => CurrentTable();

    /// <summary>Resolves the target of one request under the current configuration.</summary>
    /// <param name="surface">The listener the request arrived on, <see cref="Surface.Rest"/> or <see cref="Surface.Soap"/>.</param>
    /// <param name="operation">The matched catalogue operation; null for a request that matched none.</param>
    /// <returns>
    /// The target named by the operation's <c>Routing:Operations:&lt;surface&gt;</c> entry when it is
    /// listed, otherwise <c>Routing:DefaultTarget</c>, with that surface's base URL and the deadline.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is neither surface.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> belongs to the other surface.</exception>
    /// <exception cref="InvalidOperationException">
    /// The entry serving the request names no usable target; the message names the entry by its
    /// configuration path. No other target is substituted. Also thrown, unchanged from the
    /// configuration binder, when the configuration cannot be bound and no table has been published yet.
    /// </exception>
    /// <exception cref="OptionsValidationException">The configuration fails validation and no table has been published yet.</exception>
    public ResolvedTarget Resolve(Surface surface, OperationDescriptor? operation)
    {
        var surfaceRoute = SurfaceRouteFor(surface);
        if (operation is not null && operation.Surface != surface)
        {
            throw new ArgumentException(
                $"Operation {operation.Id} belongs to the {operation.Surface} surface and cannot be routed as a {surface} request.",
                nameof(operation));
        }

        var table = CurrentTable();
        var routes = table.For(surface);

        Route route;
        if (operation is null)
        {
            route = routes.Default;
        }
        else if (!routes.ByRoutingKey.TryGetValue(operation.RoutingKey, out route!))
        {
            // A descriptor from outside the catalogue: resolve its entry directly, by the same rules.
            route = CompileOperationRoute(table.Routing, table.Backend, surfaceRoute, operation.RoutingKey, routes.Default);
        }

        return route.Target ?? throw new InvalidOperationException(
            $"The routing table cannot route {(operation is null ? $"an unmatched {surface} request" : operation.Id)}: {route.Failure}.");
    }

    /// <summary>Returns the effective routing table under the current configuration.</summary>
    /// <remarks>
    /// The snapshot is built with the same table <see cref="Resolve"/> uses, so every row names the
    /// target a request for that operation is forwarded to.
    /// </remarks>
    /// <returns>
    /// The default target, the <c>Backend:TimeoutSeconds</c> the table applies, every configured target
    /// and one row per catalogue operation.
    /// </returns>
    /// <exception cref="OptionsValidationException">The configuration fails validation and no table has been published yet.</exception>
    /// <exception cref="InvalidOperationException">The configuration cannot be bound and no table has been published yet.</exception>
    public RoutingSnapshot Describe() => CurrentTable().Snapshot;

    private static SurfaceRoute SurfaceRouteFor(Surface surface) => surface switch
    {
        Surface.Rest => RestRoute,
        Surface.Soap => SoapRoute,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Only the Rest and Soap surfaces are routed."),
    };

    private CompiledTable CurrentTable()
    {
        // Unchanged options: the published table, without taking the lock.
        var published = Volatile.Read(ref _table);
        if (published is not null && IsCompiledFromCurrentOptions(published))
        {
            return published;
        }

        // Each rebuild reads the options while holding the lock, after every earlier publication, so a
        // table is replaced only by one compiled from options at least as new as its own. A caller that
        // read older options before the lock never publishes them, and a rejected reload keeps the
        // newest valid table in service.
        lock (_publication)
        {
            published = _table;
            if (ReadCurrent(_routing, published) is not { } routing
                || ReadCurrent(_backend, published) is not { } backend)
            {
                // ReadCurrent returns null only while a table is in service.
                return published!;
            }

            if (published is not null && published.IsCompiledFrom(routing, backend))
            {
                return published;
            }

            var table = Compile(routing, backend);
            Volatile.Write(ref _table, table);

            if (_reportedFailure is not null)
            {
                _reportedFailure = null;
                LogReloadAccepted(_logger);
            }

            return table;
        }
    }

    // A rejected reload makes CurrentValue throw until the next reload; that is not a match here, and
    // it is reported under the lock, in order with the publications.
    private bool IsCompiledFromCurrentOptions(CompiledTable table)
    {
        try
        {
            return table.IsCompiledFrom(_routing.CurrentValue, _backend.CurrentValue);
        }
        catch (Exception exception) when (IsRejectedReload(exception))
        {
            return false;
        }
    }

    // Returns the monitor's current options. A reload whose options cannot be bound or fail validation
    // is reported and gives null while a table is in service; before one is published the exception
    // propagates unchanged, so invalid start-up configuration stays fatal.
    private TOptions? ReadCurrent<TOptions>(IOptionsMonitor<TOptions> monitor, CompiledTable? inService)
        where TOptions : class
    {
        try
        {
            return monitor.CurrentValue;
        }
        catch (Exception exception) when (inService is not null && IsRejectedReload(exception))
        {
            ReportRejectedReload(typeof(TOptions), exception);
            return null;
        }
    }

    // The options factory caches a failed creation, so CurrentValue rethrows the same exception until
    // the next reload: OptionsValidationException from a validator, InvalidOperationException from the
    // configuration binder (a value that does not convert to its option's type). Nothing broader is a
    // configuration failure, and neither is an ObjectDisposedException from a container shutting down.
    private static bool IsRejectedReload(Exception exception) =>
        exception is OptionsValidationException or (InvalidOperationException and not ObjectDisposedException);

    // Called only while _publication is held. Logs a rejected reload once per distinct failure, and keeps
    // the failure as the key that suppresses repeats until a valid reload clears it. The message names the
    // failing keys as the validator or the binder words them; the options themselves are never written.
    // RoutingOptionsValidator builds its failures from configuration paths and fixed phrases and never
    // shows a base URL, or a key or value that may hold user information. The binder's message quotes the
    // value it could not convert, so a message holding an at-sign, which user information always needs,
    // is replaced by a fixed phrase; the key then tells such failures apart by the message's hash alone, so
    // neither the log entry nor the key holds a credential.
    private void ReportRejectedReload(Type optionsType, Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        var message = exception.Message.ReplaceLineEndings(" ");
        var failure = RoutingOptionsValidator.HoldsAtSign(message)
            ? "the message is not shown because it may contain user information"
            : message;
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{optionsType.FullName}|{exceptionType}|{failure}|{message.GetHashCode(StringComparison.Ordinal)}");
        if (!string.Equals(_reportedFailure, key, StringComparison.Ordinal))
        {
            _reportedFailure = key;
            LogReloadRejected(_logger, optionsType.Name, exceptionType, failure);
        }
    }

    private CompiledTable Compile(RoutingOptions routing, BackendOptions backend)
    {
        var operations = new List<RoutedOperation>(_catalogue.Operations.Count);
        var rest = CompileSurface(routing, backend, RestRoute, operations);
        var soap = CompileSurface(routing, backend, SoapRoute, operations);

        var snapshot = new RoutingSnapshot(
            routing.DefaultTarget ?? string.Empty,
            backend.TimeoutSeconds,
            DescribeTargets(routing, backend),
            operations.AsReadOnly());

        return new CompiledTable(routing, backend, rest, soap, snapshot);
    }

    private SurfaceRoutes CompileSurface(
        RoutingOptions routing,
        BackendOptions backend,
        SurfaceRoute surface,
        List<RoutedOperation> described)
    {
        var defaultRoute = CompileRoute(routing, backend, surface, DefaultTargetPath, routing.DefaultTarget, DefaultTargetUsage);
        var byRoutingKey = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in _catalogue.GetBySurface(surface.Surface))
        {
            var route = CompileOperationRoute(routing, backend, surface, operation.RoutingKey, defaultRoute);
            byRoutingKey.TryAdd(operation.RoutingKey, route);
            described.Add(new RoutedOperation(operation.Id, surface.Section, operation.RoutingKey, route.TargetName));
        }

        return new SurfaceRoutes(defaultRoute, byRoutingKey.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static Route CompileOperationRoute(
        RoutingOptions routing,
        BackendOptions backend,
        SurfaceRoute surface,
        string routingKey,
        Route defaultRoute)
    {
        // The binder or a test may leave Operations null; that lists nothing.
        if (routing.Operations is not { } operations
            || !operations.TryGetTargetName(surface.Surface, routingKey, out var configuredKey, out var targetName))
        {
            return defaultRoute;
        }

        var entry = $"{OperationsPath}:{surface.Section}:{configuredKey}";
        return CompileRoute(routing, backend, surface, entry, targetName, $"serves {entry}");
    }

    // An entry that cannot be resolved becomes a failed route that names it in the validator's
    // wording, so only the requests that entry serves fail and no other target is substituted. Like the
    // validator's, the failure quotes no base URL and no target name that may hold user information.
    private static Route CompileRoute(
        RoutingOptions routing,
        BackendOptions backend,
        SurfaceRoute surface,
        string entry,
        string? targetName,
        string usage)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return Route.Failed(targetName ?? string.Empty, $"{entry} is empty");
        }

        if (!routing.TryGetTarget(targetName, out var name, out var target))
        {
            return Route.Failed(targetName, RoutingOptionsValidator.NamesUndefinedTarget(entry, targetName));
        }

        var url = surface.UrlOf(target);
        if (string.IsNullOrWhiteSpace(url))
        {
            return Route.Failed(name, $"{TargetsPath}:{name} has no {surface.UrlName} but {usage}");
        }

        // The failure names the entry and the rule it breaks, never the URL, which may hold a credential.
        if (!RoutingOptionsValidator.TryParseBaseUrl(url, out var baseUrl))
        {
            return Route.Failed(
                name,
                $"{TargetsPath}:{name}:{surface.UrlName} {RoutingOptionsValidator.DescribeBaseUrlProblem(url)}");
        }

        // A non-positive deadline would make the forwarder cancel every call at once or throw, so it is
        // refused like an unusable URL rather than returned.
        var seconds = target.TimeoutSeconds ?? backend.TimeoutSeconds;
        if (seconds <= 0)
        {
            var key = target.TimeoutSeconds is null
                ? BackendOptions.TimeoutKey
                : $"{TargetsPath}:{name}:{nameof(RoutingTarget.TimeoutSeconds)}";
            return Route.Failed(name, string.Create(CultureInfo.InvariantCulture, $"{key} must be greater than 0 (was {seconds})"));
        }

        return new Route(name, new ResolvedTarget(name, baseUrl, TimeSpan.FromSeconds(seconds)), Failure: null);
    }

    // The configuration binder never binds a target section to a null entry: an empty or null-valued
    // section binds to a target with nothing set, which validation rejects as empty. A null entry can come
    // only from a dictionary built in code; RoutingOptions.TryGetTarget treats it as not found, so it is
    // not listed either.
    private static ReadOnlyCollection<RoutingTargetInfo> DescribeTargets(RoutingOptions routing, BackendOptions backend)
    {
        if (routing.Targets is not { Count: > 0 } targets)
        {
            return ReadOnlyCollection<RoutingTargetInfo>.Empty;
        }

        return targets
            .Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RoutingTargetInfo(
                pair.Key,
                AsConfigured(pair.Value.RestBaseUrl),
                AsConfigured(pair.Value.SoapBaseUrl),
                pair.Value.TimeoutSeconds ?? backend.TimeoutSeconds))
            .ToList()
            .AsReadOnly();
    }

    // The validator treats a blank URL as absent, so the snapshot does too.
    private static string? AsConfigured(string? url) => string.IsNullOrWhiteSpace(url) ? null : url;

    [LoggerMessage(
        EventId = 1101,
        EventName = "RoutingReloadRejected",
        Level = LogLevel.Error,
        Message = "Rejected the reloaded {OptionsType} configuration; routing continues with the last valid table. {ExceptionType}: {Failure}")]
    private static partial void LogReloadRejected(ILogger logger, string optionsType, string exceptionType, string failure);

    [LoggerMessage(
        EventId = 1102,
        EventName = "RoutingReloadAccepted",
        Level = LogLevel.Information,
        Message = "The reloaded configuration is valid again; routing uses the rebuilt table")]
    private static partial void LogReloadAccepted(ILogger logger);

    /// <summary>One surface's routing section and base URL.</summary>
    /// <param name="Surface">The catalogue surface.</param>
    /// <param name="Section">The section under <c>Routing:Operations</c>, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="UrlName">The target property holding the surface's base URL.</param>
    /// <param name="UrlOf">Reads that property from a target.</param>
    private sealed record SurfaceRoute(Surface Surface, string Section, string UrlName, Func<RoutingTarget, string?> UrlOf);

    /// <summary>The outcome of resolving one entry: a target, or the failure naming the entry.</summary>
    /// <param name="TargetName">The target name the entry resolves to, as configured or as written when undefined.</param>
    /// <param name="Target">The resolved target; null when the entry cannot be resolved.</param>
    /// <param name="Failure">Why the entry cannot be resolved; null when <paramref name="Target"/> is set.</param>
    private sealed record Route(string TargetName, ResolvedTarget? Target, string? Failure)
    {
        public static Route Failed(string targetName, string failure) => new(targetName, Target: null, failure);
    }

    /// <summary>One surface's compiled routes.</summary>
    /// <param name="Default">The route of <c>Routing:DefaultTarget</c> for this surface.</param>
    /// <param name="ByRoutingKey">The route of every catalogue operation of the surface, keyed case-insensitively.</param>
    private sealed record SurfaceRoutes(Route Default, FrozenDictionary<string, Route> ByRoutingKey);

    /// <summary>The routes and snapshot compiled from one pair of options instances.</summary>
    /// <param name="Routing">The routing options instance the table was compiled from.</param>
    /// <param name="Backend">The backend options instance the table was compiled from.</param>
    /// <param name="Rest">The REST routes.</param>
    /// <param name="Soap">The SOAP routes.</param>
    /// <param name="Snapshot">The effective table <see cref="Describe"/> returns.</param>
    private sealed record CompiledTable(
        RoutingOptions Routing,
        BackendOptions Backend,
        SurfaceRoutes Rest,
        SurfaceRoutes Soap,
        RoutingSnapshot Snapshot)
    {
        public SurfaceRoutes For(Surface surface) => surface == Surface.Rest ? Rest : Soap;

        // Options instances are compared by reference: a reload always yields new instances.
        public bool IsCompiledFrom(RoutingOptions routing, BackendOptions backend) =>
            ReferenceEquals(Routing, routing) && ReferenceEquals(Backend, backend);
    }
}

/// <summary>
/// Publishes the routing table of the start-up options while the host starts, after start-up validation
/// and before any listener accepts a request.
/// </summary>
/// <remarks>
/// The host runs every <see cref="IHostedLifecycleService.StartingAsync"/> after the
/// <c>ValidateOnStart</c> checks and before any hosted service's <see cref="IHostedService.StartAsync"/>,
/// the one that starts Kestrel included, so invalid start-up configuration still fails in those checks,
/// with their message. Without this step the first request would compile the first table, and a reload
/// rejected before that request would leave no valid table to keep in service.
/// </remarks>
internal sealed class OperationRouterStartup : IHostedLifecycleService
{
    private readonly OperationRouter _router;

    /// <summary>Creates the start-up step; <c>Program.cs</c> registers it as a hosted service.</summary>
    /// <param name="router">The router whose table is published.</param>
    /// <exception cref="ArgumentNullException"><paramref name="router"/> is null.</exception>
    public OperationRouterStartup(OperationRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// <summary>Publishes the table of the start-up options.</summary>
    /// <param name="cancellationToken">Signals that start-up is abandoned.</param>
    /// <returns>A completed task; the table is compiled synchronously.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="OptionsValidationException">The start-up configuration fails validation.</exception>
    /// <exception cref="InvalidOperationException">The start-up configuration cannot be bound.</exception>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _router.Initialize();
        return Task.CompletedTask;
    }

    // The table needs no other step of the host's lifecycle.

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
