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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Admin;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Erewhon.Facade.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

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

// Header pass-through (class PassThroughTransformerTests) sits here, beside the request-line
// pass-through, as a class of its own: the results report counts test classes by the name suffixes
// "RoutingTests", "FailureBehaviourTests" and "ListenerIsolationTests", and this name matches none.
//
// Hop-by-hop handling and response timing of the facade's pass-through transform, through the facade's
// real REST and SOAP sockets. A field that a Connection header nominates belongs to one hop (RFC 9110,
// section 7.6.1), so neither the caller's nominated request fields nor the backend's nominated response
// fields may cross the facade, while every other end-to-end field, the Content-Length framing and the
// body bytes pass unchanged. That holds when the caller's Connection list also holds close, keep-alive
// or upgrade, to which Kestrel reduces its own copy of the header, and for every request of a
// keep-alive connection, whatever the framing of the request before it. The backend's status line and
// headers reach the caller as soon as the backend sends them, before its first body byte; once they
// have, a deadline that passes before the body ends aborts the response instead of re-heading it.
//
// The caller's request is written on a raw socket, because HttpClient joins repeated header lines into
// one, and the backend's responses come from a raw listener, because Kestrel would not write a response
// head with Connection lines naming other fields or send a head before its body is released.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every message here is a fixed literal: no port, URL or elapsed
// time. The order check is deterministic: the backend withholds its body until the test releases it,
// and the 10 s bound only ends the wait of a facade that holds the head back.
/// <summary>
/// Shows that fields nominated by a <c>Connection</c> header are dropped on the request and the response
/// hop while the other end-to-end fields pass, mixed lists and keep-alive connections included, that the
/// response head is not held until the body, and that a deadline passing after the head aborts the
/// response.
/// </summary>
public sealed class PassThroughTransformerTests
{
    private const string BackendStubId = "A";
    private const string EndToEndHeader = "X-End-To-End";
    private const string EndToEndValue = "kept";
    private const string ContentTypeHeader = "Content-Type";
    private const string ContentLengthHeader = "Content-Length";
    private const string TransferEncodingHeader = "Transfer-Encoding";
    private const string SoapActionHeader = "SOAPAction";
    private const string CrLf = "\r\n";

    // The custom field the mixed-list and keep-alive requests carry, nominated or not.
    private const string HopField = "X-Hop-One";
    private const string HopValue = "one";

    // A request field line that nominates the custom field next to keep-alive, as one Connection line.
    private const string KeepAliveNominatingLine = "Connection: X-Hop-One, keep-alive";

    // The largest message head the raw reader accepts, as Kestrel's default request header limit.
    private const int MaxMessageHeadBytes = 32 * 1024;

    // Backend:TimeoutSeconds for the call whose body never follows its head.
    private const int MidBodyDeadlineSeconds = 2;

    // The blank line that ends a message head.
    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

    // The caller's request names these fields in two Connection lines, in another case than their own
    // field lines: two custom fields, a request field (Accept) and a content field of the body.
    private static readonly string[] NominatedRequestFields = ["X-Hop-One", "X-Hop-Two", "Accept", "Content-Language"];

    // The backend's response names these fields in two Connection lines, likewise.
    private static readonly string[] NominatedResponseFields = ["X-Hop-One", "X-Hop-Two", "Content-Language"];

    // A JSON null: four bytes, the body of every raw backend response.
    private static readonly byte[] BackendBody = "null"u8.ToArray();

