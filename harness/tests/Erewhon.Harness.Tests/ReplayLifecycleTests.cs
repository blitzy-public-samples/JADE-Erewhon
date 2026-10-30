using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
using Erewhon.Harness.Compare;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Modes;
using Erewhon.Harness.Report;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The replay exit path (AAP 0.8.4) and exit code (AAP 0.7.7), driven in-process through the seams of
/// <see cref="ReplayMode"/>: a fake body, report writers that render with the real
/// <see cref="ResultsReportWriter"/> and record, fail or block their writes without touching the disk, one
/// that fails every render instead, the real writer anchored at a temporary repository root, and a shutdown
/// client that records its calls.
/// Whatever the body, the report write or a shutdown does, every component is shut down, the final line is
/// the last line printed and the exit code is 0 or 1. The admin URLs name reserved <c>.invalid</c> hosts
/// that nothing contacts, because every request goes through the fakes. The conformance-evidence
/// and baseline-evidence tests run the real <see cref="ReplayBody"/> instead, against a temporary
/// repository root and a closed loopback port, so every network step fails at once. The test of a blank
/// facade admin URL runs it too, and contacts nothing: its backend is external and its facade listeners
/// are a loopback socket that is only checked for connections. The kept-evidence test runs it through the
/// exit path against a loopback stub as its facade, and refuses only the case that takes the run's kept
/// discrepancy evidence over its limit. It also
/// covers the lifecycle clients behind that path: shutdown confirmation against scripted and loopback
/// admin replies, state-reset confirmation against scripted admin responses, and the lifecycle console
/// sink, which escapes every printed line and never lets a writer that throws replace a computed result;
/// the capture mode against a loopback stand-in stub, which refuses or stops with exit code 1 whenever
/// the seed facts cannot be confirmed, and otherwise publishes them in the manifest it writes last;
/// the modes' console diagnostics, which classify an unreadable SOAP <c>statusCode</c> without quoting the
/// response and keep each <c>export-openapi</c>, <c>summary</c> and entry-point line one inert line;
/// the empty-state reset bracket of replay and capture, whose seed restore still runs and is confirmed
/// when the run is cancelled inside it, and is abandoned at the cancelled-run limit when it is itself held;
/// and a cancelled replay's shutdowns, each cut to the cancelled-run limit when the configured limit is
/// longer. It also covers the start-up and exit of the harness process
/// itself: the entry point, which refuses an argument that is not a setting before any mode runs and loads
/// its settings strictly or leniently, so a replay whose settings are partly unusable still writes its
/// report to the configured path; the cancellation Main hands SIGTERM and SIGINT to, whose completion
/// passes the mode's exit code through or gives exit code 1 within its bound; and the supervisors that
/// send SIGTERM, which wait longer than the cancelled exit budget and that bound before they kill it.
/// It also checks that <see cref="LoopbackHost"/>, which builds every in-process web host of these tests, listens
/// on 127.0.0.1 only, whatever listener settings the process environment holds.
/// </summary>
/// <remarks>
/// No failure message shows a value that varies between runs: a temporary path, the run date, a measured
/// time, a port or a marker. Text that holds one is checked with fixed-message predicates, or compared after
/// both sides are redacted.
/// </remarks>
public sealed partial class ReplayLifecycleTests : IDisposable
{
    private const string FacadeAdminUrl = "http://facade.invalid:9090";
    private const string StandInAdminUrl = "http://standin.invalid:9090";
    private const string RepoRootToken = "<repo-root>";
    private const string ReportTitle = "# Erewhon strangler facade — results";
    private const string CaseMatrixPath = "harness/config/case-matrix.json";

    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(30);

    // The report-write limit of the stalled-write test, and the time within which that run must end: the limit
    // plus cleanup, far inside the run limit.
    private static readonly TimeSpan ShortReportWriteLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AbandonedWriteRunBound = TimeSpan.FromSeconds(10);

    // The report-write limit of the refused-destination tests: below the run limit, so a write that blocked
    // instead of being refused would be abandoned and fail the refusal assertion rather than hang the run.
    private static readonly TimeSpan RefusalReportWriteLimit = TimeSpan.FromSeconds(10);

    // The cancelled-run shutdown limit of the cancelled-shutdown tests, and a configured shutdown limit longer
    // than both it and the run limit, so only the cancelled-run limit can end a shutdown that confirms nothing.
    private const int ShortCancelledShutdownLimitSeconds = 1;
    private const int LongShutdownLimitSeconds = 60;
    private static readonly TimeSpan ShortCancelledShutdownLimit = TimeSpan.FromSeconds(ShortCancelledShutdownLimitSeconds);

    // How long the facade's shutdown has run when a test cancels the run during it.
    private static readonly TimeSpan ShutdownBeforeCancellation = TimeSpan.FromMilliseconds(500);

    private static readonly Regex FinalLinePattern = new(
        @"^\d+ of 22 operations equivalent, \d+ documented differences$",
        RegexOptions.CultureInvariant);

    private static readonly string[] BothComponents = [ShutdownTarget.FacadeComponent, ShutdownTarget.StandInComponent];

    // The only exit codes the harness may return (AAP 0.7.7).
    private static readonly int[] ExitCodes = [0, 1];

    // Created on first use, so only the exit-path and entry-point tests touch the file system; removed in Dispose.
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

    /// <summary>A report destination the real, rooted writer must refuse.</summary>
    public enum RefusedReportPath
    {
        /// <summary>A directory below the repository root, as <c>harness/</c> is in the exit-code verification.</summary>
        DirectoryBelowRoot,

        /// <summary>A FIFO below the repository root that nothing reads, at the report's own name.</summary>
        UnreadFifoBelowRoot,

        /// <summary>A file in a new directory under a temporary directory beside the repository root.</summary>
        OutsideRoot,
    }

    /// <summary>How the facade's <c>/admin/info</c> read for the run facts ends.</summary>
    public enum FacadeInfoAnswer
    {
        /// <summary>HTTP 200 with the facade's framework description.</summary>
        Described,

        /// <summary>HTTP 200 with a JSON object that has no <c>frameworkDescription</c>.</summary>
        Fieldless,

        /// <summary>HTTP 200 with a body that is not JSON.</summary>
        NotJson,

        /// <summary>HTTP 200 with a whitespace-only <c>frameworkDescription</c>.</summary>
        BlankDescription,

        /// <summary>HTTP 500.</summary>
        ServerError,

        /// <summary>The connection is refused.</summary>
        ConnectionRefused,

        /// <summary>The response ends prematurely.</summary>
        ConnectionReset,

        /// <summary>The response body never arrives, so the request limit ends the read.</summary>
        NoResponse,

        /// <summary><c>Harness:Target:AdminUrl</c> is blank, so no request is sent.</summary>
        BlankUrl,

        /// <summary><c>Harness:Target:AdminUrl</c> carries user information, so no request is sent.</summary>
        CredentialUrl,

        /// <summary>The run never reached the read, as when an earlier step ended the body.</summary>
        NotRead,
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stdout.Dispose();
        _console.Dispose();
        _captureConsole.Dispose();
        _repoRoot?.Dispose();
        _cancellationSource?.Dispose();
        _bracketRoot?.Dispose();
        if (_confinedTemp is not null)
        {
            Directory.Delete(_confinedTemp, recursive: true);
        }
        if (_seedDirectory is not null)
        {
            Directory.Delete(_seedDirectory, recursive: true);
        }

        // The capture root's path is chosen before its first directory is created, so it may not exist.
        if (_captureRoot is not null && Directory.Exists(_captureRoot))
        {
            Directory.Delete(_captureRoot, recursive: true);
        }

        // The mode diagnostics root's path is recorded before its directory is created, so it may not exist.
        if (_modeDiagnosticsRoot is not null && Directory.Exists(_modeDiagnosticsRoot))
        {
            Directory.Delete(_modeDiagnosticsRoot, recursive: true);
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
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), Redacted(LastNonEmptyLine(printed)));

        Assert.Equal(BothComponents, shutdown.Components);
        Assert.Equal([new Uri(FacadeAdminUrl), new Uri(StandInAdminUrl)], shutdown.Calls.Select(call => call.AdminBaseUrl));
        Assert.DoesNotContain(mode.State.Failures, IsShutdownItem);

