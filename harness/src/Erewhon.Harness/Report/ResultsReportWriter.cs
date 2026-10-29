using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Report;

// The report's input types live in this file and Modes/ReplayMode.cs implements IReportSource, so
// the report never references a Modes, Checks, Lifecycle or Baseline type and the dependency between
// replay and report stays one-directional.

/// <summary>
/// The comparison outcome of one captured case: one instance per baseline record, in record order
/// (AAP 0.7.5, 0.7.7).
/// </summary>
/// <remarks>
/// Cases the stand-in could not answer at capture have no baseline record, so they never appear as a
/// <see cref="CaseResult"/>. They reach the report only through <see cref="BaselineFacts.NotCaptured"/>
/// and <c>CASE:&lt;caseId&gt;</c> items with nature <c>not-captured</c>, and the report never presents
/// them as captured or compared.
/// </remarks>
/// <param name="CaseId">The case id from the case matrix, for example <c>REST-getAgent-01</c>.</param>
/// <param name="Operation">The catalogue operation id, for example <c>Rest.getAgent</c>.</param>
/// <param name="Order">The record's replay position.</param>
/// <param name="Compared">
/// True when a comparison was performed. A case that received no response still counts as compared,
/// with a <c>no-response:&lt;reason&gt;</c> discrepancy.
/// </param>
/// <param name="Equivalent">True when the comparison found no discrepancy.</param>
/// <param name="Discrepancies">Every discrepancy found, in canonical order; empty when equivalent.</param>
/// <param name="WellFormed">True when the observed body is well formed for its surface.</param>
/// <param name="StatusMatched">True when the observed status code equals the baseline's.</param>
/// <param name="BaselineStatus">The recorded status code.</param>
/// <param name="ObservedStatus">The observed status code; null when no response was obtained.</param>
/// <param name="ItemNature">
/// The nature of the case's <c>CASE:</c> failure item, <c>d:&lt;signature&gt;</c>, when the case is not
/// equivalent; null otherwise.
/// </param>
public sealed record CaseResult(
    string CaseId,
    string Operation,
    int Order,
    bool Compared,
    bool Equivalent,
    IReadOnlyList<Discrepancy> Discrepancies,
    bool WellFormed,
    bool StatusMatched,
    int? BaselineStatus,
    int? ObservedStatus,
    string? ItemNature);

/// <summary>
/// The outcome of one separate check (AAP 0.7.8), keyed in <see cref="IReportSource.Checks"/> by its
/// <see cref="CheckNames"/> value.
/// </summary>
/// <param name="Name">One of the <see cref="CheckNames"/> values.</param>
/// <param name="Outcome">One of the <see cref="CheckOutcomes"/> values.</param>
/// <param name="Cases">The cases the check counted; 0 for checks that count none.</param>
/// <param name="Passed">The counted cases that passed.</param>
/// <param name="Detail">
/// Report context: for <see cref="CheckNames.Failure"/> the unreachable and timeout split supplied by
/// <c>Checks/FacadeTestResults.cs</c>; null when there is none.
/// </param>
public sealed record CheckResult(string Name, string Outcome, int Cases, int Passed, string? Detail);

/// <summary>The names of the separate checks, the keys of <see cref="IReportSource.Checks"/>.</summary>
public static class CheckNames
{
    /// <summary>The per-operation routing tests.</summary>
    public const string Routing = "routing";

    /// <summary>The unreachable-backend and timeout tests.</summary>
    public const string Failure = "failure";

    /// <summary>The listener-isolation tests.</summary>
    public const string ListenerIsolation = "listener-isolation";

    /// <summary>The OpenAPI drift check.</summary>
    public const string OpenApiDrift = "openapi-drift";

    /// <summary>The WSDL contract check.</summary>
    public const string WsdlContract = "wsdl-contract";

    /// <summary>The JADE-unchanged check.</summary>
    public const string JadeUnchanged = "jade-unchanged";

    /// <summary>The ordering-evidence check.</summary>
    public const string Ordering = "ordering";

    /// <summary>The stand-in conformance check.</summary>
    public const string Conformance = "conformance";

    /// <summary>The facade tests' TRX file.</summary>
    public const string FacadeTrx = "facade-trx";
}

/// <summary>The outcomes a <see cref="CheckResult"/> can have.</summary>
public static class CheckOutcomes
{
    /// <summary>The check ran and passed.</summary>
    public const string Pass = "pass";

    /// <summary>The check ran and failed.</summary>
    public const string Fail = "fail";

    /// <summary>The check did not run.</summary>
    public const string NotRun = "not-run";
}

/// <summary>The run facts the report states (AAP 0.8.2).</summary>
/// <remarks>
/// The report renders the target and backend URLs without user information, so a URL configured with a
/// user name or password never reaches <c>RESULTS.md</c> or the console.
/// </remarks>
/// <param name="UtcDate">When the run took place.</param>
/// <param name="ExecutionMode"><c>docker</c> or <c>local-processes</c>.</param>
/// <param name="FacadeFrameworkDescription">
/// The facade's <c>RuntimeInformation.FrameworkDescription</c> from its <c>/admin/info</c>; null when the
/// facade was not reached.
/// </param>
/// <param name="SdkPin">The SDK version pinned in <c>harness/global.json</c>; null when unreadable.</param>
/// <param name="TargetRestUrl">The facade REST base URL cases were sent to.</param>
/// <param name="TargetSoapUrl">The facade SOAP base URL cases were sent to.</param>
/// <param name="BackendRestUrl">The backend REST base URL.</param>
/// <param name="BackendSoapUrl">The backend SOAP base URL.</param>
/// <param name="BackendAdminUrl">The backend admin URL; null or blank for an external backend.</param>
/// <param name="StartingCommit">The starting commit the JADE-unchanged check compares against.</param>
/// <param name="BaselineCommit">The commit that first added <c>baseline/manifest.json</c>; null when not found.</param>
/// <param name="FacadeCommit">The commit that first added a <c>facade/</c> path; null when not found.</param>
public sealed record RunFacts(
    DateTimeOffset UtcDate,
    string ExecutionMode,
    string? FacadeFrameworkDescription,
    string? SdkPin,
    string TargetRestUrl,
    string TargetSoapUrl,
    string BackendRestUrl,
    string BackendSoapUrl,
    string? BackendAdminUrl,
    string StartingCommit,
    string? BaselineCommit,
    string? FacadeCommit)
{
    // dotnet itself accepts comments and trailing commas in global.json.
    private static readonly JsonDocumentOptions GlobalJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <c>sdk.version</c> from a <c>global.json</c>, normally <c>harness/global.json</c>
    /// (<c>HarnessOptions.HarnessGlobalJsonPath</c>). Never throws.
    /// </summary>
    /// <param name="globalJsonPath">The file path.</param>
    /// <returns>The pinned SDK version; null when the file is absent, unreadable or has no such value.</returns>
    public static string? TryReadSdkPin(string globalJsonPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(globalJsonPath) || !File.Exists(globalJsonPath))
            {
                return null;
            }

            using var stream = File.OpenRead(globalJsonPath);
            using var document = JsonDocument.Parse(stream, GlobalJsonOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sdk", out var sdk)
                && sdk.ValueKind == JsonValueKind.Object
                && sdk.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                var value = version.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }
        catch (Exception)
        {
            // The SDK pin is a reported fact only; an unreadable file reports it as unknown.
            return null;
        }
    }
}

