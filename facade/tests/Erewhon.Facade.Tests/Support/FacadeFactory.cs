// Hosts the facade under test on real loopback Kestrel listeners and builds the requests the routing,
// failure-behaviour and listener-isolation tests send to it. The facade picks the endpoint for a request
// by the socket it arrived on (HttpContext.Connection.LocalPort), so only real sockets are evidence: the
// in-memory TestServer client of WebApplicationFactory is never created here, and every request goes
// through the raw client of CreateRawClient to RestBaseAddress, SoapBaseAddress or AdminBaseAddress.
//
// Every operation fact a request carries (verb, path template, parameters, SOAP action, request element,
// endpoint path and query, namespaces, content type) is read from OperationCatalogue.Default; only the
// sample parameter values are chosen here.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every assertion and exception message raised here is a fixed
// literal: no port, URL, elapsed time, configuration value or inner exception message.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using Erewhon.Contracts.Catalogue;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The facade tests bind real sockets and assert on elapsed times, and WebApplicationFactory captures the
// facade's host through hosting diagnostics that every host built in the process raises, so no two tests
// may run at once. This is the only parallelisation setting in the assembly. It is xunit.v3's
// Parallelization attribute rather than CollectionBehavior(DisableTestParallelization = true), which
// xunit.v3 4.x marks obsolete as an error (CS0619).
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Erewhon.Facade.Tests.Support;

/// <summary>The routing targets and extra settings one facade instance starts with.</summary>
/// <remarks>
/// <c>RoutingOptionsValidator</c> requires every referenced target to have the base URL of each surface it
/// serves, and the default target (<c>backend</c>) to have both, so a <c>stub</c> target named by a routing
/// override should be given both <see cref="StubRestBaseUrl"/> and <see cref="StubSoapBaseUrl"/>.
/// </remarks>
public sealed class FacadeTestSettings
{
    /// <summary><c>Routing:Targets:backend:RestBaseUrl</c>, for example a stub's <c>RestBaseUrl</c>.</summary>
    public required string BackendRestBaseUrl { get; init; }

    /// <summary><c>Routing:Targets:backend:SoapBaseUrl</c>, for example a stub's <c>SoapBaseUrl</c>.</summary>
    public required string BackendSoapBaseUrl { get; init; }

    /// <summary><c>Routing:Targets:stub:RestBaseUrl</c>; the target is defined only when a URL is supplied.</summary>
    public string? StubRestBaseUrl { get; init; }

    /// <summary><c>Routing:Targets:stub:SoapBaseUrl</c>; the target is defined only when a URL is supplied.</summary>
    public string? StubSoapBaseUrl { get; init; }

    /// <summary>
    /// Further configuration keys, applied after every other setting so that they win, for example
    /// <c>Settings = { ["Routing:Operations:Rest:getCompany"] = "stub" }</c>. Keys are matched
    /// case-insensitively, as configuration keys are.
    /// </summary>
    public Dictionary<string, string?> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>Backend:TimeoutSeconds</c>; the facade's own default (30) applies when null.</summary>
    public int? BackendTimeoutSeconds { get; init; }

    /// <summary>
    /// <c>Facade:WsdlPath</c>. When null, a path that does not exist is used, so the facade starts with
    /// <c>wsdl: absent</c> and forwards WSDL requests to the default target.
    /// </summary>
    public string? WsdlPath { get; init; }
}

