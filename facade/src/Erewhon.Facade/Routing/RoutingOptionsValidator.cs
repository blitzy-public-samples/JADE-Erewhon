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
/// <item><description>every target is non-empty and every base URL it carries is an absolute http or https URL with a host and no user information, query or fragment;</description></item>
/// <item><description>every target timeout is greater than 0.</description></item>
/// </list>
/// <para>
/// No message quotes a configured base URL: a rejected one is named by its configuration path and the
/// rule it breaks, for example <c>Routing:Targets:backend:RestBaseUrl must not contain user information</c>.
/// A target name is quoted only when it holds no at-sign (<see cref="NamesUndefinedTarget"/>). The
/// failures reach the host's start-up error log, the one line <c>Program.cs</c> writes to standard error
/// and <c>OperationRouter</c>'s rejected-reload log entry, so a credential pasted into the routing
/// configuration never reaches any of them.
/// </para>
/// <para>
/// The set of operations comes only from the injected <see cref="OperationCatalogue"/>, and every
/// lookup goes through <see cref="RoutingOptions.TryGetTarget"/> and
/// <see cref="RoutingOperations.TryGetTargetName"/>, so an entry spelled in another case resolves
/// exactly as <c>OperationRouter</c> resolves it. Only the form of an address is checked: reachability
/// is not. The facade carries and adds no credential, so a base URL carrying one is rejected rather than
/// silently unused. The validator holds no state and never changes the options it validates.
/// </para>
/// </remarks>
/// <param name="catalogue">The operation catalogue, <see cref="OperationCatalogue.Default"/> in the facade.</param>
public sealed class RoutingOptionsValidator(OperationCatalogue catalogue) : IValidateOptions<RoutingOptions>
{
    private const string DefaultTargetPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.DefaultTarget);
    private const string TargetsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Targets);
    private const string OperationsPath = RoutingOptions.SectionName + ":" + nameof(RoutingOptions.Operations);

    // The characters that end user information in a URL: '@' and its two compatibility forms, U+FF20
    // and U+FE6B, which NFKC folds to '@'. No other character folds to it.
    private const string AtSignCharacters = "@\uFF20\uFE6B";

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
    /// Parses a target base URL: an absolute <c>http</c> or <c>https</c> URL with a host and no user
    /// information, query or fragment, surrounding blanks ignored. <c>OperationRouter</c> parses with this
    /// method too, so the router never accepts a URL start-up rejected.
    /// </summary>
    /// <remarks>
    /// YARP's <c>IHttpForwarder</c> appends the request's raw path and query to the destination prefix,
    /// so a query or fragment in the prefix would corrupt every forwarded address. A query or fragment
    /// that is only a bare <c>?</c> or <c>#</c> is rejected as well. The facade sends no credential, so
    /// user information would go unused while <c>GET /admin/info</c> and the logs could show it; it is
    /// rejected, and so is a host holding a compatibility form of <c>@</c> (U+FF20 or U+FE6B), which the
    /// runtime reads as part of the host rather than as the end of user information.
    /// <see cref="DescribeBaseUrlProblem"/> applies the same rules, so the two never disagree.
    /// </remarks>
    /// <param name="value">The configured value.</param>
    /// <param name="uri">The parsed URL; null when the value is rejected.</param>
    /// <returns>True when the value is a usable base URL; false for null, whitespace or any other form.</returns>
    public static bool TryParseBaseUrl(string? value, [NotNullWhen(true)] out Uri? uri) =>
        TryReadBaseUrl(value, out uri, out _);

    /// <summary>
    /// Says why a configured base URL is unusable, in a fixed phrase that quotes no part of the value, so
    /// a credential in a rejected URL never reaches a failure message or a log.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns>
    /// Null when <see cref="TryParseBaseUrl"/> accepts the value. Otherwise the first rule it breaks, worded
    /// to follow the entry's configuration path: <c>is empty</c>, <c>is not an absolute URL</c>,
    /// <c>is not an http or https URL</c>, <c>has no host</c>, <c>must not contain user information</c>,
    /// <c>must not contain a query</c> or <c>must not contain a fragment</c>.
    /// </returns>
    internal static string? DescribeBaseUrlProblem(string? value) =>
        TryReadBaseUrl(value, out _, out var problem) ? null : problem;

    /// <summary>
    /// The failure of an entry that names an undefined target. The name is quoted as configured unless it
    /// holds <c>@</c> or a compatibility form of it: user information always needs one, so a URL with a
    /// credential pasted where a target name belongs is never echoed.
    /// </summary>
    /// <param name="entry">The entry's configuration path, such as <c>Routing:DefaultTarget</c>.</param>
    /// <param name="targetName">The target name the entry holds.</param>
    /// <returns>The failure message, naming <paramref name="entry"/>.</returns>
    internal static string NamesUndefinedTarget(string entry, string targetName) =>
        HoldsAtSign(targetName)
            ? $"{entry} names an undefined target, not shown because it may contain user information"
            : $"{entry} names undefined target '{targetName}'";

    /// <summary>True when <paramref name="text"/> holds <c>@</c> or one of its compatibility forms, U+FF20 and U+FE6B.</summary>
    /// <param name="text">The text to search; null holds none.</param>
    /// <returns>True when user information could end inside <paramref name="text"/>.</returns>
    internal static bool HoldsAtSign(string? text) => text.AsSpan().IndexOfAny(AtSignCharacters) >= 0;

    // The one implementation of the base URL rules behind TryParseBaseUrl and DescribeBaseUrlProblem.
    // Every problem is a fixed phrase that quotes no part of the value, not even the scheme, because the
    // runtime parses "user:secret@backend" as an absolute URL whose scheme is "user".
    private static bool TryReadBaseUrl(
        string? value,
        [NotNullWhen(true)] out Uri? uri,
        [NotNullWhen(false)] out string? problem)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "is empty";
        }
        else if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            problem = "is not an absolute URL";
        }
        else if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            problem = "is not an http or https URL";
        }
        else if (parsed.Host.Length == 0)
        {
            problem = "has no host";
        }
        else if (parsed.UserInfo.Length > 0 || HoldsAtSign(parsed.Host))
        {
            problem = "must not contain user information";
        }
        else if (parsed.Query.Length > 0)
        {
            problem = "must not contain a query";
        }
        else if (parsed.Fragment.Length > 0)
        {
            problem = "must not contain a fragment";
        }
        else
        {
            uri = parsed;
            problem = null;
            return true;
        }

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
            failures.Add(NamesUndefinedTarget(DefaultTargetPath, options.DefaultTarget));
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
                    failures.Add(NamesUndefinedTarget(entry, targetName));
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
    // cut-over is caught before that target is first used. The message names the entry and the rule it
    // breaks, never the value, which may hold a credential.
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
                if (!IsAbsent(value) && DescribeBaseUrlProblem(value) is { } problem)
                {
                    failures.Add($"{TargetsPath}:{name}:{surface.UrlName} {problem}");
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
