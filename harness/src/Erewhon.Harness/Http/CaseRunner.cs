using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Http;

/// <summary>
/// What one request obtained: the response exactly as received, or the reason no HTTP response was
/// obtained. Nothing in it is parsed, decoded or re-serialised; normalisation happens only when responses
/// are compared.
/// </summary>
/// <param name="Status">The HTTP status code; null when no response was obtained.</param>
/// <param name="ContentType">
/// The raw <c>Content-Type</c> header text as the server sent it; null when the response has none or no
/// response was obtained.
/// </param>
/// <param name="Headers">
/// Every response header value, one pair per value, response headers first and then content headers, in
/// the order the transport holds them. Trailing headers are not included.
/// </param>
/// <param name="Body">
/// The exact body bytes, never decompressed or decoded; <see cref="Array.Empty{T}"/> for an empty body or
/// when no response was obtained. Never null.
/// </param>
/// <param name="NoResponse">
/// A <see cref="NoResponseReasons"/> value when no HTTP response was obtained; null when one was.
/// </param>
/// <param name="Elapsed">Time from the start of the send to the end of the body read or the failure.</param>
public sealed record CaseResponse(
    int? Status,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    string? NoResponse,
    TimeSpan Elapsed)
{
    /// <summary>True when an HTTP response was obtained.</summary>
    public bool Answered => NoResponse is null;

    /// <summary>
    /// Diagnostic text for a failed request: <c>&lt;ExceptionType&gt;: &lt;message&gt;</c> of the failure
    /// and the innermost exception's type. It may be printed or quoted, but it varies between runs (ports,
    /// host names, operating-system messages), so it is never part of a signature or a comparison.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>The first value of the response header named <paramref name="name"/>, ignoring case.</summary>
    /// <param name="name">The header name, for example <c>Content-Type</c>.</param>
    /// <returns>The raw header value, or null when the response has no such header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public string? GetHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (headerName, value) in Headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Builds the result of a request that obtained no HTTP response.</summary>
    /// <param name="reason">One of the <see cref="NoResponseReasons"/> values.</param>
    /// <param name="elapsed">Time from the start of the send to the failure.</param>
    /// <param name="detail">Diagnostic text, see <see cref="Detail"/>.</param>
    /// <returns>A response with no status, content type, headers or body.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not a <see cref="NoResponseReasons"/> value.</exception>
    public static CaseResponse Failed(string reason, TimeSpan elapsed, string? detail) =>
        NoResponseReasons.IsKnown(reason)
            ? new CaseResponse(null, null, [], [], reason, elapsed) { Detail = detail }
            : throw new ArgumentException(
                $"'{reason}' is not a no-response reason; expected '{NoResponseReasons.Connect}', "
                + $"'{NoResponseReasons.Timeout}' or '{NoResponseReasons.Reset}'.",
                nameof(reason));
}

/// <summary>
/// Sends one request byte for byte and captures the response raw. The same requests are sent to the
/// stand-in at capture and to the facade at replay, so the runner adds nothing a case does not carry: no
/// <c>Accept</c>, <c>User-Agent</c>, <c>Accept-Encoding</c>, cookie, credential or <c>X-Forwarded-*</c>
/// header, no proxy, no redirect and no decompression. Only <c>Host</c> and <c>Content-Length</c> come
/// from the transport.
/// </summary>
/// <remarks>
/// <para>The runner keeps no per-call state, so one instance can be shared by concurrent callers. It
/// writes nothing to the console or to disk; logging belongs to its callers.</para>
/// <para>SocketsHttpHandler resends a <c>GET</c> on a new connection, up to three times, when the server
/// closes the connection before sending any response byte, and the last attempt decides the reason
/// (observed on .NET 10). It never resends a request with a body, so a state-changing case reaches the
/// server at most once.</para>
/// </remarks>
public sealed class CaseRunner : IDisposable
{
    /// <summary>The per-request limit used when no other is configured, 60 seconds.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private const string NotHttpUrlProblem = "The URL is not an absolute http or https URL.";

    // CancellationTokenSource.CancelAfter and SocketsHttpHandler.ConnectTimeout both reject a finite
    // duration beyond int.MaxValue milliseconds, so it is rejected where the value enters.
    private static readonly TimeSpan MaximumFiniteTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    // Without this option Uri unescapes and re-escapes the path and query and removes dot segments, so a
    // case target would not reach the wire exactly as recorded.
    private static readonly UriCreationOptions RawPathAndQuery = new()
    {
        DangerousDisablePathAndQueryCanonicalization = true,
    };

