// Per-operation routing (class RoutingTests) through the facade's real REST and SOAP sockets. Each case
// moves exactly one operation to a second target by changing one
// Routing:Operations:<Rest|Soap>:<jadeMethod> key, calls all 22 catalogue operations at their normal
// addresses, and asserts that the moved operation alone reaches stub B, through the same URL and with
// B's response (status, Content-Type and body) unchanged, while the other 21 still reach stub A, the
// default "backend" target, and return A's. The two stubs send Content-Types that differ in media type
// and charset, so a dropped, rewritten or swapped Content-Type fails. REST and SOAP each get a case
// because the facade matches them differently: REST by verb and path, SOAP by the SOAPAction header and
// then by the request body.
//
// Request-line pass-through (class RequestLinePassThroughTests) adds a third stub C as
// Routing:DefaultTarget, distinct from the backend A, and sends each request over a raw socket so that
// its request line reaches the facade byte for byte. Its raw-target, method-token and asterisk-form
// cases prove that the shared RestOperationMatcher, applied to the raw path alone, decides which target
// a REST request reaches, whichever endpoint ASP.NET Core routing selected for it, and that the target
// receives the caller's method token and request target unchanged, whatever their form.
//
// The results report (harness/src/Erewhon.Harness/Checks/FacadeTestResults.cs) counts test classes by
// the name suffixes "RoutingTests", "FailureBehaviourTests" and "ListenerIsolationTests", and every
// class ending in "RoutingTests" counts as the per-operation routing check, which must stay the two
// reroute cases alone. Every other class in this file is therefore a class of its own whose name ends
// in none of the three suffixes.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// assertion's file and line and the assertion message, so every message in this file is built only
// from run-invariant values: fixed literals, operation and case ids, stub ids, target and listener
// names, configuration paths, methods, request targets, SOAP actions, content types, status codes and
// byte counts. None holds a port assigned at run time, a URL or Host value that carries one, an elapsed
// time or a GUID, which is also why no Assert.Single, Assert.Empty or Assert.Equal is applied to a
// recorded StubRequest: its dump would print the Host header and with it a port.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Admin;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Erewhon.Facade.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Proves that one routing key moves one operation and nothing else (the per-operation routing check of
/// the results report), on the facade hosted on real loopback Kestrel listeners. Every call must return
/// the status, Content-Type and body bytes of the stub it reached, unchanged.
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

    // Each stub sends its own Content-Type, and the two differ in both media type and charset, so a
    // response whose Content-Type the facade dropped, rewrote or took from the other stub cannot pass.
    private static readonly StubContentType BackendContentType =
        new("text/plain; charset=utf-8", "text/plain", "utf-8");

    private static readonly StubContentType RerouteContentType =
        new("application/x-erewhon-stub-b; charset=iso-8859-1", "application/x-erewhon-stub-b", "iso-8859-1");

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
        await using StubBackend backend = await StubBackend.StartAsync(
            BackendStubId, new StubBackendOptions { ExtraHeaders = BackendContentType.AsResponseHeaders() }, ct);
        await using StubBackend stub = await StubBackend.StartAsync(
            RerouteStubId,
            new StubBackendOptions { Status = RerouteStatus, ExtraHeaders = RerouteContentType.AsResponseHeaders() },
            ct);

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
                AssertForwardedUnchanged(
                    op.Id, prepared, response, reached: stub, bypassed: backend, RerouteStatus, RerouteContentType);
            }
            else
            {
                AssertForwardedUnchanged(
                    op.Id, prepared, response, reached: backend, bypassed: stub, BackendStatus, BackendContentType);
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
                rerouted.Id + " without SOAPAction",
                bodyOnly,
                response,
                reached: stub,
                bypassed: backend,
                RerouteStatus,
                RerouteContentType);
        }
    }

    private static Uri SocketFor(FacadeFactory facade, OperationDescriptor op) =>
        op.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;

    private static StubListener ListenerFor(OperationDescriptor op) =>
        op.Surface == Surface.Rest ? StubListener.Rest : StubListener.Soap;

    // Asserts that the call reached 'reached' once and 'bypassed' never, on the listener of the
    // operation's surface, with the caller's method, raw target, body, Content-Type and SOAPAction
    // unchanged and no X-Forwarded-* header added, and that the facade returned the stub's own status,
    // X-Stub-Id, Content-Type (one value, byte for byte, and the media type and charset a client parses
    // from it) and body bytes.
    private static void AssertForwardedUnchanged(
        string label,
        PreparedRequest prepared,
        RawResponse response,
        StubBackend reached,
        StubBackend bypassed,
        int expectedStatus,
        StubContentType expectedContentType)
    {
        IReadOnlyList<StubRequest> received = reached.Received;
        int bypassedCount = bypassed.Received.Count;
        Assert.True(
            received.Count == 1,
            $"{label}: stub {reached.Id} did not receive exactly one request");
        Assert.True(
            bypassedCount == 0,
            $"{label}: stub {bypassed.Id} received an unexpected request, expected none");

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

        // Only fixed values reach these messages: the observed header is whatever the facade produced,
        // so it is compared but never printed.
        string[] contentTypes = response.Headers.TryGetValue(ContentTypeHeader, out string[]? values) ? values : [];
        Assert.True(
            contentTypes.Length == 1 && string.Equals(contentTypes[0], expectedContentType.Value, StringComparison.Ordinal),
            $"{label}: facade response did not carry stub {reached.Id}'s Content-Type {expectedContentType.Value} unchanged as its only Content-Type");
        Assert.True(
            string.Equals(response.MediaType, expectedContentType.MediaType, StringComparison.Ordinal),
            $"{label}: facade response media type is not stub {reached.Id}'s {expectedContentType.MediaType}");
        Assert.True(
            string.Equals(response.CharSet, expectedContentType.CharSet, StringComparison.Ordinal),
            $"{label}: facade response charset is not stub {reached.Id}'s {expectedContentType.CharSet}");

        Assert.True(
            response.Body.AsSpan().SequenceEqual(request.ResponseBody),
            $"{label}: facade response body differs from the bytes stub {reached.Id} sent");
    }

    // A Content-Type a stub sends: the exact header value, and the media type and charset a client
    // parses from it.
    private sealed record StubContentType(string Value, string MediaType, string CharSet)
    {
        // The stub's response headers: this Content-Type in place of the stub's default one.
        public IReadOnlyDictionary<string, string> AsResponseHeaders() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ContentTypeHeader] = Value };
    }
}

