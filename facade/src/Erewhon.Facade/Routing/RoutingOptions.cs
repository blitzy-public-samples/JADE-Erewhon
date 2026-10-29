using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Erewhon.Contracts.Catalogue;

namespace Erewhon.Facade.Routing;

/// <summary>
/// The facade's per-operation routing table, bound from the <c>Routing</c> configuration section
/// (<see cref="SectionName"/>). Each catalogue operation is forwarded to the target its entry under
/// <see cref="Operations"/> names, and every unlisted operation or unmatched request goes to
/// <see cref="DefaultTarget"/>.
/// </summary>
/// <remarks>
/// <para>
/// Compose and tests override any value through environment variables with <c>__</c> in place of
/// <c>:</c>, for example <c>Routing__Targets__backend__RestBaseUrl</c>. Moving one operation means
/// adding a target and changing one key, such as <c>Routing__Targets__orders__RestBaseUrl</c> plus
/// <c>Routing__Operations__Rest__&lt;jadeMethod&gt;=orders</c>; callers and the other operations are
/// untouched.
/// </para>
/// <para>
/// This type is only the bound shape. <c>RoutingOptionsValidator</c> rejects a table that does not
/// resolve every catalogue operation at start-up, and <c>OperationRouter</c> resolves the target per
/// request. Both look entries up through <see cref="TryGetTarget"/> and
/// <see cref="RoutingOperations.TryGetTargetName"/>, because configuration keys are case-insensitive:
/// an operation key or target name spelled in another case, for example by an environment variable,
/// must reach the same entry, whether or not the bound dictionaries kept their case-insensitive
/// comparer.
/// </para>
/// <para>
/// No operation name and no address is written in code: the operations come from the catalogue and the
/// entries and base URLs from <c>appsettings.json</c> and the environment. Instances are read
/// concurrently and never mutated after binding.
/// </para>
/// </remarks>
public sealed class RoutingOptions
{
    /// <summary>The configuration section the routing table is bound from: <c>Routing</c>.</summary>
    public const string SectionName = "Routing";

    /// <summary>
    /// <c>Routing:DefaultTarget</c> (env <c>Routing__DefaultTarget</c>): the name of the target in
    /// <see cref="Targets"/> that serves every operation not listed under <see cref="Operations"/> and
    /// every request on the REST or SOAP listener that matches no catalogue operation, so it needs both
    /// base URLs. Defaults to <c>backend</c>.
    /// </summary>
    public string DefaultTarget { get; set; } = "backend";

    /// <summary>
    /// <c>Routing:Targets:&lt;name&gt;</c> (env <c>Routing__Targets__&lt;name&gt;__&lt;property&gt;</c>):
    /// the forwarding targets by name. Starts empty; names are matched case-insensitively. A target
    /// section with no values binds to a null entry, which <see cref="TryGetTarget"/> treats as not
    /// found.
    /// </summary>
    /// <remarks>
    /// A concrete <see cref="Dictionary{TKey, TValue}"/>, so the configuration binder fills this
    /// instance and keeps its <see cref="StringComparer.OrdinalIgnoreCase"/> comparer.
    /// </remarks>
    public Dictionary<string, RoutingTarget> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Routing:Operations:Rest|Soap:&lt;jadeMethod&gt;</c>: the target named for each listed
    /// operation, per surface.
    /// </summary>
    public RoutingOperations Operations { get; set; } = new();

