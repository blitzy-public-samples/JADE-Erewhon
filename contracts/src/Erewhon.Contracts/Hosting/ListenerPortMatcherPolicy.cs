using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Erewhon.Contracts.Hosting;

/// <summary>
/// The three Kestrel listeners run by each Erewhon HTTP service (the facade and the JADE stand-in).
/// Every endpoint belongs to exactly one of them and is dispatched only for requests that arrived
/// on that listener's socket.
/// </summary>
public enum ListenerKind
{
    /// <summary>
    /// The REST listener: the address of the JADE <c>ErewhonREST</c> application (65000 by default).
    /// </summary>
    Rest,

    /// <summary>
    /// The SOAP listener: the address of the JADE <c>WebServiceHTTP</c> application (21001 by default).
    /// </summary>
    Soap,

    /// <summary>
    /// The internal admin listener (9090 by default) carrying the unauthenticated health, info,
    /// reset, shutdown and OpenAPI endpoints. It is never published to callers.
    /// </summary>
    Admin,
}

/// <summary>
/// Endpoint metadata naming the listener an endpoint belongs to. It is added by
/// <see cref="ListenerRoutingExtensions.RequireListener{TBuilder}(TBuilder, ListenerKind)"/> and read
/// by <see cref="ListenerPortMatcherPolicy"/>, which compares the listener's configured port with
/// the local port of the connection. Nothing the caller sends, the <c>Host</c> header included,
/// takes part in that comparison.
/// </summary>
/// <param name="listener">The listener the endpoint is served on.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="listener"/> is not a defined <see cref="ListenerKind"/>.</exception>
public sealed class ListenerMetadata(ListenerKind listener)
{
    // An undefined kind is rejected here, at mapping time, so the matcher policy never meets one per request.

    /// <summary>The listener the endpoint is served on.</summary>
    public ListenerKind Listener { get; } = Enum.IsDefined(listener)
        ? listener
        : throw new ArgumentOutOfRangeException(nameof(listener), listener, "Unknown listener kind.");

    /// <summary>Returns the listener in the form shown by routing diagnostics, for example <c>Listener: Rest</c>.</summary>
    public override string ToString() => $"Listener: {Listener}";
}

/// <summary>
/// The local port of each listener, as <see cref="ListenerPortMatcherPolicy"/> matches it against
/// <see cref="ConnectionInfo.LocalPort"/>. A value of 0 means "not configured"; start-up validation
/// registered by <see cref="ListenerRoutingExtensions.AddListenerPortMatcherPolicy(IServiceCollection)"/>
/// rejects it.
/// </summary>
/// <remarks>
/// <para>
/// This type carries no defaults, because each host binds its own configuration keys: the facade
/// reads <c>Facade:RestPort</c>, <c>Facade:SoapPort</c> and <c>Facade:AdminPort</c>, the stand-in its
/// <c>StandIn</c> port keys, and the local-process runner moves the stand-in to other ports.
/// The values must be exactly the ports the host passes to Kestrel in <c>ConfigureKestrel</c>,
/// for example:
/// </para>
/// <code>
/// services.AddOptions&lt;ListenerPorts&gt;().Configure&lt;IConfiguration&gt;((p, c) =&gt;
/// {
///     p.RestPort = c.GetValue("Facade:RestPort", 65000);
///     p.SoapPort = c.GetValue("Facade:SoapPort", 21001);
///     p.AdminPort = c.GetValue("Facade:AdminPort", 9090);
/// });
/// services.AddListenerPortMatcherPolicy();
/// </code>
/// <para>
/// A section whose keys are named <c>RestPort</c>, <c>SoapPort</c> and <c>AdminPort</c> can also be
/// bound directly with <c>services.Configure&lt;ListenerPorts&gt;(configuration.GetSection("StandIn"))</c>.
/// </para>
/// </remarks>
public sealed class ListenerPorts
{
    /// <summary>The local port of the REST listener.</summary>
    public int RestPort { get; set; }