    // The second Connection line of each direction also holds an empty element, a token that is not a
    // valid field name, and Content-Length, which frames the body and must survive.
    private static readonly byte[] NominatingResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK" + CrLf
        + "Content-Type: application/json; charset=utf-8" + CrLf
        + "Connection: X-HOP-ONE, x-hop-two, close" + CrLf
        + "X-Hop-One: one" + CrLf
        + "x-Hop-Two: two" + CrLf
        + EndToEndHeader + ": " + EndToEndValue + CrLf
        + "Connection: content-language ,, bad token\t, Content-Length" + CrLf
        + "Content-Language: en" + CrLf
        + "Content-Length: 4" + CrLf
        + CrLf
        + "null");

    private static readonly byte[] HeldBodyResponseHead = Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK" + CrLf
        + "Content-Type: application/json; charset=utf-8" + CrLf
        + EndToEndHeader + ": " + EndToEndValue + CrLf
        + "Content-Length: 4" + CrLf
        + "Connection: close" + CrLf
        + CrLf);

    // How long a raw backend withholds its body when the test never releases it.
    private static readonly TimeSpan BodyWithheldBound = TimeSpan.FromSeconds(10);

    // How long a raw exchange with the facade may take before it fails the test, as the raw client's.
    private static readonly TimeSpan RawExchangeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Sends a request with a body whose two <c>Connection</c> lines nominate custom, request and content
    /// fields, and asserts that none of them reaches the backend while an unrelated custom field, the
    /// content type, the <c>Content-Length</c> framing and the body bytes arrive unchanged.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent")]
    [InlineData("Soap.updateAgentWithDTO")]
    public async Task ConnectionNominatedRequestFieldsDoNotReachBackend(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        string contentLength = body.Length.ToString(CultureInfo.InvariantCulture);
        Uri socket = SocketFor(facade, operation);

        var head = new StringBuilder()
            .Append(prepared.Method.Method).Append(' ').Append(prepared.PathAndQuery).Append(" HTTP/1.1").Append(CrLf)
            .Append("Host: ").Append(socket.Authority).Append(CrLf)
            .Append("Connection: X-HOP-ONE, x-hop-two, accept").Append(CrLf)
            .Append("X-Hop-One: one").Append(CrLf)
            .Append("x-Hop-Two: two").Append(CrLf)
            .Append("Accept: application/json").Append(CrLf)
            .Append(EndToEndHeader).Append(": ").Append(EndToEndValue).Append(CrLf)
            .Append("Connection: content-language ,, bad token\t, Content-Length").Append(CrLf)
            .Append("Content-Language: en").Append(CrLf)
            .Append(ContentTypeHeader).Append(": ").Append(prepared.ContentType).Append(CrLf);
        if (prepared.SoapAction is not null)
        {
            head.Append(SoapActionHeader).Append(": ").Append(prepared.SoapAction).Append(CrLf);
        }

        head.Append(ContentLengthHeader).Append(": ").Append(contentLength).Append(CrLf).Append(CrLf);

        int status = await SendRawAsync(socket, [.. Encoding.ASCII.GetBytes(head.ToString()), .. body], ct);

        Assert.Equal(200, status);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 1, "backend did not receive exactly one request");
        StubRequest forwarded = received[0];
        foreach (string field in NominatedRequestFields)
        {
            Assert.True(forwarded.Header(field) is null, "Connection-nominated request field " + field + " reached the backend");
        }

        Assert.Equal(EndToEndValue, forwarded.Header(EndToEndHeader));
        Assert.Equal(prepared.ContentType, forwarded.Header(ContentTypeHeader));
        Assert.Equal(prepared.SoapAction, forwarded.Header(SoapActionHeader));
        Assert.Equal(contentLength, forwarded.Header(ContentLengthHeader));
        Assert.True(forwarded.Header(TransferEncodingHeader) is null, "request reached the backend re-framed with Transfer-Encoding");
        Assert.Equal(body, forwarded.Body);
    }

    /// <summary>
    /// Sends a request with a body whose <c>Connection</c> list nominates a custom field next to exactly
    /// one of the options <c>close</c>, <c>keep-alive</c> and <c>upgrade</c>, in one line or in two, and
    /// asserts that the custom field does not reach the backend while an unrelated custom field, the
    /// content type, the <c>Content-Length</c> framing and the body bytes arrive unchanged. Kestrel
    /// reduces its own copy of such a header to the option alone.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    /// <param name="connectionValues">The value of each <c>Connection</c> line, in sending order.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent", new[] { "X-Hop-One, close" })]
    [InlineData("Rest.postCreateAgent", new[] { "X-Hop-One, keep-alive" })]
    [InlineData("Rest.postCreateAgent", new[] { "X-Hop-One, upgrade" })]
    [InlineData("Rest.postCreateAgent", new[] { "keep-alive", "X-Hop-One" })]
    [InlineData("Soap.updateAgentWithDTO", new[] { "X-Hop-One, close" })]
    [InlineData("Soap.updateAgentWithDTO", new[] { "X-Hop-One, keep-alive" })]
    [InlineData("Soap.updateAgentWithDTO", new[] { "X-Hop-One, upgrade" })]
    [InlineData("Soap.updateAgentWithDTO", new[] { "keep-alive", "X-Hop-One" })]
    public async Task FieldNominatedNextToConnectionOptionDoesNotReachBackend(string operationId, string[] connectionValues)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        Uri socket = SocketFor(facade, operation);
        string[] fieldLines = [.. connectionValues.Select(value => "Connection: " + value), HopField + ": " + HopValue];

        int status = await SendRawAsync(socket, ContentLengthRequest(prepared, body, socket, fieldLines), ct);

        Assert.Equal(200, status);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 1, "backend did not receive exactly one request");
        AssertForwardedWithContentLength(received[0], prepared, body);
        Assert.True(received[0].Header(HopField) is null, "custom field nominated next to a connection option reached the backend");
    }

    /// <summary>
    /// Sends requests on one keep-alive connection, alike but for their <c>Connection</c> lines: every
    /// other request, the first included, nominates a custom field next to <c>keep-alive</c>, and the rest
    /// carry the same field without nominating it. Asserts that the field is dropped from exactly the
    /// requests that nominate it, whether each request follows the previous response or all are
    /// pipelined, more of them than the facade frames ahead of dispatch included.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    /// <param name="requestCount">The number of requests sent on the connection.</param>
    /// <param name="pipelined">Whether all requests are written before any response is read.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent", 2, false)]
    [InlineData("Rest.postCreateAgent", 2, true)]
    [InlineData("Rest.postCreateAgent", 40, true)]
    [InlineData("Soap.updateAgentWithDTO", 2, false)]
    [InlineData("Soap.updateAgentWithDTO", 2, true)]
    [InlineData("Soap.updateAgentWithDTO", 40, true)]
    public async Task EachKeepAliveRequestDropsOnlyFieldsItsOwnConnectionLinesNominate(
        string operationId,
        int requestCount,
        bool pipelined)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        Uri socket = SocketFor(facade, operation);
        string hopLine = HopField + ": " + HopValue;
        byte[][] requests =
        [
            .. Enumerable.Range(0, requestCount).Select(index => ContentLengthRequest(
                prepared, body, socket, index % 2 == 0 ? new[] { KeepAliveNominatingLine, hopLine } : new[] { hopLine })),
        ];

        IReadOnlyList<int> statuses = await SendRawOnOneConnectionAsync(socket, requests, pipelined, ct);

        Assert.True(statuses.Count == requestCount && statuses.All(status => status == 200), "a keep-alive request was not answered with 200");
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == requestCount, "backend did not receive every keep-alive request");
        for (int index = 0; index < requestCount; index++)
        {
            AssertForwardedWithContentLength(received[index], prepared, body);
            if (index % 2 == 0)
            {
                Assert.True(received[index].Header(HopField) is null, "custom field nominated by its own request reached the backend");
            }
            else
            {
                Assert.True(received[index].Header(HopField) == HopValue, "un-nominated custom field of a keep-alive request did not reach the backend");
            }
        }
    }

    /// <summary>
    /// Sends, on one keep-alive connection, a request whose body is chunked with a chunk extension and a
    /// trailer field, then a request whose <c>Connection</c> line nominates a custom field next to
    /// <c>keep-alive</c>. Asserts that the chunked body arrives whole and that the field is dropped from
    /// the second request, which is only found by following the first request's chunked framing.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    /// <param name="pipelined">Whether both requests are written before either response is read.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent", false)]
    [InlineData("Rest.postCreateAgent", true)]
    [InlineData("Soap.updateAgentWithDTO", false)]
    [InlineData("Soap.updateAgentWithDTO", true)]
    public async Task NominatedFieldIsDroppedFromRequestAfterChunkedRequest(string operationId, bool pipelined)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        Uri socket = SocketFor(facade, operation);
        string hopLine = HopField + ": " + HopValue;

        IReadOnlyList<int> statuses = await SendRawOnOneConnectionAsync(
            socket,
            [
                ChunkedRequest(prepared, body, socket, [hopLine]),
                ContentLengthRequest(prepared, body, socket, [KeepAliveNominatingLine, hopLine]),
            ],
            pipelined,
            ct);

        Assert.Equal([200, 200], statuses);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 2, "backend did not receive exactly two requests");
        Assert.Equal(body, received[0].Body);
        Assert.True(received[0].Header(HopField) == HopValue, "un-nominated custom field of the chunked request did not reach the backend");
        Assert.Equal(EndToEndValue, received[0].Header(EndToEndHeader));
        AssertForwardedWithContentLength(received[1], prepared, body);
        Assert.True(received[1].Header(HopField) is null, "custom field nominated after a chunked request reached the backend");
    }

    /// <summary>
    /// Has the backend answer with two <c>Connection</c> lines that nominate custom and content fields,
    /// and asserts that none of them reaches the caller while an unrelated custom field, the
    /// <c>Content-Length</c> framing and the body bytes arrive unchanged.
    /// </summary>
    /// <param name="operationId">The catalogue operation called at its normal address.</param>
    [Theory]
    [InlineData("Rest.getCompany")]
    [InlineData("Soap.getAgentNames")]
    public async Task ConnectionNominatedResponseFieldsDoNotReachCaller(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawBackend backend = RawBackend.Start((stream, token) => stream.WriteAsync(NominatingResponse, token).AsTask());
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        using HttpClient client = facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(
            client, OperationRequests.For(operation).ToMessage(SocketFor(facade, operation)), ct);

        Assert.Equal(200, response.Status);
        foreach (string field in NominatedResponseFields)
        {
            Assert.True(response.Header(field) is null, "Connection-nominated response field " + field + " reached the caller");
        }

        Assert.Equal(EndToEndValue, response.Header(EndToEndHeader));
        Assert.Equal("4", response.Header(ContentLengthHeader));
        Assert.True(response.Header(TransferEncodingHeader) is null, "response reached the caller re-framed with Transfer-Encoding");
        Assert.Equal(BackendBody, response.Body);
        Assert.True(backend.RequestCount == 1, "backend did not receive exactly one request");
    }

    /// <summary>
    /// Has the backend send its status line and headers and then withhold the body until the test has
    /// received the facade's response head, and asserts that the head arrived first and the full body
    /// after it.
    /// </summary>
    /// <param name="operationId">The catalogue operation called at its normal address.</param>
    [Theory]
    [InlineData("Rest.getCompany")]
    [InlineData("Soap.getAgentNames")]
    public async Task ResponseHeadReachesCallerBeforeBackendSendsBody(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using RawBackend backend = RawBackend.Start(async (stream, token) =>
        {
            await stream.WriteAsync(HeldBodyResponseHead, token);
            await stream.FlushAsync(token);
            try
            {
                await release.Task.WaitAsync(BodyWithheldBound, token);
            }
            catch (TimeoutException)
            {
                // The facade held the head back: the body is sent anyway, so the call completes and the
                // test fails on the order it observed instead of hanging.
            }

            bodySent.TrySetResult();
            await stream.WriteAsync(BackendBody, token);
        });
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        using HttpClient client = facade.CreateRawClient();
        using HttpRequestMessage request = OperationRequests.For(operation).ToMessage(SocketFor(facade, operation));

        byte[] body;
        bool headBeforeBody;
        string? endToEnd;
        long? contentLength;
        int status;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            headBeforeBody = !bodySent.Task.IsCompleted;
            release.TrySetResult();

            status = (int)response.StatusCode;
            endToEnd = response.Headers.NonValidated.TryGetValues(EndToEndHeader, out HeaderStringValues values)
                ? values.ToString()
                : null;
            contentLength = response.Content.Headers.ContentLength;
            body = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception exception) when (IsTransportFailure(exception, ct))
        {
            Assert.Fail("facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        Assert.True(headBeforeBody, "response head reached the caller only with the backend's body");
        Assert.Equal(200, status);
        Assert.Equal(EndToEndValue, endToEnd);
        Assert.Equal((long?)BackendBody.Length, contentLength);
        Assert.Equal(BackendBody, body);
        Assert.True(backend.RequestCount == 1, "backend did not receive exactly one request");
    }

    /// <summary>
    /// Has the backend send its status line and headers and then never send the body, with a 2 s
    /// deadline, and asserts that the caller receives the backend's head and then an aborted body:
    /// the response the backend started is never re-headed with the facade's timeout response.
    /// </summary>
    /// <param name="operationId">The catalogue operation called at its normal address.</param>
    [Theory]
    [InlineData("Rest.getCompany")]
    [InlineData("Soap.getAgentNames")]
    public async Task DeadlineAfterResponseHeadAbortsResponse(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawBackend backend = RawBackend.Start(async (stream, token) =>
        {
            await stream.WriteAsync(HeldBodyResponseHead, token);
            await stream.FlushAsync(token);

            // The body never follows: the facade's deadline passes first, and disposal ends the wait.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = backend.BaseUrl,
                BackendSoapBaseUrl = backend.BaseUrl,
                BackendTimeoutSeconds = MidBodyDeadlineSeconds,
            },
            ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        using HttpClient client = facade.CreateRawClient();
        using HttpRequestMessage request = OperationRequests.For(operation).ToMessage(SocketFor(facade, operation));

        int status;
        bool bodyAborted;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            status = (int)response.StatusCode;
            try
            {
                await response.Content.ReadAsByteArrayAsync(ct);
                bodyAborted = false;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                bodyAborted = true;
            }
        }
        catch (Exception exception) when (IsTransportFailure(exception, ct))
        {
            Assert.Fail("facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        Assert.Equal(200, status);
        Assert.True(bodyAborted, "response body was not aborted after the deadline passed");
        Assert.True(backend.RequestCount == 1, "backend did not receive exactly one request");
    }

    private static Uri SocketFor(FacadeFactory facade, OperationDescriptor operation) =>
        operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;

    // Asserts that a request sent with a Content-Length body reached the backend with its unrelated
    // end-to-end field, content type, SOAPAction, Content-Length framing and body bytes unchanged.
    private static void AssertForwardedWithContentLength(StubRequest forwarded, PreparedRequest prepared, byte[] body)
    {
        Assert.Equal(EndToEndValue, forwarded.Header(EndToEndHeader));
        Assert.Equal(prepared.ContentType, forwarded.Header(ContentTypeHeader));
        Assert.Equal(prepared.SoapAction, forwarded.Header(SoapActionHeader));
        Assert.Equal(body.Length.ToString(CultureInfo.InvariantCulture), forwarded.Header(ContentLengthHeader));
        Assert.True(forwarded.Header(TransferEncodingHeader) is null, "request reached the backend re-framed with Transfer-Encoding");
        Assert.Equal(body, forwarded.Body);
    }

    // The head of a raw request for the prepared operation without its framing line and final blank
    // line: the request line, Host, the given field lines, an unrelated end-to-end field, the content
    // type and any SOAPAction.
    private static StringBuilder RequestHeadStart(PreparedRequest prepared, Uri socket, IEnumerable<string> fieldLines)
    {
        var head = new StringBuilder()
            .Append(prepared.Method.Method).Append(' ').Append(prepared.PathAndQuery).Append(" HTTP/1.1").Append(CrLf)
            .Append("Host: ").Append(socket.Authority).Append(CrLf);
        foreach (string line in fieldLines)
        {
            head.Append(line).Append(CrLf);
        }

        head.Append(EndToEndHeader).Append(": ").Append(EndToEndValue).Append(CrLf)
            .Append(ContentTypeHeader).Append(": ").Append(prepared.ContentType).Append(CrLf);
        if (prepared.SoapAction is not null)
        {
            head.Append(SoapActionHeader).Append(": ").Append(prepared.SoapAction).Append(CrLf);
        }

        return head;
    }

    // A raw request for the prepared operation whose body is framed by Content-Length.
    private static byte[] ContentLengthRequest(PreparedRequest prepared, byte[] body, Uri socket, IEnumerable<string> fieldLines)
    {
        StringBuilder head = RequestHeadStart(prepared, socket, fieldLines)
            .Append(ContentLengthHeader).Append(": ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append(CrLf)
            .Append(CrLf);
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. body];
    }

    // A raw request for the prepared operation whose body is sent chunked: its first half as a chunk with
    // a chunk extension, the rest as a second chunk, then the last chunk and one trailer field.
    private static byte[] ChunkedRequest(PreparedRequest prepared, byte[] body, Uri socket, IEnumerable<string> fieldLines)
    {
        if (body.Length < 2)
        {
            throw new InvalidOperationException("operation request body is too short for two chunks");
        }

        int split = body.Length / 2;
        StringBuilder head = RequestHeadStart(prepared, socket, fieldLines)
            .Append(TransferEncodingHeader).Append(": chunked").Append(CrLf)
            .Append(CrLf);
        string firstChunkLine = split.ToString("x", CultureInfo.InvariantCulture) + ";ext=one" + CrLf;
        string secondChunkLine = (body.Length - split).ToString("x", CultureInfo.InvariantCulture) + CrLf;
        string lastChunkAndTrailer = "0" + CrLf + "X-Trailer: t" + CrLf + CrLf;
        return
        [
            .. Encoding.ASCII.GetBytes(head.ToString() + firstChunkLine),
            .. body.AsSpan(0, split),
            .. Encoding.ASCII.GetBytes(CrLf + secondChunkLine),
            .. body.AsSpan(split),
            .. Encoding.ASCII.GetBytes(CrLf + lastChunkAndTrailer),
        ];
    }

    // Writes the request bytes as they are on a new connection to the facade socket, reads the
    // response head and its Content-Length body, and returns the status code.
    private static async Task<int> SendRawAsync(Uri socket, byte[] request, CancellationToken ct) =>
        (await SendRawOnOneConnectionAsync(socket, [request], pipelined: false, ct))[0];

    // Writes each request's bytes as they are on one new connection to the facade socket, and returns
    // the status code of each response in order, each read with its Content-Length body. Pipelined, all
    // requests are written at once before any response is read; otherwise each is written only once the
    // response to the one before has been read.
    private static async Task<IReadOnlyList<int>> SendRawOnOneConnectionAsync(
        Uri socket,
        IReadOnlyList<byte[]> requests,
        bool pipelined,
        CancellationToken ct)
    {
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exchange.CancelAfter(RawExchangeTimeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var heads = new List<string>(requests.Count);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, socket.Port, exchange.Token);
            NetworkStream stream = client.GetStream();
            if (pipelined)
            {
                await stream.WriteAsync(requests.SelectMany(request => request).ToArray(), exchange.Token);
            }

            foreach (byte[] request in requests)
            {
                if (!pipelined)
                {
                    await stream.WriteAsync(request, exchange.Token);
                }

                heads.Add(await ReadMessageAsync(stream, exchange.Token));
            }
        }
        catch (Exception exception) when (IsTransportFailure(exception, ct) || exception is InvalidDataException)
        {
            Assert.Fail("raw facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        return [.. heads.Select(StatusOf)];
    }

    // The status code of a raw response head.
    private static int StatusOf(string head)
    {
        // "HTTP/1.1 200 OK": the status code is the second element of the status line.
        int lineEnd = head.IndexOf(CrLf, StringComparison.Ordinal);
        string[] statusLine = (lineEnd < 0 ? head : head[..lineEnd]).Split(' ');
        Assert.True(
            statusLine.Length >= 2
                && statusLine[0] == "HTTP/1.1"
                && int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out _),
            "raw facade response has no HTTP/1.1 status line");
        return int.Parse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken ct) =>
        exception is HttpRequestException or IOException or SocketException
        || (exception is OperationCanceledException && !ct.IsCancellationRequested);

    // Reads one HTTP/1.1 message from the stream: its head, then as many body bytes as its
    // Content-Length declares, which are discarded. Returns the head without its final blank line.
    // The head is read one byte at a time, so no byte of a message that follows it on the connection,
    // as pipelined responses do, is taken with it.
    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxMessageHeadBytes];
        int filled = 0;
        while (!buffer.AsSpan(0, filled).EndsWith(HeadTerminator))
        {
            if (filled == buffer.Length)
            {
                throw new InvalidDataException("message head exceeds the raw reader's bound");
            }

            int read = await stream.ReadAsync(buffer.AsMemory(filled, 1), ct);
            if (read == 0)
            {
                throw new InvalidDataException("connection closed before the message head ended");
            }

            filled += read;
        }

        string head = Encoding.ASCII.GetString(buffer, 0, filled - HeadTerminator.Length);
        long remaining = BodyLengthOf(head);
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                throw new InvalidDataException("connection closed before the message body ended");
            }

            remaining -= read;
        }

        return head;
    }

    // The declared body length of a message head; 0 without Content-Length. A chunked body is not
    // read here, so such a message is refused rather than treated as ending at its head.
    private static long BodyLengthOf(string head)
    {
        long length = 0;
        foreach (string line in head.Split(CrLf))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            ReadOnlySpan<char> name = line.AsSpan(0, colon).Trim();
            ReadOnlySpan<char> value = line.AsSpan(colon + 1).Trim();
            if (name.Equals(TransferEncodingHeader, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("raw reader does not read chunked bodies");
            }

            if (name.Equals(ContentLengthHeader, StringComparison.OrdinalIgnoreCase)
                && !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                throw new InvalidDataException("message Content-Length is not a number");
            }
        }

        return length;
    }

    /// <summary>
    /// A backend on one ephemeral loopback port that answers each connection's single request with bytes
    /// the test writes, for response heads Kestrel would not produce. The request is read in full before
    /// the answer: its head, then as many body bytes as its <c>Content-Length</c> declares; a chunked
    /// request is closed without an answer.
    /// </summary>
    private sealed class RawBackend : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<NetworkStream, CancellationToken, Task> _answer;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Lock _gate = new();
        private readonly List<Task> _connections = [];
        private readonly Task _accepting;
        private int _requests;
        private int _disposed;

        private RawBackend(TcpListener listener, Func<NetworkStream, CancellationToken, Task> answer)
        {
            _listener = listener;
            _answer = answer;
            BaseUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            _accepting = AcceptAsync();
        }

        /// <summary>The listener's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>, with no trailing slash.</summary>
        public string BaseUrl { get; }

        /// <summary>The number of requests read in full so far.</summary>
        public int RequestCount => Volatile.Read(ref _requests);

        /// <summary>Starts listening; every request is answered by <paramref name="answer"/>.</summary>
        /// <param name="answer">Writes the whole response to the connection; the backend then closes it.</param>
        /// <returns>The started backend; the caller disposes it.</returns>
        public static RawBackend Start(Func<NetworkStream, CancellationToken, Task> answer)
        {
            ArgumentNullException.ThrowIfNull(answer);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new RawBackend(listener, answer);
        }

        /// <summary>Stops listening, cancels every answer in progress and waits for each connection to close.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting;

            Task[] connections;
            lock (_gate)
            {
                connections = [.. _connections];
            }

            await Task.WhenAll(connections);
            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (Exception exception)
                    when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The backend is stopping.
                    return;
                }

                lock (_gate)
                {
                    _connections.Add(ServeAsync(client));
                }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    await ReadMessageAsync(stream, _stopping.Token);
                    Interlocked.Increment(ref _requests);
                    await _answer(stream, _stopping.Token);
                    await stream.FlushAsync(_stopping.Token);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
                    or InvalidDataException or ObjectDisposedException)
                {
                    // The facade closed the connection, the request was not one this backend reads, or the
                    // backend is stopping; the connection is closed without an answer.
                }
            }
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

