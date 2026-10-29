using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Erewhon.Harness.Report;

/// <summary>
/// One (<c>itemId</c>, <c>nature</c>) pair a register entry documents (AAP 0.7.7). Both values are
/// compared ordinally and exactly, untrimmed, with a <see cref="FailureItem"/>'s identity.
/// </summary>
/// <param name="ItemId">The failing item's id, for example <c>CASE:REST-getAgent-01</c>; never blank.</param>
/// <param name="Nature">The failing item's nature, for example <c>d:0123456789abcdef</c>; never blank.</param>
public sealed record RegisterMatch(string ItemId, string Nature);

/// <summary>
/// One entry of the known-limitations register <c>harness/config/known-limitations.json</c>
/// (AAP 0.7.7): an assumption, substitution, difference, conformance gap, contract or check failure,
/// with the exact failing items it documents.
/// </summary>
/// <param name="Id">The unique entry id, for example <c>KL-REST-PATHS</c>; never blank.</param>
/// <param name="Category">One of <see cref="RegisterCategories.All"/>.</param>
/// <param name="Operation">
/// The catalogue id of the operation the entry concerns, such as <c>Soap.getPhoto</c>; null when it
/// concerns none. Informational only: it plays no part in matching.
/// </param>
/// <param name="Matches">
/// The exact (<c>itemId</c>, <c>nature</c>) pairs the entry documents; empty for assumptions and for
/// substitutions that cause no failing item.
/// </param>
/// <param name="Reason">What was not done and why; empty when the file gives none.</param>
/// <param name="Attempts">The correction attempts made before the entry was added; never negative.</param>
/// <param name="ActionTaken">What was done instead; empty when the file gives none.</param>
public sealed record RegisterEntry(
    string Id,
    string Category,
    string? Operation,
    IReadOnlyList<RegisterMatch> Matches,
    string Reason,
    int Attempts,
    string ActionTaken);

/// <summary>The categories a register entry can have (AAP 0.7.7), spelled exactly as the file holds them.</summary>
public static class RegisterCategories
{
    /// <summary>An element the repository does not state outright, applied by a fixed rule.</summary>
    public const string Assumption = "assumption";

    /// <summary>A fallback taken in place of a planned step.</summary>
    public const string Substitution = "substitution";

    /// <summary>An equivalence difference that persisted after the correction attempts.</summary>
    public const string Difference = "difference";

    /// <summary>A stand-in conformance member that is unreachable in source or not closed after the attempts.</summary>
    public const string ConformanceGap = "conformance-gap";

    /// <summary>An OpenAPI or WSDL contract failure that persisted after the correction attempts.</summary>
    public const string Contract = "contract";

    /// <summary>Any other check failure: facade tests, readiness, reset, JADE-unchanged or ordering.</summary>
    public const string Check = "check";

    /// <summary>Every category, in the order <c>assumption</c>, <c>substitution</c>, <c>difference</c>,
    /// <c>conformance-gap</c>, <c>contract</c>, <c>check</c>.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Assumption, Substitution, Difference, ConformanceGap, Contract, Check];

