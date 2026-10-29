using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Lifecycle;

/// <summary>
/// Shuts down one component through its admin listener and confirms that it stopped. Replay's cleanup
/// step calls it for the facade and the stand-in, so that their processes exit and plain
/// <c>docker compose up</c> ends with the harness's status.
/// </summary>
public interface IComponentShutdown
{
    /// <summary>Requests the component's shutdown and waits, within a bound, for its stop to be confirmed.</summary>
    /// <param name="target">The component and its admin base URL.</param>
    /// <param name="ct">Ends the wait early; the outcome then reports the component as not stopped.</param>
    /// <returns>Whether the stop was confirmed, with a human-readable detail.</returns>
    Task<ShutdownOutcome> ShutdownAsync(ShutdownTarget target, CancellationToken ct);
}

/// <summary>A component the harness shuts down at the end of a run, and its admin listener.</summary>
/// <remarks>
/// The constructor and the init accessors reject a blank <see cref="Component"/> and an
/// <see cref="AdminBaseUrl"/> that is not an absolute <c>http</c> or <c>https</c> URL without user
/// information, so every target yields a valid <c>CHK-SHUTDOWN:&lt;component&gt;</c> item and no
/// shutdown request or detail carries a credential.
/// </remarks>
/// <param name="Component">The component label, <see cref="FacadeComponent"/> or <see cref="StandInComponent"/>.</param>
/// <param name="AdminBaseUrl">The admin listener base URL, for example <c>http://facade:9090</c>.</param>
public sealed record ShutdownTarget(string Component, Uri AdminBaseUrl)
{
    /// <summary>The facade's component label, as readiness and the report use it.</summary>
    public const string FacadeComponent = "facade";

    /// <summary>The stand-in's component label, as readiness and the report use it.</summary>
    public const string StandInComponent = "standin";

    private readonly string _component = RequiredComponent(Component);
    private readonly Uri _adminBaseUrl = RequiredAdminUrl(AdminBaseUrl);

    /// <summary>The component label; never blank.</summary>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public string Component
    {
        get => _component;
        init => _component = RequiredComponent(value);
    }

    /// <summary>The admin listener base URL; always an absolute <c>http</c> or <c>https</c> URL without user information.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">
    /// The value is not an absolute <c>http</c> or <c>https</c> URL, or carries user information.
    /// </exception>
    public Uri AdminBaseUrl
    {
        get => _adminBaseUrl;
        init => _adminBaseUrl = RequiredAdminUrl(value);
    }

    /// <summary>The component's <see cref="AdminRoutes.Shutdown"/> URL.</summary>
    internal Uri ShutdownUrl => Resolve(AdminRoutes.Shutdown);

    /// <summary>The component's <see cref="AdminRoutes.Health"/> URL.</summary>
    internal Uri HealthUrl => Resolve(AdminRoutes.Health);

    /// <summary>
    /// Returns the components a run shuts down, facade first because it fronts the stand-in: the facade
    /// from <c>Target:AdminUrl</c>, then the stand-in from <c>Backend:AdminUrl</c>.
    /// </summary>
    /// <remarks>
    /// A blank or invalid admin URL, one carrying user information included, omits its component. For
    /// the backend that is the point: a blank <c>Backend:AdminUrl</c> marks an external backend, which is
    /// never shut down. A caller that must shut the facade down treats its absence as a stop it cannot
    /// confirm.
    /// </remarks>
    /// <param name="options">The harness settings.</param>
    /// <returns>Zero, one or two targets, in shutdown order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static IReadOnlyList<ShutdownTarget> ForRun(HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var targets = new List<ShutdownTarget>(2);
        if (TryCreate(FacadeComponent, options.Target?.AdminUrl, out var facade))
        {
            targets.Add(facade);
        }

        if (TryCreate(StandInComponent, options.Backend?.AdminUrl, out var standIn))
        {
            targets.Add(standIn);
        }

