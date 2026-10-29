using System.Globalization;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Modes;

/// <summary>
/// The work of one <c>replay</c> run between start-up and the exit path: everything that observes the
/// facade and the backend and records it in a <see cref="ReplayRunState"/>. <see cref="ReplayMode"/> owns
/// what happens afterwards (report, shutdown, final line, exit code), so a body that throws or is cancelled
/// can never skip it. <c>ReplayLifecycleTests</c> injects a fake body through this seam.
/// </summary>
public interface IReplayBody
{
    /// <summary>Runs the replay and records every observation in <paramref name="state"/>.</summary>
    /// <param name="state">The run state the report is built from.</param>
    /// <param name="ct">Cancels the run; <see cref="ReplayMode"/> records the cancellation as <c>CHK-EXCEPTION</c>.</param>
    Task ExecuteAsync(ReplayRunState state, CancellationToken ct);
}

/// <summary>
/// Everything one replay run observed, as the results report reads it (AAP 0.8.2): failing items, case
/// results, the planned and captured cases, the separate checks and the run and baseline facts.
/// </summary>
/// <remarks>
/// <para>A fresh instance already renders a valid report: every collection is empty, the facts say
/// "unknown" and the baseline facts are <see cref="BaselineFacts.None"/>, because the report is built on
/// every exit path, including one taken before the body recorded anything.</para>
/// <para>Every member is guarded by one private lock and every collection getter returns a copy, so the
/// report's snapshot never races a check that is still recording.</para>
/// </remarks>
public sealed class ReplayRunState : IReportSource
{
    private const string Unknown = "unknown";

    private readonly Lock _gate = new();
    private readonly List<FailureItem> _failures = [];
    private readonly HashSet<(string ItemId, string Nature)> _failureKeys = [];
    private readonly List<CaseResult> _cases = [];
    private readonly Dictionary<string, CheckResult> _checks = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<string>> _planned = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<string>> _captured = new(StringComparer.Ordinal);

    private RunFacts _facts = new(
        DateTimeOffset.UtcNow,
        Unknown,
        FacadeFrameworkDescription: null,
        SdkPin: null,
        TargetRestUrl: "",
        TargetSoapUrl: "",
        BackendRestUrl: "",
        BackendSoapUrl: "",
        BackendAdminUrl: null,
        StartingCommit: Unknown,
        BaselineCommit: null,
        FacadeCommit: null);

    private BaselineFacts _baseline = BaselineFacts.None;
    private ConformanceSummary? _conformance;
    private JadeUnchangedSummary? _jadeUnchanged;
    private bool _externalBackend;

    /// <inheritdoc/>
    /// <remarks>A snapshot in recording order; later recordings do not change it.</remarks>
    public IReadOnlyList<FailureItem> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>A snapshot in record order; later recordings do not change it.</remarks>
    public IReadOnlyList<CaseResult> Cases
    {
        get
        {
            lock (_gate)
            {
                return _cases.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PlannedCasesByOperation
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, IReadOnlyList<string>>(_planned, StringComparer.Ordinal).AsReadOnly();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>A snapshot; a later <see cref="SetCaptured"/> does not change it.</remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CapturedCasesByOperation
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, IReadOnlyList<string>>(_captured, StringComparer.Ordinal).AsReadOnly();
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, CheckResult> Checks
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, CheckResult>(_checks, StringComparer.Ordinal).AsReadOnly();
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The value set is null.</exception>
    public RunFacts Facts
    {
        get
        {
            lock (_gate)
            {
                return _facts;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _facts = value;
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The value set is null.</exception>
    public BaselineFacts Baseline
    {
        get
        {
            lock (_gate)
            {
                return _baseline;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _baseline = value;
            }
        }
    }

    /// <inheritdoc/>
    public ConformanceSummary? Conformance
    {
        get
        {
            lock (_gate)
            {
                return _conformance;
            }
        }

        set
        {
            lock (_gate)
            {
                _conformance = value;
            }
        }
    }

    /// <inheritdoc/>
    public JadeUnchangedSummary? JadeUnchanged
    {
        get
        {
            lock (_gate)
            {
                return _jadeUnchanged;
            }
        }

        set
        {
            lock (_gate)
            {
                _jadeUnchanged = value;
            }
        }
    }

    /// <inheritdoc/>
    public bool ExternalBackend
    {
        get
        {
            lock (_gate)
            {
                return _externalBackend;
            }
        }

        set
        {
            lock (_gate)
            {
                _externalBackend = value;
            }
        }
    }

    /// <summary>
    /// Records a failing item. An item whose (<c>ItemId</c>, <c>Nature</c>) pair is already recorded is
    /// ignored, so the first one, with its detail, is kept; insertion order is preserved.
    /// </summary>
    /// <param name="item">The failing item.</param>
    /// <returns>True when the item was recorded; false when its pair was already present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    public bool AddFailure(FailureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (!_failureKeys.Add((item.ItemId, item.Nature)))
            {
                return false;
            }

            _failures.Add(item);
            return true;
        }
    }

    /// <summary>Records every item of <paramref name="items"/> through <see cref="AddFailure"/>, skipping nulls.</summary>
    /// <param name="items">The failing items; null records nothing.</param>
    public void AddFailures(IEnumerable<FailureItem?>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is not null)
            {
                AddFailure(item);
            }
        }
    }

    /// <summary>
    /// Records a case result, replacing an earlier result with the same case id (ordinal) in its place;
    /// otherwise appends it.
    /// </summary>
    /// <param name="result">The case result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public void AddCase(CaseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var index = _cases.FindIndex(existing => string.Equals(existing.CaseId, result.CaseId, StringComparison.Ordinal));
            if (index >= 0)
            {
                _cases[index] = result;
            }
            else
            {
                _cases.Add(result);
            }
        }
    }