// Invoker lifecycle (class InvokerLifecycleTests) sits here, beside the routing checks whose reloads
// it follows, as a class of its own: the results report counts test classes whose names end in
// "RoutingTests" as the two-case per-operation routing check, and this name matches none.
//
// Lifecycle of BackendForwarder's per-target invokers across routing reloads and disposal. A target in
// service keeps one pooled invoker. A reload that supersedes the target, or Dispose, retires its invoker,
// which is disposed once the calls still using it have ended: the cache keeps no invoker the routing
// table no longer names, and no call loses its invoker while it runs.
//
// The forwarder is built directly over the real OperationRouter, PassThroughTransformer and
// SoapFaultWriter and over a recording IHttpForwarder, so each test sees the exact invoker every call
// was given and can hold a call in flight. A reload is an options monitor returning a new options
// instance. Whether an invoker has been disposed is read from a request it sends to a closed loopback
// port: a disposed invoker throws ObjectDisposedException, a live one HttpRequestException.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every message here is a fixed literal: no port, URL or elapsed time.
/// <summary>
/// Reuse, retirement and disposal of the facade's per-target <see cref="HttpMessageInvoker"/>s across
/// routing reloads and <see cref="BackendForwarder.Dispose"/>.
/// </summary>
public sealed class InvokerLifecycleTests
{
    private const string TargetName = "backend";
    private const string OperationId = "Rest.getCompany";