        return targets;
    }

    /// <summary>Creates a target from a configured admin URL, without throwing.</summary>
    /// <param name="component">The component label.</param>
    /// <param name="adminUrl">The configured admin base URL; surrounding whitespace is ignored.</param>
    /// <param name="target">The target; null when the method returns false.</param>
    /// <returns>
    /// True when <paramref name="component"/> is non-blank and <paramref name="adminUrl"/> is an
    /// absolute <c>http</c> or <c>https</c> URL without user information.
    /// </returns>
    public static bool TryCreate(string component, string? adminUrl, [NotNullWhen(true)] out ShutdownTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(component)
            || !CaseRunner.TryParseHttpUrl(adminUrl, out var uri, out _))
        {
            return false;
        }

        target = new ShutdownTarget(component, uri);
        return true;
    }

    private static string RequiredComponent(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A shutdown target needs a non-blank component label.", nameof(Component))
            : value;

    // The message gives the reason without quoting the URL, which may carry a password.
    private static Uri RequiredAdminUrl(Uri? value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(AdminBaseUrl));
        return CaseRunner.IsCredentialFreeHttpUrl(value, out var problem)
            ? value
            : throw new ArgumentException(
                $"A shutdown target needs an absolute http or https admin URL without user information. {problem}",
                nameof(AdminBaseUrl));
    }

    // Any path on the base URL is kept, and its query and fragment are dropped.
    private Uri Resolve(string route) =>
        new(AdminBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + route, UriKind.Absolute);
}

/// <summary>The result of shutting down one component.</summary>
/// <param name="Component">The component label of the target.</param>
/// <param name="Stopped">True when the component accepted the shutdown and its stop was confirmed.</param>
/// <param name="Detail">
/// Human-readable account of the shutdown call and the stop confirmation. It may hold timings, because a
/// <c>CHK-SHUTDOWN</c> item is never documentable and its detail never takes part in matching.
/// </param>
public sealed record ShutdownOutcome(string Component, bool Stopped, string Detail)
{
    /// <summary>The label of an outcome whose target was missing.</summary>
    internal const string UnknownComponent = "unknown";

    /// <summary>
    /// Returns null for a stopped component; otherwise the never-documentable
    /// <c>CHK-SHUTDOWN:&lt;component&gt;</c> item with nature <c>still-running</c>.
    /// </summary>
    public FailureItem? ToFailureItem() =>
        Stopped ? null : new FailureItem(FailureItemIds.Shutdown(Component), FailureItemIds.StillRunning, Detail: Detail);

    /// <summary>The not-stopped outcome of a shutdown that threw <paramref name="exception"/>.</summary>
    internal static ShutdownOutcome FromException(string component, Exception exception) =>
        new(component, false, $"exception: {exception.GetType().Name}: {exception.Message}");
}

/// <summary>Shuts down several components so that a failure for one never skips the next.</summary>
public static class ComponentShutdownExtensions
{
    /// <summary>
    /// Calls <see cref="IComponentShutdown.ShutdownAsync"/> for every target in turn. A call that throws
    /// yields a not-stopped outcome for its target, and the next target is still shut down.
    /// </summary>
    /// <param name="shutdown">The shutdown implementation.</param>
    /// <param name="targets">The targets, in shutdown order.</param>
    /// <param name="ct">Passed to every call; it never stops the loop.</param>
    /// <returns>One outcome per target, in target order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shutdown"/> or <paramref name="targets"/> is null.</exception>
    public static async Task<IReadOnlyList<ShutdownOutcome>> ShutdownEachAsync(
        this IComponentShutdown shutdown,
        IEnumerable<ShutdownTarget> targets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(targets);

        var outcomes = new List<ShutdownOutcome>();
        foreach (var target in targets)
        {
            var component = target?.Component ?? ShutdownOutcome.UnknownComponent;
            try
            {
                var outcome = await shutdown.ShutdownAsync(target!, ct).ConfigureAwait(false);
                outcomes.Add(outcome ?? new ShutdownOutcome(component, false, "no outcome returned"));
            }
            catch (Exception ex)
            {
                outcomes.Add(ShutdownOutcome.FromException(component, ex));
            }
        }

        return outcomes;
    }
}