/// <summary>
/// Proves, on the facade hosted on real loopback Kestrel listeners, that the operation a REST request
/// is, and the method token and request target its target receives, follow from the caller's raw request
/// line alone: raw-target dispatch, the method token as sent, and the asterisk-form <c>OPTIONS *</c>. Also
/// proves that the setup's teardown releases every stub listener when an earlier part fails to stop.
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

    // The parts of the raw-target setup in the order its teardown disposes them: the facade, then C, B and A.
    private const string FacadePartName = "facade";
    private const string TeardownOrder = FacadePartName + "," + DefaultStubId + "," + RerouteStubId + "," + BackendStubId;

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

    /// <summary>
    /// Drives the teardown of the raw-target setup with four recording parts standing for the facade and
    /// stubs C, B and A, in its disposal order, the named ones failing to stop. Asserts that every part is
    /// disposed exactly once and in that order whatever failed before it, so a facade whose host throws
    /// while stopping leaves no stub listener behind, and that the first failure in disposal order is the
    /// one that propagates once all have been disposed.
    /// </summary>
    /// <param name="failing">The comma-separated names of the parts whose disposal throws.</param>
    /// <param name="expectedMessage">The message of the failure that must propagate.</param>
    [Theory]
    [InlineData(FacadePartName, "facade failed to stop")]
    [InlineData(RerouteStubId, "B failed to stop")]
    [InlineData(FacadePartName + "," + RerouteStubId, "facade failed to stop")]
    public async Task TeardownDisposesEveryPartInOrderAndRethrowsTheFirstFailure(string failing, string expectedMessage)
    {
        string[] failingNames = failing.Split(',');
        var disposed = new ConcurrentQueue<string>();
        IAsyncDisposable[] parts =
        [
            .. TeardownOrder.Split(',').Select(
                name => new RecordingPart(name, failingNames.Contains(name, StringComparer.Ordinal), disposed)),
        ];

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DisposeInOrderAsync(parts).AsTask());

        Assert.True(
            string.Equals(failure.Message, expectedMessage, StringComparison.Ordinal),
            $"teardown with {failing} failing propagated '{failure.Message}', expected '{expectedMessage}'");
        string order = string.Join(",", disposed);
        Assert.True(
            string.Equals(order, TeardownOrder, StringComparison.Ordinal),
            $"teardown with {failing} failing disposed {order}, expected {TeardownOrder}, each part once");
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
            Assert.True(otherCount == 0, $"{label}: stub {other.Id} received an unexpected request, expected none");
        }

        IReadOnlyList<StubRequest> received = reached.Received;
        Assert.True(received.Count == 1, $"{label}: stub {reached.Id} did not receive exactly one request");

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

    // Disposes each part in the order given, each one whatever the ones before it did, so that a part
    // that throws while stopping, such as a facade host, cannot leave the parts after it listening. Once
    // all have been disposed, the first failure is rethrown with its original stack trace; any later one
    // is dropped, because the first is the one that explains the teardown.
    private static async ValueTask DisposeInOrderAsync(params IAsyncDisposable[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ExceptionDispatchInfo? firstFailure = null;
        foreach (IAsyncDisposable part in parts)
        {
            try
            {
                await part.DisposeAsync();
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
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
                // What started stops in reverse order of start, each part whatever the ones before it did.
                // The start failure is the outcome the test reports, so a failure to stop a part is
                // dropped here rather than allowed to replace it, and the start failure is rethrown below.
                try
                {
                    await DisposeInOrderAsync([.. Enumerable.Reverse(started)]);
                }
                catch (Exception)
                {
                    // Dropped, as explained above: every part has already been disposed by now.
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

        // The facade stops first, so no forwarded call is in flight when the stubs stop. Each stub stops
        // even when the facade's host throws while stopping, so no stub listener outlives the test, and
        // that failure is rethrown once all four have been disposed.
        public ValueTask DisposeAsync() => DisposeInOrderAsync(Facade, Fallback, Rerouted, Backend);
    }

    // One part of the raw-target setup in the teardown test: records its name when disposed and, when told
    // to, then fails asynchronously, as a host that throws while stopping does.
    private sealed class RecordingPart(string name, bool fails, ConcurrentQueue<string> disposed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            disposed.Enqueue(name);
            if (fails)
            {
                throw new InvalidOperationException(name + " failed to stop");
            }
        }
    }

}

// SOAP operation resolution (class SoapResolutionTests). Three stubs tell the outcomes apart:
// getAgentNames is routed to stub B, every other operation stays with the backend, stub A, and the
// default target is stub C. The SOAPAction header, quotes stripped, is matched before the body. The
// conflicting cases name one operation in the header and the other in the body, in both directions, so
// a resolver that ignores the header, or reads the body first, sends one of them to the wrong stub; and
// each non-empty unmatched header that leaves the body to decide is one a lenient matcher (ignoring
// case, trimming quotes one by one, or matching a prefix or suffix) would take for getClientNames,
// which the backend serves. Bodies of twice the inspection bound still reach the stub with every byte.
/// <summary>
/// Proves, on the facade hosted on real loopback Kestrel listeners, that a SOAP <c>POST</c> reaches the
/// target of the operation its <c>SOAPAction</c> header names, else of the operation the first child of
/// its SOAP 1.1 <c>Body</c> names within the 64 KiB inspection bound, else the default target, always
/// with the caller's request and the target's response unchanged.
/// </summary>
public sealed class SoapResolutionTests
{
    // The stubs, rerouted target and headers of the per-operation routing cases.
    private const string BackendStubId = RoutingTests.BackendStubId;
    private const string RerouteStubId = RoutingTests.RerouteStubId;
    private const string RerouteTargetName = RoutingTests.RerouteTargetName;
    private const string StubIdHeader = RoutingTests.StubIdHeader;
    private const string ForwardedHeaderPrefix = RoutingTests.ForwardedHeaderPrefix;
    private const int RerouteStatus = RoutingTests.RerouteStatus;
    private const int BackendStatus = 200;

    // The default target, distinct from the backend and from the rerouted stub.
    private const string DefaultStubId = "C";
    private const string DefaultTargetName = "dflt";
    private const int DefaultStatus = 202;

    private const string SoapActionHeader = "SOAPAction";
    private const string ContentTypeHeader = "Content-Type";

    // getAgentNames is routed to stub B; getClientNames stays with the backend, stub A, as the shipped
    // table lists it. Each conflicting case names one of the two in the header and the other in the body.
    private const string ReroutedOperation = "getAgentNames";
    private const string BackendOperation = "getClientNames";

    // Facts neither the catalogue nor the frozen SOAP 1.1 interface defines: an element name no
    // operation has, a namespace the catalogue does not use, and the SOAP 1.2 envelope namespace and
    // content type.
    private const string UnknownElement = "unknownOperation";
    private const string OtherNamespace = "urn:example:other/";
    private const string Soap12EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string Soap12ContentType = "application/soap+xml; charset=utf-8";

    // A Header whose end tag does not match its start tag: the document is malformed before its Body.
    private const string MismatchedHeader = "<soap:Header></soap:Head>";

    private const string PaddingElement = "padding";

    // Twice the inspection bound, so that a padded body puts the first Body child, or the end of the
    // body after a child near its start, a whole bound away from the byte at which inspection stops.
    private const int PaddingLength = 2 * Erewhon.Contracts.Matching.SoapOperationResolver.MaxInspectionBytes;

    /// <summary>
    /// With <c>getAgentNames</c> routed to stub B, the backend stub A and a default target C, posts the
    /// request <paramref name="caseId"/> names to the SOAP endpoint on the SOAP socket and asserts that
    /// only <paramref name="expectedStubId"/> receives it, with the method, raw target, <c>Content-Type</c>,
    /// <c>SOAPAction</c> and body bytes unchanged, and that the facade returns that stub's response.
    /// </summary>
    /// <param name="caseId">The <c>SOAPAction</c> header and body the case sends, built by <see cref="BuildRequest"/>.</param>
    /// <param name="expectedStubId">
    /// A for an operation the backend serves, B for <c>getAgentNames</c>, C for a request that names no operation.
    /// </param>
    [Theory]
    // The SOAPAction header decides, whatever the body holds.
    [InlineData("action B, body A", RerouteStubId)]
    [InlineData("action A, body B", BackendStubId)]
    [InlineData("unquoted action B, body A", RerouteStubId)]
    [InlineData("action B, unknown Body child", RerouteStubId)]
    [InlineData("action B, malformed body naming A", RerouteStubId)]
    [InlineData("action B, empty body", RerouteStubId)]
    [InlineData("action A, Body child B beyond 64 KiB", BackendStubId)]
    // An absent, empty or unmatched SOAPAction lets the first Body child decide.
    [InlineData("empty action, body B", RerouteStubId)]
    [InlineData("quoted empty action, body B", RerouteStubId)]
    [InlineData("action A without its closing quote, body B", RerouteStubId)]
    [InlineData("action A without its opening quote, body B", RerouteStubId)]
    [InlineData("action A in upper case, body B", RerouteStubId)]
    [InlineData("action A with a suffix, body B", RerouteStubId)]
    [InlineData("bare operation name A, body B", RerouteStubId)]
    [InlineData("no action, body A of twice 64 KiB", BackendStubId)]
    // A request that names no operation goes to the default target.
    [InlineData("no action, empty body", DefaultStubId)]
    [InlineData("no action, text before the envelope", DefaultStubId)]
    [InlineData("no action, mismatched tag before Body", DefaultStubId)]
    [InlineData("unknown action, mismatched tag before Body", DefaultStubId)]
    [InlineData("no action, SOAP 1.2 envelope", DefaultStubId)]
    [InlineData("no action, no envelope", DefaultStubId)]
    [InlineData("no action, empty-element Body", DefaultStubId)]
    [InlineData("no action, Body without child", DefaultStubId)]
    [InlineData("no action, Body child in no namespace", DefaultStubId)]
    [InlineData("no action, Body child in another namespace", DefaultStubId)]
    [InlineData("no action, unknown Body child", DefaultStubId)]
    [InlineData("no action, Body child in upper case", DefaultStubId)]
    [InlineData("no action, second Body child B", DefaultStubId)]
    [InlineData("no action, Header child B", DefaultStubId)]
    [InlineData("no action, Body child B beyond 64 KiB of Header", DefaultStubId)]
    [InlineData("no action, Body child B beyond 64 KiB of comment", DefaultStubId)]
    public async Task SoapPostReachesTheTargetItsActionOrBodyNames(string caseId, string expectedStubId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Built before any host starts, so an unknown case id fails without starting one.
        PreparedRequest prepared = BuildRequest(caseId);
        await using ResolutionSetup setup = await ResolutionSetup.StartAsync(ct);

        using HttpClient client = setup.Facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(client, prepared.ToMessage(setup.Facade.SoapBaseAddress), ct);

        AssertReachedOnly(caseId, setup, expectedStubId, prepared, response);
    }

    // The request a case id names: POST to the catalogue's SOAP endpoint path and query with the
    // catalogue's content type, unless the case says otherwise. Every catalogue fact (endpoint, content
    // type, envelope and message namespaces, SOAP actions and request element names) comes from
    // OperationCatalogue.Default. The operation recorded with each request is the one it calls, or for
    // an unresolved one, the operation its body would name.
    private static PreparedRequest BuildRequest(string caseId)
    {
        OperationCatalogue catalogue = OperationCatalogue.Default;
        SoapAddress address = catalogue.Addresses.Soap;
        OperationDescriptor rerouted = catalogue.Find(Surface.Soap, ReroutedOperation);
        OperationDescriptor backend = catalogue.Find(Surface.Soap, BackendOperation);
        SoapBinding reroutedBinding = BindingOf(rerouted);
        SoapBinding backendBinding = BindingOf(backend);

        string envelopeNamespace = address.EnvelopeNamespace;
        string messageNamespace = address.TargetNamespace;
        string reroutedAction = reroutedBinding.Action;
        string backendAction = backendBinding.Action;
        string bodyA = Element(backendBinding.RequestElement, messageNamespace);
        string bodyB = Element(reroutedBinding.RequestElement, messageNamespace);
        string unknownChild = Element(UnknownElement, messageNamespace);
        string padding = new('x', PaddingLength);
        string paddedHeader = Header(Element(PaddingElement, OtherNamespace, padding));

        return caseId switch
        {
            "action B, body A" => Post(rerouted, Quoted(reroutedAction), Envelope(envelopeNamespace, null, bodyA)),
            "action A, body B" => Post(backend, Quoted(backendAction), Envelope(envelopeNamespace, null, bodyB)),
            "unquoted action B, body A" => Post(rerouted, reroutedAction, Envelope(envelopeNamespace, null, bodyA)),
            "action B, unknown Body child" => Post(rerouted, Quoted(reroutedAction), Envelope(envelopeNamespace, null, unknownChild)),
            "action B, malformed body naming A" => Post(rerouted, Quoted(reroutedAction), Envelope(envelopeNamespace, MismatchedHeader, bodyA)),
            "action B, empty body" => Post(rerouted, Quoted(reroutedAction), string.Empty),
            "action A, Body child B beyond 64 KiB" => Post(backend, Quoted(backendAction), Envelope(envelopeNamespace, paddedHeader, bodyB)),

            "empty action, body B" => Post(rerouted, string.Empty, Envelope(envelopeNamespace, null, bodyB)),
            "quoted empty action, body B" => Post(rerouted, Quoted(string.Empty), Envelope(envelopeNamespace, null, bodyB)),
            "action A without its closing quote, body B" => Post(rerouted, "\"" + backendAction, Envelope(envelopeNamespace, null, bodyB)),
            "action A without its opening quote, body B" => Post(rerouted, backendAction + "\"", Envelope(envelopeNamespace, null, bodyB)),
            "action A in upper case, body B" => Post(rerouted, Quoted(backendAction.ToUpperInvariant()), Envelope(envelopeNamespace, null, bodyB)),
            "action A with a suffix, body B" => Post(rerouted, Quoted(backendAction + "Suffix"), Envelope(envelopeNamespace, null, bodyB)),
            "bare operation name A, body B" => Post(rerouted, Quoted(backend.JadeMethod), Envelope(envelopeNamespace, null, bodyB)),
            "no action, body A of twice 64 KiB" => Post(
                backend,
                null,
                Envelope(
                    envelopeNamespace,
                    null,
                    Element(backendBinding.RequestElement, messageNamespace, Element(PaddingElement, null, padding)))),

            "no action, empty body" => Post(rerouted, null, string.Empty),
            "no action, text before the envelope" => Post(rerouted, null, "text" + Envelope(envelopeNamespace, null, bodyB)),
            "no action, mismatched tag before Body" => Post(rerouted, null, Envelope(envelopeNamespace, MismatchedHeader, bodyB)),
            "unknown action, mismatched tag before Body" => Post(
                rerouted, Quoted(address.SoapActionPrefix + UnknownElement), Envelope(envelopeNamespace, MismatchedHeader, bodyB)),
            "no action, SOAP 1.2 envelope" => Post(rerouted, null, Envelope(Soap12EnvelopeNamespace, null, bodyB), Soap12ContentType),
            "no action, no envelope" => Post(rerouted, null, bodyB),
            "no action, empty-element Body" => Post(rerouted, null, Envelope(envelopeNamespace, null, null)),
            "no action, Body without child" => Post(rerouted, null, Envelope(envelopeNamespace, null, " ")),
            "no action, Body child in no namespace" => Post(
                rerouted, null, Envelope(envelopeNamespace, null, Element(reroutedBinding.RequestElement, null))),
            "no action, Body child in another namespace" => Post(
                rerouted, null, Envelope(envelopeNamespace, null, Element(reroutedBinding.RequestElement, OtherNamespace))),
            "no action, unknown Body child" => Post(rerouted, null, Envelope(envelopeNamespace, null, unknownChild)),
            "no action, Body child in upper case" => Post(
                rerouted, null, Envelope(envelopeNamespace, null, Element(reroutedBinding.RequestElement.ToUpperInvariant(), messageNamespace))),
            "no action, second Body child B" => Post(rerouted, null, Envelope(envelopeNamespace, null, unknownChild + bodyB)),
            "no action, Header child B" => Post(rerouted, null, Envelope(envelopeNamespace, Header(bodyB), unknownChild)),
            "no action, Body child B beyond 64 KiB of Header" => Post(rerouted, null, Envelope(envelopeNamespace, paddedHeader, bodyB)),
            "no action, Body child B beyond 64 KiB of comment" => Post(
                rerouted, null, Envelope(envelopeNamespace, "<!--" + padding + "-->", bodyB)),

            _ => throw new ArgumentOutOfRangeException(nameof(caseId), "unknown SOAP resolution case"),
        };
    }

    private static SoapBinding BindingOf(OperationDescriptor operation) =>
        operation.Soap ?? throw new InvalidOperationException("SOAP operation has no SOAP binding");

    // A POST of the UTF-8 'body', without a byte-order mark, to the catalogue's SOAP endpoint path and
    // query; 'soapAction' is the exact header value, null for no SOAPAction header.
    private static PreparedRequest Post(OperationDescriptor operation, string? soapAction, string body, string? contentType = null)
    {
        SoapAddress address = OperationCatalogue.Default.Addresses.Soap;
        return new PreparedRequest(
            operation,
            HttpMethod.Post,
            address.EndpointPathAndQuery,
            Encoding.UTF8.GetBytes(body),
            contentType ?? address.ContentType,
            soapAction);
    }

    private static string Quoted(string value) => "\"" + value + "\"";

    // An envelope in 'envelopeNamespace', bound to the prefix soap, holding 'beforeBody' (a Header, a
    // comment or nothing) and then a Body holding 'bodyContent', or an empty-element Body when it is null.
    private static string Envelope(string envelopeNamespace, string? beforeBody, string? bodyContent) =>
        "<soap:Envelope xmlns:soap=\"" + AttributeValue(envelopeNamespace) + "\">"
        + beforeBody
        + (bodyContent is null ? "<soap:Body/>" : "<soap:Body>" + bodyContent + "</soap:Body>")
        + "</soap:Envelope>";

    private static string Header(string content) => "<soap:Header>" + content + "</soap:Header>";

    // An element with 'content', declaring 'namespaceUri' as its default namespace; with a null
    // namespace it declares none and keeps the namespace in scope where it is placed.
    private static string Element(string localName, string? namespaceUri, string content = "") =>
        "<" + localName
        + (namespaceUri is null ? string.Empty : " xmlns=\"" + AttributeValue(namespaceUri) + "\"")
        + ">" + content + "</" + localName + ">";

    // A value escaped for a double-quoted XML attribute.
    private static string AttributeValue(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    // Asserts that the stub named 'expectedStubId' received the call once on its SOAP listener and the
    // other two stubs none, with the method, raw target, Content-Type, SOAPAction (or its absence) and
    // body bytes exactly as sent and no X-Forwarded-* header, and that the facade returned that stub's
    // own status, X-Stub-Id and body bytes.
    private static void AssertReachedOnly(
        string caseId,
        ResolutionSetup setup,
        string expectedStubId,
        PreparedRequest prepared,
        RawResponse response)
    {
        StubBackend reached = setup.Stub(expectedStubId);
        foreach (StubBackend other in setup.All.Where(stub => !ReferenceEquals(stub, reached)))
        {
            int otherCount = other.Received.Count;
            Assert.True(otherCount == 0, $"{caseId}: stub {other.Id} received an unexpected request, expected none");
        }

        IReadOnlyList<StubRequest> received = reached.Received;
        Assert.True(received.Count == 1, $"{caseId}: stub {reached.Id} did not receive exactly one request");

        StubRequest request = received[0];
        Assert.True(
            request.Listener == StubListener.Soap,
            $"{caseId}: stub {reached.Id} received the call on its {request.Listener} listener, expected {StubListener.Soap}");
        Assert.True(
            string.Equals(request.Method, prepared.Method.Method, StringComparison.Ordinal),
            $"{caseId}: stub {reached.Id} received method {request.Method}, expected {prepared.Method.Method}");
        Assert.True(
            string.Equals(request.RawTarget, prepared.PathAndQuery, StringComparison.Ordinal),
            $"{caseId}: stub {reached.Id} did not receive the request target {prepared.PathAndQuery} unchanged");

        byte[] sentBody = prepared.Body ?? [];
        Assert.True(
            request.Body.AsSpan().SequenceEqual(sentBody),
            $"{caseId}: stub {reached.Id} did not receive the {sentBody.Length} request body bytes unchanged");
        Assert.True(
            string.Equals(request.Header(ContentTypeHeader), prepared.ContentType, StringComparison.Ordinal),
            $"{caseId}: stub {reached.Id} did not receive Content-Type {prepared.ContentType} unchanged");

        if (prepared.SoapAction is null)
        {
            Assert.True(
                request.Header(SoapActionHeader) is null,
                $"{caseId}: stub {reached.Id} received a SOAPAction header the caller did not send");
        }
        else
        {
            Assert.True(
                string.Equals(request.Header(SoapActionHeader), prepared.SoapAction, StringComparison.Ordinal),
                $"{caseId}: stub {reached.Id} did not receive the SOAPAction header as sent, quotes included");
        }

        Assert.False(
            request.Headers.Keys.Any(name => name.StartsWith(ForwardedHeaderPrefix, StringComparison.OrdinalIgnoreCase)),
            $"{caseId}: stub {reached.Id} received an X-Forwarded-* header the caller did not send");

        int expectedStatus = ResolutionSetup.StatusOf(expectedStubId);
        Assert.True(
            response.Status == expectedStatus,
            $"{caseId}: facade answered status {response.Status}, expected stub {reached.Id}'s status {expectedStatus}");
        string? stubId = response.Header(StubIdHeader);
        Assert.True(
            string.Equals(stubId, reached.Id, StringComparison.Ordinal),
            $"{caseId}: facade response carried X-Stub-Id {stubId ?? "(absent)"}, expected {reached.Id}");
        Assert.True(
            response.Body.AsSpan().SequenceEqual(request.ResponseBody),
            $"{caseId}: facade response body differs from the bytes stub {reached.Id} sent");
    }

    // Stub A as target "backend", stub B as target "stub" serving getAgentNames, and stub C as target
    // "dflt", the Routing:DefaultTarget, behind one facade. Every other catalogue operation stays listed
    // with "backend" in the shipped table, so only requests the resolver leaves unresolved reach C.
    private sealed class ResolutionSetup : IAsyncDisposable
    {
        private ResolutionSetup(StubBackend backend, StubBackend rerouted, StubBackend fallback, FacadeFactory facade)
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

        public static async Task<ResolutionSetup> StartAsync(CancellationToken ct)
        {
            OperationDescriptor reroutedOperation = OperationCatalogue.Default.Find(Surface.Soap, ReroutedOperation);

            // The stubs start before the facade: the factory must not run while another host is built.
            List<StubBackend> started = new(3);
            try
            {
                StubBackend backend = await StubBackend.StartAsync(BackendStubId, new StubBackendOptions { Status = BackendStatus }, ct);
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
                            [$"Routing:Operations:{reroutedOperation.Surface}:{reroutedOperation.RoutingKey}"] = RerouteTargetName,
                        },
                    },
                    ct);

                return new ResolutionSetup(backend, rerouted, fallback, facade);
            }
            catch
            {
                // StubBackend.DisposeAsync never throws, so every stub started so far is released.
                for (int index = started.Count - 1; index >= 0; index--)
                {
                    await started[index].DisposeAsync();
                }

                throw;
            }
        }

        public static int StatusOf(string id) => id switch
        {
            BackendStubId => BackendStatus,
            RerouteStubId => RerouteStatus,
            DefaultStubId => DefaultStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(id), "stub id is not A, B or C"),
        };

        public StubBackend Stub(string id) => id switch
        {
            BackendStubId => Backend,
            RerouteStubId => Rerouted,
            DefaultStubId => Fallback,
            _ => throw new ArgumentOutOfRangeException(nameof(id), "stub id is not A, B or C"),
        };

        // The facade stops first, so no forwarded call is in flight when the stubs stop. Each disposal
        // runs even when an earlier one throws, so no stub listener outlives the test; the first failure
        // propagates, because a stub's own disposal never throws.
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Facade.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Fallback.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        await Rerouted.DisposeAsync();
                    }
                    finally
                    {
                        await Backend.DisposeAsync();
                    }
                }
            }
        }
    }
}

