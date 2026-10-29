// Per-operation routing through the facade's real REST and SOAP sockets. Each case moves exactly one
// operation to a second target by changing one Routing:Operations:<Rest|Soap>:<jadeMethod> key, calls
// all 22 catalogue operations at their normal addresses, and asserts that the moved operation alone
// reaches stub B, through the same URL and with B's response unchanged, while the other 21 still reach
// stub A, the default "backend" target. REST and SOAP each get a case because the facade matches them
// differently: REST by verb and path, SOAP by the SOAPAction header and then by the request body.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every message here is built only from fixed values: operation ids,
// stub ids, listener names, request targets and status codes. No port, URL, elapsed time or GUID ever
// reaches a message, which is also why no Assert.Single, Assert.Empty or Assert.Equal is applied to a
// recorded StubRequest: its dump would print the Host header and with it a port.

using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Tests.Support;
using Xunit;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Proves that one routing key moves one operation and nothing else (the per-operation routing check of
/// the results report), on the facade hosted on real loopback Kestrel listeners.
/// </summary>
public sealed class RoutingTests
{
    private const string BackendStubId = "A";
    private const string RerouteStubId = "B";
    private const string RerouteTargetName = "stub";
    private const string StubIdHeader = "X-Stub-Id";
    private const string SoapActionHeader = "SOAPAction";
    private const string ContentTypeHeader = "Content-Type";
    private const string ForwardedHeaderPrefix = "X-Forwarded-";

    // The 22 operations carried over from ErewhonRestService and ErewhonWebService.
    private const int RestOperationCount = 13;
    private const int SoapOperationCount = 9;

    private const int BackendStatus = 200;

    // Distinct from the backend stub's 200, so a response that came from the wrong stub cannot pass.
    private const int RerouteStatus = 203;

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