    /// <summary>
    /// Finds the target named <paramref name="name"/> in <see cref="Targets"/>, ignoring case.
    /// </summary>
    /// <param name="name">A target name, such as the value of an operation entry or <see cref="DefaultTarget"/>.</param>
    /// <param name="configuredName">
    /// The target's key as configured, which may differ in case from <paramref name="name"/>; null when
    /// not found.
    /// </param>
    /// <param name="target">The target; null when not found.</param>
    /// <returns>
    /// True when a target with a non-null entry is configured under that name. False for a null or
    /// whitespace name, an undefined name, or a target section that bound to a null entry. When a
    /// hand-built case-sensitive dictionary holds several spellings of the name, an exact match wins,
    /// and otherwise the first match in ordinal key order.
    /// </returns>
    public bool TryGetTarget(
        string? name,
        [NotNullWhen(true)] out string? configuredName,
        [NotNullWhen(true)] out RoutingTarget? target)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && ConfiguredKeyLookup.TryFind(Targets, name, skipNullValues: true, out configuredName, out var found)
            && found is not null)
        {
            target = found;
            return true;
        }

        configuredName = null;
        target = null;
        return false;
    }
}

/// <summary>
/// One forwarding target, bound from <c>Routing:Targets:&lt;name&gt;</c>. It carries only addresses and
/// an optional deadline: the facade adds no credential, token or other caller identity to a forwarded
/// call.
/// </summary>
/// <remarks>
/// A null, empty or whitespace-only URL counts as absent; <c>RoutingOptionsValidator</c> requires the
/// URL of each surface the target serves, and both for <see cref="RoutingOptions.DefaultTarget"/>.
/// An unset Compose variable such as <c>Routing__Targets__backend__RestBaseUrl=${BACKEND_REST_URL}</c>
/// binds as an empty string and is therefore reported as absent at start-up. No base URL has a default.
/// </remarks>
public sealed class RoutingTarget
{
    /// <summary>
    /// <c>Routing:Targets:&lt;name&gt;:RestBaseUrl</c> (env
    /// <c>Routing__Targets__&lt;name&gt;__RestBaseUrl</c>): the absolute http or https base address REST
    /// operations are forwarded to, with the request's raw path and query appended unchanged.
    /// </summary>
    public string? RestBaseUrl { get; set; }

    /// <summary>
    /// <c>Routing:Targets:&lt;name&gt;:SoapBaseUrl</c> (env
    /// <c>Routing__Targets__&lt;name&gt;__SoapBaseUrl</c>): the absolute http or https base address SOAP
    /// operations and the SOAP listener's unmatched requests are forwarded to, with the request's raw
    /// path and query appended unchanged.
    /// </summary>
    public string? SoapBaseUrl { get; set; }

