// Facade-only failure behaviour: what a caller receives when the backend its operation is routed to
// cannot be reached, or does not answer within Backend:TimeoutSeconds. These are the only failure
// responses the facade builds (BackendForwarder's problem+json and SoapFaultWriter's Fault), and with
// the published WSDL's soap:address the only content it builds on the REST and SOAP listeners: every
// other failed call is aborted rather than answered, or gets the server's own 500 for an exception that
// escapes request handling (class BackendForwarderTests), and the admin listener's own responses are
// internal to it. The equivalence replay excludes these failure responses, so they are proven here,
// against the facade on real loopback Kestrel listeners.
// The expected texts are the plan's literals, held here independently of the facade's own constants,
// which are asserted to match the same wire values. Each payload is held to its complete fixed form:
// the problem body byte for byte against JSON composed here, with its exact Content-Type and member
// set, and the Fault's whole Envelope/Body/Fault structure, with faultcode resolved as a QName.
//
// The facade-tests container runs with network_mode none, so the unreachable targets are a closed
// loopback port and a name under the reserved .invalid domain, which never resolves.
//
// The other test classes here, LogLevelFloorsTests (the framework log floors, without a socket),
// BackendErrorPassThroughTests, ForwardingTransportTests and BackendForwarderTests, are classes of their
// own whose names do not end in "FailureBehaviourTests": the results report counts every test class with
// that suffix as the unreachable and timeout check.
//
// A failing test is identified across runs by its class, its TRX display name, which holds its theory
// arguments, and a signature over the exception type, the failing line and the assertion message. So
// every message, Assert.Equal operand and theory datum in this file is run-invariant: no port, URL,
// elapsed time or GUID. Timing bounds are Assert.True calls with fixed messages, and log entries are
// never passed to Assert.Single or Assert.Equal, because their dump holds durations.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Facade;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.Routing;
using Erewhon.Facade.Soap;
using Erewhon.Facade.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Forwarder;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

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
    private const string ServerFaultLocalName = "Server";
    private const string UnavailableText = "Backend unavailable";
    private const string TimedOutText = "Backend timed out";

    // The problem detail is these two parts around the operation's JADE method name from the catalogue.
    private const string ProblemDetailStart = "The backend for operation ";
    private const string UnavailableDetailEnd = " could not be reached.";
    private const string TimedOutDetailEnd = " did not respond in time.";
    private const string ContentTypeHeader = "Content-Type";

    private const int SlowBackendTimeoutSeconds = 2;

    // A REST operation with a body; the declared body is never completed, one byte every 0.5 s.
    private const string TrickledUploadOperationId = "Rest.postCreateClient";
    private const string JsonContentType = "application/json";
    private const int TrickledBodyLength = 1000;
    private const string TimeoutOutcome = "timeout";

    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly string[] ProblemMembers = ["type", "title", "status", "detail"];

    private static readonly char[] XmlWhitespace = [' ', '\t', '\r', '\n'];

    private static readonly TimeSpan UnreachableBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowBackendDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimeoutLowerBound = TimeSpan.FromSeconds(SlowBackendTimeoutSeconds);
    private static readonly TimeSpan TimeoutUpperBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrickleInterval = TimeSpan.FromMilliseconds(500);

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

        AssertProblemResponse(response, 502, UnavailableText, UnavailableDetailEnd, RestOperationId);
        Assert.Equal(UnavailableText, BackendForwarder.UnavailableTitle);

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

        AssertSoapFault(response, UnavailableText);
        Assert.Equal(UnavailableText, SoapFaultWriter.BackendUnavailable);

        Assert.True(response.Elapsed <= UnreachableBound, "unreachable SOAP response exceeded 5 s bound");
    }

    [Fact]
    public async Task RestCallToSlowBackendReturns504AfterDeadline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend slow = await StartSlowBackendAsync(ct);
        await using FacadeFactory facade = await StartFacadeForSlowBackendAsync(slow, ct);

        RawResponse response = await SendAsync(facade, RestOperationId, ct);

        AssertProblemResponse(response, 504, TimedOutText, TimedOutDetailEnd, RestOperationId);
        Assert.Equal(TimedOutText, BackendForwarder.TimedOutTitle);

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

        AssertSoapFault(response, TimedOutText);
        Assert.Equal(TimedOutText, SoapFaultWriter.BackendTimedOut);

        Assert.True(
            response.Elapsed >= TimeoutLowerBound && response.Elapsed <= TimeoutUpperBound,
            "SOAP timeout response outside the 2-5 s window");

        // The call reached the backend, so the Fault is the deadline's and not an unreachable result.
        Assert.True(slow.Received.Count == 1, "slow SOAP backend did not receive exactly one request");
    }

    // The deadline fires while the facade is still waiting for upload bytes, so it cancels a pending read
    // of the caller's body. The 504 must still arrive at the deadline, and neither the cancelled read nor
    // the server's drain of the rest of the body afterwards may be logged as an error.
    [Fact]
    public async Task RestUploadTrickledPastDeadlineReturns504WithoutErrorLog()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using StubBackend slow = await StartSlowBackendAsync(ct);
        FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = slow.RestBaseUrl,
                BackendSoapBaseUrl = slow.SoapBaseUrl,
                BackendTimeoutSeconds = SlowBackendTimeoutSeconds,
                LoggerProvider = logs,
            },
            ct);

        RawSocketResponse response;
        try
        {
            OperationDescriptor operation = OperationCatalogue.Default.GetById(TrickledUploadOperationId);
            response = await RawSocketClient.SendTrickledUploadAsync(
                facade.RestBaseAddress,
                OperationRequests.ForRest(operation).PathAndQuery,
                JsonContentType,
                TrickledBodyLength,
                TrickleInterval,
                ct);
        }
        finally
        {
            // Stopping the host waits for the connection to end, so everything the call logs, the server's
            // drain of the unread body included, has been written before the log is read.
            await facade.DisposeAsync();
        }

        // The raw socket response holds no headers, so the Content-Type of this 504, which the facade writes
        // as for every REST timeout, is asserted by RestCallToSlowBackendReturns504AfterDeadline.
        Assert.Equal(504, response.Status);
        AssertProblemBody(response.Body, 504, TimedOutText, TimedOutDetailEnd, TrickledUploadOperationId);

        Assert.True(
            response.Elapsed >= TimeoutLowerBound && response.Elapsed <= TimeoutUpperBound,
            "trickled-upload timeout response outside the 2-5 s window");

        Assert.True(
            !logs.Entries.Any(entry => entry.Level >= LogLevel.Error),
            "facade logged an error for an upload cut off by the deadline");

        // Assert.Single would print the entries, whose messages hold durations.
        IReadOnlyList<CapturedLog> calls = logs.BackendCalls;
        Assert.True(calls.Count == 1, "trickled upload did not write exactly one backend call line");
        Assert.Equal(TimeoutOutcome, calls[0].Value("Outcome"));
        Assert.Equal(504, calls[0].Value("Status"));
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

    // Asserts the complete REST failure response: the status, exactly one Content-Type value that is
    // exactly application/problem+json as received, and the fixed problem body (AssertProblemBody).
    private static void AssertProblemResponse(
        RawResponse response, int status, string title, string detailEnd, string operationId)
    {
        Assert.Equal(status, response.Status);
        Assert.True(
            response.Headers.TryGetValue(ContentTypeHeader, out string[]? contentType) && contentType.Length == 1,
            "problem response does not carry exactly one Content-Type value");
        Assert.True(
            string.Equals(ProblemMediaType, contentType[0], StringComparison.Ordinal),
            "problem Content-Type is not exactly application/problem+json");
        Assert.Equal(ProblemMediaType, BackendForwarder.ProblemContentType);

        AssertProblemBody(response.Body, status, title, detailEnd, operationId);
    }

    // Asserts the fixed problem body against expectations authored here, never taken from the facade's
    // builder: a JSON object whose members are exactly type, title, status and detail, each once; type
    // about:blank; the given title; status as a JSON integer; the detail sentence naming the operation's
    // JADE method from the catalogue; and bytes equal to the UTF-8 of that object written without
    // whitespace in the order type, title, status, detail. No message holds a received value.
    private static void AssertProblemBody(byte[] body, int status, string title, string detailEnd, string operationId)
    {
        string jadeMethod = OperationCatalogue.Default.GetById(operationId).JadeMethod;

        // The expected JSON is composed without escaping, which is exact only for a name like this.
        Assert.True(jadeMethod.All(char.IsAsciiLetterOrDigit), "operation name would need escaping in the expected problem JSON");
        string detail = ProblemDetailStart + jadeMethod + detailEnd;

        using (JsonDocument problem = ParseProblem(body))
        {
            JsonElement root = problem.RootElement;

            // Four members whose distinct names are the four expected ones: none missing, extra or repeated.
            var names = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (JsonProperty member in root.EnumerateObject())
            {
                names.Add(member.Name);
                count++;
            }

            Assert.True(
                count == ProblemMembers.Length && names.SetEquals(ProblemMembers),
                "problem body members are not exactly type, title, status and detail, each once");

            Assert.True(
                string.Equals(ProblemType, StringMember(root, "type"), StringComparison.Ordinal),
                "problem type member is not about:blank");
            Assert.True(
                string.Equals(title, StringMember(root, "title"), StringComparison.Ordinal),
                "problem title member is not the expected title");
            AssertStatusMember(Member(root, "status"), status);
            Assert.True(
                string.Equals(detail, StringMember(root, "detail"), StringComparison.Ordinal),
                "problem detail member is not the expected sentence for the operation");
        }

        string expected =
            "{\"type\":\"" + ProblemType
            + "\",\"title\":\"" + title
            + "\",\"status\":" + status.ToString(CultureInfo.InvariantCulture)
            + ",\"detail\":\"" + detail + "\"}";
        Assert.True(
            body.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            "problem body bytes are not the expected JSON in the order type, title, status, detail");
    }

    private static JsonDocument ParseProblem(byte[] body)
    {
        JsonDocument problem = JsonDocument.Parse(body);
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

    // Asserts HTTP 500, text/xml in UTF-8, and the complete SOAP 1.1 Server Fault: a root
    // {http://schemas.xmlsoap.org/soap/envelope/}Envelope whose only element is the Body (the facade writes
    // no Header), a Body whose only element is the Fault, and a Fault whose elements are exactly the
    // unqualified faultcode and faultstring, in that order, each holding text only. Envelope, Body and
    // Fault hold no text but whitespace. faultcode is resolved as a QName through the namespace
    // declarations in scope at faultcode, so a prefix bound to another namespace fails even when the text
    // reads soap:Server; faultstring must equal the expected text exactly. No message holds a received value.
    private static void AssertSoapFault(RawResponse response, string expectedFaultString)
    {
        Assert.Equal(500, response.Status);
        Assert.Equal(SoapMediaType, response.MediaType, ignoreCase: true);
        Assert.Equal(SoapCharSet, response.CharSet, ignoreCase: true);

        XElement? envelope = XDocument.Parse(response.BodyText).Root;
        Assert.NotNull(envelope);
        Assert.True(envelope.Name == SoapEnvelope + "Envelope", "SOAP root element is not the SOAP 1.1 Envelope");

        XElement body = SingleSoapChild(
            envelope,
            SoapEnvelope + "Body",
            "SOAP Envelope holds text outside its elements",
            "SOAP Envelope does not hold exactly one element, the SOAP 1.1 Body");
        XElement fault = SingleSoapChild(
            body,
            SoapEnvelope + "Fault",
            "SOAP Body holds text outside its elements",
            "SOAP Body does not hold exactly one element, the SOAP 1.1 Fault");

        AssertNoSoapText(fault, "SOAP Fault holds text outside its elements");
        XElement[] fields = [.. fault.Elements()];
        Assert.True(
            fields.Length == 2 && fields[0].Name == XName.Get("faultcode") && fields[1].Name == XName.Get("faultstring"),
            "SOAP Fault elements are not exactly the unqualified faultcode and faultstring, in that order");
        XElement faultCode = fields[0];
        XElement faultString = fields[1];
        Assert.True(!faultCode.HasElements && !faultString.HasElements, "SOAP faultcode or faultstring holds an element");

        // An xsd:QName collapses whitespace, and an unprefixed one takes the default namespace in scope.
        string code = faultCode.Value.Trim(XmlWhitespace);
        int colon = code.IndexOf(':', StringComparison.Ordinal);
        XNamespace? codeNamespace = colon switch
        {
            < 0 => faultCode.GetDefaultNamespace(),
            0 => null,
            _ => faultCode.GetNamespaceOfPrefix(code[..colon]),
        };
        Assert.True(
            codeNamespace == SoapEnvelope && string.Equals(ServerFaultLocalName, code[(colon + 1)..], StringComparison.Ordinal),
            "SOAP faultcode does not resolve to Server in the SOAP 1.1 envelope namespace");
        Assert.True(
            string.Equals(SoapFaultWriter.FaultCodeServer, code, StringComparison.Ordinal),
            "SOAP faultcode text differs from the facade's FaultCodeServer constant");

        Assert.True(
            string.Equals(expectedFaultString, faultString.Value, StringComparison.Ordinal),
            "SOAP faultstring is not the expected text");
    }

    // Asserts that an element-only SOAP container holds no text but whitespace and exactly one element, of
    // the given name, and returns that element.
    private static XElement SingleSoapChild(XElement container, XName name, string textMessage, string elementMessage)
    {
        AssertNoSoapText(container, textMessage);
        XElement[] children = [.. container.Elements()];
        Assert.True(children.Length == 1 && children[0].Name == name, elementMessage);
        return children[0];
    }

    // Text and CDATA directly inside an element-only SOAP container may only be XML whitespace.
    private static void AssertNoSoapText(XElement container, string message) =>
        Assert.True(container.Nodes().OfType<XText>().All(text => text.Value.Trim(XmlWhitespace).Length == 0), message);
}

// Log level floors (class LogLevelFloorsTests). Program.cs floors the framework categories whose lines
// print what a caller sent, because REST path segments carry agent and client names and YARP's forwarder
// writes a stack trace for every failed call: Microsoft.AspNetCore.Hosting.Diagnostics,
// Microsoft.AspNetCore.Routing and Microsoft.AspNetCore.Server.Kestrel.BadRequests at Warning, and Yarp
// at Error. Every category outside the floors must log exactly as configured. Each case builds only the
// logging services, from an in-memory Logging section, the console provider and the floors, and asks a
// logger of the category whether a level is enabled; nothing is written.
/// <summary>
/// Shows that the facade's log level floors keep every category under them at the floor's minimum level
/// or above for any <c>Logging</c> configuration, a configuration reload included, while a stricter
/// configured setting, a provider's own rule and a wildcard rule keep the levels they configure.
/// </summary>
public sealed class LogLevelFloorsTests
{
    private const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string DfaMatcher = "Microsoft.AspNetCore.Routing.Matching.DfaMatcher";
    private const string EndpointMiddleware = "Microsoft.AspNetCore.Routing.EndpointMiddleware";
    private const string KestrelBadRequests = "Microsoft.AspNetCore.Server.Kestrel.BadRequests";
    private const string KestrelConnections = "Microsoft.AspNetCore.Server.Kestrel.Connections";
    private const string HttpForwarder = "Yarp.ReverseProxy.Forwarder.HttpForwarder";
    private const string BackendForwarder = "Erewhon.Facade.Forwarding.BackendForwarder";
    private const string HostingLifetime = "Microsoft.Hosting.Lifetime";
    private const string OtherDfaMatcher = "Microsoft.Other.Matching.DfaMatcher";

    // Default, Microsoft.AspNetCore, Yarp and System at Trace, with Trace rules that outrank a
    // provider-less rule on a floor's prefix: the console provider's own Microsoft.AspNetCore rule and
    // an HttpForwarder category rule longer than the Yarp floor's prefix. The floors must still hold.
    private const string RaisedToTrace = "raised-to-trace";

    // A rule shorter than the Routing floor's prefix that matches only a category under it.
    private const string ConsoleWildcardNone = "console-wildcard-none";
    private const string ConsoleWildcardCritical = "console-wildcard-critical";
    private const string ConsoleFullNameWildcardNone = "console-full-name-wildcard-none";
    private const string WildcardNone = "wildcard-none";

    // Settings stricter than the floors, next to an unrelated rule of the console provider's own.
    private const string Stricter = "stricter";

    // A wildcard rule longer than the Routing floor's prefix that also matches categories outside it.
    private const string LongerWildcard = "longer-wildcard";