    /// <summary>Returns true when <paramref name="category"/> is one of <see cref="All"/>, compared ordinally.</summary>
    /// <param name="category">The category to test; null is never known.</param>
    public static bool IsKnown(string? category)
    {
        if (category is null)
        {
            return false;
        }

        foreach (var known in All)
        {
            if (string.Equals(known, category, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The ids of the register entries the harness refers to by name.</summary>
public static class KnownLimitationIds
{
    /// <summary>The baseline comes from the JADE stand-in, not live JADE.</summary>
    public const string BaselineStandIn = "KL-BASELINE-STANDIN";

    /// <summary>The REST path scheme is assumed.</summary>
    public const string RestPaths = "KL-REST-PATHS";

    /// <summary>The SOAP action names follow the JADE default convention and are assumed.</summary>
    public const string SoapActions = "KL-SOAP-ACTIONS";

    /// <summary>The WSDL follows the JADE default conventions and is assumed.</summary>
    public const string SoapWsdl = "KL-SOAP-WSDL";

    /// <summary>The JADE unit tests were not run, because no live JADE is used.</summary>
    public const string JadeTestsNotRun = "KL-JADE-TESTS-NOT-RUN";

    /// <summary>The backend is not the stand-in, so no reset or seed confirmation took place.</summary>
    public const string SubNoReset = "KL-SUB-NORESET";

    /// <summary>
    /// The entries the "Known limitations" section of <c>RESULTS.md</c> always carries, in the order it
    /// renders them first (AAP 0.8.2): <see cref="BaselineStandIn"/>, <see cref="RestPaths"/>,
    /// <see cref="SoapActions"/>, <see cref="SoapWsdl"/> and <see cref="JadeTestsNotRun"/>.
    /// </summary>
    public static IReadOnlyList<string> Mandatory { get; } =
        [BaselineStandIn, RestPaths, SoapActions, SoapWsdl, JadeTestsNotRun];
}

/// <summary>
/// The known-limitations register <c>harness/config/known-limitations.json</c>, read-only (AAP 0.7.7).
/// It decides whether a failing item is documented, which drives each operation's result, the final
/// line of <c>RESULTS.md</c> and the harness's exit code.
/// </summary>
/// <remarks>
/// <para>
/// A failing item is documented exactly when some entry's <see cref="RegisterEntry.Matches"/> holds
/// both its <see cref="FailureItem.ItemId"/> and its <see cref="FailureItem.Nature"/>, compared
/// ordinally. A nature covers every discrepancy of its item, so a changed value at a documented path,
/// an additional mismatch, a different failing assertion or a different theory invocation each gives
/// a new, undocumented failure. Items for which <see cref="FailureItem.IsNeverDocumentable"/> is true
/// (<c>CHK-SHUTDOWN</c>, <c>CHK-REPORT</c>, <c>CHK-EXCEPTION</c>) are never documented, whatever the
/// register lists.
/// </para>
/// <para>
/// Only the execution agent edits the file; this type has no API that writes, appends to or
/// regenerates it. An instance is immutable once constructed: lookups are built from the entries as
/// they stand at construction, so concurrent reads are safe.
/// </para>
/// </remarks>
public sealed class KnownLimitationsRegister
{
    /// <summary>The only register document version this type reads.</summary>
    public const int SupportedVersion = 1;

    private const string ErrorPrefix = "Invalid known-limitations register: ";

    // Member names exactly as harness/config/known-limitations.json spells them.
    private const string VersionKey = "version";
    private const string EntriesKey = "entries";
    private const string IdKey = "id";
    private const string CategoryKey = "category";
    private const string OperationKey = "operation";
    private const string MatchesKey = "matches";
    private const string ItemIdKey = "itemId";
    private const string NatureKey = "nature";
    private const string ReasonKey = "reason";
    private const string AttemptsKey = "attempts";
    private const string ActionTakenKey = "actionTaken";

    // The register is edited by hand, so comments and trailing commas are tolerated. A repeated member
    // is still an error: silently keeping one of two "matches" lists would change which failures count
    // as documented, and with them the exit code.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
    };

    // Keyed on the pair alone; string equality in a value tuple is ordinal.
    private readonly Dictionary<(string ItemId, string Nature), RegisterEntry> _byMatch;
    private readonly Dictionary<string, RegisterEntry> _byId;

    /// <summary>
    /// Builds a register from entries in file order, validating each: a non-blank id that no earlier
    /// entry has (ordinal), a category from <see cref="RegisterCategories.All"/>, non-null
    /// <see cref="RegisterEntry.Reason"/>, <see cref="RegisterEntry.ActionTaken"/> and
    /// <see cref="RegisterEntry.Matches"/>, a non-negative <see cref="RegisterEntry.Attempts"/>, and
    /// matches whose item id and nature are non-blank. Values are kept exactly, untrimmed. When two
    /// entries list the same pair, the earlier one is the entry <see cref="FindEntry"/> returns.
    /// </summary>
    /// <param name="entries">The entries, in file order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="InvalidDataException">An entry breaks a condition above; the message names its index and id.</exception>
    public KnownLimitationsRegister(IEnumerable<RegisterEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = new List<RegisterEntry>();
        var byMatch = new Dictionary<(string ItemId, string Nature), RegisterEntry>();
        var byId = new Dictionary<string, RegisterEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var index = ordered.Count;
            var matches = Validate(entry, index, byId);
            byId.Add(entry.Id, entry);
            foreach (var match in matches)
            {
                byMatch.TryAdd((match.ItemId, match.Nature), entry);
            }

            ordered.Add(entry);
        }

        Entries = ordered.AsReadOnly();
        _byMatch = byMatch;
        _byId = byId;
    }

    /// <summary>A shared register with no entries, which documents nothing.</summary>
    public static KnownLimitationsRegister Empty { get; } = new(Array.Empty<RegisterEntry>());

    /// <summary>Every entry, in file order.</summary>
    public IReadOnlyList<RegisterEntry> Entries { get; }

    /// <summary>Reads and parses a register file, as <see cref="Parse"/> does.</summary>
    /// <param name="path">The file path, normally <c>harness/config/known-limitations.json</c>.</param>
    /// <returns>The register.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="InvalidDataException">The content is not JSON or not a valid register; the message names the path.</exception>
    public static KnownLimitationsRegister Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var json = File.ReadAllText(path, Encoding.UTF8);
        try
        {
            return Parse(json);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{path}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Parses register text: a root object with <c>version</c> equal to <see cref="SupportedVersion"/>
    /// and an <c>entries</c> array. Each entry is an object with a non-blank string <c>id</c> and a
    /// string <c>category</c>; <c>operation</c> is absent, null or a string; <c>matches</c> is absent or
    /// null (no matches) or an array of objects with string <c>itemId</c> and <c>nature</c>;
    /// <c>attempts</c> is absent (0) or a non-negative integer; <c>reason</c> and <c>actionTaken</c> are
    /// absent or null (empty) or strings. Member names are matched exactly, unknown members are
    /// ignored, and the entries are then validated as the constructor does.
    /// </summary>
    /// <param name="json">The register text; comments and trailing commas are allowed, repeated members are not.</param>
    /// <returns>The register.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="InvalidDataException">The text is not JSON or breaks a condition above; the message names the entry index and id.</exception>
    public static KnownLimitationsRegister Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{ErrorPrefix}not valid JSON: {exception.Message}", exception);
        }

        List<RegisterEntry> entries;
        using (document)
        {
            entries = ReadEntries(document.RootElement);
        }

        return new KnownLimitationsRegister(entries);
    }

    /// <summary>
    /// Returns true when the register documents <paramref name="item"/>: the item is not
    /// never-documentable and some entry lists its exact (<c>itemId</c>, <c>nature</c>) pair.
    /// <see cref="FailureItem.Operation"/> and <see cref="FailureItem.Detail"/> play no part.
    /// </summary>
    /// <param name="item">The failing item; null is never documented.</param>
    public bool IsDocumented(FailureItem? item) => FindEntry(item) is not null;

    /// <summary>
    /// Returns the first entry, in file order, that documents <paramref name="item"/> under the rule of
    /// <see cref="IsDocumented"/>; null when none does, when the item is null, and always for a
    /// never-documentable item.
    /// </summary>
    /// <param name="item">The failing item.</param>
    public RegisterEntry? FindEntry(FailureItem? item)
    {
        if (item is null || item.IsNeverDocumentable)
        {
            return null;
        }

        return _byMatch.TryGetValue((item.ItemId, item.Nature), out var entry) ? entry : null;
    }

    /// <summary>Returns the entry with id <paramref name="id"/>, compared ordinally; null when there is none.</summary>
    /// <param name="id">The entry id, for example <c>KL-SUB-NORESET</c>.</param>
    public RegisterEntry? FindById(string? id) =>
        id is not null && _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>Returns the entries whose category equals <paramref name="category"/> ordinally, in file order.</summary>
    /// <param name="category">One of the <see cref="RegisterCategories"/> values; null matches no entry.</param>
    public IEnumerable<RegisterEntry> ByCategory(string? category) =>
        Entries.Where(entry => string.Equals(entry.Category, category, StringComparison.Ordinal));


    private static RegisterMatch[] Validate(RegisterEntry? entry, int index, Dictionary<string, RegisterEntry> byId)
    {
        if (entry is null)
        {
            throw InvalidEntry(index, null, "the entry is null.");
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw InvalidEntry(index, null, $"'{IdKey}' must not be blank.");
        }

        if (byId.ContainsKey(entry.Id))
        {
            throw InvalidEntry(index, entry.Id, $"'{IdKey}' repeats the id of an earlier entry.");
        }

        if (!RegisterCategories.IsKnown(entry.Category))
        {
            throw InvalidEntry(
                index,
                entry.Id,
                $"'{CategoryKey}' is '{entry.Category}', which is not one of {string.Join(", ", RegisterCategories.All)}.");
        }

        if (entry.Reason is null)
        {
            throw InvalidEntry(index, entry.Id, $"'{ReasonKey}' must not be null.");
        }

        if (entry.ActionTaken is null)
        {
            throw InvalidEntry(index, entry.Id, $"'{ActionTakenKey}' must not be null.");
        }

        if (entry.Attempts < 0)
        {
            throw InvalidEntry(index, entry.Id, $"'{AttemptsKey}' must not be negative, but is {entry.Attempts}.");
        }

        if (entry.Matches is null)
        {
            throw InvalidEntry(index, entry.Id, $"'{MatchesKey}' must not be null.");
        }

        // A blank pair could never equal a failure item's identity, which is never blank, so it can only
        // be an editing mistake. Values are otherwise kept exactly: no trimming.
        var matches = entry.Matches.ToArray();
        for (var position = 0; position < matches.Length; position++)
        {
            var match = matches[position];
            if (match is null)
            {
                throw InvalidEntry(index, entry.Id, $"{MatchesKey}[{position}] is null.");
            }

            if (string.IsNullOrWhiteSpace(match.ItemId))
            {
                throw InvalidEntry(index, entry.Id, $"{MatchesKey}[{position}].{ItemIdKey} must not be blank.");
            }

            if (string.IsNullOrWhiteSpace(match.Nature))
            {
                throw InvalidEntry(index, entry.Id, $"{MatchesKey}[{position}].{NatureKey} must not be blank.");
            }
        }

        return matches;
    }

    private static List<RegisterEntry> ReadEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"the root must be an object, but is {root.ValueKind}.");
        }

