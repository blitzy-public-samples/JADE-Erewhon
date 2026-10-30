using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Matching;

namespace Erewhon.Harness.Cases;

/// <summary>
/// The capture input <c>harness/config/case-matrix.json</c>, loaded and validated against the shared
/// operation catalogue (AAP 0.7.3). The order of <see cref="Cases"/> is the fixed replay order: REST
/// reads, SOAP reads, REST writes, SOAP writes, and last the <c>getCompany</c> empty-state case. A case's
/// 1-based position in that order is the <c>NNN</c> of its baseline record
/// <c>baseline/records/&lt;rest|soap&gt;/NNN-&lt;operation&gt;-&lt;case&gt;.json</c> (AAP 0.7.4).
/// </summary>
/// <remarks>
/// <para>
/// Every operation fact a case is checked against (surface, JADE method, state change, return type, REST
/// verb and path template, SOAP endpoint and action) comes from <see cref="OperationCatalogue"/>, through
/// <see cref="RestOperationMatcher"/> and <see cref="SoapOperationResolver"/>; this class restates none.
/// </para>
/// <para>
/// A matrix that breaks any rule fails to load with an <see cref="InvalidDataException"/> whose message
/// starts <c>case-matrix: </c> and names the first offending case. Nothing is sorted: the file's order is
/// authoritative.
/// </para>
/// <para>The instance is immutable after construction and safe for concurrent reads.</para>
/// </remarks>
public sealed partial class CaseMatrix
{
    /// <summary>The number of cases the matrix holds: 36 REST and 20 SOAP (AAP 0.7.3).</summary>
    public const int ExpectedCaseCount = 56;

    /// <summary>The only <c>version</c> of <c>case-matrix.json</c> this loader reads.</summary>
    public const int SupportedVersion = 1;

    private const string MessagePrefix = "case-matrix: ";
    private const string RestIdPrefix = "REST";
    private const string SoapIdPrefix = "SOAP";
    private const string SoapActionHeader = "SOAPAction";
    private const string AcceptHeader = "Accept";
    private const string SoapMethod = "POST";
    private const string DtoResultType = "DTOResult";
    private const int MinStatus = 100;
    private const int MaxStatus = 599;

