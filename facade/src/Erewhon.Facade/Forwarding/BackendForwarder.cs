using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
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
/// A backend that gives no usable response, or an expired deadline, before the backend's status line
/// and headers have been taken over gives REST 502 or 504 with a problem+json body, or a SOAP 1.1 Fault
/// from <see cref="SoapFaultWriter"/>. A failure after them aborts the connection, because the
/// backend's status line has already been taken over and must never be replaced. Neither path outlasts
/// the deadline: a linked token cancels the call when it expires, even while
/// <see cref="ForwarderRequestConfig.ActivityTimeout"/> is still being reset by activity, and writing a
/// failure response has a budget of its own, after which the connection is aborted. Any other failure
/// the caller did not cause, short of an exception escaping the call, aborts the connection with
/// nothing written, so the facade builds no failure response besides these two.
/// </para>
/// <para>
/// The logged outcome says how the call ended:
/// <list type="bullet">
/// <item><description>
/// <c>forwarded</c>: the backend's response was taken over; the status is the backend's. That includes an
/// answer the backend gave before it had read the whole upload, such as 413 for its own body-size limit,
/// and a response relayed in full before YARP found the upload incomplete.
/// </description></item>
/// <item><description>
/// <c>unavailable</c>: before the deadline, the backend gave no response whose status line and headers
/// could be taken over. It could not be reached (refused, unresolved or failed connect), or it was
/// reached and then closed or reset the connection, dropped the upload, answered with bytes that are no
/// valid status line or header section, or sent a head that cannot be relayed, such as two different
/// <c>Content-Length</c> values. The caller gets 502 or the "Backend unavailable" Fault, whether or not
/// the request had a body and however much of it the backend received.
/// </description></item>
/// <item><description>
/// <c>timeout</c>: the deadline expired; the caller gets 504 or the "Backend timed out" Fault, or an
/// aborted connection when the backend's response had already started.
/// </description></item>
/// <item><description>
/// <c>aborted</c>: the caller left or broke its own request (disconnected, reset or truncated its upload)
/// before the call completed, including while the backend's response head was being sent to it. Nothing
/// is written; the status is the one already sent, or 0 when no response was sent.
/// </description></item>
/// <item><description>
/// <c>failed</c>: anything else. The backend failed the call after its status line and headers had been
/// taken over, for example by dropping the connection during the response body; the facade could not
/// read the request body (for example when the temporary storage of a buffered SOAP body is full) or
/// create the outgoing request; or an exception escaped. The connection is aborted and nothing is
/// written; the status is the backend's already taken over, or 0 when no response was sent. An
/// escaping exception is rethrown unchanged and logged with the status already sent, or the 500 the
/// server sends for an unhandled exception.
/// </description></item>
/// </list>
/// Forwarded and aborted calls are logged at Information, every other outcome at Warning.
/// </para>
/// <para>
/// Each distinct <see cref="ResolvedTarget"/> gets its own <see cref="HttpMessageInvoker"/> over a
/// <see cref="SocketsHttpHandler"/> with no proxy, redirects, cookies, decompression or trace-header
/// injection, whose connections are <see cref="BackendConnectionStream"/>s, created on the first call to
/// it and reused, with its connection pool, by every later call. The target is resolved on every call,
/// so a routing reload applies to the next request. A call holds a lease on its invoker until its
/// response has been forwarded, written or aborted. When a reload leaves a target out of the routing
/// table, or the forwarder is disposed, the target's invoker is retired: no new call takes it, and it is
/// disposed once its last lease is released, so calls in flight finish on it and no superseded invoker
/// or connection pool outlives them. The class keeps no per-request state, which lives in each call's
/// <see cref="BackendCallState"/>, and is safe for concurrent use as a singleton.
/// </para>
/// <para>
/// The handler writes a known method in upper case whatever case the caller sent, and it cannot write
/// the asterisk form of <c>OPTIONS *</c>. A request whose request line would therefore differ from the
/// caller's is sent through an invoker of its own, built with the same settings and disposed when the
/// call ends, whose connection writes the caller's exact method token and target in place of the
/// handler's. Every other byte of the request is the handler's, unchanged.
/// </para>
/// <para>
/// Each call writes exactly one operational log line (surface, operation, target, status, duration,
/// outcome), whether it returns or throws; a call whose target was never resolved names the target
/// <c>unresolved</c>. No body or header value is logged, and no write is audited or persisted: state
/// changes only in the backend.
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
    private const string OutcomeAborted = "aborted";
    private const string OutcomeFailed = "failed";

    // The target logged for a call whose target was never resolved.
    private const string UnresolvedTarget = "unresolved";

    // The status logged when no response was sent: the caller left first, or the connection was aborted.
    private const int NoResponseStatus = 0;

    // How long writing a 502, 504 or Fault may take. The body is a few hundred bytes, so only a caller
    // that stops reading holds the write up; the connection is then aborted rather than left to hold the
    // failure path, and the call's line, beyond the deadline.
    private static readonly TimeSpan FailureWriteBudget = TimeSpan.FromSeconds(2);

    // The routing validators bound deadlines only from below, while CancelAfter, ActivityTimeout and
    // SocketsHttpHandler.ConnectTimeout reject more than int.MaxValue milliseconds (about 24.8 days). A
    // longer configured deadline is served as that maximum rather than failing every call.
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromMilliseconds(int.MaxValue);

    // SocketsHttpHandler's own wait for 100 Continue on a request that carries "Expect: 100-continue".
    private static readonly TimeSpan DefaultExpectContinueWait = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Forwards the request to the target routed for <paramref name="operation"/> and copies the
    /// backend's response to the caller, or writes the facade's failure response when the backend gives
    /// no usable response or misses the deadline.
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
    /// <remarks>
    /// Every exception after the argument check, from these or from YARP, the transformer or a failure
    /// response write, propagates unchanged after the call's line has been written with outcome
    /// <c>failed</c>.
    /// </remarks>
    public async Task ForwardAsync(HttpContext context, Surface surface, OperationDescriptor? operation)
    {
        ArgumentNullException.ThrowIfNull(context);

        // From here on, every call writes exactly one line, from the finally block, with what the call
        // reached; an exception still propagates unchanged.
        var start = Stopwatch.GetTimestamp();
        var opName = operation?.JadeMethod ?? UnmatchedOperation;
        var targetName = UnresolvedTarget;
        CallResult? result = null;
        BackendCallState? callState = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var target = _router.Resolve(surface, operation);
            targetName = target.Name;
            var timeout = DeadlineOf(target);

            // The deadline's own source is cancelled only by its timer, so after the call it still tells an
            // expired deadline apart from a caller abort; the call itself observes both.
            var callStart = Stopwatch.GetTimestamp();
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

            // The deadline can cancel YARP's read of an upload still arriving; this keeps the server's body
            // reader valid for its drain after the 504 or Fault. SOAP inspection installs it earlier.
            CancellableRequestBody.Install(context);

            // Shared with the body reader, the backend connection and the transformer for the rest of the
            // call, so that a backend answering before the upload is over has its answer returned.
            callState = BackendCallState.Begin(PassThroughTransformer.CarriesLengthDelimitedBody(context));

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

            result = error == ForwarderError.None
                ? new CallResult(OutcomeForwarded, context.Response.StatusCode)
                : await HandleFailureAsync(context, surface, opName, timeout, error, deadline, callStart, callState).ConfigureAwait(false);
        }
        finally
        {
            callState?.End();

            // No result means an exception is propagating: the server answers it with 500 unless the
            // response has already started, in which case the status already sent stands.
            var (outcome, status) = result ?? new CallResult(
                OutcomeFailed,
                context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError);

            LogBackendCall(
                _logger,
                LevelOf(outcome),
                surface,
                opName,
                targetName,
                status,
                (long)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds),
                outcome);
        }
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
    /// <remarks>
    /// The checks run in a fixed order: an expired deadline, a backend response already relayed in full,
    /// a caller that left, a timeout YARP or the handler reported, a backend response already taken over,
    /// a backend that gave no usable response, and last every other failure, which is the facade's own
    /// and is aborted with no response.
    /// </remarks>
    /// <returns>The logged outcome and status.</returns>
    private async Task<CallResult> HandleFailureAsync(
        HttpContext context,
        Surface surface,
        string opName,
        TimeSpan timeout,
        ForwarderError error,
        CancellationTokenSource deadline,
        long start,
        BackendCallState callState)
    {
        var response = context.Response;
        var exception = context.GetForwarderErrorFeature()?.Exception;

        // Once YARP copies the backend's body, the backend's status line and headers are the response's,
        // even before the first flush has sent them; a response relayed in full may not have been flushed.
        var responseRelayed = callState.ResponseRelayed;
        var responseTaken = response.HasStarted || IsResponseBodyError(error) || responseRelayed;

        // YARP reports an expired deadline token as RequestCanceled with 502, so the timeout is read from
        // the timer-only source, never from YARP's status.
        if (deadline.IsCancellationRequested)
        {
            return await AnswerAsync(context, surface, opName, responseTaken, isTimeout: true).ConfigureAwait(false);
        }

        // YARP checks the upload only after it has copied the backend's whole response, so a request-body
        // error it reports then, a backend that stopped reading or a caller that stopped sending, leaves
        // the caller with the backend's complete answer, which stands as it was sent.
        if (responseRelayed && IsRequestBodyError(error))
        {
            return new CallResult(OutcomeForwarded, response.StatusCode);
        }

        // The caller is gone or broke its own request: nobody is left to answer, and the status YARP set
        // for this case is a placeholder of its own, never one the backend sent.
        if (IsCallerAbort(context, error, exception))
        {
            return new CallResult(OutcomeAborted, response.HasStarted ? response.StatusCode : NoResponseStatus);
        }

        // A client-side copy error the caller did not cause, such as a buffered SOAP body whose temporary
        // storage is full, takes the checks below like any other error: the timeout it carries, a response
        // already taken over, a backend that ended the upload, or else the facade's own failure.
        if (error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout
            || HasTimeoutInChain(exception)
            || Stopwatch.GetElapsedTime(start) >= timeout)
        {
            return await AnswerAsync(context, surface, opName, responseTaken, isTimeout: true).ConfigureAwait(false);
        }

        // The backend's status line and headers are already the response's when the backend fails the call
        // afterwards, for example by dropping the connection during its response body: they are never
        // replaced, so the connection is aborted.
        if (responseTaken)
        {
            return Fail(context, responseTaken: true);
        }

        // The backend gave no response whose status line and headers the facade could take over, so the
        // caller gets the facade's unavailable answer, whether or not the request had a body and however
        // far its upload got. Request covers every failure before the response head: a refused, unresolved
        // or failed connect, and a backend that was reached and then closed or reset the connection, or
        // answered with bytes that are no valid status line or header section. RequestBodyDestination is a
        // backend that dropped the connection during the upload without an answer the HTTP stack could
        // read. ResponseHeaders is a head the facade cannot relay, such as two different Content-Length
        // values. An abandoned upload is one the backend ended while it was being sent, by failing a write
        // of it, sending something or closing or resetting the connection, that brought no answer the
        // facade could take over.
        if (error is ForwarderError.Request or ForwarderError.RequestBodyDestination or ForwarderError.ResponseHeaders
            || callState.UploadAbandoned)
        {
            return await AnswerAsync(context, surface, opName, responseTaken: false, isTimeout: false).ConfigureAwait(false);
        }

        // The facade failed the call itself, for example when YARP could not create the outgoing request or
        // could not read the caller's upload: the connection is aborted with no response.
        return Fail(context, responseTaken: false);
    }

    /// <summary>
    /// Answers a backend that gave no usable response, or an expired deadline, with the facade's failure
    /// response.
    /// </summary>
    /// <returns>The outcome and the status written, or the backend's status when the response was aborted.</returns>
    private async Task<CallResult> AnswerAsync(HttpContext context, Surface surface, string opName, bool responseTaken, bool isTimeout)
    {
        var outcome = isTimeout ? OutcomeTimeout : OutcomeUnavailable;
        var response = context.Response;

        // The backend's status line and headers are already the response's: a facade 502 or 504 would
        // replace them, so the response is aborted and never re-headed.
        if (responseTaken)
        {
            context.Abort();
            return new CallResult(outcome, response.StatusCode);
        }

        // Drops the empty-bodied 502/504 and any headers YARP set on its error path.
        response.Clear();

        // The write gets a budget of its own, never the expired deadline's token, which would cancel the
        // 504 before it is written. A caller that leaves or stops reading gets nothing more.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(FailureWriteBudget);
        try
        {
            if (surface == Surface.Soap)
            {
                var faultString = isTimeout ? SoapFaultWriter.BackendTimedOut : SoapFaultWriter.BackendUnavailable;
                await _faultWriter.WriteAsync(response, faultString, budget.Token).ConfigureAwait(false);
            }
            else
            {
                await WriteProblemAsync(response, isTimeout, opName, budget.Token).ConfigureAwait(false);
            }
        }
        catch (Exception writeFailure) when (writeFailure is OperationCanceledException or IOException)
        {
            // The caller disconnected, or the budget ran out while the caller was not reading: the
            // connection is ended rather than left holding the response.
            if (!context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
            }
        }

        return new CallResult(outcome, response.StatusCode);
    }

    /// <summary>
    /// Ends a call that failed for a reason the facade has no failure response for by aborting the
    /// connection and writing nothing: the facade's only failure responses are the unavailable and
    /// timed-out answers, and a backend status line already taken over is never replaced.
    /// </summary>
    /// <param name="context">The call's context.</param>
    /// <param name="responseTaken">Whether the backend's status line and headers are already the response's.</param>
    /// <returns>Outcome <c>failed</c> with the backend's taken-over status, or 0 when no response was sent.</returns>
    private static CallResult Fail(HttpContext context, bool responseTaken)
    {
        context.Abort();
        return new CallResult(OutcomeFailed, responseTaken ? context.Response.StatusCode : NoResponseStatus);
    }

    // YARP reports these only after the backend's status line and headers were copied to the response.
    private static bool IsResponseBodyError(ForwarderError error) => error is ForwarderError.ResponseBodyDestination
        or ForwarderError.ResponseBodyCanceled
        or ForwarderError.ResponseBodyClient
        or ForwarderError.UpgradeResponseDestination
        or ForwarderError.UpgradeResponseCanceled
        or ForwarderError.UpgradeResponseClient;

    private static bool IsRequestBodyError(ForwarderError error) => error is ForwarderError.RequestBodyClient
        or ForwarderError.RequestBodyDestination
        or ForwarderError.RequestBodyCanceled;

    /// <summary>
    /// Tells whether the call ended because the caller left or broke its own request; the deadline has
    /// already been ruled out.
    /// </summary>
    /// <remarks>
    /// Only Kestrel raises <see cref="BadHttpRequestException"/> (a truncated or malformed upload),
    /// <see cref="ConnectionResetException"/> and <see cref="ConnectionAbortedException"/>, and only for the
    /// caller's connection, so any of them in the error's chain settles it. Otherwise a cancellation or a
    /// client-side copy error counts when the caller's connection is gone or the copy was cancelled, and a
    /// failed copy of the backend's status line and headers (ResponseHeaders) counts only when both hold:
    /// the caller left while they were being sent to it, which cancels that copy. A cancelled
    /// <see cref="HttpContext.RequestAborted"/> alone is not enough: YARP aborts the connection itself
    /// after a backend failure during the response body (ResponseBodyDestination), which is not among
    /// these errors.
    /// </remarks>
    private static bool IsCallerAbort(HttpContext context, ForwarderError error, Exception? exception)
    {
        if (HasInChain(exception, static current => current is BadHttpRequestException or ConnectionResetException or ConnectionAbortedException))
        {
            return true;
        }

        if (error == ForwarderError.ResponseHeaders)
        {
            return context.RequestAborted.IsCancellationRequested
                && HasInChain(exception, static current => current is OperationCanceledException);
        }

        var cancelledOrClientSide = error is ForwarderError.RequestCanceled
            or ForwarderError.RequestBodyCanceled
            or ForwarderError.ResponseBodyCanceled
            or ForwarderError.RequestBodyClient
            or ForwarderError.ResponseBodyClient
            or ForwarderError.UpgradeRequestCanceled
            or ForwarderError.UpgradeResponseCanceled
            or ForwarderError.UpgradeRequestClient
            or ForwarderError.UpgradeResponseClient;

        return cancelledOrClientSide
            && (context.RequestAborted.IsCancellationRequested
                || HasInChain(exception, static current => current is OperationCanceledException));
    }

    // Walks inner exceptions, all of an AggregateException's included, visiting each exception once.
    private static bool HasInChain(Exception? exception, Func<Exception, bool> match)
    {
        if (exception is null)
        {
            return false;
        }

        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            if (match(current))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }

    private static LogLevel LevelOf(string outcome) =>
        outcome is OutcomeForwarded or OutcomeAborted ? LogLevel.Information : LogLevel.Warning;

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
    // so a caller's own trace headers pass through unchanged. The connect callback opens the socket as the
    // handler does by default, over a stream that lets a backend's answer during an upload through. Given
    // a caller's request line, the invoker serves one call only, because the line belongs to that call's
    // request: every connection it opens writes that line in place of the first request line the handler
    // writes, and nothing else changes.
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
            ConnectCallback = BackendConnectionStream.ConnectAsync,

            // Latin-1 maps each header byte to one char and back, matching the selectors Kestrel uses
            // in Program.cs, so non-ASCII header bytes pass unchanged in both directions instead of
            // failing the call (the handler's default accepts only ASCII request header values).
            RequestHeaderEncodingSelector = static (_, _) => System.Text.Encoding.Latin1,
            ResponseHeaderEncodingSelector = static (_, _) => System.Text.Encoding.Latin1,

            // The facade imposes no response-head ceiling of its own: the backend's status line and
            // headers are relayed whatever their size, instead of the handler's 64 KiB default failing a
            // valid response. The value is in KiB, and the handler caps its byte budget at int.MaxValue.
            MaxResponseHeadersLength = int.MaxValue,

            // A caller's own "Expect: 100-continue" holds the body back until the backend's 100 Continue
            // or the end of this wait. It is the smaller of the handler's one-second default and half the
            // target's deadline, so the wait can never consume the call's deadline before the body is sent.
            Expect100ContinueTimeout = connectTimeout / 2 < DefaultExpectContinueWait ? connectTimeout / 2 : DefaultExpectContinueWait,
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

    // The one completion event of every call; its level comes from LevelOf, so a filter at Warning still
    // shows every unavailable, timed-out and failed call.
    [LoggerMessage(
        EventId = 1001,
        EventName = "BackendCall",
        Message = "Backend call {Surface} {Operation} -> {Target}: status {Status}, {DurationMs} ms, outcome {Outcome}")]
    private static partial void LogBackendCall(
        ILogger logger,
        LogLevel level,
        Surface surface,
        string operation,
        string target,
        int status,
        long durationMs,
        string outcome);

    /// <summary>How a call ended, as its line reports it.</summary>
    /// <param name="Outcome"><c>forwarded</c>, <c>unavailable</c>, <c>timeout</c>, <c>aborted</c> or <c>failed</c>.</param>
    /// <param name="Status">The status sent or taken over from the backend; 0 when no response was sent.</param>
    private readonly record struct CallResult(string Outcome, int Status);

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
/// <see cref="IOException"/> instead, so that request fails once and the forwarder gives the caller its
/// unavailable answer. A request without content keeps the handler's resend, and a connection the
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