/// <summary>The baseline facts the report's counts state (AAP 0.8.2).</summary>
/// <param name="PlannedOperations">Operations the case matrix plans cases for (22).</param>
/// <param name="PlannedCases">Cases the case matrix plans (56).</param>
/// <param name="CapturedOperations">Distinct operations with at least one baseline record.</param>
/// <param name="CapturedCases">Baseline records.</param>
/// <param name="NotCaptured">The manifest's <c>notCaptured</c> case ids.</param>
/// <param name="WsdlCaptured">True when the stand-in WSDL record exists.</param>
/// <param name="ManifestPresent">True when <c>baseline/manifest.json</c> was read.</param>
/// <param name="CaptureExecution">The manifest's <c>captureExecution</c>; null when absent.</param>
public sealed record BaselineFacts(
    int PlannedOperations,
    int PlannedCases,
    int CapturedOperations,
    int CapturedCases,
    IReadOnlyList<string> NotCaptured,
    bool WsdlCaptured,
    bool ManifestPresent,
    string? CaptureExecution)
{
    /// <summary>No baseline facts: every count zero, nothing captured, no manifest.</summary>
    public static BaselineFacts None { get; } = new(0, 0, 0, 0, Array.Empty<string>(), false, false, null);
}

/// <summary>The stand-in conformance counts (AAP 0.7.8).</summary>
/// <param name="MembersChecked">Catalogue members checked.</param>
/// <param name="Passed">Members that passed.</param>
/// <param name="GapsByNature">The number of gaps per nature, for example <c>unreachable-in-source</c>.</param>
public sealed record ConformanceSummary(int MembersChecked, int Passed, IReadOnlyDictionary<string, int> GapsByNature);

/// <summary>The JADE-unchanged counts (AAP 0.2.2, 0.7.8).</summary>
/// <param name="PathsChecked">Paths under <c>Erewhon/</c> checked.</param>
/// <param name="Modified">Paths modified, added or deleted since the starting commit.</param>
public sealed record JadeUnchangedSummary(int PathsChecked, int Modified);

/// <summary>
/// Everything a replay run observed, as the report reads it. <c>Modes/ReplayMode.cs</c> implements it
/// with its run state; tests implement it with inline values.
/// </summary>
public interface IReportSource
{
    /// <summary>Every failing item recorded, in recording order.</summary>
    IReadOnlyList<FailureItem> Failures { get; }

    /// <summary>One result per captured case, in record order.</summary>
    IReadOnlyList<CaseResult> Cases { get; }

    /// <summary>Catalogue operation id to its planned case ids, in matrix order.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> PlannedCasesByOperation { get; }

    /// <summary>The separate checks' results, keyed by <see cref="CheckNames"/> value.</summary>
    IReadOnlyDictionary<string, CheckResult> Checks { get; }

    /// <summary>The run facts.</summary>
    RunFacts Facts { get; }

    /// <summary>The baseline facts.</summary>
    BaselineFacts Baseline { get; }

    /// <summary>The stand-in conformance counts; null when the conformance result was not read.</summary>
    ConformanceSummary? Conformance { get; }

    /// <summary>The JADE-unchanged counts; null when the check did not run.</summary>
    JadeUnchangedSummary? JadeUnchanged { get; }

    /// <summary>True when the backend is not the stand-in, so no reset or seed confirmation took place.</summary>
    bool ExternalBackend { get; }
}

/// <summary>The per-operation results of AAP 0.7.7, spelled as the report shows them.</summary>
public static class OperationResults
{
    /// <summary>Every planned case was captured, compared and found equivalent.</summary>
    public const string Equivalent = "equivalent";

    /// <summary>Some case failed, and the register documents every failing case item.</summary>
    public const string DocumentedDifference = "documented difference";

    /// <summary>Some failing case item is not documented, or a planned case is unaccounted for.</summary>
    public const string UndocumentedDifference = "undocumented difference";

    /// <summary>None of the operation's cases was captured; counts towards neither N nor M.</summary>
    public const string NotCompared = "not compared";
}

/// <summary>One operation's row in the per-operation table (AAP 0.8.2).</summary>
/// <param name="OperationId">The catalogue id, for example <c>Soap.getPhoto</c>.</param>
/// <param name="JadeMethod">The JADE method name, for example <c>getPhoto</c>.</param>
/// <param name="Surface">The surface; null when it cannot be derived from an id without a catalogue.</param>
/// <param name="Planned">Planned cases.</param>
/// <param name="Captured">Captured cases, that is cases with a baseline record that replay reported.</param>
/// <param name="Compared">Captured cases that were compared.</param>
/// <param name="EquivalentCases">Compared cases found equivalent.</param>
/// <param name="Result">One of the <see cref="OperationResults"/> values.</param>
/// <param name="FailingItems">The operation's failing <c>CASE:</c> items.</param>
public sealed record OperationRow(
    string OperationId,
    string JadeMethod,
    Surface? Surface,
    int Planned,
    int Captured,
    int Compared,
    int EquivalentCases,
    string Result,
    IReadOnlyList<FailureItem> FailingItems);

/// <summary>A failing item with the register entry that documents it, if any (AAP 0.7.7).</summary>
/// <param name="Item">The failing item.</param>
/// <param name="Entry">The first register entry, in file order, that documents the item; null when none does.</param>
/// <param name="Documented">True when the register documents the item.</param>
public sealed record ReportedFailure(FailureItem Item, RegisterEntry? Entry, bool Documented);

/// <summary>
/// The computed content of <c>RESULTS.md</c> (AAP 0.8.2): the per-operation results, N, M, the final
/// line, demo acceptance and whether every failing item is documented, which
/// <c>Modes/ReplayMode.cs</c> uses for the exit code (AAP 0.7.7, 0.8.4).
/// </summary>
/// <remarks>
/// <see cref="Build"/> snapshots the source, so the report and its rendering stay consistent even if
/// the source changes afterwards. An instance is immutable.
/// </remarks>
public sealed class ResultsReport
{
    /// <summary>The number of carried-over operations the final line counts against.</summary>
    public const int TotalOperations = 22;

    private const int DemoAcceptanceThreshold = 20;
    private const string RestIdPrefix = "Rest.";
    private const string SoapIdPrefix = "Soap.";

    private ResultsReport(
        IReportSource source,
        KnownLimitationsRegister register,
        Snapshot snapshot,
        IReadOnlyList<OperationRow> operations,
        IReadOnlyList<ReportedFailure> failures,
        bool catalogueAvailable)
    {
        Source = source;
        Register = register;
        Operations = operations;
        Failures = failures;
        CatalogueAvailable = catalogueAvailable;
        Facts = snapshot.Facts;
        Baseline = snapshot.Baseline;
        Cases = snapshot.Cases;
        Checks = snapshot.Checks;
        Conformance = snapshot.Conformance;
        JadeUnchanged = snapshot.JadeUnchanged;
        ExternalBackend = snapshot.ExternalBackend;
        NotCaptured = snapshot.NotCaptured;

        EquivalentCount = CountResult(operations, OperationResults.Equivalent);
        DocumentedDifferenceCount = CountResult(operations, OperationResults.DocumentedDifference);
        UndocumentedDifferenceCount = CountResult(operations, OperationResults.UndocumentedDifference);
        NotComparedCount = CountResult(operations, OperationResults.NotCompared);

        UndocumentedFailures = Array.AsReadOnly(failures.Where(failure => !failure.Documented).Select(failure => failure.Item).ToArray());
        AllFailuresDocumented = UndocumentedFailures.Count == 0;
        FinalLine = FormatFinalLine(EquivalentCount, DocumentedDifferenceCount);

        var casesById = CaseFailuresById(failures);
        CasesCompared = Cases.Count(result => result.Compared);
        CasesEquivalent = Cases.Count(result => result.Compared && result.Equivalent);
        CasesDocumentedDifference = Cases.Count(result =>
            result.Compared && !result.Equivalent && IsCaseDocumented(result.CaseId, casesById));
        CasesUndocumentedDifference = Cases.Count(result =>
            result.Compared && !result.Equivalent && !IsCaseDocumented(result.CaseId, casesById));

        PlannedCaseTotal = Baseline.PlannedCases > 0 ? Baseline.PlannedCases : operations.Sum(row => row.Planned);
        PlannedOperationTotal = Baseline.PlannedOperations > 0
            ? Baseline.PlannedOperations
            : operations.Count(row => row.Planned > 0);

        DemoAcceptanceShortfall = DemoShortfall(EquivalentCount, operations, Cases);
        DemoAcceptanceMet = DemoAcceptanceShortfall.Count == 0;
    }

