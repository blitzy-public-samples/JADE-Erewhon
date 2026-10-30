using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
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
/// a response relayed in full before YARP found the upload incomplete, and a response relayed in full
/// before the caller left, such as a response with no body to a caller that closes its connection as
/// soon as it has read the head.
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
/// before the backend's whole response had been relayed to it, including while the backend's response
/// head was being sent to it. Nothing is written; the status is the one already sent, or 0 when no
/// response was sent.
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
/// injection, which connects and negotiates TLS by its own defaults and reads and writes each HTTP/1.x
/// connection through a <see cref="BackendConnectionStream"/> above TLS. It is created on the first call
/// to its target and reused, with its connection pool, by every later call. The target is resolved on
/// every call, so a routing reload applies to the next request. A call holds a lease on its invoker
/// until its response has been forwarded, written or aborted. When a reload leaves a target out of the
/// routing table, or the forwarder is disposed, the target's invoker is retired: no new call takes it,
/// and it is disposed once its last lease is released, so calls in flight finish on it and no superseded
/// invoker or connection pool outlives them. The class keeps no per-request state, which lives in each
/// call's <see cref="BackendCallState"/>, and is safe for concurrent use as a singleton.
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

    // The chain policy an https target's certificate is validated with; null keeps the platform's default.
    private readonly X509ChainPolicy? _certificateChainPolicy;

    private int _disposed;

    // The routing table's target set the cache was last swept against; another instance means a reload.
    private IReadOnlySet<ResolvedTarget>? _sweptTargets;

    /// <summary>
    /// Creates the forwarder; <c>Program.cs</c> registers it as a singleton. An https target's certificate
    /// is validated by the platform's default rules: its chain must end at a root the system trusts, and
    /// it must name the target's host.
    /// </summary>
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
        : this(forwarder, router, transformer, faultWriter, logger, certificateChainPolicy: null)
    {
    }

    /// <summary>
    /// Creates the forwarder with the chain policy an https target's certificate is validated with, for
    /// example one that trusts a test certificate authority alone. The policy decides only which roots
    /// the certificate's chain may end at, and how it is checked; the certificate must still name the
    /// target's host, and no certificate is accepted without validation.
    /// </summary>
    /// <param name="forwarder">YARP's direct forwarder, registered by <c>AddHttpForwarder()</c>.</param>
    /// <param name="router">Resolves each request's target from its surface and operation.</param>
    /// <param name="transformer">The byte-faithful pass-through transform used for every call.</param>
    /// <param name="faultWriter">Writes the SOAP Fault of a failed SOAP call.</param>
    /// <param name="logger">Receives one line per forwarded call.</param>
    /// <param name="certificateChainPolicy">
    /// The chain policy, copied here so that later changes to it have no effect; null keeps the
    /// platform's default, as the public constructor does.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument but <paramref name="certificateChainPolicy"/> is null.</exception>
    internal BackendForwarder(
        IHttpForwarder forwarder,
        OperationRouter router,
        PassThroughTransformer transformer,
        SoapFaultWriter faultWriter,
        ILogger<BackendForwarder> logger,
        X509ChainPolicy? certificateChainPolicy)
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
        _certificateChainPolicy = certificateChainPolicy?.Clone();
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
            var singleUse = callerLine is null ? null : CreateInvoker(timeout, _certificateChainPolicy, callerLine);
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
    /// The checks run in a fixed order: an expired deadline, which stays a timeout even for a response
    /// already relayed in full; a backend response already relayed in full, after which only the upload
    /// or the copy towards the caller failed; a caller that left before that; a timeout YARP or the
    /// handler reported; a backend response already taken over; a backend that gave no usable response;
    /// and last every other failure, which is the facade's own and is aborted with no response.
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

        // Once the backend's whole response has been relayed, the caller has the backend's complete answer,
        // which stands as it was sent. YARP checks the upload only after it has copied that response, so a
        // request-body error it reports then is a backend that stopped reading or a caller that stopped
        // sending. A cancelled or failed copy towards the caller is a caller that left once it had the whole
        // response, for example one that closes its connection as soon as it has read a head with no body
        // after it, before YARP has finished its copy.
        if (responseRelayed
            && (IsRequestBodyError(error) || error is ForwarderError.ResponseBodyCanceled or ForwarderError.ResponseBodyClient))
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
            var entry = _invokers.GetOrAdd(target, static (key, policy) => new InvokerEntry(key, policy), _certificateChainPolicy);
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
    /// <param name="certificateChainPolicy">The forwarder's certificate chain policy; null for the platform's default.</param>
    private sealed class InvokerEntry(ResolvedTarget target, X509ChainPolicy? certificateChainPolicy)
    {
        private const int RetiredFlag = int.MinValue;

        // Lazy makes two first calls to a target create at most one invoker; creating the entry creates none.
        private readonly Lazy<HttpMessageInvoker> _invoker = new(
            () => CreateInvoker(DeadlineOf(target), certificateChainPolicy),
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
    // so a caller's own trace headers pass through unchanged. The handler connects, and for an https
    // target negotiates TLS, by its own defaults; its plaintext stream filter then puts each HTTP/1.x
    // connection's plaintext stream, above TLS, inside a BackendConnectionStream. Given a caller's request
    // line, the invoker serves one call only, because the line belongs to that call's request: every
    // connection it opens writes that line in place of the first request line the handler writes, and
    // nothing else changes.
    private static HttpMessageInvoker CreateInvoker(
        TimeSpan connectTimeout,
        X509ChainPolicy? certificateChainPolicy,
        CallerRequestLine? callerLine = null)
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

            // The facade imposes no response-head ceiling of its own: the backend's status line and
            // headers are relayed whatever their size, instead of the handler's 64 KiB default failing a
            // valid response. The value is in KiB, and the handler caps its byte budget at int.MaxValue.
            MaxResponseHeadersLength = int.MaxValue,

            // A caller's own "Expect: 100-continue" holds the body back until the backend's 100 Continue
            // or the end of this wait. It is the smaller of the handler's one-second default and half the
            // target's deadline, so the wait can never consume the call's deadline before the body is sent.
            Expect100ContinueTimeout = connectTimeout / 2 < DefaultExpectContinueWait ? connectTimeout / 2 : DefaultExpectContinueWait,
        };

        // The chain policy replaces only the trust anchors and checks the certificate's chain is built
        // with; no validation callback is installed, so the handler's own validation, the host name check
        // included, decides every handshake.
        if (certificateChainPolicy is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = certificateChainPolicy };
        }

        // The filter's streams, innermost first: the connection's BackendConnectionStream around its
        // plaintext stream, then, given a caller's line, the request-line rewriting stream, whose rewritten
        // line is what the BackendConnectionStream writes.
        handler.PlaintextStreamFilter = callerLine is null
            ? static (filterContext, _) => ValueTask.FromResult(WatchedConnection(filterContext))
            : (filterContext, _) => ValueTask.FromResult<Stream>(
                new RequestLineRewritingStream(WatchedConnection(filterContext), callerLine));

        // A request with content, every write included, reaches the backend at most once, also when it
        // carries "Expect: 100-continue" and the backend closes the connection before the body starts.
        // SingleDeliveryHandler wraps the stream the filter above returns and adds, removes and changes
        // no header or byte.
        return new HttpMessageInvoker(new SingleDeliveryHandler(handler), disposeHandler: true);
    }

    // An HTTP/1.x connection's plaintext stream, the NetworkStream of an http target or the SslStream of an
    // https target, inside a BackendConnectionStream; a connection of any other protocol keeps its stream.
    private static Stream WatchedConnection(SocketsHttpPlaintextStreamFilterContext context) =>
        context.NegotiatedHttpVersion.Major == 1
            ? new BackendConnectionStream(context.PlaintextStream)
            : context.PlaintextStream;

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
/// <see cref="SocketsHttpHandler"/> resends a request on another connection, up to three more times, when
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
    /// first, and the guard wraps the stream that filter returns, so the guard sees the bytes exactly as
    /// the handler writes and reads them, above every stream of that filter.
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
// serves the call ForwardAsync makes: ForwardAsync begins every call's BackendCallState, CreateInvoker's
// plaintext stream filter wraps every HTTP/1.x backend connection in a BackendConnectionStream, and
// ForwardAsync installs CancellableRequestBody, as SoapEndpoints does before its inspection.
/// <summary>
/// What one forwarded call has reached so far, shared by the parts of the forwarding path that see
/// the same call from different sides: <see cref="BackendForwarder"/>, which begins and ends it and
/// reads it to decide the outcome, <see cref="CancellableRequestBody"/>, which reads the caller's
/// upload, <see cref="BackendConnectionStream"/>, which writes it to the backend and watches the backend
/// while it is in progress, and <see cref="PassThroughTransformer"/>, which sees the backend's response
/// go through. This is the one account of early replies, the end of an upload and the padding of an
/// abandoned one; the other types describe only their own part in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The call's context.</b> The state travels with the call's execution context, so the backend
/// connection, which serves one call after another and knows nothing of them, finds the call it is
/// writing for. Every read and write of a forwarded body runs in that context, because YARP copies both
/// inside the forwarder's call. Outside a forwarded call, <see cref="Current"/> is null and nothing here
/// applies.
/// </para>
/// <para>
/// <b>Early replies.</b> A backend may answer an upload before it has read all of it, for example with 413
/// for its own body-size limit, at once for a declared length over it or once a chunked upload crosses
/// it, and then keep the connection open or close it. Without <c>Expect: 100-continue</c> the HTTP stack
/// reads no response until it has sent the whole body, so a caller that pauses its upload would hold the
/// answer back until the deadline, and a write to a connection the backend closed would fail the call
/// and discard the answer: the caller would get the facade's timed-out or unavailable answer in place of
/// the backend's own. No <c>Expect: 100-continue</c> is added to make the backend answer first, because
/// the backend may not support the expectation.
/// </para>
/// <para>
/// <b>The end of the upload.</b> The upload therefore ends as soon as the backend shows it takes no more
/// of it: the connection fails a write of the upload because the backend reset or closed it, or, while
/// the upload is in progress, the backend sends anything but interim responses (1xx other than 101), or
/// ends or resets the connection. The upload is in progress from the first read of the caller's body
/// during the call, once the connection the call writes on has registered itself here with its first
/// write, until the caller's body has been read to its end, the upload has ended or the call is over.
/// The connection watches what the backend sends from its registration on, because the request head goes
/// out before the first read of the caller's body, and on a pooled connection the handler's own read
/// ahead can take a reply the backend sent at once: an interim response before the upload starts is
/// passed over, while any other reply is kept and ends the upload as soon as it starts. While the upload
/// is in progress the connection also reads ahead of the handler, so that a reply is seen while the
/// caller pauses. The watch ends with the upload, so nothing it leaves behind fires for this call later,
/// or for another call on the same pooled connection.
/// </para>
/// <para>
/// <b>The abandoned upload.</b> When the upload ends before the caller's body has, the connection drops
/// every later write of this call, and a read waiting on the caller's body returns at once. A chunked body
/// ends there, so the caller is read no further. A body whose length the caller declared must still reach
/// that length, because YARP and the HTTP stack each fail a body shorter than the <c>Content-Length</c>
/// its request head announced and discard the backend's answer with it; <see cref="CancellableRequestBody"/>
/// completes it with padding bytes, none of which is sent. The HTTP stack then returns the backend's
/// answer exactly as the backend sent it, or, when the backend closed or reset the connection without
/// one, fails the call, which the forwarder answers as an unavailable backend at once.
/// </para>
/// <para>
/// <b>Locking.</b> Every change of state takes one lock, under which the connection is told to start and
/// stop its watch and to drop writes. The connection reports a reply or a failed write only outside its
/// own lock, so the two locks are always taken in that order.
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

    // Whether _connection watches what the backend sends for this call, and whether it also reads ahead
    // of the handler, which it does only while the upload is in progress.
    private bool _watchingReplies;
    private bool _readingAhead;

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
    /// caller's response: its head has been sent and no body follows it, its declared body length has
    /// been written, or its body has been copied to the end. A caller that leaves after this has the
    /// backend's complete answer.
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

    /// <summary>
    /// Records that the backend's whole response has been copied to the caller's response.
    /// <see cref="PassThroughTransformer"/> calls it as soon as it sees the end of the response: once a
    /// head with no body after it has been sent, once the declared body length has been written, or once
    /// a body of unknown length has been copied to the end. Calling it again changes nothing.
    /// </summary>
    public void MarkResponseRelayed() => Volatile.Write(ref _responseRelayed, 1);

    /// <summary>
    /// Registers the backend connection the call writes on. The connection calls it for every write it
    /// makes during the call, so it is known by the time the request head goes out; from then on it
    /// watches what the backend sends for the call, reading ahead of the handler too while the upload is
    /// in progress.
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
                ReadAheadIfUploadingLocked();
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
                    ReadAheadIfUploadingLocked();
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

    // The connection reads ahead of the handler only while the upload is in progress: once it has
    // started, on a connection whose replies are watched, which they are from its registration until the
    // upload finishes or ends.
    private void ReadAheadIfUploadingLocked()
    {
        if (!_watchingReplies || _readingAhead || !_uploadStarted)
        {
            return;
        }

        _readingAhead = true;
        _connection!.StartReadAhead(this);
    }

    private void StopWatchingLocked()
    {
        if (!_watchingReplies)
        {
            return;
        }

        _watchingReplies = false;
        _readingAhead = false;
        _connection!.StopWatching(this);
    }
}

