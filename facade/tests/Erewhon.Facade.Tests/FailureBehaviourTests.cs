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
// The log level floors (class LogLevelFloorsTests), which bound what the framework logs for each failed
// call and keep request data out of the framework logs, are tested here, without a socket, as a class
// of their own: the results report counts every test class whose name ends in "FailureBehaviourTests"
// as the unreachable and timeout check.
//
// Failures are identified across runs by a signature over the exception type, the failing line and the
// assertion message, so every message and Assert.Equal operand here is run-invariant: no port, URL,
// elapsed time or GUID. Timing bounds are Assert.True calls with fixed messages.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Hosting;
using Erewhon.Facade.Forwarding;
using Erewhon.Facade.Soap;
using Erewhon.Facade.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

// The log level floors the facade applies in Program.cs to the framework categories whose lines print
// what a caller sent: Microsoft.AspNetCore.Hosting.Diagnostics, Microsoft.AspNetCore.Routing and
// Microsoft.AspNetCore.Server.Kestrel.BadRequests at Warning, and Yarp at Error. REST path segments
// carry agent and client names, and YARP's forwarder writes a stack trace for every failed call. A floor
// must hold whatever the Logging configuration says, a configured setting stricter than the floor must
// stay as strict, and every category outside the floors must log exactly as configured.
//
// Each case builds only the logging services, from an in-memory Logging section, the console provider
// and the facade's floors, and asks a logger of the category whether a level is enabled. No socket is
// opened and nothing is written.
//
// Facade test failures are identified across runs by a signature over the exception type, the failing
// line and the assertion message, so every message and Assert.Equal operand here is a fixed literal.
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

    // The QA reproduction: every framework category raised to Trace, also through the console provider's
    // own rule and through a rule longer than the Yarp floor's prefix.
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

// Forwarding transport (class ForwardingTransportTests) sits here, because its single-delivery cases
// end in the facade's unreachable-backend 502 or SOAP Fault, as a class of its own: the results
// report counts test classes whose names end in "FailureBehaviourTests" as the six-case
// unreachable and timeout check, and this name matches none.
//
// Transport fidelity of forwarding, observed as raw bytes on both sides of the facade: a body-less write,
// and a write sent with Expect: 100-continue with or without a body, reaches a backend that closes the
// connection without answering exactly once, a body-less request keeps the wire form the HTTP stack gives
// it, and header values outside ASCII pass byte for byte in both directions. The backends are raw TcpListener sockets and the caller is a raw socket, because HttpClient
// adds Content-Length: 0 to a body-less write and refuses non-ASCII header values on the test's own side,
// and a Kestrel stub would decode the bytes it is meant to record.
//
// Failures are identified across runs by a signature over the exception type, the failing line and the
// assertion message, so every message and Assert.Equal operand here is run-invariant: the backend's
// authority is replaced by a fixed token before request heads are compared, and no port, URL or elapsed
// time appears in a message.
/// <summary>
/// Raw-byte checks of what the facade sends to a backend and returns to a caller: single delivery of
/// body-less writes and of writes sent with <c>Expect: 100-continue</c>, the unchanged wire form of
/// body-less requests, and Latin-1 header bytes in both directions.
/// </summary>
public sealed class ForwardingTransportTests
{
    private const string PostCreateTenderId = "Rest.postCreateTender";
    private const string PostCreateAgentId = "Rest.postCreateAgent";
    private const string GetCompanyId = "Rest.getCompany";

    // A REST target that is no catalogue operation, which the REST catch-all forwards to the default target.
    private const string UnmatchedTarget = "/x";

    // Stands for the backend's authority (127.0.0.1:<port>) in every compared request head.
    private const string BackendAuthorityToken = "{backend}";

    private const string ProbeHeader = "X-Probe: one\r\n";

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

    // The raw target of a catalogue REST operation at its normal address, with the catalogue's verb.
    private static string CatalogueTarget(string operationId) =>
        OperationRequests.ForRest(OperationCatalogue.Default.GetById(operationId)).PathAndQuery;

    private static Task<FacadeFactory> StartFacadeAsync(RawBackend backend, CancellationToken ct) =>
        FacadeFactory.StartAsync(
            new FacadeTestSettings { BackendRestBaseUrl = backend.BaseUrl, BackendSoapBaseUrl = backend.BaseUrl },
            ct);

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
}