// Header pass-through (class PassThroughTransformerTests). A field that a Connection header nominates
// belongs to one hop (RFC 9110, section 7.6.1). The caller's request is written on a raw socket,
// because HttpClient joins repeated header lines into one, and the backend's responses come from a raw
// listener, because Kestrel would not write a response head with Connection lines naming other fields
// or send a head before its body is released. The order check is deterministic: the backend withholds
// its body until the test releases it, and the 10 s bound only ends the wait of a facade that holds the
// head back.
/// <summary>
/// Shows that fields nominated by a <c>Connection</c> header are dropped on the request and the response
/// hop while the other end-to-end fields pass, mixed lists, keep-alive connections and every request
/// framing Kestrel accepts included, that the response head is not held until the body, and that a
/// deadline passing after the head aborts the response.
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

    // Lengths of the padding chunk extension of a chunked request: over 8 KiB, and 1 MiB, which reaches
    // the facade over many reads.
    private const int LongExtensionPadding = 9000;
    private const int ManyReadsExtensionPadding = 1024 * 1024;

    // The largest message head the raw reader accepts, as Kestrel's default request header limit.
    private const int MaxMessageHeadBytes = 32 * 1024;

    // Backend:TimeoutSeconds for the call whose body never follows its head.
    private const int MidBodyDeadlineSeconds = 2;

    // The fixed message of an HttpClient exchange, head and body, that outlived RawExchangeTimeout.
    private const string ExchangeBoundExpiredMessage = "facade response did not end within the 15 s exchange bound";

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

    // The window, from just before the request is sent, in which the facade must abort a body that never
    // follows its head: not before the deadline, and within the failure tests' 5 s timeout bound.
    private static readonly TimeSpan MidBodyAbortLowerBound = TimeSpan.FromSeconds(MidBodyDeadlineSeconds);
    private static readonly TimeSpan MidBodyAbortUpperBound = TimeSpan.FromSeconds(5);

    // True when the exchange bound, or the raw client's own timeout of the same length, ended an
    // HttpClient exchange rather than the test run did: a cancellation the test did not request, or any
    // failure once the bound has passed.
    private static bool ExchangeBoundEnded(Exception exception, CancellationTokenSource exchange, CancellationToken ct) =>
        !ct.IsCancellationRequested && (exception is OperationCanceledException || exchange.IsCancellationRequested);

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
    /// Sends, on one keep-alive connection, a request whose body is chunked with chunk extensions and a
    /// trailer field, then a request whose <c>Connection</c> line nominates a custom field next to
    /// <c>keep-alive</c>. Asserts that the chunked body arrives whole and that the field is dropped from
    /// the second request, which is only found by following the first request's chunked framing,
    /// however long its chunk extensions are.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    /// <param name="pipelined">Whether both requests are written before either response is read.</param>
    /// <param name="extensionPadding">
    /// The length of a padding extension on the first chunk-size line and on the last chunk's, or 0 for
    /// none.
    /// </param>
    [Theory]
    [InlineData("Rest.postCreateAgent", false, 0)]
    [InlineData("Rest.postCreateAgent", true, 0)]
    [InlineData("Rest.postCreateAgent", false, LongExtensionPadding)]
    [InlineData("Rest.postCreateAgent", true, LongExtensionPadding)]
    [InlineData("Rest.postCreateAgent", false, ManyReadsExtensionPadding)]
    [InlineData("Rest.postCreateAgent", true, ManyReadsExtensionPadding)]
    [InlineData("Soap.updateAgentWithDTO", false, 0)]
    [InlineData("Soap.updateAgentWithDTO", true, 0)]
    [InlineData("Soap.updateAgentWithDTO", false, LongExtensionPadding)]
    [InlineData("Soap.updateAgentWithDTO", true, LongExtensionPadding)]
    [InlineData("Soap.updateAgentWithDTO", false, ManyReadsExtensionPadding)]
    [InlineData("Soap.updateAgentWithDTO", true, ManyReadsExtensionPadding)]
    public async Task NominatedFieldIsDroppedFromRequestAfterChunkedRequest(string operationId, bool pipelined, int extensionPadding)
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
                ChunkedRequest(prepared, body, socket, [hopLine], extensionPadding),
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
    /// Sends, on one keep-alive connection, a request whose <c>Content-Length</c> value Kestrel reads as
    /// the length of its body although it is not a plain digit string, then a request whose
    /// <c>Connection</c> line nominates a custom field next to <c>keep-alive</c>. Asserts that the first
    /// body arrives whole and that the field is dropped from the second request, which is only found by
    /// framing the first body at that length.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    /// <param name="form">
    /// The form of the first request's <c>Content-Length</c> value: the length after a plus sign, between
    /// a vertical tab and a form feed, or before a NUL.
    /// </param>
    [Theory]
    [InlineData("Rest.postCreateAgent", "plus-sign")]
    [InlineData("Rest.postCreateAgent", "surrounding-white-space")]
    [InlineData("Rest.postCreateAgent", "trailing-nul")]
    [InlineData("Soap.updateAgentWithDTO", "plus-sign")]
    [InlineData("Soap.updateAgentWithDTO", "surrounding-white-space")]
    [InlineData("Soap.updateAgentWithDTO", "trailing-nul")]
    public async Task NominatedFieldIsDroppedFromRequestAfterContentLengthInAnotherForm(string operationId, string form)
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
        string length = body.Length.ToString(CultureInfo.InvariantCulture);
        string contentLength = form switch
        {
            "plus-sign" => "+" + length,
            "surrounding-white-space" => "\v" + length + "\f",
            "trailing-nul" => length + "\0",
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unknown Content-Length form"),
        };
        StringBuilder firstHead = RequestHeadStart(prepared, socket, [hopLine])
            .Append(ContentLengthHeader).Append(": ").Append(contentLength).Append(CrLf)
            .Append(CrLf);

        IReadOnlyList<int> statuses = await SendRawOnOneConnectionAsync(
            socket,
            [
                [.. Encoding.ASCII.GetBytes(firstHead.ToString()), .. body],
                ContentLengthRequest(prepared, body, socket, [KeepAliveNominatingLine, hopLine]),
            ],
            pipelined: false,
            ct);

        Assert.Equal([200, 200], statuses);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 2, "backend did not receive exactly two requests");
        Assert.Equal(body, received[0].Body);
        Assert.True(received[0].Header(HopField) == HopValue, "un-nominated custom field of the first request did not reach the backend");
        AssertForwardedWithContentLength(received[1], prepared, body);
        Assert.True(received[1].Header(HopField) is null, "custom field nominated after a request with another Content-Length form reached the backend");
    }

    /// <summary>
    /// Sends a request whose request line runs the first byte of its target into its version, with no
    /// space between them, which Kestrel dispatches with that byte alone as the target, and whose
    /// <c>Connection</c> line nominates a custom field next to <c>keep-alive</c>. Asserts that the
    /// request reaches the backend at that target, with its body whole and without the field.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent")]
    [InlineData("Soap.updateAgentWithDTO")]
    public async Task NominatedFieldIsDroppedFromRequestWhoseTargetRunsIntoItsVersion(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        Uri socket = SocketFor(facade, operation);
        byte[] request = ContentLengthRequest(prepared, body, socket, [KeepAliveNominatingLine, HopField + ": " + HopValue]);

        // The request line "<method> /xHTTP/1.1" replaces the request's own, which ends at its first LF.
        int requestLineLength = Array.IndexOf(request, (byte)'\n') + 1;
        byte[] runOn = [.. Encoding.ASCII.GetBytes(prepared.Method.Method + " /xHTTP/1.1" + CrLf), .. request.AsSpan(requestLineLength)];

        int status = await SendRawAsync(socket, runOn, ct);

        Assert.Equal(200, status);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 1, "backend did not receive exactly one request");
        Assert.Equal("/", received[0].RawTarget);
        Assert.Equal(body, received[0].Body);
        Assert.Equal(EndToEndValue, received[0].Header(EndToEndHeader));
        Assert.True(received[0].Header(HopField) is null, "custom field nominated by a request whose target runs into its version reached the backend");
    }

    /// <summary>
    /// Writes at once, on one connection, a request whose <c>Connection</c> line nominates a custom field
    /// next to <c>keep-alive</c> and then a request head with an obsolete line folding, which Kestrel
    /// rejects. Asserts that the first request reaches the backend without the field and that the second
    /// is answered with 400 without reaching it.
    /// </summary>
    /// <param name="operationId">A catalogue operation whose request carries a body.</param>
    [Theory]
    [InlineData("Rest.postCreateAgent")]
    [InlineData("Soap.updateAgentWithDTO")]
    public async Task NominatedFieldIsDroppedFromRequestPipelinedBeforeRejectedHead(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl }, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        byte[] body = prepared.Body ?? throw new InvalidOperationException("operation request carries no body");
        Uri socket = SocketFor(facade, operation);
        byte[] rejected = Encoding.ASCII.GetBytes(
            prepared.Method.Method + " " + prepared.PathAndQuery + " HTTP/1.1" + CrLf
            + "Host: " + socket.Authority + CrLf
            + " folded: line" + CrLf
            + CrLf);

        IReadOnlyList<int> statuses = await SendRawOnOneConnectionAsync(
            socket,
            [ContentLengthRequest(prepared, body, socket, [KeepAliveNominatingLine, HopField + ": " + HopValue]), rejected],
            pipelined: true,
            ct);

        Assert.Equal([200, 400], statuses);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 1, "backend did not receive exactly one request");
        AssertForwardedWithContentLength(received[0], prepared, body);
        Assert.True(received[0].Header(HopField) is null, "custom field nominated by a request pipelined before a rejected head reached the backend");
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
    /// after it. The head and the body are read under one 15 s exchange bound, so a facade that never
    /// ends the response fails the test instead of hanging it.
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

        // HttpClient's own timeout ends once the head is read, so one bound covers the head and the body.
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exchange.CancelAfter(RawExchangeTimeout);

        byte[] body;
        bool headBeforeBody;
        string? endToEnd;
        long? contentLength;
        int status;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, exchange.Token);
            headBeforeBody = !bodySent.Task.IsCompleted;
            release.TrySetResult();

            status = (int)response.StatusCode;
            endToEnd = response.Headers.NonValidated.TryGetValues(EndToEndHeader, out HeaderStringValues values)
                ? values.ToString()
                : null;
            contentLength = response.Content.Headers.ContentLength;
            body = await response.Content.ReadAsByteArrayAsync(exchange.Token);
        }
        catch (Exception exception) when (ExchangeBoundEnded(exception, exchange, ct))
        {
            Assert.Fail(ExchangeBoundExpiredMessage);
            throw new UnreachableException();
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
    /// the response the backend started is never re-headed with the facade's timeout response. The
    /// abort must come between 2 and 5 s after the request is sent, and the head and the body are read
    /// under one 15 s exchange bound, so a facade that never aborts fails the test instead of hanging it.
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

        // HttpClient's own timeout ends once the head is read, so one bound covers the head and the body.
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exchange.CancelAfter(RawExchangeTimeout);

        int status;
        bool bodyAborted;
        TimeSpan bodyEndedAfter;
        long sendStart = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, exchange.Token);
            status = (int)response.StatusCode;
            try
            {
                await response.Content.ReadAsByteArrayAsync(exchange.Token);
                bodyAborted = false;
            }
            catch (Exception exception)
                when ((exception is HttpRequestException or IOException) && !exchange.IsCancellationRequested)
            {
                // The facade broke off the response; a read the exchange bound cancelled is no abort.
                bodyAborted = true;
            }

            bodyEndedAfter = Stopwatch.GetElapsedTime(sendStart);
        }
        catch (Exception exception) when (ExchangeBoundEnded(exception, exchange, ct))
        {
            Assert.Fail(ExchangeBoundExpiredMessage);
            throw new UnreachableException();
        }
        catch (Exception exception) when (IsTransportFailure(exception, ct))
        {
            Assert.Fail("facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        Assert.Equal(200, status);
        Assert.True(bodyAborted, "response body was not aborted after the deadline passed");
        Assert.True(bodyEndedAfter >= MidBodyAbortLowerBound, "response body was aborted before the 2 s deadline passed");
        Assert.True(bodyEndedAfter <= MidBodyAbortUpperBound, "response body was not aborted within 5 s of the request");
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
    // a chunk extension, the rest as a second chunk, then the last chunk and one trailer field. A non-zero
    // extension padding adds a further extension of that many padding bytes to the first chunk-size line
    // and to the last chunk's.
    private static byte[] ChunkedRequest(
        PreparedRequest prepared,
        byte[] body,
        Uri socket,
        IEnumerable<string> fieldLines,
        int extensionPadding)
    {
        if (body.Length < 2)
        {
            throw new InvalidOperationException("operation request body is too short for two chunks");
        }

        int split = body.Length / 2;
        StringBuilder head = RequestHeadStart(prepared, socket, fieldLines)
            .Append(TransferEncodingHeader).Append(": chunked").Append(CrLf)
            .Append(CrLf);
        string padding = extensionPadding > 0 ? ";pad=" + new string('a', extensionPadding) : string.Empty;
        string firstChunkLine = split.ToString("x", CultureInfo.InvariantCulture) + ";ext=one" + padding + CrLf;
        string secondChunkLine = (body.Length - split).ToString("x", CultureInfo.InvariantCulture) + CrLf;
        string lastChunkAndTrailer = "0" + padding + CrLf + "X-Trailer: t" + CrLf + CrLf;
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
                    when (exception is OperationCanceledException or SocketException or ObjectDisposedException
                        || (exception is InvalidOperationException && _stopping.IsCancellationRequested))
                {
                    // The backend is stopping. TcpListener.AcceptTcpClientAsync throws InvalidOperationException
                    // when DisposeAsync ran Stop() between two accepts, so during disposal that ends the loop too.
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

// Configuration of the facade under test (class FacadeTestHostSettingsTests). FacadeFactory starts the
// facade with its shipped appsettings.json and the settings the test supplies, and with nothing taken
// from the environment of the process that runs the tests: a Routing__*, Backend__* or Facade__*
// variable exported in the shell, plain or with the ASPNETCORE_ or DOTNET_ prefix the host also reads,
// never reaches the facade. The shell cases set process environment variables for the length of one
// test and restore them afterwards, which is safe because the assembly runs no two tests at once
// (FacadeFactory.cs).
/// <summary>
/// Proves that an extra setting overrides the typed or factory value of the same key whatever its letter
/// case, and that environment variables of the test process neither reroute an operation of the facade
/// under test nor stop its start-up.
/// </summary>
public sealed class FacadeTestHostSettingsTests
{
    private const string BackendStubId = "A";
    private const string OverrideStubId = "B";
    private const string StubIdHeader = "X-Stub-Id";
    private const int BackendStatus = 200;

    // Distinct from stub A's 200, so a response that came from the wrong stub cannot pass.
    private const int OverrideStatus = 203;

    // Backend:TimeoutSeconds in the shipped appsettings.json.
    private const int ShippedTimeoutSeconds = 30;

    // Given through the typed properties. The timeout differs from the shipped one, so a typed value the
    // facade did not apply cannot pass either.
    private const int TypedTimeoutSeconds = 7;
    private const string TypedWsdlPath = "no-wsdl-typed/ErewhonWebService.wsdl";

    // Given through Settings. Both WSDL paths are relative to the test's base directory and name no file,
    // so the facade starts with wsdl: absent either way.
    private const int ExtraTimeoutSeconds = 2;
    private const string ExtraTimeoutSecondsText = "2";
    private const string ExtraWsdlPath = "no-wsdl-extra/ErewhonWebService.wsdl";

    // Base URLs of a backend the facade never calls: it starts without connecting to its targets.
    private const string UnusedRestBaseUrl = "http://127.0.0.1:2";
    private const string UnusedSoapBaseUrl = "http://127.0.0.1:3";

    // An ambient reroute exported in the test process's environment: one operation moved to a new target
    // whose base URL is a closed loopback port. The facade under test must ignore it, so the operation still
    // reaches stub A and the facade defines no target of this name.
    private const string ShellTargetName = "orders";
    private const string ShellTargetBaseUrl = "http://127.0.0.1:2";

    /// <summary>
    /// Gives <paramref name="key"/> a value through <see cref="FacadeTestSettings.Settings"/> that its
    /// typed property also sets, and checks the timeout and WSDL path the facade applies.
    /// </summary>
    /// <param name="key">The extra setting's key, in the letter case of the row.</param>
    /// <param name="value">The extra setting's value.</param>
    /// <param name="expectedTimeoutSeconds">The <c>Backend:TimeoutSeconds</c> the facade must apply.</param>
    /// <param name="expectedWsdlPath">The <c>Facade:WsdlPath</c> the facade must apply.</param>
    [Theory]
    [InlineData("Backend:TimeoutSeconds", ExtraTimeoutSecondsText, ExtraTimeoutSeconds, TypedWsdlPath)]
    [InlineData("backend:TIMEOUTSECONDS", ExtraTimeoutSecondsText, ExtraTimeoutSeconds, TypedWsdlPath)]
    [InlineData("Facade:WsdlPath", ExtraWsdlPath, TypedTimeoutSeconds, ExtraWsdlPath)]
    [InlineData("FACADE:wsdlpath", ExtraWsdlPath, TypedTimeoutSeconds, ExtraWsdlPath)]
    public async Task ExtraSettingOverridesTypedValueOfTheSameKey(
        string key, string value, int expectedTimeoutSeconds, string expectedWsdlPath)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = UnusedRestBaseUrl,
                BackendSoapBaseUrl = UnusedSoapBaseUrl,
                BackendTimeoutSeconds = TypedTimeoutSeconds,
                WsdlPath = TypedWsdlPath,
                Settings = { [key] = value },
            },
            ct);

        int timeoutSeconds = facade.Services.GetRequiredService<OperationRouter>().Describe().BackendTimeoutSeconds;
        string wsdlPath = facade.Services.GetRequiredService<IOptions<FacadeOptions>>().Value.WsdlPath;
        Assert.True(
            timeoutSeconds == expectedTimeoutSeconds,
            "Backend:TimeoutSeconds: facade applied another value than the extra or typed setting gave it");
        Assert.True(
            string.Equals(wsdlPath, expectedWsdlPath, StringComparison.Ordinal),
            "Facade:WsdlPath: facade applied another value than the extra or typed setting gave it");
    }

    /// <summary>
    /// Gives <c>Routing:Targets:backend:RestBaseUrl</c> stub B's REST base URL through
    /// <see cref="FacadeTestSettings.Settings"/> while the typed backend URLs are stub A's, and checks that
    /// a REST operation reaches stub B alone while a SOAP operation still reaches stub A.
    /// </summary>
    /// <param name="key">The extra setting's key, in the letter case of the row.</param>
    [Theory]
    [InlineData("Routing:Targets:backend:RestBaseUrl")]
    [InlineData("routing:TARGETS:Backend:restbaseurl")]
    public async Task ExtraBaseUrlOverridesTypedBackendBaseUrl(string key)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationDescriptor restOperation = OperationCatalogue.Default.Find(Surface.Rest, "getCompany");
        OperationDescriptor soapOperation = OperationCatalogue.Default.Find(Surface.Soap, "getAgentNames");

        // Both stubs start before the facade: the factory must not run while another host is built.
        await using StubBackend typed = await StubBackend.StartAsync(BackendStubId, ct: ct);
        await using StubBackend extra = await StubBackend.StartAsync(
            OverrideStubId, new StubBackendOptions { Status = OverrideStatus }, ct);

        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = typed.RestBaseUrl,
                BackendSoapBaseUrl = typed.SoapBaseUrl,
                Settings = { [key] = extra.RestBaseUrl },
            },
            ct);
        using HttpClient client = facade.CreateRawClient();

        RawResponse rest = await FacadeFactory.SendAsync(
            client, OperationRequests.For(restOperation).ToMessage(facade.RestBaseAddress), ct);
        Assert.True(
            extra.ReceivedOn(StubListener.Rest).Count == 1 && extra.Received.Count == 1,
            "Rest.getCompany did not reach the REST base URL of the extra setting exactly once");
        Assert.True(typed.Received.Count == 0, "Rest.getCompany reached the typed REST base URL the extra setting replaced");
        Assert.True(
            rest.Status == OverrideStatus && string.Equals(rest.Header(StubIdHeader), OverrideStubId, StringComparison.Ordinal),
            "Rest.getCompany: facade did not return the response of the extra setting's REST base URL");

        typed.ClearLog();
        extra.ClearLog();

        RawResponse soap = await FacadeFactory.SendAsync(
            client, OperationRequests.For(soapOperation).ToMessage(facade.SoapBaseAddress), ct);
        Assert.True(
            typed.ReceivedOn(StubListener.Soap).Count == 1 && typed.Received.Count == 1,
            "Soap.getAgentNames did not reach the typed SOAP base URL exactly once");
        Assert.True(extra.Received.Count == 0, "Soap.getAgentNames reached the stub that only the extra REST base URL names");
        Assert.True(
            soap.Status == BackendStatus && string.Equals(soap.Header(StubIdHeader), BackendStubId, StringComparison.Ordinal),
            "Soap.getAgentNames: facade did not return the response of the typed SOAP base URL");
    }

    /// <summary>
    /// Exports a rerouting of <paramref name="operation"/> to a new target, with <paramref name="prefix"/>,
    /// in the test process's environment, and checks that the facade defines no such target and still
    /// forwards the operation to its backend, stub A.
    /// </summary>
    /// <param name="prefix">The environment variable prefix: none, <c>ASPNETCORE_</c> or <c>DOTNET_</c>.</param>
    /// <param name="surface">The rerouted operation's surface, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="operation">The rerouted operation's JADE method name, its routing key.</param>
    [Theory]
    [InlineData("", "Rest", "getCompany")]
    [InlineData("ASPNETCORE_", "Soap", "getAgentNames")]
    [InlineData("DOTNET_", "Rest", "getCompany")]
    public async Task ShellRoutingVariablesDoNotReachTheFacade(string prefix, string surface, string operation)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationDescriptor rerouted = OperationCatalogue.Default.Find(Enum.Parse<Surface>(surface), operation);

        using var shell = new ProcessEnvironment();
        shell.Set($"{prefix}Routing__Operations__{surface}__{operation}", ShellTargetName);
        shell.Set($"{prefix}Routing__Targets__{ShellTargetName}__{surface}BaseUrl", ShellTargetBaseUrl);

        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, ct: ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = backend.RestBaseUrl,
                BackendSoapBaseUrl = backend.SoapBaseUrl,
            },
            ct);

        using HttpClient client = facade.CreateRawClient();
        Uri socket = rerouted.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        RawResponse response = await FacadeFactory.SendAsync(client, OperationRequests.For(rerouted).ToMessage(socket), ct);
        Assert.True(
            backend.Received.Count == 1,
            "the operation the shell environment reroutes did not reach stub A exactly once");
        Assert.True(
            response.Status == BackendStatus && string.Equals(response.Header(StubIdHeader), BackendStubId, StringComparison.Ordinal),
            "facade did not return stub A's response for the operation the shell environment reroutes");

        // A variable for a key appsettings.json does not hold, such as a new target, would otherwise reach
        // the facade even from a source that appsettings.json overrides.
        RoutingSnapshot table = facade.Services.GetRequiredService<OperationRouter>().Describe();
        Assert.False(
            table.Targets.Any(target => string.Equals(target.Name, ShellTargetName, StringComparison.OrdinalIgnoreCase)),
            "facade defined a routing target exported in the shell environment");
    }

    /// <summary>
    /// Exports, with <paramref name="prefix"/>, a routing key that names no catalogue operation and a
    /// <c>Backend:TimeoutSeconds</c> of 0, either of which fails the facade's start-up validation, and
    /// checks that the facade starts with the shipped timeout.
    /// </summary>
    /// <param name="prefix">The environment variable prefix: none, <c>ASPNETCORE_</c> or <c>DOTNET_</c>.</param>
    [Theory]
    [InlineData("")]
    [InlineData("ASPNETCORE_")]
    [InlineData("DOTNET_")]
    public async Task ShellVariablesThatFailValidationDoNotStopStartUp(string prefix)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using var shell = new ProcessEnvironment();
        shell.Set($"{prefix}Routing__Operations__Rest__notAnOperation", "backend");
        shell.Set($"{prefix}Backend__TimeoutSeconds", "0");

        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = UnusedRestBaseUrl,
                BackendSoapBaseUrl = UnusedSoapBaseUrl,
            },
            ct);

        int timeoutSeconds = facade.Services.GetRequiredService<OperationRouter>().Describe().BackendTimeoutSeconds;
        Assert.True(
            timeoutSeconds == ShippedTimeoutSeconds,
            "Backend:TimeoutSeconds: facade applied another value than the shipped appsettings.json gives it");
    }

    // Sets process environment variables for one test and, on disposal, restores each one's previous value,
    // or its absence, in the reverse order of setting.
    private sealed class ProcessEnvironment : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _saved = [];

        public void Set(string name, string value)
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            for (int index = _saved.Count - 1; index >= 0; index--)
            {
                Environment.SetEnvironmentVariable(_saved[index].Name, _saved[index].Previous);
            }

            _saved.Clear();
        }
    }
}

