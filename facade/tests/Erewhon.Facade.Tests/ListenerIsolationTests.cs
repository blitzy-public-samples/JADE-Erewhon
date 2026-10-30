// Listener isolation of the facade: every endpoint is dispatched by the socket a request arrived on
// (ListenerPortMatcherPolicy compares the endpoint's listener port with HttpContext.Connection.LocalPort),
// never by the caller-controlled Host header. The admin endpoints are unauthenticated and contained only
// by that isolation and by network placement, so a request on the REST or SOAP socket that names an admin
// path and forges the admin port in Host must reach that listener's catch-all and be forwarded to the
// backend, never answered by the admin endpoint.
//
// Evidence comes only from real sockets: FacadeFactory hosts the facade on three loopback Kestrel
// listeners, requests go through its raw client to RestBaseAddress, SoapBaseAddress or AdminBaseAddress,
// and each stub records the listener a request arrived on from its own accepting socket. The in-memory
// TestServer reports a local port of 0, which matches no listener, so it can never show isolation.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message. Forged Host values carry run-time ports, so they are built per test
// and never asserted on or printed. Every message is built only from fixed values (stub ids, sockets,
// listener names, methods, paths and Host variants); observed counts and lengths are never printed.
// That is also why no Assert.Single, Assert.Empty or Assert.Equal is applied to recorded StubRequest
// data, its fields included: the dump would print observed values, among them the Host header's port.

using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Shows on real Kestrel listeners that admin paths are served only on the admin socket, that REST
/// operations are dispatched on the REST socket whatever the <c>Host</c> header says, and that each public
/// socket's unmatched requests reach that socket's own catch-all. Admin-socket responses carry
/// <c>X-Content-Type-Options: nosniff</c>; responses forwarded from a public socket carry none.
/// </summary>
/// <remarks>
/// <para>
/// Every test gets its own facade and stubs. Target <c>backend</c> (the default target) is stub A and
/// target <c>stub</c> is stub B, with only REST <c>getCompany</c> routed to <c>stub</c>. On the REST
/// socket, whichever endpoint routing selects, a request is forwarded to the target of the operation
/// <c>RestOperationMatcher</c> resolves from its verb and raw path, and to the default target when it
/// resolves to none; the SOAP catch-all forwards to the default target. So <c>GET /company</c> on the REST
/// socket resolves to <c>getCompany</c> and arrives at B, while the admin paths sent to the REST and SOAP
/// sockets, and <c>GET /company</c> sent to the SOAP socket, match no operation on that surface and
/// arrive at A.
/// </para>
/// <para>
/// The stubs never send <c>X-Content-Type-Options</c>, so on a forwarded response any value would be the
/// facade's own.
/// </para>
/// </remarks>
public sealed class ListenerIsolationTests : IAsyncLifetime
{
    private const string BackendStubId = "A";
    private const string RerouteStubId = "B";
    private const string StubIdHeader = "X-Stub-Id";

    // The routing target name FacadeTestSettings defines from StubRestBaseUrl and StubSoapBaseUrl.
    private const string RerouteTarget = "stub";

    // The host name of every forged Host value; the port, when present, is a facade listener's.
    private const string ForgedHostName = "localhost";

    // The admin health body, as Admin/AdminEndpoints.cs writes it.
    private const string HealthyBody = "ok";

    // The header Admin/AdminEndpoints.cs adds to every admin-socket response, and its only value.
    private const string ContentTypeOptionsHeader = "X-Content-Type-Options";
    private const string NoSniff = "nosniff";

    // A document name the facade does not register; the OpenAPI handler reads it from the query string.
    private const string UnknownOpenApiDocumentQuery = "?documentName=not-a-document";

    private const string JsonMediaType = "application/json";
    private const string OpenApiVersionMember = "openapi";
    private const string FrameworkDescriptionMember = "frameworkDescription";

    // The only rerouted operation; its request (verb and path) comes from the catalogue.
    private static readonly OperationDescriptor GetCompany = OperationCatalogue.Default.Find(Surface.Rest, "getCompany");
    private static readonly PreparedRequest GetCompanyRequest = OperationRequests.ForRest(GetCompany);

    private StubBackend? _backend;
    private StubBackend? _reroute;
    private FacadeFactory? _facade;
    private HttpClient? _client;

    private StubBackend Backend => _backend ?? throw new InvalidOperationException("listener isolation fixture is not initialised");

    private StubBackend Reroute => _reroute ?? throw new InvalidOperationException("listener isolation fixture is not initialised");

    private FacadeFactory Facade => _facade ?? throw new InvalidOperationException("listener isolation fixture is not initialised");