/// <summary>
/// One HTTP/1.x backend connection as the handler reads and writes it: the connection's plaintext
/// stream, through which the backend is watched for a call's upload, so that a backend that replies
/// during the upload ends it as <see cref="BackendCallState"/> describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layering.</b> The handler connects, and negotiates TLS for an https target, by its own defaults, so
/// connect failures, their <see cref="HttpRequestError"/>, the connect timeout and certificate validation
/// are the handler's. Its plaintext stream filter then wraps the connection's plaintext stream, the
/// <see cref="NetworkStream"/> of an http target or the <see cref="SslStream"/> of an https target, in
/// this stream, beneath the request-line rewriting stream a call may need and the guard of
/// <see cref="SingleDeliveryHandler"/>. Everything read and classified here is therefore plaintext HTTP:
/// TLS records, handshake and post-handshake messages included, never reach it, and nothing here reads
/// the socket itself.
/// </para>
/// <para>
/// <b>Reads.</b> At most one read of the wrapped stream is outstanding at any time, because a TLS stream
/// serves one reader at a time. While a call's upload is in progress and the handler has no read
/// outstanding, this stream reads ahead of the handler, one read at a time, so that a reply the handler
/// would read only once the whole body is sent is seen as it arrives. A handler read that arrives while a
/// read ahead is in flight waits for it instead of reading beside it. The bytes a read ahead returns are
/// kept and handed to the handler's later reads in order, each at most once and every one of them unless
/// the bound below drops it, and a zero-byte read returns at once while bytes are kept. The end of the
/// stream or the failure a read ahead meets is kept too, and every handler read gets it once the kept
/// bytes are taken. A read ahead is never cancelled, because a cancelled read can leave a TLS stream
/// unusable: a watch that stops only starts no further read ahead.
/// </para>
/// <para>
/// <b>Bound.</b> At most <see cref="MaxReadAheadBytes"/> are kept. When that many are and a read ahead is
/// due, the interim responses among them of which the handler has been handed no byte are dropped: every
/// complete one before the response in progress, and that response too when it is interim. The rest of
/// an interim response cut that way is dropped as it arrives, by reads ahead that go on, and that the
/// handler's reads wait for, until its head ends, also once the watch has stopped. An interim response the
/// handler has been handed part of, a status line that is still incomplete and everything from the
/// backend's reply on are never dropped; when nothing can be, no read ahead starts until the handler takes
/// some bytes. A backend that sends interim responses without end thus neither grows the buffer nor hides
/// a reply behind them, unless the handler already holds the start of one whose rest exceeds the bound.
/// Should the rest of a cut response show that it is no interim response the HTTP stack accepts, the
/// handler gets an <see cref="IOException"/> after the bytes kept before it, as the HTTP stack would have
/// failed the call on that response, and the call gets the reply.
/// </para>
/// <para>
/// <b>Classification.</b> From a call's first write until its upload finishes or ends, every completed read
/// of the wrapped stream, the handler's and each read ahead, is classified once, in the order the bytes
/// arrived, however the reads split them. A response is interim when the HTTP stack passes over it: its
/// status line is <c>HTTP/1.</c>, a digit, a space and a code from 100 to 199 other than 101, then the end
/// of the line or a space and a reason phrase that holds no NUL and no CR, and each of its header lines
/// holds a colon or, after the first, begins with a space or tab. A line ends at LF, and one CR before the
/// LF is not part of it, as the HTTP stack reads response heads. An interim response's head is passed over
/// up to the empty line that ends it, and the watch goes on to the next response. A status line that is
/// still incomplete decides nothing until more bytes arrive. Anything else is the backend's reply,
/// reported to the call once: a final status, 101 Switching Protocols, which ends HTTP/1.1 on the
/// connection, a response the HTTP stack would reject, a byte that begins no HTTP response, the end of the
/// stream, or a failure other than this connection's disposal or a cancellation. Classification changes
/// and consumes no byte. Each watch is started and stopped by its call, so it never reports to another
/// call on the same connection.
/// </para>
/// <para>
/// <b>Writes.</b> Each write registers the connection on the current call. Once the call's upload has
/// started, a write that fails because the backend reset or closed the connection ends the upload
/// instead of failing the call: a <see cref="SocketException"/> with
/// <see cref="SocketError.ConnectionReset"/>, <see cref="SocketError.Shutdown"/> for a broken pipe, or
/// <see cref="SocketError.ConnectionAborted"/>, found among the failure's inner exceptions, where the
/// network stream puts it and a TLS stream passes it on. From the end of the upload on, every write of
/// that call completes without sending anything. Every other failure surfaces unchanged, and so does any
/// failure before the upload has started, such as the request head on a pooled connection the backend
/// has dropped, or after the handler has disposed the connection to cancel the call.
/// </para>
/// <para>
/// <b>No reuse.</b> A connection whose upload was ended is never used for another call: every read for any
/// other call, the handler's checks of an idle connection included, returns the end of the stream, which
/// the handler takes as a connection it must not reuse, and a write for another call fails as a closed
/// connection would.
/// </para>
/// </remarks>
internal sealed class BackendConnectionStream : Stream
{
    /// <summary>
    /// The most bytes, 64 KiB, that reads ahead keep for the handler; while that many are kept, interim
    /// responses the handler has not begun to read are dropped to make room, and no read ahead starts
    /// when there are none.
    /// </summary>
    internal const int MaxReadAheadBytes = 64 * 1024;