// WSDL dispatch (class WsdlDispatchTests) on the facade's real SOAP socket, with stub A as target
// "backend" and a distinct stub C as Routing:DefaultTarget. "No WSDL" is the factory's default
// Facade:WsdlPath, a file that does not exist. The WSDL is a small fixture written for each test into a
// directory of its own under the temporary directory, never contracts/wsdl/ErewhonWebService.wsdl
// itself. Its CRLF line breaks, single-quoted attributes and doubled space would not survive a facade
// that re-serialised the document, nor its non-ASCII text one that re-encoded it. The expected address
// is built from the literal endpoint the facade must publish, not from the catalogue or the facade's
// own escaping. Requests are written on a raw socket, so the request target and the Host header reach
// the facade exactly as written.
/// <summary>
/// Proves that a WSDL request on the SOAP socket publishes the loaded WSDL with only its
/// <c>soap:address/@location</c> set to the caller's XML-escaped address and reaches no target, that
/// every other GET, and a WSDL request while no WSDL is loaded, reaches the default target unchanged,
/// and that <c>/admin/info</c> reports the WSDL as present or absent.
/// </summary>
public sealed class WsdlDispatchTests
{
    private const string BackendStubId = RoutingTests.BackendStubId;
    private const string StubIdHeader = RoutingTests.StubIdHeader;
    private const string ForwardedHeaderPrefix = RoutingTests.ForwardedHeaderPrefix;