    private HttpClient Client => _client ?? throw new InvalidOperationException("listener isolation fixture is not initialised");

    /// <summary>Starts stub A, stub B and then the facade, which must never start concurrently with a stub.</summary>
    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            _backend = await StubBackend.StartAsync(BackendStubId, ct: ct);
            _reroute = await StubBackend.StartAsync(RerouteStubId, ct: ct);
            _facade = await FacadeFactory.StartAsync(
                new FacadeTestSettings
                {
                    BackendRestBaseUrl = _backend.RestBaseUrl,
                    BackendSoapBaseUrl = _backend.SoapBaseUrl,
                    StubRestBaseUrl = _reroute.RestBaseUrl,
                    StubSoapBaseUrl = _reroute.SoapBaseUrl,
                    Settings = { ["Routing:Operations:Rest:" + GetCompany.RoutingKey] = RerouteTarget },
                },
                ct);
            _client = _facade.CreateRawClient();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Disposes the client, the facade, stub B and stub A, each attempted even when an earlier one fails;
    /// the first failure is rethrown afterwards. Safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        HttpClient? client = Interlocked.Exchange(ref _client, null);
        FacadeFactory? facade = Interlocked.Exchange(ref _facade, null);
        StubBackend? reroute = Interlocked.Exchange(ref _reroute, null);
        StubBackend? backend = Interlocked.Exchange(ref _backend, null);
        var failures = new List<Exception>();

        try
        {
            client?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (facade is not null)
            {
                await facade.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (reroute is not null)
            {
                await reroute.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (backend is not null)
            {
                await backend.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }
    }

    [Theory]
    [InlineData("Rest", "GET", AdminRoutes.OpenApiDocument)]
    [InlineData("Rest", "POST", AdminRoutes.Shutdown)]
    [InlineData("Soap", "GET", AdminRoutes.OpenApiDocument)]
    [InlineData("Soap", "POST", AdminRoutes.Shutdown)]
    public async Task AdminPathOnPublicSocketWithForgedHostReachesCatchAll(string socket, string method, string path)
    {
        (Uri baseAddress, StubListener listener) = PublicSocket(socket);
        string label = $"{socket} socket {method} {path} with forged admin Host";

        RawResponse response = await SendAsync(baseAddress, new HttpMethod(method), path, HostFor("admin-port"));

        // The socket's catch-all forwarded the request unchanged to the default target on the same surface.
        StubRequest forwarded = SingleRequest(Backend, label);
        AssertRequest(forwarded, Backend, listener, method, path, label);
        AssertNoRequests(Reroute, label);

        // The answer is stub A's, not the shutdown endpoint's 202 nor an OpenAPI document.
        Assert.Equal(200, response.Status);
        Assert.Equal(BackendStubId, response.Header(StubIdHeader));
        Assert.True(
            response.Body.AsSpan().SequenceEqual(forwarded.ResponseBody),
            $"{label}: facade response body differs from the bytes stub {Backend.Id} sent");
        AssertNoContentTypeOptions(response, label);

        // The facade is still serving on both the admin and the REST sockets.
        Backend.ClearLog();
        Reroute.ClearLog();

        RawResponse health = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.Health, host: null);
        Assert.Equal(200, health.Status);
        AssertNoSniff(health, $"{label}, then admin socket GET {AdminRoutes.Health}");

        string followUp = $"{label}, then REST socket {GetCompanyRequest.Method.Method} {GetCompanyRequest.PathAndQuery}";
        RawResponse company = await SendAsync(GetCompanyRequest, Facade.RestBaseAddress, host: null);
        Assert.Equal(200, company.Status);
        Assert.Equal(RerouteStubId, company.Header(StubIdHeader));
        AssertNoContentTypeOptions(company, followUp);
        StubRequest dispatched = SingleRequest(Reroute, followUp, StubListener.Rest);
        AssertRequest(dispatched, Reroute, StubListener.Rest, GetCompanyRequest.Method.Method, GetCompanyRequest.PathAndQuery, followUp);
        AssertNoRequests(Backend, followUp);

        IHostApplicationLifetime lifetime = Facade.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(
            lifetime.ApplicationStopping.IsCancellationRequested,
            "facade began stopping after an admin path arrived on a public socket");
    }

    // "rest-port" is the control case: the Host header names the socket the request really arrived on.
    [Theory]
    [InlineData("no-port")]
    [InlineData("admin-port")]
    [InlineData("soap-port")]
    [InlineData("rest-port")]
    public async Task RestOperationOnRestSocketIsDispatchedWhateverTheHost(string hostVariant)
    {
        string label = $"REST socket {GetCompanyRequest.Method.Method} {GetCompanyRequest.PathAndQuery} with Host variant {hostVariant}";

        RawResponse response = await SendAsync(GetCompanyRequest, Facade.RestBaseAddress, HostFor(hostVariant));

        // Arriving at stub B shows the REST listener handled the request and resolved it to getCompany, the
        // only operation routed there: the SOAP catch-all would have forwarded it to stub A, and the admin
        // catch-all forwards nothing.
        StubRequest dispatched = SingleRequest(Reroute, label);
        AssertRequest(dispatched, Reroute, StubListener.Rest, GetCompanyRequest.Method.Method, GetCompanyRequest.PathAndQuery, label);
        AssertNoRequests(Backend, label);

        Assert.Equal(200, response.Status);
        Assert.Equal(RerouteStubId, response.Header(StubIdHeader));

        // The admin-port variant shows the header follows the socket, never the Host header.
        AssertNoContentTypeOptions(response, label);
    }

    [Fact]
    public async Task RestPathOnSoapSocketIsHandledBySoapCatchAll()
    {
        string label = $"SOAP socket {GetCompanyRequest.Method.Method} {GetCompanyRequest.PathAndQuery}";

        RawResponse response = await SendAsync(GetCompanyRequest, Facade.SoapBaseAddress, host: null);

        // On the REST socket this request resolves to getCompany and would reach stub B; the SOAP socket has
        // no operation at this path, so its catch-all forwards it to the default target's SOAP base URL.
        StubRequest forwarded = SingleRequest(Backend, label);
        AssertRequest(forwarded, Backend, StubListener.Soap, GetCompanyRequest.Method.Method, GetCompanyRequest.PathAndQuery, label);
        AssertNoRequests(Reroute, label);

        Assert.Equal(200, response.Status);
        Assert.Equal(BackendStubId, response.Header(StubIdHeader));
        AssertNoContentTypeOptions(response, label);
    }

    [Fact]
    public async Task HealthAnswersOnAdminSocket()
    {
        string label = $"admin socket GET {AdminRoutes.Health}";

        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.Health, host: null);

        Assert.Equal(200, response.Status);
        Assert.Equal(HealthyBody, response.BodyText);
        AssertNoSniff(response, label);
        AssertNothingForwarded(label);
    }

