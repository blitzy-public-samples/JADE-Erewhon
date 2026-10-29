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
/// Waits until a component's admin listener answers <c>GET /health</c> with 200, polling once per
/// <see cref="HarnessOptions.ReadinessPollInterval"/> up to <see cref="HarnessOptions.ReadinessTimeout"/>.
/// A component that never becomes ready is reported as a <c>CHK-READY:&lt;component&gt;</c>
/// <c>timeout</c> item and the run continues to the report.
/// </summary>
/// <remarks>
/// <para>Readiness is established only here: the <c>aspnet</c> runtime images carry no <c>curl</c>, so
/// Compose cannot health-check the facade or the stand-in.</para>
/// <para>Components are reached only through their admin URL; the probe sends no credential and no
/// header of its own, because the admin listeners are unauthenticated and contained by network
/// placement. An admin URL carrying user information is therefore rejected before any probe.</para>
/// <para>Network conditions and caller cancellation never throw: they end in the timeout item, so
/// replay's report and shutdown steps always run. Instances are thread-safe, so one instance can wait on
/// the facade and the stand-in concurrently. One line is printed per wait, never one per probe.</para>
/// </remarks>
/// <param name="options">The harness settings that own the readiness limit and poll interval.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
public sealed class Readiness(HarnessOptions options) : IDisposable
{
    private static readonly TimeSpan MinProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly HarnessOptions options = options ?? throw new ArgumentNullException(nameof(options));

    // The first initializer has already rejected a null options, so the parameter is non-null here.
    private readonly HttpClient client = CreateClient(ProbeTimeoutFor(options.ReadinessPollInterval));

    private volatile bool disposed;

    /// <summary>
    /// Polls <c>GET &lt;adminUrl&gt;/health</c> until it answers 200, the readiness limit passes or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="component">The component label, <c>facade</c> or <c>standin</c>.</param>
    /// <param name="adminUrl">
    /// The component's admin base URL, for example <c>http://standin:9090</c>. Blank for an external
    /// backend, which has no admin listener.
    /// </param>
    /// <param name="ct">Stops polling early; the component then counts as not ready.</param>
    /// <returns>
    /// Null when the component answered 200, or when <paramref name="adminUrl"/> is blank and nothing
    /// was probed. Otherwise <c>CHK-READY:&lt;component&gt;</c> with nature <c>timeout</c>: the limit
    /// passed, the caller cancelled, or <paramref name="adminUrl"/> is not an absolute
    /// <c>http</c>/<c>https</c> URL without user information. The detail of a rejected URL gives the
    /// reason without quoting the setting.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public async Task<FailureItem?> WaitAsync(string component, string? adminUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ObjectDisposedException.ThrowIf(disposed, this);

        // An external backend has no admin listener; the caller records its substitution separately.
        if (string.IsNullOrWhiteSpace(adminUrl))
        {
            return null;
        }

        // The setting is never quoted: a rejected URL may carry a password.
        if (!TryCreateProbeUri(adminUrl, out var probeUri, out var problem))
        {
            return NotReady(component, $"invalid admin URL: {problem}", limit: null);
        }

        var limit = options.ReadinessTimeout;
        var interval = options.ReadinessPollInterval;
        var probeTimeout = ProbeTimeoutFor(interval);
        var watch = Stopwatch.StartNew();
        var last = "no probe completed";

        FailureItem Expired() => NotReady(
            component,
            string.Create(CultureInfo.InvariantCulture, $"no 200 from {probeUri} within {limit.TotalSeconds:0} s; last: {last}"),
            limit);

        while (!ct.IsCancellationRequested)
        {
            var probeStarted = watch.Elapsed;
            var remaining = limit - probeStarted;
            if (remaining <= TimeSpan.Zero)
            {
                return Expired();
            }

            var outcome = await ProbeAsync(probeUri, probeTimeout < remaining ? probeTimeout : remaining, ct);
            if (outcome.Ready)
            {
                Console.Out.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $"ready: {component} ({watch.ElapsedMilliseconds} ms)"));
                return null;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            last = outcome.Observation;