    /// <summary>The local port of the SOAP listener.</summary>
    public int SoapPort { get; set; }

    /// <summary>The local port of the admin listener.</summary>
    public int AdminPort { get; set; }

    /// <summary>Returns the configured local port of <paramref name="listener"/>.</summary>
    /// <param name="listener">The listener whose port is wanted.</param>
    /// <returns>The configured port, or 0 when it has not been configured.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="listener"/> is not a defined <see cref="ListenerKind"/>.</exception>
    public int PortFor(ListenerKind listener) => listener switch
    {
        ListenerKind.Rest => RestPort,
        ListenerKind.Soap => SoapPort,
        ListenerKind.Admin => AdminPort,
        _ => throw new ArgumentOutOfRangeException(nameof(listener), listener, "Unknown listener kind."),
    };
}

/// <summary>
/// Registration helpers for listener isolation: <see cref="RequireListener{TBuilder}(TBuilder, ListenerKind)"/>
/// on every endpoint and <see cref="AddListenerPortMatcherPolicy(IServiceCollection)"/> once per host.
/// </summary>
public static class ListenerRoutingExtensions
{
    /// <summary>
    /// Restricts the endpoints built by <paramref name="builder"/> to requests that arrive on the
    /// socket of <paramref name="listener"/>, whatever <c>Host</c> header they carry.
    /// </summary>
    /// <remarks>
    /// Works on a single endpoint, on a <see cref="RouteGroupBuilder"/> and on the builder returned by
    /// <c>MapOpenApi()</c>. When both a group and one of its endpoints name a listener, the endpoint's
    /// own choice wins: group conventions run first, and the policy reads the last
    /// <see cref="ListenerMetadata"/> through <c>GetMetadata&lt;ListenerMetadata&gt;()</c>.
    /// </remarks>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint, group or convention builder to restrict.</param>
    /// <param name="listener">The listener the endpoints are served on.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="listener"/> is not a defined <see cref="ListenerKind"/>.</exception>
    public static TBuilder RequireListener<TBuilder>(this TBuilder builder, ListenerKind listener)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var metadata = new ListenerMetadata(listener);
        builder.Add(endpoint => endpoint.Metadata.Add(metadata));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="ListenerPortMatcherPolicy"/> and the start-up validation of
    /// <see cref="ListenerPorts"/>. The call is idempotent. It configures no port values; the host
    /// configures <see cref="ListenerPorts"/> itself, from the same values it gives Kestrel.
    /// </summary>
    /// <remarks>
    /// Start-up fails with an <see cref="OptionsValidationException"/> when any port is outside
    /// 1 to 65535 or two listeners share a port. An unset port would leave every endpoint of its
    /// listener unreachable, and a shared port would break isolation and leave the per-listener
    /// catch-all endpoints ambiguous.
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddListenerPortMatcherPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, ListenerPortMatcherPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ListenerPorts>, ListenerPortsValidator>());
        services.AddOptions<ListenerPorts>().ValidateOnStart();
        return services;
    }

    // An IValidateOptions implementation rather than OptionsBuilder.Validate(predicate, message):
    // the messages must name the offending property and value, which a fixed message cannot, and
    // TryAddEnumerable keeps repeated registration from reporting every failure twice.
    private sealed class ListenerPortsValidator : IValidateOptions<ListenerPorts>
    {
        private const int MinimumPort = 1;

        public ValidateOptionsResult Validate(string? name, ListenerPorts options)
        {
            ArgumentNullException.ThrowIfNull(options);

            (string Property, int Port)[] ports =
            [
                (nameof(ListenerPorts.RestPort), options.RestPort),
                (nameof(ListenerPorts.SoapPort), options.SoapPort),
                (nameof(ListenerPorts.AdminPort), options.AdminPort),
            ];

            var failures = new List<string>();
            foreach (var (property, port) in ports)
            {
                if (!IsInRange(port))
                {
                    failures.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{nameof(ListenerPorts)}: {property} ({port}) must be between {MinimumPort} and {IPEndPoint.MaxPort}"));
                }
            }

            // Out-of-range ports are already reported above, so only valid ports are compared.
            for (var first = 0; first < ports.Length; first++)
            {
                for (var second = first + 1; second < ports.Length; second++)
                {
                    if (IsInRange(ports[first].Port) && ports[first].Port == ports[second].Port)
                    {
                        failures.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{nameof(ListenerPorts)}: {ports[first].Property} and {ports[second].Property} are both {ports[first].Port}; each listener needs its own port"));
                    }
                }
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static bool IsInRange(int port) => port is >= MinimumPort and <= IPEndPoint.MaxPort;
    }
}

/// <summary>
/// Endpoint selector policy that keeps a candidate endpoint only when the configured port of its
/// <see cref="ListenerMetadata"/> listener equals <see cref="ConnectionInfo.LocalPort"/>, the port
/// of the socket the request arrived on.
/// </summary>
/// <remarks>
/// <para>
/// Dispatch depends on the socket alone. The policy never reads the <c>Host</c> header, any other
/// request header or a forwarded address, so a caller cannot reach another listener's endpoints by
/// naming its port. For example, on the REST socket a request to <c>/admin/shutdown</c> or
/// <c>/openapi/v1.json</c> carrying <c>Host: facade:9090</c> leaves only the REST catch-all valid
/// and is answered (forwarded) by it; a REST request whose <c>Host</c> has no port, or names another
/// port, is dispatched to its operation as usual; and a REST path sent to the SOAP socket is handled
/// by the SOAP catch-all. <see cref="ConnectionInfo.LocalPort"/> is the port inside the container,
/// so publishing a listener on another host port does not change matching.
/// </para>
/// <para>
/// The policy is fail-closed: an endpoint without <see cref="ListenerMetadata"/> is never selected,
/// and neither is one whose listener has no configured port. The in-memory test server reports a
/// local port of 0, which therefore matches no listener; isolation can be observed only on real
/// Kestrel sockets.
/// </para>
/// </remarks>
public sealed class ListenerPortMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    private readonly IOptionsMonitor<ListenerPorts> _ports;

    /// <summary>Creates the policy over the host's listener ports.</summary>
    /// <param name="ports">The listener ports, read afresh for every request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ports"/> is <see langword="null"/>.</exception>
    public ListenerPortMatcherPolicy(IOptionsMonitor<ListenerPorts> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
    }

    /// <summary>
    /// Orders this policy among the endpoint selector policies. The value has no effect on which
    /// endpoint is selected.
    /// </summary>
    public override int Order => -50;

    /// <summary>
    /// Returns <see langword="true"/> for every endpoint set, so that endpoints lacking
    /// <see cref="ListenerMetadata"/> are seen and rejected rather than skipped.
    /// </summary>
    /// <param name="endpoints">The endpoints of one routing state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return true;
    }

    /// <summary>
    /// Invalidates every candidate whose listener is missing, unconfigured or bound to a port other
    /// than the connection's local port.
    /// </summary>
    /// <param name="httpContext">The request being routed.</param>
    /// <param name="candidates">The candidate endpoints for the request path and method.</param>
    /// <returns>A completed task; the policy does no asynchronous work.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpContext"/> or <paramref name="candidates"/> is <see langword="null"/>.</exception>
    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(candidates);

        var ports = _ports.CurrentValue;
        var localPort = httpContext.Connection.LocalPort;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var metadata = candidates[i].Endpoint.Metadata.GetMetadata<ListenerMetadata>();
            if (metadata is null)
            {
                candidates.SetValidity(i, false);
                continue;
            }

            // A port of 0 means "not configured" and must never match the in-memory server's local port of 0.
            var port = ports.PortFor(metadata.Listener);
            if (port <= 0 || port != localPort)
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }
}