    /// <summary>
    /// The source the report was built from, as given and still live. <see cref="Build"/> copies
    /// everything the report holds from it, so no value of the report reads it afterwards and a later
    /// change to it changes neither the report nor its rendering.
    /// </summary>
    public IReportSource Source { get; }

    /// <summary>The register failing items were matched against.</summary>
    public KnownLimitationsRegister Register { get; }

    /// <summary>One row per operation: catalogue order, or ordinal id order without a catalogue.</summary>
    public IReadOnlyList<OperationRow> Operations { get; }

    /// <summary>
    /// Every failing item, de-duplicated by (<c>ItemId</c>, <c>Nature</c>): the source's items in their
    /// order, then the <c>not-captured</c> and case items the source did not already hold.
    /// </summary>
    public IReadOnlyList<ReportedFailure> Failures { get; }

    /// <summary>N: operations whose result is <see cref="OperationResults.Equivalent"/>.</summary>
    public int EquivalentCount { get; }

    /// <summary>M: operations whose result is <see cref="OperationResults.DocumentedDifference"/>.</summary>
    public int DocumentedDifferenceCount { get; }

    /// <summary>Operations whose result is <see cref="OperationResults.UndocumentedDifference"/>.</summary>
    public int UndocumentedDifferenceCount { get; }

    /// <summary>Operations whose result is <see cref="OperationResults.NotCompared"/>.</summary>
    public int NotComparedCount { get; }

    /// <summary>
    /// True when the register documents every failing item; vacuously true when there is none. Never
    /// true with a <c>CHK-REPORT</c>, <c>CHK-SHUTDOWN</c> or <c>CHK-EXCEPTION</c> item, which no entry
    /// can document (AAP 0.7.7).
    /// </summary>
    public bool AllFailuresDocumented { get; }

    /// <summary>The failing items the register does not document, in <see cref="Failures"/> order.</summary>
    public IReadOnlyList<FailureItem> UndocumentedFailures { get; }

    /// <summary>
    /// True when at least 20 operations are equivalent and every other operation has a compared case
    /// and returns the baseline status with a well-formed payload in each. Reported, never enforced.
    /// </summary>
    public bool DemoAcceptanceMet { get; }

    /// <summary>Why demo acceptance is not met; empty when it is.</summary>
    public IReadOnlyList<string> DemoAcceptanceShortfall { get; }

    /// <summary><c>N of 22 operations equivalent, M documented differences</c>.</summary>
    public string FinalLine { get; }

    /// <summary>False when the report was built without the operation catalogue.</summary>
    public bool CatalogueAvailable { get; }

    internal RunFacts? Facts { get; }

    internal BaselineFacts Baseline { get; }

    internal IReadOnlyList<CaseResult> Cases { get; }

    internal IReadOnlyDictionary<string, CheckResult> Checks { get; }

    internal ConformanceSummary? Conformance { get; }

    internal JadeUnchangedSummary? JadeUnchanged { get; }

    internal bool ExternalBackend { get; }

    internal IReadOnlyList<string> NotCaptured { get; }

    internal int CasesCompared { get; }

    internal int CasesEquivalent { get; }

    internal int CasesDocumentedDifference { get; }

    internal int CasesUndocumentedDifference { get; }

    internal int PlannedCaseTotal { get; }

    internal int PlannedOperationTotal { get; }