    // A valid category prefix, for the floors rejected for their minimum level.
    private const string RoutingPrefix = "Microsoft.AspNetCore.Routing";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Configurations =
        new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            [RaisedToTrace] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Yarp"] = "Trace",
                ["Logging:LogLevel:System"] = "Trace",
                ["Logging:LogLevel:Yarp.ReverseProxy.Forwarder.HttpForwarder"] = "Trace",
                ["Logging:Console:LogLevel:Microsoft.AspNetCore"] = "Trace",
            },
            [ConsoleWildcardNone] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:Console:LogLevel:*DfaMatcher"] = "None",
            },
            [ConsoleWildcardCritical] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:Console:LogLevel:*DfaMatcher"] = "Critical",
            },
            [ConsoleFullNameWildcardNone] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider:LogLevel:*DfaMatcher"] = "None",
            },
            [WildcardNone] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:*DfaMatcher"] = "None",
            },
            [Stricter] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "None",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error",
                ["Logging:LogLevel:Yarp.ReverseProxy.Forwarder"] = "None",
                ["Logging:Console:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
            },
            [LongerWildcard] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.*.Matching.DfaMatcher"] = "Trace",
            },
        };

    /// <summary>Configuration name, category, level and whether a logger of the category logs that level.</summary>
    public static TheoryData<string, string, LogLevel, bool> Levels { get; } = new()
    {
        { RaisedToTrace, HostingDiagnostics, LogLevel.Information, false },
        { RaisedToTrace, HostingDiagnostics, LogLevel.Warning, true },
        { RaisedToTrace, DfaMatcher, LogLevel.Debug, false },
        { RaisedToTrace, DfaMatcher, LogLevel.Warning, true },
        { RaisedToTrace, KestrelBadRequests, LogLevel.Debug, false },
        { RaisedToTrace, HttpForwarder, LogLevel.Warning, false },
        { RaisedToTrace, HttpForwarder, LogLevel.Error, true },
        { RaisedToTrace, KestrelConnections, LogLevel.Trace, true },
        { RaisedToTrace, BackendForwarder, LogLevel.Trace, true },
        { ConsoleWildcardNone, DfaMatcher, LogLevel.Warning, false },
        { ConsoleWildcardNone, DfaMatcher, LogLevel.Error, false },
        { ConsoleWildcardNone, DfaMatcher, LogLevel.Critical, false },
        { ConsoleWildcardNone, EndpointMiddleware, LogLevel.Information, false },
        { ConsoleWildcardNone, EndpointMiddleware, LogLevel.Warning, true },
        { ConsoleWildcardCritical, DfaMatcher, LogLevel.Warning, false },
        { ConsoleWildcardCritical, DfaMatcher, LogLevel.Error, false },
        { ConsoleWildcardCritical, DfaMatcher, LogLevel.Critical, true },
        { ConsoleFullNameWildcardNone, DfaMatcher, LogLevel.Error, false },
        { ConsoleFullNameWildcardNone, DfaMatcher, LogLevel.Critical, false },
        { WildcardNone, DfaMatcher, LogLevel.Warning, false },
        { WildcardNone, DfaMatcher, LogLevel.Error, false },
        { WildcardNone, DfaMatcher, LogLevel.Critical, false },
        { Stricter, HostingDiagnostics, LogLevel.Warning, false },
        { Stricter, HostingDiagnostics, LogLevel.Error, true },
        { Stricter, DfaMatcher, LogLevel.Warning, false },
        { Stricter, DfaMatcher, LogLevel.Error, true },
        { Stricter, HttpForwarder, LogLevel.Critical, false },
        { Stricter, BackendForwarder, LogLevel.Critical, false },
        { Stricter, HostingLifetime, LogLevel.Information, true },
        { Stricter, HostingLifetime, LogLevel.Debug, false },
        { LongerWildcard, OtherDfaMatcher, LogLevel.Trace, true },
        { LongerWildcard, DfaMatcher, LogLevel.Debug, false },
        { LongerWildcard, DfaMatcher, LogLevel.Warning, true },
    };

    /// <summary>Category prefix, minimum level and the exception and parameter an invalid floor is rejected with.</summary>
    public static TheoryData<string?, LogLevel, Type, string> InvalidFloors { get; } = new()
    {
        { null, LogLevel.Warning, typeof(ArgumentNullException), "categoryPrefix" },
        { "", LogLevel.Warning, typeof(ArgumentException), "categoryPrefix" },
        { "   ", LogLevel.Warning, typeof(ArgumentException), "categoryPrefix" },
        { "*", LogLevel.Warning, typeof(ArgumentException), "categoryPrefix" },
        { "Microsoft.AspNetCore.*", LogLevel.Warning, typeof(ArgumentException), "categoryPrefix" },
        { RoutingPrefix, (LogLevel)7, typeof(ArgumentOutOfRangeException), "minimum" },
        { RoutingPrefix, (LogLevel)(-1), typeof(ArgumentOutOfRangeException), "minimum" },
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void FloorsHoldAndStricterSettingsStayStrict(string configurationName, string category, LogLevel level, bool expected)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Configurations[configurationName])
            .Build();
        using ServiceProvider services = BuildLogging(configuration);

        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        Assert.Equal(expected, logger.IsEnabled(level));
    }

    [Fact]
    public void ReloadThatRaisesFlooredCategoriesKeepsThemAtTheirFloors()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Logging:LogLevel:Default"] = "Information",
            })
            .Build();
        using ServiceProvider services = BuildLogging(configuration);
        ILoggerFactory factory = services.GetRequiredService<ILoggerFactory>();
        ILogger hosting = factory.CreateLogger(HostingDiagnostics);
        ILogger forwarder = factory.CreateLogger(HttpForwarder);
        ILogger backendForwarder = factory.CreateLogger(BackendForwarder);
        Assert.False(backendForwarder.IsEnabled(LogLevel.Debug), "unfloored category logged Debug before the reload");

        configuration["Logging:LogLevel:Default"] = "Trace";
        configuration["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace";
        configuration["Logging:Console:LogLevel:Yarp"] = "Trace";
        configuration.Reload();

        Assert.True(backendForwarder.IsEnabled(LogLevel.Debug), "reload did not reach the logger filter options");
        Assert.False(hosting.IsEnabled(LogLevel.Information), "Hosting.Diagnostics logged below its floor after the reload");
        Assert.True(hosting.IsEnabled(LogLevel.Warning), "Hosting.Diagnostics did not log its floor level after the reload");
        Assert.False(forwarder.IsEnabled(LogLevel.Warning), "HttpForwarder logged below its floor after the reload");
        Assert.True(forwarder.IsEnabled(LogLevel.Error), "HttpForwarder did not log its floor level after the reload");
    }

    [Theory]
    [MemberData(nameof(InvalidFloors))]
    public void InvalidFloorIsRejected(string? categoryPrefix, LogLevel minimum, Type exceptionType, string parameterName)
    {
        ILoggingBuilder? captured = null;
        new ServiceCollection().AddLogging(builder => captured = builder);
        ILoggingBuilder logging = captured ?? throw new InvalidOperationException("AddLogging did not pass its builder");

        Exception exception = Assert.Throws(exceptionType, () => logging.AddLogLevelFloor(categoryPrefix!, minimum));

        Assert.Equal(parameterName, Assert.IsAssignableFrom<ArgumentException>(exception).ParamName);
    }

    [Fact]
    public void NullLoggingBuilderIsRejected()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => LogLevelFloors.AddLogLevelFloor(null!, RoutingPrefix, LogLevel.Warning));

        Assert.Equal("logging", exception.ParamName);
    }

    // The logging services of the facade (Program.cs): the Logging section, the console provider and the
    // floors, in the order the facade registers them.
    private static ServiceProvider BuildLogging(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddConsole();
            logging
                .AddLogLevelFloor("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
                .AddLogLevelFloor("Microsoft.AspNetCore.Routing", LogLevel.Warning)
                .AddLogLevelFloor("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Warning)
                .AddLogLevelFloor("Yarp", LogLevel.Error);
        });
        return services.BuildServiceProvider();
    }
}

// Backend error pass-through (class BackendErrorPassThroughTests): a backend's own error answer, 502 and
// 504 included, is forwarded and never replaced by the facade's problem+json or Fault. Each case points
// the facade at a stub backend on real loopback Kestrel listeners and calls the facade's real socket.
// The backend's bodies, content types and headers are literals authored here, independently of
// BackendForwarder and SoapFaultWriter; the SOAP bodies are backend Faults whose faultstring is neither
// of the facade's.
/// <summary>
/// Shows that a backend's own error responses (400, 404, 500, and 502 and 504 with and without a body)
/// reach the caller unchanged through the facade's REST and SOAP listeners, and that each is logged as
/// one forwarded call with the backend's status.
/// </summary>
public sealed class BackendErrorPassThroughTests
{
    private const string GetCompanyId = "Rest.getCompany";
    private const string PostCreateAgentId = "Rest.postCreateAgent";
    private const string SoapGetAgentId = "Soap.getAgent";

    private const string StubId = "erroring";
    private const string StubIdHeader = "X-Stub-Id";
    private const string BackendErrorHeader = "X-Backend-Error";
    private const string BackendErrorValue = "raised-by-backend";
    private const string ContentTypeHeader = "Content-Type";
    private const string ContentLengthHeader = "Content-Length";

    private const string RestContentType = "application/json; charset=utf-8";
    private const string SoapContentType = "text/xml; charset=utf-8";

    private const string ForwardedOutcome = "forwarded";

    private static readonly TimeSpan LogWaitBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The body-less REST read <c>getCompany</c>, the REST write <c>postCreateAgent</c> with its JSON
    /// body, and the SOAP operation <c>getAgent</c>, each against a backend answering 502 or 504 with a
    /// body or with none, or 400, 404 or 500 with a body.
    /// </summary>
    public static TheoryData<string, int, bool> BackendErrors()
    {
        var data = new TheoryData<string, int, bool>();
        string[] operations = [GetCompanyId, PostCreateAgentId, SoapGetAgentId];
        foreach (string operationId in operations)
        {
            data.Add(operationId, 502, false);
            data.Add(operationId, 502, true);
            data.Add(operationId, 504, false);
            data.Add(operationId, 504, true);
            data.Add(operationId, 400, false);
            data.Add(operationId, 404, false);
            data.Add(operationId, 500, false);
        }

        return data;
    }

