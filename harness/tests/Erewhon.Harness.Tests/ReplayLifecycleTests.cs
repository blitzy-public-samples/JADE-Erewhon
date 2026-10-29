using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Seed;
using Erewhon.Harness;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Modes;
using Erewhon.Harness.Report;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The replay exit path (AAP 0.8.4) and exit code (AAP 0.7.7), driven in-process through the seams of
/// <see cref="ReplayMode"/>: a fake body, a report writer that renders with the real
/// <see cref="ResultsReportWriter"/> but never touches the disk, and a shutdown client that records its
/// calls. Whatever the body, the report write or a shutdown does, every component is shut down, the final
/// line is the last line printed and the exit code is 0 or 1. The admin URLs name reserved
/// <c>.invalid</c> hosts that nothing contacts, because every request goes through the fakes. The
/// conformance-evidence tests run the real <see cref="ReplayBody"/> instead, against a temporary
/// repository root and a closed loopback port, so every network step fails at once. It also
/// covers the lifecycle clients behind that path: shutdown confirmation against scripted and loopback
/// admin replies, state-reset confirmation against scripted admin responses, and the lifecycle console
/// sink, which escapes every printed line and never lets a writer that throws replace a computed result;
/// and the capture mode against a loopback stand-in stub, which refuses or stops with exit code 1 whenever
/// the seed facts cannot be confirmed, and otherwise publishes them in the manifest it writes last.
/// </summary>
/// <remarks>
/// Assertions on the console and the report text use fixed messages, because both hold the per-test
/// temporary path and the run date.
/// </remarks>
public sealed partial class ReplayLifecycleTests : IDisposable
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

    // Created on first use, so only the exit-path tests touch the file system; removed in Dispose.
    private TempDirectory? _repoRoot;
    private readonly StringWriter _stdout = new(CultureInfo.InvariantCulture);

    // The console the lifecycle clients under test print to, in the shutdown, state-reset and console tests.
    private readonly StringWriter _console = new(CultureInfo.InvariantCulture);

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
        _console.Dispose();
        _captureConsole.Dispose();
        _repoRoot?.Dispose();
        if (_seedDirectory is not null)
        {
            Directory.Delete(_seedDirectory, recursive: true);
        }

        // The capture root's path is chosen before its first directory is created, so it may not exist.
        if (_captureRoot is not null && Directory.Exists(_captureRoot))
        {
            Directory.Delete(_captureRoot, recursive: true);
        }
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

    // The body reads two records and compares neither, recording no case result and no failing item, so
    // every recorded item is documented; the uncompared captured cases still fail the run.
    [Fact]
    public async Task CapturedCasesNotCompared_Exit1_NamedBeforeTheFinalLine()
    {
        var body = new FakeBody((state, _) =>
        {
            state.SetCaptured(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["Rest.getAgent"] = ["REST-getAgent-01"],
                ["Soap.getPhoto"] = ["SOAP-getPhoto-01"],
            });
            return Task.CompletedTask;
        });
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(body, writer, shutdown, KnownLimitationsRegister.Empty);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        Assert.Empty(mode.State.Failures);
        Assert.Equal(BothComponents, shutdown.Components);
        Assert.Equal(1, writer.WriteCount);
        Assert.True(writer.Written[^1].Contains("### Captured cases not compared", StringComparison.Ordinal), "the written report lists the captured cases not compared");

        var lines = StdoutText.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();
        Assert.Equal(
            "replay: 2 captured case(s) not compared, so their operations are undocumented differences: REST-getAgent-01, SOAP-getPhoto-01",
            lines[^2]);
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), lines[^1]);
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

    // The SDK pin is baked into the harness assembly at build time, so the run facts state it although the
    // repository root holds no harness/global.json: replay never reads that file.
    [Fact]
    public void DescribeRun_RepoRootWithoutGlobalJson_StatesBuildTimeSdkPin()
    {
        var options = Options();
        Assert.False(File.Exists(RepoRootDirectory.PathOf("harness/global.json")), "the temporary repository root holds no harness/global.json");
        var expected = CommittedSdkPin();
        Assert.False(string.IsNullOrWhiteSpace(expected), "harness/global.json pins an SDK version");

        var facts = ReplayBody.DescribeRun(options, DateTimeOffset.UtcNow, frameworkDescription: null, baselineCommit: null, facadeCommit: null);

        Assert.Equal(expected, facts.SdkPin);
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
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        if (captureExecution is not null)
        {
            RepoRootDirectory.Write("baseline/manifest.json", $$"""{"source":"jade-standin","captureExecution":"{{captureExecution}}","notCaptured":[],"wsdlCaptured":false}""");
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

    /// <summary>The run's repository root: a temporary directory, created on first use.</summary>
    private TempDirectory RepoRootDirectory => _repoRoot ??= new TempDirectory();

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
        RepoRoot = RepoRootDirectory.Root,
        ReportPath = RepoRootDirectory.PathOf("RESULTS.md"),
        ExecutionMode = "local-processes",
    };

    // The final line must be the harness's last line; the line is shown with the temporary path removed.
    private void AssertFinalLineLast() => Assert.Matches(FinalLinePattern, Redacted(LastNonEmptyLine(StdoutText)));

    private string Redacted(string text) => text.Replace(RepoRootDirectory.Root, RepoRootToken, StringComparison.Ordinal);

    // sdk.version of the checked-in harness/global.json, read the way dotnet reads it (comments and trailing
    // commas allowed).
    private static string? CommittedSdkPin()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "harness", "global.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
    }

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

// ---- Shutdown confirmation ----------------------------------------------------------------------
// Shutdown of one component against scripted admin replies: it is reported stopped only when its
// shutdown call is answered with a 2xx status and health then refuses the connection. A failed or
// rejected shutdown call, any HTTP answer to health and every inconclusive probe, a host name that is
// not found included, leave it still running, as a CHK-SHUTDOWN:<component> item with nature
// still-running, within the shutdown limit. Scripted admin URLs name a reserved .invalid host that
// only the scripted handler answers, and a scripted handler opens no watch connection. The real-socket
// tests use the client's own socket handler, watch connection included, against servers that listen on
// loopback only.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and shutting every component down is the cleanup step of the replay exit path.
public sealed partial class ReplayLifecycleTests
{
    private const string ShutdownRequest = "POST " + AdminRoutes.Shutdown;
    private const string HealthRequest = "GET " + AdminRoutes.Health;

    private const string StillRunningItemId = "CHK-SHUTDOWN:" + ShutdownTarget.StandInComponent;
    private const string StillRunningNature = "still-running";
    private const string FailedCallNote = "(shutdown call failed)";
    private const string RejectedCallNote = "(shutdown rejected)";

    // Headroom over the shutdown limit for scheduling; a wait that ignored the limit overruns it by far more.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    // The brief-refusal server answers health for AnsweringFor after the 202, stops listening StopAfterAnswer
    // after its last answer and refuses connections for RefusalWindow. The window closes 120 ms after that
    // answer, well before the next probe on the client's 250 ms spacing.
    private static readonly TimeSpan AnsweringFor = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StopAfterAnswer = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan RefusalWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>How one scripted admin request ends.</summary>
    public enum AdminReply
    {
        /// <summary>HTTP 200.</summary>
        Ok,

        /// <summary>HTTP 202, the stand-in's and the facade's answer to a shutdown call.</summary>
        Accepted,

        /// <summary>HTTP 204.</summary>
        NoContent,

        /// <summary>HTTP 500.</summary>
        ServerError,

        /// <summary>HTTP 503.</summary>
        ServiceUnavailable,

        /// <summary>The connection is refused.</summary>
        ConnectionRefused,

        /// <summary>The resolver answers that the host name does not exist.</summary>
        HostNotFound,

        /// <summary>The resolver answers that the host name has no address.</summary>
        NoData,

        /// <summary>The resolver fails temporarily.</summary>
        TryAgain,

        /// <summary>The resolver fails without recovery.</summary>
        NoRecovery,

        /// <summary>Name resolution fails with no socket error code.</summary>
        NameResolutionWithoutCode,

        /// <summary>No route to the host.</summary>
        HostUnreachable,

        /// <summary>The network is unreachable.</summary>
        NetworkUnreachable,

        /// <summary>No answer before the request's own timeout.</summary>
        TimedOut,

        /// <summary>The response ends prematurely.</summary>
        ResponseEnded,

        /// <summary>
        /// The resolver fails temporarily, but only once the request has been cancelled, as for a probe that
        /// starts just before the shutdown limit: the client then reports the cancellation, not the failure.
        /// </summary>
        TryAgainAfterCancellation,
    }

    // ---- Stop confirmed -------------------------------------------------------------------------