    // The transport sets these from the target and the actual body bytes, and comparison ignores them.
    private static readonly HashSet<string> TransportManagedHeaders =
        new(["Host", "Content-Length", "Transfer-Encoding", "Connection"], StringComparer.OrdinalIgnoreCase);

    // Strict, so a body text that UTF-8 cannot represent exactly (a lone surrogate) is rejected instead
    // of being sent with replacement bytes. GetBytes never writes a byte order mark.
    private static readonly UTF8Encoding BodyEncoding =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly SocketError[] ConnectSocketErrors =
    [
        SocketError.ConnectionRefused,
        SocketError.HostNotFound,
        SocketError.HostUnreachable,
        SocketError.NetworkUnreachable,
        SocketError.NetworkDown,
        SocketError.TryAgain,
        SocketError.NoData,
        SocketError.AddressNotAvailable,
        SocketError.TimedOut,
    ];

    private readonly HttpClient _client;

    /// <summary>Creates a runner whose connection attempts are limited to <see cref="DefaultRequestTimeout"/>.</summary>
    public CaseRunner()
        : this(DefaultRequestTimeout)
    {
    }

    /// <summary>Creates a runner whose connection attempts are limited to <paramref name="connectTimeout"/>.</summary>
    /// <param name="connectTimeout">A positive duration, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="connectTimeout"/> is zero, negative other than infinite, or beyond
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    public CaseRunner(TimeSpan connectTimeout)
    {
        ValidateTimeout(connectTimeout, nameof(connectTimeout));

        // UseProxy = false keeps HTTP_PROXY and similar container settings from redirecting harness
        // traffic; the other settings keep the handler from adding or altering anything.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
            PreAuthenticate = false,
            Credentials = null,
        };