/// <summary>
/// The facade (<c>Erewhon.Facade</c>'s <c>Program</c>) running in this process on three real Kestrel
/// listeners on 127.0.0.1, one per facade listener, each on a port that was free when the factory was
/// created.
/// </summary>
/// <remarks>
/// <para>
/// Start a facade through <see cref="StartAsync"/>, which constructs, starts and verifies an instance.
/// Start every <c>StubBackend</c> the facade should reach before calling it, never concurrently with it:
/// <c>WebApplicationFactory</c> intercepts host building while the facade's entry point runs, and a host
/// built elsewhere at that moment could be captured in place of the facade's.
/// </para>
/// <para>
/// The facade's own <c>ConfigureKestrel</c> callback binds its listeners from the <c>Facade:*Port</c>
/// settings this factory supplies, replacing the dynamic URL that <c>UseKestrel()</c> sets.
/// <see cref="StartAsync"/> verifies the bound addresses. Disposal stops the host, within the facade's own
/// 5 s shutdown timeout, and releases the ports.
/// </para>
/// </remarks>
public sealed class FacadeFactory : WebApplicationFactory<global::Program>
{
    private const string LoopbackHost = "127.0.0.1";
    private const int MaxStartAttempts = 3;
    private const int MaxPortDraws = 32;

    // A raw client gives up after this long, so a facade that hangs fails the test instead of stalling it.
    private static readonly TimeSpan RawClientTimeout = TimeSpan.FromSeconds(15);

    private readonly KeyValuePair<string, string?>[] _settings;

