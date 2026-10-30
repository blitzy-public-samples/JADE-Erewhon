using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

// ---- Log level floors ---------------------------------------------------------------------------
// A hosting helper shared by the facade's and the stand-in's Program.cs, which both floor the framework
// categories whose lines print what a caller sent. It sits with the listener policy, the other hosting
// helper the two hosts share.
/// <summary>
/// Minimum log levels that hold for a group of logging categories whatever the <c>Logging</c>
/// configuration says. The facade and the stand-in use them for the framework categories whose lines
/// print what a caller sent, such as the request path, because REST path segments carry agent and
/// client names.
/// </summary>
/// <remarks>
/// <para>
/// A floor amends the bound <see cref="LoggerFilterOptions"/> after every configuration source has
/// been applied, and again each time the configuration reloads. From then on a provider logs a level of
/// a category under the floor only when the level is at least the floor's minimum and the rules and
/// global minimum level, as configured, allow that provider to log it. No setting can lower a category
/// under the floor: not a <c>Logging:LogLevel</c> entry for the same or a longer category, not a
/// provider's own <c>Logging:&lt;provider&gt;:LogLevel</c> entry, not a wildcard rule and not an
/// <c>AddFilter</c> call. A setting stricter than the floor keeps the category as strict as it was, and
/// every category outside the floor logs exactly as configured.
/// </para>
/// <para>
/// A category is under a floor when its name starts with the floor's prefix, ignoring case, which is
/// how Microsoft.Extensions.Logging matches a rule to a category. The configured rules are evaluated as
/// that library selects them: a provider's own rule, named by its full type name or by its
/// <see cref="ProviderAliasAttribute"/> alias, beats every rule without a provider, a longer category
/// beats a shorter one, and on a tie the later rule wins.
/// </para>
/// </remarks>
public static class LogLevelFloors
{
    private const char Wildcard = '*';

