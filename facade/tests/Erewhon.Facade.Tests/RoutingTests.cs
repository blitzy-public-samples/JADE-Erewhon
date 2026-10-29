// Per-operation routing (class RoutingTests) through the facade's real REST and SOAP sockets. Each case
// moves exactly one operation to a second target by changing one
// Routing:Operations:<Rest|Soap>:<jadeMethod> key, calls all 22 catalogue operations at their normal
// addresses, and asserts that the moved operation alone reaches stub B, through the same URL and with
// B's response unchanged, while the other 21 still reach stub A, the default "backend" target. REST and
// SOAP each get a case because the facade matches them differently: REST by verb and path, SOAP by the
// SOAPAction header and then by the request body.
//
// Request-line pass-through (class RequestLinePassThroughTests) adds a third stub C as
// Routing:DefaultTarget, distinct from the backend A, and sends each request over a raw socket so that
// its request line reaches the facade byte for byte. Its raw-target, method-token and asterisk-form
// cases prove that the shared RestOperationMatcher, applied to the raw path alone, decides which target
// a REST request reaches, whichever endpoint ASP.NET Core routing selected for it, and that the target
// receives the caller's method token and request target unchanged, whatever their form. They are a
// class of their own because the results report counts every test class whose name ends in
// "RoutingTests" as the per-operation routing check, which is the two reroute cases alone.
//
// Base-URL credential handling (class BaseUrlCredentialTests) proves, in-process, that a routing
// target's base URL carrying user information is rejected and never echoed. It is a separate class for
// the same reason: the results report counts every test class whose name ends in "RoutingTests" as the
// two-case per-operation routing check.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every message here is built only from fixed values: operation ids,
// stub ids, listener names, request targets and status codes. No port, URL, elapsed time or GUID ever
// reaches a message, which is also why no Assert.Single, Assert.Empty or Assert.Equal is applied to a
// recorded StubRequest: its dump would print the Host header and with it a port.