    /// <summary>Records a separate check's result, replacing an earlier one of the same <see cref="CheckResult.Name"/>.</summary>
    /// <param name="result">The check result, named by a <see cref="CheckNames"/> value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException">The result has a blank name.</exception>
    public void SetCheck(CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Name, nameof(result));
        lock (_gate)
        {
            _checks[result.Name] = result;
        }
    }

    /// <summary>
    /// Replaces the planned cases: catalogue operation id to its planned case ids in matrix order. The
    /// lists are copied, so later changes to the caller's collections do not reach the report.
    /// </summary>
    /// <param name="plannedCaseIdsByOperation">The plan; blank keys are skipped and a null list reads as empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plannedCaseIdsByOperation"/> is null.</exception>
    public void SetPlanned(IReadOnlyDictionary<string, IReadOnlyList<string>> plannedCaseIdsByOperation)
    {
        ArgumentNullException.ThrowIfNull(plannedCaseIdsByOperation);

        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (operation, ids) in plannedCaseIdsByOperation)
        {
            if (!string.IsNullOrWhiteSpace(operation))
            {
                copy[operation] = Array.AsReadOnly((ids ?? []).ToArray());
            }
        }

        lock (_gate)
        {
            _planned = copy;
        }
    }

    /// <summary>
    /// Replaces the capture evidence: catalogue operation id to the case ids that have a baseline record,
    /// in record order. The lists are copied, so later changes to the caller's collections do not reach
    /// the report.
    /// </summary>
    /// <param name="capturedCaseIdsByOperation">The captured cases; blank keys are skipped and a null list reads as empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capturedCaseIdsByOperation"/> is null.</exception>
    public void SetCaptured(IReadOnlyDictionary<string, IReadOnlyList<string>> capturedCaseIdsByOperation)
    {
        ArgumentNullException.ThrowIfNull(capturedCaseIdsByOperation);

        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (operation, ids) in capturedCaseIdsByOperation)
        {
            if (!string.IsNullOrWhiteSpace(operation))
            {
                copy[operation] = Array.AsReadOnly((ids ?? []).ToArray());
            }
        }

        lock (_gate)
        {
            _captured = copy;
        }
    }
}

/// <summary>
/// The <c>replay</c> mode's lifecycle owner (AAP 0.8.4): it runs the body, then on every exit path
/// attempts the report, shuts the facade and the stand-in down, rewrites the report when cleanup added
/// items, prints the final line as the harness's last line and returns 0 or 1.
/// </summary>
/// <remarks>
/// <para>The body runs inside one <c>try</c>/<c>catch</c>/<c>finally</c>. The <c>catch</c> records any
/// exception, cancellation included, as <c>CHK-EXCEPTION</c>. The <c>finally</c> runs the report attempt,
/// the cleanup and the report rewrite, each inside its own <c>try</c>/<c>catch</c>, so a failure in one never
/// skips the next. Cleanup uses <see cref="CancellationToken.None"/>, so a cancelled run (SIGTERM) still shuts
/// its components down; every wait is bounded by the shutdown client's own limits.</para>
/// <para>The exit code is 0 only when the last report write succeeded, the register documents every
/// failing item, both those recorded here and those the report derives, and the report holds no captured
/// case the replay left uncompared (<see cref="ResultsReport.UnaccountedCases"/>). <c>CHK-REPORT</c>,
/// <c>CHK-SHUTDOWN</c> and <c>CHK-EXCEPTION</c> are never documentable, so any of them gives 1
/// (AAP 0.7.7). No other value is returned, and <see cref="RunAsync"/> never throws.</para>
/// <para>The mode writes only the report, through <see cref="IResultsReportWriter"/>; the body writes
/// <c>artifacts/**</c>. Nothing here writes <c>baseline/**</c>, <c>contracts/**</c> or the register.</para>
/// </remarks>
public sealed class ReplayMode
{
    private const string LinePrefix = "replay: ";

    // Never quoted: a configured URL may carry a password, and the outcome does not depend on its text.
    private const string UnusableAdminUrl =
        "admin URL is blank or not an absolute http(s) URL without user information, so the stop cannot be requested or confirmed";

    private readonly HarnessOptions _options;
    private readonly IReplayBody _body;
    private readonly IResultsReportWriter _writer;
    private readonly IComponentShutdown _shutdown;
    private readonly KnownLimitationsRegister _register;
    private readonly TextWriter _stdout;

    // The last report that was built and rendered, written or not; it supplies the final line.
    private ResultsReport? _lastReport;
    private bool _lastWriteSucceeded;