// BackendCallState, BackendConnectionStream and CancellableRequestBody live in this file because each
// serves the call ForwardAsync makes: ForwardAsync begins every call's BackendCallState, CreateInvoker
// opens every backend connection as a BackendConnectionStream, and ForwardAsync installs
// CancellableRequestBody, as SoapEndpoints does before its inspection.
/// <summary>
/// What one forwarded call has reached so far, shared by the parts of the forwarding path that see
/// the same call from different sides: <see cref="BackendForwarder"/>, which begins and ends it and
/// reads it to decide the outcome, <see cref="CancellableRequestBody"/>, which reads the caller's
/// upload, <see cref="BackendConnectionStream"/>, which writes it to the backend and watches the backend
/// while it is in progress, and <see cref="PassThroughTransformer"/>, which sees the backend's response
/// go through.
/// </summary>
/// <remarks>
/// <para>
/// The state travels with the call's execution context, so the backend connection, which serves one
/// call after another and knows nothing of them, finds the call it is writing for. Every read and write
/// of a forwarded body runs in that context, because YARP copies both inside the forwarder's call.
/// Outside a forwarded call, <see cref="Current"/> is null and nothing here applies.
/// </para>
/// <para>
/// A backend may answer an upload before it has read all of it, for example with 413 for its own
/// body-size limit, and keep the connection open or close it. Without <c>Expect: 100-continue</c> the
/// HTTP stack reads no response until it has sent the whole body, so a caller that pauses its upload
/// would hold the answer back until the deadline, and a write to a connection the backend closed would
/// fail the call and discard the answer. The upload therefore ends as soon as the backend shows it
/// takes no more of it: its connection fails a write of the upload, or, while the upload is in
/// progress, it sends anything but an interim (1xx) response, or closes or resets the connection.
/// </para>
/// <para>
/// The upload is in progress from the first read of the caller's body during the call, once the
/// connection the call writes on has registered itself here with its first write, until the caller's
/// body has been read to its end, the upload has ended or the call is over. For that time the
/// connection watches its socket (<see cref="BackendConnectionStream"/>). It watches the handler's reads
/// from its registration on, because the request head goes out before the first read of the caller's
/// body, and on a pooled connection the handler's read ahead can take a reply the backend sent at once:
/// an interim response before the upload starts is passed over, while any other reply is kept and ends
/// the upload as soon as it starts. Both watches end with the upload, so nothing they leave behind fires
/// for this call later, or for another call on the same pooled connection.
/// </para>
/// <para>
/// When the upload ends, the connection drops every later write of this call, and a read waiting on the
/// caller's body returns at once. A chunked body ends there, so the caller is read no further. A body
/// whose length the caller declared must still reach that length, because both the forwarder and the
/// HTTP stack fail a body shorter than its declared length and discard the backend's answer with it;
/// <see cref="CancellableRequestBody"/> completes it with padding bytes, none of which is sent. The HTTP
/// stack then returns the backend's answer exactly as the backend sent it, or, when the backend closed
/// or reset the connection without one, fails the call, which the forwarder answers as an unavailable
/// backend at once.
/// </para>
/// </remarks>
internal sealed class BackendCallState
{
    private static readonly AsyncLocal<BackendCallState?> CurrentCall = new();