using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Admin;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Proves that one routing key moves one operation and nothing else (the per-operation routing check of
/// the results report), on the facade hosted on real loopback Kestrel listeners.
/// </summary>
public sealed class RoutingTests
{
    // The internal constants are shared with RequestLinePassThroughTests, whose setup uses the same
    // stubs, rerouted target and headers.
    internal const string BackendStubId = "A";
    internal const string RerouteStubId = "B";
    internal const string RerouteTargetName = "stub";
    internal const string StubIdHeader = "X-Stub-Id";
    private const string SoapActionHeader = "SOAPAction";
    private const string ContentTypeHeader = "Content-Type";
    internal const string ForwardedHeaderPrefix = "X-Forwarded-";

    // The 22 operations carried over from ErewhonRestService and ErewhonWebService.
    private const int RestOperationCount = 13;
    private const int SoapOperationCount = 9;

    private const int BackendStatus = 200;

    // Distinct from the backend stub's 200, so a response that came from the wrong stub cannot pass.
    internal const int RerouteStatus = 203;

    /// <summary>
    /// Routes <paramref name="operation"/> of <paramref name="surface"/> to target <c>stub</c> (stub B)
    /// while target <c>backend</c> is stub A, then calls every catalogue operation once.
    /// </summary>
    /// <param name="surface">The rerouted operation's surface, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="operation">The rerouted operation's JADE method name, its routing key.</param>
    [Theory]
    [InlineData("Rest", "getCompany")]
    [InlineData("Soap", "getAgentNames")]
    public async Task ReroutingOneOperationAffectsOnlyThatOperation(string surface, string operation)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        OperationCatalogue catalogue = OperationCatalogue.Default;
        OperationDescriptor rerouted = catalogue.Find(Enum.Parse<Surface>(surface), operation);
        IReadOnlyList<OperationDescriptor> restOperations = catalogue.GetBySurface(Surface.Rest);
        IReadOnlyList<OperationDescriptor> soapOperations = catalogue.GetBySurface(Surface.Soap);
        Assert.Equal(RestOperationCount, restOperations.Count);
        Assert.Equal(SoapOperationCount, soapOperations.Count);

        // Both stubs start before the facade: the factory must not run while another host is built.
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using StubBackend stub = await StubBackend.StartAsync(
            RerouteStubId, new StubBackendOptions { Status = RerouteStatus }, ct);

        // Exactly one routing key differs from the shipped table, in the documented key form.
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = backend.RestBaseUrl,
                BackendSoapBaseUrl = backend.SoapBaseUrl,
                StubRestBaseUrl = stub.RestBaseUrl,
                StubSoapBaseUrl = stub.SoapBaseUrl,
                Settings = { [$"Routing:Operations:{rerouted.Surface}:{rerouted.RoutingKey}"] = RerouteTargetName },
            },
            ct);

        using HttpClient client = facade.CreateRawClient();

        int reroutedCalls = 0;
        foreach (OperationDescriptor op in restOperations.Concat(soapOperations))
        {
            backend.ClearLog();
            stub.ClearLog();

            // The rerouted operation is sent exactly as every other one: same socket, same request.
            PreparedRequest prepared = OperationRequests.For(op);
            RawResponse response = await FacadeFactory.SendAsync(client, prepared.ToMessage(SocketFor(facade, op)), ct);

            bool isRerouted = string.Equals(op.Id, rerouted.Id, StringComparison.Ordinal);
            if (isRerouted)
            {
                reroutedCalls++;
                AssertForwardedUnchanged(op.Id, prepared, response, reached: stub, bypassed: backend, RerouteStatus);
            }
            else
            {
                AssertForwardedUnchanged(op.Id, prepared, response, reached: backend, bypassed: stub, BackendStatus);
            }
        }

        Assert.True(reroutedCalls == 1, $"{rerouted.Id}: the rerouted operation was not called exactly once among the catalogue operations");

        if (rerouted.Surface == Surface.Soap)
        {
            // Without a SOAPAction header only the facade's bounded body inspection can name the
            // operation; an unresolved request would go to the default target, stub A.
            backend.ClearLog();
            stub.ClearLog();

            PreparedRequest bodyOnly = OperationRequests.ForSoap(rerouted, includeSoapAction: false);
            RawResponse response = await FacadeFactory.SendAsync(client, bodyOnly.ToMessage(facade.SoapBaseAddress), ct);
            AssertForwardedUnchanged(
                rerouted.Id + " without SOAPAction", bodyOnly, response, reached: stub, bypassed: backend, RerouteStatus);
        }
    }

    private static Uri SocketFor(FacadeFactory facade, OperationDescriptor op) =>
        op.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;

    private static StubListener ListenerFor(OperationDescriptor op) =>
        op.Surface == Surface.Rest ? StubListener.Rest : StubListener.Soap;

    // Asserts that the call reached 'reached' once and 'bypassed' never, on the listener of the
    // operation's surface, with the caller's method, raw target, body, Content-Type and SOAPAction
    // unchanged and no X-Forwarded-* header added, and that the facade returned the stub's own status,
    // X-Stub-Id and body bytes.
    private static void AssertForwardedUnchanged(
        string label,
        PreparedRequest prepared,
        RawResponse response,
        StubBackend reached,
        StubBackend bypassed,
        int expectedStatus)
    {
        IReadOnlyList<StubRequest> received = reached.Received;
        int bypassedCount = bypassed.Received.Count;
        Assert.True(
            received.Count == 1,
            $"{label}: stub {reached.Id} received {received.Count} requests, expected exactly 1");
        Assert.True(
            bypassedCount == 0,
            $"{label}: stub {bypassed.Id} received {bypassedCount} requests, expected none");

        StubRequest request = received[0];
        StubListener expectedListener = ListenerFor(prepared.Operation);
        Assert.True(
            request.Listener == expectedListener,
            $"{label}: stub {reached.Id} received the call on its {request.Listener} listener, expected {expectedListener}");
        Assert.True(
            string.Equals(request.Method, prepared.Method.Method, StringComparison.Ordinal),
            $"{label}: stub {reached.Id} received method {request.Method}, expected {prepared.Method.Method}");
        Assert.True(
            string.Equals(request.RawTarget, prepared.PathAndQuery, StringComparison.Ordinal),
            $"{label}: stub {reached.Id} did not receive the request target {prepared.PathAndQuery} unchanged");
        Assert.True(
            request.Body.AsSpan().SequenceEqual(prepared.Body ?? []),
            $"{label}: stub {reached.Id} did not receive the request body bytes unchanged");

        if (prepared.ContentType is not null)
        {
            Assert.True(
                string.Equals(request.Header(ContentTypeHeader), prepared.ContentType, StringComparison.Ordinal),
                $"{label}: stub {reached.Id} did not receive Content-Type {prepared.ContentType} unchanged");
        }

        if (prepared.SoapAction is not null)
        {
            Assert.True(
                string.Equals(request.Header(SoapActionHeader), prepared.SoapAction, StringComparison.Ordinal),
                $"{label}: stub {reached.Id} did not receive SOAPAction {prepared.SoapAction} unchanged, quotes included");
        }
        else
        {
            Assert.True(
                request.Header(SoapActionHeader) is null,
                $"{label}: stub {reached.Id} received a SOAPAction header the caller did not send");
        }

        Assert.False(
            request.Headers.Keys.Any(name => name.StartsWith(ForwardedHeaderPrefix, StringComparison.OrdinalIgnoreCase)),
            $"{label}: stub {reached.Id} received an X-Forwarded-* header the caller did not send");

        Assert.True(
            response.Status == expectedStatus,
            $"{label}: facade answered status {response.Status}, expected stub {reached.Id}'s status {expectedStatus}");
        string? stubId = response.Header(StubIdHeader);
        Assert.True(
            string.Equals(stubId, reached.Id, StringComparison.Ordinal),
            $"{label}: facade response carried X-Stub-Id {stubId ?? "(absent)"}, expected {reached.Id}");
        Assert.True(
            response.Body.AsSpan().SequenceEqual(request.ResponseBody),
            $"{label}: facade response body differs from the bytes stub {reached.Id} sent");
    }
}