    /// <summary>
    /// The operation is sent at its normal address to the facade socket of its surface, and the backend
    /// answers it with <paramref name="status"/>, its own Content-Type, <c>X-Stub-Id</c> and
    /// <c>X-Backend-Error</c> headers and a fixed body, or an empty one.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendErrors))]
    public async Task BackendErrorResponsePassesThroughUnchanged(string operationId, int status, bool emptyBody)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        bool soap = operation.Surface == Surface.Soap;
        string contentType = soap ? SoapContentType : RestContentType;
        byte[] body = emptyBody ? [] : Encoding.UTF8.GetBytes(soap ? SoapFaultBody(status) : RestErrorBody(status));
        var logs = new LogCapture();
        await using StubBackend backend = await StubBackend.StartAsync(
            StubId,
            new StubBackendOptions
            {
                Status = status,
                Body = body,
                ExtraHeaders = new Dictionary<string, string>
                {
                    [ContentTypeHeader] = contentType,
                    [BackendErrorHeader] = BackendErrorValue,
                },
            },
            ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = backend.RestBaseUrl,
                BackendSoapBaseUrl = backend.SoapBaseUrl,
                LoggerProvider = logs,
            },
            ct);

        PreparedRequest request = OperationRequests.For(operation);
        Uri socket = soap ? facade.SoapBaseAddress : facade.RestBaseAddress;
        RawResponse response;
        using (HttpClient client = facade.CreateRawClient())
        {
            response = await FacadeFactory.SendAsync(client, request.ToMessage(socket), ct);
        }

        Assert.Equal(status, response.Status);
        AssertSingleHeader(response, ContentTypeHeader, contentType);
        AssertSingleHeader(response, StubIdHeader, StubId);
        AssertSingleHeader(response, BackendErrorHeader, BackendErrorValue);
        AssertSingleHeader(response, ContentLengthHeader, body.Length.ToString(CultureInfo.InvariantCulture));
        Assert.True(response.Body.AsSpan().SequenceEqual(body), "facade response body differs from the backend's body");

        // The response is the backend's answer to this very call, on the listener of its surface.
        Assert.True(backend.Received.Count == 1, "backend did not receive exactly one request");
        IReadOnlyList<StubRequest> received = backend.ReceivedOn(soap ? StubListener.Soap : StubListener.Rest);
        Assert.True(received.Count == 1, "backend request did not arrive on the listener of its surface");
        Assert.Equal(request.PathAndQuery, received[0].RawTarget);

        // Assert.Single would print the entries, whose messages hold durations.
        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "backend error response did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(status, calls[0].Value("Status"));
    }

    // A JSON error body of the backend's own; the 502 and 504 ones are problem documents unlike the
    // facade's, and the 404 one carries a non-ASCII character so that its UTF-8 bytes are compared too.
    private static string RestErrorBody(int status) => status switch
    {
        400 => "{\"error\":\"invalid agent\",\"status\":400}",
        404 => "{\"error\":\"no agent named Zo\u00eb\",\"status\":404}",
        500 => "{\"error\":\"backend failure\",\"status\":500}",
        502 => "{\"type\":\"about:blank\",\"title\":\"Upstream unavailable\",\"status\":502,\"detail\":\"The backend's own upstream could not be reached.\"}",
        504 => "{\"type\":\"about:blank\",\"title\":\"Upstream timed out\",\"status\":504,\"detail\":\"The backend's own upstream did not respond in time.\"}",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    // A SOAP 1.1 Fault envelope of the backend's own, whose faultstring is neither "Backend unavailable"
    // nor "Backend timed out".
    private static string SoapFaultBody(int status) => status switch
    {
        400 => SoapFault("soap:Client", "Agent request rejected"),
        404 => SoapFault("soap:Client", "No agent named Zo\u00eb"),
        500 => SoapFault("soap:Server", "Agent lookup failed"),
        502 => SoapFault("soap:Server", "Upstream unavailable"),
        504 => SoapFault("soap:Server", "Upstream timed out"),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string SoapFault(string faultCode, string faultString) =>
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><soap:Fault>"
        + "<faultcode>" + faultCode + "</faultcode><faultstring>" + faultString + "</faultstring>"
        + "</soap:Fault></soap:Body></soap:Envelope>";

    // The header must arrive exactly once with exactly the backend's value; the message names the header,
    // so a missing header gives a signature of its own.
    private static void AssertSingleHeader(RawResponse response, string name, string expected)
    {
        Assert.True(response.Headers.TryGetValue(name, out string[]? values), "facade response has no " + name + " header");
        Assert.True(values.Length == 1, "facade response does not carry the " + name + " header exactly once");
        Assert.Equal(expected, values[0]);
    }
}

// Forwarding transport (class ForwardingTransportTests), observed as raw bytes on both sides of the
// facade. The backends are raw TcpListener sockets and the caller is a raw socket where bytes matter,
// because HttpClient adds Content-Length: 0 to a body-less write, refuses non-ASCII header values and
// response heads over 64 KiB on the test's own side, sends a whole body before it reads any response,
// and a Kestrel stub would decode the bytes it is meant to record. The backend's authority is replaced
// by a fixed token before request heads are compared.
/// <summary>
/// Raw-byte checks of what the facade sends to a backend and returns to a caller: single delivery of
/// body-less writes and of writes sent with <c>Expect: 100-continue</c>, the unchanged wire form of
/// body-less requests, Latin-1 header bytes in both directions, response heads beyond 64 KiB, request
/// heads at and over the limit of 100 header lines, chunked writes whose head and trailer lines reach or
/// pass that limit together, the unavailable answer to a backend that breaks its reply, uploads that carry
/// only the caller's own <c>Expect</c>, and a backend's answer to, or drop of, an upload the caller has
/// paused.
/// </summary>
public sealed class ForwardingTransportTests
{
    private const string PostCreateTenderId = "Rest.postCreateTender";
    private const string PostCreateAgentId = "Rest.postCreateAgent";
    private const string PostCreateRetailSaleId = "Rest.postCreateRetailSale";
    private const string GetCompanyId = "Rest.getCompany";
    private const string GetAgentNamesId = "Soap.getAgentNames";

    // How a backend that has received the whole request breaks the exchange before any usable response
    // head: bytes that are no HTTP response, a close or a reset without an answer, a status line whose code
    // is not a number, a header line without a colon, and two Content-Length values that disagree.
    private const string GarbageReply = "garbage";
    private const string CloseUnanswered = "close";
    private const string ResetUnanswered = "reset";
    private const string BadStatusLine = "bad-status-line";
    private const string BadHeaderLine = "bad-header-line";
    private const string ConflictingContentLength = "conflicting-content-length";

    private const string ForwardedOutcome = "forwarded";
    private const string UnavailableOutcome = "unavailable";

    private const string JsonContentType = "application/json";
    private const string ExpectHeaderName = "Expect";

    // The HTTP stack's default response-head limit, 64 KiB, and header lines that together exceed it.
    private const int DefaultResponseHeadLimit = 64 * 1024;
    private const int FillerLineCount = 80;
    private const int FillerValueLength = 1000;

    // Kestrel's default limit on the header lines of a request head, Host included, which the facade keeps
    // on every listener; CallerHead writes two of those lines itself, Host and Connection.
    private const int HeaderLineLimit = 100;
    private const int CallerHeadFieldLines = 2;

    // The header lines of the chunked write whose trailer lines Kestrel counts against the same limit.
    private const int TrailerWriteHeadLines = 60;

    // An upload of 1 MiB, the size from which an HTTP client such as curl asks for 100-continue itself.
    private const int LargeUploadLength = 1024 * 1024;

    // A target deadline of one second, the handler's default wait for 100 Continue.
    private const int ShortDeadlineSeconds = 1;

    // A REST target that is no catalogue operation, which the REST catch-all forwards to the default target.
    private const string UnmatchedTarget = "/x";

    // Stands for the backend's authority (127.0.0.1:<port>) in every compared request head.
    private const string BackendAuthorityToken = "{backend}";

    private const string ProbeHeader = "X-Probe: one\r\n";

    // The callers of a paused upload: a REST write, a SOAP call naming its operation in SOAPAction, and a
    // SOAP call without SOAPAction, whose body the facade inspects and buffers before forwarding it.
    private const string RestCaller = "rest";
    private const string SoapActionCaller = "soap-action";
    private const string SoapInspectedCaller = "soap-inspected";

    // How an early-answering backend treats a request once it has read the head: it answers 413 and
    // closes its side, answers and keeps the connection open, or answers 100 Continue, reads part of the
    // body and then answers 413; it reads part of the body and closes or resets without answering; or it
    // sends an interim response, 100 Continue after the facade's wait for it or 103 Early Hints at once,
    // and then reads the whole body and answers 200.
    private const string AnswerThenClose = "answer-then-close";
    private const string AnswerKeepOpen = "answer-keep-open";
    private const string ContinueThenAnswer = "continue-then-answer";
    private const string LateContinue = "late-continue";
    private const string EarlyHints = "early-hints";

    // The early answer, as the backend sends it and as the caller must receive its status, headers and body.
    private const string EarlyAnswerHead =
        "HTTP/1.1 413 Payload Too Large\r\nContent-Type: text/plain\r\nX-Early: rejected\r\nContent-Length: 8\r\n";
    private const string EarlyAnswerBody = "rejected";
    private const string EarlyAnswerHeaderLine = "\r\nX-Early: rejected\r\n";
    private const string EarlyAnswerContentTypeLine = "\r\nContent-Type: text/plain\r\n";

    // A paused upload declares 4 MiB, or is chunked, and sends one 128 KiB part, then only reads. The
    // target deadline is several seconds, and the backend's answer must arrive well inside it.
    private const int PausedDeclaredLength = 4 * 1024 * 1024;
    private const int PausedPartLength = 128 * 1024;
    private const int PausedUploadDeadlineSeconds = 8;

    // An upload during which the backend sends an interim response: 256 KiB, sent in two halves.
    private const int InterimUploadLength = 256 * 1024;
    private const int InterimDeadlineSeconds = 6;

    // A paused upload carrying the caller's own Expect whose declared length is small enough, at most
    // 1024 bytes, that the HTTP stack still sends the body after the backend has refused the expectation
    // with a final status; the caller sends half of it.
    private const int SmallDeclaredLength = 1000;
    private const int SmallPartLength = 500;

    // Makes Kestrel report the request as upgradable; YARP tunnels only WebSocket and SPDY/* upgrades and
    // drops these hop-by-hop headers from any other request.
    private const string UntunnelledUpgradeHeaders = "Connection: Upgrade\r\nUpgrade: foo\r\n";
    private const string ContentLengthZero = "Content-Length: 0\r\n";
    private const string ExpectContinueHeader = "Expect: 100-continue\r\n";
    private const string ProblemContentTypeLine = "\r\nContent-Type: application/problem+json\r\n";

    // UTF-8 "Zoë" and Latin-1 "café" as raw header lines; Latin-1 maps each byte to one char, so these
    // strings and their Encoding.Latin1 bytes are the same octets.
    private const string Utf8NameHeader = "X-Name: Zo\u00c3\u00ab\r\n";
    private const string Latin1ResponseHeader = "X-Latin: caf\u00e9\r\n";
    private const string Utf8ResponseHeader = "X-Utf8: Zo\u00c3\u00ab\r\n";
    private const string BackendBody = "{\"stub\":\"hdr\"}";

    private static readonly TimeSpan RawCallerTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LogWaitBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnavailableBound = TimeSpan.FromSeconds(5);

    // Well inside the paused upload's 8 s deadline; the answer itself takes milliseconds.
    private static readonly TimeSpan PausedAnswerBound = TimeSpan.FromSeconds(2);

    // The caller sends the second half of an interim-answered upload after this pause, by which time the
    // facade's one-second wait for 100 Continue has ended and the late 100 Continue (1.5 s) has arrived.
    private static readonly TimeSpan InterimUploadPause = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Body-less writes and the headers they carry: the path-only catalogue write <c>postCreateTender</c>,
    /// alone, with <c>Expect: 100-continue</c>, and with an explicit <c>Content-Length: 0</c> as well; a
    /// <c>PUT</c> and a custom method sent to the REST catch-all; and a <c>POST</c> asking for an upgrade
    /// the forwarder does not tunnel, which it forwards as an ordinary request.
    /// </summary>
    public static TheoryData<string, string, string> BodylessWrites() => new()
    {
        { "POST", CatalogueTarget(PostCreateTenderId), ProbeHeader },
        { "POST", CatalogueTarget(PostCreateTenderId), ExpectContinueHeader },
        { "POST", CatalogueTarget(PostCreateTenderId), ContentLengthZero + ExpectContinueHeader },
        { "PUT", UnmatchedTarget, ProbeHeader },
        { "FOO", UnmatchedTarget, ProbeHeader },
        { "POST", UnmatchedTarget, UntunnelledUpgradeHeaders },
    };

    /// <summary>
    /// Whether the body of a write sent with <c>Expect: 100-continue</c> is chunked rather than framed by
    /// <c>Content-Length</c>.
    /// </summary>
    public static TheoryData<bool> ExpectContinueBodyFramings() => new() { false, true };

    /// <summary>
    /// Body-less requests, whether the HTTP stack writes <c>Content-Length: 0</c> for their method, and
    /// whether the facade sends them to a backend that closes unanswered exactly once. Both hold for
    /// <c>POST</c>. <c>GET</c> and <c>DELETE</c> get no <c>Content-Length</c>; they are forwarded without
    /// content so that no framing header is added, which leaves the HTTP stack free to resend them.
    /// </summary>
    public static TheoryData<string, string, bool, bool> BodylessHeads() => new()
    {
        { "POST", CatalogueTarget(PostCreateTenderId), true, true },
        { "GET", CatalogueTarget(GetCompanyId), false, false },
        { "DELETE", UnmatchedTarget, false, false },
    };

    /// <summary>
    /// Every way of breaking the reply, for a REST write with a JSON body, a body-less REST write, a REST
    /// read and a SOAP operation, which always carries a body.
    /// </summary>
    public static MatrixTheoryData<string, string> BrokenRepliesByOperation() => new(
        [GarbageReply, CloseUnanswered, ResetUnanswered, BadStatusLine, BadHeaderLine, ConflictingContentLength],
        [PostCreateAgentId, PostCreateRetailSaleId, GetCompanyId, GetAgentNamesId]);

    /// <summary>
    /// A body-less read and a write with a body, each answered with a success and with an error status.
    /// </summary>
    public static TheoryData<string, int> LargeResponseHeads() => new()
    {
        { GetCompanyId, 200 },
        { GetCompanyId, 404 },
        { PostCreateAgentId, 200 },
        { PostCreateAgentId, 404 },
    };

    /// <summary>
    /// Request heads of exactly the 100 header lines Kestrel admits, on the REST and SOAP listeners, which
    /// forward them, and of one line more, on the REST, SOAP and admin listeners.
    /// </summary>
    public static TheoryData<ListenerKind, int> HeaderLineHeads() => new()
    {
        { ListenerKind.Rest, HeaderLineLimit },
        { ListenerKind.Soap, HeaderLineLimit },
        { ListenerKind.Rest, HeaderLineLimit + 1 },
        { ListenerKind.Soap, HeaderLineLimit + 1 },
        { ListenerKind.Admin, HeaderLineLimit + 1 },
    };

    /// <summary>
    /// Trailer sections of a chunked write whose head has <see cref="TrailerWriteHeadLines"/> header lines:
    /// one that brings head and trailer to exactly the 100 lines Kestrel admits, and one of a line more.
    /// </summary>
    public static TheoryData<int> TrailerLineCounts() => new()
    {
        HeaderLineLimit - TrailerWriteHeadLines,
        HeaderLineLimit - TrailerWriteHeadLines + 1,
    };

    /// <summary>Whether an upload is chunked rather than framed by <c>Content-Length</c>.</summary>
    public static TheoryData<bool> UploadFramings() => new() { false, true };

    /// <summary>
    /// Every caller of a paused upload, of declared length or chunked, against each way a backend answers
    /// it early: 413 and a close of its side, 413 on a connection it keeps open, and, to a caller's own
    /// <c>Expect: 100-continue</c>, 100 Continue, part of the body read, then 413.
    /// </summary>
    public static MatrixTheoryData<string, bool, string> PausedUploadsAnswered() => new(
        [RestCaller, SoapActionCaller, SoapInspectedCaller],
        [false, true],
        [AnswerThenClose, AnswerKeepOpen, ContinueThenAnswer]);

    /// <summary>
    /// Every caller of a paused upload, of declared length or chunked, against a backend that reads part of
    /// it and then closes or resets the connection without answering.
    /// </summary>
    public static MatrixTheoryData<string, bool, string> PausedUploadsDropped() => new(
        [RestCaller, SoapActionCaller, SoapInspectedCaller],
        [false, true],
        [CloseUnanswered, ResetUnanswered]);

    /// <summary>
    /// The ways a backend answers the head of an upload at once: closing its side, or keeping the
    /// connection open.
    /// </summary>
    public static TheoryData<string> ImmediateAnswers() => new() { AnswerThenClose, AnswerKeepOpen };

    /// <summary>
    /// The interim responses a backend sends during an upload it goes on to read in full: a 100 Continue to
    /// the caller's own <c>Expect</c> that arrives after the facade's wait for it, and 103 Early Hints to an
    /// upload without <c>Expect</c>.
    /// </summary>
    public static TheoryData<string> InterimResponses() => new() { LateContinue, EarlyHints };

    [Theory]
    [MemberData(nameof(BodylessWrites))]
    public async Task BodylessWriteToBackendThatClosesUnansweredIsSentOnce(string method, string target, string headers)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawBackend backend = RawBackend.Start(reply: null);
        await using FacadeFactory facade = await StartFacadeAsync(backend, ct);

        RawExchange response = await SendRawAsync(facade.RestPort, CallerHead(method, target, headers), ct);

        Assert.Equal(502, response.Status);
        Assert.True(
            response.Head.Contains(ProblemContentTypeLine, StringComparison.Ordinal),
            "unanswered body-less write did not get the facade's problem+json response");
        Assert.True(backend.Heads.Count == 1, "body-less write did not reach the closing backend exactly once");
    }

    [Theory]
    [MemberData(nameof(ExpectContinueBodyFramings))]
    public async Task WriteWithBodyAndExpectContinueToBackendThatClosesUnansweredIsSentOnce(bool chunked)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawBackend backend = RawBackend.Start(reply: null);
        await using FacadeFactory facade = await StartFacadeAsync(backend, ct);
        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        string body = Encoding.Latin1.GetString(write.Body!);
        string length = body.Length.ToString(chunked ? "x" : "D", CultureInfo.InvariantCulture);
        string headers = "Content-Type: " + write.ContentType + "\r\n"
            + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + length + "\r\n")
            + ExpectContinueHeader;
        string framedBody = chunked ? length + "\r\n" + body + "\r\n0\r\n\r\n" : body;

        RawExchange response = await SendRawAsync(
            facade.RestPort, CallerHead(write.Method.Method, write.PathAndQuery, headers, framedBody), ct);

        Assert.Equal(502, response.Status);
        Assert.True(
            response.Head.Contains(ProblemContentTypeLine, StringComparison.Ordinal),
            "unanswered write with a body did not get the facade's problem+json response");
        IReadOnlyList<byte[]> heads = backend.Heads;
        Assert.True(heads.Count == 1, "write with a body and Expect: 100-continue did not reach the closing backend exactly once");
        Assert.True(
            backend.Normalise(heads[0]).Contains("\r\n" + ExpectContinueHeader, StringComparison.Ordinal),
            "Expect: 100-continue did not reach the backend unchanged");
    }

    // The backend answers the first request on a connection and keeps it open, then closes it on the
    // next request without answering, as a backend that fails after receiving a write on a pooled
    // connection does. A resend would go out on a new connection, where the backend answers again.
    [Fact]
    public async Task ExpectContinueWriteOnReusedConnectionToBackendThatClosesUnansweredIsSentOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] reply = Encoding.Latin1.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + "Content-Length: " + BackendBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "\r\n"
            + BackendBody);
        await using RawBackend backend = RawBackend.Start(reply, closeNextUnanswered: true);
        await using FacadeFactory facade = await StartFacadeAsync(backend, ct);

        RawExchange first = await SendRawAsync(
            facade.RestPort, CallerHead("GET", CatalogueTarget(GetCompanyId), ProbeHeader), ct);
        RawExchange write = await SendRawAsync(
            facade.RestPort, CallerHead("POST", CatalogueTarget(PostCreateTenderId), ExpectContinueHeader), ct);

        Assert.Equal(200, first.Status);
        Assert.Equal(502, write.Status);
        Assert.True(
            write.Head.Contains(ProblemContentTypeLine, StringComparison.Ordinal),
            "unanswered write on a reused connection did not get the facade's problem+json response");
        Assert.True(
            backend.Heads.Count == 2,
            "write on a reused connection did not reach the closing backend exactly once");
    }

    [Theory]
    [MemberData(nameof(BodylessHeads))]
    public async Task BodylessRequestKeepsItsWireForm(string method, string target, bool contentLengthZero, bool sentOnce)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RawBackend backend = RawBackend.Start(reply: null);
        await using FacadeFactory facade = await StartFacadeAsync(backend, ct);

        RawExchange response = await SendRawAsync(facade.RestPort, CallerHead(method, target, ProbeHeader), ct);

        Assert.Equal(502, response.Status);
        IReadOnlyList<byte[]> heads = backend.Heads;
        if (sentOnce)
        {
            Assert.True(heads.Count == 1, "body-less write did not reach the closing backend exactly once");
        }
        else
        {
            Assert.True(heads.Count >= 1, "body-less request did not reach the backend");
        }

        string expected = method + " " + target + " HTTP/1.1\r\n"
            + "Host: " + BackendAuthorityToken + "\r\n"
            + ProbeHeader
            + (contentLengthZero ? ContentLengthZero : string.Empty)
            + "\r\n";
        foreach (byte[] head in heads)
        {
            Assert.Equal(expected, backend.Normalise(head));
        }
    }

    [Fact]
    public async Task NonAsciiHeaderBytesPassThroughBothWays()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] reply = Encoding.Latin1.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + Latin1ResponseHeader
            + Utf8ResponseHeader
            + "Content-Length: " + BackendBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "Connection: close\r\n"
            + "\r\n"
            + BackendBody);
        await using RawBackend backend = RawBackend.Start(reply);
        await using FacadeFactory facade = await StartFacadeAsync(backend, ct);
        string target = CatalogueTarget(GetCompanyId);

        RawExchange response = await SendRawAsync(facade.RestPort, CallerHead("GET", target, Utf8NameHeader), ct);

        IReadOnlyList<byte[]> heads = backend.Heads;
        Assert.True(heads.Count == 1, "request with a non-ASCII header did not reach the backend exactly once");
        Assert.Equal(
            "GET " + target + " HTTP/1.1\r\nHost: " + BackendAuthorityToken + "\r\n" + Utf8NameHeader + "\r\n",
            backend.Normalise(heads[0]));

        Assert.Equal(200, response.Status);
        Assert.True(
            response.Head.Contains("\r\n" + Latin1ResponseHeader, StringComparison.Ordinal),
            "Latin-1 response header bytes did not reach the caller unchanged");
        Assert.True(
            response.Head.Contains("\r\n" + Utf8ResponseHeader, StringComparison.Ordinal),
            "UTF-8 response header bytes did not reach the caller unchanged");
        Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));
    }

    /// <summary>
    /// A backend's response head beyond the HTTP stack's 64 KiB default, with a success or an error
    /// status, reaches the caller as the backend sent it, for a body-less read and for a write with a
    /// body: the status, every header line in order and the body bytes, logged <c>forwarded</c> with the
    /// backend's status. The facade sets no response-head limit of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(LargeResponseHeads))]
    public async Task ResponseHeadBeyond64KiBReachesCallerUnchanged(string operationId, int status)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string filler = FillerHeaderLines();
        Assert.True(filler.Length > DefaultResponseHeadLimit, "filler header lines do not exceed the 64 KiB default");
        byte[] reply = Encoding.Latin1.GetBytes(
            "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + (status == 200 ? " OK" : " Not Found") + "\r\n"
            + "Content-Type: " + JsonContentType + "\r\n"
            + filler
            + "Content-Length: " + BackendBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "\r\n"
            + BackendBody);
        var logs = new LogCapture();
        await using ScriptedBackend backend = ScriptedBackend.Start(reply);
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, timeoutSeconds: null, ct);

        PreparedRequest request = OperationRequests.ForRest(OperationCatalogue.Default.GetById(operationId));
        string body = request.Body is null ? string.Empty : Encoding.Latin1.GetString(request.Body);
        string headers = request.Body is null
            ? ProbeHeader
            : "Content-Type: " + request.ContentType + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n";
        RawExchange response = await SendRawAsync(
            facade.RestPort, CallerHead(request.Method.Method, request.PathAndQuery, headers, body), ct);

        Assert.Equal(status, response.Status);
        Assert.True(
            response.Head.Contains("\r\n" + filler, StringComparison.Ordinal),
            "backend's header lines beyond 64 KiB did not reach the caller unchanged and in order");
        Assert.True(
            response.Head.Contains("\r\nContent-Type: " + JsonContentType + "\r\n", StringComparison.Ordinal),
            "backend's Content-Type did not reach the caller unchanged");
        Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));

        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "request answered with a large head did not reach the backend exactly once");
        Assert.True(received[0].Body.AsSpan().SequenceEqual(request.Body ?? []), "backend did not receive the request body byte for byte");

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "large response head did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(status, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Information, calls[0].Level);
    }

    /// <summary>
    /// The facade keeps Kestrel's limit of 100 header lines, <c>Host</c> included, on every listener. A
    /// head of 101 header lines sent to the REST, SOAP or admin listener is answered 431 by the facade
    /// itself: it never reaches the backend and writes no backend call line. A head of exactly 100 header
    /// lines, for the catalogue REST read <c>getCompany</c> or a SOAP call naming its operation in
    /// <c>SOAPAction</c>, is forwarded once: the backend receives the request line, the body and every
    /// field line the caller sent unchanged, apart from <c>Host</c>, which names the backend, and the
    /// hop-by-hop <c>Connection</c>, which is dropped, and its 200 reaches the caller, logged
    /// <c>forwarded</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeaderLineHeads))]
    public async Task HeadAtHeaderLineLimitIsForwardedAndOneLineOverIsRefused(ListenerKind listener, int headerLines)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using ScriptedBackend backend = ScriptedBackend.Start(OkReply());
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, timeoutSeconds: null, ct);

        // The REST and SOAP calls are catalogue operations; the admin request is the health check.
        PreparedRequest? call = listener switch
        {
            ListenerKind.Rest => OperationRequests.ForRest(OperationCatalogue.Default.GetById(GetCompanyId)),
            ListenerKind.Soap => OperationRequests.ForSoap(OperationCatalogue.Default.GetById(GetAgentNamesId)),
            ListenerKind.Admin => null,
            _ => throw new ArgumentOutOfRangeException(nameof(listener)),
        };
        int port = listener switch
        {
            ListenerKind.Rest => facade.RestPort,
            ListenerKind.Soap => facade.SoapPort,
            _ => facade.AdminPort,
        };
        string method = call?.Method.Method ?? "GET";
        string target = call?.PathAndQuery ?? AdminRoutes.Health;
        string body = call?.Body is { } bytes ? Encoding.Latin1.GetString(bytes) : string.Empty;

        // The call's own field lines, then distinct tiny lines up to the intended count with the Host and
        // Connection lines CallerHead adds.
        var callerFields = new List<string>();
        if (call?.ContentType is { } contentType)
        {
            callerFields.Add("Content-Type: " + contentType);
        }

        if (body.Length > 0)
        {
            callerFields.Add("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture));
        }

        if (call?.SoapAction is { } soapAction)
        {
            callerFields.Add("SOAPAction: " + soapAction);
        }

        for (int line = callerFields.Count + CallerHeadFieldLines; line < headerLines; line++)
        {
            callerFields.Add("X-Line-" + line.ToString("D3", CultureInfo.InvariantCulture) + ": v");
        }

        byte[] request = CallerHead(method, target, string.Concat(callerFields.Select(field => field + "\r\n")), body);
        Assert.True(
            FieldLines(Encoding.Latin1.GetString(request)).Length == headerLines,
            "composed request head does not carry the intended number of header lines");

        RawExchange response = await SendRawAsync(port, request, ct);

        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        if (headerLines > HeaderLineLimit)
        {
            Assert.Equal(431, response.Status);
            Assert.True(received.Count == 0, "head over the header line limit reached the backend");
            Assert.True(logs.BackendCalls.Count == 0, "head over the header line limit wrote a backend call line");
        }
        else
        {
            Assert.Equal(200, response.Status);
            Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));
            Assert.True(received.Count == 1, "head at the header line limit did not reach the backend exactly once");
            Assert.True(
                received[0].Head.StartsWith(method + " " + target + " HTTP/1.1\r\n", StringComparison.Ordinal),
                "head at the header line limit did not reach the backend with its request line unchanged");

            string[] forwarded = FieldLines(received[0].Head);
            string backendHost = "Host: " + new Uri(backend.BaseUrl).Authority;
            Assert.True(
                forwarded.Count(field => field.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) == 1
                    && forwarded.Contains(backendHost, StringComparer.Ordinal),
                "head at the header line limit did not reach the backend with one Host line naming the backend");
            Assert.Equal(
                callerFields.Order(StringComparer.Ordinal),
                forwarded.Where(field => !string.Equals(field, backendHost, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.True(
                received[0].Body.AsSpan().SequenceEqual(Encoding.Latin1.GetBytes(body)),
                "backend did not receive the body of a head at the header line limit byte for byte");

            IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
            Assert.True(calls.Count == 1, "head at the header line limit did not write exactly one backend call line");
            Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
            Assert.Equal(200, calls[0].Value("Status"));
        }
    }

    /// <summary>
    /// Kestrel counts the trailer lines of a chunked request body against the same limit of 100 header
    /// lines as its head. A chunked catalogue write <c>postCreateAgent</c> whose head of 60 header lines
    /// and trailer section hold exactly 100 lines together is forwarded: the backend receives the whole
    /// body once, byte for byte, and its 200 reaches the caller, logged <c>forwarded</c>. With one trailer
    /// line more, the facade's Kestrel answers 431 once it reads the trailer section, and the backend never
    /// receives the complete request.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrailerLineCounts))]
    public async Task TrailerLinesWithinHeaderLineLimitAreForwardedAndOneLineOverIsRefused(int trailerLines)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using ScriptedBackend backend = ScriptedBackend.Start(OkReply());
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, timeoutSeconds: null, ct);

        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        string body = Encoding.Latin1.GetString(write.Body!);

        // The write's own field lines, then distinct tiny lines up to the intended count with the Host and
        // Connection lines CallerHead adds; the body is one chunk, followed by the last chunk and the
        // trailer section.
        var headFields = new List<string> { "Content-Type: " + write.ContentType, "Transfer-Encoding: chunked" };
        for (int line = headFields.Count + CallerHeadFieldLines; line < TrailerWriteHeadLines; line++)
        {
            headFields.Add("X-Line-" + line.ToString("D3", CultureInfo.InvariantCulture) + ": v");
        }

        var framedBody = new StringBuilder()
            .Append(body.Length.ToString("x", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append(body).Append("\r\n")
            .Append("0\r\n");
        for (int line = 0; line < trailerLines; line++)
        {
            framedBody.Append("X-Trailer-").Append(line.ToString("D3", CultureInfo.InvariantCulture)).Append(": t\r\n");
        }

        framedBody.Append("\r\n");

        byte[] request = CallerHead(
            write.Method.Method,
            write.PathAndQuery,
            string.Concat(headFields.Select(field => field + "\r\n")),
            framedBody.ToString());
        Assert.True(
            FieldLines(Encoding.Latin1.GetString(request)).Length == TrailerWriteHeadLines,
            "composed request head does not carry the intended number of header lines");

        RawExchange response = await SendRawAsync(facade.RestPort, request, ct);

        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        if (TrailerWriteHeadLines + trailerLines > HeaderLineLimit)
        {
            Assert.Equal(431, response.Status);
            Assert.True(received.Count == 0, "write whose trailer lines pass the header line limit completed at the backend");
        }
        else
        {
            Assert.Equal(200, response.Status);
            Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));
            Assert.True(received.Count == 1, "write whose trailer lines reach the header line limit did not reach the backend exactly once");
            Assert.True(
                received[0].Body.AsSpan().SequenceEqual(write.Body!),
                "backend did not receive the chunked body of a write at the header line limit byte for byte");

            IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
            Assert.True(calls.Count == 1, "write at the header line limit did not write exactly one backend call line");
            Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
            Assert.Equal(200, calls[0].Value("Status"));
        }
    }

    /// <summary>
    /// A backend that receives the whole request and then gives no usable response head gets the
    /// facade's unavailable answer, whether or not the request carried a body: REST 502 with the fixed
    /// problem+json body, SOAP HTTP 500 with the SOAP 1.1 "Backend unavailable" Fault, logged
    /// <c>unavailable</c> with that status. A write reaches the backend exactly once; the HTTP stack may
    /// resend a body-less read.
    /// </summary>
    [Theory]
    [MemberData(nameof(BrokenRepliesByOperation))]
    public async Task BackendThatBreaksItsReplyGetsTheUnavailableAnswer(string breakage, string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using ScriptedBackend backend = ScriptedBackend.Start(BrokenReply(breakage), reset: breakage == ResetUnanswered);
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, timeoutSeconds: null, ct);

        OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
        PreparedRequest request = OperationRequests.For(operation);
        Uri socket = operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        using HttpClient client = facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(client, request.ToMessage(socket), ct);

        int expectedStatus = UnavailableAnswers.AssertMatches(response, operationId);
        Assert.True(response.Elapsed <= UnavailableBound, "unavailable answer to a broken reply exceeded 5 s bound");

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "broken reply did not write exactly one backend call line");
        Assert.Equal(UnavailableOutcome, calls[0].Value("Outcome"));
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);

        int received = backend.Requests.Count;
        if (request.Method == HttpMethod.Get)
        {
            Assert.True(received >= 1, "read answered with a broken reply did not reach the backend");
        }
        else
        {
            Assert.True(received == 1, "write answered with a broken reply did not reach the backend exactly once");
        }
    }

    /// <summary>
    /// A backend that refuses every request carrying an <c>Expect</c> header with 417, and accepts the
    /// same upload without one, accepts a 1 MiB upload the caller sent without <c>Expect</c>, framed by
    /// <c>Content-Length</c> or chunked: the facade adds no expectation, so the backend's 200 reaches the
    /// caller, logged <c>forwarded</c>, and the backend receives the body byte for byte, once.
    /// </summary>
    [Theory]
    [MemberData(nameof(UploadFramings))]
    public async Task UploadWithoutExpectIsAcceptedByBackendThatRefusesExpectations(bool chunked)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        byte[] refusal = "HTTP/1.1 417 Expectation Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await using ScriptedBackend backend = ScriptedBackend.Start(OkReply(), expectationReply: refusal);
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, timeoutSeconds: null, ct);

        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        string body = new('x', LargeUploadLength);
        string headers = "Content-Type: " + write.ContentType + "\r\n"
            + (chunked
                ? "Transfer-Encoding: chunked\r\n"
                : "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n");
        string framedBody = chunked
            ? body.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n" + body + "\r\n0\r\n\r\n"
            : body;

        RawExchange response = await SendRawAsync(
            facade.RestPort, CallerHead(write.Method.Method, write.PathAndQuery, headers, framedBody), ct);

        Assert.Equal(200, response.Status);
        Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));
        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "upload without Expect did not reach the backend exactly once");
        Assert.True(received[0].HeaderCount(ExpectHeaderName) == 0, "an Expect header the caller did not send reached the backend");
        Assert.True(received[0].Body.AsSpan().SequenceEqual(Encoding.Latin1.GetBytes(body)), "backend did not receive the upload byte for byte");

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "accepted upload did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(200, calls[0].Value("Status"));
    }

    /// <summary>
    /// A caller's own <c>Expect: 100-continue</c> reaches a backend that never sends 100 Continue and
    /// answers once the body has arrived, under a one-second target deadline: the facade's wait for 100
    /// Continue ends inside the deadline, so the body is sent, once, and the backend's 200 reaches the
    /// caller, logged <c>forwarded</c>, never the facade's 504.
    /// </summary>
    [Fact]
    public async Task CallerExpectContinueToSilentBackendIsAnsweredWithinShortDeadline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using ScriptedBackend backend = ScriptedBackend.Start(OkReply());
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, ShortDeadlineSeconds, ct);

        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        string body = Encoding.Latin1.GetString(write.Body!);
        string headers = "Content-Type: " + write.ContentType + "\r\n"
            + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + ExpectContinueHeader;

        RawExchange response = await SendRawAsync(
            facade.RestPort, CallerHead(write.Method.Method, write.PathAndQuery, headers, body), ct);

        Assert.Equal(200, response.Status);
        Assert.Equal(BackendBody, Encoding.Latin1.GetString(response.Body));
        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "write with the caller's Expect did not reach the backend exactly once");
        Assert.True(received[0].HeaderCount(ExpectHeaderName) == 1, "the caller's Expect did not reach the backend exactly once");
        Assert.Equal("100-continue", received[0].HeaderValue(ExpectHeaderName));
        Assert.True(received[0].Body.AsSpan().SequenceEqual(write.Body!), "backend did not receive the body byte for byte");

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "write with the caller's Expect did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(200, calls[0].Value("Status"));
    }

    /// <summary>
    /// A backend that answers an upload the caller has paused, before reading all of it, has its answer
    /// reach the caller unchanged, well inside the deadline: for a REST write, a SOAP call naming its
    /// operation in <c>SOAPAction</c> and a SOAP call whose body the facade inspects, each declaring 4 MiB
    /// or chunked, sending 128 KiB and then only reading on its open connection. Whether the backend
    /// answers at once and closes its side, answers and keeps the connection open, or answers a caller's
    /// own <c>Expect: 100-continue</c>, reads part of the body and then answers, the caller gets the
    /// backend's 413 with its headers and body, logged <c>forwarded</c>; the backend receives the request
    /// once, with an <c>Expect</c> only when the caller sent one and no more of the body than the caller
    /// sent, and the facade logs no error.
    /// </summary>
    [Theory]
    [MemberData(nameof(PausedUploadsAnswered))]
    public async Task BackendAnswerToPausedUploadReachesCallerAtOnce(string caller, bool chunked, string behaviour)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        bool callerExpects = behaviour == ContinueThenAnswer;
        PausedUpload upload = PausedUpload.For(caller, chunked, callerExpects);
        await using EarlyAnswerBackend backend = EarlyAnswerBackend.Start(behaviour);
        FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, PausedUploadDeadlineSeconds, ct);

        UploadExchange exchange;
        try
        {
            exchange = await SendUploadAsync(upload.Port(facade), upload.HeadAndPart, rest: null, ct);
        }
        finally
        {
            // Stopping the host waits for the caller's connection to end, so everything the call logs, the
            // server's drain of the unsent body included, has been written before the log is read.
            await facade.DisposeAsync();
        }

        AssertEarlyAnswer(exchange);

        // Disposing the backend waits for its connections to end, so its count of body bytes is final. A
        // body of declared length goes out as the caller's own bytes, so the count shows that none of the
        // padding completing it was sent; the HTTP stack frames a chunked body in chunks of its own.
        await backend.DisposeAsync();
        Assert.True(chunked || backend.BodyBytesReceived <= upload.PartLength, "backend received more of the paused upload than the caller sent");

        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "paused upload did not reach the backend exactly once");
        Assert.Equal(callerExpects ? 1 : 0, received[0].HeaderCount(ExpectHeaderName));

        IReadOnlyList<CapturedLog> calls = logs.BackendCalls;
        Assert.True(calls.Count == 1, "answered paused upload did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(413, calls[0].Value("Status"));
        Assert.Equal(upload.JadeMethod, calls[0].Value("Operation"));
        Assert.Equal(LogLevel.Information, calls[0].Level);
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for a backend's answer to a paused upload");
    }

    /// <summary>
    /// A backend that reads part of an upload the caller has paused and then closes or resets the
    /// connection without answering gets the facade's unavailable answer at once, well inside the
    /// deadline, never the timeout: REST 502 with the fixed problem+json body, or the SOAP "Backend
    /// unavailable" Fault, logged <c>unavailable</c>. The upload reaches the backend once, with no more of
    /// the body than the caller sent, and the facade logs no error.
    /// </summary>
    [Theory]
    [MemberData(nameof(PausedUploadsDropped))]
    public async Task BackendThatDropsPausedUploadGetsTheUnavailableAnswerAtOnce(string caller, bool chunked, string behaviour)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        PausedUpload upload = PausedUpload.For(caller, chunked, expectContinue: false);
        await using EarlyAnswerBackend backend = EarlyAnswerBackend.Start(behaviour);
        FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, PausedUploadDeadlineSeconds, ct);

        UploadExchange exchange;
        try
        {
            exchange = await SendUploadAsync(upload.Port(facade), upload.HeadAndPart, rest: null, ct);
        }
        finally
        {
            await facade.DisposeAsync();
        }

        int expectedStatus = UnavailableAnswers.AssertMatches(exchange.ToRawResponse(), upload.OperationId);
        Assert.True(exchange.Elapsed <= PausedAnswerBound, "unavailable answer to a dropped paused upload did not arrive well inside the deadline");
        Assert.True(backend.Requests.Count == 1 && backend.Connections == 1, "dropped paused upload did not reach the backend exactly once");

        // Disposing the backend waits for its connections to end, so its count of body bytes is final. A
        // body of declared length goes out as the caller's own bytes, so the count shows that none of the
        // padding completing it was sent; the HTTP stack frames a chunked body in chunks of its own.
        await backend.DisposeAsync();
        Assert.True(chunked || backend.BodyBytesReceived <= upload.PartLength, "backend received more of the paused upload than the caller sent");

        IReadOnlyList<CapturedLog> calls = logs.BackendCalls;
        Assert.True(calls.Count == 1, "dropped paused upload did not write exactly one backend call line");
        Assert.Equal(UnavailableOutcome, calls[0].Value("Outcome"));
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for a dropped paused upload");
    }

    /// <summary>
    /// On a backend connection reused from an earlier call, a backend that answers the head of a paused
    /// upload at once, and keeps the connection open, has its 413 reach the caller unchanged well inside
    /// the deadline, although the HTTP stack's read ahead on the reused connection takes the answer before
    /// the facade has read any of the caller's body. Both calls are logged <c>forwarded</c> and travel on
    /// one backend connection.
    /// </summary>
    [Theory]
    [MemberData(nameof(UploadFramings))]
    public async Task BackendAnswerToHeadOfPausedUploadOnReusedConnectionReachesCallerAtOnce(bool chunked)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        PausedUpload upload = PausedUpload.For(RestCaller, chunked, expectContinue: false);
        await using EarlyAnswerBackend backend = EarlyAnswerBackend.Start(AnswerKeepOpen, answerFirstRequest: true);
        FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, PausedUploadDeadlineSeconds, ct);

        RawExchange first;
        UploadExchange exchange;
        try
        {
            first = await SendRawAsync(facade.RestPort, CallerHead("GET", CatalogueTarget(GetCompanyId), ProbeHeader), ct);
            exchange = await SendUploadAsync(facade.RestPort, upload.HeadAndPart, rest: null, ct);
        }
        finally
        {
            await facade.DisposeAsync();
        }

        Assert.Equal(200, first.Status);
        AssertEarlyAnswer(exchange);
        Assert.True(
            backend.Connections == 1 && backend.Requests.Count == 2,
            "the paused upload did not reach the backend once on the connection of the earlier call");

        // Disposing the backend waits for its connections to end, so its count of body bytes is final. A
        // body of declared length goes out as the caller's own bytes, so the count shows that none of the
        // padding completing it was sent; the HTTP stack frames a chunked body in chunks of its own.
        await backend.DisposeAsync();
        Assert.True(chunked || backend.BodyBytesReceived <= upload.PartLength, "backend received more of the paused upload than the caller sent");

        IReadOnlyList<CapturedLog> calls = logs.BackendCalls;
        Assert.True(calls.Count == 2, "reused connection did not write exactly two backend call lines");
        Assert.Equal(ForwardedOutcome, calls[1].Value("Outcome"));
        Assert.Equal(413, calls[1].Value("Status"));
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for an answer on a reused connection");
    }

    /// <summary>
    /// A backend that refuses the caller's own <c>Expect: 100-continue</c> on a small paused upload with
    /// 413 at once, before any of the body has been read, has its answer reach the caller unchanged well
    /// inside the deadline, whether it closes its side or keeps the connection open. The HTTP stack reads
    /// that answer before it starts sending the small body, and still sends it: the answer it read before
    /// the upload began ends the upload as soon as it starts. Logged <c>forwarded</c>, the request reaches
    /// the backend once, carrying the caller's <c>Expect</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ImmediateAnswers))]
    public async Task BackendRefusalOfExpectationOnSmallPausedUploadReachesCallerAtOnce(string behaviour)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using EarlyAnswerBackend backend = EarlyAnswerBackend.Start(behaviour);
        FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, PausedUploadDeadlineSeconds, ct);

        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        byte[] part = new byte[SmallPartLength];
        Array.Fill(part, (byte)'x');
        byte[] head = Encoding.Latin1.GetBytes(
            "POST " + write.PathAndQuery + " HTTP/1.1\r\nHost: facade\r\n"
            + "Content-Type: " + write.ContentType + "\r\n"
            + ExpectContinueHeader
            + "Content-Length: " + SmallDeclaredLength.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

        UploadExchange exchange;
        try
        {
            exchange = await SendUploadAsync(facade.RestPort, [.. head, .. part], rest: null, ct);
        }
        finally
        {
            await facade.DisposeAsync();
        }

        AssertEarlyAnswer(exchange);
        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "small paused upload with the caller's Expect did not reach the backend exactly once");
        Assert.Equal(1, received[0].HeaderCount(ExpectHeaderName));

        // Disposing the backend waits for its connections to end, so its count of body bytes is final. A
        // body of declared length goes out as the caller's own bytes, so the count shows that none of the
        // padding completing it was sent; the HTTP stack frames a chunked body in chunks of its own.
        await backend.DisposeAsync();
        Assert.True(backend.BodyBytesReceived <= SmallPartLength, "backend received more of the paused upload than the caller sent");

        IReadOnlyList<CapturedLog> calls = logs.BackendCalls;
        Assert.True(calls.Count == 1, "refused expectation did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(413, calls[0].Value("Status"));
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for a refused expectation");
    }

    /// <summary>
    /// An interim response during an upload does not end it: a 100 Continue to the caller's own
    /// <c>Expect</c> that arrives after the facade's one-second wait for it, and 103 Early Hints to an
    /// upload without <c>Expect</c>, both while the caller pauses between the two halves of its body. The
    /// backend still receives the whole upload, once and byte for byte, and its 200 reaches the caller,
    /// logged <c>forwarded</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(InterimResponses))]
    public async Task InterimResponseDuringUploadDoesNotEndIt(string interim)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        bool callerExpects = interim == LateContinue;
        await using EarlyAnswerBackend backend = EarlyAnswerBackend.Start(interim);
        await using FacadeFactory facade = await StartFacadeAsync(backend.BaseUrl, logs, InterimDeadlineSeconds, ct);

        PreparedRequest write = OperationRequests.ForRest(OperationCatalogue.Default.GetById(PostCreateAgentId));
        byte[] body = new byte[InterimUploadLength];
        Array.Fill(body, (byte)'x');
        byte[] head = Encoding.Latin1.GetBytes(
            "POST " + write.PathAndQuery + " HTTP/1.1\r\nHost: facade\r\n"
            + "Content-Type: " + write.ContentType + "\r\n"
            + (callerExpects ? ExpectContinueHeader : string.Empty)
            + "Content-Length: " + InterimUploadLength.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        int half = InterimUploadLength / 2;

        UploadExchange exchange = await SendUploadAsync(facade.RestPort, [.. head, .. body.AsSpan(0, half)], body[half..], ct);

        Assert.Equal(200, exchange.Response.Status);
        Assert.Equal(BackendBody, Encoding.Latin1.GetString(exchange.Response.Body));
        IReadOnlyList<ScriptedRequest> received = backend.Requests;
        Assert.True(received.Count == 1, "upload answered with an interim response did not reach the backend exactly once");
        Assert.True(received[0].Body.AsSpan().SequenceEqual(body), "backend did not receive the whole upload byte for byte after its interim response");
        Assert.Equal(callerExpects ? 1 : 0, received[0].HeaderCount(ExpectHeaderName));

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "upload answered with an interim response did not write exactly one backend call line");
        Assert.Equal(ForwardedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(200, calls[0].Value("Status"));
    }

    // The backend's early answer as the caller must receive it: status 413, the backend's X-Early and
    // Content-Type lines and its body unchanged, well inside the paused upload's deadline.
    private static void AssertEarlyAnswer(UploadExchange exchange)
    {
        RawExchange response = exchange.Response;
        Assert.Equal(413, response.Status);
        Assert.True(response.Head.Contains(EarlyAnswerHeaderLine, StringComparison.Ordinal), "backend's X-Early header did not reach the caller unchanged");
        Assert.True(
            response.Head.Contains(EarlyAnswerContentTypeLine, StringComparison.Ordinal),
            "backend's Content-Type did not reach the caller unchanged");
        Assert.Equal(EarlyAnswerBody, Encoding.Latin1.GetString(response.Body));
        Assert.True(exchange.Elapsed <= PausedAnswerBound, "backend's answer to a paused upload did not reach the caller well inside the deadline");
    }

    // The raw target of a catalogue REST operation at its normal address, with the catalogue's verb.
    private static string CatalogueTarget(string operationId) =>
        OperationRequests.ForRest(OperationCatalogue.Default.GetById(operationId)).PathAndQuery;

    private static Task<FacadeFactory> StartFacadeAsync(RawBackend backend, CancellationToken ct) =>
        FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl },
            ct);

    // Routes both surfaces to one backend, captures the facade's log and sets the target deadline; null
    // keeps the facade's default.
    private static Task<FacadeFactory> StartFacadeAsync(string baseUrl, LogCapture logs, int? timeoutSeconds, CancellationToken ct) =>
        FacadeFactory.StartAsync(
            new FacadeTestSettings
            {
                BackendRestBaseUrl = baseUrl,
                BackendSoapBaseUrl = baseUrl,
                BackendTimeoutSeconds = timeoutSeconds,
                LoggerProvider = logs,
            },
            ct);

    // A backend's 200 with the JSON body BackendBody, framed by Content-Length.
    private static byte[] OkReply() => Encoding.Latin1.GetBytes(
        "HTTP/1.1 200 OK\r\n"
        + "Content-Type: " + JsonContentType + "\r\n"
        + "Content-Length: " + BackendBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "\r\n"
        + BackendBody);

    // The bytes a backend that breaks its reply writes once it has read the request; null for none.
    private static byte[]? BrokenReply(string breakage) => breakage switch
    {
        GarbageReply => "THIS IS NOT HTTP\r\n\r\n"u8.ToArray(),
        CloseUnanswered or ResetUnanswered => null,
        BadStatusLine => "HTTP/1.1 abc OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(),
        BadHeaderLine => "HTTP/1.1 200 OK\r\nThis Is Not A Header\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(),
        ConflictingContentLength => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 5\r\n\r\nok"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(breakage)),
    };

    // FillerLineCount lines "X-Fill-NN: vvv...", each FillerValueLength value bytes, CRLF-terminated.
    private static string FillerHeaderLines()
    {
        var lines = new StringBuilder();
        for (int line = 0; line < FillerLineCount; line++)
        {
            lines.Append("X-Fill-")
                .Append(line.ToString("D2", CultureInfo.InvariantCulture))
                .Append(": ")
                .Append('v', FillerValueLength)
                .Append("\r\n");
        }

        return lines.ToString();
    }

    // The field lines of a request head, without their CRLF: every line after the request line up to the
    // blank line that ends the header section.
    private static string[] FieldLines(string head)
    {
        int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(end >= 0, "request head has no complete header section");
        return head[..end].Split("\r\n")[1..];
    }

    // An HTTP/1.1 request head that frames no body unless extraHeaders carries a Content-Length or
    // Transfer-Encoding, followed by the already framed body bytes. Connection: close is hop-by-hop, so it
    // never reaches the backend; it makes the facade close the connection after answering, which ends the
    // caller's read. The Host value is not forwarded either.
    private static byte[] CallerHead(string method, string target, string extraHeaders, string framedBody = "") =>
        Encoding.Latin1.GetBytes(
            method + " " + target + " HTTP/1.1\r\nHost: facade\r\n" + extraHeaders + "Connection: close\r\n\r\n"
            + framedBody);

    // Sends the raw request to the facade's REST socket and reads until the facade closes the connection.
    private static async Task<RawExchange> SendRawAsync(int port, byte[] request, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RawCallerTimeout);
        byte[] received;
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(request, limit.Token);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, limit.Token);
            received = buffer.ToArray();
        }
        catch (Exception exception) when (
            exception is SocketException or IOException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Assert.Fail("raw facade request failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        return RawExchange.Parse(received);
    }

    // Sends an upload's head and first part to a facade socket and reads the final response while the
    // connection stays open, without waiting for the facade to close it. With no rest the upload is
    // paused for good; otherwise the rest is sent after InterimUploadPause. The elapsed time runs from the
    // end of the first part to the end of the final response's body.
    private static async Task<UploadExchange> SendUploadAsync(int port, byte[] first, byte[]? rest, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RawCallerTimeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(first, limit.Token);
            var stopwatch = Stopwatch.StartNew();

            Task sendRest = rest is null ? Task.CompletedTask : SendAfterPauseAsync(stream, rest, limit.Token);
            RawExchange response = await ReadFinalResponseAsync(stream, limit.Token);
            stopwatch.Stop();
            await sendRest;
            return new UploadExchange(response, stopwatch.Elapsed);
        }
        catch (Exception exception) when (
            exception is SocketException or IOException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Assert.Fail("raw facade upload failed at the transport level (" + exception.GetType().Name + ")");
            throw new UnreachableException();
        }

        static async Task SendAfterPauseAsync(NetworkStream stream, byte[] rest, CancellationToken ct)
        {
            await Task.Delay(InterimUploadPause, ct);
            await stream.WriteAsync(rest, ct);
        }
    }

    // Reads until a final response and the whole body its Content-Length declares have arrived, past any
    // interim response, such as the 100 Continue the facade's server writes to a caller's own Expect.
    private static async Task<RawExchange> ReadFinalResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        using var received = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            if (TryTakeFinalResponse(received.GetBuffer().AsSpan(0, (int)received.Length)) is { } response)
            {
                return response;
            }

            int read = await stream.ReadAsync(buffer, ct);
            Assert.True(read > 0, "facade closed the connection before a complete final response");
            received.Write(buffer, 0, read);
        }
    }

    // The final response in the bytes received so far, or null while its head or body is incomplete.
    private static RawExchange? TryTakeFinalResponse(ReadOnlySpan<byte> received)
    {
        int start = 0;
        while (true)
        {
            int end = received[start..].IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                return null;
            }

            string head = Encoding.Latin1.GetString(received.Slice(start, end + 4));
            string[] statusLine = head[..head.IndexOf('\r', StringComparison.Ordinal)].Split(' ');
            int status = -1;
            Assert.True(
                statusLine.Length >= 2 && int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out status),
                "facade response has no status code");
            start += end + 4;
            if (status < 200)
            {
                continue;
            }

            int length = -1;
            foreach (string line in head.Split("\r\n").Skip(1))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(
                        int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length),
                        "facade's final response has a malformed Content-Length");
                }
            }

            Assert.True(length >= 0, "facade's final response has no Content-Length");
            return received.Length - start < length ? null : new RawExchange(status, head, received.Slice(start, length).ToArray());
        }
    }

    /// <summary>The final response to an upload as the raw caller received it, and how long it took.</summary>
    /// <param name="Response">The final response.</param>
    /// <param name="Elapsed">The time from the end of the upload's first part to the end of the response.</param>
    private sealed record UploadExchange(RawExchange Response, TimeSpan Elapsed)
    {
        /// <summary>The response in the form <see cref="UnavailableAnswers"/> asserts on.</summary>
        public RawResponse ToRawResponse()
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in Response.Head.Split("\r\n").Skip(1))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    string name = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    headers[name] = headers.TryGetValue(name, out string[]? values) ? [.. values, value] : [value];
                }
            }

            string? mediaType = null;
            string? charSet = null;
            if (headers.TryGetValue("Content-Type", out string[]? contentType)
                && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType[0], out System.Net.Http.Headers.MediaTypeHeaderValue? parsed))
            {
                mediaType = parsed.MediaType;
                charSet = parsed.CharSet;
            }

            return new RawResponse(Response.Status, mediaType, charSet, headers, Response.Body, Elapsed);
        }
    }

    /// <summary>
    /// A paused upload as a raw caller sends it: the request head, without <c>Connection: close</c>, and the
    /// one part of the body sent before the caller only reads. The part begins with the operation's sample
    /// body, so a SOAP call without <c>SOAPAction</c> still resolves its operation from it, and is filled
    /// with <c>x</c> to 128 KiB; a chunked upload sends it as one chunk and no last chunk.
    /// </summary>
    /// <param name="OperationId">The catalogue id of the operation called.</param>
    /// <param name="JadeMethod">The operation's JADE method, as the facade logs it.</param>
    /// <param name="Surface">The listener the upload is sent to.</param>
    /// <param name="HeadAndPart">The request head followed by the framed part.</param>
    /// <param name="PartLength">The length of the framed part the caller sends after the head.</param>
    private sealed record PausedUpload(string OperationId, string JadeMethod, Surface Surface, byte[] HeadAndPart, int PartLength)
    {
        /// <summary>Composes the paused upload of the given caller.</summary>
        /// <param name="caller"><c>rest</c>, <c>soap-action</c> or <c>soap-inspected</c>.</param>
        /// <param name="chunked">True for a chunked upload; otherwise 4 MiB are declared.</param>
        /// <param name="expectContinue">True to send the caller's own <c>Expect: 100-continue</c>.</param>
        /// <returns>The upload.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="caller"/> is none of the three.</exception>
        public static PausedUpload For(string caller, bool chunked, bool expectContinue)
        {
            string operationId = caller switch
            {
                RestCaller => PostCreateAgentId,
                SoapActionCaller or SoapInspectedCaller => GetAgentNamesId,
                _ => throw new ArgumentOutOfRangeException(nameof(caller)),
            };
            OperationDescriptor operation = OperationCatalogue.Default.GetById(operationId);
            PreparedRequest request = caller == RestCaller
                ? OperationRequests.ForRest(operation)
                : OperationRequests.ForSoap(operation, includeSoapAction: caller == SoapActionCaller);

            byte[] part = new byte[PausedPartLength];
            Array.Fill(part, (byte)'x');
            request.Body!.CopyTo(part, 0);

            var head = new StringBuilder();
            head.Append(request.Method.Method).Append(' ').Append(request.PathAndQuery).Append(" HTTP/1.1\r\nHost: facade\r\n");
            head.Append("Content-Type: ").Append(request.ContentType).Append("\r\n");
            if (request.SoapAction is not null)
            {
                head.Append("SOAPAction: ").Append(request.SoapAction).Append("\r\n");
            }

            if (expectContinue)
            {
                head.Append(ExpectContinueHeader);
            }

            head.Append(chunked
                ? "Transfer-Encoding: chunked\r\n"
                : "Content-Length: " + PausedDeclaredLength.ToString(CultureInfo.InvariantCulture) + "\r\n");
            head.Append("\r\n");

            byte[] framedPart = chunked
                ? [.. Encoding.ASCII.GetBytes(PausedPartLength.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), .. part, .. "\r\n"u8]
                : part;
            return new PausedUpload(
                operationId,
                operation.JadeMethod,
                operation.Surface,
                [.. Encoding.Latin1.GetBytes(head.ToString()), .. framedPart],
                framedPart.Length);
        }

        /// <summary>The facade port of the upload's surface.</summary>
        public int Port(FacadeFactory facade) => Surface == Surface.Rest ? facade.RestPort : facade.SoapPort;
    }

    /// <summary>The final response as the raw caller received it.</summary>
    /// <param name="Status">The status code from the status line.</param>
    /// <param name="Head">The status line and header section, terminator included, one char per byte.</param>
    /// <param name="Body">The bytes after the header section.</param>
    private sealed record RawExchange(int Status, string Head, byte[] Body)
    {
        // Skips interim 1xx responses, such as the 100 Continue the facade's server writes once the body
        // of a request sent with Expect: 100-continue is first read.
        public static RawExchange Parse(byte[] received)
        {
            int start = 0;
            while (true)
            {
                int end = received.AsSpan(start).IndexOf("\r\n\r\n"u8);
                Assert.True(end >= 0, "facade response has no complete header section");

                string head = Encoding.Latin1.GetString(received, start, end + 4);
                string[] statusLine = head[..head.IndexOf('\r', StringComparison.Ordinal)].Split(' ');
                Assert.True(
                    statusLine.Length >= 2 && int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out _),
                    "facade response has no status code");

                int status = int.Parse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture);
                start += end + 4;
                if (status >= 200)
                {
                    return new RawExchange(status, head, received[start..]);
                }
            }
        }
    }

    /// <summary>
    /// A backend on a raw loopback socket that records the bytes of every request head it receives and
    /// then either closes the connection without answering or writes a fixed reply and closes, or, after
    /// the reply, keeps the connection open for one more request head and closes without answering it.
    /// </summary>
    private sealed class RawBackend : IAsyncDisposable
    {
        private const int MaxHeadBytes = 64 * 1024;

        private static readonly TimeSpan ConnectionLimit = TimeSpan.FromSeconds(15);

        private readonly TcpListener _listener;
        private readonly byte[]? _reply;
        private readonly bool _closeNextUnanswered;
        private readonly ConcurrentQueue<byte[]> _heads = new();
        private readonly ConcurrentQueue<Task> _connections = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _disposed;

        private RawBackend(byte[]? reply, bool closeNextUnanswered)
        {
            _reply = reply;
            _closeNextUnanswered = closeNextUnanswered;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Authority = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            _acceptLoop = AcceptLoopAsync();
        }

        /// <summary>The <c>host:port</c> the backend listens on, as the facade's <c>Host</c> header names it.</summary>
        public string Authority { get; }

        /// <summary>The base URL to route a target to.</summary>
        public string BaseUrl => "http://" + Authority;

        /// <summary>The request heads received so far, in arrival order, terminator included.</summary>
        public IReadOnlyList<byte[]> Heads => [.. _heads];

        /// <summary>Starts listening on a free loopback port.</summary>
        /// <param name="reply">
        /// The bytes written after the first request head on each connection; null to close without answering.
        /// </param>
        /// <param name="closeNextUnanswered">
        /// True to keep each connection open after <paramref name="reply"/>, record the next request head on
        /// it and close without answering; <paramref name="reply"/> must then keep the connection alive.
        /// </param>
        /// <returns>The listening backend; the caller disposes it.</returns>
        public static RawBackend Start(byte[]? reply, bool closeNextUnanswered = false) => new(reply, closeNextUnanswered);

        /// <summary>Returns a head as one char per byte, with this backend's authority replaced by a fixed token.</summary>
        /// <param name="head">A head from <see cref="Heads"/>.</param>
        /// <returns>The run-invariant head text.</returns>
        public string Normalise(byte[] head) =>
            Encoding.Latin1.GetString(head).Replace(Authority, BackendAuthorityToken, StringComparison.Ordinal);

        /// <summary>Stops listening and waits for every connection to end. Safe to call more than once; never throws.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _connections.Enqueue(ServeAsync(socket));
            }
        }

        // The head is recorded before the connection closes, so by the time the facade sees the close, and
        // so before it answers its caller, the head is counted.
        private async Task ServeAsync(Socket socket)
        {
            using (socket)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(ConnectionLimit);
                try
                {
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    byte[] head = await ReadHeadAsync(stream, limit.Token);
                    if (head.Length > 0)
                    {
                        _heads.Enqueue(head);
                        if (_reply is not null)
                        {
                            await stream.WriteAsync(_reply, limit.Token);
                            if (_closeNextUnanswered)
                            {
                                byte[] next = await ReadHeadAsync(stream, limit.Token);
                                if (next.Length > 0)
                                {
                                    _heads.Enqueue(next);
                                }
                            }
                        }
                    }

                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
                {
                    // The facade or the test ended the connection first; the head, if any, is already recorded.
                }
            }
        }

        // Reads up to and including the blank line that ends the header section, or whatever arrived before
        // the peer closed or the bound was reached.
        private static async Task<byte[]> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var head = new List<byte>();
            byte[] buffer = new byte[4096];
            while (head.Count < MaxHeadBytes)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                head.AddRange(buffer.AsSpan(0, read));
                int end = CollectionsMarshal.AsSpan(head).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    return [.. head.GetRange(0, end + 4)];
                }
            }

            return [.. head];
        }
    }

    /// <summary>One request as a <see cref="ScriptedBackend"/> received it.</summary>
    /// <param name="Head">The request line and header section, terminator included, one char per byte.</param>
    /// <param name="Body">The body bytes, with any chunked framing removed; empty when none was read.</param>
    private sealed record ScriptedRequest(string Head, byte[] Body)
    {
        /// <summary>The number of header lines with the given name, matched case-insensitively.</summary>
        public int HeaderCount(string name) =>
            Head.Split("\r\n").Skip(1).Count(line =>
                line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0
                && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The first value of the named header, trimmed, or null.</summary>
        public string? HeaderValue(string name)
        {
            foreach (string line in Head.Split("\r\n").Skip(1))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A backend on a raw loopback socket that reads each request in full, its head and the body its
    /// <c>Content-Length</c> or chunked framing declares, records it, and then writes a fixed reply, or
    /// nothing, and closes the connection, or resets it. It never sends 100 Continue. Given an expectation
    /// reply, it answers a request carrying <c>Expect</c> with that reply at once, before reading any of
    /// its body. Connections are served concurrently, one request each.
    /// </summary>
    private sealed class ScriptedBackend : IAsyncDisposable
    {
        private const int MaxHeadBytes = 64 * 1024;

        private static readonly byte[] LineEnd = "\r\n"u8.ToArray();
        private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();
        private static readonly TimeSpan ConnectionLimit = TimeSpan.FromSeconds(15);

        private readonly TcpListener _listener;
        private readonly byte[]? _reply;
        private readonly bool _reset;
        private readonly byte[]? _expectationReply;
        private readonly ConcurrentQueue<ScriptedRequest> _requests = new();
        private readonly ConcurrentQueue<Task> _connections = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _disposed;

        private ScriptedBackend(byte[]? reply, bool reset, byte[]? expectationReply)
        {
            _reply = reply;
            _reset = reset;
            _expectationReply = expectationReply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            _acceptLoop = AcceptLoopAsync();
        }

        /// <summary>The base URL to route a target to.</summary>
        public string BaseUrl { get; }

        /// <summary>The requests received so far, in arrival order.</summary>
        public IReadOnlyList<ScriptedRequest> Requests => [.. _requests];

        /// <summary>Starts listening on a free loopback port.</summary>
        /// <param name="reply">The bytes written once a request has been read; null to write nothing.</param>
        /// <param name="reset">True to reset the connection after the reply instead of closing it.</param>
        /// <param name="expectationReply">
        /// The bytes written, before any body is read, to a request carrying <c>Expect</c>; null to treat
        /// such a request like any other.
        /// </param>
        /// <returns>The listening backend; the caller disposes it.</returns>
        public static ScriptedBackend Start(byte[]? reply, bool reset = false, byte[]? expectationReply = null) =>
            new(reply, reset, expectationReply);

        /// <summary>Stops listening and waits for every connection to end. Safe to call more than once; never throws.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _connections.Enqueue(ServeAsync(socket));
            }
        }

        // The request is recorded before anything is written or the connection ends, so by the time the
        // facade sees the backend's reply or close, and so before it answers its caller, it is counted.
        private async Task ServeAsync(Socket socket)
        {
            using (socket)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(ConnectionLimit);
                try
                {
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    var reader = new ConnectionReader(stream);
                    byte[]? headBytes = await reader.ReadThroughAsync(HeadEnd, MaxHeadBytes, limit.Token);
                    if (headBytes is null)
                    {
                        return;
                    }

                    string head = Encoding.Latin1.GetString(headBytes);
                    var received = new ScriptedRequest(head, []);
                    if (_expectationReply is not null && received.HeaderValue(ExpectHeaderName) is not null)
                    {
                        _requests.Enqueue(received);
                        await stream.WriteAsync(_expectationReply, limit.Token);
                        socket.Shutdown(SocketShutdown.Both);
                        return;
                    }

                    byte[]? body = await ReadBodyAsync(reader, received, limit.Token);
                    if (body is null)
                    {
                        // The facade ended the connection before the declared body arrived.
                        return;
                    }

                    _requests.Enqueue(received with { Body = body });
                    if (_reply is not null)
                    {
                        await stream.WriteAsync(_reply, limit.Token);
                    }

                    if (_reset)
                    {
                        // A zero linger time makes the close a reset.
                        socket.LingerState = new LingerOption(true, 0);
                    }
                    else
                    {
                        socket.Shutdown(SocketShutdown.Both);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
                {
                    // The facade or the test ended the connection first; a complete request is already recorded.
                }
            }
        }

        // Reads the body the head declares: chunked when Transfer-Encoding names chunked, otherwise the
        // Content-Length bytes, or none. Returns null when the connection ends first.
        private static async Task<byte[]?> ReadBodyAsync(ConnectionReader reader, ScriptedRequest request, CancellationToken ct)
        {
            if (request.HeaderValue("Transfer-Encoding") is { } coding && coding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var body = new MemoryStream();
                while (true)
                {
                    byte[]? sizeLine = await reader.ReadThroughAsync(LineEnd, MaxHeadBytes, ct);
                    if (sizeLine is null)
                    {
                        return null;
                    }

                    string size = Encoding.ASCII.GetString(sizeLine).Trim().Split(';')[0];
                    long length = long.Parse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    if (length == 0)
                    {
                        // The trailer section, possibly empty, ends with a blank line.
                        while (await reader.ReadThroughAsync(LineEnd, MaxHeadBytes, ct) is { } trailer)
                        {
                            if (trailer.Length == LineEnd.Length)
                            {
                                return body.ToArray();
                            }
                        }

                        return null;
                    }

                    if (!await reader.CopyExactlyAsync(body, length, ct) || await reader.ReadThroughAsync(LineEnd, MaxHeadBytes, ct) is null)
                    {
                        return null;
                    }
                }
            }

            long declared = request.HeaderValue("Content-Length") is { } value
                ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
                : 0;
            using var exact = new MemoryStream();
            return await reader.CopyExactlyAsync(exact, declared, ct) ? exact.ToArray() : null;
        }
    }

    /// <summary>
    /// A backend on a raw loopback socket that treats each request as its behaviour scripts, once it has
    /// read the head, without waiting for the body: it answers 413 at once and closes its side, answers
    /// and keeps the connection open, answers 100 Continue, reads 64 KiB of the body and answers 413, reads
    /// 32 KiB and closes or resets without answering, or sends a late 100 Continue or 103 Early Hints and
    /// then reads the whole body its <c>Content-Length</c> declares and answers 200. It records every head
    /// before it answers, and the body it read in full. After answering, it drains what still arrives, so
    /// that its close is a reset only when the behaviour is one. Given a first request to answer, it
    /// answers the first request on each connection with a 200 that keeps the connection alive and treats
    /// the next request as scripted. Connections are served concurrently.
    /// </summary>
    private sealed class EarlyAnswerBackend : IAsyncDisposable
    {
        private const int MaxHeadBytes = 64 * 1024;
        private const int ContinueReadLength = 64 * 1024;
        private const int DroppedReadLength = 32 * 1024;

        private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();
        private static readonly byte[] ContinueReply = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
        private static readonly byte[] EarlyHintsReply = "HTTP/1.1 103 Early Hints\r\nLink: </agent.css>; rel=preload\r\n\r\n"u8.ToArray();
        private static readonly byte[] AnswerClosing = Encoding.Latin1.GetBytes(EarlyAnswerHead + "Connection: close\r\n\r\n" + EarlyAnswerBody);
        private static readonly byte[] AnswerKeepingOpen = Encoding.Latin1.GetBytes(EarlyAnswerHead + "\r\n" + EarlyAnswerBody);
        private static readonly byte[] OkClosing = Encoding.Latin1.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: " + JsonContentType + "\r\n"
            + "Content-Length: " + BackendBody.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "Connection: close\r\n"
            + "\r\n"
            + BackendBody);

        private static readonly TimeSpan ConnectionLimit = TimeSpan.FromSeconds(15);

        // After the facade's one-second wait for 100 Continue, and before the caller's pause ends.
        private static readonly TimeSpan LateContinueDelay = TimeSpan.FromMilliseconds(1500);

        private readonly TcpListener _listener;
        private readonly string _behaviour;
        private readonly bool _answerFirstRequest;
        private readonly ConcurrentQueue<ScriptedRequest> _requests = new();
        private readonly ConcurrentQueue<Task> _connections = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _connectionCount;
        private long _bodyBytesReceived;
        private int _disposed;

        private EarlyAnswerBackend(string behaviour, bool answerFirstRequest)
        {
            _behaviour = behaviour;
            _answerFirstRequest = answerFirstRequest;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            _acceptLoop = AcceptLoopAsync();
        }

        /// <summary>The base URL to route a target to.</summary>
        public string BaseUrl { get; }

        /// <summary>The requests received so far, in arrival order; a body only when it was read in full.</summary>
        public IReadOnlyList<ScriptedRequest> Requests => [.. _requests];

        /// <summary>The number of connections accepted so far.</summary>
        public int Connections => Volatile.Read(ref _connectionCount);

        /// <summary>
        /// Every byte received after a request head, on every connection, whether read as a body or drained;
        /// final once the backend has been disposed, which waits for every connection to end.
        /// </summary>
        public long BodyBytesReceived => Interlocked.Read(ref _bodyBytesReceived);

        /// <summary>Starts listening on a free loopback port.</summary>
        /// <param name="behaviour">One of the scripted behaviours, for example <c>answer-then-close</c>.</param>
        /// <param name="answerFirstRequest">True to answer each connection's first request with a 200 that keeps it alive.</param>
        /// <returns>The listening backend; the caller disposes it.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="behaviour"/> is not a scripted behaviour.</exception>
        public static EarlyAnswerBackend Start(string behaviour, bool answerFirstRequest = false)
        {
            if (behaviour is not (AnswerThenClose or AnswerKeepOpen or ContinueThenAnswer or CloseUnanswered or ResetUnanswered or LateContinue or EarlyHints))
            {
                throw new ArgumentOutOfRangeException(nameof(behaviour));
            }

            return new EarlyAnswerBackend(behaviour, answerFirstRequest);
        }

        /// <summary>Stops listening and waits for every connection to end. Safe to call more than once; never throws.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _connectionCount);
                _connections.Enqueue(ServeAsync(socket));
            }
        }

        // Each head is recorded before anything is written in reply, so by the time the facade sees the
        // reply or the close, and so before it answers its caller, the request is counted.
        private async Task ServeAsync(Socket socket)
        {
            using (socket)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(ConnectionLimit);
                ConnectionReader? reader = null;
                long headBytes = 0;
                try
                {
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    reader = new ConnectionReader(stream);
                    byte[]? head = await reader.ReadThroughAsync(HeadEnd, MaxHeadBytes, limit.Token);
                    if (head is not null && _answerFirstRequest)
                    {
                        headBytes += head.Length;
                        _requests.Enqueue(new ScriptedRequest(Encoding.Latin1.GetString(head), []));
                        await stream.WriteAsync(OkReply(), limit.Token);
                        head = await reader.ReadThroughAsync(HeadEnd, MaxHeadBytes, limit.Token);
                    }

                    if (head is not null)
                    {
                        headBytes += head.Length;
                        await ServeScriptedAsync(socket, stream, reader, new ScriptedRequest(Encoding.Latin1.GetString(head), []), limit.Token);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
                {
                    // The facade or the test ended the connection first; every head read is already recorded.
                }
                finally
                {
                    // Whatever arrived after the heads, however the connection ended.
                    Interlocked.Add(ref _bodyBytesReceived, Math.Max((reader?.BytesRead ?? 0) - headBytes, 0));
                }
            }
        }

        private async Task ServeScriptedAsync(Socket socket, NetworkStream stream, ConnectionReader reader, ScriptedRequest request, CancellationToken ct)
        {
            switch (_behaviour)
            {
                case AnswerThenClose:
                    _requests.Enqueue(request);
                    await stream.WriteAsync(AnswerClosing, ct);
                    socket.Shutdown(SocketShutdown.Send);
                    await reader.DrainAsync(ct);
                    return;

                case AnswerKeepOpen:
                    // No FIN follows the answer: the connection stays open until the facade closes it.
                    _requests.Enqueue(request);
                    await stream.WriteAsync(AnswerKeepingOpen, ct);
                    await reader.DrainAsync(ct);
                    return;

                case ContinueThenAnswer:
                    _requests.Enqueue(request);
                    await stream.WriteAsync(ContinueReply, ct);
                    if (await reader.CopyExactlyAsync(Stream.Null, ContinueReadLength, ct))
                    {
                        await stream.WriteAsync(AnswerClosing, ct);
                        socket.Shutdown(SocketShutdown.Send);
                        await reader.DrainAsync(ct);
                    }

                    return;

                case CloseUnanswered:
                    _requests.Enqueue(request);
                    if (await reader.CopyExactlyAsync(Stream.Null, DroppedReadLength, ct))
                    {
                        socket.Shutdown(SocketShutdown.Send);
                        await reader.DrainAsync(ct);
                    }

                    return;

                case ResetUnanswered:
                    _requests.Enqueue(request);
                    if (await reader.CopyExactlyAsync(Stream.Null, DroppedReadLength, ct))
                    {
                        // A zero linger time makes the close a reset.
                        socket.LingerState = new LingerOption(true, 0);
                    }

                    return;

                default:
                    // LateContinue or EarlyHints: an interim response, then the whole declared body and a 200.
                    if (_behaviour == LateContinue)
                    {
                        await Task.Delay(LateContinueDelay, ct);
                        await stream.WriteAsync(ContinueReply, ct);
                    }
                    else
                    {
                        await stream.WriteAsync(EarlyHintsReply, ct);
                    }

                    long declared = request.HeaderValue("Content-Length") is { } value
                        ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
                        : 0;
                    using (var body = new MemoryStream())
                    {
                        if (!await reader.CopyExactlyAsync(body, declared, ct))
                        {
                            return;
                        }

                        _requests.Enqueue(request with { Body = body.ToArray() });
                    }

                    await stream.WriteAsync(OkClosing, ct);
                    socket.Shutdown(SocketShutdown.Send);
                    await reader.DrainAsync(ct);
                    return;
            }
        }
    }

    /// <summary>Reads one connection's bytes in order, keeping what arrived past the part asked for.</summary>
    /// <param name="stream">The connection, read but not owned.</param>
    private sealed class ConnectionReader(NetworkStream stream)
    {
        private readonly byte[] _chunk = new byte[16 * 1024];
        private byte[] _pending = [];

        /// <summary>The number of bytes read from the connection so far, whether taken or still pending.</summary>
        public long BytesRead { get; private set; }

        /// <summary>Reads and discards everything that arrives until the connection ends.</summary>
        public async Task DrainAsync(CancellationToken ct)
        {
            _pending = [];
            while (await FillAsync(ct))
            {
                _pending = [];
            }
        }

        /// <summary>Reads up to and including <paramref name="delimiter"/>.</summary>
        /// <returns>The bytes read, delimiter included; null when the connection ended or the bound was reached first.</returns>
        public async Task<byte[]?> ReadThroughAsync(byte[] delimiter, int bound, CancellationToken ct)
        {
            while (true)
            {
                int end = _pending.AsSpan().IndexOf(delimiter);
                if (end >= 0)
                {
                    int length = end + delimiter.Length;
                    byte[] taken = _pending[..length];
                    _pending = _pending[length..];
                    return taken;
                }

                if (_pending.Length >= bound || !await FillAsync(ct))
                {
                    return null;
                }
            }
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes to <paramref name="destination"/>.</summary>
        /// <returns>False when the connection ended first.</returns>
        public async Task<bool> CopyExactlyAsync(Stream destination, long count, CancellationToken ct)
        {
            long remaining = count;
            while (remaining > 0)
            {
                if (_pending.Length == 0 && !await FillAsync(ct))
                {
                    return false;
                }

                int take = (int)Math.Min(remaining, _pending.Length);
                destination.Write(_pending, 0, take);
                _pending = _pending[take..];
                remaining -= take;
            }

            return true;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            int read = await stream.ReadAsync(_chunk, ct);
            if (read == 0)
            {
                return false;
            }

            BytesRead += read;
            byte[] next = new byte[_pending.Length + read];
            _pending.CopyTo(next, 0);
            _chunk.AsSpan(0, read).CopyTo(next.AsSpan(_pending.Length));
            _pending = next;
            return true;
        }
    }
}