    // The most bytes one read ahead asks for; the buffer of kept bytes grows from this to MaxReadAheadBytes.
    private const int ReadAheadLength = 4 * 1024;

    // The offset of a response that is not wholly among the kept bytes, because the handler has been handed
    // some of it.
    private const int NotKept = -1;

    private readonly Stream _inner;

    // Guards the watch, the kept bytes and the reads of the wrapped stream. It is never held while
    // calling into a BackendCallState, because a call holds its own lock when it calls in here.
    private readonly Lock _gate = new();

    // The call whose upload this connection ended; set once.
    private BackendCallState? _abandonedBy;

    // The watch of the backend for the call writing on this connection, until its upload finishes or
    // ends; null when there is none.
    private UploadWatch? _watch;

    // The bytes read ahead that the handler has not taken yet: _keptCount bytes from _keptStart in _kept.
    private byte[] _kept = [];
    private int _keptStart;
    private int _keptCount;

    // The end of the stream, or the failure, that a read ahead met; the handler gets it after the bytes.
    private bool _keptEnd;
    private Exception? _keptFailure;

    // The responses in what the backend sends, fed every completed read in order while the watch
    // classifies them for its call or the rest of a cut interim head is being dropped.
    private InterimResponseScanner _scanner;

    // Offsets from the first kept byte, or NotKept: where the response the scanner is in begins, and where
    // the complete interim responses before it that the handler has not begun to read begin, so that
    // those bytes up to _responseOffset may be dropped. Kept so that _droppableOffset <= _responseOffset
    // whenever it is not NotKept.
    private int _responseOffset = NotKept;
    private int _droppableOffset = NotKept;