    private readonly Lock _gate = new();

    // Whether the request body is framed by its declared Content-Length, which the HTTP stack sends in
    // full or fails the call; otherwise it is chunked, or there is none.
    private readonly bool _lengthDelimitedBody;

    // The caller's body reader while an upload read is waiting on it; cancelled when the upload ends.
    private PipeReader? _waitingUploadReader;

    // The backend connection this call writes on, registered by the connection's first write for it.
    private BackendConnectionStream? _connection;

    // Whether _connection watches the handler's reads for this call, and whether it also watches its
    // socket, which it does only while the upload is in progress.
    private bool _watchingReplies;
    private bool _watchingSocket;

    // Whether the backend replied with something other than an interim response before the upload began.
    private bool _repliedBeforeUpload;

    private bool _uploadStarted;
    private bool _uploadFinished;
    private bool _uploadAbandoned;
    private bool _ended;
    private int _responseRelayed;

    private BackendCallState(bool lengthDelimitedBody) => _lengthDelimitedBody = lengthDelimitedBody;

    /// <summary>The forwarded call the current execution context belongs to; null outside one.</summary>
    public static BackendCallState? Current => CurrentCall.Value;

    /// <summary>
    /// Whether the upload ended before the caller's body did, so that the rest of it was dropped: the
    /// backend's connection failed a write of it, or the backend sent something other than an interim
    /// response, or closed or reset the connection, while it was in progress.
    /// </summary>
    public bool UploadAbandoned
    {
        get
        {
            lock (_gate)
            {
                return _uploadAbandoned;
            }
        }
    }

