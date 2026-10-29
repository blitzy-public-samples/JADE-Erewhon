using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Yarp.ReverseProxy.Forwarder;

namespace Erewhon.Facade.Forwarding;

/// <summary>
/// Forwards one REST or SOAP request to the target its operation is routed to, within that target's
/// deadline, and builds the only responses the facade makes besides the WSDL: backend unavailable and
/// backend timed out.
/// </summary>
/// <remarks>
/// <para>
/// Every request the facade does not answer itself goes through <see cref="ForwardAsync"/>: the 22
/// catalogue operations, suffixed REST forms, unmatched traffic on the REST and SOAP listeners, and WSDL
/// requests when no WSDL file is present. The backend's status, end-to-end headers and body bytes reach
/// the caller unchanged, 4xx and 5xx results included; nothing is parsed or re-serialised.
/// </para>
/// <para>
/// A failure before the backend's response headers arrive gives REST 502 or 504 with a problem+json body,
/// or a SOAP 1.1 Fault from <see cref="SoapFaultWriter"/>. A failure after them aborts the connection,
/// because the backend's status line has already been taken over and must never be replaced. Neither
/// path outlasts the deadline: a linked token cancels the call when it expires, even while
/// <see cref="ForwarderRequestConfig.ActivityTimeout"/> is still being reset by activity.
/// </para>
/// <para>
/// Each distinct <see cref="ResolvedTarget"/> gets its own <see cref="HttpMessageInvoker"/> over a
/// <see cref="SocketsHttpHandler"/> with no proxy, redirects, cookies, decompression or trace-header
/// injection. The target is resolved on every call, so a routing reload applies to the next request.
/// The class keeps no per-request state and is safe for concurrent use as a singleton.
/// </para>
/// <para>
/// Each call writes one operational log line (surface, operation, target, status, duration, outcome).
/// No body or header value is logged, and no write is audited or persisted: state changes only in the
/// backend.
/// </para>
/// </remarks>
public sealed partial class BackendForwarder : IDisposable
{
    /// <summary>The content type of the REST failure responses, without a charset parameter.</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>The problem <c>title</c> of the REST 502 response.</summary>
    public const string UnavailableTitle = "Backend unavailable";

    /// <summary>The problem <c>title</c> of the REST 504 response.</summary>
    public const string TimedOutTitle = "Backend timed out";

    /// <summary>The operation name logged and reported for a request that matched no catalogue operation.</summary>
    public const string UnmatchedOperation = "unmatched";

    private const string ProblemType = "about:blank";

    private const string OutcomeForwarded = "forwarded";
    private const string OutcomeUnavailable = "unavailable";
    private const string OutcomeTimeout = "timeout";

    // The routing validators bound deadlines only from below, while CancelAfter, ActivityTimeout and
    // SocketsHttpHandler.ConnectTimeout reject more than int.MaxValue milliseconds (about 24.8 days). A
    // longer configured deadline is served as that maximum rather than failing every call.
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromMilliseconds(int.MaxValue);

    // Lazy makes the race between two first requests to a target create at most one invoker, so every
    // invoker that exists is reachable from here and disposed with the forwarder.
    private readonly ConcurrentDictionary<ResolvedTarget, Lazy<HttpMessageInvoker>> _invokers = new();

    private readonly IHttpForwarder _forwarder;
    private readonly OperationRouter _router;
    private readonly PassThroughTransformer _transformer;
    private readonly SoapFaultWriter _faultWriter;
    private readonly ILogger<BackendForwarder> _logger;

    private int _disposed;