    // Provider aliases by provider full type name, resolved once per provider; null when it has none.
    private static readonly ConcurrentDictionary<string, string?> ProviderAliases = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps every category whose name starts with <paramref name="categoryPrefix"/> at
    /// <paramref name="minimum"/> or above, for every logging provider, whatever the <c>Logging</c>
    /// configuration says.
    /// </summary>
    /// <param name="logging">The host's logging builder.</param>
    /// <param name="categoryPrefix">
    /// The category name, or the start of the category names, the floor covers, for example
    /// <c>Microsoft.AspNetCore.Routing</c>. It is matched ignoring case and cannot contain a wildcard.
    /// </param>
    /// <param name="minimum">
    /// The lowest level a category under the floor logs; <see cref="LogLevel.None"/> turns the
    /// categories off.
    /// </param>
    /// <returns><paramref name="logging"/>, so further calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logging"/> or <paramref name="categoryPrefix"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="categoryPrefix"/> is empty, white space or contains <c>*</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimum"/> is not a defined <see cref="LogLevel"/>.</exception>
    public static ILoggingBuilder AddLogLevelFloor(this ILoggingBuilder logging, string categoryPrefix, LogLevel minimum)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryPrefix);
        if (categoryPrefix.Contains(Wildcard))
        {
            throw new ArgumentException("A log level floor's category prefix cannot contain the wildcard '*'.", nameof(categoryPrefix));
        }

        if (!Enum.IsDefined(minimum))
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "Unknown log level.");
        }

        // Post-configuration runs after every Configure step, the binding of the Logging section
        // included, and is repeated whenever the options are rebuilt after a configuration reload.
        logging.Services.PostConfigure<LoggerFilterOptions>(options => Apply(options, categoryPrefix, minimum));
        return logging;
    }

    // Amends the rules so that, for every provider and every category under the prefix, whichever rule
    // Microsoft.Extensions.Logging selects allows a level only when it is at least the minimum and the
    // rules and global minimum level as they stood before this call allowed it.
    private static void Apply(LoggerFilterOptions options, string categoryPrefix, LogLevel minimum)
    {
        IList<LoggerFilterRule> rules = options.Rules;
        LoggerFilterRule[] configured = [.. rules];
        LogLevel configuredMinLevel = options.MinLevel;

        // A rule longer than the prefix that can match a category under it beats the floor rules on the
        // prefix itself. It keeps its level, which still decides every category outside the prefix, and
        // its filter also requires the minimum for the categories under the prefix.
        for (int i = 0; i < rules.Count; i++)
        {
            LoggerFilterRule rule = rules[i];
            if (IsLongerThan(rule, categoryPrefix) && CanMatchUnder(rule, categoryPrefix))
            {
                rules[i] = new LoggerFilterRule(
                    rule.ProviderName, rule.CategoryName, rule.LogLevel, WithFloor(rule.Filter, categoryPrefix, minimum));
            }
        }

        // Every other rule that matches a category under the prefix loses to a floor rule of the same
        // provider on the prefix, appended last so that it also wins every tie, and so does the global
        // minimum level when no rule matches. Each floor rule passes a level only when the configured
        // selection for the actual provider and category allows it, so appending one for every provider
        // name the rules use, and one for the rules without a provider, changes nothing else.
        Func<string?, string?, LogLevel, bool> floor = (provider, category, level) =>
            level >= minimum && ConfiguredAllows(configured, configuredMinLevel, provider, category, level);
        rules.Add(new LoggerFilterRule(null, categoryPrefix, minimum, floor));
        var flooredProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (LoggerFilterRule rule in configured)
        {
            if (rule.ProviderName is { } provider && flooredProviders.Add(provider))
            {
                rules.Add(new LoggerFilterRule(provider, categoryPrefix, minimum, floor));
            }
        }
    }

    // A longer rule's filter, which in addition lets a category under the prefix log no level below the
    // minimum. The rule's own level is checked before the filter runs.
    private static Func<string?, string?, LogLevel, bool> WithFloor(
        Func<string?, string?, LogLevel, bool>? filter, string categoryPrefix, LogLevel minimum) =>
        (provider, category, level) =>
            (category is null || !category.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase) || level >= minimum)
            && (filter is null || filter(provider, category, level));

    // Whether the configured rules and global minimum level let the provider with this full type name
    // log the level of the category, decided as Microsoft.Extensions.Logging decides it: the selected
    // rule's level (none: every level) or, without a matching rule, the global minimum level, where a
    // level above Critical turns the provider off for the category, and then the selected rule's filter.
    // Filters are invoked on every call and their results are never cached, since they may hold state.
    private static bool ConfiguredAllows(
        LoggerFilterRule[] configured, LogLevel configuredMinLevel, string? provider, string? category, LogLevel level)
    {
        string? alias = provider is null ? null : ProviderAliases.GetOrAdd(provider, ResolveAlias);
        LoggerFilterRule? selected = Select(configured, provider, alias, category ?? string.Empty);
        LogLevel? allowed = selected is null ? configuredMinLevel : selected.LogLevel;
        if (allowed is { } lowest && (lowest > LogLevel.Critical || level < lowest))
        {
            return false;
        }

        return selected?.Filter is not { } filter || filter(provider, category, level);
    }

    // The rule Microsoft.Extensions.Logging selects for a provider and a category. A rule applies to the
    // provider when it names the provider's full type name or its alias.
    private static LoggerFilterRule? Select(LoggerFilterRule[] rules, string? provider, string? alias, string category)
    {
        LoggerFilterRule? selected = null;
        foreach (LoggerFilterRule rule in rules)
        {
            if (IsBetter(rule, selected, provider, category)
                || (alias is not null && IsBetter(rule, selected, alias, category)))
            {
                selected = rule;
            }
        }

        return selected;
    }

    // The alias a provider type declares with ProviderAliasAttribute (not inherited), which rules may use
    // instead of its full type name. The type is looked up by that name in the loaded assemblies, which
    // hold every provider a logger was created from. A type found nowhere, or declaring no alias or an
    // empty one, has none.
    private static string? ResolveAlias(string providerFullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(providerFullName, throwOnError: false);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or BadImageFormatException)
            {
                // A name the assembly cannot parse, or whose type arguments it cannot load, is not its type.
                continue;
            }

            if (type is not null)
            {
                string? alias = type.GetCustomAttribute<ProviderAliasAttribute>(inherit: false)?.Alias;
                return string.IsNullOrEmpty(alias) ? null : alias;
            }
        }

        return null;
    }

    private static bool IsBetter(LoggerFilterRule rule, LoggerFilterRule? current, string? provider, string category)
    {
        if (rule.ProviderName != null && !string.Equals(rule.ProviderName, provider, StringComparison.Ordinal))
        {
            return false;
        }

        if (rule.CategoryName is { } name && !Matches(name, category))
        {
            return false;
        }

        // A provider's own rule beats every rule without a provider, whatever their categories.
        if (current?.ProviderName != null)
        {
            if (rule.ProviderName == null)
            {
                return false;
            }
        }
        else if (rule.ProviderName != null)
        {
            return true;
        }

        // Between rules of the same kind a longer category wins, and on a tie the later rule.
        return current?.CategoryName is not { } currentName
            || (rule.CategoryName is { } ruleName && ruleName.Length >= currentName.Length);
    }

    // A rule's category matches when the category starts with the part before the single wildcard (the
    // whole name without one) and ends with the part after it, ignoring case. Microsoft.Extensions.Logging
    // rejects a name with more than one wildcard itself when it selects rules for a logger, so such a
    // rule never matches here.
    private static bool Matches(string name, string category)
    {
        int wildcard = name.IndexOf(Wildcard);
        if (wildcard < 0)
        {
            return category.StartsWith(name, StringComparison.OrdinalIgnoreCase);
        }

        if (name.IndexOf(Wildcard, wildcard + 1) >= 0)
        {
            return false;
        }

        return category.AsSpan().StartsWith(name.AsSpan(0, wildcard), StringComparison.OrdinalIgnoreCase)
            && category.AsSpan().EndsWith(name.AsSpan(wildcard + 1), StringComparison.OrdinalIgnoreCase);
    }

    // Selection prefers the longer category name, wildcard included; a rule without a category is shortest.
    private static bool IsLongerThan(LoggerFilterRule rule, string categoryPrefix) =>
        rule.CategoryName is { } name && name.Length > categoryPrefix.Length;

    // Whether some category under the prefix can match the rule: the part of its name before the wildcard
    // (all of it without one, nothing without a category) and the prefix must be prefixes of one name.
    private static bool CanMatchUnder(LoggerFilterRule rule, string categoryPrefix)
    {
        string name = rule.CategoryName ?? string.Empty;
        int wildcard = name.IndexOf(Wildcard);
        string head = wildcard < 0 ? name : name[..wildcard];
        return head.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase)
            || categoryPrefix.StartsWith(head, StringComparison.OrdinalIgnoreCase);
    }
}