    /// <summary>
    /// Whether the caller's body ends at its next read: the upload was abandoned and is not framed by a
    /// declared length.
    /// </summary>
    public bool CallerBodyEnded
    {
        get
        {
            lock (_gate)
            {
                return _uploadAbandoned && !_lengthDelimitedBody;
            }
        }
    }

    /// <summary>
    /// Whether the rest of the caller's body, up to its declared length, is completed with padding bytes
    /// that are never sent: the upload was abandoned and is framed by a declared length.
    /// </summary>
    public bool PadsDeclaredBody
    {
        get
        {
            lock (_gate)
            {
                return _uploadAbandoned && _lengthDelimitedBody;
            }
        }
    }

    /// <summary>
    /// Whether the backend's whole response, status line, headers and body, has been copied to the
    /// caller's response.
    /// </summary>
    public bool ResponseRelayed => Volatile.Read(ref _responseRelayed) != 0;

    /// <summary>
    /// Starts the state of a new forwarded call and makes it <see cref="Current"/> for the rest of the
    /// calling method and everything it awaits.
    /// </summary>
    /// <param name="lengthDelimitedBody">
    /// Whether the incoming request carries a body framed by its declared <c>Content-Length</c>, as the
    /// HTTP stack then sends it to the backend.
    /// </param>
    /// <returns>The call's state; end it with <see cref="End"/> when the call is over.</returns>
    public static BackendCallState Begin(bool lengthDelimitedBody)
    {
        var state = new BackendCallState(lengthDelimitedBody);
        CurrentCall.Value = state;
        return state;
    }

    /// <summary>
    /// Ends the call: no later write failure or backend reply can abandon its upload, the backend is no
    /// longer watched for it, and no reader is cancelled for it.
    /// </summary>
    public void End()
    {
        lock (_gate)
        {
            _ended = true;
            _waitingUploadReader = null;
            StopWatchingLocked();
        }

        CurrentCall.Value = null;
    }

    /// <summary>Records that the backend's whole response has been copied to the caller's response.</summary>
    public void MarkResponseRelayed() => Volatile.Write(ref _responseRelayed, 1);

    /// <summary>
    /// Registers the backend connection the call writes on. The connection calls it for every write it
    /// makes during the call, so it is known by the time the request head goes out; from then on it
    /// watches the handler's reads for the call, and its socket too while the upload is in progress.
    /// </summary>
    /// <param name="connection">The connection writing for this call.</param>
    public void RegisterConnection(BackendConnectionStream connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Every write after the first finds its connection registered without taking the lock.
        if (ReferenceEquals(Volatile.Read(ref _connection), connection))
        {
            return;
        }

        lock (_gate)
        {
            if (_ended || ReferenceEquals(_connection, connection))
            {
                return;
            }

            // A call writes on one connection; should the handler move it to another, only that one is
            // watched from then on, and a reply seen on the earlier one is no answer to this call.
            StopWatchingLocked();
            _repliedBeforeUpload = false;
            Volatile.Write(ref _connection, connection);
            if (!_uploadFinished && !_uploadAbandoned)
            {
                _watchingReplies = true;
                connection.WatchReplies(this);
                WatchSocketIfUploadingLocked();
            }
        }
    }

