using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Erewhon.Contracts.Catalogue;
using Microsoft.Extensions.Options;

namespace Erewhon.Facade.Routing;

/// <summary>
/// Rejects a routing table that does not route every catalogue operation, so a wrong table never
/// starts. <c>Program.cs</c> registers it as the <see cref="IValidateOptions{TOptions}"/> of
/// <see cref="RoutingOptions"/> and binds the options with <c>ValidateOnStart</c>, so any failure
/// stops start-up with an <see cref="OptionsValidationException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is collected and reported as its own entry of
/// <see cref="OptionsValidationException.Failures"/>, each naming the bad entry by its configuration
/// path with keys as configured, for example
/// <c>Routing:Operations:Soap:getPhoto names undefined target 'nowhere'</c>. Messages come in rule
/// order, then catalogue order, then ordinal key order, so the same table always gives the same
/// output:
/// </para>
/// <list type="number">
/// <item><description>the default target is named, defined and has both base URLs;</description></item>
/// <item><description>every listed catalogue operation names a defined target with its surface's base URL;</description></item>
/// <item><description>every listed key is a catalogue operation of its surface;</description></item>
/// <item><description>every target is non-empty and every base URL it carries is well formed;</description></item>
/// <item><description>every target timeout is greater than 0.</description></item>
/// </list>
/// <para>
/// The set of operations comes only from the injected <see cref="OperationCatalogue"/>, and every
/// lookup goes through <see cref="RoutingOptions.TryGetTarget"/> and
/// <see cref="RoutingOperations.TryGetTargetName"/>, so an entry spelled in another case resolves
/// exactly as <c>OperationRouter</c> resolves it. Only the form of an address is checked: reachability
/// is not, and the facade carries no credential to check. The validator holds no state and never
/// changes the options it validates.
/// </para>
/// </remarks>
/// <param name="catalogue">The operation catalogue, <see cref="OperationCatalogue.Default"/> in the facade.</param>
public sealed class RoutingOptionsValidator(OperationCatalogue catalogue) : IValidateOptions<RoutingOptions>
{
    private const string DefaultTargetPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.DefaultTarget);
    private const string TargetsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Targets);
    private const string OperationsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Operations);

    // The two surfaces the routing table has sections for, in catalogue order, with the base URL each
    // surface's operations are forwarded to.
    private static readonly SurfaceRoute[] Surfaces =
    [
        new(Surface.Rest, nameof(RoutingOperations.Rest), "REST", nameof(RoutingTarget.RestBaseUrl), static target => target.RestBaseUrl),
        new(Surface.Soap, nameof(RoutingOperations.Soap), "SOAP", nameof(RoutingTarget.SoapBaseUrl), static target => target.SoapBaseUrl),
    ];

    private readonly OperationCatalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));

    /// <summary>Validates one routing table; every named instance is validated alike.</summary>
    /// <param name="name">The options instance name; it does not change the rules.</param>
    /// <param name="options">The bound routing table. Null dictionaries count as empty.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/>, or a failure listing every bad entry. A bad
    /// configuration never throws.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ValidateOptionsResult Validate(string? name, RoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var operations = options.Operations ?? new RoutingOperations();
        KeyValuePair<string, RoutingTarget>[] targets = options.Targets is null
            ? []
            : [.. options.Targets.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];

        var defaultTarget = ValidateDefaultTarget(options, failures);
        ValidateCatalogueOperations(options, operations, defaultTarget, failures);
        ValidateListedKeys(operations, failures);
        ValidateBaseUrls(targets, failures);
        ValidateTimeouts(targets, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Parses a target base URL: an absolute <c>http</c> or <c>https</c> URL with a host and no query or
    /// fragment, surrounding blanks ignored. <c>OperationRouter</c> parses with this method too, so the
    /// router never accepts a URL start-up rejected.
    /// </summary>
    /// <remarks>
    /// YARP's <c>IHttpForwarder</c> appends the request's raw path and query to the destination prefix,
    /// so a query or fragment in the prefix would corrupt every forwarded address. A query or fragment
    /// that is only a bare <c>?</c> or <c>#</c> is rejected as well.
    /// </remarks>
    /// <param name="value">The configured value.</param>
    /// <param name="uri">The parsed URL; null when the value is rejected.</param>
    /// <returns>True when the value is a usable base URL; false for null, whitespace or any other form.</returns>
    public static bool TryParseBaseUrl(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && parsed.Host.Length > 0
            && parsed.Query.Length == 0
            && parsed.Fragment.Length == 0)
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    // Rule 1. Returns the default target when it is defined, whatever its URLs, so rule 2 can tell
    // which missing URLs this rule has already reported.
    private static RoutingTarget? ValidateDefaultTarget(RoutingOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.DefaultTarget))
        {
            failures.Add($"{DefaultTargetPath} is empty");
            return null;
        }

        if (!options.TryGetTarget(options.DefaultTarget, out var configuredName, out var target))
        {
            failures.Add($"{DefaultTargetPath} names undefined target '{options.DefaultTarget}'");
            return null;
        }

        foreach (var surface in Surfaces)
        {
            if (IsAbsent(surface.UrlOf(target)))
            {
                failures.Add(
                    $"{TargetsPath}:{configuredName} has no {surface.UrlName} but is {DefaultTargetPath} " +
                    "(unmatched requests on both listeners use it)");
            }
        }

        return target;
    }

    // Rule 2. An unlisted operation resolves to the default target, so every way it can fail (no
    // default, an undefined one, or one without the surface's URL) is already a single rule-1 message
    // and nothing is added per operation. For the same reason a listed operation that names the
    // default target is not reported again for a URL rule 1 found missing; every other listed
    // operation is checked on its own.
    private void ValidateCatalogueOperations(
        RoutingOptions options,
        RoutingOperations operations,
        RoutingTarget? defaultTarget,
        List<string> failures)
    {
        foreach (var surface in Surfaces)
        {
            foreach (var operation in _catalogue.GetBySurface(surface.Surface))
            {
                if (!operations.TryGetTargetName(surface.Surface, operation.RoutingKey, out var configuredKey, out var targetName))
                {
                    continue;
                }

                var entry = $"{OperationsPath}:{surface.Section}:{configuredKey}";
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    failures.Add($"{entry} is empty");
                }
                else if (!options.TryGetTarget(targetName, out var targetKey, out var target))
                {
                    failures.Add($"{entry} names undefined target '{targetName}'");
                }
                else if (IsAbsent(surface.UrlOf(target)) && !ReferenceEquals(target, defaultTarget))
                {
                    failures.Add($"{TargetsPath}:{targetKey} has no {surface.UrlName} but serves {entry}");
                }
            }
        }
    }

    // Rule 3. A key valid only on the other surface fails here, because routing is per surface.
    private void ValidateListedKeys(RoutingOperations operations, List<string> failures)
    {
        foreach (var surface in Surfaces)
        {
            var routingKeys = _catalogue.GetBySurface(surface.Surface)
                .Select(static operation => operation.RoutingKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in operations.For(surface.Surface).Keys.Order(StringComparer.Ordinal))
            {
                if (!routingKeys.Contains(key))
                {
                    failures.Add($"{OperationsPath}:{surface.Section}:{key} is not a catalogue {surface.Label} operation");
                }
            }
        }
    }

    // Rule 4. Applies to every target, referenced or not, so a typo in a target staged for a later
    // cut-over is caught before that target is first used.
    private static void ValidateBaseUrls(KeyValuePair<string, RoutingTarget>[] targets, List<string> failures)
    {
        foreach (var (name, target) in targets)
        {
            if (IsEmpty(target))
            {
                failures.Add($"{TargetsPath}:{name} is empty");
                continue;
            }

            foreach (var surface in Surfaces)
            {
                var value = surface.UrlOf(target);
                if (!IsAbsent(value) && !TryParseBaseUrl(value, out _))
                {
                    failures.Add(
                        $"{TargetsPath}:{name}:{surface.UrlName} '{value}' is not an absolute http or https URL " +
                        "without query or fragment");
                }
            }
        }
    }

    // Rule 5.
    private static void ValidateTimeouts(KeyValuePair<string, RoutingTarget>[] targets, List<string> failures)
    {
        foreach (var (name, target) in targets)
        {
            if (target?.TimeoutSeconds is int seconds && seconds <= 0)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{TargetsPath}:{name}:{nameof(RoutingTarget.TimeoutSeconds)} must be greater than 0 (was {seconds})"));
            }
        }
    }

    // A null, empty or whitespace-only URL counts as not configured.
    private static bool IsAbsent(string? url) => string.IsNullOrWhiteSpace(url);

    // The configuration binder turns an empty target section, or one holding only unrecognised keys
    // such as a misspelled property name, into a target with nothing set rather than a null entry, so
    // both forms count as an empty section.
    private static bool IsEmpty([NotNullWhen(false)] RoutingTarget? target) =>
        target is null
        || (IsAbsent(target.RestBaseUrl) && IsAbsent(target.SoapBaseUrl) && target.TimeoutSeconds is null);

    /// <summary>One surface's section, label and base URL in the routing table.</summary>
    /// <param name="Surface">The catalogue surface.</param>
    /// <param name="Section">The section under <c>Routing:Operations</c>, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="Label">The surface as written in messages, <c>REST</c> or <c>SOAP</c>.</param>
    /// <param name="UrlName">The target property holding the surface's base URL.</param>
    /// <param name="UrlOf">Reads that property from a target.</param>
    private sealed record SurfaceRoute(
        Surface Surface,
        string Section,
        string Label,
        string UrlName,
        Func<RoutingTarget, string?> UrlOf);
}