// The facade's unavailable answer (class UnavailableAnswers) holds assertions only and sits between the
// two test classes whose real-socket cases end in it, ForwardingTransportTests and BackendForwarderTests.
// Its expected values are the plan's literals, authored here, not taken from the facade's constants.
/// <summary>
/// Asserts the facade's unavailable answer exactly as the plan states it: REST 502 with the fixed
/// problem+json body, SOAP HTTP 500 with the SOAP 1.1 "Backend unavailable" Fault.
/// </summary>
internal static class UnavailableAnswers
{
    private const string ProblemMediaType = "application/problem+json";
    private const string SoapMediaType = "text/xml";
    private const string SoapCharSet = "utf-8";
    private const string UnavailableText = "Backend unavailable";
    private const string ServerFaultCode = "Server";

    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>Asserts the unavailable answer of the operation's surface.</summary>
    /// <param name="response">The caller's response.</param>
    /// <param name="operationId">The catalogue id of the operation called, for example <c>Rest.getCompany</c>.</param>
    /// <returns>The status asserted: 502 on REST, 500 on SOAP.</returns>
    public static int AssertMatches(RawResponse response, string operationId)
    {
        if (OperationCatalogue.Default.GetById(operationId).Surface == Surface.Rest)
        {
            AssertProblem(response, operationId);
            return 502;
        }

        AssertFault(response);
        return 500;
    }