    /// <summary>
    /// Registers a read of the caller's upload that is about to wait on <paramref name="reader"/>. The
    /// first such read of the call starts the upload, which ends there if the backend has already replied.
    /// </summary>
    /// <param name="reader">The server's request-body reader the read waits on.</param>
    /// <returns>
    /// False when the caller's body has ended (<see cref="CallerBodyEnded"/>), so the read must end the
    /// body instead of reading; otherwise true, and the read must call <see cref="EndUploadRead"/> when it
    /// returns.
    /// </returns>
    public bool TryBeginUploadRead(PipeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        lock (_gate)
        {
            if (!_uploadStarted)
            {
                _uploadStarted = true;
                if (_repliedBeforeUpload && _watchingReplies)
                {
                    AbandonLocked(_connection!);
                }
                else
                {
                    WatchSocketIfUploadingLocked();
                }
            }

            if (_uploadAbandoned && !_lengthDelimitedBody)
            {
                return false;
            }

            if (!_ended)
            {
                _waitingUploadReader = reader;
            }

            return true;
        }
    }

    /// <summary>Unregisters the read registered by <see cref="TryBeginUploadRead"/>.</summary>
    public void EndUploadRead()
    {
        lock (_gate)
        {
            _waitingUploadReader = null;
        }
    }

    /// <summary>
    /// Records that the caller's body has been read to its end: the upload is no longer in progress, so
    /// the backend is no longer watched for it, and its answer is read as the HTTP stack reads any other.
    /// </summary>
    public void MarkUploadFinished()
    {
        lock (_gate)
        {
            _uploadFinished = true;
            StopWatchingLocked();
        }
    }

    /// <summary>
    /// Abandons the upload after <paramref name="connection"/> failed a write of it: the rest of it is
    /// dropped, and a read waiting on the caller's body returns at once.
    /// </summary>
    /// <param name="connection">The connection whose write failed.</param>
    /// <returns>
    /// True when the upload had started and the call is still running, so the failure is the end of
    /// the upload; false when the failure must surface as it is, because it happened before the upload
    /// (the request head) or after the call.
    /// </returns>
    public bool TryAbandonUpload(BackendConnectionStream connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            if (_ended || !_uploadStarted)
            {
                return false;
            }

            AbandonLocked(connection);
            return true;
        }
    }

    /// <summary>
    /// Takes in that the backend sent something other than an interim response on
    /// <paramref name="connection"/>, or closed or reset it. While the upload is in progress, the upload
    /// is abandoned: the rest of it is dropped, and a read waiting on the caller's body returns at once.
    /// Before the upload has started, the reply is kept, and the upload is abandoned as it starts. Does
    /// nothing once the upload has finished or ended, once the call is over, or for a connection the call
    /// no longer writes on.
    /// </summary>
    /// <param name="connection">The connection whose watch saw the backend reply.</param>
    public void AbandonUploadOnBackendReply(BackendConnectionStream connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            // The connection's watch of replies lasts exactly until the upload finishes or ends.
            if (!_watchingReplies || !ReferenceEquals(_connection, connection))
            {
                return;
            }

            if (_uploadStarted)
            {
                AbandonLocked(connection);
            }
            else
            {
                _repliedBeforeUpload = true;
            }
        }
    }

    // Marks the upload abandoned once. The connection drops every later write of this call before the lock
    // is released, so no byte the caller's body yields from then on, padding included, is sent. A read
    // waiting on the caller's body returns as cancelled, which CancellableRequestBody turns into the end
    // of a chunked body or the padding of a body of declared length; the reader stays valid for the
    // server's drain of whatever the caller still sends. The read is returned under the lock, which End
    // takes to clear it, so no reader is cancelled once the call is over; the lock is re-entrant, so a
    // read that resumed on this thread could still read the state.
    private void AbandonLocked(BackendConnectionStream connection)
    {
        if (_uploadAbandoned)
        {
            return;
        }

        _uploadAbandoned = true;
        connection.DropWritesOf(this);
        StopWatchingLocked();
        _waitingUploadReader?.CancelPendingRead();
    }

    // The socket is watched only while the upload is in progress: once it has started, on a connection
    // whose replies are watched, which they are from its registration until the upload finishes or ends.
    private void WatchSocketIfUploadingLocked()
    {
        if (!_watchingReplies || _watchingSocket || !_uploadStarted)
        {
            return;
        }

        _watchingSocket = true;
        _connection!.WatchSocket(this);
    }

    private void StopWatchingLocked()
    {
        if (!_watchingReplies)
        {
            return;
        }

        _watchingReplies = false;
        _watchingSocket = false;
        _connection!.StopWatching(this);
    }
}

/// <summary>
/// The connection to a backend, as each target's <see cref="SocketsHttpHandler"/> opens it: the
/// handler's own socket and stream, except that a backend which answers, closes or resets the
/// connection during an upload ends the upload instead of holding it up or failing the call, so that
/// its answer reaches the caller.
/// </summary>
/// <remarks>
/// <para>
/// A backend may answer before it has read the whole request body, for example 413 for its own
/// body-size limit, at once for a declared length over it or once a chunked upload crosses it, and then
/// keep the connection open or close it. The HTTP stack reads that answer only once the body has been
/// sent: while the caller pauses its upload the answer waits for the deadline, and a write to a closed
/// connection fails the call, so the caller would get the facade's timed-out or unavailable answer in
/// place of the backend's own. No <c>Expect: 100-continue</c> is added to make the backend answer first,
/// because the backend may not support the expectation.
/// </para>
/// <para>
/// The end of the upload is therefore taken from the backend's side of the connection, in three ways,
/// and recorded on the call's <see cref="BackendCallState"/>:
/// <list type="bullet">
/// <item><description>
/// A write of the call that fails because the backend reset or closed the connection
/// (<see cref="SocketError.ConnectionReset"/>, <see cref="SocketError.Shutdown"/> for a broken pipe, or
/// <see cref="SocketError.ConnectionAborted"/>) once the upload has started.
/// </description></item>
/// <item><description>
/// While the upload is in progress, a zero-byte receive on the socket, which completes when data, the
/// end of the stream or a reset arrives without consuming anything, followed by a peek at the first
/// bytes that arrived.
/// </description></item>
/// <item><description>
/// From the call's first write until its upload finishes or ends, a read the handler makes that returns
/// bytes or the end of the stream: its read for the final status after 100 Continue, or the read it
/// starts on a pooled connection before reusing it, which takes whatever arrives ahead of the zero-byte
/// receive, even a reply to the request head that arrives before the upload has started; such a reply
/// ends the upload as soon as it starts.
/// </description></item>
/// </list>
/// An interim (1xx) response is no end: the backend still expects the rest of the body, for example
/// after a 100 Continue that arrives once the handler has stopped waiting for it, and the handler reads
/// past it. The watch on the handler's reads goes on past one; the socket watch stops at one, because the
/// interim response stays in the socket, unread, until the upload is over. Neither watch changes or
/// consumes a byte. Each watch is started and stopped by its call, so neither fires for another call on
/// the same connection.
/// </para>
/// <para>
/// From then on every write of that call completes without sending anything. A chunked body ends at the
/// caller's next read; a body of declared length is completed with padding bytes that are dropped here,
/// so the HTTP stack finishes the upload it framed. The HTTP stack then returns the backend's answer
/// exactly as the backend sent it, or, when the backend sent none, fails the call when it reads the
/// closed or reset connection. Every other failure, and any failure before the upload has started,
/// such as the request head on a pooled connection the backend has dropped, or after the handler has
/// disposed the connection to cancel the call, surfaces unchanged.
/// </para>
/// <para>
/// A connection whose upload was ended is never used for another call: once the call is over, every read
/// returns the end of the stream, which the handler takes as a connection it must not reuse.
/// </para>
/// <para>
/// Everything else is the handler's default: the socket is created, connected and disposed on failure
/// as SocketsHttpHandler does without a connect callback, so connect failures, their
/// <see cref="HttpRequestError"/> and the connect timeout are unchanged, and bytes are read and written
/// without buffering.
/// </para>
/// </remarks>
internal sealed class BackendConnectionStream : NetworkStream
{
    // The call whose upload this connection ended; set once.
    private BackendCallState? _abandonedBy;