            // Sleep out the rest of the interval. A next probe that would get less than the minimum probe
            // time before the deadline is not started: it could only end in its own cut-off, and that
            // would replace the last real observation in the detail.
            var now = watch.Elapsed;
            var untilDeadline = limit - now;
            var pause = interval - (now - probeStarted);
            var finalPoll = untilDeadline - pause < MinProbeTimeout;
            if (finalPoll)
            {
                pause = untilDeadline;
            }

            if (pause > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(pause, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (finalPoll)
            {
                return Expired();
            }
        }

        var cancelled = string.Create(
            CultureInfo.InvariantCulture,
            $"cancelled after {watch.ElapsedMilliseconds} ms; last: {last}");
        return NotReady(component, cancelled, limit: null);
    }

    /// <summary>Releases the probe client.</summary>
    public void Dispose()
    {
        disposed = true;
        client.Dispose();
    }

    // One probe, bounded by its own linked token rather than HttpClient.Timeout, so a probe timeout
    // (caller token not cancelled) stays distinguishable from caller cancellation.
    private async Task<ProbeOutcome> ProbeAsync(Uri probeUri, TimeSpan timeout, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeCts.Token);
            return response.StatusCode == HttpStatusCode.OK
                ? new ProbeOutcome(true, "status 200")
                : new ProbeOutcome(false, string.Create(CultureInfo.InvariantCulture, $"status {(int)response.StatusCode}"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var observation = string.Create(
                CultureInfo.InvariantCulture,
                $"no answer within {timeout.TotalMilliseconds:0} ms");
            return new ProbeOutcome(false, observation);
        }
        catch (OperationCanceledException)
        {
            return new ProbeOutcome(false, "cancelled by the caller");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeOutcome(false, $"{nameof(HttpRequestException)} ({ex.HttpRequestError}): {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return new ProbeOutcome(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static FailureItem NotReady(string component, string detail, TimeSpan? limit)
    {
        Console.Out.WriteLine(limit is { } l
            ? string.Create(CultureInfo.InvariantCulture, $"not ready: {component} after {l.TotalSeconds:0} s ({detail})")
            : $"not ready: {component} ({detail})");
        return new FailureItem(FailureItemIds.Ready(component), FailureItemIds.Timeout, Detail: detail);
    }

    // The admin URL is an opaque base whose path is kept, and /health is appended to it. Only an absolute
    // http or https URL without user information can be probed, so any other scheme (including a
    // host:port that parses as a scheme) or a URL carrying credentials counts as invalid rather than
    // throwing. The probe URL is checked under the same rule, so a timeout detail never holds credentials.
    private static bool TryCreateProbeUri(
        string adminUrl,
        [NotNullWhen(true)] out Uri? probeUri,
        out string problem)
    {
        if (!CaseRunner.TryParseHttpUrl(adminUrl, out _, out problem))
        {
            probeUri = null;
            return false;
        }

        return CaseRunner.TryParseHttpUrl(adminUrl.Trim().TrimEnd('/') + AdminRoutes.Health, out probeUri, out problem);
    }

    // min(interval, 5 s), but at least 500 ms; the caller further caps it at the time left.
    private static TimeSpan ProbeTimeoutFor(TimeSpan interval)
    {
        var timeout = interval < MaxProbeTimeout ? interval : MaxProbeTimeout;
        return timeout < MinProbeTimeout ? MinProbeTimeout : timeout;
    }

    // No proxy, redirects, cookies or decompression: the probe must reach the admin listener itself, and
    // a 3xx or a proxy's answer is not the component saying it is ready.
    private static HttpClient CreateClient(TimeSpan connectTimeout) =>
        new(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = connectTimeout,
            },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private readonly record struct ProbeOutcome(bool Ready, string Observation);
}