    /// <summary>Returns the final line exactly: <c>&lt;n&gt; of 22 operations equivalent, &lt;m&gt; documented differences</c>.</summary>
    /// <param name="n">Equivalent operations.</param>
    /// <param name="m">Operations with a documented difference.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public static string FormatFinalLine(int n, int m)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfNegative(m);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{n} of {TotalOperations} operations equivalent, {m} documented differences");
    }

    /// <summary>
    /// Builds the report from whatever the source holds. Total: a partial or empty source (no cases,
    /// plans, checks or conformance) still yields a report, because <c>Modes/ReplayMode.cs</c> calls this
    /// on every exit path.
    /// </summary>
    /// <param name="source">The run's observations.</param>
    /// <param name="register">The known-limitations register.</param>
    /// <param name="catalogue">
    /// The operation catalogue, which supplies the 22 rows in catalogue order; null when it could not be
    /// loaded, in which case rows are derived from the planned and replayed cases.
    /// </param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="register"/> is null.</exception>
    public static ResultsReport Build(IReportSource source, KnownLimitationsRegister register, OperationCatalogue? catalogue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(register);

        var snapshot = Snapshot.Take(source);
        var caseOperations = IndexCaseOperations(snapshot.Cases, snapshot.Planned);
        var failures = CollectFailures(snapshot, caseOperations, register);

        var casesByOperation = new Dictionary<string, List<CaseResult>>(StringComparer.Ordinal);
        foreach (var result in snapshot.Cases)
        {
            if (OperationOf(result) is { } operation)
            {
                GetOrAdd(casesByOperation, operation).Add(result);
            }
        }

        var caseFailuresByOperation = new Dictionary<string, List<ReportedFailure>>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            if (failure.Item.IsCaseItem && !string.IsNullOrWhiteSpace(failure.Item.Operation))
            {
                GetOrAdd(caseFailuresByOperation, failure.Item.Operation).Add(failure);
            }
        }

        var descriptors = catalogue is not null
            ? catalogue.Operations.Select(operation => (operation.Id, operation.JadeMethod, (Surface?)operation.Surface))
            : DeriveOperations(snapshot.Planned.Keys, casesByOperation.Keys, caseFailuresByOperation.Keys);

        var rows = new List<OperationRow>();
        foreach (var (id, jadeMethod, surface) in descriptors)
        {
            var cases = casesByOperation.GetValueOrDefault(id) ?? [];
            var caseFailures = caseFailuresByOperation.GetValueOrDefault(id) ?? [];
            var planned = snapshot.Planned.TryGetValue(id, out var plannedIds) && plannedIds.Count > 0
                ? plannedIds
                : FallbackPlanned(id, cases, caseFailures, snapshot.NotCaptured, caseOperations);

            rows.Add(new OperationRow(
                id,
                jadeMethod,
                surface,
                planned.Count,
                cases.Count,
                cases.Count(result => result.Compared),
                cases.Count(result => result.Compared && result.Equivalent),
                Classify(planned, cases, caseFailures),
                Array.AsReadOnly(caseFailures.Select(failure => failure.Item).ToArray())));
        }

        return new ResultsReport(source, register, snapshot, rows.AsReadOnly(), failures, catalogue is not null);
    }

    private static int CountResult(IReadOnlyList<OperationRow> rows, string result) =>
        rows.Count(row => string.Equals(row.Result, result, StringComparison.Ordinal));

    private static List<TValue> GetOrAdd<TValue>(Dictionary<string, List<TValue>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map.Add(key, list);
        }

        return list;
    }

    private static string? OperationOf(CaseResult result) =>
        !string.IsNullOrWhiteSpace(result.Operation)
            ? result.Operation
            : FailureItemIds.OperationIdFromCaseId(result.CaseId);

    // Case id to operation, from the case results first and the plan second. A CASE: item that names
    // no operation is attributed through this index before falling back to the case id's own shape.
    private static Dictionary<string, string> IndexCaseOperations(
        IReadOnlyList<CaseResult> cases,
        IReadOnlyDictionary<string, IReadOnlyList<string>> planned)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var result in cases)
        {
            if (!string.IsNullOrEmpty(result.CaseId) && OperationOf(result) is { } operation)
            {
                index.TryAdd(result.CaseId, operation);
            }
        }

        foreach (var (operation, ids) in planned)
        {
            foreach (var id in ids)
            {
                index.TryAdd(id, operation);
            }
        }

        return index;
    }

    private static string? AttributeCase(string caseId, Dictionary<string, string> caseOperations) =>
        caseOperations.GetValueOrDefault(caseId) ?? FailureItemIds.OperationIdFromCaseId(caseId);

    private static ReadOnlyCollection<ReportedFailure> CollectFailures(
        Snapshot snapshot,
        Dictionary<string, string> caseOperations,
        KnownLimitationsRegister register)
    {
        var seen = new HashSet<(string ItemId, string Nature)>();
        var items = new List<FailureItem>();

        void Add(FailureItem item)
        {
            if (!seen.Add((item.ItemId, item.Nature)))
            {
                return;
            }

            // Operation is report context only and plays no part in register matching, so filling it
            // in for a case item that lacks it changes nothing but the report's attribution.
            if (string.IsNullOrWhiteSpace(item.Operation)
                && FailureItemIds.TryGetCaseId(item.ItemId, out var caseId)
                && AttributeCase(caseId, caseOperations) is { } operation)
            {
                item = item with { Operation = operation };
            }

            items.Add(item);
        }

        foreach (var item in snapshot.Failures)
        {
            Add(item);
        }

        foreach (var caseId in snapshot.NotCaptured)
        {
            Add(new FailureItem(FailureItemIds.Case(caseId), FailureItemIds.NotCaptured, AttributeCase(caseId, caseOperations)));
        }

        foreach (var result in snapshot.Cases)
        {
            if (!result.Compared || result.Equivalent || string.IsNullOrWhiteSpace(result.CaseId))
            {
                continue;
            }

            // A compared, non-equivalent case always yields a failing item. Without a supplied nature it
            // gets the d:<signature> the replay computes from the same discrepancies, so a missing nature
            // can never turn a difference into an unreported one.
            var nature = !string.IsNullOrWhiteSpace(result.ItemNature)
                ? result.ItemNature
                : FailureItemIds.Diff(DiscrepancySignature.Compute(
                    (result.Discrepancies ?? Array.Empty<Discrepancy>()).Where(discrepancy => discrepancy is not null)));
            Add(new FailureItem(FailureItemIds.Case(result.CaseId), nature, OperationOf(result)));
        }

        return Array.AsReadOnly(items
            .Select(item => new ReportedFailure(item, register.FindEntry(item), register.IsDocumented(item)))
            .ToArray());
    }

    private static IEnumerable<(string Id, string JadeMethod, Surface? Surface)> DeriveOperations(
        params IEnumerable<string>[] idSources)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in idSources)
        {
            foreach (var id in source)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        foreach (var id in ids)
        {
            var dot = id.IndexOf('.', StringComparison.Ordinal);
            var jadeMethod = dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
            Surface? surface = id.StartsWith(RestIdPrefix, StringComparison.Ordinal) ? Surface.Rest
                : id.StartsWith(SoapIdPrefix, StringComparison.Ordinal) ? Surface.Soap
                : null;
            yield return (id, jadeMethod, surface);
        }
    }

    private static IReadOnlyList<string> FallbackPlanned(
        string operation,
        List<CaseResult> cases,
        List<ReportedFailure> caseFailures,
        IReadOnlyList<string> notCaptured,
        Dictionary<string, string> caseOperations)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in cases)
        {
            if (!string.IsNullOrEmpty(result.CaseId) && seen.Add(result.CaseId))
            {
                ids.Add(result.CaseId);
            }
        }

        foreach (var caseId in notCaptured)
        {
            if (string.Equals(AttributeCase(caseId, caseOperations), operation, StringComparison.Ordinal) && seen.Add(caseId))
            {
                ids.Add(caseId);
            }
        }

        foreach (var failure in caseFailures)
        {
            if (FailureItemIds.TryGetCaseId(failure.Item.ItemId, out var caseId) && seen.Add(caseId))
            {
                ids.Add(caseId);
            }
        }

        return ids;
    }


    // AAP 0.7.7: equivalent when every planned case was captured, compared and equivalent; a documented
    // difference when every failing case item is documented and every planned case was either compared
    // or is itself a documented failing item; otherwise an undocumented difference.
    private static string Classify(IReadOnlyList<string> planned, List<CaseResult> cases, List<ReportedFailure> caseFailures)
    {
        if (cases.Count == 0)
        {
            return OperationResults.NotCompared;
        }

        var equivalentIds = new HashSet<string>(StringComparer.Ordinal);
        var comparedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in cases)
        {
            if (result.Compared && result.CaseId is not null)
            {
                comparedIds.Add(result.CaseId);
                if (result.Equivalent)
                {
                    equivalentIds.Add(result.CaseId);
                }
            }
        }

        if (caseFailures.Count == 0
            && cases.All(result => result.Compared && result.Equivalent)
            && planned.All(equivalentIds.Contains))
        {
            return OperationResults.Equivalent;
        }

        var failingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in caseFailures)
        {
            if (FailureItemIds.TryGetCaseId(failure.Item.ItemId, out var caseId))
            {
                failingIds.Add(caseId);
            }
        }

        var documented = caseFailures.Count > 0
            && caseFailures.All(failure => failure.Documented)
            && planned.All(id => comparedIds.Contains(id) || failingIds.Contains(id))
            && cases.All(result => (result.Compared && result.Equivalent)
                || (result.CaseId is not null && failingIds.Contains(result.CaseId)));

        return documented ? OperationResults.DocumentedDifference : OperationResults.UndocumentedDifference;
    }

    // AAP 0.7.7 demo acceptance: at least 20 equivalent operations, and every other operation returns the
    // baseline status code with a well-formed payload in each of its compared cases.
    private static ReadOnlyCollection<string> DemoShortfall(int equivalent, IReadOnlyList<OperationRow> rows, IReadOnlyList<CaseResult> cases)
    {
        var shortfall = new List<string>();
        if (equivalent < DemoAcceptanceThreshold)
        {
            shortfall.Add(string.Create(CultureInfo.InvariantCulture, $"N = {equivalent}, below {DemoAcceptanceThreshold}"));
        }

        foreach (var row in rows)
        {
            if (string.Equals(row.Result, OperationResults.Equivalent, StringComparison.Ordinal))
            {
                continue;
            }

            var compared = cases
                .Where(result => result.Compared
                    && string.Equals(OperationOf(result), row.OperationId, StringComparison.Ordinal))
                .ToArray();
            if (compared.Length == 0)
            {
                shortfall.Add($"{row.OperationId}: not compared");
                continue;
            }

            foreach (var result in compared)
            {
                if (!result.StatusMatched)
                {
                    shortfall.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{row.OperationId}: status differs in {result.CaseId} (baseline {StatusText(result.BaselineStatus)}, observed {StatusText(result.ObservedStatus)})"));
                }

                if (!result.WellFormed)
                {
                    shortfall.Add($"{row.OperationId}: malformed payload in {result.CaseId}");
                }
            }
        }

        return shortfall.AsReadOnly();
    }

    private static string StatusText(int? status) =>
        status is { } value ? value.ToString(CultureInfo.InvariantCulture) : "none";

    private static Dictionary<string, List<ReportedFailure>> CaseFailuresById(IReadOnlyList<ReportedFailure> failures)
    {
        var byId = new Dictionary<string, List<ReportedFailure>>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            if (FailureItemIds.TryGetCaseId(failure.Item.ItemId, out var caseId))
            {
                GetOrAdd(byId, caseId).Add(failure);
            }
        }

        return byId;
    }

    private static bool IsCaseDocumented(string? caseId, Dictionary<string, List<ReportedFailure>> caseFailuresById) =>
        caseId is not null
        && caseFailuresById.TryGetValue(caseId, out var failures)
        && failures.Count > 0
        && failures.All(failure => failure.Documented);

    /// <summary>
    /// A null-free copy of the source taken once, so a source that changes during or after
    /// <see cref="Build"/> cannot make the computed results and the rendered text disagree. The copy is
    /// deep: every collection, nested ones included, is copied into a read-only collection that only the
    /// snapshot holds, and what it keeps by reference (failure items, discrepancies, check results, run
    /// facts and JADE-unchanged counts) is immutable.
    /// </summary>
    private sealed record Snapshot(
        IReadOnlyList<FailureItem> Failures,
        IReadOnlyList<CaseResult> Cases,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Planned,
        IReadOnlyDictionary<string, CheckResult> Checks,
        RunFacts? Facts,
        BaselineFacts Baseline,
        IReadOnlyList<string> NotCaptured,
        ConformanceSummary? Conformance,
        JadeUnchangedSummary? JadeUnchanged,
        bool ExternalBackend)
    {
        // The interface declares these members non-null, but Build runs on every exit path of a
        // replay, including after a failure part-way through populating the source, so a null is
        // read as empty rather than allowed to throw.
        public static Snapshot Take(IReportSource source)
        {
            var baseline = source.Baseline ?? BaselineFacts.None;

            var planned = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (operation, ids) in source.PlannedCasesByOperation ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    planned[operation] = DistinctIds(ids);
                }
            }

            var checks = new Dictionary<string, CheckResult>(StringComparer.Ordinal);
            foreach (var (name, result) in source.Checks ?? new Dictionary<string, CheckResult>())
            {
                if (name is not null && result is not null)
                {
                    checks[name] = result;
                }
            }

            // A case result and a conformance summary are records, but their lists are the source's own.
            var cases = Array.ConvertAll(
                NonNull(source.Cases),
                result => result with { Discrepancies = Array.AsReadOnly(NonNull(result.Discrepancies)) });
            var conformance = source.Conformance is { } summary
                ? summary with { GapsByNature = ReadOnlyCopy(summary.GapsByNature) }
                : null;

            return new Snapshot(
                Array.AsReadOnly(NonNull(source.Failures)),
                Array.AsReadOnly(cases),
                planned.AsReadOnly(),
                checks.AsReadOnly(),
                source.Facts,
                baseline with { NotCaptured = Array.AsReadOnly(NonNull(baseline.NotCaptured)) },
                DistinctIds(baseline.NotCaptured),
                conformance,
                source.JadeUnchanged,
                source.ExternalBackend);
        }

        private static T[] NonNull<T>(IReadOnlyList<T>? items)
            where T : class =>
            items is null ? [] : items.Where(item => item is not null).ToArray();

        private static ReadOnlyCollection<string> DistinctIds(IReadOnlyList<string>? ids) =>
            ids is null
                ? ReadOnlyCollection<string>.Empty
                : Array.AsReadOnly(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray());

        // An entry-for-entry copy; a null dictionary reads as empty.
        private static ReadOnlyDictionary<string, int> ReadOnlyCopy(IReadOnlyDictionary<string, int>? counts)
        {
            var copy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, count) in counts ?? new Dictionary<string, int>())
            {
                if (key is not null)
                {
                    copy[key] = count;
                }
            }

            return copy.AsReadOnly();
        }
    }
}


