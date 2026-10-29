using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Modes;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The replay exit path (AAP 0.8.4) and exit code (AAP 0.7.7), driven in-process through the seams of
/// <see cref="ReplayMode"/>: a fake body, a report writer that renders with the real
/// <see cref="ResultsReportWriter"/> but never touches the disk, and a shutdown client that records its
/// calls. Whatever the body, the report write or a shutdown does, every component is shut down, the final
/// line is the last line printed and the exit code is 0 or 1. The admin URLs name reserved
/// <c>.invalid</c> hosts that nothing contacts, because every request goes through the fakes. The
/// conformance-evidence tests run the real <see cref="ReplayBody"/> instead, against a temporary
/// repository root and a closed loopback port, so every network step fails at once.
/// </summary>
/// <remarks>
/// Assertions on the console and the report text use fixed messages, because both hold the per-test
/// temporary path and the run date.
/// </remarks>
public sealed class ReplayLifecycleTests : IDisposable
{
    private const string FacadeAdminUrl = "http://facade.invalid:9090";
    private const string StandInAdminUrl = "http://standin.invalid:9090";
    private const string RepoRootToken = "<repo-root>";
    private const string CaseMatrixPath = "harness/config/case-matrix.json";

    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(30);

    private static readonly Regex FinalLinePattern = new(
        @"^\d+ of 22 operations equivalent, \d+ documented differences$",
        RegexOptions.CultureInvariant);

    private static readonly string[] BothComponents = [ShutdownTarget.FacadeComponent, ShutdownTarget.StandInComponent];

    // The only exit codes the harness may return (AAP 0.7.7).
    private static readonly int[] ExitCodes = [0, 1];

    private readonly TempDirectory _repoRoot = new();
    private readonly StringWriter _stdout = new(CultureInfo.InvariantCulture);

    /// <summary>How the first shutdown call fails.</summary>
    public enum ShutdownFailure
    {
        /// <summary>The client returns an outcome that is not stopped.</summary>
        ReturnsNotStopped,

        /// <summary>The client's call faults with <see cref="HttpRequestException"/>.</summary>
        Throws,
    }

    /// <summary>How the replay body fails.</summary>
    public enum BodyFailure
    {
        /// <summary>The body throws <see cref="InvalidOperationException"/>.</summary>
        Throws,

        /// <summary>The run's token is cancelled before the body starts, as on SIGTERM.</summary>
        Cancelled,
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stdout.Dispose();
        _repoRoot.Dispose();
    }

    [Fact]
    public async Task ReportWriteFailure_RecordsItem_PrintsReport_ShutsDownAll_Exits1()
    {
        var options = Options();
        var writer = new ThrowingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty, options);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        Assert.Contains(mode.State.Failures, item =>
            item.ItemId == FailureItemIds.Report && item.Nature == FailureItemIds.WriteFailed);

        // Cleanup added nothing, so the one failed write is the only attempt, and it targeted the report path.
        Assert.Equal(1, writer.WriteAttempts);
        Assert.True(
            writer.Paths.All(path => string.Equals(path, options.EffectiveReportPath, StringComparison.Ordinal)),
            "every report write targets Harness:ReportPath");
        Assert.False(File.Exists(options.EffectiveReportPath), "the mode writes the report only through its writer");