    // Strict reading: names are matched exactly in camelCase, an unknown member is an error, and so is a
    // repeated key, which System.Text.Json would otherwise resolve silently by keeping its last value.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
    };

    private static readonly IReadOnlyList<CaseDefinition> NoCases = ReadOnlyCollection<CaseDefinition>.Empty;

    private readonly Dictionary<string, int> _positions;
    private readonly Dictionary<string, IReadOnlyList<CaseDefinition>> _groups;
    private readonly int _restCount;
    private readonly int _soapCount;

    private CaseMatrix(int version, string description, CaseDefinition[] cases, Dictionary<string, int> positions)
    {
        Version = version;
        Description = description;
        Cases = Array.AsReadOnly(cases);
        _positions = positions;

        // OrderedDictionary guarantees the documented key order, first appearance in the matrix, where a
        // plain Dictionary's enumeration order is unspecified.
        var byOperation = new OrderedDictionary<string, List<CaseDefinition>>(StringComparer.Ordinal);
        var byGroup = new Dictionary<string, List<CaseDefinition>>(StringComparer.Ordinal);
        foreach (var group in CaseGroups.Order)
        {
            byGroup.Add(group, []);
        }

        foreach (var definition in cases)
        {
            if (!byOperation.TryGetValue(definition.Operation, out var operationCases))
            {
                operationCases = [];
                byOperation.Add(definition.Operation, operationCases);
            }

            operationCases.Add(definition);
            byGroup[definition.Group].Add(definition);
            if (definition.Surface == Surface.Rest)
            {
                _restCount++;
            }
            else
            {
                _soapCount++;
            }
        }

        var operations = new OrderedDictionary<string, IReadOnlyList<CaseDefinition>>(StringComparer.Ordinal);
        foreach (var (operation, operationCases) in byOperation)
        {
            operations.Add(operation, operationCases.AsReadOnly());
        }

        ByOperation = new ReadOnlyDictionary<string, IReadOnlyList<CaseDefinition>>(operations);
        _groups = byGroup.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<CaseDefinition>)entry.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    /// <summary>The matrix file's <c>version</c>, always <see cref="SupportedVersion"/>.</summary>
    public int Version { get; }

    /// <summary>The matrix file's <c>description</c>.</summary>
    public string Description { get; }

    /// <summary>
    /// Every case in matrix order, which is the fixed replay order of AAP 0.7.3: every seed-state read runs
    /// before any write, and the empty-state <c>getCompany</c> read is last, after the writes, isolated from
    /// them by the reset to the empty state before it and the seed reset after it.
    /// </summary>
    public IReadOnlyList<CaseDefinition> Cases { get; }

    /// <summary>
    /// The cases of each operation, keyed by catalogue operation id (for example <c>Rest.getAgent</c>) with
    /// an ordinal comparer. Keys enumerate in order of first appearance in <see cref="Cases"/>, and each
    /// list is in matrix order. Every catalogue operation has an entry, because a matrix that leaves one
    /// uncovered does not load.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CaseDefinition>> ByOperation { get; }

    /// <summary>The number of operations the matrix plans cases for: 22 (AAP 0.7.3).</summary>
    public int PlannedOperationCount => ByOperation.Count;

    /// <summary>The number of planned cases: <see cref="ExpectedCaseCount"/> (AAP 0.7.3).</summary>
    public int PlannedCaseCount => Cases.Count;

    /// <summary>
    /// Reads and validates a matrix file. The file is read as UTF-8 and only read, never written.
    /// </summary>
    /// <remarks>
    /// The file is read through <see cref="ConfinedTree"/> below <paramref name="root"/>: a link on its way or at
    /// it is refused and never followed, and a FIFO, a socket, a device or a file over
    /// <see cref="HarnessOptions.EvidenceByteLimit"/> fails at once, so nothing blocks the run. The bytes are
    /// decoded as <see cref="File.ReadAllText(string, Encoding)"/> decodes them: UTF-8 unless a byte order mark
    /// names another encoding.
    /// </remarks>
    /// <param name="path">The path of <c>case-matrix.json</c>, normally <c>HarnessOptions.CaseMatrixPath</c>.</param>
    /// <param name="catalogue">The catalogue the cases are validated against, normally <see cref="OperationCatalogue.Default"/>.</param>
    /// <param name="root">
    /// The directory the read is confined to, normally the repository root; null confines it to the file's own
    /// directory.
    /// </param>
    /// <returns>The validated matrix.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="catalogue"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist, or a directory stands at its name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="IOException">
    /// The path is not below <paramref name="root"/>, the file or a directory on its way is a symbolic link, the
    /// file is not a regular file or exceeds <see cref="HarnessOptions.EvidenceByteLimit"/>, or it cannot be read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The operating system denies access on the way.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown.
    /// </exception>
    /// <exception cref="InvalidDataException">The content breaks a rule of <see cref="Parse"/>.</exception>
    public static CaseMatrix Load(string path, OperationCatalogue catalogue, string? root = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(catalogue);
        var fullPath = Path.GetFullPath(path);
        var bytes = new ConfinedTree(root ?? Path.GetDirectoryName(fullPath) ?? fullPath)
            .ReadFile(fullPath, HarnessOptions.EvidenceByteLimit)
            ?? throw ConfinedTree.NotFound(fullPath);
        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd(), catalogue);
    }

    /// <summary>
    /// Parses and validates matrix JSON against the catalogue (AAP 0.7.1, 0.7.3).
    /// </summary>
    /// <remarks>
    /// The document must be <c>{"version":1,"description":...,"cases":[...]}</c> with no unknown or repeated
    /// member. Each case must have a well-formed unique id agreeing with its catalogue operation; a known
    /// kind, pre-state and group; the catalogue's <c>stateChanging</c>; a group order of rest-read,
    /// soap-read, rest-write, soap-write, empty-state with each group on its surface and read or write
    /// side; the empty pre-state exactly in the empty-state group; an HTTP status in 100-599; a
    /// <c>DTOResult</c> status code only on a SOAP operation returning <c>DTOResult</c>; no <c>Accept</c>
    /// header; for REST, a direct path with no query that the shared matcher maps to the case's
    /// operation; for SOAP, a <c>POST</c> to the catalogue endpoint whose <c>SOAPAction</c> resolves to the
    /// case's operation, with a body. The matrix must hold <see cref="ExpectedCaseCount"/> cases and cover
    /// every catalogue operation.
    /// </remarks>
    /// <param name="json">The matrix JSON text.</param>
    /// <param name="catalogue">The catalogue the cases are validated against.</param>
    /// <returns>The validated matrix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> or <paramref name="catalogue"/> is null.</exception>
    /// <exception cref="InvalidDataException">
    /// The JSON is invalid or breaks a rule; the message starts <c>case-matrix: </c> and names the case.
    /// </exception>
    public static CaseMatrix Parse(string json, OperationCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(catalogue);

        var raw = Deserialize(json);
        if (raw.Version is not int version)
        {
            throw Invalid("missing 'version'");
        }

        if (version != SupportedVersion)
        {
            throw Invalid($"unsupported version {version}; this loader reads version {SupportedVersion}");
        }

        if (raw.Description is null)
        {
            throw Invalid("missing 'description'");
        }

        if (string.IsNullOrWhiteSpace(raw.Description))
        {
            throw Invalid("'description' is empty");
        }

        if (raw.Cases is null)
        {
            throw Invalid("missing 'cases'");
        }

        var validator = new CaseValidator(catalogue);
        var cases = new CaseDefinition[raw.Cases.Count];
        for (var index = 0; index < raw.Cases.Count; index++)
        {
            var definition = MapCase(raw.Cases[index], index);
            validator.Validate(definition, index);
            cases[index] = definition;
        }

        if (cases.Length != ExpectedCaseCount)
        {
            throw Invalid($"expected {ExpectedCaseCount} cases, found {cases.Length}");
        }

        var covered = cases.Select(definition => definition.Operation).ToHashSet(StringComparer.Ordinal);
        var uncovered = catalogue.Operations
            .Where(operation => !covered.Contains(operation.Id))
            .Select(operation => operation.Id)
            .ToList();
        if (uncovered.Count > 0)
        {
            throw Invalid($"catalogue operations with no case: {string.Join(", ", uncovered)}");
        }

        return new CaseMatrix(version, raw.Description, cases, validator.Positions);
    }

    /// <summary>
    /// Returns a case's 1-based position in matrix order, which is the <c>NNN</c> of its baseline record
    /// and the record's <c>order</c> (AAP 0.7.4).
    /// </summary>
    /// <param name="caseId">The case id, compared ordinally, for example <c>REST-getAgent-01</c>.</param>
    /// <returns>The position, from 1 to <see cref="PlannedCaseCount"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caseId"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The matrix has no case with that id; the message names it.</exception>
    public int PositionOf(string caseId)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        return _positions.TryGetValue(caseId, out var position)
            ? position
            : throw UnknownCase(caseId);
    }

    /// <summary>Finds a case by id.</summary>
    /// <param name="caseId">The case id, compared ordinally.</param>
    /// <param name="definition">The case when found; otherwise null.</param>
    /// <returns>True when the matrix has a case with that id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caseId"/> is null.</exception>
    public bool TryGetCase(string caseId, [NotNullWhen(true)] out CaseDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        if (_positions.TryGetValue(caseId, out var position))
        {
            definition = Cases[position - 1];
            return true;
        }

        definition = null;
        return false;
    }

    /// <summary>Returns a case by id.</summary>
    /// <param name="caseId">The case id, compared ordinally.</param>
    /// <returns>The case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caseId"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The matrix has no case with that id; the message names it.</exception>
    public CaseDefinition GetCase(string caseId) =>
        TryGetCase(caseId, out var definition) ? definition : throw UnknownCase(caseId);

    /// <summary>
    /// Returns the cases of one group of <see cref="CaseGroups.Order"/> in matrix order (AAP 0.7.3).
    /// </summary>
    /// <param name="group">The group name, compared ordinally, for example <see cref="CaseGroups.RestRead"/>.</param>
    /// <returns>The group's cases; empty for a group with no case or an unknown name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    public IReadOnlyList<CaseDefinition> GetGroup(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _groups.TryGetValue(group, out var cases) ? cases : NoCases;
    }

    /// <summary>Returns the number of planned cases on one surface: 36 REST and 20 SOAP (AAP 0.7.3).</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>The surface's case count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined <see cref="Surface"/>.</exception>
    public int CountFor(Surface surface) => surface switch
    {
        Surface.Rest => _restCount,
        Surface.Soap => _soapCount,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };

    private static RawMatrix Deserialize(string json)
    {
        RawMatrix? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawMatrix>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            var path = ex.Path ?? "$";
            var caseId = TryFindCaseId(json, path);
            var at = caseId is null ? path : $"{path} (case '{caseId}')";
            throw new InvalidDataException($"{MessagePrefix}invalid JSON at {at}: {ex.Message}", ex);
        }

        return raw ?? throw Invalid("document is empty");
    }

    // Names the case a deserialisation error sits in. The document may not even parse, so every failure
    // here only means no id can be named.
    private static string? TryFindCaseId(string json, string path)
    {
        var match = CasePathPattern().Match(path);
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, out var index))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("cases", out var cases)
                && cases.ValueKind == JsonValueKind.Array
                && index < cases.GetArrayLength()
                && cases[index] is { ValueKind: JsonValueKind.Object } element
                && element.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CaseDefinition MapCase(RawCase? raw, int index)
    {
        if (raw is null)
        {
            throw Invalid($"{Label(null, index)}: is null");
        }

        var label = Label(raw.Id, index);
        var id = raw.Id ?? throw Missing(label, "id");
        var operation = raw.Operation ?? throw Missing(label, "operation");
        var kind = raw.Kind ?? throw Missing(label, "kind");
        var stateChanging = raw.StateChanging ?? throw Missing(label, "stateChanging");
        var preState = raw.PreState ?? throw Missing(label, "preState");
        var group = raw.Group ?? throw Missing(label, "group");
        var request = raw.Request ?? throw Missing(label, "request");
        var method = request.Method ?? throw Missing(label, "request.method");
        var pathAndQuery = request.PathAndQuery ?? throw Missing(label, "request.pathAndQuery");
        var rawHeaders = request.Headers ?? throw Missing(label, "request.headers");
        var expectedStatus = raw.ExpectedStatus ?? throw Missing(label, "expectedStatus");
        var description = raw.Description ?? throw Missing(label, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw Invalid($"{label}: 'description' is empty");
        }

        // An ordered map keeps the headers in the file's order, so a recorded request lists them as written.
        var headers = new OrderedDictionary<string, string>(rawHeaders.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in rawHeaders)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Invalid($"{label}: request.headers has an empty or blank header name");
            }

            if (value is null)
            {
                throw Invalid($"{label}: request.headers '{name}' is null");
            }

            if (!headers.TryAdd(name, value))
            {
                throw Invalid($"{label}: request.headers names '{name}' more than once (header names ignore case)");
            }
        }

        return new CaseDefinition(
            id,
            operation,
            kind,
            stateChanging,
            preState,
            group,
            new CaseRequest(method, pathAndQuery, new ReadOnlyDictionary<string, string>(headers), request.BodyText),
            expectedStatus,
            raw.ExpectedResultStatusCode,
            description);
    }

    private static string Label(string? caseId, int index) =>
        caseId is null ? $"case #{index + 1} (no id)" : $"case '{caseId}' (#{index + 1})";

    private static InvalidDataException Invalid(string message) => new(MessagePrefix + message);

    private static InvalidDataException Missing(string label, string member) => Invalid($"{label}: missing '{member}'");

    private static KeyNotFoundException UnknownCase(string caseId) =>
        new($"The case matrix has no case with id '{caseId}'.");

    private static string Lower(bool value) => value ? "true" : "false";

    // '\z' rather than '$': '$' also matches before a final newline, which would admit "REST-getAgent-01\n".
    // '[0-9]' rather than '\d', which also matches non-ASCII digits.
    [GeneratedRegex(@"^(REST|SOAP)-([A-Za-z]+)-([0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();

    [GeneratedRegex(@"^\$\.cases\[([0-9]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CasePathPattern();


    /// <summary>
    /// Checks each case, in matrix order, against the catalogue and against the cases before it. One
    /// instance serves one <see cref="Parse"/> call, so the matcher and resolver are built once per call.
    /// </summary>
    private sealed class CaseValidator(OperationCatalogue catalogue)
    {
        private readonly OperationCatalogue _catalogue = catalogue;
        private readonly RestOperationMatcher _restMatcher = new(catalogue);
        private readonly SoapOperationResolver _soapResolver = new(catalogue);
        private readonly string _soapEndpoint = catalogue.Addresses.Soap.EndpointPathAndQuery;
        private int _previousGroupIndex = -1;
        private string? _previousLabel;

        /// <summary>The 1-based position of every case validated so far, keyed by id (ordinal).</summary>
        public Dictionary<string, int> Positions { get; } = new(StringComparer.Ordinal);

        public void Validate(CaseDefinition definition, int index)
        {
            var label = Label(definition.Id, index);
            var idMatch = CaseIdPattern().Match(definition.Id);
            if (!idMatch.Success)
            {
                throw Invalid($"{label}: id does not have the form <REST|SOAP>-<jadeMethod>-<NN>");
            }

            if (Positions.TryGetValue(definition.Id, out var firstPosition))
            {
                throw Invalid($"{label}: duplicate id '{definition.Id}', already used by case #{firstPosition}");
            }

            var operation = ValidateOperation(definition, label, idMatch);
            ValidateEnumerations(definition, label);
            if (definition.StateChanging != operation.StateChanging)
            {
                throw Invalid(
                    $"{label}: stateChanging is {Lower(definition.StateChanging)} but catalogue operation " +
                    $"'{operation.Id}' has {Lower(operation.StateChanging)}");
            }

            ValidateGroup(definition, label, operation);
            ValidateStatuses(definition, label, operation);
            if (definition.GetHeader(AcceptHeader) is not null)
            {
                throw Invalid($"{label}: request has an '{AcceptHeader}' header; characterised requests carry none");
            }

            if (operation.Surface == Surface.Rest)
            {
                ValidateRest(definition.Request, label, operation);
            }
            else
            {
                ValidateSoap(definition.Request, label, operation);
            }

            Positions.Add(definition.Id, index + 1);
        }

        private OperationDescriptor ValidateOperation(CaseDefinition definition, string label, Match idMatch)
        {
            if (!_catalogue.TryGetById(definition.Operation, out var operation))
            {
                throw Invalid($"{label}: operation '{definition.Operation}' is not a catalogue operation id");
            }

            var idPrefix = idMatch.Groups[1].Value;
            var expectedPrefix = operation.Surface == Surface.Rest ? RestIdPrefix : SoapIdPrefix;
            if (!string.Equals(idPrefix, expectedPrefix, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{label}: id prefix '{idPrefix}' does not match catalogue operation '{operation.Id}', " +
                    $"which is {operation.Surface} (prefix '{expectedPrefix}')");
            }

            var idMethod = idMatch.Groups[2].Value;
            if (!string.Equals(idMethod, operation.JadeMethod, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{label}: id names JADE method '{idMethod}' but catalogue operation '{operation.Id}' " +
                    $"is '{operation.JadeMethod}'");
            }

            return operation;
        }

        private static void ValidateEnumerations(CaseDefinition definition, string label)
        {
            if (definition.Kind is not (CaseKinds.Success or CaseKinds.Error))
            {
                throw Invalid($"{label}: kind '{definition.Kind}' is not '{CaseKinds.Success}' or '{CaseKinds.Error}'");
            }

            if (definition.PreState is not (CasePreStates.Seed or CasePreStates.Empty))
            {
                throw Invalid(
                    $"{label}: preState '{definition.PreState}' is not '{CasePreStates.Seed}' or '{CasePreStates.Empty}'");
            }

            if (CaseGroups.IndexOf(definition.Group) < 0)
            {
                throw Invalid($"{label}: group '{definition.Group}' is not one of {string.Join(", ", CaseGroups.Order)}");
            }
        }

        private void ValidateGroup(CaseDefinition definition, string label, OperationDescriptor operation)
        {
            var groupIndex = CaseGroups.IndexOf(definition.Group);
            if (groupIndex < _previousGroupIndex)
            {
                throw Invalid(
                    $"{label}: group '{definition.Group}' follows group '{CaseGroups.Order[_previousGroupIndex]}' " +
                    $"of {_previousLabel}; groups run in the order {string.Join(", ", CaseGroups.Order)}");
            }

            _previousGroupIndex = groupIndex;
            _previousLabel = label;

            // The empty-state group is bound by its pre-state below, not by surface or read/write side.
            (Surface Surface, bool StateChanging)? side = definition.Group switch
            {
                CaseGroups.RestRead => (Surface.Rest, false),
                CaseGroups.SoapRead => (Surface.Soap, false),
                CaseGroups.RestWrite => (Surface.Rest, true),
                CaseGroups.SoapWrite => (Surface.Soap, true),
                _ => null,
            };

            if (side is { } expected)
            {
                if (operation.Surface != expected.Surface)
                {
                    throw Invalid(
                        $"{label}: group '{definition.Group}' holds only {expected.Surface} cases, but catalogue " +
                        $"operation '{operation.Id}' is {operation.Surface}");
                }

                if (operation.StateChanging != expected.StateChanging)
                {
                    throw Invalid(
                        $"{label}: group '{definition.Group}' holds only operations with stateChanging " +
                        $"{Lower(expected.StateChanging)}, but catalogue operation '{operation.Id}' has " +
                        $"{Lower(operation.StateChanging)}");
                }
            }

            var inEmptyGroup = string.Equals(definition.Group, CaseGroups.EmptyState, StringComparison.Ordinal);
            if (inEmptyGroup != definition.IsEmptyState)
            {
                throw Invalid(
                    $"{label}: preState '{definition.PreState}' in group '{definition.Group}'; preState " +
                    $"'{CasePreStates.Empty}' belongs exactly to group '{CaseGroups.EmptyState}'");
            }
        }

        private static void ValidateStatuses(CaseDefinition definition, string label, OperationDescriptor operation)
        {
            if (definition.ExpectedStatus is < MinStatus or > MaxStatus)
            {
                throw Invalid(
                    $"{label}: expectedStatus {definition.ExpectedStatus} is not an HTTP status ({MinStatus}-{MaxStatus})");
            }

            if (definition.ExpectedResultStatusCode is int resultStatusCode
                && (operation.Surface != Surface.Soap
                    || !string.Equals(operation.ReturnType, DtoResultType, StringComparison.Ordinal)))
            {
                throw Invalid(
                    $"{label}: expectedResultStatusCode {resultStatusCode} is allowed only on a SOAP operation " +
                    $"returning {DtoResultType}, but catalogue operation '{operation.Id}' is {operation.Surface} " +
                    $"returning {operation.ReturnType}");
            }
        }

        private void ValidateRest(CaseRequest request, string label, OperationDescriptor operation)
        {
            if (!request.PathAndQuery.StartsWith('/'))
            {
                throw Invalid($"{label}: REST pathAndQuery '{request.PathAndQuery}' does not start with '/'");
            }

            if (request.QueryPart is not null)
            {
                throw Invalid(
                    $"{label}: REST pathAndQuery '{request.PathAndQuery}' has a query; characterised REST requests carry none");
            }

            var matched = _restMatcher.TryMatch(request.Method, request.PathPart, out var match) ? match.Operation.Id : null;
            if (!string.Equals(matched, operation.Id, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{label}: {request.Method} {request.PathAndQuery} matches catalogue operation " +
                    $"{Quote(matched)}, not '{operation.Id}'");
            }
        }

        private void ValidateSoap(CaseRequest request, string label, OperationDescriptor operation)
        {
            if (!string.Equals(request.Method, SoapMethod, StringComparison.Ordinal))
            {
                throw Invalid($"{label}: SOAP request method is '{request.Method}', not '{SoapMethod}'");
            }

            if (!string.Equals(request.PathAndQuery, _soapEndpoint, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{label}: SOAP pathAndQuery '{request.PathAndQuery}' is not the catalogue endpoint '{_soapEndpoint}'");
            }

            // The raw value goes to the resolver, quotes included: stripping them is the resolver's rule.
            var soapAction = request.GetHeader(SoapActionHeader)
                ?? throw Invalid($"{label}: SOAP request has no '{SoapActionHeader}' header");
            var resolved = _soapResolver.ResolveFromSoapAction(soapAction)?.Id;
            if (!string.Equals(resolved, operation.Id, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"{label}: {SoapActionHeader} {soapAction} resolves to catalogue operation " +
                    $"{Quote(resolved)}, not '{operation.Id}'");
            }

            if (string.IsNullOrWhiteSpace(request.BodyText))
            {
                throw Invalid($"{label}: SOAP request has no 'bodyText'");
            }
        }

        private static string Quote(string? operationId) => operationId is null ? "none" : $"'{operationId}'";
    }

    // Deserialisation-only shapes. Every member is nullable, so a missing member is detected here and
    // reported by name, whatever System.Text.Json does with absent constructor parameters.
    private sealed class RawMatrix
    {
        public int? Version { get; init; }

        public string? Description { get; init; }

        public List<RawCase?>? Cases { get; init; }
    }

    private sealed class RawCase
    {
        public string? Id { get; init; }

        public string? Operation { get; init; }

        public string? Kind { get; init; }

        public bool? StateChanging { get; init; }

        public string? PreState { get; init; }

        public string? Group { get; init; }

        public RawRequest? Request { get; init; }

        public int? ExpectedStatus { get; init; }

        public int? ExpectedResultStatusCode { get; init; }

        public string? Description { get; init; }
    }

    private sealed class RawRequest
    {
        public string? Method { get; init; }

        public string? PathAndQuery { get; init; }

        public Dictionary<string, string?>? Headers { get; init; }

        public string? BodyText { get; init; }
    }
}
