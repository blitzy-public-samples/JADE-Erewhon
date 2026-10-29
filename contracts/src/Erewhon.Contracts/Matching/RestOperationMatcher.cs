using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Erewhon.Contracts.Catalogue;

namespace Erewhon.Contracts.Matching;

/// <summary>
/// The result of matching a REST request to a catalogue operation. Every value is the raw text of the
/// request path, still percent-encoded, so a caller that forwards the request keeps it byte for byte.
/// </summary>
/// <param name="Operation">The matched <see cref="Surface.Rest"/> operation.</param>
/// <param name="RawSegmentValues">
/// The raw value of each path-template placeholder, in template order, which is signature order
/// (<see cref="ParameterDescriptor.Position"/>). Empty for an operation with no path parameter.
/// </param>
/// <param name="RawParameters">
/// The same raw values keyed by parameter name, with an ordinal comparer and enumerated in template
/// order. Body parameters do not appear.
/// </param>
/// <param name="FormatSuffix">
/// The output-format suffix removed from the last path segment exactly as found, for example
/// <c>.json</c>, or null when the path carried none.
/// </param>
public sealed record RestMatch(
    OperationDescriptor Operation,
    IReadOnlyList<string> RawSegmentValues,
    IReadOnlyDictionary<string, string> RawParameters,
    string? FormatSuffix)
{
    /// <summary>
    /// Returns a path parameter's value with its percent-escapes decoded by
    /// <see cref="Uri.UnescapeDataString(string)"/>. A <c>+</c> stays a <c>+</c>, because the value is a
    /// path segment, not form data; an invalid escape is kept as written.
    /// </summary>
    /// <param name="name">The JADE parameter name, for example <c>pName</c>.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The operation has no path parameter of that name.</exception>
    public string GetDecoded(string name) => Uri.UnescapeDataString(RawParameters[name]);
}

/// <summary>
/// Matches an HTTP method and raw request path to one of the catalogue's REST operations: the facade's
/// REST catch-all, the stand-in's routing and the harness's case validation all use this class, so the
/// three agree on which request is which operation. Every verb, path template, parameter and format
/// suffix comes from <see cref="OperationCatalogue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Matching works on the raw path, never on a decoded one, so <c>/agen%74</c> is not <c>/agent</c> and
/// an encoded <c>%2F</c> stays inside its segment. Literal segments compare ordinally and
/// case-sensitively, the method case-insensitively. Placeholder values are not checked against their
/// JADE types: rejecting a value is the backend's behaviour, and a caller must never refuse a request
/// the backend would accept.
/// </para>
/// <para>
/// A request that matches no operation is not an error. The facade forwards it unchanged to its default
/// target, so <see cref="TryMatch"/> only answers false.
/// </para>
/// <para>The instance is immutable after construction and safe for concurrent use as a singleton.</para>
/// </remarks>
/// <param name="catalogue">The catalogue whose REST operations and format suffixes are matched.</param>
/// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
/// <exception cref="InvalidOperationException">
/// A REST operation has no REST binding or a path template the matcher cannot bind, or a format suffix
/// is unusable; the message names the operation id or the catalogue key.
/// </exception>
public sealed class RestOperationMatcher(OperationCatalogue catalogue)
{
    private const char SegmentSeparator = '/';
    private const char QuerySeparator = '?';
    private const char PlaceholderOpen = '{';
    private const char PlaceholderClose = '}';

    private static readonly ReadOnlyCollection<string> NoValues = ReadOnlyCollection<string>.Empty;

    private static readonly ReadOnlyDictionary<string, string> NoParameters =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    // Field initializers run in textual order, so the null check here precedes every other use.
    private readonly CompiledTemplate[] _templates =
        CompileTemplates(catalogue ?? throw new ArgumentNullException(nameof(catalogue)));

    private readonly string[] _formatSuffixes = OrderFormatSuffixes(catalogue.Addresses.Rest.FormatSuffixes);