    // Whether the rest of the interim head in progress is dropped as it arrives, because its start was.
    private bool _droppingHead;

    // Completes once the read ahead in flight has kept what it read; null when none is in flight.
    private Task? _readAhead;

    // The handler's reads of the wrapped stream in progress; a read ahead starts only while there are none.
    private int _handlerReads;

    private int _disposed;

    /// <summary>Wraps one connection's plaintext stream, which this instance then owns.</summary>
    /// <param name="inner">
    /// The connection's plaintext stream: its <see cref="NetworkStream"/>, or its <see cref="SslStream"/>
    /// for an https target.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public BackendConnectionStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    // How a handler read goes on once it has looked at what reads ahead kept.
    private enum HandlerRead
    {
        // It was answered from the kept bytes, the kept end of the stream or the kept failure.
        Served,

        // It waits for the read ahead in flight, then looks again.
        Joins,

        // It reads the wrapped stream itself, counted in _handlerReads.
        ReadsInner,
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanTimeout => _inner.CanTimeout;

    /// <inheritdoc />
    public override int ReadTimeout
    {
        get => _inner.ReadTimeout;
        set => _inner.ReadTimeout = value;
    }

    /// <inheritdoc />
    public override int WriteTimeout
    {
        get => _inner.WriteTimeout;
        set => _inner.WriteTimeout = value;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a connection is a forward-only stream.</exception>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a connection is a forward-only stream.</exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a connection is a forward-only stream.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a connection is a forward-only stream.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

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

        // A synchronous read blocks on the read ahead in flight, whose task never faults, then looks again.
        HandlerRead next;
        int served;
        Exception? failure;
        Task? inFlight;
        while ((next = BeginHandlerRead(buffer, out served, out failure, out inFlight)) == HandlerRead.Joins)
        {
            inFlight!.GetAwaiter().GetResult();
        }

        if (next == HandlerRead.Served)
        {
            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            return served;
        }

        int count;
        try
        {
            count = _inner.Read(buffer);
        }
        catch (Exception readFailure)
        {
            EndHandlerRead(ReadOnlySpan<byte>.Empty, endOfStream: false, readFailure);
            throw;
        }

        EndHandlerRead(buffer[..count], endOfStream: count == 0 && !buffer.IsEmpty, failure: null);
        return count;
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

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        return BeginHandlerRead(buffer.Span, out var served, out var failure, out var inFlight) switch
        {
            HandlerRead.Served => failure is null ? ValueTask.FromResult(served) : ValueTask.FromException<int>(failure),
            HandlerRead.Joins => JoinReadAheadAsync(inFlight!, buffer, cancellationToken),
            _ => ReadInnerAsync(buffer, cancellationToken),
        };
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
            _inner.Write(buffer);
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
            write = _inner.WriteAsync(buffer, cancellationToken);
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
    /// Starts watching what the backend sends for <paramref name="call"/>, which has just registered this
    /// connection, in place of any earlier watch. Called by the call under its lock; the call stops the
    /// watch with <see cref="StopWatching"/> when its upload finishes or ends, or when the call is over.
    /// </summary>
    /// <param name="call">The call writing on this connection.</param>
    internal void WatchReplies(BackendCallState call)
    {
        lock (_gate)
        {
            _watch = new UploadWatch(call);

            // A call writes its request once the response to the one before has been read, so the next byte
            // the backend sends begins a response. That response came after any interim head being dropped,
            // so none is being dropped now; were one, its rest would still be dropped first.
            if (!_droppingHead)
            {
                _scanner = default;
                _responseOffset = NotKept;
                _droppableOffset = NotKept;
            }
        }
    }

    /// <summary>
    /// Starts reading ahead of the handler for <paramref name="call"/>, whose upload has just come to be in
    /// progress; does nothing unless the call's watch is the connection's. Called by the call under its
    /// lock.
    /// </summary>
    /// <param name="call">The call whose upload is in progress.</param>
    internal void StartReadAhead(BackendCallState call)
    {
        ReadAhead? read = null;
        lock (_gate)
        {
            if (_watch is { } watch && ReferenceEquals(watch.Call, call))
            {
                watch.ReadsAhead = true;
                read = TryStartReadAheadLocked();
            }
        }

        Launch(read);
    }

    /// <summary>
    /// Stops the watch started for <paramref name="call"/>, if it is still the connection's: no read ahead
    /// starts for it any more, and nothing read from then on is classified for it. Reads ahead that drop
    /// the rest of a cut interim head go on until that head ends.
    /// </summary>
    /// <param name="call">The call whose upload has finished or ended, or that is over.</param>
    internal void StopWatching(BackendCallState call)
    {
        lock (_gate)
        {
            if (_watch is { } watch && ReferenceEquals(watch.Call, call))
            {
                _watch = null;
            }
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
        // A read or write failing from here on was failed by the handler's own disposal, never by the
        // backend. A read ahead in flight fails with the wrapped stream and keeps that failure.
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    // Runs a read ahead started under the gate on the thread pool, so that it runs, and reports a reply,
    // outside the gate and outside the lock of a call that started it, without that call's execution
    // context.
    private static void Launch(ReadAhead? read)
    {
        if (read is not null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static read => _ = read.Owner.ReadAheadAsync(read), read, preferLocal: false);
        }
    }

    // Once the upload has been ended, a write of that call sends nothing. A write of any other call cannot
    // happen, because the handler never reuses the connection, and fails as the closed connection would.
    private static IOException ClosedByBackend() => new(
        "The backend closed this connection during an earlier upload.",
        new SocketException((int)SocketError.ConnectionReset));

    // Whether an inner exception of a failed write, at any depth, is the backend's reset or close of the
    // connection: the network stream wraps the SocketException in its IOException, which a TLS stream
    // passes on unchanged or inside one of its own.
    private static bool IsClosedByBackend(IOException failure)
    {
        for (var current = failure.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: SocketError.ConnectionReset or SocketError.Shutdown or SocketError.ConnectionAborted,
                })
            {
                return true;
            }
        }

        return false;
    }