        // Each call enforces its own deadline, so the client-wide timeout is disabled.
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        _client.DefaultRequestHeaders.Clear();
    }

    /// <summary>The per-request limit configured in <paramref name="options"/>.</summary>
    /// <param name="options">The harness settings.</param>
    /// <returns>
    /// <see cref="HarnessOptions.RequestTimeoutSeconds"/> as a duration, or <see cref="DefaultRequestTimeout"/>
    /// when it is zero or negative.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static TimeSpan RequestTimeoutOf(HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.RequestTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
            : DefaultRequestTimeout;
    }

    /// <summary>Creates a runner whose connection attempts are limited to the configured per-request limit.</summary>
    /// <param name="options">The harness settings.</param>
    /// <returns>A new runner, owned and disposed by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The configured limit is beyond <see cref="int.MaxValue"/> milliseconds.</exception>
    public static CaseRunner FromOptions(HarnessOptions options) => new(RequestTimeoutOf(options));

    /// <summary>
    /// Appends a raw request target to the scheme and authority of <paramref name="baseUrl"/>, keeping
    /// the target exactly as written.
    /// </summary>
    /// <param name="baseUrl">An absolute <c>http</c> or <c>https</c> URL; any path, query or trailing slash is dropped.</param>
    /// <param name="pathAndQuery">An already percent-encoded target starting with <c>/</c>.</param>
    /// <returns>A URI whose path and query are <paramref name="pathAndQuery"/> unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseUrl"/> is not an absolute <c>http</c>/<c>https</c> URL without user information,
    /// or <paramref name="pathAndQuery"/> is not a raw target. A message about the base URL never quotes it.
    /// </exception>
    public static Uri Combine(string baseUrl, string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!TryParseHttpUrl(baseUrl, out var parsed, out var problem))
        {
            throw new ArgumentException(problem, nameof(baseUrl));
        }

        return Combine(parsed, pathAndQuery);
    }

    /// <inheritdoc cref="Combine(string, string)"/>
    public static Uri Combine(Uri baseUrl, string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ValidateTargetUri(baseUrl, nameof(baseUrl));
        ValidatePathAndQuery(pathAndQuery);

        var uri = new Uri(baseUrl.GetLeftPart(UriPartial.Authority) + pathAndQuery, in RawPathAndQuery);

        // The request line is written from PathAndQuery; a target the parser would still reshape is
        // rejected rather than sent differently from the case.
        if (!string.Equals(uri.PathAndQuery, pathAndQuery, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Request target '{pathAndQuery}' cannot be sent unchanged; it would become '{uri.PathAndQuery}'.",
                nameof(pathAndQuery));
        }

        return uri;
    }

    /// <summary>Sends <paramref name="request"/> to the listener at <paramref name="baseUrl"/>.</summary>
    /// <param name="baseUrl">The listener's absolute base URL, for example <c>http://standin:65000</c>.</param>
    /// <param name="request">The case request, sent exactly as it is.</param>
    /// <param name="timeout">The deadline for the whole call, body read included; positive or infinite.</param>
    /// <param name="ct">The caller's cancellation, which propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The raw response, or the reason none was obtained.</returns>
    /// <exception cref="ArgumentException">The base URL, target, headers or body cannot be sent unchanged.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<CaseResponse> SendAsync(Uri baseUrl, CaseRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRawAsync(
            Combine(baseUrl, request.PathAndQuery), request.Method, request.Headers, request.BodyText, timeout, ct);
    }

    /// <inheritdoc cref="SendAsync(Uri, CaseRequest, TimeSpan, CancellationToken)"/>
    public Task<CaseResponse> SendAsync(string baseUrl, CaseRequest request, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRawAsync(
            Combine(baseUrl, request.PathAndQuery), request.Method, request.Headers, request.BodyText, timeout, ct);
    }

    /// <summary>
    /// Sends a <c>GET</c> with no header and no body, as used for WSDL, OpenAPI and admin fetches.
    /// </summary>
    /// <param name="absoluteUri">The target, preferably made by <see cref="Combine(Uri, string)"/>.</param>
    /// <param name="timeout">The deadline for the whole call; positive or infinite.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The raw response, or the reason none was obtained.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="absoluteUri"/> is not an absolute http or https URI without user information.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<CaseResponse> GetAsync(Uri absoluteUri, TimeSpan timeout, CancellationToken ct) =>
        SendRawAsync(absoluteUri, "GET", null, null, timeout, ct);

    /// <summary>
    /// Sends one request exactly as given and captures the response raw, within <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>The method is sent as given. A non-null <paramref name="bodyText"/> is sent as its UTF-8
    /// bytes with no byte order mark and its line endings kept; a null one sends no content, unless a
    /// header must travel as a content header, which then rides on empty content.</para>
    /// <para>Headers are added in enumeration order with their exact spelling, spacing and quotes, as
    /// request headers or, when the transport classes them as content headers, as content headers.
    /// <c>Host</c>, <c>Content-Length</c>, <c>Transfer-Encoding</c> and <c>Connection</c> are left to
    /// the transport.</para>
    /// <para>A request that obtains no HTTP response returns <see cref="CaseResponse.Failed"/> with
    /// exactly one of the <see cref="NoResponseReasons"/> values, classified by
    /// <see cref="ClassifyFailure"/>.</para>
    /// </remarks>
    /// <param name="absoluteUri">The absolute http or https target.</param>
    /// <param name="method">The HTTP method, for example <c>GET</c> or <c>POST</c>.</param>
    /// <param name="headers">The headers to send, or null for none.</param>
    /// <param name="bodyText">The exact body text, or null for no body.</param>
    /// <param name="timeout">The deadline for connect, headers and body read together; positive or infinite.</param>
    /// <param name="ct">The caller's cancellation, which propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The raw response, or the reason none was obtained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="absoluteUri"/> or <paramref name="method"/> is null.</exception>
    /// <exception cref="ArgumentException">The target, method, headers or body cannot be sent unchanged.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive or infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<CaseResponse> SendRawAsync(
        Uri absoluteUri,
        string method,
        IReadOnlyDictionary<string, string>? headers,
        string? bodyText,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(absoluteUri);
        ValidateTargetUri(absoluteUri, nameof(absoluteUri));
        ValidateTimeout(timeout, nameof(timeout));
        var start = Stopwatch.GetTimestamp();

        using var request = BuildRequest(absoluteUri, method, headers, bodyText);
        using var deadline = new CancellationTokenSource();
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(timeout);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        string? reason = null;
        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            return Capture(response, body, Stopwatch.GetElapsedTime(start));
        }
        catch (Exception ex) when ((reason = ClassifyFailure(
            ex, ct.IsCancellationRequested, deadline.IsCancellationRequested)) is not null)
        {
            // The filter leaves unclassified exceptions, the caller's cancellation included, unwound
            // with their original stack.
            return CaseResponse.Failed(reason, Stopwatch.GetElapsedTime(start), DescribeFailure(ex));
        }
    }

    /// <summary>Disposes the HTTP client and its handler.</summary>
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Classifies an exception thrown while sending a request or reading its response, first match wins:
    /// the caller's cancellation is rethrown; an expired deadline is <c>timeout</c>; a connection that
    /// could not be established is <c>connect</c>; a connection that failed once established is
    /// <c>reset</c>; anything else is rethrown as a configuration or programming error.
    /// </summary>
    /// <param name="ex">The exception thrown by the send or the body read.</param>
    /// <param name="callerCancelled">Whether the caller's token was cancelled.</param>
    /// <param name="deadlineFired">Whether the per-call deadline had expired.</param>
    /// <returns>A <see cref="NoResponseReasons"/> value, or null when the exception must be rethrown.</returns>
    internal static string? ClassifyFailure(Exception ex, bool callerCancelled, bool deadlineFired)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (callerCancelled && ex is OperationCanceledException)
        {
            return null;
        }

        // Cancellation by the deadline surfaces differently during connect, headers and body read, so
        // the deadline's own state decides, whatever was thrown.
        if (deadlineFired && !callerCancelled)
        {
            return NoResponseReasons.Timeout;
        }

        // SocketsHttpHandler.ConnectTimeout surfaces as a TaskCanceledException or HttpRequestException
        // wrapping a TimeoutException.
        if (FindInChain<TimeoutException>(ex) is not null)
        {
            return NoResponseReasons.Connect;
        }

        if (FindInChain<HttpRequestException>(ex) is { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError })
        {
            return NoResponseReasons.Connect;
        }

        if (FindInChain<SocketException>(ex) is { } socketException
            && Array.IndexOf(ConnectSocketErrors, socketException.SocketErrorCode) >= 0)
        {
            return NoResponseReasons.Connect;
        }

        // HttpIOException derives from IOException, so a connection closed before or during the
        // response is covered here.
        if (FindInChain<HttpRequestException>(ex) is not null
            || FindInChain<IOException>(ex) is not null
            || FindInChain<SocketException>(ex) is not null)
        {
            return NoResponseReasons.Reset;
        }

        return null;
    }

    /// <summary>
    /// Parses a configured base or admin URL under the one rule every harness HTTP client applies: an
    /// absolute <c>http</c> or <c>https</c> URL without user information. Surrounding white space is
    /// ignored.
    /// </summary>
    /// <remarks>
    /// No layer sends credentials, so a URL carrying user information is refused before any request
    /// instead of being carried into the authority. <paramref name="problem"/> is printed and reported,
    /// so it never quotes <paramref name="text"/>, which may hold a password.
    /// </remarks>
    /// <param name="text">The configured URL, for example <c>http://standin:9090</c>.</param>
    /// <param name="uri">The parsed URL; null when the method returns false.</param>
    /// <param name="problem">
    /// Why <paramref name="text"/> was rejected, as one sentence naming at most the host or a scheme the
    /// runtime knows; empty when it was accepted.
    /// </param>
    /// <returns>
    /// True when <paramref name="text"/> is an absolute <c>http</c> or <c>https</c> URL without user
    /// information; false when it is null, blank, relative, of another scheme or carries user information.
    /// </returns>
    internal static bool TryParseHttpUrl(string? text, [NotNullWhen(true)] out Uri? uri, out string problem)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "The URL is blank.";
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
        {
            problem = NotHttpUrlProblem;
            return false;
        }

        if (!IsCredentialFreeHttpUrl(parsed, out problem))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Applies the rule of <see cref="TryParseHttpUrl"/> to an already parsed URI.</summary>
    /// <param name="uri">The URI to check.</param>
    /// <param name="problem">
    /// Why <paramref name="uri"/> was rejected, as one sentence naming at most the host or a scheme the
    /// runtime knows; empty when it was accepted.
    /// </param>
    /// <returns>True when <paramref name="uri"/> is an absolute <c>http</c> or <c>https</c> URL without user information.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    internal static bool IsCredentialFreeHttpUrl(Uri uri, out string problem)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // A relative URI has no scheme to read. On Unix an absolute file path parses as a file: URI, so
        // the scheme is checked as well.
        if (!uri.IsAbsoluteUri)
        {
            problem = NotHttpUrlProblem;
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            // A URL written without its scheme, such as user:password@host:9090, parses with the user
            // name as its scheme, so only a scheme the runtime itself registers is named.
            problem = UriParser.IsKnownScheme(uri.Scheme)
                ? $"The URL uses the '{uri.Scheme}' scheme; only http and https are accepted."
                : NotHttpUrlProblem;
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            problem = $"The URL for host '{uri.Host}' carries user information, which the harness never sends.";
            return false;
        }

        problem = "";
        return true;
    }

    private static HttpRequestMessage BuildRequest(
        Uri absoluteUri,
        string method,
        IReadOnlyDictionary<string, string>? headers,
        string? bodyText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var request = new HttpRequestMessage(new HttpMethod(method), absoluteUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        try
        {
            if (bodyText is not null)
            {
                request.Content = new ByteArrayContent(EncodeBody(bodyText));
            }

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    AddHeader(request, name, value);
                }
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    // Typed properties (ContentType, Accept and so on) are never used, because they re-serialise the
    // value; TryAddWithoutValidation keeps it exactly as written.
    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A request header has an empty name.", nameof(name));
        }

        if (TransportManagedHeaders.Contains(name))
        {
            return;
        }

        ValidateHeaderValue(name, value);
        if (request.Headers.TryAddWithoutValidation(name, value))
        {
            return;
        }

        request.Content ??= new ByteArrayContent(Array.Empty<byte>());
        if (!request.Content.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException(
                $"Request header '{name}' can be added neither as a request header nor as a content header.",
                nameof(name));
        }
    }

    // The transport refuses CR, LF and non-ASCII header values only when sending, with an
    // HttpRequestException that would read as a dropped connection; they are rejected here instead.
    private static void ValidateHeaderValue(string name, string? value)
    {
        if (value is null)
        {
            throw new ArgumentException($"Request header '{name}' has no value.", nameof(value));
        }

        foreach (var c in value)
        {
            if (c != '\t' && (c < '\u0020' || c > '\u007E'))
            {
                throw new ArgumentException(
                    $"Request header '{name}' holds U+{(int)c:X4}; only visible ASCII, space and tab can be sent unchanged.",
                    nameof(value));
            }
        }
    }

    private static byte[] EncodeBody(string bodyText)
    {
        try
        {
            return BodyEncoding.GetBytes(bodyText);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException(
                "The request body holds text that UTF-8 cannot represent exactly, such as a lone surrogate.",
                nameof(bodyText),
                ex);
        }
    }

    private static CaseResponse Capture(HttpResponseMessage response, byte[] body, TimeSpan elapsed)
    {
        var contentHeaders = response.Content.Headers.NonValidated;
        var contentType = contentHeaders.TryGetValues("Content-Type", out var contentTypeValues)
            ? contentTypeValues.ToString()
            : null;

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in response.Headers.NonValidated)
        {
            foreach (var value in values)
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        foreach (var (name, values) in contentHeaders)
        {
            foreach (var value in values)
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return new CaseResponse(
            (int)response.StatusCode,
            contentType,
            headers.AsReadOnly(),
            body.Length == 0 ? Array.Empty<byte>() : body,
            NoResponse: null,
            elapsed);
    }

    private static string DescribeFailure(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        var description = $"{TypeName(ex)}: {ex.Message}";
        return ReferenceEquals(innermost, ex) ? description : $"{description} (innermost {TypeName(innermost)})";
    }

    private static string TypeName(Exception ex) => ex.GetType().FullName ?? ex.GetType().Name;

    private static T? FindInChain<T>(Exception ex)
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

    private static void ValidateTargetUri(Uri uri, string paramName)
    {
        if (!IsCredentialFreeHttpUrl(uri, out var problem))
        {
            throw new ArgumentException(problem, paramName);
        }
    }

    private static void ValidatePathAndQuery(string pathAndQuery)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            throw new ArgumentException($"Request target '{pathAndQuery}' does not start with '/'.", nameof(pathAndQuery));
        }

        foreach (var c in pathAndQuery)
        {
            if (c <= '\u0020' || c >= '\u007F' || c == '#')
            {
                throw new ArgumentException(
                    $"Request target '{pathAndQuery}' holds U+{(int)c:X4}; it must be percent-encoded ASCII without '#'.",
                    nameof(pathAndQuery));
            }
        }
    }

    private static void ValidateTimeout(TimeSpan value, string paramName)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > MaximumFiniteTimeout))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                "The limit must be positive and at most int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
        }
    }
}