    /// <summary>
    /// Matches a request to the first REST operation, in catalogue order, whose verb and path template it
    /// fits. Never throws for any request input; anything unmatched, malformed or null answers false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="rawPath"/> is the path exactly as the request carried it, still percent-encoded and
    /// without the query, for example the raw request target or the escaped <c>PathBase + Path</c>. Text
    /// from the first <c>?</c> onwards is ignored. The caller's string is never changed.
    /// </para>
    /// <para>
    /// The path after its leading <c>/</c> splits on <c>/</c> with empty segments kept, so a trailing
    /// slash or <c>//</c> leaves an empty segment that no template accepts. When the last segment ends
    /// with a catalogue format suffix (<c>.jsonn</c> tried before <c>.json</c>), that one suffix is removed
    /// from the last segment only and reported in <see cref="RestMatch.FormatSuffix"/>. A last segment
    /// that is nothing but a suffix names neither a resource nor a parameter value, so it does not match.
    /// </para>
    /// <para>
    /// A template matches when the method equals its verb ignoring case, the segment count is the
    /// template's, each literal segment is equal ordinally, and each placeholder segment is non-empty.
    /// </para>
    /// </remarks>
    /// <param name="method">The HTTP method, for example <c>GET</c>; compared case-insensitively.</param>
    /// <param name="rawPath">The raw, percent-encoded request path starting with <c>/</c>.</param>
    /// <param name="match">The match when the method returns true; otherwise null.</param>
    /// <returns>True when the request is a catalogue REST operation.</returns>
    public bool TryMatch(string method, string rawPath, [NotNullWhen(true)] out RestMatch? match)
    {
        match = null;
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(rawPath) || rawPath[0] != SegmentSeparator)
        {
            return false;
        }

        var path = rawPath.AsSpan(1);
        var queryStart = path.IndexOf(QuerySeparator);
        if (queryStart >= 0)
        {
            path = path[..queryStart];
        }

        var segments = path.ToString().Split(SegmentSeparator);
        var formatSuffix = RemoveFormatSuffix(segments);
        if (formatSuffix is not null && segments[^1].Length == 0)
        {
            return false;
        }

        foreach (var template in _templates)
        {
            if (template.Matches(method, segments))
            {
                match = template.Bind(segments, formatSuffix);
                return true;
            }
        }