    // Decides, under the gate, how a handler read goes on: answered from what reads ahead kept, waiting for
    // the read ahead in flight, or reading the wrapped stream itself, which is then counted so that no
    // read ahead starts beside it. Taking kept bytes can make room for a read ahead that the bound held
    // back, which starts outside the gate. While the rest of a cut interim head is being dropped, the
    // handler never reads the wrapped stream itself: it waits for a read ahead, which drops those bytes.
    private HandlerRead BeginHandlerRead(Span<byte> destination, out int served, out Exception? failure, out Task? inFlight)
    {
        served = 0;
        failure = null;
        inFlight = null;
        ReadAhead? next = null;
        HandlerRead decision;
        lock (_gate)
        {
            if (_keptCount > 0)
            {
                served = Math.Min(destination.Length, _keptCount);
                _kept.AsSpan(_keptStart, served).CopyTo(destination);
                TakeKeptLocked(served);
                next = TryStartReadAheadLocked();
                decision = HandlerRead.Served;
            }
            else if (_keptEnd || _keptFailure is not null)
            {
                failure = _keptFailure;
                decision = HandlerRead.Served;
            }
            else if (_readAhead is { } pending)
            {
                inFlight = pending;
                decision = HandlerRead.Joins;
            }
            else if (_droppingHead && (next = TryStartReadAheadLocked()) is not null)
            {
                inFlight = next.Completion;
                decision = HandlerRead.Joins;
            }
            else
            {
                _handlerReads++;
                decision = HandlerRead.ReadsInner;
            }
        }

        Launch(next);
        return decision;
    }

    // Classifies a completed handler read of the wrapped stream for the current watch, reports a reply to
    // the watch's call, and otherwise lets a read ahead start now that the handler reads nothing. A failed
    // handler read starts no read ahead, whatever failed it. Nothing is kept while the handler reads the
    // wrapped stream itself, so its bytes come just before the first byte kept from then on, and no head
    // is being dropped then, so none of them is dropped.
    private void EndHandlerRead(ReadOnlySpan<byte> bytes, bool endOfStream, Exception? failure)
    {
        UploadWatch? replied;
        ReadAhead? next = null;
        lock (_gate)
        {
            _handlerReads--;
            replied = TrackLocked(
                bytes, -bytes.Length, endOfStream || (failure is not null && IsBackendFailure(failure)), out _, out _);
            if (replied is null && failure is null)
            {
                next = TryStartReadAheadLocked();
            }
        }

        replied?.Call.AbandonUploadOnBackendReply(this);
        Launch(next);
    }