/// <summary>
/// Proves, on the facade hosted on real loopback Kestrel listeners, that the operation a REST request
/// is, and the method token and request target its target receives, follow from the caller's raw request
/// line alone: raw-target dispatch, the method token as sent, and the asterisk-form <c>OPTIONS *</c>.
/// </summary>
public sealed class RequestLinePassThroughTests
{
    // The stubs, rerouted target and headers of the per-operation routing cases.
    private const string BackendStubId = RoutingTests.BackendStubId;
    private const string RerouteStubId = RoutingTests.RerouteStubId;
    private const string RerouteTargetName = RoutingTests.RerouteTargetName;
    private const string StubIdHeader = RoutingTests.StubIdHeader;
    private const string ForwardedHeaderPrefix = RoutingTests.ForwardedHeaderPrefix;
    private const int RerouteStatus = RoutingTests.RerouteStatus;

    // The default target of the raw-target cases, distinct from the backend and from the rerouted stub.
    private const string DefaultStubId = "C";
    private const string DefaultTargetName = "dflt";
    private const int DefaultStatus = 202;

    private const string HostHeader = "Host";
    private const string AbsoluteForm = "absolute";

    /// <summary>
    /// With <c>getCompany</c> routed to stub B and a default target C distinct from the backend A, sends
    /// one <c>GET</c> to the REST socket and asserts that only the stub the shared matcher's verdict on the
    /// raw path names receives it, with the raw request target unchanged and its own authority as
    /// <c>Host</c>. Case variants, escaped literals, dot segments, trailing slashes and bare suffixes select
    /// an operation endpoint in ASP.NET Core routing but are no catalogue operation, so they reach C.
    /// </summary>
    /// <param name="form">
    /// <c>origin</c> to send <paramref name="target"/> as the request target, or <c>absolute</c> to send it
    /// after <c>http://</c> and the facade's REST authority, with a matching <c>Host</c>.
    /// </param>
    /// <param name="target">The origin-form target, or what follows the authority in absolute form.</param>
    /// <param name="expectedStubId">The stub that must receive the request: A, B or C.</param>
    /// <param name="expectedRawTarget">The request target that stub must receive.</param>
    [Theory]
    [InlineData("origin", "/company", RerouteStubId, "/company")]
    [InlineData("origin", "/company.json", RerouteStubId, "/company.json")]
    [InlineData("origin", "/COMPANY", DefaultStubId, "/COMPANY")]
    [InlineData("origin", "/Company", DefaultStubId, "/Company")]
    [InlineData("origin", "/company/", DefaultStubId, "/company/")]
    [InlineData("origin", "/compan%79", DefaultStubId, "/compan%79")]
    [InlineData("origin", "/%63ompany", DefaultStubId, "/%63ompany")]
    [InlineData("origin", "/COMPANY.json", DefaultStubId, "/COMPANY.json")]
    [InlineData("origin", "/%63ompany.json", DefaultStubId, "/%63ompany.json")]
    [InlineData("origin", "/x/../company", DefaultStubId, "/x/../company")]
    [InlineData("origin", "/./company", DefaultStubId, "/./company")]
    [InlineData("origin", "/AGENT/Hank%20Williams", DefaultStubId, "/AGENT/Hank%20Williams")]
    [InlineData("origin", "/allagents", DefaultStubId, "/allagents")]
    [InlineData("origin", "/agent/.json", DefaultStubId, "/agent/.json")]
    [InlineData("origin", "/agent/Hank%20Williams", BackendStubId, "/agent/Hank%20Williams")]
    [InlineData(AbsoluteForm, "/company", RerouteStubId, "/company")]
    [InlineData(AbsoluteForm, "/%63ompany", DefaultStubId, "/%63ompany")]
    [InlineData(AbsoluteForm, "/agent/%48ank%20Williams?x=%2f", BackendStubId, "/agent/%48ank%20Williams?x=%2f")]
    [InlineData(AbsoluteForm, "?x=%2f", DefaultStubId, "/?x=%2f")]
    [InlineData(AbsoluteForm, "", DefaultStubId, "/")]
    public async Task RestRequestIsDispatchedByItsRawTarget(string form, string target, string expectedStubId, string expectedRawTarget)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawTargetSetup setup = await RawTargetSetup.StartAsync(ct);