        if (!root.TryGetProperty(VersionKey, out var version))
        {
            throw Invalid($"the root has no '{VersionKey}'.");
        }

        if (version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != SupportedVersion)
        {
            throw Invalid($"'{VersionKey}' must be {SupportedVersion}, but is {version.GetRawText()}.");
        }

        if (!root.TryGetProperty(EntriesKey, out var entries))
        {
            throw Invalid($"the root has no '{EntriesKey}'.");
        }

        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"'{EntriesKey}' must be an array, but is {entries.ValueKind}.");
        }

        var result = new List<RegisterEntry>(entries.GetArrayLength());
        foreach (var element in entries.EnumerateArray())
        {
            result.Add(ReadEntry(element, result.Count));
        }

        return result;
    }

    private static RegisterEntry ReadEntry(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidEntry(index, null, $"the entry must be an object, but is {element.ValueKind}.");
        }

        var id = RequiredString(element, IdKey, index, null, null);
        var category = RequiredString(element, CategoryKey, index, id, null);
        var operation = OptionalString(element, OperationKey, index, id);
        var matches = ReadMatches(element, index, id);
        var reason = OptionalString(element, ReasonKey, index, id) ?? string.Empty;
        var attempts = ReadAttempts(element, index, id);
        var actionTaken = OptionalString(element, ActionTakenKey, index, id) ?? string.Empty;
        return new RegisterEntry(id, category, operation, matches, reason, attempts, actionTaken);
    }

    private static IReadOnlyList<RegisterMatch> ReadMatches(JsonElement entry, int index, string id)
    {
        if (!entry.TryGetProperty(MatchesKey, out var matches) || matches.ValueKind == JsonValueKind.Null)
        {
            return ReadOnlyCollection<RegisterMatch>.Empty;
        }

        if (matches.ValueKind != JsonValueKind.Array)
        {
            throw InvalidEntry(index, id, $"'{MatchesKey}' must be an array or null, but is {matches.ValueKind}.");
        }

        var result = new List<RegisterMatch>(matches.GetArrayLength());
        foreach (var match in matches.EnumerateArray())
        {
            var path = $"{MatchesKey}[{result.Count}]";
            if (match.ValueKind != JsonValueKind.Object)
            {
                throw InvalidEntry(index, id, $"{path} must be an object, but is {match.ValueKind}.");
            }

            result.Add(new RegisterMatch(
                RequiredString(match, ItemIdKey, index, id, path),
                RequiredString(match, NatureKey, index, id, path)));
        }

        return result.AsReadOnly();
    }

    private static int ReadAttempts(JsonElement entry, int index, string id)
    {
        if (!entry.TryGetProperty(AttemptsKey, out var attempts))
        {
            return 0;
        }

        return attempts.ValueKind == JsonValueKind.Number && attempts.TryGetInt32(out var value) && value >= 0
            ? value
            : throw InvalidEntry(
                index,
                id,
                $"'{AttemptsKey}' must be a non-negative integer, but is {attempts.GetRawText()}.");
    }

    private static string RequiredString(JsonElement parent, string key, int index, string? id, string? path)
    {
        var member = path is null ? key : $"{path}.{key}";
        if (!parent.TryGetProperty(key, out var value))
        {
            throw InvalidEntry(index, id, $"'{member}' is missing.");
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw InvalidEntry(index, id, $"'{member}' must be a string, but is {value.ValueKind}.");
    }

    private static string? OptionalString(JsonElement parent, string key, int index, string id)
    {
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw InvalidEntry(index, id, $"'{key}' must be a string or null, but is {value.ValueKind}.");
    }

    private static InvalidDataException InvalidEntry(int index, string? id, string message) =>
        Invalid(string.IsNullOrWhiteSpace(id)
            ? $"{EntriesKey}[{index}]: {message}"
            : $"{EntriesKey}[{index}] ({id}): {message}");

    private static InvalidDataException Invalid(string message) => new(ErrorPrefix + message);
}