        return false;
    }

    // Removes at most one suffix, the longest the last segment ends with, and returns it as found.
    private string? RemoveFormatSuffix(string[] segments)
    {
        var last = segments[^1];
        foreach (var suffix in _formatSuffixes)
        {
            if (last.EndsWith(suffix, StringComparison.Ordinal))
            {
                segments[^1] = last[..^suffix.Length];
                return suffix;
            }
        }

        return null;
    }

    private static string[] OrderFormatSuffixes(IReadOnlyList<string> formatSuffixes)
    {
        foreach (var suffix in formatSuffixes)
        {
            // An empty suffix would end every segment, and one holding '/' could never end a single segment.
            if (suffix.Length == 0 || suffix.Contains(SegmentSeparator))
            {
                throw new InvalidOperationException(
                    $"Operation catalogue addresses.rest.formatSuffixes entry '{suffix}' must be non-empty and " +
                    $"contain no '{SegmentSeparator}'.");
            }
        }

        // OrderByDescending is stable, so suffixes of equal length keep their catalogue order.
        return [.. formatSuffixes.OrderByDescending(suffix => suffix.Length)];
    }

    private static CompiledTemplate[] CompileTemplates(OperationCatalogue catalogue) =>
        [.. catalogue.GetBySurface(Surface.Rest).Select(CompiledTemplate.Compile)];

    /// <summary>One REST operation's verb and path template, split into segments once at construction.</summary>
    private sealed class CompiledTemplate
    {
        private readonly string _verb;

        // One entry per template segment: the literal text, or null where the segment is a placeholder.
        private readonly string?[] _literals;

        // The request-segment index of each placeholder, in template order, and the parameter it binds.
        private readonly int[] _placeholderIndexes;
        private readonly string[] _parameterNames;

        private CompiledTemplate(
            OperationDescriptor operation,
            string verb,
            string?[] literals,
            int[] placeholderIndexes,
            string[] parameterNames)
        {
            Operation = operation;
            _verb = verb;
            _literals = literals;
            _placeholderIndexes = placeholderIndexes;
            _parameterNames = parameterNames;
        }

        public OperationDescriptor Operation { get; }

        public static CompiledTemplate Compile(OperationDescriptor operation)
        {
            if (operation.Rest is not { } rest)
            {
                throw Inconsistent(operation, "has no rest binding");
            }

            var template = rest.PathTemplate;
            if (string.IsNullOrEmpty(template) || template[0] != SegmentSeparator)
            {
                throw Inconsistent(operation, $"rest.pathTemplate '{template}' does not start with '{SegmentSeparator}'");
            }

            var pathParameters = operation.Parameters
                .Where(parameter => parameter.Location == ParameterLocation.Path)
                .OrderBy(parameter => parameter.Position)
                .Select(parameter => parameter.Name)
                .ToArray();

            var templateSegments = template[1..].Split(SegmentSeparator);
            var literals = new string?[templateSegments.Length];
            var placeholderIndexes = new List<int>();
            var placeholderNames = new List<string>();
            for (var index = 0; index < templateSegments.Length; index++)
            {
                var segment = templateSegments[index];

                // An empty literal would let a trailing or doubled slash, or a bare format suffix, match.
                if (segment.Length == 0)
                {
                    throw Inconsistent(operation, $"rest.pathTemplate '{template}' has an empty segment");
                }

                if (!IsPlaceholder(segment))
                {
                    literals[index] = segment;
                    continue;
                }

                var name = segment[1..^1];
                if (!pathParameters.Contains(name, StringComparer.Ordinal))
                {
                    throw Inconsistent(
                        operation,
                        $"rest.pathTemplate '{template}' placeholder '{segment}' names no path parameter");
                }

                // A repeated name would make Bind's dictionary throw per request instead of here.
                if (placeholderNames.Contains(name, StringComparer.Ordinal))
                {
                    throw Inconsistent(
                        operation,
                        $"rest.pathTemplate '{template}' placeholder '{segment}' appears more than once");
                }

                placeholderIndexes.Add(index);
                placeholderNames.Add(name);
            }

            if (!placeholderNames.SequenceEqual(pathParameters, StringComparer.Ordinal))
            {
                throw Inconsistent(
                    operation,
                    $"rest.pathTemplate '{template}' placeholders [{string.Join(", ", placeholderNames)}] are not " +
                    $"exactly its path parameters in signature order [{string.Join(", ", pathParameters)}]");
            }

            return new CompiledTemplate(operation, rest.Verb, literals, [.. placeholderIndexes], [.. placeholderNames]);
        }

        public bool Matches(string method, string[] segments)
        {
            if (segments.Length != _literals.Length || !string.Equals(method, _verb, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var index = 0; index < segments.Length; index++)
            {
                var matched = _literals[index] is { } literal
                    ? string.Equals(segments[index], literal, StringComparison.Ordinal)
                    : segments[index].Length > 0;
                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        public RestMatch Bind(string[] segments, string? formatSuffix)
        {
            if (_placeholderIndexes.Length == 0)
            {
                return new RestMatch(Operation, NoValues, NoParameters, formatSuffix);
            }

            var values = new string[_placeholderIndexes.Length];
            var parameters = new Dictionary<string, string>(values.Length, StringComparer.Ordinal);
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = segments[_placeholderIndexes[index]];
                parameters.Add(_parameterNames[index], values[index]);
            }

            return new RestMatch(Operation, Array.AsReadOnly(values), parameters.AsReadOnly(), formatSuffix);
        }

        private static bool IsPlaceholder(string segment) =>
            segment.Length > 2
            && segment[0] == PlaceholderOpen
            && segment[^1] == PlaceholderClose
            && segment.AsSpan(1, segment.Length - 2).IndexOfAny(PlaceholderOpen, PlaceholderClose) < 0;

        private static InvalidOperationException Inconsistent(OperationDescriptor operation, string problem) =>
            new($"Operation catalogue entry '{operation.Id}' {problem}, so RestOperationMatcher cannot match it.");
    }
}
