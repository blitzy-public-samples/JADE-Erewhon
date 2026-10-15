using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Erewhon.Application.Messages;

/// <summary>
/// The locale-5129 message templates read from the embedded <c>messages.en-NZ.json</c> resource,
/// keyed by the names in <see cref="MessageKeys"/>, with <c>{0}</c>, <c>{1}</c>, ... placeholders
/// numbered in JADE parameter declaration order.
/// </summary>
public sealed class MessageCatalog
{
    /// <summary>The manifest resource name of the embedded message file.</summary>
    public const string ResourceName = "Erewhon.Application.Messages.messages.en-NZ.json";

    private static readonly Lazy<MessageCatalog> DefaultInstance =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, string> _templates;

    private MessageCatalog(Dictionary<string, string> templates)
    {
        _templates = templates;
        All = new ReadOnlyDictionary<string, string>(templates);
    }

    /// <summary>The catalogue loaded from <see cref="ResourceName"/> on first access; the same instance on every access.</summary>
    public static MessageCatalog Default => DefaultInstance.Value;

    /// <summary>Every message key and its template, read-only.</summary>
    public IReadOnlyDictionary<string, string> All { get; }

    /// <summary>Returns whether a template is defined for the key, matched case-sensitively.</summary>
    /// <param name="key">The message key, e.g. <see cref="MessageKeys.AgentNameNotFound"/>.</param>
    /// <returns><see langword="true"/> when the key is defined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _templates.ContainsKey(key);
    }

    /// <summary>Returns the unformatted template for the key, placeholders included.</summary>
    /// <param name="key">The message key, e.g. <see cref="MessageKeys.AgentNameNotFound"/>.</param>
    /// <returns>The template text, e.g. <c>Agent {0} not found</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No template is defined for <paramref name="key"/>.</exception>
    public string GetTemplate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_templates.TryGetValue(key, out var template))
        {
            throw new KeyNotFoundException($"Message key '{key}' is not defined in {ResourceName}.");
        }

        return template;
    }

    /// <summary>
    /// Formats the template for the key with arguments in JADE parameter order, each argument
    /// converted with the invariant culture; a template may use any of them, in any order, or none.
    /// </summary>
    /// <param name="key">The message key, e.g. <see cref="MessageKeys.AgentNameNotFound"/>.</param>
    /// <param name="args">The message parameters in JADE declaration order.</param>
    /// <returns>The message text, e.g. <c>Agent Bob not found</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No template is defined for <paramref name="key"/>.</exception>
    /// <exception cref="FormatException">The template refers to more arguments than were supplied.</exception>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, GetTemplate(key), args ?? Array.Empty<object?>());

    /// <summary>Formats the template for the key from <see cref="Default"/> with arguments in JADE parameter order.</summary>
    /// <param name="key">The message key, e.g. <see cref="MessageKeys.AgentNameNotFound"/>.</param>
    /// <param name="args">The message parameters in JADE declaration order.</param>
    /// <returns>The message text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No template is defined for <paramref name="key"/>.</exception>
    /// <exception cref="FormatException">The template refers to more arguments than were supplied.</exception>
    public static string Get(string key, params object?[] args) => Default.Format(key, args);

    private static MessageCatalog Load()
    {
        Assembly assembly = typeof(MessageCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' was not found in assembly {assembly.GetName().Name}.");
        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"The root of {ResourceName} must be a JSON object, but is {root.ValueKind}.");
        }

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Message key '{property.Name}' in {ResourceName} must have a string value, but has {property.Value.ValueKind}.");
            }

            if (!templates.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new InvalidOperationException(
                    $"Message key '{property.Name}' is defined more than once in {ResourceName}.");
            }
        }

        return new MessageCatalog(templates);
    }
}