    [Fact]
    public async Task OpenApiDocumentAnswersOnAdminSocket()
    {
        string label = $"admin socket GET {AdminRoutes.OpenApiDocument}";

        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.OpenApiDocument, host: null);

        Assert.Equal(200, response.Status);
        Assert.Equal(JsonMediaType, response.MediaType);
        using (JsonDocument document = JsonDocument.Parse(response.Body))
        {
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(
                document.RootElement.TryGetProperty(OpenApiVersionMember, out JsonElement version)
                    && version.ValueKind == JsonValueKind.String,
                "OpenAPI document has no top-level openapi string member");
        }

        AssertNoSniff(response, label);

        // The framework's OpenAPI handler writes its own 404 for a document name it does not know.
        string unknownLabel = $"{label} for an unknown document name";
        RawResponse unknown = await SendAsync(
            Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.OpenApiDocument + UnknownOpenApiDocumentQuery, host: null);
        Assert.Equal(404, unknown.Status);
        AssertNoSniff(unknown, unknownLabel);

        AssertNothingForwarded(label);
    }

    [Fact]
    public async Task AdminInfoAnswersOnAdminSocket()
    {
        string label = $"admin socket GET {AdminRoutes.Info}";

        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.Info, host: null);

        Assert.Equal(200, response.Status);
        Assert.Equal(JsonMediaType, response.MediaType);
        using (JsonDocument document = JsonDocument.Parse(response.Body))
        {
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(
                document.RootElement.TryGetProperty(FrameworkDescriptionMember, out JsonElement framework)
                    && framework.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(framework.GetString()),
                "admin info has no non-empty frameworkDescription string member");
        }

        AssertNoSniff(response, label);
        AssertNothingForwarded(label);
    }

    // "/company" is a catalogue REST path: on the admin socket it is not an operation.
    [Theory]
    [InlineData("/not-an-admin-path")]
    [InlineData("/company")]
    public async Task UnknownAdminPathReturns404AndForwardsNothing(string path)
    {
        string label = $"admin socket GET {path}";

        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, path, host: null);

        Assert.Equal(404, response.Status);
        Assert.True(
            response.Body.Length == 0,
            $"{label}: admin catch-all answered 404 with a non-empty body, expected an empty body");
        AssertNoSniff(response, label);
        AssertNothingForwarded(label);
    }

    private void AssertNothingForwarded(string label)
    {
        AssertNoRequests(Backend, label);
        AssertNoRequests(Reroute, label);
    }

    // Asserts that an admin-socket response carries X-Content-Type-Options once, with exactly the value
    // nosniff; the message names only the expected header.
    private static void AssertNoSniff(RawResponse response, string label)
    {
        Assert.True(
            response.Headers.TryGetValue(ContentTypeOptionsHeader, out string[]? values)
                && values.Length == 1
                && string.Equals(response.Header(ContentTypeOptionsHeader), NoSniff, StringComparison.Ordinal),
            $"{label}: response does not carry {ContentTypeOptionsHeader}: {NoSniff} exactly once");
    }

    // Asserts that a response forwarded from a public socket carries no X-Content-Type-Options: the facade
    // adds the header only on the admin socket and passes the stub's headers through unchanged.
    private static void AssertNoContentTypeOptions(RawResponse response, string label)
    {
        Assert.True(
            response.Header(ContentTypeOptionsHeader) is null,
            $"{label}: forwarded response carries {ContentTypeOptionsHeader}, expected none");
    }

    // Asserts that the stub recorded exactly one request, on any listener or only on the one given, and
    // returns it. The snapshot is read once, so the list returned from is the list that was checked.
    private static StubRequest SingleRequest(StubBackend stub, string label, StubListener? listener = null)
    {
        IReadOnlyList<StubRequest> received = listener is { } only ? stub.ReceivedOn(only) : stub.Received;
        string where = listener is { } on ? $" on its {on} listener" : string.Empty;
        Assert.True(
            received.Count == 1,
            $"{label}: stub {stub.Id} did not receive exactly one request{where}");
        return received[0];
    }

    private static void AssertNoRequests(StubBackend stub, string label)
    {
        int count = stub.Received.Count;
        Assert.True(count == 0, $"{label}: stub {stub.Id} received an unexpected request, expected none");
    }

    // Asserts that a recorded request arrived on the expected listener with the caller's method and raw
    // target unchanged; each message names only the expected value.
    private static void AssertRequest(
        StubRequest request,
        StubBackend stub,
        StubListener listener,
        string method,
        string rawTarget,
        string label)
    {
        Assert.True(
            request.Listener == listener,
            $"{label}: stub {stub.Id} did not receive the request on its {listener} listener");
        Assert.True(
            string.Equals(request.Method, method, StringComparison.Ordinal),
            $"{label}: stub {stub.Id} did not receive method {method}");
        Assert.True(
            string.Equals(request.RawTarget, rawTarget, StringComparison.Ordinal),
            $"{label}: stub {stub.Id} did not receive the request target {rawTarget} unchanged");
    }

    private (Uri BaseAddress, StubListener Listener) PublicSocket(string socket) => socket switch
    {
        "Rest" => (Facade.RestBaseAddress, StubListener.Rest),
        "Soap" => (Facade.SoapBaseAddress, StubListener.Soap),
        _ => throw new ArgumentOutOfRangeException(nameof(socket), "socket must be Rest or Soap"),
    };

    // A Host value for the REST socket: no port, or the port of one of the facade's listeners.
    private string HostFor(string variant) => variant switch
    {
        "no-port" => ForgedHostName,
        "admin-port" => HostWithPort(Facade.AdminPort),
        "soap-port" => HostWithPort(Facade.SoapPort),
        "rest-port" => HostWithPort(Facade.RestPort),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), "unknown Host variant"),
    };

    private static string HostWithPort(int port) =>
        ForgedHostName + ":" + port.ToString(CultureInfo.InvariantCulture);

    // An HTTP/1.1 request to one facade socket. A POST carries an empty body, so it is sent with
    // Content-Length: 0; a non-null host replaces the Host header the base address would give.
    private Task<RawResponse> SendAsync(Uri baseAddress, HttpMethod method, string pathAndQuery, string? host)
    {
        var request = new HttpRequestMessage(method, new Uri(baseAddress, pathAndQuery))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        if (method == HttpMethod.Post)
        {
            request.Content = new ByteArrayContent([]);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return FacadeFactory.SendAsync(Client, request, TestContext.Current.CancellationToken);
    }

    private Task<RawResponse> SendAsync(PreparedRequest prepared, Uri baseAddress, string? host) =>
        FacadeFactory.SendAsync(Client, prepared.ToMessage(baseAddress, host), TestContext.Current.CancellationToken);
}