    /// <summary>Creates the forwarder; <c>Program.cs</c> registers it as a singleton.</summary>
    /// <param name="forwarder">YARP's direct forwarder, registered by <c>AddHttpForwarder()</c>.</param>
    /// <param name="router">Resolves each request's target from its surface and operation.</param>
    /// <param name="transformer">The byte-faithful pass-through transform used for every call.</param>
    /// <param name="faultWriter">Writes the SOAP Fault of a failed SOAP call.</param>
    /// <param name="logger">Receives one line per forwarded call.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public BackendForwarder(
        IHttpForwarder forwarder,
        OperationRouter router,
        PassThroughTransformer transformer,
        SoapFaultWriter faultWriter,
        ILogger<BackendForwarder> logger)
    {
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(transformer);
        ArgumentNullException.ThrowIfNull(faultWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _forwarder = forwarder;
        _router = router;
        _transformer = transformer;
        _faultWriter = faultWriter;
        _logger = logger;
    }

    /// <summary>The category of a <see cref="ForwarderError"/>, which decides what the caller receives.</summary>
    private enum FailureCategory
    {
        /// <summary>The caller disconnected or sent a broken body; nothing is written.</summary>
        CallerSide,

        /// <summary>The backend's status and headers were already taken over; the connection is aborted.</summary>
        ResponseStarted,

        /// <summary>No backend response was taken over; the facade writes its failure response.</summary>
        PreResponse,
    }

    /// <summary>
    /// Forwards the request to the target routed for <paramref name="operation"/> and copies the
    /// backend's response to the caller, or writes the facade's failure response when the backend is
    /// unreachable or misses the deadline.
    /// </summary>
    /// <param name="context">The incoming request on the REST or SOAP listener.</param>
    /// <param name="surface">The listener the request arrived on.</param>
    /// <param name="operation">
    /// The matched catalogue operation; null for a request that matched none, which goes to
    /// <c>Routing:DefaultTarget</c> like any listed operation goes to its own target.
    /// </param>
    /// <returns>A task that completes when the response has been forwarded, written or aborted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The forwarder has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="surface"/> is not routable, or <paramref name="operation"/> belongs to the other
    /// surface, as reported by <see cref="OperationRouter.Resolve"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The routing entry serving the request names no usable target.</exception>
    public async Task ForwardAsync(HttpContext context, Surface surface, OperationDescriptor? operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var target = _router.Resolve(surface, operation);
        var opName = operation?.JadeMethod ?? UnmatchedOperation;
        var timeout = DeadlineOf(target);
        var invoker = InvokerFor(target);

        // The deadline's own source is cancelled only by its timer, so after the call it still tells an
        // expired deadline apart from a caller abort; the call itself observes both.
        var start = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);

        // HTTP/1.1, as the JADE applications speak it; response buffering stays off so bodies stream.
        var config = new ForwarderRequestConfig
        {
            ActivityTimeout = timeout,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // PassThroughTransformer appends the caller's raw request target to this prefix.
        var prefix = target.BaseUrl.AbsoluteUri.TrimEnd('/');

        var error = await _forwarder
            .SendAsync(context, prefix, invoker, config, _transformer, call.Token)
            .ConfigureAwait(false);

        var outcome = error == ForwarderError.None
            ? OutcomeForwarded
            : await HandleFailureAsync(context, surface, opName, timeout, error, deadline, start).ConfigureAwait(false);

        LogBackendCall(
            _logger,
            surface,
            opName,
            target.Name,
            context.Response.StatusCode,
            (long)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds),
            outcome);
    }

    /// <summary>Disposes every invoker created so far. Calling it again does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var invoker in _invokers.Values)
        {
            if (invoker.IsValueCreated)
            {
                invoker.Value.Dispose();
            }
        }

        _invokers.Clear();
    }

    /// <summary>Decides and writes what the caller receives after a failed backend call.</summary>
    /// <returns>The logged outcome.</returns>
    private async Task<string> HandleFailureAsync(
        HttpContext context,
        Surface surface,
        string opName,
        TimeSpan timeout,
        ForwarderError error,
        CancellationTokenSource deadline,
        long start)
    {
        var category = Categorise(error);
        var exception = context.GetForwarderErrorFeature()?.Exception;

        // YARP reports an expired deadline token as RequestCanceled with 502, so the timeout is read from
        // the timer-only source, never from YARP's status.
        var deadlineFired = deadline.IsCancellationRequested;

        // RequestAborted alone does not mean the caller left: when the backend fails after the response
        // started, YARP aborts the connection itself, which also cancels RequestAborted. The caller is
        // taken to have aborted only when the call ended in a cancellation before the deadline fired.
        var clientAborted = context.RequestAborted.IsCancellationRequested
            && !deadlineFired
            && (exception is null or OperationCanceledException);

        // The caller is gone or sent a broken body: there is nobody to answer, and YARP's state stands.
        if (clientAborted || category == FailureCategory.CallerSide)
        {
            return OutcomeForwarded;
        }

        var elapsed = Stopwatch.GetElapsedTime(start);
        var isTimeout = error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout
            || deadlineFired
            || HasTimeoutInChain(exception)
            || elapsed >= timeout;
        var outcome = isTimeout ? OutcomeTimeout : OutcomeUnavailable;

        // The backend's status line and headers are already the response's: a facade 502 or 504 would
        // replace them, so the response is aborted and never re-headed.
        if (context.Response.HasStarted || category == FailureCategory.ResponseStarted)
        {
            context.Abort();
            return outcome;
        }

        // Drops the empty-bodied 502/504 and any headers YARP set on its error path.
        context.Response.Clear();

        try
        {
            if (surface == Surface.Soap)
            {
                var faultString = isTimeout ? SoapFaultWriter.BackendTimedOut : SoapFaultWriter.BackendUnavailable;
                await _faultWriter.WriteAsync(context.Response, faultString, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await WriteProblemAsync(context.Response, isTimeout, opName, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception writeFailure) when (writeFailure is OperationCanceledException or IOException)
        {
            // The caller disconnected while the failure response was being written; nobody is left to answer.
        }

        return outcome;
    }

    private static FailureCategory Categorise(ForwarderError error) => error switch
    {
        ForwarderError.RequestBodyClient
            or ForwarderError.ResponseBodyClient
            or ForwarderError.UpgradeRequestClient
            or ForwarderError.UpgradeResponseClient => FailureCategory.CallerSide,

        ForwarderError.ResponseBodyDestination
            or ForwarderError.ResponseBodyCanceled
            or ForwarderError.UpgradeResponseDestination
            or ForwarderError.UpgradeResponseCanceled => FailureCategory.ResponseStarted,

        // Request (refused, unresolved or failed connect, reset before headers), RequestTimedOut,
        // RequestCanceled, RequestBodyCanceled, RequestBodyDestination, ResponseHeaders, RequestCreation,
        // NoAvailableDestinations, UpgradeRequestCanceled, UpgradeRequestDestination,
        // UpgradeActivityTimeout and any member a later YARP adds.
        _ => FailureCategory.PreResponse,
    };

    // SocketsHttpHandler reports an expired ConnectTimeout as a cancellation wrapping a TimeoutException.
    private static bool HasTimeoutInChain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static Task WriteProblemAsync(HttpResponse response, bool isTimeout, string opName, CancellationToken cancellationToken)
    {
        var status = isTimeout ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
        var body = BuildProblemBody(isTimeout, status, opName);

        response.StatusCode = status;
        response.ContentType = ProblemContentType;
        response.ContentLength = body.Length;

        return response.Body.WriteAsync(body, cancellationToken).AsTask();
    }

    // Written by hand rather than through ProblemDetails, which adds a traceId and varies member order:
    // the harness's discrepancy signatures need these bytes identical on every run, so the body holds
    // only fixed text and the operation name, in the order type, title, status, detail.
    private static ReadOnlyMemory<byte> BuildProblemBody(bool isTimeout, int status, string opName)
    {
        var buffer = new ArrayBufferWriter<byte>(192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", ProblemType);
            writer.WriteString("title", isTimeout ? TimedOutTitle : UnavailableTitle);
            writer.WriteNumber("status", status);
            writer.WriteString(
                "detail",
                isTimeout
                    ? $"The backend for operation {opName} did not respond in time."
                    : $"The backend for operation {opName} could not be reached.");
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static TimeSpan DeadlineOf(ResolvedTarget target) =>
        target.Timeout > MaxDeadline ? MaxDeadline : target.Timeout;

    private HttpMessageInvoker InvokerFor(ResolvedTarget target) =>
        _invokers.GetOrAdd(
            target,
            static key => new Lazy<HttpMessageInvoker>(
                () => CreateInvoker(DeadlineOf(key)),
                LazyThreadSafetyMode.ExecutionAndPublication))
        .Value;

    // An HttpMessageInvoker over a bare handler, never HttpClient or IHttpClientFactory, which would add
    // default headers and policies. With no ActivityHeadersPropagator the handler injects no traceparent,
    // so a caller's own trace headers pass through unchanged.
    private static HttpMessageInvoker CreateInvoker(TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
            ActivityHeadersPropagator = null,
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    [LoggerMessage(
        EventId = 1001,
        EventName = "BackendCall",
        Level = LogLevel.Information,
        Message = "Backend call {Surface} {Operation} -> {Target}: status {Status}, {DurationMs} ms, outcome {Outcome}")]
    private static partial void LogBackendCall(
        ILogger logger,
        Surface surface,
        string operation,
        string target,
        int status,
        long durationMs,
        string outcome);
}
