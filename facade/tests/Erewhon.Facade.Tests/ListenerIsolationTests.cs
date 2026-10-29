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
// and never asserted on or printed; every assertion compares fixed values only.

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
/// socket's unmatched requests reach that socket's own catch-all.
/// </summary>
/// <remarks>
/// Every test gets its own facade and stubs. Target <c>backend</c> (the default target) is stub A and
/// target <c>stub</c> is stub B, with only REST <c>getCompany</c> routed to <c>stub</c>, so a request that
/// reaches the <c>getCompany</c> endpoint is observable at B and anything a catch-all forwards arrives at A.
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

        RawResponse response = await SendAsync(baseAddress, new HttpMethod(method), path, HostFor("admin-port"));

        // The socket's catch-all forwarded the request unchanged to the default target on the same surface.
        StubRequest forwarded = Assert.Single(Backend.Received);
        Assert.Equal(listener, forwarded.Listener);
        Assert.Equal(method, forwarded.Method);
        Assert.Equal(path, forwarded.RawTarget);
        Assert.Empty(Reroute.Received);

        // The answer is stub A's, not the shutdown endpoint's 202 nor an OpenAPI document.
        Assert.Equal(200, response.Status);
        Assert.Equal(BackendStubId, response.Header(StubIdHeader));
        Assert.Equal(forwarded.ResponseBody, response.Body);

        // The facade is still serving on both the admin and the REST sockets.
        Backend.ClearLog();
        Reroute.ClearLog();

        RawResponse health = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.Health, host: null);
        Assert.Equal(200, health.Status);

        RawResponse company = await SendAsync(GetCompanyRequest, Facade.RestBaseAddress, host: null);
        Assert.Equal(200, company.Status);
        Assert.Equal(RerouteStubId, company.Header(StubIdHeader));
        StubRequest dispatched = Assert.Single(Reroute.ReceivedOn(StubListener.Rest));
        Assert.Equal(GetCompanyRequest.PathAndQuery, dispatched.RawTarget);
        Assert.Empty(Backend.Received);

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
        RawResponse response = await SendAsync(GetCompanyRequest, Facade.RestBaseAddress, HostFor(hostVariant));

        // Only the getCompany endpoint routes to stub B; any catch-all would have forwarded to stub A.
        StubRequest dispatched = Assert.Single(Reroute.Received);
        Assert.Equal(StubListener.Rest, dispatched.Listener);
        Assert.Equal(GetCompanyRequest.Method.Method, dispatched.Method);
        Assert.Equal(GetCompanyRequest.PathAndQuery, dispatched.RawTarget);
        Assert.Empty(Backend.Received);

        Assert.Equal(200, response.Status);
        Assert.Equal(RerouteStubId, response.Header(StubIdHeader));
    }

    [Fact]
    public async Task RestPathOnSoapSocketIsHandledBySoapCatchAll()
    {
        RawResponse response = await SendAsync(GetCompanyRequest, Facade.SoapBaseAddress, host: null);

        // The REST getCompany endpoint would have routed to stub B; the SOAP catch-all forwards to the
        // default target's SOAP base URL.
        StubRequest forwarded = Assert.Single(Backend.Received);
        Assert.Equal(StubListener.Soap, forwarded.Listener);
        Assert.Equal(GetCompanyRequest.Method.Method, forwarded.Method);
        Assert.Equal(GetCompanyRequest.PathAndQuery, forwarded.RawTarget);
        Assert.Empty(Reroute.Received);

        Assert.Equal(200, response.Status);
        Assert.Equal(BackendStubId, response.Header(StubIdHeader));
    }

    [Fact]
    public async Task HealthAnswersOnAdminSocket()
    {
        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, AdminRoutes.Health, host: null);

        Assert.Equal(200, response.Status);
        Assert.Equal(HealthyBody, response.BodyText);
        AssertNothingForwarded();
    }

    [Fact]
    public async Task OpenApiDocumentAnswersOnAdminSocket()
    {
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

        AssertNothingForwarded();
    }

    [Fact]
    public async Task AdminInfoAnswersOnAdminSocket()
    {
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

        AssertNothingForwarded();
    }

    // "/company" is a catalogue REST path: on the admin socket it is not an operation.
    [Theory]
    [InlineData("/not-an-admin-path")]
    [InlineData("/company")]
    public async Task UnknownAdminPathReturns404AndForwardsNothing(string path)
    {
        RawResponse response = await SendAsync(Facade.AdminBaseAddress, HttpMethod.Get, path, host: null);

        Assert.Equal(404, response.Status);
        Assert.Empty(response.Body);
        AssertNothingForwarded();
    }

    private void AssertNothingForwarded()
    {
        Assert.Empty(Backend.Received);
        Assert.Empty(Reroute.Received);
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
