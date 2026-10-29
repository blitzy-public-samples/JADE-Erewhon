using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
/// injection. The target is resolved on every call, so a routing reload applies to the next request.
/// The class keeps no per-request state and is safe for concurrent use as a singleton.
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
        var callerLine = CallerRequestLineToRestore(context);
        var singleUse = callerLine is null ? null : CreateInvoker(timeout, callerLine);
        var invoker = singleUse ?? InvokerFor(target);

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
        };

        if (callerLine is not null)
        {
            handler.PlaintextStreamFilter = (filterContext, _) =>
                ValueTask.FromResult<Stream>(new RequestLineRewritingStream(filterContext.PlaintextStream, callerLine));
        }

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