    // Status 502, one Content-Type value that is exactly application/problem+json, and the body bytes of
    // the fixed object naming the operation's JADE method, the part of the operation id after its surface.
    private static void AssertProblem(RawResponse response, string operationId)
    {
        Assert.Equal(502, response.Status);
        Assert.True(
            response.Headers.TryGetValue("Content-Type", out string[]? contentType)
            && contentType.Length == 1
            && string.Equals(ProblemMediaType, contentType[0], StringComparison.Ordinal),
            "unavailable answer does not carry exactly the Content-Type application/problem+json");

        string jadeMethod = operationId[(operationId.IndexOf('.', StringComparison.Ordinal) + 1)..];
        string expected = "{\"type\":\"about:blank\",\"title\":\"" + UnavailableText + "\",\"status\":502,"
            + "\"detail\":\"The backend for operation " + jadeMethod + " could not be reached.\"}";
        Assert.True(
            response.Body.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            "unavailable answer body is not the fixed problem+json object for the operation");
    }

    // HTTP 500 with text/xml; charset=utf-8, and an Envelope in the SOAP 1.1 envelope namespace whose only
    // child is a Body, whose only child is a Fault holding one faultcode that resolves to the envelope
    // namespace's Server and one faultstring "Backend unavailable", as a SOAP 1.1 receiver reads them.
    private static void AssertFault(RawResponse response)
    {
        Assert.Equal(500, response.Status);
        Assert.Equal(SoapMediaType, response.MediaType);
        Assert.Equal(SoapCharSet, response.CharSet);

        XElement envelope = XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root
            ?? throw new InvalidOperationException("fault document has no root element");
        Assert.True(envelope.Name == SoapEnvelope + "Envelope", "fault root is not a SOAP 1.1 Envelope");
        XElement[] bodies = [.. envelope.Elements()];
        Assert.True(bodies.Length == 1 && bodies[0].Name == SoapEnvelope + "Body", "fault Envelope does not hold exactly one SOAP 1.1 Body");
        XElement[] faults = [.. bodies[0].Elements()];
        Assert.True(faults.Length == 1 && faults[0].Name == SoapEnvelope + "Fault", "fault Body does not hold exactly one SOAP 1.1 Fault");

        XElement[] codes = [.. faults[0].Elements("faultcode")];
        Assert.True(codes.Length == 1, "Fault does not hold exactly one unqualified faultcode");
        string[] qualifiedName = codes[0].Value.Trim().Split(':');
        Assert.True(
            qualifiedName.Length == 2
            && codes[0].GetNamespaceOfPrefix(qualifiedName[0]) == SoapEnvelope
            && string.Equals(ServerFaultCode, qualifiedName[1], StringComparison.Ordinal),
            "faultcode does not resolve to the SOAP 1.1 envelope namespace's Server");

        XElement[] strings = [.. faults[0].Elements("faultstring")];
        Assert.True(strings.Length == 1, "Fault does not hold exactly one unqualified faultstring");
        Assert.Equal(UnavailableText, strings[0].Value);
    }
}