    /// <summary>Creates the mode.</summary>
    /// <param name="options">The harness settings: report path and the facade and backend admin URLs.</param>
    /// <param name="body">The replay work; <see cref="ReplayBody"/> in production.</param>
    /// <param name="writer">Renders and writes <c>RESULTS.md</c>.</param>
    /// <param name="shutdown">Shuts each component down and confirms its stop.</param>
    /// <param name="register">The known-limitations register failing items are matched against.</param>
    /// <param name="stdout">Where the mode's lines, a failed report's text and the final line go.</param>
    /// <param name="startupFailures">
    /// Items recorded before the mode existed, such as <c>CHK-EXCEPTION</c> for a configuration, catalogue
    /// or register load failure; null for none.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ReplayMode(
        HarnessOptions options,
        IReplayBody body,
        IResultsReportWriter writer,
        IComponentShutdown shutdown,
        KnownLimitationsRegister register,
        TextWriter stdout,
        IReadOnlyList<FailureItem>? startupFailures = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));

        State = new ReplayRunState();
        State.AddFailures(startupFailures);

        // Seeded from the settings so a report written after an early failure still states the run facts
        // and whether the backend is external; the body refines both.
        State.Facts = ReplayBody.DescribeRun(options, DateTimeOffset.UtcNow, frameworkDescription: null, baselineCommit: null, facadeCommit: null);
        State.ExternalBackend = string.IsNullOrWhiteSpace(options.Backend?.AdminUrl);
    }

    /// <summary>The run state the report is built from; tests read the recorded items from it.</summary>
    public ReplayRunState State { get; }

    /// <summary>Runs the replay and its exit path. Call once per instance.</summary>
    /// <param name="ct">Cancels the body only; the report and cleanup always run.</param>
    /// <returns>
    /// 0 when the report was written, every failing item is documented and every captured case was
    /// compared; otherwise 1.
    /// </returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            await _body.ExecuteAsync(State, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordUnhandled(ex);
        }
        finally
        {
            await FinishAsync().ConfigureAwait(false);
        }

        return Conclude();
    }

    // The three exit-path steps of AAP 0.8.4, each guarded on its own.
    private async Task FinishAsync()
    {
        try
        {
            AttemptReport();
        }
        catch (Exception ex)
        {
            RecordReportFailure($"report attempt failed: {Describe(ex)}");
        }

        var countBefore = State.Failures.Count;
        try
        {
            await ShutDownComponentsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ShutDownAsync guards each component, so this is a fault outside both calls: it names no
            // component and is recorded as the unhandled exception it is.
            State.AddFailure(new FailureItem(FailureItemIds.Exception, FailureItemIds.ExceptionNature(ex), Detail: $"cleanup: {SafeMessage(ex)}"));
            Print($"cleanup failed: {Describe(ex)}");
        }

        try
        {
            if (State.Failures.Count > countBefore)
            {
                Print("cleanup added failing items; rewriting the report");
                AttemptReport();
            }
        }
        catch (Exception ex)
        {
            RecordReportFailure($"report rewrite failed: {Describe(ex)}");
        }
    }

    private void RecordUnhandled(Exception ex)
    {
        try
        {
            State.AddFailure(new FailureItem(FailureItemIds.Exception, FailureItemIds.ExceptionNature(ex), Detail: SafeMessage(ex)));
            Print($"unhandled {Describe(ex)}");
        }
        catch (Exception recordFailure)
        {
            // Only a fault in the recording itself reaches here; the item is still recorded, without detail.
            State.AddFailure(new FailureItem(FailureItemIds.Exception, ex.GetType().FullName ?? ex.GetType().Name));
            Print($"unhandled exception could not be described: {recordFailure.GetType().Name}");
        }
    }

    // Builds, renders and writes the report. A write failure records CHK-REPORT and prints the full
    // report text, rebuilt so it shows that item; a build or render failure records CHK-REPORT and prints
    // the plain failure list. Neither throws.
    private void AttemptReport()
    {
        ResultsReport report;
        string text;
        try
        {
            report = ResultsReport.Build(State, _register, TryGetCatalogue());
            text = _writer.Render(report);
        }
        catch (Exception ex)
        {
            _lastWriteSucceeded = false;
            RecordReportFailure($"report could not be built or rendered: {Describe(ex)}");
            PrintFailureList();
            return;
        }

        _lastReport = report;
        try
        {
            var path = _options.EffectiveReportPath;
            _writer.Write(path, text);
            _lastWriteSucceeded = true;
            Print($"report written to {path}");
            return;
        }
        catch (Exception ex)
        {
            _lastWriteSucceeded = false;
            RecordReportFailure(SafeMessage(ex));
            Print($"report could not be written ({Describe(ex)}); its full text follows");
        }

        try
        {
            var rebuilt = ResultsReport.Build(State, _register, TryGetCatalogue());
            text = _writer.Render(rebuilt);
            _lastReport = rebuilt;
        }
        catch (Exception ex)
        {
            Print($"report could not be rebuilt with the {FailureItemIds.Report} item ({Describe(ex)}); printing the text as first rendered");
        }

        WriteRaw(text);
    }

    private void RecordReportFailure(string detail)
    {
        _lastWriteSucceeded = false;
        State.AddFailure(new FailureItem(FailureItemIds.Report, FailureItemIds.WriteFailed, Detail: detail));
    }

    // The facade always (it fronts the stand-in, so it goes first); the stand-in only when the backend has
    // an admin URL. A blank Backend:AdminUrl marks an external backend, which is never shut down.
    private async Task ShutDownComponentsAsync()
    {
        await ShutDownAsync(ShutdownTarget.FacadeComponent, _options.Target?.AdminUrl).ConfigureAwait(false);

        var backendAdminUrl = _options.Backend?.AdminUrl;
        if (string.IsNullOrWhiteSpace(backendAdminUrl))
        {
            Print("cleanup: the backend is external and is never shut down");
            return;
        }

        await ShutDownAsync(ShutdownTarget.StandInComponent, backendAdminUrl).ConfigureAwait(false);
    }

    // The item is always named after the target's component, never after what the shutdown client
    // returned, so the CHK-SHUTDOWN item of each component is the one recorded whatever the client does.
    private async Task ShutDownAsync(string component, string? adminUrl)
    {
        try
        {
            if (!ShutdownTarget.TryCreate(component, adminUrl, out var target))
            {
                State.AddFailure(new FailureItem(FailureItemIds.Shutdown(component), FailureItemIds.StillRunning, Detail: UnusableAdminUrl));
                Print($"cleanup: {component} {UnusableAdminUrl}");
                return;
            }

            var outcome = await _shutdown.ShutdownAsync(target, CancellationToken.None).ConfigureAwait(false);
            if (outcome is null || !outcome.Stopped)
            {
                State.AddFailure(new FailureItem(
                    FailureItemIds.Shutdown(component),
                    FailureItemIds.StillRunning,
                    Detail: outcome?.Detail ?? "no shutdown outcome returned"));
            }
        }
        catch (Exception ex)
        {
            State.AddFailure(new FailureItem(FailureItemIds.Shutdown(component), FailureItemIds.StillRunning, Detail: SafeMessage(ex)));
            Print($"cleanup: {component} shutdown failed: {Describe(ex)}");
        }
    }

    // Exit code first, then the undocumented items, then the final line, which nothing may follow.
    private int Conclude()
    {
        var exitCode = 1;
        try
        {
            var undocumented = State.Failures.Where(item => !_register.IsDocumented(item)).ToList();
            if (_lastReport is { } report)
            {
                foreach (var item in report.UndocumentedFailures)
                {
                    if (!undocumented.Any(known => known.ItemId == item.ItemId && known.Nature == item.Nature))
                    {
                        undocumented.Add(item);
                    }
                }
            }

            // Both the recorded items and the report's own verdict must hold: the report also derives items
            // (a compared, non-equivalent case without a recorded nature), so its AllFailuresDocumented can be
            // false where the recorded items alone look documented. A captured case the replay never compared
            // is no failing item, yet it leaves its operation an undocumented difference, so it fails the run
            // as well.
            var unaccounted = _lastReport?.UnaccountedCases ?? [];
            var allDocumented = undocumented.Count == 0 && unaccounted.Count == 0 && _lastReport is { AllFailuresDocumented: true };
            exitCode = _lastWriteSucceeded && allDocumented ? 0 : 1;

            Print(string.Create(
                CultureInfo.InvariantCulture,
                $"{undocumented.Count} undocumented failing item(s); report {(_lastWriteSucceeded ? "written" : "not written")}; exit {exitCode}"));
            foreach (var item in undocumented)
            {
                Print($"  {item.ItemId} {item.Nature}");
            }

            if (unaccounted.Count > 0)
            {
                Print(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{unaccounted.Count} captured case(s) not compared, so their operations are undocumented differences: {string.Join(", ", unaccounted)}"));
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Print($"exit code could not be determined ({Describe(ex)}); exiting 1");
        }

        WriteRaw((_lastReport?.FinalLine ?? ResultsReport.FormatFinalLine(0, 0)) + Environment.NewLine);
        try
        {
            _stdout.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The final line was handed to the writer; a writer that cannot flush leaves only the exit code,
            // which is still returned.
            WriteFallback(ex);
        }

        return exitCode;
    }

    private void PrintFailureList()
    {
        foreach (var item in State.Failures)
        {
            Print($"  {item.ItemId} {item.Nature}");
        }
    }

    private void Print(string line) => LifecycleConsole.WriteLine(_stdout, LinePrefix + line);

    // Writes text exactly (the report's Markdown, the final line); a writer that fails falls back to
    // standard error, so the evidence still reaches the operator.
    private void WriteRaw(string text)
    {
        try
        {
            _stdout.Write(text.EndsWith('\n') ? text : text + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            WriteFallback(ex);
            try
            {
                Console.Error.Write(text);
            }
            catch (Exception errorFailure) when (errorFailure is IOException or ObjectDisposedException)
            {
                // Standard error is unusable as well; the exit code still carries the verdict.
                return;
            }
        }
    }

    private static void WriteFallback(Exception ex)
    {
        try
        {
            Console.Error.WriteLine($"{LinePrefix}console writer failed: {ex.GetType().Name}");
        }
        catch (Exception errorFailure) when (errorFailure is IOException or ObjectDisposedException)
        {
            // Standard error is unusable as well; the exit code still carries the verdict.
            return;
        }
    }

    // The report must build without the catalogue, so a catalogue that cannot be loaded, whatever the
    // load threw, gives rows derived from the planned and replayed cases rather than no report. The load
    // failure itself is already a startup CHK-EXCEPTION item recorded by Program.
    private static OperationCatalogue? TryGetCatalogue()
    {
        try
        {
            return OperationCatalogue.Default;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Describe(Exception ex) => $"{ex.GetType().FullName}: {SafeMessage(ex)}";

    // A Message getter can itself throw; the type name then stands in for it.
    internal static string SafeMessage(Exception ex)
    {
        try
        {
            return ex.Message;
        }
        catch (Exception)
        {
            return ex.GetType().FullName ?? ex.GetType().Name;
        }
    }
}

/// <summary>
/// The production replay body (AAP 0.3.2.4, 0.7): waits for the facade and the stand-in, resets and
/// confirms the stand-in, replays every baseline record through the facade in recorded order, compares
/// each response with its record, runs the separate checks and records everything in the run state.
/// </summary>
/// <remarks>
/// <para>Cases go to the facade (<c>Harness:Target:*</c>), exactly as recorded: method, raw path and
/// query, the recorded headers and body, nothing added. The backend (<c>Harness:Backend:*</c>) is reached
/// only for readiness, the reset and seed confirmation through its admin endpoints, and the WSDL contract
/// check. An external backend (no admin URL) is never reset; the report records KL-SUB-NORESET.</para>
/// <para>Every step runs inside a guard that records an exception as <c>CHK-EXCEPTION</c> and carries on,
/// so one failing case or check never prevents the others. Only the caller's cancellation ends the body
/// early, and <see cref="ReplayMode"/> still writes the report and shuts the components down.</para>
/// <para>The body reads <c>baseline/**</c>, <c>harness/config/case-matrix.json</c> and the checked-in
/// contracts, and writes only <c>artifacts/discrepancies/**</c> and <c>artifacts/replay/**</c> (the checks
/// write their own <c>artifacts/openapi/**</c> and <c>artifacts/wsdl/**</c>). It never writes
/// <c>baseline/**</c>, <c>contracts/**</c>, the register or anything under <c>Erewhon/</c>, and it never
/// prints the final line, which belongs to <see cref="ReplayMode"/>.</para>
/// </remarks>
/// <param name="options">The harness settings.</param>
/// <param name="catalogue">The operation catalogue; null when it could not be loaded, which fails the body at once.</param>
/// <param name="register">
/// The known-limitations register, the same instance <see cref="ReplayMode"/> matches failing items
/// against; the stand-in conformance check's outcome is judged against it.
/// </param>
/// <param name="stdout">Where the body's console lines go.</param>
/// <exception cref="ArgumentNullException">
/// <paramref name="options"/>, <paramref name="register"/> or <paramref name="stdout"/> is null.
/// </exception>
public sealed class ReplayBody(
    HarnessOptions options,
    OperationCatalogue? catalogue,
    KnownLimitationsRegister register,
    TextWriter stdout) : IReplayBody
{
    private const string Unknown = "unknown";

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly OperationCatalogue? _catalogue = catalogue;
    private readonly KnownLimitationsRegister _register = register ?? throw new ArgumentNullException(nameof(register));
    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The operation catalogue is unavailable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task ExecuteAsync(ReplayRunState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var catalogue = _catalogue ?? throw new InvalidOperationException("Operation catalogue unavailable");

        using var runner = CaseRunner.FromOptions(_options);
        using var reset = new StateReset(_options);
        var execution = new Execution(_options, catalogue, _register, state, runner, reset, _stdout, ct);
        await execution.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The run facts the report states, from the settings, what the run found and the SDK pin baked into
    /// the harness at build time (<see cref="RunFacts.TryReadSdkPin"/>), so no repository file is read.
    /// </summary>
    /// <param name="options">The harness settings.</param>
    /// <param name="utcDate">When the run started.</param>
    /// <param name="frameworkDescription">The facade's framework description from its <c>/admin/info</c>; null when not read.</param>
    /// <param name="baselineCommit">The commit that first added <c>baseline/manifest.json</c>; null when not found.</param>
    /// <param name="facadeCommit">The commit that first added a <c>facade/</c> path; null when not found.</param>
    /// <returns>The facts; never throws for any settings value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal static RunFacts DescribeRun(
        HarnessOptions options,
        DateTimeOffset utcDate,
        string? frameworkDescription,
        string? baselineCommit,
        string? facadeCommit)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RunFacts(
            utcDate,
            options.RecognisedExecutionMode ?? Unknown,
            frameworkDescription,
            RunFacts.TryReadSdkPin(),
            options.Target?.RestBaseUrl ?? "",
            options.Target?.SoapBaseUrl ?? "",
            options.Backend?.RestBaseUrl ?? "",
            options.Backend?.SoapBaseUrl ?? "",
            options.Backend?.AdminUrl,
            string.IsNullOrWhiteSpace(options.StartingCommit) ? Unknown : options.StartingCommit,
            baselineCommit,
            facadeCommit);
    }

    /// <summary>One run of the body: the per-run inputs, clients and findings, used by one thread at a time.</summary>
    private sealed class Execution(
        HarnessOptions options,
        OperationCatalogue catalogue,
        KnownLimitationsRegister register,
        ReplayRunState state,
        CaseRunner runner,
        StateReset reset,
        TextWriter stdout,
        CancellationToken ct)
    {
        private const string LinePrefix = "replay: ";
        private const string DiscrepanciesDirectoryName = "discrepancies";
        private const string ReplayDirectoryName = "replay";
        private const string ArtifactExtension = ".json";
        private const string ObservedBodyExtension = ".body";
        private const string ManifestName = "baseline/manifest.json";
        private const string ConformanceName = "baseline/conformance.json";
        private const string RulesName = "baseline/normalisation-rules.json";
        private const string MatrixName = "harness/config/case-matrix.json";
        private const string IncompleteDetail = "the check did not complete";

        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly BaselineStore _store = new(options);

        private BaselineManifest? _manifest;
        private CaseMatrix? _matrix;
        private IReadOnlyList<BaselineEntry> _entries = [];
        private bool _recordsRead;
        private ResetExpectations _expectations = ResetExpectations.None;
        private Normaliser? _normaliser;
        private ResponseComparer? _comparer;
        private bool _facadeReady;
        private string? _baselineCommit;
        private string? _facadeCommit;

        public async Task RunAsync()
        {
            PrintBaseline($"replay through the facade: {SourceLabels.BaselineLabel}");

            // Step 0: run facts first, so a failure in any later step still reports them.
            state.ExternalBackend = options.IsExternalBackend;
            state.Facts = DescribeRun(options, _startedAt, frameworkDescription: null, baselineCommit: null, facadeCommit: null);
            GuardSync("prepare artifacts", PrepareArtifacts);

            // Step 1: readiness of the facade and, unless the backend is external, the stand-in.
            await Guard("readiness", WaitForComponentsAsync).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Step 2: the manifest's seed facts, then the seed reset they are confirmed against.
            await Guard($"read {ManifestName}", ReadManifestAsync).ConfigureAwait(false);
            await Guard("reset at replay start", ResetAtStartAsync).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Step 3: the plan, the records, the counts and the normalisation rules.
            GuardSync($"load {MatrixName}", LoadMatrix);
            state.SetPlanned(PlannedByOperation());
            await Guard("read baseline records", ReadRecordsAsync).ConfigureAwait(false);
            GuardSync("count the baseline", () => state.Baseline = DescribeBaseline());
            GuardSync("record not-captured cases", RecordNotCaptured);
            await Guard($"load {RulesName}", LoadRulesAsync).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Step 4: every record, in recorded order.
            await ReplayCasesAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Step 5: the separate checks, then the final run facts.
            await Guard("stand-in conformance check", ConformanceAsync).ConfigureAwait(false);
            await Guard("OpenAPI drift check", OpenApiDriftAsync).ConfigureAwait(false);
            await Guard("WSDL contract check", WsdlContractAsync).ConfigureAwait(false);
            GuardSync("facade test results", ReadFacadeTests);
            await Guard("JADE-unchanged check", JadeUnchangedAsync).ConfigureAwait(false);
            await Guard("ordering evidence", OrderingAsync).ConfigureAwait(false);

            var frameworkDescription = await ReadFacadeFrameworkAsync().ConfigureAwait(false);
            state.Facts = DescribeRun(options, _startedAt, frameworkDescription, _baselineCommit, _facadeCommit);
        }

        // Only discrepancies/ and replay/ are this run's to clear; facade-tests/ was written before
        // replay started, and openapi/ and wsdl/ belong to their checks.
        private void PrepareArtifacts()
        {
            RecreateDirectory(Path.Combine(options.ArtifactsDirectory, DiscrepanciesDirectoryName));
            RecreateDirectory(Path.Combine(options.ArtifactsDirectory, ReplayDirectoryName));
        }

        // A link is removed itself and never followed, so clearing can never delete anything outside
        // artifacts/; a recursive delete removes nested links without following them either.
        private static void RecreateDirectory(string path)
        {
            if (new FileInfo(path).LinkTarget is not null || File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            Directory.CreateDirectory(path);
        }

        private async Task WaitForComponentsAsync()
        {
            using var readiness = new Readiness(options, stdout);
            var facade = readiness.WaitAsync(ShutdownTarget.FacadeComponent, options.Target.AdminUrl, ct);
            var standIn = options.IsExternalBackend
                ? Task.FromResult<FailureItem?>(null)
                : readiness.WaitAsync(ShutdownTarget.StandInComponent, options.Backend.AdminUrl, ct);

            var notReady = await Task.WhenAll(facade, standIn).ConfigureAwait(false);
            state.AddFailures(notReady);
            _facadeReady = notReady[0] is null;
            if (!_facadeReady)
            {
                Print($"the facade is not ready, so no case is sent and each is compared as {DiscrepancyKinds.NoResponse(NoResponseReasons.Connect)}");
            }
        }

        private async Task ReadManifestAsync()
        {
            _manifest = await _store.TryReadManifestAsync(ct).ConfigureAwait(false);
            if (_manifest is null)
            {
                PrintBaseline($"{ManifestName} absent: no seed facts to confirm");
                return;
            }

            _expectations = _manifest.HasSeedFacts
                ? ResetExpectations.FromManifest(_manifest.SeedSha256, _manifest.ReferenceDate)
                : ResetExpectations.None;
            PrintBaseline(string.Create(
                CultureInfo.InvariantCulture,
                $"{ManifestName}: source {_manifest.Source}, capture execution {_manifest.CaptureExecution ?? "not recorded"}, "
                + $"{_manifest.NotCaptured.Count} case(s) not captured, seed facts {(_manifest.HasSeedFacts ? "confirmed at every reset" : "absent")}"));
        }

        private async Task ResetAtStartAsync()
        {
            if (options.IsExternalBackend)
            {
                PrintBaseline($"external backend: no reset or seed confirmation ({KnownLimitationIds.SubNoReset})");
                return;
            }

            await ResetAsync(ResetPoints.ReplayStart, AdminRoutes.ResetStateSeed).ConfigureAwait(false);
        }

        private async Task ResetAsync(string point, string resetState)
        {
            var result = await reset.ResetAsync(point, resetState, _expectations, ct).ConfigureAwait(false);
            state.AddFailures(result.Failures);
        }

        private void LoadMatrix()
        {
            _matrix = CaseMatrix.Load(options.CaseMatrixPath, catalogue);
            Print(string.Create(
                CultureInfo.InvariantCulture,
                $"{MatrixName}: {_matrix.PlannedCaseCount} planned cases over {_matrix.PlannedOperationCount} operations"));
        }

        // Every catalogue operation, so the report has a row and a plan for all 22 even when the matrix
        // could not be loaded.
        private Dictionary<string, IReadOnlyList<string>> PlannedByOperation()
        {
            var planned = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var operation in catalogue.Operations)
            {
                planned[operation.Id] = _matrix is not null && _matrix.ByOperation.TryGetValue(operation.Id, out var cases)
                    ? cases.Select(definition => definition.Id).ToArray()
                    : [];
            }

            return planned;
        }

        private async Task ReadRecordsAsync()
        {
            _entries = await _store.ReadEntriesAsync(ct).ConfigureAwait(false);
            _recordsRead = true;

            // The capture evidence, recorded before any case is replayed, so a case that is never compared
            // (no comparer, cancellation, a failed case step) is still reported as captured.
            state.SetCaptured(_entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Record.Operation))
                .GroupBy(entry => entry.Record.Operation, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(entry => entry.Record.CaseId).ToArray(),
                    StringComparer.Ordinal));
            PrintBaseline(string.Create(CultureInfo.InvariantCulture, $"{_entries.Count} baseline record(s) read and verified"));
        }

        private BaselineFacts DescribeBaseline()
        {
            var manifest = _manifest;
            return new BaselineFacts(
                _matrix?.PlannedOperationCount ?? manifest?.Counts.PlannedOperations ?? 0,
                _matrix?.PlannedCaseCount ?? manifest?.Counts.PlannedCases ?? 0,
                _entries.Select(entry => entry.Record.Operation).Distinct(StringComparer.Ordinal).Count(),
                _entries.Count,
                manifest?.NotCaptured ?? [],
                _store.WsdlExists && (manifest is null || manifest.WsdlCaptured),
                manifest is not null,
                manifest?.CaptureExecution);
        }

        // Beyond the manifest's notCaptured list, a planned case without a baseline record is also reported
        // as not-captured: left unreported, its operation would read as a difference with no failing item
        // behind it, and the exit code, which judges failing items only, would pass an undocumented gap.
        // Unlisted gaps are derived only from records that were read, so a damaged baseline is reported once,
        // as the exception, rather than as every planned case.
        private void RecordNotCaptured()
        {
            var reasons = new List<(string CaseId, string Reason)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var caseId in _manifest?.NotCaptured ?? [])
            {
                if (!string.IsNullOrWhiteSpace(caseId) && seen.Add(caseId))
                {
                    reasons.Add((caseId, $"listed in {ManifestName} notCaptured"));
                }
            }

            if (_recordsRead && _matrix is not null)
            {
                var recorded = _entries.Select(entry => entry.Record.CaseId).ToHashSet(StringComparer.Ordinal);
                foreach (var definition in _matrix.Cases)
                {
                    if (!recorded.Contains(definition.Id) && seen.Add(definition.Id))
                    {
                        reasons.Add((definition.Id, $"planned in {MatrixName} but has no baseline record"));
                    }
                }
            }

            foreach (var (caseId, reason) in reasons)
            {
                var operation = _matrix is not null && _matrix.TryGetCase(caseId, out var definition)
                    ? definition.Operation
                    : FailureItemIds.OperationIdFromCaseId(caseId);
                state.AddFailure(new FailureItem(FailureItemIds.Case(caseId), FailureItemIds.NotCaptured, operation, reason));
            }

            if (reasons.Count > 0)
            {
                PrintBaseline(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{reasons.Count} planned case(s) not captured; each is reported as {FailureItemIds.NotCaptured}, never as compared"));
            }
        }

        // The committed rules are the ones the baseline was captured under. Without the file the same two
        // rules are derived from the catalogue, and a file that does not parse is reported and replaced the
        // same way, so the cases are still compared.
        private async Task LoadRulesAsync()
        {
            NormalisationRules? rules = null;
            var absent = false;
            await Guard($"parse {RulesName}", async () =>
            {
                var text = await _store.ReadNormalisationRulesAsync(ct).ConfigureAwait(false);
                if (text is null)
                {
                    absent = true;
                    return;
                }

                rules = NormalisationRules.Parse(text);
                PrintBaseline($"normalisation rules: {RulesName}");
            }).ConfigureAwait(false);

            if (rules is null)
            {
                rules = NormalisationRules.FromCatalogue(catalogue);
                PrintBaseline($"{RulesName} {(absent ? "absent" : "unusable")}: the normalisation rules are derived from the catalogue");
            }

            _normaliser = new Normaliser(rules);
            _comparer = new ResponseComparer(catalogue, _normaliser);
        }

        private async Task ReplayCasesAsync()
        {
            if (_comparer is null)
            {
                Print(string.Create(
                    CultureInfo.InvariantCulture,
                    $"no comparer could be built, so none of the {_entries.Count} record(s) is compared"));
                return;
            }

            foreach (var entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                await Guard($"replay case {entry.Record.CaseId}", () => ReplayCaseAsync(entry, _comparer)).ConfigureAwait(false);
            }
        }

        // The empty-state case is bracketed by an empty reset before it and a seed reset after it, which
        // runs even when the case itself fails, so no later state depends on it (AAP 0.7.3).
        private async Task ReplayCaseAsync(BaselineEntry entry, ResponseComparer comparer)
        {
            var record = entry.Record;
            var operation = catalogue.GetById(record.Operation);
            var bracketed = string.Equals(record.PreState, CasePreStates.Empty, StringComparison.Ordinal)
                && !options.IsExternalBackend;

            if (bracketed)
            {
                await ResetAsync(ResetPoints.Before(record.CaseId), AdminRoutes.ResetStateEmpty).ConfigureAwait(false);
            }

            try
            {
                await CompareCaseAsync(entry, operation, comparer).ConfigureAwait(false);
            }
            finally
            {
                if (bracketed)
                {
                    await ResetAsync(ResetPoints.After(record.CaseId), AdminRoutes.ResetStateSeed).ConfigureAwait(false);
                }
            }
        }

        private async Task CompareCaseAsync(BaselineEntry entry, OperationDescriptor operation, ResponseComparer comparer)
        {
            var record = entry.Record;
            var observed = _facadeReady
                ? await SendAsync(record, operation).ConfigureAwait(false)
                : new ResponseSnapshot(null, null, ReadOnlyMemory<byte>.Empty, NoResponseReasons.Connect);
            var baseline = new ResponseSnapshot(record.Response.Status, record.Response.ContentType, entry.Body);
            var comparison = comparer.Compare(operation, baseline, observed);

            string? nature = null;
            if (!comparison.Equivalent)
            {
                // Prepared once, so the nature's signature and the artifact describe the same canonical list.
                var canonical = DiscrepancySignature.Prepare(comparison.Discrepancies);
                var item = new FailureItem(FailureItemIds.Case(record.CaseId), FailureItemIds.Diff(canonical.Signature), record.Operation);
                nature = item.Nature;
                var note = await WriteDiscrepancyArtifactAsync(item, canonical).ConfigureAwait(false);
                state.AddFailure(note is null ? item : item with { Detail = note });
            }

            state.AddCase(new CaseResult(
                record.CaseId,
                record.Operation,
                record.Order,
                Compared: true,
                comparison.Equivalent,
                comparison.Discrepancies,
                comparison.WellFormed,
                comparison.StatusMatched,
                record.Response.Status,
                observed.Status,
                nature));

            var outcome = comparison.Equivalent
                ? "equivalent"
                : observed.NoResponse is { } reason
                    ? $"differs ({nature}; no response: {reason})"
                    : string.Create(CultureInfo.InvariantCulture, $"differs ({nature}; {comparison.Discrepancies.Count} discrepancies)");
            PrintBaseline(string.Create(CultureInfo.InvariantCulture, $"{record.Order:000} {record.CaseId}: {outcome}"));
        }

        // The recorded request is sent unchanged to the facade listener of the operation's surface.
        private async Task<ResponseSnapshot> SendAsync(BaselineRecord record, OperationDescriptor operation)
        {
            var baseUrl = operation.Surface switch
            {
                Surface.Rest => options.Target.RestBaseUrl,
                Surface.Soap => options.Target.SoapBaseUrl,
                _ => throw new InvalidDataException(
                    $"Operation '{operation.Id}' of case {record.CaseId} has surface '{operation.Surface}', which no facade listener serves."),
            };

            var request = new CaseRequest(record.Request.Method, record.Request.PathAndQuery, record.Request.Headers, record.Request.BodyText);
            var response = await runner.SendAsync(baseUrl, request, CaseRunner.RequestTimeoutOf(options), ct).ConfigureAwait(false);
            if (response.Answered)
            {
                await SaveObservedBodyAsync(record, response.Body).ConfigureAwait(false);
            }

            return new ResponseSnapshot(response.Status, response.ContentType, response.Body, response.NoResponse);
        }

        // Diagnostic only: artifacts/replay/<rest|soap>/<record file stem>.body holds the observed bytes.
        private async Task SaveObservedBodyAsync(BaselineRecord record, byte[] body)
        {
            try
            {
                var directory = Path.Combine(options.ArtifactsDirectory, ReplayDirectoryName, BaselineStore.SurfaceDirectoryName(record.CaseId));
                var stem = Path.GetFileNameWithoutExtension(BaselineStore.RecordFileName(record));
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, stem + ObservedBodyExtension), body, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsArtifactFailure(ex))
            {
                Print($"observed body of {record.CaseId} not saved: {ReplayMode.Describe(ex)}");
            }
        }

        // Writes artifacts/discrepancies/<itemId>.json. A failure to write it is returned as a note for the
        // item's detail and never thrown, so the failing item itself is always recorded.
        private async Task<string?> WriteDiscrepancyArtifactAsync(FailureItem item, CanonicalDiscrepancies canonical)
        {
            try
            {
                var directory = Path.Combine(options.ArtifactsDirectory, DiscrepanciesDirectoryName);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, DiscrepancySignature.SanitiseFileName(item.ItemId) + ArtifactExtension);
                await File.WriteAllBytesAsync(path, BaselineStore.SerializeDocument(canonical.ToArtifact(item)), ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) when (IsArtifactFailure(ex))
            {
                var note = $"discrepancy artifact not written: {ReplayMode.Describe(ex)}";
                Print($"{item.ItemId}: {note}");
                return note;
            }
        }

        private static bool IsArtifactFailure(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or InvalidDataException;

        // Conformance is read from capture's result, never recomputed: the stand-in's conformance is a fact
        // about the baseline, not about the facade.
        //
        // Capture writes the conformance result beside the records it captured, so a captured baseline
        // without it has lost mandatory evidence. That absence is thrown, and the guard records it as the
        // CHK-EXCEPTION item no register entry can document, so the run cannot exit 0 on it. Only a baseline
        // from which nothing was captured has no result to read: the hand-written not-run manifest, or no
        // manifest at all, with no record. Such a baseline reports the check as not run and adds no item.
        private async Task ConformanceAsync()
        {
            state.SetCheck(NotRun(CheckNames.Conformance, IncompleteDetail));
            var node = await _store.TryReadConformanceAsync(ct).ConfigureAwait(false);
            if (node is null)
            {
                if (_manifest is { IsNotRun: false } || _entries.Count > 0)
                {
                    const string detail = $"{ConformanceName} absent although the baseline was captured";
                    state.SetCheck(NotRun(CheckNames.Conformance, detail));
                    PrintBaseline($"conformance: {detail}; the stand-in conformance evidence is missing");
                    throw new FileNotFoundException(
                        $"{ConformanceName} is absent although the baseline was captured; capture writes the stand-in conformance result beside its records, so the conformance evidence is missing.",
                        options.ConformancePath);
                }

                state.SetCheck(NotRun(CheckNames.Conformance, $"{ConformanceName} absent"));
                PrintBaseline($"conformance: {ConformanceName} absent; not run");
                return;
            }

            var result = ConformanceResult.Parse(node);
            var check = result.ToCheckResult(register);
            state.Conformance = result.Summary;
            state.SetCheck(check);
            state.AddFailures(result.Failures);

            // The detail carries the verdict's reason: how many failing items the register documents and
            // how many seed-token assertions hold.
            PrintBaseline(string.IsNullOrWhiteSpace(check.Detail) ? DescribeCheck(check) : $"{DescribeCheck(check)}; {check.Detail}");
        }

        private async Task OpenApiDriftAsync()
        {
            state.SetCheck(NotRun(CheckNames.OpenApiDrift, IncompleteDetail));
            if (_normaliser is null)
            {
                RecordContractNotRun(CheckNames.OpenApiDrift, FailureItemIds.OpenApiDrift);
                return;
            }

            var check = new OpenApiDriftCheck(options, new JsonSemanticComparer(catalogue, _normaliser), runner);
            await RecordContractAsync(await check.RunAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private async Task WsdlContractAsync()
        {
            state.SetCheck(NotRun(CheckNames.WsdlContract, IncompleteDetail));
            if (_normaliser is null)
            {
                RecordContractNotRun(CheckNames.WsdlContract, FailureItemIds.WsdlContract);
                return;
            }

            var check = new WsdlContractCheck(options, catalogue, new XmlSemanticComparer(catalogue, _normaliser), runner);
            await RecordContractAsync(await check.RunAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // Without normalisation rules neither semantic comparer exists; the check is reported as not run,
        // the item its own not-run outcome would carry.
        private void RecordContractNotRun(string checkName, string itemId)
        {
            const string detail = "no normalisation rules, so no semantic comparer could be built";
            state.SetCheck(NotRun(checkName, detail));
            state.AddFailure(new FailureItem(itemId, FailureItemIds.NotRun, Detail: detail));
            Print($"check {checkName}: not-run ({detail})");
        }

        private async Task RecordContractAsync(ContractCheckResult result)
        {
            state.SetCheck(result.Result);
            if (result.Failure is { } failure)
            {
                var item = failure;
                if (result.Canonical is { } canonical)
                {
                    var note = await WriteDiscrepancyArtifactAsync(failure, canonical).ConfigureAwait(false);
                    if (note is not null)
                    {
                        item = failure with { Detail = string.IsNullOrEmpty(failure.Detail) ? note : $"{failure.Detail}; {note}" };
                    }
                }

                state.AddFailure(item);
            }

            Print(DescribeCheck(result.Result));
        }

        private void ReadFacadeTests()
        {
            foreach (var name in new[] { CheckNames.Routing, CheckNames.Failure, CheckNames.ListenerIsolation })
            {
                state.SetCheck(NotRun(name, IncompleteDetail));
            }

            var summary = new FacadeTestResults(options).Read();
            state.SetCheck(summary.Routing);
            state.SetCheck(summary.Failure);
            state.SetCheck(summary.ListenerIsolation);
            state.AddFailures(summary.Failures);
            foreach (var check in new[] { summary.Routing, summary.Failure, summary.ListenerIsolation })
            {
                Print(DescribeCheck(check));
            }
        }

        private async Task JadeUnchangedAsync()
        {
            state.SetCheck(NotRun(CheckNames.JadeUnchanged, IncompleteDetail));
            var result = await new JadeUnchangedCheck(options).RunAsync(ct).ConfigureAwait(false);
            state.SetCheck(result.Result);
            state.JadeUnchanged = result.Summary;
            state.AddFailures(result.Failures);
            Print(DescribeCheck(result.Result));
        }

        private async Task OrderingAsync()
        {
            state.SetCheck(NotRun(CheckNames.Ordering, IncompleteDetail));
            var result = await new OrderingEvidence(options).RunAsync(ct).ConfigureAwait(false);
            state.SetCheck(result.Result);
            if (result.Failure is { } failure)
            {
                state.AddFailure(failure);
            }

            _baselineCommit = result.BaselineCommit;
            _facadeCommit = result.FacadeCommit;
            Print(DescribeCheck(result.Result));
        }

        // A reported fact only: a facade that does not answer leaves the .NET version unavailable, and no
        // item is recorded for it.
        private async Task<string?> ReadFacadeFrameworkAsync()
        {
            try
            {
                var info = await reset.ReadInfoAsync(options.Target.AdminUrl, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(info?.FrameworkDescription) ? null : info.FrameworkDescription;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Print($"facade /admin/info not read: {ReplayMode.Describe(ex)}");
                return null;
            }
        }

        private static CheckResult NotRun(string name, string detail) => new(name, CheckOutcomes.NotRun, 0, 0, detail);

        private static string DescribeCheck(CheckResult check) =>
            string.Create(CultureInfo.InvariantCulture, $"check {check.Name}: {check.Outcome} ({check.Passed} of {check.Cases} passed)");

        // Records an exception as CHK-EXCEPTION and carries on, so one failing step never prevents the
        // others; the caller's cancellation is the one exception that ends the body.
        private async Task Guard(string what, Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordStepFailure(what, ex);
            }
        }

        private void GuardSync(string what, Action step)
        {
            try
            {
                step();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordStepFailure(what, ex);
            }
        }

        private void RecordStepFailure(string what, Exception ex)
        {
            state.AddFailure(new FailureItem(FailureItemIds.Exception, FailureItemIds.ExceptionNature(ex), Detail: $"{what}: {ReplayMode.SafeMessage(ex)}"));
            Print($"{what} failed: {ReplayMode.Describe(ex)}");
        }

        private void Print(string line) => LifecycleConsole.WriteLine(stdout, LinePrefix + line);

        private void PrintBaseline(string line) => LifecycleConsole.WriteLine(stdout, $"{SourceLabels.ConsolePrefix} {line}");
    }
}