    /// <summary>
    /// <c>Routing:Targets:&lt;name&gt;:TimeoutSeconds</c> (env
    /// <c>Routing__Targets__&lt;name&gt;__TimeoutSeconds</c>): this target's per-call deadline in
    /// seconds, overriding <c>Backend:TimeoutSeconds</c> (default 30) when set. Must be greater than 0.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// The per-operation entries of the routing table, bound from <c>Routing:Operations</c>. Each key is a
/// catalogue operation's <see cref="OperationDescriptor.RoutingKey"/> (its JADE method name) and each
/// value names a target in <see cref="RoutingOptions.Targets"/>. Both dictionaries start empty; the
/// shipped entries live in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// The surface-qualified sections keep the REST and SOAP operations that share a JADE method name
/// distinct, so the two are never merged into one dictionary. Keys are matched case-insensitively.
/// Both properties are concrete <see cref="Dictionary{TKey, TValue}"/> instances, so the configuration
/// binder fills them and keeps their <see cref="StringComparer.OrdinalIgnoreCase"/> comparer.
/// </remarks>
public sealed class RoutingOperations
{
    /// <summary>
    /// <c>Routing:Operations:Rest:&lt;jadeMethod&gt;</c> (env
    /// <c>Routing__Operations__Rest__&lt;jadeMethod&gt;</c>): the target of each listed REST operation.
    /// </summary>
    public Dictionary<string, string> Rest { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Routing:Operations:Soap:&lt;jadeMethod&gt;</c> (env
    /// <c>Routing__Operations__Soap__&lt;jadeMethod&gt;</c>): the target of each listed SOAP operation.
    /// </summary>
    public Dictionary<string, string> Soap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries of one surface, keyed as configured; empty when the section bound to null.</summary>
    /// <param name="surface"><see cref="Surface.Rest"/> or <see cref="Surface.Soap"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is neither value.</exception>
    public IReadOnlyDictionary<string, string> For(Surface surface) =>
        (IReadOnlyDictionary<string, string>?)DictionaryFor(surface) ?? ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// Finds the entry for <paramref name="routingKey"/> under <paramref name="surface"/>, ignoring case.
    /// </summary>
    /// <param name="surface"><see cref="Surface.Rest"/> or <see cref="Surface.Soap"/>.</param>
    /// <param name="routingKey">The operation's <see cref="OperationDescriptor.RoutingKey"/>.</param>
    /// <param name="configuredKey">
    /// The entry's key as configured, which may differ in case from <paramref name="routingKey"/>; null
    /// when the operation is not listed.
    /// </param>
    /// <param name="targetName">
    /// The configured target name, which may be null, empty or undefined; resolving and reporting it is
    /// the caller's work.
    /// </param>
    /// <returns>
    /// True when the operation is listed, whatever its value; false when it is not, in which case
    /// <see cref="RoutingOptions.DefaultTarget"/> applies.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is neither value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="routingKey"/> is null.</exception>
    public bool TryGetTargetName(
        Surface surface,
        string routingKey,
        [NotNullWhen(true)] out string? configuredKey,
        out string? targetName)
    {
        var entries = DictionaryFor(surface);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (!string.IsNullOrWhiteSpace(routingKey)
            && ConfiguredKeyLookup.TryFind(entries, routingKey, skipNullValues: false, out configuredKey, out targetName))
        {
            return true;
        }

        configuredKey = null;
        targetName = null;
        return false;
    }

    // The properties are non-nullable, but binding or a test may still assign null.
    private Dictionary<string, string>? DictionaryFor(Surface surface) => surface switch
    {
        Surface.Rest => Rest,
        Surface.Soap => Soap,
        _ => throw new ArgumentOutOfRangeException(
            nameof(surface), surface, "Routing entries exist only for the Rest and Soap surfaces."),
    };
}

/// <summary>Case-insensitive lookup of a configured key that also returns the key as stored.</summary>
file static class ConfiguredKeyLookup
{
    /// <summary>
    /// Finds <paramref name="key"/> in <paramref name="map"/>: first directly, under the dictionary's own
    /// comparer, then by an ordinal case-insensitive scan when that comparer is not
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. The scan prefers the exact spelling and otherwise
    /// picks the first match in ordinal key order, so the result never depends on insertion order.
    /// Allocates nothing.
    /// </summary>
    public static bool TryFind<TValue>(
        Dictionary<string, TValue>? map,
        string key,
        bool skipNullValues,
        [NotNullWhen(true)] out string? configuredKey,
        out TValue? value)
        where TValue : class
    {
        configuredKey = null;
        value = null;
        if (map is null || map.Count == 0)
        {
            return false;
        }

        // The span lookup is the dictionary's own hash lookup, and unlike TryGetValue it also yields the
        // stored key, whose case differs from 'key' under a case-insensitive comparer.
        if (map.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            if (lookup.TryGetValue(key.AsSpan(), out var storedKey, out var storedValue)
                && (storedValue is not null || !skipNullValues))
            {
                configuredKey = storedKey;
                value = storedValue;
                return true;
            }

            if (ReferenceEquals(map.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                // A dictionary that already ignores case holds no other spelling for the scan to find.
                return false;
            }
        }

        foreach (var (candidateKey, candidateValue) in map)
        {
            if ((candidateValue is null && skipNullValues)
                || !string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(candidateKey, key, StringComparison.Ordinal))
            {
                // Reached only when the comparer offers no span lookup: the exact spelling still wins.
                configuredKey = candidateKey;
                value = candidateValue;
                return true;
            }

            if (configuredKey is null || string.CompareOrdinal(candidateKey, configuredKey) < 0)
            {
                configuredKey = candidateKey;
                value = candidateValue;
            }
        }

        return configuredKey is not null;
    }
}