    // The default target, distinct from the backend A in its status and Content-Type, so a GET answered
    // by any other target, or by the facade itself, cannot pass for a forwarded one.
    private const string DefaultStubId = "C";
    private const string DefaultTargetName = "dflt";
    private const int DefaultStatus = 202;
    private const string DefaultContentType = "text/x-erewhon-default; charset=us-ascii";

    private const string HostHeader = "Host";
    private const string ContentTypeHeader = "Content-Type";
    private const string ContentLengthHeader = "Content-Length";

    // The WSDL setting of a case, as /admin/info names it.
    private const string Present = "present";
    private const string Absent = "absent";

    // The SOAP endpoint and its WSDL request, as the consumer and the published WSDL address them.
    private const string EndpointTarget = "/jade/jadehttp.dll?WebServiceHTTP&serviceName=ErewhonWebService&listName=WebServiceHTTP";
    private const string WsdlTarget = EndpointTarget + "&wsdl";

    // What the published soap:address/@location holds after its scheme and authority, XML-escaped.
    private const string EscapedEndpoint = "/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP";

    // A Host header naming neither the facade's socket nor any listener of this host.
    private const string ForgedHost = "erewhon.example:21001";

    private const int WsdlStatus = 200;
    private const string WsdlContentType = "text/xml; charset=utf-8";

    private const string SoapBindingNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
    private const string AddressElement = "address";
    private const string LocationAttribute = "location";

    private const string AdminInfoWsdlMember = "wsdl";

    /// <summary>
    /// With a WSDL loaded, sends a WSDL request to the SOAP socket and asserts that the facade answers
    /// it itself: status 200, <c>text/xml; charset=utf-8</c>, and exactly the fixture's bytes with only
    /// the <c>soap:address/@location</c> value replaced by <c>http://</c>, the <c>Host</c> header as sent,
    /// XML-escaped, and the escaped endpoint, with a matching <c>Content-Length</c>. Neither stub
    /// receives anything. The address follows the <c>Host</c> header, whatever socket the request used.
    /// </summary>
    /// <param name="caseId">The fixed name of the case, used in every message.</param>
    /// <param name="target">The origin-form request target; its query asks for the WSDL.</param>
    /// <param name="host">The <c>Host</c> header as sent; null for the authority of the facade's SOAP socket.</param>
    /// <param name="escapedHost">How the published address must carry <paramref name="host"/>; null when unchanged.</param>
    [Theory]
    [InlineData("socket-authority", WsdlTarget, null, null)]
    [InlineData("forged-host-and-port", WsdlTarget, ForgedHost, null)]
    [InlineData("host-without-port", WsdlTarget, "erewhon.example", null)]
    [InlineData("host-with-ampersand", WsdlTarget, "a&b.example:21001", "a&amp;b.example:21001")]
    [InlineData("bare-key", "/jade/jadehttp.dll?wsdl", ForgedHost, null)]
    [InlineData("upper-case-key-with-value", "/jade/jadehttp.dll?WSDL=1", ForgedHost, null)]
    [InlineData("percent-encoded-key", "/jade/jadehttp.dll?%77sdl", ForgedHost, null)]
    public async Task WsdlRequestPublishesTheLoadedWsdlWithTheCallersAddress(string caseId, string target, string? host, string? escapedHost)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using WsdlSetup setup = await WsdlSetup.StartAsync(withWsdl: true, ct);

        string sentHost = host ?? setup.Facade.SoapBaseAddress.Authority;
        RawResponse response = await FacadeFactory.SendRawAsync(setup.Facade.SoapPort, "GET " + target + " HTTP/1.1", sentHost, ct);

        AssertNothingForwarded(caseId, setup);
        Assert.True(response.Status == WsdlStatus, $"{caseId}: facade answered status {response.Status}, expected {WsdlStatus}");
        Assert.True(
            string.Equals(response.Header(ContentTypeHeader), WsdlContentType, StringComparison.Ordinal),
            $"{caseId}: published WSDL does not carry Content-Type {WsdlContentType}");
        Assert.True(response.Header(StubIdHeader) is null, $"{caseId}: published WSDL carries an X-Stub-Id header");

        byte[] expected =
        [
            .. Encoding.UTF8.GetBytes(WsdlFixture.Prefix),
            .. Encoding.UTF8.GetBytes("http://" + (escapedHost ?? sentHost) + EscapedEndpoint),
            .. Encoding.UTF8.GetBytes(WsdlFixture.Suffix),
        ];
        Assert.True(
            response.Body.AsSpan().SequenceEqual(expected),
            $"{caseId}: published WSDL is not the fixture's bytes with only soap:address/@location replaced by the caller's escaped address");
        Assert.True(
            string.Equals(response.Header(ContentLengthHeader), expected.Length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            $"{caseId}: published WSDL's Content-Length does not match its bytes");

        string? location = ReadSingleAddressLocation(caseId, response.Body);
        Assert.True(
            string.Equals(location, "http://" + sentHost + EndpointTarget, StringComparison.Ordinal),
            $"{caseId}: published soap:address/@location does not read back as the caller's address");
    }

    /// <summary>
    /// Sends a GET to the SOAP socket with a forged <c>Host</c> and asserts that only the default target
    /// C receives it, on its SOAP listener, as a <c>GET</c> with the exact raw request target, its own
    /// authority as <c>Host</c>, no body and no <c>X-Forwarded-*</c> header, and that the facade returns
    /// C's status, <c>X-Stub-Id</c>, <c>Content-Type</c> and body unchanged. Without a WSDL this holds for
    /// WSDL requests too; with one, for every GET that is not a WSDL request on the endpoint path.
    /// </summary>
    /// <param name="wsdl"><c>present</c> to load the fixture WSDL, <c>absent</c> to load none.</param>
    /// <param name="caseId">The fixed name of the case, used in every message.</param>
    /// <param name="target">The origin-form request target.</param>
    [Theory]
    [InlineData(Absent, "wsdl-request", WsdlTarget)]
    [InlineData(Absent, "wsdl-bare-key", "/jade/jadehttp.dll?wsdl")]
    [InlineData(Absent, "wsdl-upper-case-key-with-value", "/jade/jadehttp.dll?WSDL=1")]
    [InlineData(Absent, "wsdl-percent-encoded-key", "/jade/jadehttp.dll?%77sdl")]
    [InlineData(Absent, "endpoint-without-wsdl-key", EndpointTarget)]
    [InlineData(Absent, "other-path-with-wsdl-key", "/jade/other.dll?wsdl")]
    [InlineData(Present, "endpoint-without-wsdl-key", EndpointTarget)]
    [InlineData(Present, "endpoint-without-query", "/jade/jadehttp.dll")]
    [InlineData(Present, "wsdl-as-value", "/jade/jadehttp.dll?x=wsdl")]
    [InlineData(Present, "longer-key", "/jade/jadehttp.dll?wsdlx")]
    [InlineData(Present, "double-encoded-key", "/jade/jadehttp.dll?%2577sdl")]
    [InlineData(Present, "other-path-with-wsdl-key", "/jade/other.dll?wsdl")]
    [InlineData(Present, "endpoint-sub-path-with-wsdl-key", "/jade/jadehttp.dll/x?wsdl")]
    public async Task GetIsForwardedUnchangedToTheDefaultTarget(string wsdl, string caseId, string target)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using WsdlSetup setup = await WsdlSetup.StartAsync(IsPresent(wsdl), ct);

        RawResponse response = await FacadeFactory.SendRawAsync(setup.Facade.SoapPort, "GET " + target + " HTTP/1.1", ForgedHost, ct);

        string label = $"{caseId} with the WSDL {wsdl}";
        int backendCount = setup.Backend.Received.Count;
        Assert.True(backendCount == 0, $"{label}: stub {BackendStubId} received an unexpected request, expected none");

        IReadOnlyList<StubRequest> received = setup.Fallback.Received;
        Assert.True(received.Count == 1, $"{label}: stub {DefaultStubId} did not receive exactly one request");

        StubRequest request = received[0];
        Assert.True(
            request.Listener == StubListener.Soap,
            $"{label}: stub {DefaultStubId} received the call on its {request.Listener} listener, expected {StubListener.Soap}");
        Assert.True(
            string.Equals(request.Method, HttpMethods.Get, StringComparison.Ordinal),
            $"{label}: stub {DefaultStubId} received method {request.Method}, expected {HttpMethods.Get}");
        Assert.True(
            string.Equals(request.RawTarget, target, StringComparison.Ordinal),
            $"{label}: stub {DefaultStubId} did not receive the request target unchanged");

        string ownAuthority = new Uri(setup.Fallback.SoapBaseUrl).Authority;
        Assert.True(
            string.Equals(request.Header(HostHeader), ownAuthority, StringComparison.Ordinal),
            $"{label}: stub {DefaultStubId} did not receive its own Soap listener authority as Host");
        Assert.False(
            request.Headers.Keys.Any(name => name.StartsWith(ForwardedHeaderPrefix, StringComparison.OrdinalIgnoreCase)),
            $"{label}: stub {DefaultStubId} received an X-Forwarded-* header the caller did not send");
        Assert.True(request.Body.Length == 0, $"{label}: stub {DefaultStubId} received a request body the caller did not send");