        string authority = setup.Facade.RestBaseAddress.Authority;
        string requestTarget = string.Equals(form, AbsoluteForm, StringComparison.Ordinal) ? "http://" + authority + target : target;
        RawResponse response = await FacadeFactory.SendRawAsync(setup.Facade.RestPort, "GET " + requestTarget + " HTTP/1.1", authority, ct);

        AssertReachedOnly(
            $"{form} GET {target}", setup, expectedStubId, StubListener.Rest, "GET", expectedRawTarget, response);
    }

    /// <summary>
    /// Sends a method token in the letter case the caller chose, or a custom one, to the REST socket and
    /// asserts that the stub the request is routed to receives that exact token, with the target unchanged.
    /// The matcher compares the verb ignoring case, so <c>get /company</c> is still <c>getCompany</c>.
    /// </summary>
    /// <param name="method">The method token as sent.</param>
    /// <param name="target">The origin-form request target.</param>
    /// <param name="expectedStubId">The stub that must receive the request: A, B or C.</param>
    [Theory]
    [InlineData("get", "/company", RerouteStubId)]
    [InlineData("Get", "/company", RerouteStubId)]
    [InlineData("post", "/createAgent", BackendStubId)]
    [InlineData("get", "/unknown", DefaultStubId)]
    [InlineData("FOO", "/x", DefaultStubId)]
    public async Task MethodTokenReachesTheTargetAsSent(string method, string target, string expectedStubId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawTargetSetup setup = await RawTargetSetup.StartAsync(ct);

        RawResponse response = await FacadeFactory.SendRawAsync(
            setup.Facade.RestPort, method + " " + target + " HTTP/1.1", setup.Facade.RestBaseAddress.Authority, ct);

        AssertReachedOnly($"{method} {target}", setup, expectedStubId, StubListener.Rest, method, target, response);
    }

    /// <summary>
    /// Sends the asterisk-form <c>OPTIONS * HTTP/1.1</c> to the REST or SOAP socket and asserts that the
    /// default target C receives <c>OPTIONS *</c> on the listener of that surface, never a path such as
    /// <c>/</c>.
    /// </summary>
    /// <param name="socket">The facade socket the request is sent to, <c>Rest</c> or <c>Soap</c>.</param>
    [Theory]
    [InlineData("Rest")]
    [InlineData("Soap")]
    public async Task AsteriskFormOptionsReachesTheTargetAsSent(string socket)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawTargetSetup setup = await RawTargetSetup.StartAsync(ct);

        StubListener listener = Enum.Parse<StubListener>(socket);
        Uri baseAddress = listener == StubListener.Rest ? setup.Facade.RestBaseAddress : setup.Facade.SoapBaseAddress;
        RawResponse response = await FacadeFactory.SendRawAsync(baseAddress.Port, "OPTIONS * HTTP/1.1", baseAddress.Authority, ct);

        AssertReachedOnly($"OPTIONS * on the {socket} socket", setup, DefaultStubId, listener, "OPTIONS", "*", response);
    }

    // Asserts that the stub named 'expectedStubId' received the call once on 'listener' and the other two
    // stubs none, with the method token and raw target exactly as expected, its own listener's authority
    // as Host and no X-Forwarded-* header, and that the facade returned that stub's status, X-Stub-Id and
    // body bytes.
    private static void AssertReachedOnly(
        string label,
        RawTargetSetup setup,
        string expectedStubId,
        StubListener listener,
        string expectedMethod,
        string expectedRawTarget,
        RawResponse response)
    {
        StubBackend reached = setup.Stub(expectedStubId);
        foreach (StubBackend other in setup.All.Where(stub => !ReferenceEquals(stub, reached)))
        {
            int otherCount = other.Received.Count;
            Assert.True(otherCount == 0, $"{label}: stub {other.Id} received {otherCount} requests, expected none");
        }

        IReadOnlyList<StubRequest> received = reached.Received;
        Assert.True(received.Count == 1, $"{label}: stub {reached.Id} received {received.Count} requests, expected exactly 1");

        StubRequest request = received[0];
        Assert.True(
            request.Listener == listener,
            $"{label}: stub {reached.Id} received the call on its {request.Listener} listener, expected {listener}");
        Assert.True(
            string.Equals(request.Method, expectedMethod, StringComparison.Ordinal),
            $"{label}: stub {reached.Id} received method {request.Method}, expected {expectedMethod}");
        Assert.True(
            string.Equals(request.RawTarget, expectedRawTarget, StringComparison.Ordinal),
            $"{label}: stub {reached.Id} did not receive the request target {expectedRawTarget} unchanged");

        string ownAuthority = new Uri(listener == StubListener.Rest ? reached.RestBaseUrl : reached.SoapBaseUrl).Authority;
        Assert.True(
            string.Equals(request.Header(HostHeader), ownAuthority, StringComparison.Ordinal),
            $"{label}: stub {reached.Id} did not receive its own {listener} listener authority as Host");
        Assert.False(
            request.Headers.Keys.Any(name => name.StartsWith(ForwardedHeaderPrefix, StringComparison.OrdinalIgnoreCase)),
            $"{label}: stub {reached.Id} received an X-Forwarded-* header the caller did not send");

        Assert.True(
            response.Status == request.ResponseStatus,
            $"{label}: facade answered status {response.Status}, expected stub {reached.Id}'s status {request.ResponseStatus}");
        string? stubId = response.Header(StubIdHeader);
        Assert.True(
            string.Equals(stubId, reached.Id, StringComparison.Ordinal),
            $"{label}: facade response carried X-Stub-Id {stubId ?? "(absent)"}, expected {reached.Id}");
        Assert.True(
            response.Body.AsSpan().SequenceEqual(request.ResponseBody),
            $"{label}: facade response body differs from the bytes stub {reached.Id} sent");
    }

    // Stub A as target "backend", stub B as target "stub" serving getCompany, and stub C as target "dflt",
    // the Routing:DefaultTarget, behind one facade. Every other catalogue operation stays listed with
    // "backend" in the shipped table, so only requests the matcher leaves unresolved reach C.
    private sealed class RawTargetSetup : IAsyncDisposable
    {
        private RawTargetSetup(StubBackend backend, StubBackend rerouted, StubBackend fallback, FacadeFactory facade)
        {
            Backend = backend;
            Rerouted = rerouted;
            Fallback = fallback;
            Facade = facade;
        }

        public StubBackend Backend { get; }

        public StubBackend Rerouted { get; }

        public StubBackend Fallback { get; }

        public FacadeFactory Facade { get; }

        public IEnumerable<StubBackend> All => [Backend, Rerouted, Fallback];

        public static async Task<RawTargetSetup> StartAsync(CancellationToken ct)
        {
            // The stubs start before the facade: the factory must not run while another host is built.
            var started = new List<IAsyncDisposable>(3);
            try
            {
                StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
                started.Add(backend);
                StubBackend rerouted = await StubBackend.StartAsync(RerouteStubId, new StubBackendOptions { Status = RerouteStatus }, ct);
                started.Add(rerouted);
                StubBackend fallback = await StubBackend.StartAsync(DefaultStubId, new StubBackendOptions { Status = DefaultStatus }, ct);
                started.Add(fallback);

                FacadeFactory facade = await FacadeFactory.StartAsync(
                    new FacadeTestSettings
                    {
                        BackendRestBaseUrl = backend.RestBaseUrl,
                        BackendSoapBaseUrl = backend.SoapBaseUrl,
                        StubRestBaseUrl = rerouted.RestBaseUrl,
                        StubSoapBaseUrl = rerouted.SoapBaseUrl,
                        Settings =
                        {
                            [$"Routing:Targets:{DefaultTargetName}:RestBaseUrl"] = fallback.RestBaseUrl,
                            [$"Routing:Targets:{DefaultTargetName}:SoapBaseUrl"] = fallback.SoapBaseUrl,
                            ["Routing:DefaultTarget"] = DefaultTargetName,
                            ["Routing:Operations:Rest:getCompany"] = RerouteTargetName,
                        },
                    },
                    ct);

                return new RawTargetSetup(backend, rerouted, fallback, facade);
            }
            catch
            {
                for (int index = started.Count - 1; index >= 0; index--)
                {
                    await started[index].DisposeAsync();
                }

                throw;
            }
        }

        public StubBackend Stub(string id) => id switch
        {
            BackendStubId => Backend,
            RerouteStubId => Rerouted,
            DefaultStubId => Fallback,
            _ => throw new ArgumentOutOfRangeException(nameof(id), "stub id is not A, B or C"),
        };

        // The facade stops first, so no forwarded call is in flight when the stubs stop.
        public async ValueTask DisposeAsync()
        {
            await Facade.DisposeAsync();
            await Fallback.DisposeAsync();
            await Rerouted.DisposeAsync();
            await Backend.DisposeAsync();
        }
    }

}