// BackendForwarder's handling of every other way a call ends (class BackendForwarderTests): the one
// per-call line (event 1001, BackendCall) with the outcome, level and status of each failure kind,
// exceptions, the bounded failure-response write, caller aborts and the backend's own answers to uploads.
// The classification and write cases construct the forwarder directly over a fake IHttpForwarder that
// reports each YARP error as YARP does; the caller and upload cases run the facade on real loopback
// Kestrel listeners.
/// <summary>
/// The per-call line, the caller's response and the connection for every way a forwarded call can end
/// besides an unreachable backend or an expired deadline.
/// </summary>
public sealed class BackendForwarderTests
{
    private const string RestOperationId = "Rest.getCompany";
    private const string SoapOperationId = "Soap.getAgent";
    private const string UploadOperationId = "Rest.postCreateAgent";

    private const string BackendTarget = "backend";
    private const string UnresolvedTarget = "unresolved";

    private const string Forwarded = "forwarded";
    private const string Unavailable = "unavailable";
    private const string TimedOut = "timeout";
    private const string Aborted = "aborted";
    private const string Failed = "failed";

    private const string UnavailableTitle = "Backend unavailable";
    private const string TimedOutTitle = "Backend timed out";
    private const string JsonContentType = "application/json";
    private const string ExpectHeader = "Expect";
    private const string ContinueExpectation = "100-continue";

    // Exceptions as YARP reports them in IForwarderErrorFeature for each kind of failure.
    private const string NoCause = "none";
    private const string RefusedCause = "connection-refused";
    private const string ConnectTimeoutCause = "connect-timeout";
    private const string CallerCancelledCause = "caller-cancelled";
    private const string TruncatedUploadCause = "truncated-upload";
    private const string CallerResetCause = "caller-reset";
    private const string TempSpaceFullCause = "temp-space-full";
    private const string UploadReadTimeoutCause = "upload-read-timeout";
    private const string BackendResetCause = "backend-reset";
    private const string ConnectFailedCause = "connect-failed";
    private const string NameNotResolvedCause = "name-not-resolved";
    private const string SendFailedCause = "send-failed";
    private const string ResponseEndedCause = "response-ended";
    private const string InvalidStatusLineCause = "invalid-status-line";
    private const string InvalidResponseHeadCause = "invalid-response-head";
    private const string InvalidRequestCause = "invalid-request";

    // Where an exception escapes the forwarder.
    private const string ThrowsFromForwarder = "forwarder";
    private const string ThrowsAfterResponseStarted = "after-response-started";
    private const string ThrowsFromResolution = "resolution";

    // How a caller abandons its call on a real socket.
    private const string DisconnectBeforeResponse = "disconnect-before-response";
    private const string TruncatedUpload = "truncated-upload";

    // Unit-level targets are never connected to: the fake forwarder answers for them.
    private const string UnitRestBaseUrl = "http://backend.invalid:65000";
    private const string UnitSoapBaseUrl = "http://backend.invalid:21001";

    private const int CallerDeclaredBodyLength = 1000;
    private const int CallerSentBodyLength = 10;
    private const int SmallBodyLength = 1000;
    private const int LargeBodyLength = 1024 * 1024;

    // The early-rejecting backend's own body-size limit, and an upload well beyond it.
    private const long BackendBodyLimit = 64 * 1024;
    private const int OverLimitBodyLength = 2 * 1024 * 1024;

    // A chunked upload, which a backend can reject only once it has read up to its limit: 40 MiB against
    // Kestrel's default limit of 30,000,000 bytes, as the JADE stand-in has it.
    private const long DefaultBackendBodyLimit = 30_000_000;
    private const int ChunkedOverLimitBodyLength = 40 * 1024 * 1024;

    // The limit of a backend that rejects an upload the caller never completes, well before its end.
    private const long SmallBackendBodyLimit = 1024 * 1024;