        Assert.True(
            response.Status == DefaultStatus,
            $"{label}: facade answered status {response.Status}, expected stub {DefaultStubId}'s status {DefaultStatus}");
        string? stubId = response.Header(StubIdHeader);
        Assert.True(
            string.Equals(stubId, DefaultStubId, StringComparison.Ordinal),
            $"{label}: facade response carried X-Stub-Id {stubId ?? "(absent)"}, expected {DefaultStubId}");
        Assert.True(
            string.Equals(response.Header(ContentTypeHeader), DefaultContentType, StringComparison.Ordinal),
            $"{label}: facade response does not carry stub {DefaultStubId}'s Content-Type unchanged");
        Assert.True(
            response.Body.AsSpan().SequenceEqual(request.ResponseBody),
            $"{label}: facade response body differs from the bytes stub {DefaultStubId} sent");
    }

    /// <summary>
    /// Asserts that <c>/admin/info</c>, on the admin socket, reports <c>"wsdl": "present"</c> while the
    /// fixture WSDL is loaded and <c>"wsdl": "absent"</c> while none is, and forwards nothing.
    /// </summary>
    /// <param name="wsdl"><c>present</c> to load the fixture WSDL, <c>absent</c> to load none; the status expected.</param>
    [Theory]
    [InlineData(Present)]
    [InlineData(Absent)]
    public async Task AdminInfoReportsWhetherTheWsdlIsPublished(string wsdl)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using WsdlSetup setup = await WsdlSetup.StartAsync(IsPresent(wsdl), ct);

        RawResponse response = await FacadeFactory.SendRawAsync(
            setup.Facade.AdminPort,
            "GET " + Erewhon.Contracts.Admin.AdminRoutes.Info + " HTTP/1.1",
            setup.Facade.AdminBaseAddress.Authority,
            ct);

        string label = $"admin info with the WSDL {wsdl}";
        AssertNothingForwarded(label, setup);
        Assert.True(response.Status == 200, $"{label}: facade answered status {response.Status}, expected 200");

        string? reported = null;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty(AdminInfoWsdlMember, out System.Text.Json.JsonElement status)
                && status.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                reported = status.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            Assert.Fail($"{label}: body is not JSON");
        }

        Assert.True(
            string.Equals(reported, wsdl, StringComparison.Ordinal),
            $"{label}: the wsdl member is {reported ?? "(no string)"}, expected {wsdl}");
    }

    private static bool IsPresent(string wsdl) => wsdl switch
    {
        Present => true,
        Absent => false,
        _ => throw new ArgumentOutOfRangeException(nameof(wsdl), "WSDL setting is neither present nor absent"),
    };

    private static void AssertNothingForwarded(string label, WsdlSetup setup)
    {
        foreach (StubBackend stub in setup.All)
        {
            int count = stub.Received.Count;
            Assert.True(count == 0, $"{label}: stub {stub.Id} received an unexpected request, expected none");
        }
    }

    // Reads the published document as an XML parser does, which fails a document that is not
    // well-formed, and returns the value of its one soap:address/@location with every escape resolved.
    private static string? ReadSingleAddressLocation(string label, byte[] document)
    {
        System.Xml.XmlReaderSettings settings = new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit };
        int count = 0;
        string? location = null;
        try
        {
            using MemoryStream stream = new(document, writable: false);
            using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element
                    && string.Equals(reader.LocalName, AddressElement, StringComparison.Ordinal)
                    && string.Equals(reader.NamespaceURI, SoapBindingNamespace, StringComparison.Ordinal))
                {
                    count++;
                    location = reader.GetAttribute(LocationAttribute);
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            Assert.Fail($"{label}: published WSDL is not well-formed XML");
        }

        Assert.True(count == 1, $"{label}: published WSDL does not hold exactly one soap:address element");
        return location;
    }

    // The fixture WSDL, split around the value of its one soap:address/@location. It is modelled on
    // contracts/wsdl/ErewhonWebService.wsdl: the XML declaration, the stand-in label comment, the
    // catalogue's WSDL, SOAP-binding and message namespaces and service name, and one catalogue
    // operation. The CRLF line breaks, single-quoted attributes and doubled space are what a
    // re-serialising facade would change, and the non-ASCII text what a re-encoding one would.
    private static class WsdlFixture
    {
        private const string LineBreak = "\r\n";

        // The address the file holds before any request rewrites it, XML-escaped as in the file.
        private const string OriginalLocation = "http://fixture.invalid:15001" + EscapedEndpoint;

        /// <summary>Every character of the file before the location value, up to its opening quote.</summary>
        public static readonly string Prefix = BuildPrefix();

        /// <summary>Every character of the file after the location value, from its closing quote.</summary>
        public static readonly string Suffix = string.Join(
            LineBreak,
            "\" />",
            "    </wsdl:port>",
            "  </wsdl:service>",
            "</wsdl:definitions>",
            string.Empty);

        /// <summary>The file's bytes: UTF-8 without a byte-order mark.</summary>
        public static readonly byte[] Bytes = Encoding.UTF8.GetBytes(Prefix + OriginalLocation + Suffix);

        private static string BuildPrefix()
        {
            OperationCatalogue catalogue = OperationCatalogue.Default;
            SoapAddress address = catalogue.Addresses.Soap;
            SoapBinding operation = catalogue.Find(Surface.Soap, "getAgentNames").Soap
                ?? throw new InvalidOperationException("getAgentNames has no SOAP binding");
            string wsdlNamespace = address.WsdlNamespaces["wsdl"];
            string soapNamespace = address.WsdlNamespaces["soap"];
            string messageNamespace = address.TargetNamespace;
            string service = address.ServiceName;
            string element = operation.RequestElement;

            return string.Join(
                LineBreak,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<!-- " + Erewhon.Contracts.SourceLabels.WsdlHeaderComment + " -->",
                $"<wsdl:definitions xmlns:wsdl=\"{wsdlNamespace}\"  xmlns:soap=\"{soapNamespace}\" xmlns:tns='{messageNamespace}' targetNamespace=\"{messageNamespace}\" name=\"{service}\">",
                "  <wsdl:documentation>WSDL dispatch fixture \u2014 caf\u00E9 &amp; r\u00E9sum\u00E9</wsdl:documentation>",
                $"  <wsdl:message name=\"{element}SoapIn\" />",
                $"  <wsdl:portType name=\"{service}Soap\">",
                $"    <wsdl:operation name=\"{element}\">",
                $"      <wsdl:input message='tns:{element}SoapIn' />",
                "    </wsdl:operation>",
                "  </wsdl:portType>",
                $"  <wsdl:binding name=\"{service}Soap\" type='tns:{service}Soap'>",
                "    <soap:binding transport=\"http://schemas.xmlsoap.org/soap/http\" style=\"document\" />",
                $"    <wsdl:operation name=\"{element}\">",
                $"      <soap:operation soapAction=\"{operation.Action}\" style=\"document\" />",
                "    </wsdl:operation>",
                "  </wsdl:binding>",
                $"  <wsdl:service name=\"{service}\">",
                $"    <wsdl:port name=\"{service}Soap\" binding=\"tns:{service}Soap\">",
                "      <soap:address location=\"");
        }
    }

    // Stub A as target "backend" and stub C as target "dflt", the Routing:DefaultTarget, behind one
    // facade. With a WSDL, Facade:WsdlPath names the fixture, written into a new directory under the
    // temporary directory; without one, the factory's default path, which does not exist, applies.
    private sealed class WsdlSetup : IAsyncDisposable
    {
        private const string FixtureFileName = "ErewhonWebService.wsdl";

        private readonly string? _fixtureDirectory;

        private WsdlSetup(StubBackend backend, StubBackend fallback, FacadeFactory facade, string? fixtureDirectory)
        {
            Backend = backend;
            Fallback = fallback;
            Facade = facade;
            _fixtureDirectory = fixtureDirectory;
        }

        public StubBackend Backend { get; }

        public StubBackend Fallback { get; }

        public FacadeFactory Facade { get; }

        public IEnumerable<StubBackend> All => [Backend, Fallback];

        public static async Task<WsdlSetup> StartAsync(bool withWsdl, CancellationToken ct)
        {
            string? fixtureDirectory = null;
            StubBackend? backend = null;
            StubBackend? fallback = null;
            try
            {
                string? wsdlPath = null;
                if (withWsdl)
                {
                    fixtureDirectory = Path.Combine(Path.GetTempPath(), "erewhon-facade-wsdl-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(fixtureDirectory);
                    wsdlPath = Path.Combine(fixtureDirectory, FixtureFileName);
                    await File.WriteAllBytesAsync(wsdlPath, WsdlFixture.Bytes, ct);
                }

                // The stubs start before the facade: the factory must not run while another host is built.
                backend = await StubBackend.StartAsync(BackendStubId, null, ct);
                fallback = await StubBackend.StartAsync(
                    DefaultStubId,
                    new StubBackendOptions
                    {
                        Status = DefaultStatus,
                        ExtraHeaders = new Dictionary<string, string> { [ContentTypeHeader] = DefaultContentType },
                    },
                    ct);

                FacadeFactory facade = await FacadeFactory.StartAsync(
                    new FacadeTestSettings
                    {
                        BackendRestBaseUrl = backend.RestBaseUrl,
                        BackendSoapBaseUrl = backend.SoapBaseUrl,
                        WsdlPath = wsdlPath,
                        Settings =
                        {
                            [$"Routing:Targets:{DefaultTargetName}:RestBaseUrl"] = fallback.RestBaseUrl,
                            [$"Routing:Targets:{DefaultTargetName}:SoapBaseUrl"] = fallback.SoapBaseUrl,
                            ["Routing:DefaultTarget"] = DefaultTargetName,
                        },
                    },
                    ct);

                return new WsdlSetup(backend, fallback, facade, fixtureDirectory);
            }
            catch
            {
                await ReleaseAsync(facade: null, fallback, backend, fixtureDirectory);
                throw;
            }
        }

        public ValueTask DisposeAsync() => ReleaseAsync(Facade, Fallback, Backend, _fixtureDirectory);

        // The facade stops first, so no forwarded call is in flight when the stubs stop. Each later
        // resource is still released when an earlier one's disposal throws.
        private static async ValueTask ReleaseAsync(
            FacadeFactory? facade, StubBackend? fallback, StubBackend? backend, string? fixtureDirectory)
        {
            try
            {
                if (facade is not null)
                {
                    await facade.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (fallback is not null)
                    {
                        await fallback.DisposeAsync();
                    }
                }
                finally
                {
                    try
                    {
                        if (backend is not null)
                        {
                            await backend.DisposeAsync();
                        }
                    }
                    finally
                    {
                        if (fixtureDirectory is not null && Directory.Exists(fixtureDirectory))
                        {
                            Directory.Delete(fixtureDirectory, recursive: true);
                        }
                    }
                }
            }
        }
    }
}

// Credential handling of the routing table's target base URLs (class BaseUrlCredentialTests). The
// facade carries and adds no credential. A failure names the entry by its configuration path and the
// rule the value breaks; the start-up error log, the line Program.cs writes to standard error and the
// rejected-reload log entry are all built from those failures. Every case runs in-process against
// RoutingOptionsValidator, OperationRouter and AdminEndpoints.DisplayBaseUrl directly: a table the
// validator rejects never starts a host, and the sanitiser is the only part of /admin/info that a URL
// passes through. The URLs are synthetic, use no live port and hold no real credential.
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

// Routing configuration lifecycle (class RoutingConfigurationLifecycleTests). A reload of the hosted
// facade sets each key on every provider of its configuration root and then reloads the root, as a
// changed settings file or environment does. The test's in-memory source is the last provider and keeps
// its values when reloaded, so the keys win over appsettings.json. The base URLs of the start-up cases,
// which never start, and of the in-process cases are synthetic and never contacted.
/// <summary>
/// Proves that an invalid routing table stops the facade's start-up with a failure naming the entry, that
/// one changed key moves one operation in the running facade, that a rejected reload keeps the last valid
/// table, the start-up table included, in service until a valid reload, and that a table compiled from
/// older options never replaces a newer one.
/// </summary>
public sealed class RoutingConfigurationLifecycleTests
{
    private const string BackendTarget = "backend";
    private const string StubTarget = "stub";
    private const string OrdersTarget = "orders";

    private const string BackendStubId = "A";
    private const string StubStubId = "B";
    private const string StubIdHeader = "X-Stub-Id";

    // Distinct from the backend stub's 200, so a response that came from the wrong stub cannot pass.
    private const int StubStatus = 203;

    // The 13 REST and 9 SOAP operations carried over from ErewhonRestService and ErewhonWebService.
    private const int CatalogueOperationCount = 22;

    private const string GetCompanyKey = "Routing:Operations:Rest:getCompany";
    private const string BackendTimeoutKey = "Routing:Targets:backend:TimeoutSeconds";

    // OperationRouter's rejected-reload and reload-accepted log entries.
    private const int ReloadRejectedEventId = 1101;
    private const int ReloadAcceptedEventId = 1102;

    // How an in-process case rejects a reload: through RoutingOptionsValidator, or through the
    // configuration binder for a value that does not convert to its option's type.
    private const string ValidatorRejection = "validator";
    private const string BinderRejection = "binder";

    // The start-up cases never bind a listener or send a request, so these URLs are never contacted.
    private const string SyntheticRestBaseUrl = "http://127.0.0.1:1";
    private const string SyntheticSoapBaseUrl = "http://127.0.0.1:2";

    // Ends every wait of the ordered-publication case, so a regression fails it instead of hanging it.
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(10);

    private static readonly string RouterCategory = typeof(OperationRouter).FullName!;