/// <summary>
/// Shuts a component down over HTTP: <c>POST</c> <see cref="AdminRoutes.Shutdown"/>, then
/// <c>GET</c> <see cref="AdminRoutes.Health"/> polled until the connection is refused, all within
/// <see cref="HarnessOptions.ShutdownTimeout"/>.
/// </summary>
/// <remarks>
/// <para>The shutdown call is bounded by <see cref="HarnessOptions.ShutdownRequestTimeout"/> and by the
/// time left; each probe, and each opening of the watch connection described below, by one second and the
/// time left, with about 250 ms between probes outside the bursts described below.</para>
/// <para>A component is reported stopped only when the shutdown call was answered with a 2xx status and
/// health polling then confirmed the stop. Every other outcome of the call (a non-2xx answer, a timeout,
/// a reset, a refused connection, a failed name lookup, an unreachable host or any other error) is a
/// failed shutdown call, and the component is reported still running. Health is still polled within the
/// limit after a failed call, so the detail says what it showed.</para>
/// <para>A stop is confirmed only by a health probe whose connection is refused after an accepted call:
/// nothing listens on the admin port any more. Any HTTP answer means the component is still running. A
/// probe timeout or reset, an unreachable host or network and every failed name lookup, a host name that
/// is not found included, prove nothing either way: a container's name and address can be withdrawn while
/// its process still runs, and a resolver outage or a network partition produces the same outcomes. A
/// component whose connection is not refused by the limit is reported still running.</para>
/// <para>A stopped container's port can refuse connections for only tens of milliseconds before the
/// container engine withdraws its address and name, and a probe every 250 ms can miss that window. So
/// the harness's own socket handler first opens an idle watch connection to the admin port, before the
/// shutdown call, and sends nothing on it. A stopping server unbinds its listeners and then closes its
/// idle connections, so the watch ends at about the moment the port starts refusing. The client then
/// probes at once and about every 10 ms for a second, in case the server closed its idle connections
/// shortly before it stopped listening, and opens a new watch if the component still answers after that.
/// The watch only times the probes: it never confirms a stop, and a watch that cannot be opened, ends
/// early or never ends leaves the result to the probes alone.</para>
/// <para><see cref="ShutdownAsync"/> never throws and never waits beyond the limit. Every request asks
/// for the connection to be closed and no connection is pooled, so a kept-alive socket cannot report a
/// reset where a new connection would be refused. No credential is sent: the admin listeners are
/// unauthenticated. Instances are thread-safe.</para>
/// </remarks>
public sealed class HttpComponentShutdown : IComponentShutdown, IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeSpacing = TimeSpan.FromMilliseconds(250);

    // Once the watch connection ends, probes follow each other this closely for this long, so that a
    // refusal lasting only tens of milliseconds is still met.
    private static readonly TimeSpan FastProbeSpacing = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FastProbeBurst = TimeSpan.FromSeconds(1);

    // Opening the watch connection is bounded by this and by the time left.
    private static readonly TimeSpan WatchConnectTimeout = TimeSpan.FromSeconds(1);

    // CancelAfter needs a positive delay even when almost no time is left.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly HttpClient _client;
    private readonly TextWriter _output;
    private readonly TimeSpan _shutdownLimit;
    private readonly TimeSpan _requestLimit;

    // Only the client's own socket handler reaches the network, so only then is a watch connection opened.
    private readonly bool _opensWatch;

    /// <summary>Creates a shutdown client with the limits of <paramref name="options"/>.</summary>
    /// <param name="options">The harness settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public HttpComponentShutdown(HarnessOptions options)
        : this(options, handler: null, output: null)
    {
    }

    /// <summary>Creates a shutdown client over <paramref name="handler"/>, writing its lines to <paramref name="output"/>.</summary>
    /// <param name="options">The harness settings.</param>
    /// <param name="handler">The message handler, owned by the caller; null for the harness's own socket handler.</param>
    /// <param name="output">The console writer; null for <see cref="Console.Out"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal HttpComponentShutdown(HarnessOptions options, HttpMessageHandler? handler, TextWriter? output)
    {
        ArgumentNullException.ThrowIfNull(options);

        _shutdownLimit = options.ShutdownTimeout;
        _requestLimit = options.ShutdownRequestTimeout;
        _output = output ?? Console.Out;
        _opensWatch = handler is null;
        _client = handler is null
            ? new HttpClient(CreateHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    private enum ProbeKind
    {
        Answered,
        Refused,

        // The resolver answered that the name has no address: HostNotFound or NoData. A name can be
        // withdrawn while its process still runs, as when its container leaves the network, so this
        // proves nothing either; it is kept apart from the other lookup failures only for the detail.
        NameNotFound,

        // Any other name lookup failure (TryAgain, NoRecovery, or no definite answer), which proves nothing.
        NameLookupFailed,
        Unreachable,
        TimedOut,
        Reset,
        Failed,
        Cancelled,
    }

    /// <inheritdoc />
    /// <remarks>Prints one line through <see cref="LifecycleConsole"/>, after the outcome is computed:
    /// <c>shutdown: &lt;component&gt; stopped (&lt;detail&gt;)</c> or
    /// <c>shutdown: &lt;component&gt; still running (&lt;detail&gt;)</c>. A writer that fails loses only
    /// the line, never the outcome.</remarks>
    public async Task<ShutdownOutcome> ShutdownAsync(ShutdownTarget target, CancellationToken ct)
    {
        ShutdownOutcome outcome;
        try
        {
            outcome = target is null
                ? new ShutdownOutcome(ShutdownOutcome.UnknownComponent, false, "no shutdown target given")
                : await ShutdownCoreAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = ShutdownOutcome.FromException(target?.Component ?? ShutdownOutcome.UnknownComponent, ex);
        }

        LifecycleConsole.WriteLine(
            _output,
            outcome.Stopped
                ? $"shutdown: {outcome.Component} stopped ({outcome.Detail})"
                : $"shutdown: {outcome.Component} still running ({outcome.Detail})");
        return outcome;
    }

    /// <summary>Disposes the HTTP client and the handler it owns.</summary>
    public void Dispose() => _client.Dispose();

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.Zero,
    };

    private async Task<ShutdownOutcome> ShutdownCoreAsync(ShutdownTarget target, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // Opened before the shutdown call, while the component still accepts connections, and closed on
        // every way out of this method.
        using var watch = new WatchConnection(_opensWatch ? target.AdminBaseUrl : null);
        await watch.OpenAsync(Bound(WatchConnectTimeout, Remaining(clock)), ct).ConfigureAwait(false);

        var post = await SendAsync(HttpMethod.Post, target.ShutdownUrl, Bound(_requestLimit, Remaining(clock)), ct)
            .ConfigureAwait(false);
        if (post.Kind == ProbeKind.Cancelled)
        {
            return new ShutdownOutcome(target.Component, false, Invariant($"POST cancelled after {clock.ElapsedMilliseconds} ms"));
        }

        var postText = DescribePost(post);

        // Only a 2xx answer shows that the component took the request. Any other outcome is a failed
        // shutdown call, so the component is reported still running whatever health shows next.
        var postAccepted = post.Kind == ProbeKind.Answered && post.Status is >= 200 and <= 299;
        var failedNote = postAccepted
            ? string.Empty
            : post.Kind == ProbeKind.Answered ? " (shutdown rejected)" : " (shutdown call failed)";

        int? lastStatus = null;
        ProbeResult? lastProbe = null;
        TimeSpan? burstEnd = null;
        while (Remaining(clock) > TimeSpan.Zero)
        {
            var probeLimit = Bound(ProbeTimeout, Remaining(clock));
            var probe = await SendAsync(HttpMethod.Get, target.HealthUrl, probeLimit, ct)
                .ConfigureAwait(false);
            switch (probe.Kind)
            {
                case ProbeKind.Cancelled:
                    return Cancelled(target, postText, clock);

                // The only stop evidence: nothing listens on the admin port any more.
                case ProbeKind.Refused:
                    return new ShutdownOutcome(
                        target.Component,
                        postAccepted,
                        Invariant($"{postText}; refused after {clock.ElapsedMilliseconds} ms{failedNote}"));
                case ProbeKind.Answered:
                    lastStatus = probe.Status;
                    break;
            }

            // A probe that the shutdown limit cut below its own timeout can end in that cut-off whatever the
            // component does, and the client then reports any failure arriving with it as a timeout. Such a
            // timeout shows only that time ran out, so it never replaces an earlier observation in the detail;
            // a component that only ever times out still reports its timeout.
            var cutOffAtLimit = probe.Kind == ProbeKind.TimedOut && probeLimit < ProbeTimeout;
            if (!cutOffAtLimit || lastProbe is null)
            {
                lastProbe = probe;
            }

            // A burst that met no refusal while the component still answers means its server closed the
            // watch early, so a new watch keeps the trigger for the rest of the wait.
            if (burstEnd is { } end && clock.Elapsed >= end)
            {
                burstEnd = null;
                if (probe.Kind == ProbeKind.Answered)
                {
                    await watch.OpenAsync(Bound(WatchConnectTimeout, Remaining(clock)), ct).ConfigureAwait(false);
                }
            }

            var remaining = Remaining(clock);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                var spacing = burstEnd is null ? ProbeSpacing : FastProbeSpacing;
                if (await WaitAsync(Bound(spacing, remaining), watch.Closed, ct).ConfigureAwait(false))
                {
                    // The server closed the watch, as it does once it has stopped listening: probe at once,
                    // then closely for a while.
                    watch.Close();
                    burstEnd = clock.Elapsed + FastProbeBurst;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Cancelled(target, postText, clock);
            }
        }

        var limit = _shutdownLimit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var unconfirmed = (lastStatus, lastProbe) switch
        {
            ({ } status, _) => Invariant($"still answering at {limit} s (last {status})"),
            (null, { } probe) => $"not refused by {limit} s (last {DescribeInconclusive(probe)})",
            _ => $"no health probe by {limit} s",
        };
        return new ShutdownOutcome(target.Component, false, $"{postText}; {unconfirmed}{failedNote}");
    }

    private async Task<ProbeResult> SendAsync(HttpMethod method, Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.ConnectionClose = true;
        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
            return new ProbeResult(ProbeKind.Answered, (int)response.StatusCode, null);
        }
        catch (Exception ex) when (TryClassify(ex, ct, out var failure))
        {
            return failure;
        }
    }

    // Classifies the network outcomes a probe can meet. Anything else (a disposed client, an invalid
    // request) is a fault of the harness and reaches ShutdownAsync's own catch.
    private static bool TryClassify(Exception ex, CancellationToken ct, out ProbeResult result)
    {
        if (ex is OperationCanceledException)
        {
            result = new ProbeResult(ct.IsCancellationRequested ? ProbeKind.Cancelled : ProbeKind.TimedOut, null, null);
            return true;
        }

        if (ex is not (HttpRequestException or SocketException or IOException))
        {
            result = default;
            return false;
        }

        var kind = FindInner<SocketException>(ex)?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => ProbeKind.Refused,
            SocketError.HostNotFound or SocketError.NoData => ProbeKind.NameNotFound,
            SocketError.TryAgain or SocketError.NoRecovery => ProbeKind.NameLookupFailed,
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => ProbeKind.Unreachable,
            SocketError.TimedOut => ProbeKind.TimedOut,
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => ProbeKind.Reset,
            _ when ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                ProbeKind.NameLookupFailed,
            _ when ex is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }
                || FindInner<IOException>(ex) is not null => ProbeKind.Reset,
            _ => ProbeKind.Failed,
        };

        var note = kind == ProbeKind.Failed
            ? ex is HttpRequestException http ? http.HttpRequestError.ToString() : ex.GetType().Name
            : null;
        result = new ProbeResult(kind, null, note);
        return true;
    }

    private static T? FindInner<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static string DescribePost(ProbeResult post) => post.Kind switch
    {
        ProbeKind.Answered => Invariant($"POST {post.Status}"),
        ProbeKind.Refused => "POST connect",
        ProbeKind.NameNotFound => "POST connect (host not found)",
        ProbeKind.NameLookupFailed => "POST connect (name lookup failed)",
        ProbeKind.Unreachable => "POST connect (host unreachable)",
        ProbeKind.TimedOut => "POST timeout",
        ProbeKind.Reset => "POST reset",
        _ => $"POST error {post.Note}",
    };

    private static string DescribeInconclusive(ProbeResult probe) => probe.Kind switch
    {
        ProbeKind.NameNotFound => "host not found",
        ProbeKind.NameLookupFailed => "name lookup failed",
        ProbeKind.Unreachable => "host unreachable",
        ProbeKind.TimedOut => "timeout",
        ProbeKind.Reset => "reset",
        _ => $"error {probe.Note}",
    };

    private static ShutdownOutcome Cancelled(ShutdownTarget target, string postText, Stopwatch clock) =>
        new(target.Component, false, Invariant($"{postText}; cancelled after {clock.ElapsedMilliseconds} ms"));

    private static TimeSpan Bound(TimeSpan limit, TimeSpan remaining)
    {
        var wait = limit < remaining ? limit : remaining;
        return wait > MinimumWait ? wait : MinimumWait;
    }

    // Waits for the spacing to pass or the watch to end, whichever comes first, and returns true when the
    // watch ended. The caller's cancellation ends the wait with an OperationCanceledException.
    private static async Task<bool> WaitAsync(TimeSpan spacing, Task? watchClosed, CancellationToken ct)
    {
        if (watchClosed is null)
        {
            await Task.Delay(spacing, ct).ConfigureAwait(false);
            return false;
        }

        using var delayEnd = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(spacing, delayEnd.Token);
        var first = await Task.WhenAny(delay, watchClosed).ConfigureAwait(false);

        // Releases the timer of a delay that lost to the watch.
        delayEnd.Cancel();
        ct.ThrowIfCancellationRequested();
        return first == watchClosed;
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private TimeSpan Remaining(Stopwatch clock) => _shutdownLimit - clock.Elapsed;

    private readonly record struct ProbeResult(ProbeKind Kind, int? Status, string? Note);

    /// <summary>
    /// The watch connection of one shutdown: a TCP connection to the admin port that sends nothing and
    /// only waits for the server to close it. At most one is open at a time; disposal closes it.
    /// </summary>
    private sealed class WatchConnection : IDisposable
    {
        private readonly Uri? _adminUrl;
        private Socket? _socket;

        /// <summary>Creates a watch for <paramref name="adminUrl"/>, or one that never opens when it is null.</summary>
        public WatchConnection(Uri? adminUrl) => _adminUrl = adminUrl;

        /// <summary>
        /// Completes, and never faults, once the server closes or resets the open connection; null while no
        /// connection is open.
        /// </summary>
        public Task? Closed { get; private set; }

        /// <summary>Closes any open connection and opens a new one, best-effort, within <paramref name="timeout"/>.</summary>
        public async Task OpenAsync(TimeSpan timeout, CancellationToken ct)
        {
            Close();
            if (_adminUrl is null)
            {
                return;
            }

            Socket? socket = null;
            try
            {
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bound.CancelAfter(timeout);
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                var host = _adminUrl.IdnHost;
                EndPoint endPoint = IPAddress.TryParse(host, out var address)
                    ? new IPEndPoint(address, _adminUrl.Port)
                    : new DnsEndPoint(host, _adminUrl.Port);
                await socket.ConnectAsync(endPoint, bound.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The watch only times the probes, so whatever keeps it from opening leaves the probes as they are.
                socket?.Dispose();
                return;
            }

            _socket = socket;
            Closed = WaitForCloseAsync(socket);
        }

        /// <summary>Closes the open connection, if any.</summary>
        public void Close()
        {
            _socket?.Dispose();
            _socket = null;
            Closed = null;
        }

        /// <inheritdoc />
        public void Dispose() => Close();

        // Ends when a receive yields no data, an orderly close, or fails: a reset, an abort or this watch's own
        // disposal. The connection never carried a request, so whatever a server sends on it is dropped.
        private static async Task WaitForCloseAsync(Socket socket)
        {
            var buffer = new byte[64];
            var open = true;
            while (open)
            {
                try
                {
                    open = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, CancellationToken.None)
                        .ConfigureAwait(false) > 0;
                }
                catch (Exception)
                {
                    // A failed receive ends the connection as surely as an orderly close.
                    open = false;
                }
            }
        }
    }
}