    // The watch of the backend for the call writing on this connection, until its upload finishes or
    // ends; null when there is none.
    private UploadWatch? _watch;

    private int _disposed;

    private BackendConnectionStream(Socket socket)
        : base(socket, ownsSocket: true)
    {
    }

    /// <summary>
    /// Opens a connection to the backend, for <see cref="SocketsHttpHandler.ConnectCallback"/>: a TCP
    /// socket with Nagle's algorithm disabled, connected to the endpoint the handler names.
    /// </summary>
    /// <param name="context">The endpoint and the request that needs the connection.</param>
    /// <param name="cancellationToken">Cancelled by the handler's connect timeout or by the call.</param>
    /// <returns>The connected stream, which owns the socket.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="SocketException">The connection was refused, the name was not resolved or the connect failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new BackendConnectionStream(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (IsEndedForAnotherCall())
        {
            return 0;
        }

        var read = base.Read(buffer);
        ObserveRead(buffer, read);
        return read;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        byte value = 0;
        return Read(new Span<byte>(ref value)) == 0 ? -1 : value;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (IsEndedForAnotherCall())
        {
            return ValueTask.FromResult(0);
        }

        // A read that completed at once, as reads of data already received do, is observed without an
        // await; any other is observed when it completes, which may be during an upload that started
        // after the read did.
        var read = base.ReadAsync(buffer, cancellationToken);
        if (!read.IsCompletedSuccessfully)
        {
            return ObserveReadAsync(read, buffer);
        }

