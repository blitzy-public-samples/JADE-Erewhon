// Minimal stub HTTP backend for the facade tests. A test points the facade's routing targets at
// instances of this class (target "backend" at stub A, target "stub" at stub B), calls the facade, and
// then asserts which stub received the call, on which listener, with exactly which request target,
// headers and body, and that the stub's response came back unchanged.
//
// Each stub has two loopback listeners in the roles of the backend's REST application (65000 in
// production) and SOAP application (21001); the stub itself always binds ephemeral ports. The listener
// a request arrived on is taken only from the accepting socket (HttpContext.Connection.LocalPort),
// never from the Host header, because the listener-isolation tests forge Host on purpose.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every exception message raised here is a fixed literal, and the
// default response body names the stub, listener, method and target but never a port.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Erewhon.Facade.Tests.Support;

/// <summary>The stub listener whose socket accepted a request.</summary>
public enum StubListener
{
    /// <summary>The listener in the role of the backend's REST application.</summary>
    Rest,

    /// <summary>The listener in the role of the backend's SOAP application.</summary>
    Soap,
}

/// <summary>
/// One request exactly as the stub received it, with the response the stub sent, or would have sent
/// had the caller not aborted the call first.
/// </summary>
/// <param name="Listener">The listener whose socket accepted the connection.</param>
/// <param name="Method">The request method.</param>
/// <param name="RawTarget">The request target as on the wire: path and query, undecoded.</param>
/// <param name="Headers">Every request header, keyed case-insensitively, values in received order.</param>
/// <param name="Body">The request body bytes; after an aborted upload, only the bytes read before it.</param>
/// <param name="ResponseStatus">The response status.</param>
/// <param name="ResponseBody">The response body bytes.</param>
public sealed record StubRequest(
    StubListener Listener,
    string Method,
    string RawTarget,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body,
    int ResponseStatus,
    byte[] ResponseBody)
{
    /// <summary>Returns the first value of the named header, matched case-insensitively, or null.</summary>
    public string? Header(string name)
    {
        if (Headers.TryGetValue(name, out string[]? values))
        {
            return values.Length > 0 ? values[0] : null;
        }

        // The stub builds Headers with a case-insensitive comparer; a dictionary supplied through the
        // public constructor may not have one.
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

/// <summary>How a stub answers every request on either listener.</summary>
public sealed class StubBackendOptions
{
    /// <summary>The response status: a final status code from 200 to 599.</summary>
    public int Status { get; init; } = 200;

    /// <summary>
    /// Response headers applied after <c>X-Stub-Id</c> and the default <c>Content-Type</c>. Each entry
    /// replaces a header of the same name, so a <c>Content-Type</c> entry overrides the default.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>
    /// The response body. When null, the body is the UTF-8 text
    /// <c>stub &lt;id&gt; &lt;Rest|Soap&gt; &lt;method&gt; &lt;raw target&gt;</c>.
    /// </summary>
    public byte[]? Body { get; init; }

    /// <summary>
    /// How long the stub waits after recording a request before answering it. The wait ends early, with
    /// no response written, when the caller aborts the call or the stub is disposed.
    /// </summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// A stub backend with a REST listener and a SOAP listener on ephemeral loopback ports. It records every
/// request on arrival, before any delay, and answers it with the configured response plus an
/// <c>X-Stub-Id</c> header naming the stub.
/// </summary>
/// <remarks>
/// Start stubs before starting a <c>FacadeFactory</c>, never concurrently with its start-up.
/// <c>WebApplicationFactory</c> captures the facade's host through <c>HostFactoryResolver</c>, which
/// listens to the hosting diagnostics of every host built in the process while the facade's entry point
/// runs, so a stub built at that moment could be captured in place of the facade.
/// </remarks>
public sealed class StubBackend : IAsyncDisposable
{
    private const string StubIdHeader = "X-Stub-Id";
    private const string DefaultContentType = "text/plain; charset=utf-8";

    // Stopping is bounded so that a handler waiting out a long delay cannot hold up disposal: when the
    // deadline passes, Kestrel aborts the open connections, which cancels RequestAborted.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly List<StubRequest> _received = [];
    private readonly int _status;
    private readonly byte[]? _body;
    private readonly KeyValuePair<string, string>[] _extraHeaders;
    private readonly TimeSpan _delay;
    private WebApplication? _app;
    private int _disposed;

    private StubBackend(string id, StubBackendOptions options)
    {
        Id = id;
        _status = options.Status;
        _delay = options.Delay;

        // Copied once, so a caller changing its arrays or dictionary later cannot change the answers.
        _body = options.Body?.ToArray();
        _extraHeaders = options.ExtraHeaders?.ToArray() ?? [];
    }

    /// <summary>The stub's name, sent back as the <c>X-Stub-Id</c> response header.</summary>
    public string Id { get; }

    /// <summary>The port of the REST listener on 127.0.0.1.</summary>
    public int RestPort { get; private set; }

    /// <summary>The port of the SOAP listener on 127.0.0.1.</summary>
    public int SoapPort { get; private set; }

    /// <summary>The REST listener's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>, with no trailing slash.</summary>
    public string RestBaseUrl => LoopbackUrl(RestPort);

    /// <summary>The SOAP listener's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>, with no trailing slash.</summary>
    public string SoapBaseUrl => LoopbackUrl(SoapPort);

    /// <summary>A snapshot of every request received since start or the last <see cref="ClearLog"/>, in arrival order.</summary>
    public IReadOnlyList<StubRequest> Received
    {
        get
        {
            lock (_gate)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>A snapshot of the requests received on one listener, in arrival order.</summary>
    public IReadOnlyList<StubRequest> ReceivedOn(StubListener listener)
    {
        lock (_gate)
        {
            return _received.Where(request => request.Listener == listener).ToArray();
        }
    }

    /// <summary>Forgets every request received so far.</summary>
    public void ClearLog()
    {
        lock (_gate)
        {
            _received.Clear();
        }
    }

    /// <summary>
    /// Starts a stub with a REST listener and then a SOAP listener, each on an ephemeral port of
    /// 127.0.0.1, and returns once both accept connections.
    /// </summary>
    /// <param name="id">The stub's name: visible ASCII without spaces, echoed in <c>X-Stub-Id</c>.</param>
    /// <param name="options">How the stub answers; the defaults answer 200 with the default body.</param>
    /// <param name="ct">Cancels start-up.</param>
    public static async Task<StubBackend> StartAsync(string id, StubBackendOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0 || !id.All(c => c is > ' ' and <= '~'))
        {
            throw new ArgumentException("stub backend id must be visible ASCII without spaces", nameof(id));
        }

        options ??= new StubBackendOptions();
        if (options.Status is < 200 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "stub backend status must be a final status code from 200 to 599");
        }

        if (options.Delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "stub backend delay must not be negative");
        }

        var stub = new StubBackend(id, options);
        ListenOptions? restListen = null;
        ListenOptions? soapListen = null;

        // The empty builder reads no configuration at all: neither the facade's appsettings.json, which
        // the project reference copies into the test output, nor ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS
        // (set by the .NET SDK image), nor ASPNETCORE_FORWARDEDHEADERS_ENABLED, whose middleware would
        // strip X-Forwarded-* headers before the stub could record them. It also adds no logging
        // provider, no host-filtering middleware and no configuration file watcher.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            // No Server header and no request body limit: whatever the facade forwards reaches the stub
            // whole, and the stub's response headers are only the ones set below.
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;

            // Listen order is REST then SOAP; the address fallback in ResolvePorts relies on it.
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                restListen = listen;
            });
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                soapListen = listen;
            });
        });
        builder.Services.AddRoutingCore();

        WebApplication app = builder.Build();

        // One catch-all for every method and path on both listeners, and no middleware before it.
        RequestDelegate handler = stub.HandleAsync;
        app.Map("/{**path}", handler);

        try
        {
            await app.StartAsync(ct);

            (int restPort, int soapPort) = ResolvePorts(app, restListen, soapListen);
            if (restPort <= 0 || soapPort <= 0 || restPort == soapPort)
            {
                throw new InvalidOperationException("stub backend did not bind two distinct loopback ports");
            }

            // No request can arrive before this point in practice: no caller knows the ports yet.
            stub.RestPort = restPort;
            stub.SoapPort = soapPort;
            stub._app = app;
            return stub;
        }
        catch
        {
            await StopAndDisposeAsync(app);
            throw;
        }
    }

    /// <summary>
    /// Returns a loopback port that was free a moment ago and has been released again, so a connection
    /// to it is refused. Also usable to draw free ports for other listeners.
    /// </summary>
    public static int GetClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Stops both listeners within about two seconds, aborting calls still in flight, and releases the
    /// host. Safe to call more than once; never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        WebApplication? app = _app;
        _app = null;
        if (app is not null)
        {
            await StopAndDisposeAsync(app);
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        StubListener listener = context.Connection.LocalPort == RestPort ? StubListener.Rest : StubListener.Soap;
        string method = context.Request.Method;
        string rawTarget = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        Dictionary<string, string[]> headers = CopyHeaders(context.Request.Headers);

        // The response is fixed before the body is read or the delay starts, so a call the facade aborts
        // is still recorded with the answer it would have received.
        byte[] responseBody = _body ?? Encoding.UTF8.GetBytes($"stub {Id} {listener} {method} {rawTarget}");

        using var body = new MemoryStream();
        bool aborted = false;
        try
        {
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The caller reset the connection or Kestrel rejected the upload; keep what arrived.
            aborted = true;
        }

        lock (_gate)
        {
            _received.Add(new StubRequest(listener, method, rawTarget, headers, body.ToArray(), _status, responseBody));
        }

        if (aborted)
        {
            return;
        }

        if (_delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(_delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The facade gave up (its deadline passed) or the stub is stopping: answer nothing.
                return;
            }
        }

        HttpResponse response = context.Response;
        response.StatusCode = _status;
        response.Headers[StubIdHeader] = Id;
        response.ContentType = DefaultContentType;
        foreach ((string name, string value) in _extraHeaders)
        {
            response.Headers[name] = value;
        }

        if (_status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
        {
            return;
        }

        // An explicit length keeps Kestrel from choosing chunked transfer coding; a HEAD response
        // declares the length and carries no body.
        response.ContentLength = responseBody.Length;
        if (!HttpMethods.IsHead(method))
        {
            await response.Body.WriteAsync(responseBody);
        }
    }

    private static Dictionary<string, string[]> CopyHeaders(IHeaderDictionary source)
    {
        var copy = new Dictionary<string, string[]>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, var values) in source)
        {
            var copied = new string[values.Count];
            for (int i = 0; i < copied.Length; i++)
            {
                copied[i] = values[i] ?? string.Empty;
            }

            copy[name] = copied;
        }

        return copy;
    }

    // Kestrel writes each bound endpoint back into its ListenOptions when binding port 0. The server
    // addresses feature lists the same bindings in listen order and covers the case where it does not.
    private static (int RestPort, int SoapPort) ResolvePorts(WebApplication app, ListenOptions? restListen, ListenOptions? soapListen)
    {
        int rest = restListen?.IPEndPoint?.Port ?? 0;
        int soap = soapListen?.IPEndPoint?.Port ?? 0;
        if (rest > 0 && soap > 0)
        {
            return (rest, soap);
        }

        ICollection<string>? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        int[] ports = addresses?
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri.Port : 0)
            .ToArray() ?? [];
        return ports.Length == 2 ? (ports[0], ports[1]) : (0, 0);
    }

    private static string LoopbackUrl(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");

    private static async Task StopAndDisposeAsync(WebApplication app)
    {
        using var deadline = new CancellationTokenSource(StopTimeout);
        try
        {
            await app.StopAsync(deadline.Token);
        }
        catch (Exception)
        {
            // Disposal is best effort and must not replace the outcome of the test that owns the stub;
            // the host is disposed below whether or not it stopped cleanly.
        }

        try
        {
            await app.DisposeAsync();
        }
        catch (Exception)
        {
            // As above: a failure to release the host never fails the owning test.
        }
    }
}