    /// <summary>
    /// A routing table that breaks one rule stops the facade's start-up, through the validator the facade
    /// registers or the shape check it runs before binding, with exactly one failure naming the entry.
    /// </summary>
    /// <param name="key">The one routing key added to the shipped table.</param>
    /// <param name="value">The key's value.</param>
    /// <param name="failure">The one failure start-up must report, as RoutingOptionsValidator words it.</param>
    [Theory]
    [InlineData("Routing:Operations:Rest:nope", BackendTarget, "Routing:Operations:Rest:nope is not a catalogue REST operation")]
    [InlineData(GetCompanyKey, "nowhere", "Routing:Operations:Rest:getCompany names undefined target 'nowhere'")]
    [InlineData(
        "Routing:Operations:Soap:getAgentNames",
        StubTarget,
        "Routing:Targets:stub has no SoapBaseUrl but serves Routing:Operations:Soap:getAgentNames")]
    [InlineData(BackendTimeoutKey, "0", "Routing:Targets:backend:TimeoutSeconds must be greater than 0 (was 0)")]
    [InlineData(BackendTimeoutKey, "abc", "Routing:Targets:backend:TimeoutSeconds must be an integer greater than 0 (was 'abc')")]
    [InlineData("Routing:Unknown", "x", "Routing:Unknown is not a routing setting (expected DefaultTarget, Targets or Operations)")]
    public async Task InvalidStartUpRoutingStopsTheFacadeNamingTheEntry(string key, string value, string failure)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Target stub has a REST base URL only, which is valid while no SOAP operation is routed to it.
        var settings = new FacadeTestSettings
        {
            BackendRestBaseUrl = SyntheticRestBaseUrl,
            BackendSoapBaseUrl = SyntheticSoapBaseUrl,
            StubRestBaseUrl = SyntheticRestBaseUrl,
            Settings = { [key] = value },
        };

