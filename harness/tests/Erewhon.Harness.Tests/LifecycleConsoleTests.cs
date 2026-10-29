using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The lifecycle clients' console sink: every printed line is escaped so that an admin value or a
/// setting can neither break it into a forged <c>[jade-standin]</c> line nor drive the terminal, while
/// failure items keep the exact values; and a writer that throws never replaces the readiness item, reset
/// result or shutdown outcome already computed. Admin requests go to a reserved <c>.invalid</c> host that
/// only the scripted handler answers, or to a loopback listener the test owns.
/// </summary>
public sealed class LifecycleConsoleTests : IDisposable
{
    private const string AdminUrl = "http://standin.invalid:9090";
    private const string ReferenceDate = "2026-01-01";
    private const long PreviousGeneration = 5;

    private const string InfoRequest = "GET " + AdminRoutes.Info;
    private const string SeedResetRequest =
        "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=" + AdminRoutes.ResetStateSeed;

    private static readonly string SeedSha256 = new('a', 64);

    private readonly StringWriter _console = new(CultureInfo.InvariantCulture);

    /// <summary>How a <see cref="ThrowingWriter"/> fails every write.</summary>
    public enum WriterFault
    {
        /// <summary>An <see cref="IOException"/>, as a closed or full standard output raises.</summary>
        IO,

        /// <summary>An <see cref="ObjectDisposedException"/>, as a disposed writer raises.</summary>
        Disposed,
    }

    /// <inheritdoc />
    public void Dispose() => _console.Dispose();

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
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, source: raw)));

        var result = await ResetAsync(admin, Options(), _console);

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
        var options = Options();
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
        var options = Options();
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
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, source: "other", mutations: 3)));
        var writer = new ThrowingWriter(fault);

        var result = await ResetAsync(admin, Options(), writer);

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
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, () => new HttpResponseMessage(HttpStatusCode.OK))
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));
        var writer = new ThrowingWriter(fault);

        var result = await ResetAsync(admin, Options(), writer);

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
        var options = Options();
        options.Backend.AdminUrl = string.Empty;
        var admin = new ScriptedHandler();
        var writer = new ThrowingWriter(fault);

        var result = await ResetAsync(admin, options, writer);

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
            new ShutdownTarget(ShutdownTarget.StandInComponent, new Uri(AdminUrl)),
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

    private static HarnessOptions Options()
    {
        var options = new HarnessOptions { RequestTimeoutSeconds = 5 };
        options.Backend.AdminUrl = AdminUrl;
        return options;
    }

    // A path under the test's temporary directory that is never created.
    private static string MissingDirectory() =>
        Path.Combine(Path.GetTempPath(), $"erewhon-lifecycle-console-{Guid.NewGuid():N}", "missing");

    private static async Task<StateResetResult> ResetAsync(
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

    private static JsonObject Info(long generation, string source = SourceLabels.StandIn, long mutations = 0) => new()
    {
        ["source"] = source,
        ["seedSha256"] = SeedSha256,
        ["referenceDate"] = ReferenceDate,
        ["generation"] = generation,
        ["mutations"] = mutations,
    };

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
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
