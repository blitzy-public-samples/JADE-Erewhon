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
/// <param name="Targets">Every configured target, referenced or not, in ordinal name order.</param>
/// <param name="Operations">
/// One row per catalogue operation: the REST operations in catalogue order, then the SOAP ones.
/// </param>
public sealed record RoutingSnapshot(
    string DefaultTarget,
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
/// instance into an immutable table that is swapped without locks, which makes the router safe for
/// concurrent requests as a singleton. A reload that fails validation keeps the last valid table in
/// service.
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

    // Read with Volatile.Read and replaced whole with Volatile.Write; never mutated.
    private CompiledTable? _table;

    // The rejected reload last logged, so a failure set is logged once rather than per request.
    private string? _reportedFailure;

    /// <summary>Creates the router; <c>Program.cs</c> registers it as a singleton.</summary>
    /// <param name="routing">The routing table, validated by <see cref="RoutingOptionsValidator"/>.</param>
    /// <param name="backend">The <c>Backend</c> section, whose <c>TimeoutSeconds</c> is the default deadline.</param>
    /// <param name="catalogue">The operation catalogue, the only source of operations.</param>
    /// <param name="logger">Receives one error per rejected reload and one note when a reload is valid again.</param>
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
    /// configuration path. No other target is substituted.
    /// </exception>
    /// <exception cref="OptionsValidationException">The configuration is invalid and no valid table was ever built.</exception>
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
    /// <returns>The default target, every configured target and one row per catalogue operation.</returns>
    /// <exception cref="OptionsValidationException">The configuration is invalid and no valid table was ever built.</exception>
    public RoutingSnapshot Describe() => CurrentTable().Snapshot;

    private static SurfaceRoute SurfaceRouteFor(Surface surface) => surface switch
    {
        Surface.Rest => RestRoute,
        Surface.Soap => SoapRoute,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Only the Rest and Soap surfaces are routed."),
    };

    private CompiledTable CurrentTable()
    {
        RoutingOptions routing;
        BackendOptions backend;
        try
        {
            routing = _routing.CurrentValue;
            backend = _backend.CurrentValue;
        }
        catch (OptionsValidationException exception)
        {
            // ValidateOnStart guarantees a valid first read, so only a reload can reach this with a
            // table in service.
            var lastValid = Volatile.Read(ref _table);
            if (lastValid is null)
            {
                throw;
            }

            ReportRejectedReload(exception);
            return lastValid;
        }

        var table = Volatile.Read(ref _table);
        if (table is not null && ReferenceEquals(table.Routing, routing) && ReferenceEquals(table.Backend, backend))
        {
            return table;
        }

        // Concurrent rebuilds produce equivalent tables, and a table written from an older options
        // instance is replaced on the next call because its references no longer match.
        table = Compile(routing, backend);
        Volatile.Write(ref _table, table);

        if (Interlocked.Exchange(ref _reportedFailure, null) is not null)
        {
            LogReloadAccepted(_logger);
        }

        return table;
    }

    // Logs a rejected reload once per distinct failure set, and keeps that set as the key that suppresses
    // repeats until a valid reload clears it. The failures can be logged verbatim: RoutingOptionsValidator
    // builds every one from configuration paths and fixed phrases, never quoting a base URL or a target
    // name that may hold user information, and the Backend check's message is fixed text, so neither the
    // log entry nor the key holds a credential.
    private void ReportRejectedReload(OptionsValidationException exception)
    {
        var failures = string.Join("; ", exception.Failures);
        var key = $"{exception.OptionsType.FullName}|{failures}";
        if (!string.Equals(Interlocked.Exchange(ref _reportedFailure, key), key, StringComparison.Ordinal))
        {
            LogReloadRejected(_logger, exception.OptionsType.Name, failures);
        }
    }

    private CompiledTable Compile(RoutingOptions routing, BackendOptions backend)
    {
        var operations = new List<RoutedOperation>(_catalogue.Operations.Count);
        var rest = CompileSurface(routing, backend, RestRoute, operations);
        var soap = CompileSurface(routing, backend, SoapRoute, operations);

        var snapshot = new RoutingSnapshot(
            routing.DefaultTarget ?? string.Empty,
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

    // A target section that bound to a null entry is not a target (RoutingOptions.TryGetTarget treats
    // it as not found), so it is not listed.
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
        Message = "Rejected the reloaded {OptionsType} configuration; routing continues with the last valid table. Failures: {Failures}")]
    private static partial void LogReloadRejected(ILogger logger, string optionsType, string failures);

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
    }
}