    private const string UrlChange = "url";
    private const string TargetTimeoutChange = "target-timeout";
    private const string BackendTimeoutChange = "backend-timeout";

    private const string DisposedBeforeCall = "before-call";
    private const string DisposedDuringResolution = "during-resolution";

    private const int ReloadCount = 5;
    private const int BackendTimeoutSeconds = 30;

    [Fact]
    public async Task CallsToAnUnchangedTargetShareOneLiveInvoker()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        await facade.CallAsync(ct);
        await facade.CallAsync(ct);

        // A reload to equal values rebuilds the routing table but leaves its target in service.
        facade.Routing.CurrentValue = RoutingFor(urlIndex: 0, targetTimeoutSeconds: null);
        await facade.CallAsync(ct);

        IReadOnlyList<HttpMessageInvoker> used = facade.Forwarder.Invokers;
        Assert.True(used.Count == 3, "Each call reaches the forwarder once.");
        Assert.True(used.All(invoker => ReferenceEquals(invoker, used[0])), "Calls to an unchanged target share one invoker.");
        Assert.False(await IsDisposedAsync(used[0], ct), "The invoker of a target in service stays alive.");
    }

    [Theory]
    [InlineData(UrlChange)]
    [InlineData(TargetTimeoutChange)]
    [InlineData(BackendTimeoutChange)]
    public async Task ReloadsThatChangeTheTargetDisposeEverySupersededInvoker(string change)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        await facade.CallAsync(ct);
        for (int reload = 1; reload <= ReloadCount; reload++)
        {
            Reload(facade, change, reload);
            await facade.CallAsync(ct);
        }

        // Once more under the last table, which must reuse the current target's invoker.
        await facade.CallAsync(ct);

        IReadOnlyList<HttpMessageInvoker> used = facade.Forwarder.Invokers;
        Assert.True(used.Count == ReloadCount + 2, "Each call reaches the forwarder once.");
        Assert.True(
            used.Take(ReloadCount + 1).Distinct().Count() == ReloadCount + 1,
            "Every reload that changes the target gives it a new invoker.");
        Assert.True(ReferenceEquals(used[^1], used[^2]), "The current target's invoker is reused.");

        for (int superseded = 0; superseded < ReloadCount; superseded++)
        {
            Assert.True(await IsDisposedAsync(used[superseded], ct), "Every superseded invoker is disposed.");
        }

        Assert.False(await IsDisposedAsync(used[^1], ct), "The current target's invoker stays alive.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASupersededInvokerStaysAliveUntilItsCallInFlightEnds(bool callNewTargetFirst)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        HeldCall hold = facade.Forwarder.HoldNextCall();
        Task inFlight = facade.CallAsync(ct);
        HttpMessageInvoker superseded = await hold.Entered.Task.WaitAsync(ct);

        facade.Routing.CurrentValue = RoutingFor(urlIndex: 1, targetTimeoutSeconds: null);
        if (callNewTargetFirst)
        {
            // The first call to end under the new table sweeps the cache while the held call still
            // holds its lease; without it, the held call retires its own superseded invoker as it ends.
            await facade.CallAsync(ct);
            Assert.False(ReferenceEquals(facade.Forwarder.Invokers[^1], superseded), "The new target gets its own invoker.");
        }

        Assert.False(await IsDisposedAsync(superseded, ct), "A superseded invoker stays alive while its call is in flight.");

        hold.Released.TrySetResult();
        await inFlight.WaitAsync(ct);

        Assert.True(await IsDisposedAsync(superseded, ct), "A superseded invoker is disposed when its last call ends.");

        await facade.CallAsync(ct);
        HttpMessageInvoker current = facade.Forwarder.Invokers[^1];
        Assert.False(ReferenceEquals(current, superseded), "The new target never uses the superseded invoker.");
        Assert.False(await IsDisposedAsync(current, ct), "The current target's invoker stays alive.");
        if (callNewTargetFirst)
        {
            Assert.True(ReferenceEquals(current, facade.Forwarder.Invokers[1]), "The current target's invoker is reused.");
        }
    }

    [Fact]
    public async Task DisposeLetsACallInFlightFinishOnItsInvokerAndThenDisposesIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        HeldCall hold = facade.Forwarder.HoldNextCall();
        Task inFlight = facade.CallAsync(ct);
        HttpMessageInvoker leased = await hold.Entered.Task.WaitAsync(ct);

        facade.Instance.Dispose();
        Assert.False(await IsDisposedAsync(leased, ct), "Dispose leaves the invoker of a call in flight alive.");

        hold.Released.TrySetResult();
        await inFlight.WaitAsync(ct);
        Assert.True(await IsDisposedAsync(leased, ct), "The invoker is disposed when the call in flight ends.");

        // A second Dispose finds nothing left to do and must not throw.
        facade.Instance.Dispose();
        Assert.True(await IsDisposedAsync(leased, ct), "A second Dispose leaves the invoker disposed.");
    }

    [Fact]
    public async Task ACallEndsNormallyWhenTheRoutingTableCannotBeReadAsItEnds()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        HeldCall hold = facade.Forwarder.HoldNextCall();
        Task inFlight = facade.CallAsync(ct);
        HttpMessageInvoker leased = await hold.Entered.Task.WaitAsync(ct);

        // A reload the configuration binder cannot bind makes every read of the routing options throw.
        facade.Routing.Unreadable = true;
        hold.Released.TrySetResult();
        await inFlight.WaitAsync(ct);
        Assert.False(await IsDisposedAsync(leased, ct), "An unreadable routing table retires no invoker.");

        facade.Routing.Unreadable = false;
        await facade.CallAsync(ct);
        Assert.True(ReferenceEquals(facade.Forwarder.Invokers[^1], leased), "The target's invoker is reused once the routing table is readable.");
    }

    [Theory]
    [InlineData(DisposedBeforeCall)]
    [InlineData(DisposedDuringResolution)]
    public async Task ACallThatMeetsDisposeIsRefusedWithoutForwarding(string timing)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ForwarderUnderTest facade = new();

        await facade.CallAsync(ct);
        HttpMessageInvoker idle = facade.Forwarder.Invokers[0];

        switch (timing)
        {
            case DisposedBeforeCall:
                facade.Instance.Dispose();
                break;

            case DisposedDuringResolution:
                // Dispose runs after the call's own disposal check, while the call resolves its target,
                // which is the window in which a call used to create an invoker nothing would dispose.
                facade.Routing.OnNextRead(facade.Instance.Dispose);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown disposal timing.");
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.CallAsync(ct));

        Assert.True(facade.Forwarder.Invokers.Count == 1, "A call that meets Dispose never reaches the forwarder.");
        Assert.True(await IsDisposedAsync(idle, ct), "Dispose disposes an invoker no call is using at once.");
    }

    // Every target serves both surfaces, as the default target must, and no operation is listed, so
    // every request goes to the default target.
    private static RoutingOptions RoutingFor(int urlIndex, int? targetTimeoutSeconds) => new()
    {
        DefaultTarget = TargetName,
        Targets =
        {
            [TargetName] = new RoutingTarget
            {
                RestBaseUrl = string.Create(CultureInfo.InvariantCulture, $"http://rest-{urlIndex}.invalid:65000"),
                SoapBaseUrl = string.Create(CultureInfo.InvariantCulture, $"http://soap-{urlIndex}.invalid:21001"),
                TimeoutSeconds = targetTimeoutSeconds,
            },
        },
    };

    private static void Reload(ForwarderUnderTest facade, string change, int reload)
    {
        switch (change)
        {
            case UrlChange:
                facade.Routing.CurrentValue = RoutingFor(reload, targetTimeoutSeconds: null);
                break;

            case TargetTimeoutChange:
                facade.Routing.CurrentValue = RoutingFor(urlIndex: 0, BackendTimeoutSeconds + reload);
                break;

            case BackendTimeoutChange:
                facade.Backend.CurrentValue = new BackendOptions { TimeoutSeconds = BackendTimeoutSeconds + reload };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown routing change.");
        }
    }

    // A refused connection proves the invoker still sends; a disposed one refuses before it connects.
    private static async Task<bool> IsDisposedAsync(HttpMessageInvoker invoker, CancellationToken ct)
    {
        Uri closed = new(string.Create(
            CultureInfo.InvariantCulture, $"http://127.0.0.1:{StubBackend.GetClosedLoopbackPort()}/"));
        using HttpRequestMessage probe = new(HttpMethod.Get, closed);
        try
        {
            using HttpResponseMessage response = await invoker.SendAsync(probe, ct);
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }

        Assert.Fail("A request to a closed loopback port was answered.");
        return false;
    }

    /// <summary>A forwarder over a recording IHttpForwarder and routing options a test can reload.</summary>
    private sealed class ForwarderUnderTest : IDisposable
    {
        public ForwarderUnderTest()
        {
            OperationRouter router = new(Routing, Backend, OperationCatalogue.Default, NullLogger<OperationRouter>.Instance);
            Instance = new BackendForwarder(
                Forwarder, router, new PassThroughTransformer(), new SoapFaultWriter(), NullLogger<BackendForwarder>.Instance);
        }

        public OptionsMonitorStub<RoutingOptions> Routing { get; } = new(RoutingFor(urlIndex: 0, targetTimeoutSeconds: null));

        public OptionsMonitorStub<BackendOptions> Backend { get; } = new(new BackendOptions { TimeoutSeconds = BackendTimeoutSeconds });

        public RecordingForwarder Forwarder { get; } = new();

        public BackendForwarder Instance { get; }

        /// <summary>Forwards one REST catalogue call, as the REST endpoint does.</summary>
        public Task CallAsync(CancellationToken ct) => Instance.ForwardAsync(
            new DefaultHttpContext { RequestAborted = ct },
            Surface.Rest,
            OperationCatalogue.Default.GetById(OperationId));

        public void Dispose() => Instance.Dispose();
    }

    /// <summary>
    /// An options monitor whose value a test replaces, as a configuration reload does, which can run an
    /// action inside the next read, and which can fail every read as a reload the binder rejects does.
    /// </summary>
    private sealed class OptionsMonitorStub<TOptions>(TOptions initial) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        private TOptions _value = initial;
        private Action? _onNextRead;
        private volatile bool _unreadable;

        public TOptions CurrentValue
        {
            get
            {
                Interlocked.Exchange(ref _onNextRead, null)?.Invoke();
                return _unreadable
                    ? throw new InvalidOperationException("The options cannot be bound.")
                    : Volatile.Read(ref _value);
            }

            set => Volatile.Write(ref _value, value);
        }

        /// <summary>When true, every read of <see cref="CurrentValue"/> throws, as binding a malformed reload does.</summary>
        public bool Unreadable
        {
            get => _unreadable;
            set => _unreadable = value;
        }

        /// <summary>Runs <paramref name="action"/> once, inside the next read of <see cref="CurrentValue"/>.</summary>
        public void OnNextRead(Action action) => Volatile.Write(ref _onNextRead, action);

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    /// <summary>
    /// Records the invoker of every call and answers it at once, or holds it in flight when a test asked
    /// for that with <see cref="HoldNextCall"/>.
    /// </summary>
    private sealed class RecordingForwarder : IHttpForwarder
    {
        private readonly ConcurrentQueue<HeldCall> _holds = new();
        private readonly List<HttpMessageInvoker> _invokers = [];
        private readonly Lock _gate = new();

        /// <summary>The invoker of every call so far, in call order.</summary>
        public IReadOnlyList<HttpMessageInvoker> Invokers
        {
            get
            {
                lock (_gate)
                {
                    return [.. _invokers];
                }
            }
        }

        /// <summary>Holds the next call in flight until the returned call is released.</summary>
        public HeldCall HoldNextCall()
        {
            HeldCall hold = new();
            _holds.Enqueue(hold);
            return hold;
        }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer) =>
            SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer, CancellationToken.None);

        public async ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _invokers.Add(httpClient);
            }

            if (_holds.TryDequeue(out HeldCall? hold))
            {
                hold.Entered.TrySetResult(httpClient);
                await hold.Released.Task.WaitAsync(cancellationToken);
            }

            return ForwarderError.None;
        }
    }

    /// <summary>A call held in flight: entered with its invoker, and ended when released.</summary>
    private sealed class HeldCall
    {
        public TaskCompletionSource<HttpMessageInvoker> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