// Credential handling of the routing table's target base URLs. The facade carries and adds no
// credential, so a base URL carrying user information is rejected at start-up and on reload, and no
// validation failure, rejected-reload log entry or /admin/info body quotes a configured base URL, or a
// target name that may hold one. A failure names the entry by its configuration path and the rule the
// value breaks; the start-up error log, the line Program.cs writes to standard error and the
// rejected-reload log entry are all built from those failures.
//
// Every case runs in-process against RoutingOptionsValidator, OperationRouter and
// AdminEndpoints.DisplayBaseUrl directly: a table the validator rejects never starts a host, and the
// sanitiser is the only part of /admin/info that a URL passes through.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every value here is a fixed literal: the URLs are synthetic, use
// no live port and hold no real credential.
/// <summary>
/// Proves that a base URL carrying user information is rejected without being echoed, that a target
/// name holding an at-sign is never quoted, that a rejected reload logs no credential while routing keeps
/// the last valid table, and that <c>/admin/info</c> never shows user information.
/// </summary>
public sealed class BaseUrlCredentialTests
{
    private const string TargetName = "backend";
    private const string RestBaseUrl = "http://standin:65000";
    private const string SoapBaseUrl = "http://standin:21001";

    // The fragments of the synthetic credentials that must never reach a message or a log. "user" alone
    // is not one, because the rejection itself says "must not contain user information".
    private const string SecretFragment = "secret";
    private const string UserNameFragment = "user:";
    private const string AtSign = "@";