    [Theory]
    [InlineData(AdminReply.Ok, "POST 200")]
    [InlineData(AdminReply.Accepted, "POST 202")]
    [InlineData(AdminReply.NoContent, "POST 204")]
    public async Task AcceptedCall_ThenRefusedConnection_IsStopped(AdminReply shutdownReply, string postText)
    {
        var component = ScriptedComponent.WithHealth(shutdownReply, AdminReply.Ok, AdminReply.ConnectionRefused);

        var (outcome, _) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 2));

        Assert.True(outcome.Stopped, outcome.Detail);
        Assert.Null(outcome.ToFailureItem());
        Assert.Equal(ShutdownTarget.StandInComponent, outcome.Component);
        Assert.Matches($"^{Regex.Escape(postText)}; refused after \\d+ ms$", outcome.Detail);
        Assert.Equal(new[] { ShutdownRequest, HealthRequest, HealthRequest }, component.Requests);
    }

    // ---- Stop not confirmed ---------------------------------------------------------------------

    // The first probe still answers and the name is then never found again, as when a container's name is
    // withdrawn from the network's DNS. That withdrawal also happens to a container whose process still
    // runs, so however often it repeats it never confirms a stop.
    [Theory]
    [InlineData(AdminReply.HostNotFound)]
    [InlineData(AdminReply.NoData)]
    public async Task AcceptedCall_ThenNameNotFoundOnEveryLaterProbe_IsStillRunningAtTheLimit(AdminReply notFound)
    {
        var component = ScriptedComponent.WithHealth(AdminReply.Accepted, AdminReply.Ok, notFound);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal("POST 202; still answering at 1 s (last 200)", outcome.Detail);
        Assert.True(component.HealthProbes >= 3, $"only {component.HealthProbes} health probe(s)");
    }

    [Fact]
    public async Task AcceptedCall_ThenNameNotFoundOnce_ThenAnswers_IsStillRunningAtTheLimit()
    {
        var component = ScriptedComponent.WithHealth(AdminReply.Accepted, AdminReply.HostNotFound, AdminReply.Ok);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal("POST 202; still answering at 1 s (last 200)", outcome.Detail);
        Assert.True(component.HealthProbes >= 2, $"only {component.HealthProbes} health probe(s)");
    }

    // "Host not found" alternates with another inconclusive outcome; neither, nor the two together, is a refusal.
    [Theory]
    [InlineData(AdminReply.TryAgain)]
    [InlineData(AdminReply.NameResolutionWithoutCode)]
    [InlineData(AdminReply.ResponseEnded)]
    [InlineData(AdminReply.HostUnreachable)]
    public async Task AcceptedCall_ThenNameNotFoundInterleaved_IsStillRunningAtTheLimit(AdminReply between)
    {
        var component = ScriptedComponent.WithCyclingHealth(AdminReply.Accepted, AdminReply.HostNotFound, between);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 2));

        AssertStillRunning(outcome, elapsed, limitSeconds: 2);
        Assert.StartsWith("POST 202; not refused by 2 s (last ", outcome.Detail, StringComparison.Ordinal);
        Assert.True(component.HealthProbes >= 3, $"only {component.HealthProbes} health probe(s)");
    }

    [Theory]
    [InlineData(AdminReply.HostNotFound, "host not found")]
    [InlineData(AdminReply.NoData, "host not found")]
    [InlineData(AdminReply.TryAgain, "name lookup failed")]
    [InlineData(AdminReply.NoRecovery, "name lookup failed")]
    [InlineData(AdminReply.NameResolutionWithoutCode, "name lookup failed")]
    [InlineData(AdminReply.HostUnreachable, "host unreachable")]
    [InlineData(AdminReply.NetworkUnreachable, "host unreachable")]
    [InlineData(AdminReply.TimedOut, "timeout")]
    [InlineData(AdminReply.ResponseEnded, "reset")]
    public async Task AcceptedCall_ThenOnlyInconclusiveProbes_IsStillRunningAtTheLimit(AdminReply probe, string last)
    {
        var component = ScriptedComponent.WithHealth(AdminReply.Accepted, probe);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal($"POST 202; not refused by 1 s (last {last})", outcome.Detail);
        Assert.True(component.HealthProbes >= 1, "no health probe");
    }

    // Every probe meets the same temporary lookup failure, but the last one, cut short by the limit, ends in
    // its own timeout first. That cut-off is no observation of the component, so the detail keeps the lookup
    // failure the earlier probe saw.
    [Fact]
    public async Task AcceptedCall_ThenInconclusiveProbe_ThenCutOffAtTheLimit_KeepsTheLastObservation()
    {
        var component = ScriptedComponent.WithHealth(
            AdminReply.Accepted,
            AdminReply.TryAgain,
            AdminReply.TryAgainAfterCancellation);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal("POST 202; not refused by 1 s (last name lookup failed)", outcome.Detail);
        Assert.DoesNotContain("timeout", outcome.Detail, StringComparison.Ordinal);
        Assert.True(component.HealthProbes >= 2, $"only {component.HealthProbes} health probe(s)");
    }

    // ---- Failed shutdown call -------------------------------------------------------------------

    // Health is still polled after a failed call, so the detail says what it showed, but the outcome is
    // never a stop: neither a refusal nor a name that is no longer found makes it one.
    [Theory]
    [InlineData(AdminReply.TimedOut, "POST timeout", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.ResponseEnded, "POST reset", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.ConnectionRefused, "POST connect", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.HostNotFound, "POST connect (host not found)", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.TryAgain, "POST connect (name lookup failed)", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.HostUnreachable, "POST connect (host unreachable)", FailedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.ServerError, "POST 500", RejectedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.ServiceUnavailable, "POST 503", RejectedCallNote, AdminReply.ConnectionRefused)]
    [InlineData(AdminReply.TimedOut, "POST timeout", FailedCallNote, AdminReply.HostNotFound)]
    public async Task FailedCall_ThenStopEvidence_IsStillRunning(
        AdminReply shutdownReply,
        string postText,
        string note,
        AdminReply health)
    {
        var component = ScriptedComponent.WithHealth(shutdownReply, health);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 3, requestSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 3);
        if (health == AdminReply.ConnectionRefused)
        {
            Assert.Matches($"^{Regex.Escape(postText)}; refused after \\d+ ms {Regex.Escape(note)}$", outcome.Detail);
            Assert.Equal(new[] { ShutdownRequest, HealthRequest }, component.Requests);
        }
        else
        {
            Assert.Equal($"{postText}; not refused by 3 s (last host not found) {note}", outcome.Detail);
            Assert.True(component.HealthProbes >= 2, $"only {component.HealthProbes} health probe(s)");
        }
    }

    // ---- Real socket ----------------------------------------------------------------------------

    [Fact]
    public async Task AcceptedCallOverLoopback_ThenListenerClosed_IsStoppedByRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var serverBound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        serverBound.CancelAfter(TimeSpan.FromSeconds(10));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = AnswerOnceThenStopListeningAsync(listener, serverBound.Token);
            var target = new ShutdownTarget(
                ShutdownTarget.StandInComponent,
                new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")));

            ShutdownOutcome outcome;
            using (var shutdown = new HttpComponentShutdown(ShutdownOptions(shutdownSeconds: 5), handler: null, _console))
            {
                outcome = await shutdown.ShutdownAsync(target, ct);
            }

            var requestHead = await server;

            Assert.StartsWith(ShutdownRequest + " HTTP/1.1\r\n", requestHead, StringComparison.Ordinal);
            Assert.True(outcome.Stopped, outcome.Detail);
            Assert.Null(outcome.ToFailureItem());
            Assert.Matches("^POST 202; refused after \\d+ ms$", outcome.Detail);
        }
        finally
        {
            listener.Stop();
        }
    }

    // The port refuses connections only inside a window that no probe on the 250 ms spacing reaches, and
    // then accepts connections without answering, as a stopped container's withdrawn address leaves probes
    // unanswered. Only the end of the watch connection, which the server closes as it stops listening,
    // brings a probe into the window.
    [Fact]
    public async Task AcceptedCallOverLoopback_ThenBriefRefusalBetweenProbes_IsStoppedThroughTheWatch()
    {
        using var serverBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        serverBound.CancelAfter(TimeSpan.FromSeconds(10));

        var listener = StartLoopbackListener(port: 0);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = RefuseBrieflyAfterAnsweringAsync(listener, serverBound.Token);

            var (outcome, _) = await ShutdownOverLoopbackAsync(port, ShutdownOptions(shutdownSeconds: 3));
            await serverBound.CancelAsync();
            var heads = await server;

            Assert.True(outcome.Stopped, outcome.Detail);
            Assert.Null(outcome.ToFailureItem());
            Assert.Matches("^POST 202; refused after \\d+ ms$", outcome.Detail);
            Assert.True(heads.Count >= 2, $"{heads.Count} request(s) answered; no health probe before the stop");
            Assert.StartsWith(ShutdownRequest + " HTTP/1.1\r\n", heads[0], StringComparison.Ordinal);
            Assert.All(
                heads.Skip(1),
                head => Assert.StartsWith(HealthRequest + " HTTP/1.1\r\n", head, StringComparison.Ordinal));
        }
        finally
        {
            listener.Stop();
        }
    }

    // A server that closes the watch connection at once, before the shutdown call, and then answers health
    // for good: the early end of the watch brings only a burst of closely spaced probes, a new watch is
    // opened once the burst finds the component still answering, and nothing confirms a stop.
    [Fact]
    public async Task AcceptedCallOverLoopback_ThenWatchClosedEarly_AndHealthAnswering_IsStillRunningAtTheLimit()
    {
        using var serverBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        serverBound.CancelAfter(TimeSpan.FromSeconds(10));

        var listener = StartLoopbackListener(port: 0);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = CloseWatchThenAnswerAsync(listener, serverBound.Token);

            var (outcome, elapsed) = await ShutdownOverLoopbackAsync(port, ShutdownOptions(shutdownSeconds: 2));
            await serverBound.CancelAsync();
            var heads = await server;

            AssertStillRunning(outcome, elapsed, limitSeconds: 2);
            Assert.Equal("POST 202; still answering at 2 s (last 200)", outcome.Detail);
            Assert.Single(heads, head => head.StartsWith(ShutdownRequest + " HTTP/1.1\r\n", StringComparison.Ordinal));
            var probes = heads.Count(head => head.StartsWith(HealthRequest + " HTTP/1.1\r\n", StringComparison.Ordinal));
            Assert.True(probes >= 20, $"only {probes} health probe(s): no fast burst after the watch ended");
            var watches = heads.Count(head => head.Length == 0);
            Assert.True(watches >= 2, $"{watches} watch connection(s): none opened again after the burst");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static HarnessOptions ShutdownOptions(int shutdownSeconds, int requestSeconds = 1) => new()
    {
        ShutdownTimeoutSeconds = shutdownSeconds,
        ShutdownRequestTimeoutSeconds = requestSeconds,
    };

    private static void AssertStillRunning(ShutdownOutcome outcome, TimeSpan elapsed, int limitSeconds)
    {
        Assert.False(outcome.Stopped, outcome.Detail);
        var item = outcome.ToFailureItem();
        Assert.NotNull(item);
        Assert.Equal(StillRunningItemId, item.ItemId);
        Assert.Equal(StillRunningNature, item.Nature);
        Assert.Equal(outcome.Detail, item.Detail);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(limitSeconds) + Slack,
            $"Shutdown took {elapsed.TotalMilliseconds:0} ms against a {limitSeconds} s limit.");
    }

    // Accepts the watch connection, which sends nothing, and then the shutdown call, stops listening before
    // it answers, so that every later connection is refused, then answers 202 and closes. Returns the
    // request head of the shutdown call.
    private static async Task<string> AnswerOnceThenStopListeningAsync(TcpListener listener, CancellationToken ct)
    {
        using var watch = await listener.AcceptTcpClientAsync(ct);
        using var client = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();

        var head = await ReadRequestHeadAsync(client.GetStream(), ct);
        await WriteEmptyResponseAsync(client, "202 Accepted", ct);
        return head;
    }

    // Accepts the watch connection and holds it without reading, answers the shutdown call with 202, then
    // answers health with 200 until AnsweringFor has passed since the 202. Shortly after that last answer it
    // stops listening and closes the watch, as a stopping server does, and RefusalWindow later binds the
    // port again with a listener that accepts connections and never answers, as a withdrawn container
    // address leaves probes without an answer. Returns the request heads it answered, in order.
    private static async Task<IReadOnlyList<string>> RefuseBrieflyAfterAnsweringAsync(
        TcpListener listener,
        CancellationToken ct)
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var heads = new List<string>();
        var held = new List<TcpClient>();
        TcpListener? blackHole = null;
        try
        {
            held.Add(await listener.AcceptTcpClientAsync(ct));
            var sinceAccepted = new Stopwatch();
            do
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                heads.Add(await ReadRequestHeadAsync(client.GetStream(), ct));
                await WriteEmptyResponseAsync(client, heads.Count == 1 ? "202 Accepted" : "200 OK", ct);
                sinceAccepted.Start();
            }
            while (sinceAccepted.Elapsed < AnsweringFor);

            await Task.Delay(StopAfterAnswer, ct);
            listener.Stop();
            held.ForEach(client => client.Dispose());
            held.Clear();

            await Task.Delay(RefusalWindow, ct);
            blackHole = StartLoopbackListener(port);
            while (true)
            {
                held.Add(await blackHole.AcceptTcpClientAsync(ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test cancels once the shutdown has returned.
            return heads;
        }
        finally
        {
            held.ForEach(client => client.Dispose());
            blackHole?.Stop();
            listener.Stop();
        }
    }

    // Closes the first connection, the watch, as soon as it is accepted, then answers the shutdown call with
    // 202 and every health probe with 200 until cancelled, each connection on its own. Returns the request
    // head of every connection; one that sent no request, as a watch does, gives an empty head.
    private static async Task<IReadOnlyList<string>> CloseWatchThenAnswerAsync(TcpListener listener, CancellationToken ct)
    {
        var heads = new ConcurrentQueue<string>();
        var connections = new List<Task>();
        try
        {
            using (await listener.AcceptTcpClientAsync(ct))
            {
                heads.Enqueue(string.Empty);
            }

            while (true)
            {
                connections.Add(AnswerConnectionAsync(await listener.AcceptTcpClientAsync(ct), heads, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The test cancels once the shutdown has returned.
            await Task.WhenAll(connections);
            return [.. heads];
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AnswerConnectionAsync(TcpClient client, ConcurrentQueue<string> heads, CancellationToken ct)
    {
        using (client)
        {
            string head;
            try
            {
                head = await ReadRequestHeadAsync(client.GetStream(), ct);
            }
            catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // A connection reset, or still open when the test ends, without a request.
                head = string.Empty;
            }

            heads.Enqueue(head);
            if (head.Length == 0)
            {
                return;
            }

            try
            {
                var status = head.StartsWith(ShutdownRequest + " ", StringComparison.Ordinal) ? "202 Accepted" : "200 OK";
                await WriteEmptyResponseAsync(client, status, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // A probe cut off at the shutdown limit closes its connection before the answer is written.
                return;
            }
        }
    }

    private static async Task WriteEmptyResponseAsync(TcpClient client, string status, CancellationToken ct)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
            ct);
        await stream.FlushAsync(ct);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    // ReuseAddress lets a listener bind a port whose closed connections are still in TIME_WAIT.
    private static TcpListener StartLoopbackListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        return listener;
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        const int MaximumHead = 64 * 1024;
        var buffer = new byte[4096];
        var head = new StringBuilder();
        while (head.Length < MaximumHead && !head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return head.ToString();
    }

    private async Task<(ShutdownOutcome Outcome, TimeSpan Elapsed)> ShutdownAsync(
        ScriptedComponent component,
        HarnessOptions options)
    {
        using var shutdown = new HttpComponentShutdown(options, component, _console);
        var target = new ShutdownTarget(ShutdownTarget.StandInComponent, new Uri(StandInAdminUrl));
        var clock = Stopwatch.StartNew();
        var outcome = await shutdown.ShutdownAsync(target, TestContext.Current.CancellationToken);
        return (outcome, clock.Elapsed);
    }

    // Shuts down a loopback admin listener through the client's own socket handler, watch connection included.
    private async Task<(ShutdownOutcome Outcome, TimeSpan Elapsed)> ShutdownOverLoopbackAsync(
        int port,
        HarnessOptions options)
    {
        using var shutdown = new HttpComponentShutdown(options, handler: null, _console);
        var target = new ShutdownTarget(
            ShutdownTarget.StandInComponent,
            new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")));
        var clock = Stopwatch.StartNew();
        var outcome = await shutdown.ShutdownAsync(target, TestContext.Current.CancellationToken);
        return (outcome, clock.Elapsed);
    }

    /// <summary>
    /// Answers the shutdown call with one scripted reply and each health probe with the next of its
    /// scripted replies; an unscripted request, or one that does not ask for the connection to be closed,
    /// fails the shutdown instead of reaching the network.
    /// </summary>
    private sealed class ScriptedComponent : HttpMessageHandler
    {
        private readonly AdminReply _shutdownReply;
        private readonly AdminReply[] _healthReplies;
        private readonly bool _cycle;

        private ScriptedComponent(AdminReply shutdownReply, AdminReply[] healthReplies, bool cycle)
        {
            if (healthReplies.Length == 0)
            {
                throw new ArgumentException("A scripted component needs at least one health reply.", nameof(healthReplies));
            }

            _shutdownReply = shutdownReply;
            _healthReplies = healthReplies;
            _cycle = cycle;
        }

        /// <summary>Every request received, as <c>&lt;method&gt; &lt;path and query&gt;</c>.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>The number of health probes answered.</summary>
        public int HealthProbes { get; private set; }

        /// <summary>Health probes get <paramref name="healthReplies"/> in order, then the last one forever.</summary>
        public static ScriptedComponent WithHealth(AdminReply shutdownReply, params AdminReply[] healthReplies) =>
            new(shutdownReply, healthReplies, cycle: false);

        /// <summary>Health probes get <paramref name="healthReplies"/> in order, over and over.</summary>
        public static ScriptedComponent WithCyclingHealth(AdminReply shutdownReply, params AdminReply[] healthReplies) =>
            new(shutdownReply, healthReplies, cycle: true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var line = $"{request.Method} {request.RequestUri?.PathAndQuery}";
            Requests.Add(line);
            if (request.Headers.ConnectionClose != true)
            {
                throw new InvalidOperationException($"Admin request without Connection: close: {line}");
            }

            AdminReply reply;
            if (Requests.Count == 1 && string.Equals(line, ShutdownRequest, StringComparison.Ordinal))
            {
                reply = _shutdownReply;
            }
            else if (Requests.Count > 1 && string.Equals(line, HealthRequest, StringComparison.Ordinal))
            {
                var index = HealthProbes++;
                reply = _cycle
                    ? _healthReplies[index % _healthReplies.Length]
                    : _healthReplies[Math.Min(index, _healthReplies.Length - 1)];
            }
            else
            {
                throw new InvalidOperationException($"Unscripted admin request: {line}");
            }

            return await RespondAsync(reply, cancellationToken);
        }

        private static async Task<HttpResponseMessage> RespondAsync(AdminReply reply, CancellationToken ct)
        {
            switch (reply)
            {
                case AdminReply.Ok:
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case AdminReply.Accepted:
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                case AdminReply.NoContent:
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case AdminReply.ServerError:
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                case AdminReply.ServiceUnavailable:
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                case AdminReply.TimedOut:
                    // Waits until the shutdown client's own request timeout cancels the request.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("The request was never cancelled.");
                case AdminReply.TryAgainAfterCancellation:
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw Failure(AdminReply.TryAgain);
                    }

                    throw new InvalidOperationException("The request was never cancelled.");
                default:
                    throw Failure(reply);
            }
        }

        // The exceptions SocketsHttpHandler raises for these outcomes: the request error with the socket
        // error as its inner exception, where the platform reports one.
        private static HttpRequestException Failure(AdminReply reply) => reply switch
        {
            AdminReply.ConnectionRefused => SocketFailure(
                HttpRequestError.ConnectionError, "Connection refused", SocketError.ConnectionRefused),
            AdminReply.HostNotFound => SocketFailure(
                HttpRequestError.NameResolutionError, "Name or service not known", SocketError.HostNotFound),
            AdminReply.NoData => SocketFailure(
                HttpRequestError.NameResolutionError, "No address associated with hostname", SocketError.NoData),
            AdminReply.TryAgain => SocketFailure(
                HttpRequestError.NameResolutionError, "Temporary failure in name resolution", SocketError.TryAgain),
            AdminReply.NoRecovery => SocketFailure(
                HttpRequestError.NameResolutionError, "Non-recoverable failure in name resolution", SocketError.NoRecovery),
            AdminReply.NameResolutionWithoutCode => new HttpRequestException(
                HttpRequestError.NameResolutionError, "Name resolution failed (standin.invalid:9090)"),
            AdminReply.HostUnreachable => SocketFailure(
                HttpRequestError.ConnectionError, "No route to host", SocketError.HostUnreachable),
            AdminReply.NetworkUnreachable => SocketFailure(
                HttpRequestError.ConnectionError, "Network is unreachable", SocketError.NetworkUnreachable),
            AdminReply.ResponseEnded => new HttpRequestException(
                HttpRequestError.ResponseEnded, "The response ended prematurely."),
            _ => throw new ArgumentOutOfRangeException(nameof(reply), reply, "Unknown admin reply."),
        };

        private static HttpRequestException SocketFailure(HttpRequestError error, string message, SocketError code) =>
            new(error, $"{message} (standin.invalid:9090)", new SocketException((int)code));
    }
}

// ---- State-reset confirmation -------------------------------------------------------------------
// State-reset confirmation against scripted admin responses: after every reset the generation must be
// proven to have increased, the mutation count must be 0 and the source must be the stand-in, and a
// first capture always confirms the reference date it expects. Every mismatch is one
// CHK-RESET:<point> item with a deterministic mismatch:<field>:<observed> nature. No request leaves
// the process: the admin URL names a reserved .invalid host that only the scripted handler answers.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and every replay begins with this reset.
public sealed partial class ReplayLifecycleTests
{
    private const string GenerationField = "generation";
    private const string MutationsField = "mutations";
    private const string ReferenceDateField = "referenceDate";
    private const string SeedSha256Field = "seedSha256";
    private const string ExpectedReferenceDateKey = "Harness:ExpectedReferenceDate";

    private const string DefaultReferenceDate = "2026-01-01";
    private const string OtherReferenceDate = "2025-06-30";
    private const long PreviousGeneration = 5;

    private const string InfoRequest = "GET " + AdminRoutes.Info;
    private const string SeedResetRequest =
        "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=" + AdminRoutes.ResetStateSeed;
    private const string EmptyResetRequest =
        "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=" + AdminRoutes.ResetStateEmpty;

    // Created on first use, so only the tests that need a seed touch the file system; removed in Dispose.
    private string? _seedDirectory;
    private string? _seedSha256;

    /// <summary>
    /// A one-file seed directory, created on first use, so a first capture has a fingerprint to expect.
    /// </summary>
    private string StateResetSeedDirectory
    {
        get
        {
            if (_seedDirectory is null)
            {
                _seedDirectory = Directory.CreateTempSubdirectory("erewhon-statereset-").FullName;
                File.WriteAllText(Path.Combine(_seedDirectory, "Company.txt"), "Erewhon Investments Inc\tErewhon House\n");
            }

            return _seedDirectory;
        }
    }

    /// <summary>The fingerprint of <see cref="StateResetSeedDirectory"/>, which it creates on first use.</summary>
    private string StateResetSeedSha256 => _seedSha256 ??= SeedFingerprint.ComputeSha256Hex(StateResetSeedDirectory);

    /// <summary>How the read before the reset ends.</summary>
    public enum PreResetRead
    {
        /// <summary>HTTP 503.</summary>
        ServiceUnavailable,

        /// <summary>The connection is refused.</summary>
        ConnectionRefused,

        /// <summary>The response ends prematurely.</summary>
        ConnectionReset,

        /// <summary>A 2xx body that is not a JSON object.</summary>
        NotJson,

        /// <summary>A valid info object without a generation.</summary>
        NoGeneration,
    }

    // ---- Generation -----------------------------------------------------------------------------

    [Theory]
    [InlineData(PreResetRead.ServiceUnavailable, "before-503")]
    [InlineData(PreResetRead.ConnectionRefused, "before-connect")]
    [InlineData(PreResetRead.ConnectionReset, "before-reset")]
    [InlineData(PreResetRead.NotJson, "before-invalid")]
    [InlineData(PreResetRead.NoGeneration, "before-null")]
    public async Task PreResetGenerationUnknown_RecordsOneGenerationMismatch_AndStillResets(
        PreResetRead preRead,
        string observed)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Respond(preRead))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(StateResetSeedSha256, DefaultReferenceDate));

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(GenerationField, observed), item.Nature);
        Assert.Contains("pre-reset generation is unavailable", item.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, admin.Requests);
        Assert.Equal(PreviousGeneration + 1, result.After?.Generation);
    }

    [Fact]
    public async Task PreResetReadFailure_StillChecksTheOtherFields()
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Respond(PreResetRead.ServiceUnavailable))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, mutations: 3)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var itemId = FailureItemIds.Reset(ResetPoints.ReplayStart);
        Assert.Equal(
            new[]
            {
                (itemId, FailureItemIds.Mismatch(GenerationField, "before-503")),
                (itemId, FailureItemIds.Mismatch(MutationsField, "3")),
            },
            result.Failures.Select(failure => (failure.ItemId, failure.Nature)));
    }

    [Theory]
    [InlineData(PreviousGeneration)]
    [InlineData(PreviousGeneration - 1)]
    [InlineData(0L)]
    public async Task GenerationNotIncreased_RecordsTheObservedGeneration(long after)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(after)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(
            FailureItemIds.Mismatch(GenerationField, after.ToString(CultureInfo.InvariantCulture)),
            item.Nature);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostResetGenerationMissing_RecordsOneNullMismatch(bool preReadSucceeds)
    {
        var admin = new ScriptedAdmin()
            .Then(
                InfoRequest,
                () => preReadSucceeds ? Json(Info(PreviousGeneration)) : Respond(PreResetRead.ServiceUnavailable))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(generation: null)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(GenerationField, "null"), item.Nature);
    }

    [Fact]
    public async Task GenerationIncreased_WithMatchingFacts_Succeeds()
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(StateResetSeedSha256, DefaultReferenceDate));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, admin.Requests);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} reset {AdminRoutes.ResetStateSeed} at {ResetPoints.ReplayStart}: generation 6",
            _console.ToString(),
            StringComparison.Ordinal);
    }

    // ---- Seed facts after every reset -----------------------------------------------------------

    // The stand-in fingerprints its seed directory and reports its reference date whichever state it
    // builds, so an empty reset confirms both facts exactly as a seed reset does, in the same order;
    // only an expectation without seed facts leaves them unchecked.
    [Theory]
    [InlineData(AdminRoutes.ResetStateSeed, false, false, true)]
    [InlineData(AdminRoutes.ResetStateSeed, true, false, true)]
    [InlineData(AdminRoutes.ResetStateSeed, false, true, true)]
    [InlineData(AdminRoutes.ResetStateSeed, true, true, true)]
    [InlineData(AdminRoutes.ResetStateSeed, true, true, false)]
    [InlineData(AdminRoutes.ResetStateEmpty, false, false, true)]
    [InlineData(AdminRoutes.ResetStateEmpty, true, false, true)]
    [InlineData(AdminRoutes.ResetStateEmpty, false, true, true)]
    [InlineData(AdminRoutes.ResetStateEmpty, true, true, true)]
    [InlineData(AdminRoutes.ResetStateEmpty, true, true, false)]
    public async Task SeedFacts_AreConfirmedAfterEveryReset(
        string state,
        bool seedChanged,
        bool dateChanged,
        bool factsExpected)
    {
        var observedSeed = seedChanged ? new string('0', 64) : StateResetSeedSha256;
        var observedDate = dateChanged ? OtherReferenceDate : DefaultReferenceDate;
        var resetRequest = string.Equals(state, AdminRoutes.ResetStateEmpty, StringComparison.Ordinal)
            ? EmptyResetRequest
            : SeedResetRequest;
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(resetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, observedDate, observedSeed)));
        var expected = factsExpected
            ? ResetExpectations.FromManifest(StateResetSeedSha256, DefaultReferenceDate)
            : ResetExpectations.None;

        var result = await ResetToStateAsync(admin, state, expected);

        var natures = new List<string>();
        if (factsExpected && seedChanged)
        {
            natures.Add(FailureItemIds.Mismatch(SeedSha256Field, observedSeed));
        }

        if (factsExpected && dateChanged)
        {
            natures.Add(FailureItemIds.Mismatch(ReferenceDateField, OtherReferenceDate));
        }

        var itemId = FailureItemIds.Reset(ResetPoints.ReplayStart);
        Assert.Equal(
            natures.Select(nature => (itemId, nature)),
            result.Failures.Select(failure => (failure.ItemId, failure.Nature)));
        Assert.Equal(natures.Count == 0, result.Succeeded);
        Assert.Equal(new[] { InfoRequest, resetRequest, InfoRequest }, admin.Requests);
        Assert.Equal(PreviousGeneration + 1, result.After?.Generation);
    }

    private async Task<StateResetResult> ResetToStateAsync(
        ScriptedAdmin admin,
        string state,
        ResetExpectations expected)
    {
        using var reset = new StateReset(StateResetOptions(), admin, _console);
        return await reset.ResetAsync(ResetPoints.ReplayStart, state, expected, TestContext.Current.CancellationToken);
    }

    // ---- First-capture reference date -----------------------------------------------------------

    [Theory]
    [InlineData("", DefaultReferenceDate)]
    [InlineData("   ", DefaultReferenceDate)]
    [InlineData(" 2025-06-30 ", OtherReferenceDate)]
    [InlineData("2026-1-1", "2026-1-1")]
    public void EffectiveExpectedReferenceDate_BlankIsTheDefault_OtherwiseTrimmed(string configured, string effective)
    {
        var options = new HarnessOptions { ExpectedReferenceDate = configured };

        Assert.Equal(effective, options.EffectiveExpectedReferenceDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FirstCapture_BlankExpectedDate_ExpectsTheDefault(string configured)
    {
        var options = StateResetOptions(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(new ResetExpectations(StateResetSeedSha256, DefaultReferenceDate), expected);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} is blank; expecting the default reference date {DefaultReferenceDate}",
            _console.ToString(),
            StringComparison.Ordinal);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, OtherReferenceDate)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, OtherReferenceDate)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.CaptureStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(ReferenceDateField, OtherReferenceDate), item.Nature);
    }

    [Theory]
    [InlineData("2026-1-1", DefaultReferenceDate)]
    [InlineData("01/01/2026", DefaultReferenceDate)]
    [InlineData("2026-01-01T00:00:00", DefaultReferenceDate)]
    [InlineData("not-a-date", "not-a-date")]
    public async Task FirstCapture_InvalidExpectedDate_AlwaysRecordsReferenceDateMismatch(
        string configured,
        string reported)
    {
        var options = StateResetOptions(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(StateResetSeedSha256, expected.SeedSha256);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, reported)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, reported)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.CaptureStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(ReferenceDateField, reported), item.Nature);
        Assert.Contains($"{ExpectedReferenceDateKey} is not a yyyy-MM-dd date", item.Detail, StringComparison.Ordinal);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} '{configured}' is not a yyyy-MM-dd date",
            _console.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DefaultReferenceDate, DefaultReferenceDate)]
    [InlineData(OtherReferenceDate, OtherReferenceDate)]
    [InlineData(" 2025-06-30 ", OtherReferenceDate)]
    public async Task FirstCapture_ValidExpectedDate_Passes(string configured, string reported)
    {
        var options = StateResetOptions(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(new ResetExpectations(StateResetSeedSha256, reported), expected);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, reported)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, reported)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(ExpectedReferenceDateKey, _console.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "\t")]
    public async Task FromManifest_BlankValues_AreNotChecked(string? seedSha256, string? referenceDate)
    {
        var expected = ResetExpectations.FromManifest(seedSha256, referenceDate);

        Assert.Null(expected.SeedSha256);
        Assert.Null(expected.ReferenceDate);

        var otherSeed = new string('0', 64);
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, OtherReferenceDate, otherSeed)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, OtherReferenceDate, otherSeed)));

        var result = await ResetAsync(admin, expected);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
    }

    // ---- Reference date parsing -----------------------------------------------------------------

    // An xsd:date timezone, up to the 14:00 bound either way, does not change the reported date.
    [Theory]
    [InlineData(DefaultReferenceDate)]
    [InlineData("2026-01-01Z")]
    [InlineData("2026-01-01+14:00")]
    [InlineData("2026-01-01-14:00")]
    public async Task ReportedXsdDate_OnTheExpectedDate_Passes(string reported)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, reported)));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(StateResetSeedSha256, DefaultReferenceDate));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
    }

    // Both sides must be exact xsd:dates naming the same day, whatever the host's timezone: surrounding
    // whitespace, unpadded parts, dateTimes, offsets past 14:00 or with 60+ minutes, impossible dates,
    // other notations, non-ASCII (fullwidth) digits and a missing member never match, and two equal texts
    // that are not dates are not compared as strings. A null reported value removes the member.
    [Theory]
    [InlineData(DefaultReferenceDate, "2026-1-1")]
    [InlineData(DefaultReferenceDate, " 2026-01-01")]
    [InlineData(DefaultReferenceDate, "2026-01-01 ")]
    [InlineData(DefaultReferenceDate, "2026-01-01T00:30:00+14:00")]
    [InlineData(DefaultReferenceDate, "2026-01-01T00:00:00Z")]
    [InlineData(DefaultReferenceDate, "2026-01-01+14:01")]
    [InlineData(DefaultReferenceDate, "2026-01-01+15:00")]
    [InlineData(DefaultReferenceDate, "2026-01-01+13:60")]
    [InlineData(DefaultReferenceDate, "2026-02-30")]
    [InlineData(DefaultReferenceDate, "01/01/2026")]
    [InlineData(DefaultReferenceDate, "\uFF12\uFF10\uFF12\uFF16-01-01")]
    [InlineData(DefaultReferenceDate, null)]
    [InlineData("2026-1-1", "2026-1-1")]
    [InlineData("2026-02-30", "2026-02-30")]
    public async Task ReportedDateNotTheExpectedXsdDate_RecordsTheRawReportedText(string expectedDate, string? reported)
    {
        var after = Info(PreviousGeneration + 1);
        if (reported is null)
        {
            after.Remove(ReferenceDateField);
        }
        else
        {
            after[ReferenceDateField] = reported;
        }

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(after));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(StateResetSeedSha256, expectedDate));

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(ReferenceDateField, reported ?? "null"), item.Nature);
        Assert.Contains($"expected {expectedDate}", item.Detail, StringComparison.Ordinal);
        if (string.Equals(expectedDate, DefaultReferenceDate, StringComparison.Ordinal))
        {
            Assert.DoesNotContain("is not a yyyy-MM-dd date", item.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(
                $"expected {expectedDate}, which is not a yyyy-MM-dd date",
                item.Detail,
                StringComparison.Ordinal);
        }
    }

    // ---- Admin URL ------------------------------------------------------------------------------

    // The second URL has no scheme, so it parses with the user name as its scheme.
    [Theory]
    [InlineData("http://alice:example-pass@standin.invalid:9090")]
    [InlineData("alice:example-pass@standin.invalid:9090")]
    [InlineData("ftp://alice:example-pass@standin.invalid:9090")]
    public async Task CredentialBearingAdminUrl_IsRejectedBeforeAnyRequest_WithoutDisclosingIt(string adminUrl)
    {
        var options = StateResetOptions();
        options.Backend.AdminUrl = adminUrl;
        var admin = new ScriptedAdmin();

        var result = await ResetAsync(admin, ResetExpectations.None, options);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch("http", "invalid-url"), item.Nature);
        Assert.Empty(admin.Requests);

        var reader = new ScriptedAdmin();
        using (var reset = new StateReset(options, reader, _console))
        {
            Assert.Null(await reset.ReadInfoAsync(adminUrl, TestContext.Current.CancellationToken));
        }

        Assert.Empty(reader.Requests);

        Assert.NotNull(item.Detail);
        foreach (var credential in new[] { "example-pass", "alice" })
        {
            Assert.DoesNotContain(credential, item.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(credential, _console.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- Undecodable info strings ---------------------------------------------------------------

    // JSON escapes as raw text: an unpaired high and low surrogate, which parse but throw when decoded,
    // and a decodable letter that takes their place in the control body.
    private const string LoneHighSurrogate = "\\uD800";
    private const string LoneLowSurrogate = "\\uDC00";
    private const string DecodableEscape = "\\u0041";

    // Each row puts an unpaired surrogate in one member the reader decodes. The last row puts it in a
    // member name, which the case-insensitive lookup of an absent member (frameworkDescription) decodes.
    [Theory]
    [InlineData("source", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("source", "\"jade-" + LoneLowSurrogate + "standin\"")]
    [InlineData("seedSha256", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("referenceDate", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("frameworkDescription", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("framework", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("generation", "\"" + LoneHighSurrogate + "\"")]
    [InlineData("mutations", "\"" + LoneHighSurrogate + "\"")]
    [InlineData(LoneHighSurrogate, "1")]
    public async Task UndecodableInfoString_MakesTheWholeInfoInvalid_WithoutThrowing(string member, string valueJson)
    {
        var body = RawInfo(PreviousGeneration + 1, member, valueJson);
        var expected = ResetExpectations.FromManifest(StateResetSeedSha256, DefaultReferenceDate);
        var itemId = FailureItemIds.Reset(ResetPoints.ReplayStart);

        // With a decodable escape in place of the surrogate the body is a valid info, so the surrogate
        // alone makes it invalid.
        Assert.NotNull(AdminInfo.Parse(Decodable(body)));
        Assert.Null(AdminInfo.Parse(body));

        var postReset = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => RawJson(body));

        var postResult = await ResetAsync(postReset, expected);

        var postItem = Assert.Single(postResult.Failures);
        Assert.Equal(
            (itemId, FailureItemIds.Mismatch("http", "info-invalid")),
            (postItem.ItemId, postItem.Nature));
        Assert.Null(postResult.After);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, postReset.Requests);

        var preReset = new ScriptedAdmin()
            .Then(InfoRequest, () => RawJson(body))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));

        var preResult = await ResetAsync(preReset, expected);

        var preItem = Assert.Single(preResult.Failures);
        Assert.Equal(
            (itemId, FailureItemIds.Mismatch(GenerationField, "before-invalid")),
            (preItem.ItemId, preItem.Nature));
        Assert.Equal(PreviousGeneration + 1, preResult.After?.Generation);

        var reader = new ScriptedAdmin().Then(InfoRequest, () => RawJson(body));
        using (var reset = new StateReset(StateResetOptions(), reader, _console))
        {
            Assert.Null(await reset.ReadInfoAsync(StandInAdminUrl, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new[] { InfoRequest }, reader.Requests);
    }

    private static string Decodable(string json) => json
        .Replace(LoneHighSurrogate, DecodableEscape, StringComparison.Ordinal)
        .Replace(LoneLowSurrogate, DecodableEscape, StringComparison.Ordinal);

    private static HttpResponseMessage RawJson(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // The stand-in's /admin/info written as raw JSON text, because a JsonObject cannot hold an unpaired
    // surrogate escape. The value of member is replaced by valueJson, or member is appended when the
    // stand-in's info has none; member is written as raw text too, so it may hold an escape.
    private string RawInfo(long generation, string member, string valueJson)
    {
        var members = new List<(string Name, string ValueJson)>
        {
            ("source", $"\"{SourceLabels.StandIn}\""),
            ("seedSha256", $"\"{StateResetSeedSha256}\""),
            ("referenceDate", $"\"{DefaultReferenceDate}\""),
            ("generation", generation.ToString(CultureInfo.InvariantCulture)),
            ("mutations", "0"),
        };
        var index = members.FindIndex(existing => string.Equals(existing.Name, member, StringComparison.Ordinal));
        if (index >= 0)
        {
            members[index] = (member, valueJson);
        }
        else
        {
            members.Add((member, valueJson));
        }

        return "{" + string.Join(",", members.Select(pair => $"\"{pair.Name}\":{pair.ValueJson}")) + "}";
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static HttpResponseMessage ResetAccepted() => new(HttpStatusCode.OK);

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private HarnessOptions StateResetOptions(string? expectedReferenceDate = null)
    {
        var options = new HarnessOptions { SeedDirectory = StateResetSeedDirectory, RequestTimeoutSeconds = 5 };
        options.Backend.AdminUrl = StandInAdminUrl;
        if (expectedReferenceDate is not null)
        {
            options.ExpectedReferenceDate = expectedReferenceDate;
        }

        return options;
    }

    // The stand-in's /admin/info members; generation is left out when null, as a backend without one would.
    private JsonObject Info(
        long? generation,
        string referenceDate = DefaultReferenceDate,
        string? seedSha256 = null,
        long mutations = 0)
    {
        var info = new JsonObject
        {
            ["source"] = SourceLabels.StandIn,
            ["seedSha256"] = seedSha256 ?? StateResetSeedSha256,
            ["referenceDate"] = referenceDate,
            ["mutations"] = mutations,
        };
        if (generation is { } value)
        {
            info["generation"] = value;
        }

        return info;
    }

    private HttpResponseMessage Respond(PreResetRead preRead) => preRead switch
    {
        PreResetRead.ServiceUnavailable => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        PreResetRead.ConnectionRefused => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (standin.invalid:9090)"),
        PreResetRead.ConnectionReset => throw new HttpRequestException(
            HttpRequestError.ResponseEnded, "The response ended prematurely."),
        PreResetRead.NotJson => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("starting", Encoding.UTF8, "text/plain"),
        },
        PreResetRead.NoGeneration => Json(Info(generation: null)),
        _ => throw new ArgumentOutOfRangeException(nameof(preRead), preRead, "Unknown pre-reset read."),
    };

    private async Task<StateResetResult> ResetAsync(
        ScriptedAdmin admin,
        ResetExpectations expected,
        HarnessOptions? options = null,
        string point = ResetPoints.ReplayStart)
    {
        using var reset = new StateReset(options ?? StateResetOptions(), admin, _console);
        return await reset.ResetAsync(point, AdminRoutes.ResetStateSeed, expected, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers each admin request with the next scripted response, after checking it is the scripted
    /// method and path; an unscripted request fails the test instead of reaching the network.
    /// </summary>
    private sealed class ScriptedAdmin : HttpMessageHandler
    {
        private readonly Queue<(string Request, Func<HttpResponseMessage> Respond)> _script = new();

        /// <summary>Every request received, as <c>&lt;method&gt; &lt;path and query&gt;</c>.</summary>
        public List<string> Requests { get; } = [];

        public ScriptedAdmin Then(string request, Func<HttpResponseMessage> respond)
        {
            _script.Enqueue((request, respond));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var line = $"{request.Method} {request.RequestUri?.PathAndQuery}";
            Requests.Add(line);
            if (!_script.TryDequeue(out var step) || !string.Equals(step.Request, line, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unscripted admin request: {line}");
            }

            return Task.FromResult(step.Respond());
        }
    }
}

// ---- Capture mode -------------------------------------------------------------------------------
// The capture mode against an in-process loopback stand-in stub, in a temporary repository root of its
// own that holds a copy of the committed case matrix and a one-file seed directory; the repository's
// own baseline/ is never touched. Expected seed facts no stand-in can confirm refuse the capture before
// the stand-in is contacted; a capture-start reset whose seed hash or reference date differs from the
// first capture's or the manifest's refuses it before any write; an empty-state bracket reset that is
// not confirmed stops it after the bracket's seed restore, with no manifest; and a confirmed capture
// publishes the confirmed seed facts in a manifest written after every other file. Each refusal or
// stop exits 1.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and capture's refusals and stops follow the reset confirmation covered above. The field
// names and the reset requests come from the state-reset members, and ReferenceDate from the
// console-sink members.
public sealed partial class ReplayLifecycleTests
{
    private const string CaptureOtherReferenceDate = "2025-06-01";
    private const string OtherSeedSha256 = "34df80d4c1b0a5e2f7d8c9b0a1e2f3d4c5b6a7980f1e2d3c4b5a69788796a5b4";

    private const string ManifestFile = "baseline/manifest.json";
    private const string ManifestTemporaryFile = "baseline/.manifest.json.tmp";
    private const string ConformanceFile = "baseline/conformance.json";
    private const string ContractsWsdlFile = "contracts/wsdl/ErewhonWebService.wsdl";

    // A record file no case of the matrix writes, so only a clear can remove it.
    private const string SentinelRecord = "baseline/records/rest/999-getAgent-99.json";

    private const string NotTouched = "capture cannot start; baseline/ not touched";
    private const string RepoRootNotFound = "No directory above the test binaries holds harness/config.";

    // Readiness and request limits: generous for a loaded host, never reached by a passing test.
    private const int LimitSeconds = 10;

    private static readonly string[] AdminRequests = ["GET /health", "GET /admin/info", SeedResetRequest];

    // The console the capture mode under test prints to.
    private readonly StringWriter _captureConsole = new();

    // Created on first use, so only the capture mode tests touch the file system; removed in Dispose.
    private string? _captureRoot;
    private string? _captureSeedSha256;

    /// <summary>
    /// The capture mode tests' temporary repository root, created on first use with a copy of the committed
    /// case matrix, a one-file seed directory, whose fingerprint <see cref="CaptureSeedSha256"/> holds, and a
    /// record file no case writes.
    /// </summary>
    private string CaptureRoot
    {
        get
        {
            if (_captureRoot is null)
            {
                // Full resolves against this property, so the root's path is chosen before anything is created.
                // The run sets TMPDIR to a per-clone directory, so parallel checkouts never share a root.
                _captureRoot = Path.Combine(Path.GetTempPath(), "erewhon-capture-mode-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Full("harness/config"));
                File.Copy(
                    Path.Combine(FindCaptureRepoRoot(), "harness", "config", "case-matrix.json"), Full("harness/config/case-matrix.json"));
                Directory.CreateDirectory(Full("Erewhon/DataFiles"));
                File.WriteAllBytes(Full("Erewhon/DataFiles/Clients.txt"), "Hank Williams\t1 Main St\tWellington\n"u8.ToArray());
                _captureSeedSha256 = SeedFingerprint.ComputeSha256Hex(Full("Erewhon/DataFiles"));
                Directory.CreateDirectory(Full("baseline/records/rest"));
                File.WriteAllBytes(Full(SentinelRecord), "{\"sentinel\":true}\n"u8.ToArray());
            }

            return _captureRoot;
        }
    }

    /// <summary>The fingerprint of the seed directory below <see cref="CaptureRoot"/>, which it creates on first use.</summary>
    private string CaptureSeedSha256
    {
        get
        {
            // Reading the root runs its setup, which fingerprints the seed directory.
            _ = CaptureRoot;
            return _captureSeedSha256
                ?? throw new InvalidOperationException("The capture root was left without a seed fingerprint by a failed setup.");
        }
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing-seed-directory", "seedSha256 'unavailable'")]
    [InlineData("malformed-reference-date", "referenceDate 'invalid'")]
    [InlineData("placeholder-manifest", "seedSha256 'unavailable'")]
    [InlineData("blank-manifest-fact", "referenceDate blank")]
    public async Task UnconfirmableSeedFacts_RefuseBeforeTheStandInIsContacted(string setup, string unconfirmable)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, TestCancellation);
        var options = CaptureOptions(standIn);
        switch (setup)
        {
            case "missing-seed-directory":
                options.SeedDirectory = Full("Erewhon/Missing");
                break;
            case "malformed-reference-date":
                options.ExpectedReferenceDate = "01/01/2026";
                break;
            case "placeholder-manifest":
                WriteManifest(options, "unavailable", ReferenceDate);
                break;
            case "blank-manifest-fact":
                WriteManifest(options, CaptureSeedSha256, "");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setup), setup, null);
        }

        var before = Snapshot();

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Empty(standIn.Requests);
        Assert.Equal(before, Snapshot());
        Assert.EndsWith($"seed facts cannot be confirmed (expected {unconfirmable}); {NotTouched}", LastLine());
    }

    [Theory]
    [InlineData(false, ReferenceDateField)]
    [InlineData(false, SeedSha256Field)]
    [InlineData(true, ReferenceDateField)]
    [InlineData(true, SeedSha256Field)]
    public async Task UnconfirmedCaptureStartReset_RefusesBeforeAnyWrite(bool recapture, string mismatchedField)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            mismatchedField == SeedSha256Field ? OtherSeedSha256 : CaptureSeedSha256,
            mismatchedField == ReferenceDateField ? CaptureOtherReferenceDate : ReferenceDate,
            failEmptyReset: false,
            TestCancellation);
        var options = CaptureOptions(standIn);
        if (recapture)
        {
            WriteManifest(options, CaptureSeedSha256, ReferenceDate);
        }

        var before = Snapshot();

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Equal(before, Snapshot());
        Assert.Equal(recapture, File.Exists(Full(ManifestFile)));
        Assert.Single(standIn.Requests, request => request == SeedResetRequest);
        Assert.All(standIn.Requests, request => Assert.Contains(request, AdminRequests));
        Assert.EndsWith($"{ResetPoints.CaptureStart} reset not confirmed; {NotTouched}", LastLine());
    }

    [Fact]
    public async Task UnconfirmedEmptyStateBracket_StopsTheCaptureAfterTheSeedRestoreWithNoManifest()
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: true, TestCancellation);
        var options = CaptureOptions(standIn);
        WriteManifest(options, CaptureSeedSha256, ReferenceDate);
        var emptyStateCase = CaseMatrix.Load(options.CaseMatrixPath, OperationCatalogue.Default)
            .Cases.Single(static definition => definition.IsEmptyState).Id;

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Equal(
            [SeedResetRequest, EmptyResetRequest, SeedResetRequest],
            standIn.Requests.Where(static request => request.StartsWith("POST /admin/reset", StringComparison.Ordinal)));
        Assert.False(File.Exists(Full(ManifestFile)));
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.False(File.Exists(Full(ConformanceFile)));
        Assert.False(File.Exists(Full(ContractsWsdlFile)));
        Assert.EndsWith(
            $"reset bracket of {emptyStateCase} not confirmed; capture stopped, baseline/manifest.json not written",
            LastLine());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCapture_PublishesTheConfirmedSeedFactsInTheManifestLast(bool recapture)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, TestCancellation);
        var options = CaptureOptions(standIn);
        if (recapture)
        {
            WriteManifest(options, CaptureSeedSha256, ReferenceDate);
        }

        Assert.Equal(0, await CaptureAsync(options));

        Assert.True(new BaselineStore(options).TryReadManifest(out var manifest));
        Assert.Equal(CaptureSeedSha256, manifest.SeedSha256);
        Assert.Equal(ReferenceDate, manifest.ReferenceDate);
        Assert.Equal(new StandInInfo(CaptureSeedSha256, ReferenceDate, 1), manifest.StandIn);
        Assert.Equal(new ManifestCounts(22, 56, 22, 56), manifest.Counts);
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.False(File.Exists(Full(SentinelRecord)));
        Assert.Contains("[jade-standin] baseline/manifest.json withdrawn until this capture completes", Lines());

        var published = File.GetLastWriteTimeUtc(Full(ManifestFile));
        Assert.All(
            Directory.EnumerateFiles(Full("baseline"), "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Full("contracts"), "*", SearchOption.AllDirectories))
                .Where(file => file != Full(ManifestFile)),
            file => Assert.True(File.GetLastWriteTimeUtc(file) <= published, Relative(file)));
    }

    // The message is fixed: a path would differ between checkouts and runs.
    private static string FindCaptureRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "harness", "config")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(RepoRootNotFound);
    }

    // A manifest as an earlier capture wrote it, with the given seed facts.
    private static void WriteManifest(HarnessOptions options, string seedSha256, string referenceDate) =>
        new BaselineStore(options).WriteManifest(new BaselineManifest
        {
            StandIn = new StandInInfo(seedSha256, referenceDate, 2),
            SeedSha256 = seedSha256,
            ReferenceDate = referenceDate,
            Counts = new ManifestCounts(22, 56, 22, 56),
            NotCaptured = [],
            WsdlCaptured = true,
            StartingCommit = options.StartingCommit,
            CaptureCommit = "0123456789abcdef0123456789abcdef01234567",
            CaptureExecution = CaptureExecutionValues.LocalProcesses,
        });

    private HarnessOptions CaptureOptions(CaptureStandInStub standIn) => new()
    {
        RepoRoot = CaptureRoot,
        ExecutionMode = CaptureExecutionValues.LocalProcesses,
        SeedDirectory = Full("Erewhon/DataFiles"),
        ExpectedReferenceDate = ReferenceDate,
        ReadinessTimeoutSeconds = LimitSeconds,
        RequestTimeoutSeconds = LimitSeconds,
        Backend = new HarnessEndpoints
        {
            RestBaseUrl = standIn.BaseUrl,
            SoapBaseUrl = standIn.BaseUrl,
            AdminUrl = standIn.BaseUrl,
        },
    };

    private Task<int> CaptureAsync(HarnessOptions options) =>
        new CaptureMode(options, OperationCatalogue.Default, _captureConsole).RunAsync(TestCancellation);

    private string[] Lines() =>
        _captureConsole.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string LastLine() => Lines()[^1];

    private string Full(string relative) => Path.Combine(CaptureRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Relative(string path) => Path.GetRelativePath(CaptureRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    // Every regular file below the temporary root, as "relative/path=hex" in ordinal order.
    private List<string> Snapshot() =>
    [
        .. Directory.EnumerateFiles(CaptureRoot, "*", SearchOption.AllDirectories)
            .Select(file => Relative(file) + "=" + Convert.ToHexStringLower(File.ReadAllBytes(file)))
            .Order(StringComparer.Ordinal),
    ];
}


// ---- Lifecycle console sink ---------------------------------------------------------------------
// The lifecycle clients' console sink: every printed line is escaped so that an admin value or a
// setting can neither break it into a forged [jade-standin] line nor drive the terminal, while failure
// items keep the exact values; and a writer that throws never replaces the readiness item, reset result
// or shutdown outcome already computed. Admin requests go to a reserved .invalid host that only the
// scripted handler answers, or to a loopback listener the test owns.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and the replay's lifecycle clients print through this sink. The admin requests,
// PreviousGeneration and Json come from the state-reset members.
public sealed partial class ReplayLifecycleTests
{
    private const string ReferenceDate = "2026-01-01";

    private static readonly string SeedSha256 = new('a', 64);

    /// <summary>How a <see cref="ThrowingWriter"/> fails every write.</summary>
    public enum WriterFault
    {
        /// <summary>An <see cref="IOException"/>, as a closed or full standard output raises.</summary>
        IO,

        /// <summary>An <see cref="ObjectDisposedException"/>, as a disposed writer raises.</summary>
        Disposed,
    }

    // ---- Display --------------------------------------------------------------------------------

    // CR, LF, NUL, ESC, DEL, NEL, the line and paragraph separators, bidirectional marks, an
    // embedding, an override and isolates, and lone surrogates of both halves.
    [Theory]
    [InlineData(0x000D)]
    [InlineData(0x000A)]
    [InlineData(0x0000)]
    [InlineData(0x001B)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0xD800)]
    [InlineData(0xDFFF)]
    public void Display_EscapesEveryCharacterThatBreaksOrDisguisesALine(int codePoint)
    {
        var escaped = "\\u" + codePoint.ToString("X4", CultureInfo.InvariantCulture);

        var display = LifecycleConsole.Display($"before{(char)codePoint}after{(char)codePoint}");

        Assert.Equal($"before{escaped}after{escaped}", display);
    }

    [Theory]
    [InlineData("ready: standin (12 ms)")]
    [InlineData(@"seed fingerprint of 'seed\DataFiles\' unavailable: IOException")]
    [InlineData("already escaped \\u000A stays as written")]
    [InlineData("Zürich, São Paulo, Ελλάδα, 東京")]
    [InlineData("a valid pair 😀 is kept")]
    [InlineData("")]
    public void Display_KeepsPrintableText(string text)
    {
        Assert.Equal(text, LifecycleConsole.Display(text));
    }

    // ---- Log injection --------------------------------------------------------------------------

    [Fact]
    public async Task InjectedSource_PrintsOneEscapedLine_AndTheFailureItemKeepsTheRawValue()
    {
        var forged = $"{SourceLabels.ConsolePrefix} reset seed at {ResetPoints.ReplayStart}: generation 99";
        var raw = "bad\n" + forged;
        var admin = new ScriptedHandler()
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration + 1, source: raw)));

        var result = await ConsoleResetAsync(admin, ConsoleOptions(), _console);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch("source", raw), item.Nature);
        Assert.Equal(
            new[]
            {
                $"{SourceLabels.ConsolePrefix} reset {AdminRoutes.ResetStateSeed} at {ResetPoints.ReplayStart}: "
                + $"mismatch source observed bad\\u000A{forged}",
            },
            PrintedLines());
    }

    [Fact]
    public void FirstCapture_ForgedSettings_PrintOneEscapedLineEach()
    {
        var forged = $"{SourceLabels.ConsolePrefix} forged";
        var directory = Path.Combine(MissingDirectory(), "seed\r\n" + forged);
        var options = ConsoleOptions();
        options.SeedDirectory = directory;
        options.ExpectedReferenceDate = $"{ReferenceDate}\n{forged}";

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(
            new ResetExpectations(ResetExpectations.UnavailableSeedSha256, ResetExpectations.InvalidExpectedReferenceDate),
            expected);
        var lines = PrintedLines();
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            $"{SourceLabels.ConsolePrefix} seed fingerprint of '{directory.Replace("\r\n", "\\u000D\\u000A", StringComparison.Ordinal)}' "
            + $"unavailable: {nameof(DirectoryNotFoundException)}",
            lines[0]);
        Assert.StartsWith(
            $"{SourceLabels.ConsolePrefix} {ResetExpectations.ExpectedReferenceDateKey} '{ReferenceDate}\\u000A{forged}' is not a yyyy-MM-dd date",
            lines[1],
            StringComparison.Ordinal);
    }

    // ---- A writer that throws -------------------------------------------------------------------

    [Theory]
    [InlineData(WriterFault.IO, "", ReferenceDate)]
    [InlineData(WriterFault.Disposed, "", ReferenceDate)]
    [InlineData(WriterFault.IO, "not-a-date", ResetExpectations.InvalidExpectedReferenceDate)]
    [InlineData(WriterFault.Disposed, "not-a-date", ResetExpectations.InvalidExpectedReferenceDate)]
    public void FirstCapture_MissingSeedDirectory_ThrowingWriter_StillReturnsTheExpectations(
        WriterFault fault,
        string configuredDate,
        string expectedDate)
    {
        var options = ConsoleOptions();
        options.SeedDirectory = MissingDirectory();
        options.ExpectedReferenceDate = configuredDate;
        var writer = new ThrowingWriter(fault);

        var expected = ResetExpectations.FirstCapture(options, writer);

        Assert.Equal(new ResetExpectations(ResetExpectations.UnavailableSeedSha256, expectedDate), expected);
        Assert.Equal(2, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task ResetMismatch_ThrowingWriter_StillReturnsEveryFailure(WriterFault fault)
    {
        var admin = new ScriptedHandler()
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration + 1, source: "other", mutations: 3)));
        var writer = new ThrowingWriter(fault);

        var result = await ConsoleResetAsync(admin, ConsoleOptions(), writer);

        var itemId = FailureItemIds.Reset(ResetPoints.ReplayStart);
        (string ItemId, string Nature)[] expected =
        [
            (itemId, FailureItemIds.Mismatch("mutations", "3")),
            (itemId, FailureItemIds.Mismatch("source", "other")),
        ];
        Assert.Equal(expected, result.Failures.Select(failure => (failure.ItemId, failure.Nature)));
        Assert.Equal(PreviousGeneration + 1, result.After?.Generation);
        Assert.Equal(2, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task ResetConfirmed_ThrowingWriter_StillSucceeds(WriterFault fault)
    {
        var admin = new ScriptedHandler()
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(ConsoleInfo(PreviousGeneration + 1)));
        var writer = new ThrowingWriter(fault);

        var result = await ConsoleResetAsync(admin, ConsoleOptions(), writer);

        Assert.True(result.Succeeded);
        Assert.Equal(PreviousGeneration + 1, result.After?.Generation);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, admin.Requests);
        Assert.Equal(1, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task ResetSkipped_ThrowingWriter_StillReturnsSkipped(WriterFault fault)
    {
        var options = ConsoleOptions();
        options.Backend.AdminUrl = string.Empty;
        var admin = new ScriptedHandler();
        var writer = new ThrowingWriter(fault);

        var result = await ConsoleResetAsync(admin, options, writer);

        Assert.Same(StateResetResult.Skipped, result);
        Assert.Empty(admin.Requests);
        Assert.Equal(1, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task Readiness_ThrowingWriter_StillReportsReady(WriterFault fault)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var answering = AnswerHealthOnceAsync(listener, ct);
        var writer = new ThrowingWriter(fault);
        using var readiness = new Readiness(
            new HarnessOptions { ReadinessTimeoutSeconds = 10, ReadinessPollIntervalSeconds = 1 },
            writer);

        var item = await readiness.WaitAsync(
            ShutdownTarget.StandInComponent,
            string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"),
            ct);

        Assert.Null(item);
        Assert.Equal(1, writer.Attempts);
        Assert.StartsWith($"GET {AdminRoutes.Health} HTTP/1.1", await answering, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task Readiness_ThrowingWriter_StillReturnsTheNotReadyItem(WriterFault fault)
    {
        var writer = new ThrowingWriter(fault);
        using var readiness = new Readiness(new HarnessOptions(), writer);

        var item = await readiness.WaitAsync(
            ShutdownTarget.StandInComponent,
            "ftp://standin.invalid:9090",
            TestContext.Current.CancellationToken);

        Assert.NotNull(item);
        Assert.Equal(FailureItemIds.Ready(ShutdownTarget.StandInComponent), item.ItemId);
        Assert.Equal(FailureItemIds.Timeout, item.Nature);
        Assert.Equal(1, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task Shutdown_ThrowingWriter_StillReturnsTheStoppedOutcome(WriterFault fault)
    {
        using var admin = new ScriptedHandler()
            .Then("POST " + AdminRoutes.Shutdown, () => new HttpResponseMessage(HttpStatusCode.Accepted))
            .Then("GET " + AdminRoutes.Health, () => throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                "Connection refused (standin.invalid:9090)",
                new SocketException((int)SocketError.ConnectionRefused)));
        var writer = new ThrowingWriter(fault);
        using var shutdown = new HttpComponentShutdown(
            new HarnessOptions { ShutdownTimeoutSeconds = 5, ShutdownRequestTimeoutSeconds = 2 },
            admin,
            writer);

        var outcome = await shutdown.ShutdownAsync(
            new ShutdownTarget(ShutdownTarget.StandInComponent, new Uri(StandInAdminUrl)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ShutdownTarget.StandInComponent, outcome.Component);
        Assert.True(outcome.Stopped, outcome.Detail);
        Assert.Null(outcome.ToFailureItem());
        Assert.Equal(new[] { "POST " + AdminRoutes.Shutdown, "GET " + AdminRoutes.Health }, admin.Requests);
        Assert.Equal(1, writer.Attempts);
    }

    [Theory]
    [InlineData(WriterFault.IO)]
    [InlineData(WriterFault.Disposed)]
    public async Task Shutdown_ThrowingWriter_StillReturnsTheNotStoppedOutcome(WriterFault fault)
    {
        using var admin = new ScriptedHandler();
        var writer = new ThrowingWriter(fault);
        using var shutdown = new HttpComponentShutdown(new HarnessOptions(), admin, writer);

        var outcome = await shutdown.ShutdownAsync(null!, TestContext.Current.CancellationToken);

        Assert.False(outcome.Stopped);
        Assert.Equal(FailureItemIds.StillRunning, outcome.ToFailureItem()?.Nature);
        Assert.Empty(admin.Requests);
        Assert.Equal(1, writer.Attempts);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static HarnessOptions ConsoleOptions()
    {
        var options = new HarnessOptions { RequestTimeoutSeconds = 5 };
        options.Backend.AdminUrl = StandInAdminUrl;
        return options;
    }

    // A path under the test's temporary directory that is never created.
    private static string MissingDirectory() =>
        Path.Combine(Path.GetTempPath(), $"erewhon-lifecycle-console-{Guid.NewGuid():N}", "missing");

    private static async Task<StateResetResult> ConsoleResetAsync(
        ScriptedHandler admin,
        HarnessOptions options,
        TextWriter output)
    {
        using var reset = new StateReset(options, admin, output);
        return await reset.ResetAsync(
            ResetPoints.ReplayStart,
            AdminRoutes.ResetStateSeed,
            ResetExpectations.None,
            TestContext.Current.CancellationToken);
    }

    private static JsonObject ConsoleInfo(long generation, string source = SourceLabels.StandIn, long mutations = 0) => new()
    {
        ["source"] = source,
        ["seedSha256"] = SeedSha256,
        ["referenceDate"] = ReferenceDate,
        ["generation"] = generation,
        ["mutations"] = mutations,
    };

    // Accepts one connection, reads its request head and answers 200; returns the request head.
    private static async Task<string> AnswerHealthOnceAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
        return head.ToString();
    }

    // The console text as lines, without the terminator of the last one.
    private List<string> PrintedLines()
    {
        var text = _console.ToString();
        Assert.EndsWith(Environment.NewLine, text, StringComparison.Ordinal);
        return [.. text[..^Environment.NewLine.Length].Split(Environment.NewLine)];
    }

    /// <summary>A console writer whose every write fails, counting the attempts.</summary>
    private sealed class ThrowingWriter(WriterFault fault) : TextWriter
    {
        public int Attempts { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => throw Fault();

        public override void Write(string? value) => throw Fault();

        public override void WriteLine(string? value) => throw Fault();

        private Exception Fault()
        {
            Attempts++;
            return fault == WriterFault.Disposed
                ? new ObjectDisposedException(nameof(ThrowingWriter))
                : new IOException("No space left on device");
        }
    }

    /// <summary>
    /// Answers each admin request with the next scripted response, after checking it is the scripted
    /// method and path; an unscripted request fails the test instead of reaching the network.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(string Request, Func<HttpResponseMessage> Respond)> _script = new();

        /// <summary>Every request received, as <c>&lt;method&gt; &lt;path and query&gt;</c>.</summary>
        public List<string> Requests { get; } = [];

        public ScriptedHandler Then(string request, Func<HttpResponseMessage> respond)
        {
            _script.Enqueue((request, respond));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var line = $"{request.Method} {request.RequestUri?.PathAndQuery}";
            Requests.Add(line);
            if (!_script.TryDequeue(out var step) || !string.Equals(step.Request, line, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unscripted admin request: {line}");
            }

            return Task.FromResult(step.Respond());
        }
    }
}

// ---- Support types of the capture mode tests ----------------------------------------------------
// The capture stand-in stub serves the capture mode tests of ReplayLifecycleTests; it is an internal
// support type, not a test class.

/// <summary>
/// An in-process stand-in on 127.0.0.1 and an ephemeral port, as far as capture reaches it: <c>GET /health</c>
/// answers 200; <c>GET /admin/info</c> reports the source label, the given seed hash and reference date, the
/// reset generation and no mutations; <c>POST /admin/reset</c> increases the generation and answers 200, except
/// an empty-state reset when it is told to fail, which answers 500 and changes nothing; and every other request,
/// each case and the WSDL fetch, answers 200 with the JSON body <c>{}</c>. Every request is recorded as
/// <c>METHOD raw-target</c> before it is answered.
/// </summary>
internal sealed class CaptureStandInStub : IAsyncDisposable
{
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string ResetPrefix = "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=";

    // Bounds the graceful stop; idle keep-alive connections close at once, so it is never reached.
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _seedSha256;
    private readonly string _referenceDate;
    private readonly bool _failEmptyReset;
    private WebApplication? _app;
    private long _generation;

    private CaptureStandInStub(string seedSha256, string referenceDate, bool failEmptyReset)
    {
        _seedSha256 = seedSha256;
        _referenceDate = referenceDate;
        _failEmptyReset = failEmptyReset;
    }

    /// <summary>The stub's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>, for the REST, SOAP and admin URLs alike.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>Every request received so far, as <c>METHOD raw-target</c>, in arrival order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Starts a stub reporting <paramref name="seedSha256"/> and <paramref name="referenceDate"/>.</summary>
    /// <param name="seedSha256">The seed hash <c>/admin/info</c> reports.</param>
    /// <param name="referenceDate">The reference date <c>/admin/info</c> reports.</param>
    /// <param name="failEmptyReset">Whether an empty-state reset answers 500.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running stub, which the caller disposes.</returns>
    public static async Task<CaptureStandInStub> StartAsync(
        string seedSha256, string referenceDate, bool failEmptyReset, CancellationToken ct)
    {
        var stub = new CaptureStandInStub(seedSha256, referenceDate, failEmptyReset);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownLimit);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        app.Run(stub.HandleAsync);
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
            stub.BaseUrl = new Uri(address).GetLeftPart(UriPartial.Authority);
            stub._app = app;
            return stub;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops the server and releases its port.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        var request = $"{context.Request.Method} {target}";
        _requests.Enqueue(request);

        var (status, body) = Answer(request);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private (int Status, string Body) Answer(string request)
    {
        if (request == "GET " + AdminRoutes.Health)
        {
            return (StatusCodes.Status200OK, "{}");
        }

        if (request == "GET " + AdminRoutes.Info)
        {
            var info = new JsonObject
            {
                ["source"] = SourceLabels.StandIn,
                ["seedSha256"] = _seedSha256,
                ["referenceDate"] = _referenceDate,
                ["generation"] = Interlocked.Read(ref _generation),
                ["mutations"] = 0,
            };
            return (StatusCodes.Status200OK, info.ToJsonString());
        }

        if (request.StartsWith(ResetPrefix, StringComparison.Ordinal))
        {
            if (_failEmptyReset && request[ResetPrefix.Length..] == AdminRoutes.ResetStateEmpty)
            {
                return (StatusCodes.Status500InternalServerError, "{}");
            }

            Interlocked.Increment(ref _generation);
            return (StatusCodes.Status200OK, "{}");
        }

        return (StatusCodes.Status200OK, "{}");
    }
}