    private static readonly XNamespace SoapEnvelope = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly TimeSpan SlowBackendDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallerPatience = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LogWaitBound = TimeSpan.FromSeconds(5);

    // A 1 s deadline plus the forwarder's 2 s failure-write budget, with margin; a hang is cut off at 10 s.
    private static readonly TimeSpan BoundedWriteLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every YARP error kind ends in exactly one line whose outcome, level and status say what happened,
    /// and the caller gets the facade response, nothing, or an aborted connection that outcome stands
    /// for. Caller aborts never report YARP's placeholder status; a backend that dropped the upload or
    /// sent a head the facade cannot relay, before its response was taken over, gets the unavailable
    /// answer, and one that failed after it is aborted.
    /// </summary>
    [Theory]
    [InlineData(ForwarderError.None, NoCause, false, true, Forwarded, LogLevel.Information, 200, false, null)]
    [InlineData(ForwarderError.Request, RefusedCause, false, false, Unavailable, LogLevel.Warning, 502, false, UnavailableTitle)]
    [InlineData(ForwarderError.RequestTimedOut, ConnectTimeoutCause, false, false, TimedOut, LogLevel.Warning, 504, false, TimedOutTitle)]
    [InlineData(ForwarderError.RequestCanceled, CallerCancelledCause, true, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.RequestBodyCanceled, TruncatedUploadCause, false, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.RequestBodyClient, CallerResetCause, false, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.ResponseBodyCanceled, CallerCancelledCause, true, true, Aborted, LogLevel.Information, 200, false, null)]
    [InlineData(ForwarderError.ResponseHeaders, CallerCancelledCause, true, false, Aborted, LogLevel.Information, 0, false, null)]
    [InlineData(ForwarderError.RequestBodyDestination, BackendResetCause, false, false, Unavailable, LogLevel.Warning, 502, false, UnavailableTitle)]
    [InlineData(ForwarderError.ResponseBodyDestination, BackendResetCause, true, true, Failed, LogLevel.Warning, 200, true, null)]
    [InlineData(ForwarderError.ResponseHeaders, InvalidResponseHeadCause, false, false, Unavailable, LogLevel.Warning, 502, false, UnavailableTitle)]
    public async Task CallEndIsLoggedOnceWithItsOutcomeLevelAndStatus(
        ForwarderError error,
        string cause,
        bool requestAborted,
        bool responseStarted,
        string expectedOutcome,
        LogLevel expectedLevel,
        int expectedStatus,
        bool expectedConnectionAbort,
        string? expectedTitle)
    {
        using var call = new UnitCall();
        Exception? exception = CauseOf(cause);
        using var harness = new ForwarderHarness(
            (context, _) =>
            {
                if (responseStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    call.Response.Started = true;
                }
                else if (error != ForwarderError.None)
                {
                    // YARP's own placeholder for a failure, never the backend's status.
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }

                if (error != ForwarderError.None)
                {
                    context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, exception));
                }

                // A caller that left, or YARP aborting the connection itself after a backend failure.
                if (requestAborted)
                {
                    call.Lifetime.CancelRequestAborted();
                }

                return ValueTask.FromResult(error);
            });

        await harness.Forwarder.ForwardAsync(call.Context, Surface.Rest, Operation(RestOperationId));

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "call did not write exactly one backend call line");
        Assert.Equal(expectedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(expectedLevel, calls[0].Level);
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(BackendTarget, calls[0].Value("Target"));
        Assert.Equal(expectedConnectionAbort, call.Lifetime.AbortCalled);

        if (expectedTitle is not null)
        {
            using JsonDocument problem = JsonDocument.Parse(call.Body.ToArray());
            Assert.True(problem.RootElement.TryGetProperty("title", out JsonElement title), "problem body has no title member");
            Assert.Equal(expectedTitle, title.GetString());
            return;
        }

        Assert.True(call.Body.Length == 0, "facade wrote a body for a call it does not answer with one");
    }

    /// <summary>
    /// A failure on the facade's side of a call, before any response, gets no response the facade makes
    /// up, on either surface. A client-side copy error the caller did not cause, such as a full temporary
    /// store for a buffered body, and an outgoing request YARP could not create each end in one
    /// <c>failed</c> line at Warning with status 0 and an aborted connection, with nothing written: no
    /// 500, no problem+json and no Fault. A client-side copy error that carries a timeout is that timeout,
    /// answered with REST 504 or the "Backend timed out" Fault and never aborted.
    /// </summary>
    [Theory]
    [InlineData("Rest", ForwarderError.RequestBodyClient, TempSpaceFullCause, Failed, 0, true, null)]
    [InlineData("Soap", ForwarderError.RequestBodyClient, TempSpaceFullCause, Failed, 0, true, null)]
    [InlineData("Rest", ForwarderError.RequestCreation, InvalidRequestCause, Failed, 0, true, null)]
    [InlineData("Soap", ForwarderError.RequestCreation, InvalidRequestCause, Failed, 0, true, null)]
    [InlineData("Rest", ForwarderError.RequestBodyClient, UploadReadTimeoutCause, TimedOut, 504, false, TimedOutTitle)]
    [InlineData("Soap", ForwarderError.RequestBodyClient, UploadReadTimeoutCause, TimedOut, 500, false, TimedOutTitle)]
    public async Task FacadeSideFailureIsAbortedUnlessItCarriesATimeout(
        string surface,
        ForwarderError error,
        string cause,
        string expectedOutcome,
        int expectedStatus,
        bool expectedConnectionAbort,
        string? expectedText)
    {
        using var call = new UnitCall();
        using var harness = new ForwarderHarness(
            (context, _) =>
            {
                // YARP's own placeholder for a failure, never the backend's status.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, CauseOf(cause)));
                return ValueTask.FromResult(error);
            });

        Surface parsed = Enum.Parse<Surface>(surface);
        OperationDescriptor operation = Operation(parsed == Surface.Rest ? RestOperationId : SoapOperationId);
        await harness.Forwarder.ForwardAsync(call.Context, parsed, operation);

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "facade-side failure did not write exactly one backend call line");
        Assert.Equal(expectedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(BackendTarget, calls[0].Value("Target"));
        Assert.Equal(expectedConnectionAbort, call.Lifetime.AbortCalled);

        HttpResponse response = call.Context.Response;
        if (expectedText is null)
        {
            // The abort discards the response, and the facade put nothing of its own into it.
            Assert.True(call.Body.Length == 0, "facade wrote a body for a call it aborts");
            Assert.True(response.StatusCode != StatusCodes.Status500InternalServerError, "facade set a 500 on a call it aborts");
            Assert.True(response.ContentLength is null, "facade set a Content-Length on a call it aborts");
            Assert.True(response.ContentType is null, "facade set a Content-Type on a call it aborts");
            Assert.True(response.Headers.Connection.Count == 0, "facade set a Connection header on a call it aborts");
            return;
        }

        Assert.Equal(expectedStatus, response.StatusCode);
        if (parsed == Surface.Rest)
        {
            using JsonDocument problem = JsonDocument.Parse(call.Body.ToArray());
            Assert.True(problem.RootElement.TryGetProperty("title", out JsonElement title), "problem body has no title member");
            Assert.Equal(expectedText, title.GetString());
            return;
        }

        XElement envelope = XDocument.Parse(Encoding.UTF8.GetString(call.Body.ToArray())).Root
            ?? throw new InvalidOperationException("fault document has no root element");
        XElement[] faultStrings = [.. envelope.Elements(SoapEnvelope + "Body").Elements(SoapEnvelope + "Fault").Elements("faultstring")];
        Assert.True(faultStrings.Length == 1, "Fault body does not hold exactly one faultstring");
        Assert.Equal(expectedText, faultStrings[0].Value);
    }

    /// <summary>
    /// An expired deadline is the outcome whatever error YARP reports for it, even a caller-side
    /// cancellation: before the response the caller gets the 504, and once the backend's response has
    /// started the connection is aborted and the backend's status is never replaced.
    /// </summary>
    [Theory]
    [InlineData(ForwarderError.RequestCanceled, false, 504, false)]
    [InlineData(ForwarderError.ResponseBodyCanceled, true, 200, true)]
    public async Task ExpiredDeadlineIsTimeoutAndNeverReheadsStartedResponse(
        ForwarderError error, bool responseStarted, int expectedStatus, bool expectedConnectionAbort)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var call = new UnitCall();
        using var harness = new ForwarderHarness(
            async (context, token) =>
            {
                if (responseStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    call.Response.Started = true;
                }

                // The backend stalls; the call's 1 s deadline cancels the token.
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    // YARP reports the cancelled copy with the error under test.
                }

                if (!responseStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }

                context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, CauseOf(CallerCancelledCause)));
                return error;
            },
            timeoutSeconds: 1);

        try
        {
            await harness.Forwarder.ForwardAsync(call.Context, Surface.Rest, Operation(RestOperationId)).WaitAsync(HangGuard, ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail("call past its deadline never ended");
        }

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "expired deadline did not write exactly one backend call line");
        Assert.Equal(TimedOut, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(expectedConnectionAbort, call.Lifetime.AbortCalled);

        if (responseStarted)
        {
            Assert.True(call.Body.Length == 0, "facade wrote into a response the backend had started");
            return;
        }

        using JsonDocument problem = JsonDocument.Parse(call.Body.ToArray());
        Assert.True(problem.RootElement.TryGetProperty("title", out JsonElement title), "problem body has no title member");
        Assert.Equal(TimedOutTitle, title.GetString());
    }

    /// <summary>
    /// A <see cref="ForwarderError.Request"/> failure is answered "Backend unavailable", logged
    /// <c>unavailable</c> with status 502 and never aborted, whatever its cause and however far the
    /// upload got: a failed connect or name lookup, and a backend that was reached and then broke the
    /// connection or answered with an invalid status line, before or after any body byte was sent to it,
    /// for a request with or without a body.
    /// </summary>
    [Theory]
    [InlineData(ConnectFailedCause, true, true)]
    [InlineData(NameNotResolvedCause, true, true)]
    [InlineData(RefusedCause, true, true)]
    [InlineData(SendFailedCause, true, true)]
    [InlineData(ResponseEndedCause, true, true)]
    [InlineData(InvalidStatusLineCause, true, true)]
    [InlineData(SendFailedCause, true, false)]
    [InlineData(ResponseEndedCause, true, false)]
    [InlineData(SendFailedCause, false, false)]
    [InlineData(ResponseEndedCause, false, false)]
    [InlineData(InvalidStatusLineCause, false, false)]
    public async Task RequestFailureIsUnavailableWhateverTheUploadReached(string cause, bool carriesBody, bool uploadStarted)
    {
        using var call = new UnitCall();
        if (carriesBody)
        {
            call.Context.Request.Method = HttpMethods.Post;
            call.Context.Request.Path = "/createAgent";
            call.Context.Request.ContentLength = CallerDeclaredBodyLength;
            call.Context.Request.Body = new MemoryStream(new byte[CallerDeclaredBodyLength]);
        }

        using var harness = new ForwarderHarness(
            async (context, ct) =>
            {
                // The HTTP stack starts sending a body by reading the caller's upload.
                if (uploadStarted)
                {
                    _ = await context.Request.Body.ReadAsync(new byte[CallerDeclaredBodyLength], ct);
                }

                // YARP's own placeholder for a failed request, never the backend's status.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(ForwarderError.Request, CauseOf(cause)));
                return ForwarderError.Request;
            });

        await harness.Forwarder.ForwardAsync(call.Context, Surface.Rest, Operation(carriesBody ? UploadOperationId : RestOperationId));

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "request failure did not write exactly one backend call line");
        Assert.Equal(Unavailable, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.Equal(502, calls[0].Value("Status"));
        Assert.False(call.Lifetime.AbortCalled, "request failure aborted the caller's connection instead of answering");

        using JsonDocument problem = JsonDocument.Parse(call.Body.ToArray());
        Assert.True(problem.RootElement.TryGetProperty("title", out JsonElement title), "problem body has no title member");
        Assert.Equal(UnavailableTitle, title.GetString());
    }

    /// <summary>
    /// YARP checks the upload only after it has relayed the backend's whole response, so a request-body
    /// error it reports then leaves the caller with the backend's complete answer: logged
    /// <c>forwarded</c> with the backend's status and never aborted, whoever broke the upload. When the
    /// deadline expires while YARP waits for the upload, the call is a timeout, and the backend's response,
    /// relayed but possibly not yet sent, is aborted rather than replaced by the 504.
    /// </summary>
    [Theory]
    [InlineData(ForwarderError.RequestBodyDestination, BackendResetCause, false, Forwarded, LogLevel.Information, false)]
    [InlineData(ForwarderError.RequestBodyClient, CallerResetCause, false, Forwarded, LogLevel.Information, false)]
    [InlineData(ForwarderError.RequestBodyCanceled, TruncatedUploadCause, false, Forwarded, LogLevel.Information, false)]
    [InlineData(ForwarderError.RequestBodyCanceled, CallerCancelledCause, true, TimedOut, LogLevel.Warning, true)]
    public async Task RequestBodyErrorAfterRelayedResponseKeepsBackendAnswer(
        ForwarderError error,
        string cause,
        bool deadlineExpires,
        string expectedOutcome,
        LogLevel expectedLevel,
        bool expectedConnectionAbort)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var call = new UnitCall();
        call.Context.Request.Method = HttpMethods.Post;
        call.Context.Request.Path = "/createAgent";
        call.Context.Request.ContentLength = CallerDeclaredBodyLength;
        using var harness = new ForwarderHarness(
            async (context, token) =>
            {
                // YARP copies the backend's 413 and its empty body, asks the transformer for the trailers,
                // and only then checks the upload, which may still be under way.
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                using var backendResponse = new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge);
                await new PassThroughTransformer().TransformResponseTrailersAsync(context, backendResponse, token);

                if (deadlineExpires)
                {
                    // The upload stalls; the call's 1 s deadline cancels the token.
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // YARP reports the cancelled upload with the error under test.
                    }
                }

                context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, CauseOf(cause)));
                return error;
            },
            timeoutSeconds: 1);

        try
        {
            await harness.Forwarder.ForwardAsync(call.Context, Surface.Rest, Operation(UploadOperationId)).WaitAsync(HangGuard, ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail("call with a relayed response never ended");
        }

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "relayed response did not write exactly one backend call line");
        Assert.Equal(expectedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(expectedLevel, calls[0].Level);
        Assert.Equal(413, calls[0].Value("Status"));
        Assert.Equal(expectedConnectionAbort, call.Lifetime.AbortCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, call.Context.Response.StatusCode);
        Assert.True(call.Body.Length == 0, "facade wrote into the backend's relayed response");
    }

    /// <summary>
    /// An exception from routing, YARP or the transformer propagates unchanged after exactly one
    /// <c>failed</c> line, which reports the status already sent or the 500 the server sends for it, and
    /// names the target <c>unresolved</c> when resolution itself failed.
    /// </summary>
    [Theory]
    [InlineData(ThrowsFromForwarder, 500, BackendTarget)]
    [InlineData(ThrowsAfterResponseStarted, 200, BackendTarget)]
    [InlineData(ThrowsFromResolution, 500, UnresolvedTarget)]
    public async Task EscapingExceptionIsRethrownAfterOneFailedLine(string where, int expectedStatus, string expectedTarget)
    {
        using var call = new UnitCall();
        var thrown = new InvalidOperationException("forwarder failure raised by the test");
        using var harness = new ForwarderHarness(
            (context, _) =>
            {
                if (where == ThrowsAfterResponseStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    call.Response.Started = true;
                }

                throw thrown;
            });

        // An operation of the other surface makes the router throw before any target is known.
        OperationDescriptor operation = Operation(where == ThrowsFromResolution ? SoapOperationId : RestOperationId);
        Exception caught = await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Forwarder.ForwardAsync(call.Context, Surface.Rest, operation));

        if (where == ThrowsFromResolution)
        {
            Assert.IsType<ArgumentException>(caught);
        }
        else
        {
            Assert.Same(thrown, caught);
        }

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "escaping exception did not write exactly one backend call line");
        Assert.Equal(Failed, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(expectedTarget, calls[0].Value("Target"));
    }

    /// <summary>
    /// A caller that stops reading cannot hold the failure response, or the call's line, past the
    /// write budget: the connection is aborted and the line keeps its outcome.
    /// </summary>
    [Theory]
    [InlineData("Rest", ForwarderError.Request, Unavailable)]
    [InlineData("Soap", ForwarderError.Request, Unavailable)]
    [InlineData("Rest", ForwarderError.RequestCanceled, TimedOut)]
    public async Task FailureResponseToCallerThatStopsReadingIsAbortedWithinBudget(string surface, ForwarderError error, string expectedOutcome)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var call = new UnitCall(new StalledWriteStream());
        using var harness = new ForwarderHarness(
            async (context, token) =>
            {
                if (error == ForwarderError.RequestCanceled)
                {
                    // The backend never answers; the call's 1 s deadline cancels the token.
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // YARP reports the cancelled call as RequestCanceled.
                    }
                }

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(error, CauseOf(error == ForwarderError.Request ? RefusedCause : CallerCancelledCause)));
                return error;
            },
            timeoutSeconds: 1);

        Surface parsed = Enum.Parse<Surface>(surface);
        OperationDescriptor operation = Operation(parsed == Surface.Rest ? RestOperationId : SoapOperationId);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await harness.Forwarder.ForwardAsync(call.Context, parsed, operation).WaitAsync(HangGuard, ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail("failure response write to a stalled caller never ended");
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed <= BoundedWriteLimit, "failure response write outlasted its budget");
        Assert.True(call.Lifetime.AbortCalled, "connection of a caller that stopped reading was not aborted");

        IReadOnlyList<CapturedLog> calls = harness.Logs.BackendCalls;
        Assert.True(calls.Count == 1, "stalled failure write did not write exactly one backend call line");
        Assert.Equal(expectedOutcome, calls[0].Value("Outcome"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
    }

    /// <summary>
    /// A caller that disconnects before the response, or cuts its upload short, is logged
    /// <c>aborted</c> with status 0 at Information: not YARP's placeholder 400, and not an unreachable
    /// backend.
    /// </summary>
    [Theory]
    [InlineData(DisconnectBeforeResponse)]
    [InlineData(TruncatedUpload)]
    public async Task CallerAbortIsLoggedAsAbortedWithNoStatus(string kind)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using StubBackend slow = await StubBackend.StartAsync("slow", new StubBackendOptions { Delay = SlowBackendDelay }, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = slow.RestBaseUrl, BackendSoapBaseUrl = slow.SoapBaseUrl, LoggerProvider = logs },
            ct);

        if (kind == DisconnectBeforeResponse)
        {
            PreparedRequest request = OperationRequests.For(Operation(RestOperationId));
            await RawSocketClient.SendAndCloseAsync(
                facade.RestBaseAddress, request.Method.Method, request.PathAndQuery, contentType: null, 0, 0, CallerPatience, ct);
        }
        else
        {
            PreparedRequest request = OperationRequests.For(Operation(UploadOperationId));
            await RawSocketClient.SendAndCloseAsync(
                facade.RestBaseAddress,
                request.Method.Method,
                request.PathAndQuery,
                JsonContentType,
                CallerDeclaredBodyLength,
                CallerSentBodyLength,
                CallerPatience,
                ct);
        }

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "caller abort did not write exactly one backend call line");
        Assert.Equal(Aborted, calls[0].Value("Outcome"));
        Assert.Equal(0, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Information, calls[0].Level);
    }

    /// <summary>
    /// An upload reaches the backend with an <c>Expect</c> header only when the caller sent one: a small
    /// upload, one of 1 MiB and one of unknown length arrive without <c>Expect</c>, and a caller's own
    /// <c>Expect: 100-continue</c> arrives exactly once, unchanged. The body arrives whole either way.
    /// </summary>
    [Theory]
    [InlineData(SmallBodyLength, false, false, null)]
    [InlineData(LargeBodyLength, false, false, null)]
    [InlineData(SmallBodyLength, true, false, null)]
    [InlineData(LargeBodyLength, true, false, null)]
    [InlineData(SmallBodyLength, false, true, ContinueExpectation)]
    public async Task UploadCarriesOnlyTheCallersOwnExpect(
        int bodyLength, bool unknownLength, bool callerExpectsContinue, string? expectedExpect)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using StubBackend backend = await StubBackend.StartAsync("A", options: null, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.RestBaseUrl, BackendSoapBaseUrl = backend.SoapBaseUrl },
            ct);

        byte[] body = new byte[bodyLength];
        Array.Fill(body, (byte)'x');
        // The catalogue request without its sample body; the upload under test is set as the content.
        PreparedRequest prepared = OperationRequests.For(Operation(UploadOperationId)) with { Body = null, ContentType = null };
        HttpRequestMessage message = prepared.ToMessage(facade.RestBaseAddress);
        HttpContent content = unknownLength ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", JsonContentType);
        message.Content = content;
        if (callerExpectsContinue)
        {
            message.Headers.ExpectContinue = true;
        }

        using HttpClient client = facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(client, message, ct);

        Assert.Equal(200, response.Status);
        IReadOnlyList<StubRequest> received = backend.Received;
        Assert.True(received.Count == 1, "backend did not receive exactly one upload");
        Assert.True(received[0].Body.AsSpan().SequenceEqual(body), "backend did not receive the upload byte for byte");
        Assert.Equal(expectedExpect, received[0].Header(ExpectHeader));
        int expectValues = received[0].Headers.TryGetValue(ExpectHeader, out string[]? values) ? values.Length : 0;
        Assert.Equal(expectedExpect is null ? 0 : 1, expectValues);
    }

    /// <summary>
    /// A backend that rejects an upload beyond its own body-size limit and closes the connection has its
    /// 413 reach the caller unchanged and logged <c>forwarded</c>, although neither the caller nor the
    /// facade sent <c>Expect</c>: the facade adds no body-size limit and never reports such a backend
    /// unavailable. A declared length over the limit is rejected before the backend reads any body byte,
    /// while the facade is still sending the body; a chunked upload once it crosses the limit.
    /// </summary>
    [Theory]
    [InlineData(UploadOperationId, false)]
    [InlineData(SoapOperationId, false)]
    [InlineData(UploadOperationId, true)]
    [InlineData(SoapOperationId, true)]
    public async Task BackendEarlyRejectionOfLargeUploadReachesCaller(string operationId, bool unknownLength)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        long bodyLimit = unknownLength ? DefaultBackendBodyLimit : BackendBodyLimit;
        await using LimitedBackend backend = await LimitedBackend.StartAsync(bodyLimit, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl, LoggerProvider = logs },
            ct);

        OperationDescriptor operation = Operation(operationId);
        byte[] body = new byte[unknownLength ? ChunkedOverLimitBodyLength : OverLimitBodyLength];
        Array.Fill(body, (byte)'x');
        PreparedRequest prepared = OperationRequests.For(operation);
        Uri socket = operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        HttpRequestMessage message = (prepared with { Body = unknownLength ? null : body }).ToMessage(socket);
        if (unknownLength)
        {
            var content = new UnknownLengthContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", prepared.ContentType);
            message.Content = content;
        }

        using HttpClient client = facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(client, message, ct);

        Assert.Equal(413, response.Status);
        Assert.Equal("0", response.Header("Content-Length"));
        Assert.True(response.Body.Length == 0, "early rejection reached the caller with a body");
        Assert.True(backend.Requests == 1, "backend did not receive exactly one request");
        Assert.True(backend.Expectations == 0, "an Expect header the caller did not send reached the backend");
        if (unknownLength)
        {
            Assert.True(backend.BodyBytesRead <= bodyLimit, "backend read more of the upload than its own limit");
        }
        else
        {
            Assert.True(backend.BodyBytesRead == 0, "backend read body bytes of a declared length over its limit");
        }

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "early rejection did not write exactly one backend call line");
        Assert.Equal(Forwarded, calls[0].Value("Outcome"));
        Assert.Equal(413, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Information, calls[0].Level);
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for a backend's early rejection");
    }

    /// <summary>
    /// The backend's answer to an upload it rejects partway reaches the caller while the caller is still
    /// sending, because the facade stops reading an upload the backend no longer takes: a chunked body the
    /// caller never completes, and that carries no <c>Expect</c>, still gets the backend's 413, logged
    /// <c>forwarded</c>.
    /// </summary>
    [Theory]
    [InlineData(UploadOperationId)]
    [InlineData(SoapOperationId)]
    public async Task BackendRejectionOfUnfinishedUploadReachesCallerAtOnce(string operationId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using LimitedBackend backend = await LimitedBackend.StartAsync(SmallBackendBodyLimit, ct);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl, LoggerProvider = logs },
            ct);

        OperationDescriptor operation = Operation(operationId);
        PreparedRequest prepared = OperationRequests.For(operation);
        var headers = new List<KeyValuePair<string, string>> { new("Content-Type", prepared.ContentType ?? JsonContentType) };
        if (prepared.SoapAction is not null)
        {
            headers.Add(new("SOAPAction", prepared.SoapAction));
        }

        Uri socket = operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        RawSocketResponse response = await RawSocketClient.SendUnfinishedChunkedUploadAsync(
            socket, prepared.PathAndQuery, headers, ChunkedOverLimitBodyLength, ct);

        Assert.Equal(413, response.Status);
        Assert.True(response.Body.Length == 0, "rejection of an unfinished upload reached the caller with a body");
        Assert.True(backend.Requests == 1, "backend did not receive exactly one request");
        Assert.True(backend.BodyBytesRead <= SmallBackendBodyLimit, "backend read more of the upload than its own limit");

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "rejection of an unfinished upload did not write exactly one backend call line");
        Assert.Equal(Forwarded, calls[0].Value("Outcome"));
        Assert.Equal(413, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Information, calls[0].Level);
        Assert.True(logs.Entries.All(entry => entry.Level < LogLevel.Error), "facade logged an error for a rejected unfinished upload");
    }

    /// <summary>
    /// A backend that reads an upload and then closes or resets the connection without answering gave
    /// no response to take over, so the caller gets the facade's unavailable answer: REST 502 with the
    /// fixed problem+json body, or the SOAP "Backend unavailable" Fault, logged <c>unavailable</c> with
    /// that status. The upload is sent once.
    /// </summary>
    [Theory]
    [InlineData(UploadOperationId, false)]
    [InlineData(UploadOperationId, true)]
    [InlineData(SoapOperationId, false)]
    [InlineData(SoapOperationId, true)]
    public async Task BackendThatDropsUploadWithoutAnswerGetsTheUnavailableAnswer(string operationId, bool reset)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using DroppingBackend backend = DroppingBackend.Start(reset);
        await using FacadeFactory facade = await FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl, LoggerProvider = logs },
            ct);

        OperationDescriptor operation = Operation(operationId);
        Uri socket = operation.Surface == Surface.Rest ? facade.RestBaseAddress : facade.SoapBaseAddress;
        using HttpClient client = facade.CreateRawClient();
        RawResponse response = await FacadeFactory.SendAsync(client, OperationRequests.For(operation).ToMessage(socket), ct);

        int expectedStatus = UnavailableAnswers.AssertMatches(response, operationId);

        IReadOnlyList<CapturedLog> calls = await logs.WaitForBackendCallsAsync(1, LogWaitBound, ct);
        Assert.True(calls.Count == 1, "dropped upload did not write exactly one backend call line");
        Assert.Equal(Unavailable, calls[0].Value("Outcome"));
        Assert.Equal(expectedStatus, calls[0].Value("Status"));
        Assert.Equal(LogLevel.Warning, calls[0].Level);
        Assert.True(backend.Connections == 1, "backend was not sent the upload exactly once");
    }

    private static OperationDescriptor Operation(string operationId) => OperationCatalogue.Default.GetById(operationId);

    // The exception YARP records for each kind of failure, shaped as observed on Kestrel and
    // SocketsHttpHandler.
    private static Exception? CauseOf(string cause) => cause switch
    {
        NoCause => null,
        RefusedCause => new HttpRequestException(
            "Connection refused", new SocketException((int)SocketError.ConnectionRefused)),
        ConnectTimeoutCause => new TaskCanceledException(
            "The connection attempt timed out.", new TimeoutException("A connection could not be established in time.")),
        CallerCancelledCause => new TaskCanceledException("The operation was canceled."),
        TruncatedUploadCause => new AggregateException(
            new OperationCanceledException(
                "The request body copy was canceled.", new BadHttpRequestException("Unexpected end of request content.")),
            new OperationCanceledException("The operation was canceled.")),
        CallerResetCause => new AggregateException(
            new IOException("The client reset the request stream.", new ConnectionResetException("Connection reset by peer")),
            new HttpRequestException("An error occurred while sending the request.")),
        TempSpaceFullCause => new AggregateException(
            new IOException(
                "An error occurred when reading the request body from the client.", new IOException("No space left on device")),
            new HttpRequestException("An error occurred while sending the request.")),
        // A failed read of the caller's upload whose chain holds a timeout and no cancellation.
        UploadReadTimeoutCause => new AggregateException(
            new IOException(
                "An error occurred when reading the request body from the client.",
                new TimeoutException("The read did not complete in time.")),
            new HttpRequestException("An error occurred while sending the request.")),
        BackendResetCause => new AggregateException(
            new IOException(
                "Unable to write data to the transport connection: Broken pipe.", new SocketException((int)SocketError.Shutdown)),
            new HttpRequestException("An error occurred while sending the request.")),
        ConnectFailedCause => new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Network is unreachable (backend.invalid:65000)",
            new SocketException((int)SocketError.NetworkUnreachable)),
        NameNotResolvedCause => new HttpRequestException(
            HttpRequestError.NameResolutionError,
            "Name or service not known (backend.invalid:65000)",
            new SocketException((int)SocketError.HostNotFound)),
        SendFailedCause => new HttpRequestException(
            HttpRequestError.Unknown,
            "An error occurred while sending the request.",
            new IOException("Unable to write data to the transport connection: Broken pipe.", new SocketException((int)SocketError.Shutdown))),
        ResponseEndedCause => new HttpRequestException(
            HttpRequestError.ResponseEnded,
            "An error occurred while sending the request.",
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")),
        InvalidStatusLineCause => new HttpRequestException(
            HttpRequestError.InvalidResponse, "Received an invalid status line: 'THIS IS NOT HTTP'."),
        InvalidResponseHeadCause => new InvalidOperationException(
            "Invalid Content-Length: \"2,5\". Value must be a positive integral number."),
        InvalidRequestCause => new InvalidOperationException("The outgoing request could not be created."),
        _ => throw new ArgumentOutOfRangeException(nameof(cause)),
    };

    /// <summary>A forwarder constructed over a fake <see cref="IHttpForwarder"/>, with its log captured.</summary>
    private sealed class ForwarderHarness : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;

        public ForwarderHarness(Func<HttpContext, CancellationToken, ValueTask<ForwarderError>> send, int timeoutSeconds = 30)
        {
            var routing = new RoutingOptions { DefaultTarget = BackendTarget };
            routing.Targets[BackendTarget] = new RoutingTarget { RestBaseUrl = UnitRestBaseUrl, SoapBaseUrl = UnitSoapBaseUrl };
            var router = new OperationRouter(
                new FixedOptionsMonitor<RoutingOptions>(routing),
                new FixedOptionsMonitor<BackendOptions>(new BackendOptions { TimeoutSeconds = timeoutSeconds }),
                OperationCatalogue.Default,
                NullLogger<OperationRouter>.Instance);

            _loggerFactory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            Forwarder = new BackendForwarder(
                new FakeForwarder(send),
                router,
                new PassThroughTransformer(),
                new SoapFaultWriter(),
                _loggerFactory.CreateLogger<BackendForwarder>());
        }

        public LogCapture Logs { get; } = new();

        public BackendForwarder Forwarder { get; }

        public void Dispose()
        {
            Forwarder.Dispose();
            _loggerFactory.Dispose();
        }
    }

    /// <summary>Answers every call through the supplied delegate instead of a backend.</summary>
    private sealed class FakeForwarder(Func<HttpContext, CancellationToken, ValueTask<ForwarderError>> send) : IHttpForwarder
    {
        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer) => send(context, CancellationToken.None);

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer,
            CancellationToken cancellationToken) => send(context, cancellationToken);
    }

    private sealed class ErrorFeature(ForwarderError error, Exception? exception) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;

        public Exception? Exception { get; } = exception;
    }

    private sealed class FixedOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = value;

        public TOptions Get(string? name) => CurrentValue;

        // The value never changes, so no listener is ever called.
        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }

    /// <summary>
    /// A REST request context whose response body is captured, whose response start the fake forwarder
    /// sets, and whose connection abort is recorded and cancels <c>RequestAborted</c> as the server's does.
    /// </summary>
    private sealed class UnitCall : IDisposable
    {
        public UnitCall(Stream? responseBody = null)
        {
            Context = new DefaultHttpContext();
            Context.Features.Set<IHttpResponseFeature>(Response);
            Context.Features.Set<IHttpRequestLifetimeFeature>(Lifetime);
            Context.Request.Method = HttpMethods.Get;
            Context.Request.Path = "/company";
            Context.Response.Body = responseBody ?? Body;
        }

        public DefaultHttpContext Context { get; }

        public StartableResponseFeature Response { get; } = new();

        public RecordingLifetime Lifetime { get; } = new();

        public MemoryStream Body { get; } = new();

        public void Dispose()
        {
            Lifetime.Dispose();
            Body.Dispose();
        }
    }

    private sealed class StartableResponseFeature : HttpResponseFeature
    {
        public bool Started { get; set; }

        public override bool HasStarted => Started;
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _aborted = new();

        public bool AbortCalled { get; private set; }

        public CancellationToken RequestAborted
        {
            get => _aborted.Token;
            set => throw new NotSupportedException("the test lifetime's token is fixed");
        }

        public void Abort()
        {
            AbortCalled = true;
            _aborted.Cancel();
        }

        public void CancelRequestAborted() => _aborted.Cancel();

        public void Dispose() => _aborted.Dispose();
    }

    /// <summary>A response body whose writes wait until their token is cancelled, as a caller that stops reading leaves them.</summary>
    private sealed class StalledWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Request content that reports no length, so the client sends it chunked.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body, 0, body.Length);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(body, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A backend on one loopback port, for both surfaces, with a body-size limit of its own, enforced as the
    /// JADE stand-in's Kestrel limit is: an upload declared over the limit is answered 413 before any body
    /// byte is read, a chunked one as soon as it crosses the limit, and the connection is closed.
    /// </summary>
    private sealed class LimitedBackend : IAsyncDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

        private readonly WebApplication _app;
        private int _requests;
        private int _expectations;
        private long _bodyBytesRead;

        private LimitedBackend(WebApplication app) => _app = app;

        public string BaseUrl { get; private set; } = string.Empty;

        public int Requests => Volatile.Read(ref _requests);

        /// <summary>The number of requests received with an <c>Expect</c> header.</summary>
        public int Expectations => Volatile.Read(ref _expectations);

        public long BodyBytesRead => Interlocked.Read(ref _bodyBytesRead);

        public static async Task<LimitedBackend> StartAsync(long bodyLimit, CancellationToken ct)
        {
            // The empty builder reads no configuration and adds no logging provider, like StubBackend's.
            WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                Args = [],
                EnvironmentName = Environments.Production,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = bodyLimit;
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            });
            builder.Services.AddRoutingCore();

            WebApplication app = builder.Build();
            var backend = new LimitedBackend(app);
            RequestDelegate handler = backend.HandleAsync;
            app.Map("/{**path}", handler);

            try
            {
                await app.StartAsync(ct);
                string? address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
                if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out Uri? bound) || bound.Port <= 0)
                {
                    throw new InvalidOperationException("limited backend did not bind a loopback port");
                }

                backend.BaseUrl = bound.GetLeftPart(UriPartial.Authority);
                return backend;
            }
            catch
            {
                await backend.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(StopTimeout);
            try
            {
                await _app.StopAsync(deadline.Token);
            }
            catch (Exception)
            {
                // Best effort, as for StubBackend: stopping must not replace the owning test's outcome.
            }

            await _app.DisposeAsync();
        }

        private async Task HandleAsync(HttpContext context)
        {
            Interlocked.Increment(ref _requests);
            if (context.Request.Headers.Expect.Count > 0)
            {
                Interlocked.Increment(ref _expectations);
            }

            byte[] buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    Interlocked.Add(ref _bodyBytesRead, read);
                }
            }
            catch (BadHttpRequestException rejected)
            {
                // Kestrel refuses a declared length over the limit at the first read, before 100 Continue,
                // and a chunked body at the read that crosses the limit.
                context.Response.StatusCode = rejected.StatusCode;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    /// <summary>
    /// A backend on one loopback port that reads each request's head and declared body, then closes the
    /// connection, or resets it, without sending any response. Connections are served one at a time.
    /// </summary>
    private sealed class DroppingBackend : IAsyncDisposable
    {
        private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

        private readonly TcpListener _listener;
        private readonly bool _reset;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serving;
        private int _connections;

        private DroppingBackend(TcpListener listener, bool reset)
        {
            _listener = listener;
            _reset = reset;
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            _serving = ServeAsync();
        }

        public string BaseUrl { get; }

        public int Connections => Volatile.Read(ref _connections);

        public static DroppingBackend Start(bool reset)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new DroppingBackend(listener, reset);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _serving;
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using Socket socket = await _listener.AcceptSocketAsync(_stopping.Token);
                    Interlocked.Increment(ref _connections);
                    try
                    {
                        await ReadRequestAsync(socket, _stopping.Token);
                    }
                    finally
                    {
                        // A zero linger time makes the close a reset; otherwise the backend sends its FIN.
                        if (_reset)
                        {
                            socket.LingerState = new LingerOption(true, 0);
                        }

                        socket.Close();
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped by DisposeAsync, or the facade closed its side first: nothing more is served.
            }
        }

        // Reads the head and as many body bytes as its Content-Length declares, or until the facade closes.
        private static async Task ReadRequestAsync(Socket socket, CancellationToken ct)
        {
            using var received = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int bodyStart = -1;
            long bodyLength = 0;
            while (bodyStart < 0 || received.Length - bodyStart < bodyLength)
            {
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (read == 0)
                {
                    return;
                }

                received.Write(buffer, 0, read);
                if (bodyStart < 0)
                {
                    int headEnd = received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf(HeadTerminator);
                    if (headEnd >= 0)
                    {
                        bodyStart = headEnd + HeadTerminator.Length;
                        bodyLength = DeclaredLength(Encoding.ASCII.GetString(received.GetBuffer(), 0, headEnd));
                    }
                }
            }
        }

        private static long DeclaredLength(string head)
        {
            foreach (string line in head.Split("\r\n"))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0
                    && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
                {
                    return length;
                }
            }

            return 0;
        }
    }
}