    // A handler read that arrived while a read ahead was in flight. The handler's cancellation ends only
    // its own wait; the read ahead goes on and keeps what it reads.
    private async ValueTask<int> JoinReadAheadAsync(Task inFlight, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            switch (BeginHandlerRead(buffer.Span, out var served, out var failure, out var next))
            {
                case HandlerRead.Served:
                    if (failure is not null)
                    {
                        ExceptionDispatchInfo.Throw(failure);
                    }

                    return served;

                case HandlerRead.Joins:
                    inFlight = next!;
                    break;

                default:
                    return await ReadInnerAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The handler's own read of the wrapped stream, already counted by BeginHandlerRead. A read that
    // completed at once, as reads of data already received do, is classified without an await.
    private ValueTask<int> ReadInnerAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ValueTask<int> read;
        try
        {
            read = _inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception failure)
        {
            EndHandlerRead(ReadOnlySpan<byte>.Empty, endOfStream: false, failure);
            throw;
        }

        if (!read.IsCompletedSuccessfully)
        {
            return AwaitInnerReadAsync(read, buffer);
        }

        var count = read.Result;
        EndHandlerRead(buffer.Span[..count], endOfStream: count == 0 && !buffer.IsEmpty, failure: null);
        return ValueTask.FromResult(count);
    }

    // Every response byte the handler reads itself passes through here, so a read that completes later
    // reuses a pooled state machine instead of allocating one.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> AwaitInnerReadAsync(ValueTask<int> read, Memory<byte> buffer)
    {
        int count;
        try
        {
            count = await read.ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            EndHandlerRead(ReadOnlySpan<byte>.Empty, endOfStream: false, failure);
            throw;
        }

        EndHandlerRead(buffer.Span[..count], endOfStream: count == 0 && !buffer.IsEmpty, failure: null);
        return count;
    }

    // One read ahead of the wrapped stream, never cancelled. What it returns is classified for the current
    // watch and kept for the handler, except the rest of a cut interim head, which is dropped; a reply is
    // reported to the watch's call, and otherwise the next read ahead starts when the watch still reads
    // ahead or a head is still being dropped. Its task never faults, so a handler read waiting for it only
    // ever wakes to look at what it kept.
    private async Task ReadAheadAsync(ReadAhead read)
    {
        var count = 0;
        Exception? failure = null;
        try
        {
            count = await _inner.ReadAsync(read.Target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        UploadWatch? replied;
        ReadAhead? next = null;
        lock (_gate)
        {
            _readAhead = null;
            if (failure is not null)
            {
                _keptFailure = failure;
            }
            else if (count == 0)
            {
                _keptEnd = true;
            }
            else
            {
                _keptCount += count;
            }

            replied = TrackLocked(
                read.Target.Span[..count],
                _keptCount - count,
                failure is null ? count == 0 : IsBackendFailure(failure),
                out var dropped,
                out var rejectedCutHead);

            // The dropped bytes begin the read, so the bytes it kept after them move up to its start.
            if (dropped > 0)
            {
                read.Target.Span[dropped..count].CopyTo(read.Target.Span);
                _keptCount -= dropped;
            }

            // The HTTP stack would have failed the call on the response whose start was dropped.
            if (rejectedCutHead)
            {
                _keptFailure = new IOException("The backend sent an interim response that is not valid HTTP.");
            }

            if (replied is null)
            {
                next = TryStartReadAheadLocked();
            }
        }

        read.Complete();
        replied?.Call.AbandonUploadOnBackendReply(this);
        Launch(next);
    }

    // Feeds a completed read of the wrapped stream to the response tracking while the watch classifies
    // for its call or a cut interim head is being dropped, and returns the watch when the read holds the
    // backend's reply, which the caller reports to the watch's call outside the gate. firstOffset is the
    // offset of the read's first byte from the first kept byte: the bytes of a read ahead are the last
    // ones kept, and the bytes of a handler read come just before the first one kept after them. dropped
    // tells how many of the read's first bytes are the rest of a cut interim head, which the caller drops
    // from the kept bytes; rejectedCutHead tells that they show that head to be no interim response the
    // HTTP stack accepts, in which case every byte of the read is dropped.
    private UploadWatch? TrackLocked(
        ReadOnlySpan<byte> bytes, int firstOffset, bool ended, out int dropped, out bool rejectedCutHead)
    {
        dropped = 0;
        rejectedCutHead = false;
        var reply = false;
        for (var index = 0; index < bytes.Length && !reply && (_watch is { ReplyTaken: false } || _droppingHead); index++)
        {
            if (_scanner.AtResponseStart)
            {
                BeginResponseLocked(firstOffset + index - dropped);
            }

            switch (_scanner.Advance(bytes[index]))
            {
                case InterimResponseScanner.Step.HeadEnded:
                    if (_droppingHead)
                    {
                        _droppingHead = false;
                        dropped = index + 1;
                    }

                    // The next byte begins the next response, also when it arrives with a later read.
                    BeginResponseLocked(firstOffset + index + 1 - dropped);
                    break;

                case InterimResponseScanner.Step.Reply:
                    if (_droppingHead)
                    {
                        _droppingHead = false;
                        rejectedCutHead = true;
                        dropped = bytes.Length;
                    }

                    reply = true;
                    break;
            }
        }

        if (_droppingHead)
        {
            dropped = bytes.Length;
        }

        return (reply || ended) && _watch is { } watch && watch.TakeReply() ? watch : null;
    }

    // Records that a response begins at the given offset from the first kept byte. A negative offset is a
    // byte the handler has been handed, so neither that response nor anything before it is droppable.
    // Otherwise, unless droppable interim responses lead up to it, a run of them may begin with it.
    private void BeginResponseLocked(int offset)
    {
        if (offset < 0)
        {
            _responseOffset = NotKept;
            _droppableOffset = NotKept;
        }
        else
        {
            _responseOffset = offset;
            if (_droppableOffset == NotKept)
            {
                _droppableOffset = offset;
            }
        }
    }

    // Hands the first served kept bytes to the handler. A response that begins among them is no longer
    // droppable, and a run of droppable interim responses that begins among them starts over at the
    // response in progress, because where the next of them begins is not kept.
    private void TakeKeptLocked(int served)
    {
        if (served == 0)
        {
            return;
        }

        _keptStart += served;
        _keptCount -= served;
        _responseOffset = _responseOffset >= served ? _responseOffset - served : NotKept;
        _droppableOffset = _droppableOffset >= served ? _droppableOffset - served : _responseOffset;
    }

    // Drops, from the kept bytes at the bound, the interim responses the handler has been handed no byte
    // of: the complete ones before the response in progress and, when that is interim too, what has
    // arrived of it, whose rest is then dropped as it arrives. A status line that is still incomplete is
    // kept, because it may begin the reply. The handler is otherwise handed every byte; dropping these,
    // only at the bound, keeps the watch going under any amount of interim responses and is invisible to
    // the handler and the caller. The HTTP stack passes over every interim response, and the one it acts
    // on, a 100 Continue to Expect: 100-continue, matters only while it holds the body back, before the
    // upload starts and so before any read ahead; 101 is a reply here, never dropped; YARP relays no
    // interim response; and the handler's limit on response heads, which interim heads count towards, is
    // set as high as it goes.
    private void DropInterimLocked()
    {
        if (_droppableOffset == NotKept)
        {
            return;
        }

        var cutsHead = _scanner.InInterimHead;
        var end = cutsHead ? _keptCount : _responseOffset;
        if (end == _droppableOffset)
        {
            return;
        }

        _kept.AsSpan(_keptStart + end, _keptCount - end).CopyTo(_kept.AsSpan(_keptStart + _droppableOffset));
        _keptCount -= end - _droppableOffset;
        if (cutsHead)
        {
            _droppingHead = true;
            _responseOffset = NotKept;
            _droppableOffset = NotKept;
        }
        else
        {
            _responseOffset = _droppableOffset;
        }
    }

    // Starts a read ahead when the current watch reads ahead and has taken no reply, or a cut interim head
    // is being dropped, nothing else reads the wrapped stream, no read ahead has met its end or a failure,
    // fewer than MaxReadAheadBytes are kept, after dropping interim responses at the bound, and the
    // connection is not disposed. It reads into the free space after the kept bytes, moving them to the
    // front of the buffer, or into a larger one, first when the space is too small. The caller passes the
    // result to Launch outside the gate.
    private ReadAhead? TryStartReadAheadLocked()
    {
        if (!(_watch is { ReadsAhead: true, ReplyTaken: false } || _droppingHead)
            || _readAhead is not null
            || _handlerReads > 0
            || _keptEnd
            || _keptFailure is not null
            || Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        if (_keptCount >= MaxReadAheadBytes)
        {
            DropInterimLocked();
            if (_keptCount >= MaxReadAheadBytes)
            {
                return null;
            }
        }

        var length = Math.Min(ReadAheadLength, MaxReadAheadBytes - _keptCount);
        if (_kept.Length - _keptStart - _keptCount < length)
        {
            var buffer = _kept.Length - _keptCount >= length
                ? _kept
                : new byte[Math.Min(MaxReadAheadBytes, Math.Max(_kept.Length * 2, _keptCount + length))];
            _kept.AsSpan(_keptStart, _keptCount).CopyTo(buffer);
            _kept = buffer;
            _keptStart = 0;
        }

        var read = new ReadAhead(this, _kept.AsMemory(_keptStart + _keptCount, length));
        _readAhead = read.Completion;
        return read;
    }

    // Whether a failed read of the wrapped stream is the backend's doing, a reset or any other failure of
    // the connection or of TLS, rather than this connection's disposal, a local close or a cancellation,
    // which leave the upload as it is.
    private bool IsBackendFailure(Exception failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || failure is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.OperationAborted })
            {
                return false;
            }
        }

        return true;
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

    // Once the upload has been ended, a read for anything but the backend's answer to that call, the
    // handler's checks of an idle connection included, finds the end of the stream.
    private bool IsEndedForAnotherCall() =>
        Volatile.Read(ref _abandonedBy) is { } abandonedBy && !ReferenceEquals(abandonedBy, BackendCallState.Current);

    // Takes a write failure as the end of the upload when the backend reset or closed the connection
    // while the current call's upload was being sent.
    private bool TryEndUpload(IOException failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsClosedByBackend(failure))
        {
            return false;
        }

        // The call drops this connection's later writes for it when it abandons the upload.
        return BackendCallState.Current is { } call && call.TryAbandonUpload(this);
    }

    /// <summary>One read ahead: the free space it reads into, and the task a waiting handler read awaits.</summary>
    /// <param name="owner">The connection it reads.</param>
    /// <param name="target">The free space after the kept bytes, which nothing else touches until it completes.</param>
    private sealed class ReadAhead(BackendConnectionStream owner, Memory<byte> target)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The connection it reads.</summary>
        public BackendConnectionStream Owner => owner;

        /// <summary>The free space it reads into.</summary>
        public Memory<byte> Target => target;

        /// <summary>Completes once what the read returned has been kept.</summary>
        public Task Completion => _completion.Task;

        /// <summary>Completes <see cref="Completion"/>; called once.</summary>
        public void Complete() => _completion.SetResult();
    }