        // The harness's last line is the final line of the report it printed.
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), Redacted(LastNonEmptyLine(StdoutText)));
    }

    // A report that cannot be built or rendered has no text to write or print: the mode records CHK-REPORT,
    // prints the plain failure list instead, still shuts every component down, and exits 1 with the final line
    // of a run without a report last. When cleanup adds an item, the rewrite renders again, fails the same way
    // and prints the list again, now with that item.
    [Theory]
    [InlineData(null)]
    [InlineData(ShutdownFailure.ReturnsNotStopped)]
    public async Task ReportRenderFailure_RecordsItem_PrintsFailureList_ShutsDownAll_Exits1(ShutdownFailure? firstShutdown)
    {
        const string renderFailurePrefix = "report could not be built or rendered: ";
        var options = Options();
        var writer = new ThrowingRenderReportWriter();
        var shutdown = new RecordingShutdown(firstShutdown);
        var mode = NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty, options);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        var cleanupFailed = firstShutdown is not null;
        var reportItem = new FailureItem(FailureItemIds.Report, FailureItemIds.WriteFailed);
        var shutdownItem = new FailureItem(FailureItemIds.Shutdown(ShutdownTarget.FacadeComponent), FailureItemIds.StillRunning);
        FailureItem[] expectedItems = cleanupFailed ? [reportItem, shutdownItem] : [reportItem];
        Assert.Equal(
            expectedItems.Select(item => (item.ItemId, item.Nature)),
            mode.State.Failures.Select(item => (item.ItemId, item.Nature)));

        // A repeated render failure adds no second CHK-REPORT item, so the one recorded names the first.
        Assert.Equal(renderFailurePrefix + ReplayMode.Describe(ThrowingRenderReportWriter.RenderFailure()), mode.State.Failures[0].Detail);

        // The first render precedes every item; the rewrite renders the report holding both items once cleanup
        // added one. No render produced text, so nothing was written and no file was created.
        Assert.Equal(cleanupFailed ? 2 : 1, writer.Rendered.Count);
        Assert.Empty(writer.Rendered[0].Failures);
        if (cleanupFailed)
        {
            Assert.Equal(
                expectedItems.Select(item => (item.ItemId, item.Nature)),
                writer.Rendered[^1].UndocumentedFailures.Select(item => (item.ItemId, item.Nature)));
        }

        Assert.Equal(0, writer.WriteAttempts);
        Assert.False(File.Exists(options.EffectiveReportPath), "a report that could not be rendered creates no report file");
        Assert.Empty(SpecialFileSupport.Names(RepoRootDirectory.Root));

        Assert.Equal(BothComponents, shutdown.Components);
        Assert.Equal([new Uri(FacadeAdminUrl), new Uri(StandInAdminUrl)], shutdown.Calls.Select(call => call.AdminBaseUrl));

        // Each failed render prints the items recorded so far in place of the report text; the conclusion and the
        // final line follow, and nothing else is printed.
        var text = Redacted(StdoutText);
        Assert.False(text.Contains(ReportTitle, StringComparison.Ordinal), "no report text is printed when the report could not be rendered");
        static string Listed(FailureItem item) => "replay:   " + FailureItemDisplay.Describe(item);
        string[] failureLists = cleanupFailed
            ? [Listed(reportItem), "replay: cleanup added failing items; rewriting the report", Listed(reportItem), Listed(shutdownItem)]
            : [Listed(reportItem)];
        string[] expectedLines =
        [
            .. failureLists,
            string.Create(CultureInfo.InvariantCulture, $"replay: {expectedItems.Length} undocumented failing item(s); report not written; exit 1"),
            .. expectedItems.Select(Listed),
            "replay: 0 documented failing item(s)",
            ResultsReport.FormatFinalLine(0, 0),
        ];
        Assert.Equal(
            expectedLines,
            text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0));
    }

    // A write that never ends, as one to a stalled destination does, is abandoned at the report-write limit and
    // handled exactly like a write that throws, so cleanup, the final line and the exit code still follow.
    [Fact]
    public async Task ReportWriteThatNeverEnds_IsAbandonedAtTheLimit_PrintsReport_ShutsDownAll_Exits1()
    {
        using var writer = new BlockingReportWriter();
        var shutdown = new RecordingShutdown();
        Assert.Equal(TimeSpan.FromSeconds(30), NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty).ReportWriteLimit);
        var mode = new ReplayMode(Options(), FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty, _stdout)
        {
            ReportWriteLimit = ShortReportWriteLimit,
        };

        var elapsed = Stopwatch.StartNew();
        var exitCode = await RunAsync(mode);
        elapsed.Stop();

        AssertExitCode(1, exitCode);
        Assert.Equal(1, writer.WriteAttempts);
        var item = Assert.Single(mode.State.Failures);
        Assert.Equal((FailureItemIds.Report, FailureItemIds.WriteFailed), (item.ItemId, item.Nature));
        Assert.True(
            item.Detail is not null && item.Detail.Contains("did not finish within 1 s and was abandoned", StringComparison.Ordinal),
            "the CHK-REPORT item names the abandoned write");
        Assert.True(
            StdoutText.Contains("report could not be written (System.TimeoutException: ", StringComparison.Ordinal),
            "the console states that the write was abandoned");

        var printed = writer.Rendered[^1];
        Assert.True(StdoutText.Contains(printed, StringComparison.Ordinal), "stdout holds the full report text");
        Assert.True(printed.Contains(FailureItemIds.Report, StringComparison.Ordinal), "the printed report lists the CHK-REPORT item");

        Assert.Equal(BothComponents, shutdown.Components);
        Assert.True(elapsed.Elapsed < AbandonedWriteRunBound, "an abandoned report write holds the exit path no longer than its limit");
        Assert.Equal(ResultsReport.FormatFinalLine(0, 0), Redacted(LastNonEmptyLine(StdoutText)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(int.MaxValue + 1L)]
    public void ReportWriteLimit_NotPositiveOrAboveIntMaxMilliseconds_IsRefused(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayMode(
            Options(), FakeBody.NoOp(), new RecordingReportWriter(), new RecordingShutdown(), KnownLimitationsRegister.Empty, _stdout)
        {
            ReportWriteLimit = TimeSpan.FromMilliseconds(milliseconds),
        });
    }

    // With the real writer anchored at the repository root, a destination it must not write is refused at once:
    // CHK-REPORT, the report text on the console, nothing created at the destination or outside the root, every
    // component shut down and exit 1. An unread FIFO would block a write that opened it.
    [Theory]
    [InlineData(RefusedReportPath.DirectoryBelowRoot, "is a directory")]
    [InlineData(RefusedReportPath.UnreadFifoBelowRoot, "is not a regular file")]
    [InlineData(RefusedReportPath.OutsideRoot, "is outside the repository root")]
    public async Task RootedWriter_RefusedReportPath_RecordsItem_PrintsReport_CreatesNothing_ShutsDownAll_Exits1(
        RefusedReportPath destination, string refusal)
    {
        using var outside = new TempDirectory();
        var reportPath = destination switch
        {
            RefusedReportPath.DirectoryBelowRoot => Directory.CreateDirectory(RepoRootDirectory.PathOf("harness")).FullName,
            RefusedReportPath.UnreadFifoBelowRoot => RepoRootDirectory.PathOf("RESULTS.md"),
            _ => Path.Combine(outside.Root, "reports", "RESULTS.md"),
        };
        if (destination == RefusedReportPath.UnreadFifoBelowRoot)
        {
            SpecialFileSupport.CreateFifo(reportPath);
        }

        var options = Options();
        options.ReportPath = reportPath;
        var shutdown = new RecordingShutdown();
        var mode = new ReplayMode(
            options, FakeBody.NoOp(), new ResultsReportWriter(RepoRootDirectory.Root), shutdown, KnownLimitationsRegister.Empty, _stdout)
        {
            ReportWriteLimit = RefusalReportWriteLimit,
        };

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        var item = Assert.Single(mode.State.Failures);
        Assert.Equal((FailureItemIds.Report, FailureItemIds.WriteFailed), (item.ItemId, item.Nature));
        Assert.True(
            item.Detail is not null && item.Detail.Contains(refusal, StringComparison.Ordinal),
            "the CHK-REPORT item names why the destination was refused");

        // The printed report, from its title to its final line, is the one rebuilt with the CHK-REPORT item.
        var text = StdoutText;
        var start = text.IndexOf(ReportTitle, StringComparison.Ordinal);
        Assert.True(start >= 0, "stdout holds the report text");
        var end = text.IndexOf(ResultsReport.FormatFinalLine(0, 0), start, StringComparison.Ordinal);
        Assert.True(end > start, "the printed report ends with its final line");
        Assert.True(text[start..end].Contains(FailureItemIds.Report, StringComparison.Ordinal), "the printed report lists the CHK-REPORT item");

        // Nothing was written: the entry the report was pointed at is as it was, no temporary file is left
        // beside it, and nothing appeared outside the root.
        Assert.Equal(
            destination == RefusedReportPath.OutsideRoot ? [] : [Path.GetFileName(reportPath)],
            SpecialFileSupport.Names(RepoRootDirectory.Root));
        Assert.Empty(SpecialFileSupport.Names(outside.Root));
        if (destination == RefusedReportPath.DirectoryBelowRoot)
        {
            Assert.Empty(SpecialFileSupport.Names(reportPath));
        }
        else if (destination == RefusedReportPath.UnreadFifoBelowRoot)
        {
            Assert.True(SpecialFileSupport.IsFifo(reportPath), "a refused FIFO is left in place");
        }

        Assert.Equal(BothComponents, shutdown.Components);
        AssertFinalLineLast();
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

        // Cleanup never inherits the run's token, and a shutdown limit within the cancelled-run limit runs on a
        // token nothing cancels, so a cancelled run still shuts its components down.
        Assert.Equal(BothComponents, shutdown.Components);
        Assert.All(shutdown.Calls, call => Assert.False(call.TokenCancelled));

        Assert.Equal(1, writer.WriteCount);
        Assert.True(writer.Written[^1].Contains(FailureItemIds.Exception, StringComparison.Ordinal), "the written report lists the CHK-EXCEPTION item");
        AssertFinalLineLast();
    }

    // A cancelled run whose configured shutdown limit is longer than the cancelled-run limit, against a client
    // that confirms no stop until its token is cancelled. The run is cancelled before the body, as on SIGTERM
    // during the replay, or while the facade's shutdown waits. Each component is still shut down, and each call
    // ends at the cancelled-run limit counted from the later of the cancellation and its own start, so the
    // stand-in keeps a whole slice of its own. Both become CHK-SHUTDOWN (still-running), the report is rewritten
    // with them, the final line is the last line and the exit code is 1, all within the two slices.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRun_ShutdownBeyondTheCancelledLimit_EachComponentEndsAtTheLimit_ReportRewritten_Exits1(
        bool cancelledDuringShutdown)
    {
        var options = Options();
        options.ShutdownTimeoutSeconds = LongShutdownLimitSeconds;
        var writer = new RecordingReportWriter();
        var shutdown = new UnconfirmedShutdown();
        Assert.Equal(
            TimeSpan.FromSeconds(ReplayMode.CancelledShutdownLimitSeconds),
            NewMode(FakeBody.NoOp(), writer, shutdown, KnownLimitationsRegister.Empty).CancelledShutdownLimit);
        var body = cancelledDuringShutdown
            ? FakeBody.NoOp()
            : new FakeBody((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var mode = new ReplayMode(options, body, writer, shutdown, KnownLimitationsRegister.Empty, _stdout)
        {
            CancelledShutdownLimit = ShortCancelledShutdownLimit,
        };

        // The limit ends the wait, so a shutdown that the cancellation never reaches fails the test.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RunLimit);
        using var run = new CancellationTokenSource();
        var cancelledAt = Stopwatch.GetTimestamp();
        if (!cancelledDuringShutdown)
        {
            await run.CancelAsync();
        }

        var exiting = mode.RunAsync(run.Token);
        if (cancelledDuringShutdown)
        {
            await shutdown.FirstCallStarted.WaitAsync(limit.Token);
            await Task.Delay(ShutdownBeforeCancellation, limit.Token);
            cancelledAt = Stopwatch.GetTimestamp();
            await run.CancelAsync();
        }

        var exitCode = await exiting.WaitAsync(limit.Token);
        var sinceCancellation = Stopwatch.GetElapsedTime(cancelledAt);

        AssertExitCode(1, exitCode);
        var calls = shutdown.Calls;
        Assert.Equal(BothComponents, calls.Select(call => call.Component));
        foreach (var call in calls)
        {
            Assert.False(call.CancelledAtStart, "a shutdown's token was already cancelled when the shutdown started");
            Assert.True(call.CancelledAtEnd, "a shutdown ended before its token was cancelled");
            var slice = Stopwatch.GetElapsedTime(Math.Max(cancelledAt, call.Started), call.Ended);
            Assert.True(slice >= ShortCancelledShutdownLimit - TimerTolerance, "a shutdown was abandoned before the cancelled-run limit");
            Assert.True(slice < ShortCancelledShutdownLimit + Slack, "a shutdown outlasted the cancelled-run limit");
        }

        if (cancelledDuringShutdown)
        {
            Assert.True(
                Stopwatch.GetElapsedTime(calls[0].Started, calls[0].Ended) >= ShutdownBeforeCancellation + ShortCancelledShutdownLimit - TimerTolerance,
                "the facade's limit counted from its start rather than from the cancellation");
        }

        var abandoned = $"; abandoned at the cancelled run's {ShortCancelledShutdownLimitSeconds} s shutdown limit";
        var shutdownItems = mode.State.Failures.Where(IsShutdownItem).ToList();
        Assert.Equal(BothComponents.Select(FailureItemIds.Shutdown), shutdownItems.Select(item => item.ItemId));
        Assert.All(shutdownItems, item =>
        {
            Assert.Equal(FailureItemIds.StillRunning, item.Nature);
            Assert.True(item.Detail?.EndsWith(abandoned, StringComparison.Ordinal) == true, "a CHK-SHUTDOWN item does not say its shutdown was abandoned");
        });
        Assert.Equal(cancelledDuringShutdown ? 0 : 1, mode.State.Failures.Count(item => item.ItemId == FailureItemIds.Exception));

        // The first write precedes cleanup; the rewrite after cleanup shows both items cleanup added.
        Assert.Equal(2, writer.WriteCount);
        foreach (var component in BothComponents)
        {
            var itemId = FailureItemIds.Shutdown(component);
            Assert.False(writer.Written[0].Contains(itemId, StringComparison.Ordinal), "the first report is written before cleanup");
            Assert.True(writer.Written[^1].Contains(itemId, StringComparison.Ordinal), "the rewritten report lists every CHK-SHUTDOWN item");
        }

        Assert.True(sinceCancellation < (2 * ShortCancelledShutdownLimit) + Slack, "the cancelled exit path outlasted its two shutdown slices");
        AssertFinalLineLast();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(int.MaxValue + 1L)]
    public void CancelledRunLimits_NotPositiveOrAboveIntMaxMilliseconds_AreRefused(long milliseconds)
    {
        var limit = TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayMode(
            Options(), FakeBody.NoOp(), new RecordingReportWriter(), new RecordingShutdown(), KnownLimitationsRegister.Empty, _stdout)
        {
            CancelledShutdownLimit = limit,
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayBody(Options(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout)
        {
            CancelledRestorationLimit = limit,
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureMode(Options(), OperationCatalogue.Default, _stdout)
        {
            CancelledRestorationLimit = limit,
        });
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

        // The lines are shown with the temporary path removed.
        var lines = Redacted(StdoutText).Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();
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

    // A documented seed-token item holding an email, a documented ordering item, an undocumented reset
    // item holding a hostile source and a documented SOAP conformance gap whose wrong-type nature names an
    // XML element a response chose. The conclusion names all four with their console natures, the
    // documented ones with the entry that documents each. Whether the report is written or, after a failed
    // write, printed for recovery, the exact natures survive in the report text, no observed value reaches
    // standard output outside it, and the final line stays last.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conclusion_NamesDocumentedItemsWithTheirEntries_AndPrintsNoObservedValue(bool reportWriteFails)
    {
        const string email = "brian.private@example.org";
        const string hostileSource = "Hank " + email + "\u001B[31m\u202ERED";
        const string privateName = "Jane_Doe_private";
        var seed = new FailureItem(FailureItemIds.Seed("brian-olsen-email"), FailureItemIds.Differs($"\"{email}\""));
        var ordering = new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotAncestor);
        var reset = new FailureItem(FailureItemIds.Reset(ResetPoints.ReplayStart), FailureItemIds.Mismatch("source", hostileSource));
        var wrongType = new FailureItem(
            FailureItemIds.Conformance(Surface.Soap, "DTOResult", "allErrors"),
            FailureItemIds.WrongType("item-name:" + privateName));
        var register = new KnownLimitationsRegister(
        [
            new RegisterEntry("KL-SEED-EMAIL", RegisterCategories.ConformanceGap, null, [new(seed.ItemId, seed.Nature)], "seed differs", 3, "registered"),
            new RegisterEntry("KL-CHK-ORDERING", RegisterCategories.Check, null, [new(ordering.ItemId, ordering.Nature)], "history", 0, "registered"),
            new RegisterEntry("KL-CONF-ALLERRORS", RegisterCategories.ConformanceGap, null, [new(wrongType.ItemId, wrongType.Nature)], "item element misnamed", 3, "registered"),
        ]);
        var throwing = new ThrowingReportWriter();
        var recording = new RecordingReportWriter();
        var mode = NewMode(
            FakeBody.Adding(seed, ordering, reset, wrongType),
            reportWriteFails ? throwing : recording,
            new RecordingShutdown(),
            register);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        Assert.Equal(
            [(seed.ItemId, seed.Nature), (ordering.ItemId, ordering.Nature), (reset.ItemId, reset.Nature), (wrongType.ItemId, wrongType.Nature)],
            mode.State.Failures.Take(4).Select(item => (item.ItemId, item.Nature)));

        var reportText = reportWriteFails ? throwing.Rendered[^1] : recording.Written[^1];
        Assert.True(reportText.Contains(seed.Nature, StringComparison.Ordinal), "the report keeps the exact seed-token nature");
        Assert.True(reportText.Contains(reset.Nature, StringComparison.Ordinal), "the report keeps the exact reset nature");
        Assert.True(reportText.Contains(wrongType.Nature, StringComparison.Ordinal), "the report keeps the exact wrong-type nature");
        Assert.Equal(reportWriteFails, StdoutText.Contains(reportText, StringComparison.Ordinal));

        // Standard output holds the temporary report path, so the text around a match is not shown.
        var outside = StdoutText.Replace(reportText, string.Empty, StringComparison.Ordinal);
        foreach (var raw in new[] { email, "Hank", privateName, "\u001B", "\u202E", "\\u001B", "\\u202E" })
        {
            Assert.False(
                outside.Contains(raw, StringComparison.Ordinal),
                $"standard output outside the report holds '{LifecycleConsole.Display(raw)}'");
        }

        var lines = outside.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var undocumentedCount = reportWriteFails ? 2 : 1;
        var countLine = lines.FindIndex(line => line.StartsWith(
            string.Create(CultureInfo.InvariantCulture, $"replay: {undocumentedCount} undocumented failing item(s); "),
            StringComparison.Ordinal));
        var resetLine = lines.IndexOf("replay:   CHK-RESET:replay-start mismatch:source:<redacted: 39 chars>");
        var documentedLine = lines.IndexOf("replay: 3 documented failing item(s)");
        var seedLine = lines.IndexOf("replay:   CONF:SEED:brian-olsen-email differs:<redacted: 27 chars> (documented: KL-SEED-EMAIL)");
        var orderingLine = lines.IndexOf("replay:   CHK-ORDERING not-ancestor (documented: KL-CHK-ORDERING)");
        var wrongTypeLine = lines.IndexOf(
            "replay:   CONF:Soap:DTOResult.allErrors wrong-type:item-name:<redacted: 16 chars> (documented: KL-CONF-ALLERRORS)");
        Assert.True(countLine >= 0, "the conclusion counts the undocumented items");
        Assert.True(resetLine > countLine, "the undocumented reset item follows its count, redacted");
        Assert.True(documentedLine > resetLine, "the documented count follows the undocumented list");
        Assert.True(seedLine > documentedLine, "the documented seed-token item names its entry, redacted");
        Assert.True(orderingLine > seedLine, "the documented ordering item names its entry, in report order");
        Assert.True(wrongTypeLine > orderingLine, "the documented wrong-type item names its entry with its local name redacted");
        AssertFinalLineLast();
    }

    // The real body with one baseline record. The backend is external, so no stand-in is waited for or reset,
    // and both facade listeners are one loopback socket the test never accepts from, so any connection the
    // run made would still wait in its backlog. A 1 s request limit keeps a regression from hanging the run.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankFacadeAdminUrl_RecordsFacadeNotReady_SendsNoCase(string facadeAdminUrl)
    {
        const string caseId = "REST-getAgent-01";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var facadeUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var options = Options(backendAdminUrl: "");
        options.Target = new HarnessEndpoints { RestBaseUrl = facadeUrl, SoapBaseUrl = facadeUrl, AdminUrl = facadeAdminUrl };
        options.RequestTimeoutSeconds = 1;
        var body = """{"name":"Hank Williams"}"""u8.ToArray();
        new BaselineStore(options).WriteRecord(
            new BaselineRecord(
                1,
                caseId,
                "Rest.getAgent",
                CaseKinds.Success,
                StateChanging: false,
                CasePreStates.Seed,
                SourceLabels.StandIn,
                new BaselineRequest("GET", "/agent/Hank%20Williams", new Dictionary<string, string> { ["Accept"] = "application/json" }, null),
                BaselineResponse.FromRawBody(200, "application/json; charset=utf-8", body, bodyFile: null)),
            body);
        var state = new ReplayRunState();

        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            limit.CancelAfter(RunLimit);
            await new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout)
                .ExecuteAsync(state, limit.Token)
                .WaitAsync(limit.Token);
        }

        var ready = Assert.Single(state.Failures, item => item.ItemId == FailureItemIds.Ready(ShutdownTarget.FacadeComponent));
        Assert.Equal(FailureItemIds.Timeout, ready.Nature);
        Assert.True(ready.Detail?.Contains("Harness:Target:AdminUrl", StringComparison.Ordinal) == true, "the item names the blank setting");
        Assert.DoesNotContain(state.Failures, item => item.ItemId == FailureItemIds.Ready(ShutdownTarget.StandInComponent));
        Assert.True(
            StdoutText.Contains($"not ready: {ShutdownTarget.FacadeComponent} (", StringComparison.Ordinal),
            "the console reports the facade as not ready");

        // The .NET run fact states the blank setting, in one console line and no item.
        Assert.Equal("Harness:Target:AdminUrl is blank", state.Facts.FacadeFrameworkUnavailable);
        Assert.Null(state.Facts.FacadeFrameworkDescription);
        Assert.Equal(
            ["replay: facade .NET version unavailable: Harness:Target:AdminUrl is blank (no admin URL is configured, so no request was sent)"],
            StdoutText.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Contains(".NET version", StringComparison.Ordinal)));
        Assert.DoesNotContain(state.Failures, item => item.Detail?.Contains(AdminRoutes.Info, StringComparison.Ordinal) == true);

        // The case is compared as unanswered, and no connection reached the facade listeners.
        var result = Assert.Single(state.Cases);
        Assert.Equal(caseId, result.CaseId);
        Assert.True(result.Compared, "the case is compared");
        Assert.False(result.Equivalent, "an unanswered case is not equivalent");
        Assert.Null(result.ObservedStatus);
        Assert.Contains(result.Discrepancies, discrepancy => discrepancy.Kind == DiscrepancyKinds.NoResponse(NoResponseReasons.Connect));
        Assert.Contains(state.Failures, item => item.ItemId == FailureItemIds.Case(caseId) && item.Nature == result.ItemNature);
        Assert.False(listener.Pending(), "no request reached the facade listeners");
    }

    // The real body and exit path, with the capture stand-in stub as the facade: it answers every case 200 with
    // the JSON body {}, and the backend is external, so nothing is reset. Of five records, the first, third and
    // fourth differ from {} and the second and fifth equal it. The run may keep the evidence of the first and
    // third, counted from their own comparisons as EvidenceBudget counts it, plus limitOffset characters. At
    // exactly that total the third is kept and the smaller fourth is refused; one character less refuses the
    // third instead, which charges nothing, so the fourth is kept. A refused case is the never-documentable
    // CHK-EXCEPTION with the run's message and keeps no result or d: item, every other case keeps its own, the
    // report is written and the run exits 1.
    [Theory]
    [InlineData(0, "REST-getAllClients-01")]
    [InlineData(-1, "REST-getAllAgents-01")]
    public async Task KeptEvidenceOverTheRunLimit_RefusesTheCaseThatExceedsIt_KeepsTheOthers_WritesTheReport_Exits1(
        int limitOffset, string refusedCaseId)
    {
        const string contentType = "application/json; charset=utf-8";

        // Each record states its planned case's kind and request, which sends no header, so the reconciliation
        // with the committed matrix keeps all five.
        (int Order, string CaseId, string Operation, string Kind, string PathAndQuery, string Body)[] records =
        [
            (1, "REST-getAgent-01", "Rest.getAgent", CaseKinds.Success, "/agent/Hank%20Williams", """{"name":"Hank Williams"}"""),
            (2, "REST-getAgent-02", "Rest.getAgent", CaseKinds.Error, "/agent/Nobody%20Agent", "{}"),
            (3, "REST-getAllAgents-01", "Rest.getAllAgents", CaseKinds.Success, "/allAgents", $$"""[{"name":"{{new string('x', 200)}}"}]"""),
            (4, "REST-getAllClients-01", "Rest.getAllClients", CaseKinds.Success, "/allClients", "[]"),
            (5, "REST-getAllSales-01", "Rest.getAllSales", CaseKinds.Success, "/allSales", "{}"),
        ];

        await using var facade = await CaptureStandInStub.StartAsync(
            new string('a', 64), DefaultReferenceDate, failEmptyReset: false, TestCancellation);
        var options = Options(backendAdminUrl: "");
        options.Target = new HarnessEndpoints { RestBaseUrl = facade.BaseUrl, SoapBaseUrl = facade.BaseUrl, AdminUrl = facade.BaseUrl };
        options.Backend = new HarnessEndpoints { RestBaseUrl = facade.BaseUrl, SoapBaseUrl = facade.BaseUrl, AdminUrl = "" };
        options.RequestTimeoutSeconds = LimitSeconds;

        var store = new BaselineStore(options);
        var comparer = new ResponseComparer(OperationCatalogue.Default, new Normaliser(NormalisationRules.FromCatalogue(OperationCatalogue.Default)));
        var evidence = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (order, caseId, operation, kind, pathAndQuery, text) in records)
        {
            var body = Encoding.UTF8.GetBytes(text);
            store.WriteRecord(
                new BaselineRecord(
                    order,
                    caseId,
                    operation,
                    kind,
                    StateChanging: false,
                    CasePreStates.Seed,
                    SourceLabels.StandIn,
                    new BaselineRequest("GET", pathAndQuery, new Dictionary<string, string>(), null),
                    BaselineResponse.FromRawBody(200, contentType, body, bodyFile: null)),
                body);
            evidence[caseId] = KeptEvidence(comparer.Compare(
                OperationCatalogue.Default.GetById(operation),
                new ResponseSnapshot(200, contentType, body),
                new ResponseSnapshot(200, contentType, "{}"u8.ToArray())).Discrepancies);
        }

        // The committed matrix plans each record at its position for its operation, and a captured manifest that
        // lists no case as not captured completes the baseline, so no other step fails with the refusal's type.
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        WriteManifest(options, new string('a', 64), DefaultReferenceDate);

        Assert.Equal(0, evidence["REST-getAgent-02"] + evidence["REST-getAllSales-01"]);
        Assert.True(evidence["REST-getAllClients-01"] > 0, "the fourth case differs from {}");
        Assert.True(evidence["REST-getAllClients-01"] < evidence["REST-getAllAgents-01"], "the fourth case keeps less evidence than the third");
        var limit = checked((int)(evidence["REST-getAgent-01"] + evidence["REST-getAllAgents-01"] + limitOffset));

        Assert.Equal(
            HarnessOptions.EvidenceByteLimit,
            new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout).KeptEvidenceLimit);
        var replay = new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout)
        {
            KeptEvidenceLimit = limit,
        };
        var writer = new RecordingReportWriter();
        var shutdown = new RecordingShutdown();
        var mode = NewMode(replay, writer, shutdown, KnownLimitationsRegister.Empty, options);

        var exitCode = await RunAsync(mode);

        AssertExitCode(1, exitCode);
        var state = mode.State;
        var refusal = Assert.Single(state.Failures, item => item.Detail?.StartsWith("replay case ", StringComparison.Ordinal) == true);
        Assert.Equal((FailureItemIds.Exception, typeof(InvalidDataException).FullName), (refusal.ItemId, refusal.Nature));
        Assert.True(refusal.IsNeverDocumentable, "a case refused for the run's kept evidence can never be documented");
        Assert.Equal(
            string.Create(
                CultureInfo.InvariantCulture,
                $"replay case {refusedCaseId}: The discrepancy evidence the replay run keeps for its report exceeds the {limit}-character limit, so the case is refused rather than truncated."),
            refusal.Detail);
        Assert.DoesNotContain(state.Cases, result => result.CaseId == refusedCaseId);
        Assert.DoesNotContain(state.Failures, item => item.ItemId == FailureItemIds.Case(refusedCaseId));

        foreach (var caseId in records.Select(record => record.CaseId).Where(caseId => caseId != refusedCaseId))
        {
            var result = Assert.Single(state.Cases, result => result.CaseId == caseId);
            Assert.True(result.Compared, "every case the run did not refuse is compared");
            Assert.Equal(evidence[caseId] == 0, result.Equivalent);
            Assert.Equal(evidence[caseId], KeptEvidence(result.Discrepancies));
            var natures = state.Failures.Where(item => item.ItemId == FailureItemIds.Case(caseId)).Select(item => item.Nature).ToList();
            Assert.Equal(result.Equivalent ? 0 : 1, natures.Count);
            Assert.All(natures, nature => Assert.Equal(result.ItemNature, nature));
        }

        // At exactly the limit the case is kept: the run then holds the whole limit, and never more.
        var kept = state.Cases.Sum(result => KeptEvidence(result.Discrepancies));
        Assert.True(kept <= limit, "the run keeps no more evidence than its limit");
        Assert.Equal(limitOffset == 0, kept == limit);

        Assert.Equal(1, writer.WriteCount);
        Assert.True(writer.Written[^1].Contains(FailureItemIds.Case("REST-getAgent-01"), StringComparison.Ordinal), "the report lists the kept difference");
        Assert.True(writer.Written[^1].Contains(FailureItemIds.Exception, StringComparison.Ordinal), "the report lists the refused case's CHK-EXCEPTION");
        Assert.Equal([ShutdownTarget.FacadeComponent], shutdown.Components);
        AssertFinalLineLast();
    }

    // A negative kept-evidence or kept-body limit is refused by its seam.
    [Fact]
    public void KeptLimits_Negative_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayBody(Options(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout)
        {
            KeptEvidenceLimit = -1,
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureMode(Options(), OperationCatalogue.Default, _stdout)
        {
            KeptBodyLimit = -1,
        });
    }

    // The characters of discrepancy evidence a run keeps for a case, counted as EvidenceBudget counts them: each
    // path, plus each value's exact literal text or the display of an absent or valid value.
    private static long KeptEvidence(IEnumerable<Discrepancy> discrepancies)
    {
        static int Size(DiscrepancyValue value) => value.Text?.Length ?? value.ToString().Length;
        return discrepancies.Sum(discrepancy => (long)discrepancy.Path.Length + Size(discrepancy.Baseline) + Size(discrepancy.Observed));
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

    // The .NET run fact states what the facade's /admin/info read observed: the description it gave, or why
    // none is available, without quoting the URL or the body. A blank or credential-bearing admin URL sends no
    // request, and a run that never reached the read says so. The 1 s request limit bounds the unanswered read.
    [Theory]
    [InlineData(FacadeInfoAnswer.Described, ".NET 10.0.14")]
    [InlineData(FacadeInfoAnswer.Fieldless, "unavailable (facade /admin/info gave no frameworkDescription)")]
    [InlineData(FacadeInfoAnswer.NotJson, "unavailable (facade /admin/info answer is not a JSON object whose strings can be decoded)")]
    [InlineData(FacadeInfoAnswer.BlankDescription, "unavailable (facade /admin/info gave a blank frameworkDescription)")]
    [InlineData(FacadeInfoAnswer.ServerError, "unavailable (facade /admin/info answered HTTP 500)")]
    [InlineData(FacadeInfoAnswer.ConnectionRefused, "unavailable (facade /admin/info not reached)")]
    [InlineData(FacadeInfoAnswer.ConnectionReset, "unavailable (facade /admin/info connection closed before a complete response)")]
    [InlineData(FacadeInfoAnswer.NoResponse, "unavailable (facade /admin/info gave no response within 1 s)")]
    [InlineData(FacadeInfoAnswer.BlankUrl, "unavailable (Harness:Target:AdminUrl is blank)")]
    [InlineData(FacadeInfoAnswer.CredentialUrl, "unavailable (Harness:Target:AdminUrl is not an absolute http(s) URL without user information)")]
    [InlineData(FacadeInfoAnswer.NotRead, "unavailable (facade /admin/info not read)")]
    public async Task FacadeFrameworkRunFact_StatesWhatTheInfoReadObserved(FacadeInfoAnswer answer, string expectedValue)
    {
        const string credential = "example-pass";
        var options = Options();
        options.RequestTimeoutSeconds = 1;
        options.Target.AdminUrl = answer switch
        {
            FacadeInfoAnswer.BlankUrl => "   ",
            FacadeInfoAnswer.CredentialUrl => "http://alice:" + credential + "@facade.invalid:9090",
            _ => FacadeAdminUrl,
        };

        string? description = null;
        string? unavailable = null;
        if (answer != FacadeInfoAnswer.NotRead)
        {
            var sendsRequest = answer is not (FacadeInfoAnswer.BlankUrl or FacadeInfoAnswer.CredentialUrl);
            var admin = new ScriptedAdmin();
            if (sendsRequest)
            {
                admin.Then(InfoRequest, () => FacadeInfo(answer));
            }

            AdminInfoRead read;
            using (var reset = new StateReset(options, admin, _console))
            {
                read = await reset.ReadInfoOutcomeAsync(options.Target.AdminUrl, TestContext.Current.CancellationToken);
            }

            Assert.Equal(sendsRequest ? [InfoRequest] : Array.Empty<string>(), admin.Requests);
            (description, unavailable) = ReplayBody.DescribeFacadeFramework(read, options.RequestTimeout);
            Assert.True(description is null != unavailable is null, "exactly one of the description and the reason is set");
        }

        var state = new ReplayRunState
        {
            Facts = ReplayBody.DescribeRun(
                options, DateTimeOffset.UtcNow, description, baselineCommit: null, facadeCommit: null, frameworkUnavailable: unavailable),
        };
        var text = new ResultsReportWriter().Render(ResultsReport.Build(state, KnownLimitationsRegister.Empty, OperationCatalogue.Default));

        // The report holds the run date and the temporary root, so only its .NET row is shown, and the search for
        // the credential has a fixed message.
        const string frameworkRow = "| .NET (facade runtime) | ";
        Assert.Equal(
            [$"{frameworkRow}{expectedValue} |"],
            text.Split('\n').Where(line => line.StartsWith(frameworkRow, StringComparison.Ordinal)));
        Assert.False(text.Contains(credential, StringComparison.Ordinal), "the report never quotes the admin URL's credential");
    }

    // The facade's /admin/info answer for each scripted outcome; an exception stands for the failure the
    // socket handler raises.
    private static HttpResponseMessage FacadeInfo(FacadeInfoAnswer answer) => answer switch
    {
        FacadeInfoAnswer.Described => Json(new JsonObject { ["component"] = "facade", ["frameworkDescription"] = ".NET 10.0.14" }),
        FacadeInfoAnswer.Fieldless => Json(new JsonObject { ["component"] = "stub" }),
        FacadeInfoAnswer.NotJson => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json", Encoding.UTF8, "text/html"),
        },
        FacadeInfoAnswer.BlankDescription => Json(new JsonObject { ["component"] = "stub", ["frameworkDescription"] = "   " }),
        FacadeInfoAnswer.ServerError => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        FacadeInfoAnswer.ConnectionRefused => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (facade.invalid:9090)", new SocketException((int)SocketError.ConnectionRefused)),
        FacadeInfoAnswer.ConnectionReset => throw new HttpRequestException(
            HttpRequestError.ResponseEnded, "The response ended prematurely."),

        // Headers arrive, but the body is an empty pipe nothing ever writes to, so only the request limit
        // ends the read.
        FacadeInfoAnswer.NoResponse => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new Pipe().Reader.AsStream()),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "The answer sends no request."),
    };

    // Without baseline/conformance.json, a captured baseline (its manifest names how capture ran) fails
    // the run through a never-documentable CHK-EXCEPTION item, while a baseline from which nothing was
    // captured (the hand-written not-run manifest, or no manifest, and no record) reports the check as
    // not run and adds no item. The committed case matrix is copied in, and the captured manifest states
    // its label and both seed facts, so no other step throws.
    [Theory]
    [InlineData("local-processes", true)]
    [InlineData("not-run", false)]
    [InlineData(null, false)]
    public async Task ConformanceResultAbsent_FailsOnlyACapturedBaseline(string? captureExecution, bool expectFailure)
    {
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        if (captureExecution is not null)
        {
            var capturedFacts = captureExecution == CaptureExecutionValues.NotRun
                ? ""
                : $",\"label\":\"{SourceLabels.BaselineLabel}\",\"seedSha256\":\"{new string('a', 64)}\",\"referenceDate\":\"2026-01-01\"";
            RepoRootDirectory.Write("baseline/manifest.json", $$"""{"source":"jade-standin","captureExecution":"{{captureExecution}}"{{capturedFacts}},"notCaptured":[],"wsdlCaptured":false}""");
        }

        var state = new ReplayRunState();
        var body = new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RunLimit);
        await body.ExecuteAsync(state, limit.Token).WaitAsync(limit.Token);

        var check = state.Checks[CheckNames.Conformance];
        Assert.Equal(CheckOutcomes.NotRun, check.Outcome);
        var exceptions = state.Failures.Where(item => item.ItemId == FailureItemIds.Exception).ToList();
        // The item's detail and standard output come from a run against a temporary root and a loopback port,
        // so their text is not shown.
        if (expectFailure)
        {
            var item = Assert.Single(exceptions);
            Assert.Equal(typeof(FileNotFoundException).FullName, item.Nature);
            Assert.True(
                item.Detail?.StartsWith(
                    "stand-in conformance check: baseline/conformance.json is absent although the baseline was captured",
                    StringComparison.Ordinal) == true,
                "the CHK-EXCEPTION item names the absent conformance result of a captured baseline");
            Assert.True(item.IsNeverDocumentable, "a missing conformance result can never be documented");
            Assert.Equal("baseline/conformance.json absent although the baseline was captured", check.Detail);
            Assert.True(
                StdoutText.Contains(
                    "[jade-standin] conformance: baseline/conformance.json absent although the baseline was captured; the stand-in conformance evidence is missing",
                    StringComparison.Ordinal),
                "the console states that the conformance evidence of a captured baseline is missing");
        }
        else
        {
            Assert.Empty(exceptions);
            Assert.Equal("baseline/conformance.json absent", check.Detail);
            Assert.True(
                StdoutText.Contains("[jade-standin] conformance: baseline/conformance.json absent; not run", StringComparison.Ordinal),
                "the console states that the conformance check did not run");
        }
    }

    // Before any case is counted or compared, the real body reconciles the records with the manifest and the
    // committed case matrix. Every evidence problem becomes one never-documentable CHK-EXCEPTION, printed as a
    // [jade-standin] line, and the records the reconciliation keeps are still compared. A record of a case the
    // manifest lists as not captured, or one the matrix does not plan where it stands, is neither compared nor
    // counted as captured; a planned case among them is reported as not captured exactly once. So is a record
    // whose request (method, pathAndQuery, headers or bodyText), kind, stateChanging or preState is not its
    // planned case's, each problem naming the recorded and the planned value, and one whose preState is neither
    // seed nor empty, which is refused even when the matrix cannot be loaded. A captured
    // manifest must state its source and label, while the hand-written not-run fallback, with no record, every
    // case listed and neither member, raises no evidence item; a stated label other than the baseline label
    // text is a problem in either. Each scenario starts from the committed REST-getAgent-01 record and a
    // manifest that states source jade-standin, the baseline label text and both seed facts.
    [Theory]
    [InlineData("manifest-absent", "baseline/manifest.json is absent although 1 baseline record(s) were read; capture publishes it last, as its completion record, so the capture is not shown complete and no seed fact can be confirmed", "REST-getAgent-01", null, null)]
    [InlineData("seed-sha-missing", "baseline/manifest.json records capture execution local-processes but leaves seedSha256 missing or blank, so the seed state cannot be confirmed; only a not-run manifest holds no seed facts", "REST-getAgent-01", null, null)]
    [InlineData("seed-sha-blank", "baseline/manifest.json records capture execution local-processes but leaves seedSha256 missing or blank, so the seed state cannot be confirmed; only a not-run manifest holds no seed facts", "REST-getAgent-01", null, null)]
    [InlineData("reference-date-blank", "baseline/manifest.json records capture execution local-processes but leaves referenceDate missing or blank, so the seed state cannot be confirmed; only a not-run manifest holds no seed facts", "REST-getAgent-01", null, null)]
    [InlineData("source-live-jade", "baseline/manifest.json has source 'live-jade', but the baseline is stand-in output and is labelled 'jade-standin' in every place", "REST-getAgent-01", null, null)]
    [InlineData("source-missing", "baseline/manifest.json records capture execution local-processes but states no source, so the baseline is not shown to be stand-in output; a captured manifest states source 'jade-standin', and only a not-run manifest omits it", "REST-getAgent-01", null, null)]
    [InlineData("label-missing", "baseline/manifest.json records capture execution local-processes but states no label, so the baseline is not shown to be stand-in output; a captured manifest carries the label text 'Recorded from the JADE stand-in, not from live JADE', and only a not-run manifest omits it", "REST-getAgent-01", null, null)]
    [InlineData("label-other", "baseline/manifest.json has label 'Recorded from live JADE', but the baseline is stand-in output and carries the label text 'Recorded from the JADE stand-in, not from live JADE'", "REST-getAgent-01", null, null)]
    [InlineData("not-run-with-record", "baseline/manifest.json records capture execution not-run, which holds no record, but 1 record(s) remain: baseline/records/rest/001-getAgent-01.json", "REST-getAgent-01", null, null)]
    [InlineData("listed-not-captured", "baseline/manifest.json lists case REST-getAgent-01 in notCaptured, but baseline/records/rest/001-getAgent-01.json records it; the case is reported as not captured and never compared", null, "REST-getAgent-01", "listed in baseline/manifest.json notCaptured")]
    [InlineData("unplanned-case", "baseline/records/rest/057-getAgent-99.json records case REST-getAgent-99, which harness/config/case-matrix.json does not plan; it is not compared", "REST-getAgent-01", "REST-getAgent-99", null)]
    [InlineData("other-position", "baseline/records/rest/002-getAgent-01.json records case REST-getAgent-01 at position 2 of operation Rest.getAgent, but harness/config/case-matrix.json plans it at position 1 of operation Rest.getAgent; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/002-getAgent-01.json records case REST-getAgent-01 at position 2")]
    [InlineData("not-run-every-case-listed", null, null, "REST-getAgent-01", "listed in baseline/manifest.json notCaptured")]
    [InlineData("not-run-label-other", "baseline/manifest.json has label 'Recorded from live JADE', but the baseline is stand-in output and carries the label text 'Recorded from the JADE stand-in, not from live JADE'", null, "REST-getAgent-01", "listed in baseline/manifest.json notCaptured")]
    [InlineData("request-path-other", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request pathAndQuery '/agent/Suzie%20Wu', but harness/config/case-matrix.json plans '/agent/Hank%20Williams'; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request pathAndQuery '/agent/Suzie%20Wu', but harness/config/case-matrix.json plans '/agent/Hank%20Williams'; it is not compared")]
    [InlineData("request-method-other", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request method 'POST', but harness/config/case-matrix.json plans 'GET'; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request method 'POST', but harness/config/case-matrix.json plans 'GET'; it is not compared")]
    [InlineData("request-header-added", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request headers {Accept: 'application/json'}, but harness/config/case-matrix.json plans {}; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request headers {Accept: 'application/json'}, but harness/config/case-matrix.json plans {}; it is not compared")]
    [InlineData("request-body-set", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request bodyText of 2 characters (SHA-256 44136fa355b3678a…), but harness/config/case-matrix.json plans none; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with request bodyText of 2 characters (SHA-256 44136fa355b3678a…), but harness/config/case-matrix.json plans none; it is not compared")]
    [InlineData("kind-error", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with kind 'error', but harness/config/case-matrix.json plans 'success'; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with kind 'error', but harness/config/case-matrix.json plans 'success'; it is not compared")]
    [InlineData("state-changing", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with stateChanging true, but harness/config/case-matrix.json plans false; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with stateChanging true, but harness/config/case-matrix.json plans false; it is not compared")]
    [InlineData("pre-state-invalid", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with preState 'bogus', which is neither 'seed' nor 'empty', but harness/config/case-matrix.json plans 'seed'; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with preState 'bogus', which is neither 'seed' nor 'empty', but harness/config/case-matrix.json plans 'seed'; it is not compared")]
    [InlineData("pre-state-empty", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with preState 'empty', but harness/config/case-matrix.json plans 'seed'; it is not compared", null, "REST-getAgent-01", "planned in harness/config/case-matrix.json, but its baseline record was refused: baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with preState 'empty', but harness/config/case-matrix.json plans 'seed'; it is not compared")]
    [InlineData("pre-state-invalid-no-plan", "baseline/records/rest/001-getAgent-01.json records case REST-getAgent-01 with preState 'bogus', which is neither 'seed' nor 'empty'; it is not compared", null, "REST-getAgent-01", null)]
    public async Task BaselineEvidence_IsReconciledBeforeAnyCaseIsCountedOrCompared(
        string scenario, string? problem, string? comparedCaseId, string? uncomparedCaseId, string? notCapturedReason)
    {
        const string recordFile = "baseline/records/rest/001-getAgent-01.json";
        var matrixText = File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath));
        RepoRootDirectory.Write(CaseMatrixPath, matrixText);
        var record = JsonNode.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), recordFile)))!.AsObject();
        var manifest = new JsonObject
        {
            ["source"] = SourceLabels.StandIn,
            ["label"] = SourceLabels.BaselineLabel,
            ["seedSha256"] = new string('a', 64),
            ["referenceDate"] = "2026-01-01",
            ["notCaptured"] = new JsonArray(),
            ["wsdlCaptured"] = false,
            ["captureExecution"] = CaptureExecutionValues.LocalProcesses,
        };

        switch (scenario)
        {
            case "manifest-absent":
                manifest = null;
                break;
            case "seed-sha-missing":
                manifest.Remove("seedSha256");
                break;
            case "seed-sha-blank":
                manifest["seedSha256"] = "";
                break;
            case "reference-date-blank":
                manifest["referenceDate"] = "  ";
                break;
            case "source-live-jade":
                manifest["source"] = "live-jade";
                break;
            case "source-missing":
                manifest.Remove("source");
                break;
            case "label-missing":
                manifest.Remove("label");
                break;
            case "label-other":
                manifest["label"] = "Recorded from live JADE";
                break;
            case "not-run-with-record":
                manifest = new JsonObject { ["source"] = SourceLabels.StandIn, ["captureExecution"] = CaptureExecutionValues.NotRun };
                break;
            case "listed-not-captured":
                manifest["notCaptured"] = new JsonArray("REST-getAgent-01");
                break;
            case "unplanned-case":
                var unplanned = record.DeepClone().AsObject();
                unplanned["order"] = 57;
                unplanned["caseId"] = "REST-getAgent-99";
                RepoRootDirectory.Write("baseline/records/rest/057-getAgent-99.json", unplanned.ToJsonString());
                break;
            case "other-position":
                record["order"] = 2;
                RepoRootDirectory.Write("baseline/records/rest/002-getAgent-01.json", record.ToJsonString());
                record = null;
                break;
            case "not-run-every-case-listed":
            case "not-run-label-other":
                // The hand-written fallback, which states neither source nor label.
                var planned = CaseMatrix.Parse(matrixText, OperationCatalogue.Default).Cases.Select(definition => (JsonNode?)definition.Id);
                manifest = new JsonObject
                {
                    ["captureExecution"] = CaptureExecutionValues.NotRun,
                    ["notCaptured"] = new JsonArray([.. planned]),
                };
                if (scenario == "not-run-label-other")
                {
                    manifest["label"] = "Recorded from live JADE";
                }

                record = null;
                break;
            case "request-path-other":
                record["request"]!["pathAndQuery"] = "/agent/Suzie%20Wu";
                break;
            case "request-method-other":
                record["request"]!["method"] = "POST";
                break;
            case "request-header-added":
                record["request"]!["headers"] = new JsonObject { ["Accept"] = "application/json" };
                break;
            case "request-body-set":
                record["request"]!["bodyText"] = "{}";
                break;
            case "kind-error":
                record["kind"] = CaseKinds.Error;
                break;
            case "state-changing":
                record["stateChanging"] = true;
                break;
            case "pre-state-invalid":
                record["preState"] = "bogus";
                break;
            case "pre-state-empty":
                record["preState"] = CasePreStates.Empty;
                break;
            case "pre-state-invalid-no-plan":
                // Without the plan, whose load then fails the run on its own, the preState is still checked.
                File.Delete(RepoRootDirectory.PathOf(CaseMatrixPath));
                record["preState"] = "bogus";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        if (record is not null)
        {
            RepoRootDirectory.Write(recordFile, record.ToJsonString());
        }

        if (manifest is not null)
        {
            RepoRootDirectory.Write("baseline/manifest.json", manifest.ToJsonString());
        }

        var state = new ReplayRunState();
        var body = new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout);
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            limit.CancelAfter(RunLimit);
            await body.ExecuteAsync(state, limit.Token).WaitAsync(limit.Token);
        }

        // The run works in a temporary root against a loopback port: an item's detail is shown with the root
        // removed, and standard output is checked with fixed messages.
        const string evidenceStep = "confirm the baseline evidence: ";
        var evidence = state.Failures
            .Where(item => item.ItemId == FailureItemIds.Exception && item.Detail?.StartsWith(evidenceStep, StringComparison.Ordinal) == true)
            .ToList();
        if (problem is null)
        {
            Assert.Empty(evidence);
            Assert.False(
                StdoutText.Contains("[jade-standin] baseline evidence: ", StringComparison.Ordinal),
                "the console states no baseline evidence problem");
        }
        else
        {
            var item = Assert.Single(evidence);
            Assert.Equal(typeof(InvalidDataException).FullName, item.Nature);
            Assert.True(item.IsNeverDocumentable, "an evidence problem can never be documented");
            Assert.Equal(
                $"{evidenceStep}The baseline evidence is incomplete or inconsistent (1 problem(s)): {problem}.",
                item.Detail is null ? null : Redacted(item.Detail));
            Assert.True(
                StdoutText.Contains($"[jade-standin] baseline evidence: {problem}", StringComparison.Ordinal),
                "the console states the scenario's baseline evidence problem");
        }

        var captured = state.CapturedCasesByOperation.Values.SelectMany(ids => ids).ToList();
        if (comparedCaseId is not null)
        {
            var result = Assert.Single(state.Cases);
            Assert.Equal(comparedCaseId, result.CaseId);
            Assert.True(result.Compared, "a kept record is still compared");
            Assert.Equal([comparedCaseId], captured);
        }
        else
        {
            Assert.Empty(state.Cases);
            Assert.Empty(captured);
        }

        if (uncomparedCaseId is not null)
        {
            Assert.DoesNotContain(state.Cases, result => result.CaseId == uncomparedCaseId);
            Assert.DoesNotContain(uncomparedCaseId, captured);
            var notCaptured = state.Failures
                .Where(item => item.ItemId == FailureItemIds.Case(uncomparedCaseId) && item.Nature == FailureItemIds.NotCaptured)
                .ToList();
            if (notCapturedReason is null)
            {
                Assert.Empty(notCaptured);
            }
            else
            {
                var item = Assert.Single(notCaptured);
                Assert.StartsWith(notCapturedReason, item.Detail is null ? null : Redacted(item.Detail), StringComparison.Ordinal);
            }
        }
    }

    // The evidence is confirmed first, and each record is read again when its case is replayed, so a record
    // rewritten in between is held to the plan again: the real body refuses it as its own never-documentable
    // CHK-EXCEPTION, naming the record and the planned value, before any reset or request, so the empty-state
    // bracket never opens and the case is neither sent nor compared. The committed matrix and empty-state record
    // REST-getCompany-03 are copied in, the capture stub serves as both facade and stand-in, and the record is
    // rewritten as the console announces the normalisation rules, the step between the confirmation and the
    // first case.
    [Theory]
    [InlineData("preState", "bogus", "preState 'bogus', which is neither 'seed' nor 'empty', but harness/config/case-matrix.json plans 'empty'")]
    [InlineData("request.pathAndQuery", "/company.json", "request pathAndQuery '/company.json', but harness/config/case-matrix.json plans '/company'")]
    public async Task RecordRewrittenAfterTheEvidenceIsConfirmed_IsRefusedAsItsCaseBeforeAnyResetOrRequest(
        string member, string value, string divergence)
    {
        const string caseStep = "replay case REST-getCompany-03: ";
        var seedSha256 = new string('a', 64);
        await using var stub = await CaptureStandInStub.StartAsync(seedSha256, DefaultReferenceDate, failEmptyReset: false, TestCancellation);
        var options = Options();
        options.Target = new HarnessEndpoints { RestBaseUrl = stub.BaseUrl, SoapBaseUrl = stub.BaseUrl, AdminUrl = stub.BaseUrl };
        options.Backend = new HarnessEndpoints { RestBaseUrl = stub.BaseUrl, SoapBaseUrl = stub.BaseUrl, AdminUrl = stub.BaseUrl };
        options.RequestTimeoutSeconds = LimitSeconds;
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        var recordText = File.ReadAllText(Path.Combine(FindRepoRoot(), EmptyStateRecordFile));
        RepoRootDirectory.Write(EmptyStateRecordFile, recordText);
        WriteManifest(options, seedSha256, DefaultReferenceDate);

        // No baseline/normalisation-rules.json is copied in, so the rules line is the derived-rules one.
        using var console = new TriggeringConsole("the normalisation rules are derived from the catalogue", () =>
        {
            var record = JsonNode.Parse(recordText)!.AsObject();
            var owner = member.StartsWith("request.", StringComparison.Ordinal) ? record["request"]!.AsObject() : record;
            owner[member.Split('.')[^1]] = value;
            RepoRootDirectory.Write(EmptyStateRecordFile, record.ToJsonString());
        });

        // Readiness writes from two probes at once.
        using var output = TextWriter.Synchronized(console);
        var state = new ReplayRunState();
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            limit.CancelAfter(RunLimit);
            await new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, output)
                .ExecuteAsync(state, limit.Token)
                .WaitAsync(limit.Token);
        }

        Assert.True(console.Fired, "the record was rewritten between the confirmation and the first case");
        Assert.DoesNotContain(state.Failures, item => item.Detail?.StartsWith("confirm the baseline evidence: ", StringComparison.Ordinal) == true);
        var refusal = Assert.Single(state.Failures, item => item.Detail?.StartsWith(caseStep, StringComparison.Ordinal) == true);
        Assert.Equal((FailureItemIds.Exception, typeof(InvalidDataException).FullName), (refusal.ItemId, refusal.Nature));
        Assert.True(refusal.IsNeverDocumentable, "a record refused at its replay can never be documented");
        Assert.Equal(
            $"{caseStep}The baseline record of case REST-getCompany-03 is refused before any reset or request: "
            + $"{EmptyStateRecordFile} records case REST-getCompany-03 with {divergence}; it is not compared.",
            refusal.Detail);
        Assert.Empty(state.Cases);

        // Only the replay-start reset reached the stand-in, and no case request reached the facade.
        var requests = stub.Requests;
        Assert.Equal([SeedResetRequest], requests.Where(request => request.StartsWith("POST " + AdminRoutes.Reset, StringComparison.Ordinal)));
        Assert.DoesNotContain(requests, request => request.StartsWith("GET /company", StringComparison.Ordinal));
    }

    // The confirmation reads each indexed record again, so a record that can no longer be read by then is an
    // evidence problem of its own: the real body names the record and why in the one evidence CHK-EXCEPTION,
    // and the planned case is reported as not captured with that refusal, never replayed. The committed
    // REST-getAgent-01 record and case matrix are copied in, and the record is removed as the console announces
    // the records read, the step before the confirmation.
    [Fact]
    public async Task RecordUnreadableWhenTheEvidenceIsConfirmed_IsRefusedAsEvidence_AndReportedNotCaptured()
    {
        const string recordFile = "baseline/records/rest/001-getAgent-01.json";
        const string problem =
            $"{recordFile} of case REST-getAgent-01 cannot be read again (Baseline record {recordFile} of case "
            + "REST-getAgent-01 is missing.); it is not compared";
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath)));
        RepoRootDirectory.Write(recordFile, File.ReadAllText(Path.Combine(FindRepoRoot(), recordFile)));
        RepoRootDirectory.Write("baseline/manifest.json", new JsonObject
        {
            ["source"] = SourceLabels.StandIn,
            ["label"] = SourceLabels.BaselineLabel,
            ["seedSha256"] = new string('a', 64),
            ["referenceDate"] = DefaultReferenceDate,
            ["notCaptured"] = new JsonArray(),
            ["wsdlCaptured"] = false,
            ["captureExecution"] = CaptureExecutionValues.LocalProcesses,
        }.ToJsonString());
        using var console = new TriggeringConsole(
            "baseline record(s) read; each body is verified when its case is replayed",
            () => File.Delete(RepoRootDirectory.PathOf(recordFile)));

        // Readiness writes from two probes at once.
        using var output = TextWriter.Synchronized(console);
        var state = new ReplayRunState();
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            limit.CancelAfter(RunLimit);
            await new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, output)
                .ExecuteAsync(state, limit.Token)
                .WaitAsync(limit.Token);
        }

        Assert.True(console.Fired, "the record was removed between the index and the confirmation");
        var evidence = Assert.Single(
            state.Failures, item => item.Detail?.StartsWith("confirm the baseline evidence: ", StringComparison.Ordinal) == true);
        Assert.Equal((FailureItemIds.Exception, typeof(InvalidDataException).FullName), (evidence.ItemId, evidence.Nature));
        Assert.Equal(
            $"confirm the baseline evidence: The baseline evidence is incomplete or inconsistent (1 problem(s)): {problem}.",
            evidence.Detail);
        Assert.True(console.ToString().Contains($"[jade-standin] baseline evidence: {problem}", StringComparison.Ordinal), "the console states the problem");
        var notCaptured = Assert.Single(
            state.Failures,
            item => item.ItemId == FailureItemIds.Case("REST-getAgent-01") && item.Nature == FailureItemIds.NotCaptured);
        Assert.Equal($"planned in {CaseMatrixPath}, but its baseline record was refused: {problem}", notCaptured.Detail);
        Assert.Empty(state.Cases);
        Assert.Empty(state.CapturedCasesByOperation.Values.SelectMany(ids => ids));
        Assert.DoesNotContain(state.Failures, item => item.Detail?.StartsWith("replay case ", StringComparison.Ordinal) == true);
    }

    // A stand-in WSDL record that is a link to a file outside the repository root is refused, and the refusal
    // hides no other baseline fact: the real body records it as one never-documentable CHK-EXCEPTION naming the
    // WSDL, counts the WSDL as not captured, and still counts the plan, the captured record and the manifest's
    // facts. The manifest records the WSDL as captured, so only the refusal makes it absent. The committed
    // REST-getAgent-01 record, case matrix and WSDL are copied in, and the manifest states its source, its
    // label and both seed facts.
    [Fact]
    public async Task BaselineWsdlLink_IsRefused_OtherBaselineFactsStillCounted()
    {
        const string recordFile = "baseline/records/rest/001-getAgent-01.json";
        const string wsdlFile = "baseline/wsdl/standin-ErewhonWebService.wsdl";
        var matrixText = File.ReadAllText(Path.Combine(FindRepoRoot(), CaseMatrixPath));
        RepoRootDirectory.Write(CaseMatrixPath, matrixText);
        RepoRootDirectory.Write(recordFile, File.ReadAllText(Path.Combine(FindRepoRoot(), recordFile)));
        RepoRootDirectory.Write("baseline/manifest.json", new JsonObject
        {
            ["source"] = SourceLabels.StandIn,
            ["label"] = SourceLabels.BaselineLabel,
            ["seedSha256"] = new string('a', 64),
            ["referenceDate"] = "2026-01-01",
            ["notCaptured"] = new JsonArray(),
            ["wsdlCaptured"] = true,
            ["captureExecution"] = CaptureExecutionValues.LocalProcesses,
        }.ToJsonString());
        using var outside = new TempDirectory();
        var target = outside.Write("standin-ErewhonWebService.wsdl", File.ReadAllText(Path.Combine(FindRepoRoot(), wsdlFile)));
        var link = RepoRootDirectory.PathOf(wsdlFile);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        try
        {
            var state = new ReplayRunState();
            var body = new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, KnownLimitationsRegister.Empty, _stdout);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                limit.CancelAfter(RunLimit);
                await body.ExecuteAsync(state, limit.Token).WaitAsync(limit.Token);
            }

            var step = $"read {wsdlFile}";
            var refused = Assert.Single(
                state.Failures,
                item => item.ItemId == FailureItemIds.Exception && item.Detail?.StartsWith($"{step}: ", StringComparison.Ordinal) == true);
            Assert.Equal(typeof(InvalidOperationException).FullName, refused.Nature);
            Assert.True(refused.IsNeverDocumentable, "a refused WSDL record can never be documented");

            // Standard output holds the temporary root and the loopback port, so it is checked with a fixed message.
            Assert.True(StdoutText.Contains($"{step} failed: ", StringComparison.Ordinal), "the console states that reading the WSDL record failed");
            Assert.DoesNotContain(
                state.Failures,
                item => item.ItemId == FailureItemIds.Exception && item.Detail?.StartsWith("count the baseline: ", StringComparison.Ordinal) == true);

            var matrix = CaseMatrix.Parse(matrixText, OperationCatalogue.Default);
            var baseline = state.Baseline;
            Assert.Equal(matrix.PlannedOperationCount, baseline.PlannedOperations);
            Assert.Equal(matrix.PlannedCaseCount, baseline.PlannedCases);
            Assert.Equal(1, baseline.CapturedOperations);
            Assert.Equal(1, baseline.CapturedCases);
            Assert.Empty(baseline.NotCaptured);
            Assert.False(baseline.WsdlCaptured, "a refused WSDL record counts as not captured");
            Assert.True(baseline.ManifestPresent, "the manifest facts are still counted");
            Assert.Equal(CaptureExecutionValues.LocalProcesses, baseline.CaptureExecution);

            // Both paths lie in temporary directories, so the comparison has a fixed message.
            Assert.True(
                string.Equals(target, new FileInfo(link).LinkTarget, StringComparison.Ordinal),
                "the refused WSDL record is left as the link to its target outside the root");
        }
        finally
        {
            // The link goes before its target's directory, so the repository root never holds a dangling link.
            File.Delete(link);
        }
    }

    // The committed conformance result, with the committed register documenting its 99 gaps, counts the
    // members the report states, on the console and in the check. The same file claiming a pass for the
    // unreachable DTOError.guid, its counts adjusted so it parses, contradicts the catalogue: it fails the
    // run through a never-documentable CHK-EXCEPTION item and counts nothing. Replay never writes the file.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConformanceResult_IsHeldToTheCatalogueAndCountedByMember(bool unreachablePassClaimed)
    {
        const string unreachableMember = "CONF:Rest:DTOError.guid";
        var repoRoot = FindRepoRoot();
        RepoRootDirectory.Write(CaseMatrixPath, File.ReadAllText(Path.Combine(repoRoot, CaseMatrixPath)));
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, ConformanceFile)))!.AsObject();
        var members = document["members"]!.AsArray();
        var index = members.Select(member => member!["itemId"]!.GetValue<string>()).ToList().IndexOf(unreachableMember);
        Assert.Equal(FailureItemIds.UnreachableInSource, members[index]!["result"]!.GetValue<string>());
        if (unreachablePassClaimed)
        {
            members[index]!["result"] = "pass";
            var counts = document["counts"]!;
            counts["passed"] = counts["passed"]!.GetValue<int>() + 1;
            counts["gapsByNature"]![FailureItemIds.UnreachableInSource] = counts["gapsByNature"]![FailureItemIds.UnreachableInSource]!.GetValue<int>() - 1;
        }

        var written = RepoRootDirectory.Write(ConformanceFile, document.ToJsonString());
        var register = KnownLimitationsRegister.Load(Path.Combine(repoRoot, "harness", "config", "known-limitations.json"));
        var state = new ReplayRunState();
        var body = new ReplayBody(ClosedPortOptions(), OperationCatalogue.Default, register, _stdout);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(RunLimit);
        await body.ExecuteAsync(state, limit.Token).WaitAsync(limit.Token);

        // The run works in a temporary root against a loopback port: the item's detail is shown with the root
        // removed, and standard output is checked with fixed messages.
        var check = state.Checks[CheckNames.Conformance];
        var exceptions = state.Failures.Where(item => item.ItemId == FailureItemIds.Exception).ToList();
        if (unreachablePassClaimed)
        {
            var item = Assert.Single(exceptions);
            Assert.Equal(typeof(InvalidDataException).FullName, item.Nature);
            Assert.True(item.IsNeverDocumentable, "a conformance result the catalogue contradicts can never be documented");
            Assert.Equal(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"stand-in conformance check: baseline/conformance.json: $.members[{index}].result is 'pass', but the catalogue records '{unreachableMember}' as unreachable in source, so its result must be 'unreachable-in-source'."),
                item.Detail is null ? null : Redacted(item.Detail));
            Assert.Equal(CheckOutcomes.NotRun, check.Outcome);
            Assert.Equal("the check did not complete", check.Detail);
            Assert.Null(state.Conformance);
            Assert.DoesNotContain(state.Failures, failure => failure.ItemId.StartsWith("CONF:", StringComparison.Ordinal));
            Assert.False(StdoutText.Contains("check conformance:", StringComparison.Ordinal), "the console states no conformance count");
        }
        else
        {
            Assert.Empty(exceptions);
            Assert.Equal(CheckOutcomes.Pass, check.Outcome);
            Assert.Equal(216, check.Cases);
            Assert.Equal(117, check.Passed);
            Assert.Equal("99 of 99 failing items documented by the register; 2 of 2 seed-token assertions hold", check.Detail);
            Assert.NotNull(state.Conformance);
            Assert.Equal((216, 117), (state.Conformance.MembersChecked, state.Conformance.Passed));
            Assert.True(
                StdoutText.Contains(
                    "[jade-standin] check conformance: pass (117 of 216 members passed); 99 of 99 failing items documented by the register; 2 of 2 seed-token assertions hold",
                    StringComparison.Ordinal),
                "the console states the conformance counts of the committed result");
        }

        // Replay reads the baseline and never writes it.
        Assert.Equal(document.ToJsonString(), File.ReadAllText(written));
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

    /// <summary>
    /// A console that keeps what is written to it and runs an action once, as the first line containing a marker
    /// is written, so a test can change the repository between two steps of a run. Wrap it in
    /// <see cref="TextWriter.Synchronized"/> where lines arrive from several threads.
    /// </summary>
    private sealed class TriggeringConsole(string marker, Action action) : StringWriter(CultureInfo.InvariantCulture)
    {
        /// <summary>Whether the marker was written and the action ran.</summary>
        public bool Fired { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!Fired && value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                Fired = true;
                action();
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

    /// <summary>
    /// Fails every render with the same fixed exception and records the report each render was given; counts
    /// every write, which a report that could not be rendered must never reach.
    /// </summary>
    private sealed class ThrowingRenderReportWriter : IResultsReportWriter
    {
        private const string RenderFailureMessage = "the report cannot be rendered";

        private readonly List<ResultsReport> _rendered = [];
        private int _writeAttempts;

        /// <summary>The report of every render call, in call order.</summary>
        public IReadOnlyList<ResultsReport> Rendered => _rendered;

        public int WriteAttempts => Volatile.Read(ref _writeAttempts);

        /// <summary>The exception every render throws, created anew for each call.</summary>
        public static InvalidOperationException RenderFailure() => new(RenderFailureMessage);

        public string Render(ResultsReport report)
        {
            _rendered.Add(report);
            throw RenderFailure();
        }

        public void Write(string path, string content) => Interlocked.Increment(ref _writeAttempts);
    }

    /// <summary>
    /// Renders with the real writer and blocks every write until disposed, as a write to a destination that
    /// stalls does; a released write then fails, as one abandoned to a stalled destination eventually may.
    /// </summary>
    private sealed class BlockingReportWriter : IResultsReportWriter, IDisposable
    {
        private readonly ResultsReportWriter _renderer = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _rendered = [];
        private int _writeAttempts;

        public IReadOnlyList<string> Rendered => _rendered;

        public int WriteAttempts => Volatile.Read(ref _writeAttempts);

        public string Render(ResultsReport report)
        {
            var text = _renderer.Render(report);
            _rendered.Add(text);
            return text;
        }

        public void Write(string path, string content)
        {
            Interlocked.Increment(ref _writeAttempts);
            _released.Task.Wait();
            throw new IOException("the stalled report write was released");
        }

        public void Dispose() => _released.TrySetResult();
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

    /// <summary>
    /// One call of <see cref="UnconfirmedShutdown"/>: its component, its start and end as
    /// <see cref="Stopwatch"/> timestamps, and whether its token was cancelled at each.
    /// </summary>
    private sealed record UnconfirmedShutdownCall(string Component, long Started, long Ended, bool CancelledAtStart, bool CancelledAtEnd);

    /// <summary>
    /// A shutdown client that confirms no stop: each call waits until its token is cancelled, or at most the
    /// run limit, then reports its component still running. Each call is recorded as it ends.
    /// </summary>
    private sealed class UnconfirmedShutdown : IComponentShutdown
    {
        private readonly Lock _gate = new();
        private readonly List<UnconfirmedShutdownCall> _calls = [];
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first call starts.</summary>
        public Task FirstCallStarted => _firstCallStarted.Task;

        public IReadOnlyList<UnconfirmedShutdownCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task<ShutdownOutcome> ShutdownAsync(ShutdownTarget target, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            var cancelledAtStart = ct.IsCancellationRequested;
            _firstCallStarted.TrySetResult();

            // Ends at the token's cancellation; the run limit only bounds a token nothing cancels.
            await Task.Delay(RunLimit, ct).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            var cancelledAtEnd = ct.IsCancellationRequested;
            lock (_gate)
            {
                _calls.Add(new UnconfirmedShutdownCall(target.Component, started, Stopwatch.GetTimestamp(), cancelledAtStart, cancelledAtEnd));
            }

            return new ShutdownOutcome(target.Component, false, cancelledAtEnd ? "cancelled" : "never cancelled");
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

    // Stands for a measured time in a detail or console line, so a failure message reads the same in every run.
    private const string ElapsedToken = "<elapsed>";

    // Headroom over the shutdown limit for scheduling; a wait that ignored the limit overruns it by far more.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    // A measured time as the shutdown and readiness clients print it: "refused after 123 ms", "(45 ms)".
    private static readonly Regex MeasuredMilliseconds = new("[0-9]+ ms\\b", RegexOptions.CultureInvariant);

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

        Assert.True(outcome.Stopped, WithoutMilliseconds(outcome.Detail));
        Assert.Null(outcome.ToFailureItem());
        Assert.Equal(ShutdownTarget.StandInComponent, outcome.Component);
        Assert.Equal($"{postText}; refused after {ElapsedToken} ms", WithoutMilliseconds(outcome.Detail));

        // How many probes a client that missed the refusal would send depends on timing, so the message is fixed.
        Assert.True(
            component.Requests.SequenceEqual([ShutdownRequest, HealthRequest, HealthRequest]),
            "the requests are the shutdown call, the answered health probe and the refused one, in that order");
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
        Assert.Equal("POST 202; still answering at 1 s (last 200)", WithoutMilliseconds(outcome.Detail));
        Assert.True(component.HealthProbes >= 3, "fewer than 3 health probes");
    }

    [Fact]
    public async Task AcceptedCall_ThenNameNotFoundOnce_ThenAnswers_IsStillRunningAtTheLimit()
    {
        var component = ScriptedComponent.WithHealth(AdminReply.Accepted, AdminReply.HostNotFound, AdminReply.Ok);

        var (outcome, elapsed) = await ShutdownAsync(component, ShutdownOptions(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal("POST 202; still answering at 1 s (last 200)", WithoutMilliseconds(outcome.Detail));
        Assert.True(component.HealthProbes >= 2, "fewer than 2 health probes");
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
        Assert.StartsWith("POST 202; not refused by 2 s (last ", WithoutMilliseconds(outcome.Detail), StringComparison.Ordinal);
        Assert.True(component.HealthProbes >= 3, "fewer than 3 health probes");
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
        Assert.Equal($"POST 202; not refused by 1 s (last {last})", WithoutMilliseconds(outcome.Detail));
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
        Assert.Equal("POST 202; not refused by 1 s (last name lookup failed)", WithoutMilliseconds(outcome.Detail));
        Assert.DoesNotContain("timeout", WithoutMilliseconds(outcome.Detail), StringComparison.Ordinal);
        Assert.True(component.HealthProbes >= 2, "fewer than 2 health probes");
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
            Assert.Equal($"{postText}; refused after {ElapsedToken} ms {note}", WithoutMilliseconds(outcome.Detail));
            Assert.True(
                component.Requests.SequenceEqual([ShutdownRequest, HealthRequest]),
                "the requests are the shutdown call and the one refused health probe, in that order");
        }
        else
        {
            Assert.Equal($"{postText}; not refused by 3 s (last host not found) {note}", WithoutMilliseconds(outcome.Detail));
            Assert.True(component.HealthProbes >= 2, "fewer than 2 health probes");
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

            // A request head names the listener's port in its Host header, so it is never shown.
            Assert.True(
                requestHead.StartsWith(ShutdownRequest + " HTTP/1.1\r\n", StringComparison.Ordinal),
                "the request the listener answered is the shutdown call");
            Assert.True(outcome.Stopped, WithoutMilliseconds(outcome.Detail));
            Assert.Null(outcome.ToFailureItem());
            Assert.Equal($"POST 202; refused after {ElapsedToken} ms", WithoutMilliseconds(outcome.Detail));
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

            Assert.True(outcome.Stopped, WithoutMilliseconds(outcome.Detail));
            Assert.Null(outcome.ToFailureItem());
            Assert.Equal($"POST 202; refused after {ElapsedToken} ms", WithoutMilliseconds(outcome.Detail));
            Assert.True(heads.Count >= 2, "fewer than 2 requests answered; no health probe before the stop");

            // A request head names the listener's port in its Host header, so it is never shown.
            Assert.True(
                heads[0].StartsWith(ShutdownRequest + " HTTP/1.1\r\n", StringComparison.Ordinal),
                "the first request answered is the shutdown call");
            Assert.True(
                heads.Skip(1).All(head => head.StartsWith(HealthRequest + " HTTP/1.1\r\n", StringComparison.Ordinal)),
                "every request answered after the shutdown call is a health probe");
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
            Assert.Equal("POST 202; still answering at 2 s (last 200)", WithoutMilliseconds(outcome.Detail));

            // A request head names the listener's port in its Host header, and the number of probes depends
            // on timing, so neither is shown.
            Assert.True(
                heads.Count(head => head.StartsWith(ShutdownRequest + " HTTP/1.1\r\n", StringComparison.Ordinal)) == 1,
                "exactly one shutdown call reached the listener");
            var probes = heads.Count(head => head.StartsWith(HealthRequest + " HTTP/1.1\r\n", StringComparison.Ordinal));
            Assert.True(probes >= 20, "fewer than 20 health probes: no fast burst after the watch ended");
            var watches = heads.Count(head => head.Length == 0);
            Assert.True(watches >= 2, "fewer than 2 watch connections: none opened again after the burst");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HarnessOptions ShutdownOptions(int shutdownSeconds, int requestSeconds = 1) => new()
    {
        ShutdownTimeoutSeconds = shutdownSeconds,
        ShutdownRequestTimeoutSeconds = requestSeconds,
    };

    // The text with every measured time replaced by ElapsedToken, which no client prints, so a detail compared
    // with a literal that holds no measured time is equal exactly when the raw detail is.
    private static string WithoutMilliseconds(string text) => MeasuredMilliseconds.Replace(text, ElapsedToken + " ms");

    private static void AssertStillRunning(ShutdownOutcome outcome, TimeSpan elapsed, int limitSeconds)
    {
        Assert.False(outcome.Stopped, WithoutMilliseconds(outcome.Detail));
        var item = outcome.ToFailureItem();
        Assert.NotNull(item);
        Assert.Equal(StillRunningItemId, item.ItemId);
        Assert.Equal(StillRunningNature, item.Nature);
        Assert.True(
            string.Equals(outcome.Detail, item.Detail, StringComparison.Ordinal),
            "the CHK-SHUTDOWN item carries the outcome's detail");
        Assert.True(
            elapsed < TimeSpan.FromSeconds(limitSeconds) + Slack,
            $"the shutdown outlasted its {limitSeconds} s limit plus slack");
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

    // ---- Oversized admin bodies ----------------------------------------------------------------

    // An /admin/info body one byte over the admin limit of a mebibyte is no admin answer, whether its length is
    // declared or it is sent without one: it is the failure oversized, never parsed or truncated, so the reset
    // records the info read after it as mismatch:http:info-oversized, and a plain read reports oversized.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedInfoBody_IsTheFailureOversized(bool declaresLength)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => OversizedInfo(declaresLength));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch("http", "info-oversized"), item.Nature);
        Assert.Contains($"GET {StandInAdminUrl}{AdminRoutes.Info}: body exceeds the 1048576-byte admin limit", item.Detail, StringComparison.Ordinal);
        Assert.Null(result.After);

        var reader = new ScriptedAdmin().Then(InfoRequest, () => OversizedInfo(declaresLength));
        AdminInfoRead read;
        using (var reset = new StateReset(StateResetOptions(), reader, _console))
        {
            read = await reset.ReadInfoOutcomeAsync(StandInAdminUrl, TestCancellation);
        }

        Assert.Null(read.Info);
        Assert.Equal("oversized", read.Failure);
    }

    // A stand-in info object padded to one byte over the admin limit, which would parse were it read whole.
    private HttpResponseMessage OversizedInfo(bool declaresLength)
    {
        const int adminLimit = 1024 * 1024;
        var info = Info(PreviousGeneration + 1);
        var unpadded = Encoding.UTF8.GetByteCount(info.ToJsonString());
        info["pad"] = new string('x', adminLimit + 1 - unpadded - "\"pad\":\"\",".Length);
        var bytes = Encoding.UTF8.GetBytes(info.ToJsonString());
        Assert.Equal(adminLimit + 1, bytes.Length);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declaresLength ? new ByteArrayContent(bytes) : new UnsizedContent(bytes),
        };
    }

    /// <summary>Content that states no length, as a body sent without <c>Content-Length</c> does.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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
// not confirmed stops it after the bracket's seed restore, with no manifest; answered bodies over the
// kept-body limit stop it before the record of the body that exceeds it, with no manifest; and a confirmed capture
// publishes the confirmed seed facts in a manifest written after every other file. A served WSDL the
// facade would publish is stored in the baseline and written as the contract only when none exists; one
// it would refuse writes neither WSDL file and is recorded as not captured; and a contracts directory
// linked out of the repository fails the capture without writing outside. Each refusal or stop exits 1.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and capture's refusals and stops follow the reset confirmation covered above. The field
// names and the reset requests come from the state-reset members, ReferenceDate from the console-sink
// members, and the protected directory, its snapshot and the sentinel bytes from the confinement
// members.
public sealed partial class ReplayLifecycleTests
{
    private const string CaptureOtherReferenceDate = "2025-06-01";
    private const string OtherSeedSha256 = "34df80d4c1b0a5e2f7d8c9b0a1e2f3d4c5b6a7980f1e2d3c4b5a69788796a5b4";

    private const string ManifestFile = "baseline/manifest.json";
    private const string ManifestTemporaryFile = "baseline/.manifest.json.tmp";
    private const string ConformanceFile = "baseline/conformance.json";
    private const string ContractsWsdlFile = "contracts/wsdl/ErewhonWebService.wsdl";
    private const string BaselineWsdlFile = "baseline/wsdl/standin-ErewhonWebService.wsdl";

    // A WSDL the facade publishes: well-formed, with exactly one soap:address that carries a location.
    private const string PublishableWsdl = """
        <?xml version="1.0" encoding="utf-8"?>
        <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" name="ErewhonWebService">
          <wsdl:service name="ErewhonWebService">
            <wsdl:port name="WebServiceHTTP" binding="WebServiceHTTPBinding">
              <soap:address location="http://127.0.0.1:15001/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP" />
            </wsdl:port>
          </wsdl:service>
        </wsdl:definitions>

        """;
    private const string PublishableWsdlAddress =
        """<soap:address location="http://127.0.0.1:15001/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP" />""";

    private const string WsdlDropSuffix = "; baseline WSDL removed, contracts WSDL left as it is";

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
        AssertLastLineEndsWith($"seed facts cannot be confirmed (expected {unconfirmable}); {NotTouched}");
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
        AssertLastLineEndsWith($"{ResetPoints.CaptureStart} reset not confirmed; {NotTouched}");
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
        AssertLastLineEndsWith($"reset bracket of {emptyStateCase} not confirmed; capture stopped, baseline/manifest.json not written");
    }

    // The stub answers every case with the 2-byte body {}, so a kept-body limit of 5 bytes keeps the first two
    // bodies and one of 6 keeps three, the last of them exactly at the limit. The next answered body exceeds it
    // and stops the capture before its record is written: the cases before it keep their records, no later case
    // or WSDL fetch is sent, an earlier manifest stays withdrawn and the capture exits 1.
    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    public async Task KeptBodiesOverTheLimit_StopTheCaptureBeforeTheRecordThatExceedsIt_WithNoManifest(int keptBodyLimit, int stoppedAt)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, TestCancellation);
        var options = CaptureOptions(standIn);
        WriteManifest(options, CaptureSeedSha256, ReferenceDate);
        var matrix = CaseMatrix.Load(options.CaseMatrixPath, OperationCatalogue.Default);
        var stopped = matrix.Cases.Single(definition => matrix.PositionOf(definition.Id) == stoppedAt).Id;
        Assert.Equal(HarnessOptions.EvidenceByteLimit, new CaptureMode(options, OperationCatalogue.Default, _captureConsole).KeptBodyLimit);
        var capture = new CaptureMode(options, OperationCatalogue.Default, _captureConsole)
        {
            KeptBodyLimit = keptBodyLimit,
        };

        Assert.Equal(1, await capture.RunAsync(TestCancellation).WaitAsync(RunLimit, TestCancellation));

        Assert.False(File.Exists(Full(ManifestFile)));
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.False(File.Exists(Full(ConformanceFile)));
        Assert.False(File.Exists(Full(BaselineWsdlFile)));
        Assert.Equal(
            matrix.Cases.Take(stoppedAt - 1).Select(definition => matrix.PositionOf(definition.Id).ToString("D3", CultureInfo.InvariantCulture)),
            Directory.EnumerateFiles(Full("baseline/records"), "*", SearchOption.AllDirectories)
                .Select(file => Path.GetFileName(file)[..3])
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            stoppedAt,
            standIn.Requests.Count(request => !request.StartsWith("GET /health", StringComparison.Ordinal)
                && !request.StartsWith("GET /admin/", StringComparison.Ordinal)
                && !request.StartsWith("POST /admin/", StringComparison.Ordinal)));
        AssertLastLineEndsWith(string.Create(
            CultureInfo.InvariantCulture,
            $"{stoppedAt:D3}/{matrix.PlannedCaseCount:D3} {stopped}: the response bodies this capture keeps for its conformance check exceed the {keptBodyLimit}-byte limit; capture stopped, baseline/manifest.json not written"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCapture_PublishesTheConfirmedSeedFactsInTheManifestLast(bool recapture)
    {
        // A publishable WSDL, so the contracts WSDL is among the files the manifest must follow.
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, Encoding.UTF8.GetBytes(PublishableWsdl), TestCancellation);
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
        Assert.True(
            Lines().Contains("[jade-standin] baseline/manifest.json withdrawn until this capture completes", StringComparer.Ordinal),
            "the capture states that baseline/manifest.json is withdrawn until it completes");

        // Each file is named relative to the temporary root, so a failure reads the same in every run.
        var published = File.GetLastWriteTimeUtc(Full(ManifestFile));
        Assert.All(
            Directory.EnumerateFiles(Full("baseline"), "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Full("contracts"), "*", SearchOption.AllDirectories))
                .Where(file => file != Full(ManifestFile))
                .Select(Relative),
            relative => Assert.True(File.GetLastWriteTimeUtc(Full(relative)) <= published, relative));
    }

    // The baseline WSDL is the served bytes; the contracts WSDL is those bytes labelled, written only when absent,
    // so an existing contract keeps its bytes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishableWsdl_IsStoredInTheBaseline_AndWrittenAsTheContractOnlyWhenAbsent(bool contractPresent)
    {
        var served = Encoding.UTF8.GetBytes(PublishableWsdl);
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, served, TestCancellation);
        var options = CaptureOptions(standIn);
        if (contractPresent)
        {
            Directory.CreateDirectory(Full("contracts/wsdl"));
            File.WriteAllBytes(Full(ContractsWsdlFile), SentinelBytes);
        }

        Assert.Equal(0, await CaptureAsync(options));

        Assert.True(new BaselineStore(options).TryReadManifest(out var manifest));
        Assert.True(manifest.WsdlCaptured, "A publishable WSDL must be recorded as captured.");
        Assert.Equal(served, File.ReadAllBytes(Full(BaselineWsdlFile)));
        Assert.Equal(contractPresent ? SentinelBytes : CaptureMode.WithHeaderComment(served), File.ReadAllBytes(Full(ContractsWsdlFile)));
        Assert.Equal(["ErewhonWebService.wsdl"], ShownNames(Full("contracts/wsdl")));

        // The console names the temporary root and the stub's loopback port, so its lines are not shown.
        Assert.True(
            Lines().Contains(
                contractPresent ? "[jade-standin] contracts WSDL present; not overwritten" : "[jade-standin] wrote contracts WSDL",
                StringComparer.Ordinal),
            "the capture states that it wrote the contracts WSDL or kept the one present");
    }

    // Each body the facade would refuse to publish, with the reason the capture gives. A baseline WSDL from an
    // earlier capture is removed, so neither file records the body.
    [Theory]
    [InlineData("truncated", "not well-formed XML")]
    [InlineData("dtd", "not well-formed XML")]
    [InlineData("two-soap-addresses", "expected exactly one soap:address, found 2")]
    [InlineData("no-soap-address", "expected exactly one soap:address, found 0")]
    [InlineData("no-location", "soap:address has no location attribute")]
    [InlineData("invalid-utf8", "not UTF-8")]
    [InlineData("utf16", "not UTF-8")]
    public async Task UnpublishableWsdl_IsNotCaptured_AndNeitherWsdlFileIsWritten(string form, string problem)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, UnpublishableWsdl(form), TestCancellation);
        var options = CaptureOptions(standIn);
        Directory.CreateDirectory(Full("baseline/wsdl"));
        File.WriteAllBytes(Full(BaselineWsdlFile), Encoding.UTF8.GetBytes(PublishableWsdl));

        Assert.Equal(0, await CaptureAsync(options));

        Assert.True(new BaselineStore(options).TryReadManifest(out var manifest));
        Assert.False(manifest.WsdlCaptured, "A WSDL the facade would not publish must not be recorded as captured.");
        Assert.False(File.Exists(Full(BaselineWsdlFile)), "A WSDL the facade would not publish must leave no baseline WSDL.");
        Assert.False(Directory.Exists(Full("contracts")), "A WSDL the facade would not publish must write no contracts WSDL.");

        // The console names the temporary root and the stub's loopback port, so its lines are not shown.
        Assert.True(
            Lines().Contains($"[jade-standin] WARN WSDL not captured: the facade would not publish it ({problem}){WsdlDropSuffix}", StringComparer.Ordinal),
            "the capture warns that the WSDL is not captured, with the form's reason");
    }

    // A contracts directory linked out of the repository is refused like a linked baseline directory: the
    // capture exits 1 with no manifest, and nothing outside changes. The protected directory, its snapshot and
    // the refusal check come from the confinement members.
    [Theory]
    [InlineData("contracts")]
    [InlineData("contracts/wsdl")]
    public async Task ContractsWsdl_ThroughLinkedAncestor_FailsTheCaptureAndWritesNothingOutside(string linkedDirectory)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            CaptureSeedSha256, ReferenceDate, failEmptyReset: false, Encoding.UTF8.GetBytes(PublishableWsdl), TestCancellation);
        var options = CaptureOptions(standIn);
        ProtectedTree();
        Directory.CreateDirectory(Path.GetDirectoryName(Full(linkedDirectory))!);
        Directory.CreateSymbolicLink(Full(linkedDirectory), ConfinedOutside);
        var before = ConfinedSnapshot(ConfinedOutside);

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.False(File.Exists(Full(ManifestFile)), "A refused contracts WSDL must leave no manifest.");
        Assert.NotNull(new FileInfo(Full(linkedDirectory)).LinkTarget);

        // The console names the temporary root and the stub's loopback port, so the last line is not shown.
        Assert.True(
            LastLine().StartsWith("[jade-standin] capture failed: System.IO.IOException: ", StringComparison.Ordinal),
            "the capture's last console line states that it failed with an IOException");
        Assert.True(
            LastLine().Contains(
                $"{LinkRefusal}: refused '{ContractsWsdlFile}', because '{linkedDirectory}' is a symbolic link.",
                StringComparison.Ordinal),
            "the capture's last console line names the refused contracts WSDL and its linked directory");
    }

    // The publishable WSDL broken in one way the facade's publication rule refuses.
    private static byte[] UnpublishableWsdl(string form) => form switch
    {
        "truncated" => Encoding.UTF8.GetBytes(PublishableWsdl[..(PublishableWsdl.Length / 2)]),
        "dtd" => Encoding.UTF8.GetBytes(PublishableWsdl.Replace(
            "?>\n", "?>\n<!DOCTYPE wsdl:definitions [<!ENTITY e \"e\">]>\n", StringComparison.Ordinal)),
        "two-soap-addresses" => Encoding.UTF8.GetBytes(PublishableWsdl.Replace(
            PublishableWsdlAddress, PublishableWsdlAddress + PublishableWsdlAddress, StringComparison.Ordinal)),
        "no-soap-address" => Encoding.UTF8.GetBytes(PublishableWsdl.Replace(PublishableWsdlAddress, "", StringComparison.Ordinal)),
        "no-location" => Encoding.UTF8.GetBytes(PublishableWsdl.Replace(PublishableWsdlAddress, "<soap:address />", StringComparison.Ordinal)),
        "invalid-utf8" => [.. Encoding.UTF8.GetBytes(PublishableWsdl), 0xFF],
        "utf16" => [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(PublishableWsdl.Replace("utf-8", "utf-16", StringComparison.Ordinal))],
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown WSDL form."),
    };

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

    // The console names the temporary root and the stub's loopback port, so the last line is not shown.
    private void AssertLastLineEndsWith(string expectedEnd) =>
        Assert.True(
            LastLine().EndsWith(expectedEnd, StringComparison.Ordinal),
            $"the capture's last console line does not end with '{expectedEnd}'");

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

// ---- Empty-state bracket under cancellation -----------------------------------------------------
// The empty-state bracket under cancellation, for the real ReplayBody and the real CaptureMode: once
// POST /admin/reset?state=empty has been sent for the getCompany empty-state case, a run cancelled while
// that reset's confirmation read or the case's own request is outstanding still sends
// POST /admin/reset?state=seed and has it confirmed, and the cancellation still ends the run: replay's
// body throws it, capture exits 1, and each reset it confirms is printed on the writer the mode was given.
// A run cancelled while that seed reset itself is outstanding abandons it
// at the cancelled-run limit, records that it did and still ends. Each run works in a temporary repository
// root of its own holding copies of the committed case matrix and harness settings (and, for replay, the
// committed empty-state record), against one in-process loopback stub that serves the stand-in's admin
// endpoints and answers every other request at once with 404.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and the bracket is the reset that replay and capture run around their one empty-state case.
// The admin requests come from the shutdown and state-reset members, ReferenceDate from the console-sink
// members, FindRepoRoot from the exit-path members, Slack from the shutdown members and TimerTolerance
// from the signal-cancellation members.
public sealed partial class ReplayLifecycleTests
{
    private const string EmptyStateCaseId = "REST-getCompany-03";
    private const string EmptyStateRecordFile = "baseline/records/rest/056-getCompany-03.json";
    private const string HarnessConfigFile = "harness/config/harness.json";
    private const string BracketSeedDirectoryName = "seed";
    private const string EmptyStateRequest = "GET /company";
    private const string SeedRestorationLabel = "seed restoration after " + EmptyStateCaseId;

    // A passing test cancels the run as soon as the stub holds the hung request, so neither the request
    // limit nor the hang limit is reached; both only bound a failing run.
    private const int BracketRequestLimitSeconds = 20;
    private const int BracketTestTimeoutMilliseconds = 120_000;
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(90);

    // The cancelled-run limit of the held-restoration test: far below the request limit, so a restoration the
    // cancellation did not bound would outlast the test's bound on the run.
    private const int ShortRestorationLimitSeconds = 2;
    private static readonly TimeSpan ShortRestorationLimit = TimeSpan.FromSeconds(ShortRestorationLimitSeconds);

    // Created on first use, so only the bracket tests touch the file system; removed in Dispose.
    private TempDirectory? _bracketRoot;
    private string? _bracketSeedSha256;

    /// <summary>The request the stub holds unanswered, inside the empty-state bracket, until the run is cancelled.</summary>
    public enum HangPoint
    {
        /// <summary>The <c>GET /admin/info</c> that confirms the empty reset, sent after the reset itself.</summary>
        EmptyResetConfirmation,

        /// <summary>The case's own <c>GET /company</c> against the empty state.</summary>
        CaseRequest,

        /// <summary>The <c>POST /admin/reset?state=seed</c> that restores the seed state after the case.</summary>
        SeedRestoration,
    }

    /// <summary>The mode whose empty-state bracket a test runs.</summary>
    public enum BracketMode
    {
        /// <summary>The real <see cref="ReplayBody"/>, whose cancellation is thrown.</summary>
        Replay,

        /// <summary>The real <see cref="CaptureMode"/>, which exits 1.</summary>
        Capture,
    }

    /// <summary>
    /// The bracket tests' temporary repository root, created on first use with copies of the committed case
    /// matrix and harness settings and a one-file seed directory, whose fingerprint
    /// <see cref="BracketSeedSha256"/> holds and the stub reports, so a first capture confirms every reset.
    /// </summary>
    private TempDirectory BracketRoot
    {
        get
        {
            if (_bracketRoot is null)
            {
                _bracketRoot = new TempDirectory();
                CopyCommitted(_bracketRoot, CaseMatrixPath);
                CopyCommitted(_bracketRoot, HarnessConfigFile);
                _bracketRoot.Write($"{BracketSeedDirectoryName}/Company.txt", "Erewhon Investments Inc\tErewhon House\n");
                _bracketSeedSha256 = SeedFingerprint.ComputeSha256Hex(_bracketRoot.PathOf(BracketSeedDirectoryName));
            }

            return _bracketRoot;
        }
    }

    /// <summary>The fingerprint of the seed directory below <see cref="BracketRoot"/>, which it creates on first use.</summary>
    private string BracketSeedSha256
    {
        get
        {
            // Reading the root runs its setup, which fingerprints the seed directory.
            _ = BracketRoot;
            return _bracketSeedSha256
                ?? throw new InvalidOperationException("The bracket root was left without a seed fingerprint by a failed setup.");
        }
    }

    [Theory(Timeout = BracketTestTimeoutMilliseconds)]
    [InlineData(HangPoint.EmptyResetConfirmation)]
    [InlineData(HangPoint.CaseRequest)]
    public async Task Replay_CancelledInsideTheEmptyStateBracket_RestoresTheSeedStateAndPropagatesTheCancellation(HangPoint hang)
    {
        CopyCommitted(BracketRoot, EmptyStateRecordFile);
        await using var stub = await BracketStandInStub.StartAsync(hang, BracketSeedSha256, TestContext.Current.CancellationToken);
        var state = new ReplayRunState();
        using var console = new StringWriter(CultureInfo.InvariantCulture);

        // Readiness writes from two probes at once.
        using var output = TextWriter.Synchronized(console);
        using var run = new CancellationTokenSource();

        var execution = new ReplayBody(BracketOptions(stub.BaseUrl), OperationCatalogue.Default, KnownLimitationsRegister.Empty, output)
            .ExecuteAsync(state, run.Token);
        var held = await HoldsHungRequestAsync(stub, execution);
        await run.CancelAsync();
        var outcome = await Record.ExceptionAsync(() => execution);

        Assert.True(held, "the run ended before the stub held the request inside the empty-state bracket");
        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        AssertSeedRestoredAfterEmptyReset(stub.Requests);
        var restoreItem = FailureItemIds.Reset(ResetPoints.After(EmptyStateCaseId));
        Assert.False(
            state.Failures.Any(item => string.Equals(item.ItemId, restoreItem, StringComparison.Ordinal)),
            "the seed restoration after the empty-state case was not confirmed");
        Assert.False(
            state.Failures.Any(item => item.Detail?.StartsWith(SeedRestorationLabel, StringComparison.Ordinal) == true),
            "the seed restoration after the empty-state case threw");
        AssertResetLinesOnModeWriter(
            console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ResetPoints.ReplayStart,
            hang);
    }

    [Theory(Timeout = BracketTestTimeoutMilliseconds)]
    [InlineData(HangPoint.EmptyResetConfirmation)]
    [InlineData(HangPoint.CaseRequest)]
    public async Task Capture_CancelledInsideTheEmptyStateBracket_RestoresTheSeedStateAndExitsOne(HangPoint hang)
    {
        await using var stub = await BracketStandInStub.StartAsync(hang, BracketSeedSha256, TestContext.Current.CancellationToken);
        using var console = new StringWriter(CultureInfo.InvariantCulture);

        // Readiness writes from two probes at once.
        using var output = TextWriter.Synchronized(console);
        using var run = new CancellationTokenSource();

        var capture = new CaptureMode(BracketOptions(stub.BaseUrl), OperationCatalogue.Default, output).RunAsync(run.Token);
        var held = await HoldsHungRequestAsync(stub, capture);
        await run.CancelAsync();
        var exitCode = await capture;

        Assert.True(held, "the capture ended before the stub held the request inside the empty-state bracket");
        Assert.Equal(1, exitCode);
        AssertSeedRestoredAfterEmptyReset(stub.Requests);
        var lines = console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(
            lines.Any(line => line.StartsWith($"{SourceLabels.ConsolePrefix} capture failed: ", StringComparison.Ordinal)
                              && line.Contains("CanceledException", StringComparison.Ordinal)),
            "the capture did not end with the cancellation");
        var restoreItem = FailureItemIds.Reset(ResetPoints.After(EmptyStateCaseId));
        Assert.False(
            lines.Any(line => line.Contains(restoreItem, StringComparison.Ordinal)),
            "the seed restoration after the empty-state case was not confirmed");
        Assert.False(
            lines.Any(line => line.Contains(SeedRestorationLabel, StringComparison.Ordinal)),
            "the seed restoration after the empty-state case threw");
        AssertResetLinesOnModeWriter(lines, ResetPoints.CaptureStart, hang);
    }

    // The seed restoration itself is held: the stub never answers the POST /admin/reset?state=seed that follows
    // the empty-state case, and the run is cancelled while it waits. The restoration still runs after the
    // cancellation, but only for the cancelled-run limit (15 s, shortened here), so the run ends at that limit
    // rather than at the request limit, its cancellation still ends it (replay's body throws it, capture exits 1
    // and writes no manifest) and the abandoned restoration is recorded.
    [Theory(Timeout = BracketTestTimeoutMilliseconds)]
    [InlineData(BracketMode.Replay)]
    [InlineData(BracketMode.Capture)]
    public async Task CancelledWhileTheSeedRestorationIsHeld_AbandonsItAtTheLimit_AndTheRunStillEnds(BracketMode mode)
    {
        if (mode == BracketMode.Replay)
        {
            CopyCommitted(BracketRoot, EmptyStateRecordFile);
        }

        await using var stub = await BracketStandInStub.StartAsync(HangPoint.SeedRestoration, BracketSeedSha256, TestContext.Current.CancellationToken);
        var options = BracketOptions(stub.BaseUrl);
        var state = new ReplayRunState();
        using var console = new StringWriter(CultureInfo.InvariantCulture);

        // Readiness writes from two probes at once.
        using var output = TextWriter.Synchronized(console);
        using var run = new CancellationTokenSource();

        var productionLimit = TimeSpan.FromSeconds(ReplayMode.CancelledRestorationLimitSeconds);
        Task execution;
        if (mode == BracketMode.Replay)
        {
            Assert.Equal(productionLimit, new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, output).CancelledRestorationLimit);
            execution = new ReplayBody(options, OperationCatalogue.Default, KnownLimitationsRegister.Empty, output)
            {
                CancelledRestorationLimit = ShortRestorationLimit,
            }.ExecuteAsync(state, run.Token);
        }
        else
        {
            Assert.Equal(productionLimit, new CaptureMode(options, OperationCatalogue.Default, output).CancelledRestorationLimit);
            execution = new CaptureMode(options, OperationCatalogue.Default, output)
            {
                CancelledRestorationLimit = ShortRestorationLimit,
            }.RunAsync(run.Token);
        }

        var held = await HoldsHungRequestAsync(stub, execution);
        var cancelledAt = Stopwatch.GetTimestamp();
        await run.CancelAsync();
        var outcome = await Record.ExceptionAsync(() => execution);
        var sinceCancellation = Stopwatch.GetElapsedTime(cancelledAt);

        Assert.True(held, "the run ended before the stub held the seed restoration");
        Assert.True(sinceCancellation >= ShortRestorationLimit - TimerTolerance, "the seed restoration was abandoned before its limit");
        Assert.True(sinceCancellation < ShortRestorationLimit + Slack, "the seed restoration outlasted its limit");

        // The bracket as the stub saw it: one empty reset, then the seed reset it held, and nothing after it.
        var requests = stub.Requests;
        var empty = IndexOf(requests, EmptyResetRequest, start: 0);
        Assert.True(empty >= 0, "the stub received no empty reset");
        var seed = IndexOf(requests, SeedResetRequest, empty + 1);
        Assert.True(seed > empty, "the stub received no seed reset after the empty reset");
        Assert.True(requests.Count <= seed + 1, "a request followed the abandoned seed restoration");

        // The console and a failed step's detail can name the temporary root and the stub's loopback port, so
        // they are checked with fixed messages.
        var lines = console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mode == BracketMode.Replay)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(outcome);
            var abandoned = $"{SeedRestorationLabel} was abandoned {ShortRestorationLimitSeconds} s after the run was cancelled";
            var item = Assert.Single(state.Failures, item => item.Detail?.StartsWith(SeedRestorationLabel, StringComparison.Ordinal) == true);
            Assert.Equal(FailureItemIds.Exception, item.ItemId);
            Assert.True(
                string.Equals(abandoned, item.Detail, StringComparison.Ordinal),
                "the CHK-EXCEPTION item does not state that the seed restoration was abandoned at the cancelled-run limit");
            Assert.True(
                lines.Contains($"replay: {abandoned}", StringComparer.Ordinal),
                "the console does not state that the seed restoration was abandoned at the cancelled-run limit");
            var restoreItem = FailureItemIds.Reset(ResetPoints.After(EmptyStateCaseId));
            Assert.False(
                state.Failures.Any(failure => string.Equals(failure.ItemId, restoreItem, StringComparison.Ordinal)),
                "the abandoned seed restoration was also recorded as a reset mismatch");
        }
        else
        {
            // An exception's text can name the root or the port, so only its type is shown.
            Assert.True(outcome is null, $"the capture threw {outcome?.GetType().FullName} instead of exiting 1");
            Assert.Equal(1, await (Task<int>)execution);
            Assert.True(
                lines.Contains(
                    $"{SourceLabels.ConsolePrefix} WARN {SeedRestorationLabel} was abandoned {ShortRestorationLimitSeconds} s after the capture was cancelled",
                    StringComparer.Ordinal),
                "the console does not warn that the seed restoration was abandoned at the cancelled-run limit");
            Assert.True(
                lines.Any(line => line.StartsWith($"{SourceLabels.ConsolePrefix} reset bracket of {EmptyStateCaseId} not confirmed", StringComparison.Ordinal)),
                "the capture did not stop at the unconfirmed bracket");
            Assert.False(File.Exists(BracketRoot.PathOf("baseline/manifest.json")), "the capture wrote a manifest after an abandoned seed restoration");
        }
    }

    // The bracket's order as the stub saw it: one empty reset, then a seed reset, then the read that
    // confirms it.
    private static void AssertSeedRestoredAfterEmptyReset(IReadOnlyList<string> requests)
    {
        var empty = IndexOf(requests, EmptyResetRequest, start: 0);
        Assert.True(empty >= 0, "the stub received no empty reset");
        Assert.True(IndexOf(requests, EmptyResetRequest, empty + 1) < 0, "the stub received more than one empty reset");

        var seed = IndexOf(requests, SeedResetRequest, empty + 1);
        Assert.True(seed > empty, "the stub received no seed reset after the empty reset");
        Assert.True(
            IndexOf(requests, InfoRequest, seed + 1) > seed,
            "the seed reset after the empty reset was not followed by its confirmation read");
    }

    // The reset lines of a bracket whose seed restoration was confirmed, on the writer the mode was given: the
    // start reset, the empty reset when the stub answered its confirmation, then the seed restoration, each
    // with the generation the stub reached and no other reset line among them.
    private static void AssertResetLinesOnModeWriter(string[] lines, string startPoint, HangPoint hang)
    {
        var expected = new List<string> { ResetLine(AdminRoutes.ResetStateSeed, startPoint, 1) };
        if (hang == HangPoint.CaseRequest)
        {
            expected.Add(ResetLine(AdminRoutes.ResetStateEmpty, ResetPoints.Before(EmptyStateCaseId), 2));
        }

        expected.Add(ResetLine(AdminRoutes.ResetStateSeed, ResetPoints.After(EmptyStateCaseId), 3));
        Assert.Equal(expected, lines.Where(line => line.StartsWith($"{SourceLabels.ConsolePrefix} reset ", StringComparison.Ordinal)));

        static string ResetLine(string state, string point, int generation) => string.Create(
            CultureInfo.InvariantCulture,
            $"{SourceLabels.ConsolePrefix} reset {state} at {point}: generation {generation}");
    }

    private static int IndexOf(IReadOnlyList<string> requests, string request, int start)
    {
        for (var index = start; index < requests.Count; index++)
        {
            if (string.Equals(requests[index], request, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    // True once the stub holds the hung request; false when the run ended first or the hang never came
    // within the limit, so the caller cancels the run before it fails the test.
    private static async Task<bool> HoldsHungRequestAsync(BracketStandInStub stub, Task run)
    {
        try
        {
            var first = await Task.WhenAny(stub.HangReached, run).WaitAsync(HangLimit, TestContext.Current.CancellationToken);
            return first == stub.HangReached;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // The committed settings, with the stub as every listener of both the facade and the backend, the
    // temporary root as the repository and a request limit far below the test's own.
    private HarnessOptions BracketOptions(string baseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(BracketRoot.PathOf(HarnessConfigFile), optional: false, reloadOnChange: false)
            .Build();
        HarnessOptions options;
        try
        {
            options = configuration.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>()
                ?? throw new InvalidDataException($"{HarnessConfigFile} has no {HarnessOptions.SectionName} section");
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }

        options.RepoRoot = BracketRoot.Root;
        options.SeedDirectory = BracketSeedDirectoryName;
        options.RequestTimeoutSeconds = BracketRequestLimitSeconds;
        foreach (var endpoints in new[] { options.Target, options.Backend })
        {
            endpoints.RestBaseUrl = baseUrl;
            endpoints.SoapBaseUrl = baseUrl;
            endpoints.AdminUrl = baseUrl;
        }

        return options;
    }

    private static void CopyCommitted(TempDirectory root, string relativePath)
    {
        var source = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var target = root.PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    /// <summary>
    /// An in-process HTTP/1.1 stand-in on 127.0.0.1 and an ephemeral port. <c>GET /health</c> answers 200;
    /// <c>POST /admin/reset?state=seed|empty</c> raises the generation and switches the state;
    /// <c>GET /admin/info</c> reports the stand-in source, the seed fingerprint, the reference date, the
    /// generation and no mutations. Inside the empty state it holds the one request its
    /// <see cref="HangPoint"/> names unanswered until the client gives up or the stub stops; every other
    /// request target gets 404 and no body. Every request is recorded as <c>&lt;method&gt; &lt;raw target&gt;</c>
    /// on arrival.
    /// </summary>
    private sealed class BracketStandInStub : IAsyncDisposable
    {
        // Bounds the graceful stop; a held request is released by the stopping token at once.
        private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

        private readonly ConcurrentQueue<string> _requests = new();
        private readonly TaskCompletionSource _hangReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly HangPoint _hang;
        private readonly string _seedSha256;
        private WebApplication? _app;
        private long _generation;
        private bool _empty;
        private bool _held;

        private BracketStandInStub(HangPoint hang, string seedSha256)
        {
            _hang = hang;
            _seedSha256 = seedSha256;
        }

        /// <summary>The stub's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
        public string BaseUrl { get; private set; } = "";

        /// <summary>Every request received so far, in arrival order.</summary>
        public IReadOnlyList<string> Requests => [.. _requests];

        /// <summary>Completes when the stub starts holding the hung request.</summary>
        public Task HangReached => _hangReached.Task;

        /// <summary>Starts a stub that holds the request <paramref name="hang"/> names.</summary>
        public static async Task<BracketStandInStub> StartAsync(HangPoint hang, string seedSha256, CancellationToken ct)
        {
            var stub = new BracketStandInStub(hang, seedSha256);
            var builder = LoopbackHost.CreateBuilder(ShutdownLimit);

            var app = builder.Build();
            app.Run(stub.HandleAsync);
            try
            {
                await app.StartAsync(ct);
                stub.BaseUrl = LoopbackHost.BoundAddress(app).GetLeftPart(UriPartial.Authority);
                stub._app = app;
                return stub;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <summary>Stops the server, releasing any held request, and frees its port.</summary>
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

            switch (request)
            {
                case HealthRequest:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;

                // Held before it changes the state, so the stub stays empty, as a stand-in that never
                // answers the restoration would.
                case SeedResetRequest when TakeHang(HangPoint.SeedRestoration):
                    await HoldAsync(context);
                    return;

                case EmptyResetRequest or SeedResetRequest:
                    lock (_gate)
                    {
                        _generation++;
                        _empty = string.Equals(request, EmptyResetRequest, StringComparison.Ordinal);
                    }

                    await WriteJsonAsync(context, "{}");
                    return;

                case InfoRequest:
                    if (TakeHang(HangPoint.EmptyResetConfirmation))
                    {
                        await HoldAsync(context);
                        return;
                    }

                    await WriteJsonAsync(context, Info());
                    return;

                case EmptyStateRequest when TakeHang(HangPoint.CaseRequest):
                    await HoldAsync(context);
                    return;

                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
            }
        }

        // The named request is held once, and only inside the empty state.
        private bool TakeHang(HangPoint point)
        {
            lock (_gate)
            {
                if (_hang != point || !_empty || _held)
                {
                    return false;
                }

                _held = true;
                return true;
            }
        }

        private string Info()
        {
            long generation;
            lock (_gate)
            {
                generation = _generation;
            }

            return new JsonObject
            {
                ["source"] = SourceLabels.StandIn,
                ["seedSha256"] = _seedSha256,
                ["referenceDate"] = ReferenceDate,
                ["generation"] = generation,
                ["mutations"] = 0,
            }.ToJsonString();
        }

        // Nothing is answered: the client's cancellation aborts the request, or the stub's stop releases it.
        private async Task HoldAsync(HttpContext context)
        {
            _hangReached.TrySetResult();
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var release = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            try
            {
                await Task.Delay(Timeout.Infinite, release.Token);
            }
            catch (OperationCanceledException) when (release.IsCancellationRequested)
            {
                // Released: the held request ends without an answer.
            }
        }

        private static async Task WriteJsonAsync(HttpContext context, string json)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(json, context.RequestAborted);
        }
    }
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

    // A 64-digit fingerprint and an email address, placed where /admin/info and the seed-token assertions
    // put observed values.
    private const string ConsoleSha = "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789abcdef";
    private const string ConsoleEmail = "brian.private@example.org";

    // An XML local name a SOAP response can put in a collection, where a wrong-type nature names it.
    private const string ConsolePrivateName = "Jane_Doe_private";

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

    // CR, LF, NUL, ESC, DEL, NEL, the line and paragraph separators, the Arabic letter mark and the other
    // bidirectional marks, an embedding, an override and isolates, and lone surrogates of both halves.
    [Theory]
    [InlineData(0x000D)]
    [InlineData(0x000A)]
    [InlineData(0x0000)]
    [InlineData(0x001B)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x061C)]
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
    public async Task InjectedSource_PrintsOneRedactedLine_AndTheFailureItemKeepsTheRawValue()
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
                + string.Create(CultureInfo.InvariantCulture, $"mismatch source observed <redacted: {raw.Length} chars>"),
            },
            PrintedLines());
    }

    // ---- Failure display ------------------------------------------------------------------------

    [Theory]
    // differs: every observed value but the empty one and the absent token is redacted, ESC included.
    [InlineData("differs:\"" + ConsoleEmail + "\"", "differs:<redacted: 27 chars>")]
    [InlineData("differs:\"\u001B[31mX\"", "differs:<redacted: 8 chars>")]
    [InlineData("differs:\"\"", "differs:<redacted: 2 chars>")]
    [InlineData("differs:7", "differs:<redacted: 1 char>")]
    [InlineData("differs:absent", "differs:absent")]
    [InlineData("differs:", "differs:")]
    // mismatch: the reset's own tokens and the strict form of each field are shown.
    [InlineData("mismatch:http:connect", "mismatch:http:connect")]
    [InlineData("mismatch:http:503", "mismatch:http:503")]
    [InlineData("mismatch:http:invalid-url", "mismatch:http:invalid-url")]
    [InlineData("mismatch:http:info-invalid", "mismatch:http:info-invalid")]
    [InlineData("mismatch:http:info-404", "mismatch:http:info-404")]
    [InlineData("mismatch:http:oversized", "mismatch:http:oversized")]
    [InlineData("mismatch:http:info-oversized", "mismatch:http:info-oversized")]
    [InlineData("mismatch:generation:before-oversized", "mismatch:generation:before-oversized")]
    [InlineData("mismatch:generation:before-null", "mismatch:generation:before-null")]
    [InlineData("mismatch:generation:before-timeout", "mismatch:generation:before-timeout")]
    [InlineData("mismatch:generation:5", "mismatch:generation:5")]
    [InlineData("mismatch:mutations:-3", "mismatch:mutations:-3")]
    [InlineData("mismatch:source:null", "mismatch:source:null")]
    [InlineData("mismatch:source:", "mismatch:source:")]
    [InlineData("mismatch:seedSha256:" + ConsoleSha, "mismatch:seedSha256:" + ConsoleSha)]
    [InlineData("mismatch:referenceDate:2026-01-01", "mismatch:referenceDate:2026-01-01")]
    // mismatch: anything else is redacted, including a value of another field's form.
    [InlineData("mismatch:source:jade-standin-2", "mismatch:source:<redacted: 14 chars>")]
    [InlineData("mismatch:source:Hank " + ConsoleEmail + "\u001B[31m\u202ERED", "mismatch:source:<redacted: 39 chars>")]
    [InlineData("mismatch:seedSha256:" + ConsoleEmail, "mismatch:seedSha256:<redacted: 25 chars>")]
    [InlineData("mismatch:seedSha256: " + ConsoleSha, "mismatch:seedSha256:<redacted: 65 chars>")]
    [InlineData("mismatch:seedSha256:2026-01-01", "mismatch:seedSha256:<redacted: 10 chars>")]
    [InlineData("mismatch:referenceDate:2026-01-01\u001B]0;x\u0007", "mismatch:referenceDate:<redacted: 16 chars>")]
    [InlineData("mismatch:referenceDate:2026-1-01", "mismatch:referenceDate:<redacted: 9 chars>")]
    [InlineData("mismatch:generation:12a", "mismatch:generation:<redacted: 3 chars>")]
    [InlineData("mismatch:mutations:\u0663", "mismatch:mutations:<redacted: 1 char>")]
    [InlineData("mismatch:http:info-" + ConsoleEmail, "mismatch:http:<redacted: 30 chars>")]
    [InlineData("mismatch:http:before-5031", "mismatch:http:<redacted: 11 chars>")]
    [InlineData("mismatch:" + ConsoleEmail + ":connect", "mismatch:<redacted: 33 chars>")]
    [InlineData("mismatch:nofield", "mismatch:<redacted: 7 chars>")]
    // wrong-type: every wire-type token the conformance check writes is shown, behind any item: prefixes.
    [InlineData("wrong-type:object", "wrong-type:object")]
    [InlineData("wrong-type:array", "wrong-type:array")]
    [InlineData("wrong-type:string", "wrong-type:string")]
    [InlineData("wrong-type:number", "wrong-type:number")]
    [InlineData("wrong-type:boolean", "wrong-type:boolean")]
    [InlineData("wrong-type:element", "wrong-type:element")]
    [InlineData("wrong-type:text", "wrong-type:text")]
    [InlineData("wrong-type:item:element", "wrong-type:item:element")]
    [InlineData("wrong-type:item:item:text", "wrong-type:item:item:text")]
    [InlineData("wrong-type:xsi-type:unresolved", "wrong-type:xsi-type:unresolved")]
    [InlineData("wrong-type:item:xsi-type:unresolved", "wrong-type:item:xsi-type:unresolved")]
    // wrong-type: an XML item element's name and an xsi:type name are redacted and their prefixes kept;
    // any other token is redacted as a whole, spellings that differ only in case, empty tokens and control
    // characters included.
    [InlineData("wrong-type:xsi-type:{urn:other}" + ConsolePrivateName, "wrong-type:xsi-type:<redacted: 27 chars>")]
    [InlineData("wrong-type:item:xsi-type:{urn:other}" + ConsolePrivateName, "wrong-type:item:xsi-type:<redacted: 27 chars>")]
    [InlineData("wrong-type:item-name:{urn:other}" + ConsolePrivateName, "wrong-type:item-name:<redacted: 27 chars>")]
    [InlineData("wrong-type:item-name:" + ConsolePrivateName, "wrong-type:item-name:<redacted: 16 chars>")]
    [InlineData("wrong-type:item:item-name:" + ConsolePrivateName, "wrong-type:item:item-name:<redacted: 16 chars>")]
    [InlineData("wrong-type:item:item:item-name:x", "wrong-type:item:item:item-name:<redacted: 1 char>")]
    [InlineData("wrong-type:item-name:" + ConsoleEmail, "wrong-type:item-name:<redacted: 25 chars>")]
    [InlineData("wrong-type:" + ConsolePrivateName, "wrong-type:<redacted: 16 chars>")]
    [InlineData("wrong-type:item:" + ConsolePrivateName, "wrong-type:<redacted: 21 chars>")]
    [InlineData("wrong-type:item:textual", "wrong-type:<redacted: 12 chars>")]
    [InlineData("wrong-type:String", "wrong-type:<redacted: 6 chars>")]
    [InlineData("wrong-type:item:\u001B[31m", "wrong-type:<redacted: 10 chars>")]
    [InlineData("wrong-type:item:", "wrong-type:<redacted: 5 chars>")]
    [InlineData("wrong-type:", "wrong-type:<redacted: 0 chars>")]
    // Every other nature holds no response value and is shown unchanged.
    [InlineData("d:0123456789abcdef", "d:0123456789abcdef")]
    [InlineData("failed:fedcba9876543210", "failed:fedcba9876543210")]
    [InlineData("modified:3b18e512dba79e4c8300dd08aeb37f8e728b8dad", "modified:3b18e512dba79e4c8300dd08aeb37f8e728b8dad")]
    [InlineData("unreachable-in-source", "unreachable-in-source")]
    [InlineData("not-ancestor", "not-ancestor")]
    [InlineData("System.InvalidOperationException", "System.InvalidOperationException")]
    public void FailureItemDisplay_HidesObservedValues_AndKeepsEveryOtherNature(string nature, string expected)
    {
        var item = new FailureItem("CHK-RESET:replay-start", nature);

        var display = FailureItemDisplay.Nature(nature);

        Assert.Equal(expected, display);
        Assert.Equal($"CHK-RESET:replay-start {expected}", FailureItemDisplay.Describe(item));

        // The item itself keeps the exact nature, and no redacted value reaches the display.
        Assert.Equal(nature, item.Nature);
        Assert.DoesNotContain(ConsoleEmail, display, StringComparison.Ordinal);
        Assert.DoesNotContain(ConsolePrivateName, display, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001B", display, StringComparison.Ordinal);
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

        // The lines are shown with the per-test temporary directory above "missing" replaced by a fixed token;
        // the escaped part of the path below it stays visible.
        var temporary = Path.GetDirectoryName(Path.GetDirectoryName(directory))!;
        const string temporaryToken = "<temporary-directory>";
        var escapedDirectory = directory.Replace("\r\n", "\\u000D\\u000A", StringComparison.Ordinal);
        Assert.Equal(
            $"{SourceLabels.ConsolePrefix} seed fingerprint of '{escapedDirectory.Replace(temporary, temporaryToken, StringComparison.Ordinal)}' "
            + $"unavailable: {nameof(DirectoryNotFoundException)}",
            lines[0].Replace(temporary, temporaryToken, StringComparison.Ordinal));
        Assert.StartsWith(
            $"{SourceLabels.ConsolePrefix} {ResetExpectations.ExpectedReferenceDateKey} '{ReferenceDate}\\u000A{forged}' is not a yyyy-MM-dd date",
            lines[1].Replace(temporary, temporaryToken, StringComparison.Ordinal),
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

        // A request head names the listener's port in its Host header, so it is never shown.
        Assert.True(
            (await answering).StartsWith($"GET {AdminRoutes.Health} HTTP/1.1", StringComparison.Ordinal),
            "the request the listener answered is the health probe");
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

    // Capture passes its stand-in prefix, so its ready and not-ready lines carry it; replay and
    // export-openapi pass none, so their lines start with the outcome.
    [Theory]
    [InlineData(SourceLabels.ConsolePrefix + " ", true)]
    [InlineData(SourceLabels.ConsolePrefix + " ", false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public async Task Readiness_StartsItsLineWithTheGivenPrefix(string? linePrefix, bool listening)
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var answering = listening ? AnswerHealthOnceAsync(listener, ct) : null;
        using var readiness = new Readiness(
            new HarnessOptions { ReadinessTimeoutSeconds = 10, ReadinessPollIntervalSeconds = 1 },
            _console,
            linePrefix);

        // A URL that is not http or https is rejected at once, without a probe.
        var item = await readiness.WaitAsync(
            ShutdownTarget.StandInComponent,
            listening ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}") : "ftp://standin.invalid:9090",
            ct);

        Assert.Equal(listening, item is null);
        if (answering is not null)
        {
            await answering;
        }

        // The ready line holds the measured wait, which is shown as a fixed token.
        var expectedStart = (linePrefix ?? "")
            + (listening ? $"ready: {ShutdownTarget.StandInComponent} (" : $"not ready: {ShutdownTarget.StandInComponent} (invalid admin URL: ");
        Assert.StartsWith(expectedStart, Assert.Single(PrintedLines().Select(WithoutMilliseconds)), StringComparison.Ordinal);
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
        Assert.True(outcome.Stopped, WithoutMilliseconds(outcome.Detail));
        Assert.Null(outcome.ToFailureItem());

        // How many probes a client that missed the refusal would send depends on timing, so the message is fixed.
        Assert.True(
            admin.Requests.SequenceEqual(["POST " + AdminRoutes.Shutdown, "GET " + AdminRoutes.Health]),
            "the requests are the shutdown call and the one refused health probe, in that order");
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
        // The text can hold a temporary path or a measured time, so the check of its end is not shown.
        var text = _console.ToString();
        Assert.True(text.EndsWith(Environment.NewLine, StringComparison.Ordinal), "the console text ends with a line terminator");
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

// ---- Harness entry point ------------------------------------------------------------------------
// The harness entry point: the argument check that runs before any mode, the strict and lenient
// settings loaders, and a replay whose settings are partly unusable. An argument after the mode that is
// not a --Harness:Key=value setting is refused before anything is read. The lenient loader keeps every
// setting that can be read and converted, so a replay with an unreadable harness.json or an unknown
// execution mode still writes its report to the configured path and treats an empty backend admin URL
// as an external backend. Each setting binds as binding the whole section would, an explicit null in
// harness.json included. A FIFO or a link at harness.json is refused at once by both loaders.
// Every setting is passed as a --Harness:... argument, which overrides the environment, or written to
// the tree's own harness.json, so no test sets a process environment variable. Every tree is a
// TempDirectory, never the committed one, and no assertion reads standard error. URLs name loopback
// ports that nothing listens on.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and the entry point starts every mode, the replay whose exit path is covered above
// included. The tree is the exit-path members' repository root, created on first use, and standard
// output is theirs too.
public sealed partial class ReplayLifecycleTests
{
    private const string RootToken = "{root}";
    private const string FinalLine = "3 of 22 operations equivalent, 1 documented differences";
    private const string HarnessJson = "harness/config/harness.json";
    private const string MalformedHarnessJson = "{ \"Harness\": { \"ReadinessTimeoutSeconds\": 6, ";
    private const string TargetAdminUrl = "http://127.0.0.1:1";

    // A refused argument returns at once; a mode that ran instead would wait on its default hosts far longer.
    private static readonly TimeSpan RejectBound = TimeSpan.FromSeconds(5);

    // Hard limits for one call: the token is cancelled and the wait ends, so a hang fails the test.
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplayLimit = TimeSpan.FromSeconds(60);

    // Each list holds one argument that is not a setting; a bare key or "--help" would otherwise take the
    // argument after it as its value, and a bare word would be ignored.
    [Theory]
    [InlineData("capture --Harness:RepoRoot={root} --help")]
    [InlineData("summary --Harness:RepoRoot={root} extra junk")]
    [InlineData("summary --Harness:RepoRoot={root} -x")]
    [InlineData("summary --Harness:RepoRoot={root} -x=1")]
    [InlineData("summary --Harness:RepoRoot={root} /x")]
    [InlineData("summary --Harness:RepoRoot={root} --")]
    [InlineData("summary --Harness:RepoRoot={root} --=1")]
    [InlineData("summary --Harness:RepoRoot={root} capture")]
    [InlineData("summary --Harness:RepoRoot={root} --Harness:Typo=1")]
    [InlineData("summary --Harness:RepoRoot={root} --Harness:Target=x")]
    [InlineData("summary --Harness:RepoRoot {root}")]
    [InlineData("export-openapi --Harness:RepoRoot={root} --help --Harness:Target:AdminUrl=")]
    public async Task ArgumentThatIsNotASetting_IsRefusedBeforeTheModeRuns(string commandLine)
    {
        // summary would print this report's line and exit 0 had the argument been ignored.
        RepoRootDirectory.Write("RESULTS.md", $"# Results\n\n{FinalLine}\n");
        var elapsed = Stopwatch.StartNew();

        var exitCode = await RunProgramAsync(Args(commandLine), CallLimit);

        Assert.Equal(1, exitCode);
        Assert.True(elapsed.Elapsed < RejectBound, "the argument is refused before any setting, file or network is read");

        // A mode that ran would print the temporary root and measured waits, so the text is not shown.
        Assert.True(_stdout.ToString().Length == 0, "the refused argument prints nothing to standard output");
        Assert.False(Directory.Exists(RepoRootDirectory.PathOf("baseline")), "no mode ran, so nothing wrote baseline/");
        Assert.False(Directory.Exists(RepoRootDirectory.PathOf("contracts")), "no mode ran, so nothing wrote contracts/");
    }

    // Keys match case-insensitively, as configuration keys do, and an empty value is a value.
    [Theory]
    [InlineData("summary --Harness:RepoRoot={root} --harness:reportpath={root}/R.md")]
    [InlineData("summary --Harness:RepoRoot={root} --Harness:ReportPath={root}/R.md --Harness:Backend:AdminUrl=")]
    public async Task SettingArguments_AreAccepted(string commandLine)
    {
        RepoRootDirectory.Write("R.md", $"# Results\n\n{FinalLine}\n");

        var exitCode = await RunProgramAsync(Args(commandLine), CallLimit);

        Assert.Equal(0, exitCode);
        Assert.Equal([FinalLine], Redacted(_stdout.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void UnreadableHarnessJson_LenientKeepsEveryOtherSetting_StrictThrows()
    {
        RepoRootDirectory.Write(HarnessJson, MalformedHarnessJson);
        var args = DestinationArgs("--Harness:ShutdownTimeoutSeconds=7");
        var problems = new List<Program.OptionsProblem>();

        var options = Program.LoadOptions(args, problems);

        AssertConfiguredDestinations(options);
        Assert.Equal(7, options.ShutdownTimeoutSeconds);
        var problem = AssertSingleProblem(problems);
        Assert.IsType<InvalidDataException>(problem.Exception);
        Assert.Contains("harness.json", problem.Context, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => Program.LoadOptions(args));
    }

    // A FIFO at harness.json that nobody writes to, and a link there to a file outside the repository, each
    // fail the read at once instead of blocking the start or being followed: the strict loader throws the
    // refusal, and the lenient one records it and keeps every other setting, the outside file's never applied.
    [Theory]
    [InlineData("fifo", "ConfinedTree could not read 'harness/config/harness.json': it is not a regular file (a FIFO).")]
    [InlineData(
        "link",
        "ConfinedTree never reads through a link: refused 'harness/config/harness.json', because 'harness/config/harness.json' is a symbolic link.")]
    public async Task HarnessJsonThatIsAFifoOrALink_IsRefusedPromptly_LenientKeepsEveryOtherSetting(string entry, string expectedRefusal)
    {
        using var outside = new TempDirectory();
        var path = RepoRootDirectory.PathOf(HarnessJson);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (entry == "fifo")
        {
            SpecialFileSupport.CreateFifo(path);
        }
        else
        {
            File.CreateSymbolicLink(path, outside.Write("harness.json", """{ "Harness": { "ReadinessTimeoutSeconds": 6 } }"""));
        }

        var args = DestinationArgs("--Harness:ShutdownTimeoutSeconds=7");
        var problems = new List<Program.OptionsProblem>();

        var options = await Task.Run(() => Program.LoadOptions(args, problems), TestCancellation).WaitAsync(PromptLimit, TestCancellation);
        var strict = await Assert.ThrowsAsync<IOException>(
            () => Task.Run(() => Program.LoadOptions(args), TestCancellation).WaitAsync(PromptLimit, TestCancellation));

        Assert.Equal(expectedRefusal, strict.Message);
        AssertConfiguredDestinations(options);
        Assert.Equal(7, options.ShutdownTimeoutSeconds);
        Assert.Equal(new HarnessOptions().ReadinessTimeoutSeconds, options.ReadinessTimeoutSeconds);
        var problem = AssertSingleProblem(problems);
        Assert.Contains("harness.json", problem.Context, StringComparison.Ordinal);
        Assert.Equal(expectedRefusal, Assert.IsType<IOException>(problem.Exception).Message);
    }

    [Fact]
    public void UnconvertibleSetting_LenientKeepsItsDefaultAndEveryOtherValue_StrictThrows()
    {
        RepoRootDirectory.Write(HarnessJson, """{ "Harness": { "ReadinessTimeoutSeconds": 6 } }""");
        var args = DestinationArgs("--Harness:RequestTimeoutSeconds=abc");
        var problems = new List<Program.OptionsProblem>();
        var defaults = new HarnessOptions();

        var options = Program.LoadOptions(args, problems);

        AssertConfiguredDestinations(options);
        Assert.Equal(6, options.ReadinessTimeoutSeconds);
        Assert.Equal(defaults.RequestTimeoutSeconds, options.RequestTimeoutSeconds);

        // Setting one endpoint key keeps the defaults of the others.
        Assert.Equal(defaults.Target.RestBaseUrl, options.Target.RestBaseUrl);

        var problem = AssertSingleProblem(problems);
        var conversion = Assert.IsType<InvalidOperationException>(problem.Exception);
        Assert.Contains("Harness:RequestTimeoutSeconds", conversion.Message, StringComparison.Ordinal);
        var thrown = Assert.Throws<InvalidOperationException>(() => Program.LoadOptions(args));
        Assert.Equal(conversion.Message, thrown.Message);
    }

    // Each setting is bound on its own, yet ends as binding the whole section of one configuration over the
    // same sources leaves it, whatever harness.json holds: a missing key, an explicit null, "", a value, a key
    // holding only nested entries, or a null endpoints object. An argument outranks harness.json both ways: a
    // URL replaces a null, and "" replaces a URL.
    [Theory]
    [InlineData("""{ "Harness": { } }""", null, "http://standin:9090", false)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": null } } }""", null, null, true)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": "" } } }""", null, "", true)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": "http://127.0.0.1:2" } } }""", null, "http://127.0.0.1:2", false)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": { "Nested": "x" } } } }""", null, "http://standin:9090", false)]
    [InlineData("""{ "Harness": { "Backend": null } }""", null, "http://standin:9090", false)]
    [InlineData("""{ "Harness": { "ReadinessTimeoutSeconds": null } }""", null, "http://standin:9090", false)]
    [InlineData("""{ "Harness": { "ReportPath": null, "SeedDirectory": null } }""", null, "http://standin:9090", false)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": null } } }""", "--Harness:Backend:AdminUrl=http://127.0.0.1:1", "http://127.0.0.1:1", false)]
    [InlineData("""{ "Harness": { "Backend": { "AdminUrl": "http://127.0.0.1:2" } } }""", "--Harness:Backend:AdminUrl=", "", true)]
    public void EachSetting_BindsAsTheWholeSectionWould(
        string harnessJson, string? argument, string? expectedAdminUrl, bool expectedExternal)
    {
        var path = RepoRootDirectory.Write(HarnessJson, harnessJson);
        string[] args = argument is null
            ? [$"--Harness:RepoRoot={RepoRootDirectory.Root}"]
            : [$"--Harness:RepoRoot={RepoRootDirectory.Root}", argument];
        var problems = new List<Program.OptionsProblem>();

        var strict = Program.LoadOptions(args);
        var lenient = Program.LoadOptions(args, problems);
        var whole = BindWholeSection(path, args);

        Assert.True(problems.Count == 0, $"the lenient loader recorded {ProblemContexts(problems)}");
        Assert.Equal(expectedAdminUrl, whole.Backend.AdminUrl);
        Assert.Equal(expectedExternal, whole.IsExternalBackend);
        foreach (var options in new[] { strict, lenient })
        {
            Assert.Equal(whole.Backend.AdminUrl, options.Backend.AdminUrl);
            Assert.Equal(whole.IsExternalBackend, options.IsExternalBackend);
            Assert.Equal(whole.ReadinessTimeoutSeconds, options.ReadinessTimeoutSeconds);
            Assert.Equal(RedactedSetting(whole.ReportPath), RedactedSetting(options.ReportPath));
            Assert.Equal(RedactedSetting(whole.SeedDirectory), RedactedSetting(options.SeedDirectory));
            Assert.Equal(RepoRootToken, Redacted(options.RepoRoot));
        }
    }

    [Fact]
    public async Task Replay_WithUnreadableHarnessJsonAndUnknownMode_ReportsBoth_AtTheConfiguredReportPath()
    {
        RepoRootDirectory.Write(HarnessJson, MalformedHarnessJson);
        var reportPath = RepoRootDirectory.PathOf("custom.md");
        var ports = ClosedPorts.Take(5);
        string[] args =
        [
            "replay",
            $"--Harness:RepoRoot={RepoRootDirectory.Root}",
            $"--Harness:ReportPath={reportPath}",
            "--Harness:ExecutionMode=kubernetes",
            $"--Harness:Target:RestBaseUrl=http://127.0.0.1:{ports[0]}",
            $"--Harness:Target:SoapBaseUrl=http://127.0.0.1:{ports[1]}",
            $"--Harness:Target:AdminUrl=http://127.0.0.1:{ports[2]}",
            $"--Harness:Backend:RestBaseUrl=http://127.0.0.1:{ports[3]}",
            $"--Harness:Backend:SoapBaseUrl=http://127.0.0.1:{ports[4]}",
            "--Harness:Backend:AdminUrl=",
            "--Harness:ReadinessTimeoutSeconds=1",
            "--Harness:RequestTimeoutSeconds=1",
            "--Harness:ShutdownTimeoutSeconds=1",
            "--Harness:ShutdownRequestTimeoutSeconds=1",
        ];

        var exitCode = await RunProgramAsync(args, ReplayLimit);

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(reportPath), "the report is written to Harness:ReportPath");
        Assert.False(File.Exists(RepoRootDirectory.PathOf("RESULTS.md")), "the default report path is never written");

        // The report and standard output hold the temporary root, loopback ports and the run date, so their text
        // is not shown.
        var report = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
        Assert.True(
            report.Contains(typeof(InvalidDataException).FullName!, StringComparison.Ordinal),
            "the report names the exception that left harness.json unread");
        Assert.True(
            report.Contains("harness.json could not be read, so its settings are not applied", StringComparison.Ordinal),
            "the report states that harness.json could not be read");
        Assert.True(
            report.Contains("Harness:ExecutionMode 'kubernetes' is neither 'local-processes' nor 'docker'", StringComparison.Ordinal),
            "the report states that the execution mode is unknown");
        Assert.True(
            report.Contains("| Execution mode | `unknown` |", StringComparison.Ordinal),
            "the report's run facts show the execution mode as unknown");

        // The empty backend admin URL survived, so the backend counts as external and is left running.
        Assert.True(
            _stdout.ToString().Contains("cleanup: the backend is external and is never shut down", StringComparison.Ordinal),
            "the console states that the external backend is never shut down");
    }

    // Runs the entry point with a hard limit that cancels the run's token and also ends the wait.
    private async Task<int> RunProgramAsync(string[] args, TimeSpan limit)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        run.CancelAfter(limit);
        return await Program.RunAsync(args, _stdout, run.Token).WaitAsync(run.Token);
    }

    private string[] Args(string commandLine) =>
        [.. commandLine.Split(' ').Select(argument => argument.Replace(RootToken, RepoRootDirectory.Root, StringComparison.Ordinal))];

    // The settings as binding the whole section gives them: one configuration over harness.json, the
    // environment and the arguments, bound at once; no section at all means every default.
    private static HarnessOptions BindWholeSection(string harnessJsonPath, string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(harnessJsonPath, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        try
        {
            return configuration.GetSection(HarnessOptions.SectionName).Get<HarnessOptions>() ?? new HarnessOptions();
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }

    private string ReportPath => RepoRootDirectory.PathOf("custom.md");

    // The settings that decide where the report goes and what cleanup shuts down, then the case's own.
    private string[] DestinationArgs(params string[] more) =>
    [
        $"--Harness:RepoRoot={RepoRootDirectory.Root}",
        $"--Harness:ReportPath={ReportPath}",
        $"--Harness:Target:AdminUrl={TargetAdminUrl}",
        "--Harness:Backend:AdminUrl=",
        .. more,
    ];

    // Paths below the temporary tree are compared with its root replaced by RepoRootToken, so a failure reads the
    // same in every run.
    private void AssertConfiguredDestinations(HarnessOptions options)
    {
        Assert.Equal(RepoRootToken, Redacted(options.RepoRoot));
        Assert.Equal(Redacted(ReportPath), Redacted(options.EffectiveReportPath));
        Assert.Equal(TargetAdminUrl, options.Target.AdminUrl);
        Assert.Equal("", options.Backend.AdminUrl);
        Assert.True(options.IsExternalBackend, "an empty backend admin URL marks an external backend");
    }

    private string? RedactedSetting(string? value) => value is null ? null : Redacted(value);

    // A settings problem holds an exception whose message can name the temporary tree, so only the fixed
    // contexts are shown.
    private static Program.OptionsProblem AssertSingleProblem(List<Program.OptionsProblem> problems)
    {
        Assert.True(problems.Count == 1, $"the lenient loader recorded {ProblemContexts(problems)}, not exactly one problem");
        return problems[0];
    }

    private static string ProblemContexts(List<Program.OptionsProblem> problems) =>
        problems.Count == 0 ? "no problem" : string.Join("; ", problems.Select(problem => problem.Context));
}

// ---- Signal cancellation ------------------------------------------------------------------------
// The cancellation Main hands SIGTERM and SIGINT to, driven directly on a token source: requests are
// made the way the signal handler makes them, and no signal is raised. Only the first request starts
// the token's callbacks, and its task is the one retained. Completion passes the mode's exit code
// through when no request was made or the callbacks finished; a callback that throws, or one still
// running at the bound, gives one standard-error line and exit code 1, and a thrown fault is observed
// rather than left for the finaliser. The supervisors that send SIGTERM, run-local.sh and the Compose
// harness service, wait longer than replay's cancelled exit budget plus this bound before they kill it.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and this completion is the last step of the harness process's exit path, after its mode,
// the replay included, has returned its exit code. RunLimit, ExitCodes, AssertExitCode and FindRepoRoot
// come from the exit-path members, and Slack from the shutdown members.
public sealed partial class ReplayLifecycleTests
{
    private const string FailedPrefix = "cancellation callbacks failed: ";

    // Short, so the still-running case ends quickly; a completion that ignored it would wait for RunLimit.
    private static readonly TimeSpan TestBound = TimeSpan.FromMilliseconds(200);

    // The early wake-up a timer may show; Slack is the headroom over a bound for scheduling on a loaded host.
    private static readonly TimeSpan TimerTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CollectRetryDelay = TimeSpan.FromMilliseconds(20);

    // Created on first use, so only the signal-cancellation tests make them; the source is disposed in Dispose.
    private CancellationTokenSource? _cancellationSource;
    private ConcurrentQueue<string>? _cancellationErrors;

    /// <summary>When a request arrives too late to cancel anything.</summary>
    public enum LateRequest
    {
        /// <summary>After completion has begun, as from a handler dispatched before its registration was disposed.</summary>
        AfterCompletion,

        /// <summary>After the token source was disposed.</summary>
        AfterSourceDisposed,
    }

    /// <summary>The token source the cancellation under test cancels, created on first use.</summary>
    private CancellationTokenSource CancellationSource => _cancellationSource ??= new CancellationTokenSource();

    /// <summary>The standard-error lines the cancellation under test writes, created on first use.</summary>
    private ConcurrentQueue<string> CancellationErrors => _cancellationErrors ??= new ConcurrentQueue<string>();

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task NoRequestOrCompletedCallbacks_PassExitCodeThrough(int exitCode, bool requested)
    {
        var ran = 0;
        using var registration = CancellationSource.Token.Register(() => Interlocked.Increment(ref ran));
        var cancellation = NewCancellation();
        if (requested)
        {
            cancellation.Request();
        }

        var result = await CompleteAsync(cancellation, exitCode, Program.SignalCancellation.CompletionLimit);

        AssertExitCode(exitCode, result);
        Assert.Empty(CancellationErrors);
        Assert.Equal(requested, CancellationSource.IsCancellationRequested);
        Assert.Equal(requested ? 1 : 0, Volatile.Read(ref ran));
        Assert.Equal(requested, cancellation.Callbacks is { IsCompletedSuccessfully: true });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task ThrowingCallbacks_AreObservedAndReported_Exit1(int exitCode, int throwing)
    {
        // The unique marker tells this test's faults apart from any other unobserved task exception; every
        // standard-error line is shown with it replaced by a fixed token, so a failure reads the same in every run.
        const string markerToken = "<marker>";
        var marker = Guid.NewGuid().ToString("N");
        var messages = Enumerable.Range(1, throwing).Select(index => $"callback {index} failed {marker}").ToArray();
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(ex => ex.Message.Contains(marker, StringComparison.Ordinal)))
            {
                unobserved.Enqueue(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (result, callbacks) = await RunThrowingAsync(exitCode, messages);

            AssertExitCode(1, result);
            var line = Assert.Single(CancellationErrors.Select(error => error.Replace(marker, markerToken, StringComparison.Ordinal)));
            Assert.StartsWith(FailedPrefix, line, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            foreach (var message in messages)
            {
                Assert.Contains(
                    $"{typeof(InvalidOperationException).FullName}: {message.Replace(marker, markerToken, StringComparison.Ordinal)}",
                    line,
                    StringComparison.Ordinal);
            }

            // Once the task is collected, the finaliser has run that would publish a fault nobody observed.
            Assert.True(await CollectedAsync(callbacks), "the callbacks' task was collected, so its finaliser has run");
            Assert.True(unobserved.IsEmpty, "a fault of the callbacks reached TaskScheduler.UnobservedTaskException");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task CallbackStillRunningAtBound_IsReported_Exits1WithinBound()
    {
        using var release = new ManualResetEventSlim();
        using var registration = CancellationSource.Token.Register(() => release.Wait(RunLimit));
        var cancellation = NewCancellation();
        cancellation.Request();
        var callbacks = cancellation.Callbacks;
        Assert.NotNull(callbacks);

        int result;
        TimeSpan elapsed;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            result = await CompleteAsync(cancellation, 0, TestBound);
            elapsed = stopwatch.Elapsed;
            Assert.False(callbacks.IsCompleted, "the callback is still held when completion returns");
        }
        finally
        {
            await ReleaseAsync(release, callbacks);
        }

        AssertExitCode(1, result);

        // A timing assertion with a fixed message: the measured time is never shown.
        Assert.True(
            elapsed >= TestBound - TimerTolerance && elapsed <= TestBound + Slack,
            "completion did not return at its bound, within the timer tolerance before it or the slack after it");
        Assert.Equal("cancellation callbacks were still running after 0.2 s", Assert.Single(CancellationErrors));
        Assert.True(callbacks.IsCompletedSuccessfully, "the released callback finishes");
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(16, true)]
    public async Task RepeatedRequests_StartOneCancellation_RetainFirstTask(int requests, bool concurrent)
    {
        using var release = new ManualResetEventSlim();
        var ran = 0;
        using var registration = CancellationSource.Token.Register(() =>
        {
            Interlocked.Increment(ref ran);
            release.Wait(RunLimit);
        });
        var cancellation = NewCancellation();
        var ct = TestContext.Current.CancellationToken;

        Task? callbacks = null;
        int result;
        try
        {
            // On pool threads, so a request that waited for the held callback fails at RunLimit instead of hanging.
            var requesting = concurrent
                ? Task.WhenAll(Enumerable.Range(0, requests).Select(_ => Task.Run(cancellation.Request, ct)))
                : Task.Run(
                    () =>
                    {
                        for (var request = 0; request < requests; request++)
                        {
                            cancellation.Request();
                        }
                    },
                    ct);
            await requesting.WaitAsync(RunLimit, ct);

            callbacks = cancellation.Callbacks;
            Assert.NotNull(callbacks);
            Assert.True(CancellationSource.IsCancellationRequested);

            // A request on a source already cancelled gets a task that has already completed, so a retained
            // task still running while the only callback is held is the first request's.
            Assert.False(callbacks.IsCompleted, "the retained task is the one running the held callback");
            cancellation.Request();
            Assert.Same(callbacks, cancellation.Callbacks);

            release.Set();
            result = await CompleteAsync(cancellation, 0, Program.SignalCancellation.CompletionLimit);
        }
        finally
        {
            await ReleaseAsync(release, callbacks);
        }

        AssertExitCode(0, result);
        Assert.Same(callbacks, cancellation.Callbacks);
        Assert.True(callbacks.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.Empty(CancellationErrors);
    }

    [Theory]
    [InlineData(LateRequest.AfterCompletion)]
    [InlineData(LateRequest.AfterSourceDisposed)]
    public async Task LateRequest_StartsNothing(LateRequest late)
    {
        var ran = 0;
        using var registration = CancellationSource.Token.Register(() => Interlocked.Increment(ref ran));
        var cancellation = NewCancellation();
        if (late == LateRequest.AfterCompletion)
        {
            AssertExitCode(0, await CompleteAsync(cancellation, 0, Program.SignalCancellation.CompletionLimit));
        }
        else
        {
            CancellationSource.Dispose();
        }

        cancellation.Request();

        Assert.Null(cancellation.Callbacks);
        Assert.Equal(0, Volatile.Read(ref ran));
        AssertExitCode(1, await CompleteAsync(cancellation, 1, Program.SignalCancellation.CompletionLimit));
        Assert.Empty(CancellationErrors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1000)]
    public async Task NegativeBound_IsRejected(int boundMilliseconds)
    {
        var cancellation = NewCancellation();
        cancellation.Request();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cancellation.CompleteAsync(0, TimeSpan.FromMilliseconds(boundMilliseconds)));
        Assert.Empty(CancellationErrors);
    }

    // The wait is limited independently of the bound under test, so a completion that ignores it still ends.
    private static Task<int> CompleteAsync(Program.SignalCancellation cancellation, int exitCode, TimeSpan bound) =>
        cancellation.CompleteAsync(exitCode, bound).WaitAsync(RunLimit, TestContext.Current.CancellationToken);

    // The continuations that completed the run can still be on the stack of the thread that finished the task,
    // their state machines holding it, so collection is retried from a later timer callback until the limit.
    private static async Task<bool> CollectedAsync(WeakReference target)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!target.IsAlive)
            {
                return true;
            }

            if (stopwatch.Elapsed >= Slack)
            {
                return false;
            }

            await Task.Delay(CollectRetryDelay, TestContext.Current.CancellationToken);
        }
    }

    // Releases a held callback and waits for it, so the event is never disposed under a waiting callback.
    private static async Task ReleaseAsync(ManualResetEventSlim release, Task? callbacks)
    {
        release.Set();
        if (callbacks is not null)
        {
            await callbacks.WaitAsync(RunLimit, TestContext.Current.CancellationToken);
        }
    }

    // Runs on a source and a cancellation of its own and hands back only a weak reference to the callbacks'
    // task, so that once it returns nothing holds the task and the caller can let the finaliser judge it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(int Result, WeakReference Callbacks)> RunThrowingAsync(int exitCode, string[] messages)
    {
        using var source = new CancellationTokenSource();
        var registrations = messages
            .Select(message => source.Token.Register(() => throw new InvalidOperationException(message)))
            .ToArray();
        try
        {
            var cancellation = new Program.SignalCancellation(source, CancellationErrors.Enqueue);
            cancellation.Request();
            var callbacks = cancellation.Callbacks;
            Assert.NotNull(callbacks);

            var result = await CompleteAsync(cancellation, exitCode, Program.SignalCancellation.CompletionLimit);

            Assert.True(callbacks.IsFaulted, "every callback ran and the task holds their faults");
            return (result, new WeakReference(callbacks));
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }

    private Program.SignalCancellation NewCancellation() => new(CancellationSource, CancellationErrors.Enqueue);

    // The two supervisors that SIGTERM the harness, run-local.sh and the Compose harness service, each wait
    // longer than replay's cancelled exit budget plus this completion wait before they kill it, so a SIGTERM
    // always leaves the cancelled run time for its report, both shutdowns and its final line. The budget is
    // the 105 s both files describe.
    [Fact]
    public void Supervisors_WaitLongerThanTheCancelledExitBudgetAndTheCompletionWait()
    {
        var root = FindRepoRoot();
        var needed = ReplayMode.CancelledExitBudget + Program.SignalCancellation.CompletionLimit;

        Assert.Equal(TimeSpan.FromSeconds(105), ReplayMode.CancelledExitBudget);
        Assert.True(
            HarnessStopSeconds(File.ReadAllText(Path.Combine(root, "harness", "scripts", "run-local.sh"))) > needed,
            "run-local.sh HARNESS_STOP_SECONDS does not exceed the cancelled exit budget plus the completion wait");
        Assert.True(
            HarnessStopGracePeriod(File.ReadAllText(Path.Combine(root, "docker-compose.yml"))) > needed,
            "the harness service's stop_grace_period does not exceed the cancelled exit budget plus the completion wait");
    }

    // The value of run-local.sh's one `readonly HARNESS_STOP_SECONDS=<seconds>` line.
    private static TimeSpan HarnessStopSeconds(string script)
    {
        var setting = Assert.Single(Regex.Matches(
            script, @"^readonly HARNESS_STOP_SECONDS=(?<seconds>[0-9]+)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant));
        return TimeSpan.FromSeconds(int.Parse(setting.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    // The stop_grace_period of docker-compose.yml's harness service: the one key of that name indented as a
    // service key between `  harness:` and the next line indented less, in the h/m/s form the file uses.
    private static TimeSpan HarnessStopGracePeriod(string compose)
    {
        var lines = compose.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var service = Array.IndexOf(lines, "  harness:");
        Assert.True(service >= 0, "docker-compose.yml has no harness service");

        var periods = new List<string>();
        for (var index = service + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith("    ", StringComparison.Ordinal))
            {
                break;
            }

            var period = Regex.Match(line, @"^    stop_grace_period:[ \t]*(?<value>[0-9hms]+)[ \t]*$", RegexOptions.CultureInvariant);
            if (period.Success)
            {
                periods.Add(period.Groups["value"].Value);
            }
        }

        var parts = Regex.Match(Assert.Single(periods), @"^(?:(?<h>[0-9]+)h)?(?:(?<m>[0-9]+)m)?(?:(?<s>[0-9]+)s)?$", RegexOptions.CultureInvariant);
        Assert.True(parts.Success, "the harness service's stop_grace_period is not a duration of whole hours, minutes and seconds");
        return TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) + TimeSpan.FromSeconds(Part("s"));

        int Part(string unit) => parts.Groups[unit].Success
            ? int.Parse(parts.Groups[unit].Value, NumberStyles.None, CultureInfo.InvariantCulture)
            : 0;
    }
}

// ---- Mode diagnostics ---------------------------------------------------------------------------
// The modes' ordinary console diagnostics: capture classifies an unreadable SOAP statusCode without
// quoting the response body or the parser's message; export-openapi announces its readiness wait; and
// the lines of export-openapi, summary and the entry point stay single inert lines whatever control or
// bidirectional characters a path or message holds. The fake facade listens on loopback only.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and these lines are escaped by the console sink covered above. The standard-error test
// replaces Console.Error, which is process-wide, so the whole class belongs to a collection that xunit
// runs on its own, after every other test class and never beside one; within the class, tests run one at
// a time. The test still keeps only the terminated lines that start with its mode's prefix and name its
// own root, so a stray line could neither fail it nor pass it.
[Collection(ProcessWideConsoleCollection.Name)]
public sealed partial class ReplayLifecycleTests
{
    // ESC starting a colour sequence, BEL ending an OSC title sequence, a right-to-left override and an
    // Arabic letter mark: each is legal in a Linux file name and would drive or disguise a terminal line
    // if printed as it is.
    private const string HostileName = "x\u001B[31my\u001B]0;t\u0007\u202Ez\u061Cw";

    // Bounds the fake facade's wait for the mode's requests, so a mode that stops before readiness fails
    // the test instead of leaving it waiting.
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(30);

    // Stands for ModeDiagnosticsRoot, which differs in every run, in the lines a failure message shows.
    private const string ModeDiagnosticsRootToken = "<mode-diagnostics-root>";

    // Created on first use, so only the mode diagnostics tests touch the file system; removed in Dispose.
    private string? _modeDiagnosticsRoot;

    /// <summary>The <c>export-openapi</c> outcome a test drives.</summary>
    public enum ExportOutcome
    {
        /// <summary>The contract is written under the repository root.</summary>
        Written,

        /// <summary><c>contracts/openapi</c> under the repository root is a file, so opening the contract directory throws.</summary>
        WriteFailed,
    }

    /// <summary>The standard-error diagnostic a test drives.</summary>
    public enum ErrorLine
    {
        /// <summary><c>summary</c> with no report under the repository root.</summary>
        SummaryMissingReport,

        /// <summary>The entry point with a <c>harness.json</c> that is not JSON.</summary>
        InvalidConfiguration,
    }

    /// <summary>The mode diagnostics tests' temporary directory, created empty on first use.</summary>
    private string ModeDiagnosticsRoot
    {
        get
        {
            if (_modeDiagnosticsRoot is null)
            {
                // The path is recorded before the directory is created, so Dispose removes whatever exists.
                // The run sets TMPDIR to a per-clone directory, so parallel checkouts never share a root.
                _modeDiagnosticsRoot = Path.Combine(Path.GetTempPath(), $"erewhon-mode-diagnostics-{Guid.NewGuid():N}");
                Directory.CreateDirectory(_modeDiagnosticsRoot);
            }

            return _modeDiagnosticsRoot;
        }
    }

    // ---- Capture: SOAP statusCode --------------------------------------------------------------

    // Non-XML text holding an email; a statusCode holding an email and ESC, which XML forbids; a statusCode
    // holding an email and a right-to-left override, which XML allows; no statusCode element at all; and a
    // readable code, whose surrounding white space xsd:int collapses.
    [Theory]
    [InlineData("<<not xml jane.parser@example.org \u001B[2J", null, "the body is not XML (XmlException)")]
    [InlineData("<r><statusCode>jane.secret@example.org \u001B[2J</statusCode></r>", null, "the body is not XML (XmlException)")]
    [InlineData("<r><statusCode>jane.secret@example.org \u202Edrow</statusCode></r>", null, "statusCode is not an integer")]
    [InlineData("<r><name>jane.secret@example.org</name></r>", null, "the body has no statusCode element")]
    [InlineData("<r><statusCode> 404 </statusCode></r>", 404, "")]
    public void ReadResultStatusCode_ReportsAFixedClassification(string body, int? expectedCode, string expectedProblem)
    {
        var code = CaptureMode.ReadResultStatusCode(Encoding.UTF8.GetBytes(body), out var problem);

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedProblem, problem);
    }

    // ---- export-openapi ------------------------------------------------------------------------

    // The readiness wait is announced with the configured limit, its unprefixed ready line with the measured
    // wait follows on the mode's own writer, and then the outcome follows as one line. The root holds
    // contracts/, so the mode reaches readiness; a regular file where contracts/openapi belongs makes the
    // write fail.
    [Theory]
    [InlineData(ExportOutcome.Written, "export-openapi: wrote 2 bytes to ")]
    [InlineData(ExportOutcome.WriteFailed, "export-openapi: failed: System.IO.")]
    public async Task ExportOpenApi_AnnouncesTheWait_AndPrintsOneEscapedLinePerOutcome(ExportOutcome outcome, string expectedStart)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(ModeDiagnosticsRoot, HostileName);
        var contracts = Directory.CreateDirectory(Path.Combine(root, "contracts")).FullName;
        if (outcome == ExportOutcome.WriteFailed)
        {
            await File.WriteAllTextAsync(Path.Combine(contracts, "openapi"), "not a directory", ct);
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var answering = AnswerFacadeAsync(listener, connections: 2, ct);
        var options = new HarnessOptions
        {
            RepoRoot = root,
            ReadinessTimeoutSeconds = 10,
            ReadinessPollIntervalSeconds = 1,
            RequestTimeoutSeconds = 5,
        };
        options.Target.AdminUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await new ExportOpenApiMode(options, stdout).RunAsync(ct);

        Assert.Equal(outcome == ExportOutcome.Written ? 0 : 1, exitCode);
        Assert.Equal(
            new[] { $"GET {AdminRoutes.Health}", $"GET {AdminRoutes.OpenApiDocument}" },
            await answering.WaitAsync(AnswerLimit, ct));
        var lines = ModeDiagnosticLines(ModeRedacted(stdout.ToString()));
        Assert.Equal(3, lines.Count);
        Assert.Equal("export-openapi: waiting up to 10 s for the facade admin listener", lines[0]);
        Assert.Matches(@"^ready: facade \([0-9]+ ms\)\z", lines[1]);
        var line = lines[2];
        if (outcome == ExportOutcome.Written)
        {
            AssertEscaped(line, expectedStart, ModeRedacted(LifecycleConsole.Display(root)));
            Assert.Equal(ModeRedacted($"{expectedStart}{LifecycleConsole.Display(options.ContractsOpenApiPath)}"), line);
            Assert.Equal("{}", await File.ReadAllTextAsync(options.ContractsOpenApiPath, ct));
        }
        else
        {
            // The confined write names the refused entry relative to the repository root, so the line quotes no
            // part of the hostile root and stays one inert line.
            const string refusal = "ConfinedTree could not open 'contracts/openapi': it is not a directory.";
            AssertEscaped(line, expectedStart, refusal);
            Assert.Equal($"{expectedStart}IOException: {refusal}", line);
        }
    }

    // ---- Standard error ------------------------------------------------------------------------

    [Theory]
    [InlineData(ErrorLine.SummaryMissingReport, "summary", "summary: '")]
    [InlineData(ErrorLine.InvalidConfiguration, "capture", "capture: failed: System.IO.InvalidDataException: ")]
    public async Task StandardErrorLines_AreOneEscapedLine(ErrorLine errorLine, string mode, string expectedStart)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(ModeDiagnosticsRoot, HostileName);
        var configDirectory = Path.Combine(root, "harness", "config");
        Directory.CreateDirectory(configDirectory);
        if (errorLine == ErrorLine.InvalidConfiguration)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory, "harness.json"), "{", ct);
        }

        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var original = Console.Error;
        int exitCode;
        Console.SetError(stderr);
        try
        {
            exitCode = await Program.RunAsync([mode, $"--Harness:RepoRoot={root}"], stdout, ct);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(1, exitCode);

        // A mode that printed here would print the temporary root and measured waits, so the text is not shown.
        Assert.True(stdout.ToString().Length == 0, "the mode prints nothing to standard output");

        // The class's collection keeps every other test off the swapped standard error while this one runs (see
        // ProcessWideConsoleCollection). Only this run's line names this test's own repository root, so a stray
        // line could still neither fail nor pass it; the mode prints exactly one.
        var modePrefix = mode + ": ";
        var escapedRoot = ModeRedacted(LifecycleConsole.Display(root));
        var line = Assert.Single(
            TerminatedLines(ModeRedacted(stderr.ToString())),
            printed => printed.StartsWith(modePrefix, StringComparison.Ordinal)
                && printed.Contains(escapedRoot, StringComparison.Ordinal));
        AssertEscaped(line, expectedStart, escapedRoot);
        if (errorLine == ErrorLine.SummaryMissingReport)
        {
            Assert.Equal(
                ModeRedacted(
                    $"{expectedStart}{LifecycleConsole.Display(new HarnessOptions { RepoRoot = root }.EffectiveReportPath)}' does not exist"),
                line);
        }
    }

    // The text with ModeDiagnosticsRoot, a plain path that Display keeps as it is, replaced by
    // ModeDiagnosticsRootToken, which no mode prints. Both sides of a comparison go through it, so it holds
    // exactly when the raw comparison does, and the hostile part of the path below the root stays visible.
    private string ModeRedacted(string text) => text.Replace(ModeDiagnosticsRoot, ModeDiagnosticsRootToken, StringComparison.Ordinal);

    // The line starts as expected, quotes the escaped hostile text, holds none of HostileName's controls
    // as they are, and holds no character that Display escapes, so it can neither split nor drive the
    // console. The line and the text are given redacted by ModeRedacted.
    private static void AssertEscaped(string line, string expectedStart, string escapedText)
    {
        Assert.StartsWith(expectedStart, line, StringComparison.Ordinal);
        Assert.Contains(escapedText, line, StringComparison.Ordinal);
        Assert.DoesNotContain(line, c => c is '\u001B' or '\u0007' or '\u202E' or '\u061C');
        Assert.Equal(line, LifecycleConsole.Display(line));
    }

    // The printed text as lines, each with its terminator removed.
    private static List<string> ModeDiagnosticLines(string text)
    {
        Assert.True(text.EndsWith(Environment.NewLine, StringComparison.Ordinal), "the printed text ends with a line terminator");
        return [.. text[..^Environment.NewLine.Length].Split(Environment.NewLine)];
    }

    // The lines of text that end with a terminator, each with it removed; text after the last terminator,
    // which another writer of the same stream may still be completing, is left out.
    private static string[] TerminatedLines(string text) => text.Split(Environment.NewLine)[..^1];

    // Answers each of the next connections with 200 and closes it: a JSON object for the OpenAPI document
    // and an empty body for anything else. Returns each request's method and target, in arrival order.
    private static async Task<List<string>> AnswerFacadeAsync(TcpListener listener, int connections, CancellationToken ct)
    {
        var requests = new List<string>();
        for (var i = 0; i < connections; i++)
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

            var requestLine = head.ToString().Split("\r\n", 2)[0].Split(' ');
            var request = requestLine.Length >= 2 ? $"{requestLine[0]} {requestLine[1]}" : head.ToString();
            requests.Add(request);
            var body = request == $"GET {AdminRoutes.OpenApiDocument}" ? "{}" : "";
            var response = string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
            await stream.FlushAsync(ct);
        }

        return requests;
    }
}

// ---- Fixture hosts ------------------------------------------------------------------------------
// Every in-process web host of the harness tests is built by LoopbackHost, and listens on 127.0.0.1 and
// nowhere else whatever listener settings the process environment holds. The test sets process environment
// variables, which anything else in the process that read its environment meanwhile would see, so it lives in
// this class, whose collection xunit runs on its own, and it restores each variable before it ends.
public sealed partial class ReplayLifecycleTests
{
    // Bound the fixture host's graceful stop and its one request, so neither can leave the test waiting.
    private static readonly TimeSpan FixtureShutdownLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FixtureRequestLimit = TimeSpan.FromSeconds(30);

    /// <summary>The ambient listener setting a fixture host test places in the process environment.</summary>
    public enum AmbientListener
    {
        /// <summary>A <c>Kestrel:Endpoints</c> entry, which a configured host binds beside its explicit listener.</summary>
        KestrelEndpoint,

        /// <summary>
        /// <c>ASPNETCORE_URLS</c> with <c>ASPNETCORE_PREFERHOSTINGURLS</c>, which a configured host binds instead of its
        /// explicit listener.
        /// </summary>
        PreferredHostingUrl,
    }

    [Theory]
    [InlineData(AmbientListener.KestrelEndpoint)]
    [InlineData(AmbientListener.PreferredHostingUrl)]
    public async Task LoopbackHost_ListensOnlyOnItsLoopbackListener_WhateverTheEnvironmentConfigures(AmbientListener ambient)
    {
        var ct = TestContext.Current.CancellationToken;
        var ambientPort = ClosedPorts.Take(1)[0];
        var ambientUrl = string.Create(CultureInfo.InvariantCulture, $"http://{IPAddress.Any}:{ambientPort}");
        (string Name, string Value)[] settings = ambient switch
        {
            AmbientListener.KestrelEndpoint => [("Kestrel__Endpoints__Ambient__Url", ambientUrl)],
            AmbientListener.PreferredHostingUrl => [("ASPNETCORE_URLS", ambientUrl), ("ASPNETCORE_PREFERHOSTINGURLS", "true")],
            _ => throw new ArgumentOutOfRangeException(nameof(ambient), ambient, null),
        };
        var originals = settings.Select(setting => (setting.Name, Value: Environment.GetEnvironmentVariable(setting.Name))).ToArray();
        try
        {
            foreach (var (name, value) in settings)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            await using var app = LoopbackHost.CreateBuilder(FixtureShutdownLimit).Build();
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
            await app.StartAsync(ct);
            try
            {
                var addresses = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
                Assert.True(addresses.Count == 1, "the host is bound to exactly one address");
                var bound = new Uri(addresses.Single());
                Assert.True(
                    bound.Host == IPAddress.Loopback.ToString() && bound.Port > 0,
                    "the host's only address is 127.0.0.1 with an assigned port");
                Assert.True(LoopbackHost.BoundAddress(app) == bound, "the loopback host reports the host's only address");

                // A proxy from the environment must not intercept the loopback request.
                using var http = new HttpClient(
                    new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false },
                    disposeHandler: true)
                {
                    Timeout = FixtureRequestLimit,
                };
                using var response = await http.GetAsync(bound, ct);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

                // A wildcard listener on the ambient port would accept a loopback connection.
                using var probe = new TcpClient();
                var refused = await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, ambientPort, ct));
                Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);

                // No configuration provider reads the environment or a file, so neither the variables set here
                // nor a later change to an appsettings file reaches the host.
                Assert.False(
                    ConfigurationProviders((IConfigurationRoot)app.Configuration)
                        .Any(provider => provider is EnvironmentVariablesConfigurationProvider or FileConfigurationProvider),
                    "the host's configuration reads neither the environment nor a file");
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            foreach (var (name, value) in originals)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    // Every provider of the configuration, with each chained configuration replaced by its own providers.
    private static IEnumerable<IConfigurationProvider> ConfigurationProviders(IConfigurationRoot configuration) =>
        configuration.Providers.SelectMany(provider => provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot chained }
            ? ConfigurationProviders(chained)
            : [provider]);
}

/// <summary>
/// The test collection of <see cref="ReplayLifecycleTests"/>. Its standard-error test replaces the process-wide
/// <see cref="Console.Error"/>, and its fixture host test sets process environment variables, so xunit runs this
/// collection on its own, after every parallel collection and never beside a test of another class. This is a
/// collection definition only, with no tests of its own, so the harness test project keeps its six test classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideConsoleCollection
{
    /// <summary>The collection's name, which <see cref="ReplayLifecycleTests"/> names in its collection attribute.</summary>
    public const string Name = "Process-wide console";
}

// ---- Support types of the capture mode tests ----------------------------------------------------
// The capture stand-in stub serves the capture mode tests of ReplayLifecycleTests; it is an internal
// support type, not a test class.

/// <summary>
/// An in-process stand-in on 127.0.0.1 and an ephemeral port, as far as capture reaches it: <c>GET /health</c>
/// answers 200; <c>GET /admin/info</c> reports the source label, the given seed hash and reference date, the
/// reset generation and no mutations; <c>POST /admin/reset</c> increases the generation and answers 200, except
/// an empty-state reset when it is told to fail, which answers 500 and changes nothing; the WSDL fetch answers
/// 200 with the WSDL bytes it is given, as <c>text/xml</c>; and every other request, each case and a WSDL fetch
/// of a stub given no WSDL included, answers 200 with the JSON body <c>{}</c>. Every request is recorded as
/// <c>METHOD raw-target</c> before it is answered.
/// </summary>
internal sealed class CaptureStandInStub : IAsyncDisposable
{
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string WsdlContentType = "text/xml; charset=utf-8";
    private const string ResetPrefix = "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=";

    // Bounds the graceful stop; idle keep-alive connections close at once, so it is never reached.
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private static readonly string WsdlRequest = "GET " + OperationCatalogue.Default.Addresses.Soap.WsdlPathAndQuery;

    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _seedSha256;
    private readonly string _referenceDate;
    private readonly bool _failEmptyReset;
    private readonly byte[]? _wsdl;
    private WebApplication? _app;
    private long _generation;

    private CaptureStandInStub(string seedSha256, string referenceDate, bool failEmptyReset, byte[]? wsdl)
    {
        _seedSha256 = seedSha256;
        _referenceDate = referenceDate;
        _failEmptyReset = failEmptyReset;
        _wsdl = wsdl;
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
    public static Task<CaptureStandInStub> StartAsync(
        string seedSha256, string referenceDate, bool failEmptyReset, CancellationToken ct) =>
        StartAsync(seedSha256, referenceDate, failEmptyReset, wsdl: null, ct);

    /// <summary>
    /// Starts a stub reporting <paramref name="seedSha256"/> and <paramref name="referenceDate"/> that serves
    /// <paramref name="wsdl"/> to the WSDL fetch.
    /// </summary>
    /// <param name="seedSha256">The seed hash <c>/admin/info</c> reports.</param>
    /// <param name="referenceDate">The reference date <c>/admin/info</c> reports.</param>
    /// <param name="failEmptyReset">Whether an empty-state reset answers 500.</param>
    /// <param name="wsdl">The exact bytes the WSDL fetch answers with, or null for the JSON body <c>{}</c>.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running stub, which the caller disposes.</returns>
    public static async Task<CaptureStandInStub> StartAsync(
        string seedSha256, string referenceDate, bool failEmptyReset, byte[]? wsdl, CancellationToken ct)
    {
        var stub = new CaptureStandInStub(seedSha256, referenceDate, failEmptyReset, wsdl);
        var builder = LoopbackHost.CreateBuilder(ShutdownLimit);

        var app = builder.Build();
        app.Run(stub.HandleAsync);
        try
        {
            await app.StartAsync(ct);
            stub.BaseUrl = LoopbackHost.BoundAddress(app).GetLeftPart(UriPartial.Authority);
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

        // The WSDL goes out as the exact bytes given, which may on purpose be no UTF-8 text at all.
        if (_wsdl is { } wsdl && request == WsdlRequest)
        {
            await WriteAsync(context, StatusCodes.Status200OK, WsdlContentType, wsdl);
            return;
        }

        var (status, body) = Answer(request);
        await WriteAsync(context, status, JsonContentType, Encoding.UTF8.GetBytes(body));
    }

    private static async Task WriteAsync(HttpContext context, int status, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
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

/// <summary>
/// Physical confinement of the replay's artifact and report writers against real files: every test owns a
/// fresh temporary directory holding a repository root and a protected directory outside it, and checks that
/// no recreate, write or delete through <see cref="ConfinedTree"/> reaches the protected directory through a
/// symbolic link, whether the link sits on <c>artifacts/</c>, on a directory below it, at the target, inside a
/// cleared directory, dangles, is a hard link or is swapped in mid-way; that a published file replaces only a
/// regular file and never opens or follows the entry at its name, a FIFO included; that a file published
/// create-only, as capture publishes the contracts WSDL, keeps a regular file at its name and refuses every
/// other entry there and every linked directory above it; that a read returns a regular file whole, at most
/// its byte limit, finds nothing where no file is, and refuses a FIFO, a link on the way or at the name and an
/// oversized file at once, without reading the protected directory; that paths not strictly
/// below the root are refused; and that <see cref="DiscrepancySignature.ArtifactFileName"/> keeps an item id
/// byte for byte. The repository's own tree is never touched.
/// </summary>
public sealed partial class ReplayLifecycleTests
{
    // The confinement tests' sandbox: a fresh temporary directory per test, created on first use, holding a
    // repository root and a protected directory outside it. The run sets TMPDIR to a per-clone directory, so
    // parallel checkouts never share one; Dispose removes it.
    private string? _confinedTemp;
    private ConfinedTree? _confinedTree;

    private const string LinkRefusal = "ConfinedTree never writes or deletes through a link";
    private const string OutsideRefusal = "ConfinedTree writes and deletes only below its root";
    private const string OutsideReadRefusal = "ConfinedTree reads only below its root";

    // The file the read tests read, two directories below the root as harness.json is.
    private const string ReadTarget = "harness/config/harness.json";

    // A refused read allocates its exception and message, never a buffer the length of the file.
    private const long RefusalAllocationBound = 1024 * 1024;
    private const string DirectoryRefusal = "it is a directory.";
    private const string NotRegularFileRefusal = "it is not a regular file, so it is left in place.";
    private const string PublishedName = "RESULTS.md";
    private const string SentinelName = "SENTINEL";

    // The file capture publishes create-only, two directories below the root.
    private const string NewFileName = "contracts/wsdl/ErewhonWebService.wsdl";

    // Rounds of every recreate, write and delete the swap test runs while its directories are swapped.
    private const int SwapIterations = 300;

    // The publish race runs at least this many publications, and keeps publishing until the swapper has put at
    // least this many FIFOs and links on the name.
    private const int MinimumRacePublications = 100;
    private const int MinimumRaceSwaps = 60;

    // A publication that has not ended by then is blocked, which fails the test instead of hanging it.
    private static readonly TimeSpan PromptLimit = TimeSpan.FromSeconds(10);

    // The directories the swap test replaces with links: one a cleared directory's subdirectory and a written
    // file's parent, the other a recreated directory itself.
    private static readonly string[] SwappedDirectories = ["artifacts/replay/rest", "artifacts/discrepancies"];

    // What WithoutPublishingGuid shows in place of a publication's temporary file name.
    private const string PublishingTemporaryToken = ".<random>.publishing";

    private static readonly byte[] SentinelBytes = "protected content outside the repository\n"u8.ToArray();
    private static readonly byte[] FirstBytes = "{\"first\":\"a longer document than the second\"}"u8.ToArray();
    private static readonly byte[] SecondBytes = "{\"second\":2}"u8.ToArray();

    // FIFOs and links the publish race's swapper has put on the published name.
    private int _confinedRaceSwaps;

    /// <summary>Each linked directory above a recreated target, with the target.</summary>
    public static TheoryData<string, string> LinkedAncestors { get; } = new()
    {
        { "artifacts", "artifacts/discrepancies" },
        { "artifacts", "artifacts/replay" },
        { "artifacts/replay", "artifacts/replay/rest" },
    };

    /// <summary>Each linked directory above a written file, written synchronously and asynchronously.</summary>
    public static TheoryData<string, bool> LinkedWriteAncestors { get; } = new()
    {
        { "artifacts", false },
        { "artifacts", true },
        { "artifacts/replay", false },
        { "artifacts/replay", true },
    };

    /// <summary>A dangling or a live link at a written file, written synchronously and asynchronously.</summary>
    public static TheoryData<bool, bool> LinkAtFileName { get; } = new()
    {
        { false, false },
        { false, true },
        { true, false },
        { true, true },
    };

    /// <summary>Item ids that name their discrepancy artifact exactly, the longest allowed name included.</summary>
    public static TheoryData<string> ArtifactItemIds { get; } =
    [
        "CASE:REST-getAgent-01",
        "CASE:SOAP-getPhoto-02",
        "CHK-OPENAPI-DRIFT",
        "CHK-WSDL-CONTRACT",
        new string('a', 250),
    ];

    /// <summary>
    /// The forms of item id that cannot be one safe Linux file name, by name, so that test names stay printable;
    /// <see cref="UnusableItemId"/> builds each id.
    /// </summary>
    public static TheoryData<string> UnusableItemIdForms { get; } =
    [
        "null",
        "empty",
        "dot",
        "dot-dot",
        "slash",
        "backslash",
        "nul",
        "line-feed",
        "delete",
        "c1-control",
        "lone-surrogate",
        "ascii-name-over-255-bytes",
        "utf8-name-over-255-bytes",
    ];

    /// <summary>Each entry at a published file's name that is refused and kept, with the refusal it gives.</summary>
    public static TheoryData<string, string> RefusedPublishTargets { get; } = new()
    {
        { "fifo", NotRegularFileRefusal },
        { "file-link", NotRegularFileRefusal },
        { "dangling-link", NotRegularFileRefusal },
        { "directory-link", NotRegularFileRefusal },
        { "directory", DirectoryRefusal },
    };

    // ---- Recreating a directory ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(LinkedAncestors))]
    public void RecreateDirectory_LinkedAncestor_IsRefusedAndTheOutsideIsIntact(string linkedDirectory, string target)
    {
        ProtectedTree();
        LinkToOutside(linkedDirectory);
        var before = ConfinedSnapshot(ConfinedOutside);

        AssertLinkRefusal(Assert.Throws<IOException>(() => ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull(target))), linkedDirectory);

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(IsLink(linkedDirectory), "A refused recreate must leave the linked directory in place.");
    }

    [Fact]
    public void RecreateDirectory_FileAboveTheTarget_IsRefusedAndKept()
    {
        File.WriteAllBytes(ConfinedFull("artifacts"), SentinelBytes);

        var refusal = Assert.Throws<IOException>(() => ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull("artifacts/discrepancies")));

        Assert.Contains("'artifacts': it is not a directory", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(SentinelBytes, File.ReadAllBytes(ConfinedFull("artifacts")));
    }

    [Theory]
    [InlineData("directory-link")]
    [InlineData("file-link")]
    [InlineData("dangling-link")]
    [InlineData("file")]
    [InlineData("absent")]
    public void RecreateDirectory_EntryAtTheTarget_IsRemovedItselfAndAnEmptyDirectoryCreated(string entry)
    {
        ProtectedTree();
        Directory.CreateDirectory(ConfinedFull("artifacts"));
        var target = ConfinedFull("artifacts/discrepancies");
        switch (entry)
        {
            case "directory-link":
                Directory.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, "discrepancies"));
                break;
            case "file-link":
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, SentinelName));
                break;
            case "dangling-link":
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, "created-through-link"));
                break;
            case "file":
                File.WriteAllBytes(target, SentinelBytes);
                break;
        }

        var before = ConfinedSnapshot(ConfinedOutside);

        ConfinedTreeUnderTest.RecreateDirectory(target);

        Assert.False(IsLink("artifacts/discrepancies"), "The recreated entry must be a real directory, not a link.");
        Assert.True(Directory.Exists(target), "The recreated entry must be a directory.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(target).Select(ConfinedRelative));
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    [Fact]
    public void RecreateDirectory_ClearsNestedEntries_WithoutFollowingLinks()
    {
        ProtectedTree();
        var outsideFile = Path.Combine(ConfinedOutside, SentinelName);
        Directory.CreateDirectory(ConfinedFull("artifacts/replay/rest/deeper"));
        Directory.CreateDirectory(ConfinedFull("artifacts/replay/soap"));
        File.WriteAllBytes(ConfinedFull("artifacts/replay/rest/001-getAgent-01.body"), FirstBytes);
        File.WriteAllBytes(ConfinedFull("artifacts/replay/rest/deeper/.hidden"), FirstBytes);
        Directory.CreateSymbolicLink(ConfinedFull("artifacts/replay/soap/directory-link"), ConfinedOutside);
        File.CreateSymbolicLink(ConfinedFull("artifacts/replay/soap/file-link"), outsideFile);
        File.CreateSymbolicLink(ConfinedFull("artifacts/replay/rest/deeper/dangling-link"), Path.Combine(ConfinedOutside, "missing"));
        CreateHardLink(ConfinedFull("artifacts/replay/soap/hard-link"), outsideFile);
        var before = ConfinedSnapshot(ConfinedOutside);

        ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull("artifacts/replay"));

        Assert.True(Directory.Exists(ConfinedFull("artifacts/replay")), "The cleared directory must exist again.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfinedFull("artifacts/replay")).Select(ConfinedRelative));
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    // ---- Writing a file -----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFile_CreatesMissingDirectories_AndReplacesTheFileExactly(bool useAsync)
    {
        const string file = "artifacts/replay/rest/001-getAgent-01.body";

        await ConfinedWrite(file, FirstBytes, useAsync);
        await ConfinedWrite(file, SecondBytes, useAsync);

        Assert.Equal(SecondBytes, await File.ReadAllBytesAsync(ConfinedFull(file), TestCancellation));
        Assert.Equal([file], Directory.EnumerateFiles(ConfinedRoot, "*", SearchOption.AllDirectories).Select(ConfinedRelative));
    }

    [Theory]
    [MemberData(nameof(LinkedWriteAncestors))]
    public async Task WriteFile_ThroughLinkedAncestor_IsRefusedAndWritesNothingOutside(string linkedDirectory, bool useAsync)
    {
        ProtectedTree();
        LinkToOutside(linkedDirectory);
        var before = ConfinedSnapshot(ConfinedOutside);

        foreach (var file in new[] { "artifacts/replay/rest/001-getAgent-01.body", "artifacts/replay/new/002-getAgent-02.body" })
        {
            AssertLinkRefusal(await Assert.ThrowsAsync<IOException>(() => ConfinedWrite(file, FirstBytes, useAsync)), linkedDirectory);
        }

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    [Theory]
    [MemberData(nameof(LinkAtFileName))]
    public async Task WriteFile_LinkAtTheFileName_IsRefusedAndItsTargetUnchanged(bool dangling, bool useAsync)
    {
        ProtectedTree();
        const string file = "artifacts/discrepancies/CASE:REST-getAgent-01.json";
        var target = Path.Combine(ConfinedOutside, dangling ? "created-through-link" : SentinelName);
        Directory.CreateDirectory(ConfinedFull("artifacts/discrepancies"));
        File.CreateSymbolicLink(ConfinedFull(file), target);
        var before = ConfinedSnapshot(ConfinedOutside);

        AssertLinkRefusal(await Assert.ThrowsAsync<IOException>(() => ConfinedWrite(file, FirstBytes, useAsync)), file);

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(IsLink(file), "A refused write must leave the link in place.");
    }

    // A hard link is no symbolic link, so the write is allowed; it must replace the entry, never write through it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFile_HardLinkAtTheFileName_IsReplacedWithoutChangingTheOutsideFile(bool useAsync)
    {
        ProtectedTree();
        const string file = "artifacts/openapi/live.json";
        var outsideFile = Path.Combine(ConfinedOutside, SentinelName);
        Directory.CreateDirectory(ConfinedFull("artifacts/openapi"));
        CreateHardLink(ConfinedFull(file), outsideFile);

        await ConfinedWrite(file, SecondBytes, useAsync);

        Assert.Equal(SecondBytes, await File.ReadAllBytesAsync(ConfinedFull(file), TestCancellation));
        Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(outsideFile, TestCancellation));
    }

    [Fact]
    public async Task WriteFileAsync_CancelledBeforeTheWrite_LeavesTheTreeUntouched()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConfinedTreeUnderTest.WriteFileAsync(ConfinedFull("artifacts/wsdl/facade.wsdl"), FirstBytes, cancelled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfinedRoot).Select(ConfinedRelative));
    }

    // ---- Publishing a file --------------------------------------------------------------------------

    [Fact]
    public void PublishFile_CreatesMissingDirectories_AndReplacesARegularFileWhole()
    {
        const string file = "artifacts/report/" + PublishedName;
        Assert.True(ConfinedTreeUnderTest.Contains(ConfinedFull(file)), "A path below the root must be contained.");

        ConfinedTreeUnderTest.PublishFile(ConfinedFull(file), FirstBytes);
        ConfinedTreeUnderTest.PublishFile(ConfinedFull(file), SecondBytes);

        Assert.Equal(SecondBytes, File.ReadAllBytes(ConfinedFull(file)));
        Assert.Equal([PublishedName], ShownNames(ConfinedFull("artifacts/report")));
    }

    // A hard link is a regular file, so it is replaced; the outside file it shares an inode with keeps its content.
    [Fact]
    public void PublishFile_HardLinkAtTheName_IsReplacedWithoutChangingTheOutsideFile()
    {
        ProtectedTree();
        var outsideFile = Path.Combine(ConfinedOutside, SentinelName);
        CreateHardLink(ConfinedFull(PublishedName), outsideFile);

        ConfinedTreeUnderTest.PublishFile(ConfinedFull(PublishedName), SecondBytes);

        Assert.Equal(SecondBytes, File.ReadAllBytes(ConfinedFull(PublishedName)));
        Assert.Equal(SentinelBytes, File.ReadAllBytes(outsideFile));
        Assert.Equal([PublishedName], ShownNames(ConfinedRoot));
    }

    // The entry is examined, never opened: an unread FIFO would otherwise block the call for good, so the call
    // runs off the test's thread and must end within the prompt limit.
    [Theory]
    [MemberData(nameof(RefusedPublishTargets))]
    public async Task PublishFile_EntryThatIsNotARegularFile_IsRefusedPromptlyAndKept(string entry, string refusal)
    {
        ProtectedTree();
        const string name = "harness";
        var target = ConfinedFull(name);
        PlaceRefusedPublishTarget(entry, target);
        var before = ConfinedSnapshot(ConfinedOutside);

        var publish = Task.Run(() => ConfinedTreeUnderTest.PublishFile(target, FirstBytes), TestCancellation);
        var failure = await Assert.ThrowsAsync<IOException>(() => publish.WaitAsync(PromptLimit, TestCancellation));

        Assert.Contains($"ConfinedTree could not publish '{name}': {refusal}", failure.Message, StringComparison.Ordinal);
        Assert.Equal([name], ShownNames(ConfinedRoot));
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(RefusedPublishTargetKept(entry, name), "A refused publication must leave the entry at the name as it was.");
    }

    [Theory]
    [InlineData("artifacts")]
    [InlineData("artifacts/report")]
    public void PublishFile_ThroughLinkedAncestor_IsRefusedAndWritesNothingOutside(string linkedDirectory)
    {
        ProtectedTree();
        LinkToOutside(linkedDirectory);
        var before = ConfinedSnapshot(ConfinedOutside);

        AssertLinkRefusal(
            Assert.Throws<IOException>(() => ConfinedTreeUnderTest.PublishFile(ConfinedFull("artifacts/report/" + PublishedName), FirstBytes)),
            linkedDirectory);

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(IsLink(linkedDirectory), "A refused publication must leave the linked directory in place.");
    }

    // An entry swapped in at the name between the check and the rename is replaced as the entry it is: a FIFO is
    // never opened, so no publication blocks, and a link is never followed. A background thread keeps moving a
    // fresh FIFO and a link to the protected sentinel onto the name while the publications run. Which of them
    // succeed depends on timing, so only that each ends promptly, succeeding or refused with an I/O error, that
    // nothing outside changes and that no temporary file is left is asserted.
    [Fact]
    public async Task PublishFile_RacingAFifoOrLinkSwappedInAtTheName_NeverBlocksOrFollows()
    {
        ProtectedTree();
        var target = ConfinedFull(PublishedName);
        var before = ConfinedSnapshot(ConfinedOutside);

        Exception? swapFailure = null;
        using var stop = new CancellationTokenSource();
        var swapper = new Thread(() => swapFailure = SwapSpecialEntriesOnto(target, stop.Token)) { IsBackground = true };
        swapper.Start();
        try
        {
            for (var publications = 0;
                publications < MinimumRacePublications || (Volatile.Read(ref _confinedRaceSwaps) < MinimumRaceSwaps && swapper.IsAlive);
                publications++)
            {
                var outcome = await Outcome(
                    () => Task.Run(() => ConfinedTreeUnderTest.PublishFile(target, FirstBytes), TestCancellation).WaitAsync(PromptLimit, TestCancellation));
                Assert.True(
                    outcome is null or IOException,
                    "A publication racing a swap must end promptly, succeeding or refused with an I/O error.");
            }
        }
        finally
        {
            await stop.CancelAsync();
            swapper.Join();
        }

        // An exception's text names the temporary root, so only its type is shown.
        Assert.True(swapFailure is null, $"The swapper must not fail, but it threw {swapFailure?.GetType().FullName}.");
        Assert.True(Volatile.Read(ref _confinedRaceSwaps) >= MinimumRaceSwaps, "The swapper must have raced the publications.");
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.DoesNotContain(ShownNames(ConfinedRoot), name => name.EndsWith(".publishing", StringComparison.Ordinal));
    }

    // ---- Publishing a new file ----------------------------------------------------------------------

    [Fact]
    public async Task PublishNewFileAsync_CreatesMissingDirectories_AndWritesTheBytesExactly()
    {
        Assert.True(await ConfinedTreeUnderTest.PublishNewFileAsync(ConfinedFull(NewFileName), FirstBytes, TestCancellation));

        Assert.Equal(FirstBytes, await File.ReadAllBytesAsync(ConfinedFull(NewFileName), TestCancellation));
        Assert.Equal([Path.GetFileName(NewFileName)], ShownNames(ConfinedFull("contracts/wsdl")));
    }

    // A regular file at the name is kept byte for byte, a hard link to a file outside included, which is neither
    // replaced nor written through.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishNewFileAsync_RegularFileAtTheName_IsKeptAndReportedAsPresent(bool hardLink)
    {
        ProtectedTree();
        Directory.CreateDirectory(ConfinedFull("contracts/wsdl"));
        if (hardLink)
        {
            CreateHardLink(ConfinedFull(NewFileName), Path.Combine(ConfinedOutside, SentinelName));
        }
        else
        {
            await File.WriteAllBytesAsync(ConfinedFull(NewFileName), SentinelBytes, TestCancellation);
        }

        var before = ConfinedSnapshot(ConfinedOutside);

        Assert.False(await ConfinedTreeUnderTest.PublishNewFileAsync(ConfinedFull(NewFileName), FirstBytes, TestCancellation));

        Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(ConfinedFull(NewFileName), TestCancellation));
        Assert.Equal([Path.GetFileName(NewFileName)], ShownNames(ConfinedFull("contracts/wsdl")));
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    [Theory]
    [InlineData("contracts")]
    [InlineData("contracts/wsdl")]
    public async Task PublishNewFileAsync_ThroughLinkedAncestor_IsRefusedAndWritesNothingOutside(string linkedDirectory)
    {
        ProtectedTree();
        LinkToOutside(linkedDirectory);
        var before = ConfinedSnapshot(ConfinedOutside);

        AssertLinkRefusal(
            await Assert.ThrowsAsync<IOException>(
                () => ConfinedTreeUnderTest.PublishNewFileAsync(ConfinedFull(NewFileName), FirstBytes, TestCancellation)),
            linkedDirectory);

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(IsLink(linkedDirectory), "A refused publication must leave the linked directory in place.");
    }

    // The entry is examined, never opened, as for PublishFile: an unread FIFO would otherwise block the call for
    // good, so the call must end within the prompt limit.
    [Theory]
    [MemberData(nameof(RefusedPublishTargets))]
    public async Task PublishNewFileAsync_EntryThatIsNotARegularFile_IsRefusedPromptlyAndKept(string entry, string refusal)
    {
        ProtectedTree();
        const string name = "harness";
        var target = ConfinedFull(name);
        PlaceRefusedPublishTarget(entry, target);
        var before = ConfinedSnapshot(ConfinedOutside);

        var publish = Task.Run(() => ConfinedTreeUnderTest.PublishNewFileAsync(target, FirstBytes, TestCancellation), TestCancellation);
        var failure = await Assert.ThrowsAsync<IOException>(() => publish.WaitAsync(PromptLimit, TestCancellation));

        Assert.Contains($"ConfinedTree could not publish '{name}': {refusal}", failure.Message, StringComparison.Ordinal);
        Assert.Equal([name], ShownNames(ConfinedRoot));
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(RefusedPublishTargetKept(entry, name), "A refused publication must leave the entry at the name as it was.");
    }

    [Fact]
    public async Task PublishNewFileAsync_CancelledBeforeTheCall_LeavesTheTreeUntouched()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConfinedTreeUnderTest.PublishNewFileAsync(ConfinedFull(NewFileName), FirstBytes, cancelled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfinedRoot).Select(ConfinedRelative));
    }

    // ---- Deleting a file ----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteFile_LinkAtTheTarget_IsRemovedItself(bool dangling)
    {
        ProtectedTree();
        const string file = "artifacts/wsdl/backend.wsdl";
        Directory.CreateDirectory(ConfinedFull("artifacts/wsdl"));
        File.CreateSymbolicLink(ConfinedFull(file), Path.Combine(ConfinedOutside, dangling ? "missing" : SentinelName));
        var before = ConfinedSnapshot(ConfinedOutside);

        ConfinedTreeUnderTest.DeleteFile(ConfinedFull(file));

        Assert.False(File.Exists(ConfinedFull(file)) || IsLink(file), "The link itself must be removed.");
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    [Fact]
    public void DeleteFile_ThroughLinkedAncestor_IsRefusedAndDeletesNothingOutside()
    {
        ProtectedTree();
        LinkToOutside("artifacts");
        var before = ConfinedSnapshot(ConfinedOutside);

        AssertLinkRefusal(
            Assert.Throws<IOException>(() => ConfinedTreeUnderTest.DeleteFile(ConfinedFull($"artifacts/discrepancies/{SentinelName}"))),
            "artifacts");

        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
    }

    [Fact]
    public void DeleteFile_MissingDirectoryOrDirectoryAtTheTarget_CreatesAndRemovesNothing()
    {
        ConfinedTreeUnderTest.DeleteFile(ConfinedFull("artifacts/openapi/live.json"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfinedRoot).Select(ConfinedRelative));

        Directory.CreateDirectory(ConfinedFull("artifacts/openapi/live.json"));
        ConfinedTreeUnderTest.DeleteFile(ConfinedFull("artifacts/openapi/live.json"));
        Assert.True(Directory.Exists(ConfinedFull("artifacts/openapi/live.json")), "A directory at the target must be kept.");
    }

    // ---- Replacement races --------------------------------------------------------------------------

    // A directory swapped for a link while the tree works must never redirect it. A background thread keeps
    // moving artifacts/replay/rest and artifacts/discrepancies aside, putting a link to the protected directory
    // in each place and moving the directory back, while every recreate, write and delete runs against the
    // moving tree. Which operations succeed depends on timing, so only that each one succeeds or fails with an
    // I/O error, and that the protected directory is unchanged, is asserted.
    [Fact]
    public async Task ConcurrentSwap_NeverClearsWritesOrDeletesThroughTheSwappedInLink()
    {
        ProtectedTree();
        File.WriteAllBytes(Path.Combine(ConfinedOutside, "001-getAgent-01.body"), SentinelBytes);
        File.WriteAllBytes(Path.Combine(ConfinedOutside, "CASE-REST-getAgent-01.json"), SentinelBytes);
        ConfinedTreeUnderTest.WriteFile(ConfinedFull("artifacts/replay/rest/001-getAgent-01.body"), FirstBytes);
        ConfinedTreeUnderTest.WriteFile(ConfinedFull("artifacts/discrepancies/CASE-REST-getAgent-01.json"), FirstBytes);
        var before = ConfinedSnapshot(ConfinedOutside);
        Func<Task>[] operations =
        [
            () => ConfinedWrite("artifacts/replay/rest/001-getAgent-01.body", FirstBytes, useAsync: false),
            () => ConfinedWrite("artifacts/discrepancies/CASE-REST-getAgent-01.json", FirstBytes, useAsync: true),
            () => Synchronously(() => ConfinedTreeUnderTest.DeleteFile(ConfinedFull("artifacts/replay/rest/001-getAgent-01.body"))),
            () => Synchronously(() => ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull("artifacts/replay"))),
            () => Synchronously(() => ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull("artifacts/discrepancies"))),
        ];

        Exception? swapFailure = null;
        using var stop = new CancellationTokenSource();
        var swapper = new Thread(() => swapFailure = SwapForLinks(stop.Token)) { IsBackground = true };
        swapper.Start();
        try
        {
            for (var iteration = 0; iteration < SwapIterations; iteration++)
            {
                foreach (var operation in operations)
                {
                    var outcome = await Outcome(operation);
                    Assert.True(
                        outcome is null or IOException,
                        "An operation racing a swap must succeed or fail with an I/O error.");
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            swapper.Join();
            RemoveSwappedInLinks();
        }

        // An exception's text names the temporary root, so only its type is shown.
        Assert.True(swapFailure is null, $"The swapper must not fail, but it threw {swapFailure?.GetType().FullName}.");
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        ConfinedTreeUnderTest.RecreateDirectory(ConfinedFull("artifacts/replay"));
        ConfinedTreeUnderTest.WriteFile(ConfinedFull("artifacts/replay/rest/001-getAgent-01.body"), SecondBytes);
        Assert.Equal(SecondBytes, await File.ReadAllBytesAsync(ConfinedFull("artifacts/replay/rest/001-getAgent-01.body"), TestCancellation));
    }

    // ---- Reading a file -----------------------------------------------------------------------------

    // A regular file at or below the limit is read whole, an empty one, one exactly at a small limit and one
    // longer than the first read buffer included.
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(5, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(200_000, 200_000, false)]
    [InlineData(200_000, 200_000, true)]
    [InlineData(200_000, HarnessOptions.EvidenceByteLimit, false)]
    [InlineData(200_000, HarnessOptions.EvidenceByteLimit, true)]
    public async Task ReadFile_RegularFileWithinTheLimit_ReturnsEveryByte(int length, int byteLimit, bool useAsync)
    {
        var content = Enumerable.Range(0, length).Select(static index => (byte)(index % 251)).ToArray();
        var path = ConfinedFull(ReadTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        Assert.Equal(content, await ConfinedRead(ConfinedTreeUnderTest, path, byteLimit, useAsync));
        Assert.Equal(content, File.ReadAllBytes(path));
    }

    // A sparse file reports its length without its bytes being written, so the largest case costs no disk. The
    // reported length decides before any buffer exists: the refusal allocates far less on this thread than the
    // file is long.
    [Theory]
    [InlineData(1L, 0, false)]
    [InlineData(1L, 0, true)]
    [InlineData(6L, 5, false)]
    [InlineData(6L, 5, true)]
    [InlineData(HarnessOptions.EvidenceByteLimit + 1L, HarnessOptions.EvidenceByteLimit, false)]
    [InlineData(HarnessOptions.EvidenceByteLimit + 1L, HarnessOptions.EvidenceByteLimit, true)]
    public async Task ReadFile_FileOverTheLimit_IsRefusedBeforeItIsRead(long length, int byteLimit, bool useAsync)
    {
        var path = ConfinedFull(ReadTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write))
        {
            RandomAccess.SetLength(handle, length);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var refusal = await Assert.ThrowsAsync<IOException>(() => ConfinedRead(ConfinedTreeUnderTest, path, byteLimit, useAsync));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal($"ConfinedTree could not read '{ReadTarget}': it exceeds the {byteLimit}-byte limit.", refusal.Message);
        Assert.True(allocated < RefusalAllocationBound, $"The refusal allocated {allocated} bytes, as if it had read the file.");
    }

    // A missing root, a missing directory on the way, a missing file and a directory at the name are nothing to
    // read, and the read creates nothing on the way.
    [Theory]
    [InlineData("missing-root", false)]
    [InlineData("missing-root", true)]
    [InlineData("missing-directory", false)]
    [InlineData("missing-directory", true)]
    [InlineData("missing-file", false)]
    [InlineData("missing-file", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    public async Task ReadFile_NothingThere_ReturnsNullAndCreatesNothing(string entry, bool useAsync)
    {
        var root = entry == "missing-root" ? Path.Combine(ConfinedTemp, "absent-root") : ConfinedRoot;
        switch (entry)
        {
            case "missing-file":
                Directory.CreateDirectory(Path.GetDirectoryName(ConfinedFull(ReadTarget))!);
                break;
            case "directory":
                Directory.CreateDirectory(ConfinedFull(ReadTarget));
                break;
        }

        var before = ConfinedSnapshot(ConfinedTemp);

        var bytes = await ConfinedRead(new ConfinedTree(root), Path.Combine(root, ReadTarget), HarnessOptions.EvidenceByteLimit, useAsync);

        Assert.Null(bytes);
        Assert.Equal(before, ConfinedSnapshot(ConfinedTemp));
    }

    // A FIFO without a writer, a link to one, a link at the name, dangling or not, and a linked directory on the
    // way each refuse the read at once, read nothing outside the root and stay in place.
    [Theory]
    [InlineData("fifo", false)]
    [InlineData("fifo", true)]
    [InlineData("link-to-fifo", false)]
    [InlineData("link-to-fifo", true)]
    [InlineData("file-link", false)]
    [InlineData("file-link", true)]
    [InlineData("dangling-link", false)]
    [InlineData("dangling-link", true)]
    [InlineData("linked-parent", false)]
    [InlineData("linked-parent", true)]
    [InlineData("linked-ancestor", false)]
    [InlineData("linked-ancestor", true)]
    public async Task ReadFile_SpecialFileOrLink_IsRefusedPromptlyAndLeftInPlace(string entry, bool useAsync)
    {
        ProtectedTree();
        var (expected, kept) = PlaceRefusedReadEntry(entry);
        var before = ConfinedSnapshot(ConfinedOutside);
        var path = ConfinedFull(ReadTarget);

        var read = Task.Run(() => ConfinedRead(ConfinedTreeUnderTest, path, HarnessOptions.EvidenceByteLimit, useAsync), TestCancellation);
        var refusal = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(PromptLimit, TestCancellation));

        Assert.Equal(expected, refusal.Message);
        Assert.Equal(before, ConfinedSnapshot(ConfinedOutside));
        Assert.True(kept(), "A refused read must leave the entry as it was.");
    }

    // ---- Lexical containment ------------------------------------------------------------------------

    [Theory]
    [InlineData(".")]
    [InlineData("artifacts/..")]
    [InlineData("..")]
    [InlineData("../outside/SENTINEL")]
    [InlineData("artifacts/../../outside/discrepancies")]
    [InlineData("../repo-sibling/artifacts")]
    [InlineData("/")]
    public async Task PathNotStrictlyBelowTheRoot_IsRefusedByEveryOperation(string relative)
    {
        ProtectedTree();
        var path = Path.Combine(ConfinedRoot, relative);
        var before = ConfinedSnapshot(ConfinedTemp);

        Assert.Contains(OutsideRefusal, Assert.Throws<IOException>(() => ConfinedTreeUnderTest.RecreateDirectory(path)).Message, StringComparison.Ordinal);
        Assert.Contains(OutsideRefusal, Assert.Throws<IOException>(() => ConfinedTreeUnderTest.WriteFile(path, FirstBytes)).Message, StringComparison.Ordinal);
        Assert.Contains(
            OutsideRefusal,
            (await Assert.ThrowsAsync<IOException>(() => ConfinedTreeUnderTest.WriteFileAsync(path, FirstBytes, TestCancellation))).Message,
            StringComparison.Ordinal);
        Assert.Contains(OutsideRefusal, Assert.Throws<IOException>(() => ConfinedTreeUnderTest.DeleteFile(path)).Message, StringComparison.Ordinal);
        Assert.Contains(OutsideRefusal, Assert.Throws<IOException>(() => ConfinedTreeUnderTest.PublishFile(path, FirstBytes)).Message, StringComparison.Ordinal);
        Assert.Contains(
            OutsideRefusal,
            (await Assert.ThrowsAsync<IOException>(() => ConfinedTreeUnderTest.PublishNewFileAsync(path, FirstBytes, TestCancellation))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            OutsideReadRefusal,
            Assert.Throws<IOException>(() => ConfinedTreeUnderTest.ReadFile(path, HarnessOptions.EvidenceByteLimit)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            OutsideReadRefusal,
            (await Assert.ThrowsAsync<IOException>(
                () => ConfinedTreeUnderTest.ReadFileAsync(path, HarnessOptions.EvidenceByteLimit, TestCancellation))).Message,
            StringComparison.Ordinal);
        Assert.False(ConfinedTreeUnderTest.Contains(path), "A path not strictly below the root must not be contained.");

        Assert.Equal(before, ConfinedSnapshot(ConfinedTemp));
    }

    // ---- Discrepancy artifact names -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ArtifactItemIds))]
    public void ArtifactFileName_KeepsTheItemIdByteForByte(string itemId)
    {
        var expected = itemId + ".json";

        Assert.Equal(expected, DiscrepancySignature.ArtifactFileName(itemId));
        Assert.True(DiscrepancySignature.TryArtifactFileName(itemId, out var fileName), "A usable item id must name its artifact.");
        Assert.Equal(expected, fileName);

        ConfinedTreeUnderTest.WriteFile(ConfinedFull($"artifacts/discrepancies/{fileName}"), FirstBytes);
        Assert.Equal(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(Assert.Single(Directory.EnumerateFiles(ConfinedFull("artifacts/discrepancies")).Select(Path.GetFileName))!));
    }

    [Theory]
    [MemberData(nameof(UnusableItemIdForms))]
    public void ArtifactFileName_RefusesAnIdThatIsNotOneSafeFileName(string form)
    {
        var itemId = UnusableItemId(form);

        Assert.ThrowsAny<ArgumentException>(() => DiscrepancySignature.ArtifactFileName(itemId!));
        Assert.False(DiscrepancySignature.TryArtifactFileName(itemId, out var fileName), "An unusable item id must name no artifact.");
        Assert.Null(fileName);
    }

    // 251 ASCII characters, or 126 two-byte characters, give a name of 256 or 257 UTF-8 bytes with ".json".
    private static string? UnusableItemId(string form) => form switch
    {
        "null" => null,
        "empty" => "",
        "dot" => ".",
        "dot-dot" => "..",
        "slash" => "CASE:REST/getAgent-01",
        "backslash" => "CASE:REST\\getAgent-01",
        "nul" => "CASE:REST-getAgent-01\0",
        "line-feed" => "CASE:REST-getAgent-01\n",
        "delete" => "CASE:REST-getAgent-01\u007F",
        "c1-control" => "CASE:REST-getAgent-01\u0085",
        "lone-surrogate" => "CASE:REST-getAgent-\uD800",
        "ascii-name-over-255-bytes" => new string('a', 251),
        "utf8-name-over-255-bytes" => new string('\u00E9', 126),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Unknown item id form."),
    };

    // Puts one of the RefusedPublishTargets entries at target.
    private void PlaceRefusedPublishTarget(string entry, string target)
    {
        switch (entry)
        {
            case "fifo":
                SpecialFileSupport.CreateFifo(target);
                break;
            case "file-link":
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, SentinelName));
                break;
            case "dangling-link":
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, "created-through-link"));
                break;
            case "directory-link":
                Directory.CreateSymbolicLink(target, ConfinedOutside);
                break;
            case "directory":
                Directory.CreateDirectory(target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown publish target entry.");
        }
    }

    // Whether the entry PlaceRefusedPublishTarget put at the name below the root is still there as it was.
    private bool RefusedPublishTargetKept(string entry, string name)
    {
        var target = ConfinedFull(name);
        return entry switch
        {
            "fifo" => SpecialFileSupport.IsFifo(target),
            "directory" => !IsLink(name) && Directory.Exists(target) && SpecialFileSupport.Names(target).Count == 0,
            _ => IsLink(name),
        };
    }

    // Reads path through tree, synchronously or not; the synchronous form's outcome is returned as a task.
    private static async Task<byte[]?> ConfinedRead(ConfinedTree tree, string path, int byteLimit, bool useAsync) =>
        useAsync ? await tree.ReadFileAsync(path, byteLimit, TestCancellation) : tree.ReadFile(path, byteLimit);

    // Puts one of the refused read entries on the way to ReadTarget, each a real escape route or a FIFO nobody
    // writes to, and returns the refusal the read must give and a check that the entry is still in place. A
    // FIFO stays below the root, so no snapshot of the protected directory ever opens it.
    private (string Refusal, Func<bool> Kept) PlaceRefusedReadEntry(string entry)
    {
        var target = ConfinedFull(ReadTarget);
        var parent = Path.GetDirectoryName(target)!;
        var linkAtName = $"ConfinedTree never reads through a link: refused '{ReadTarget}', because '{ReadTarget}' is a symbolic link.";
        switch (entry)
        {
            case "fifo":
                Directory.CreateDirectory(parent);
                SpecialFileSupport.CreateFifo(target);
                return ($"ConfinedTree could not read '{ReadTarget}': it is not a regular file (a FIFO).", () => SpecialFileSupport.IsFifo(target));
            case "link-to-fifo":
                Directory.CreateDirectory(parent);
                SpecialFileSupport.CreateFifo(Path.Combine(parent, "pipe"));
                File.CreateSymbolicLink(target, Path.Combine(parent, "pipe"));
                return (linkAtName, () => IsLink(ReadTarget) && SpecialFileSupport.IsFifo(Path.Combine(parent, "pipe")));
            case "file-link":
                Directory.CreateDirectory(parent);
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, SentinelName));
                return (linkAtName, () => IsLink(ReadTarget));
            case "dangling-link":
                Directory.CreateDirectory(parent);
                File.CreateSymbolicLink(target, Path.Combine(ConfinedOutside, "absent"));
                return (linkAtName, () => IsLink(ReadTarget));
            case "linked-parent":
                File.WriteAllBytes(Path.Combine(ConfinedOutside, "harness.json"), SentinelBytes);
                LinkToOutside("harness/config");
                return (
                    $"ConfinedTree never reads through a link: refused '{ReadTarget}', because 'harness/config' is a symbolic link.",
                    () => IsLink("harness/config"));
            case "linked-ancestor":
                Directory.CreateDirectory(Path.Combine(ConfinedOutside, "config"));
                File.WriteAllBytes(Path.Combine(ConfinedOutside, "config", "harness.json"), SentinelBytes);
                LinkToOutside("harness");
                return (
                    $"ConfinedTree never reads through a link: refused '{ReadTarget}', because 'harness' is a symbolic link.",
                    () => IsLink("harness"));
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown refused read entry.");
        }
    }

    private static void AssertLinkRefusal(IOException refusal, string linkedComponent)
    {
        Assert.Contains(LinkRefusal, refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"because '{linkedComponent}' is a symbolic link", refusal.Message, StringComparison.Ordinal);
    }

    // The protected directory holds the entries replay clears, so a clear that followed a link would delete them.
    private void ProtectedTree()
    {
        File.WriteAllBytes(Path.Combine(ConfinedOutside, SentinelName), SentinelBytes);
        foreach (var directory in new[] { "discrepancies", "replay/rest", "rest" })
        {
            Directory.CreateDirectory(Path.Combine(ConfinedOutside, directory));
            File.WriteAllBytes(Path.Combine(ConfinedOutside, directory, SentinelName), SentinelBytes);
        }

        File.WriteAllBytes(Path.Combine(ConfinedOutside, "rest", "001-getAgent-01.body"), SentinelBytes);
    }

    // Links relative (a directory below the root) to the protected directory; the setup is a real escape
    // route, because the protected sentinel is visible through the link.
    private void LinkToOutside(string relative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfinedFull(relative))!);
        Directory.CreateSymbolicLink(ConfinedFull(relative), ConfinedOutside);
        Assert.True(File.Exists(Path.Combine(ConfinedFull(relative), SentinelName)), "The link must reach the protected directory.");
    }

    // Until stop, moves each swapped directory aside, links its path to the protected directory, removes that
    // link and moves the directory back. A step the tree's own work rules out, such as linking a path where the
    // tree has just recreated the directory, is skipped. Every step renames or removes an entry below the root,
    // never one in the protected directory. Returns the first other failure, or null.
    private Exception? SwapForLinks(CancellationToken stop)
    {
        try
        {
            for (var cycle = 0; !stop.IsCancellationRequested; cycle++)
            {
                foreach (var directory in SwappedDirectories)
                {
                    var path = ConfinedFull(directory);
                    var aside = $"{path}.aside-{cycle}";
                    if (!Attempt(() => Directory.Move(path, aside)))
                    {
                        continue;
                    }

                    if (Attempt(() => Directory.CreateSymbolicLink(path, ConfinedOutside)))
                    {
                        Attempt(() => File.Delete(path));
                    }

                    Attempt(() => Directory.Move(aside, path));
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Whether a swap step succeeded; it fails only with the error of a path the tree changed first, such as a
    // link the tree already replaced with a directory.
    private static bool Attempt(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Until stop, stages a fresh FIFO or a link to the protected sentinel beside target, in turn, and renames it
    // onto target, replacing whatever a publication left there. Every step works below the root, never in the
    // protected directory. Returns the first failure, or null.
    private Exception? SwapSpecialEntriesOnto(string target, CancellationToken stop)
    {
        try
        {
            var staged = target + ".staged";
            for (var cycle = 0; !stop.IsCancellationRequested; cycle++)
            {
                if (cycle % 2 == 0)
                {
                    SpecialFileSupport.CreateFifo(staged);
                }
                else
                {
                    File.CreateSymbolicLink(staged, Path.Combine(ConfinedOutside, SentinelName));
                }

                File.Move(staged, target, overwrite: true);
                Interlocked.Increment(ref _confinedRaceSwaps);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // A link the swapper left behind is removed, so the final write and the cleanup see directories.
    private void RemoveSwappedInLinks()
    {
        foreach (var directory in SwappedDirectories)
        {
            if (IsLink(directory))
            {
                File.Delete(ConfinedFull(directory));
            }
        }
    }

    private static Task Synchronously(Action operation)
    {
        operation();
        return Task.CompletedTask;
    }

    // The exception an operation ends with, or null when it succeeds.
    private static async Task<Exception?> Outcome(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private Task ConfinedWrite(string relative, byte[] bytes, bool useAsync)
    {
        if (useAsync)
        {
            return ConfinedTreeUnderTest.WriteFileAsync(ConfinedFull(relative), bytes, TestCancellation);
        }

        ConfinedTreeUnderTest.WriteFile(ConfinedFull(relative), bytes);
        return Task.CompletedTask;
    }

    // Every entry below a directory, in ordinal order: files as "relative/path=hex", directories as
    // "relative/path/" and links as "relative/path->relative/target", so a created, changed or removed entry
    // changes the snapshot. Paths are relative and a publication's temporary file is shown by
    // WithoutPublishingGuid, so a failure reads the same in every run.
    private static List<string> ConfinedSnapshot(string directory) =>
    [
        .. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry =>
            {
                var relative = WithoutPublishingGuid(Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'));
                var linkTarget = new FileInfo(entry).LinkTarget;
                return linkTarget is not null
                    ? relative + "->" + Path.GetRelativePath(directory, linkTarget).Replace(Path.DirectorySeparatorChar, '/')
                    : Directory.Exists(entry)
                        ? relative + "/"
                        : relative + "=" + Convert.ToHexStringLower(File.ReadAllBytes(entry));
            })
            .Order(StringComparer.Ordinal),
    ];

    // A publication writes a temporary file named "." + a new Guid's 32 hex digits + ".publishing" beside its
    // target, so one left behind is shown with those digits replaced by a token no publication writes. No listing
    // holds such a file before the operation under test, so a listing compared after this replacement differs
    // from the expected one exactly when the raw listing does.
    private static string WithoutPublishingGuid(string relative) =>
        PublishingTemporaryName().Replace(relative, PublishingTemporaryToken);

    // The last component of a relative path when it is a publication's temporary file name; \z, unlike $, also
    // rejects a trailing newline.
    [GeneratedRegex(@"(?<=\A|/)\.[0-9a-f]{32}\.publishing\z", RegexOptions.CultureInvariant)]
    private static partial Regex PublishingTemporaryName();

    // SpecialFileSupport.Names, with each publication's temporary file shown by WithoutPublishingGuid.
    private static List<string> ShownNames(string directory) =>
        [.. SpecialFileSupport.Names(directory).Select(WithoutPublishingGuid)];

    // .NET has no hard-link API, so ln(1) creates it, bounded like every other tool the tests run.
    private static void CreateHardLink(string link, string existingFile) =>
        Assert.True(SpecialFileSupport.Run("ln", existingFile, link) == 0, "ln must create the hard link.");

    private bool IsLink(string relative) => new FileInfo(ConfinedFull(relative)).LinkTarget is not null;

    private string ConfinedFull(string relative) => Path.Combine(ConfinedRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    // Paths are asserted relative to the root, so a failure reads the same in every run.
    private string ConfinedRelative(string path) => Path.GetRelativePath(ConfinedRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private string ConfinedTemp => _confinedTemp ??= CreateConfinedSandbox();

    private string ConfinedRoot => Path.Combine(ConfinedTemp, "repo");

    private string ConfinedOutside => Path.Combine(ConfinedTemp, "outside");

    private ConfinedTree ConfinedTreeUnderTest => _confinedTree ??= new ConfinedTree(ConfinedRoot);

    private static string CreateConfinedSandbox()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erewhon-confined-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "repo"));
        Directory.CreateDirectory(Path.Combine(temp, "outside"));
        return temp;
    }
}

/// <summary>
/// Named pipes (FIFOs) for the tests that point a write at one, and a directory listing that is safe beside
/// one. .NET has no API that creates or recognises a FIFO, so mkfifo(1) creates it and the shell's
/// <c>test -p</c> recognises it. Nothing here opens an entry, so nothing can block on a FIFO that nobody reads.
/// Each tool here, and ln(1) for the tests' hard links, runs through <see cref="Run"/>, which bounds it.
/// </summary>
internal static class SpecialFileSupport
{
    private static readonly TimeSpan ToolLimit = TimeSpan.FromSeconds(30);

    // How long a killed tool gets to exit, and how often a wait for a tool looks at the test's cancellation.
    private static readonly TimeSpan WindDownLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CancellationPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Creates a FIFO at <paramref name="path"/>, which must not exist.</summary>
    public static void CreateFifo(string path) =>
        Assert.True(Run("mkfifo", path) == 0, "mkfifo must create the FIFO.");

    /// <summary>Whether the entry at <paramref name="path"/> is a FIFO; a link to one is not.</summary>
    public static bool IsFifo(string path) =>
        new FileInfo(path).LinkTarget is null && Run("sh", "-c", "test -p \"$1\"", "sh", path) == 0;

    /// <summary>The names in <paramref name="directory"/>, dot names included, in ordinal order.</summary>
    public static List<string> Names(string directory) =>
    [
        .. Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0 })
            .Select(entry => Path.GetRelativePath(directory, entry))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Runs a tool with its arguments passed as they are and returns its exit code. A tool that does not end
    /// within the tool limit fails the test, and a cancelled test stops waiting for it, instead of hanging; either
    /// way the tool and everything it spawned are killed and their exit awaited, so none outlives the test.
    /// </summary>
    public static int Run(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var exited = false;
        try
        {
            exited = WaitForExit(process, ct);
            if (!exited)
            {
                ct.ThrowIfCancellationRequested();
                Assert.Fail($"{tool} did not finish within the tool limit.");
            }

            return process.ExitCode;
        }
        finally
        {
            if (!exited)
            {
                Stop(process);
            }
        }
    }

    // Waits until the tool exits, the tool limit passes or the test is cancelled, whichever comes first, looking
    // at the cancellation between short slices of the wait. Reports whether the tool exited.
    private static bool WaitForExit(Process process, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            var remaining = ToolLimit - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (process.WaitForExit(remaining < CancellationPollInterval ? remaining : CancellationPollInterval))
            {
                return true;
            }
        }

        return false;
    }

    // Kills the tool and everything it spawned, then waits a bounded time for its exit, so it is gone before its
    // Process is released. A tool that already exited needs neither.
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The tool exited before the kill: nothing is left to stop.
            return;
        }

        process.WaitForExit(WindDownLimit);
    }
}