    // The rejected-reload log entry OperationRouter writes.
    private const int ReloadRejectedEventId = 1101;

    /// <summary>
    /// A base URL carrying user information, on either surface's property, is rejected with one failure
    /// that names the entry and the rule it breaks and quotes no part of the value.
    /// </summary>
    /// <param name="property">The target property holding the URL, <c>RestBaseUrl</c> or <c>SoapBaseUrl</c>.</param>
    /// <param name="url">The configured URL.</param>
    /// <param name="reason">The rule the URL breaks first, as the failure words it.</param>
    [Theory]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend:65000", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend:65000", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "https://user@backend", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "https://user@backend", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend/base?bad=1", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend/base?bad=1", "must not contain user information")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "http://user:secret@backend:99999", "is not an absolute URL")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "http://user:secret@backend:99999", "is not an absolute URL")]
    [InlineData(nameof(RoutingTarget.RestBaseUrl), "user:secret@backend", "is not an http or https URL")]
    [InlineData(nameof(RoutingTarget.SoapBaseUrl), "user:secret@backend", "is not an http or https URL")]
    public void ValidatorRejectsBaseUrlWithUserInformationWithoutEchoingIt(string property, string url, string reason)
    {
        RoutingOptions options = Table(RestBaseUrl, SoapBaseUrl);
        RoutingTarget target = options.Targets[TargetName];
        if (property == nameof(RoutingTarget.RestBaseUrl))
        {
            target.RestBaseUrl = url;
        }
        else
        {
            target.SoapBaseUrl = url;
        }

        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Equal(new[] { $"Routing:Targets:{TargetName}:{property} {reason}" }, result.Failures);
        AssertHoldsNoCredential(result.FailureMessage);

        // The text Program.cs writes to standard error and the host logs when start-up stops.
        AssertHoldsNoCredential(new OptionsValidationException(Options.DefaultName, typeof(RoutingOptions), result.Failures).Message);
    }

    /// <summary>
    /// Shipped and documented base URLs, which carry no user information, still validate, and the
    /// router's parser accepts them.
    /// </summary>
    /// <param name="restBaseUrl">The target's REST base URL.</param>
    /// <param name="soapBaseUrl">The target's SOAP base URL.</param>
    [Theory]
    [InlineData("http://standin:65000", "http://standin:21001")]
    [InlineData("http://orders:8080", "http://orders:8081")]
    [InlineData("http://127.0.0.1:1/base", "http://127.0.0.1:2/base")]
    public void ValidatorAcceptsBaseUrlsWithoutUserInformation(string restBaseUrl, string soapBaseUrl)
    {
        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default)
            .Validate(Options.DefaultName, Table(restBaseUrl, soapBaseUrl));

        Assert.True(result.Succeeded);
        Assert.True(RoutingOptionsValidator.TryParseBaseUrl(restBaseUrl, out _));
        Assert.True(RoutingOptionsValidator.TryParseBaseUrl(soapBaseUrl, out _));
    }

    /// <summary>
    /// The reason helper and <see cref="RoutingOptionsValidator.TryParseBaseUrl"/> agree on every value:
    /// null for a usable base URL, otherwise the first rule broken, in a fixed phrase.
    /// </summary>
    /// <param name="url">The configured value.</param>
    /// <param name="reason">The expected phrase; null when the value is usable.</param>
    [Theory]
    [InlineData("http://standin:65000", null)]
    [InlineData(" https://standin/base/ ", null)]
    [InlineData("   ", "is empty")]
    [InlineData("standin:65000/base?x", "is not an http or https URL")]
    [InlineData("not a url", "is not an absolute URL")]
    [InlineData("ftp://user:secret@backend", "is not an http or https URL")]
    [InlineData("http://user:@backend", "must not contain user information")]
    [InlineData("http://user\uFF20backend:65000", "must not contain user information")]
    [InlineData("http://user\uFE6Bbackend", "must not contain user information")]
    [InlineData("http://backend/base?x=1", "must not contain a query")]
    [InlineData("http://backend?", "must not contain a query")]
    [InlineData("http://backend/base#top", "must not contain a fragment")]
    public void ReasonNamesTheFirstRuleBrokenAndAgreesWithTheParser(string url, string? reason)
    {
        Assert.Equal(reason, RoutingOptionsValidator.DescribeBaseUrlProblem(url));
        Assert.Equal(reason is null, RoutingOptionsValidator.TryParseBaseUrl(url, out Uri? parsed));
        Assert.Equal(reason is null, parsed is not null);
    }

    /// <summary>
    /// An undefined target name is quoted as configured unless it holds an at-sign, by the validator's
    /// default-target and operation rules and by the router's failure for an unresolvable entry.
    /// </summary>
    /// <param name="targetName">The undefined target name both entries hold.</param>
    /// <param name="quoted">Whether the failures quote the name.</param>
    [Theory]
    [InlineData("nowhere", true)]
    [InlineData("http://user:secret@backend:65000", false)]
    [InlineData("user\uFF20backend", false)]
    public void UndefinedTargetNameIsQuotedOnlyWithoutAtSign(string targetName, bool quoted)
    {
        string notShown = "names an undefined target, not shown because it may contain user information";
        string defaultFailure = quoted
            ? $"Routing:DefaultTarget names undefined target '{targetName}'"
            : $"Routing:DefaultTarget {notShown}";
        string operationFailure = quoted
            ? $"Routing:Operations:Soap:getPhoto names undefined target '{targetName}'"
            : $"Routing:Operations:Soap:getPhoto {notShown}";

        RoutingOptions options = Table(RestBaseUrl, SoapBaseUrl);
        options.DefaultTarget = targetName;
        options.Operations.Soap["getPhoto"] = targetName;

        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Equal(new[] { defaultFailure, operationFailure }, result.Failures);
        AssertHoldsNoCredential(result.FailureMessage);

        // The router builds the same failure for an entry it cannot resolve. Only a table that skipped
        // validation can reach it, so the monitor here serves the table unvalidated.
        RoutingOptions routed = Table(RestBaseUrl, SoapBaseUrl);
        routed.Operations.Soap["getPhoto"] = targetName;
        OperationRouter router = Router(new TestOptionsMonitor<RoutingOptions>(routed), new CapturingLogger());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => router.Resolve(Surface.Soap, OperationCatalogue.Default.Find(Surface.Soap, "getPhoto")));
        Assert.Equal($"The routing table cannot route Soap.getPhoto: {operationFailure}.", exception.Message);
        AssertHoldsNoCredential(exception.Message);
    }

    /// <summary>
    /// A reload rejected for a base URL carrying user information and a query keeps the last valid table
    /// in service and is logged once, naming the entry and holding no credential, however many requests
    /// arrive while it stays rejected.
    /// </summary>
    [Fact]
    public void RejectedReloadKeepsLastValidTableAndLogsNoCredential()
    {
        var routing = new TestOptionsMonitor<RoutingOptions>(Table(RestBaseUrl, SoapBaseUrl));
        var logger = new CapturingLogger();
        OperationRouter router = Router(routing, logger);

        Uri restBaseUrl = router.Resolve(Surface.Rest, operation: null).BaseUrl;
        Assert.Equal(new Uri(RestBaseUrl), restBaseUrl);

        // The exception the options monitor throws for the reloaded table, produced by the real validator.
        RoutingOptions reloaded = Table("http://USER:SECRET@backend/base?bad=1", SoapBaseUrl);
        ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default).Validate(Options.DefaultName, reloaded);
        Assert.True(result.Failed);
        routing.Rejection = new OptionsValidationException(Options.DefaultName, typeof(RoutingOptions), result.Failures);

        Assert.Equal(restBaseUrl, router.Resolve(Surface.Rest, operation: null).BaseUrl);
        Assert.Equal(restBaseUrl, router.Resolve(Surface.Rest, OperationCatalogue.Default.Find(Surface.Rest, "getCompany")).BaseUrl);
        Assert.Equal(new Uri(SoapBaseUrl), router.Resolve(Surface.Soap, operation: null).BaseUrl);

        LogEntry rejected = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, rejected.Level);
        Assert.Equal(ReloadRejectedEventId, rejected.EventId);
        Assert.Contains("Routing:Targets:backend:RestBaseUrl must not contain user information", rejected.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, static entry =>
        {
            AssertHoldsNoCredential(entry.Message);
            Assert.All(entry.State, static value => AssertHoldsNoCredential(value));
        });
    }

    /// <summary>
    /// <see cref="AdminEndpoints.DisplayBaseUrl"/> reports an accepted URL exactly as configured, strips
    /// user information, query and fragment from an http or https URL carrying user information, and
    /// reports null for anything it cannot show safely.
    /// </summary>
    /// <param name="configured">The configured base URL.</param>
    /// <param name="expected">The <c>/admin/info</c> value.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("http://standin:65000", "http://standin:65000")]
    [InlineData("http://standin:21001", "http://standin:21001")]
    [InlineData("http://127.0.0.1:1/base", "http://127.0.0.1:1/base")]
    [InlineData("http://user:secret@backend:65000", "http://backend:65000/")]
    [InlineData("https://user@backend", "https://backend/")]
    [InlineData("http://user:secret@backend/base?bad=1#top", "http://backend/base")]
    [InlineData("http://user:secret@backend:99999", null)]
    [InlineData("user:secret@backend", null)]
    [InlineData("ftp://user:secret@backend", null)]
    [InlineData("http://user\uFF20backend:65000", null)]
    [InlineData("http://backend/base?x=1", null)]
    public void AdminInfoBaseUrlNeverShowsUserInformation(string? configured, string? expected)
    {
        string? shown = AdminEndpoints.DisplayBaseUrl(configured);

        Assert.Equal(expected, shown);
        AssertHoldsNoCredential(shown);
    }

    // The shipped shape: one target with both base URLs, the default target, no operation listed.
    private static RoutingOptions Table(string restBaseUrl, string soapBaseUrl) => new()
    {
        DefaultTarget = TargetName,
        Targets = new Dictionary<string, RoutingTarget>(StringComparer.OrdinalIgnoreCase)
        {
            [TargetName] = new RoutingTarget { RestBaseUrl = restBaseUrl, SoapBaseUrl = soapBaseUrl },
        },
    };

    private static OperationRouter Router(IOptionsMonitor<RoutingOptions> routing, ILogger<OperationRouter> logger) =>
        new(routing, new TestOptionsMonitor<BackendOptions>(new BackendOptions()), OperationCatalogue.Default, logger);

    private static void AssertHoldsNoCredential(string? text)
    {
        if (text is null)
        {
            return;
        }

        Assert.DoesNotContain(SecretFragment, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserNameFragment, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AtSign, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFF20", text, StringComparison.Ordinal);
    }

    /// <summary>One captured log call.</summary>
    /// <param name="Level">The entry's level.</param>
    /// <param name="EventId">The entry's event id.</param>
    /// <param name="Message">The formatted message.</param>
    /// <param name="State">Every structured state value, as text.</param>
    private sealed record LogEntry(LogLevel Level, int EventId, string Message, IReadOnlyList<string?> State);

    /// <summary>An <see cref="ILogger{TCategoryName}"/> that records every entry, enabled at every level.</summary>
    private sealed class CapturingLogger : ILogger<OperationRouter>
    {
        private readonly List<LogEntry> _entries = [];
        private readonly Lock _gate = new();

        /// <summary>The entries logged so far, in order.</summary>
        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            IReadOnlyList<string?> values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? [.. pairs.Select(static pair => pair.Value?.ToString())]
                : [];
            var entry = new LogEntry(logLevel, eventId.Id, formatter(state, exception), values);
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }
    }

    /// <summary>
    /// An <see cref="IOptionsMonitor{TOptions}"/> serving one fixed value until <see cref="Rejection"/> is
    /// set, and from then on throwing it, as the options monitor does for a reload that fails validation.
    /// </summary>
    /// <param name="value">The value served while no rejection is set.</param>
    private sealed class TestOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        private volatile OptionsValidationException? _rejection;

        /// <summary>The exception every read throws once set; null serves the value.</summary>
        public OptionsValidationException? Rejection
        {
            get => _rejection;
            set => _rejection = value;
        }

        public TOptions CurrentValue => _rejection is { } rejection ? throw rejection : value;

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