/// <summary>Renders and writes <c>RESULTS.md</c>; <c>ReplayLifecycleTests</c> substitutes a throwing writer.</summary>
public interface IResultsReportWriter
{
    /// <summary>Returns the report's Markdown text.</summary>
    /// <param name="report">The built report.</param>
    string Render(ResultsReport report);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, replacing the whole file.</summary>
    /// <param name="path">The report path, normally <c>HarnessOptions.EffectiveReportPath</c>.</param>
    /// <param name="content">The rendered report.</param>
    void Write(string path, string content);
}

/// <summary>
/// Renders the results report in the section order of AAP 0.8.2 and writes it as UTF-8 without a BOM
/// and with <c>\n</c> line endings. Rendering is deterministic: invariant culture, UTC dates, fixed
/// section and row order. The last line is the final line, so <c>tail -n 1 RESULTS.md</c> and
/// <c>Modes/SummaryMode.cs</c> return it exactly. The instance holds no state.
/// </summary>
public sealed class ResultsReportWriter : IResultsReportWriter
{
    private const string Title = "# Erewhon strangler facade — results";
    private const string Dash = "—";
    private const string NotRun = "not run";

    // The same literal as CaptureExecutionValues.NotRun in Baseline/BaselineManifest.cs, restated here so
    // the report does not depend on the Baseline folder.
    private const string CaptureExecutionNotRun = "not-run";

    // The ASCII punctuation PlainText escapes: each can open or close an inline construct (emphasis,
    // strikethrough, code span, link, autolink, raw HTML, entity reference) or escape the next character.
    private const string InlineMarkupCharacters = "\\`*_~<>&[]";

    // The characters that end user information in a URL: '@' and its two compatibility forms, U+FF20
    // and U+FE6B, which NFKC folds to '@'. No other character folds to it.
    private const string AtSignCharacters = "@\uFF20\uFE6B";

    // Follows a URL whose user information was removed, outside its code span.
    private const string UserInformationRemoved = " (user information removed)";

    // Stands for a URL of which nothing is left once its user information is removed.
    private const string UrlNotShown = "not shown";