    /// <summary>
    /// One call's watch of what the backend sends on this connection, from the call's first write until
    /// its upload finishes or ends. Read and changed only under the connection's gate.
    /// </summary>
    /// <param name="call">The call whose upload is watched.</param>
    private sealed class UploadWatch(BackendCallState call)
    {
        /// <summary>The call whose upload is watched.</summary>
        public BackendCallState Call => call;

        /// <summary>
        /// Whether the connection reads ahead of the handler for the call, as it does once the upload is in
        /// progress.
        /// </summary>
        public bool ReadsAhead { get; set; }

        /// <summary>
        /// Whether a reply has been taken, after which nothing is classified or read ahead for the call.
        /// </summary>
        public bool ReplyTaken { get; private set; }

        /// <summary>Takes the backend's reply for the call.</summary>
        /// <returns>True the first time only, when the reply is to be reported to the call.</returns>
        public bool TakeReply()
        {
            if (ReplyTaken)
            {
                return false;
            }

            ReplyTaken = true;
            return true;
        }
    }

    /// <summary>
    /// Tells the interim responses a backend sends apart from anything else, byte by byte in the order the
    /// bytes arrive, however reads split them, as the HTTP stack reads responses: the status line is
    /// <c>HTTP/1.</c>, a digit, a space and a code from 100 to 199 other than 101, then the end of the line
    /// or a space and a reason phrase that holds no NUL and no CR, and each header line holds a colon or,
    /// after the first, begins with a space or tab, up to the empty line that ends the head. A line ends at
    /// LF, and one CR before the LF is not part of it.
    /// </summary>
    private struct InterimResponseScanner
    {
        // The positions, in "HTTP/1.x 1xx", of the version digit and of the code's last two digits, and the
        // length of that prefix, after which the line ends or a space begins the reason phrase.
        private const int VersionDigitIndex = 7;
        private const int CodeTensIndex = 10;
        private const int CodeUnitsIndex = 11;
        private const int StatusCodeEnd = 12;