    /// <summary>
    /// Draws three distinct free loopback ports and prepares the facade's settings. The facade is not
    /// started; <see cref="StartAsync"/> constructs, starts and verifies an instance.
    /// </summary>
    /// <param name="settings">The routing targets and extra settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public FacadeFactory(FacadeTestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        (RestPort, SoapPort, AdminPort) = DrawDistinctPorts();
        RestBaseAddress = LoopbackAddress(RestPort);
        SoapBaseAddress = LoopbackAddress(SoapPort);
        AdminBaseAddress = LoopbackAddress(AdminPort);
        _settings = BuildSettings(settings, RestPort, SoapPort, AdminPort);
    }

    /// <summary>The port of the REST listener (<c>Facade:RestPort</c>) on 127.0.0.1.</summary>
    public int RestPort { get; }

    /// <summary>The port of the SOAP listener (<c>Facade:SoapPort</c>) on 127.0.0.1.</summary>
    public int SoapPort { get; }

    /// <summary>The port of the admin listener (<c>Facade:AdminPort</c>) on 127.0.0.1.</summary>
    public int AdminPort { get; }

    /// <summary><c>http://127.0.0.1:&lt;RestPort&gt;/</c>.</summary>
    public Uri RestBaseAddress { get; }

    /// <summary><c>http://127.0.0.1:&lt;SoapPort&gt;/</c>.</summary>
    public Uri SoapBaseAddress { get; }

    /// <summary><c>http://127.0.0.1:&lt;AdminPort&gt;/</c>.</summary>
    public Uri AdminBaseAddress { get; }

    /// <summary>
    /// Starts the facade on real Kestrel listeners and returns once it has bound exactly
    /// <see cref="RestPort"/>, <see cref="SoapPort"/> and <see cref="AdminPort"/> on 127.0.0.1. A port taken
    /// by another process between drawing and binding is retried with fresh ports, up to three attempts.
    /// </summary>
    /// <param name="settings">The routing targets and extra settings.</param>
    /// <param name="ct">Cancels start-up before an attempt begins.</param>
    /// <returns>The started factory; the caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Every attempt found a port in use, or the facade bound other addresses than the ones allocated.
    /// </exception>
    public static async Task<FacadeFactory> StartAsync(FacadeTestSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        for (int attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var factory = new FacadeFactory(settings);
            try
            {
                factory.UseKestrel();

                // StartServer runs the facade's entry point and starts its host synchronously.
                await Task.Run(factory.StartServer, ct);
                factory.VerifyBinding();
                return factory;
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                await factory.DisposeAsync();
            }
            catch
            {
                await factory.DisposeAsync();
                throw;
            }
        }

        throw new InvalidOperationException("facade could not bind free loopback ports");
    }

    /// <summary>
    /// Creates a client for the facade's real sockets: no proxy (an environment <c>HTTP_PROXY</c> must
    /// never intercept loopback calls), no redirects, no cookies, no decompression, and a 15 s timeout.
    /// It has no base address; requests carry absolute URIs built from the base addresses.
    /// </summary>
    /// <returns>A new client; the caller disposes it.</returns>
    public HttpClient CreateRawClient() =>
        new(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            },
            disposeHandler: true)
        {
            Timeout = RawClientTimeout,
        };

    /// <summary>
    /// Sends <paramref name="request"/>, reads the whole response body and returns the response with the
    /// time from just before sending to just after the body was read. The request is disposed afterwards.
    /// </summary>
    /// <param name="client">A client from <see cref="CreateRawClient"/>.</param>
    /// <param name="request">The request, for example from <see cref="PreparedRequest.ToMessage"/>.</param>
    /// <param name="ct">The test's cancellation token.</param>
    /// <returns>The response status, content type, headers, body bytes and elapsed time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="request"/> is null.</exception>
    /// <remarks>
    /// A transport failure (connection refused or reset, or no response within the client's timeout) fails
    /// the test with a fixed message naming only the exception type. Cancellation of <paramref name="ct"/>
    /// propagates as <see cref="OperationCanceledException"/>.
    /// </remarks>
    public static async Task<RawResponse> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        using (request)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
                stopwatch.Stop();

                return new RawResponse(
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType,
                    response.Content.Headers.ContentType?.CharSet,
                    MergeHeaders(response),
                    body,
                    stopwatch.Elapsed);
            }
            catch (Exception exception) when (IsTransportFailure(exception, ct))
            {
                Assert.Fail("facade request failed at the transport level (" + exception.GetType().Name + ")");
                throw new UnreachableException();
            }
        }
    }

    /// <summary>
    /// Sends one body-less HTTP/1.1 request whose request line is written byte for byte as given, over a
    /// new TCP connection to 127.0.0.1:<paramref name="port"/>, and returns the response. It sends what
    /// <see cref="HttpClient"/> cannot: a method token in any letter case, an absolute-form or asterisk-form
    /// target, and dot segments or escapes that must stay as written.
    /// </summary>
    /// <param name="port">A facade listener port, for example <see cref="RestPort"/>.</param>
    /// <param name="requestLine">
    /// The request line without its CRLF, for example <c>get /company HTTP/1.1</c>: printable ASCII and spaces.
    /// </param>
    /// <param name="host">The <c>Host</c> header value, for example the authority of <see cref="RestBaseAddress"/>.</param>
    /// <param name="ct">The test's cancellation token.</param>
    /// <returns>The response status, content type, headers, body bytes and elapsed time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requestLine"/> or <paramref name="host"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestLine"/> or <paramref name="host"/> is empty or holds a character other than
    /// printable ASCII and space.
    /// </exception>
    /// <remarks>
    /// The request carries only <c>Host</c> and <c>Connection: close</c>, so the facade closes the
    /// connection after its response, and the response ends at the end of the stream. The body is
    /// returned as it arrived, with no transfer coding removed. A connection failure, no response within
    /// 15 s, or a response without a status line and complete header section fails the test with a fixed
    /// message. Cancellation of <paramref name="ct"/> propagates as <see cref="OperationCanceledException"/>.
    /// </remarks>
    public static async Task<RawResponse> SendRawAsync(int port, string requestLine, string host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestLine);
        ArgumentNullException.ThrowIfNull(host);
        if (!IsRawHeadText(requestLine) || !IsRawHeadText(host))
        {
            throw new ArgumentException("raw request line and host must be non-empty printable ASCII");
        }

        byte[] head = Encoding.ASCII.GetBytes(requestLine + "\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RawClientTimeout);

        byte[] received;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(head, timeout.Token);

            using var response = new MemoryStream();
            await stream.CopyToAsync(response, timeout.Token);
            received = response.ToArray();
        }
        catch (Exception exception) when (exception is SocketException or IOException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Assert.Fail("raw facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        stopwatch.Stop();
        return ParseRawResponse(received, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs the facade as in production, never in the factory's default Development environment, whose
    /// developer exception page would answer in place of a forwarded response. Each setting is applied
    /// twice: as a host setting, which reaches the facade's builder through its arguments, and as an
    /// in-memory application configuration source, added last, so it also wins over the facade's
    /// <c>appsettings.json</c> and any <c>Facade__*</c>, <c>Backend__*</c> or <c>Routing__*</c> environment
    /// variable.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        foreach ((string key, string? value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
    }

    private void VerifyBinding()
    {
        ICollection<string>? addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var expected = new HashSet<int> { RestPort, SoapPort, AdminPort };
        var bound = new HashSet<int>();
        bool matches = addresses is not null && addresses.Count == expected.Count;
        foreach (string address in addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
                && string.Equals(uri.Host, LoopbackHost, StringComparison.Ordinal))
            {
                bound.Add(uri.Port);
            }
            else
            {
                matches = false;
            }
        }

        if (!matches || !bound.SetEquals(expected))
        {
            throw new InvalidOperationException("facade did not bind the allocated listener ports");
        }
    }

    // The ordered settings the facade starts with; a later pair overrides an earlier one with the same key.
    private static KeyValuePair<string, string?>[] BuildSettings(FacadeTestSettings settings, int restPort, int soapPort, int adminPort)
    {
        var pairs = new List<KeyValuePair<string, string?>>
        {
            new("Facade:RestPort", restPort.ToString(CultureInfo.InvariantCulture)),
            new("Facade:SoapPort", soapPort.ToString(CultureInfo.InvariantCulture)),
            new("Facade:AdminPort", adminPort.ToString(CultureInfo.InvariantCulture)),
            new("Facade:BindAddress", LoopbackHost),
            new("Facade:WsdlPath", settings.WsdlPath ?? Path.Combine(AppContext.BaseDirectory, "no-wsdl", "ErewhonWebService.wsdl")),
            new("Routing:Targets:backend:RestBaseUrl", settings.BackendRestBaseUrl),
            new("Routing:Targets:backend:SoapBaseUrl", settings.BackendSoapBaseUrl),
        };

        if (settings.StubRestBaseUrl is not null)
        {
            pairs.Add(new("Routing:Targets:stub:RestBaseUrl", settings.StubRestBaseUrl));
        }

        if (settings.StubSoapBaseUrl is not null)
        {
            pairs.Add(new("Routing:Targets:stub:SoapBaseUrl", settings.StubSoapBaseUrl));
        }

        if (settings.BackendTimeoutSeconds is int timeoutSeconds)
        {
            pairs.Add(new("Backend:TimeoutSeconds", timeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        pairs.AddRange(settings.Settings);
        return [.. pairs];
    }

    private static (int RestPort, int SoapPort, int AdminPort) DrawDistinctPorts()
    {
        var drawn = new List<int>(3);
        for (int draw = 0; drawn.Count < 3; draw++)
        {
            if (draw == MaxPortDraws)
            {
                throw new InvalidOperationException("facade could not draw three distinct free loopback ports");
            }

            int port = StubBackend.GetClosedLoopbackPort();
            if (!drawn.Contains(port))
            {
                drawn.Add(port);
            }
        }

        return (drawn[0], drawn[1], drawn[2]);
    }

    private static Uri LoopbackAddress(int port) =>
        new(string.Create(CultureInfo.InvariantCulture, $"http://{LoopbackHost}:{port}/"), UriKind.Absolute);

    // Kestrel reports a taken port as an IOException ("Failed to bind to address ...: address already in
    // use") wrapping AddressInUseException, and the host may wrap that again.
    private static bool IsAddressInUse(Exception exception)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out Exception? current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            if (current is AddressInUseException
                || current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                || (current is IOException && current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
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

    private static bool IsTransportFailure(Exception exception, CancellationToken ct) =>
        exception is HttpRequestException or IOException
        || (exception is OperationCanceledException && !ct.IsCancellationRequested);

    private static bool IsRawHeadText(string text) =>
        text.Length > 0 && text.All(c => c is >= ' ' and <= '~');

    // The status line, the header fields and the bytes after the empty line of a response read to the end
    // of its connection; a repeated header name keeps every value in received order.
    private static RawResponse ParseRawResponse(byte[] received, TimeSpan elapsed)
    {
        int headEnd = received.AsSpan().IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            Assert.Fail("raw facade response has no complete header section");
            throw new UnreachableException();
        }

        string[] lines = Encoding.Latin1.GetString(received, 0, headEnd).Split("\r\n");
        string[] statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length < 2
            || !statusLine[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || statusLine[1].Length != 3
            || !int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
        {
            Assert.Fail("raw facade response has no valid status line");
            throw new UnreachableException();
        }

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                Assert.Fail("raw facade response has a malformed header line");
                throw new UnreachableException();
            }

            string name = line[..colon];
            string value = line[(colon + 1)..].Trim(' ', '\t');
            headers[name] = headers.TryGetValue(name, out string[]? existing) ? [.. existing, value] : [value];
        }

        MediaTypeHeaderValue? contentType =
            headers.TryGetValue("Content-Type", out string[]? contentTypes)
            && MediaTypeHeaderValue.TryParse(contentTypes[0], out MediaTypeHeaderValue? parsed)
                ? parsed
                : null;

        return new RawResponse(status, contentType?.MediaType, contentType?.CharSet, headers, received[(headEnd + 4)..], elapsed);
    }

    // Response and content headers in one case-insensitive map, values as received on the wire (the
    // non-validated view never reformats them); a name present in both keeps both sets of values.
    private static Dictionary<string, string[]> MergeHeaders(HttpResponseMessage response)
    {
        var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(merged, response.Headers.NonValidated);
        AddHeaders(merged, response.Content.Headers.NonValidated);
        return merged;
    }

    private static void AddHeaders(Dictionary<string, string[]> merged, HttpHeadersNonValidated headers)
    {
        foreach ((string name, HeaderStringValues values) in headers)
        {
            string[] copied = [.. values];
            merged[name] = merged.TryGetValue(name, out string[]? existing) ? [.. existing, .. copied] : copied;
        }
    }
}

/// <summary>One response from the facade exactly as the raw client received it.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="MediaType">The media type of <c>Content-Type</c>, for example <c>application/problem+json</c>; null when absent.</param>
/// <param name="CharSet">The <c>charset</c> parameter of <c>Content-Type</c>; null when absent.</param>
/// <param name="Headers">
/// Every response and content header, keyed case-insensitively, with values as received; a name present
/// in both sets keeps both sets of values.
/// </param>
/// <param name="Body">The response body bytes.</param>
/// <param name="Elapsed">The time from just before the request was sent to just after the body was read.</param>
public sealed record RawResponse(
    int Status,
    string? MediaType,
    string? CharSet,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body,
    TimeSpan Elapsed)
{
    /// <summary>The body decoded as UTF-8.</summary>
    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>Returns the first value of the named header, matched case-insensitively, or null.</summary>
    public string? Header(string name)
    {
        if (Headers.TryGetValue(name, out string[]? values))
        {
            return values.Length > 0 ? values[0] : null;
        }

        // FacadeFactory builds Headers with a case-insensitive comparer; a dictionary supplied through
        // the public constructor may not have one.
        foreach ((string key, string[] candidate) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Length > 0 ? candidate[0] : null;
            }
        }

        return null;
    }
}

/// <summary>
/// A request for one catalogue operation at its normal address, ready to be sent to any of the facade's
/// sockets. Built by <see cref="OperationRequests"/>.
/// </summary>
/// <param name="Operation">The catalogue operation the request calls.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="PathAndQuery">
/// The request target, already escaped, which a backend receives unchanged as its raw target, for
/// example <c>/agent/Sample</c>.
/// </param>
/// <param name="Body">The request body bytes; null for a request without content.</param>
/// <param name="ContentType">The exact <c>Content-Type</c> value sent with <paramref name="Body"/>; null without a body.</param>
/// <param name="SoapAction">The exact <c>SOAPAction</c> value, quotes included; null to send no <c>SOAPAction</c> header.</param>
public sealed record PreparedRequest(
    OperationDescriptor Operation,
    HttpMethod Method,
    string PathAndQuery,
    byte[]? Body,
    string? ContentType,
    string? SoapAction)
{
    /// <summary>
    /// Creates a new HTTP/1.1 message for this request against <paramref name="baseAddress"/>. Headers are
    /// added verbatim, without validation or reformatting.
    /// </summary>
    /// <param name="baseAddress">The socket to send to, for example <see cref="FacadeFactory.RestBaseAddress"/>.</param>
    /// <param name="host">
    /// A <c>Host</c> header value to send in place of the base address's authority, for example to forge
    /// another listener's port; null keeps the default.
    /// </param>
    /// <returns>A fresh message on every call; <see cref="FacadeFactory.SendAsync"/> disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> is null.</exception>
    public HttpRequestMessage ToMessage(Uri baseAddress, string? host = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var message = new HttpRequestMessage(Method, new Uri(baseAddress, PathAndQuery))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        if (Body is not null)
        {
            var content = new ByteArrayContent(Body);
            if (ContentType is not null)
            {
                content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            }

            message.Content = content;
        }

        if (SoapAction is not null)
        {
            message.Headers.TryAddWithoutValidation("SOAPAction", SoapAction);
        }

        if (host is not null)
        {
            message.Headers.Host = host;
        }

        return message;
    }
}

/// <summary>
/// Builds a deterministic request for any catalogue operation from <see cref="OperationCatalogue.Default"/>
/// alone. Parameter values are fixed samples that need no escaping, so the raw target a backend receives
/// equals <see cref="PreparedRequest.PathAndQuery"/> byte for byte.
/// </summary>
public static class OperationRequests
{
    private const string StringSample = "Sample";
    private const string IntegerSample = "1";
    private const string DecimalSample = "1.00";

    // The fallback content type of a REST body; the catalogue names application/json for both body operations.
    private const string JsonContentType = "application/json";

    // The DTO member the SOAP body sets for a DTO parameter, mirroring DtoJsonBody.
    private const string DtoNameElement = "name";

    // The REST DTO request body, UTF-8 without a BOM: a DTOAgent or DTOClient holding only a name.
    private static readonly byte[] DtoJsonBody = "{\"name\":\"Sample\"}"u8.ToArray();

    /// <summary>Builds the request for <paramref name="operation"/> on its own surface.</summary>
    /// <param name="operation">A catalogue operation.</param>
    /// <returns>The REST or SOAP request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation's surface is neither REST nor SOAP.</exception>
    public static PreparedRequest For(OperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Surface switch
        {
            Surface.Rest => ForRest(operation),
            Surface.Soap => ForSoap(operation),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), "operation surface is neither Rest nor Soap"),
        };
    }

    /// <summary>
    /// Builds the REST request: the binding's verb, and its path template with each path parameter
    /// replaced by a sample for its JADE type (<c>String</c> <c>Sample</c>, <c>Integer</c> <c>1</c>,
    /// <c>Decimal</c> <c>1.00</c>). A body parameter becomes the UTF-8 JSON body <c>{"name":"Sample"}</c>
    /// with the binding's request content type.
    /// </summary>
    /// <param name="operation">A REST catalogue operation.</param>
    /// <returns>The request, for example <c>GET /agent/Sample</c> or <c>POST /createTender/Sample/Sample/1/1.00</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The operation has no REST binding, a parameter is not carried in the path or body, or a path
    /// template placeholder names no path parameter.
    /// </exception>
    public static PreparedRequest ForRest(OperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RestBinding rest = operation.Rest ?? throw new InvalidOperationException("operation has no REST binding");

        string path = rest.PathTemplate;
        byte[]? body = null;
        string? contentType = null;
        foreach (ParameterDescriptor parameter in operation.Parameters.OrderBy(parameter => parameter.Position))
        {
            switch (parameter.Location)
            {
                case ParameterLocation.Path:
                    path = path.Replace(
                        "{" + parameter.Name + "}", Uri.EscapeDataString(SampleFor(parameter.JadeType)), StringComparison.Ordinal);
                    break;
                case ParameterLocation.Body:
                    body = [.. DtoJsonBody];
                    contentType = rest.RequestContentType ?? JsonContentType;
                    break;
                default:
                    throw new InvalidOperationException("REST operation parameter is carried neither in the path nor in the body");
            }
        }

        if (path.Contains('{', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unresolved path template placeholder");
        }

        return new PreparedRequest(
            operation, new HttpMethod(rest.Verb.ToUpperInvariant()), path, body, contentType, SoapAction: null);
    }

    /// <summary>
    /// Builds the SOAP request: <c>POST</c> to the catalogue's SOAP endpoint path and query with the
    /// catalogue's SOAP content type, the operation's quoted <c>SOAPAction</c>, and a SOAP 1.1
    /// document/literal wrapped envelope holding the request element with one sample child per parameter
    /// in signature order; a DTO parameter holds a single <c>name</c> member.
    /// </summary>
    /// <param name="operation">A SOAP catalogue operation.</param>
    /// <param name="includeSoapAction">False to send no <c>SOAPAction</c> header, so only the body names the operation.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The operation has no SOAP binding, or a parameter is not carried as a request child element.
    /// </exception>
    public static PreparedRequest ForSoap(OperationDescriptor operation, bool includeSoapAction = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        SoapBinding soap = operation.Soap ?? throw new InvalidOperationException("operation has no SOAP binding");
        OperationCatalogue catalogue = OperationCatalogue.Default;
        SoapAddress address = catalogue.Addresses.Soap;

        return new PreparedRequest(
            operation,
            HttpMethod.Post,
            address.EndpointPathAndQuery,
            BuildEnvelope(catalogue, address, soap, operation.Parameters),
            address.ContentType,
            includeSoapAction ? "\"" + soap.Action + "\"" : null);
    }

    private static byte[] BuildEnvelope(
        OperationCatalogue catalogue, SoapAddress address, SoapBinding soap, IReadOnlyList<ParameterDescriptor> parameters)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = true,
            Indent = false,
            CloseOutput = false,
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement("soap", "Envelope", address.EnvelopeNamespace);
            writer.WriteStartElement("soap", "Body", address.EnvelopeNamespace);

            // The wrapper declares the message namespace as the default, which its children inherit.
            writer.WriteStartElement(soap.RequestElement, address.TargetNamespace);
            foreach (ParameterDescriptor parameter in parameters.OrderBy(parameter => parameter.Position))
            {
                if (parameter.Location != ParameterLocation.Element)
                {
                    throw new InvalidOperationException("SOAP operation parameter is not carried as a request element");
                }

                writer.WriteStartElement(parameter.Name, address.TargetNamespace);
                if (catalogue.TryGetDto(parameter.JadeType, out _))
                {
                    writer.WriteElementString(DtoNameElement, address.TargetNamespace, StringSample);
                }
                else
                {
                    writer.WriteString(SampleFor(parameter.JadeType));
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return stream.ToArray();
    }

    private static string SampleFor(string jadeType) => jadeType switch
    {
        "Integer" => IntegerSample,
        "Decimal" => DecimalSample,
        _ => StringSample,
    };
}
