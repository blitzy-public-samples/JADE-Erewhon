using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
/// injection, created on the first call to it and reused, with its connection pool, by every later
/// call. The target is resolved on every call, so a routing reload applies to the next request. A call
/// holds a lease on its invoker until its response has been forwarded, written or aborted. When a
/// reload leaves a target out of the routing table, or the forwarder is disposed, the target's invoker
/// is retired: no new call takes it, and it is disposed once its last lease is released, so calls in
/// flight finish on it and no superseded invoker or connection pool outlives them. The class is safe
/// for concurrent use as a singleton.
/// </para>
/// <para>
/// The handler writes a known method in upper case whatever case the caller sent, and it cannot write
/// the asterisk form of <c>OPTIONS *</c>. A request whose request line would therefore differ from the
/// caller's is sent through an invoker of its own, built with the same settings and disposed when the
/// call ends, whose connection writes the caller's exact method token and target in place of the
/// handler's. Every other byte of the request is the handler's, unchanged.
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

    // One entry per target in use. An entry leaves the cache when it is retired, and its invoker is
    // disposed by whichever comes last of the retirement and the release of its last lease, so every
    // invoker created is disposed exactly once and never while a call is using it.
    private readonly ConcurrentDictionary<ResolvedTarget, InvokerEntry> _invokers = new();

    private readonly IHttpForwarder _forwarder;
    private readonly OperationRouter _router;
    private readonly PassThroughTransformer _transformer;
    private readonly SoapFaultWriter _faultWriter;
    private readonly ILogger<BackendForwarder> _logger;

    private int _disposed;

    // The routing table's target set the cache was last swept against; another instance means a reload.
    private IReadOnlySet<ResolvedTarget>? _sweptTargets;

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

        // A request whose request line the handler would write differently from the caller's gets an
        // invoker of its own that restores the caller's line, for this call only; every other call shares
        // the target's pooled invoker. SendAsync returns once the response body has been copied or the
        // call has failed, so the single-use invoker and its connection are released on every path.
        // A call on the target's pooled invoker holds a lease on it until the call ends, so neither a
        // reload nor Dispose can dispose that invoker under it; a single-use invoker needs none.
        var callerLine = CallerRequestLineToRestore(context);
        var singleUse = callerLine is null ? null : CreateInvoker(timeout, callerLine);
        using var lease = singleUse is null ? AcquireInvoker(target) : default;
        var invoker = singleUse ?? lease.Invoker;

        ForwarderError error;
        try
        {
            error = await _forwarder
                .SendAsync(context, prefix, invoker, config, _transformer, call.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            singleUse?.Dispose();
        }

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

    /// <summary>
    /// Retires every invoker: one no call is using is disposed at once, and one still in use when its last
    /// call ends. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A call that leases an entry after this scan sees the flag and retires the entry itself.
        foreach (var pair in _invokers)
        {
            Retire(pair);
        }
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

    // Leases the target's invoker, adding its entry on the first call. The entry creates no handler: only
    // a lease holder that passed the disposal re-check creates one, so every handler is reachable by a
    // retirement that will dispose it.
    private InvokerLease AcquireInvoker(ResolvedTarget target)
    {
        while (true)
        {
            var entry = _invokers.GetOrAdd(target, static key => new InvokerEntry(key));
            if (!entry.TryAcquire())
            {
                // Retired between the lookup and the lease: unlist it if it is still listed, then retry
                // with a new entry, unless the retirement was Dispose's.
                _invokers.TryRemove(new KeyValuePair<ResolvedTarget, InvokerEntry>(target, entry));
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                continue;
            }

            // Dispose sets the flag before it scans the cache, and the lease above is an interlocked
            // write, so either this check sees the flag or the scan still finds this entry and retires
            // it. Whoever unlists an entry retires it, so an entry the scan misses is retired anyway.
            if (Volatile.Read(ref _disposed) == 0)
            {
                return new InvokerLease(this, target, entry);
            }

            Retire(new KeyValuePair<ResolvedTarget, InvokerEntry>(target, entry));
            entry.Release();
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    // Runs when a call ends, however it ends, so it must not throw and replace the call's own outcome.
    // A routing table that cannot be read, such as after a reload that fails binding, leaves retirement
    // to a later call; nothing is hidden, because the next call's Resolve reads the same table and
    // reports the same failure.
    private void ReleaseInvoker(ResolvedTarget target, InvokerEntry entry)
    {
        entry.Release();

        IReadOnlySet<ResolvedTarget> inService;
        try
        {
            inService = _router.TargetsInService();
        }
        catch (Exception)
        {
            return;
        }

        // The call resolved its target from a table that a reload replaced while it was running.
        if (!inService.Contains(target))
        {
            Retire(new KeyValuePair<ResolvedTarget, InvokerEntry>(target, entry));
        }

        // The first call to end under a rebuilt table retires every target that table no longer routes to.
        if (ReferenceEquals(Volatile.Read(ref _sweptTargets), inService)
            || ReferenceEquals(Interlocked.Exchange(ref _sweptTargets, inService), inService))
        {
            return;
        }

        foreach (var pair in _invokers)
        {
            if (!inService.Contains(pair.Key))
            {
                Retire(pair);
            }
        }
    }

    // Unlists the entry if it is still listed under its target, then retires it; both steps are
    // idempotent, so concurrent sweeps, the call's own check and Dispose may all reach the same entry.
    private void Retire(KeyValuePair<ResolvedTarget, InvokerEntry> pair)
    {
        _invokers.TryRemove(pair);
        pair.Value.Retire();
    }

    /// <summary>
    /// One target's invoker with its lease count and retirement flag, kept in one integer so that both
    /// change atomically: the sign bit is the flag and the other bits count the calls holding a lease.
    /// </summary>
    /// <remarks>
    /// No lease is granted once the entry is retired. The invoker is created on first use by a lease
    /// holder and disposed by whichever comes last of <see cref="Retire"/> and the release of the last
    /// lease, so it is disposed exactly once and never while a call is using it. The count cannot reach
    /// the sign bit: that would take more than two billion calls in flight on one target.
    /// </remarks>
    /// <param name="target">The target whose deadline becomes the handler's connect timeout.</param>
    private sealed class InvokerEntry(ResolvedTarget target)
    {
        private const int RetiredFlag = int.MinValue;

        // Lazy makes two first calls to a target create at most one invoker; creating the entry creates none.
        private readonly Lazy<HttpMessageInvoker> _invoker = new(
            () => CreateInvoker(DeadlineOf(target)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private int _state;

        /// <summary>The target's invoker, created on first use; read only while holding a lease.</summary>
        public HttpMessageInvoker Invoker => _invoker.Value;

        /// <summary>Takes a lease unless the entry has been retired.</summary>
        /// <returns>True when the lease is held; false when the entry is retired and must not be used.</returns>
        public bool TryAcquire()
        {
            var state = Volatile.Read(ref _state);
            while ((state & RetiredFlag) == 0)
            {
                var observed = Interlocked.CompareExchange(ref _state, state + 1, state);
                if (observed == state)
                {
                    return true;
                }

                state = observed;
            }

            return false;
        }

        /// <summary>Returns a lease, disposing the invoker when it was the last lease of a retired entry.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _state) == RetiredFlag)
            {
                DisposeInvoker();
            }
        }

        /// <summary>
        /// Stops further leases and disposes the invoker at once when no lease is held. Calling it again
        /// does nothing.
        /// </summary>
        public void Retire()
        {
            if (Interlocked.Or(ref _state, RetiredFlag) == 0)
            {
                DisposeInvoker();
            }
        }

        // Runs once, with no lease held and none grantable, so no call can create the invoker after the
        // check. An entry that was never used, or whose creation threw, holds no invoker.
        private void DisposeInvoker()
        {
            if (_invoker.IsValueCreated)
            {
                _invoker.Value.Dispose();
            }
        }
    }

    /// <summary>One call's lease on its target's invoker, returned when the call ends.</summary>
    /// <param name="owner">The forwarder whose cache holds <paramref name="entry"/>.</param>
    /// <param name="target">The target the call resolved to.</param>
    /// <param name="entry">The entry a lease has already been taken on.</param>
    private readonly struct InvokerLease(BackendForwarder owner, ResolvedTarget target, InvokerEntry entry) : IDisposable
    {
        /// <summary>The leased invoker, created on the first call to its target.</summary>
        public HttpMessageInvoker Invoker => entry.Invoker;

        /// <summary>Returns the lease and retires invokers a reload has superseded; a default instance holds none.</summary>
        public void Dispose() => owner?.ReleaseInvoker(target, entry);
    }

    // SocketsHttpHandler writes a known method in its registered upper case whatever case the caller used
    // (HttpMethod.Parse returns the token it writes), and it cannot write the asterisk form. Every other
    // part of the line it writes is already the caller's: PassThroughTransformer hands it the origin form
    // of the raw target with canonicalisation disabled, so the path and query go out as they arrived.
    // CONNECT, whose target the handler would write in authority form, never gets this far: YARP refuses
    // it in any letter case before a request is created. Returns the caller's method token, and the
    // asterisk target, when the line must be restored; null when the handler already writes the caller's
    // line, or when a token is one no request line could carry verbatim, which Kestrel never admits and
    // which then takes the ordinary path.
    private static CallerRequestLine? CallerRequestLineToRestore(HttpContext context)
    {
        var method = context.Request.Method;
        var target = PassThroughTransformer.IsAsteriskForm(PassThroughTransformer.GetRawTarget(context))
            ? PassThroughTransformer.AsteriskFormTarget
            : null;

        if (!IsRequestLineToken(method) || (target is not null && !IsRequestLineToken(target)))
        {
            return null;
        }

        string written;
        try
        {
            written = HttpMethod.Parse(method).Method;
        }
        catch (FormatException)
        {
            // Not an HTTP token: YARP cannot create the outgoing request either, and its error path answers.
            return null;
        }

        return string.Equals(written, method, StringComparison.Ordinal) && target is null
            ? null
            : new CallerRequestLine(method, target);
    }

    // A method token or request target the rewriting stream may write: non-empty visible ASCII, so it holds
    // no space, CR or LF that would split or end the request line.
    private static bool IsRequestLineToken(string value) =>
        value.Length > 0 && value.All(c => c is > ' ' and <= '~');

    // An HttpMessageInvoker over a bare handler, never HttpClient or IHttpClientFactory, which would add
    // default headers and policies. With no ActivityHeadersPropagator the handler injects no traceparent,
    // so a caller's own trace headers pass through unchanged. Given a caller's request line, the invoker
    // serves one call only, because the line belongs to that call's request: every connection it opens
    // writes that line in place of the first request line the handler writes, and nothing else changes.
    private static HttpMessageInvoker CreateInvoker(TimeSpan connectTimeout, CallerRequestLine? callerLine = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
            ActivityHeadersPropagator = null,

            // Latin-1 maps each header byte to one char and back, matching the selectors Kestrel uses
            // in Program.cs, so non-ASCII header bytes pass unchanged in both directions instead of
            // failing the call (the handler's default accepts only ASCII request header values).
            RequestHeaderEncodingSelector = static (_, _) => System.Text.Encoding.Latin1,
            ResponseHeaderEncodingSelector = static (_, _) => System.Text.Encoding.Latin1,
        };

        if (callerLine is not null)
        {
            handler.PlaintextStreamFilter = (filterContext, _) =>
                ValueTask.FromResult<Stream>(new RequestLineRewritingStream(filterContext.PlaintextStream, callerLine));
        }

        // A request with content, every write included, reaches the backend at most once, also when it
        // carries "Expect: 100-continue" and the backend closes the connection before the body starts.
        // SingleDeliveryHandler wraps the stream the filter above returns and adds, removes and changes
        // no header or byte.
        return new HttpMessageInvoker(new SingleDeliveryHandler(handler), disposeHandler: true);
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

    /// <summary>The parts of a caller's request line the backend must receive as the caller sent them.</summary>
    /// <param name="Method">The caller's exact method token, for example <c>get</c>.</param>
    /// <param name="Target">The request target to write, <c>*</c>; null to keep the target the handler writes.</param>
    private sealed record CallerRequestLine(string Method, string? Target);

    /// <summary>
    /// The plaintext stream of one backend connection, whose first request line carries the caller's
    /// method token, and target when one is given, in place of the handler's. Every other byte passes
    /// unchanged in both directions.
    /// </summary>
    /// <remarks>
    /// Writes are held back only until the first CRLF has been written, however many writes the line
    /// spans; the line is bounded by the request target Kestrel accepted. The rewritten line then goes out
    /// with every byte written after it, and later writes, the headers' remainder and the body included,
    /// pass straight through, as do reads, flushes and disposal. A held-back line that is not
    /// <c>method SP target SP version</c> goes out unchanged. One instance serves one connection and
    /// shares no state with any other.
    /// </remarks>
    private sealed class RequestLineRewritingStream : Stream
    {
        private const byte Space = (byte)' ';

        private readonly Stream _inner;
        private readonly CallerRequestLine _callerLine;

        // The bytes written so far of the first request line; null once that line has gone out.
        private ArrayBufferWriter<byte>? _pendingLine = new(256);

        public RequestLineRewritingStream(Stream inner, CallerRequestLine callerLine)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(callerLine);
            if (!IsRequestLineToken(callerLine.Method) || (callerLine.Target is not null && !IsRequestLineToken(callerLine.Target)))
            {
                throw new ArgumentException("The method token and target must be non-empty visible ASCII.", nameof(callerLine));
            }

            _inner = inner;
            _callerLine = callerLine;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanWrite => _inner.CanWrite;

        public override bool CanSeek => false;

        public override bool CanTimeout => _inner.CanTimeout;

        public override int ReadTimeout
        {
            get => _inner.ReadTimeout;
            set => _inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => _inner.WriteTimeout;
            set => _inner.WriteTimeout = value;
        }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_pendingLine is null)
            {
                _inner.Write(buffer);
                return;
            }

            if (TakeHead(buffer) is { } head)
            {
                _inner.Write(head);
            }
        }

        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pendingLine is null)
            {
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled(cancellationToken);
            }

            return TakeHead(buffer.Span) is { } head
                ? _inner.WriteAsync(head, cancellationToken)
                : ValueTask.CompletedTask;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override ValueTask DisposeAsync()
        {
            _pendingLine = null;
            GC.SuppressFinalize(this);
            return _inner.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pendingLine = null;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        // Holds 'data' back until the first CRLF has arrived, then returns the rewritten line followed by
        // every byte written after it; null while the line is still incomplete.
        private byte[]? TakeHead(ReadOnlySpan<byte> data)
        {
            var pending = _pendingLine!;

            // The search starts one byte back, so a CR that ended the previous write pairs with an LF
            // that starts this one.
            var searchFrom = Math.Max(pending.WrittenCount - 1, 0);
            pending.Write(data);
            var held = pending.WrittenSpan;
            var lineEnd = held[searchFrom..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                return null;
            }

            lineEnd += searchFrom;
            var line = held[..lineEnd];
            var rest = held[lineEnd..];
            var methodEnd = line.IndexOf(Space);
            var targetEnd = line.LastIndexOf(Space);

            byte[] head;
            if (methodEnd <= 0 || targetEnd <= methodEnd)
            {
                head = held.ToArray();
            }
            else
            {
                var target = _callerLine.Target is null ? line[(methodEnd + 1)..targetEnd] : Encoding.ASCII.GetBytes(_callerLine.Target);
                var versionPart = line[targetEnd..];
                var methodLength = Encoding.ASCII.GetByteCount(_callerLine.Method);
                head = new byte[methodLength + 1 + target.Length + versionPart.Length + rest.Length];

                var cursor = Encoding.ASCII.GetBytes(_callerLine.Method, head);
                head[cursor++] = Space;
                target.CopyTo(head.AsSpan(cursor));
                cursor += target.Length;
                versionPart.CopyTo(head.AsSpan(cursor));
                cursor += versionPart.Length;
                rest.CopyTo(head.AsSpan(cursor));
            }

            _pendingLine = null;
            return head;
        }
    }
}

// SingleDeliveryHandler, which sends a request with content to the backend at most once, lives in this
// file because CreateInvoker is its only user: it wraps every per-target handler.
/// <summary>
/// Sends a request that carries content, which is every write and every request with a body, to the
/// backend at most once over HTTP/1.x, however the backend ends the connection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SocketsHttpHandler"/> resends a request on a new connection, up to three more times, when
/// the backend closes the connection before answering and no body byte has gone out yet. A body-less
/// request and a request holding its body back for <c>Expect: 100-continue</c> can both be in that
/// state after the backend has received the whole request head, so each resend can be another backend
/// transaction. The handler has no setting that turns the resend off, and it decides on the resend only
/// when a read of the connection ends the stream before the response starts; a failed read is never
/// resent.
/// </para>
/// <para>
/// This handler wraps each HTTP/1.x connection stream. A read that ends the stream after a request
/// with content was written and before any byte of its answer arrived is reported as an
/// <see cref="IOException"/> instead, so that request fails once and the forwarder answers it as an
/// unreachable backend. A request without content keeps the handler's resend, and a connection the
/// backend closed while idle is still discarded before reuse. No byte sent or received changes.
/// </para>
/// </remarks>
internal sealed class SingleDeliveryHandler : DelegatingHandler
{
    // True while a request with content is being sent. Set only inside the send, so every write the
    // handler makes for that request sees it, including a body sent after an Expect wait, and nothing
    // after the send returns does.
    private static readonly AsyncLocal<bool> SendingContent = new();

    /// <summary>Wraps <paramref name="inner"/> and installs the connection stream guard on it.</summary>
    /// <param name="inner">
    /// A handler that has not sent a request yet. A plaintext stream filter it already has still runs
    /// first, and the guard wraps the stream that filter returns, so the guard sees the bytes as they
    /// go to and come from the connection.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public SingleDeliveryHandler(SocketsHttpHandler inner)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        var filter = inner.PlaintextStreamFilter;
        if (filter is null)
        {
            inner.PlaintextStreamFilter = static (context, _) => ValueTask.FromResult(Guard(context, context.PlaintextStream));
        }
        else
        {
            inner.PlaintextStreamFilter = async (context, cancellationToken) =>
                Guard(context, await filter(context, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Content is null
            ? base.SendAsync(request, cancellationToken)
            : SendWithContentAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null)
        {
            return base.Send(request, cancellationToken);
        }

        // A synchronous method does not restore the caller's value on return, so it is restored here.
        var previous = SendingContent.Value;
        SendingContent.Value = true;
        try
        {
            return base.Send(request, cancellationToken);
        }
        finally
        {
            SendingContent.Value = previous;
        }
    }

    // An async method's change to an AsyncLocal is undone for its caller when it returns.
    private async Task<HttpResponseMessage> SendWithContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendingContent.Value = true;
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // HTTP/1.x carries one request at a time on a connection, which the guard relies on; any other
    // protocol keeps the stream it was given.
    private static Stream Guard(SocketsHttpPlaintextStreamFilterContext context, Stream stream) =>
        context.NegotiatedHttpVersion.Major == 1 ? new GuardedConnectionStream(stream) : stream;

    /// <summary>
    /// A connection stream that reports the end of the stream as an <see cref="IOException"/> while a
    /// request with content written on it has had no byte of answer.
    /// </summary>
    /// <param name="inner">The connection's plaintext stream, owned by this instance.</param>
    private sealed class GuardedConnectionStream(Stream inner) : Stream
    {
        // Written by every non-empty write and cleared by every read that returns bytes. The handler
        // writes a request head before it reads the response, so a read that ends the stream finds the
        // value its own request's writes left.
        private volatile bool _awaitingAnswerToContent;

        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer), buffer.Length);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Every response byte passes through here, so a read that completes later reuses a pooled state
        // machine instead of allocating one.
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            Observe(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Mark(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            Mark(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Mark(int length)
        {
            if (length > 0)
            {
                _awaitingAnswerToContent = SendingContent.Value;
            }
        }

        // A zero-byte read into an empty buffer only waits for data and is not the end of the stream.
        private int Observe(int read, int requested)
        {
            if (read > 0)
            {
                _awaitingAnswerToContent = false;
            }
            else if (requested > 0 && _awaitingAnswerToContent)
            {
                throw new IOException("The backend closed the connection without answering a request that carries content.");
            }

            return read;
        }
    }
}
