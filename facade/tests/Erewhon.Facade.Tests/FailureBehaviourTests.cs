// Facade-only failure behaviour: what a caller receives when the backend its operation is routed to
// cannot be reached, or does not answer within Backend:TimeoutSeconds. These are the only responses the
// facade builds itself (BackendForwarder's problem+json and SoapFaultWriter's Fault), so the equivalence
// replay excludes them and they are proven here, against the facade on real loopback Kestrel listeners.
// The expected texts are the plan's literals, held here independently of the facade's own constants,
// which are asserted to match the same wire values.
//
// The facade-tests container runs with network_mode none, so the unreachable targets are a closed
// loopback port and a name under the reserved .invalid domain, which never resolves.
//
// Failures are identified across runs by a signature over the exception type, the failing line and the
// assertion message, so every message and Assert.Equal operand here is run-invariant: no port, URL,
// elapsed time or GUID. Timing bounds are Assert.True calls with fixed messages.

using System.Text.Json;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.Soap;
using Erewhon.Facade.Tests.Support;
using Xunit;

namespace Erewhon.Facade.Tests;

/// <summary>
/// Unreachable-backend and timeout responses of the facade: REST 502 or 504 with a problem+json body,
/// and on SOAP HTTP 500 with a SOAP 1.1 Fault, each within a fixed bound.
/// </summary>
public sealed class FailureBehaviourTests
{
    private const string ClosedPort = "closed-port";
    private const string UnresolvableHost = "unresolvable-host";

    // A name under .invalid (RFC 6761) never resolves; the ports are the backend's production ones.
    private const string UnresolvableRestBaseUrl = "http://backend.invalid:65000";
    private const string UnresolvableSoapBaseUrl = "http://backend.invalid:21001";

    private const string RestOperationId = "Rest.getCompany";
    private const string SoapOperationId = "Soap.getAgent";

    private const string ProblemMediaType = "application/problem+json";
    private const string ProblemType = "about:blank";
    private const string SoapMediaType = "text/xml";
    private const string SoapCharSet = "utf-8";
    private const string ServerFaultCode = "soap:Server";
    private const string UnavailableText = "Backend unavailable";
    private const string TimedOutText = "Backend timed out";