        // The host's start-up validation throws, and the facade's entry point leaves the exception to the
        // in-process caller unchanged.
        OptionsValidationException validation = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await using FacadeFactory started = await FacadeFactory.StartAsync(settings, ct);
        });

        Assert.True(validation.OptionsType == typeof(RoutingOptions), "start-up was stopped by options other than RoutingOptions");
        Assert.Equal(failure, Assert.Single(validation.Failures));
    }

    /// <summary>
    /// Changing one operation's routing key in the running facade's configuration moves that operation,
    /// and only it, from stub A (target <c>backend</c>) to stub B (target <c>stub</c>) from the next
    /// request on; before the change every catalogue operation reaches stub A.
    /// </summary>
    /// <param name="surface">The moved operation's surface, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="operation">The moved operation's JADE method name, its routing key.</param>
    [Theory]
    [InlineData("Rest", "getCompany")]
    [InlineData("Soap", "getAgentNames")]
    public async Task ChangingOneOperationKeyInTheRunningFacadeMovesOnlyThatOperation(string surface, string operation)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationDescriptor moved = OperationCatalogue.Default.Find(Enum.Parse<Surface>(surface), operation);

        // Both stubs start before the facade: the factory must not run while another host is built.
        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using StubBackend stub = await StubBackend.StartAsync(StubStubId, new StubBackendOptions { Status = StubStatus }, ct);

        // Target stub is defined from the start, and no operation is routed to it.
        await using FacadeFactory facade = await FacadeFactory.StartAsync(Hosted(backend, stub, logs: null), ct);
        using HttpClient client = facade.CreateRawClient();

        await AssertEveryOperationReachesAsync(client, facade, backend, stub, movedId: null, "before the change", ct);

        IReadOnlyList<OptionsValidationException> rejections =
            Reload(facade, ($"Routing:Operations:{moved.Surface}:{moved.RoutingKey}", StubTarget));
        Assert.True(rejections.Count == 0, "the facade rejected the reload that moves one operation");

        await AssertEveryOperationReachesAsync(client, facade, backend, stub, moved.Id, "after the change", ct);
    }

    /// <summary>
    /// A reload the facade rejects before its first request, through the validator or the shape check,
    /// leaves the table published while the facade started serving every request and is logged once as
    /// an error naming the entry; the next valid reload replaces that table and is logged once as accepted.
    /// </summary>
    /// <param name="key">The routing key the rejected reload sets.</param>
    /// <param name="value">The value that makes the reload invalid.</param>
    /// <param name="failure">The failure the rejection and its log entry must name.</param>
    [Theory]
    [InlineData(GetCompanyKey, "nowhere", "Routing:Operations:Rest:getCompany names undefined target 'nowhere'")]
    [InlineData(BackendTimeoutKey, "abc", "Routing:Targets:backend:TimeoutSeconds must be an integer greater than 0 (was 'abc')")]
    public async Task ReloadRejectedBeforeTheFirstRequestKeepsTheStartUpTableInService(string key, string value, string failure)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationCatalogue catalogue = OperationCatalogue.Default;
        OperationDescriptor getCompany = catalogue.Find(Surface.Rest, "getCompany");
        OperationDescriptor[] unmoved = [catalogue.Find(Surface.Rest, "getAllAgents"), catalogue.Find(Surface.Soap, "getAgentNames")];
        var logs = new LogCapture();

        await using StubBackend backend = await StubBackend.StartAsync(BackendStubId, null, ct);
        await using StubBackend stub = await StubBackend.StartAsync(StubStubId, new StubBackendOptions { Status = StubStatus }, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(Hosted(backend, stub, logs), ct);
        using HttpClient client = facade.CreateRawClient();

        // No request has reached the facade yet, so only the table published while it started can serve
        // the requests below. A rejection the reload itself surfaces must be this entry's.
        IReadOnlyList<OptionsValidationException> rejections = Reload(facade, (key, value));
        Assert.True(
            rejections.All(rejection => rejection.OptionsType == typeof(RoutingOptions) && rejection.Failures.Contains(failure)),
            "the reload was rejected for another reason than the entry it breaks");

        foreach (OperationDescriptor op in unmoved.Prepend(getCompany))
        {
            await AssertCallReachesAsync(client, facade, op, reached: backend, bypassed: stub, $"{op.Id} after the rejected reload", ct);
        }

        IReadOnlyList<CapturedLog> rejected = RouterLog(logs, ReloadRejectedEventId);
        Assert.True(rejected.Count == 1, "the rejected reload was not logged exactly once");
        Assert.True(rejected[0].Level == LogLevel.Error, "the rejected reload was not logged as an error");
        Assert.True(
            rejected[0].Message.Contains(failure, StringComparison.Ordinal),
            "the rejected-reload log entry does not name the rejected entry");
        Assert.True(RouterLog(logs, ReloadAcceptedEventId).Count == 0, "a reload was logged as accepted while it stayed rejected");

        // One valid reload withdraws the rejected value and moves getCompany to target stub.
        rejections = Reload(facade, (key, null), (GetCompanyKey, StubTarget));
        Assert.True(rejections.Count == 0, "the facade rejected the valid reload");

        await AssertCallReachesAsync(client, facade, getCompany, reached: stub, bypassed: backend, $"{getCompany.Id} after the valid reload", ct);
        foreach (OperationDescriptor op in unmoved)
        {
            await AssertCallReachesAsync(client, facade, op, reached: backend, bypassed: stub, $"{op.Id} after the valid reload", ct);
        }

        IReadOnlyList<CapturedLog> accepted = RouterLog(logs, ReloadAcceptedEventId);
        Assert.True(accepted.Count == 1, "the valid reload after the rejected one was not logged exactly once as accepted");
        Assert.True(accepted[0].Level == LogLevel.Information, "the accepted reload was not logged as information");
        Assert.True(RouterLog(logs, ReloadRejectedEventId).Count == 1, "the rejected reload was logged again after the valid one");
    }

    /// <summary>
    /// An initialised router moves exactly the operation whose key a reload changes: every other
    /// operation and every unmatched request still resolves to the default target, and the routing
    /// snapshot that <c>/admin/info</c> reports shows the same routes.
    /// </summary>
    /// <param name="surface">The moved operation's surface, <c>Rest</c> or <c>Soap</c>.</param>
    /// <param name="operation">The moved operation's JADE method name, its routing key.</param>
    [Theory]
    [InlineData("Rest", "getCompany")]
    [InlineData("Soap", "getAgentNames")]
    public void InitializedRouterMovesOnlyTheChangedOperationOnReload(string surface, string operation)
    {
        OperationDescriptor moved = OperationCatalogue.Default.Find(Enum.Parse<Surface>(surface), operation);
        var logs = new LogCapture();
        using ILoggerFactory loggers = LoggersFor(logs);
        var routing = new ReloadableOptionsMonitor<RoutingOptions>(Table());
        OperationRouter router = Router(routing, new ReloadableOptionsMonitor<BackendOptions>(new BackendOptions()), loggers);

        router.Initialize();
        AssertResolvesTo(router, moved: null, StubTarget, "start-up");
        IReadOnlySet<ResolvedTarget> startUpTargets = router.TargetsInService();

        routing.Reload(Table(moved));
        AssertResolvesTo(router, moved, StubTarget, "after the reload");

        Assert.False(
            ReferenceEquals(router.TargetsInService(), startUpTargets),
            "the reload that moves an operation did not rebuild the routing table");
        Assert.True(logs.Entries.Count == 0, "a valid reload was logged as rejected or as accepted after a rejection");
    }

    /// <summary>
    /// A reload rejected after the router published its start-up table and before the first resolution,
    /// by the validator (routing options) or by the configuration binder (backend options), leaves that
    /// table in service and is logged once as an error; the next valid reload replaces the table and is
    /// logged once as accepted. Before any table is published the same rejection propagates unchanged,
    /// so invalid start-up configuration still stops the facade.
    /// </summary>
    /// <param name="rejection">How the reload is rejected: <c>validator</c> or <c>binder</c>.</param>
    [Theory]
    [InlineData(ValidatorRejection)]
    [InlineData(BinderRejection)]
    public void ReloadRejectedBeforeTheFirstResolutionKeepsTheStartUpTableUntilAValidReload(string rejection)
    {
        OperationDescriptor getCompany = OperationCatalogue.Default.Find(Surface.Rest, "getCompany");
        var logs = new LogCapture();
        using ILoggerFactory loggers = LoggersFor(logs);

        var unpublishedRouting = new ReloadableOptionsMonitor<RoutingOptions>(Table());
        var unpublishedBackend = new ReloadableOptionsMonitor<BackendOptions>(new BackendOptions());
        OperationRouter unpublished = Router(unpublishedRouting, unpublishedBackend, loggers);
        Exception refused = Reject(rejection, unpublishedRouting, unpublishedBackend).Rejected;
        Exception thrown = Assert.ThrowsAny<Exception>(unpublished.Initialize);
        Assert.True(ReferenceEquals(thrown, refused), "a router with no published table did not rethrow the rejected configuration unchanged");
        Assert.True(logs.Entries.Count == 0, "a router with no published table logged the rejected configuration");

        var routing = new ReloadableOptionsMonitor<RoutingOptions>(Table());
        var backend = new ReloadableOptionsMonitor<BackendOptions>(new BackendOptions());
        OperationRouter router = Router(routing, backend, loggers);
        router.Initialize();
        (Exception rejected, string optionsType, string failure) = Reject(rejection, routing, backend);

        AssertResolvesTo(router, moved: null, StubTarget, "after the rejected reload");
        AssertResolvesTo(router, moved: null, StubTarget, "after the rejected reload, again");

        IReadOnlyList<CapturedLog> rejectedEntries = RouterLog(logs, ReloadRejectedEventId);
        Assert.True(rejectedEntries.Count == 1, "the rejected reload was not logged exactly once");
        Assert.True(rejectedEntries[0].Level == LogLevel.Error, "the rejected reload was not logged as an error");
        Assert.True(
            Equals(rejectedEntries[0].Value("OptionsType"), optionsType)
                && Equals(rejectedEntries[0].Value("ExceptionType"), rejected.GetType().FullName)
                && rejectedEntries[0].Message.Contains(failure, StringComparison.Ordinal),
            "the rejected-reload log entry does not name the rejected options, the rejection and its entry");
        Assert.True(RouterLog(logs, ReloadAcceptedEventId).Count == 0, "a reload was logged as accepted while it stayed rejected");

        // A valid reload of both sections, which also moves getCompany to target stub.
        routing.Reload(Table(getCompany));
        backend.Reload(new BackendOptions());
        AssertResolvesTo(router, getCompany, StubTarget, "after the valid reload");

        IReadOnlyList<CapturedLog> accepted = RouterLog(logs, ReloadAcceptedEventId);
        Assert.True(accepted.Count == 1, "the valid reload after the rejected one was not logged exactly once as accepted");
        Assert.True(accepted[0].Level == LogLevel.Information, "the accepted reload was not logged as information");
        Assert.True(RouterLog(logs, ReloadRejectedEventId).Count == 1, "the rejected reload was logged again after the valid one");
    }

    /// <summary>
    /// A resolution that read older options before a newer table was published neither resolves from
    /// those options nor publishes a table of them: it resolves from the newer table, which stays the one
    /// in service.
    /// </summary>
    [Fact]
    public async Task AResolutionThatReadOlderOptionsNeverReplacesANewerTable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationDescriptor getCompany = OperationCatalogue.Default.Find(Surface.Rest, "getCompany");
        var routing = new ReloadableOptionsMonitor<RoutingOptions>(Table());
        OperationRouter router = new(
            routing,
            new ReloadableOptionsMonitor<BackendOptions>(new BackendOptions()),
            OperationCatalogue.Default,
            NullLogger<OperationRouter>.Instance);
        router.Initialize();

        // A reload newer than the published table moves getCompany to target stub. The reader's first read
        // returns it only after the test has published a still newer reload, which moves getCompany to
        // target orders.
        routing.Reload(Table(getCompany, StubTarget));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        routing.OnNextRead(read =>
        {
            paused.TrySetResult();
            if (!release.Wait(WaitBound, ct))
            {
                throw new TimeoutException("the paused routing read was not released in time");
            }

            return read;
        });

        Task<ResolvedTarget> reader = Task.Factory.StartNew(
            () => router.Resolve(Surface.Rest, getCompany),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        ResolvedTarget newest;
        IReadOnlySet<ResolvedTarget> published;
        try
        {
            await paused.Task.WaitAsync(WaitBound, ct);
            routing.Reload(Table(getCompany, OrdersTarget));
            newest = router.Resolve(Surface.Rest, getCompany);
            published = router.TargetsInService();
        }
        finally
        {
            release.Set();
        }

        ResolvedTarget delayed = await reader.WaitAsync(WaitBound, ct);

        Assert.True(string.Equals(newest.Name, OrdersTarget, StringComparison.Ordinal), "the newest reload was not published");
        Assert.True(
            string.Equals(delayed.Name, OrdersTarget, StringComparison.Ordinal),
            "a resolution that read older options before a newer table was published resolved from the older options");
        Assert.True(
            ReferenceEquals(router.TargetsInService(), published),
            "a resolution that read older options before a newer table was published replaced that table");
    }

    // Both stubs as targets of the hosted facade: backend at stub A and stub at stub B, each with both
    // base URLs, so the shipped table, which routes every operation to backend, stays valid.
    private static FacadeTestSettings Hosted(StubBackend backend, StubBackend stub, LogCapture? logs) => new()
    {
        BackendRestBaseUrl = backend.RestBaseUrl,
        BackendSoapBaseUrl = backend.SoapBaseUrl,
        StubRestBaseUrl = stub.RestBaseUrl,
        StubSoapBaseUrl = stub.SoapBaseUrl,
        LoggerProvider = logs,
    };

    // Sets each key on every provider of the hosted facade's configuration root, then reloads the root.
    // The options monitor rebuilds the routing options as the root reports the change, so a table the
    // facade rejects surfaces here as nested AggregateExceptions over OptionsValidationException; those
    // rejections are returned for the caller to judge. Any other exception propagates.
    private static IReadOnlyList<OptionsValidationException> Reload(FacadeFactory facade, params (string Key, string? Value)[] settings)
    {
        if (facade.Services.GetRequiredService<IConfiguration>() is not IConfigurationRoot configuration)
        {
            Assert.Fail("the facade's configuration is not a reloadable configuration root");
            throw new UnreachableException();
        }

        foreach ((string key, string? value) in settings)
        {
            configuration[key] = value;
        }

        try
        {
            configuration.Reload();
            return [];
        }
        catch (AggregateException exception) when (exception.Flatten().InnerExceptions.All(static inner => inner is OptionsValidationException))
        {
            return [.. exception.Flatten().InnerExceptions.Cast<OptionsValidationException>()];
        }
    }

    // Calls every catalogue operation once at its normal address and asserts that the operation with id
    // movedId reached stub alone and every other operation reached backend alone.
    private static async Task AssertEveryOperationReachesAsync(
        HttpClient client,
        FacadeFactory facade,
        StubBackend backend,
        StubBackend stub,
        string? movedId,
        string phase,
        CancellationToken ct)
    {
        IReadOnlyList<OperationDescriptor> operations = OperationCatalogue.Default.Operations;
        Assert.True(operations.Count == CatalogueOperationCount, "the catalogue does not hold the 22 carried-over operations");

        int movedCalls = 0;
        foreach (OperationDescriptor op in operations)
        {
            bool isMoved = string.Equals(op.Id, movedId, StringComparison.Ordinal);
            movedCalls += isMoved ? 1 : 0;
            await AssertCallReachesAsync(
                client, facade, op, reached: isMoved ? stub : backend, bypassed: isMoved ? backend : stub, $"{op.Id} {phase}", ct);
        }

        Assert.True(
            movedCalls == (movedId is null ? 0 : 1),
            $"{phase}: the moved operation was not called exactly once among the catalogue operations");
    }

    // Sends the operation's request to the socket of its surface and asserts that it reached 'reached'
    // once, on the listener of that surface, and 'bypassed' never, and that the facade returned the
    // status, X-Stub-Id and body bytes 'reached' sent.
    private static async Task AssertCallReachesAsync(
        HttpClient client,
        FacadeFactory facade,
        OperationDescriptor op,
        StubBackend reached,
        StubBackend bypassed,
        string label,
        CancellationToken ct)
    {
        reached.ClearLog();
        bypassed.ClearLog();

        Uri socket = op.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        RawResponse response = await FacadeFactory.SendAsync(client, OperationRequests.For(op).ToMessage(socket), ct);

        IReadOnlyList<StubRequest> received = reached.Received;
        int bypassedCount = bypassed.Received.Count;
        Assert.True(received.Count == 1, $"{label}: stub {reached.Id} did not receive exactly one request");
        Assert.True(bypassedCount == 0, $"{label}: stub {bypassed.Id} received an unexpected request, expected none");

        StubRequest request = received[0];
        StubListener expectedListener = op.Surface == Surface.Rest ? StubListener.Rest : StubListener.Soap;
        Assert.True(
            request.Listener == expectedListener,
            $"{label}: stub {reached.Id} received the call on its {request.Listener} listener, expected {expectedListener}");

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

    // Resolves every catalogue operation, then an unmatched request on each surface, then reads the
    // routing snapshot, and asserts that 'moved' resolves to movedTo and everything else to target
    // backend, each with that target's base URL for the surface, and that the snapshot names the same
    // target for every operation.
    private static void AssertResolvesTo(OperationRouter router, OperationDescriptor? moved, string movedTo, string phase)
    {
        IReadOnlyList<OperationDescriptor> operations = OperationCatalogue.Default.Operations;
        foreach (OperationDescriptor op in operations)
        {
            string expected = ExpectedTarget(op, moved, movedTo);
            ResolvedTarget resolved = router.Resolve(op.Surface, op);
            Assert.True(
                string.Equals(resolved.Name, expected, StringComparison.Ordinal),
                $"{phase}: {op.Id} resolved to target {resolved.Name}, expected {expected}");
            Assert.True(
                resolved.BaseUrl == new Uri(BaseUrl(expected, op.Surface)),
                $"{phase}: {op.Id} resolved to another base URL than the {op.Surface} base URL of target {expected}");
        }

        foreach (Surface surface in new[] { Surface.Rest, Surface.Soap })
        {
            ResolvedTarget unmatched = router.Resolve(surface, operation: null);
            Assert.True(
                string.Equals(unmatched.Name, BackendTarget, StringComparison.Ordinal),
                $"{phase}: an unmatched {surface} request resolved to target {unmatched.Name}, expected {BackendTarget}");
        }

        IReadOnlyList<RoutedOperation> rows = router.Describe().Operations;
        Assert.True(rows.Count == operations.Count, $"{phase}: the routing snapshot does not list every catalogue operation once");
        foreach (OperationDescriptor op in operations)
        {
            string expected = ExpectedTarget(op, moved, movedTo);
            RoutedOperation? row = rows.FirstOrDefault(candidate => string.Equals(candidate.Id, op.Id, StringComparison.Ordinal));
            Assert.True(
                row is not null && string.Equals(row.Target, expected, StringComparison.Ordinal),
                $"{phase}: the routing snapshot does not show {op.Id} routed to target {expected}");
        }
    }

    private static string ExpectedTarget(OperationDescriptor op, OperationDescriptor? moved, string movedTo) =>
        string.Equals(op.Id, moved?.Id, StringComparison.Ordinal) ? movedTo : BackendTarget;

    // The router's own entries with the given event id.
    private static IReadOnlyList<CapturedLog> RouterLog(LogCapture logs, int eventId) =>
        [.. logs.Entries.Where(entry => entry.EventId.Id == eventId && string.Equals(entry.Category, RouterCategory, StringComparison.Ordinal))];

    // The shipped shape with every catalogue operation listed: each names target backend, except
    // 'moved', which names movedTo. Targets backend, stub and orders each have both base URLs.
    private static RoutingOptions Table(OperationDescriptor? moved = null, string movedTo = StubTarget)
    {
        var table = new RoutingOptions { DefaultTarget = BackendTarget };
        foreach (string target in new[] { BackendTarget, StubTarget, OrdersTarget })
        {
            table.Targets[target] = new RoutingTarget
            {
                RestBaseUrl = BaseUrl(target, Surface.Rest),
                SoapBaseUrl = BaseUrl(target, Surface.Soap),
            };
        }

        foreach (OperationDescriptor op in OperationCatalogue.Default.Operations)
        {
            Dictionary<string, string> section = op.Surface == Surface.Rest ? table.Operations.Rest : table.Operations.Soap;
            section[op.RoutingKey] = string.Equals(op.Id, moved?.Id, StringComparison.Ordinal) ? movedTo : BackendTarget;
        }

        return table;
    }

    // A synthetic base URL per target and surface.
    private static string BaseUrl(string target, Surface surface) =>
        surface == Surface.Rest ? "http://" + target + ".invalid:65000" : "http://" + target + ".invalid:21001";

    private static ILoggerFactory LoggersFor(LogCapture logs) =>
        LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));

    private static OperationRouter Router(
        IOptionsMonitor<RoutingOptions> routing,
        IOptionsMonitor<BackendOptions> backend,
        ILoggerFactory loggers) =>
        new(routing, backend, OperationCatalogue.Default, loggers.CreateLogger<OperationRouter>());

    // Makes every read of one monitor fail until its next reload, as the options monitor's reads do
    // after a reload it cannot build: the routing monitor with the validator's failure for an entry
    // naming an undefined target, or the backend monitor with the binder's failure for a
    // Backend:TimeoutSeconds that is no integer. Returns the exception, the options type the router
    // names and the text that names the entry.
    private static (Exception Rejected, string OptionsType, string Failure) Reject(
        string rejection,
        ReloadableOptionsMonitor<RoutingOptions> routing,
        ReloadableOptionsMonitor<BackendOptions> backend)
    {
        switch (rejection)
        {
            case ValidatorRejection:
                ValidateOptionsResult result = new RoutingOptionsValidator(OperationCatalogue.Default)
                    .Validate(Options.DefaultName, Table(OperationCatalogue.Default.Find(Surface.Rest, "getCompany"), "nowhere"));
                Assert.True(result.Failed, "the validator accepted a table naming an undefined target");
                var invalid = new OptionsValidationException(Options.DefaultName, typeof(RoutingOptions), result.Failures);
                routing.Reject(invalid);
                return (invalid, nameof(RoutingOptions), "Routing:Operations:Rest:getCompany names undefined target 'nowhere'");

            case BinderRejection:
                IConfigurationRoot malformed = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { [BackendOptions.TimeoutKey] = "abc" })
                    .Build();
                InvalidOperationException unbound = Assert.Throws<InvalidOperationException>(
                    () => malformed.GetSection(BackendOptions.SectionName).Get<BackendOptions>());
                backend.Reject(unbound);
                return (unbound, nameof(BackendOptions), BackendOptions.TimeoutKey);

            default:
                throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Unknown reload rejection.");
        }
    }

    /// <summary>
    /// An options monitor whose value a test replaces, as a configuration reload does; whose reads can
    /// all fail with one exception until the next reload, as the options monitor's do after a reload that
    /// fails binding or validation; and which can hand the next read's value through a hook.
    /// </summary>
    /// <param name="initial">The value served until the first reload.</param>
    private sealed class ReloadableOptionsMonitor<TOptions>(TOptions initial) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        private TOptions _value = initial;
        private Exception? _rejection;
        private Func<TOptions, TOptions>? _onNextRead;

        public TOptions CurrentValue
        {
            get
            {
                if (Volatile.Read(ref _rejection) is { } rejection)
                {
                    throw rejection;
                }

                TOptions value = Volatile.Read(ref _value);
                return Interlocked.Exchange(ref _onNextRead, null) is { } onRead ? onRead(value) : value;
            }
        }

        /// <summary>Serves <paramref name="value"/> from now on and clears any rejection.</summary>
        public void Reload(TOptions value)
        {
            Volatile.Write(ref _value, value);
            Volatile.Write(ref _rejection, null);
        }

        /// <summary>Makes every read throw <paramref name="rejection"/> until the next <see cref="Reload"/>.</summary>
        public void Reject(Exception rejection) => Volatile.Write(ref _rejection, rejection);

        /// <summary>Passes the value of the next read through <paramref name="onRead"/>, once, on the reading thread.</summary>
        public void OnNextRead(Func<TOptions, TOptions> onRead) => Volatile.Write(ref _onNextRead, onRead);

        public TOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}

// Invoker lifecycle (class InvokerLifecycleTests). A target in service keeps one pooled invoker. A
// reload that supersedes the target, or Dispose, retires its invoker, which is disposed once the calls
// still using it have ended: the cache keeps no invoker the routing table no longer names, and no call
// loses its invoker while it runs. The forwarder is built directly over the real OperationRouter,
// PassThroughTransformer and SoapFaultWriter and over a recording IHttpForwarder, so each test sees the
// exact invoker every call was given and can hold a call in flight. A reload is an options monitor
// returning a new options instance. Whether an invoker has been disposed is read from a request it sends
// to a closed loopback port: a disposed invoker throws ObjectDisposedException, a live one
// HttpRequestException.
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
                // Dispose runs after the call's own disposal check, while the call resolves its target.
                // The call must still throw ObjectDisposedException, never reaching the forwarder or
                // acquiring an invoker, which after Dispose would be owned by nothing and never disposed.
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