        // Where the bytes are: the status line up to its code, the rest of it, or the header lines.
        private Part _part;

        // How many bytes of the status line up to its code have matched, and whether its tens digit is 0, so
        // that a units digit 1 makes the code 101.
        private int _matched;
        private bool _codeTensZero;

        // Whether the previous byte of the status line was a CR, which only the line's LF may follow.
        private bool _crPending;

        // The current header line: how many bytes it holds before its LF, counted up to two, which is all an
        // empty line needs, whether the last of them is a CR, whether it begins with a space or tab, whether
        // it holds a colon, and whether it is the first.
        private int _lineLength;
        private bool _lineEndsWithCr;
        private bool _lineFolds;
        private bool _lineHasColon;
        private bool _laterHeaderLine;

        /// <summary>What one byte shows about the response it belongs to.</summary>
        public enum Step
        {
            /// <summary>The status line is still too short to tell an interim response from a reply.</summary>
            Undecided,

            /// <summary>The byte belongs to an interim response's head, which goes on.</summary>
            Interim,

            /// <summary>The byte ends an interim response's head; the next byte begins the next response.</summary>
            HeadEnded,

            /// <summary>The response is no interim response the HTTP stack accepts: it is the reply.</summary>
            Reply,
        }

        private enum Part
        {
            StatusCode,
            StatusRest,
            HeaderLines,
        }

        /// <summary>Whether the next byte begins a response.</summary>
        public readonly bool AtResponseStart => _part == Part.StatusCode && _matched == 0;

        /// <summary>Whether the bytes so far are in the head of an interim response.</summary>
        public readonly bool InInterimHead => _part != Part.StatusCode;

        private static ReadOnlySpan<byte> StatusPrefix => "HTTP/1.x 1"u8;

        /// <summary>Feeds the next byte the backend sent; called no more once it gives <see cref="Step.Reply"/>.</summary>
        /// <param name="value">The byte, which follows every byte fed before.</param>
        /// <returns>What the byte shows.</returns>
        public Step Advance(byte value) => _part switch
        {
            Part.StatusCode => AdvanceStatusCode(value),
            Part.StatusRest => AdvanceStatusRest(value),
            _ => AdvanceHeaderLine(value),
        };

        private Step AdvanceStatusCode(byte value)
        {
            var index = _matched++;
            if (index < StatusCodeEnd)
            {
                var matches = index switch
                {
                    VersionDigitIndex or CodeTensIndex => char.IsAsciiDigit((char)value),
                    CodeUnitsIndex => char.IsAsciiDigit((char)value) && !(_codeTensZero && value == (byte)'1'),
                    _ => value == StatusPrefix[index],
                };
                _codeTensZero = index == CodeTensIndex ? value == (byte)'0' : _codeTensZero;
                return matches ? Step.Undecided : Step.Reply;
            }

            // The code ends the line, or a space begins the reason phrase.
            _part = Part.StatusRest;
            switch (value)
            {
                case (byte)' ':
                    return Step.Interim;

                case (byte)'\r':
                    _crPending = true;
                    return Step.Interim;

                case (byte)'\n':
                    _part = Part.HeaderLines;
                    return Step.Interim;

                default:
                    return Step.Reply;
            }
        }

        private Step AdvanceStatusRest(byte value)
        {
            if (value == (byte)'\n')
            {
                _part = Part.HeaderLines;
                return Step.Interim;
            }

            if (_crPending || value == 0)
            {
                return Step.Reply;
            }

            _crPending = value == (byte)'\r';
            return Step.Interim;
        }

        private Step AdvanceHeaderLine(byte value)
        {
            if (value != (byte)'\n')
            {
                _lineFolds = _lineLength == 0 ? value is (byte)' ' or (byte)'\t' : _lineFolds;
                _lineHasColon |= value == (byte)':';
                _lineEndsWithCr = value == (byte)'\r';
                _lineLength = Math.Min(_lineLength + 1, 2);
                return Step.Interim;
            }

            // An empty line ends the head, and the scanner starts over for the next response.
            if (_lineLength == (_lineEndsWithCr ? 1 : 0))
            {
                this = default;
                return Step.HeadEnded;
            }

            if (!_lineHasColon && !(_lineFolds && _laterHeaderLine))
            {
                return Step.Reply;
            }

            _lineLength = 0;
            _lineEndsWithCr = false;
            _lineFolds = false;
            _lineHasColon = false;
            _laterHeaderLine = true;
            return Step.Interim;
        }
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
/// Once the call has abandoned the upload, as <see cref="BackendCallState"/> describes, a read already
/// waiting for the caller's next bytes returns at once. A chunked body ends there
/// (<see cref="BackendCallState.CallerBodyEnded"/>). The remainder of a body of declared length
/// (<see cref="BackendCallState.PadsDeclaredBody"/>) is returned as padding: zero bytes made here, as many
/// as each read's buffer holds and the declared length still owes, never read from the caller, and
/// dropped by the backend connection. A remainder above <see cref="MaxPaddedRemainder"/> is first read from
/// the caller, every byte dropped, until it is within it, so a caller declaring an outsized length cannot
/// make the facade produce and discard padding for the rest of the deadline; the deadline bounds that
/// read. Bytes read during SOAP inspection count towards the declared length like any other.
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
    // and the length still owes; 0 once nothing is owed, which ends the body. These are the padding bytes
    // BackendCallState accounts for, counted in _returned like the caller's own. Pads nothing, and returns
    // false, for any other body, and while the remainder is above MaxPaddedRemainder.
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