        var count = read.Result;
        ObserveRead(buffer.Span, count);
        return ValueTask.FromResult(count);
    }

    /// <inheritdoc />
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);

    /// <inheritdoc />
    public override int EndRead(IAsyncResult asyncResult) => TaskToAsyncResult.End<int>(asyncResult);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registers the connection on the current call; completes without sending once the call's upload has
    /// ended.
    /// </remarks>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var call = BackendCallState.Current;
        if (Volatile.Read(ref _abandonedBy) is { } abandonedBy)
        {
            if (ReferenceEquals(abandonedBy, call))
            {
                return;
            }

            throw ClosedByBackend();
        }

        call?.RegisterConnection(this);
        try
        {
            base.Write(buffer);
        }
        catch (IOException failure) when (TryEndUpload(failure))
        {
            // The backend closed the connection during the upload; its answer, if any, is read next.
        }
    }

    /// <inheritdoc />
    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registers the connection on the current call; completes without sending once the call's upload has
    /// ended.
    /// </remarks>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var call = BackendCallState.Current;
        if (Volatile.Read(ref _abandonedBy) is { } abandonedBy)
        {
            return ReferenceEquals(abandonedBy, call)
                ? ValueTask.CompletedTask
                : ValueTask.FromException(ClosedByBackend());
        }

        call?.RegisterConnection(this);
        ValueTask write;
        try
        {
            write = base.WriteAsync(buffer, cancellationToken);
        }
        catch (IOException failure) when (TryEndUpload(failure))
        {
            return ValueTask.CompletedTask;
        }

        // A write that completed at once, as most do, is returned as it is.
        return write.IsCompletedSuccessfully ? write : AwaitWriteAsync(write);
    }

    /// <inheritdoc />
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);

    /// <inheritdoc />
    public override void EndWrite(IAsyncResult asyncResult) => TaskToAsyncResult.End(asyncResult);

    /// <summary>
    /// Starts watching the handler's reads for <paramref name="call"/>, which has just registered this
    /// connection. Called by the call under its lock; the call stops the watch with
    /// <see cref="StopWatching"/> when its upload finishes or ends, or when the call is over.
    /// </summary>
    /// <param name="call">The call writing on this connection.</param>
    internal void WatchReplies(BackendCallState call) => Interlocked.Exchange(ref _watch, new UploadWatch(call))?.Stop();

    /// <summary>
    /// Starts watching the socket as well for <paramref name="call"/>, whose upload has just come to be in
    /// progress; does nothing unless the call's watch is the connection's. Called by the call under its
    /// lock.
    /// </summary>
    /// <param name="call">The call whose upload is in progress.</param>
    internal void WatchSocket(BackendCallState call)
    {
        if (Volatile.Read(ref _watch) is not { } watch || !ReferenceEquals(watch.Call, call))
        {
            return;
        }

        // The socket watch starts on the thread pool, so that a reply already waiting is reported outside
        // the call's lock, and it carries no execution context, because it knows its call.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => _ = state.Connection.WatchSocketAsync(state.Watch),
            (Connection: this, Watch: watch),
            preferLocal: false);
    }

    /// <summary>
    /// Stops the watch started for <paramref name="call"/>, if it is still the connection's; its pending
    /// socket receive is cancelled and nothing it observes afterwards reaches the call.
    /// </summary>
    /// <param name="call">The call whose upload has finished or ended, or that is over.</param>
    internal void StopWatching(BackendCallState call)
    {
        if (Volatile.Read(ref _watch) is { } watch
            && ReferenceEquals(watch.Call, call)
            && ReferenceEquals(Interlocked.CompareExchange(ref _watch, null, watch), watch))
        {
            watch.Stop();
        }
    }

    /// <summary>
    /// Makes every later write of <paramref name="call"/> complete without sending anything, because its
    /// upload has ended; the first call to end an upload on the connection is the only one.
    /// </summary>
    /// <param name="call">The call whose upload has ended.</param>
    internal void DropWritesOf(BackendCallState call) => Interlocked.CompareExchange(ref _abandonedBy, call, null);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // A write failing from here on was failed by the handler's own cancellation, never by the backend.
        Volatile.Write(ref _disposed, 1);
        base.Dispose(disposing);
    }

    // Observes a read the handler made, once it has completed.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ObserveReadAsync(ValueTask<int> read, Memory<byte> buffer)
    {
        var count = await read.ConfigureAwait(false);
        ObserveRead(buffer.Span, count);
        return count;
    }

    // While a call's replies are watched, the first read of the handler that returns anything but an
    // interim response, or the end of the stream, is reported to the call, which ends its upload or, before
    // the upload has started, ends it as it starts. A zero-byte read only waits for data and reports nothing.
    private void ObserveRead(ReadOnlySpan<byte> buffer, int count)
    {
        if (Volatile.Read(ref _watch) is { } watch && watch.TakesReply(buffer, count))
        {
            watch.Call.AbandonUploadOnBackendReply(this);
        }
    }

    // Waits, without consuming anything, for the backend to send data, end the stream or reset the
    // connection, and ends the upload unless what arrived is an interim response. A reset or any other
    // socket error ends it as the end of the stream does; a stopped watch, a disposed connection or a
    // local close ends the watch and leaves the upload as it is.
    private async Task WatchSocketAsync(UploadWatch watch)
    {
        try
        {
            await Socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, watch.Token).ConfigureAwait(false);

            // The peek returns at once with the bytes waiting, or 0 at the end of the stream.
            var head = new byte[InterimResponseScanner.StatusPrefixLength];
            var peeked = await Socket.ReceiveAsync(head, SocketFlags.Peek, watch.Token).ConfigureAwait(false);
            if (peeked > 0 && InterimResponseScanner.StartsInterim(head.AsSpan(0, peeked)))
            {
                return;
            }
        }
        catch (SocketException failure) when (failure.SocketErrorCode != SocketError.OperationAborted)
        {
            // A reset, or another error that ends the connection: the backend takes no more of the upload.
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped with the upload, or the connection was disposed or closed by the handler.
            return;
        }

        if (!watch.Token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            watch.Call.AbandonUploadOnBackendReply(this);
        }
    }

    private async ValueTask AwaitWriteAsync(ValueTask write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (IOException failure) when (TryEndUpload(failure))
        {
            // The backend closed the connection during the upload; its answer, if any, is read next.
        }
    }

    // Once the upload has been ended, a write of that call sends nothing. A write of any other call cannot
    // happen, because the handler never reuses the connection, and fails as the closed connection would.
    private static IOException ClosedByBackend() => new(
        "The backend closed this connection during an earlier upload.",
        new SocketException((int)SocketError.ConnectionReset));

    // Once the upload has been ended, a read for anything but the backend's answer to that call, the
    // handler's checks of an idle connection included, finds the end of the stream.
    private bool IsEndedForAnotherCall() =>
        Volatile.Read(ref _abandonedBy) is { } abandonedBy && !ReferenceEquals(abandonedBy, BackendCallState.Current);

    // Takes a write failure as the end of the upload when the backend reset or closed the connection
    // while the current call's upload was being sent.
    private bool TryEndUpload(IOException failure)
    {
        if (Volatile.Read(ref _disposed) != 0
            || failure.InnerException is not SocketException
            {
                SocketErrorCode: SocketError.ConnectionReset or SocketError.Shutdown or SocketError.ConnectionAborted,
            })
        {
            return false;
        }

        // The call drops this connection's later writes for it when it abandons the upload.
        return BackendCallState.Current is { } call && call.TryAbandonUpload(this);
    }

    /// <summary>
    /// One call's watch of the backend on this connection: of the handler's reads from the call's first
    /// write until its upload finishes or ends, and of the socket while the upload is in progress.
    /// </summary>
    private sealed class UploadWatch
    {
        private readonly CancellationTokenSource _stop = new();

        // Fed only by the handler's reads, which a connection serves one at a time, until a reply is taken.
        private InterimResponseScanner _scanner;
        private bool _replyTaken;

        /// <summary>Creates the watch of <paramref name="call"/>'s upload.</summary>
        /// <param name="call">The call whose upload is watched.</param>
        public UploadWatch(BackendCallState call)
        {
            Call = call;
            Token = _stop.Token;
        }

        /// <summary>The call whose upload is watched.</summary>
        public BackendCallState Call { get; }

        /// <summary>Cancelled when the watch stops; taken at creation, so it stays usable after <see cref="Stop"/>.</summary>
        public CancellationToken Token { get; }

        /// <summary>
        /// Tells whether a completed handler read is the first to hold anything but interim responses, or
        /// to find the end of the stream; a zero-byte read, which only waits for data, never is.
        /// </summary>
        /// <param name="buffer">The buffer the read was given.</param>
        /// <param name="count">The number of bytes the read returned.</param>
        public bool TakesReply(ReadOnlySpan<byte> buffer, int count)
        {
            if (_replyTaken || (count == 0 ? buffer.IsEmpty : !_scanner.SeesReply(buffer[..count])))
            {
                return false;
            }

            _replyTaken = true;
            return true;
        }

        /// <summary>Cancels the socket watch's pending receive and releases the token source; called once.</summary>
        public void Stop()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Tells interim (1xx) responses apart from anything else a backend sends, from the bytes in the order
    /// they arrive, however a read splits them: a response that begins <c>HTTP/1.</c>, a digit, a space
    /// and <c>1</c> is interim, and its head is skipped up to the blank line that ends it.
    /// </summary>
    private struct InterimResponseScanner
    {
        /// <summary>The length of <c>HTTP/1.x 1</c>, which an interim response's status line begins with.</summary>
        public const int StatusPrefixLength = 10;

        // The digit after "HTTP/1." may be any digit.
        private const int VersionDigitIndex = 7;

        private int _prefixMatched;
        private bool _inInterimHead;
        private int _headEndMatched;

        private static ReadOnlySpan<byte> StatusPrefix => "HTTP/1.x 1"u8;

        private static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

        /// <summary>Tells whether <paramref name="bytes"/> begins with a complete interim status-line prefix.</summary>
        public static bool StartsInterim(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < StatusPrefixLength)
            {
                return false;
            }

            for (var index = 0; index < StatusPrefixLength; index++)
            {
                if (!MatchesPrefix(index, bytes[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Feeds the next bytes the backend sent and tells whether any of them belongs to something other
        /// than an interim response: a final status line, or bytes that are no HTTP response.
        /// </summary>
        public bool SeesReply(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                if (_inInterimHead)
                {
                    // CR is the only byte of the blank line's pattern that can restart a partial match.
                    _headEndMatched = value == HeadEnd[_headEndMatched] ? _headEndMatched + 1 : value == (byte)'\r' ? 1 : 0;
                    if (_headEndMatched == HeadEnd.Length)
                    {
                        _inInterimHead = false;
                        _headEndMatched = 0;
                        _prefixMatched = 0;
                    }

                    continue;
                }

                if (!MatchesPrefix(_prefixMatched, value))
                {
                    return true;
                }

                if (++_prefixMatched == StatusPrefixLength)
                {
                    _inInterimHead = true;
                }
            }

            return false;
        }

        private static bool MatchesPrefix(int index, byte value) =>
            index == VersionDigitIndex ? char.IsAsciiDigit((char)value) : value == StatusPrefix[index];
    }
}

/// <summary>
/// The request body as the forwarder and the SOAP inspection read it: the server's own body reader,
/// with a cancelled read ended through <see cref="PipeReader.CancelPendingRead"/> rather than through
/// the read's token.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel leaves its HTTP/1.1 request-body reader marked as reading when a read is cancelled through
/// its token. That happens whenever the deadline or a caller abort cancels YARP's copy of an upload that
/// is still arriving, and once the facade's 504 or Fault has been sent, Kestrel's automatic drain of the
/// rest of the body fails with "Reading is already in progress", logged as an Error
/// (RequestBodyDrainBodyReaderInvalidState). A read ended by <see cref="PipeReader.CancelPendingRead"/>
/// returns normally and is completed with <see cref="PipeReader.AdvanceTo(SequencePosition)"/>, so the
/// reader stays valid and the drain runs.
/// </para>
/// <para>
/// During a forwarded call, the reads also tell the call's <see cref="BackendCallState"/> where the upload
/// is: the first read starts it, and the read that hands on the caller's last bytes, or the end of the
/// body, finishes it.
/// </para>
/// <para>
/// Once the call has abandoned the upload, because the backend answered, closed or reset the connection
/// while it was in progress, or failed a write of it, the rest of the caller's body has nowhere to go. A
/// read already waiting for the caller's next bytes returns at once. A chunked body ends there
/// (<see cref="BackendCallState.CallerBodyEnded"/>), so the caller is read no further. A body of declared
/// length (<see cref="BackendCallState.PadsDeclaredBody"/>) must still reach that length, because YARP
/// and the HTTP stack each fail a body that ends short of the <c>Content-Length</c> its request head
/// announced, and the backend's answer would be discarded with it. Its remainder is therefore returned as
/// padding: zero bytes made here, never read from the caller and never sent, because the backend
/// connection drops every write of an abandoned upload. They exist only so that both byte counts reach
/// the declared length and the HTTP stack goes on to read the backend's answer. A remainder above
/// <see cref="MaxPaddedRemainder"/> is first read from the caller, every byte dropped, until it is within
/// it, so a caller declaring an outsized length cannot make the facade produce and discard padding for
/// the rest of the deadline; the deadline bounds that read. Bytes read during SOAP inspection count
/// towards the declared length like any other.
/// </para>
/// <para>
/// Everything else is the server's own behaviour: the bytes, the end of the body, Kestrel's errors for a
/// truncated or reset upload, the 100 Continue sent on the first read and the synchronous-IO policy,
/// which synchronous reads keep by going to the server's stream.
/// </para>
/// </remarks>
internal sealed class CancellableRequestBody : Stream
{
    /// <summary>
    /// The largest remainder of a body of declared length, 16 MiB, that is completed with padding once
    /// its upload has been abandoned; a larger remainder is read from the caller down to it first.
    /// </summary>
    internal const long MaxPaddedRemainder = 16 * 1024 * 1024;

    private static readonly Action<object?> CancelPendingRead = static reader => ((PipeReader)reader!).CancelPendingRead();

    private readonly PipeReader _reader;
    private readonly Stream _serverBody;

    // The caller's declared Content-Length, if any, and how many body bytes this stream has returned so
    // far, padding included: what a body of declared length still owes is their difference.
    private readonly long? _declaredLength;
    private long _returned;

    private CancellableRequestBody(PipeReader reader, Stream serverBody, long? declaredLength)
    {
        _reader = reader;
        _serverBody = serverBody;
        _declaredLength = declaredLength;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the body is a forward-only stream.</exception>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the body is a forward-only stream.</exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Puts this stream in place of the request body of <paramref name="context"/>, once per request.
    /// Call it before anything else reads, wraps or buffers the body, so that every later read of the
    /// server's body goes through it; a request that cannot have a body is left as it is.
    /// </summary>
    /// <param name="context">The incoming request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static void Install(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Features.Get<CancellableRequestBody>() is not null
            || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return;
        }

        // BodyReader is read before Body is replaced, so it is the server's own reader.
        var request = context.Request;
        var body = new CancellableRequestBody(request.BodyReader, request.Body, request.ContentLength);
        request.Body = body;
        context.Features.Set(body);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => CountReturned(_serverBody.Read(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => CountReturned(_serverBody.Read(buffer));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A zero-length <paramref name="buffer"/> waits for data and returns 0 without consuming any, as
    /// the server's stream does. A cancelled read throws <see cref="OperationCanceledException"/> after
    /// the reader has been completed. During a forwarded call whose upload has been abandoned, a chunked
    /// body ends, with 0 and without consuming anything, and a body of declared length returns padding up
    /// to that length, or the caller's bytes while the remainder is above <see cref="MaxPaddedRemainder"/>.
    /// </remarks>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Outside a forwarded call, for example during SOAP inspection, there is no call state. The first
        // read of a call starts its upload, which may end it at once, so padding is decided after it.
        var call = BackendCallState.Current;
        if (call is not null && !call.TryBeginUploadRead(_reader))
        {
            return 0;
        }

        try
        {
            if (call is not null && TryPad(call, buffer.Span, out var padded))
            {
                return padded;
            }

            while (true)
            {
                ReadResult result;
                using (cancellationToken.UnsafeRegister(CancelPendingRead, _reader))
                {
                    result = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                }

                var readable = result.Buffer;
                if (result.IsCanceled)
                {
                    _reader.AdvanceTo(readable.Start);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Returned because the call abandoned the upload: a chunked body ends here, a body of
                    // declared length is padded, and one whose remainder is too large to pad is read on.
                    if (call is { CallerBodyEnded: true })
                    {
                        return 0;
                    }

                    if (call is not null && TryPad(call, buffer.Span, out padded))
                    {
                        return padded;
                    }

                    if (call is { UploadAbandoned: true })
                    {
                        continue;
                    }

                    throw new OperationCanceledException("The request body read was canceled.");
                }

                if (!readable.IsEmpty)
                {
                    var count = (int)Math.Min(readable.Length, buffer.Length);
                    var copied = readable.Slice(0, count);
                    copied.CopyTo(buffer.Span);
                    _reader.AdvanceTo(copied.End);
                    _returned += count;

                    // The caller's last bytes are handed on: the upload is no longer in progress.
                    if (result.IsCompleted && count == readable.Length)
                    {
                        call?.MarkUploadFinished();
                    }

                    return count;
                }

                _reader.AdvanceTo(readable.End);
                if (result.IsCompleted)
                {
                    call?.MarkUploadFinished();
                    return 0;
                }
            }
        }
        finally
        {
            call?.EndUploadRead();
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the body is a forward-only stream.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the request body is read-only.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the request body is read-only.</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int CountReturned(int read)
    {
        _returned += read;
        return read;
    }

    // Completes the declared length of an abandoned upload with zero bytes, as many as the buffer holds
    // and the length still owes; 0 once nothing is owed, which ends the body. None of these bytes comes
    // from the caller or reaches the backend: the backend connection drops every write of the abandoned
    // upload, and the padding exists only so that YARP's and the HTTP stack's counts of the body reach
    // the length the request head announced. Pads nothing, and returns false, for any other body, and
    // while the remainder is above MaxPaddedRemainder.
    private bool TryPad(BackendCallState call, Span<byte> buffer, out int padded)
    {
        padded = 0;
        if (_declaredLength is not { } declared || !call.PadsDeclaredBody)
        {
            return false;
        }

        var remainder = Math.Max(declared - _returned, 0);
        if (remainder > MaxPaddedRemainder)
        {
            return false;
        }

        padded = (int)Math.Min(buffer.Length, remainder);
        buffer[..padded].Clear();
        _returned += padded;
        return true;
    }
}
