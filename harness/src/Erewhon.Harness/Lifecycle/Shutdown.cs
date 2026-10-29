using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Erewhon.Contracts.Admin;
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
/// <see cref="AdminBaseUrl"/> that is not an absolute <c>http</c> or <c>https</c> URL, so every target
/// yields a valid <c>CHK-SHUTDOWN:&lt;component&gt;</c> item.
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

    /// <summary>The admin listener base URL; always an absolute <c>http</c> or <c>https</c> URL.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The value is not an absolute <c>http</c> or <c>https</c> URL.</exception>
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
    /// A blank or invalid admin URL omits its component. For the backend that is the point: a blank
    /// <c>Backend:AdminUrl</c> marks an external backend, which is never shut down. A caller that must
    /// shut the facade down treats its absence as a stop it cannot confirm.
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
    /// absolute <c>http</c> or <c>https</c> URL.
    /// </returns>
    public static bool TryCreate(string component, string? adminUrl, [NotNullWhen(true)] out ShutdownTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(component)
            || string.IsNullOrWhiteSpace(adminUrl)
            || !Uri.TryCreate(adminUrl.Trim(), UriKind.Absolute, out var uri)
            || !IsHttp(uri))
        {
            return false;
        }

        target = new ShutdownTarget(component, uri);
        return true;
    }

    // On Unix an absolute file path parses as an absolute file: URI, so the scheme is checked too.
    private static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string RequiredComponent(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A shutdown target needs a non-blank component label.", nameof(Component))
            : value;

    private static Uri RequiredAdminUrl(Uri? value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(AdminBaseUrl));
        return IsHttp(value)
            ? value
            : throw new ArgumentException("A shutdown target needs an absolute http or https admin URL.", nameof(AdminBaseUrl));
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
/// time left; each probe by one second and the time left, with about 250 ms between probes. A stop is
/// confirmed by a refused connection, an unresolvable host (a stopped Compose container's name
/// disappears) or an unreachable host (a removed container's address is no longer routable). Any HTTP
/// answer means the component is still running; a probe timeout or reset proves nothing either way.</para>
/// <para>A component is reported stopped only when the stop was confirmed and the shutdown call was not
/// answered with a non-success status. A timeout, reset or refused connection on the shutdown call is
/// tolerated, because the process may exit while answering or may already have stopped.</para>
/// <para><see cref="ShutdownAsync"/> never throws and never waits beyond the limit. Every request asks
/// for the connection to be closed and no connection is pooled, so a kept-alive socket cannot report a
/// reset where a new connection would be refused. No credential is sent: the admin listeners are
/// unauthenticated. Instances are thread-safe.</para>
/// </remarks>
public sealed class HttpComponentShutdown : IComponentShutdown, IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeSpacing = TimeSpan.FromMilliseconds(250);

    // CancelAfter needs a positive delay even when almost no time is left.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly HttpClient _client;
    private readonly TextWriter _output;
    private readonly TimeSpan _shutdownLimit;
    private readonly TimeSpan _requestLimit;

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
        _client = handler is null
            ? new HttpClient(CreateHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    private enum ProbeKind
    {
        Answered,
        Refused,
        NameNotResolved,
        Unreachable,
        TimedOut,
        Reset,
        Failed,
        Cancelled,
    }

    /// <inheritdoc />
    /// <remarks>Prints one line: <c>shutdown: &lt;component&gt; stopped (&lt;detail&gt;)</c> or
    /// <c>shutdown: &lt;component&gt; still running (&lt;detail&gt;)</c>.</remarks>
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

        _output.WriteLine(outcome.Stopped
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

        var post = await SendAsync(HttpMethod.Post, target.ShutdownUrl, Bound(_requestLimit, Remaining(clock)), ct)
            .ConfigureAwait(false);
        if (post.Kind == ProbeKind.Cancelled)
        {
            return new ShutdownOutcome(target.Component, false, Invariant($"POST cancelled after {clock.ElapsedMilliseconds} ms"));
        }

        var postText = DescribePost(post);
        var postRejected = post.Kind == ProbeKind.Answered && post.Status is < 200 or > 299;
        var rejectedNote = postRejected ? " (shutdown rejected)" : string.Empty;

        int? lastStatus = null;
        ProbeResult? lastProbe = null;
        while (Remaining(clock) > TimeSpan.Zero)
        {
            var probe = await SendAsync(HttpMethod.Get, target.HealthUrl, Bound(ProbeTimeout, Remaining(clock)), ct)
                .ConfigureAwait(false);
            switch (probe.Kind)
            {
                case ProbeKind.Cancelled:
                    return Cancelled(target, postText, clock);
                case ProbeKind.Refused or ProbeKind.NameNotResolved or ProbeKind.Unreachable:
                    return new ShutdownOutcome(
                        target.Component,
                        !postRejected,
                        Invariant($"{postText}; {DescribeStop(probe.Kind)} after {clock.ElapsedMilliseconds} ms{rejectedNote}"));
                case ProbeKind.Answered:
                    lastStatus = probe.Status;
                    break;
            }

            lastProbe = probe;
            var remaining = Remaining(clock);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(Bound(ProbeSpacing, remaining), ct).ConfigureAwait(false);
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
        return new ShutdownOutcome(target.Component, false, $"{postText}; {unconfirmed}{rejectedNote}");
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
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProbeKind.NameNotResolved,
            SocketError.HostUnreachable or SocketError.NetworkUnreachable => ProbeKind.Unreachable,
            SocketError.TimedOut => ProbeKind.TimedOut,
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => ProbeKind.Reset,
            _ when ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                ProbeKind.NameNotResolved,
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
        ProbeKind.NameNotResolved => "POST connect (host not resolved)",
        ProbeKind.Unreachable => "POST connect (host unreachable)",
        ProbeKind.TimedOut => "POST timeout",
        ProbeKind.Reset => "POST reset",
        _ => $"POST error {post.Note}",
    };

    private static string DescribeStop(ProbeKind kind) => kind switch
    {
        ProbeKind.Refused => "refused",
        ProbeKind.NameNotResolved => "host not resolved",
        _ => "host unreachable",
    };

    private static string DescribeInconclusive(ProbeResult probe) => probe.Kind switch
    {
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

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private TimeSpan Remaining(Stopwatch clock) => _shutdownLimit - clock.Elapsed;

    private readonly record struct ProbeResult(ProbeKind Kind, int? Status, string? Note);
}
