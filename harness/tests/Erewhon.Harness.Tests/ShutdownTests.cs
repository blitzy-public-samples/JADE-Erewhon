using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Lifecycle;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Shutdown of one component against scripted admin replies: it is reported stopped only when its
/// shutdown call is answered with a 2xx status and health then refuses the connection. A failed or
/// rejected shutdown call, any HTTP answer to health and every inconclusive probe, a host name that is not
/// found included, leave it still running, as a <c>CHK-SHUTDOWN:&lt;component&gt;</c> item with nature
/// <c>still-running</c>, within the shutdown limit. Scripted admin URLs name a reserved <c>.invalid</c>
/// host that only the scripted handler answers, and a scripted handler opens no watch connection. The
/// real-socket tests use the client's own socket handler, watch connection included, against servers
/// that listen on loopback only.
/// </summary>
public sealed class ShutdownTests : IDisposable
{
    private const string AdminUrl = "http://standin.invalid:9090";
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

    private readonly StringWriter _console = new(CultureInfo.InvariantCulture);

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

    /// <inheritdoc />
    public void Dispose() => _console.Dispose();

    // ---- Stop confirmed -------------------------------------------------------------------------

    [Theory]
    [InlineData(AdminReply.Ok, "POST 200")]
    [InlineData(AdminReply.Accepted, "POST 202")]
    [InlineData(AdminReply.NoContent, "POST 204")]
    public async Task AcceptedCall_ThenRefusedConnection_IsStopped(AdminReply shutdownReply, string postText)
    {
        var component = ScriptedComponent.WithHealth(shutdownReply, AdminReply.Ok, AdminReply.ConnectionRefused);

        var (outcome, _) = await ShutdownAsync(component, Options(shutdownSeconds: 2));

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

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 1));

        AssertStillRunning(outcome, elapsed, limitSeconds: 1);
        Assert.Equal("POST 202; still answering at 1 s (last 200)", outcome.Detail);
        Assert.True(component.HealthProbes >= 3, $"only {component.HealthProbes} health probe(s)");
    }

    [Fact]
    public async Task AcceptedCall_ThenNameNotFoundOnce_ThenAnswers_IsStillRunningAtTheLimit()
    {
        var component = ScriptedComponent.WithHealth(AdminReply.Accepted, AdminReply.HostNotFound, AdminReply.Ok);

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 1));

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

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 2));

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

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 1));

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

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 1));

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

        var (outcome, elapsed) = await ShutdownAsync(component, Options(shutdownSeconds: 3, requestSeconds: 1));

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
            using (var shutdown = new HttpComponentShutdown(Options(shutdownSeconds: 5), handler: null, _console))
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

            var (outcome, _) = await ShutdownOverLoopbackAsync(port, Options(shutdownSeconds: 3));
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

            var (outcome, elapsed) = await ShutdownOverLoopbackAsync(port, Options(shutdownSeconds: 2));
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

    private static HarnessOptions Options(int shutdownSeconds, int requestSeconds = 1) => new()
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
        var target = new ShutdownTarget(ShutdownTarget.StandInComponent, new Uri(AdminUrl));
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