    private const string DiffNaturePrefix = "d:";
    private const string DiscrepancyArtifactDirectory = "artifacts/discrepancies/";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The text a mandatory or KL-SUB-NORESET entry renders with when the register does not hold it.
    private static readonly IReadOnlyDictionary<string, string> BuiltInLimitations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [KnownLimitationIds.BaselineStandIn] =
            $"The baseline was recorded from the JADE stand-in (`{SourceLabels.StandIn}`), not from live JADE; every baseline and stand-in response is stand-in output.",
        [KnownLimitationIds.RestPaths] =
            "The REST path scheme is assumed: the verb prefix is removed from the method name to give the resource, primitive parameters are path segments in signature order, and the Direct REST form on port 65000 is used.",
        [KnownLimitationIds.SoapActions] =
            "The SOAP actions follow the JADE default `urn:JadeWebServices/WebServiceHTTP/<operation>` and are assumed.",
        [KnownLimitationIds.SoapWsdl] =
            "The WSDL is modelled by the stand-in on JADE 2022 default conventions and is assumed.",
        [KnownLimitationIds.JadeTestsNotRun] =
            "The JADE unit tests were not run, because no live JADE is used.",
        [KnownLimitationIds.SubNoReset] =
            "The backend is not the stand-in, so no state reset or seed confirmation took place; equivalence was still compared.",
    };

    /// <summary>Creates a writer.</summary>
    public ResultsReportWriter()
    {
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public string Render(ResultsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var md = new MarkdownBuilder();
        md.Line(Title).Blank();
        RenderBanner(md, report);
        RenderRunFacts(md, report);
        RenderCounts(md, report);
        RenderOperations(md, report);
        RenderDemoAcceptance(md, report);
        RenderDifferences(md, report);
        RenderKnownLimitations(md, report);
        RenderHowToRun(md);

        // The previous section ends with a blank line; nothing may follow the final line.
        md.Line(MarkdownBuilder.SingleLine(report.FinalLine));
        return md.ToString();
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="IOException"><paramref name="path"/> is a directory, or the write fails.</exception>
    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        // The exit-code verification points the report at a directory (AAP 0.8.4); this must fail even
        // for a root process, which could otherwise not be stopped by permissions.
        if (Directory.Exists(path))
        {
            throw new IOException($"Report path '{path}' is a directory");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalised = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        File.WriteAllText(path, normalised, Utf8NoBom);
    }

    private static void RenderBanner(MarkdownBuilder md, ResultsReport report)
    {
        var facts = report.Facts;
        if (report.ExternalBackend)
        {
            md.Line($"> Backend used: external backend at {UrlCode(facts?.BackendRestUrl)} / {UrlCode(facts?.BackendSoapUrl)} (unverified).");
            md.Line(">");
            md.Line($"> Baseline responses are JADE stand-in ({Code(SourceLabels.StandIn)}) output.");
        }
        else
        {
            md.Line($"> Backend used: JADE stand-in ({Code(SourceLabels.StandIn)}), not live JADE. Baseline and stand-in responses are stand-in output.");
        }

        var baseline = report.Baseline;
        if (!baseline.ManifestPresent
            || string.Equals(baseline.CaptureExecution, CaptureExecutionNotRun, StringComparison.Ordinal))
        {
            var execution = !string.IsNullOrWhiteSpace(baseline.CaptureExecution)
                ? Code(baseline.CaptureExecution)
                : baseline.ManifestPresent ? "not recorded" : "no `baseline/manifest.json`";
            md.Line(">");
            md.Line($"> Baseline not captured (capture execution: {execution}).");
        }

        md.Blank();
    }

    private static void RenderRunFacts(MarkdownBuilder md, ResultsReport report)
    {
        var facts = report.Facts;
        md.Line("## Run facts").Blank();
        md.Header("Fact", "Value");
        md.Row("Date (UTC)", facts is null ? Dash : facts.UtcDate.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture));
        md.Row("Execution mode", Code(facts?.ExecutionMode));
        md.Row(
            ".NET (facade runtime)",
            string.IsNullOrWhiteSpace(facts?.FacadeFrameworkDescription)
                ? "unavailable (facade /admin/info not reached)"
                : PlainText(facts.FacadeFrameworkDescription));
        md.Row("SDK pin (`harness/global.json`)", Code(facts?.SdkPin));
        md.Row("Harness version", Code(HarnessOptions.HarnessVersion));
        md.Row("Target REST URL", UrlCode(facts?.TargetRestUrl));
        md.Row("Target SOAP URL", UrlCode(facts?.TargetSoapUrl));
        md.Row("Backend REST URL", UrlCode(facts?.BackendRestUrl));
        md.Row("Backend SOAP URL", UrlCode(facts?.BackendSoapUrl));
        md.Row(
            "Backend admin URL",
            string.IsNullOrWhiteSpace(facts?.BackendAdminUrl) ? "none (external backend: no reset)" : UrlCode(facts.BackendAdminUrl));
        md.Row("Starting commit", Code(facts?.StartingCommit));
        md.Row("Baseline commit", OrNotFound(facts?.BaselineCommit));
        md.Row("First facade commit", OrNotFound(facts?.FacadeCommit));
        md.Blank();
    }

    private static void RenderCounts(MarkdownBuilder md, ResultsReport report)
    {
        var baseline = report.Baseline;
        md.Line("## Counts").Blank();
        md.Header("Area", "Measure", "Value");

        md.Row("Baseline", "operations (planned)", Number(report.PlannedOperationTotal));
        md.Row("Baseline", "cases (planned)", Number(report.PlannedCaseTotal));
        md.Row("Baseline", "operations captured", Number(baseline.CapturedOperations));
        md.Row("Baseline", "cases captured", Number(baseline.CapturedCases));
        md.Row(
            "Baseline",
            "cases not captured",
            report.NotCaptured.Count == 0
                ? "0"
                : $"{Number(report.NotCaptured.Count)}: {string.Join(", ", report.NotCaptured.Select(Code))}");
        md.Row("Baseline", "WSDL record", baseline.WsdlCaptured ? "captured" : "absent");
        md.Row(
            "Baseline",
            "capture execution",
            !string.IsNullOrWhiteSpace(baseline.CaptureExecution)
                ? Code(baseline.CaptureExecution)
                : baseline.ManifestPresent ? "not recorded" : "no manifest");

        md.Row("Equivalence", "cases compared", Number(report.CasesCompared));
        md.Row("Equivalence", "cases equivalent", Number(report.CasesEquivalent));
        md.Row("Equivalence", "cases with documented difference", Number(report.CasesDocumentedDifference));
        md.Row("Equivalence", "cases with undocumented difference", Number(report.CasesUndocumentedDifference));
        md.Row("Equivalence", "cases not compared", Number(Math.Max(0, report.PlannedCaseTotal - report.CasesCompared)));
        md.Row("Equivalence", "operations equivalent", Number(report.EquivalentCount));
        md.Row("Equivalence", "operations with documented difference", Number(report.DocumentedDifferenceCount));
        md.Row("Equivalence", "operations with undocumented difference", Number(report.UndocumentedDifferenceCount));
        md.Row("Equivalence", "operations not compared", Number(report.NotComparedCount));

        const string conformanceArea = "Stand-in conformance";
        if (report.Conformance is { } conformance)
        {
            md.Row(conformanceArea, "members checked", Number(conformance.MembersChecked));
            md.Row(conformanceArea, "members passed", Number(conformance.Passed));
            var gaps = (conformance.GapsByNature ?? new Dictionary<string, int>())
                .Where(gap => !string.IsNullOrEmpty(gap.Key))
                .OrderBy(gap => gap.Key, StringComparer.Ordinal)
                .ToArray();
            if (gaps.Length == 0)
            {
                md.Row(conformanceArea, "gaps", "0");
            }

            foreach (var (nature, count) in gaps)
            {
                md.Row(conformanceArea, $"gaps: {Code(nature)}", Number(count));
            }
        }
        else
        {
            md.Row(conformanceArea, "members checked", NotRun);
        }

        if (report.Checks.TryGetValue(CheckNames.Conformance, out var conformanceCheck))
        {
            md.Row(conformanceArea, "outcome", CheckText(conformanceCheck));
        }

        RenderTestCheck(md, "Routing", report.Checks.GetValueOrDefault(CheckNames.Routing), "detail");
        RenderTestCheck(md, "Failure (unreachable and timeout)", report.Checks.GetValueOrDefault(CheckNames.Failure), "unreachable / timeout");
        RenderTestCheck(md, "Listener isolation", report.Checks.GetValueOrDefault(CheckNames.ListenerIsolation), "detail");
        md.Row("Facade test results (TRX)", "result", TrxText(report));

        md.Row("Contracts", "OpenAPI drift", CheckText(report.Checks.GetValueOrDefault(CheckNames.OpenApiDrift)));
        md.Row("Contracts", "WSDL contract", CheckText(report.Checks.GetValueOrDefault(CheckNames.WsdlContract)));

        if (report.JadeUnchanged is { } jade)
        {
            md.Row("JADE unchanged", "paths checked", Number(jade.PathsChecked));
            md.Row("JADE unchanged", "paths modified", Number(jade.Modified));
        }
        else
        {
            md.Row("JADE unchanged", "paths checked", CheckText(report.Checks.GetValueOrDefault(CheckNames.JadeUnchanged)));
        }

        md.Row("Ordering evidence", "result", CheckText(report.Checks.GetValueOrDefault(CheckNames.Ordering)));
        md.Blank();
    }

    // A routing, failure or listener-isolation check: its cases, passes and outcome, or "not run" when
    // the TRX supplied no result for it.
    private static void RenderTestCheck(MarkdownBuilder md, string area, CheckResult? check, string detailLabel)
    {
        if (check is null || (IsNotRun(check.Outcome) && check.Cases == 0))
        {
            md.Row(area, "cases", NotRun);
        }
        else
        {
            md.Row(area, "cases", Number(check.Cases));
            md.Row(area, "passed", Number(check.Passed));
            md.Row(area, "outcome", OutcomeText(check.Outcome));
        }

        if (!string.IsNullOrWhiteSpace(check?.Detail))
        {
            md.Row(area, detailLabel, PlainText(check.Detail));
        }
    }

    // Checks/FacadeTestResults.cs reports a missing or unusable TRX as CHK-FACADE-TRX items rather than as
    // a check of its own, so the TRX row falls back to those items and to whether any test result was read.
    private static string TrxText(ResultsReport report)
    {
        if (report.Checks.TryGetValue(CheckNames.FacadeTrx, out var trx))
        {
            return CheckText(trx);
        }

        var natures = report.Failures
            .Where(failure => string.Equals(failure.Item.ItemId, FailureItemIds.FacadeTrx, StringComparison.Ordinal))
            .Select(failure => Code(failure.Item.Nature))
            .ToArray();
        if (natures.Length > 0)
        {
            return $"fail ({string.Join(", ", natures)})";
        }

        var read = new[] { CheckNames.Routing, CheckNames.Failure, CheckNames.ListenerIsolation }
            .Any(name => report.Checks.TryGetValue(name, out var check) && check.Cases > 0);
        return read ? "read" : NotRun;
    }

    private static void RenderOperations(MarkdownBuilder md, ResultsReport report)
    {
        md.Line("## Per-operation results").Blank();
        if (!report.CatalogueAvailable)
        {
            md.Line("The operation catalogue could not be loaded, so rows are derived from the planned and replayed cases and an operation with neither is not listed.").Blank();
        }

        if (report.Operations.Count == 0)
        {
            md.Line("None.").Blank();
            return;
        }

        md.Header("Operation", "Surface", "Catalogue id", "Planned", "Captured", "Compared", "Result");
        foreach (var row in report.Operations)
        {
            md.Row(
                Code(row.JadeMethod),
                SurfaceText(row.Surface),
                Code(row.OperationId),
                Number(row.Planned),
                Number(row.Captured),
                Number(row.Compared),
                row.Result);
        }

        md.Blank();
    }

    private static void RenderDemoAcceptance(MarkdownBuilder md, ResultsReport report)
    {
        md.Line("## Demo acceptance").Blank();
        md.Line("Reported, not enforced: met when at least 20 of 22 operations are equivalent and every other operation returns the baseline status code with a well-formed payload.").Blank();
        if (report.DemoAcceptanceMet)
        {
            md.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Met: at least 20 of 22 operations equivalent ({report.EquivalentCount} of {ResultsReport.TotalOperations}), and every other operation returns the baseline status code with a well-formed payload.")).Blank();
            return;
        }

        md.Line("Not met.").Blank();
        foreach (var line in report.DemoAcceptanceShortfall)
        {
            md.Line($"- {PlainText(line)}");
        }

        md.Blank();
    }

    private static void RenderDifferences(MarkdownBuilder md, ResultsReport report)
    {
        md.Line("## Differences").Blank();

        md.Line("### Equivalence cases").Blank();
        var caseFailures = report.Failures.Where(failure => failure.Item.IsCaseItem).ToArray();
        if (caseFailures.Length == 0)
        {
            md.Line("None.").Blank();
        }
        else
        {
            md.Header("Case", "Operation", "Nature", "Documented", "Register entry", "Attempts", "Artifact");
            foreach (var failure in caseFailures)
            {
                var item = failure.Item;
                var caseId = FailureItemIds.TryGetCaseId(item.ItemId, out var id) ? id : item.ItemId;
                md.Row(
                    Code(caseId),
                    Code(item.Operation),
                    Code(item.Nature),
                    failure.Documented ? "yes" : "no",
                    Code(failure.Entry?.Id),
                    AttemptsText(failure),
                    ArtifactText(item));
            }

            md.Blank();
        }

        md.Line("### Other failing items").Blank();
        var otherFailures = report.Failures.Where(failure => !failure.Item.IsCaseItem).ToArray();
        if (otherFailures.Length == 0)
        {
            md.Line("None.").Blank();
            return;
        }

        md.Header("Item id", "Operation", "Nature", "Documented", "Register entry", "Attempts", "Artifact", "Detail");
        foreach (var failure in otherFailures)
        {
            var item = failure.Item;
            md.Row(
                Code(item.ItemId),
                Code(item.Operation),
                Code(item.Nature),
                failure.Documented ? "yes" : "no",
                Code(failure.Entry?.Id),
                AttemptsText(failure),
                ArtifactText(item),
                PlainText(item.Detail));
        }

        md.Blank();
    }

    private static void RenderKnownLimitations(MarkdownBuilder md, ResultsReport report)
    {
        md.Line("## Known limitations").Blank();

        var rendered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in KnownLimitationIds.Mandatory)
        {
            md.Line(LimitationLine(id, report));
            rendered.Add(id);
        }

        if (report.ExternalBackend)
        {
            md.Line(LimitationLine(KnownLimitationIds.SubNoReset, report));
            rendered.Add(KnownLimitationIds.SubNoReset);
        }

        foreach (var entry in report.Register.Entries)
        {
            if (rendered.Add(entry.Id))
            {
                md.Line(EntryLine(entry, report));
            }
        }

        md.Blank();
    }

    private static string LimitationLine(string id, ResultsReport report) =>
        report.Register.FindById(id) is { } entry
            ? EntryLine(entry, report)
            : $"- **{id}** (built-in text; not in the register) — {BuiltInLimitations[id]}";

    private static string EntryLine(RegisterEntry entry, ResultsReport report)
    {
        var operation = string.IsNullOrWhiteSpace(entry.Operation) ? string.Empty : $"; {Code(entry.Operation)}";
        var documents = report.Failures.Count(failure =>
            failure.Entry is not null && string.Equals(failure.Entry.Id, entry.Id, StringComparison.Ordinal));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"- **{PlainText(entry.Id)}** ({PlainText(entry.Category)}{operation}) — Not done / why: {Sentence(entry.Reason)} Done instead: {Sentence(entry.ActionTaken)} Attempts: {entry.Attempts}. Documents {documents} failing item(s).");
    }

    private static void RenderHowToRun(MarkdownBuilder md)
    {
        md.Line("## How to run").Blank();

        md.Line("Start the stand-in, the facade and the harness, and replay the baseline through the facade:").Blank();
        md.Code("docker compose up --build");

        md.Line("Point the facade at another backend (no stand-in, so no state reset):").Blank();
        md.Code("COMPOSE_PROFILES= BACKEND_REST_URL=http://<host>:65000 BACKEND_SOAP_URL=http://<host>:21001 BACKEND_ADMIN_URL= docker compose up --build");

        md.Line("Without Docker, capture, export the OpenAPI description or replay with local processes:").Blank();
        md.Code(
            "harness/scripts/run-local.sh capture",
            "harness/scripts/run-local.sh export-openapi",
            "harness/scripts/run-local.sh replay");

        md.Line("Run the facade and harness tests. Each `dotnet` command must run inside its component directory, so that the component's `global.json` SDK and test-runner pins apply:").Blank();
        md.Code(
            "(cd facade && dotnet test --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj)",
            "(cd harness && dotnet test --project tests/Erewhon.Harness.Tests/Erewhon.Harness.Tests.csproj)");
    }

    private static string AttemptsText(ReportedFailure failure) =>
        failure.Documented && failure.Entry is { } entry ? Number(entry.Attempts) : Dash;

    private static string ArtifactText(FailureItem item) =>
        item.Nature.StartsWith(DiffNaturePrefix, StringComparison.Ordinal)
            ? Code($"{DiscrepancyArtifactDirectory}{DiscrepancySignature.SanitiseFileName(item.ItemId)}.json")
            : Dash;

    private static string CheckText(CheckResult? check)
    {
        if (check is null)
        {
            return NotRun;
        }

        var outcome = OutcomeText(check.Outcome);
        return string.IsNullOrWhiteSpace(check.Detail) ? outcome : $"{outcome} — {PlainText(check.Detail)}";
    }

    private static string OutcomeText(string? outcome) => outcome switch
    {
        null or "" => Dash,
        CheckOutcomes.NotRun => NotRun,
        _ => PlainText(outcome),
    };

    private static bool IsNotRun(string? outcome) =>
        string.Equals(outcome, CheckOutcomes.NotRun, StringComparison.Ordinal);

    private static string SurfaceText(Surface? surface) => surface switch
    {
        Surface.Rest => "REST",
        Surface.Soap => "SOAP",
        _ => Dash,
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string OrNotFound(string? value) => string.IsNullOrWhiteSpace(value) ? "not found" : Code(value);

    // A code span on one line whose content, under CommonMark's code-span rules, is the value exactly. The
    // fence is one backtick longer than the value's longest backtick run, so no run inside closes it early.
    // One space pads each side when the value starts or ends with a backtick, which would otherwise join
    // the fence, or starts and ends with a space without being all spaces, since CommonMark strips exactly
    // one space from each side of such content. Any other value is written as `value`.
    private static string Code(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Dash;
        }

        var text = MarkdownBuilder.SingleLine(value);
        var longestRun = 0;
        var run = 0;
        foreach (var character in text)
        {
            run = character == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }

        var fence = new string('`', longestRun + 1);
        var padded = text[0] == '`'
            || text[^1] == '`'
            || (text[0] == ' ' && text[^1] == ' ' && text.AsSpan().ContainsAnyExcept(' '));
        var padding = padded ? " " : string.Empty;
        return string.Concat(fence, padding, text, padding, fence);
    }

    // A configured URL as a code span without its user information. No layer sends credentials, yet a
    // misconfigured URL can still hold user:password, and RESULTS.md is committed and its text printed to
    // standard output when the write fails, so user information is never rendered. A value without an
    // at-sign cannot hold any and renders as Code renders it. An absolute http or https URL renders as the
    // runtime parses it, without user information; an at-sign the runtime reads as path, query or fragment
    // is not user information. The runtime reads a compatibility at-sign as part of the host, so a parsed
    // host holding any at-sign still holds its user information and renders as any other value. Any other
    // value renders only what follows its last at-sign, so a malformed URL is never echoed with what may be
    // a password.
    private static string UrlCode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny(AtSignCharacters) < 0)
        {
            return Code(value);
        }

        var text = value.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            && uri.Host.AsSpan().IndexOfAny(AtSignCharacters) < 0)
        {
            var parsed = Code(uri.GetComponents(UriComponents.HttpRequestUrl | UriComponents.Fragment, UriFormat.UriEscaped));
            return uri.UserInfo.Length > 0 ? parsed + UserInformationRemoved : parsed;
        }

        var remainder = text[(text.AsSpan().LastIndexOfAny(AtSignCharacters) + 1)..].Trim();
        return (remainder.Length > 0 ? Code(remainder) : UrlNotShown) + UserInformationRemoved;
    }

    // Register text as one sentence ending in punctuation, so the fixed "Done instead:" and "Attempts:"
    // parts read correctly whether or not the register's text ends with a full stop. The sentence is
    // escaped as PlainText, so it renders exactly as the register holds it.
    private static string Sentence(string? text)
    {
        var line = MarkdownBuilder.SingleLine(text ?? string.Empty).Trim();
        if (line.Length == 0)
        {
            return Dash + ".";
        }

        return PlainText(line[^1] is '.' or '!' or '?' ? line : line + ".");
    }

    // Plain text as Markdown inline text whose rendering is the text itself: on one line, with a backslash
    // before each InlineMarkupCharacters character. CommonMark renders a backslash followed by ASCII
    // punctuation as that character, so a placeholder such as <op> is not read as raw HTML, and the
    // backslashes of \/Date(ms)\/ stay visible. MarkdownBuilder.Row escapes '|' afterwards.
    private static string PlainText(string? value)
    {
        var text = MarkdownBuilder.SingleLine(value);
        if (text.AsSpan().IndexOfAny(InlineMarkupCharacters) < 0)
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length * 2);
        foreach (var character in text)
        {
            if (InlineMarkupCharacters.Contains(character, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>Markdown text with <c>\n</c> line endings only, independent of the platform's newline.</summary>
    private sealed class MarkdownBuilder
    {
        private readonly StringBuilder _text = new();

        public static string SingleLine(string? value) =>
            (value ?? string.Empty)
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');

        public MarkdownBuilder Line(string text)
        {
            _text.Append(text).Append('\n');
            return this;
        }

        public MarkdownBuilder Blank()
        {
            _text.Append('\n');
            return this;
        }

        public void Header(params string[] cells)
        {
            Row(cells);
            _text.Append('|');
            for (var column = 0; column < cells.Length; column++)
            {
                _text.Append(" --- |");
            }

            _text.Append('\n');
        }

        // Each cell on one line with '|' escaped, so no value can break the table; empty cells read as a dash.
        public void Row(params string?[] cells)
        {
            _text.Append('|');
            foreach (var cell in cells)
            {
                var text = string.IsNullOrEmpty(cell) ? Dash : SingleLine(cell).Replace("|", "\\|", StringComparison.Ordinal);
                _text.Append(' ').Append(text).Append(" |");
            }

            _text.Append('\n');
        }

        public void Code(params string[] lines)
        {
            _text.Append("```sh\n");
            foreach (var line in lines)
            {
                _text.Append(line).Append('\n');
            }

            _text.Append("```\n\n");
        }

        public override string ToString() => _text.ToString();
    }
}