        // The text printed is the report rebuilt after the failure, so it shows the CHK-REPORT item.
        var printed = writer.Rendered[^1];
        Assert.True(StdoutText.Contains(printed, StringComparison.Ordinal), "stdout holds the full report text");
        Assert.True(printed.Contains("Backend used: JADE stand-in", StringComparison.Ordinal), "the printed report carries its banner");
        Assert.True(printed.Contains(FailureItemIds.Report, StringComparison.Ordinal), "the printed report lists the CHK-REPORT item");
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), LastNonEmptyLine(printed));

        Assert.Equal(BothComponents, shutdown.Components);
        Assert.Equal([new Uri(FacadeAdminUrl), new Uri(StandInAdminUrl)], shutdown.Calls.Select(call => call.AdminBaseUrl));
        Assert.DoesNotContain(mode.State.Failures, IsShutdownItem);

        // The harness's last line is the final line of the report it printed.
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), Redacted(LastNonEmptyLine(StdoutText)));
    }

    [Theory]
    [InlineData(BodyFailure.Throws, typeof(InvalidOperationException))]
    [InlineData(BodyFailure.Cancelled, typeof(OperationCanceledException))]
    public async Task BodyThrows_RecordsException_StillShutsDown_Exits1(BodyFailure failure, Type expectedException)
    {
        var body = failure == BodyFailure.Throws
            ? FakeBody.Throwing(new InvalidOperationException("body failed"))
            : new FakeBody((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(body, writer, shutdown, KnownLimitationsRegister.Empty);

        var exitCode = await RunAsync(mode, cancelBeforeRun: failure == BodyFailure.Cancelled);

        AssertExitCode(1, exitCode);
        var thrown = body.Thrown;
        Assert.NotNull(thrown);
        Assert.IsType(expectedException, thrown, exactMatch: true);
        var item = Assert.Single(mode.State.Failures);
        Assert.Equal(FailureItemIds.Exception, item.ItemId);
        Assert.Equal(FailureItemIds.ExceptionNature(thrown), item.Nature);

        // Cleanup never inherits the run's token, so a cancelled run still shuts its components down.
        Assert.Equal(BothComponents, shutdown.Components);
        Assert.All(shutdown.Calls, call => Assert.False(call.TokenCancelled));

        Assert.Equal(1, writer.WriteCount);
        Assert.True(writer.Written[^1].Contains(FailureItemIds.Exception, StringComparison.Ordinal), "the written report lists the CHK-EXCEPTION item");
        AssertFinalLineLast();
    }

    [Theory]
    [InlineData(ShutdownFailure.ReturnsNotStopped)]
    [InlineData(ShutdownFailure.Throws)]
    public async Task FirstShutdownFails_SecondStillCalled_Exits1(ShutdownFailure failure)
    {
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown(failFirstCall: failure);
        var mode = NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        Assert.Equal(BothComponents, shutdown.Components);

        var item = Assert.Single(mode.State.Failures, IsShutdownItem);
        Assert.Equal(FailureItemIds.Shutdown(ShutdownTarget.FacadeComponent), item.ItemId);
        Assert.Equal(FailureItemIds.StillRunning, item.Nature);

        // The first write precedes cleanup; the rewrite after cleanup shows the item cleanup added.
        var shutdownItemId = FailureItemIds.Shutdown(ShutdownTarget.FacadeComponent);
        Assert.Equal(2, writer.WriteCount);
        Assert.False(writer.Written[0].Contains(shutdownItemId, StringComparison.Ordinal), "the first report is written before cleanup");
        Assert.True(writer.Written[^1].Contains(shutdownItemId, StringComparison.Ordinal), "the rewritten report lists the CHK-SHUTDOWN item");
        AssertFinalLineLast();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyBackendAdminUrl_ShutsDownOnlyFacade(string backendAdminUrl)
    {
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty, Options(backendAdminUrl));

        var exitCode = await RunAsync(mode);

        // An external backend is never shut down, and leaving it running is not a CHK-SHUTDOWN item.
        Assert.Equal([ShutdownTarget.FacadeComponent], shutdown.Components);
        Assert.Empty(mode.State.Failures);
        Assert.Equal(1, writer.WriteCount);
        AssertExitCode(0, exitCode);
        AssertFinalLineLast();
    }

    // Two recorded items, of which the register documents both, one or none: only the first exits 0.
    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public async Task FailingItems_Exit0OnlyWhenAllDocumented(int documentedPairs, int expectedExitCode)
    {
        RegisterMatch[] pairs =
        [
            new(FailureItemIds.Ordering, FailureItemIds.NotRun),
            new(FailureItemIds.JadeUnchanged, FailureItemIds.NotRun),
        ];
        var register = documentedPairs == 0
            ? KnownLimitationsRegister.Empty
            : new KnownLimitationsRegister(
            [
                new RegisterEntry(
                    "KL-SUB-NOGIT",
                    RegisterCategories.Substitution,
                    null,
                    pairs.Take(documentedPairs).ToArray(),
                    "no .git",
                    0,
                    "reported not run"),
            ]);
        var body = FakeBody.Adding(
            new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun),
            new FailureItem(FailureItemIds.JadeUnchanged, FailureItemIds.NotRun));
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(body, writer, shutdown, register);

        var exitCode = await RunAsync(mode);

        AssertExitCode(expectedExitCode, exitCode);
        Assert.Equal(
            [(FailureItemIds.Ordering, FailureItemIds.NotRun), (FailureItemIds.JadeUnchanged, FailureItemIds.NotRun)],
            mode.State.Failures.Select(item => (item.ItemId, item.Nature)));
        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(BothComponents, shutdown.Components);
        AssertFinalLineLast();
    }

    // Without baseline/conformance.json, a captured baseline (its manifest names how capture ran) fails
    // the run through a never-documentable CHK-EXCEPTION item, while a baseline from which nothing was
    // captured (the hand-written not-run manifest, or no manifest, and no record) reports the check as
    // not run and adds no item. The committed case matrix is copied in, so no other step throws.
    [Theory]
    [InlineData("local-processes", true)]
    [InlineData("not-run", false)]
    [InlineData(null, false)]
    public async Task ConformanceResultAbsent_FailsOnlyACapturedBaseline(string? captureExecution, bool expectFailure)
    {
        _repoRoot.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        if (captureExecution is not null)
        {
            _repoRoot.Write("baseline/manifest.json", $$"""{"source":"jade-standin","captureExecution":"{{captureExecution}}","notCaptured":[],"wsdlCaptured":false}""");
        }

        var state = new ReplayRunState();
        var body = new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RunLimit);
        await body.ExecuteAsync(state, limit.Token).WaitAsync(limit.Token);

        var check = state.Checks[CheckNames.Conformance];
        Assert.Equal(CheckOutcomes.NotRun, check.Outcome);
        var exceptions = state.Failures.Where(item => item.ItemId == FailureItemIds.Exception).ToList();
        if (expectFailure)
        {
            var item = Assert.Single(exceptions);
            Assert.Equal(typeof(FileNotFoundException).FullName, item.Nature);
            Assert.StartsWith("stand-in conformance check: baseline/conformance.json is absent although the baseline was captured", item.Detail, StringComparison.Ordinal);
            Assert.True(item.IsNeverDocumentable, "a missing conformance result can never be documented");
            Assert.Equal("baseline/conformance.json absent although the baseline was captured", check.Detail);
            Assert.Contains(
                "[jade-standin] conformance: baseline/conformance.json absent although the baseline was captured; the stand-in conformance evidence is missing",
                StdoutText,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(exceptions);
            Assert.Equal("baseline/conformance.json absent", check.Detail);
            Assert.Contains("[jade-standin] conformance: baseline/conformance.json absent; not run", StdoutText, StringComparison.Ordinal);
        }
    }

    private string StdoutText => _stdout.ToString();

    // The message is fixed: a path would differ between checkouts and runs.
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, CaseMatrixPath)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("No ancestor of the test assembly holds harness/config/case-matrix.json.");
    }

    // A loopback port that was just free: nothing listens there, so every request is refused at once.
    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Every listener on the closed port and the shortest limits, so readiness, reset and the contract
    // fetches fail within seconds.
    private HarnessOptions ClosedPortOptions()
    {
        var closed = $"http://127.0.0.1:{ClosedLoopbackPort().ToString(CultureInfo.InvariantCulture)}";
        var options = Options();
        options.Target = new HarnessEndpoints { RestBaseUrl = closed, SoapBaseUrl = closed, AdminUrl = closed };
        options.Backend = new HarnessEndpoints { RestBaseUrl = closed, SoapBaseUrl = closed, AdminUrl = closed };
        options.ReadinessTimeoutSeconds = 1;
        options.ReadinessPollIntervalSeconds = 1;
        options.RequestTimeoutSeconds = 2;
        return options;
    }

    private static bool IsShutdownItem(FailureItem item) =>
        item.ItemId.StartsWith(FailureItemIds.ShutdownPrefix, StringComparison.Ordinal);

    // Every exit code is checked against both values, so a run that returned anything else fails here.
    private static void AssertExitCode(int expected, int actual)
    {
        Assert.Contains(actual, ExitCodes);
        Assert.Equal(expected, actual);
    }

    // Runs the mode with a 30 s limit: the limit cancels the run's token and also ends the wait, so a hang
    // anywhere in the exit path, where the mode no longer observes the token, still fails the test.
    private static async Task<int> RunAsync(ReplayMode mode, bool cancelBeforeRun = false)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RunLimit);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        if (cancelBeforeRun)
        {
            await run.CancelAsync();
        }

        return await mode.RunAsync(run.Token).WaitAsync(limit.Token);
    }

    private static string LastNonEmptyLine(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).LastOrDefault(line => line.Trim().Length > 0) ?? "";

    private ReplayMode NewMode(
        IReplayBody body,
        IResultsReportWriter writer,
        IComponentShutdown shutdown,
        KnownLimitationsRegister register,
        HarnessOptions? options = null) =>
        new(options ?? Options(), body, writer, shutdown, register, _stdout);

    private HarnessOptions Options(string backendAdminUrl = StandInAdminUrl) => new()
    {
        Target = new HarnessEndpoints
        {
            RestBaseUrl = "http://facade.invalid:65000",
            SoapBaseUrl = "http://facade.invalid:21001",
            AdminUrl = FacadeAdminUrl,
        },
        Backend = new HarnessEndpoints
        {
            RestBaseUrl = "http://standin.invalid:65000",
            SoapBaseUrl = "http://standin.invalid:21001",
            AdminUrl = backendAdminUrl,
        },
        RepoRoot = _repoRoot.Root,
        ReportPath = _repoRoot.PathOf("RESULTS.md"),
        ExecutionMode = "local-processes",
    };

    // The final line must be the harness's last line; the line is shown with the temporary path removed.
    private void AssertFinalLineLast() => Assert.Matches(FinalLinePattern, Redacted(LastNonEmptyLine(StdoutText)));

    private string Redacted(string text) => text.Replace(_repoRoot.Root, RepoRootToken, StringComparison.Ordinal);

    /// <summary>A replay body that runs the given delegate and keeps the exception that escaped it.</summary>
    private sealed class FakeBody(Func<ReplayRunState, CancellationToken, Task> run) : IReplayBody
    {
        /// <summary>The exception the body threw; null when it completed.</summary>
        public Exception? Thrown { get; private set; }

        public static FakeBody NoOp() => new((_, _) => Task.CompletedTask);

        public static FakeBody Adding(params FailureItem[] items) => new((state, _) =>
        {
            foreach (var item in items)
            {
                state.AddFailure(item);
            }

            return Task.CompletedTask;
        });

        public static FakeBody Throwing(Exception exception) => new((_, _) => throw exception);

        public async Task ExecuteAsync(ReplayRunState state, CancellationToken ct)
        {
            try
            {
                await run(state, ct);
            }
            catch (Exception ex)
            {
                Thrown = ex;
                throw;
            }
        }
    }

    /// <summary>Renders with the real writer, so the text is the genuine report, and fails every write.</summary>
    private sealed class ThrowingReportWriter : IResultsReportWriter
    {
        private readonly ResultsReportWriter _renderer = new();
        private readonly List<string> _rendered = [];
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Rendered => _rendered;

        public IReadOnlyList<string> Paths => _paths;

        public int WriteAttempts => _paths.Count;

        public string Render(ResultsReport report)
        {
            var text = _renderer.Render(report);
            _rendered.Add(text);
            return text;
        }

        public void Write(string path, string content)
        {
            _paths.Add(path);
            throw new IOException("report path is a directory");
        }
    }

    /// <summary>Renders with the real writer and keeps every written text in memory instead of on disk.</summary>
    private sealed class RecordingReportWriter : IResultsReportWriter
    {
        private readonly ResultsReportWriter _renderer = new();
        private readonly List<string> _written = [];

        public IReadOnlyList<string> Written => _written;

        public int WriteCount => _written.Count;

        public string Render(ResultsReport report) => _renderer.Render(report);

        public void Write(string path, string content) => _written.Add(content);
    }

    /// <summary>One recorded shutdown call.</summary>
    private sealed record ShutdownCall(string Component, Uri AdminBaseUrl, bool TokenCancelled);

    /// <summary>
    /// Records every shutdown call in order and reports each component stopped, except the first call when
    /// configured to fail it.
    /// </summary>
    private sealed class RecordingShutdown(ShutdownFailure? failFirstCall = null) : IComponentShutdown
    {
        private readonly Lock _gate = new();
        private readonly List<ShutdownCall> _calls = [];

        public IReadOnlyList<ShutdownCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Components => Calls.Select(call => call.Component).ToArray();

        public Task<ShutdownOutcome> ShutdownAsync(ShutdownTarget target, CancellationToken ct)
        {
            bool first;
            lock (_gate)
            {
                first = _calls.Count == 0;
                _calls.Add(new ShutdownCall(target.Component, target.AdminBaseUrl, ct.IsCancellationRequested));
            }

            if (first && failFirstCall == ShutdownFailure.Throws)
            {
                return Task.FromException<ShutdownOutcome>(new HttpRequestException("connection refused"));
            }

            var stopped = !(first && failFirstCall == ShutdownFailure.ReturnsNotStopped);
            return Task.FromResult(new ShutdownOutcome(target.Component, stopped, stopped ? "stopped" : "refused"));
        }
    }
}