    private const int SlowBackendTimeoutSeconds = 2;

    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly TimeSpan UnreachableBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowBackendDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimeoutLowerBound = TimeSpan.FromSeconds(SlowBackendTimeoutSeconds);
    private static readonly TimeSpan TimeoutUpperBound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(ClosedPort)]
    [InlineData(UnresolvableHost)]
    public async Task RestCallToUnreachableBackendReturns502ProblemJson(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string rest, string soap) = UnreachableUrls(kind);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = rest, BackendSoapBaseUrl = soap }, ct);

        RawResponse response = await SendAsync(facade, RestOperationId, ct);

        Assert.Equal(502, response.Status);
        Assert.Equal(ProblemMediaType, response.MediaType, ignoreCase: true);
        Assert.Equal(BackendForwarder.ProblemContentType, response.MediaType, ignoreCase: true);

        using JsonDocument problem = ParseProblem(response);
        JsonElement root = problem.RootElement;
        Assert.Equal(ProblemType, StringMember(root, "type"));
        Assert.Equal(UnavailableText, StringMember(root, "title"));
        Assert.Equal(BackendForwarder.UnavailableTitle, StringMember(root, "title"));
        AssertStatusMember(Member(root, "status"), 502);
        Assert.Contains(
            OperationCatalogue.Default.GetById(RestOperationId).JadeMethod, StringMember(root, "detail"), StringComparison.Ordinal);

        Assert.True(response.Elapsed <= UnreachableBound, "unreachable REST response exceeded 5 s bound");
    }

    [Theory]
    [InlineData(ClosedPort)]
    [InlineData(UnresolvableHost)]
    public async Task SoapCallToUnreachableBackendReturnsFault(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (string rest, string soap) = UnreachableUrls(kind);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = rest, BackendSoapBaseUrl = soap }, ct);

        RawResponse response = await SendAsync(facade, SoapOperationId, ct);

        string? faultString = AssertSoapFault(response);
        Assert.Equal(UnavailableText, faultString);
        Assert.Equal(SoapFaultWriter.BackendUnavailable, faultString);

        Assert.True(response.Elapsed <= UnreachableBound, "unreachable SOAP response exceeded 5 s bound");
    }

    [Fact]
    public async Task RestCallToSlowBackendReturns504AfterDeadline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend slow = await StartSlowBackendAsync(ct);
        await using FacadeFactory facade = await StartFacadeForSlowBackendAsync(slow, ct);

        RawResponse response = await SendAsync(facade, RestOperationId, ct);

        Assert.Equal(504, response.Status);
        Assert.Equal(ProblemMediaType, response.MediaType, ignoreCase: true);

        using JsonDocument problem = ParseProblem(response);
        JsonElement root = problem.RootElement;
        Assert.Equal(TimedOutText, StringMember(root, "title"));
        Assert.Equal(BackendForwarder.TimedOutTitle, StringMember(root, "title"));
        if (root.TryGetProperty("status", out JsonElement status))
        {
            AssertStatusMember(status, 504);
        }

        Assert.True(
            response.Elapsed >= TimeoutLowerBound && response.Elapsed <= TimeoutUpperBound,
            "REST timeout response outside the 2-5 s window");

        // The call reached the backend, so the 504 is the deadline's and not an unreachable result.
        Assert.True(slow.Received.Count == 1, "slow REST backend did not receive exactly one request");
    }

    [Fact]
    public async Task SoapCallToSlowBackendReturnsFaultAfterDeadline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend slow = await StartSlowBackendAsync(ct);
        await using FacadeFactory facade = await StartFacadeForSlowBackendAsync(slow, ct);

        RawResponse response = await SendAsync(facade, SoapOperationId, ct);

        string? faultString = AssertSoapFault(response);
        Assert.Equal(TimedOutText, faultString);
        Assert.Equal(SoapFaultWriter.BackendTimedOut, faultString);

        Assert.True(
            response.Elapsed >= TimeoutLowerBound && response.Elapsed <= TimeoutUpperBound,
            "SOAP timeout response outside the 2-5 s window");

        // The call reached the backend, so the Fault is the deadline's and not an unreachable result.
        Assert.True(slow.Received.Count == 1, "slow SOAP backend did not receive exactly one request");
    }

    /// <summary>Returns the backend base URLs of an unreachable target of the given kind.</summary>
    /// <param name="kind"><c>closed-port</c> or <c>unresolvable-host</c>.</param>
    /// <returns>
    /// The REST and SOAP base URLs. The elements are not named <c>Rest</c> and <c>Soap</c>, because C#
    /// reserves the tuple element name <c>Rest</c> (CS8126).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is neither.</exception>
    private static (string RestBaseUrl, string SoapBaseUrl) UnreachableUrls(string kind)
    {
        switch (kind)
        {
            case ClosedPort:
                // Both surfaces share the one closed port: a refused connection is all either needs.
                int port = StubBackend.GetClosedLoopbackPort();
                string url = $"http://127.0.0.1:{port}";
                return (url, url);
            case UnresolvableHost:
                return (UnresolvableRestBaseUrl, UnresolvableSoapBaseUrl);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // The stub records each request on arrival and then waits far beyond the facade's deadline; the wait
    // ends when the facade aborts the call.
    private static Task<StubBackend> StartSlowBackendAsync(CancellationToken ct) =>
        StubBackend.StartAsync("slow", new StubBackendOptions { Delay = SlowBackendDelay }, ct);

    private static Task<FacadeFactory> StartFacadeForSlowBackendAsync(StubBackend slow, CancellationToken ct) =>
        FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = slow.RestBaseUrl,
                BackendSoapBaseUrl = slow.SoapBaseUrl,
                BackendTimeoutSeconds = SlowBackendTimeoutSeconds,
            },
            ct);

    // Sends the catalogue operation at its normal address to the facade socket of its own surface.
    private static async Task<RawResponse> SendAsync(FacadeFactory facade, string operationId, CancellationToken ct)
    {
        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        Uri socket = operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        using HttpClient client = facade.CreateRawClient();
        return await FacadeFactory.SendAsync(client, OperationRequests.For(operation).ToMessage(socket), ct);
    }

    private static JsonDocument ParseProblem(RawResponse response)
    {
        JsonDocument problem = JsonDocument.Parse(response.Body);
        if (problem.RootElement.ValueKind != JsonValueKind.Object)
        {
            problem.Dispose();
            Assert.Fail("problem body is not a JSON object");
        }

        return problem;
    }

    // Each message names the member, so a missing member gives a signature of its own.
    private static JsonElement Member(JsonElement problem, string name)
    {
        Assert.True(problem.TryGetProperty(name, out JsonElement value), "problem body has no " + name + " member");
        return value;
    }

    private static string StringMember(JsonElement problem, string name)
    {
        JsonElement value = Member(problem, name);
        Assert.True(value.ValueKind == JsonValueKind.String, "problem " + name + " member is not a string");
        return value.GetString()!;
    }

    private static void AssertStatusMember(JsonElement status, int expected)
    {
        Assert.True(status.ValueKind == JsonValueKind.Number, "problem status member is not a number");
        Assert.True(status.TryGetInt32(out int value), "problem status member is not an integer");
        Assert.Equal(expected, value);
    }

    // Asserts HTTP 500, text/xml in UTF-8, and a SOAP 1.1 envelope whose Body holds only a Fault with the
    // unqualified faultcode soap:Server; returns the Fault's faultstring text.
    private static string? AssertSoapFault(RawResponse response)
    {
        Assert.Equal(500, response.Status);
        Assert.Equal(SoapMediaType, response.MediaType, ignoreCase: true);
        Assert.Equal(SoapCharSet, response.CharSet, ignoreCase: true);

        XElement? envelope = XDocument.Parse(response.BodyText).Root;
        Assert.NotNull(envelope);
        Assert.Equal(SoapEnvelope + "Envelope", envelope.Name);

        XElement? body = envelope.Element(SoapEnvelope + "Body");
        Assert.NotNull(body);

        XElement fault = Assert.Single(body.Elements());
        Assert.Equal(SoapEnvelope + "Fault", fault.Name);

        string? faultCode = fault.Element("faultcode")?.Value;
        Assert.Equal(ServerFaultCode, faultCode);
        Assert.Equal(SoapFaultWriter.FaultCodeServer, faultCode);
        return fault.Element("faultstring")?.Value;
    }
}
